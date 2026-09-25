using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

/// <summary>
/// Synchronization context that executes continuations inline immediately.
/// Task.Yield() posts to SynchronizationContext.Current — by executing inline,
/// the yield becomes a no-op and the entire async chain runs synchronously.
/// Uses a recursion guard to queue nested posts and drain them after.
/// </summary>
internal class InlineSynchronizationContext : SynchronizationContext
{
    // Posts arrive from the main thread and from pool threads (event options, rewards, chests).
    // While any callback runs, other posts are queued and drained afterwards (by the running
    // thread or by Pump on the main thread), which keeps engine continuations serialized. The
    // queue is concurrent so posts from several threads cannot corrupt it.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();
    private volatile bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing)
        {
            _queue.Enqueue((d, state));
            return;
        }

        // Execute inline immediately, then drain any nested posts
        _executing = true;
        try
        {
            d(state);
            while (_queue.TryDequeue(out var item))
                item.Item1(item.Item2);
        }
        finally
        {
            _executing = false;
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    /// <summary>Nothing running and nothing queued.</summary>
    public bool IsIdle => !_executing && _queue.IsEmpty;

    public void Pump()
    {
        // Drain any remaining queued callbacks
        while (_queue.TryDequeue(out var item))
        {
            _executing = true;
            try { item.Item1(item.Item2); }
            finally { _executing = false; }
        }
    }
}

/// <summary>
/// Localization lookup — loads the game's English JSON tables for display names and text.
/// </summary>
internal class LocLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _eng = new();

    public LocLookup()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        Load(Path.Combine(baseDir, "localization_eng"), _eng);
    }

    private static void Load(string dir, Dictionary<string, Dictionary<string, string>> target)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (data != null) target[name] = data;
            }
            catch { }
        }
    }

    /// <summary>Strip BBCode tags like [gold], [/blue], [b], [sine], etc.</summary>
    private static string StripBBCode(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
    }

    /// <summary>English text for a table key, BBCode stripped; the key itself if it is missing.</summary>
    public string Text(string table, string key)
    {
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key) ?? key;
        return StripBBCode(en);
    }

    // Convenience helpers using ModelId
    public string Card(string entry) => Text("cards", entry + ".title");
    public string Monster(string entry)
    {
        var key = entry + ".name";
        var result = Text("monsters", key);
        // If no dedicated entry, fall back to the base segment key (e.g. DECIMILLIPEDE_SEGMENT_FRONT → DECIMILLIPEDE_SEGMENT)
        if (result == key)
        {
            var lastUnderscore = entry.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var baseEntry = entry[..lastUnderscore];
                var baseKey = baseEntry + ".name";
                var baseResult = Text("monsters", baseKey);
                if (baseResult != baseKey) return baseResult;
            }
        }
        return result;
    }
    public string Relic(string entry) => Text("relics", entry + ".title");
    public string Potion(string entry) => Text("potions", entry + ".title");
    public string Power(string entry) => Text("powers", entry + ".title");
    public string Event(string entry) => Text("events", entry + ".title");
    public string Act(string entry) => Text("acts", entry + ".title");

    public bool IsLoaded => _eng.Count > 0;
}

/// <summary>
/// Full run simulator — manages the game lifecycle from character selection
/// through map navigation, combat, events, rest sites, shops, and act transitions.
/// Drives the engine forward until it hits a "decision point" requiring external input.
/// </summary>
public partial class RunSimulator
{
    private static int? _expectedSaveSchemaVersion;
    private static bool _expectedSaveSchemaVersionReady;
    private static readonly object _expectedSaveSchemaVersionLock = new();

    private RunState? _runState;
    private static bool _modelDbInitialized;
    private static readonly InlineSynchronizationContext _syncCtx = new();
    private readonly ManualResetEventSlim _turnStarted = new(false);
    private readonly ManualResetEventSlim _combatEnded = new(false);
    private static readonly LocLookup _loc = new();
    private bool _eventOptionChosen;
    private int _lastEventOptionCount;
    // Auto flow: the thread-pool task running the last chosen event option (see WaitForEventOption).
    private Task? _eventOptionTask;
    private Task? _restOptionTask;
    private Task? _shopRemovalTask;

    /// <summary>The engine thread blocked on a card reward has taken the choice (or a new prompt opened).</summary>
    private bool RewardConsumed() => _cardSelector.PendingRewardCards == null || HasPendingSelection;

    // Pending rewards for card selection (populated after combat, before proceeding)
    private List<Reward>? _pendingRewards;
    private CardReward? _pendingCardReward;
    private bool _rewardsProcessed;
    private int _goldBeforeCombat;
    private int _lastKnownHp;
    private readonly HeadlessCardSelector _cardSelector = new();
    // The engine accepts one selector registration per process ("A card selector is already
    // active"); keep the scope so a second start_run/load_save reuses it.
    private IDisposable? _selectorScope;
    // Pending bundle selection (Scroll Boxes: pick 1 of N packs)
    private IReadOnlyList<IReadOnlyList<CardModel>>? _pendingBundles;
    private TaskCompletionSource<IEnumerable<CardModel>>? _pendingBundleTcs;

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null, string? flow = null, string? act1 = null)
    {
        try
        {
            // Validate everything first: a rejected start_run must leave the current run intact.
            if (!TryParseFlow(flow, out var manual, out var flowError)) return Error(flowError);
            if (ascension < 0 || ascension > MaxAscension)
                return Error($"Ascension must be 0–{MaxAscension}, got {ascension}");
            if (!KnownCharacters.Contains(character.ToLowerInvariant()))
                return Error($"Unknown character: {character}");
            EnsureModelDbInitialized();
            // Same seed handling as the game's lobby: a typed seed is canonicalized (upper case,
            // O→0, I→1) so it reproduces the in-game run; no seed means a fresh random one.
            var seedStr = string.IsNullOrWhiteSpace(seed) ? SeedHelper.GetRandomSeed() : SeedHelper.CanonicalizeSeed(seed);
            if (!TrySelectActs(seedStr, act1, out var acts, out var actError))
                return Error(actError);

            // A new run in the same process (e.g. an RL env reset) must tear down the previous
            // one first; RunManager.SetUpTest refuses to run twice ("State is already set").
            if (_runState != null) CleanUp();
            ResetRunScopedState();
            ResetManualFlowState();
            _manualFlow = manual;

            var player = CreatePlayer(character)!;
            Log($"Creating RunState with seed={seedStr}, acts={string.Join(",", acts.Select(a => a.Id.Entry))}");

            // Use CreateForTest which properly handles mutable copies internally
            _runState = RunState.CreateForTest(
                players: new[] { player },
                acts: acts,
                ascensionLevel: ascension,
                seed: seedStr
            );

            // Set up RunManager with test mode
            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // Force Neow event (blessing selection at start)
            _runState.ExtraFields.StartedWithNeow = true;

            // Generate rooms for all acts
            RunManager.Instance.GenerateRooms();
            Log("Rooms generated");

            // Launch the run
            RunManager.Instance.Launch();
            Log("Run launched");

            // Register event handlers for combat turn transitions
            SubscribeCombatEvents();

            // Finalize starting relics
            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();
            Log("Starting relics finalized");

            // Enter first act (generates map)
            RunManager.Instance.EnterAct(0, doTransition: false).GetAwaiter().GetResult();
            Log("Entered Act 0");

            // Register card selector for cards that need player choice
            _selectorScope ??= CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            // Now we should be at the map — detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    /// <summary>
    /// Choose the run's acts like the game's StartRunLobby.BeginRunLocally: each act slot is rolled
    /// from the seed (Rng "act_selection") among its unlocked acts (e.g. act 1 is Overgrowth or
    /// Underdocks), and the lobby's act-1 selector can pin act 1. Without this, CreateForTest falls
    /// back to ActModel.GetDefaultList() and act 1 is always Overgrowth.
    /// </summary>
    private static bool TrySelectActs(string seed, string? act1, out List<ActModel> acts, out string error)
    {
        error = "";
        var rng = new MegaCrit.Sts2.Core.Random.Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
        acts = ActModel.GetRandomList(rng, UnlockState.all, isMultiplayer: false).ToList();
        switch ((act1 ?? "random").Trim().ToLowerInvariant())
        {
            case "random":
            case "":
                return true;
            case "overgrowth":
                acts[0] = ModelDb.Act<MegaCrit.Sts2.Core.Models.Acts.Overgrowth>();
                return true;
            case "underdocks":
                acts[0] = ModelDb.Act<MegaCrit.Sts2.Core.Models.Acts.Underdocks>();
                return true;
            default:
                error = $"Unknown act1 '{act1}' (expected random, overgrowth or underdocks)";
                return false;
        }
    }

    // ─── Test/Debug commands ───

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>Get the backing List&lt;T&gt; behind an IReadOnlyList property via reflection.</summary>
    private static List<T>? GetBackingList<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj) as List<T>;
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        field?.SetValue(obj, value);
    }

    public Dictionary<string, object?> SetPlayer(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];

            if (args.TryGetValue("hp", out var hpEl) && player.Creature != null)
                SetField(player.Creature, "_currentHp", hpEl.GetInt32());
            if (args.TryGetValue("max_hp", out var mhpEl) && player.Creature != null)
                SetField(player.Creature, "_maxHp", mhpEl.GetInt32());
            if (args.TryGetValue("gold", out var goldEl))
                player.Gold = goldEl.GetInt32();

            if (args.TryGetValue("relics", out var relicsEl))
            {
                // Resolve every id first so an unknown one leaves the relics untouched.
                var wanted = new List<RelicModel>();
                foreach (var rEl in relicsEl.EnumerateArray())
                {
                    var id = rEl.GetString();
                    if (id == null) continue;
                    var model = ModelDb.AllRelics.FirstOrDefault(r => string.Equals(r.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
                    if (model == null) return Error($"Unknown relic: {id}");
                    wanted.Add(model);
                }
                // Owner-aware add/remove (a bare list insert left relics without an owner, and every
                // later hook threw). This sets state only: pickup effects (AfterObtained) do not run.
                foreach (var r in player.Relics.ToList())
                    player.RemoveRelicInternal(r, silent: true);
                foreach (var model in wanted)
                    player.AddRelicInternal(model.ToMutable(), silent: true);
            }
            if (args.TryGetValue("deck", out var deckEl))
            {
                // Remove existing cards from RunState tracking
                foreach (var c in player.Deck.Cards.ToList())
                    _runState.RemoveCard(c);
                player.Deck.Clear(silent: true);
                // Add new cards via RunState.CreateCard (sets Owner + registers)
                foreach (var cEl in deckEl.EnumerateArray())
                {
                    var id = cEl.GetString();
                    if (id == null) continue;
                    var canonical = ModelDb.GetById<CardModel>(new ModelId("CARD", id));
                    if (canonical != null)
                    {
                        var card = _runState.CreateCard(canonical, player);
                        player.Deck.AddInternal(card, silent: true);
                    }
                }
            }
            if (args.TryGetValue("potions", out var potionsEl))
            {
                var slots = GetBackingList<PotionModel>(player, "_potionSlots")
                         ?? GetBackingList<PotionModel?>(player, "_potionSlots") as System.Collections.IList;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++) slots[i] = null;
                    int idx = 0;
                    foreach (var pEl in potionsEl.EnumerateArray())
                    {
                        if (idx >= slots.Count) break;
                        var id = pEl.GetString();
                        if (id != null)
                        {
                            var model = ModelDb.GetById<PotionModel>(new ModelId("POTION", id));
                            // Inject a mutable instance (not the canonical model — that throws
                            // CanonicalModelException when the game reads potion.Owner) and set its
                            // Owner, or UsePotionAction fails with "without an owner!".
                            if (model != null)
                            {
                                var mutable = model.ToMutable();
                                mutable.Owner = player;
                                slots[idx] = mutable;
                            }
                        }
                        idx++;
                    }
                }
            }

            Log($"SetPlayer: hp={player.Creature?.CurrentHp} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck?.Cards?.Count}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = PlayerSummary(player),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetPlayer failed", ex); }
    }

    public Dictionary<string, object?> EnterRoom(string roomType, string? encounter, string? eventId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var runState = _runState;
            Log($"EnterRoom: type={roomType} encounter={encounter} event={eventId}");

            AbstractRoom room;
            switch (roomType.ToLowerInvariant())
            {
                case "combat":
                case "monster":
                case "elite":
                {
                    if (string.IsNullOrEmpty(encounter))
                        encounter = "SHRINKER_BEETLE_WEAK"; // default encounter
                    var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounter));
                    if (encModel == null) return Error($"Unknown encounter: {encounter}");
                    room = new CombatRoom(encModel.ToMutable(), runState);
                    break;
                }
                case "shop":
                    room = new MerchantRoom();
                    break;
                case "rest":
                case "rest_site":
                    room = new RestSiteRoom();
                    break;
                case "event":
                {
                    if (string.IsNullOrEmpty(eventId))
                        return Error("event requires 'event' parameter (e.g. CHANGELING_GROVE)");
                    var evModel = ModelDb.GetById<EventModel>(new ModelId("EVENT", eventId));
                    if (evModel == null) return Error($"Unknown event: {eventId}");
                    room = new EventRoom(evModel);
                    break;
                }
                case "treasure":
                    room = new TreasureRoom(_runState.CurrentActIndex);
                    break;
                default:
                    return Error($"Unknown room type: {roomType}");
            }

            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("EnterRoom failed", ex); }
    }

    public Dictionary<string, object?> SetDrawOrder(List<string> cardIds)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.DrawPile == null) return Error("Not in combat");

            var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
            if (drawList == null) return Error("Cannot access draw pile");

            var newOrder = new List<CardModel>();
            var available = new List<CardModel>(drawList);
            foreach (var cardId in cardIds)
            {
                var match = available.FirstOrDefault(c =>
                    c.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newOrder.Add(match);
                    available.Remove(match);
                }
            }
            newOrder.AddRange(available);

            drawList.Clear();
            drawList.AddRange(newOrder);

            Log($"SetDrawOrder: {newOrder.Count} cards, top={newOrder.FirstOrDefault()?.Id.Entry}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["draw_pile_count"] = drawList.Count,
                ["top_cards"] = newOrder.Take(5).Select(c => _loc.Card(c.Id.Entry)).ToList(),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetDrawOrder failed", ex); }
    }

    // ─── Game actions ───
    public Dictionary<string, object?> LoadSave(string saveJson, string? flow = null)
    {
        try
        {
            // Validate and parse first: a rejected load_save must leave the current run intact.
            if (!TryParseFlow(flow, out var manual, out var flowError)) return Error(flowError);
            EnsureModelDbInitialized();

            Log("Loading save file...");

            if (!ValidateSaveSchemaVersion(saveJson, out var schemaError))
                return Error($"Save schema mismatch: {schemaError}");

            var readResult = SaveManager.FromJson<SerializableRun>(saveJson);
            if (!readResult.Success || readResult.SaveData == null)
                return Error($"Failed to parse save file: {readResult.Status} {readResult.ErrorMessage}");

            if (_runState != null) CleanUp();
            ResetRunScopedState();
            ResetManualFlowState();
            _manualFlow = manual;

            var save = readResult.SaveData;
            Log($"Save loaded: seed={save.SerializableRng?.Seed}, act={save.CurrentActIndex}, ascension={save.Ascension}");

            _runState = RunState.FromSerializable(save);
            if (_runState == null)
                return Error("Failed to create RunState from save");

            Log($"RunState created, players={_runState.Players?.Count}");

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpSavedSingleplayer(_runState, save).GetAwaiter().GetResult();
            LocalContext.NetId = netService.NetId;

            SubscribeCombatEvents();
            _selectorScope ??= CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            var savedRoom = _runState.CurrentRoom;

            // Save visited coords before Launch (EnterAct will clear them)
            var savedVisitedCoords = _runState.VisitedMapCoords?.ToList() ?? new List<MapCoord>();
            var shouldResumeInitialNeow = IsInitialNeowSave(saveJson);
            Log($"Save has {savedVisitedCoords.Count} visited coords");

            RunManager.Instance.Launch();
            Log("Run launched");

            if (savedRoom is MapRoom || savedRoom == null)
            {
                // Preserve Neow for saves created before the first blessing choice.
                // Once the run has visited at least one map node, re-entering Act 1
                // should not send the player back through the Ancient start node.
                if (_runState.CurrentActIndex == 0 && savedVisitedCoords.Count > 0)
                    _runState.ExtraFields.StartedWithNeow = false;
                RunManager.Instance.EnterAct(_runState.CurrentActIndex, doTransition: false).GetAwaiter().GetResult();
                _syncCtx.Pump();
                Log($"Entered Act {_runState.CurrentActIndex}");

                if (shouldResumeInitialNeow && _runState.Map?.StartingMapPoint != null)
                {
                    Log("Restoring initial Neow event");
                    RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }

                // EnterAct clears visited coords and ActFloor — restore them from save
                if (savedVisitedCoords.Count > 0)
                {
                    if (_runState.VisitedMapCoords == null || _runState.VisitedMapCoords.Count == 0)
                    {
                        foreach (var coord in savedVisitedCoords)
                            _runState.AddVisitedMapCoord(coord);
                    }
                    _runState.ActFloor = savedVisitedCoords.Count;
                    var last = savedVisitedCoords[^1];
                    Log($"Restored map position: floor={_runState.ActFloor}, coord=({last.col},{last.row})");
                }
            }
            else
            {
                Log($"Preserving saved room: {savedRoom.GetType().Name}");
            }

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("LoadSave failed", ex);
        }
    }

    /// <summary>
    /// Expected run save <c>schema_version</c> (lazy: first load_save only, so StartRun never fails on reflection).
    /// Order: <c>STS2_SAVE_SCHEMA_VERSION</c> env → reflect sts2.dll → unknown, defer to SaveManager.
    /// </summary>
    private static int? GetExpectedSaveSchemaVersion()
    {
        if (_expectedSaveSchemaVersionReady)
            return _expectedSaveSchemaVersion;
        lock (_expectedSaveSchemaVersionLock)
        {
            if (_expectedSaveSchemaVersionReady)
                return _expectedSaveSchemaVersion;
            _expectedSaveSchemaVersion = ResolveExpectedSaveSchemaVersion();
            _expectedSaveSchemaVersionReady = true;
            return _expectedSaveSchemaVersion;
        }
    }

    private static int? ResolveExpectedSaveSchemaVersion()
    {
        var env = Environment.GetEnvironmentVariable("STS2_SAVE_SCHEMA_VERSION");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out var envVer))
            return envVer;

        var reflected = TryReflectLatestSaveSchemaVersion();
        if (reflected.HasValue)
            return reflected.Value;

        Console.Error.WriteLine(
            "[Sts2Headless] Could not read save schema from sts2.dll; deferring schema compatibility " +
            "to SaveManager.FromJson. Set STS2_SAVE_SCHEMA_VERSION to enforce a specific version.");
        return null;
    }

    /// <summary>Find static parameterless GetLatestSchemaVersion (or close) on sts2; supports int/uint/long.</summary>
    private static int? TryReflectLatestSaveSchemaVersion()
    {
        var asm = typeof(SerializableRun).Assembly;
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        var candidates = new List<(int score, string typeName, int value)>();
        foreach (var t in types)
        {
            MethodInfo? m;
            try
            {
                foreach (var name in new[] { "GetLatestSchemaVersion", "GetLatestVersion" })
                {
                    m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    var tn = t.FullName ?? "";
                    // Avoid unrelated static GetLatestVersion() elsewhere in the assembly.
                    if (name == "GetLatestVersion" && !tn.Contains("Saves", StringComparison.Ordinal))
                        continue;

                    var conv = TryConvertSchemaNumber(m.Invoke(null, null));
                    if (!conv.HasValue) continue;

                    var score = name == "GetLatestSchemaVersion" ? 100 : 0;
                    if (tn.Contains("Saves", StringComparison.Ordinal)) score += 50;
                    if (tn.Contains("Schema", StringComparison.Ordinal) || tn.Contains("Migration", StringComparison.Ordinal))
                        score += 25;
                    candidates.Add((score, tn, conv.Value));
                }
            }
            catch
            {
                // type may not support full reflection on this runtime
            }
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.score).ThenBy(c => c.typeName).First();
        return best.value;
    }

    private static int? TryConvertSchemaNumber(object? value) => value switch
    {
        int i => i,
        uint u => u <= int.MaxValue ? (int)u : null,
        long l => l >= int.MinValue && l <= int.MaxValue ? (int)l : null,
        short s => s,
        ushort us => us,
        byte b => b,
        _ => null,
    };

    private static bool ValidateSaveSchemaVersion(string saveJson, out string error)
    {
        error = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schema_version", out var versionElem))
            {
                error = "missing schema_version";
                return false;
            }

            if (versionElem.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !versionElem.TryGetInt32(out var schemaVersion))
            {
                error = "schema_version is not a valid integer";
                return false;
            }

            var expected = GetExpectedSaveSchemaVersion();
            if (expected.HasValue && schemaVersion != expected.Value)
            {
                error = $"expected v{expected.Value}, got v{schemaVersion}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"could not inspect save: {ex.Message}";
            return false;
        }
    }

    private static bool TrySetPropertyValue(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop?.CanWrite != true)
            return false;
        prop.SetValue(target, value);
        return true;
    }

    private static bool IsInitialNeowSave(string saveJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_act_index", out var actIndexElem) || actIndexElem.GetInt32() != 0)
                return false;

            var hasVisitedCoords = root.TryGetProperty("visited_map_coords", out var visitedElem)
                                && visitedElem.ValueKind == System.Text.Json.JsonValueKind.Array
                                && visitedElem.GetArrayLength() > 0;
            if (hasVisitedCoords)
                return false;

            return root.TryGetProperty("extra_fields", out var extraFieldsElem)
                && extraFieldsElem.ValueKind == System.Text.Json.JsonValueKind.Object
                && extraFieldsElem.TryGetProperty("started_with_neow", out var startedElem)
                && startedElem.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRollbackSerializedSaveToPreRoom(SerializableRun serializableRun, out string error)
    {
        error = "";

        var saveType = serializableRun.GetType();
        var visitedProp = saveType.GetProperty("VisitedMapCoords");
        if (visitedProp == null)
        {
            error = "Save data is missing VisitedMapCoords";
            return false;
        }

        var visitedValue = visitedProp.GetValue(serializableRun);
        var visitedItems = new List<object?>();
        if (visitedValue is System.Collections.IEnumerable visitedEnumerable)
        {
            foreach (var item in visitedEnumerable)
                visitedItems.Add(item);
        }

        if (visitedItems.Count == 0)
        {
            error = "Cannot roll back save before the first room";
            return false;
        }

        visitedItems.RemoveAt(visitedItems.Count - 1);

        var visitedType = visitedProp.PropertyType;
        if (visitedType.IsArray)
        {
            var elementType = visitedType.GetElementType()!;
            var array = Array.CreateInstance(elementType, visitedItems.Count);
            for (int i = 0; i < visitedItems.Count; i++)
                array.SetValue(visitedItems[i], i);
            visitedProp.SetValue(serializableRun, array);
        }
        else if (visitedType.IsGenericType)
        {
            var elementType = visitedType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in visitedItems)
                list.Add(item);
            visitedProp.SetValue(serializableRun, list);
        }
        else
        {
            error = $"Unsupported VisitedMapCoords type: {visitedType.Name}";
            return false;
        }

        TrySetPropertyValue(serializableRun, "ActFloor", visitedItems.Count);
        TrySetPropertyValue(serializableRun, "CurrentMapCoord", visitedItems.Count > 0 ? visitedItems[^1] : null);
        TrySetPropertyValue(serializableRun, "PreFinishedRoom", null);
        TrySetPropertyValue(serializableRun, "CurrentRoom", null);
        return true;
    }

    public Dictionary<string, object?> SaveCheckpoint(string? outputPath)
    {
        try
        {
            if (_runState == null)
                return Error("No active run to save");

            if (string.IsNullOrEmpty(outputPath))
                return Error("No output path specified for quit save");

            var currentRoom = _runState.CurrentRoom;
            SerializableRun serializableRun;

            if (currentRoom is MapRoom || currentRoom == null)
            {
                Log($"Saving map checkpoint (room={currentRoom?.GetType().Name ?? "null"}, outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(currentRoom);
            }
            else
            {
                Log($"Saving pre-room checkpoint from {currentRoom.GetType().Name} (outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(new MapRoom());
                if (!TryRollbackSerializedSaveToPreRoom(serializableRun, out var rollbackError))
                    return Error($"Cannot save checkpoint: {rollbackError}");
            }

            var saveJson = SaveManager.ToJson(serializableRun);
            Log($"Serialized save: {saveJson.Length} chars");

            var dir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(outputPath, saveJson);
            Log($"Save written to: {outputPath}");

            return new Dictionary<string, object?>
            {
                ["type"] = "save_result",
                ["success"] = true,
                ["path"] = outputPath,
                ["size"] = saveJson.Length,
                ["room_type"] = currentRoom?.GetType().Name,
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("SaveCheckpoint failed", ex);
        }
    }
    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var illegal = ValidateAction(action, args);
            if (illegal != null) return illegal;
            // The action will change state; its response re-exports the legal set.
            InvalidateLegal();

            var player = _runState.Players[0];

            switch (action)
            {
                case "select_map_node":
                    return DoMapSelect(player, args);
                case "play_card":
                    return DoPlayCard(player, args);
                case "end_turn":
                    return DoEndTurn(player);
                case "choose_option":
                    return DoChooseOption(player, args);
                case "select_card_reward":
                    return DoSelectCardReward(player, args);
                case "skip_card_reward":
                    return DoSkipCardReward(player);
                case "buy_card":
                    return DoBuyCard(player, args);
                case "buy_relic":
                    return DoBuyRelic(player, args);
                case "buy_potion":
                    return DoBuyPotion(player, args);
                case "remove_card":
                    return DoRemoveCard(player);
                case "select_bundle":
                    return DoSelectBundle(player, args);
                case "select_cards":
                    return DoSelectCards(player, args);
                case "skip_select":
                    return DoSkipSelect(player);
                case "use_potion":
                    return DoUsePotion(player, args);
                case "discard_potion":
                    return DoDiscardPotion(player, args);
                case "leave_room":
                    return _manualFlow ? ManualProceed(player) : DoLeaveRoom(player);
                case "proceed":
                    return _manualFlow ? ManualProceed(player) : DoProceed(player);
                case "claim_reward":
                    return DoClaimReward(player, args);
                case "select_card_reward_alternative":
                    return DoSelectCardRewardAlternative(player, args);
                case "open_chest":
                    return DoOpenChest(player);
                case "pick_relic":
                    return DoPickRelic(player, args);
                case "skip_relic":
                    return DoSkipRelic(player);
                case "crystal_sphere_divine":
                    return DoCrystalSphereDivine(player, args);
                default:
                    return Error($"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");
        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        if (col < 0 || col > byte.MaxValue || row < 0 || row > byte.MaxValue)
            return Error($"Map coord ({col},{row}) is out of range");
        var coord = new MapCoord((byte)col, (byte)row);
        // Check before resetting any room state: a bad coord must not change anything.
        var map = _runState?.Map;
        if (map == null)
            return Error("No map available");
        if (!TravelablePoints(map).Any(p => p.coord == coord))
            return Error($"Map node ({col},{row}) is not reachable from here");

        // Reset tracking for new room
        _rewardsProcessed = false;
        _pendingCardReward = null;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _pendingRewards = null;
        _lastKnownHp = player.Creature?.CurrentHp ?? 0;
        ResetRoomFlowState();

        Log($"Moving to map coord ({col},{row})");

        // BUG-013: Wait for any pending actions (relic sessions, etc.) to complete before entering new room
        WaitForActionExecutor();
        _syncCtx.Pump();

        // Call EnterMapCoord directly (same as what MoveToMapCoordAction does in TestMode)
        // This avoids the action executor which can swallow errors silently.
        RunManager.Instance.EnterMapCoord(coord).GetAwaiter().GetResult();
        _syncCtx.Pump();
        WaitForActionExecutor();

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoPlayCard(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("card_index"))
            return Error("play_card requires 'card_index'");
        if (HasPendingSelection)
            return Error("A selection is pending; resolve it before playing a card");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("Not in combat");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Invalid card index {cardIndex}, hand has {hand.Count} cards");

        var card = hand[cardIndex];

        // Determine target based on card's TargetType first
        // Self/None/All cards: target = null (game handles internally)
        // AnyEnemy cards: use target_index or auto-pick first alive enemy
        Creature? target = null;
        var cardTargetType = card.TargetType;
        if (cardTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided
            if (args.TryGetValue("target_index", out var targetObj) && targetObj != null)
            {
                var targetIndex = Convert.ToInt32(targetObj);
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state != null)
                {
                    var enemies = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIndex >= 0 && targetIndex < enemies.Count)
                        target = enemies[targetIndex];
                }
            }
            // No target_index given: only auto-target when the choice is unambiguous
            // (a single alive enemy). With multiple enemies, picking one is a real game
            // decision — return an error instead of silently targeting enemy 0 (#79).
            if (target == null)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                var alive = state?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
                if (alive.Count == 1)
                    target = alive[0];
                else if (alive.Count > 1)
                    return Error($"Card {card.Id.Entry} targets a single enemy (AnyEnemy); " +
                                 $"'target_index' is required when multiple enemies are alive ({alive.Count}).");
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        // Check if card can be played
        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var handCountBefore = hand.Count;
        var playsBefore = CombatManager.Instance.History.CardPlaysStarted.Count();

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);
        WaitForActionExecutor();
        SettleCombatActions();

        // Check if card play had no effect (hand unchanged, same card still at same index).
        // Cards like Particle Wall legitimately return to hand, so only call it a failure if the
        // engine did not record a card play either.
        var handAfter = pcs.Hand.Cards;
        var played = CombatManager.Instance.History.CardPlaysStarted.Count() > playsBefore;
        if (!played && handAfter.Count == handCountBefore && cardIndex < handAfter.Count && handAfter[cardIndex] == card)
        {
            return Error($"Card could not be played (still in hand after action): {card.GetType().Name} [{card.Id}]");
        }

        return DetectDecisionPoint();
    }

    // STS2 build 23372702 removed CombatManager.IsPlayPhase (global) in favor of a
    // per-player PlayerCombatState.Phase. Headless is single-player, so the local
    // player (Players[0]) being in the Play phase is the equivalent signal.
    private bool IsPlayPhase()
    {
        var p = (_runState != null && _runState.Players.Count > 0) ? _runState.Players[0] : null;
        return p?.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play;
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        // A pending card / card-reward / bundle selection is an unresolved prompt; ending
        // the turn here would silently mutate combat instead. Surface the prompt unchanged
        // and let the caller resolve it first (#61).
        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
        {
            Log("end_turn ignored: a card selection is pending");
            return DetectDecisionPoint();
        }

        if (!IsPlayPhase())
        {
            // Might be between phases — pump and check
            _syncCtx.Pump();
            if (!IsPlayPhase())
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                // Brief wait for ThreadPool if sync context didn't catch it
                Thread.Sleep(100);
                _syncCtx.Pump();
                if (!IsPlayPhase())
                    return DetectDecisionPoint();
            }
        }

        // Ensure no actions are still running before ending turn
        WaitForActionExecutor();

        Log($"Ending turn (round={CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        // The turn is over only once the player is back in a *later* play phase. Checking the
        // phase alone races combats whose turn loop runs off the main thread (fights started by
        // an event option): right after EndTurn the phase can still read Play for the old turn.
        var turnBefore = player.PlayerCombatState?.TurnNumber ?? 0;
        bool NextTurnReached() =>
            !CombatManager.Instance.IsInProgress || player.Creature.IsDead || HasPendingSelection
            || (IsPlayPhase() && (player.PlayerCombatState?.TurnNumber ?? 0) > turnBefore);

        // Enable SuppressYield so Task.Yield() runs inline during enemy turn processing.
        // This prevents deadlocks during boss fights (e.g., Vantom) where continuations
        // would otherwise be posted to ThreadPool and never complete.
        // Keep SuppressYield=true through the initial fallback wait loop — multi-hit
        // attacks (e.g., 10x2) have continuations between hits that also need suppression.
        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            _syncCtx.Pump();

            // Fallback: if turn didn't complete synchronously, keep pumping with SuppressYield on
            // (up to 2 s of wall clock; returns as soon as the next player turn starts). Yield
            // for the first checks: the enemy turn usually finishes within microseconds.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; !NextTurnReached() && deadline.ElapsedMilliseconds < 2000; i++)
            {
                _syncCtx.Pump();
                if (_combatEnded.IsSet) break;
                if (i < 200) Thread.Yield(); else Thread.Sleep(1);
            }
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }

        // An enemy move can ask the player to choose mid-turn (e.g. Knowledge Demon's Curse of
        // Knowledge → choose a curse). That is not a deadlock: surface the selection; resolving it
        // resumes the enemy turn (see ResolveSelectionAndResumeTurn).
        if (HasPendingSelection)
        {
            Log("Enemy turn is waiting on a player selection");
            return DetectDecisionPoint();
        }

        // Second fallback: if still stuck after SuppressYield window, cancel and retry.
        // The WaitUntilQueue TCS is likely deadlocked.
        if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
        {
            Log("EndTurn stuck, cancelling and retrying with SuppressYield...");
            try
            {
                RunManager.Instance.ActionExecutor.Cancel();
                _syncCtx.Pump();
                Thread.Sleep(50);
                _syncCtx.Pump();

                // Reset the player ready state and try again with SuppressYield
                CombatManager.Instance.UndoReadyToEndTurn(player);
                _syncCtx.Pump();

                YieldPatches.SuppressYield = true;
                try
                {
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    _syncCtx.Pump();
                }
                finally
                {
                    YieldPatches.SuppressYield = false;
                }

                var poll = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; poll.ElapsedMilliseconds < 1000; i++)
                {
                    _syncCtx.Pump();
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (IsPlayPhase()) break;
                    PollPause(i);
                }
            }
            catch (Exception ex) { Log($"Cancel retry: {ex.Message}"); }

            // NUCLEAR OPTION: If STILL stuck after 2 attempts, use ThreadPool to force
            // the enemy turn processing to complete with SuppressYield permanently on.
            if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
            {
                var stuckState = CombatManager.Instance.DebugOnlyGetState();
                var stuckEnemies = stuckState?.Enemies?.Where(e => e != null && e.IsAlive)
                    .Select(e => $"{e.Monster?.GetType().Name}(hp={e.CurrentHp})").ToList();
                Log($"EndTurn STILL stuck after retry — nuclear fallback. Round={stuckState?.RoundNumber}, " +
                    $"Enemies=[{string.Join(",", stuckEnemies ?? new())}], " +
                    $"IsPlayPhase={IsPlayPhase()}, " +
                    $"IsInProgress={CombatManager.Instance.IsInProgress}, " +
                    $"ActionExecutor.IsRunning={RunManager.Instance.ActionExecutor.IsRunning}");
                try
                {
                    // Cancel again and undo
                    RunManager.Instance.ActionExecutor.Cancel();
                    _syncCtx.Pump();
                    CombatManager.Instance.UndoReadyToEndTurn(player);
                    _syncCtx.Pump();
                    Thread.Sleep(50);

                    // Run EndTurn on ThreadPool with SuppressYield permanently on
                    YieldPatches.SuppressYield = true;
                    var endTurnTask = Task.Run(() =>
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                    });

                    // Aggressively pump sync context while waiting (up to 5 seconds)
                    for (int i = 0; i < 500; i++)
                    {
                        _syncCtx.Pump();
                        if (endTurnTask.IsCompleted) break;
                        if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                        if (IsPlayPhase()) break;
                        Thread.Sleep(10);
                    }
                    YieldPatches.SuppressYield = false;

                    // If still not play phase, try just waiting a bit more
                    if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            _syncCtx.Pump();
                            Thread.Sleep(10);
                            if (IsPlayPhase() || !CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                                break;
                        }
                    }

                    if (IsPlayPhase())
                        Log("Nuclear fallback SUCCEEDED — play phase resumed");
                    else
                    {
                        // Never report a made-up defeat: the combat is still running. Say the engine
                        // is stuck so the client can reset instead of learning from a fake game_over.
                        Log("Nuclear fallback FAILED — engine stuck at end of turn");
                        return new Dictionary<string, object?>
                        {
                            ["type"] = "error",
                            ["message"] = "engine_stuck: the enemy turn did not finish; reset the run",
                            ["engine_stuck"] = true,
                        };
                    }
                }
                catch (Exception ex)
                {
                    Log($"Nuclear fallback error: {ex.Message}");
                    YieldPatches.SuppressYield = false;
                }
            }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCardReward(Player player, Dictionary<string, object?>? args)
    {
        // Handle event-triggered card reward (blocking GetSelectedCardReward)
        if (_cardSelector.HasPendingReward)
        {
            if (args == null || !args.ContainsKey("card_index"))
                return Error("select_card_reward requires 'card_index'");
            var idx = Convert.ToInt32(args["card_index"]);
            var offered = _cardSelector.PendingRewardCards?.Count ?? 0;
            if (idx < 0 || idx >= offered)
                return Error($"card_index {idx} out of range 0..{offered - 1}");
            Log($"Resolving event card reward: index {idx}");
            _cardSelector.ResolveReward(idx);
            if (_manualFlow) return ResumeBackgroundWork();
            PollUntil(RewardConsumed, 1000);
            _syncCtx.Pump();
            WaitForActionExecutor();
            WaitForEventOption();
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("select_card_reward requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var cards = _pendingCardReward.Cards.ToList();
        if (cardIndex < 0 || cardIndex >= cards.Count)
            return Error($"Invalid card index {cardIndex}, {cards.Count} cards available");

        var card = cards[cardIndex];
        Log($"Selected card reward: {card.GetType().Name}");

        // Add card to deck
        try
        {
            MegaCrit.Sts2.Core.Commands.CardPileCmd
                .Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck)
                .GetAwaiter().GetResult();
            _syncCtx.Pump();
            RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(card);
        }
        catch (Exception ex) { Log($"Add card to deck: {ex.Message}"); }

        _pendingCardReward = null;
        // Check if more rewards pending
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            Log("Skipping event card reward");
            _cardSelector.SkipReward();
            if (_manualFlow) return ResumeBackgroundWork();
            PollUntil(RewardConsumed, 1000);
            _syncCtx.Pump();
            WaitForActionExecutor();
            WaitForEventOption();
            return DetectDecisionPoint();
        }
        if (_pendingCardReward != null)
        {
            Log("Skipping card reward");
            _pendingCardReward.OnSkipped();
            _pendingCardReward = null;
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyCard(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("buy_card requires 'card_index'");

        var idx = Convert.ToInt32(args["card_index"]);
        var allEntries = merchantRoom.GetLocalInventory().CharacterCardEntries
            .Concat(merchantRoom.GetLocalInventory().ColorlessCardEntries).ToList();
        if (idx < 0 || idx >= allEntries.Count)
            return Error($"Invalid card index {idx}");

        var entry = allEntries[idx];
        if (!entry.IsStocked) return Error("Card already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought card: {entry.CreationResult?.Card?.GetType().Name ?? "?"} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy card failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyRelic(Player player, Dictionary<string, object?>? args)
    {
        var inv = _runState?.CurrentRoom is MerchantRoom merchantRoom
            ? merchantRoom.GetLocalInventory()
            : HeadlessUiPatches.ActiveFakeMerchant(player)?.Inventory;
        if (inv == null)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("buy_relic requires 'relic_index'");

        var idx = Convert.ToInt32(args["relic_index"]);
        var entries = inv.RelicEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid relic index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Relic already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            // The pickup effect can open a card_select (e.g. KIFUDA → enchant up to 3 with
            // Adroit, #80). Run the purchase on a background task and yield as soon as a
            // pending selection appears so the caller can resolve it; the background task
            // continues once the selector's TCS is fed by select_cards.
            // entry.Model is cleared once the purchase completes; capture it for logging.
            var relicName = entry.Model?.GetType().Name ?? "?";
            var cost = entry.Cost;
            var task = Task.Run(() => entry.OnTryPurchaseWrapper(inv));
            if (_manualFlow) TrackBackground(task);
            var poll = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; poll.ElapsedMilliseconds < 1000; i++)
            {
                _syncCtx.Pump();
                if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                if (_pendingBundles != null) break;
                if (task.IsCompleted) break;
                PollPause(i);
            }
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
            {
                Log($"Buy relic {relicName}: yielded for pending selection");
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted) task.Wait(2000);
            _syncCtx.Pump();
            if (task.IsFaulted) throw task.Exception!.GetBaseException();
            Log($"Bought relic: {relicName} for {cost}g");
        }
        catch (Exception ex)
        {
            Log($"Buy relic failed: {ex.GetBaseException()}");
            return Error($"Buy relic failed: {ex.GetBaseException().Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("buy_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var entries = merchantRoom.GetLocalInventory().PotionEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid potion index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Potion already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            // entry.Model is cleared once the purchase completes; capture it for logging.
            var potionName = entry.Model?.GetType().Name ?? "?";
            var cost = entry.Cost;
            entry.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought potion: {potionName} for {cost}g");
        }
        catch (Exception ex)
        {
            // Potion purchase sometimes NullRefs in headless (missing potion slot UI)
            Log($"Buy potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRemoveCard(Player player)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");

        var removal = merchantRoom.GetLocalInventory().CardRemovalEntry;
        if (removal == null) return Error("No card removal available");
        if (player.Gold < removal.Cost) return Error("Not enough gold");

        try
        {
            // Run on background thread so card selection can pause (same pattern as event options)
            var task = Task.Run(() => removal.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()));
            _shopRemovalTask = task;
            if (_manualFlow) TrackBackground(task);
            var poll = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; poll.ElapsedMilliseconds < 1000; i++)
            {
                _syncCtx.Pump();
                if (_cardSelector.HasPending) break;
                if (task.IsCompleted) break;
                PollPause(i);
            }
            if (_cardSelector.HasPending)
            {
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted) task.Wait(2000);
            _syncCtx.Pump();
            Log($"Removed card for {removal.Cost}g");
        }
        catch (Exception ex) { return Error($"Remove card failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectBundle(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingBundleTcs == null || _pendingBundles == null)
            return Error("No pending bundle selection");
        if (args == null || !args.ContainsKey("bundle_index"))
            return Error("select_bundle requires 'bundle_index'");

        var idx = Convert.ToInt32(args["bundle_index"]);
        if (idx < 0 || idx >= _pendingBundles.Count)
            return Error($"bundle_index {idx} out of range 0..{_pendingBundles.Count - 1}");
        Log($"Bundle selection: pack {idx}");
        var bundles = _pendingBundles;
        var tcs = _pendingBundleTcs;
        _pendingBundles = null;
        _pendingBundleTcs = null;

        // Set result directly (no ContinueWith/ThreadPool)
        var selected = bundles[idx];
        tcs.TrySetResult(selected);
        if (_manualFlow) return ResumeBackgroundWork();

        _syncCtx.Pump();
        WaitForActionExecutor();
        WaitForEventOption();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCards(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (args == null || !args.ContainsKey("indices"))
            return Error("select_cards requires 'indices' (comma-separated card indices)");

        var indicesStr = args["indices"]?.ToString() ?? "";
        if (!TryParseIndices(indicesStr, out var parsed))
            return Error($"indices '{indicesStr}' must be comma-separated integers");
        var optionCount = _cardSelector.PendingOptions?.Count ?? 0;
        if (parsed.Distinct().Count() != parsed.Count || parsed.Any(i => i < 0 || i >= optionCount))
            return Error($"indices must be distinct and within 0..{optionCount - 1}");
        if (parsed.Count < _cardSelector.PendingMinSelect || parsed.Count > _cardSelector.PendingMaxSelect)
            return Error($"select {_cardSelector.PendingMinSelect}–{_cardSelector.PendingMaxSelect} cards, got {parsed.Count}");
        var indices = parsed.ToArray();

        Log($"Card selection: indices [{string.Join(",", indices)}]");
        ResolveSelectionAndResumeTurn(() => _cardSelector.ResolvePendingByIndices(indices));
        SettleCombatActions();
        if (_manualFlow) return ResumeBackgroundWork();
        _syncCtx.Pump();
        WaitForActionExecutor();
        WaitForEventOption();

        // Extra wait for rest-site SMITH: the background ChooseLocalOption task
        // needs time to complete the upgrade after card selection resolves.
        if (_runState?.CurrentRoom is RestSiteRoom)
        {
            // Let the rest option finish (the Smith upgrade runs after the selection resolves).
            PollUntil(() => _restOptionTask == null || _restOptionTask.IsCompleted || HasPendingSelection, 2000);
            WaitForActionExecutor();
            // Force to map after SMITH completes (same pattern as HEAL)
            Log("Card selection in rest site (SMITH), forcing to map");
            ForceToMap();
            return MapSelectState();
        }

        // Extra wait for shop card removal: the purchase task needs to finish
        if (_runState?.CurrentRoom is MerchantRoom)
        {
            PollUntil(() => _shopRemovalTask == null || _shopRemovalTask.IsCompleted || HasPendingSelection, 2000);
            WaitForActionExecutor();
            Log("Card selection in shop (card removal), refreshing shop state");
        }

        return DetectDecisionPoint();
    }

    /// <summary>
    /// Auto flow: an event option whose effect opened a selection keeps running on the thread
    /// pool after the selection resolves (Wood Carvings transforms the chosen card, then
    /// finishes the event). Let it finish, or raise its next selection, before exporting the
    /// decision; otherwise the stale options go out and the next choose_option enumerates
    /// them while the task replaces them ("Collection was modified").
    /// </summary>
    private void WaitForEventOption()
    {
        if (_eventOptionTask is { IsCompleted: false } task && _runState?.CurrentRoom is EventRoom)
            WaitForTaskOrPending(task);
    }

    /// <summary>
    /// Resolve a card selection. If it was raised outside the player's play phase (an enemy move
    /// mid enemy turn, or a start-of-turn effect), keep driving the turn with Task.Yield suppressed
    /// until play resumes, combat ends, or another selection is raised — the same conditions
    /// DoEndTurn waits on.
    /// </summary>
    private void ResolveSelectionAndResumeTurn(Action resolve)
    {
        var player = _runState?.Players[0];
        bool midTurn = CombatManager.Instance.IsInProgress && !IsPlayPhase();
        if (!midTurn)
        {
            resolve();
            return;
        }
        YieldPatches.SuppressYield = true;
        try
        {
            resolve();
            for (int i = 0; i < 400; i++)
            {
                _syncCtx.Pump();
                if (HasPendingSelection) break;
                if (!CombatManager.Instance.IsInProgress || player?.Creature?.IsDead == true) break;
                if (IsPlayPhase()) break;
                Thread.Sleep(5);
            }
        }
        finally { YieldPatches.SuppressYield = false; }
    }

    /// <summary>
    /// In combat, card effects can enqueue follow-up actions that run on the thread pool (e.g.
    /// Decisions, Decisions auto-playing a chosen card that asks for another selection). Wait until
    /// the executor has been idle for a short quiet period, or a new selection appears, so the
    /// exported decision is not stale.
    /// </summary>
    private void SettleCombatActions()
    {
        if (!CombatManager.Instance.IsInProgress) return;
        var executor = RunManager.Instance.ActionExecutor;
        var queues = RunManager.Instance.ActionQueueSet;
        int quiet = 0;
        for (int i = 0; i < 1000; i++)
        {
            _syncCtx.Pump();
            if (HasPendingSelection || !CombatManager.Instance.IsInProgress) return;
            // Fully settled: executor idle, no queued actions, no queued continuations. Confirm
            // once after yielding (a pool thread may be about to post) and return; this used to
            // cost a fixed 8 × 2 ms quiet period on every card play.
            if (!executor.IsRunning && queues.IsEmpty && _syncCtx.IsIdle)
            {
                Thread.Yield();
                _syncCtx.Pump();
                if (!executor.IsRunning && queues.IsEmpty && _syncCtx.IsIdle) return;
            }
            // Executor idle but actions still queued (e.g. waiting on a pause): old quiet period.
            quiet = executor.IsRunning ? 0 : quiet + 1;
            if (quiet >= 8) return;
            Thread.Sleep(2);
        }
    }

    private Dictionary<string, object?> DoSkipSelect(Player player)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (_cardSelector.PendingMinSelect > 0 && !_cardSelector.PendingCancelable)
            return Error($"This selection requires at least {_cardSelector.PendingMinSelect} card(s)");
        Log("Skipping card selection");
        ResolveSelectionAndResumeTurn(() => _cardSelector.CancelPending());
        SettleCombatActions();
        if (_manualFlow) return ResumeBackgroundWork();
        _syncCtx.Pump();
        WaitForActionExecutor();
        // Like select_cards: an event option (e.g. Neow's Hefty Tablet) keeps running after the
        // selection; without this the next observation was the stale pre-choice event page.
        WaitForEventOption();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoUsePotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("use_potion requires 'potion_index'");
        if (HasPendingSelection)
            return Error("A selection is pending; resolve it before using a potion");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");
        if (!player.CanUseOrRemovePotions)
            return Error("Potions cannot be used right now");
        if (!CanUsePotion(potion, CombatManager.Instance.IsInProgress))
            return Error($"{potion.Id.Entry} cannot be used here ({potion.Usage})");

        // Determine target based on potion's TargetType first, then fall back to target_index
        Creature? target = null;
        var potionTargetType = potion.TargetType;

        // Self-targeting potions (Flex, Fortifier, etc.) ALWAYS target the player
        // regardless of any target_index the caller provides. TargetedNoCreature (Foul Potion
        // thrown at a merchant) has no creature target.
        if (potionTargetType == TargetType.Self)
        {
            target = player.Creature;
        }
        else if (potionTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided, otherwise pick first alive enemy
            if (args.TryGetValue("target_index", out var tObj) && tObj != null)
            {
                var targetIdx = Convert.ToInt32(tObj);
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                if (combatState != null)
                {
                    var enemies = combatState.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIdx >= 0 && targetIdx < enemies.Count)
                        target = enemies[targetIdx];
                }
            }
            // Same single-enemy rule as play_card: only auto-target when unambiguous (#79).
            if (target == null && CombatManager.Instance.IsInProgress)
            {
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                var alive = combatState?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
                if (alive.Count == 1)
                    target = alive[0];
                else if (alive.Count > 1)
                    return Error($"Potion {potion.Id.Entry} targets a single enemy (AnyEnemy); " +
                                 $"'target_index' is required when multiple enemies are alive ({alive.Count}).");
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        Log($"Using potion: {potion.GetType().Name} at slot {idx} target={target?.GetType().Name ?? "none"}");
        try
        {
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
            WaitForActionExecutor();
            SettleCombatActions();
            _syncCtx.Pump();

            // Effect may require card_select before the potion slot clears — do not discard as "stuck".
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return DetectDecisionPoint();

            // The engine cancels a use it rejects; the potion then stays (it is never discarded here).
            var afterPotions = player.Potions?.ToList() ?? new();
            if (afterPotions.Contains(potion))
            {
                Log("Potion was not used (the engine cancelled the action)");
                return Error($"{potion.Id.Entry} could not be used");
            }
        }
        catch (Exception ex)
        {
            Log($"Use potion failed: {ex.Message}");
            return Error($"Use potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("discard_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");
        if (_manualFlow) return ManualChooseOption(player, optionIndex);

        // Dispatch based on ROOM TYPE (not event state) to avoid cross-contamination
        if (_runState?.CurrentRoom is RestSiteRoom restSiteRoom)
        {
            Log($"Rest site: choosing option {optionIndex}");
            try
            {
                // Run on background thread so Smith card selection can pause
                var task = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                _restOptionTask = task;
                var poll = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; poll.ElapsedMilliseconds < 1000; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending) break;
                    if (task.IsCompleted) break;
                    PollPause(i);
                }
                if (_cardSelector.HasPending)
                {
                    WaitForActionExecutor();
                    return DetectDecisionPoint();
                }
                if (!task.IsCompleted) task.Wait(2000);
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                Log($"Rest site ChooseLocalOption failed: {ex.Message}");
            }

            // After non-Smith rest site options (HEAL, etc.), the options may not clear.
            // Wait for the action to complete (heal/dig), then force transition to map.
            if (!_cardSelector.HasPending)
            {
                Log("Rest site: option chosen (non-Smith), waiting for action then forcing to map");
                // Give the action time to complete (heal HP, dig for relic, etc.)
                WaitForActionExecutor();
                PollUntil(EngineIdle, 200);
                WaitForActionExecutor();
                ForceToMap();
                return MapSelectState();
            }
        }
        // For events — use EventSynchronizer
        // Run Chosen() on a background thread so card selections can pause
        else if (_runState?.CurrentRoom is EventRoom)
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            var localEvent = eventSync?.GetLocalEvent();
            if (localEvent != null && !localEvent.IsFinished)
            {
                var options = localEvent.CurrentOptions;
                var optCountBefore = options?.Count ?? 0;
                if (options != null && optionIndex >= 0 && optionIndex < options.Count)
                {
                    try
                    {
                        _eventOptionChosen = true;
                        _lastEventOptionCount = options.Count;
                        // Run on thread pool so GetSelectedCards/GetSelectedCardReward can block.
                        // Read the option here: the live list can change before the pool thread runs.
                        var option = options[optionIndex];
                        var task = Task.Run(() => option.Chosen());
                        _ = task.ContinueWith(t => PatchReport.EngineWarning(
                                $"Event option failed: {t.Exception?.GetBaseException().Message}"),
                            TaskContinuationOptions.OnlyOnFaulted);
                        _eventOptionTask = task;
                        var poll = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; poll.ElapsedMilliseconds < 1000; i++)
                        {
                            _syncCtx.Pump();
                            if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                            if (_pendingBundles != null || _pendingCrystalSphere != null) break;
                            if (task.IsCompleted) break;
                            PollPause(i);
                        }
                        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null || _pendingCrystalSphere != null)
                        {
                            WaitForActionExecutor();
                            return DetectDecisionPoint();
                        }
                        if (!task.IsCompleted) task.Wait(2000);
                        _syncCtx.Pump();
                    }
                    catch (Exception ex) { Log($"Event choose: {ex.Message}"); }
                }

                // Note: do NOT force-finish on `optCountAfter == optCountBefore`. Events can
                // legitimately stay interactive with the same option count (Slippery Bridge
                // Hold On loops until Overcome is chosen, #59). Trust IsFinished and let the
                // next DetectDecisionPoint return the current options for the next choice.
            }
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        Log("Leaving room");
        try { RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult(); }
        catch { }
        _syncCtx.Pump();
        WaitForActionExecutor();

        // If still in a non-combat room, force to map
        var room = _runState?.CurrentRoom;
        if (room is RestSiteRoom || room is MerchantRoom || room is EventRoom || room is TreasureRoom)
        {
            Log("Force leaving non-combat room to map");
            try
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"Force leave: {ex.Message}"); }
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");

        // Check if we need to move to next act (boss defeated)
        var room = _runState?.CurrentRoom;
        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                if (IsFirstOfDoubleBoss())
                {
                    ForceToMap();
                    return MapSelectState();
                }
                // Final act boss → victory (same rule as DetectPostCombatState, #81).
                if (IsFinalAct())
                {
                    Log($"Final boss defeated via Proceed (Act {_runState.CurrentActIndex + 1}), reporting victory");
                    return GameOverState(true);
                }
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
        }

        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    #endregion

    #region Decision Point Detection

    private Dictionary<string, object?> DetectDecisionPoint()
    {
        if (_runState == null)
            return Error("No run in progress");

        var player = _runState.Players[0];
        if (_manualFlow) SyncFlowRoom();

        // Check game over (death)
        if (player.Creature != null && player.Creature.IsDead)
        {
            return GameOverState(false);
        }

        // Check if there's a pending bundle selection (Scroll Boxes: pick 1 of N packs)
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            var bundles = _pendingBundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card => CardInfo(card, PileType.None, upgradePreview: false)).ToList(),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = RunContext(),
                ["bundles"] = bundles,
                ["player"] = PlayerSummary(player),
            };
        }

        // Crystal Sphere minigame (event) waiting for a divination
        if (_pendingCrystalSphere != null)
            return CrystalSphereState(player);

        // Check if there's a pending card reward from event (GetSelectedCardReward blocking)
        if (_cardSelector.HasPendingReward)
        {
            var rewardCards = _cardSelector.PendingRewardCards!;
            var cards = rewardCards.Select((cr, i) =>
            {
                var info = CardInfo(cr.Card, PileType.None);
                info["index"] = i;
                return info;
            }).ToList();

            var alternatives = (_cardSelector.PendingRewardAlternatives ?? new List<CardRewardAlternative>())
                .Select((alt, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = alt.OptionId,
                    ["name"] = _loc.Text("card_reward_ui", "OPTION_" + alt.OptionId.ToUpperInvariant() + ".name"),
                    ["ends_selection"] = alt.AfterSelected != PostAlternateCardRewardAction.DoNothing,
                }).ToList();
            var canSkip = !_manualFlow || alternatives.Any(a => string.Equals(a["id"] as string, "Skip", StringComparison.OrdinalIgnoreCase));
            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = RunContext(),
                ["cards"] = cards,
                ["can_skip"] = canSkip,
                ["alternatives"] = alternatives.Count > 0 ? alternatives : null,
                ["from_event"] = !_manualFlow || TopRewardsScreen == null,
                ["player"] = PlayerSummary(_runState!.Players[0]),
            };
        }

        // Check if there's a pending card selection (upgrade, remove, transform, start-of-turn powers)
        checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            var opts = _cardSelector.PendingOptions.Select((card, i) =>
            {
                var info = CardInfo(card, card.Pile?.Type ?? PileType.None);
                info["index"] = i;
                return info;
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = RunContext(),
                ["cards"] = opts,
                ["min_select"] = _cardSelector.PendingMinSelect,
                ["max_select"] = _cardSelector.PendingMaxSelect,
                ["cancelable"] = _cardSelector.PendingCancelable,
                ["prompt"] = string.IsNullOrEmpty(_cardSelector.PendingPrompt) ? null : _cardSelector.PendingPrompt,
                ["source"] = string.IsNullOrEmpty(_cardSelector.PendingSource) ? null : _cardSelector.PendingSource,
                ["combat"] = CombatSnapshot(player),
                ["player"] = PlayerSummary(player),
            };
        }

        // Manual flow: an open rewards screen (combat, treasure, rest-site or event rewards)
        if (_manualFlow && TopRewardsScreen != null)
        {
            return RewardsScreenState(player);
        }

        // Check if there's a pending card reward
        if (_pendingCardReward != null)
        {
            return CardRewardState(player, _runState.CurrentRoom as CombatRoom);
        }

        // Check if RunManager reports game over (victory)
        if (RunManager.Instance.IsGameOver)
        {
            return GameOverState(true);
        }

        var room = _runState.CurrentRoom;

        // Map room — need to select a node
        if (room is MapRoom || room == null)
        {
            return MapSelectState();
        }

        // Combat room
        if (room is CombatRoom combatRoom)
        {
            // With Task.Yield() patched, combat init should be synchronous
            _syncCtx.Pump();
            WaitForActionExecutor();

            // Re-check for pending card selections AFTER pump (BUG-024: start-of-turn effects
            // like Tools of Trade create card selections during Pump, AFTER the initial HasPending check)
            if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
            {
                goto checkCardSelect;  // Jump back to card_select handling
            }

            if (CombatManager.Instance.IsInProgress && IsPlayPhase())
            {
                return CombatPlayState(player);
            }
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                return DetectPostCombatState(player, combatRoom);
            }
            // Fallback: wait (up to ~2s) for the play phase; combats started from an event option
            // run their turn loop off the main thread.
            for (int i = 0; i < 1000; i++)
            {
                _syncCtx.Pump();
                if (HasPendingSelection) return DetectDecisionPoint();
                Thread.Sleep(2);
                if (IsPlayPhase()) return CombatPlayState(player);
                if (!CombatManager.Instance.IsInProgress) return DetectPostCombatState(player, combatRoom);
            }
            return CombatPlayState(player);
        }

        // Event room
        if (room is EventRoom eventRoom)
        {
            return EventChoiceState(eventRoom);
        }

        // Rest site
        if (room is RestSiteRoom restRoom)
        {
            return RestSiteState(restRoom);
        }

        // Merchant/Shop
        if (room is MerchantRoom merchantRoom)
        {
            return ShopState(merchantRoom, player);
        }

        // Treasure room
        if (room is TreasureRoom treasureRoom)
        {
            return TreasureState(treasureRoom);
        }

        // Fallback
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
            ["context"] = RunContext(),
            ["room_type"] = room?.GetType().Name,
            ["message"] = "Unknown room type or state",
        };
    }

    private Dictionary<string, object?> MapSelectState()
    {
        var map = _runState?.Map;
        if (map == null)
        {
            Log("Map is null, generating...");
            try
            {
                RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
                _syncCtx.Pump();
                map = _runState?.Map;
            }
            catch (Exception ex)
            {
                Log($"GenerateMap failed: {ex.Message}");
            }
            if (map == null)
                return Error("No map available");
        }
        var choices = TravelablePoints(map)
            .Select(p => new Dictionary<string, object?>
            {
                ["col"] = (int)p.coord.col,
                ["row"] = (int)p.coord.row,
                ["type"] = p.PointType.ToString(),
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "map_select",
            ["context"] = RunContext(),
            ["choices"] = choices,
            // The whole act map (as get_map returns it): a player always sees it when choosing.
            ["map"] = GetFullMap().Where(kv => kv.Key != "type").ToDictionary(kv => kv.Key, kv => kv.Value),
            ["player"] = PlayerSummary(_runState!.Players[0]),
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_id"] = _runState.Act?.Id.Entry,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
        };
    }

    /// <summary>
    /// The nodes the map screen lets you travel to, as NMapScreen.RecalculateTravelability computes
    /// them: only the act's starting point (its Ancient) before any node is visited; the second
    /// boss after the first; the boss after the last row; otherwise MapTravel.GetTravelablePointsFrom,
    /// which also covers free travel (Winged Boots, Flight).
    /// </summary>
    private List<MapPoint> TravelablePoints(ActMap map)
    {
        var visited = _runState!.VisitedMapCoords;
        if (visited == null || visited.Count == 0)
            return new List<MapPoint> { map.StartingMapPoint };
        var last = visited[visited.Count - 1];
        if (map.SecondBossMapPoint != null && last == map.BossMapPoint.coord)
            return new List<MapPoint> { map.SecondBossMapPoint };
        if (last.row == map.GetRowCount() - 1)
            return new List<MapPoint> { map.BossMapPoint };
        var point = map.GetPoint(last);
        if (point == null)
        {
            Log($"Last visited coord ({last.col},{last.row}) not on the map; falling back to the starting point");
            return new List<MapPoint> { map.StartingMapPoint };
        }
        return MapTravel.GetTravelablePointsFrom(_runState, point).ToList();
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        // Track last known HP for accurate game_over reporting (BUG-005)
        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        // Alive enemies in the same order play_card's AnyEnemy targeting uses, so
        // damage_by_target[i].target_index aligns with the target_index clients pass.
        var aliveEnemiesForTargeting = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive).ToList() ?? new();

        var hand = pcs?.Hand?.Cards?.Select((c, i) =>
        {
            // Export the *currently resolved* stat values, not the card base: refresh the
            // DynamicVar previews (mirrors NCard.UpdateVisuals) so damage reflects Strength/Weak,
            // block reflects Frail, calculateddamage reflects current Block, etc. ClearPreview
            // resets PreviewValue to BaseValue, and only damage/block/calculated vars override it,
            // so reading PreviewValue uniformly is safe. Issues #65 #69 #70 #71 #74 #75.
            var stats = new Dictionary<string, object?>();
            try
            {
                c.DynamicVars.ClearPreview();
                c.UpdateDynamicVarPreview(
                    MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.Normal,
                    c.CurrentTarget, c.DynamicVars);
                foreach (var dv in c.DynamicVars.Values)
                {
                    stats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;
                }
                // Restore the live card to base state. UpdateDynamicVarPreview mutates the
                // card's preview (and for self-cost cards like Momentum Strike, leaving it in
                // preview state corrupts the subsequent PlayCardAction — card stays in hand).
                c.DynamicVars.ClearPreview();
            }
            catch { }

            // Per-target resolved damage for attack cards: the scalar `stats` above use the
            // card's CurrentTarget, but a single value can't capture target-specific modifiers
            // (Vulnerable #60, Slow #77) or conditional hit counts (Dismantle #78, X-cost
            // Whirlwind #82). Re-run the preview per enemy via MultiCreatureTargeting, the same
            // path the game uses to draw multi-target previews, and read the resolved vars.
            List<Dictionary<string, object?>>? damageByTarget = null;
            var cardText = c.Type == CardType.Attack ? CardText(c, PileType.Hand) : "";
            bool hasHitVar = c.DynamicVars.Values.Any(v => v.Name is "Repeat" or "CalculatedHits");
            bool hitsKnown = !(cardText.Contains("twice", StringComparison.OrdinalIgnoreCase) && !hasHitVar)
                             && !(hasHitVar && cardText.Contains("If ", StringComparison.Ordinal));
            if (c.Type == CardType.Attack && aliveEnemiesForTargeting.Count > 0
                && (c.TargetType == TargetType.AnyEnemy || c.TargetType == TargetType.AllEnemies))
            {
                damageByTarget = new List<Dictionary<string, object?>>();
                for (int ti = 0; ti < aliveEnemiesForTargeting.Count; ti++)
                {
                    var tgt = aliveEnemiesForTargeting[ti];
                    try
                    {
                        c.DynamicVars.ClearPreview();
                        c.UpdateDynamicVarPreview(
                            MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.MultiCreatureTargeting,
                            tgt, c.DynamicVars);
                        var tstats = new Dictionary<string, object?>();
                        foreach (var dv in c.DynamicVars.Values)
                            tstats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;

                        // Per-hit damage = calculateddamage (override cards) or damage.
                        int? perHit = tstats.TryGetValue("calculateddamage", out var cdv) && cdv is int cdi && cdi > 0
                            ? cdi
                            : (tstats.TryGetValue("damage", out var dv2) && dv2 is int di ? di : (int?)null);
                        // Hit count: explicit `repeat` var if present, else for X-cost attacks
                        // the hit count is the current X (= available energy), e.g. Whirlwind (#82).
                        // `calculatedhits` (Barrage, Finisher, Lunar Blast: hits counted from the
                        // board) wins over the static `repeat`.
                        int repeat = tstats.TryGetValue("calculatedhits", out var chv) && chv is int chi && chi > 0 ? chi
                            : tstats.TryGetValue("repeat", out var rv) && rv is int ri && ri > 0 ? ri : 1;
                        if (repeat == 1 && c.EnergyCost?.CostsX == true && pcs != null)
                            repeat = pcs.Energy;
                        // Dismantle hits twice when the target is Vulnerable (#78). The doubled
                        // hit count lives in Dismantle.OnPlay, not in any DynamicVar preview or
                        // the Hook.ModifyAttackHitCount path (which needs an AttackCommand we
                        // don't have at preview time), so it's special-cased by card entry.
                        if (repeat == 1 && c.Id.Entry == "DISMANTLE" && tgt.Powers != null
                            && tgt.Powers.Any(p => p?.Id.Entry == "VULNERABLE_POWER"))
                            repeat = 2;

                        var row = new Dictionary<string, object?>
                        {
                            ["target_index"] = ti,
                            ["name"] = MonsterName(tgt.Monster),
                        };
                        if (perHit != null) row["damage"] = perHit;
                        if (repeat > 1)
                        {
                            row["repeat"] = repeat;
                            if (perHit != null) row["total_damage"] = perHit * repeat;
                        }
                        // The hit count is only a guess when the text hard-codes extra hits
                        // ("twice", Twin Strike) or the repeat is conditional (Spite: "If you lost
                        // HP this turn"): say so instead of predicting a wrong total.
                        if (!hitsKnown) row["hits_known"] = false;
                        damageByTarget.Add(row);
                    }
                    catch { }
                    finally { c.DynamicVars.ClearPreview(); }
                }
            }

            // Use CurrentStarCost (combat-modified) for UI/can_play; BaseStarCost ignores temporary reductions.
            var starCost = c.CurrentStarCost;
            var cardInfo = CardInfo(c, PileType.Hand, upgradePreview: false);
            cardInfo["index"] = i;
            cardInfo["can_play"] = c.CanPlay(out _, out _);
            cardInfo["target_type"] = c.TargetType.ToString();
            cardInfo["stats"] = stats.Count > 0 ? stats : null;
            if (starCost > 0)
            {
                cardInfo["star_cost"] = starCost;
                // BUG-007: Override can_play for star-cost cards when player lacks stars
                if (pcs != null && pcs.Stars < starCost)
                    cardInfo["can_play"] = false;
            }
            if (damageByTarget != null && damageByTarget.Count > 0)
                cardInfo["damage_by_target"] = damageByTarget;
            return cardInfo;
        }).ToList() ?? new();

        var playerCreatures = combatState?.PlayerCreatures?.ToList();

        var allEnemies = combatState?.Enemies?.ToList() ?? new();
        var enemies = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive)
            .Select((e, i) =>
            {
                // Extract detailed intent info
                var intents = new List<Dictionary<string, object?>>();
                try
                {
                    if (e.Monster?.NextMove?.Intents != null)
                    {
                        foreach (var intent in e.Monster.NextMove.Intents)
                        {
                            var intentInfo = new Dictionary<string, object?>
                            {
                                ["type"] = intent.IntentType.ToString(),
                            };
                            // Get damage for attack intents
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null)
                            {
                                try
                                {
                                    // For multi-hit attacks, expose per-hit `damage` (matching the
                                    // game's intent description, which pairs GetSingleDamage with
                                    // Repeat) plus an explicit `total_damage`. Reporting the total in
                                    // `damage` while also reporting `hits` let clients compute
                                    // damage*hits and double-count incoming damage (#67).
                                    var hits = atk.Repeats;
                                    if (hits > 1)
                                    {
                                        intentInfo["damage"] = atk.GetSingleDamage(playerCreatures, e);
                                        intentInfo["hits"] = hits;
                                        intentInfo["total_damage"] = atk.GetTotalDamage(playerCreatures, e);
                                    }
                                    else
                                    {
                                        intentInfo["damage"] = atk.GetTotalDamage(playerCreatures, e);
                                    }
                                }
                                catch { }
                            }
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.StatusIntent status)
                                intentInfo["card_count"] = status.CardCount;
                            // The intent's hover tip: what the UI shows when you inspect it.
                            try
                            {
                                if (intent.HasIntentTip && playerCreatures != null)
                                {
                                    // Cached on everything the tip shows (type, numbers, owner).
                                    var tipKey = $"it|{intent.GetType().Name}|{e.Monster?.Id}|{intentInfo.GetValueOrDefault("damage")}|{intentInfo.GetValueOrDefault("hits")}|{intentInfo.GetValueOrDefault("card_count")}";
                                    var label = Cached(tipKey + "|l", () => { var t = intent.GetHoverTip(playerCreatures, e); return CleanText(t.Title) + "\u0000" + CleanText(t.Description); });
                                    var parts = label.Split('\u0000');
                                    if (!string.IsNullOrWhiteSpace(parts[0])) intentInfo["label"] = parts[0];
                                    if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) intentInfo["description"] = parts[1];
                                }
                            }
                            catch { }
                            intents.Add(intentInfo);
                        }
                    }
                }
                catch { }

                // Enemy powers
                var ePowers = e.Powers?.Select(PowerInfo).ToList();

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    // Stable across deaths (index renumbers among alive enemies; target_index uses index).
                    ["slot"] = allEnemies.IndexOf(e),
                    ["id"] = e.Monster?.Id.ToString(),
                    ["name"] = MonsterName(e.Monster),
                    ["hp"] = e.CurrentHp,
                    ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["intents"] = intents.Count > 0 ? intents : null,
                    ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
                    ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                };
            }).ToList() ?? new();

        // Player powers/buffs
        var playerPowers = player.Creature?.Powers?.Select(PowerInfo).ToList();

        var result = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_play",
            ["context"] = RunContext(),
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["player"] = PlayerSummary(player),
            ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
            ["exhaust_pile_count"] = pcs?.ExhaustPile?.Cards?.Count ?? 0,
            // The UI lets you view all three piles. The draw pile is sorted so its order (the
            // upcoming draws) stays hidden, as in the game's draw pile view.
            ["draw_pile"] = PileCards(pcs?.DrawPile?.Cards, sort: true),
            ["discard_pile"] = PileCards(pcs?.DiscardPile?.Cards, sort: false),
            ["exhaust_pile"] = PileCards(pcs?.ExhaustPile?.Cards, sort: false),
        };

        // Character-specific mechanics
        try
        {
            // Defect: Orbs
            var orbQueue = pcs?.OrbQueue;
            if (orbQueue?.Orbs?.Count > 0)
            {
                result["orbs"] = orbQueue.Orbs.Select((orb, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Text("orbs", orb.Id.Entry + ".title"),
                    ["type"] = orb.GetType().Name.Replace("Orb", ""),
                    ["passive"] = (int)orb.PassiveVal,
                    ["evoke"] = (int)orb.EvokeVal,
                }).ToList();
            }
            if (orbQueue != null && orbQueue.Capacity > 0)
                result["orb_slots"] = orbQueue.Capacity;

            // Regent: Stars
            if (pcs != null && pcs.Stars >= 0 && player.Character?.Id.Entry == "REGENT")
            {
                result["stars"] = pcs.Stars;
            }

            // Necrobinder: Osty (minion)
            var osty = player.Osty;
            if (osty != null)
            {
                var ostyPowers = osty.Powers?.Select(PowerInfo).ToList();
                result["osty"] = new Dictionary<string, object?>
                {
                    ["name"] = MonsterName(osty.Monster),
                    ["hp"] = osty.CurrentHp,
                    ["max_hp"] = osty.MaxHp,
                    ["block"] = osty.Block,
                    ["alive"] = osty.IsAlive,
                    ["powers"] = ostyPowers?.Count > 0 ? ostyPowers : null,
                };
            }
            else if (player.Character?.Id.Entry == "NECROBINDER")
            {
                result["osty"] = new Dictionary<string, object?> { ["alive"] = false };
            }
        }
        catch (Exception ex)
        {
            Log($"Character-specific data: {ex.Message}");
        }

        return result;
    }

    private Dictionary<string, object?> DetectPostCombatState(Player player, CombatRoom combatRoom)
    {
        Log($"Post-combat: RoomType={combatRoom.RoomType}, IsPreFinished={combatRoom.IsPreFinished}");
        _syncCtx.Pump();
        if (_manualFlow) return ManualPostCombat(player, combatRoom);

        // Generate rewards manually instead of using TestMode auto-accept
        if (_pendingRewards == null && !_rewardsProcessed)
        {
            _goldBeforeCombat = player.Gold;
            try
            {
                var rewardsSet = new RewardsSet(player).WithRewardsFromRoom(combatRoom);
                // build 23372702: GenerateWithoutOffering() now returns Task (void);
                // generated rewards live on rewardsSet.Rewards afterwards.
                rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
                var rewards = rewardsSet.Rewards;
                _syncCtx.Pump();

                // Auto-collect gold and potions, but present card choices to agent
                var cardRewards = new List<CardReward>();
                foreach (var reward in rewards)
                {
                    if (reward is GoldReward || reward is MegaCrit.Sts2.Core.Rewards.RelicReward
                        || reward is MegaCrit.Sts2.Core.Rewards.PotionReward)
                    {
                        try { reward.SelectUnsynchronized().GetAwaiter().GetResult(); _syncCtx.Pump(); }
                        catch (Exception ex) { Log($"Auto-collect reward: {ex.Message}"); }
                    }
                    else if (reward is CardReward cr)
                    {
                        cardRewards.Add(cr);
                    }
                }

                if (cardRewards.Count > 0)
                {
                    _pendingCardReward = cardRewards[0];
                    _pendingRewards = rewards;
                    return CardRewardState(player, combatRoom);
                }

                _pendingRewards = null;
            }
            catch (Exception ex) { Log($"Generate rewards: {ex.Message}"); }
        }

        // No more pending rewards — proceed
        _pendingCardReward = null;
        _pendingRewards = null;
        _rewardsProcessed = true;

        // Boss → next act, OR final victory after the last act's boss (#81). Act index is
        // 0-based and STS2 has 3 acts (0/1/2); killing the Act-3 (index 2) boss has no next
        // act — EnterNextAct NREs and DetectDecisionPoint falls through to an empty
        // map_select. Report victory directly in that case.
        if (combatRoom.RoomType == RoomType.Boss)
        {
            if (IsFirstOfDoubleBoss())
            {
                Log("First boss of a double-boss act defeated, returning to map for the second boss");
                ForceToMap();
                return MapSelectState();
            }
            if (IsFinalAct())
            {
                Log($"Final boss defeated (Act {_runState!.CurrentActIndex + 1}), reporting victory");
                return GameOverState(true);
            }
            Log("Boss defeated, entering next act");
            try
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"EnterNextAct: {ex.Message}"); }
            return DetectDecisionPoint();
        }

        // Normal → go to map
        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> CardRewardState(Player player, CombatRoom? combatRoom)
    {
        if (_pendingCardReward == null)
            return DetectPostCombatState(player, combatRoom ?? (_runState?.CurrentRoom as CombatRoom)!);

        var cards = _pendingCardReward.Cards.Select((c, i) =>
        {
            var info = CardInfo(c, PileType.None);
            info["index"] = i;
            return info;
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "card_reward",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["can_skip"] = _pendingCardReward.CanSkip,
            ["gold_earned"] = _runState!.Players[0].Gold - _goldBeforeCombat,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private void ForceToMap()
    {
        try
        {
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch { }

        if (_runState?.CurrentRoom is not MapRoom)
        {
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch (Exception ex) { Log($"ForceToMap: {ex.Message}"); }
        }
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        // The Fake Merchant has no options: its custom screen is a shop (and a merchant to throw
        // a Foul Potion at).
        if (HeadlessUiPatches.ActiveFakeMerchant(_runState?.Players[0]) is { } fakeMerchant)
            return FakeMerchantState(fakeMerchant, _runState!.Players[0]);
        if (_manualFlow && ManualFinishedEventState() is { } finishedEvent) return finishedEvent;
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        // Reset the choice-tracking flag once we re-export the event state. Earlier this
        // block force-finished events whose option count was unchanged, but that incorrectly
        // killed legitimate loops like Slippery Bridge Hold On (#59). Rely on IsFinished
        // instead and let DetectDecisionPoint show the current options for the next choice.
        if (_eventOptionChosen) _eventOptionChosen = false;

        // If event is finished, proceed to map
        if (localEvent == null || localEvent.IsFinished)
        {
            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, proceeding");
            try
            {
                RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch { }
            // Force to map if still in event room
            if (_runState?.CurrentRoom is EventRoom)
            {
                try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
                catch { }
            }
            return _runState?.CurrentRoom is MapRoom ? MapSelectState() : DetectDecisionPoint();
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch { }
            return MapSelectState();
        }

        var options = currentOptions
            .Select((opt, i) =>
            {
                // Title and description as the option button renders them (event vars filled in).
                var (title, optDesc) = EventOptionText(localEvent, opt);
                // Fallback: try to extract option ID from the key and look up as relic/card/potion
                if (title == null && opt.TextKey != null)
                {
                    // TextKey like "NEOW.pages.INITIAL.options.STONE_HUMIDIFIER" → extract "STONE_HUMIDIFIER"
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    // Try relic, then card, then just use the optionId
                    var relic = _loc.Relic(optionId);
                    if (relic != optionId + ".title")
                        title = relic;
                    else
                    {
                        var card = _loc.Card(optionId);
                        if (card != optionId + ".title")
                            title = card;
                        else
                            title = optionId.Replace("_", " ");
                    }
                }
                title ??= $"option_{i}";

                // Fallback: try relic/card description
                if (optDesc == null && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var rd = _loc.Text("relics", optionId + ".description");
                    if (rd != optionId + ".description")
                        optDesc = rd;
                }

                // Extract vars: try event's own DynamicVars first, then relic
                Dictionary<string, object?>? optVars = null;
                try
                {
                    // Event's DynamicVars (covers Gold, HpLoss, Heal, etc.)
                    if (localEvent.DynamicVars?.Values != null)
                    {
                        optVars = new Dictionary<string, object?>();
                        foreach (var dv in localEvent.DynamicVars.Values)
                            optVars[dv.Name] = (int)dv.BaseValue;
                    }
                }
                catch { }
                // Also try relic vars (for Neow options)
                if (opt.TextKey != null)
                {
                    try
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var relicModel = ModelDb.GetById<RelicModel>(new ModelId("RELIC", optionId));
                        if (relicModel != null)
                        {
                            optVars ??= new Dictionary<string, object?>();
                            var mutable = relicModel.ToMutable();
                            foreach (var dv in mutable.DynamicVars.Values)
                                optVars[dv.Name] = (int)dv.BaseValue;
                        }
                    }
                    catch { }
                }

                // `RandomCard` (Slippery Bridge / Overcome) carries a *deck index*, but the
                // description template `{RandomCard}` should render that deck card's name. Resolve
                // it to the localized name so clients can substitute it (#58). Scoped to this var
                // name on purpose: other card vars (e.g. Wood Carvings' BirdCard/ToricCard) index
                // a transform-target pool, not the deck, so a generic rule would mis-resolve them.
                if (optVars != null && optVars.TryGetValue("RandomCard", out var rcVal) && rcVal is int rcIdx)
                {
                    try
                    {
                        var deck = _runState?.Players?[0]?.Deck?.Cards;
                        if (deck != null && rcIdx >= 0 && rcIdx < deck.Count && deck[rcIdx] != null)
                            optVars["RandomCard"] = _loc.Card(deck[rcIdx].Id.Entry);
                    }
                    catch { }
                }

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["title"] = title,
                    ["description"] = optDesc,
                    ["text_key"] = opt.TextKey,
                    ["is_locked"] = opt.IsLocked,
                    ["is_proceed"] = opt.IsProceed ? true : null,
                    ["vars"] = optVars?.Count > 0 ? optVars : null,
                    ["offers"] = EventOptionOffers(opt),
                };
            }).ToList();

        // Resolve event name — try ancients table first (for Neow), then events
        var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
        var eventName = _loc.Text("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);

        // Resolve event description, suppress if key not found
        string? eventDesc = null;
        if (localEvent.Description != null)
        {
            var d = EventBodyText(localEvent);
            if (d != localEvent.Description.LocEntryKey)
                eventDesc = d;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContext(),
            ["event_name"] = eventName,
            ["description"] = eventDesc,
            ["options"] = options,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        if (_manualFlow) return ManualRestSiteState(restRoom);
        var options = restRoom.Options;
        var player = _runState!.Players[0];

        if (options == null || options.Count == 0)
        {
            // Options empty = choice already made (synchronizer cleared them), go to map
            Log("Rest site: options empty, proceeding to map");
            ForceToMap();
            return MapSelectState();
        }

        var optionList = options.Select(RestOptionInfo).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContext(),
            ["options"] = optionList,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.GetLocalInventory();
        if (inv == null) { ForceToMap(); return MapSelectState(); }

        var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
            .Select((e, i) =>
            {
                var card = e.CreationResult?.Card;
                // In a shop row "cost" is the gold price (as before; also "price"), and the card's
                // energy cost is "card_cost".
                var info = card != null ? CardInfo(card, PileType.None) : new Dictionary<string, object?> { ["name"] = "?" };
                try
                {
                    if (card != null)
                    {
                        info["card_cost"] = card.EnergyCost?.GetResolved() ?? 0;
                        // The shop entry's card can have uninitialized DynamicVars (stats: null
                        // while after_upgrade is populated, #68). Read stats and text from a fresh
                        // ModelDb clone at the card's current upgrade level, like GetUpgradedInfo.
                        var fresh = ModelDb.GetById<CardModel>(card.Id).ToMutable();
                        for (int u = 0; u < card.CurrentUpgradeLevel; u++)
                        {
                            fresh.UpgradeInternal();
                            fresh.FinalizeUpgradeInternal();
                        }
                        info["stats"] = CardStats(fresh);
                        info["description"] = CardText(fresh, PileType.None);
                    }
                }
                catch { }
                info["index"] = i;
                info["cost"] = e.Cost;
                info["price"] = e.Cost;
                info["is_stocked"] = e.IsStocked;
                info["on_sale"] = e.IsOnSale;
                return info;
            }).ToList();

        var relics = RelicRows(inv);

        var potions = inv.PotionEntries.Select((e, i) =>
        {
            var info = e.Model != null ? PotionInfo(e.Model) : new Dictionary<string, object?>();
            info["index"] = i;
            info["cost"] = e.Cost;
            info["price"] = e.Cost;
            info["is_stocked"] = e.IsStocked;
            return info;
        }).ToList();

        var removal = merchantRoom.GetLocalInventory().CardRemovalEntry;

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "shop",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["card_removal_cost"] = removal?.Cost,
            ["player"] = PlayerSummary(player),
        };
    }

    /// <summary>Sold-out slots have no model: they keep index/cost/is_stocked but no item fields.</summary>
    private List<Dictionary<string, object?>> RelicRows(MerchantInventory inv) =>
        inv.RelicEntries.Select((e, i) =>
        {
            var info = e.Model != null ? RelicInfo(e.Model) : new Dictionary<string, object?>();
            info["index"] = i;
            info["cost"] = e.Cost;
            info["price"] = e.Cost;
            info["is_stocked"] = e.IsStocked;
            return info;
        }).ToList();

    /// <summary>The Merchant??? (FakeMerchant event): a relic-only shop of fake relics.</summary>
    private Dictionary<string, object?> FakeMerchantState(MegaCrit.Sts2.Core.Models.Events.FakeMerchant fakeMerchant, Player player) => new()
    {
        ["type"] = "decision",
        ["decision"] = "fake_merchant",
        ["context"] = RunContext(),
        ["event_name"] = _loc.Event(fakeMerchant.Id.Entry),
        ["relics"] = RelicRows(fakeMerchant.Inventory),
        ["player"] = PlayerSummary(player),
    };

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        if (_manualFlow) return ManualTreasureState(treasureRoom);
        // Treasure rooms give relics via TreasureRoomRelicSynchronizer
        Log("Treasure room — collecting rewards");

        WaitForActionExecutor();
        _syncCtx.Pump();

        // TreasureRoom.EnterInternal opens a relic-picking session (BeginRelicPicking) that the
        // headless must drive, otherwise (a) no relic is ever awarded and (b) the session stays
        // open so the NEXT treasure room's BeginRelicPicking throws "relic picking session while
        // one was already occurring!" (issue #56). Auto-pick the first offered relic: PickRelicLocally
        // enqueues a PickRelicAction whose execution awards the relic and ends the session
        // (OnPicked -> EndRelicVoting). An empty offer is closed with CompleteWithNoRelics.
        try
        {
            var relicSync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (relicSync?.CurrentRelics != null)
            {
                // The actual relic grant normally lives in the UI node's RelicsAwarded handler
                // (RelicCmd.Obtain per result), which is absent in headless. Capture the awarded
                // results, then grant them ourselves after the pick resolves (granting inside the
                // event — mid action-execution — risks re-entrancy).
                List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>? awarded = null;
                Action<List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>> capture = r => awarded = r;
                relicSync.RelicsAwarded += capture;
                try
                {
                    if (relicSync.CurrentRelics.Count > 0)
                    {
                        Log($"Auto-picking treasure relic 0 of {relicSync.CurrentRelics.Count}");
                        relicSync.PickRelicLocally(0);
                    }
                    else
                    {
                        relicSync.CompleteWithNoRelics();
                    }
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                    _syncCtx.Pump();
                }
                finally { relicSync.RelicsAwarded -= capture; }

                if (awarded != null)
                {
                    foreach (var res in awarded)
                    {
                        if (res.relic != null && res.player != null)
                            RelicCmd.Obtain(res.relic.ToMutable(), res.player).GetAwaiter().GetResult();
                    }
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                    _syncCtx.Pump();
                }
            }
        }
        catch (Exception ex) { Log($"Treasure relic pick: {ex.Message}"); }

        try
        {
            treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
            _syncCtx.Pump();
            treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
        {
            // BUG-013: Relic session conflict — wait for pending session then retry
            Log($"Relic session conflict, waiting and retrying: {ex.Message}");
            WaitForActionExecutor();
            _syncCtx.Pump();
            try
            {
                treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
                _syncCtx.Pump();
                treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch (Exception retryEx) { Log($"Treasure rewards retry failed: {retryEx.Message}"); }
        }
        catch (Exception ex) { Log($"Treasure rewards: {ex.Message}"); }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        var summary = PlayerSummary(player);
        // BUG-005: When player died, the engine resets HP to max. Use last known HP instead.
        if (!isVictory)
            summary["hp"] = _lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["context"] = RunContext(),
            ["victory"] = isVictory,
            ["player"] = summary,
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    #endregion

    #region Helpers

    private void WaitForActionExecutor()
    {
        try
        {
            // Ensure sync context is set for this thread
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            // Pump the synchronization context to execute any pending continuations
            _syncCtx.Pump();

            // Executor may stay "running" while the game awaits headless card selection / reward (e.g. Attack Potion).
            // Spinning here would time out and downstream code could mis-handle an in-flight potion use (BUG-026).
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return;

            var executor = RunManager.Instance.ActionExecutor;
            if (executor.IsRunning)
            {
                // Pump while waiting for executor
                int maxPumps = 1000;
                for (int i = 0; i < maxPumps; i++)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
        }
    }

    private void SpinWaitForCombatStable()
    {
        int maxIterations = 200;
        for (int i = 0; i < maxIterations; i++)
        {
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsInProgress) return;
            if (IsPlayPhase()) return;
            WaitForActionExecutor();
            if (IsPlayPhase() || !CombatManager.Instance.IsInProgress) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>Compute what a card would look like after upgrading (stats + cost + description).</summary>
    // after_upgrade depends only on the card id, its upgrade level and its current keywords (it is
    // built from a fresh canonical clone and diffed against the live keywords), but cloning and
    // upgrading every deck card on every observation dominated the cost of an observation.
    private readonly Dictionary<(ModelId, int, string), Dictionary<string, object?>?> _upgradeInfoCache = new();

    private Dictionary<string, object?>? GetUpgradedInfo(CardModel card)
    {
        if (!card.IsUpgradable) return null;
        var keywords = card.Keywords == null ? "" : string.Join(",", card.Keywords.Where(k => k != CardKeyword.None).Select(k => (int)k).OrderBy(k => k));
        var key = (card.Id, card.CurrentUpgradeLevel, keywords);
        if (_upgradeInfoCache.TryGetValue(key, out var cached)) return cached;
        var info = BuildUpgradedInfo(card);
        _upgradeInfoCache[key] = info;
        return info;
    }

    private Dictionary<string, object?>? BuildUpgradedInfo(CardModel card)
    {
        try
        {
            var clone = ModelDb.GetById<CardModel>(card.Id).ToMutable();
            // Apply existing upgrades first
            for (int i = 0; i < card.CurrentUpgradeLevel; i++)
            {
                clone.UpgradeInternal();
                clone.FinalizeUpgradeInternal();
            }
            // Apply one more upgrade
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();

            var stats = new Dictionary<string, object?>();
            try { foreach (var dv in clone.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }

            // Compare keywords before/after upgrade
            var oldKws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var newKws = clone.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var addedKws = newKws.Except(oldKws).ToList();
            var removedKws = oldKws.Except(newKws).ToList();

            return new Dictionary<string, object?>
            {
                ["cost"] = clone.EnergyCost?.GetResolved() ?? 0,
                ["stats"] = stats.Count > 0 ? stats : null,
                ["description"] = Rendered(() => clone.GetDescriptionForPile(PileType.None), "cards", card.Id.Entry + ".description"),
                ["added_keywords"] = addedKws.Count > 0 ? addedKws : null,
                ["removed_keywords"] = removedKws.Count > 0 ? removedKws : null,
            };
        }
        catch { return null; }
    }

    private Dictionary<string, object?> PlayerSummary(Player player)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = _loc.Text("characters", (player.Character?.Id.Entry ?? "IRONCLAD") + ".title"),
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["block"] = player.Creature?.Block ?? 0,
            ["gold"] = player.Gold,
            ["relics"] = player.Relics?.Select(RelicInfo).ToList(),
            // Filled slots only, each with its slot index (use_potion/discard_potion take it);
            // max_potions is the slot count, so the agent can tell when slots are full.
            ["potions"] = player.Potions?.Select((p, i) =>
            {
                if (p == null) return null;
                var info = PotionInfo(p);
                info["index"] = i;
                var pvars = new Dictionary<string, object?>();
                try { foreach (var dv in p.DynamicVars.Values) pvars[dv.Name] = (int)dv.BaseValue; } catch { }
                info["vars"] = pvars.Count > 0 ? pvars : null;
                return info;
            }).Where(x => x != null).ToList(),
            ["max_potions"] = player.MaxPotionCount,
            ["deck_size"] = player.Deck?.Cards?.Count(c => c != null) ?? 0,
            ["deck"] = player.Deck?.Cards?.Where(c => c != null).Select(c => CardInfo(c, PileType.Deck)).ToList(),
        };
    }

    /// <summary>Common context added to every decision point.</summary>
    private Dictionary<string, object?> RunContext()
    {
        if (_runState == null) return new();
        var ctx = new Dictionary<string, object?>
        {
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_id"] = _runState.Act?.Id.Entry,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
            ["room_type"] = _runState.CurrentRoom?.RoomType.ToString(),
            ["seed"] = _runState.Rng?.StringSeed,
            ["ascension"] = _runState.AscensionLevel,
            ["total_floor"] = _runState.TotalFloor,
            ["flow"] = _manualFlow ? "manual" : "auto",
        };

        // Boss encounter info — use BossEncounter?.Id?.Entry
        try
        {
            if (_runState.Act?.BossEncounter is { } boss)
                ctx["boss"] = EncounterInfo(boss);
            // Double-boss acts (Ascension 10): the second boss is fought after the first.
            if (_runState.Act?.SecondBossEncounter is { } second)
                ctx["second_boss"] = EncounterInfo(second);
        }
        catch { }

        return ctx;
    }

    private static void EnsureModelDbInitialized()
    {
        if (_modelDbInitialized) return;
        _modelDbInitialized = true;

        TestMode.IsOn = true;

        // The current engine requires mod discovery and assembly metadata even
        // for unmodded runs. TestMode skips filesystem/workshop mod loading.
        MegaCrit.Sts2.Core.Modding.ModManager.Initialize(
            new MegaCrit.Sts2.Core.Modding.ModManagerFileIo(), null, null).GetAwaiter().GetResult();
        MegaCrit.Sts2.Core.Modding.AssemblyInfo.Init();

        // Install inline sync context on main thread
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Initialize PlatformServices before anything touches PlatformUtil
        try
        {
            // Try to access PlatformUtil to trigger its static init
            // If it fails, it won't be available but most code checks SteamInitializer.Initialized
            var _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}");
        }

        // Initialize SaveManager with a dummy profile for save/load support
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }

        // Initialize PrefsSave (FastMode etc.). build 23372702 reads PrefsSave.FastMode
        // from many gameplay paths (e.g. Slice.OnPlay anim delay); without this the
        // PrefsSave getter returns null and those paths NRE.
        try { SaveManager.Instance.InitPrefsDataForTest(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitPrefsDataForTest: {ex.Message}"); }

        // Install the Task.Yield patch but keep SuppressYield=false by default.
        // SuppressYield is toggled to true only during EndTurn to prevent boss fight deadlocks.
        PatchTaskYield();

        // Patch Cmd.Wait to be a no-op in headless mode.
        // Cmd.Wait(duration) is used for UI animations (e.g., PreviewCardPileAdd during
        // Vantom's Dismember move adding Wounds). In headless mode, these never complete
        // because there's no Godot scene tree, causing the ActionExecutor to deadlock.
        PatchCmdWait();

        // Patch TalkCmd.Play to a no-op (issue #64). Monster speech-bubble VFX during
        // moves (e.g. BygoneEffigy.WakeMove) NRE in headless and break the enemy turn.
        PatchTalkCmd();

        // SoulNexus.AfterDeath only swaps the corpse animation via NCombatRoom.Instance, which is
        // null in headless; the NRE kills the combat turn loop (false game_over). Skip it.
        PatchCosmeticNoOp(typeof(MegaCrit.Sts2.Core.Models.Monsters.SoulNexus), "AfterDeath", typeof(Creature));

        // Game logic that touches UI singletons which are null headless (see HeadlessUiPatches).
        HeadlessUiPatches.Apply();

        // Keep what the UI knows about each card selection (cancelable, can skip, prompt).
        SelectionPrefsPatches.Apply();

        // Gameplay methods that give test-only results under TestMode (see TestModePatches).
        TestModePatches.Apply();

        // Initialize localization system (needed for events, cards, etc.)
        InitLocManager();

        var subtypes = MegaCrit.Sts2.Core.Models.AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        for (int i = 0; i < subtypes.Count; i++)
        {
            try
            {
                ModelDb.Inject(subtypes[i]);
                registered++;
            }
            catch (Exception ex)
            {
                failed++;
                // Only log first few failures to reduce noise
                if (failed <= 5)
                    Console.Error.WriteLine($"[WARN] Failed to register {subtypes[i].Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[INFO] ModelDb: {registered} registered, {failed} failed out of {subtypes.Count}");

        // Progress now resolves character models, so register ModelDb first.
        SaveManager.Instance.InitProgressData();

        // The built-in test reward selector assumes every reward is taken.
        // Mirror leaving the reward screen after our explicit take/skip prompts
        // so skipped cards finish the set and its event continuation can resume.
        RewardsSet.testSelector = async set =>
        {
            // Manual flow: present the set as an interactive rewards screen and wait for the
            // player's claim_reward / proceed actions instead of taking everything.
            var sim = LocPatches._bundleSimRef;
            if (sim != null && sim._manualFlow)
            {
                await sim.RunInteractiveRewardsScreen(set);
                return;
            }
            var synchronizer = RunManager.Instance.RewardsSetSynchronizer;
            foreach (var reward in set.Rewards)
                await synchronizer.SelectLocalReward(reward);
            if (!synchronizer.IsRewardsSetCompleted(set))
                synchronizer.SkipLocalRewardsSet();
        };

        // Initialize net ID serialization cache (needed for combat actions)
        try
        {
            ModelIdSerializationCache.Init();
            Console.Error.WriteLine("[INFO] ModelIdSerializationCache initialized");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModelIdSerializationCache.Init: {ex.Message}");
        }
    }

    private const int MaxAscension = 10;
    private static readonly HashSet<string> KnownCharacters = new() { "ironclad", "silent", "defect", "regent", "necrobinder" };

    private Player? CreatePlayer(string characterName)
    {
        return characterName.ToLowerInvariant() switch
        {
            "ironclad" => Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL),
            "silent" => Player.CreateForNewRun<Silent>(UnlockState.all, 1uL),
            "defect" => Player.CreateForNewRun<Defect>(UnlockState.all, 1uL),
            "regent" => Player.CreateForNewRun<Regent>(UnlockState.all, 1uL),
            "necrobinder" => Player.CreateForNewRun<Necrobinder>(UnlockState.all, 1uL),
            _ => null
        };
    }

    private static void PatchCmdWait()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cmdwait");
            // Find Cmd.Wait(float) — it's in MegaCrit.Sts2.Core.Commands namespace
            // Find Cmd type via CardPileCmd's assembly (both are in same namespace)
            var cmdPileType = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd);
            var cmdAsm = cmdPileType.Assembly;
            Type? cmdType = cmdAsm.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
            // If not found by exact name, search by namespace + "Wait" method
            if (cmdType == null)
            {
                foreach (var t in cmdAsm.GetTypes())
                {
                    if (t.Namespace == "MegaCrit.Sts2.Core.Commands")
                    {
                        var waitM = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                            .Where(m => m.Name == "Wait").ToList();
                        if (waitM.Count > 0)
                        {
                            cmdType = t;
                            Console.Error.WriteLine($"[INFO] Found Wait() in {t.FullName}");
                            break;
                        }
                    }
                }
            }
            if (cmdType != null)
            {
                var waitMethod = cmdType.GetMethod("Wait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(float) }, null);
                if (waitMethod != null)
                {
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (prefix != null)
                    {
                        harmony.Patch(waitMethod, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Cmd.Wait() to no-op (prevents boss fight deadlocks)");
                    }
                }
                else
                {
                    // Try to find any Wait method
                    var methods = cmdType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "Wait").ToList();
                    foreach (var m in methods)
                    {
                        Console.Error.WriteLine($"[INFO] Found Cmd.Wait({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                        var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        if (prefix != null)
                        {
                            harmony.Patch(m, new HarmonyMethod(prefix));
                            Console.Error.WriteLine($"[INFO] Patched Cmd.Wait variant");
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Could not find MegaCrit.Sts2.Core.Commands.Cmd type");
            }
        }
        catch (Exception ex)
        {
            PatchReport.Warn($"Failed to patch Cmd.Wait: {ex.Message}");
        }
    }

    private static void PatchTalkCmd()
    {
        try
        {
            var harmony = new Harmony("sts2headless.talkpatch");
            var talkType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Commands.TalkCmd");
            var playMethod = talkType?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Play");
            if (playMethod == null)
            {
                PatchReport.Warn("Could not find TalkCmd.Play to patch");
                return;
            }
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.TalkCmdPlayPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null)
            {
                harmony.Patch(playMethod, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched TalkCmd.Play() to no-op (prevents enemy-move VFX crash, issue #64)");
            }
        }
        catch (Exception ex)
        {
            PatchReport.Warn($"Failed to patch TalkCmd.Play: {ex.Message}");
        }
    }

    private static void PatchCosmeticNoOp(Type type, string methodName, params Type[] parameterTypes)
    {
        try
        {
            var method = AccessTools.DeclaredMethod(type, methodName, parameterTypes);
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.SkipVoidPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (method == null || prefix == null || method.ReturnType != typeof(void))
            {
                PatchReport.Warn($"Could not patch {type.Name}.{methodName}");
                return;
            }
            new Harmony("sts2headless.cosmetic").Patch(method, new HarmonyMethod(prefix));
            Console.Error.WriteLine($"[INFO] Patched {type.Name}.{methodName}() to no-op (UI-only)");
        }
        catch (Exception ex)
        {
            PatchReport.Warn($"Failed to patch {type.Name}.{methodName}: {ex.Message}");
        }
    }

    private static void PatchTaskYield()
    {
        try
        {
            var harmony = new Harmony("sts2headless.yieldpatch");

            // Patch YieldAwaitable.YieldAwaiter.IsCompleted to return true
            // This makes `await Task.Yield()` execute synchronously (continuation runs inline)
            var yieldAwaiterType = typeof(System.Runtime.CompilerServices.YieldAwaitable)
                .GetNestedType("YieldAwaiter");
            if (yieldAwaiterType != null)
            {
                var isCompletedProp = yieldAwaiterType.GetProperty("IsCompleted");
                if (isCompletedProp != null)
                {
                    var getter = isCompletedProp.GetGetMethod();
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.IsCompletedPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (getter != null && prefix != null)
                    {
                        harmony.Patch(getter, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Task.Yield() to be synchronous");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PatchReport.Warn($"Failed to patch Task.Yield: {ex.Message}");
        }
    }

    /// <summary>
    /// Card selector for headless mode — picks first available card for any selection prompt.
    /// Used by cards like Headbutt, Armaments, etc. that need player to choose a card.
    /// </summary>
    /// <summary>
    /// Card selector that creates a pending selection decision point.
    /// When the game needs the player to choose cards (upgrade, remove, transform, bundle pick),
    /// this stores the options and waits for the main loop to provide the answer.
    /// </summary>
    internal class HeadlessCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        // Pending card selection — set by game engine, read by main loop
        public List<CardModel>? PendingOptions { get; private set; }
        public int PendingMinSelect { get; private set; }
        public int PendingMaxSelect { get; private set; }
        public string PendingPrompt { get; private set; } = "";
        /// <summary>The UI lets the player back out (Smith, Cook, shop removal): skip_select cancels.</summary>
        public bool PendingCancelable { get; private set; }
        /// <summary>The CardSelectCmd entry point without "From" (HandForDiscard, DeckForUpgrade, ChooseACardScreen...).</summary>
        public string PendingSource { get; private set; } = "";
        private TaskCompletionSource<IEnumerable<CardModel>>? _pendingTcs;

        public bool HasPending => _pendingTcs != null && !_pendingTcs.Task.IsCompleted;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var optList = options.ToList();
            var info = SelectionPrefsPatches.Take();
            if (optList.Count == 0)
                return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

            // Choose-a-card screens always pass (0, 1); the UI only offers Skip when canSkip is set.
            if (info?.CanSkip == false)
                minSelect = Math.Max(minSelect, 1);
            var cancelable = info?.Cancelable ?? false;

            // One option and a pick is required: nothing to decide, unless the UI lets you cancel.
            if (optList.Count == 1 && minSelect >= 1 && !cancelable)
                return Task.FromResult<IEnumerable<CardModel>>(optList);

            // Store pending selection and wait
            PendingOptions = optList;
            PendingMinSelect = minSelect;
            PendingMaxSelect = maxSelect;
            PendingCancelable = cancelable;
            PendingPrompt = info?.Prompt ?? "";
            PendingSource = info?.Source ?? "";
            _pendingTcs = new TaskCompletionSource<IEnumerable<CardModel>>();

            Console.Error.WriteLine($"[SIM] Card selection pending: {optList.Count} options, select {minSelect}-{maxSelect}");

            // Return the task — the main loop will complete it
            return _pendingTcs.Task;
        }

        // Clear the pending state before completing the task: TrySetResult runs the engine's
        // continuation inline, and that continuation can open the next selection (Burst replaying
        // a discard, Knowledge Demon's curse leading into a start-of-turn discard). Clearing
        // afterwards would wipe that new selection and wedge the combat.
        public void ResolvePending(IEnumerable<CardModel> selected)
        {
            var tcs = _pendingTcs;
            PendingOptions = null;
            _pendingTcs = null;
            tcs?.TrySetResult(selected);
        }

        public void ResolvePendingByIndices(int[] indices)
        {
            if (PendingOptions == null) return;
            var selected = indices
                .Where(i => i >= 0 && i < PendingOptions.Count)
                .Select(i => PendingOptions[i])
                .ToList();
            ResolvePending(selected);
        }

        public void CancelPending()
        {
            var tcs = _pendingTcs;
            PendingOptions = null;
            _pendingTcs = null;
            tcs?.TrySetResult(Array.Empty<CardModel>());
        }

        // Pending card reward from events (GetSelectedCardReward blocks until resolved)
        public List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>? PendingRewardCards { get; private set; }
        public List<CardRewardAlternative>? PendingRewardAlternatives { get; private set; }
        private ManualResetEventSlim? _rewardWait;
        private int _rewardChoice = -1;
        private int _rewardAlternativeChoice = -1;
        // Set by Resolve*/Skip so HasPendingReward drops immediately, before the blocked engine
        // thread wakes up and clears PendingRewardCards (otherwise a stale card_reward is reported).
        private volatile bool _rewardResolved;

        // NOTE: STS2 build 23372702 changed ICardSelector.GetSelectedCardReward to return
        // a CardRewardSelection struct { CardModel card; CardRewardAlternative alternative }.
        // CardReward.OnSelect interprets: alternative != null → pick that alternative
        // (Skip/Reroll); else card != null → take that card; both null → skip (no card kept).
        public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0) return default;  // Skip

            // Store pending and block until main loop resolves
            PendingRewardCards = options.ToList();
            PendingRewardAlternatives = alternatives.ToList();
            _rewardChoice = -1;
            _rewardAlternativeChoice = -1;
            _rewardResolved = false;
            _rewardWait = new ManualResetEventSlim(false);

            Console.Error.WriteLine($"[SIM] Card reward pending: {options.Count} cards (blocking)");
            // Wait for the player however long it takes (a person or a paused RL actor); a run
            // reset releases it through SkipReward.
            _rewardWait.Wait();

            var choice = _rewardChoice;
            var altChoice = _rewardAlternativeChoice;
            var alts = PendingRewardAlternatives;
            PendingRewardCards = null;
            PendingRewardAlternatives = null;
            _rewardWait = null;

            if (alts != null && altChoice >= 0 && altChoice < alts.Count)
                return new MegaCrit.Sts2.Core.TestSupport.CardRewardSelection { alternative = alts[altChoice] };
            if (choice >= 0 && choice < options.Count)
                return new MegaCrit.Sts2.Core.TestSupport.CardRewardSelection { card = options[choice].Card };
            return default;  // Skip (card=null, alternative=null)
        }

        public bool HasPendingReward => PendingRewardCards != null && _rewardWait != null && !_rewardResolved;

        public void ResolveReward(int index)
        {
            _rewardChoice = index;
            _rewardResolved = true;
            _rewardWait?.Set();
        }

        public void SkipReward()
        {
            _rewardChoice = -1;
            _rewardResolved = true;
            _rewardWait?.Set();
        }

        public void ResolveRewardAlternative(int index)
        {
            _rewardChoice = -1;
            _rewardAlternativeChoice = index;
            _rewardResolved = true;
            _rewardWait?.Set();
        }
    }

    internal static class YieldPatches
    {
        // Only suppress Task.Yield() when this flag is set (during end_turn processing)
        public static volatile bool SuppressYield;

        public static bool IsCompletedPrefix(ref bool __result)
        {
            if (SuppressYield)
            {
                __result = true;
                return false;
            }
            return true; // Let normal Yield behavior run
        }

        /// <summary>Harmony prefix: make Cmd.Wait() return completed task immediately (no-op in headless).</summary>
        public static bool CmdWaitPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }

        /// <summary>
        /// Harmony prefix: no-op TalkCmd.Play (issue #64). The speech-bubble VFX
        /// (NSpeechBubbleVfx.Create + GetVfxContainer().AddChildSafely) NREs in headless,
        /// which derails enemy moves like BygoneEffigy.WakeMove mid enemy turn and forces
        /// the EndTurn nuclear fallback / false game_over. The bubble is purely cosmetic and
        /// its return value is ignored by callers, so returning null is safe.
        /// </summary>
        public static bool TalkCmdPlayPrefix(ref MegaCrit.Sts2.Core.Nodes.Vfx.NSpeechBubbleVfx? __result)
        {
            __result = null;
            return false; // Skip original method
        }

        /// <summary>Harmony prefix: skip a void, purely visual method.</summary>
        public static bool SkipVoidPrefix() => false;
    }

    private static void InitLocManager()
    {
        // Create a LocManager instance with stub tables via reflection.
        // LocManager.Initialize() fails because PlatformUtil isn't available,
        // and Harmony can't patch some LocString methods due to JIT issues.
        // Solution: create an uninitialized LocManager, set its _tables, and
        // use Harmony only for the simple LocTable.GetRawText fallback.
        try
        {
            // Create uninitialized LocManager and set Instance
            var instanceProp = typeof(LocManager).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
            instanceProp!.SetValue(null, instance);

            // Load REAL localization data from localization_eng/ JSON files
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tables = new Dictionary<string, LocTable>();

            var locDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "localization_eng");
            if (Directory.Exists(locDir))
            {
                foreach (var file in Directory.GetFiles(locDir, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(file));
                        if (data != null)
                            tables[name] = new LocTable(name, data);
                    }
                    catch { }
                }
                Console.Error.WriteLine($"[INFO] Loaded {tables.Count} localization tables from {locDir}");
            }
            else
            {
                Console.Error.WriteLine($"[WARN] Localization dir not found: {locDir}");
                // Fallback: empty tables
                var tableNames = new[] {
                    "achievements","acts","afflictions","ancients","ascension",
                    "bestiary","card_keywords","card_library","card_reward_ui",
                    "card_selection","cards","characters","combat_messages",
                    "credits","enchantments","encounters","epochs","eras",
                    "events","ftues","game_over_screen","gameplay_ui",
                    "inspect_relic_screen","intents","main_menu_ui","map",
                    "merchant_room","modifiers","monsters","orbs","potion_lab",
                    "potions","powers","relic_collection","relics","rest_site_ui",
                    "run_history","settings_ui","static_hover_tips","stats_screen",
                    "timeline","vfx"
                };
                foreach (var name in tableNames)
                    tables[name] = new LocTable(name, new Dictionary<string, string>());
            }
            tablesField!.SetValue(instance, tables);

            // Set Language
            var langProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { langProp?.SetValue(instance, "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { cultureProp?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }

            // Initialize _smartFormatter — the game uses `new SmartFormatter()`
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                // Dump ALL fields (instance + static)
                foreach (var f in typeof(LocManager).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    Console.Error.WriteLine($"[DEBUG] LocManager {(f.IsStatic?"static":"inst")} field: {f.Name} ({f.FieldType.Name})");
                Console.Error.WriteLine($"[DEBUG] sfField: {sfField?.Name ?? "null"} type: {sfField?.FieldType?.Name ?? "null"}");
                if (sfField != null)
                {
                    try
                    {
                        // List constructors to find the right one
                        var ctors = sfField.FieldType.GetConstructors(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        Console.Error.WriteLine($"[DEBUG] SmartFormatter has {ctors.Length} constructors:");
                        foreach (var ctor in ctors)
                        {
                            var ps = ctor.GetParameters();
                            Console.Error.WriteLine($"  ({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                        }
                        // Try the one with fewest params
                        var bestCtor = ctors.OrderBy(c => c.GetParameters().Length).First();
                        var args2 = bestCtor.GetParameters().Select(p =>
                            p.HasDefaultValue ? p.DefaultValue :
                            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                        ).ToArray();
                        var sf = bestCtor.Invoke(args2);
                        // Register extensions using the game's own LoadLocFormatters logic
                        // Call it via reflection on LocManager instance
                        try
                        {
                            var loadMethod = typeof(LocManager).GetMethod("LoadLocFormatters",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (loadMethod != null)
                            {
                                loadMethod.Invoke(instance, null);
                                Console.Error.WriteLine("[INFO] SmartFormatter initialized via LoadLocFormatters");
                            }
                            else
                            {
                                sfField.SetValue(null, sf);
                                Console.Error.WriteLine("[INFO] SmartFormatter set (no LoadLocFormatters found)");
                            }
                        }
                        catch (Exception lfEx)
                        {
                            sfField.SetValue(null, sf);
                            Console.Error.WriteLine($"[WARN] LoadLocFormatters failed: {lfEx.InnerException?.Message ?? lfEx.Message}");
                        }
                    }
                    catch (Exception sfEx)
                    {
                        Console.Error.WriteLine($"[WARN] SmartFormatter create failed: {sfEx.GetType().Name}: {sfEx.Message}");
                        if (sfEx.InnerException != null)
                            Console.Error.WriteLine($"  Inner: {sfEx.InnerException.GetType().Name}: {sfEx.InnerException.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[WARN] _smartFormatter field not found in LocManager");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}"); }

            // Initialize _engTables to point to _tables (avoid null ref in fallback)
            try
            {
                var engTablesField = typeof(LocManager).GetField("_engTables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                engTablesField?.SetValue(instance, tables);
            }
            catch { }

            Console.Error.WriteLine("[INFO] LocManager initialized with stub tables");

            // Use Harmony to patch methods that need fallback behavior
            var harmony = new Harmony("sts2headless.locpatch");

            // With real loc data loaded, we only need fallback patches for:
            // 1. LocTable.GetRawText — return key for missing entries instead of throwing
            // 2. LocManager.SmartFormat — _smartFormatter is null, return raw text instead
            // We do NOT patch GetFormattedText/GetRawText on LocString anymore
            // so the real localization pipeline works (needed for Neow event etc.)

            var getRawText = typeof(LocTable).GetMethod("GetRawText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            var prefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetRawTextPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getRawText != null && prefix != null)
            {
                harmony.Patch(getRawText, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched LocTable.GetRawText");
            }

            // Patch GetLocString to not throw
            var getLocString = typeof(LocTable).GetMethod("GetLocString");
            var glsPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetLocStringPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getLocString != null && glsPrefix != null)
            {
                try { harmony.Patch(getLocString, new HarmonyMethod(glsPrefix)); }
                catch (Exception ex4) { PatchReport.Warn($"Failed to patch GetLocString: {ex4.Message}"); }
            }

            // Patch FromChooseABundleScreen to use our card selector
            try
            {
                var bundleMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var bundlePrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.BundleScreenPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (bundleMethod != null && bundlePrefix != null)
                {
                    harmony.Patch(bundleMethod, new HarmonyMethod(bundlePrefix));
                    Console.Error.WriteLine("[INFO] Patched FromChooseABundleScreen");
                }
            }
            catch (Exception ex) { PatchReport.Warn($"Bundle patch: {ex.Message}"); }

            // Replace the Crystal Sphere minigame screen with a crystal_sphere decision
            try
            {
                var showScreen = typeof(MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen).GetMethod("ShowScreen",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var csPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.CrystalSphereScreenPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (showScreen != null && csPrefix != null)
                {
                    harmony.Patch(showScreen, new HarmonyMethod(csPrefix));
                    Console.Error.WriteLine("[INFO] Patched NCrystalSphereScreen.ShowScreen");
                }
            }
            catch (Exception ex) { PatchReport.Warn($"Crystal Sphere patch: {ex.Message}"); }

            // HasEntry / IsLocalKey / LocString.Exists / GetLocStringsWithPrefix run unpatched: the
            // real tables are loaded, and forcing them (always true / always empty) changed game
            // logic (Ancient dialogue, TheArchitect's dialogue pick, merchant lines).
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] InitLocManager failed: {ex.Message}");
        }
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string patchName)
    {
        try
        {
            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            PatchMethod(harmony, method, patchName);
        }
        catch (Exception ex) { PatchReport.Warn($"Failed to patch {type.Name}.{methodName}: {ex.Message}"); }
    }

    private static void PatchMethod(Harmony harmony, System.Reflection.MethodInfo? method, string patchName)
    {
        if (method == null) return;
        try
        {
            var prefix = typeof(LocPatches).GetMethod(patchName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null) harmony.Patch(method, new HarmonyMethod(prefix));
        }
        catch (Exception ex) { PatchReport.Warn($"Failed to patch {method.Name}: {ex.Message}"); }
    }

    internal static class LocPatches
    {
        /// <summary>Real text when the key exists; the key itself (instead of a LocException) when not.</summary>
        public static bool GetRawTextPrefix(LocTable __instance, string key, ref string __result)
        {
            if (__instance.HasEntry(key)) return true;
            __result = key;
            return false;
        }

        public static bool GetFormattedTextPrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }

        public static bool GetRawTextInstancePrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }


        public static bool GetLocStringPrefix(LocTable __instance, string key, ref LocString __result)
        {
            if (__instance.HasEntry(key)) return true;
            var nameField = typeof(LocTable).GetField("_name",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tableName = nameField?.GetValue(__instance) as string ?? "_unknown";
            __result = new LocString(tableName, key);
            return false;
        }

        /// <summary>
        /// Intercept bundle selection — store bundles and wait for player to pick a pack index.
        /// </summary>
        public static bool BundleScreenPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            IReadOnlyList<IReadOnlyList<CardModel>> bundles,
            ref Task<IEnumerable<CardModel>> __result)
        {
            if (bundles.Count == 0)
            {
                __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
                return false;
            }

            // Store pending bundles for the main loop to present
            var sim = _bundleSimRef;
            if (sim != null)
            {
                sim._pendingBundles = bundles;
                sim._pendingBundleTcs = new TaskCompletionSource<IEnumerable<CardModel>>();
                Console.Error.WriteLine($"[SIM] Bundle selection pending: {bundles.Count} packs");

                __result = sim._pendingBundleTcs.Task;
                return false;
            }

            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[0]);
            return false;
        }

        // Static reference so Harmony patch can access the simulator instance
        internal static RunSimulator? _bundleSimRef;

        /// <summary>Intercept the Crystal Sphere screen; the minigame is driven by crystal_sphere_divine.</summary>
        public static bool CrystalSphereScreenPrefix(
            MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame grid,
            ref MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen? __result)
        {
            __result = null;
            _bundleSimRef?.OnCrystalSphereShown(grid);
            return false;
        }

    }

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[SIM] {message}");
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["type"] = "error", ["message"] = message };

    private static Dictionary<string, object?> ErrorWithTrace(string context, Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = $"{context}: {inner.GetType().Name}: {inner.Message}",
            ["stack_trace"] = inner.StackTrace,
        };
    }

    public Dictionary<string, object?> GetFullMap()
    {
        if (_runState?.Map == null)
            return Error("No map available");

        var map = _runState.Map;
        var rows = new List<List<Dictionary<string, object?>>>();
        var currentCoord = _runState.CurrentMapCoord;
        var visited = _runState.VisitedMapCoords;

        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                var children = point.Children?.Select(ch => new Dictionary<string, object?>
                {
                    ["col"] = (int)ch.coord.col,
                    ["row"] = (int)ch.coord.row,
                }).ToList();

                var isVisited = visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false;
                var isCurrent = currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;

                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = children,
                    ["visited"] = isVisited,
                    ["current"] = isCurrent,
                });
            }
            if (rowNodes.Count > 0)
                rows.Add(rowNodes);
        }

        // Boss node
        var bossNode = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };

        // Add boss name/id — use BossEncounter?.Id?.Entry
        try
        {
            if (_runState.Act?.BossEncounter is { } boss)
            {
                var info = EncounterInfo(boss);
                bossNode["id"] = info["id"];
                bossNode["name"] = info["name"];
            }
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["type"] = "map",
            ["context"] = RunContext(),
            ["rows"] = rows,
            ["boss"] = bossNode,
            ["current_coord"] = currentCoord.HasValue ? new Dictionary<string, object?>
            {
                ["col"] = (int)currentCoord.Value.col,
                ["row"] = (int)currentCoord.Value.row,
            } : null,
        };
    }

    /// <summary>Forget adapter state from a previous run and release any thread parked on a prompt.</summary>
    private bool _combatEventsSubscribed;

    /// <summary>CombatManager outlives runs; subscribe once so repeated start_run/load_save don't stack handlers.</summary>
    private void SubscribeCombatEvents()
    {
        if (_combatEventsSubscribed) return;
        _combatEventsSubscribed = true;
        CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
        CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
    }

    private void ResetRunScopedState()
    {
        _cardSelector.CancelPending();
        _cardSelector.SkipReward();
        _pendingBundleTcs?.TrySetResult(Array.Empty<CardModel>());
        _pendingBundles = null;
        _pendingBundleTcs = null;
        _pendingCardReward = null;
        _pendingRewards = null;
        _rewardsProcessed = false;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _textCache.Clear();
    }

    public void CleanUp()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
                RunManager.Instance.CleanUp(graceful: true);
            _runState = null;
        }
        catch (Exception ex)
        {
            Log($"CleanUp exception: {ex.Message}");
        }
    }

    #endregion
}
