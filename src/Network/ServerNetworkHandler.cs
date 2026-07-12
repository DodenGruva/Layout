using System;
using System.Collections.Generic;
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

        // Server-config policy (Module 7). Empty/null privilege = everyone may use the tool.
        private readonly string _requiredPrivilege;
        private readonly bool _adminCanOverrideLocks;

        // Per-player in-progress drag: the guide being edited and each moved point's pre-drag origin.
        // Used to coalesce a drag into a single undo entry on release — and, since Session 8, to service
        // GuideCancelGrabPacket (restore origins, or remove the point when the grab began as an insert).
        private sealed class DragSession
        {
            public Guid GuideId;
            public readonly Dictionary<int, Vec3d> Origins = new Dictionary<int, Vec3d>();

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
            string requiredPrivilege = null,
            bool adminCanOverrideLocks = true)
        {
            _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
            _guides = guideManager ?? throw new ArgumentNullException(nameof(guideManager));
            _locks = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
            _undo = undoManager ?? throw new ArgumentNullException(nameof(undoManager));
            _requiredPrivilege = string.IsNullOrWhiteSpace(requiredPrivilege) ? null : requiredPrivilege.Trim();
            _adminCanOverrideLocks = adminCanOverrideLocks;

            _channel = _sapi.Network.RegisterChannel(LayoutChannel.Name);
            LayoutPackets.RegisterMessageTypes(_channel);

            _channel
                .SetMessageHandler<GuideCreateRequestPacket>(OnCreateRequest)
                .SetMessageHandler<GuideGrabPacket>(OnGrab)
                .SetMessageHandler<GuideReleasePacket>(OnRelease)
                .SetMessageHandler<GuideCancelGrabPacket>(OnCancelGrab)
                .SetMessageHandler<GuideUpdatePacket>(OnUpdate)
                .SetMessageHandler<GuideInsertPointPacket>(OnInsert)
                .SetMessageHandler<GuideDeletePacket>(OnDelete)
                .SetMessageHandler<GuideHidePacket>(OnHide)
                .SetMessageHandler<GuideLockPointPacket>(OnLockPoint)
                .SetMessageHandler<GuideRescalePacket>(OnRescale)
                .SetMessageHandler<GuideSetProjectionPacket>(OnSetProjection)
                .SetMessageHandler<GuideSetFilledPacket>(OnSetFilled)
                .SetMessageHandler<GuideSetDivisionsPacket>(OnSetDivisions)
                .SetMessageHandler<GuideSetSidesPacket>(OnSetSides)
                .SetMessageHandler<GuideSpringBackPacket>(OnSpringBack)
                .SetMessageHandler<DraftStartPacket>(OnDraftStart)
                .SetMessageHandler<DraftCancelPacket>(OnDraftCancel)
                .SetMessageHandler<UndoRequestPacket>(OnUndo)
                .SetMessageHandler<RedoRequestPacket>(OnRedo);

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
                .WithDescription("Layout mod admin commands.")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("dispel")
                    .WithDescription("Dispel guides. 'all' clears the whole world; a number clears within that chunk radius of you.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("all-or-radius"))
                    .HandleWith(OnDispelCommand)
                .EndSubCommand();
        }

        private TextCommandResult OnDispelCommand(TextCommandCallingArgs args)
        {
            string arg = (args[0] as string)?.Trim().ToLowerInvariant();

            if (arg == "all")
            {
                int n = DispelGuides(null, 0);
                return TextCommandResult.Success($"Dispelled all {n} Layout guide(s) in the world.");
            }

            if (int.TryParse(arg, out int radius) && radius >= 0)
            {
                if (args.Caller.Player is not IServerPlayer p)
                    return TextCommandResult.Error("A radius dispel must be run by a player (use '/dispel all' from the console).");
                int n = DispelGuides(p, radius);
                return TextCommandResult.Success($"Dispelled {n} Layout guide(s) within {radius} chunk(s).");
            }

            return TextCommandResult.Error("Usage: /dispel all   OR   /dispel <chunk radius>");
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
                if (_guides.DeleteGuide(id).Status != GuideOpStatus.Success) continue;
                _locks.ClearLock(id);                             // force-free any editor's lock; it's gone
                _channel.BroadcastPacket(new GuideDeletePacket(id));
                removed++;
            }
            return removed;
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
            // 1) Bulk sync: every guide + the caps the server actually enforces.
            var all = new List<GuideDataDto>(_guides.AllGuides.Count);
            foreach (var g in _guides.AllGuides.Values) all.Add(GuideDataDto.From(g));
            _channel.SendPacket(
                new GuideBulkSyncPacket(all.ToArray(), _guides.PerGuideVoxelCap, _guides.TotalVoxelCap),
                player);

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

            if (_draftAnchors.Remove(uid))
                _channel.BroadcastPacket(new DraftAnchorRemovePacket(uid));
        }

        // ==========================================================================================
        //  Creation + drafts
        // ==========================================================================================

        private void OnCreateRequest(IServerPlayer fromPlayer, GuideCreateRequestPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            if (p?.Start == null || p.End == null || p.Settings == null) return;

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
                start, end, settings, shapeType, constraint, planeAxis, fromPlayer.PlayerUID,
                p.Apex?.ToVec3d(), p.Inverted, p.Sides, chain, p.Closed);
            switch (result.Status)
            {
                case GuideOpStatus.Success:
                    _undo.Record(fromPlayer.PlayerUID, new CreateGuideCommand(result.Guide));
                    _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(result.Guide)));
                    // The placement is done — drop this player's draft anchor for everyone else.
                    if (_draftAnchors.Remove(fromPlayer.PlayerUID))
                        _channel.BroadcastPacket(new DraftAnchorRemovePacket(fromPlayer.PlayerUID), fromPlayer);
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

                // InvalidArgument: malformed input — ignore.
            }
        }

        private void OnDraftStart(IServerPlayer fromPlayer, DraftStartPacket p)
        {
            if (DeniedByPrivilege(fromPlayer)) return;
            if (p?.Start == null) return;
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
            // Everyone learns the holder; the requester learns whether the grab was granted or denied.
            _channel.BroadcastPacket(new GuideLockStatePacket(id, outcome.HolderUid));
        }

        private void OnRelease(IServerPlayer fromPlayer, GuideReleasePacket p)
        {
            Guid id = p.GuideId();
            string uid = fromPlayer.PlayerUID;

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
                        foreach (var pair in session.Origins)
                        {
                            int idx = pair.Key;
                            Vec3d origin = pair.Value;
                            if (idx < 0 || idx >= g.ControlPoints.Count) continue;
                            Vec3d current = g.ControlPoints[idx].WorldPosition;
                            if (!SamePosition(current, origin))
                                _undo.Record(uid, new MoveControlPointCommand(id, idx, origin, current));
                        }
                    }
                }
                _drags.Remove(uid);
            }

            if (_locks.ReleaseLock(id, uid))
                _channel.BroadcastPacket(new GuideLockStatePacket(id, null));
        }

        // Session-8 right-click-cancel: the anti-release. Where OnRelease COMMITS the drag (one undo entry),
        // this discards it — dragged points snap back to their pre-drag origins, or, if the grab began as a
        // body insert, the inserted point is removed outright (insert + grab were one gesture). No undo
        // command is recorded either way. The InsertControlPointCommand recorded at insert time goes stale
        // on removal and self-cleans via the validate-then-apply discard, per the settled undo model.
        private void OnCancelGrab(IServerPlayer fromPlayer, GuideCancelGrabPacket p)
        {
            Guid id = p.GuideId();
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
                if (session.InsertedIndex >= 0)
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
                    if (_guides.BreakConstraint(id).Status == GuideOpStatus.Success)
                    {
                        _undo.Record(uid, breakCmd);
                        broke = true;
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

            GuideOperationResult result = _guides.UpdateControlPoints(id, composed.ToArray());
            switch (result.Status)
            {
                case GuideOpStatus.Success:
                    // Authoritative — every client (including the mover, keeping its mirror exact) applies
                    // it. A constraint break changed more than positions, so that case sends full state.
                    if (broke && _guides.TryGetGuide(id, out GuideData gPostMove))
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

            // ABSORB-OR-BREAK (Session 8): a body insert is a grab no constraint can absorb — an arbitrary
            // interpolation point has no place on a perfect half-circle. Break first (undoable as one
            // command carrying the pre-break points), then insert into the free parent shape. The single
            // full-state broadcast after the insert carries both changes atomically, so the client's
            // adopt-as-grab handshake sees only the final point list.
            bool broke = false;
            if (_guides.TryGetGuide(id, out GuideData gPre) && gPre.Constraint != ShapeConstraint.None)
            {
                var breakCmd = new BreakConstraintCommand(id, gPre.Constraint, gPre.ControlPoints);
                if (_guides.BreakConstraint(id).Status == GuideOpStatus.Success)
                {
                    _undo.Record(uid, breakCmd);
                    broke = true;
                }
            }

            IGuideShape shape = _guides.GetShape(id);
            float t = shape != null ? shape.GetNearestT(pos) : 0f;

            // LOCK-IN-PLACE flavour (Session 8): "lock any point of the guide". The point is created ON
            // the curve (the targeting chord's position would dent the shape), born locked, and the edit
            // lock we acquired above is released immediately — no grab follows this insert.
            if (p.Locked)
            {
                Vec3d curvePos = shape != null ? shape.GetPointAt(t) : pos;
                GuideOperationResult ins = _guides.InsertControlPoint(id, t, curvePos);
                if (ins.Status == GuideOpStatus.Success)
                {
                    int idx = ins.ControlPointIndex;
                    _guides.SetPointLocked(id, idx, true);
                    // Two undo steps, honestly: first Ctrl+Z unlocks, second removes the point.
                    _undo.Record(uid, new InsertControlPointCommand(id, idx, curvePos));
                    _undo.Record(uid, new LockPointCommand(id, idx, false, true));
                    // One full-state broadcast carries the new point AND its locked flag together.
                    if (_guides.TryGetGuide(id, out GuideData withLock))
                        _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(withLock)));
                }
                else
                {
                    if (ins.Status == GuideOpStatus.RejectedOverCap)
                        _channel.SendPacket(
                            new VoxelCapWarningPacket(id, ins.VoxelCount, ins.CapLimit), fromPlayer);
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

            bool before = g.ControlPoints[p.Index].IsLocked;
            GuideOperationResult result = _guides.SetPointLocked(id, p.Index, p.Locked);
            if (result.Status == GuideOpStatus.Success)
            {
                _undo.Record(fromPlayer.PlayerUID, new LockPointCommand(id, p.Index, before, p.Locked));
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
                _channel.BroadcastPacket(new GuideSetFilledPacket(id, p.Filled));
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
                        _channel.BroadcastPacket(new GuideCreatePacket(GuideDataDto.From(affected)));
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

        // Privilege guard (Module 7): true = reject. Applied at the top of every state-changing handler;
        // deliberately NOT applied to cleanup paths (release, draft-cancel) so a revoked privilege can never
        // strand a lock or a draft anchor.
        private bool DeniedByPrivilege(IServerPlayer fromPlayer)
        {
            if (_requiredPrivilege == null) return false;
            if (fromPlayer.HasPrivilege(_requiredPrivilege)) return false;

            fromPlayer.SendIngameError("layout-noprivilege",
                "You lack the privilege required to use the Layout tool on this server.");
            return true;
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
                    SendResync(fromPlayer, live);
                    break;
                case GuideOpStatus.GuideNotFound:
                    _channel.SendPacket(new GuideDeletePacket(id), fromPlayer);
                    break;
                default:
                    SendResync(fromPlayer, live);
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

        private static bool SamePosition(Vec3d a, Vec3d b)
        {
            if (a == null || b == null) return false;
            const double eps = 1e-6;
            return Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps && Math.Abs(a.Z - b.Z) <= eps;
        }
    }
}
