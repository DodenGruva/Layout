using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Layout.Config;
using Layout.Guide;
using Layout.Shapes;
using Layout.Systems;
using Layout.Undo;
using Layout.Undo.Commands;

namespace Layout.Network
{
    /// <summary>
    /// Server side of the Layout protocol: the one place client packets are validated and turned into
    /// authoritative state changes. It receives every C→S packet, checks it against the lock and existence
    /// rules, calls the matching <see cref="GuideManager"/> / <see cref="GuideLockManager"/> /
    /// <see cref="UndoManager"/> operation, records an undo command for the acting player, and broadcasts the
    /// result. It never trusts client data without validation, and never mutates guide state except through
    /// the managers.
    /// </summary>
    /// <remarks>
    /// RETURN-VALUE PIPELINE. The managers communicate by return value, not events (their deliberate design),
    /// so this handler is the sole consumer that turns a <see cref="GuideOperationResult"/> into the right
    /// broadcast or rejection. For undoability it follows the contract in <see cref="IGuideCommand"/>: it
    /// performs the mutation directly (to react to the result), and only on success records the command for
    /// later undo/redo — the command's Execute is reached via the redo path, never here.
    ///
    /// TWO BROADCAST STYLES.
    ///   • Direct operations broadcast a small incremental packet (a move sends just the moved indices; a
    ///     toggle sends just the new value) so every client applies the same change to its mirror.
    ///   • Undo / redo uses one generic rule instead of per-command branching: rebroadcast the affected
    ///     guide's full current state (<see cref="GuideCreatePacket"/>, an upsert on the client) — or a
    ///     delete if the guide no longer exists. That single rule covers every command type.
    ///
    /// DRAG COALESCING (why a 3-second drag is one undo entry, not thirty). Live move packets arrive at the
    /// client's throttle (~10 Hz). Applying and broadcasting each is correct, but recording an undo command
    /// per tick would bury the player's history under dozens of one-pixel nudges. Instead the handler
    /// remembers each grabbed point's PRE-DRAG position (captured the first time that index moves within a
    /// lock session) and, on release, records a single <see cref="MoveControlPointCommand"/> from origin to
    /// final position. A point that ends where it began records nothing.
    ///
    /// LOCK GATING (Module 7 — FULL EXCLUSIVITY; supersedes the earlier "ungated toggles" rule). While a
    /// guide is edit-locked, EVERY mutating operation on it from any player other than the lock holder is
    /// rejected: moves, releases, inserts, AND the property toggles (hide / rescale / projection / fill /
    /// lock-point) and dispel. The holder's exclusivity lasts exactly as long as the lock (release or
    /// disconnect frees it — a comatose grab, i.e. the holder swapped tools mid-edit, keeps it); this is
    /// still NOT ownership. An admin (controlserver privilege) may override the gate for the atomic
    /// operations when the server config allows it (<c>adminCanOverrideLocks</c>) — the remedy for a lock
    /// held indefinitely — but never for geometry edits, which genuinely require holding the lock.
    /// Inserting a body point still acquires the lock as part of the operation (and gives it back if the
    /// insert fails). Undo/redo respects the same gate inside <see cref="UndoManager"/>.
    ///
    /// PRIVILEGE GATING (Module 7). When server config sets <c>requiredPrivilege</c>, every state-changing
    /// request (create, draft-start, grab, move, insert, dispel, the toggles, undo/redo) from a player
    /// without that privilege is rejected with an in-game error. Cleanup paths (release, draft-cancel) stay
    /// open so a privilege revoked mid-session can never strand a lock or an anchor.
    ///
    /// JOIN / LEAVE. On join a player receives a full bulk sync (all guides + the active caps), the current
    /// lock state of any locked guide, and any in-progress draft anchors. On disconnect their locks are
    /// released (and broadcast as free), their undo history is dropped, their drag session is cleared, and
    /// any draft anchor of theirs is removed for everyone.
    ///
    /// THREADING. Authority mutations and game-world APIs remain on the server main thread. Immense create
    /// requests and final immense sculpts are the sole exceptions: one isolated candidate at a time performs
    /// pure shape counting and footprint generation on a low-priority worker, then returns to the tick thread
    /// for bounded claim checks and commit. Live manager collections are never exposed to that worker.
    /// </remarks>
    public class ServerNetworkHandler : IDisposable
    {
        private readonly ICoreServerAPI _sapi;
        private readonly IServerNetworkChannel _channel;
        private readonly GuideManager _guides;
        private readonly GuideLockManager _locks;
        private readonly UndoManager _undo;
        private readonly LayoutAdminPolicyManager _policies;
        // Read ONLY for the /layout info status line, and may be null. See the constructor.
        private readonly BackgroundGuidePersist _backgroundSave;

        // Server-config policy (Module 7). Empty/null privilege = everyone may use the tool.
        private readonly string _requiredPrivilege;
        // Readonly again as of v0.4.17: the panel no longer offers lock-override, so this is once more a
        // layout.json-only setting, like the privilege string above it.
        private readonly bool _adminCanOverrideLocks;
        // NOT readonly since v0.4.16: the settings page's Admin section changes these two in play.
        private bool _allowClientOnlyMode;
        private bool _chalkDurabilityEnabled;
        // The live server config and the callback that writes it back to layout.json. Held so an admin
        // change survives a restart instead of quietly reverting to whatever the file still says.
        private readonly LayoutServerConfig _config;
        private readonly Action<LayoutServerConfig> _persistConfig;
        private readonly HashSet<string> _clientOnlyPlayers = new HashSet<string>();

        // v0.2.22: per-player chalk refill-channel PREFERENCES (the player's own setting, reported on join),
        // not server policy. Needed because ItemChalkingPowder's held-interact runs on both sides and the
        // SERVER performs the mutation — without knowing the preference it would refill for a player who
        // had turned the hotbar shortcut off. Absent entry = not opted in (matches the client default).
        private readonly HashSet<string> _hotbarRefillOptIn = new HashSet<string>();
        private const int MaxGuidesPerPush = 100;

        /// <summary>Defensive ceiling on a PUSHED guide's point lists. A Free-Shape's own limit is 64.</summary>
        private const int MaxPushedControlPoints = 1024;

        // Minimum gap between one player's private-guide pushes. Generous — this is a deliberate,
        // occasional action (a chat command or a settings-page button), never something held down.
        private const long PushCooldownMilliseconds = 3000;
        private readonly Dictionary<string, long> _lastPushAt = new Dictionary<string, long>();

        // ---- Per-player mutation rate limit (A14.9) --------------------------------------------------
        //
        // There was NO rate limit on any handler. One update packet can run geometry work, voxel counting,
        // claim validation, a full-registry save and a broadcast to everyone — and a client could send
        // them as fast as it liked.
        //
        // DELIBERATELY FAR ABOVE ANYTHING A PLAYER CAN DO. A drag sends roughly ten updates a second; the
        // sustained allowance here is 120 a second, and the bucket holds a further two seconds' worth for
        // bursts. Normal play — including fast dragging, rapid toggling, and holding undo down — should
        // never come near it, so if this ever fires during ordinary play the number is wrong, not the
        // player. That is why it logs when it trips: a silently-ignored edit is a miserable thing to
        // diagnose, so it leaves a trail.
        private const double RateBucketCapacity = 240.0;
        private const double RateBucketRefillPerSecond = 120.0;

        private sealed class RateBucket
        {
            public double Tokens = RateBucketCapacity;
            public long LastMs;
            public long LastWarnMs;
        }

        private readonly Dictionary<string, RateBucket> _rateBuckets =
            new Dictionary<string, RateBucket>();

        // Immense public placements take a second path. The tick thread only performs a small threshold
        // probe and bounded claim lookups; one low-priority worker at a time owns the expensive pure geometry.
        private const int StreamedCreateVoxelThreshold = 8000;
        private const int MaxQueuedImmenseCreates = 8;
        private const int MaxClaimChecksPerTick = 128;
        private const double ClaimCheckBudgetMilliseconds = 1.0;

        private sealed class ImmenseCreateGeometry
        {
            public int VoxelCount;
            public int ExceededCap;
            public List<BlockPos> Footprint;
            public ClaimFootprintBounds ClaimBounds;
        }

        private sealed class PendingImmenseCreate
        {
            public string PlayerUid;
            public PreparedGuideCreation Prepared;
            public int CountLimit;
            public int CountLimitCap;
            public Task<ImmenseCreateGeometry> GeometryTask;
            public ImmenseCreateGeometry Geometry;
            public int ClaimIndex;
            public ClaimAccessSnapshot ClaimSnapshot;
            public int ClaimRestarts;

            // VOLATILE since v0.4.36: set on the server tick thread, now READ BY THE WORKER. Before this it
            // was tick-thread-only and cancelling did not stop anything — the geometry ran to completion
            // with the single validator lane occupied, however long after the player gave up.
            public volatile bool Cancelled;
        }

        private readonly Queue<PendingImmenseCreate> _immenseCreateQueue =
            new Queue<PendingImmenseCreate>();
        private readonly HashSet<string> _playersWithPendingImmenseCreate =
            new HashSet<string>();
        private PendingImmenseCreate _activeImmenseCreate;
        private long _immenseCreateTickId;

        private sealed class PendingImmenseSculpt
        {
            public string PlayerUid;
            public Guid GuideId;
            public GuideData ExpectedLive;
            public GuideData Candidate;
            public IGuideShape CandidateShape;
            public int CountLimit;
            public int CountLimitCap;
            public Task<ImmenseCreateGeometry> GeometryTask;
            public ImmenseCreateGeometry Geometry;
            public int ClaimIndex;
            public ClaimAccessSnapshot ClaimSnapshot;
            public int ClaimRestarts;
            public bool ReleaseRequested;

            // VOLATILE — see PendingImmenseCreate.Cancelled above; same reason, same thread pairing.
            public volatile bool Cancelled;
        }

        private readonly Queue<PendingImmenseSculpt> _immenseSculptQueue =
            new Queue<PendingImmenseSculpt>();
        private readonly Dictionary<string, PendingImmenseSculpt> _pendingImmenseSculpts =
            new Dictionary<string, PendingImmenseSculpt>();
        private PendingImmenseSculpt _activeImmenseSculpt;
        private long _immenseSculptTickId;
        private bool _disposed;

        // Per-player in-progress drag: the guide being edited and each moved point's pre-drag origin.
        // Used to coalesce a drag into a single undo entry on release — and, since Session 8, to service
        // GuideCancelGrabPacket (restore origins, or remove the point when the grab began as an insert).
        private sealed class DragSession
        {
            public Guid GuideId;
            public readonly Dictionary<int, Vec3d> Origins = new Dictionary<int, Vec3d>();
            public ShapeConstraint OriginConstraint;
            public List<ControlPoint> OriginPoints;
            public int OriginVoxelCount = -1;
            public bool CommitAsWholeShape;

            /// <summary>
            /// Index of the point this grab CREATED (body insert), or −1 for a grab of a pre-existing
            /// point. Cancel removes an inserted point outright instead of restoring it.
            /// </summary>
            public int InsertedIndex = -1;

            /// <summary>
            /// The soft-point mapping captured at the FIRST move of a structural point in this drag
            /// (Session 8): unlocked interior points flow with the structural baseline for the rest of
            /// the drag. Null until (and unless) a structural point moves.
            /// </summary>
            public SoftPointFlow SoftFlow;

            public DragSession(Guid guideId) { GuideId = guideId; }
        }

        private readonly Dictionary<string, DragSession> _drags = new Dictionary<string, DragSession>();

        // Players with a live draft start anchor, and where it is — so joiners can be shown in-progress
        // drafts and a disconnect can clean the anchor up.
        private readonly Dictionary<string, Vec3d> _draftAnchors = new Dictionary<string, Vec3d>();

        // Last cap-refusal sentence sent to each player, and when. A refused DRAG re-fires the cap path
        // roughly ten times a second, so the chat line has to be throttled or the fix is worse than the
        // silence it replaces. Keyed on the sentence, not the player alone: hitting a different cap is
        // new information and says so immediately. The WARNING PACKET is never throttled — it was already
        // flowing at that rate and it is what the HUD gauge reads.
        private const long CapRefusalRepeatMilliseconds = 4000;
        private readonly Dictionary<string, (string Message, long At)> _lastCapRefusal =
            new Dictionary<string, (string, long)>();

        public ServerNetworkHandler(
            ICoreServerAPI sapi,
            GuideManager guideManager,
            GuideLockManager lockManager,
            UndoManager undoManager,
            LayoutAdminPolicyManager adminPolicies,
            LayoutServerConfig config = null,
            Action<LayoutServerConfig> persistConfig = null,
            BackgroundGuidePersist backgroundSave = null)
        {
            _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
            _guides = guideManager ?? throw new ArgumentNullException(nameof(guideManager));
            _locks = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
            _undo = undoManager ?? throw new ArgumentNullException(nameof(undoManager));
            _policies = adminPolicies ?? throw new ArgumentNullException(nameof(adminPolicies));
            // Optional, and only ever read for the /layout info status line. Null simply omits that line —
            // it must never become something the mutation paths depend on.
            _backgroundSave = backgroundSave;
            _config = config ?? new LayoutServerConfig();
            _persistConfig = persistConfig;
            _requiredPrivilege = string.IsNullOrWhiteSpace(_config.RequiredPrivilege)
                ? null : _config.RequiredPrivilege.Trim();
            _adminCanOverrideLocks = _config.AdminCanOverrideLocks;
            _allowClientOnlyMode = _config.AllowClientOnlyMode;
            _chalkDurabilityEnabled = _config.EnableChalkDurability;

            _channel = _sapi.Network.RegisterChannel(LayoutChannel.Name);
            LayoutPackets.RegisterMessageTypes(_channel);

            // The cost weights: 1 for a per-drag edit or a toggle, 5 for a placement that can queue
            // geometry work, 10 for anything that sweeps the whole registry. See RateBucketCapacity.
            _channel
                .SetMessageHandler<GuideCreateRequestPacket>((p, x) => Gated(p, 5, () => OnCreateRequest(p, x)))
                .SetMessageHandler<ChalkChargePacket>(OnChalkCharge)
                .SetMessageHandler<ChalkInventoryRefillPacket>(OnInventoryChalkRefill)
                .SetMessageHandler<ChalkRefillPrefsPacket>(OnChalkRefillPrefs)
                .SetMessageHandler<GuideGrabPacket>((p, x) => Gated(p, 1, () => OnGrab(p, x)))
                // ⚠️ RELEASE AND CANCEL ARE NOT RATE LIMITED, deliberately. They are the packets that GIVE
                // BACK a lock and end a drag. Dropping one leaves the player holding a guide nobody else
                // can edit until they disconnect — the rate limiter would be manufacturing exactly the
                // lock-hoarding A14.10 just fixed. Both are also self-limiting: releasing a guide you do
                // not hold does nothing, so a repeat costs a dictionary lookup.
                .SetMessageHandler<GuideReleasePacket>((p, x) => WithPlayerVoxelCap(p, () => OnRelease(p, x)))
                .SetMessageHandler<GuideCancelGrabPacket>((p, x) => WithPlayerVoxelCap(p, () => OnCancelGrab(p, x)))
                .SetMessageHandler<GuideUpdatePacket>((p, x) => Gated(p, 1, () => OnUpdate(p, x)))
                .SetMessageHandler<GuideInsertPointPacket>((p, x) => Gated(p, 2, () => OnInsert(p, x)))
                .SetMessageHandler<GuideDeletePacket>((p, x) => Gated(p, 2, () => OnDelete(p, x)))
                .SetMessageHandler<GuideHidePacket>((p, x) => Gated(p, 1, () => OnHide(p, x)))
                .SetMessageHandler<GuideLockPointPacket>((p, x) => Gated(p, 1, () => OnLockPoint(p, x)))
                .SetMessageHandler<GuideRescalePacket>((p, x) => Gated(p, 2, () => OnRescale(p, x)))
                .SetMessageHandler<GuideSetProjectionPacket>((p, x) => Gated(p, 1, () => OnSetProjection(p, x)))
                .SetMessageHandler<GuideSetFilledPacket>((p, x) => Gated(p, 1, () => OnSetFilled(p, x)))
                .SetMessageHandler<GuideSetWireframePacket>((p, x) => Gated(p, 1, () => OnSetWireframe(p, x)))
                .SetMessageHandler<GuideSetDivisionsPacket>((p, x) => Gated(p, 1, () => OnSetDivisions(p, x)))
                .SetMessageHandler<GuideSetSidesPacket>((p, x) => Gated(p, 1, () => OnSetSides(p, x)))
                .SetMessageHandler<GuideSpringBackPacket>((p, x) => Gated(p, 2, () => OnSpringBack(p, x)))
                .SetMessageHandler<GuideTranslatePacket>((p, x) => Gated(p, 1, () => OnTranslate(p, x)))
                .SetMessageHandler<GuideRotatePacket>((p, x) => Gated(p, 1, () => OnRotate(p, x)))
                .SetMessageHandler<GuideTransformPacket>((p, x) => Gated(p, 2, () => OnTransform(p, x)))
                // Draft START runs a claim check and BROADCASTS the anchor to everyone, so it belongs in the
                // budget. Draft CANCEL is cleanup and is left alone for the same reason as release.
                .SetMessageHandler<DraftStartPacket>((p, x) => { if (!RateLimited(p, 1)) OnDraftStart(p, x); })
                .SetMessageHandler<DraftCancelPacket>(OnDraftCancel)
                .SetMessageHandler<UndoRequestPacket>((p, x) => Gated(p, 2, () => OnUndo(p, x)))
                .SetMessageHandler<RedoRequestPacket>((p, x) => Gated(p, 2, () => OnRedo(p, x)))
                .SetMessageHandler<LayoutAdminConfigRequestPacket>(OnAdminConfigRequest)
                .SetMessageHandler<GuideRevealMinePacket>((p, x) => Gated(p, 10, () => OnRevealMine(p, x)))
                .SetMessageHandler<PlayerRosterRequestPacket>(OnPlayerRosterRequest)
                .SetMessageHandler<PlayerGuidesRequestPacket>(OnPlayerGuidesRequest)
                .SetMessageHandler<PlayerPolicyEditPacket>(OnPlayerPolicyEdit)
                .SetMessageHandler<PlayerJailPacket>(OnPlayerJail)
                .SetMessageHandler<ClientPlacementModeRequestPacket>(OnClientPlacementModeRequest)
                .SetMessageHandler<ClientGuidePushPacket>((p, x) => Gated(p, 10, () => OnClientGuidePush(p, x)));

            _sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            _sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
            _immenseCreateTickId = _sapi.Event.RegisterGameTickListener(
                OnImmenseCreateTick, 20);
            _immenseSculptTickId = _sapi.Event.RegisterGameTickListener(
                OnImmenseSculptTick, 20);

            RegisterCommands();
        }

        // ==========================================================================================
        //  Admin commands (v0.1.26; namespaced under /layout in v0.1.27 so they can't clash with other
        //  mods):  /layout dispel all  ·  /layout dispel <chunk radius>
        // ==========================================================================================

        private void RegisterCommands()
        {
            var parsers = _sapi.ChatCommands.Parsers;
            _sapi.ChatCommands
                .Create("layout")
                .WithDescription("Layout guide and placement-mode commands.")
                .RequiresPrivilege(Privilege.chat)
                .BeginSubCommand("dispel")
                    .WithDescription("Dispel by world, radius, player, or guide id.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("target"), parsers.OptionalWord("value"))
                    .HandleWith(OnDispelCommand)
                .EndSubCommand()
                .BeginSubCommand("jail")
                    .WithDescription("Suspend a player from every public Layout mutation.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("player"))
                    .HandleWith(OnJailCommand)
                .EndSubCommand()
                .BeginSubCommand("free")
                    .WithDescription("Remove a player's public Layout suspension.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("player"))
                    .HandleWith(OnFreeCommand)
                .EndSubCommand()
                .BeginSubCommand("limit")
                    .WithDescription("Set a player's concurrent public-guide limit; 0 restores the server default.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("player"), parsers.Int("number"))
                    .HandleWith(OnLimitCommand)
                .EndSubCommand()
                .BeginSubCommand("voxelcap")
                    .WithDescription("Set a player's per-guide voxel cap; 0 restores the server default.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("player"), parsers.Int("number"))
                    .HandleWith(OnVoxelCapCommand)
                .EndSubCommand()
                .BeginSubCommand("totalvoxelcap")
                    .WithDescription("Set a player's cumulative voxel cap; 0 restores the server default.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("player"), parsers.Int("number"))
                    .HandleWith(OnTotalVoxelCapCommand)
                .EndSubCommand()
                .BeginSubCommand("info")
                    .WithDescription("Show Layout server or player usage and policy information.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.OptionalWord("player"))
                    .HandleWith(OnInfoCommand)
                .EndSubCommand()
                .BeginSubCommand("jailroster")
                    .WithDescription("List every player currently jailed from public Layout mutations.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(OnJailRosterCommand)
                .EndSubCommand()
                .BeginSubCommand("top")
                    .WithDescription("Show the largest guides or highest-usage players.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("guides-or-players"))
                    .HandleWith(OnTopCommand)
                .EndSubCommand()
                .BeginSubCommand("private")
                    .WithDescription("Place new guides privately on this client when the server permits it.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(OnPrivateModeCommand)
                .EndSubCommand()
                .BeginSubCommand("public")
                    .WithDescription("Place new guides publicly on the server.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(OnPublicModeCommand)
                .EndSubCommand()
                .BeginSubCommand("who")
                    .WithDescription("Show the creator and last sculptor of your selected or targeted guide.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(OnWhoCommand)
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Turn off all Layout guide rendering for yourself.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(OnRenderingOffCommand)
                .EndSubCommand()
                .BeginSubCommand("on")
                    .WithDescription("Turn on all Layout guide rendering for yourself.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(OnRenderingOnCommand)
                .EndSubCommand()
                .BeginSubCommand("client")
                    .WithDescription("Private-guide publication commands.")
                    .RequiresPrivilege(Privilege.chat)
                    .BeginSubCommand("push")
                        .WithDescription("Publish private guides to the server.")
                        .RequiresPrivilege(Privilege.chat)
                        .WithArgs(parsers.Word("all"))
                        .HandleWith(OnClientPushCommand)
                    .EndSubCommand()
                .EndSubCommand();
        }

        private TextCommandResult OnJailCommand(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayer(args[0] as string, out PlayerIdentity target, out string error))
                return TextCommandResult.Error(error);

            bool changed = _policies.SetJailed(target.Uid, target.Name, true);
            if (target.Online != null)
            {
                WithPlayerVoxelCap(target.Online, () => CancelPlayerPublicActivity(target.Online));
                SendPlayerPolicy(target.Online);
                target.Online.SendIngameError("layout-jailed",
                    "An administrator suspended your access to public Layout guides.");
            }
            else
            {
                _undo.ClearPlayer(target.Uid);
                ClearPlayerSessionState(target.Uid);
            }

            return TextCommandResult.Success(changed
                ? $"Jailed {target.Name}: all public Layout mutations are now blocked."
                : $"{target.Name} is already jailed.");
        }

        private TextCommandResult OnFreeCommand(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayer(args[0] as string, out PlayerIdentity target, out string error))
                return TextCommandResult.Error(error);
            bool changed = _policies.SetJailed(target.Uid, target.Name, false);
            if (target.Online != null) SendPlayerPolicy(target.Online);
            return TextCommandResult.Success(changed
                ? $"Freed {target.Name}: public Layout access is restored. Personal cap overrides were kept."
                : $"{target.Name} was not jailed. Personal cap overrides were not changed.");
        }

        private TextCommandResult OnLimitCommand(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayer(args[0] as string, out PlayerIdentity target, out string error))
                return TextCommandResult.Error(error);
            int limit = args[1] is int n ? n : -1;
            if (limit < 0) return TextCommandResult.Error("The guide limit must be 0 or greater.");

            _policies.SetGuideLimit(target.Uid, target.Name, limit);
            SendAdminConfig(target.Online);   // their panel's override notice follows the change
            int effective = _policies.EffectiveGuideLimit(target.Uid, _guides.MaxGuidesPerPlayer);
            int current = _guides.CountGuidesBy(target.Uid);
            return TextCommandResult.Success(limit == 0
                ? $"Removed {target.Name}'s custom guide limit. Effective limit: {FormatCap(effective)}; currently {current}."
                : $"Set {target.Name}'s concurrent guide limit to {limit}; currently {current}. Existing guides were not removed.");
        }

        private TextCommandResult OnVoxelCapCommand(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayer(args[0] as string, out PlayerIdentity target, out string error))
                return TextCommandResult.Error(error);
            int cap = args[1] is int n ? n : -1;
            if (cap < 0) return TextCommandResult.Error("The voxel cap must be 0 or greater.");
            if (cap > GuideManager.HardVoxelCeiling)
                return TextCommandResult.Error($"The absolute safety ceiling is {GuideManager.HardVoxelCeiling:n0} voxels per guide.");

            _policies.SetVoxelCap(target.Uid, target.Name, cap);
            int effective = _policies.EffectiveVoxelCap(target.Uid, _guides.PerGuideVoxelCap);
            if (target.Online != null) SendPlayerPolicy(target.Online);
            SendAdminConfig(target.Online);   // their panel's override notice follows the change
            return TextCommandResult.Success(cap == 0
                ? $"Removed {target.Name}'s custom voxel cap. Effective per-guide cap: {FormatCap(effective)}."
                : $"Set {target.Name}'s per-guide voxel cap to {cap:n0}. Per-player cumulative, world, and absolute safety caps still apply.");
        }

        private TextCommandResult OnTotalVoxelCapCommand(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayer(args[0] as string, out PlayerIdentity target, out string error))
                return TextCommandResult.Error(error);
            int cap = args[1] is int n ? n : -1;
            if (cap < 0)
                return TextCommandResult.Error("The cumulative voxel cap must be 0 or greater.");

            _policies.SetPlayerTotalVoxelCap(target.Uid, target.Name, cap);
            SendAdminConfig(target.Online);   // their panel's override notice follows the change
            int effective = _guides.EffectivePerPlayerTotalVoxelCap(target.Uid);
            long current = _guides.VoxelCountBy(target.Uid);
            return TextCommandResult.Success(cap == 0
                ? $"Removed {target.Name}'s custom cumulative voxel cap. Effective limit: {FormatCap(effective)}; currently {current:n0}."
                : $"Set {target.Name}'s cumulative voxel cap to {cap:n0}; currently {current:n0}. Existing guides were not removed.");
        }

        private TextCommandResult OnInfoCommand(TextCommandCallingArgs args)
        {
            string playerName = args[0] as string;
            if (string.IsNullOrWhiteSpace(playerName)) return TextCommandResult.Success(ServerInfoText());
            if (!TryResolvePlayer(playerName, out PlayerIdentity target, out string error))
                return TextCommandResult.Error(error);
            return TextCommandResult.Success(PlayerInfoText(target));
        }

        private TextCommandResult OnJailRosterCommand(TextCommandCallingArgs args)
        {
            List<PlayerPolicy> jailed = _policies.Policies
                .Where(policy => policy.Jailed)
                .OrderBy(policy => policy.LastKnownName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (jailed.Count == 0) return TextCommandResult.Success("The Layout jail roster is empty.");

            var text = new StringBuilder($"Layout jail roster ({jailed.Count}):");
            for (int i = 0; i < jailed.Count; i++)
            {
                PlayerPolicy policy = jailed[i];
                bool online = _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
                    .Any(player => player.PlayerUID == policy.PlayerUid);
                text.Append('\n').Append(i + 1).Append(". ")
                    .Append(string.IsNullOrWhiteSpace(policy.LastKnownName) ? "Unknown" : policy.LastKnownName)
                    .Append(online ? " — online" : " — offline")
                    .Append(" — ").Append(_guides.CountGuidesBy(policy.PlayerUid).ToString("n0"))
                    .Append(" public guide(s)");
            }
            return TextCommandResult.Success(text.ToString());
        }

        private TextCommandResult OnTopCommand(TextCommandCallingArgs args)
        {
            string kind = (args[0] as string)?.Trim().ToLowerInvariant();
            if (kind == "guides") return TextCommandResult.Success(TopGuidesText());
            if (kind == "players") return TextCommandResult.Success(TopPlayersText());
            return TextCommandResult.Error("Usage: /layout top guides   OR   /layout top players");
        }

        private TextCommandResult OnWhoCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command must be run by a player.");

            _channel.SendPacket(new GuideWhoQueryPacket(), player);
            return TextCommandResult.Success();
        }

        private TextCommandResult OnRenderingOffCommand(TextCommandCallingArgs args) =>
            SendRenderingState(args, false);

        private TextCommandResult OnRenderingOnCommand(TextCommandCallingArgs args) =>
            SendRenderingState(args, true);

        private TextCommandResult SendRenderingState(
            TextCommandCallingArgs args, bool enabled)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command must be run by a player.");

            _channel.SendPacket(new GuideRenderingPacket(enabled), player);
            return TextCommandResult.Success(enabled
                ? "Layout guide rendering is on for you."
                : "Layout guide rendering is off for you.");
        }

        private TextCommandResult OnPrivateModeCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command must be run by a player.");

            if (!_allowClientOnlyMode)
            {
                SendPlacementMode(player, false, false, false,
                    null);
                return TextCommandResult.Error("This server does not allow client-only Layout mode.");
            }

            SetClientOnlyMode(player, true, updatePreference: true);
            return TextCommandResult.Success("New Layout guides will be client-only.");
        }

        private TextCommandResult OnPublicModeCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command must be run by a player.");

            SetClientOnlyMode(player, false, updatePreference: true);
            return TextCommandResult.Success("New Layout guides will be stored on the server.");
        }

        private TextCommandResult OnClientPushCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command must be run by a player.");
            string argument = (args[0] as string)?.Trim().ToLowerInvariant();
            if (argument != "all") return TextCommandResult.Error("Usage: /layout client push all");
            if (_policies.IsJailed(player.PlayerUID))
                return TextCommandResult.Error("An administrator suspended your access to public Layout guides.");
            if (_clientOnlyPlayers.Contains(player.PlayerUID))
                return TextCommandResult.Error("Switch to public mode first with /layout public.");

            _channel.SendPacket(new ClientGuidePushRequestPacket(), player);
            return TextCommandResult.Success("Requested all private guides for server publication.");
        }

        private TextCommandResult OnDispelCommand(TextCommandCallingArgs args)
        {
            string arg = (args[0] as string)?.Trim().ToLowerInvariant();
            string value = (args[1] as string)?.Trim();

            if (arg == "all")
            {
                int n = DispelGuides(null, 0);
                return TextCommandResult.Success($"Dispelled all {n} Layout guide(s) in the world.");
            }

            if (arg == "player")
            {
                if (!TryResolvePlayer(value, out PlayerIdentity target, out string error))
                    return TextCommandResult.Error(error);
                int n = DispelGuidesByCreator(target.Uid);
                return TextCommandResult.Success($"Dispelled {n} guide(s) created by {target.Name}.");
            }

            if (arg == "guide")
            {
                if (!TryResolveGuide(value, out Guid id, out string error))
                    return TextCommandResult.Error(error);
                GuideData guide = _guides.AllGuides[id];
                string description = $"{guide.ShapeType} {ShortId(id)} at {FormatAnchor(guide)}";
                return DeleteGuideAdministratively(id)
                    ? TextCommandResult.Success($"Dispelled {description}.")
                    : TextCommandResult.Error("That guide no longer exists.");
            }

            if (int.TryParse(arg, out int radius) && radius >= 0)
            {
                if (args.Caller.Player is not IServerPlayer p)
                    return TextCommandResult.Error("A radius dispel must be run by a player (use '/layout dispel all' from the console).");
                int n = DispelGuides(p, radius);
                return TextCommandResult.Success($"Dispelled {n} Layout guide(s) within {radius} chunk(s).");
            }

            return TextCommandResult.Error(
                "Usage: /layout dispel all | <chunk radius> | player <name> | guide <id>");
        }

        // Dispels guides, force-freeing their locks and broadcasting the removals. A null player dispels
        // EVERY guide; otherwise only guides whose anchor lies within <radius> chunks (Chebyshev) of the
        // player. Returns the count removed.
        private int DispelGuides(IServerPlayer player, int radius)
        {
            const int chunk = 32;                                 // VS chunks are 32 blocks on a side
            int pcx = 0, pcz = 0;
            if (player != null)
            {
                pcx = (int)Math.Floor(player.Entity.Pos.X / chunk);
                pcz = (int)Math.Floor(player.Entity.Pos.Z / chunk);
            }

            var targets = new List<Guid>();
            foreach (var kv in _guides.AllGuides)
            {
                if (player == null) { targets.Add(kv.Key); continue; }
                Vec3d a = FirstAnchorPos(kv.Value);
                if (a == null) continue;
                int gcx = (int)Math.Floor(a.X / chunk), gcz = (int)Math.Floor(a.Z / chunk);
                if (Math.Abs(gcx - pcx) <= radius && Math.Abs(gcz - pcz) <= radius) targets.Add(kv.Key);
            }

            int removed = 0;
            foreach (Guid id in targets)
            {
                if (DeleteGuideAdministratively(id)) removed++;
            }
            return removed;
        }

        private int DispelGuidesByCreator(string playerUid)
        {
            var targets = _guides.AllGuides.Values
                .Where(g => g.CreatorUid == playerUid)
                .Select(g => g.Id)
                .ToList();
            int removed = 0;
            foreach (Guid id in targets)
                if (DeleteGuideAdministratively(id)) removed++;
            return removed;
        }

        private bool DeleteGuideAdministratively(Guid id)
        {
            if (_guides.DeleteGuide(id).Status != GuideOpStatus.Success) return false;
            _locks.ClearLock(id);
            foreach (string uid in _drags.Where(p => p.Value.GuideId == id).Select(p => p.Key).ToList())
                _drags.Remove(uid);
            _channel.BroadcastPacket(new GuideDeletePacket(id));
            return true;
        }

        private static Vec3d FirstAnchorPos(GuideData g)
        {
            if (g?.ControlPoints == null) return null;
            foreach (ControlPoint cp in g.ControlPoints)
                if (cp != null && !cp.IsPhantom && cp.WorldPosition != null) return cp.WorldPosition;
            return null;
        }

        // ==========================================================================================
        //  Join / leave
        // ==========================================================================================

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            _policies.RememberName(player.PlayerUID, PlayerDisplayName(player));
            // 1) Bulk sync: every guide + the caps the server actually enforces.
            var all = new List<GuideDataDto>(_guides.AllGuides.Count);
            foreach (var g in _guides.AllGuides.Values) all.Add(GuideDataDto.From(g));
            // The two trailing refill flags are DEAD as of v0.2.22 (the channel became a client preference);
            // they are still sent as false because packet fields are append-only and must not be renumbered.
            _channel.SendPacket(
                new GuideBulkSyncPacket(all.ToArray(),
                    _policies.EffectiveVoxelCap(player.PlayerUID, _guides.PerGuideVoxelCap),
                    _guides.TotalVoxelCap,
                    _allowClientOnlyMode),
                player);
            SendPlayerPolicy(player);
            SendAdminConfig(player);

            // 2) Current lock state of any locked guide, so the joiner sees what is being edited.
            foreach (var id in _guides.AllGuides.Keys)
            {
                string holder = _locks.GetHolder(id);
                if (holder != null) _channel.SendPacket(new GuideLockStatePacket(id, holder), player);
            }

            // 3) Any in-progress draft anchors of other players.
            foreach (var pair in _draftAnchors)
                _channel.SendPacket(new DraftAnchorBroadcastPacket(pair.Key, Vec3Dto.From(pair.Value)), player);
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            string uid = player.PlayerUID;

            // Free their locks and tell everyone those guides are editable again.
            IReadOnlyList<Guid> freed = _locks.ReleaseAllLocksForPlayer(uid);
            for (int i = 0; i < freed.Count; i++)
                _channel.BroadcastPacket(new GuideLockStatePacket(freed[i], null));

            _undo.ClearPlayer(uid);
            _drags.Remove(uid);
            _clientOnlyPlayers.Remove(uid);
            _hotbarRefillOptIn.Remove(uid);
            _lastCapRefusal.Remove(uid);
            _lastPushAt.Remove(uid);
            _rateBuckets.Remove(uid);
            CancelPendingImmenseCreate(uid);
            CancelPendingImmenseSculpt(uid);

            if (_draftAnchors.Remove(uid))
                _channel.BroadcastPacket(new DraftAnchorRemovePacket(uid));
        }

        private void OnClientPlacementModeRequest(IServerPlayer player, ClientPlacementModeRequestPacket packet)
        {
            bool wantsClientOnly = packet?.ClientOnly == true;
            if (wantsClientOnly && !_allowClientOnlyMode)
            {
                SendPlacementMode(player, false, false, false,
                    "This server does not allow client-only Layout mode.");
                return;
            }

            SetClientOnlyMode(player, wantsClientOnly, updatePreference: false);
        }

        private void SetClientOnlyMode(IServerPlayer player, bool clientOnly, bool updatePreference)
        {
            bool wasClientOnly = _clientOnlyPlayers.Contains(player.PlayerUID);
            if (clientOnly) _clientOnlyPlayers.Add(player.PlayerUID);
            else _clientOnlyPlayers.Remove(player.PlayerUID);

            if (wasClientOnly != clientOnly)
            {
                if (_playersWithPendingImmenseCreate.Contains(player.PlayerUID))
                    SendPlacementRejected(player, GuideOpStatus.InvalidArgument);
                CancelPendingImmenseCreate(player.PlayerUID);
                CancelPendingImmenseSculpt(player.PlayerUID);
                IReadOnlyList<Guid> freed = _locks.ReleaseAllLocksForPlayer(player.PlayerUID);
                foreach (Guid id in freed)
                    _channel.BroadcastPacket(new GuideLockStatePacket(id, null));
                _drags.Remove(player.PlayerUID);
                if (_draftAnchors.Remove(player.PlayerUID))
                    _channel.BroadcastPacket(new DraftAnchorRemovePacket(player.PlayerUID));
            }

            SendPlacementMode(player, clientOnly, true, updatePreference, null);
        }

        private void SendPlacementMode(IServerPlayer player, bool clientOnly, bool allowed,
            bool updatePreference, string message)
        {
            _channel.SendPacket(
                new ClientPlacementModePacket(clientOnly, allowed, updatePreference, message), player);
        }

        private void OnClientGuidePush(IServerPlayer player, ClientGuidePushPacket packet)
        {
            if (_clientOnlyPlayers.Contains(player.PlayerUID))
            {
                _channel.SendPacket(new ClientGuidePushResultPacket(
                    Array.Empty<byte[]>(), packet?.Guides?.Length ?? 0,
                    "Switch to public mode before pushing private guides (/layout public)."), player);
                return;
            }

            // A server that does not allow private guides at all has no business ACCEPTING a folder of
            // them. The handler checked client-only mode and jail and never this (A14.8).
            //
            // NOT A CAP, so R1 is untouched: R1 settles that private guides are not subject to the voxel
            // budgets, because private guides consume no shared resource. A guide being PUBLISHED consumes
            // both — and this check is about authorisation, not budget, on a server whose admin turned the
            // whole private-guide feature off.
            if (!_allowClientOnlyMode)
            {
                _channel.SendPacket(new ClientGuidePushResultPacket(
                    Array.Empty<byte[]>(), packet?.Guides?.Length ?? 0,
                    "This server does not allow private Layout guides."), player);
                return;
            }

            if (DeniedByPrivilege(player))
            {
                _channel.SendPacket(new ClientGuidePushResultPacket(
                    Array.Empty<byte[]>(), packet?.Guides?.Length ?? 0,
                    "Your access to public Layout guides is restricted."), player);
                return;
            }

            // ONE PUSH AT A TIME. MaxGuidesPerPush bounds a single packet at 100 and nothing bounded the
            // PACKETS, so an unbounded stream of them was accepted — each one running claim validation,
            // voxel counting and — before saves were deferred — a full registry save per guide.
            //
            // ⚠️ Deliberately a cooldown and NOT "require an outstanding server request", which is what the
            // review proposed. `ClientGuidePushPacket` is legitimately sent unprompted by the settings
            // page's "Publish Private Guides" button; ClientNetworkHandler documents the request packet as
            // "the server's way of asking, never a permission token". Gating on it would break the shipped
            // GUI button.
            if (PushThrottled(player.PlayerUID))
            {
                _channel.SendPacket(new ClientGuidePushResultPacket(
                    Array.Empty<byte[]>(), packet?.Guides?.Length ?? 0,
                    "Still publishing your last batch. Try again in a moment."), player);
                return;
            }

            ClientGuidePushDto[] incoming = packet?.Guides ?? Array.Empty<ClientGuidePushDto>();
            var accepted = new List<byte[]>();
            BlockPos firstClaimDenied = null;
            int rejected = Math.Max(0, incoming.Length - MaxGuidesPerPush);
            int count = Math.Min(incoming.Length, MaxGuidesPerPush);

            // ONE SAVE for the whole batch instead of one per accepted guide (A14.8): publishing 100
            // guides used to re-serialise the entire world's registry 100 times. This needed an explicit
            // BatchPersist scope until mutations stopped writing at all — GuideManager.MarkDirty now
            // coalesces every save on every path, so the batch is handled without saying anything here.

            for (int i = 0; i < count; i++)
            {
                try
                {
                    GuideData candidate = incoming[i]?.ToGuideData();
                    Guid localId = candidate?.Id ?? Guid.Empty;
                    if (candidate == null || localId == Guid.Empty)
                    {
                        rejected++;
                        continue;
                    }

                    // Both point lists arrive from the client and were copied wholesale with no length
                    // check. No real guide comes close to this ceiling — a Free-Shape tops out at 64
                    // corners — so it only ever refuses a list built to be big.
                    if (candidate.ControlPoints?.Count > MaxPushedControlPoints
                        || candidate.OriginalControlPoints?.Count > MaxPushedControlPoints)
                    {
                        rejected++;
                        continue;
                    }

                    candidate.Id = Guid.NewGuid();
                    candidate.CreatorUid = player.PlayerUID;
                    candidate.CreatorName = PlayerDisplayName(player);
                    candidate.LastSculptorUid = player.PlayerUID;
                    candidate.LastSculptorName = candidate.CreatorName;
                    candidate.DataVersion = GuideData.CurrentDataVersion;
                    GuideOperationResult result = _guides.RestoreGuide(candidate);
                    if (!result.IsSuccess)
                    {
                        if (result.Status == GuideOpStatus.RejectedClaimAccess && firstClaimDenied == null)
                            firstClaimDenied = result.DeniedPosition;
                        rejected++;
                        continue;
                    }

                    // Publishing is a committed transfer, not a normal server-side creation gesture. The
                    // client removes its private copy after this acceptance is confirmed, so recording a
                    // CreateGuideCommand here would let Ctrl+Z delete the player's only remaining copy.
                    _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
                    accepted.Add(NetIds.ToBytes(localId));
                }
                catch (Exception e)
                {
                    rejected++;
                    _sapi.Logger.Warning("[Layout] Rejected a pushed guide from {0}: {1}",
                        player.PlayerUID, e.Message);
                }
            }

            string summary = $"Published {accepted.Count} private guide(s); {rejected} rejected.";
            _channel.SendPacket(new ClientGuidePushResultPacket(accepted.ToArray(), rejected, summary), player);
            if (firstClaimDenied != null) SendClaimDenied(player, firstClaimDenied);
        }

        // ==========================================================================================
        //  Creation + drafts
        // ==========================================================================================

        private void OnCreateRequest(IServerPlayer fromPlayer, GuideCreateRequestPacket p)
        {
            if (DeniedByPrivilege(fromPlayer))
            {
                SendPlacementRejected(fromPlayer, GuideOpStatus.InvalidArgument);
                return;
            }
            if (p?.Start == null || p.End == null || p.Settings == null)
            {
                SendPlacementRejected(fromPlayer, GuideOpStatus.InvalidArgument);
                return;
            }

            if (_playersWithPendingImmenseCreate.Contains(fromPlayer.PlayerUID))
            {
                fromPlayer.SendIngameError("layout-validationpending",
                    "Your previous immense guide is still being validated.");
                SendPlacementRejected(fromPlayer, GuideOpStatus.InvalidArgument);
                return;
            }

            // F5 chalk gate (NO LOCKOUT rule): a kit at 0 chalk blocks NEW placements only — every other
            // operation (edit, dispel, undo) has no gate. The client pre-checks at draft start; this is the
            // authoritative backstop.
            if (ChalkApplies(fromPlayer, out ItemSlot kitSlot)
                && Items.ItemGuideTool.GetChalk(kitSlot.Itemstack) <= 0)
            {
                fromPlayer.SendIngameError("layout-outofchalk",
                    "Out of chalk. Refill the Chalking Kit with Chalking Powder (hold it and right-click).");
                SendPlacementRejected(fromPlayer, GuideOpStatus.InvalidArgument);
                return;
            }

            Vec3d start = p.Start.ToVec3d();
            Vec3d end = p.End.ToVec3d();

            // RANGE CHECK, before anything scans these (GOTCHAS G31). Every shape's scan guard bounds the
            // shape's SIZE and never its POSITION, so a one-block box at a crafted coordinate passes the
            // guard and enters a lattice loop that overflows int and never terminates — on the SERVER TICK
            // THREAD. Vec3Dto is three raw doubles off the wire and was compared to nothing at all.
            if (!GuideBounds.IsUsable(start) || !GuideBounds.IsUsable(end)
                || !GuideBounds.IsUsable(p.Apex?.ToVec3d()) || !GuideBounds.IsUsable(p.Rim?.ToVec3d()))
            {
                RejectOutOfWorld(fromPlayer);
                SendPlacementRejected(fromPlayer, GuideOpStatus.InvalidArgument);
                return;
            }

            GuideRenderSettings settings = p.Settings.ToRenderSettings();

            var shapeType = (GuideShapeType)p.ShapeType;
            var constraint = (ShapeConstraint)p.Constraint;
            var planeAxis = (PlaneAxis)p.ShapePlaneAxis;

            // 0.1.15: the Free-Shape's corner chain (validated: every entry present, count sane).
            List<Vec3d> chain = null;
            if (p.Chain != null && p.Chain.Length >= 2 && p.Chain.Length <= Shapes.FreeShape.MaxCorners)
            {
                chain = new List<Vec3d>(p.Chain.Length);
                foreach (Vec3Dto c in p.Chain)
                {
                    if (c == null) { chain = null; break; }
                    Vec3d corner = c.ToVec3d();
                    if (!GuideBounds.IsUsable(corner))
                    {
                        RejectOutOfWorld(fromPlayer);
                        SendPlacementRejected(fromPlayer, GuideOpStatus.InvalidArgument);
                        return;
                    }
                    chain.Add(corner);
                }
            }

            if (TryQueueImmenseCreate(
                fromPlayer, start, end, settings, shapeType, constraint, planeAxis,
                p.Apex?.ToVec3d(), p.Inverted, p.Sides, chain, p.Closed,
                p.Rim?.ToVec3d(), p.FlatSideAligned))
                return;

            GuideOperationResult result = _guides.CreateGuide(
                start, end, settings, shapeType, constraint, planeAxis,
                fromPlayer.PlayerUID, PlayerDisplayName(fromPlayer),
                p.Apex?.ToVec3d(), p.Inverted, p.Sides, chain, p.Closed, p.Rim?.ToVec3d(),
                p.FlatSideAligned);
            switch (result.Status)
            {
                case GuideOpStatus.Success:
                    _undo.Record(fromPlayer.PlayerUID, new CreateGuideCommand(result.Guide));
                    _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
                    // The placement is done — drop this player's draft anchor for everyone else.
                    if (_draftAnchors.Remove(fromPlayer.PlayerUID))
                        _channel.BroadcastPacket(new DraftAnchorRemovePacket(fromPlayer.PlayerUID), fromPlayer);
                    // Placement feedback for everyone in range: the chalk-line snap + dust along the whole
                    // guide (2D) or its base ring (3D volumes — a full-shell puff would flood the particle
                    // system on big spheres).
                    ChalkEffects.PlacementEffects(_sapi.World, result.Guide);
                    // F5: a COMPLETED placement is the one thing that spends chalk (2D −1, volume −2).
                    // Deliberately outside the undo system: undoing a guide does not refund its chalk, and
                    // redo re-creates via the command path so it never double-charges. Clamped at 0; the
                    // kit itself can never break (see ItemGuideTool.ConsumeChalk).
                    if (ChalkApplies(fromPlayer, out ItemSlot chargeSlot))
                        Items.ItemGuideTool.ConsumeChalk(chargeSlot,
                            GuideShapeTypes.IsVolume(shapeType)
                                ? Items.ItemGuideTool.ChalkCostVolume
                                : Items.ItemGuideTool.ChalkCostFlat);
                    break;

                case GuideOpStatus.RejectedOverCap:
                    // Small guides and already-refined drafts pre-check the per-guide cap. An immense public
                    // shell may deliberately defer that expensive final count here. The HUD cap-flash was
                    // tied to a guide id that doesn't exist yet, so it never showed — a CLEAR ingame error
                    // is the primary feedback (v0.1.26 fix: previously this rejection was effectively
                    // silent). Leave the draft so the player can shrink it or dispel some guides and retry.
                    SendCapRefusal(
                        fromPlayer, result.Guide?.Id ?? Guid.Empty, result.VoxelCount, result.CapLimit,
                        throttle: false);
                    break;

                case GuideOpStatus.RejectedOverGuideCount:
                    // Per-player or world-wide guide-count cap (server config). Nothing was built; leave the
                    // draft so the player can dispel an old guide and complete this one afterwards.
                    SendGuideCountRefusal(fromPlayer, result);
                    break;

                case GuideOpStatus.RejectedClaimAccess:
                    SendClaimDenied(fromPlayer, result.DeniedPosition);
                    break;

                case GuideOpStatus.RejectedEmpty:
                    fromPlayer.SendIngameError("layout-emptyguide", EmptyGuideText());
                    break;

                // InvalidArgument: malformed input — ignore.
            }
            if (!result.IsSuccess) SendPlacementRejected(fromPlayer, result.Status);
        }

        private bool TryQueueImmenseCreate(
            IServerPlayer player, Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType, ShapeConstraint constraint, PlaneAxis planeAxis,
            Vec3d thirdPoint, bool inverted, int sides, IReadOnlyList<Vec3d> chain,
            bool closed, Vec3d fourthPoint, bool flatSideAligned)
        {
            if (!GuideShapeTypes.IsVolume(shapeType) || settings.Wireframe) return false;

            if (!_guides.TryPrepareGuideCreation(
                start, end, settings, shapeType, constraint, planeAxis,
                player.PlayerUID, PlayerDisplayName(player), thirdPoint, inverted, sides,
                chain, closed, fourthPoint, flatSideAligned,
                out PreparedGuideCreation prepared, out _))
                return false;

            ResolveCreateCountLimit(player, out int countLimit, out int countLimitCap);
            int probeLimit = Math.Min(StreamedCreateVoxelThreshold, countLimit);
            int probeCount = prepared.CountUpTo(probeLimit);

            // At or below the threshold, preserve the established immediate path. If the applicable cap is
            // itself no larger than the threshold, that path also rejects cheaply without starting a job.
            if (probeCount <= probeLimit || countLimit <= StreamedCreateVoxelThreshold) return false;

            if (_playersWithPendingImmenseCreate.Count >= MaxQueuedImmenseCreates)
            {
                player.SendIngameError("layout-validationbusy",
                    "The immense-guide validator is busy. Please try placing this guide again shortly.");
                SendPlacementRejected(player, GuideOpStatus.InvalidArgument);
                return true;
            }

            var pending = new PendingImmenseCreate
            {
                PlayerUid = player.PlayerUID,
                Prepared = prepared,
                CountLimit = countLimit,
                CountLimitCap = countLimitCap
            };
            _playersWithPendingImmenseCreate.Add(player.PlayerUID);
            _immenseCreateQueue.Enqueue(pending);
            StartNextImmenseCreate();
            return true;
        }

        private void ResolveCreateCountLimit(
            IServerPlayer player, out int countLimit, out int countLimitCap)
        {
            countLimit = GuideManager.HardVoxelCeiling;
            countLimitCap = GuideManager.HardVoxelCeiling;

            int perGuideCap = _policies.EffectiveVoxelCap(
                player?.PlayerUID, _guides.PerGuideVoxelCap);
            if (perGuideCap > 0 && perGuideCap < countLimit)
            {
                countLimit = perGuideCap;
                countLimitCap = perGuideCap;
            }

            int playerTotalCap = player == null
                ? 0
                : _guides.EffectivePerPlayerTotalVoxelCap(player.PlayerUID);
            if (playerTotalCap > 0)
            {
                long playerAvailable =
                    (long)playerTotalCap - _guides.VoxelCountBy(player.PlayerUID);
                int playerLimit = playerAvailable <= 0 ? 0
                    : playerAvailable >= int.MaxValue ? int.MaxValue
                    : (int)playerAvailable;
                if (playerLimit < countLimit)
                {
                    countLimit = playerLimit;
                    countLimitCap = playerTotalCap;
                }
            }

            if (_guides.TotalVoxelCap <= 0) return;
            long available = (long)_guides.TotalVoxelCap - _guides.TotalVoxelCount;
            int totalLimit = available <= 0 ? 0
                : available >= int.MaxValue ? int.MaxValue
                : (int)available;
            if (totalLimit < countLimit)
            {
                countLimit = totalLimit;
                countLimitCap = _guides.TotalVoxelCap;
            }
        }

        private void StartNextImmenseCreate()
        {
            if (_disposed || _activeImmenseCreate != null || _activeImmenseSculpt != null) return;
            while (_immenseCreateQueue.Count > 0)
            {
                PendingImmenseCreate next = _immenseCreateQueue.Dequeue();
                if (next.Cancelled)
                {
                    _playersWithPendingImmenseCreate.Remove(next.PlayerUid);
                    continue;
                }

                _activeImmenseCreate = next;
                next.GeometryTask = Task.Factory.StartNew(
                    () => CalculateImmenseCreateGeometry(next),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                return;
            }
        }

        private static ImmenseCreateGeometry CalculateImmenseCreateGeometry(
            PendingImmenseCreate pending)
        {
            Thread thread = Thread.CurrentThread;
            ThreadPriority originalPriority = ThreadPriority.Normal;
            bool priorityChanged = false;
            try
            {
                try
                {
                    originalPriority = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                }
                catch (Exception) { }

                // A null return means ABANDONED. The tick thread checks Cancelled before it ever looks at
                // the result, so nothing consumes a partial answer — this just gives up the lane sooner.
                if (pending.Cancelled) return null;

                int count = pending.Prepared.CountUpTo(
                    pending.CountLimit, () => pending.Cancelled);
                if (pending.Cancelled) return null;

                if (count > pending.CountLimit)
                    return new ImmenseCreateGeometry
                    {
                        VoxelCount = count,
                        ExceededCap = pending.CountLimitCap
                    };

                List<BlockPos> footprint = GuideClaimAccessValidator.BuildFootprint(
                    pending.Prepared.Guide, pending.Prepared.Shape, () => pending.Cancelled);
                if (footprint == null) return null;      // cancelled mid-collapse

                return new ImmenseCreateGeometry
                {
                    VoxelCount = count,
                    Footprint = footprint,
                    ClaimBounds = ClaimFootprintBounds.FromFootprint(footprint)
                };
            }
            catch (OperationCanceledException) when (pending.Cancelled)
            {
                return null;
            }
            finally
            {
                if (priorityChanged)
                {
                    try { thread.Priority = originalPriority; }
                    catch (Exception) { }
                }
            }
        }

        private void OnImmenseCreateTick(float deltaTime)
        {
            if (_disposed) return;
            StartNextImmenseCreate();
            PendingImmenseCreate pending = _activeImmenseCreate;
            if (pending?.GeometryTask == null || !pending.GeometryTask.IsCompleted) return;

            if (pending.Cancelled)
            {
                CompleteActiveImmenseCreate();
                return;
            }

            if (pending.Geometry == null)
            {
                try
                {
                    pending.Geometry = pending.GeometryTask.GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    _sapi.Logger.Error(
                        "[Layout] Immense placement geometry failed for {0}: {1}",
                        pending.PlayerUid, e.Message);
                    IServerPlayer failedPlayer = FindOnlinePlayer(pending.PlayerUid);
                    if (failedPlayer != null)
                    {
                        failedPlayer.SendIngameError("layout-validationfailed",
                            "That immense guide could not be validated. Please try again.");
                        SendPlacementRejected(failedPlayer, GuideOpStatus.InvalidArgument);
                    }
                    CompleteActiveImmenseCreate();
                    return;
                }

                // The worker returns null when it noticed the cancel and gave up. Cancelled is checked
                // above, so this should be unreachable — but leaving Geometry null would re-enter this
                // block every tick forever, and a stuck validator lane is the one failure worth a belt.
                if (pending.Geometry == null)
                {
                    CompleteActiveImmenseCreate();
                    return;
                }
            }

            IServerPlayer player = FindOnlinePlayer(pending.PlayerUid);
            if (player == null)
            {
                CompleteActiveImmenseCreate();
                return;
            }

            if (pending.Geometry.ExceededCap > 0)
            {
                FinishImmenseCreate(player, GuideOperationResult.OverCap(
                    pending.Prepared.Guide, pending.Geometry.VoxelCount,
                    pending.Geometry.ExceededCap));
                CompleteActiveImmenseCreate();
                return;
            }

            List<BlockPos> footprint = pending.Geometry.Footprint ?? new List<BlockPos>();
            if (player.HasPrivilege(Privilege.controlserver))
            {
                // Admins bypass claims entirely, so there is nothing to keep consistent.
                pending.ClaimIndex = footprint.Count;
            }
            else
            {
                if (pending.ClaimSnapshot == null)
                {
                    pending.ClaimSnapshot = CaptureClaimAccessSnapshot(
                        player, pending.Geometry.ClaimBounds);
                }
                else
                {
                    ClaimRevalidationResult beforeSlice = RevalidateClaimAccess(
                        player, pending.Geometry.ClaimBounds, ref pending.ClaimSnapshot,
                        ref pending.ClaimRestarts, ref pending.ClaimIndex);
                    if (beforeSlice == ClaimRevalidationResult.Restarted) return;
                    if (beforeSlice == ClaimRevalidationResult.TooMuchChurn)
                    {
                        RejectImmenseCreateForClaimChurn(player);
                        return;
                    }
                }

                var budget = Stopwatch.StartNew();
                int checks = 0;
                while (pending.ClaimIndex < footprint.Count
                    && checks < MaxClaimChecksPerTick
                    && budget.Elapsed.TotalMilliseconds < ClaimCheckBudgetMilliseconds)
                {
                    BlockPos block = footprint[pending.ClaimIndex++];
                    checks++;
                    if (_sapi.World.Claims.TestAccess(
                        player, block, EnumBlockAccessFlags.BuildOrBreak)
                        != EnumWorldAccessResponse.Granted)
                    {
                        FinishImmenseCreate(player, GuideOperationResult.ClaimDenied(
                            pending.Prepared.Guide, block));
                        CompleteActiveImmenseCreate();
                        return;
                    }
                }

                // Walk finished — but did the claims change while we were walking? If so, rewind and do it
                // again against the world as it is now, rather than committing on a stale answer.
                if (pending.ClaimIndex >= footprint.Count)
                {
                    ClaimRevalidationResult freshness = RevalidateClaimAccess(
                        player, pending.Geometry.ClaimBounds, ref pending.ClaimSnapshot,
                        ref pending.ClaimRestarts, ref pending.ClaimIndex);
                    if (freshness == ClaimRevalidationResult.Restarted) return;
                    if (freshness == ClaimRevalidationResult.TooMuchChurn)
                    {
                        RejectImmenseCreateForClaimChurn(player);
                        return;
                    }
                }
            }

            if (pending.ClaimIndex < footprint.Count) return;

            if (DeniedByPrivilege(player))
            {
                SendPlacementRejected(player, GuideOpStatus.InvalidArgument);
                CompleteActiveImmenseCreate();
                return;
            }
            if (ChalkApplies(player, out ItemSlot kitSlot)
                && Items.ItemGuideTool.GetChalk(kitSlot.Itemstack) <= 0)
            {
                player.SendIngameError("layout-outofchalk",
                    "Out of chalk. Refill the Chalking Kit with Chalking Powder (hold it and right-click).");
                SendPlacementRejected(player, GuideOpStatus.InvalidArgument);
                CompleteActiveImmenseCreate();
                return;
            }

            GuideOperationResult committed = default;
            WithPlayerVoxelCap(player, () =>
                committed = _guides.CommitPreparedGuideCreation(
                    pending.Prepared, pending.Geometry.VoxelCount, validateAccess: false));
            FinishImmenseCreate(player, committed);
            CompleteActiveImmenseCreate();
        }

        private void FinishImmenseCreate(IServerPlayer player, GuideOperationResult result)
        {
            if (result.IsSuccess)
            {
                _undo.Record(player.PlayerUID, new CreateGuideCommand(result.Guide));
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
                if (_draftAnchors.Remove(player.PlayerUID))
                    _channel.BroadcastPacket(new DraftAnchorRemovePacket(player.PlayerUID), player);
                // Immense placements finish at a client-specific upload cadence. The placing client's
                // renderer emits feedback only after authority arrives and the clean shell is visible.
                if (ChalkApplies(player, out ItemSlot chargeSlot))
                    Items.ItemGuideTool.ConsumeChalk(chargeSlot,
                        GuideShapeTypes.IsVolume(result.Guide.ShapeType)
                            ? Items.ItemGuideTool.ChalkCostVolume
                            : Items.ItemGuideTool.ChalkCostFlat);
                return;
            }

            switch (result.Status)
            {
                case GuideOpStatus.RejectedOverCap:
                    SendCapRefusal(
                        player, result.Guide?.Id ?? Guid.Empty, result.VoxelCount, result.CapLimit,
                        throttle: false);
                    break;

                case GuideOpStatus.RejectedOverGuideCount:
                    SendGuideCountRefusal(player, result);
                    break;

                case GuideOpStatus.RejectedClaimAccess:
                    SendClaimDenied(player, result.DeniedPosition);
                    break;

                case GuideOpStatus.RejectedEmpty:
                    player.SendIngameError("layout-emptyguide", EmptyGuideText());
                    break;

                default:
                    player.SendIngameError("layout-validationfailed",
                        "That guide could not be placed. Please try again.");
                    break;
            }
            SendPlacementRejected(player, result.Status);
        }

        private void CompleteActiveImmenseCreate()
        {
            PendingImmenseCreate completed = _activeImmenseCreate;
            _activeImmenseCreate = null;
            // CHECK IDENTITY BEFORE REMOVING BY KEY (GOTCHAS G29). This set is the "one immense create per
            // player" gate, keyed by uid alone — so a cancelled job finishing late would clear the marker
            // belonging to that same player's NEWER job and let a third one in behind it. Only drop the
            // marker when this player really has nothing else waiting.
            if (completed != null && !HasQueuedImmenseCreate(completed.PlayerUid))
                _playersWithPendingImmenseCreate.Remove(completed.PlayerUid);
            StartNextImmenseCreate();
            StartNextImmenseSculpt();
        }

        private bool HasQueuedImmenseCreate(string playerUid)
        {
            foreach (PendingImmenseCreate queued in _immenseCreateQueue)
                if (!queued.Cancelled && queued.PlayerUid == playerUid) return true;
            return false;
        }

        private void CancelPendingImmenseCreate(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid)) return;
            if (_activeImmenseCreate?.PlayerUid == playerUid)
            {
                _activeImmenseCreate.Cancelled = true;
                return;
            }

            int queued = _immenseCreateQueue.Count;
            bool removed = false;
            for (int i = 0; i < queued; i++)
            {
                PendingImmenseCreate pending = _immenseCreateQueue.Dequeue();
                if (pending.PlayerUid == playerUid) removed = true;
                else _immenseCreateQueue.Enqueue(pending);
            }
            if (removed) _playersWithPendingImmenseCreate.Remove(playerUid);
        }

        private void CancelPendingImmenseSculpt(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid)) return;
            if (_pendingImmenseSculpts.TryGetValue(
                playerUid, out PendingImmenseSculpt pending))
                pending.Cancelled = true;
            _pendingImmenseSculpts.Remove(playerUid);
        }

        private IServerPlayer FindOnlinePlayer(string playerUid) =>
            _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
                .FirstOrDefault(player => player.PlayerUID == playerUid);

        // ==========================================================================================
        //  Claim-validation consistency (A14.11 / Session 41 P2 follow-on)
        // ==========================================================================================
        //
        // An immense footprint is validated a slice at a time — ≤128 blocks or ~1 ms per 20 ms tick — so a
        // big guide is checked over many ticks, and the commit that follows runs with claim validation
        // turned OFF because the walk above already did it. A claim CREATED over a block that was checked
        // early therefore never gets re-tested, and the guide settles across land that is now protected.
        //
        // The engine exposes no claim revision. ClaimAccessSnapshot therefore captures claim intersections
        // inside the exact footprint bounds plus relevant player state used by BuildOrBreak. Distant claims
        // are scanned only far enough to rule them out; they are never permission-tested or compared. This
        // remains a staleness detector around the exact TestAccess walk, never a substitute for that walk:
        // the final mod-extensible DeniedByMod event deliberately remains unknowable from claim data (G40).
        // Compare before every later slice as well as after the final one, so change-and-revert churn cannot
        // hide a slice that ran under an intermediate claim state.
        private const int MaxClaimRevalidations = 3;

        private enum ClaimRevalidationResult
        {
            Stable,
            Restarted,
            TooMuchChurn
        }

        private ClaimAccessSnapshot CaptureClaimAccessSnapshot(
            IServerPlayer player, ClaimFootprintBounds bounds) =>
            ClaimAccessSnapshot.Capture(_sapi.World?.Claims?.All, player, bounds);

        private void RejectImmenseCreateForClaimChurn(IServerPlayer player)
        {
            player.SendIngameError("layout-validationbusy",
                "Land permissions kept changing while this immense guide was checked. "
                + "Please try placing it again.");
            SendPlacementRejected(player, GuideOpStatus.InvalidArgument);
            CompleteActiveImmenseCreate();
        }

        private void RejectImmenseSculptForClaimChurn(
            IServerPlayer player, PendingImmenseSculpt pending)
        {
            player.SendIngameError("layout-validationbusy",
                "Land permissions kept changing while this immense reshape was checked. "
                + "Please try the reshape again.");
            FinishRejectedImmenseSculpt(player, pending);
            CompleteActiveImmenseSculpt();
        }

        // A changing claim/access environment can neither trap a job forever nor be treated as validated.
        // The first three changes rewind the exact walk; a fourth rejects and asks the player to retry.
        private ClaimRevalidationResult RevalidateClaimAccess(
            IServerPlayer player, ClaimFootprintBounds bounds,
            ref ClaimAccessSnapshot snapshot,
            ref int restarts, ref int claimIndex)
        {
            ClaimAccessSnapshot now = CaptureClaimAccessSnapshot(player, bounds);
            if (snapshot == null)
            {
                snapshot = now;
                claimIndex = 0;
                return ClaimRevalidationResult.Restarted;
            }
            if (snapshot.Equals(now)) return ClaimRevalidationResult.Stable;
            if (restarts >= MaxClaimRevalidations) return ClaimRevalidationResult.TooMuchChurn;

            restarts++;
            snapshot = now;
            claimIndex = 0;          // walk the whole footprint again, against the claims as they are now
            return ClaimRevalidationResult.Restarted;
        }

        private void SendPlacementRejected(IServerPlayer player, GuideOpStatus status)
        {
            if (player != null)
                _channel.SendPacket(new GuidePlacementRejectedPacket((int)status), player);
        }

        /// <summary>
        /// True when chalk durability applies to this player's placement right now: the feature is enabled
        /// (server config), the player is in a consuming game mode (not Creative/Spectator), and the ACTIVE
        /// hotbar item is the real Chalking Kit; that kit slot comes back for the gate/charge. Client-only
        /// and private placements never reach this server path at all, so they are chalk-free by
        /// construction — the F4 no-op the plan records.
        /// </summary>
        /// <summary>
        /// F5 (protocol 4): a client reports a completed PRIVATE placement so its chalk can be charged —
        /// the server owns the inventory but cannot see private guides, so this self-report is the only
        /// charge path for them. Everything checkable is validated (feature enabled, game mode, the real
        /// kit actually held) and the cost is clamped to the legal 1–2 range; a dishonest client could at
        /// most skip its own charge, which no server-side check could prevent anyway.
        /// </summary>
        private void OnChalkCharge(IServerPlayer fromPlayer, ChalkChargePacket p)
        {
            if (p == null || !_allowClientOnlyMode) return;
            if (!ChalkApplies(fromPlayer, out ItemSlot kitSlot)) return;

            Items.ItemGuideTool.ConsumeChalk(kitSlot, Math.Max(1, Math.Min(2, p.Cost)));
        }

        /// <summary>
        /// v0.2.22. Records a player's own refill-channel preferences, reported by their client on join.
        /// A preference, not a permission — the server mirrors the player's choice so that the server-side
        /// half of the held-interact does not refill for someone who switched the shortcut off.
        /// </summary>
        private void OnChalkRefillPrefs(IServerPlayer fromPlayer, ChalkRefillPrefsPacket p)
        {
            if (fromPlayer == null || p == null) return;
            if (p.AllowHotbarRefill) _hotbarRefillOptIn.Add(fromPlayer.PlayerUID);
            else _hotbarRefillOptIn.Remove(fromPlayer.PlayerUID);
        }

        /// <summary>
        /// Whether this player opted into the hotbar refill shortcut. Consulted by
        /// <c>ItemChalkingPowder</c> on the server side only; the client reads its own config directly.
        /// </summary>
        public bool HotbarRefillOptIn(string playerUid) =>
            playerUid != null && _hotbarRefillOptIn.Contains(playerUid);

        /// <summary>
        /// F5: refill the kit in a named inventory slot from the powder on the player's cursor.
        /// Re-validates everything the client claimed — the slot really holds a non-full kit, the cursor
        /// really holds powder — before consuming one powder and adding its chalk. If the client's default
        /// slot-swap actually ran first (mouse hook ordering), the cursor will hold the KIT here, this
        /// validation fails, and nothing happens: the worst case degrades to a harmless swap, never a
        /// corrupt inventory.
        /// </summary>
        /// <remarks>
        /// v0.2.22: the refill channel is now a CLIENT preference, so there is deliberately no permission
        /// check here — gating server-side would refuse the refill the player's own client just authorised.
        /// Everything below is INTEGRITY validation (does this request name a real kit and a real powder
        /// stack), which must stay: it is what keeps a lost mouse-hook race from corrupting an inventory.
        /// </remarks>
        private void OnInventoryChalkRefill(IServerPlayer fromPlayer, ChalkInventoryRefillPacket p)
        {
            if (p == null || string.IsNullOrEmpty(p.InventoryId)) return;

            IInventory inv = fromPlayer.InventoryManager?.GetInventory(p.InventoryId);
            if (inv == null || p.SlotId < 0 || p.SlotId >= inv.Count) return;

            ItemSlot kitSlot = inv[p.SlotId];
            if (!(kitSlot?.Itemstack?.Collectible is Items.ItemGuideTool)) return;

            ItemSlot cursor = fromPlayer.InventoryManager?.MouseItemSlot;
            if (!(cursor?.Itemstack?.Collectible is Items.ItemChalkingPowder) || cursor.Itemstack.StackSize <= 0)
                return;

            if (!Items.ItemGuideTool.TryAddChalk(kitSlot, Items.ItemChalkingPowder.ChalkPerPowder)) return; // full

            cursor.TakeOut(1);
            cursor.MarkDirty();
            fromPlayer.Entity.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"),
                fromPlayer.Entity, null, true, 16f, 0.6f);
        }

        private bool ChalkApplies(IServerPlayer player, out ItemSlot kitSlot)
        {
            kitSlot = null;
            if (!_chalkDurabilityEnabled) return false;

            EnumGameMode mode = player.WorldData?.CurrentGameMode ?? EnumGameMode.Survival;
            if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) return false;

            ItemSlot slot = player.InventoryManager?.ActiveHotbarSlot;
            if (!(slot?.Itemstack?.Collectible is Items.ItemGuideTool)) return false;

            kitSlot = slot;
            return true;
        }

        private void OnDraftStart(IServerPlayer fromPlayer, DraftStartPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            if (p?.Start == null) return;
            // The anchor is broadcast to every other player and fed to the claim validator; keep the same
            // range check on it as on the geometry it will become.
            if (!GuideBounds.IsUsable(p.Start.ToVec3d())) { RejectOutOfWorld(fromPlayer); return; }
            if (!fromPlayer.HasPrivilege(Privilege.controlserver))
            {
                GuideMutationAccessResult access =
                    new GuideClaimAccessValidator(_sapi, fromPlayer).ValidateAnchor(p.Start.ToVec3d());
                if (!access.Allowed)
                {
                    SendClaimDenied(fromPlayer, access.DeniedPosition);
                    return;
                }
            }
            string uid = fromPlayer.PlayerUID;

            _draftAnchors[uid] = p.Start.ToVec3d();
            // Other players render the green anchor; the placer already sees its own.
            _channel.BroadcastPacket(new DraftAnchorBroadcastPacket(uid, p.Start), fromPlayer);
        }

        private void OnDraftCancel(IServerPlayer fromPlayer, DraftCancelPacket p)
        {
            string uid = fromPlayer.PlayerUID;
            if (_draftAnchors.Remove(uid))
                _channel.BroadcastPacket(new DraftAnchorRemovePacket(uid), fromPlayer);
        }

        // ==========================================================================================
        //  Edit locks
        // ==========================================================================================

        private void OnGrab(IServerPlayer fromPlayer, GuideGrabPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.HasGuide(id))
            {
                // Stale target — make sure the client drops it.
                _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);
                return;
            }

            LockAcquireOutcome outcome = _locks.TryAcquireLock(id, fromPlayer.PlayerUID);
            if (outcome.CanEdit && _guides.TryGetGuide(id, out GuideData grabbedGuide))
            {
                ReleaseSupersededLocks(fromPlayer, id);
                DragSession session = DragFor(fromPlayer.PlayerUID, id);
                if (session.OriginPoints == null)
                {
                    session.OriginConstraint = grabbedGuide.Constraint;
                    session.OriginPoints = ClonePoints(grabbedGuide.ControlPoints);
                    session.OriginVoxelCount = grabbedGuide.CachedVoxelCount;
                }
            }
            // Everyone learns the holder; the requester learns whether the grab was granted or denied.
            _channel.BroadcastPacket(new GuideLockStatePacket(id, outcome.HolderUid));
        }

        private void OnRelease(IServerPlayer fromPlayer, GuideReleasePacket p)
        {
            // Releasing/cancelling an in-flight gesture is cleanup. It must remain possible if a claim was
            // created during the drag, or the player would be unable to restore/remove the transient edit.
            if (p != null
                && _pendingImmenseSculpts.TryGetValue(
                    fromPlayer.PlayerUID, out PendingImmenseSculpt pending)
                && pending.GuideId == p.GuideId())
            {
                // The client is already free to continue playing. Retain the authority lock only until the
                // isolated candidate finishes its low-priority count and bounded claim validation.
                pending.ReleaseRequested = true;
                return;
            }
            using (_guides.UseMutationAccessValidator(null))
                OnReleaseWithoutClaimChecks(fromPlayer, p);
        }

        private void OnReleaseWithoutClaimChecks(IServerPlayer fromPlayer, GuideReleasePacket p)
        {
            Guid id = p.GuideId();
            string uid = fromPlayer.PlayerUID;
            bool visibleChange = false;

            // Commit the drag as a single undo entry: origin -> final for each point that actually moved.
            if (_drags.TryGetValue(uid, out DragSession session) && session.GuideId == id)
            {
                if (_guides.TryGetGuide(id, out GuideData g))
                {
                    // Session-8: an INSERT-born grab released with no net movement removes its point again —
                    // the player grabbed the body and let go without reshaping anything, so leaving a control
                    // point behind would silently litter the guide with spline constraints (finding 2's
                    // accidental case). "No net movement" = never moved at all (no origin captured) or moved
                    // and returned to the insert position.
                    bool removedInsert = false;
                    if (session.InsertedIndex >= 0 &&
                        session.InsertedIndex < g.ControlPoints.Count)
                    {
                        bool moved = session.Origins.TryGetValue(session.InsertedIndex, out Vec3d insOrigin)
                            && !SamePosition(g.ControlPoints[session.InsertedIndex].WorldPosition, insOrigin);
                        if (!moved)
                        {
                            GuideOperationResult rm = _guides.RemoveControlPoint(id, session.InsertedIndex);
                            removedInsert = rm.Status == GuideOpStatus.Success;
                            if (removedInsert && _guides.TryGetGuide(id, out GuideData after))
                                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(after)));
                            // The InsertControlPointCommand recorded at insert time goes stale and
                            // self-cleans via the validate-then-apply discard, as with cancel.
                        }
                    }

                    // A grab session only ever tracks the one grabbed point, so if that point was just
                    // removed there is nothing left to commit (and its index is void anyway).
                    if (!removedInsert)
                    {
                        bool promotedMarker = HasPromotedMarker(
                            session.OriginPoints, g.ControlPoints);
                        visibleChange = promotedMarker || session.OriginConstraint != g.Constraint;
                        if (promotedMarker || session.CommitAsWholeShape)
                        {
                            _undo.Record(uid, new SpringBackCommand(
                                id, session.OriginConstraint, session.OriginPoints,
                                g.Constraint, g.ControlPoints));
                        }
                        else foreach (var pair in session.Origins)
                        {
                            int idx = pair.Key;
                            Vec3d origin = pair.Value;
                            if (idx < 0 || idx >= g.ControlPoints.Count) continue;
                            Vec3d current = g.ControlPoints[idx].WorldPosition;
                            if (!SamePosition(current, origin))
                            {
                                visibleChange = true;
                                _undo.Record(uid, new MoveControlPointCommand(id, idx, origin, current));
                            }
                        }
                    }
                    visibleChange |= session.OriginConstraint != g.Constraint;
                }
                _drags.Remove(uid);
            }

            if (visibleChange && _guides.TryGetGuide(id, out GuideData changedGuide))
                StampLastSculptor(fromPlayer, changedGuide);

            if (_locks.ReleaseLock(id, uid))
                _channel.BroadcastPacket(new GuideLockStatePacket(id, null));

            // Dragging is optimistically previewed in the mover's client mirror. Restate the complete
            // authoritative guide after release so a rejected or not-yet-sent final preview cannot remain
            // locally oversized (and therefore invisible while its control points are still targetable).
            ResyncOrDrop(fromPlayer, id);
        }

        // Session-8 right-click-cancel: the anti-release. Where OnRelease COMMITS the drag (one undo entry),
        // this discards it — dragged points snap back to their pre-drag origins, or, if the grab began as a
        // body insert, the inserted point is removed outright (insert + grab were one gesture). No undo
        // command is recorded either way. The InsertControlPointCommand recorded at insert time goes stale
        // on removal and self-cleans via the validate-then-apply discard, per the settled undo model.
        private void OnCancelGrab(IServerPlayer fromPlayer, GuideCancelGrabPacket p)
        {
            if (p != null
                && _pendingImmenseSculpts.TryGetValue(
                    fromPlayer.PlayerUID, out PendingImmenseSculpt pending)
                && pending.GuideId == p.GuideId())
            {
                pending.Cancelled = true;
                _pendingImmenseSculpts.Remove(fromPlayer.PlayerUID);
                _drags.Remove(fromPlayer.PlayerUID);
                if (_locks.ReleaseLock(pending.GuideId, fromPlayer.PlayerUID))
                    _channel.BroadcastPacket(new GuideLockStatePacket(pending.GuideId, null));
                ResyncOrDrop(fromPlayer, pending.GuideId);
                return;
            }
            CancelGrabSession(fromPlayer, p.GuideId());
        }

        private void CancelGrabSession(IServerPlayer fromPlayer, Guid id)
        {
            using (_guides.UseMutationAccessValidator(null))
                CancelGrabSessionWithoutClaimChecks(fromPlayer, id);
        }

        private void CancelGrabSessionWithoutClaimChecks(IServerPlayer fromPlayer, Guid id)
        {
            string uid = fromPlayer.PlayerUID;

            // Only the lock holder has a grab to cancel; anyone else just gets the truth restated.
            if (!_locks.IsHeldBy(id, uid))
            {
                ResyncOrDrop(fromPlayer, id);
                return;
            }

            bool mutated = false;
            if (_drags.TryGetValue(uid, out DragSession session) && session.GuideId == id
                && _guides.TryGetGuide(id, out GuideData g))
            {
                if (session.OriginPoints != null)
                {
                    // Restore the gesture atomically: every soft-flow point, the grabbed point, phantom
                    // geometry, and any constraint broken by the first move all return together.
                    mutated = _guides.RestoreConstraint(
                        id, session.OriginConstraint, session.OriginPoints,
                        session.OriginVoxelCount).Status == GuideOpStatus.Success;
                }
                else if (session.InsertedIndex >= 0)
                {
                    // Fresh body insert: cancel removes the whole gesture. RemoveControlPoint validates the
                    // index itself (the guide may have changed shape under an admin op while we dragged).
                    GuideOperationResult rm = _guides.RemoveControlPoint(id, session.InsertedIndex);
                    mutated = rm.Status == GuideOpStatus.Success;
                }
                else
                {
                    // Pre-existing point(s): put back every origin the drag captured.
                    foreach (var pair in session.Origins)
                    {
                        int idx = pair.Key;
                        if (idx < 0 || idx >= g.ControlPoints.Count) continue;
                        if (SamePosition(g.ControlPoints[idx].WorldPosition, pair.Value)) continue;
                        GuideOperationResult mv = _guides.UpdateControlPoints(
                            id, new[] { new ControlPointEdit(idx, pair.Value) });
                        mutated |= mv.Status == GuideOpStatus.Success;
                    }
                }
                _drags.Remove(uid);
            }

            // One generic full-state broadcast covers both cases (same rule the undo path uses).
            if (mutated && _guides.TryGetGuide(id, out GuideData after))
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(after)));

            if (_locks.ReleaseLock(id, uid))
                _channel.BroadcastPacket(new GuideLockStatePacket(id, null));

            if (!mutated) ResyncOrDrop(fromPlayer, id);
        }

        // ==========================================================================================
        //  Control-point edits
        // ==========================================================================================

        private void OnUpdate(IServerPlayer fromPlayer, GuideUpdatePacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            string uid = fromPlayer.PlayerUID;

            // Only the lock holder may move points. Anyone else gets corrected back to authoritative state.
            if (!_locks.IsHeldBy(id, uid))
            {
                ResyncOrDrop(fromPlayer, id);
                return;
            }
            if (!_guides.TryGetGuide(id, out GuideData g))
            {
                _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);
                return;
            }
            if (p.Edits == null || p.Edits.Length == 0) return;

            // BOUND THE BATCH. A guide cannot have more control points than it has control points, so an
            // edit array longer than the point list is nonsense by construction — but nothing checked, and
            // every entry costs a WouldBreakOnMove probe plus a soft-flow reflow before anything is
            // validated. The shipped client sends exactly one edit per drag update; this only ever fires on
            // a malformed or crafted packet (GOTCHAS G32).
            if (p.Edits.Length > g.ControlPoints.Count) return;

            DragSession session = DragFor(uid, id);
            IGuideShape shape = _guides.GetShape(id);
            bool immenseSculpt = g.CachedVoxelCount > StreamedCreateVoxelThreshold
                && GuideShapeTypes.IsVolume(g.ShapeType) && !g.IsWireframe;

            // ABSORB-OR-BREAK on move (Session 8): dragging a point the constraint cannot absorb — a
            // circle's minor handle — breaks it (one undoable command), and the move then applies to the
            // free parent. Feet/diameter anchors absorb, so they never reach this.
            bool broke = false;
            bool shouldBreak = false;
            if (g.Constraint != ShapeConstraint.None && shape != null)
            {
                for (int i = 0; i < p.Edits.Length && !shouldBreak; i++)
                {
                    if (!shape.WouldBreakOnMove(p.Edits[i].Index)) continue;
                    shouldBreak = true;
                    if (immenseSculpt) continue;
                    var breakCmd = new BreakConstraintCommand(id, g.Constraint, g.ControlPoints);
                    GuideOperationResult breakResult = _guides.BreakConstraint(id);
                    if (breakResult.Status == GuideOpStatus.Success)
                    {
                        _undo.Record(uid, breakCmd);
                        broke = true;
                    }
                    else
                    {
                        HandleNonSuccessToggle(fromPlayer, id, breakResult, g);
                        return;
                    }
                }
            }

            // Compose the full edit batch: the client's edit plus the soft-point reflow — on EVERY drag
            // of a free arch, the held point joins the baseline (anchors + locks + it) and every other
            // unlocked point flows, so nothing but locks can pin geometry. The mapping is captured ONCE
            // per drag from pre-move positions and reused for every update.
            var composed = new List<ControlPointEdit>(p.Edits.Length);
            var seenIndexes = new HashSet<int>();
            for (int i = 0; i < p.Edits.Length; i++)
            {
                int idx = p.Edits[i].Index;

                // ONE MOVE PER POINT PER PACKET. Repeating an index is contradictory — the later entry
                // silently wins — and each repeat re-runs a full soft-flow reflow over the whole point
                // list. Correct the sender rather than picking one of its two answers for it.
                if (!seenIndexes.Add(idx)) { SendResync(fromPlayer, g); return; }

                Vec3d pos = p.Edits[i].Position != null ? p.Edits[i].Position.ToVec3d() : new Vec3d();
                if (!GuideBounds.IsUsable(pos))
                {
                    // Resync as well as refuse: the mover previews its own drag optimistically, so
                    // returning in silence would leave a point sitting where the server never put it.
                    RejectOutOfWorld(fromPlayer);
                    SendResync(fromPlayer, g);
                    return;
                }
                composed.Add(new ControlPointEdit(idx, pos));

                // Session-9 revision: flow fires on EVERY drag of a free arch, not only structural drags —
                // the held point joins the baseline (Capture's grabbedIndex) and everything else unlocked
                // flows around the gesture. Previously a soft-point drag fired nothing, so the apex and
                // every earlier insert froze in place and read exactly like locks.
                if (g.ShapeType == GuideShapeType.Arch && g.Constraint == ShapeConstraint.None
                    && idx >= 0 && idx < g.ControlPoints.Count)
                {
                    session.SoftFlow = session.SoftFlow ?? SoftPointFlow.Capture(g.ControlPoints, idx);
                    if (session.SoftFlow != null && session.SoftFlow.HasWork)
                        foreach (var (sIdx, sPos) in session.SoftFlow.ComputeReflowEdits(g.ControlPoints, idx, pos))
                            composed.Add(new ControlPointEdit(sIdx, sPos));
                }
            }

            // Capture pre-drag origins for EVERYTHING in the batch (soft points included), the first time
            // each index moves — release commits them all, cancel restores them all, both already generic.
            foreach (ControlPointEdit e in composed)
            {
                if (e.Index >= 0 && e.Index < g.ControlPoints.Count && !session.Origins.ContainsKey(e.Index))
                    session.Origins[e.Index] = ClonePos(g.ControlPoints[e.Index].WorldPosition);
            }

            bool promotedLockMarker = false;
            for (int i = 0; i < g.ControlPoints.Count && !promotedLockMarker; i++)
                promotedLockMarker = g.ControlPoints[i].IsLockMarker && g.ControlPoints[i].IsLocked;

            if (immenseSculpt && TryQueueImmenseSculpt(
                fromPlayer, g, composed, shouldBreak, session))
                return;

            GuideOperationResult result = _guides.UpdateControlPoints(id, composed.ToArray());
            switch (result.Status)
            {
                case GuideOpStatus.Success:
                    // Authoritative — every client (including the mover, keeping its mirror exact) applies
                    // it. A constraint break changed more than positions, so that case sends full state.
                    if ((broke || promotedLockMarker) &&
                        _guides.TryGetGuide(id, out GuideData gPostMove))
                    {
                        _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(gPostMove)));
                    }
                    else
                    {
                        var dtos = new ControlPointEditDto[composed.Count];
                        for (int i = 0; i < composed.Count; i++)
                            dtos[i] = new ControlPointEditDto(composed[i].Index, Vec3Dto.From(composed[i].Position));
                        _channel.BroadcastPacket(new GuideUpdatePacket(id, dtos));
                    }
                    break;

                case GuideOpStatus.RejectedOverCap:
                    SendCapRefusal(fromPlayer, id, result.VoxelCount, result.CapLimit);
                    SendResync(fromPlayer, g);
                    break;

                case GuideOpStatus.RejectedClaimAccess:
                    SendClaimDenied(fromPlayer, result.DeniedPosition);
                    ResyncOrDrop(fromPlayer, id);
                    break;

                case GuideOpStatus.RejectedPointLocked:
                case GuideOpStatus.InvalidArgument:
                    SendResync(fromPlayer, g);
                    break;

                case GuideOpStatus.GuideNotFound:
                    _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);
                    break;
            }
        }

        private bool TryQueueImmenseSculpt(
            IServerPlayer player, GuideData live, IReadOnlyList<ControlPointEdit> edits,
            bool breakConstraint, DragSession session)
        {
            if (player == null || live == null || edits == null || edits.Count == 0) return false;
            if (_pendingImmenseSculpts.ContainsKey(player.PlayerUID)) return true;
            if (_pendingImmenseSculpts.Count >= MaxQueuedImmenseCreates)
            {
                player.SendIngameError("layout-validationbusy",
                    "The immense-guide validator is busy. Please try that reshape again shortly.");
                SendResync(player, live);
                return true;
            }

            GuideData candidate = live.DeepClone();
            IGuideShape candidateShape = ShapeFactory.Adopt(candidate);
            candidateShape.RecalculatePhantomPoints();
            if (breakConstraint && candidateShape.BreakConstraint())
                candidate.Constraint = ShapeConstraint.None;

            bool promotedMarker = false;
            for (int i = 0; i < candidate.ControlPoints.Count; i++)
                if (candidate.ControlPoints[i].IsLockMarker && candidate.ControlPoints[i].IsLocked)
                {
                    candidate.ControlPoints[i].IsLockMarker = false;
                    promotedMarker = true;
                }

            for (int i = 0; i < edits.Count; i++)
            {
                ControlPointEdit edit = edits[i];
                if (edit.Index < 0 || edit.Index >= candidate.ControlPoints.Count
                    || candidate.ControlPoints[edit.Index].IsPhantom
                    || candidate.ControlPoints[edit.Index].IsLocked
                    || edit.Position == null)
                {
                    SendResync(player, live);
                    return true;
                }
                candidateShape.MoveControlPoint(edit.Index, edit.Position);
            }
            candidateShape.RecalculatePhantomPoints();
            session.CommitAsWholeShape = breakConstraint || promotedMarker;

            ResolveSculptCountLimit(player, live, out int countLimit, out int countLimitCap);
            var pending = new PendingImmenseSculpt
            {
                PlayerUid = player.PlayerUID,
                GuideId = live.Id,
                ExpectedLive = live,
                Candidate = candidate,
                CandidateShape = candidateShape,
                CountLimit = countLimit,
                CountLimitCap = countLimitCap
            };
            _pendingImmenseSculpts[player.PlayerUID] = pending;
            _immenseSculptQueue.Enqueue(pending);
            StartNextImmenseSculpt();
            return true;
        }

        private void ResolveSculptCountLimit(
            IServerPlayer player, GuideData live, out int countLimit, out int countLimitCap)
        {
            countLimit = GuideManager.HardVoxelCeiling;
            countLimitCap = GuideManager.HardVoxelCeiling;

            int perGuideCap = _policies.EffectiveVoxelCap(
                player.PlayerUID, _guides.PerGuideVoxelCap);
            if (perGuideCap > 0 && perGuideCap < countLimit)
            {
                countLimit = perGuideCap;
                countLimitCap = perGuideCap;
            }

            string creatorUid = live?.CreatorUid;
            int creatorTotalCap =
                _guides.EffectivePerPlayerTotalVoxelCap(creatorUid);
            if (creatorTotalCap > 0 && !string.IsNullOrEmpty(creatorUid))
            {
                long creatorTotal = _guides.VoxelCountBy(creatorUid);
                if (creatorTotal <= creatorTotalCap)
                {
                    long creatorWithoutCurrent =
                        creatorTotal - Math.Max(0, live.CachedVoxelCount);
                    long creatorAvailable =
                        (long)creatorTotalCap - creatorWithoutCurrent;
                    int playerLimit = creatorAvailable <= 0 ? 0
                        : creatorAvailable >= int.MaxValue ? int.MaxValue
                        : (int)creatorAvailable;
                    if (playerLimit < countLimit)
                    {
                        countLimit = playerLimit;
                        countLimitCap = creatorTotalCap;
                    }
                }
            }

            if (_guides.TotalVoxelCap <= 0) return;
            long withoutCurrent = _guides.TotalVoxelCount - Math.Max(0, live.CachedVoxelCount);
            long available = (long)_guides.TotalVoxelCap - withoutCurrent;
            int totalLimit = available <= 0 ? 0
                : available >= int.MaxValue ? int.MaxValue
                : (int)available;
            if (totalLimit < countLimit)
            {
                countLimit = totalLimit;
                countLimitCap = _guides.TotalVoxelCap;
            }
        }

        private void StartNextImmenseSculpt()
        {
            if (_disposed || _activeImmenseSculpt != null || _activeImmenseCreate != null
                || _immenseCreateQueue.Count > 0) return;
            while (_immenseSculptQueue.Count > 0)
            {
                PendingImmenseSculpt next = _immenseSculptQueue.Dequeue();
                if (next.Cancelled)
                {
                    _pendingImmenseSculpts.Remove(next.PlayerUid);
                    continue;
                }

                _activeImmenseSculpt = next;
                next.GeometryTask = Task.Factory.StartNew(
                    () => CalculateImmenseSculptGeometry(next),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                return;
            }
        }

        private static ImmenseCreateGeometry CalculateImmenseSculptGeometry(
            PendingImmenseSculpt pending)
        {
            Thread thread = Thread.CurrentThread;
            ThreadPriority originalPriority = ThreadPriority.Normal;
            bool priorityChanged = false;
            try
            {
                try
                {
                    originalPriority = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                }
                catch (Exception) { }

                // Null = abandoned; see CalculateImmenseCreateGeometry.
                if (pending.Cancelled) return null;

                int count = GuideShapeVoxelCounting.CountUpTo(
                    pending.CandidateShape, pending.Candidate.VoxelScale,
                    pending.Candidate.IsFilled, pending.CountLimit,
                    () => pending.Cancelled);
                if (pending.Cancelled) return null;

                if (count > pending.CountLimit)
                    return new ImmenseCreateGeometry
                    {
                        VoxelCount = count,
                        ExceededCap = pending.CountLimitCap
                    };

                List<BlockPos> footprint = GuideClaimAccessValidator.BuildFootprint(
                    pending.Candidate, pending.CandidateShape, () => pending.Cancelled);
                if (footprint == null) return null;      // cancelled mid-collapse

                return new ImmenseCreateGeometry
                {
                    VoxelCount = count,
                    Footprint = footprint,
                    ClaimBounds = ClaimFootprintBounds.FromFootprint(footprint)
                };
            }
            catch (OperationCanceledException) when (pending.Cancelled)
            {
                return null;
            }
            finally
            {
                if (priorityChanged)
                {
                    try { thread.Priority = originalPriority; }
                    catch (Exception) { }
                }
            }
        }

        private void OnImmenseSculptTick(float deltaTime)
        {
            if (_disposed) return;
            StartNextImmenseSculpt();
            PendingImmenseSculpt pending = _activeImmenseSculpt;
            if (pending?.GeometryTask == null || !pending.GeometryTask.IsCompleted) return;
            if (pending.Cancelled)
            {
                CompleteActiveImmenseSculpt();
                return;
            }

            if (pending.Geometry == null)
            {
                try
                {
                    pending.Geometry = pending.GeometryTask.GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    _sapi.Logger.Error(
                        "[Layout] Immense sculpt geometry failed for {0}: {1}",
                        pending.PlayerUid, e.Message);
                    IServerPlayer failed = FindOnlinePlayer(pending.PlayerUid);
                    if (failed != null)
                    {
                        failed.SendIngameError("layout-validationfailed",
                            "That immense reshape could not be validated. The original guide was retained.");
                        FinishRejectedImmenseSculpt(failed, pending);
                    }
                    CompleteActiveImmenseSculpt();
                    return;
                }

                // Abandoned by the worker — see the matching guard in OnImmenseCreateTick.
                if (pending.Geometry == null)
                {
                    IServerPlayer abandoned = FindOnlinePlayer(pending.PlayerUid);
                    if (abandoned != null) FinishRejectedImmenseSculpt(abandoned, pending);
                    CompleteActiveImmenseSculpt();
                    return;
                }
            }

            IServerPlayer player = FindOnlinePlayer(pending.PlayerUid);
            if (player == null || !_locks.IsHeldBy(pending.GuideId, pending.PlayerUid))
            {
                CompleteActiveImmenseSculpt();
                return;
            }

            if (pending.Geometry.ExceededCap > 0)
            {
                SendCapRefusal(player, pending.GuideId, pending.Geometry.VoxelCount,
                    pending.Geometry.ExceededCap);
                FinishRejectedImmenseSculpt(player, pending);
                CompleteActiveImmenseSculpt();
                return;
            }

            List<BlockPos> footprint = pending.Geometry.Footprint ?? new List<BlockPos>();
            if (player.HasPrivilege(Privilege.controlserver))
            {
                // Admins bypass claims entirely, so there is nothing to keep consistent.
                pending.ClaimIndex = footprint.Count;
            }
            else
            {
                if (pending.ClaimSnapshot == null)
                {
                    pending.ClaimSnapshot = CaptureClaimAccessSnapshot(
                        player, pending.Geometry.ClaimBounds);
                }
                else
                {
                    ClaimRevalidationResult beforeSlice = RevalidateClaimAccess(
                        player, pending.Geometry.ClaimBounds, ref pending.ClaimSnapshot,
                        ref pending.ClaimRestarts, ref pending.ClaimIndex);
                    if (beforeSlice == ClaimRevalidationResult.Restarted) return;
                    if (beforeSlice == ClaimRevalidationResult.TooMuchChurn)
                    {
                        RejectImmenseSculptForClaimChurn(player, pending);
                        return;
                    }
                }

                var budget = Stopwatch.StartNew();
                int checks = 0;
                while (pending.ClaimIndex < footprint.Count
                    && checks < MaxClaimChecksPerTick
                    && budget.Elapsed.TotalMilliseconds < ClaimCheckBudgetMilliseconds)
                {
                    BlockPos block = footprint[pending.ClaimIndex++];
                    checks++;
                    if (_sapi.World.Claims.TestAccess(
                        player, block, EnumBlockAccessFlags.BuildOrBreak)
                        != EnumWorldAccessResponse.Granted)
                    {
                        SendClaimDenied(player, block);
                        FinishRejectedImmenseSculpt(player, pending);
                        CompleteActiveImmenseSculpt();
                        return;
                    }
                }

                // Claims or relevant player access state changed mid-walk: rewind and re-check, or reject
                // after bounded repeated churn. See RevalidateClaimAccess.
                if (pending.ClaimIndex >= footprint.Count)
                {
                    ClaimRevalidationResult freshness = RevalidateClaimAccess(
                        player, pending.Geometry.ClaimBounds, ref pending.ClaimSnapshot,
                        ref pending.ClaimRestarts, ref pending.ClaimIndex);
                    if (freshness == ClaimRevalidationResult.Restarted) return;
                    if (freshness == ClaimRevalidationResult.TooMuchChurn)
                    {
                        RejectImmenseSculptForClaimChurn(player, pending);
                        return;
                    }
                }
            }
            if (pending.ClaimIndex < footprint.Count) return;

            // The initial handler gate may be many ticks old by now. Match immense create and re-check the
            // Layout-specific privilege/jail policy immediately before the authoritative mutation.
            if (DeniedByPrivilege(player))
            {
                FinishRejectedImmenseSculpt(player, pending);
                CompleteActiveImmenseSculpt();
                return;
            }

            GuideOperationResult committed;
            int playerCap = _policies.EffectiveVoxelCap(
                player.PlayerUID, _guides.PerGuideVoxelCap);
            using (_guides.UsePerGuideVoxelCap(playerCap))
                committed = _guides.CommitPreparedGuideMutation(
                    pending.GuideId, pending.ExpectedLive, pending.Candidate,
                    pending.CandidateShape, pending.Geometry.VoxelCount);

            if (committed.IsSuccess)
            {
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(committed.Guide)));
                if (pending.ReleaseRequested)
                    OnReleaseWithoutClaimChecks(
                        player, new GuideReleasePacket(pending.GuideId));
            }
            else
            {
                if (committed.Status == GuideOpStatus.RejectedOverCap)
                    SendCapRefusal(player, pending.GuideId, committed.VoxelCount, committed.CapLimit);
                FinishRejectedImmenseSculpt(player, pending);
            }
            CompleteActiveImmenseSculpt();
        }

        private void FinishRejectedImmenseSculpt(
            IServerPlayer player, PendingImmenseSculpt pending)
        {
            if (player == null || pending == null) return;
            if (pending.ReleaseRequested)
                OnReleaseWithoutClaimChecks(player, new GuideReleasePacket(pending.GuideId));
            else
                ResyncOrDrop(player, pending.GuideId);
        }

        private void CompleteActiveImmenseSculpt()
        {
            PendingImmenseSculpt completed = _activeImmenseSculpt;
            _activeImmenseSculpt = null;

            // CHECK IDENTITY BEFORE REMOVING BY KEY (GOTCHAS G29). This is A14.3's actual defect: cancelling
            // an immense reshape (right-click) removes the player's entry but leaves the worker running, so
            // grabbing and reshaping again installs a NEW entry under the same uid. When the cancelled
            // worker finally finished, this line deleted the NEW one — after which nothing could find that
            // reshape by uid, so releasing it never registered and cancelling it did nothing. The player
            // saw their second reshape snap back with no message.
            if (completed != null
                && _pendingImmenseSculpts.TryGetValue(
                    completed.PlayerUid, out PendingImmenseSculpt current)
                && ReferenceEquals(current, completed))
                _pendingImmenseSculpts.Remove(completed.PlayerUid);

            StartNextImmenseCreate();
            StartNextImmenseSculpt();
        }

        private void OnInsert(IServerPlayer fromPlayer, GuideInsertPointPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            string uid = fromPlayer.PlayerUID;

            if (!_guides.HasGuide(id))
            {
                _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);
                return;
            }
            if (p.Position == null) return;

            // Inserting on the body is also a grab: take the lock first.
            LockAcquireOutcome lockOutcome = _locks.TryAcquireLock(id, uid);
            if (!lockOutcome.CanEdit)
            {
                // Someone else is editing — tell the requester who holds it; no insert.
                _channel.SendPacket(new GuideLockStatePacket(id, lockOutcome.HolderUid), fromPlayer);
                return;
            }
            ReleaseSupersededLocks(fromPlayer, id);

            Vec3d pos = p.Position.ToVec3d();
            if (!GuideBounds.IsUsable(pos))
            {
                // Release the lock this handler just took, or the guide is stranded (see the insert-failed
                // tail below, which does the same thing for a cap rejection).
                if (lockOutcome.Status == LockAcquireStatus.Acquired && _locks.ReleaseLock(id, uid))
                    _channel.BroadcastPacket(new GuideLockStatePacket(id, null));
                RejectOutOfWorld(fromPlayer);
                return;
            }

            ShapeConstraint insertOriginConstraint = ShapeConstraint.None;
            List<ControlPoint> insertOriginPoints = null;
            int insertOriginVoxelCount = -1;
            if (_guides.TryGetGuide(id, out GuideData insertOriginGuide))
            {
                insertOriginConstraint = insertOriginGuide.Constraint;
                insertOriginPoints = ClonePoints(insertOriginGuide.ControlPoints);
                insertOriginVoxelCount = insertOriginGuide.CachedVoxelCount;
            }

            // ABSORB-OR-BREAK (Session 8): a body insert is a grab no constraint can absorb — an arbitrary
            // interpolation point has no place on a perfect half-circle. Break first (undoable as one
            // command carrying the pre-break points), then insert into the free parent shape. The single
            // full-state broadcast after the insert carries both changes atomically, so the client's
            // adopt-as-grab handshake sees only the final point list.
            bool broke = false;
            if (_guides.TryGetGuide(id, out GuideData gPre) && gPre.Constraint != ShapeConstraint.None)
            {
                var breakCmd = new BreakConstraintCommand(id, gPre.Constraint, gPre.ControlPoints);
                GuideOperationResult breakResult = _guides.BreakConstraint(id);
                if (breakResult.Status == GuideOpStatus.Success)
                {
                    _undo.Record(uid, breakCmd);
                    broke = true;
                }
                else
                {
                    if (lockOutcome.Status == LockAcquireStatus.Acquired && _locks.ReleaseLock(id, uid))
                        _channel.BroadcastPacket(new GuideLockStatePacket(id, null));
                    HandleNonSuccessToggle(fromPlayer, id, breakResult, gPre);
                    return;
                }
            }

            IGuideShape shape = _guides.GetShape(id);
            float t = shape != null ? shape.GetNearestT(pos) : 0f;

            // LOCK-IN-PLACE flavour (Session 8): "lock any point of the guide". A passive Arch marker keeps
            // the exact rendered voxel selected—it cannot dent the spline because markers are omitted from
            // it. Other insert-taking shapes still project onto their mathematical curve.
            if (p.Locked)
            {
                Vec3d curvePos = shape != null ? shape.GetPointAt(t) : pos;
                bool marker = gPre != null && gPre.ShapeType == GuideShapeType.Arch;
                Vec3d insertPos = marker ? pos : curvePos;
                GuideOperationResult ins = _guides.InsertControlPoint(id, t, insertPos, marker);
                if (ins.Status == GuideOpStatus.Success)
                {
                    int idx = ins.ControlPointIndex;
                    _guides.SetPointLocked(id, idx, true);
                    // Two undo steps, honestly: first Ctrl+Z unlocks, second removes the point.
                    _undo.Record(uid, new InsertControlPointCommand(id, idx, insertPos, marker));
                    _undo.Record(uid, new LockPointCommand(id, idx, false, true));
                    // One full-state broadcast carries the new point AND its locked flag together.
                    if (_guides.TryGetGuide(id, out GuideData withLock))
                    {
                        StampLastSculptor(fromPlayer, withLock, broadcastIncremental: false);
                        _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(withLock)));
                    }
                }
                else
                {
                    if (ins.Status == GuideOpStatus.RejectedOverCap)
                        SendCapRefusal(fromPlayer, id, ins.VoxelCount, ins.CapLimit);
                    else if (ins.Status == GuideOpStatus.RejectedClaimAccess)
                        SendClaimDenied(fromPlayer, ins.DeniedPosition);
                    if (_guides.TryGetGuide(id, out GuideData gNow)) SendResync(fromPlayer, gNow);
                }

                // Whether it worked or not, this flavour never leaves the player holding the edit lock.
                if (_locks.ReleaseLock(id, uid))
                    _channel.BroadcastPacket(new GuideLockStatePacket(id, null));
                return;
            }

            GuideOperationResult result = _guides.InsertControlPoint(id, t, pos);
            if (result.Status == GuideOpStatus.Success)
            {
                int landedIndex = result.ControlPointIndex;
                _undo.Record(uid, new InsertControlPointCommand(id, landedIndex, pos));

                // Start the drag session NOW (not lazily on the first move) and tag it as insert-born, so a
                // GuideCancelGrabPacket that arrives before any move still knows to remove the point.
                DragSession session = DragFor(uid, id);
                session.InsertedIndex = landedIndex;
                session.OriginConstraint = insertOriginConstraint;
                session.OriginPoints = insertOriginPoints;
                session.OriginVoxelCount = insertOriginVoxelCount;

                // Announce the lock (the player now holds it) and the new point to everyone. After a
                // constraint break the point LIST changed shape, so one full-state packet replaces the
                // incremental insert packet — the adopt handshake works off either.
                _channel.BroadcastPacket(new GuideLockStatePacket(id, uid));
                if (broke && _guides.TryGetGuide(id, out GuideData gPost))
                    _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(gPost)));
                else
                    _channel.BroadcastPacket(new GuideInsertPointPacket(id, landedIndex, p.Position));
            }
            else
            {
                // Insert failed (e.g. cap). Don't strand the player holding a lock we just gave them.
                if (lockOutcome.Status == LockAcquireStatus.Acquired && _locks.ReleaseLock(id, uid))
                    _channel.BroadcastPacket(new GuideLockStatePacket(id, null));

                if (result.Status == GuideOpStatus.RejectedOverCap)
                    SendCapRefusal(fromPlayer, id, result.VoxelCount, result.CapLimit);
                else if (result.Status == GuideOpStatus.RejectedClaimAccess)
                    SendClaimDenied(fromPlayer, result.DeniedPosition);

                if (_guides.TryGetGuide(id, out GuideData g)) SendResync(fromPlayer, g);
            }
        }

        // ==========================================================================================
        //  Dispel
        // ==========================================================================================

        private void OnDelete(IServerPlayer fromPlayer, GuideDeletePacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g))
            {
                _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);   // already gone — ensure drop
                return;
            }

            if (BlockedByEditLock(fromPlayer, id)) return;

            // Snapshot before deleting so the delete is undoable.
            var command = new DeleteGuideCommand(g);
            GuideOperationResult result = _guides.DeleteGuide(id);
            if (result.Status == GuideOpStatus.Success)
            {
                _locks.ClearLock(id);                 // force-free any editor's lock; the guide is gone
                _undo.Record(fromPlayer.PlayerUID, command);
                _channel.BroadcastPacket(new GuideDeletePacket(id));
            }
        }

        // ==========================================================================================
        //  Per-guide property toggles (ungated, atomic)
        // ==========================================================================================

        private void OnHide(IServerPlayer fromPlayer, GuideHidePacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            bool oldHidden = g.IsHidden;
            GuideOperationResult result = _guides.SetHidden(id, p.Hidden);
            if (result.Status == GuideOpStatus.Success)
            {
                // Nothing changed — no undo entry (a no-op step in the history is worse than no step) and
                // no broadcast. This handler used to do both unconditionally, so an unchanged value spammed
                // at packet rate was an unbounded persist-and-broadcast loop across every connected player.
                if (oldHidden == result.Guide.IsHidden) return;
                _undo.Record(fromPlayer.PlayerUID, new HideGuideCommand(id, oldHidden, p.Hidden));
                StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideHidePacket(id, p.Hidden));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        /// <summary>
        /// "Reveal All" (v0.4.19): un-hides every guide this player created. Runs server-side because the
        /// client has no way to know who made a guide — see <see cref="GuideRevealMinePacket"/>.
        /// </summary>
        /// <remarks>
        /// Each guide still goes through <see cref="GuideManager.SetHidden"/> with its own undo entry and
        /// broadcast, exactly as a single un-hide does, so nothing here can drift from the one-guide path.
        ///
        /// A guide someone else is holding open for editing is SKIPPED SILENTLY rather than refused
        /// loudly: BlockedByEditLock sends a chat error per call, which on a bulk action would be a wall
        /// of identical messages about guides the player never singled out. Their own locks are fine.
        /// </remarks>
        private void OnRevealMine(IServerPlayer fromPlayer, GuideRevealMinePacket packet)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            string uid = fromPlayer.PlayerUID;
            if (string.IsNullOrEmpty(uid)) return;

            // Collected first: SetHidden mutates the dictionary this walks.
            var targets = new List<Guid>();
            foreach (GuideData g in _guides.AllGuides.Values)
            {
                if (g == null || !g.IsHidden) continue;
                if (g.CreatorUid != uid) continue;
                string holder = _locks.GetHolder(g.Id);
                if (holder != null && holder != uid) continue;
                targets.Add(g.Id);
            }

            int revealed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Guid id = targets[i];
                if (!_guides.TryGetGuide(id, out GuideData live) || !live.IsHidden) continue;
                GuideOperationResult result = _guides.SetHidden(id, false);
                if (result.Status != GuideOpStatus.Success) continue;
                _undo.Record(uid, new HideGuideCommand(id, true, false));
                StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideHidePacket(id, false));
                revealed++;
            }

            // Only the empty case needs words. When guides are revealed they visibly appear, which says it
            // better than a count would.
            if (revealed == 0)
                fromPlayer.SendIngameError("layout-nothingtoreveal",
                    "You have no hidden guides on this server.");
        }

        private void OnLockPoint(IServerPlayer fromPlayer, GuideLockPointPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;
            if (p.Index < 0 || p.Index >= g.ControlPoints.Count) { SendResync(fromPlayer, g); return; }

            ControlPoint target = g.ControlPoints[p.Index];
            if (!p.Locked && target.IsLockMarker)
            {
                var command = new RemoveLockMarkerCommand(id, p.Index, target.WorldPosition);
                GuideOperationResult removed = _guides.RemoveControlPoint(id, p.Index);
                if (removed.Status == GuideOpStatus.Success)
                {
                    _undo.Record(fromPlayer.PlayerUID, command);
                    StampLastSculptor(fromPlayer, removed.Guide, broadcastIncremental: false);
                    _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(removed.Guide)));
                }
                else HandleNonSuccessToggle(fromPlayer, id, removed, g);
                return;
            }

            bool before = target.IsLocked;
            GuideOperationResult result = _guides.SetPointLocked(id, p.Index, p.Locked);
            if (result.Status == GuideOpStatus.Success)
            {
                _undo.Record(fromPlayer.PlayerUID, new LockPointCommand(id, p.Index, before, p.Locked));
                if (before != result.Guide.ControlPoints[p.Index].IsLocked)
                    StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideLockPointPacket(id, p.Index, p.Locked));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        private void OnRescale(IServerPlayer fromPlayer, GuideRescalePacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            int oldScale = g.VoxelScale;
            GuideOperationResult result = _guides.Rescale(id, p.Scale);
            if (result.Status == GuideOpStatus.Success)
            {
                _undo.Record(fromPlayer.PlayerUID, new RescaleGuideCommand(id, oldScale, p.Scale));
                if (oldScale != result.Guide.VoxelScale) StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideRescalePacket(id, p.Scale));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        private void OnSetProjection(IServerPlayer fromPlayer, GuideSetProjectionPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            ProjectionMode oldMode = g.Projection;
            ProjectionPlane oldPlane = g.Plane;
            ProjectionMode newMode = (ProjectionMode)p.Mode;
            ProjectionPlane newPlane = p.ResolvePlane();

            // Leaving Surface BAKES the flattened positions into the points (see GuideManager.SetProjection)
            // — snapshot them first so the undo command can put the true 3D spine back, and broadcast full
            // state afterwards because more than the mode changed.
            bool bakes = oldMode == ProjectionMode.Surface && newMode == ProjectionMode.Volumetric;
            List<ControlPoint> pointsBefore = null;
            if (bakes)
            {
                pointsBefore = new List<ControlPoint>(g.ControlPoints.Count);
                foreach (var cp in g.ControlPoints) pointsBefore.Add(cp.Clone());
            }

            GuideOperationResult result = _guides.SetProjection(id, newMode, newPlane);
            if (result.Status == GuideOpStatus.Success)
            {
                _undo.Record(fromPlayer.PlayerUID,
                    new SetProjectionCommand(id, oldMode, oldPlane, newMode, newPlane, pointsBefore));
                bool changed = oldMode != result.Guide.Projection
                    || oldPlane.FlattenedAxis != result.Guide.Plane.FlattenedAxis
                    || oldPlane.PlaneOffset != result.Guide.Plane.PlaneOffset;
                if (changed) StampLastSculptor(fromPlayer, result.Guide, broadcastIncremental: !bakes);
                if (bakes)
                    _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
                else
                    _channel.BroadcastPacket(new GuideSetProjectionPacket(id, p.Mode, p.PlaneAxis, p.PlaneOffset));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        private void OnSetFilled(IServerPlayer fromPlayer, GuideSetFilledPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            bool oldFilled = g.IsFilled;
            GuideOperationResult result = _guides.SetFilled(id, p.Filled);
            if (result.Status == GuideOpStatus.Success)
            {
                _undo.Record(fromPlayer.PlayerUID, new SetFilledCommand(id, oldFilled, p.Filled));
                if (oldFilled != result.Guide.IsFilled) StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideSetFilledPacket(id, p.Filled));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        private void OnSetWireframe(IServerPlayer fromPlayer, GuideSetWireframePacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g))
            {
                _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);
                return;
            }
            if (BlockedByEditLock(fromPlayer, id)) return;

            bool oldWireframe = g.IsWireframe;
            GuideOperationResult result = _guides.SetWireframe(id, p.Wireframe);
            if (result.Status == GuideOpStatus.Success)
            {
                _undo.Record(fromPlayer.PlayerUID,
                    new SetWireframeCommand(id, oldWireframe, result.Guide.IsWireframe));
                if (oldWireframe != result.Guide.IsWireframe) StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideSetWireframePacket(id, result.Guide.IsWireframe));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        private void OnSetDivisions(IServerPlayer fromPlayer, GuideSetDivisionsPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            int oldDivisions = g.Divisions;
            GuideOperationResult result = _guides.SetDivisions(id, p.Divisions);
            if (result.Status == GuideOpStatus.Success)
            {
                // No-op guard — see OnHide.
                if (oldDivisions == result.Guide.Divisions) return;
                _undo.Record(fromPlayer.PlayerUID, new SetDivisionsCommand(id, oldDivisions, result.Guide.Divisions));
                StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideSetDivisionsPacket(id, result.Guide.Divisions));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        private void OnSetSides(IServerPlayer fromPlayer, GuideSetSidesPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            int oldSides = g.Sides;
            GuideOperationResult result = _guides.SetSides(id, p.Sides);
            if (result.Status == GuideOpStatus.Success)
            {
                if (result.Guide.Sides != oldSides)
                {
                    _undo.Record(fromPlayer.PlayerUID, new SetSidesCommand(id, oldSides, result.Guide.Sides));
                    StampLastSculptor(fromPlayer, result.Guide);
                    _channel.BroadcastPacket(new GuideSetSidesPacket(id, result.Guide.Sides));
                }
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        // SHIFT+click spring-back (Session 11): restore the as-placed control points + constraint, as one
        // undo step. Geometry is rewritten wholesale, so the broadcast is the generic full-state upsert.
        // The full-exclusivity gate applies — it's an atomic op like the toggles.
        private void OnSpringBack(IServerPlayer fromPlayer, GuideSpringBackPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            if (g.OriginalControlPoints == null || g.OriginalControlPoints.Count == 0)
            {
                fromPlayer.SendIngameError("layout-nooriginal",
                    "This guide has no recorded original form (it was placed before spring-back existed).");
                return;
            }

            // Snapshot the distorted form BEFORE restoring, so the spring-back is one clean undo step.
            ShapeConstraint beforeConstraint = g.Constraint;
            var beforePoints = new List<ControlPoint>(g.ControlPoints.Count);
            foreach (var cp in g.ControlPoints) beforePoints.Add(cp.Clone());

            GuideOperationResult result = _guides.SpringBackToOriginal(id);
            if (result.Status == GuideOpStatus.Success)
            {
                _undo.Record(fromPlayer.PlayerUID, new SpringBackCommand(
                    id, beforeConstraint, beforePoints,
                    result.Guide.Constraint, result.Guide.ControlPoints));
                StampLastSculptor(fromPlayer, result.Guide, broadcastIncremental: false);
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        // F6 Move (0.3.86): slide a whole guide. Geometry is rewritten wholesale, so — exactly like
        // spring-back — the broadcast is the generic full-state upsert and the full-exclusivity edit lock
        // applies. No chalk is charged: chalk is a PLACEMENT cost (F5) and repositioning an existing guide
        // is not a placement. No cap check either — a rigid translation cannot change the voxel count.
        private void OnTranslate(IServerPlayer fromPlayer, GuideTranslatePacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            Vec3d delta = p.ResolveDelta();
            GuideOperationResult result = _guides.TranslateGuide(id, delta);
            if (result.Status == GuideOpStatus.Success)
            {
                if (p.DeltaX != 0 || p.DeltaY != 0 || p.DeltaZ != 0)
                {
                    _undo.Record(fromPlayer.PlayerUID, new TranslateGuideCommand(id, delta));
                    StampLastSculptor(fromPlayer, result.Guide, broadcastIncremental: false);
                }
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        // F12 Rotate (0.3.90): quarter-turn a whole guide. Same shape as OnTranslate — geometry rewritten
        // wholesale, so a full-state broadcast, and the full-exclusivity edit lock applies. Unlike a move
        // this CAN change the voxel count (see GuideManager.RotateGuide), so the cap path is live.
        private void OnRotate(IServerPlayer fromPlayer, GuideRotatePacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            // Null pivot: the authority derives it and hands it back, so the undo command turns the guide
            // back about the same point rather than about wherever its new bounding centre landed.
            Vec3d pivot = null;
            GuideOperationResult result = _guides.RotateGuide(id, p.ResolveAxis(), p.QuarterTurns, ref pivot);
            if (result.Status == GuideOpStatus.Success)
            {
                if (p.QuarterTurns % 4 != 0)
                {
                    _undo.Record(fromPlayer.PlayerUID,
                        new RotateGuideCommand(id, p.ResolveAxis(), p.QuarterTurns, pivot));
                    StampLastSculptor(fromPlayer, result.Guide, broadcastIncremental: false);
                }
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        // F7/F8 Transform pad (0.3.93): one compound action — mirror and/or rotate and/or move, applied in
        // place or to a fresh copy. A COPY is the only transform action that is a placement: it charges
        // chalk, counts against the creator's cumulative budget, and can be refused on those grounds. The
        // in-place actions are free and can only be refused by a land claim.
        private void OnTransform(IServerPlayer fromPlayer, GuideTransformPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            Guid id = p.GuideId();
            if (!_guides.TryGetGuide(id, out GuideData g)) { _channel.SendPacket(new GuideDeletePacket(id), fromPlayer); return; }
            if (BlockedByEditLock(fromPlayer, id)) return;

            Vec3d delta = p.ResolveDelta();

            if (p.AsCopy)
            {
                // The same NO-LOCKOUT gate a fresh placement gets: an empty kit blocks a new guide, and
                // nothing else. Checked before the copy is built so a refusal costs no voxel generation.
                if (ChalkApplies(fromPlayer, out ItemSlot kitSlot)
                    && Items.ItemGuideTool.GetChalk(kitSlot.Itemstack) <= 0)
                {
                    fromPlayer.SendIngameError("layout-outofchalk",
                        "Out of chalk. Refill the Chalking Kit with Chalking Powder (hold it and right-click).");
                    return;
                }

                GuideOperationResult copy = _guides.CopyGuide(
                    id, delta, p.MirrorAxis, p.ResolveRotateAxis(), p.QuarterTurns,
                    fromPlayer.PlayerUID, fromPlayer.PlayerName);
                if (copy.Status != GuideOpStatus.Success) { HandleNonSuccessToggle(fromPlayer, id, copy, g); return; }

                _undo.Record(fromPlayer.PlayerUID, new CreateGuideCommand(copy.Guide));
                ChalkEffects.PlacementEffects(_sapi.World, copy.Guide);
                // Charged exactly as a placement is, and deliberately outside the undo system for the same
                // reason: undoing a copy does not refund its chalk, and redo re-creates through the command
                // path so it cannot double-charge.
                if (ChalkApplies(fromPlayer, out ItemSlot chargeSlot))
                    Items.ItemGuideTool.ConsumeChalk(chargeSlot,
                        GuideShapeTypes.IsVolume(copy.Guide.ShapeType)
                            ? Items.ItemGuideTool.ChalkCostVolume
                            : Items.ItemGuideTool.ChalkCostFlat);
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(copy.Guide)));
                return;
            }

            Vec3d pivot = null;
            GuideOperationResult result = _guides.TransformGuide(
                id, delta, p.MirrorAxis, p.ResolveRotateAxis(), p.QuarterTurns,
                inverseOrder: false, ref pivot);
            if (result.Status == GuideOpStatus.Success)
            {
                bool changed = p.QuarterTurns % 4 != 0 || p.MirrorAxis >= 0
                    || p.DeltaX != 0 || p.DeltaY != 0 || p.DeltaZ != 0;
                if (changed)
                {
                    _undo.Record(fromPlayer.PlayerUID,
                        new TransformGuideCommand(
                            id, delta, p.MirrorAxis,
                            p.ResolveRotateAxis(), p.QuarterTurns, pivot));
                    StampLastSculptor(fromPlayer, result.Guide, broadcastIncremental: false);
                }
                _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
        }

        // ==========================================================================================
        //  Undo / redo (generic full-state broadcast)
        // ==========================================================================================

        private void OnUndo(IServerPlayer fromPlayer, UndoRequestPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            ApplyUndoRedo(fromPlayer, _undo.Undo(fromPlayer.PlayerUID));
        }

        private void OnRedo(IServerPlayer fromPlayer, RedoRequestPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            ApplyUndoRedo(fromPlayer, _undo.Redo(fromPlayer.PlayerUID));
        }

        private void ApplyUndoRedo(IServerPlayer fromPlayer, UndoRedoOutcome outcome)
        {
            switch (outcome.Status)
            {
                case UndoRedoStatus.Applied:
                    GuideData affected = outcome.Result.Guide;
                    if (affected == null) break;
                    // One rule for every command type: present guide -> full state; absent -> delete.
                    if (_guides.HasGuide(affected.Id))
                    {
                        StampLastSculptor(fromPlayer, affected, broadcastIncremental: false);
                        _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(affected)));
                    }
                    else
                        _channel.BroadcastPacket(new GuideDeletePacket(affected.Id));
                    break;

                case UndoRedoStatus.Blocked:
                    GuideOperationResult blockedResult = outcome.Result;
                    switch (blockedResult.Status)
                    {
                        case GuideOpStatus.RejectedGuideLocked:
                            fromPlayer.SendIngameError("layout-guidelocked",
                                "Can't undo/redo that yet — the guide is being edited by another player.");
                            break;

                        case GuideOpStatus.RejectedOverGuideCount:
                            SendGuideCountRefusal(fromPlayer, blockedResult,
                                "Can't restore that guide - the guide limit ("
                                + blockedResult.VoxelCount.ToString("N0") + " of "
                                + blockedResult.CapLimit.ToString("N0") + ") is reached.");
                            break;

                        case GuideOpStatus.RejectedClaimAccess:
                            SendClaimDenied(fromPlayer, blockedResult.DeniedPosition,
                                "Can't undo/redo that guide because it would enter protected land");
                            break;

                        case GuideOpStatus.RejectedEmpty:
                            // Reachable only for a zero-voxel guide from a save written before v0.4.37,
                            // when creating one was still possible. Without this it would fall into the
                            // cap-warning default below and report a limit of zero, which means nothing.
                            fromPlayer.SendIngameError("layout-emptyguide",
                                "That guide has no voxels and can no longer be restored. "
                                + "Dispel it with '/layout dispel' if it is still listed.");
                            break;

                        default:   // over a voxel cap — the classic Blocked case
                            if (blockedResult.Guide != null)
                                SendCapRefusal(fromPlayer, blockedResult.Guide.Id,
                                    blockedResult.VoxelCount, blockedResult.CapLimit);
                            break;
                    }
                    break;

                // NothingToApply: nothing to do.
            }
        }

        // ==========================================================================================
        //  Helpers
        // ==========================================================================================

        /// <summary>
        /// The rate gate plus the per-player cap scope — the wrapper every mutating packet handler goes
        /// through. <paramref name="cost"/> weights the work: a toggle is 1, a placement that queues
        /// geometry is 5, a sweep over every guide is 10.
        /// </summary>
        private void Gated(IServerPlayer player, int cost, Action action)
        {
            if (RateLimited(player, cost)) return;
            WithPlayerVoxelCap(player, action);
        }

        // True = over budget, drop this packet. Standard token bucket; see the constants above for why the
        // allowance is set so far above real play.
        private bool RateLimited(IServerPlayer player, int cost)
        {
            if (player == null) return false;
            string uid = player.PlayerUID;
            long now = _sapi.World.ElapsedMilliseconds;

            if (!_rateBuckets.TryGetValue(uid, out RateBucket bucket))
            {
                bucket = new RateBucket { LastMs = now };
                _rateBuckets[uid] = bucket;
            }

            double seconds = Math.Max(0, now - bucket.LastMs) / 1000.0;
            bucket.LastMs = now;
            bucket.Tokens = Math.Min(
                RateBucketCapacity, bucket.Tokens + seconds * RateBucketRefillPerSecond);

            if (bucket.Tokens < cost)
            {
                if (now - bucket.LastWarnMs >= 5000)
                {
                    bucket.LastWarnMs = now;
                    _sapi.Logger.Notification(
                        "[Layout] Rate limit reached for {0}; Layout packets are being dropped. This "
                        + "should not happen in normal play — if it did, the limit is set too low.",
                        PlayerDisplayName(player));
                }
                return true;
            }

            bucket.Tokens -= cost;
            return false;
        }

        private void WithPlayerVoxelCap(IServerPlayer player, Action action)
        {
            int cap = _policies.EffectiveVoxelCap(player?.PlayerUID, _guides.PerGuideVoxelCap);
            GuideMutationAccessValidator accessValidator = null;
            if (player != null && !player.HasPrivilege(Privilege.controlserver))
                accessValidator = new GuideClaimAccessValidator(_sapi, player).Validate;

            using (_guides.UsePerGuideVoxelCap(cap))
            using (_guides.UseMutationAccessValidator(accessValidator))
                action();
        }

        private void SendPlayerPolicy(IServerPlayer player)
        {
            if (player == null) return;
            _channel.SendPacket(new PlayerGuidePolicyPacket(
                _policies.EffectiveVoxelCap(player.PlayerUID, _guides.PerGuideVoxelCap),
                _policies.IsJailed(player.PlayerUID)), player);
        }

        // ==========================================================================================
        //  Admin settings from the settings page (v0.4.16, protocol 20)
        // ==========================================================================================
        //
        // The same values the /layout admin commands and layout.json already govern, reachable from the
        // panel. The commands are NOT replaced: they cover the per-PLAYER overrides (jail, free, limit,
        // voxelcap, totalvoxelcap), which need a player name and belong on a command line. This section
        // covers the server-WIDE settings, which are a fixed short list and read far better as a form.

        /// <summary>Sends one player the live server settings, with their own permission to change them.</summary>
        private void SendAdminConfig(IServerPlayer player)
        {
            if (player == null) return;
            _channel.SendPacket(BuildAdminConfig(player), player);
        }

        /// <summary>Re-sends the settings to everyone — each player gets their own CanEdit.</summary>
        private void BroadcastAdminConfig()
        {
            foreach (IServerPlayer p in _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
                SendAdminConfig(p);
        }

        private LayoutAdminConfigPacket BuildAdminConfig(IServerPlayer player) =>
            new LayoutAdminConfigPacket
            {
                PerGuideVoxelCap = _guides.PerGuideVoxelCap,
                PerPlayerTotalVoxelCap = _guides.PerPlayerTotalVoxelCap,
                TotalVoxelCap = _guides.TotalVoxelCap,
                MaxGuidesPerPlayer = _guides.MaxGuidesPerPlayer,
                MaxGuidesWorldWide = _guides.MaxGuidesWorldWide,
                AllowClientOnlyMode = _allowClientOnlyMode,
                EnableChalkDurability = _chalkDurabilityEnabled,
                AdminCanOverrideLocks = _adminCanOverrideLocks,
                CanEdit = player != null && player.HasPrivilege(Privilege.controlserver),
                // Per-recipient: this packet is sent to each player individually, so their own overrides
                // ride along with it and the panel can say when the caps above do not apply to them.
                YourVoxelCapOverride = _policies.VoxelCapOverride(player?.PlayerUID),
                YourTotalVoxelCapOverride = _policies.PlayerTotalVoxelCapOverride(player?.PlayerUID),
                YourGuideLimitOverride = _policies.GuideLimitOverride(player?.PlayerUID)
            };

        /// <summary>
        /// Applies one admin setting. The privilege is re-checked HERE and not taken from the packet: the
        /// CanEdit flag the client holds is a drawing hint it could trivially forge.
        /// </summary>
        private void OnAdminConfigRequest(IServerPlayer fromPlayer, LayoutAdminConfigRequestPacket packet)
        {
            if (fromPlayer == null || packet == null) return;
            // Logged on ARRIVAL, before any validation can reject it: paired with the client's own "sending"
            // line this is what separates "the packet never got here" from "it got here and was refused".
            _sapi.Logger.Notification("[Layout] Admin config request from {0}: setting {1} = {2}.",
                PlayerDisplayName(fromPlayer), packet.Setting, packet.Value);
            if (!fromPlayer.HasPrivilege(Privilege.controlserver))
            {
                _sapi.Logger.Notification(
                    "[Layout] {0} tried to change admin setting {1} without controlserver; refused.",
                    PlayerDisplayName(fromPlayer), packet.Setting);
                // Re-send the truth so a stale or forged panel snaps back to what the server actually has.
                SendAdminConfig(fromPlayer);
                return;
            }

            if (!Enum.IsDefined(typeof(LayoutAdminSetting), packet.Setting))
            {
                SendAdminConfig(fromPlayer);
                return;
            }

            var setting = (LayoutAdminSetting)packet.Setting;

            // RETIRED SETTINGS STOP HERE. Slots 6 and 7 are still declared — pinned wire numbers are
            // append-only and deleting them is exactly the reversal G1 exists to prevent — but the server
            // has not accepted them since v0.4.17/v0.4.20. They used to fall through the switch's
            // `default:` and then run the ENTIRE tail anyway: layout.json rewritten, the change logged, and
            // the admin told "EnableChalkDurability is now unlimited" for a setting that had not changed
            // and is not even a number. No state moved, so the wire ledger's "changes nothing" was true of
            // state — but not of the reply, and the reply is the only part an admin sees.
            //
            // Answer the same way an undefined setting is answered: re-send the truth, say nothing false.
            if (setting == LayoutAdminSetting.EnableChalkDurability
                || setting == LayoutAdminSetting.AdminCanOverrideLocks)
            {
                _sapi.Logger.Notification(
                    "[Layout] {0} requested retired admin setting {1}; ignored. It is edited in "
                    + "layout.json only.", PlayerDisplayName(fromPlayer), setting);
                SendAdminConfig(fromPlayer);
                return;
            }

            int value = packet.Value;
            bool flag = value != 0;

            switch (setting)
            {
                case LayoutAdminSetting.PerGuideVoxelCap:       _config.PerGuideVoxelCap = value; break;
                case LayoutAdminSetting.PerPlayerTotalVoxelCap: _config.PerPlayerTotalVoxelCap = value; break;
                case LayoutAdminSetting.TotalVoxelCap:          _config.TotalVoxelCap = value; break;
                case LayoutAdminSetting.MaxGuidesPerPlayer:     _config.MaxGuidesPerPlayer = value; break;
                case LayoutAdminSetting.MaxGuidesWorldWide:     _config.MaxGuidesWorldWide = value; break;
                case LayoutAdminSetting.AllowClientOnlyMode:    _config.AllowClientOnlyMode = flag; break;
                // The retired slots 6 and 7 never reach here — they are answered and returned above.
                default: break;
            }

            // Normalize owns "unlimited": it folds negatives to 0 so a typed -5 means the same thing on the
            // wire, in memory, and in the file rather than three different things.
            _config.Normalize();

            _guides.ApplyCaps(
                _config.PerGuideVoxelCap, _config.TotalVoxelCap, _config.PerPlayerTotalVoxelCap,
                _config.MaxGuidesPerPlayer, _config.MaxGuidesWorldWide);
            _allowClientOnlyMode = _config.AllowClientOnlyMode;

            try { _persistConfig?.Invoke(_config); }
            catch (Exception e)
            {
                _sapi.Logger.Warning(
                    "[Layout] Admin setting applied but layout.json could not be written ({0}); "
                    + "it will revert on restart.", e.Message);
            }

            _sapi.Logger.Notification("[Layout] {0} set {1} to {2}.",
                PlayerDisplayName(fromPlayer), setting, value);

            // Tell the admin what actually landed, reading the value back out of the live manager rather
            // than echoing what they sent. That difference matters: it is the one thing on screen that
            // distinguishes "the server applied this" from "the panel shows what I typed", and a value
            // the server normalised (a negative folded to unlimited) shows up here as the number it
            // really became.
            fromPlayer.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
                "[Layout] " + AdminSettingLabel(setting) + " is now "
                + DescribeAdminValue(setting, AdminSettingValue(setting)) + ".",
                EnumChatType.Notification);

            // Everyone is told, not just the admin: a per-guide cap change has to reach every client's
            // placement pre-check, or their ghost would keep clamping to the old number and a placement
            // that the server now accepts would look refused before it was even sent.
            BroadcastAdminConfig();
            foreach (IServerPlayer p in _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
                SendPlayerPolicy(p);

            // Revoking private guides mid-session leaves players holding them. They keep their guides —
            // nothing is deleted — but their placement mode is forced back to public so no NEW private
            // guide is made under a policy that no longer allows it.
            if (setting == LayoutAdminSetting.AllowClientOnlyMode && !_allowClientOnlyMode)
                ForceEveryoneOffClientOnly();
        }

        // ==========================================================================================
        //  Admin player roster (v0.4.26, protocol 23) — behind the settings page's Players button
        // ==========================================================================================
        //
        // WHO COUNTS AS A PLAYER Layout knows about? Three sources, unioned: everyone online, everyone
        // carrying a policy (a jail or an override), and everyone who has a guide standing. None alone is
        // enough — an offline builder has no session, a builder with default limits has no policy, and a
        // jailed player who never built has no guides. Missing any of the three would leave an admin
        // hunting for someone the panel had quietly decided did not exist.

        /// <summary>How many of a player's guides the detail pane lists before summarising the rest.</summary>
        private const int MaxListedPlayerGuides = 60;

        private void OnPlayerRosterRequest(IServerPlayer fromPlayer, PlayerRosterRequestPacket packet)
        {
            if (!AdminRequestAllowed(fromPlayer, "roster")) return;
            // Admin-only, but it walks every guide in the world to build the totals, so it shares the same
            // budget as the mutations rather than being free to spam.
            if (RateLimited(fromPlayer, 10)) return;
            SendRosterTo(fromPlayer);
        }

        /// <summary>
        /// Sends one admin the whole roster plus the world totals. Every editing path ends here rather than
        /// answering with just the row it changed: the server is the authority on what was actually stored,
        /// so an edit that was clamped, or that shifted an effective cap somewhere else, shows up honestly
        /// instead of leaving the dialog displaying the number the admin typed.
        /// </summary>
        private void SendRosterTo(IServerPlayer fromPlayer)
        {
            if (fromPlayer == null) return;
            _channel.SendPacket(new PlayerRosterPacket
            {
                Players = BuildRoster(),
                WorldVoxelTotal = _guides.TotalVoxelCount,
                WorldGuideCount = _guides.GuideCount
            }, fromPlayer);
        }

        // ==========================================================================================
        //  Editing a player's limits from the Players dialog (v0.4.31, protocol 24)
        // ==========================================================================================
        //
        // The same state /layout voxelcap, totalvoxelcap and limit set, reached from a form instead of
        // three command lines. The COMMANDS ARE NOT DEPRECATED and both routes land on the same setters.
        //
        // WHAT THESE HANDLERS MUST NOT DO IS ONLY CALL THE SETTER. Each command does follow-up work that
        // the panel needs just as much: the target's own client caches its effective per-guide cap to clamp
        // its draft preview, and its settings page shows its limits. A GUI edit that skipped those would
        // leave the server correct and the affected player's client quietly stale until they reconnected.

        private void OnPlayerPolicyEdit(IServerPlayer fromPlayer, PlayerPolicyEditPacket packet)
        {
            if (!AdminRequestAllowed(fromPlayer, "player limits")) return;
            string uid = packet?.Uid;
            if (string.IsNullOrEmpty(uid)) return;

            string name = RosterName(uid);
            IServerPlayer target = OnlinePlayerByUid(uid);

            // CLAMPED, NOT REFUSED. The commands error out on a bad number because a person typed it and
            // can read the reply; here the reply is the roster, so a value that cannot be stored is pulled
            // into range and the panel redraws showing what the server really holds. Zero means "clear the
            // override", exactly as on the command line.
            int perGuide = Math.Min(Math.Max(0, packet.VoxelCapOverride), GuideManager.HardVoxelCeiling);
            int total = Math.Max(0, packet.TotalVoxelCapOverride);
            int limit = Math.Max(0, packet.GuideLimitOverride);

            bool changed = _policies.SetVoxelCap(uid, name, perGuide);
            changed |= _policies.SetPlayerTotalVoxelCap(uid, name, total);
            changed |= _policies.SetGuideLimit(uid, name, limit);

            if (target != null)
            {
                SendPlayerPolicy(target);   // their draft clamp follows the new per-guide cap
                SendAdminConfig(target);    // and their own settings page follows their overrides
            }

            if (changed)
                _sapi.Logger.Notification(
                    "[Layout] {0} set {1}'s limits: per-guide {2}, cumulative {3}, guides {4}.",
                    PlayerDisplayName(fromPlayer), name, perGuide, total, limit);

            SendRosterTo(fromPlayer);
        }

        // Mirrors OnJailCommand exactly, including the offline branch — see the remarks on PlayerJailPacket
        // for why this is not a field on the cap edit.
        private void OnPlayerJail(IServerPlayer fromPlayer, PlayerJailPacket packet)
        {
            if (!AdminRequestAllowed(fromPlayer, "jail")) return;
            string uid = packet?.Uid;
            if (string.IsNullOrEmpty(uid)) return;

            string name = RosterName(uid);
            IServerPlayer target = OnlinePlayerByUid(uid);
            bool jail = packet.Jailed;

            _policies.SetJailed(uid, name, jail);

            if (jail)
            {
                if (target != null)
                {
                    WithPlayerVoxelCap(target, () => CancelPlayerPublicActivity(target));
                    SendPlayerPolicy(target);
                    target.SendIngameError("layout-jailed",
                        "An administrator suspended your access to public Layout guides.");
                }
                else
                {
                    _undo.ClearPlayer(uid);
                    ClearPlayerSessionState(uid);
                }
            }
            else if (target != null) SendPlayerPolicy(target);

            _sapi.Logger.Notification("[Layout] {0} {1} {2}.",
                PlayerDisplayName(fromPlayer), jail ? "jailed" : "freed", name);

            SendRosterTo(fromPlayer);
        }

        private IServerPlayer OnlinePlayerByUid(string uid) =>
            string.IsNullOrEmpty(uid)
                ? null
                : _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
                    .FirstOrDefault(p => p.PlayerUID == uid);

        /// <summary>
        /// The best display name known for a uid, in the same precedence <see cref="BuildRoster"/> uses.
        /// The setters take a name as well as a uid because they REMEMBER it — a policy on an offline player
        /// is only readable later because LastKnownName was stored with it, so passing "Unknown" here would
        /// gradually erase the roster's own labels.
        /// </summary>
        private string RosterName(string uid)
        {
            IServerPlayer online = OnlinePlayerByUid(uid);
            if (online != null) return PlayerDisplayName(online);

            PlayerPolicy policy = _policies.Get(uid);
            if (!string.IsNullOrWhiteSpace(policy?.LastKnownName)) return policy.LastKnownName;

            foreach (GuideData g in _guides.AllGuides.Values)
                if (g?.CreatorUid == uid && !string.IsNullOrWhiteSpace(g.CreatorName)) return g.CreatorName;

            return "Unknown";
        }

        private void OnPlayerGuidesRequest(IServerPlayer fromPlayer, PlayerGuidesRequestPacket packet)
        {
            if (!AdminRequestAllowed(fromPlayer, "player guides")) return;
            if (RateLimited(fromPlayer, 10)) return;   // walks and sorts the whole registry — see above
            string uid = packet?.Uid;
            if (string.IsNullOrEmpty(uid)) return;

            List<GuideData> owned = _guides.AllGuides.Values
                .Where(g => g != null && g.CreatorUid == uid)
                .OrderByDescending(g => g.CachedVoxelCount)
                .ToList();

            var listed = new List<PlayerGuideDto>(Math.Min(owned.Count, MaxListedPlayerGuides));
            for (int i = 0; i < owned.Count && i < MaxListedPlayerGuides; i++)
            {
                GuideData g = owned[i];
                Vec3d anchor = FirstAnchorPos(g);
                listed.Add(new PlayerGuideDto
                {
                    ShapeName = g.ShapeType.ToString(),
                    VoxelCount = g.CachedVoxelCount,
                    X = anchor == null ? 0 : (int)Math.Floor(anchor.X),
                    Y = anchor == null ? 0 : (int)Math.Floor(anchor.Y),
                    Z = anchor == null ? 0 : (int)Math.Floor(anchor.Z),
                    Hidden = g.IsHidden
                });
            }

            _channel.SendPacket(new PlayerGuidesPacket
            {
                Uid = uid,
                Guides = listed.ToArray(),
                TotalCount = owned.Count
            }, fromPlayer);
        }

        // Both roster requests are read-only, but they expose every player's name and usage, so they are
        // gated exactly like the commands that report the same thing.
        private bool AdminRequestAllowed(IServerPlayer fromPlayer, string what)
        {
            if (fromPlayer == null) return false;
            if (fromPlayer.HasPrivilege(Privilege.controlserver)) return true;
            _sapi.Logger.Notification(
                "[Layout] {0} requested the admin {1} without controlserver; refused.",
                PlayerDisplayName(fromPlayer), what);
            return false;
        }

        private PlayerRosterEntryDto[] BuildRoster()
        {
            // uid -> best known display name. Later sources only fill gaps, so an online player's current
            // name wins over the name a stale policy or an old guide record remembers.
            var names = new Dictionary<string, string>();
            void Remember(string uid, string name)
            {
                if (string.IsNullOrEmpty(uid)) return;
                if (!names.TryGetValue(uid, out string existing) || string.IsNullOrWhiteSpace(existing))
                    names[uid] = string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
            }

            var online = new HashSet<string>();
            foreach (IServerPlayer p in _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                online.Add(p.PlayerUID);
                Remember(p.PlayerUID, PlayerDisplayName(p));
            }
            foreach (PlayerPolicy policy in _policies.Policies)
                Remember(policy.PlayerUid, policy.LastKnownName);
            foreach (GuideData g in _guides.AllGuides.Values)
                Remember(g?.CreatorUid, g?.CreatorName);

            // ONE PASS for everyone's guide count and voxel total. Asking per row instead walked the whole
            // registry twice per player, so opening this dialog cost (players × guides × 2) — a hitch that
            // grows with the product of two things a busy server has plenty of.
            Dictionary<string, (int Guides, long Voxels)> tally = _guides.TallyByCreator();

            var rows = new List<PlayerRosterEntryDto>(names.Count);
            foreach (var pair in names)
            {
                string uid = pair.Key;
                tally.TryGetValue(uid, out (int Guides, long Voxels) owned);
                rows.Add(new PlayerRosterEntryDto
                {
                    Uid = uid,
                    Name = pair.Value,
                    Online = online.Contains(uid),
                    Jailed = _policies.IsJailed(uid),
                    GuideCount = owned.Guides,
                    VoxelTotal = owned.Voxels,
                    VoxelCapOverride = _policies.VoxelCapOverride(uid),
                    TotalVoxelCapOverride = _policies.PlayerTotalVoxelCapOverride(uid),
                    GuideLimitOverride = _policies.GuideLimitOverride(uid),
                    EffectiveVoxelCap = _policies.EffectiveVoxelCap(uid, _guides.PerGuideVoxelCap),
                    EffectiveTotalVoxelCap = _guides.EffectivePerPlayerTotalVoxelCap(uid),
                    EffectiveGuideLimit = _policies.EffectiveGuideLimit(uid, _guides.MaxGuidesPerPlayer)
                });
            }

            // Online first, then by name: the people an admin can act on right now are the ones they are
            // usually looking for, and alphabetical within that keeps the list stable between refreshes.
            return rows
                .OrderByDescending(r => r.Online)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // ==========================================================================================
        //  Cap-rejection wording
        // ==========================================================================================
        //
        // SUBSTITUTE THE NUMBERS OURSELVES. SendIngameError's message parameter is a LANG KEY, and its
        // trailing arguments are only applied when that key resolves to a translated string. Ours never
        // do — they are English sentences, not keys — so the key is handed back verbatim and every
        // placeholder reached the player as literal text: "your per-guide limit of {0:n0} voxels".
        //
        // Every message here is therefore fully formed before it is sent, and none of these calls passes
        // arguments. If you add another, do the same: an unsubstituted placeholder is not a compile error
        // and looks perfectly fine in the source.
        //
        // Built in one place rather than inline because the immediate and the immense placement paths
        // report the same four refusals, and they had already drifted into two near-identical copies.

        private static string TooLargeText(int voxelCount) =>
            "That guide is too large to render (" + voxelCount.ToString("N0")
            + " voxels). Make it smaller or use a coarser scale.";

        private static string PerGuideCapText(int capLimit) =>
            "That guide exceeds your per-guide limit of " + capLimit.ToString("N0")
            + " voxels. Make it smaller or use a coarser scale.";

        private static string PlayerCapText(int capLimit) =>
            "Your guides would exceed the cumulative limit of " + capLimit.ToString("N0")
            + " voxels. Dispel or shrink one of your guides before adding more.";

        private static string WorldCapText(int voxelCount, int capLimit) =>
            "World voxel budget reached: " + voxelCount.ToString("N0") + " more would pass the "
            + capLimit.ToString("N0")
            + " limit. Dispel some guides ('/layout dispel'), coarsen the scale, or raise totalVoxelCap.";

        private static string EmptyGuideText() =>
            "That guide came out empty — the two points are too close together to make a shape. "
            + "Place it again with more distance between the clicks.";

        private static string GuideCountCapText(GuideOperationResult result) =>
            "Guide limit reached (" + result.VoxelCount.ToString("N0") + " of "
            + result.CapLimit.ToString("N0") + "). Dispel a guide before placing another.";

        // ==========================================================================================
        //  The one cap refusal
        // ==========================================================================================
        //
        // EVERY cap refusal goes through here — placement AND edit. Before v0.4.34 only the two
        // placement paths said anything; the nine edit paths sent the warning packet alone, and that
        // packet has NO SUBSCRIBER on the client, so a refused reshape, toggle or transform sprang back
        // in complete silence with the HUD unchanged.
        //
        // IT HAS TO BE SERVER-SIDE. VoxelCapWarningPacket carries a count and a limit and never says
        // WHICH cap fired, so the client cannot name it. The server can — this is that cascade, which
        // previously existed twice in the placement paths and nowhere else.

        /// <param name="throttle">
        /// True for the CONTINUOUS paths — a refused drag re-fires roughly ten times a second and without
        /// this the chat is unusable. False for a DELIBERATE one-shot act: pressing place and being told
        /// nothing is the bug this whole mechanism exists to fix, and a player who adjusts and tries again
        /// within a few seconds must not be met with silence the second time.
        /// </param>
        private void SendCapRefusal(
            IServerPlayer player, Guid guideId, int voxelCount, int capLimit, bool throttle = true)
        {
            if (player == null) return;

            (string key, string text, VoxelCapKind kind) = CapRefusalMessage(player, voxelCount, capLimit);
            if (!throttle || !CapRefusalThrottled(player.PlayerUID, text))
                player.SendIngameError(key, text);

            _channel.SendPacket(new VoxelCapWarningPacket(guideId, voxelCount, capLimit, kind), player);
        }

        /// <summary>
        /// The guide-COUNT refusal, which is the same event to the player as a voxel one and belongs on the
        /// same HUD row.
        /// </summary>
        /// <remarks>
        /// The chat error existed since the count caps did; the PACKET did not, so for three call sites the
        /// HUD's cap row sat unchanged while chat explained the refusal. Reported in play 2026-08-01.
        /// Deliberately NOT routed through <see cref="SendCapRefusal"/>: that method's whole job is working
        /// out WHICH voxel cap fired by matching limit values, and a guide count is not one of them.
        /// </remarks>
        /// <param name="text">
        /// Overrides the standard wording. The undo/redo path says "can't RESTORE that guide", which is a
        /// different situation to being refused a new one and should not be flattened into the same
        /// sentence — but it is the same refusal as far as the HUD is concerned.
        /// </param>
        private void SendGuideCountRefusal(
            IServerPlayer player, GuideOperationResult result, string text = null)
        {
            if (player == null) return;
            player.SendIngameError("layout-guidecountcap", text ?? GuideCountCapText(result));
            _channel.SendPacket(
                new VoxelCapWarningPacket(
                    result.Guide?.Id ?? Guid.Empty, result.VoxelCount, result.CapLimit,
                    VoxelCapKind.GuideCount),
                player);
        }

        // Which of the four caps was it? Matched by VALUE against the caps in force for this player,
        // because the result only reports the number it hit. Anything unmatched is the world total.
        //
        // THE KIND COMES FROM HERE and nowhere else (protocol 25). This cascade is the only place that
        // can tell the four apart, so the HUD is told what it decided rather than deciding again — a
        // second copy of this matching would be free to disagree with the chat message.
        private (string key, string text, VoxelCapKind kind) CapRefusalMessage(
            IServerPlayer player, int voxelCount, int capLimit)
        {
            if (capLimit >= GuideManager.HardVoxelCeiling)
                return ("layout-toolarge", TooLargeText(voxelCount), VoxelCapKind.HardCeiling);

            int perGuide = _policies.EffectiveVoxelCap(player.PlayerUID, _guides.PerGuideVoxelCap);
            if (perGuide > 0 && capLimit == perGuide)
                return ("layout-overcap", PerGuideCapText(capLimit), VoxelCapKind.PerGuide);

            int perPlayer = _guides.EffectivePerPlayerTotalVoxelCap(player.PlayerUID);
            if (perPlayer > 0 && capLimit == perPlayer)
                return ("layout-playerovercap", PlayerCapText(capLimit), VoxelCapKind.PerPlayer);

            return ("layout-overcap", WorldCapText(voxelCount, capLimit), VoxelCapKind.World);
        }

        // True = refuse this push, the player's last one was moments ago. Records the attempt only when it
        // is allowed, so a client hammering the endpoint cannot push the window out indefinitely.
        private bool PushThrottled(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid)) return false;
            long now = _sapi.World.ElapsedMilliseconds;
            if (_lastPushAt.TryGetValue(playerUid, out long last)
                && now - last < PushCooldownMilliseconds)
                return true;

            _lastPushAt[playerUid] = now;
            return false;
        }

        // True = stay quiet, this player was told the same thing moments ago.
        private bool CapRefusalThrottled(string playerUid, string message)
        {
            if (playerUid == null) return false;
            long now = _sapi.World.ElapsedMilliseconds;
            if (_lastCapRefusal.TryGetValue(playerUid, out (string Message, long At) last)
                && last.Message == message
                && now - last.At < CapRefusalRepeatMilliseconds)
                return true;

            _lastCapRefusal[playerUid] = (message, now);
            return false;
        }

        // The live value of one admin setting, read from the managers rather than from _config, so the
        // confirmation message reports what is actually in force.
        private int AdminSettingValue(LayoutAdminSetting setting) => setting switch
        {
            LayoutAdminSetting.PerGuideVoxelCap => _guides.PerGuideVoxelCap,
            LayoutAdminSetting.PerPlayerTotalVoxelCap => _guides.PerPlayerTotalVoxelCap,
            LayoutAdminSetting.TotalVoxelCap => _guides.TotalVoxelCap,
            LayoutAdminSetting.MaxGuidesPerPlayer => _guides.MaxGuidesPerPlayer,
            LayoutAdminSetting.MaxGuidesWorldWide => _guides.MaxGuidesWorldWide,
            LayoutAdminSetting.AllowClientOnlyMode => _allowClientOnlyMode ? 1 : 0,
            _ => 0
        };

        private static string AdminSettingLabel(LayoutAdminSetting setting) => setting switch
        {
            LayoutAdminSetting.PerGuideVoxelCap => "Voxels per guide",
            LayoutAdminSetting.PerPlayerTotalVoxelCap => "Voxels per player",
            LayoutAdminSetting.TotalVoxelCap => "Voxels in the world",
            LayoutAdminSetting.MaxGuidesPerPlayer => "Guides per player",
            LayoutAdminSetting.MaxGuidesWorldWide => "Guides in the world",
            LayoutAdminSetting.AllowClientOnlyMode => "Private guides",
            _ => setting.ToString()
        };

        private static string DescribeAdminValue(LayoutAdminSetting setting, int value)
        {
            if (setting == LayoutAdminSetting.AllowClientOnlyMode) return value != 0 ? "allowed" : "disallowed";
            return value <= 0 ? "unlimited" : value.ToString("N0");
        }

        // Puts every player currently in client-only mode back to public and tells them so. Their existing
        // private guides are untouched; /layout client push all remains the way to hand them over.
        private void ForceEveryoneOffClientOnly()
        {
            foreach (IServerPlayer p in _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (!_clientOnlyPlayers.Remove(p.PlayerUID)) continue;
                // allowed: TRUE — the thing being allowed is the switch to PUBLIC, which is going through.
                // Sending false here left the client's saved preference on Private (it only updates the
                // preference when the answer is "allowed"), so the slider stayed on Private and the player
                // would have flipped straight back to it next session.
                SendPlacementMode(p, clientOnly: false, allowed: true, updatePreference: true,
                    message: "Private guides have been disabled on this server. New guides will be public; "
                    + "the private guides you already have are still yours.");
            }
        }

        private void CancelPlayerPublicActivity(IServerPlayer player)
        {
            if (player == null) return;
            string uid = player.PlayerUID;
            if (_playersWithPendingImmenseCreate.Contains(uid))
                SendPlacementRejected(player, GuideOpStatus.InvalidArgument);
            CancelPendingImmenseCreate(uid);
            CancelPendingImmenseSculpt(uid);
            if (_drags.TryGetValue(uid, out DragSession session))
                CancelGrabSession(player, session.GuideId);
            ClearPlayerSessionState(uid);
            _undo.ClearPlayer(uid);
        }

        private void ClearPlayerSessionState(string playerUid)
        {
            IReadOnlyList<Guid> freed = _locks.ReleaseAllLocksForPlayer(playerUid);
            foreach (Guid id in freed)
                _channel.BroadcastPacket(new GuideLockStatePacket(id, null));
            _drags.Remove(playerUid);
            if (_draftAnchors.Remove(playerUid))
                _channel.BroadcastPacket(new DraftAnchorRemovePacket(playerUid));
        }

        private string ServerInfoText()
        {
            GuideData largest = _guides.AllGuides.Values
                .OrderByDescending(g => g.CachedVoxelCount)
                .FirstOrDefault();
            var lines = new List<string>
            {
                "Layout server status",
                $"Public guides: {_guides.GuideCount:n0} / {FormatCap(_guides.MaxGuidesWorldWide)}",
                $"Guide voxels: {_guides.TotalVoxelCount:n0} / {FormatCap(_guides.TotalVoxelCap)}",
                $"Per-guide voxel cap: {FormatCap(_guides.PerGuideVoxelCap)} (absolute ceiling {GuideManager.HardVoxelCeiling:n0})",
                $"Per-player cumulative voxel cap: {FormatCap(_guides.PerPlayerTotalVoxelCap)}",
                $"Default per-player guide limit: {FormatCap(_guides.MaxGuidesPerPlayer)}",
                $"Jailed players: {_policies.JailedCount}; custom guide limits: {_policies.CustomGuideLimitCount}; custom per-guide voxel caps: {_policies.CustomVoxelCapCount}; custom cumulative voxel caps: {_policies.CustomPlayerTotalVoxelCapCount}",
                $"Active edit locks: {_locks.ActiveLockCount}"
            };
            if (_backgroundSave != null) lines.Add(_backgroundSave.StatusText());
            if (largest != null)
                lines.Add($"Largest guide: {largest.ShapeType}, {largest.CachedVoxelCount:n0} voxels, {FormatAnchor(largest)}, ID {ShortId(largest.Id)}");
            return string.Join("\n", lines);
        }

        private string PlayerInfoText(PlayerIdentity target)
        {
            List<GuideData> guides = _guides.AllGuides.Values
                .Where(g => g.CreatorUid == target.Uid).ToList();
            long voxels = guides.Sum(g => (long)Math.Max(0, g.CachedVoxelCount));
            int customGuideLimit = _policies.GuideLimitOverride(target.Uid);
            int customVoxelCap = _policies.VoxelCapOverride(target.Uid);
            int customTotalVoxelCap = _policies.PlayerTotalVoxelCapOverride(target.Uid);
            int effectiveGuideLimit = _policies.EffectiveGuideLimit(target.Uid, _guides.MaxGuidesPerPlayer);
            int effectiveVoxelCap = _policies.EffectiveVoxelCap(target.Uid, _guides.PerGuideVoxelCap);
            int effectiveTotalVoxelCap =
                _guides.EffectivePerPlayerTotalVoxelCap(target.Uid);
            return string.Join("\n", new[]
            {
                $"Layout player status: {target.Name}",
                $"Public access: {(_policies.IsJailed(target.Uid) ? "jailed" : "allowed")}",
                $"Public guides: {guides.Count:n0} / {FormatCap(effectiveGuideLimit)}; attributed voxels: {voxels:n0} / {FormatCap(effectiveTotalVoxelCap)}",
                $"Guide-limit override: {(customGuideLimit > 0 ? customGuideLimit.ToString("n0") : "none (server default)")}",
                $"Per-guide voxel cap: {FormatCap(effectiveVoxelCap)}; override: {(customVoxelCap > 0 ? customVoxelCap.ToString("n0") : "none (server default)")}",
                $"Cumulative voxel cap: {FormatCap(effectiveTotalVoxelCap)}; override: {(customTotalVoxelCap > 0 ? customTotalVoxelCap.ToString("n0") : "none (server default)")}",
                $"Connection: {(target.Online != null ? "online" : "offline")}"
            });
        }

        private string TopGuidesText()
        {
            List<GuideData> guides = _guides.AllGuides.Values
                .OrderByDescending(g => g.CachedVoxelCount)
                .ThenBy(g => g.Id)
                .Take(10)
                .ToList();
            if (guides.Count == 0) return "There are no public Layout guides.";
            var text = new StringBuilder("Largest public Layout guides:");
            for (int i = 0; i < guides.Count; i++)
            {
                GuideData g = guides[i];
                text.Append('\n').Append(i + 1).Append(". ")
                    .Append(g.ShapeType).Append(" — ").Append(g.CachedVoxelCount.ToString("n0"))
                    .Append(" voxels — ").Append(string.IsNullOrWhiteSpace(g.CreatorName) ? "Unknown" : g.CreatorName)
                    .Append(" — ").Append(FormatAnchor(g)).Append(" — ID ").Append(ShortId(g.Id));
            }
            return text.ToString();
        }

        private string TopPlayersText()
        {
            var players = _guides.AllGuides.Values
                .GroupBy(g => g.CreatorUid ?? "")
                .Select(group => new
                {
                    Uid = group.Key,
                    Name = group.Select(g => g.CreatorName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Unknown",
                    Guides = group.Count(),
                    Voxels = group.Sum(g => (long)Math.Max(0, g.CachedVoxelCount))
                })
                .OrderByDescending(p => p.Voxels)
                .ThenByDescending(p => p.Guides)
                .Take(10)
                .ToList();
            if (players.Count == 0) return "There are no public Layout guides.";
            var text = new StringBuilder("Highest public Layout usage by creator:");
            for (int i = 0; i < players.Count; i++)
                text.Append('\n').Append(i + 1).Append(". ").Append(players[i].Name)
                    .Append(" — ").Append(players[i].Guides.ToString("n0")).Append(" guides — ")
                    .Append(players[i].Voxels.ToString("n0")).Append(" voxels")
                    .Append(_policies.IsJailed(players[i].Uid) ? " — jailed" : "");
            return text.ToString();
        }

        private bool TryResolvePlayer(string name, out PlayerIdentity identity, out string error)
        {
            identity = default;
            error = null;
            string wanted = name?.Trim();
            if (string.IsNullOrWhiteSpace(wanted))
            {
                error = "A player name is required.";
                return false;
            }

            IServerPlayer online = _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
                .FirstOrDefault(p => string.Equals(p.PlayerName, wanted, StringComparison.OrdinalIgnoreCase));
            if (online != null)
            {
                identity = new PlayerIdentity(online.PlayerUID, PlayerDisplayName(online), online);
                return true;
            }

            var saved = _sapi.PlayerData.GetPlayerDataByLastKnownName(wanted);
            if (saved != null && !string.IsNullOrWhiteSpace(saved.PlayerUID))
            {
                identity = new PlayerIdentity(saved.PlayerUID,
                    string.IsNullOrWhiteSpace(saved.LastKnownPlayername) ? wanted : saved.LastKnownPlayername,
                    null);
                return true;
            }

            PlayerPolicy policy = _policies.Policies.FirstOrDefault(p =>
                string.Equals(p.LastKnownName, wanted, StringComparison.OrdinalIgnoreCase));
            if (policy != null)
            {
                identity = new PlayerIdentity(policy.PlayerUid, policy.LastKnownName, null);
                return true;
            }

            GuideData authored = _guides.AllGuides.Values.FirstOrDefault(g =>
                string.Equals(g.CreatorName, wanted, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(g.CreatorUid));
            if (authored != null)
            {
                identity = new PlayerIdentity(authored.CreatorUid, authored.CreatorName, null);
                return true;
            }

            error = $"No known player named '{wanted}' was found.";
            return false;
        }

        private bool TryResolveGuide(string token, out Guid id, out string error)
        {
            id = Guid.Empty;
            error = null;
            string wanted = token?.Trim();
            if (string.IsNullOrWhiteSpace(wanted) || wanted.Length < 4)
            {
                error = "Provide at least four characters of the guide ID shown by '/layout top guides'.";
                return false;
            }
            if (Guid.TryParse(wanted, out Guid exact) && _guides.HasGuide(exact))
            {
                id = exact;
                return true;
            }
            List<Guid> matches = _guides.AllGuides.Keys
                .Where(candidate => candidate.ToString("N").StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count == 1)
            {
                id = matches[0];
                return true;
            }
            error = matches.Count > 1
                ? $"Guide ID prefix '{wanted}' is ambiguous; provide more characters."
                : $"No public guide with ID '{wanted}' exists.";
            return false;
        }

        private static string FormatCap(long cap) => cap > 0 ? cap.ToString("n0") : "unlimited";
        private static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);
        private static string FormatAnchor(GuideData guide)
        {
            Vec3d anchor = FirstAnchorPos(guide);
            return anchor == null
                ? "anchor unknown"
                : $"anchor ({anchor.X:0.#}, {anchor.Y:0.#}, {anchor.Z:0.#})";
        }

        private readonly struct PlayerIdentity
        {
            public string Uid { get; }
            public string Name { get; }
            public IServerPlayer Online { get; }
            public PlayerIdentity(string uid, string name, IServerPlayer online)
            {
                Uid = uid;
                Name = string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
                Online = online;
            }
        }

        private void StampLastSculptor(IServerPlayer player, GuideData guide, bool broadcastIncremental = true)
        {
            if (player == null || guide == null) return;
            _guides.StampLastSculptor(guide.Id, player.PlayerUID, PlayerDisplayName(player));
            if (broadcastIncremental)
                _channel.BroadcastPacket(new GuideHudMetadataPacket(guide));
        }

        private static string PlayerDisplayName(IServerPlayer player) =>
            string.IsNullOrWhiteSpace(player?.PlayerName) ? "Unknown" : player.PlayerName.Trim();

        // Privilege guard (Module 7): true = reject. Applied at the top of every state-changing handler;
        // deliberately NOT applied to cleanup paths (release, draft-cancel) so a revoked privilege can never
        // strand a lock or a draft anchor.
        private bool DeniedByPrivilege(IServerPlayer fromPlayer)
        {
            if (_policies.IsJailed(fromPlayer?.PlayerUID))
            {
                fromPlayer.SendIngameError("layout-jailed",
                    "An administrator suspended your access to public Layout guides.");
                return true;
            }
            if (_requiredPrivilege == null) return false;
            if (fromPlayer.HasPrivilege(_requiredPrivilege)) return false;

            fromPlayer.SendIngameError("layout-noprivilege",
                "You lack the privilege required to use the Layout tool on this server.");
            return true;
        }

        // ONE GUIDE PER PLAYER (v0.4.36). Grabbing a guide gives up whatever this player had grabbed before,
        // and everyone is told those guides are free again. The shipped client already grabs one at a time,
        // so in normal play this frees nothing and is invisible — but that was a CLIENT convention, and
        // relying on it let a modified client lock every guide in the world and sit on them until it
        // disconnected (GOTCHAS G32). The player's in-flight immense reshape is exempt: that lock belongs to
        // work already running, not to a player holding a guide, and it releases itself when the validator
        // finishes.
        private void ReleaseSupersededLocks(IServerPlayer player, Guid grabbed)
        {
            Guid? retain = _pendingImmenseSculpts.TryGetValue(
                player.PlayerUID, out PendingImmenseSculpt inFlight) ? inFlight.GuideId : (Guid?)null;

            IReadOnlyList<Guid> freed = _locks.ReleaseOtherLocksForPlayer(
                player.PlayerUID, grabbed, retain);
            for (int i = 0; i < freed.Count; i++)
                _channel.BroadcastPacket(new GuideLockStatePacket(freed[i], null));
        }

        // A coordinate that failed GuideBounds. The shipped client cannot produce one — it aims at blocks —
        // so this is either a modified client or a genuinely broken packet. Say so plainly and do nothing
        // else: no state was touched, and nothing is worth logging per packet.
        private void RejectOutOfWorld(IServerPlayer player)
        {
            player?.SendIngameError("layout-outofworld",
                "That guide position is outside the world and was ignored.");
        }

        private static void SendClaimDenied(IServerPlayer player, BlockPos position,
            string message = "Guide crosses protected land; you do not have build permission there")
        {
            if (player == null) return;
            if (position == null)
            {
                player.SendIngameError("layout-claimdenied", message + ".");
                return;
            }

            // Substituted here, not passed as arguments — see the cap-rejection wording remarks above.
            player.SendIngameError("layout-claimdenied",
                message + " at " + position.X + ", " + position.Y + ", " + position.Z + ".");
        }

        // Full-exclusivity gate (Module 7): true = reject, because the guide is edit-locked by someone else.
        // Used by the atomic operations (dispel + the property toggles) — geometry edits have their own,
        // stricter lock checks. An admin passes when the server config allows lock override (the remedy for
        // a lock held indefinitely by a comatose grab). On rejection the requester is (re)told who holds the
        // lock so their client can grey the guide out.
        private bool BlockedByEditLock(IServerPlayer fromPlayer, Guid id)
        {
            string holder = _locks.GetHolder(id);
            if (holder == null || holder == fromPlayer.PlayerUID) return false;
            if (_adminCanOverrideLocks && fromPlayer.HasPrivilege(Privilege.controlserver)) return false;

            _channel.SendPacket(new GuideLockStatePacket(id, holder), fromPlayer);
            fromPlayer.SendIngameError("layout-guidelocked",
                "That guide is being edited by another player right now.");
            return true;
        }

        // Shared tail for a property toggle that did not succeed: cap warning + resync, or a drop if gone.
        private void HandleNonSuccessToggle(IServerPlayer fromPlayer, Guid id, GuideOperationResult result, GuideData live)
        {
            switch (result.Status)
            {
                case GuideOpStatus.RejectedOverCap:
                    SendCapRefusal(fromPlayer, id, result.VoxelCount, result.CapLimit);
                    SendResync(fromPlayer, result.Guide ?? live);
                    break;
                case GuideOpStatus.RejectedClaimAccess:
                    SendClaimDenied(fromPlayer, result.DeniedPosition);
                    SendResync(fromPlayer, result.Guide ?? live);
                    break;
                case GuideOpStatus.GuideNotFound:
                    _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);
                    break;
                default:
                    SendResync(fromPlayer, result.Guide ?? live);
                    break;
            }
        }

        // Send one player the authoritative full state of a guide (used to undo their optimistic local edit).
        private void SendResync(IServerPlayer player, GuideData guide)
        {
            if (guide != null) _channel.SendPacket(new GuideCreatePacket(GuideDataDto.From(guide)), player);
        }

        // Correct a player who acted on a guide they no longer should: full state if it exists, else a drop.
        private void ResyncOrDrop(IServerPlayer player, Guid id)
        {
            if (_guides.TryGetGuide(id, out GuideData g)) SendResync(player, g);
            else _channel.SendPacket(new GuideDeletePacket(id), player);
        }

        private DragSession DragFor(string uid, Guid guideId)
        {
            if (!_drags.TryGetValue(uid, out DragSession s) || s.GuideId != guideId)
            {
                s = new DragSession(guideId);   // editing a different guide starts a fresh session
                _drags[uid] = s;
            }
            return s;
        }

        private static Vec3d ClonePos(Vec3d v) => v == null ? new Vec3d() : new Vec3d(v.X, v.Y, v.Z);

        private static List<ControlPoint> ClonePoints(IReadOnlyList<ControlPoint> points)
        {
            var copy = new List<ControlPoint>(points?.Count ?? 0);
            if (points != null)
                for (int i = 0; i < points.Count; i++)
                    copy.Add(points[i] == null ? new ControlPoint() : points[i].Clone());
            return copy;
        }

        private static bool HasPromotedMarker(
            IReadOnlyList<ControlPoint> before, IReadOnlyList<ControlPoint> after)
        {
            if (before == null || after == null || before.Count != after.Count) return false;
            for (int i = 0; i < before.Count; i++)
                if (before[i].IsLockMarker && before[i].IsLocked &&
                    !after[i].IsLockMarker && after[i].IsLocked)
                    return true;
            return false;
        }

        private static bool SamePosition(Vec3d a, Vec3d b)
        {
            if (a == null || b == null) return false;
            const double eps = 1e-6;
            return Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps && Math.Abs(a.Z - b.Z) <= eps;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_immenseCreateTickId != 0)
            {
                _sapi.Event.UnregisterGameTickListener(_immenseCreateTickId);
                _immenseCreateTickId = 0;
            }
            if (_immenseSculptTickId != 0)
            {
                _sapi.Event.UnregisterGameTickListener(_immenseSculptTickId);
                _immenseSculptTickId = 0;
            }
            _sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            _sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
            if (_activeImmenseCreate != null) _activeImmenseCreate.Cancelled = true;
            if (_activeImmenseSculpt != null) _activeImmenseSculpt.Cancelled = true;
            _immenseCreateQueue.Clear();
            _immenseSculptQueue.Clear();
            _playersWithPendingImmenseCreate.Clear();
            _pendingImmenseSculpts.Clear();
        }
    }
}
