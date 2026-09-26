using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Validation;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

internal enum FlystsSelectionKind { HandSimple, HandUpgrade, Grid, ChooseACard, Bundle }

/// <summary>
/// One open card screen as the game UI runs it: picks toggle on the screen and a confirm sends
/// them, instead of sts2-cli's one-shot select_cards. The rules are the screens' own
/// (NPlayerHand, NDeckCardSelectScreen, NDeckUpgrade/Enchant/TransformSelectScreen,
/// NSimpleCardSelectScreen, NCombatPileCardSelectScreen, NChooseABundleSelectionScreen), and the
/// reported flags are the ones the mod read off them. Grid picks live in a HashSet, like the
/// screens' _selectedCards, so the result has the same enumeration order.
/// </summary>
internal sealed class FlystsSelection
{
    public FlystsSelectionKind Kind { get; init; }
    public object? Token { get; init; }
    /// <summary>mod state_type for grids (deck_card_select, deck_upgrade, ...).</summary>
    public string StateType { get; init; } = "";
    /// <summary>Game UI class name, for the mod's error texts.</summary>
    public string ScreenName { get; init; } = "";
    public List<CardModel> Options { get; init; } = new();
    public List<CardModel> Display { get; init; } = new();
    public int Min { get; init; }
    public int Max { get; init; }
    public bool RequireManual { get; init; }
    public string? PromptKey { get; init; }
    public string? PromptFormatted { get; init; }
    public EnchantmentModel? Enchantment { get; init; }
    public int? EnchantmentAmount { get; init; }
    public bool CanSkip { get; init; }
    public IReadOnlyList<IReadOnlyList<CardModel>>? Bundles { get; init; }

    public int SelectedBundle { get; private set; } = -1;
    public bool PreviewOpen { get; private set; }
    public List<CardModel>? Result { get; private set; }
    public bool Completed => Result != null;

    private readonly HashSet<CardModel> _gridSelected = new();
    private readonly List<CardModel> _handSelected = new();

    public bool IsHand => Kind is FlystsSelectionKind.HandSimple or FlystsSelectionKind.HandUpgrade;
    public bool Offers(CardModel card) => Options.Contains(card);
    public bool IsSelected(CardModel card) => IsHand ? _handSelected.Contains(card) : _gridSelected.Contains(card);
    public int SelectedCount => IsHand ? _handSelected.Count : _gridSelected.Count;

    /// <summary>The mod's preview_open: an enabled visible "Cancel" node. The upgrade, enchant and
    /// transform previews have one; NDeckCardSelectScreen's preview reads false (logged live).</summary>
    public bool PreviewOpenReported => PreviewOpen && StateType is "deck_upgrade" or "deck_enchant" or "deck_transform";

    public bool CanConfirm
    {
        get
        {
            int count = SelectedCount;
            return Kind switch
            {
                // NPlayerHand.RefreshSelectModeConfirmButton
                FlystsSelectionKind.HandSimple or FlystsSelectionKind.HandUpgrade => count >= Min && count <= Max,
                FlystsSelectionKind.Bundle => SelectedBundle >= 0,
                FlystsSelectionKind.ChooseACard => false,
                _ => StateType switch
                {
                    "deck_card_select" or "deck_enchant" or "deck_transform" => PreviewOpen || (Min != Max && count >= Min),
                    "deck_upgrade" => PreviewOpen,
                    "simple_card_select" => RequireManual && count >= Min,
                    "combat_pile_select" => RequireManual && count >= Math.Min(Min, Display.Count),
                    _ => false,
                },
            };
        }
    }

    private void Complete(IEnumerable<CardModel> result) => Result = result.ToList();

    /// <summary>select_deck_card / select_hand_card / choose_card. Returns an error text or null.</summary>
    public string? Pick(int index)
    {
        if (index < 0 || index >= Display.Count)
            return Kind switch
            {
                FlystsSelectionKind.HandSimple or FlystsSelectionKind.HandUpgrade => null, // checked by the caller
                _ => $"Card index {index} out of range ({Display.Count} cards)",
            };
        var card = Display[index];
        switch (Kind)
        {
            case FlystsSelectionKind.ChooseACard:
                Complete(new[] { card });
                return null;
            case FlystsSelectionKind.HandSimple:
                // NPlayerHand.SelectCardInSimpleMode: at the limit the last pick goes back first.
                if (_handSelected.Count >= Max && _handSelected.Count > 0)
                    _handSelected.RemoveAt(_handSelected.Count - 1);
                _handSelected.Add(card);
                return null;
            case FlystsSelectionKind.HandUpgrade:
                if (_handSelected.Count != 0) _handSelected.RemoveAt(_handSelected.Count - 1);
                _handSelected.Add(card);
                return null;
        }

        // GameActions.SelectDeckCard: a full selection or an open preview refuses more picks.
        int selected = _gridSelected.Count;
        if (PreviewOpenReported || (Max > 0 && selected >= Max))
            return $"Selection is full ({selected}/{Max}); confirm or cancel_selection";
        switch (StateType)
        {
            case "deck_card_select":
            case "deck_enchant":
            case "deck_transform":
                if (_gridSelected.Add(card)) { if (Max == _gridSelected.Count) PreviewOpen = true; }
                else _gridSelected.Remove(card);
                break;
            case "deck_upgrade":
                if (_gridSelected.Add(card)) { if (Max == 1 || Max == _gridSelected.Count) PreviewOpen = true; }
                else _gridSelected.Remove(card);
                break;
            case "simple_card_select":
            case "combat_pile_select":
            {
                if (!_gridSelected.Remove(card))
                {
                    if (_gridSelected.Count < Max) _gridSelected.Add(card);
                    int need = StateType == "combat_pile_select" ? Math.Min(Max, Display.Count) : Max;
                    if (!RequireManual && _gridSelected.Count >= need) Complete(_gridSelected);
                }
                break;
            }
        }
        return null;
    }

    public string? PickBundle(int index)
    {
        if (Bundles == null) return "No bundle selection screen is open";
        if (index < 0 || index >= Bundles.Count) return $"Bundle index {index} out of range ({Bundles.Count} bundles)";
        SelectedBundle = index;
        return null;
    }

    /// <summary>confirm, as GameActions.ConfirmSelection clicks it.</summary>
    public string? Confirm()
    {
        switch (Kind)
        {
            case FlystsSelectionKind.HandSimple:
            case FlystsSelectionKind.HandUpgrade:
                if (!CanConfirm) return "Hand selection confirm button is not enabled";
                Complete(_handSelected);
                return null;
            case FlystsSelectionKind.Bundle:
                if (SelectedBundle < 0) return "No enabled confirm button on NChooseABundleSelectionScreen";
                Complete(Bundles![SelectedBundle]);
                return null;
            case FlystsSelectionKind.ChooseACard:
                return "No enabled confirm button on NChooseACardSelectionScreen";
        }
        int count = _gridSelected.Count;
        if (PreviewOpen)
        {
            bool done = StateType switch
            {
                "deck_card_select" => count >= Min,
                "deck_enchant" => count >= Min && count <= Max,
                "deck_upgrade" => count >= Max,
                _ => true,
            };
            if (done) Complete(_gridSelected);
            return null;
        }
        if (!CanConfirm) return $"No enabled confirm button on {ScreenName}";
        switch (StateType)
        {
            case "deck_card_select":
            case "deck_enchant":
                PreviewOpen = true; // the grid's Confirm opens the preview (PreviewSelection)
                break;
            case "deck_transform":
                if (RequireManual) PreviewOpen = true; else Complete(_gridSelected);
                break;
            default:
                Complete(_gridSelected);
                break;
        }
        return null;
    }

    public string? Cancel()
    {
        if (Kind != FlystsSelectionKind.Grid) return "No card selection screen is open";
        if (!PreviewOpenReported) return $"No selection preview to cancel on {ScreenName}";
        PreviewOpen = false;
        _gridSelected.Clear();
        return null;
    }
}

public partial class RunSimulator
{
    /// <summary>A run in flysts payload mode is active (read by the static card selector).</summary>
    internal static bool FlystsMode;
    private FlystsSelection? _flystsSel;
    /// <summary>The engine's current native decision (after every native step).</summary>
    private Dictionary<string, object?>? _flystsNative;
    private Dictionary<string, object?>? _flystsLastPurchase;
    private bool _flystsAbandoned;
    // Card rewards opened on the current rewards screen (MenuAutomation opens each once; a skipped
    // one stays claimable and is left behind by proceed).
    private object? _flystsRewardsScreen;
    private readonly HashSet<Reward> _flystsOpenedCardRewards = new();

    private string? FlystsDecision => _flystsNative?.GetValueOrDefault("decision") as string;

    private bool FlystsHandSelectionPending => FlystsCurrentSelection() is { IsHand: true };

    // ---- run creation --------------------------------------------------------

    /// <summary>
    /// start_run {payload: "flysts"}: a new run built the way the game's custom/standard lobby
    /// builds one for this save profile (NGame.StartNewSingleplayerRun): the profile's progress is
    /// loaded into the SaveManager (in memory), the unlock state comes from it, the acts are rolled
    /// like StartRunLobby.BeginRunLocally (including the forced undiscovered act), the starting deck
    /// gets AfterCreated (RunState.CreateForNewRun), StartedWithNeow follows the Neow epoch, the
    /// discovery-order boss changes apply in Standard mode, and relics are finalized before Launch.
    /// </summary>
    public Dictionary<string, object?> StartFlystsRun(string character, int ascension, string? seed, string? gameMode, string? profilePath)
    {
        try
        {
            if (!KnownCharacters.Contains(character.ToLowerInvariant()))
                return Error($"Unknown character: {character}");
            var mode = (gameMode ?? "").Trim().ToLowerInvariant() switch
            {
                "custom" => GameMode.Custom,
                "standard" => GameMode.Standard,
                "" => string.IsNullOrWhiteSpace(seed) && ascension == 0 ? GameMode.Standard : GameMode.Custom,
                _ => (GameMode?)null,
            };
            if (mode == null) return Error($"Unknown game_mode '{gameMode}' (expected standard or custom)");
            if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
                return Error($"profile not found: '{profilePath}' (a progress.save path is required)");
            EnsureModelDbInitialized();
            if (!LoadFlystsProfile(profilePath, out var profileError)) return Error(profileError);

            var seedStr = string.IsNullOrWhiteSpace(seed) ? SeedHelper.GetRandomSeed() : SeedHelper.CanonicalizeSeed(seed);
            if (_runState != null) CleanUp();
            ResetRunScopedState();
            ResetManualFlowState();
            ResetFlystsState();
            _manualFlow = true;
            FlystsMode = true;

            var progress = SaveManager.Instance.Progress;
            // The lobby carries each player's unlock state serialized (LobbyPlayer.unlockState).
            var unlocks = UnlockState.FromSerializable(SaveManager.Instance.GenerateUnlockStateFromProgress().ToSerializable());
            var characterModel = FlystsCharacter(character);
            var acts = FlystsRollActs(seedStr, unlocks, progress);
            // StartRunLobby.BeginRunLocally: singleplayer ascension is capped at the character's max.
            int maxAscension = progress.GetOrCreateCharacterStats(characterModel.Id).MaxAscension;
            ascension = Math.Min(Math.Max(ascension, 0), maxAscension);

            var player = Player.CreateForNewRun(characterModel, unlocks, 1uL);
            Log($"flysts run: seed={seedStr} mode={mode} acts={string.Join(",", acts.Select(a => a.Id.Entry))} asc={ascension}");
            _runState = RunState.CreateForNewRun(new[] { player }, acts.Select(a => a.ToMutable()).ToList(),
                Array.Empty<ModifierModel>(), mode.Value, ascension, seedStr);

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.ForceDiscoveryOrderModifications = mode == GameMode.Standard;
            // SetUpTest runs InitializeNewRun (grab bags, StartedWithNeow, ascension) like
            // SetUpNewSingleplayer; GenerateRooms follows it there too.
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;
            RunManager.Instance.GenerateRooms();
            RunManager.Instance.ForceDiscoveryOrderModifications = false;
            SubscribeCombatEvents();
            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();
            RunManager.Instance.Launch();
            _selectorScope ??= CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;
            RunManager.Instance.EnterAct(0, doTransition: false).GetAwaiter().GetResult();
            _syncCtx.Pump();

            return FlystsAdvance(FlystsCurrentNative(), "Run started");
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("flysts start_run failed", ex);
        }
    }

    private static CharacterModel FlystsCharacter(string name) => name.ToLowerInvariant() switch
    {
        "ironclad" => ModelDb.Character<Ironclad>(),
        "silent" => ModelDb.Character<Silent>(),
        "defect" => ModelDb.Character<Defect>(),
        "regent" => ModelDb.Character<Regent>(),
        "necrobinder" => ModelDb.Character<Necrobinder>(),
        _ => throw new ArgumentException($"Unknown character: {name}"),
    };

    /// <summary>ActModel.GetRandomList as the game runs it (TestMode off): an unlocked, not yet
    /// discovered alternative act is forced into its slot; otherwise the slot is rolled.</summary>
    private static List<ActModel> FlystsRollActs(string seed, UnlockState unlocks, ProgressState progress)
    {
        var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed), "act_selection");
        var list = new List<ActModel>();
        foreach (var slot in ModelDb.ActsByIndex)
        {
            ActModel? forced = null;
            var candidates = new List<ActModel>();
            foreach (var act in slot)
            {
                if (!act.IsUnlocked(unlocks)) continue;
                if (!act.IsDefault && !progress.DiscoveredActs.Contains(act.Id))
                {
                    forced = act;
                    break;
                }
                candidates.Add(act);
            }
            list.Add(forced ?? rng.NextItem(candidates) ?? throw new InvalidOperationException("No unlocked acts for a slot"));
        }
        return list;
    }

    /// <summary>The save profile's progress.save, loaded into SaveManager.Progress (TestMode keeps
    /// every write in memory). Loaded fresh for every run, so each seed replays identically.</summary>
    private static bool LoadFlystsProfile(string path, out string error)
    {
        error = "";
        var read = SaveManager.FromJson<SerializableProgress>(File.ReadAllText(path));
        if (!read.Success || read.SaveData == null)
        {
            error = $"Failed to parse profile {path}: {read.Status} {read.ErrorMessage}";
            return false;
        }
        var ctx = new DeserializationContext();
        SaveManager.Instance.Progress = ProgressState.FromSerializable(read.SaveData, ctx);
        foreach (var e in ctx.Errors) Log($"profile parse: {e}");
        // GameState.EnsureEpochsRevealedForRepeatPlay: the mod reveals these two on every boot.
        foreach (var id in new[] { EpochModel.GetId<NeowEpoch>(), EpochModel.GetId<CustomAndSeedsEpoch>() })
        {
            try
            {
                if (SaveManager.Instance.Progress.Epochs.FirstOrDefault(x => x.Id == id)?.State != EpochState.Revealed)
                    SaveManager.Instance.RevealEpoch(id, isDebug: true);
            }
            catch (Exception ex) { Log($"RevealEpoch {id}: {ex.Message}"); }
        }
        return true;
    }

    private void ResetFlystsState()
    {
        FlystsMode = false;
        _flystsSel = null;
        _flystsNative = null;
        _flystsLastPurchase = null;
        _flystsAbandoned = false;
        _flystsRewardsScreen = null;
        _flystsOpenedCardRewards.Clear();
    }

    // ---- native steps --------------------------------------------------------

    /// <summary>Run one native action through the validation gate; the new decision becomes current.</summary>
    private Dictionary<string, object?> FlystsNative(string action, Dictionary<string, object?>? args, out string? error)
    {
        var r = ExecuteAction(action, args);
        if (string.Equals(r.GetValueOrDefault("type") as string, "error", StringComparison.Ordinal))
        {
            error = r.GetValueOrDefault("message") as string ?? "error";
            Log($"flysts native {action} rejected: {error}");
            return r;
        }
        error = null;
        AttachLegalActions(r);
        _flystsNative = r;
        return r;
    }

    private Dictionary<string, object?> FlystsCurrentNative()
    {
        var d = DetectDecisionPoint();
        AttachLegalActions(d);
        _flystsNative = d;
        return d;
    }

    /// <summary>Auto-advance the screens the mod's MenuAutomation clicks through (rewards, treasure,
    /// crystal sphere), then build the payload.</summary>
    private Dictionary<string, object?> FlystsAdvance(Dictionary<string, object?> native, string message)
    {
        for (int guard = 0; guard < 200; guard++)
        {
            if (!string.Equals(native.GetValueOrDefault("type") as string, "decision", StringComparison.Ordinal)) break;
            var step = FlystsAutoStep(native);
            if (step == null) break;
            var next = FlystsNative(step.Value.action, step.Value.args, out var err);
            if (err != null)
            {
                Log($"flysts auto-advance {step.Value.action} failed: {err}");
                break;
            }
            native = next;
        }
        return FlystsResult("ok", message, null);
    }

    private (string action, Dictionary<string, object?>? args)? FlystsAutoStep(Dictionary<string, object?> native)
    {
        var player = _runState!.Players[0];
        switch (native.GetValueOrDefault("decision") as string)
        {
            case "rewards":
            {
                var screen = TopRewardsScreen;
                if (screen == null) return null;
                if (!ReferenceEquals(screen, _flystsRewardsScreen))
                {
                    _flystsRewardsScreen = screen;
                    _flystsOpenedCardRewards.Clear();
                }
                // MenuAutomation.ClaimNextRewardOrProceed: the first claimable reward button;
                // potions only with a free slot; each card reward opened once per screen.
                var visible = screen.VisibleRewards;
                for (int i = 0; i < visible.Count; i++)
                {
                    var r = visible[i];
                    if (r is PotionReward && !player.HasOpenPotionSlots) continue;
                    if (r is CardReward && _flystsOpenedCardRewards.Contains(r)) continue;
                    if (r is CardReward) _flystsOpenedCardRewards.Add(r);
                    return ("claim_reward", new() { ["reward_index"] = i });
                }
                return ("proceed", null);
            }
            case "treasure":
                // MenuAutomation.AdvanceTreasureRoom: open the chest, take the relic, proceed.
                if (native.GetValueOrDefault("chest_opened") is not true) return ("open_chest", null);
                if (native.GetValueOrDefault("relics") is System.Collections.IList { Count: > 0 })
                    return ("pick_relic", new() { ["relic_index"] = 0 });
                return ("proceed", null);
            case "crystal_sphere":
            {
                // MenuAutomation.AdvanceCrystalSphere: the first hidden cell in the screen's
                // x-major child order, with the tool currently selected.
                var mg = _pendingCrystalSphere;
                if (mg == null) return null;
                var tool = mg.CrystalSphereTool == MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame.CrystalSphereToolType.Big ? "big" : "small";
                for (int x = 0; x < mg.GridSize.X; x++)
                    for (int y = 0; y < mg.GridSize.Y; y++)
                        if (mg.cells[x, y].IsHidden)
                            return ("crystal_sphere_divine", new() { ["x"] = x, ["y"] = y, ["tool"] = tool });
                return null;
            }
        }
        return null;
    }

    // ---- selection tracking ----------------------------------------------------

    private FlystsSelection? FlystsCurrentSelection()
    {
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            if (_flystsSel == null || !ReferenceEquals(_flystsSel.Token, _pendingBundleTcs))
                _flystsSel = new FlystsSelection
                {
                    Kind = FlystsSelectionKind.Bundle,
                    Token = _pendingBundleTcs,
                    StateType = "choose_a_bundle",
                    ScreenName = "NChooseABundleSelectionScreen",
                    Bundles = _pendingBundles,
                };
            return _flystsSel;
        }
        if (!_cardSelector.HasPending || _cardSelector.PendingOptions == null) return null;
        var token = _cardSelector.PendingToken;
        if (_flystsSel != null && ReferenceEquals(_flystsSel.Token, token)) return _flystsSel;
        _flystsSel = FlystsNewSelection(token);
        return _flystsSel;
    }

    private FlystsSelection FlystsNewSelection(object? token)
    {
        var info = _cardSelector.PendingInfo;
        var options = _cardSelector.PendingOptions!.ToList();
        var source = info?.Source ?? _cardSelector.PendingSource;
        int min = info?.MinSelect ?? _cardSelector.PendingMinSelect;
        int max = info?.MaxSelect ?? _cardSelector.PendingMaxSelect;
        switch (source)
        {
            case "Hand":
            case "HandForDiscard":
                return new FlystsSelection
                {
                    Kind = FlystsSelectionKind.HandSimple, Token = token, Options = options, Display = options,
                    Min = min, Max = max, RequireManual = info?.RequireManualConfirmation ?? false,
                    PromptKey = info?.PromptKey,
                };
            case "HandForUpgrade":
                // NPlayerHand.SelectCards(new CardSelectorPrefs(CHOOSE_CARD_UPGRADE_HEADER, 1), ..., UpgradeSelect)
                return new FlystsSelection
                {
                    Kind = FlystsSelectionKind.HandUpgrade, Token = token, Options = options, Display = options,
                    Min = 1, Max = 1, PromptKey = "CHOOSE_CARD_UPGRADE_HEADER",
                };
            case "ChooseACardScreen":
                return new FlystsSelection
                {
                    Kind = FlystsSelectionKind.ChooseACard, Token = token, Options = options, Display = options,
                    StateType = "choose_a_card", ScreenName = "NChooseACardSelectionScreen",
                    Min = 0, Max = 1, CanSkip = info?.CanSkip ?? false,
                };
        }
        var (stateType, screen) = source switch
        {
            "DeckForUpgrade" => ("deck_upgrade", "NDeckUpgradeSelectScreen"),
            "DeckForTransformation" => ("deck_transform", "NDeckTransformSelectScreen"),
            "DeckForEnchantment" => ("deck_enchant", "NDeckEnchantSelectScreen"),
            "SimpleGrid" or "SimpleGridForRewards" => ("simple_card_select", "NSimpleCardSelectScreen"),
            "CombatPile" => ("combat_pile_select", "NCombatPileCardSelectScreen"),
            _ => ("deck_card_select", "NDeckCardSelectScreen"),
        };
        if (source is not ("DeckGeneric" or "DeckForRemoval" or "DeckForUpgrade" or "DeckForTransformation"
            or "DeckForEnchantment" or "SimpleGrid" or "SimpleGridForRewards" or "CombatPile"))
            PatchReport.EngineWarning($"flysts: card selection from '{source}' shown as deck_card_select");
        var display = options;
        if (stateType == "combat_pile_select" && info?.PileType == PileType.Draw)
            display = FlystsDrawPileOrder(options);
        return new FlystsSelection
        {
            Kind = FlystsSelectionKind.Grid, Token = token, Options = options, Display = display,
            StateType = stateType, ScreenName = screen, Min = min, Max = max,
            RequireManual = info?.RequireManualConfirmation ?? (min != max),
            PromptKey = info?.PromptKey, PromptFormatted = info?.PromptFormatted,
            Enchantment = info?.Enchantment, EnchantmentAmount = info?.EnchantmentAmount,
        };
    }

    /// <summary>NCombatPileCardSelectScreen on the draw pile: the grid sorts the pile's cards
    /// (in pile order) by rarity, then title, then id (NCardGrid.SetCards, List.Sort).</summary>
    private List<CardModel> FlystsDrawPileOrder(List<CardModel> options)
    {
        var pile = _runState?.Players[0].PlayerCombatState?.DrawPile.Cards;
        var cards = pile == null ? options.ToList() : pile.Where(options.Contains).ToList();
        if (cards.Count != options.Count) cards = options.ToList();
        static int Rarity(CardModel a) => a.Rarity <= CardRarity.Ancient ? (int)a.Rarity : a.Rarity switch
        {
            CardRarity.Status => 6,
            CardRarity.Curse => 7,
            CardRarity.Event => 8,
            CardRarity.Quest => 9,
            CardRarity.Token => 10,
            _ => 11,
        };
        var culture = MegaCrit.Sts2.Core.Localization.LocManager.Instance?.CultureInfo ?? System.Globalization.CultureInfo.InvariantCulture;
        cards.Sort((x, y) =>
        {
            int n = Rarity(x).CompareTo(Rarity(y));
            if (n != 0) return n;
            n = string.Compare(x.Title, y.Title, culture, System.Globalization.CompareOptions.None);
            if (n != 0) return n;
            return x.Id.CompareTo(y.Id);
        });
        return cards;
    }

    /// <summary>Send a completed selection to the engine (select_cards in the screen's result order).</summary>
    private string? FlystsResolveSelection(FlystsSelection sel)
    {
        if (sel.Kind == FlystsSelectionKind.Bundle)
        {
            FlystsNative("select_bundle", new() { ["bundle_index"] = sel.SelectedBundle }, out var berr);
            return berr;
        }
        var result = sel.Result!;
        var indices = result.Select(c => sel.Options.IndexOf(c)).Where(i => i >= 0).ToList();
        string? err;
        if (indices.Count == 0 && ((_lastLegal ?? new()).Any(a => (a["action"] as string) == "skip_select")))
            FlystsNative("skip_select", null, out err);
        else
            FlystsNative("select_cards", new() { ["indices"] = string.Join(",", indices) }, out err);
        return err;
    }

    // ---- commands ------------------------------------------------------------

    public Dictionary<string, object?> FlystsGetState()
    {
        try { return FlystsResult("ok", null, null); }
        catch (Exception ex) { return ErrorWithTrace("flysts_state failed", ex); }
    }

    public Dictionary<string, object?> FlystsAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null || !FlystsMode) return FlystsResult("error", null, "No run in progress");
            if (action == "set_fast_mode") return FlystsResult("ok", "Fast mode is irrelevant headless", null);
            if (action == "abandon_run")
            {
                _flystsAbandoned = true;
                return FlystsResult("ok", "Abandoning run", null);
            }
            if (_flystsAbandoned || FlystsNativeDecision() == "game_over")
                return FlystsResult("error", null, "No run in progress");
            var (message, error) = FlystsDispatch(action, args ?? new());
            if (error != null) return FlystsResult("error", null, error);
            var native = FlystsCurrentNative();
            return FlystsAdvance(native, message ?? "ok");
        }
        catch (Exception ex) { return ErrorWithTrace($"flysts action {action} failed", ex); }
    }

    private string? FlystsNativeDecision()
    {
        if (_runState == null) return null;
        var player = _runState.Players[0];
        if (player.Creature != null && player.Creature.IsDead) return "game_over";
        return _flystsNative?.GetValueOrDefault("decision") as string;
    }

    private static int? ArgInt(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v == null) return null;
        return v switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var p) => p,
            _ => null,
        };
    }

    private (string? message, string? error) FlystsDispatch(string action, Dictionary<string, object?> args)
    {
        var player = _runState!.Players[0];
        string? err;
        switch (action)
        {
            case "play_card": return FlystsPlayCard(player, args);
            case "end_turn":
            {
                if (!CombatManager.Instance.IsInProgress) return (null, "Not in combat");
                if (!IsPlayPhase() || FlystsCurrentSelection() != null) return (null, "Not in play phase - cannot act during enemy turn");
                FlystsNative("end_turn", null, out err);
                return err == null ? ("Ending turn", null) : (null, err);
            }
            case "use_potion": return FlystsUsePotion(player, args);
            case "choose_map_node":
            {
                if (FlystsDecision != "map_select") return (null, "Map screen is not open");
                int? index = ArgInt(args, "index");
                if (index == null) return (null, "Missing 'index'");
                var travelable = FlystsTravelable(_runState);
                if (index < 0 || index >= travelable.Count) return (null, $"Map node index {index} out of range ({travelable.Count} options)");
                var p = travelable[index.Value];
                FlystsNative("select_map_node", new() { ["col"] = (int)p.coord.col, ["row"] = (int)p.coord.row }, out err);
                return err == null ? ($"Traveling to map node {index}", null) : (null, err);
            }
            case "shop_purchase": return FlystsShopPurchase(player, args);
            case "leave_shop":
                if (_runState.CurrentRoom is not MerchantRoom || FlystsDecision != "shop") return (null, "Not in a shop");
                FlystsNative("proceed", null, out err);
                return err == null ? ("Leaving shop", null) : (null, err);
            case "choose_event_option": return FlystsChooseEventOption(args);
            case "choose_rest_option":
            {
                if (_runState.CurrentRoom is not RestSiteRoom room || FlystsDecision != "rest_site") return (null, "Rest site room is not open");
                int? index = ArgInt(args, "index");
                if (index == null) return (null, "Missing 'index'");
                SettleRestSiteTask();
                if (index < 0 || index >= room.Options.Count) return (null, $"Rest option index {index} out of range ({room.Options.Count} options)");
                if (_restSiteTask != null || !room.Options[index.Value].IsEnabled) return (null, $"Rest option {index} is disabled");
                FlystsNative("choose_option", new() { ["option_index"] = index.Value }, out err);
                return err == null ? ($"Selecting rest site option {index}", null) : (null, err);
            }
            case "leave_rest_site":
            {
                if (_runState.CurrentRoom is not RestSiteRoom room || FlystsDecision != "rest_site") return (null, "Rest site room is not open");
                SettleRestSiteTask();
                if (_restSiteTask != null || !(_restSiteChoiceMade || room.Options.Count == 0))
                    return (null, "Proceed button is not enabled -- pick a rest site option first");
                FlystsNative("proceed", null, out err);
                return err == null ? ("Leaving rest site", null) : (null, err);
            }
            case "select_card_reward":
            {
                if (!_cardSelector.HasPendingReward) return (null, "Card reward selection screen is not open");
                int? index = ArgInt(args, "card_index");
                if (index == null) return (null, "Missing 'card_index'");
                int n = _cardSelector.PendingRewardCards?.Count ?? 0;
                if (index < 0 || index >= n) return (null, $"Card index {index} out of range ({n} cards)");
                FlystsNative("select_card_reward", new() { ["card_index"] = index.Value }, out err);
                return err == null ? ($"Selecting card reward {index}", null) : (null, err);
            }
            case "select_card_reward_alternative":
            {
                if (!_cardSelector.HasPendingReward) return (null, "Card reward selection screen is not open");
                int? index = ArgInt(args, "index");
                if (index == null) return (null, "Missing 'index'");
                var alts = _cardSelector.PendingRewardAlternatives ?? new();
                if (index < 0 || index >= alts.Count) return (null, $"Alternative index {index} out of range ({alts.Count} alternatives)");
                if (string.Equals(alts[index.Value].OptionId, "Skip", StringComparison.OrdinalIgnoreCase))
                    FlystsNative("skip_card_reward", null, out err);
                else
                    FlystsNative("select_card_reward_alternative", new() { ["alternative_index"] = index.Value }, out err);
                return err == null ? ($"Selecting card reward alternative {index}", null) : (null, err);
            }
            case "select_bundle":
            {
                var sel = FlystsCurrentSelection();
                if (sel == null || sel.Kind != FlystsSelectionKind.Bundle) return (null, "No bundle selection screen is open");
                int? index = ArgInt(args, "index");
                if (index == null) return (null, "Missing 'index'");
                err = sel.PickBundle(index.Value);
                return err == null ? ($"Selecting bundle {index}", null) : (null, err);
            }
            case "choose_card":
            {
                var sel = FlystsCurrentSelection();
                if (sel == null || sel.Kind != FlystsSelectionKind.ChooseACard) return (null, "No choose-a-card screen is open");
                int? index = ArgInt(args, "card_index");
                if (index == null) return (null, "Missing 'card_index'");
                err = sel.Pick(index.Value);
                if (err != null) return (null, err);
                err = FlystsResolveSelection(sel);
                return err == null ? ($"Choosing card {index}", null) : (null, err);
            }
            case "select_deck_card":
            {
                var sel = FlystsCurrentSelection();
                if (sel == null || sel.Kind != FlystsSelectionKind.Grid) return (null, "No deck card selection screen is open");
                int? index = ArgInt(args, "card_index");
                if (index == null) return (null, "Missing 'card_index'");
                err = sel.Pick(index.Value);
                if (err != null) return (null, err);
                if (sel.Completed) err = FlystsResolveSelection(sel);
                return err == null ? ($"Selecting deck card {index} on {sel.ScreenName}", null) : (null, err);
            }
            case "select_hand_card":
            {
                var sel = FlystsCurrentSelection();
                if (sel == null || !sel.IsHand) return (null, "No hand card selection is active");
                int? index = ArgInt(args, "card_index");
                if (index == null) return (null, "Missing 'card_index'");
                var hand = player.PlayerCombatState?.Hand.Cards;
                if (hand == null) return (null, "No hand available");
                if (index < 0 || index >= hand.Count) return (null, $"card_index {index} out of range (hand has {hand.Count} cards)");
                var card = hand[index.Value];
                // The picked card's holder has left the hand; filtered-out cards have no visible holder.
                if (sel.IsSelected(card) || !sel.Offers(card)) return (null, $"No holder found for card at index {index}");
                if (sel.Kind == FlystsSelectionKind.HandUpgrade && card.CurrentUpgradeLevel >= card.MaxUpgradeLevel)
                    return (null, $"Card at index {index} ({card.Id.Entry}) cannot be upgraded");
                sel.Pick(sel.Display.IndexOf(card));
                return ($"Selecting hand card {index} ({card.Id.Entry})", null);
            }
            case "confirm":
            {
                var sel = FlystsCurrentSelection();
                if (sel == null) return (null, "No overlay screen is open");
                err = sel.Confirm();
                if (err != null) return (null, err);
                if (sel.Completed) err = FlystsResolveSelection(sel);
                return err == null ? ("Confirming selection", null) : (null, err);
            }
            case "cancel_selection":
            {
                var sel = FlystsCurrentSelection();
                if (sel == null) return (null, "No card selection screen is open");
                err = sel.Cancel();
                return err == null ? ("Cancelled the selection preview", null) : (null, err);
            }
            case "select_relic":
                return (null, "No relic selection screen is open");
            case "menu_select":
                return (null, "Not on a recognized menu screen");
            default:
                return (null, "Unknown action: " + action);
        }
    }

    private (string?, string?) FlystsPlayCard(Player player, Dictionary<string, object?> args)
    {
        if (!CombatManager.Instance.IsInProgress) return (null, "Not in combat");
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null) return (null, "No combat state");
        if (combatState.CurrentSide != CombatSide.Player || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || FlystsCurrentSelection() != null)
            return (null, "Not in play phase - cannot act during enemy turn");
        if (!player.Creature.IsAlive) return (null, "Player creature is dead");
        int? index = ArgInt(args, "card_index");
        if (index == null) return (null, "Missing 'card_index'");
        var hand = player.PlayerCombatState?.Hand;
        if (hand == null) return (null, "No hand available");
        if (index < 0 || index >= hand.Cards.Count) return (null, $"card_index {index} out of range (hand has {hand.Cards.Count} cards)");
        var card = hand.Cards[index.Value];
        var pcs = player.PlayerCombatState;
        if (pcs != null && !card.EnergyCost.CostsX && card.EnergyCost.GetAmountToSpend() > pcs.Energy)
            return (null, $"Cannot afford card at index {index} ({card.Id.Entry}): costs {card.EnergyCost.GetAmountToSpend()}, have {pcs.Energy} energy");
        if (!card.CanPlay(out UnplayableReason reason, out _))
            return (null, $"Card at index {index} ({card.Id.Entry}) cannot be played: {reason}");
        var nativeArgs = new Dictionary<string, object?> { ["card_index"] = index.Value };
        if (card.TargetType == TargetType.AnyEnemy)
        {
            int alive = combatState.Enemies.Count(e => e.IsAlive);
            int? target = ArgInt(args, "target");
            if (target == null || target < 0 || target >= alive)
                return (null, "Card requires a target. Provide 'target' as a 0-based index into alive enemies.");
            nativeArgs["target_index"] = target.Value;
        }
        FlystsNative("play_card", nativeArgs, out var err);
        return err == null ? ($"Playing card at index {index}", null) : (null, err);
    }

    private (string?, string?) FlystsUsePotion(Player player, Dictionary<string, object?> args)
    {
        int? slot = ArgInt(args, "slot");
        if (slot == null) return (null, "Missing 'slot'");
        var slots = player.PotionSlots;
        if (slot < 0 || slot >= slots.Count) return (null, $"Potion slot {slot} out of range ({slots.Count} slots)");
        var potion = slots[slot.Value];
        if (potion == null) return (null, $"Potion slot {slot} is empty");
        if (!FlystsPotionUsable(potion)) return (null, $"Potion {potion.Id.Entry} is not usable now");
        // sts2-cli indexes the belt without its empty slots (Player.Potions).
        int potionIndex = slots.Take(slot.Value).Count(p => p != null);
        var nativeArgs = new Dictionary<string, object?> { ["potion_index"] = potionIndex };
        if (potion.TargetType == TargetType.AnyEnemy)
        {
            var cs = CombatManager.Instance.DebugOnlyGetState();
            if (cs == null) return (null, "Potion needs an enemy target but there is no combat");
            int alive = cs.Enemies.Count(e => e.IsAlive);
            int? t = ArgInt(args, "target");
            if (t == null || t < 0 || t >= alive) return (null, "Potion requires 'target': a 0-based index into alive enemies");
            nativeArgs["target_index"] = t.Value;
        }
        FlystsNative("use_potion", nativeArgs, out var err);
        return err == null ? ($"Using potion {potion.Id.Entry} from slot {slot}", null) : (null, err);
    }

    private (string?, string?) FlystsShopPurchase(Player player, Dictionary<string, object?> args)
    {
        if (_runState!.CurrentRoom is not MerchantRoom merchantRoom || FlystsDecision != "shop") return (null, "Not in a shop");
        var inventory = merchantRoom.GetLocalInventory();
        int? index = ArgInt(args, "index");
        if (index == null) return (null, "Missing 'index'");
        var entries = inventory.AllEntries.ToList();
        if (index < 0 || index >= entries.Count) return (null, $"Shop item index {index} out of range ({entries.Count} items)");
        var entry = entries[index.Value];
        if (!entry.IsStocked) return (null, "Item is sold out");
        if (!entry.EnoughGold) return (null, $"Not enough gold (need {entry.Cost}, have {player.Gold})");
        int cost = entry.Cost;
        string? err = null;
        bool ok;
        switch (entry)
        {
            case MerchantCardEntry ce:
                FlystsNative("buy_card", new() { ["card_index"] = inventory.CardEntries.ToList().IndexOf(ce) }, out err);
                ok = err == null;
                break;
            case MerchantRelicEntry re:
                FlystsNative("buy_relic", new() { ["relic_index"] = inventory.RelicEntries.ToList().IndexOf(re) }, out err);
                ok = err == null;
                break;
            case MerchantPotionEntry pe:
                // The game refuses a potion with a full belt (the purchase task returns false).
                if (!player.HasOpenPotionSlots) { ok = false; break; }
                FlystsNative("buy_potion", new() { ["potion_index"] = inventory.PotionEntries.ToList().IndexOf(pe) }, out err);
                ok = err == null;
                break;
            case MerchantCardRemovalEntry:
                FlystsNative("remove_card", null, out err);
                ok = err == null;
                break;
            default:
                return (null, "Unknown shop entry");
        }
        _flystsLastPurchase = new Dictionary<string, object?> { ["index"] = index.Value, ["cost"] = cost, ["ok"] = ok };
        if (err != null) return (null, err);
        return ($"Purchasing item for {cost} gold (result follows on the next shop payload as last_purchase)", null);
    }

    private (string?, string?) FlystsChooseEventOption(Dictionary<string, object?> args)
    {
        if (_runState!.CurrentRoom is not EventRoom || FlystsDecision is not ("event_choice" or "fake_merchant"))
            return (null, "Event room is not open");
        int? index = ArgInt(args, "index");
        if (index == null) return (null, "Missing 'index'");
        var options = FlystsEventOptions(SafeEvent());
        if (options.Count == 0) return (null, "No event options available");
        if (index < 0 || index >= options.Count) return (null, $"Event option index {index} out of range ({options.Count} options)");
        if (options[index.Value].IsLocked) return (null, $"Event option {index} is locked");
        // A finished event's synthesized Proceed is option 0 of manual flow's event_choice too.
        FlystsNative("choose_option", new() { ["option_index"] = index.Value }, out var err);
        return err == null ? ($"Choosing event option {index}", null) : (null, err);
    }

    // ---- state -------------------------------------------------------------------

    private Dictionary<string, object?> FlystsResult(string status, string? message, string? error)
    {
        var r = new Dictionary<string, object?> { ["type"] = "flysts", ["status"] = status };
        if (message != null) r["message"] = message;
        if (error != null) r["error"] = error;
        r["state"] = FlystsBuildState();
        return r;
    }

    private Dictionary<string, object?> FlystsBuildState()
    {
        if (_runState == null || !FlystsMode)
            return new() { ["state_type"] = "menu", ["menu_screen"] = "main", ["message"] = "No run in progress.", ["options"] = new List<string>() };
        var runState = _runState;
        var player = runState.Players[0];
        Dictionary<string, object?> state;
        if (_flystsAbandoned)
        {
            state = FlystsGameOverState(runState, victory: false);
            state["abandoned"] = true;
        }
        else if (player.Creature != null && player.Creature.IsDead)
        {
            state = FlystsGameOverState(runState, victory: false);
        }
        else
        {
            var native = _flystsNative ?? FlystsCurrentNative();
            state = FlystsStateFor(native, runState, player);
        }
        AttachFlystsRunState(state);
        return state;
    }

    private Dictionary<string, object?> FlystsStateFor(Dictionary<string, object?> native, RunState runState, Player player)
    {
        var decision = native.GetValueOrDefault("decision") as string ?? "";
        var sel = FlystsCurrentSelection();
        if (sel != null)
        {
            if (sel.IsHand && runState.CurrentRoom is CombatRoom hcr) return FlystsCombatState(runState, player, hcr);
            if (sel.Kind == FlystsSelectionKind.Bundle) return FlystsBundleState(runState, player, sel);
            if (sel.Kind == FlystsSelectionKind.ChooseACard) return FlystsChooseACardState(runState, player, sel);
            return FlystsGridState(runState, player, sel);
        }
        switch (decision)
        {
            case "game_over":
                return FlystsGameOverState(runState, victory: native.GetValueOrDefault("victory") is true);
            case "card_reward":
                return FlystsCardRewardState(runState, player);
            case "combat_play":
                if (runState.CurrentRoom is CombatRoom cr) return FlystsCombatState(runState, player, cr);
                break;
            case "map_select":
                return FlystsMapState(runState, player);
            case "event_choice":
            case "fake_merchant":
                if (runState.CurrentRoom is EventRoom er) return FlystsEventState(runState, player, er);
                break;
            case "rest_site":
                if (runState.CurrentRoom is RestSiteRoom rr) return FlystsRestSiteState(runState, player, rr);
                break;
            case "shop":
                if (runState.CurrentRoom is MerchantRoom mr) return FlystsShopState(runState, player, mr);
                break;
            case "rewards":
                return new() { ["state_type"] = "menu", ["menu_screen"] = "combat_rewards", ["message"] = "Claiming combat rewards.", ["options"] = new List<string> { "claim_and_proceed" }, ["run"] = FlystsRunInfo(runState) };
            case "treasure":
                return new() { ["state_type"] = "menu", ["menu_screen"] = "treasure_room", ["message"] = "Treasure room: opening chest / taking relics.", ["options"] = new List<string> { "advance" }, ["run"] = FlystsRunInfo(runState) };
            case "crystal_sphere":
                return new() { ["state_type"] = "menu", ["menu_screen"] = "crystal_sphere", ["message"] = "Crystal sphere minigame.", ["options"] = new List<string> { "advance" }, ["run"] = FlystsRunInfo(runState) };
        }
        return new() { ["state_type"] = "unknown", ["message"] = $"Unmapped decision '{decision}' in {runState.CurrentRoom?.GetType().Name ?? "null"}", ["run"] = FlystsRunInfo(runState) };
    }
}
