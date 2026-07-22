using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
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
    /// THREADING. Everything runs on the server main thread, like the managers — no locking.
    /// </remarks>
    public class ServerNetworkHandler
    {
        private readonly ICoreServerAPI _sapi;
        private readonly IServerNetworkChannel _channel;
        private readonly GuideManager _guides;
        private readonly GuideLockManager _locks;
        private readonly UndoManager _undo;
        private readonly LayoutAdminPolicyManager _policies;

        // Server-config policy (Module 7). Empty/null privilege = everyone may use the tool.
        private readonly string _requiredPrivilege;
        private readonly bool _adminCanOverrideLocks;
        private readonly bool _allowClientOnlyMode;
        private readonly bool _chalkDurabilityEnabled;
        private readonly HashSet<string> _clientOnlyPlayers = new HashSet<string>();

        // v0.2.22: per-player chalk refill-channel PREFERENCES (the player's own setting, reported on join),
        // not server policy. Needed because ItemChalkingPowder's held-interact runs on both sides and the
        // SERVER performs the mutation — without knowing the preference it would refill for a player who
        // had turned the hotbar shortcut off. Absent entry = not opted in (matches the client default).
        private readonly HashSet<string> _hotbarRefillOptIn = new HashSet<string>();
        private const int MaxGuidesPerPush = 100;

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

        public ServerNetworkHandler(
            ICoreServerAPI sapi,
            GuideManager guideManager,
            GuideLockManager lockManager,
            UndoManager undoManager,
            LayoutAdminPolicyManager adminPolicies,
            string requiredPrivilege = null,
            bool adminCanOverrideLocks = true,
            bool allowClientOnlyMode = false,
            bool chalkDurabilityEnabled = true)
        {
            _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
            _guides = guideManager ?? throw new ArgumentNullException(nameof(guideManager));
            _locks = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
            _undo = undoManager ?? throw new ArgumentNullException(nameof(undoManager));
            _policies = adminPolicies ?? throw new ArgumentNullException(nameof(adminPolicies));
            _requiredPrivilege = string.IsNullOrWhiteSpace(requiredPrivilege) ? null : requiredPrivilege.Trim();
            _adminCanOverrideLocks = adminCanOverrideLocks;
            _allowClientOnlyMode = allowClientOnlyMode;
            _chalkDurabilityEnabled = chalkDurabilityEnabled;

            _channel = _sapi.Network.RegisterChannel(LayoutChannel.Name);
            LayoutPackets.RegisterMessageTypes(_channel);

            _channel
                .SetMessageHandler<GuideCreateRequestPacket>((p, x) => WithPlayerVoxelCap(p, () => OnCreateRequest(p, x)))
                .SetMessageHandler<ChalkChargePacket>(OnChalkCharge)
                .SetMessageHandler<ChalkInventoryRefillPacket>(OnInventoryChalkRefill)
                .SetMessageHandler<ChalkRefillPrefsPacket>(OnChalkRefillPrefs)
                .SetMessageHandler<GuideGrabPacket>((p, x) => WithPlayerVoxelCap(p, () => OnGrab(p, x)))
                .SetMessageHandler<GuideReleasePacket>((p, x) => WithPlayerVoxelCap(p, () => OnRelease(p, x)))
                .SetMessageHandler<GuideCancelGrabPacket>((p, x) => WithPlayerVoxelCap(p, () => OnCancelGrab(p, x)))
                .SetMessageHandler<GuideUpdatePacket>((p, x) => WithPlayerVoxelCap(p, () => OnUpdate(p, x)))
                .SetMessageHandler<GuideInsertPointPacket>((p, x) => WithPlayerVoxelCap(p, () => OnInsert(p, x)))
                .SetMessageHandler<GuideDeletePacket>((p, x) => WithPlayerVoxelCap(p, () => OnDelete(p, x)))
                .SetMessageHandler<GuideHidePacket>((p, x) => WithPlayerVoxelCap(p, () => OnHide(p, x)))
                .SetMessageHandler<GuideLockPointPacket>((p, x) => WithPlayerVoxelCap(p, () => OnLockPoint(p, x)))
                .SetMessageHandler<GuideRescalePacket>((p, x) => WithPlayerVoxelCap(p, () => OnRescale(p, x)))
                .SetMessageHandler<GuideSetProjectionPacket>((p, x) => WithPlayerVoxelCap(p, () => OnSetProjection(p, x)))
                .SetMessageHandler<GuideSetFilledPacket>((p, x) => WithPlayerVoxelCap(p, () => OnSetFilled(p, x)))
                .SetMessageHandler<GuideSetWireframePacket>((p, x) => WithPlayerVoxelCap(p, () => OnSetWireframe(p, x)))
                .SetMessageHandler<GuideSetDivisionsPacket>((p, x) => WithPlayerVoxelCap(p, () => OnSetDivisions(p, x)))
                .SetMessageHandler<GuideSetSidesPacket>((p, x) => WithPlayerVoxelCap(p, () => OnSetSides(p, x)))
                .SetMessageHandler<GuideSpringBackPacket>((p, x) => WithPlayerVoxelCap(p, () => OnSpringBack(p, x)))
                .SetMessageHandler<DraftStartPacket>(OnDraftStart)
                .SetMessageHandler<DraftCancelPacket>(OnDraftCancel)
                .SetMessageHandler<UndoRequestPacket>((p, x) => WithPlayerVoxelCap(p, () => OnUndo(p, x)))
                .SetMessageHandler<RedoRequestPacket>((p, x) => WithPlayerVoxelCap(p, () => OnRedo(p, x)))
                .SetMessageHandler<ClientPlacementModeRequestPacket>(OnClientPlacementModeRequest)
                .SetMessageHandler<ClientGuidePushPacket>((p, x) => WithPlayerVoxelCap(p, () => OnClientGuidePush(p, x)));

            _sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            _sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

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
            return TextCommandResult.Success(cap == 0
                ? $"Removed {target.Name}'s custom voxel cap. Effective per-guide cap: {FormatCap(effective)}."
                : $"Set {target.Name}'s per-guide voxel cap to {cap:n0}. World and absolute safety caps still apply.");
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

            if (DeniedByPrivilege(player))
            {
                _channel.SendPacket(new ClientGuidePushResultPacket(
                    Array.Empty<byte[]>(), packet?.Guides?.Length ?? 0,
                    "Your access to public Layout guides is restricted."), player);
                return;
            }

            ClientGuidePushDto[] incoming = packet?.Guides ?? Array.Empty<ClientGuidePushDto>();
            var accepted = new List<byte[]>();
            BlockPos firstClaimDenied = null;
            int rejected = Math.Max(0, incoming.Length - MaxGuidesPerPush);
            int count = Math.Min(incoming.Length, MaxGuidesPerPush);

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
            if (DeniedByPrivilege(fromPlayer)) return;
            if (p?.Start == null || p.End == null || p.Settings == null) return;

            // F5 chalk gate (NO LOCKOUT rule): a kit at 0 chalk blocks NEW placements only — every other
            // operation (edit, dispel, undo) has no gate. The client pre-checks at draft start; this is the
            // authoritative backstop.
            if (ChalkApplies(fromPlayer, out ItemSlot kitSlot)
                && Items.ItemGuideTool.GetChalk(kitSlot.Itemstack) <= 0)
            {
                fromPlayer.SendIngameError("layout-outofchalk",
                    "Out of chalk. Refill the Chalking Kit with Chalking Powder (hold it and right-click).");
                return;
            }

            Vec3d start = p.Start.ToVec3d();
            Vec3d end = p.End.ToVec3d();
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
                    chain.Add(c.ToVec3d());
                }
            }

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
                    // The client pre-checks the PER-GUIDE cap, so a server-side over-cap on CREATE is the
                    // total-voxel budget or the hard scan ceiling. The HUD cap-flash is tied to a guide id
                    // that doesn't exist yet, so it never showed — send a CLEAR ingame error instead
                    // (v0.1.26 fix: previously this rejection was effectively silent). Leave the draft so
                    // the player can shrink it or dispel some guides and retry.
                    if (result.CapLimit >= GuideManager.HardVoxelCeiling)
                        fromPlayer.SendIngameError("layout-toolarge",
                            "That guide is too large to render ({0:n0} voxels). Make it smaller or use a coarser scale.",
                            result.VoxelCount);
                    else if (_policies.EffectiveVoxelCap(fromPlayer.PlayerUID, _guides.PerGuideVoxelCap) > 0
                        && result.CapLimit == _policies.EffectiveVoxelCap(
                            fromPlayer.PlayerUID, _guides.PerGuideVoxelCap))
                        fromPlayer.SendIngameError("layout-overcap",
                            "That guide exceeds your per-guide limit of {0:n0} voxels. Make it smaller or use a coarser scale.",
                            result.CapLimit);
                    else
                        fromPlayer.SendIngameError("layout-overcap",
                            "World voxel budget reached: {0:n0} more would pass the {1:n0} limit. Dispel some guides ('/layout dispel'), coarsen the scale, or raise totalVoxelCap.",
                            result.VoxelCount, result.CapLimit);
                    _channel.SendPacket(
                        new VoxelCapWarningPacket(result.Guide.Id, result.VoxelCount, result.CapLimit),
                        fromPlayer);
                    break;

                case GuideOpStatus.RejectedOverGuideCount:
                    // Per-player or world-wide guide-count cap (server config). Nothing was built; leave the
                    // draft so the player can dispel an old guide and complete this one afterwards.
                    fromPlayer.SendIngameError("layout-guidecountcap",
                        "Guide limit reached ({0} of {1}). Dispel a guide before placing another.",
                        result.VoxelCount, result.CapLimit);
                    break;

                case GuideOpStatus.RejectedClaimAccess:
                    SendClaimDenied(fromPlayer, result.DeniedPosition);
                    break;

                // InvalidArgument: malformed input — ignore.
            }
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
                        if (promotedMarker)
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

            DragSession session = DragFor(uid, id);
            IGuideShape shape = _guides.GetShape(id);

            // ABSORB-OR-BREAK on move (Session 8): dragging a point the constraint cannot absorb — a
            // circle's minor handle — breaks it (one undoable command), and the move then applies to the
            // free parent. Feet/diameter anchors absorb, so they never reach this.
            bool broke = false;
            if (g.Constraint != ShapeConstraint.None && shape != null)
            {
                for (int i = 0; i < p.Edits.Length && !broke; i++)
                {
                    if (!shape.WouldBreakOnMove(p.Edits[i].Index)) continue;
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
            for (int i = 0; i < p.Edits.Length; i++)
            {
                int idx = p.Edits[i].Index;
                Vec3d pos = p.Edits[i].Position != null ? p.Edits[i].Position.ToVec3d() : new Vec3d();
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
                    _channel.SendPacket(
                        new VoxelCapWarningPacket(id, result.VoxelCount, result.CapLimit), fromPlayer);
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

            Vec3d pos = p.Position.ToVec3d();
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
                        _channel.SendPacket(
                            new VoxelCapWarningPacket(id, ins.VoxelCount, ins.CapLimit), fromPlayer);
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
                    _channel.SendPacket(
                        new VoxelCapWarningPacket(id, result.VoxelCount, result.CapLimit), fromPlayer);
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
                _undo.Record(fromPlayer.PlayerUID, new HideGuideCommand(id, oldHidden, p.Hidden));
                if (oldHidden != result.Guide.IsHidden) StampLastSculptor(fromPlayer, result.Guide);
                _channel.BroadcastPacket(new GuideHidePacket(id, p.Hidden));
            }
            else HandleNonSuccessToggle(fromPlayer, id, result, g);
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
                _undo.Record(fromPlayer.PlayerUID, new SetDivisionsCommand(id, oldDivisions, result.Guide.Divisions));
                if (oldDivisions != result.Guide.Divisions) StampLastSculptor(fromPlayer, result.Guide);
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
                            fromPlayer.SendIngameError("layout-guidecountcap",
                                "Can't restore that guide — the guide limit ({0} of {1}) is reached.",
                                blockedResult.VoxelCount, blockedResult.CapLimit);
                            break;

                        case GuideOpStatus.RejectedClaimAccess:
                            SendClaimDenied(fromPlayer, blockedResult.DeniedPosition,
                                "Can't undo/redo that guide because it would enter protected land");
                            break;

                        default:   // over a voxel cap — the classic Blocked case
                            if (blockedResult.Guide != null)
                                _channel.SendPacket(
                                    new VoxelCapWarningPacket(
                                        blockedResult.Guide.Id, blockedResult.VoxelCount, blockedResult.CapLimit),
                                    fromPlayer);
                            break;
                    }
                    break;

                // NothingToApply: nothing to do.
            }
        }

        // ==========================================================================================
        //  Helpers
        // ==========================================================================================

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

        private void CancelPlayerPublicActivity(IServerPlayer player)
        {
            if (player == null) return;
            string uid = player.PlayerUID;
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
                $"Default per-player guide limit: {FormatCap(_guides.MaxGuidesPerPlayer)}",
                $"Jailed players: {_policies.JailedCount}; custom guide limits: {_policies.CustomGuideLimitCount}; custom voxel caps: {_policies.CustomVoxelCapCount}",
                $"Active edit locks: {_locks.ActiveLockCount}"
            };
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
            int effectiveGuideLimit = _policies.EffectiveGuideLimit(target.Uid, _guides.MaxGuidesPerPlayer);
            int effectiveVoxelCap = _policies.EffectiveVoxelCap(target.Uid, _guides.PerGuideVoxelCap);
            return string.Join("\n", new[]
            {
                $"Layout player status: {target.Name}",
                $"Public access: {(_policies.IsJailed(target.Uid) ? "jailed" : "allowed")}",
                $"Public guides: {guides.Count:n0} / {FormatCap(effectiveGuideLimit)}; attributed voxels: {voxels:n0}",
                $"Guide-limit override: {(customGuideLimit > 0 ? customGuideLimit.ToString("n0") : "none (server default)")}",
                $"Per-guide voxel cap: {FormatCap(effectiveVoxelCap)}; override: {(customVoxelCap > 0 ? customVoxelCap.ToString("n0") : "none (server default)")}",
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

        private static void SendClaimDenied(IServerPlayer player, BlockPos position,
            string message = "Guide crosses protected land; you do not have build permission there")
        {
            if (player == null) return;
            if (position == null)
            {
                player.SendIngameError("layout-claimdenied", message + ".");
                return;
            }

            player.SendIngameError("layout-claimdenied",
                message + " at {0}, {1}, {2}.", position.X, position.Y, position.Z);
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
                    _channel.SendPacket(
                        new VoxelCapWarningPacket(id, result.VoxelCount, result.CapLimit), fromPlayer);
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
    }
}
