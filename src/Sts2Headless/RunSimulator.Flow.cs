using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

/// <summary>
/// "Manual" flow: every screen transition the real game UI makes the player click through
/// (rewards screen, proceed buttons, treasure chest, act transition) becomes an explicit
/// decision instead of being auto-advanced. Opt in with <c>"flow": "manual"</c> on
/// start_run / load_save; the default "auto" flow keeps the legacy behavior.
///
/// Rewards are driven through the engine's own path: <c>RewardsSet.Offer()</c> calls
/// <c>RewardsSet.testSelector</c> in TestMode, which here parks the set as an open rewards
/// screen until the player claims rewards (<c>RewardsSetSynchronizer.SelectLocalReward</c>, the
/// same call NRewardButton makes) and proceeds.
/// </summary>
public partial class RunSimulator
{
    private bool _manualFlow;

    private sealed class RewardsScreen
    {
        public RewardsScreen(RewardsSet set, bool isTerminal)
        {
            Set = set;
            IsTerminal = isTerminal;
        }

        public RewardsSet Set { get; }
        /// <summary>Terminal = the room-end combat screen, whose Proceed leaves the room (NRewardsScreen._isTerminal).</summary>
        public bool IsTerminal { get; }
        /// <summary>Completed when the player leaves the screen; resumes the engine's Offer().</summary>
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>In-flight SelectLocalReward (a card reward blocks inside it until card_reward is resolved).</summary>
        public Task<bool>? ClaimTask { get; set; }
        public Reward? ClaimingReward { get; set; }

        public List<Reward> VisibleRewards => Set.Rewards.Where(r => !r.SuccessfullySelected).ToList();
    }

    /// <summary>Re-export the current decision point without acting (get_state command).</summary>
    public Dictionary<string, object?> GetState()
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("get_state failed", ex); }
    }

    private readonly object _flowLock = new();
    private readonly List<RewardsScreen> _rewardsScreens = new();
    // Room actions started on the thread pool that may park on player input (event options,
    // rest-site options, chest opening, relic pickup). Resolving an input resumes them.
    private readonly List<Task> _backgroundTasks = new();

    // Per-room state, reset whenever the current room changes.
    private AbstractRoom? _flowRoom;
    private bool _roomEndRewardsOffered;
    private Task<bool>? _restSiteTask;
    private bool _restSiteChoiceMade;
    private bool _chestOpened;
    private Task? _chestTask;
    private bool _treasureRelicResolved;

    private RewardsScreen? TopRewardsScreen
    {
        get { lock (_flowLock) return _rewardsScreens.Count > 0 ? _rewardsScreens[^1] : null; }
    }

    private static bool TryParseFlow(string? flow, out bool manual, out string error)
    {
        error = "";
        manual = false;
        switch ((flow ?? "auto").Trim().ToLowerInvariant())
        {
            case "auto":
            case "":
                return true;
            case "manual":
                manual = true;
                return true;
            default:
                error = $"Unknown flow '{flow}' (expected 'auto' or 'manual')";
                return false;
        }
    }

    private void ResetManualFlowState()
    {
        lock (_flowLock)
        {
            _rewardsScreens.Clear();
            _backgroundTasks.Clear();
        }
        _pendingCrystalSphere = null;
        ResetRoomFlowState();
    }

    private void ResetRoomFlowState()
    {
        _flowRoom = null;
        _roomEndRewardsOffered = false;
        _restSiteTask = null;
        _restSiteChoiceMade = false;
        _chestOpened = false;
        _chestTask = null;
        _treasureRelicResolved = false;
    }

    /// <summary>Called at the top of DetectDecisionPoint in manual flow.</summary>
    private void SyncFlowRoom()
    {
        var room = _runState?.CurrentRoom;
        if (!ReferenceEquals(room, _flowRoom))
        {
            ResetRoomFlowState();
            _flowRoom = room;
        }
    }

    // ─── Rewards screen ───

    /// <summary>RewardsSet.testSelector target: open the screen and wait until the player leaves it.</summary>
    internal Task RunInteractiveRewardsScreen(RewardsSet set)
    {
        var screen = new RewardsScreen(set, set.Room is CombatRoom);
        lock (_flowLock) _rewardsScreens.Add(screen);
        if (screen.IsTerminal) _roomEndRewardsOffered = true;
        Log($"Rewards screen opened: {set.Rewards.Count} rewards (terminal={screen.IsTerminal})");
        return screen.Done.Task;
    }

    private void CloseRewardsScreen(RewardsScreen screen)
    {
        lock (_flowLock) _rewardsScreens.Remove(screen);
        screen.Done.TrySetResult();
    }

    /// <summary>Fold a finished claim into the screen; non-terminal screens close once everything is taken (UpdateScreenState).</summary>
    private void SettleClaim(RewardsScreen screen)
    {
        var task = screen.ClaimTask;
        if (task == null || !task.IsCompleted) return;
        if (task.IsFaulted)
            Log($"Claim {screen.ClaimingReward?.GetType().Name} failed: {task.Exception?.GetBaseException().Message}");
        else
            Log($"Claimed {screen.ClaimingReward?.GetType().Name}: success={task.Result}");
        screen.ClaimTask = null;
        screen.ClaimingReward = null;

        if (!screen.IsTerminal && RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(screen.Set))
        {
            Log("All rewards claimed, closing non-terminal rewards screen");
            CloseRewardsScreen(screen);
        }
    }

    private Dictionary<string, object?> DoClaimReward(Player player, Dictionary<string, object?>? args)
    {
        if (!_manualFlow)
            return Error("claim_reward is only available with flow=manual");
        var screen = TopRewardsScreen;
        if (screen == null)
            return Error("No rewards screen is open");
        if (HasPendingSelection)
            return Error("Resolve the open card selection first");
        SettleClaim(screen);
        if (screen.ClaimTask != null)
            return Error("A reward is still being claimed");
        if (args == null || !args.ContainsKey("reward_index"))
            return Error("claim_reward requires 'reward_index'");

        var idx = Convert.ToInt32(args["reward_index"]);
        var visible = screen.VisibleRewards;
        if (idx < 0 || idx >= visible.Count)
            return Error($"Invalid reward index {idx}, {visible.Count} rewards available");
        var reward = visible[idx];
        if (reward is PotionReward && !player.HasOpenPotionSlots)
            return Error("Potion slots are full: discard_potion first, or proceed to leave it");

        Log($"Claiming reward {idx}: {reward.GetType().Name}");
        screen.ClaimingReward = reward;
        screen.ClaimTask = Task.Run(() => RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward));
        return ResumeBackgroundWork();
    }

    private Dictionary<string, object?> DoSelectCardRewardAlternative(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPendingReward)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("alternative_index"))
            return Error("select_card_reward_alternative requires 'alternative_index'");
        var idx = Convert.ToInt32(args["alternative_index"]);
        var alts = _cardSelector.PendingRewardAlternatives;
        if (alts == null || idx < 0 || idx >= alts.Count)
            return Error($"Invalid alternative index {idx}");
        Log($"Card reward alternative: {alts[idx].OptionId}");
        _cardSelector.ResolveRewardAlternative(idx);
        if (_manualFlow) return ResumeBackgroundWork();
        PollUntil(RewardConsumed, 1000);
        _syncCtx.Pump();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> ProceedFromRewardsScreen(Player player, RewardsScreen screen)
    {
        SettleClaim(screen);
        if (screen.ClaimTask != null)
            return Error("A reward is still being claimed");

        var sync = RunManager.Instance.RewardsSetSynchronizer;
        if (!sync.IsRewardsSetCompleted(screen.Set))
        {
            Log("Proceeding: skipping unclaimed rewards");
            sync.SkipLocalRewardsSet();
        }
        CloseRewardsScreen(screen);
        _syncCtx.Pump();

        if (screen.IsTerminal && _runState?.CurrentRoom is CombatRoom combatRoom)
            return TerminalProceed(player, combatRoom);
        return ResumeBackgroundWork();
    }

    private Dictionary<string, object?> RewardsScreenState(Player player)
    {
        var screen = TopRewardsScreen!;
        SettleClaim(screen);
        if (!ReferenceEquals(TopRewardsScreen, screen))
            return DetectDecisionPoint();

        var rewards = screen.VisibleRewards.Select((r, i) => RewardInfo(player, r, i)).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rewards",
            ["context"] = RunContext(),
            ["rewards"] = rewards,
            ["is_terminal"] = screen.IsTerminal,
            ["player"] = PlayerSummary(player),
        };
    }

    private static readonly System.Reflection.FieldInfo? SpecialCardField =
        typeof(SpecialCardReward).GetField("_card", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private Dictionary<string, object?> RewardInfo(Player player, Reward r, int index)
    {
        var info = new Dictionary<string, object?> { ["index"] = index };
        switch (r)
        {
            case GoldReward g:
                info["type"] = "gold";
                info["amount"] = g.Amount;
                break;
            case PotionReward p:
                if (p.Potion != null)
                    foreach (var (k, v) in PotionInfo(p.Potion)) info[k] = v;
                info["type"] = "potion";
                info["enabled"] = player.HasOpenPotionSlots;
                break;
            case RelicReward rr:
                if (rr.Relic != null)
                    foreach (var (k, v) in RelicInfo(rr.Relic)) info[k] = v;
                info["type"] = "relic";
                break;
            case CardReward:
                // The UI only reveals the cards after the reward is clicked.
                info["type"] = "card";
                break;
            case SpecialCardReward:
                // Unlike a card reward, the UI names this card up front (Swipe's stolen card...).
                info["type"] = "special_card";
                if (SpecialCardField?.GetValue(r) is MegaCrit.Sts2.Core.Models.CardModel card)
                    info["card"] = CardInfo(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.None);
                break;
            case CardRemovalReward:
                info["type"] = "card_removal";
                break;
            case LinkedRewardSet linked:
                // Claiming one of these gives up the others.
                info["type"] = "linked";
                info["count"] = linked.Rewards.Count;
                info["rewards"] = linked.Rewards.Select((child, j) => RewardInfo(player, child, j)).ToList();
                break;
            default:
                info["type"] = r.GetType().Name;
                break;
        }
        var text = Rendered(() => r.Description.GetFormattedText(), "gameplay_ui", r.GetType().Name);
        if (!string.IsNullOrWhiteSpace(text) && text != r.GetType().Name) info["reward_text"] = text;
        info.TryAdd("enabled", true);
        return info;
    }

    // ─── Combat end ───

    private Dictionary<string, object?> ManualPostCombat(Player player, CombatRoom combatRoom)
    {
        if (TopRewardsScreen != null)
            return RewardsScreenState(player);

        if (!_roomEndRewardsOffered)
        {
            _roomEndRewardsOffered = true;
            if (combatRoom.Encounter != null && !combatRoom.Encounter.ShouldGiveRewards)
            {
                Log("Encounter gives no rewards, proceeding");
                return TerminalProceed(player, combatRoom);
            }
            // Same entry point as NCombatUi.ShowRewards: fires BeforeCombatRewardOffered and
            // Offer()s one set per player, which lands in RunInteractiveRewardsScreen.
            try { combatRoom.OfferRoomEndRewards().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Log($"OfferRoomEndRewards failed ({ex.GetBaseException().Message}), offering room rewards directly");
                try { TaskHelper.RunSafely(RewardsCmd.OfferForRoomEnd(player, combatRoom)); }
                catch (Exception ex2) { Log($"OfferForRoomEnd failed: {ex2.GetBaseException().Message}"); }
            }
            WaitForTaskOrPending(null, 2000);
            if (TopRewardsScreen != null)
                return RewardsScreenState(player);
        }
        return TerminalProceed(player, combatRoom);
    }

    /// <summary>The rewards screen's Proceed after combat (NRewardsScreen.OnProceedButtonPressed, terminal branch).</summary>
    private Dictionary<string, object?> TerminalProceed(Player player, CombatRoom combatRoom)
    {
        if (combatRoom.RoomType == RoomType.Boss)
        {
            if (IsFirstOfDoubleBoss())
            {
                Log("First boss of double boss defeated: back to the map for the second boss");
                return LeaveToMap();
            }
            if (IsFinalAct())
            {
                // The engine would now enter TheArchitect, a dialogue-only epilogue that ends in WinRun.
                Log("Final boss defeated, run won");
                return GameOverState(true);
            }
            Log("Boss defeated: moving to the next act");
            try
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"EnterNextAct: {ex.Message}"); }
            return DetectDecisionPoint();
        }

        try
        {
            // Resumes the parent event for event combats; otherwise the UI would open the map.
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        catch (Exception ex) { Log($"ProceedFromTerminalRewardsScreen: {ex.Message}"); }

        if (_runState?.CurrentRoom is CombatRoom)
            return LeaveToMap();
        return DetectDecisionPoint();
    }

    private bool IsFinalAct() =>
        _runState != null && _runState.CurrentActIndex >= _runState.Acts.Count - 1;

    private bool IsFirstOfDoubleBoss()
    {
        var map = _runState?.Map;
        return map?.SecondBossMapPoint != null && _runState!.CurrentMapCoord == map.BossMapPoint.coord;
    }

    private Dictionary<string, object?> LeaveToMap()
    {
        try
        {
            if (_runState?.CurrentRoom is not MapRoom)
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
        }
        catch (Exception ex) { Log($"LeaveToMap: {ex.Message}"); }
        return DetectDecisionPoint();
    }

    // ─── Proceed / leave ───

    private Dictionary<string, object?> ManualProceed(Player player)
    {
        if (HasPendingSelection)
            return Error("Resolve the open card selection first");

        var screen = TopRewardsScreen;
        if (screen != null)
            return ProceedFromRewardsScreen(player, screen);

        switch (_runState?.CurrentRoom)
        {
            case CombatRoom combatRoom:
                if (CombatManager.Instance.IsInProgress)
                    return Error("Combat is in progress; use end_turn");
                return ManualPostCombat(player, combatRoom);
            case EventRoom:
            {
                var ev = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
                if (ev != null && !ev.IsFinished && ev.CurrentOptions.Count > 0)
                    return Error("The event is not finished; choose an option");
                return LeaveToMap();
            }
            case RestSiteRoom restRoom:
                SettleRestSiteTask();
                if (!_restSiteChoiceMade && restRoom.Options.Count > 0)
                    return Error("Choose a rest site option first");
                return LeaveToMap();
            case MerchantRoom:
                return LeaveToMap();
            case TreasureRoom:
                if (!_chestOpened)
                    return Error("Open the chest first (open_chest)");
                if (AwaitingTreasureRelicPick())
                {
                    // The proceed button reads "Skip" while the relic is on offer.
                    RunManager.Instance.TreasureRoomRelicSynchronizer.SkipRelicLocally();
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                    _treasureRelicResolved = true;
                }
                return LeaveToMap();
            default:
                return Error("Nothing to proceed from; select a map node");
        }
    }

    // ─── Events / rest sites ───

    private Dictionary<string, object?> ManualChooseOption(Player player, int optionIndex)
    {
        if (HasPendingSelection || TopRewardsScreen != null)
            return Error("Resolve the open screen first");

        switch (_runState?.CurrentRoom)
        {
            case RestSiteRoom restRoom:
            {
                SettleRestSiteTask();
                if (_restSiteTask != null)
                    return Error("A rest site option is still resolving");
                var options = restRoom.Options;
                if (optionIndex < 0 || optionIndex >= options.Count)
                    return Error($"Invalid option index {optionIndex}, {options.Count} options available");
                if (!options[optionIndex].IsEnabled)
                    return Error($"Rest site option {options[optionIndex].OptionId} is disabled");
                Log($"Rest site: choosing {options[optionIndex].OptionId}");
                _restSiteTask = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                TrackBackground(_restSiteTask);
                return ResumeBackgroundWork();
            }
            case EventRoom:
            {
                var ev = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
                if (ev == null || ev.IsFinished || ev.CurrentOptions.Count == 0)
                {
                    // The synthesized PROCEED option (NEventRoom.SetOptions on a finished event).
                    if (optionIndex != 0)
                        return Error("The event is finished; the only option is 0 (Proceed)");
                    return LeaveToMap();
                }
                var options = ev.CurrentOptions;
                if (optionIndex < 0 || optionIndex >= options.Count)
                    return Error($"Invalid option index {optionIndex}, {options.Count} options available");
                if (options[optionIndex].IsLocked)
                    return Error($"Event option {optionIndex} is locked");
                var option = options[optionIndex];
                _eventOptionChosen = true;
                _lastEventOptionCount = options.Count;
                TrackBackground(Task.Run(() => option.Chosen()));
                return ResumeBackgroundWork();
            }
            default:
                return Error("No options to choose in this room");
        }
    }

    private void SettleRestSiteTask()
    {
        var task = _restSiteTask;
        if (task == null || !task.IsCompleted) return;
        if (task.IsFaulted)
            Log($"Rest site option failed: {task.Exception?.GetBaseException().Message}");
        else if (task.Result)
            _restSiteChoiceMade = true;
        _restSiteTask = null;
    }

    private Dictionary<string, object?> ManualRestSiteState(RestSiteRoom restRoom)
    {
        SettleRestSiteTask();
        var options = restRoom.Options;
        var optionList = options.Select(RestOptionInfo).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContext(),
            ["options"] = optionList,
            ["can_proceed"] = _restSiteChoiceMade || options.Count == 0,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    /// <summary>A finished event shows a single Proceed option; unfinished ones use the normal export.</summary>
    private Dictionary<string, object?>? ManualFinishedEventState()
    {
        var ev = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        if (ev != null && !ev.IsFinished && ev.CurrentOptions.Count > 0)
            return null;

        var eventEntry = ev?.Id?.Entry ?? "";
        var eventName = _loc.Text("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);
        string? description = null;
        if (ev?.Description != null)
        {
            var d = EventBodyText(ev);
            if (d != ev.Description.LocEntryKey) description = d;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContext(),
            ["event_name"] = eventName,
            ["description"] = description,
            ["is_finished"] = true,
            ["options"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["index"] = 0,
                    ["title"] = _loc.Text("events", "PROCEED.title"),
                    ["text_key"] = "PROCEED",
                    ["is_locked"] = false,
                    ["is_proceed"] = true,
                },
            },
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    // ─── Treasure ───

    private bool AwaitingTreasureRelicPick()
    {
        if (!_chestOpened || _treasureRelicResolved) return false;
        if (_chestTask != null && !_chestTask.IsCompleted) return false;
        var relics = RunManager.Instance.TreasureRoomRelicSynchronizer?.CurrentRelics;
        return relics != null && relics.Count > 0;
    }

    private Dictionary<string, object?> ManualTreasureState(TreasureRoom treasureRoom)
    {
        _syncCtx.Pump();
        var awaitingPick = AwaitingTreasureRelicPick();
        List<Dictionary<string, object?>>? relics = null;
        if (awaitingPick)
        {
            relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics!.Select((r, i) =>
                {
                    var info = RelicInfo(r);
                    info["index"] = i;
                    return info;
                }).ToList();
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "treasure",
            ["context"] = RunContext(),
            ["chest_opened"] = _chestOpened,
            ["relics"] = relics,
            ["can_proceed"] = _chestOpened && (_chestTask == null || _chestTask.IsCompleted),
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> DoOpenChest(Player player)
    {
        if (!_manualFlow)
            return Error("open_chest is only available with flow=manual");
        if (_runState?.CurrentRoom is not TreasureRoom treasureRoom)
            return Error("Not in a treasure room");
        if (_chestOpened)
            return Error("The chest is already open");
        _chestOpened = true;
        Log("Opening chest");
        // NTreasureRoom.OpenChest: gold first, then any extra rewards (a rewards screen), then the relic.
        _chestTask = Task.Run(async () =>
        {
            await treasureRoom.DoNormalRewards();
            await treasureRoom.DoExtraRewardsIfNeeded();
        });
        TrackBackground(_chestTask);
        return ResumeBackgroundWork();
    }

    private Dictionary<string, object?> DoPickRelic(Player player, Dictionary<string, object?>? args)
    {
        if (!_manualFlow)
            return Error("pick_relic is only available with flow=manual");
        if (!AwaitingTreasureRelicPick())
            return Error("No relic is on offer");
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("pick_relic requires 'relic_index'");
        var relicSync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        var idx = Convert.ToInt32(args["relic_index"]);
        if (idx < 0 || idx >= relicSync.CurrentRelics!.Count)
            return Error($"Invalid relic index {idx}");

        // The grant itself lives in the UI's RelicsAwarded handler (RelicCmd.Obtain per result),
        // so capture the results and obtain them ourselves once the pick resolves.
        List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>? awarded = null;
        Action<List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>> capture = r => awarded = r;
        relicSync.RelicsAwarded += capture;
        try
        {
            Log($"Picking treasure relic {idx}");
            relicSync.PickRelicLocally(idx);
            _syncCtx.Pump();
            WaitForActionExecutor();
            _syncCtx.Pump();
        }
        finally { relicSync.RelicsAwarded -= capture; }
        _treasureRelicResolved = true;

        if (awarded != null && awarded.Count > 0)
        {
            var results = awarded;
            // Pickup effects can open a card selection, so obtain off the main thread.
            TrackBackground(Task.Run(async () =>
            {
                foreach (var res in results)
                {
                    if (res.relic != null && res.player != null)
                        await RelicCmd.Obtain(res.relic.ToMutable(), res.player);
                }
            }));
        }
        return ResumeBackgroundWork();
    }

    private Dictionary<string, object?> DoSkipRelic(Player player)
    {
        if (!_manualFlow)
            return Error("skip_relic is only available with flow=manual");
        if (!AwaitingTreasureRelicPick())
            return Error("No relic is on offer");
        Log("Skipping treasure relic");
        RunManager.Instance.TreasureRoomRelicSynchronizer.SkipRelicLocally();
        _syncCtx.Pump();
        WaitForActionExecutor();
        _treasureRelicResolved = true;
        return DetectDecisionPoint();
    }

    // ─── Background work ───

    private bool HasPendingSelection =>
        _cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null
        || _pendingCrystalSphere != null;

    /// <summary>Something is waiting on the player: a selection, or a rewards screen that isn't mid-claim.</summary>
    private bool HasPendingInteraction
    {
        get
        {
            if (HasPendingSelection) return true;
            var screen = TopRewardsScreen;
            return screen != null && (screen.ClaimTask == null || screen.ClaimTask.IsCompleted);
        }
    }

    private void TrackBackground(Task task)
    {
        lock (_flowLock) _backgroundTasks.Add(task);
    }

    private Task? FirstIncompleteBackground()
    {
        lock (_flowLock)
        {
            foreach (var t in _backgroundTasks.Where(t => t.IsCompleted).ToList())
            {
                if (t.IsFaulted) PatchReport.EngineWarning($"Background task failed: {t.Exception?.GetBaseException()}");
                _backgroundTasks.Remove(t);
            }
            return _backgroundTasks.FirstOrDefault();
        }
    }

    /// <summary>Pump until <paramref name="task"/> completes or the player is needed. Returns true if the player is needed.</summary>
    /// <summary>
    /// Pause between polls of engine state: yield for the first checks (work usually finishes
    /// within microseconds), then sleep 1 ms. Replaces fixed 2–10 ms sleeps that set the latency
    /// floor of most actions.
    /// </summary>
    private static void PollPause(int iteration)
    {
        if (iteration < 100) Thread.Yield(); else Thread.Sleep(1);
    }

    /// <summary>Pump until <paramref name="done"/> or the deadline. Returns whether it finished.</summary>
    private bool PollUntil(Func<bool> done, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; ; i++)
        {
            _syncCtx.Pump();
            if (done()) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            PollPause(i);
        }
    }

    /// <summary>Executor idle, no queued actions, no queued continuations.</summary>
    private bool EngineIdle()
    {
        var executor = RunManager.Instance.ActionExecutor;
        return !executor.IsRunning && RunManager.Instance.ActionQueueSet.IsEmpty && _syncCtx.IsIdle;
    }

    private bool WaitForTaskOrPending(Task? task, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; sw.ElapsedMilliseconds < timeoutMs; i++)
        {
            _syncCtx.Pump();
            if (task != null && task.IsCompleted) break;
            if (HasPendingInteraction && (task == null || !ReferenceEquals(task, TopRewardsScreen?.ClaimTask))) break;
            if (HasPendingSelection) break;
            if (task == null && sw.ElapsedMilliseconds > 20) break;
            PollPause(i);
        }
        if (task != null && !task.IsCompleted && !HasPendingInteraction)
            Log($"Timed out after {timeoutMs}ms waiting for background work");
        _syncCtx.Pump();
        WaitForActionExecutor();
        return HasPendingInteraction;
    }

    /// <summary>
    /// After an input resolves (or a room action starts), let the engine run until it needs the
    /// player again or every parked room action has finished, then report the decision point.
    /// </summary>
    private Dictionary<string, object?> ResumeBackgroundWork()
    {
        for (int guard = 0; guard < 64; guard++)
        {
            var screen = TopRewardsScreen;
            if (screen?.ClaimTask != null)
            {
                WaitForTaskOrPending(screen.ClaimTask);
                SettleClaim(screen);
                if (HasPendingSelection || screen.ClaimTask != null) break;
                continue;
            }
            if (HasPendingInteraction) break;
            var bg = FirstIncompleteBackground();
            if (bg == null) break;
            WaitForTaskOrPending(bg);
            if (!bg.IsCompleted && !HasPendingInteraction) break; // timed out; report what we have
        }
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }
}
