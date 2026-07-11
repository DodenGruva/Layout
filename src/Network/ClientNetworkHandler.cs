using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Network
{
    /// <summary>
    /// Client side of the Layout protocol: a read-only mirror of server state plus the send methods the held
    /// tool uses to make requests. It receives S→C packets, applies them to the local mirror, and raises
    /// events for the (not-yet-built) renderer and HUD. It never writes server state and makes no
    /// authoritative decision — every change it shows has already been validated and broadcast by the server.
    /// </summary>
    /// <remarks>
    /// THE MIRROR. <see cref="Guides"/> is a <see cref="GuideData"/> per id, an exact copy of the server's
    /// record (the full control-point list, phantoms included, arrives on the wire). Module 5's renderer
    /// reads these, builds a shape over each guide's control-point list, and re-derives phantoms; because the
    /// shape adopts that list by reference, an incremental edit applied here (a moved point, an inserted
    /// point) is reflected the moment the renderer next rebuilds. This handler therefore does no shape math
    /// and depends only on the data model.
    ///
    /// EVENTS. <see cref="GuideAddedOrUpdated"/> fires whenever a guide appears or changes (the renderer
    /// rebuilds that one mesh); <see cref="GuideRemoved"/> when one is dispelled. <see cref="GuidesBulkSynced"/>
    /// fires after a join sync (rebuild everything). Lock state, remote draft anchors, and cap warnings have
    /// their own events for the HUD and tool. All events fire AFTER the mirror is updated, so a handler can
    /// read the new state directly.
    ///
    /// CAPS. <see cref="PerGuideVoxelCap"/> / <see cref="TotalVoxelCap"/> start at the v2 defaults and are
    /// overwritten by the server's real caps on join, so the tool's placement pre-check matches the server.
    /// </remarks>
    public class ClientNetworkHandler
    {
        private readonly ICoreClientAPI _capi;
        private readonly IClientNetworkChannel _channel;

        private readonly Dictionary<Guid, GuideData> _guides = new Dictionary<Guid, GuideData>();
        private readonly Dictionary<Guid, string> _lockHolders = new Dictionary<Guid, string>();
        private readonly Dictionary<string, Vec3d> _remoteDraftAnchors = new Dictionary<string, Vec3d>();

        private int _perGuideVoxelCap = 25000;
        private int _totalVoxelCap = 250000;

        // -- Read-only views for the renderer / HUD / tool ----------------------------------------

        /// <summary>The local guide mirror, by id. Treat as read-only; mutate only by sending requests.</summary>
        public IReadOnlyDictionary<Guid, GuideData> Guides => _guides;

        /// <summary>Guide id → the UID currently editing it. A guide absent here is free to grab.</summary>
        public IReadOnlyDictionary<Guid, string> LockHolders => _lockHolders;

        /// <summary>Player UID → their in-progress draft start anchor, for rendering remote anchor dots.</summary>
        public IReadOnlyDictionary<string, Vec3d> RemoteDraftAnchors => _remoteDraftAnchors;

        /// <summary>The server's active per-guide voxel cap (synced on join). Use for the placement pre-check.</summary>
        public int PerGuideVoxelCap => _perGuideVoxelCap;

        /// <summary>The server's active total voxel cap (synced on join).</summary>
        public int TotalVoxelCap => _totalVoxelCap;

        // -- Events --------------------------------------------------------------------------------

        /// <summary>A guide appeared or changed; the argument is the live mirror record. Rebuild its mesh.</summary>
        public event Action<GuideData> GuideAddedOrUpdated;

        /// <summary>A guide was removed; the argument is its id. Drop its mesh.</summary>
        public event Action<Guid> GuideRemoved;

        /// <summary>A full bulk sync was applied (e.g. on join). Rebuild everything.</summary>
        public event Action GuidesBulkSynced;

        /// <summary>A guide's lock state changed: (guide id, holder UID or null when free).</summary>
        public event Action<Guid, string> LockStateChanged;

        /// <summary>A remote player's draft anchor appeared or moved: (player UID, start position).</summary>
        public event Action<string, Vec3d> RemoteDraftAnchorChanged;

        /// <summary>A remote player's draft anchor was removed: (player UID).</summary>
        public event Action<string> RemoteDraftAnchorRemoved;

        /// <summary>A mutation was refused for exceeding a cap: (guide id, attempted count, cap).</summary>
        public event Action<Guid, int, int> VoxelCapWarningReceived;

        public ClientNetworkHandler(ICoreClientAPI capi)
        {
            _capi = capi ?? throw new ArgumentNullException(nameof(capi));

            _channel = _capi.Network.RegisterChannel(LayoutChannel.Name);
            LayoutPackets.RegisterMessageTypes(_channel);

            _channel
                .SetMessageHandler<GuideBulkSyncPacket>(OnBulkSync)
                .SetMessageHandler<GuideCreatePacket>(OnCreate)
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
                .SetMessageHandler<GuideLockStatePacket>(OnLockState)
                .SetMessageHandler<DraftAnchorBroadcastPacket>(OnDraftAnchorBroadcast)
                .SetMessageHandler<DraftAnchorRemovePacket>(OnDraftAnchorRemove)
                .SetMessageHandler<VoxelCapWarningPacket>(OnCapWarning);
        }

        // ==========================================================================================
        //  Receive: full-state
        // ==========================================================================================

        private void OnBulkSync(GuideBulkSyncPacket p)
        {
            _guides.Clear();
            _lockHolders.Clear();
            _remoteDraftAnchors.Clear();

            if (p.Guides != null)
                foreach (var dto in p.Guides)
                {
                    if (dto == null) continue;
                    GuideData g = dto.ToGuideData();
                    _guides[g.Id] = g;
                }

            _perGuideVoxelCap = p.PerGuideVoxelCap;
            _totalVoxelCap = p.TotalVoxelCap;

            GuidesBulkSynced?.Invoke();
        }

        private void OnCreate(GuideCreatePacket p)
        {
            if (p?.Guide == null) return;
            GuideData g = p.Guide.ToGuideData();
            _guides[g.Id] = g;                 // upsert: also the resync / undo-broadcast path
            GuideAddedOrUpdated?.Invoke(g);
        }

        // ==========================================================================================
        //  Receive: incremental
        // ==========================================================================================

        private void OnUpdate(GuideUpdatePacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g) || p.Edits == null) return;

            foreach (var edit in p.Edits)
            {
                if (edit?.Position == null) continue;
                if (edit.Index < 0 || edit.Index >= g.ControlPoints.Count) continue;
                Vec3d pos = edit.Position.ToVec3d();
                g.ControlPoints[edit.Index].SetPosition(pos.X, pos.Y, pos.Z);
            }
            // Phantoms are stale until the renderer rebuilds the shape and re-derives them — harmless, as
            // nothing reads phantom positions before then.
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnInsert(GuideInsertPointPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g) || p.Position == null) return;

            var cp = new ControlPoint(p.Position.ToVec3d());   // a plain body point (no role flags)
            int idx = p.Index;
            if (idx < 0 || idx > g.ControlPoints.Count) g.ControlPoints.Add(cp);
            else g.ControlPoints.Insert(idx, cp);

            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnDelete(GuideDeletePacket p)
        {
            Guid id = p.GuideId();
            bool removed = _guides.Remove(id);
            _lockHolders.Remove(id);
            if (removed) GuideRemoved?.Invoke(id);
        }

        private void OnHide(GuideHidePacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.IsHidden = p.Hidden;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnLockPoint(GuideLockPointPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            if (p.Index < 0 || p.Index >= g.ControlPoints.Count) return;
            g.ControlPoints[p.Index].IsLocked = p.Locked;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnRescale(GuideRescalePacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.VoxelScale = p.Scale;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnSetProjection(GuideSetProjectionPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.Projection = (ProjectionMode)p.Mode;
            g.Plane = p.ResolvePlane();
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnSetFilled(GuideSetFilledPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.IsFilled = p.Filled;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnSetDivisions(GuideSetDivisionsPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.Divisions = p.Divisions;
            GuideAddedOrUpdated?.Invoke(g);   // renderer repaints the marks via the normal rebuild
        }

        private void OnSetSides(GuideSetSidesPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.Sides = p.Sides;
            GuideAddedOrUpdated?.Invoke(g);   // the polygon re-derives its outline on the rebuild
        }

        // ==========================================================================================
        //  Receive: lock state, drafts, warnings
        // ==========================================================================================

        private void OnLockState(GuideLockStatePacket p)
        {
            Guid id = p.GuideId();
            string holder = string.IsNullOrEmpty(p.HolderUid) ? null : p.HolderUid;
            if (holder == null) _lockHolders.Remove(id);
            else _lockHolders[id] = holder;
            LockStateChanged?.Invoke(id, holder);
        }

        private void OnDraftAnchorBroadcast(DraftAnchorBroadcastPacket p)
        {
            if (string.IsNullOrEmpty(p.PlayerUid) || p.Start == null) return;
            Vec3d start = p.Start.ToVec3d();
            _remoteDraftAnchors[p.PlayerUid] = start;
            RemoteDraftAnchorChanged?.Invoke(p.PlayerUid, start);
        }

        private void OnDraftAnchorRemove(DraftAnchorRemovePacket p)
        {
            if (string.IsNullOrEmpty(p.PlayerUid)) return;
            if (_remoteDraftAnchors.Remove(p.PlayerUid)) RemoteDraftAnchorRemoved?.Invoke(p.PlayerUid);
        }

        private void OnCapWarning(VoxelCapWarningPacket p)
            => VoxelCapWarningReceived?.Invoke(p.GuideId(), p.CurrentCount, p.Cap);

        // ==========================================================================================
        //  Send: the requests the held tool makes (Module 7 wires these to clicks / keybinds)
        // ==========================================================================================

        /// <summary>
        /// Send the completed placement: two foot points + settings (+ Session 11: the SHIFT inversion
        /// flag, the polygon side count, a three-click triangle's apex, and — 0.1.15 — the Free-Shape's
        /// full corner chain + closed flag). The server builds the guide.
        /// </summary>
        public void SendCreateRequest(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType = GuideShapeType.Arch,
            ShapeConstraint constraint = ShapeConstraint.None,
            PlaneAxis shapePlaneAxis = PlaneAxis.Y,
            bool inverted = false, int sides = 0, Vec3d apex = null,
            IReadOnlyList<Vec3d> chain = null, bool closed = false)
        {
            Vec3Dto[] chainDto = null;
            if (chain != null && chain.Count > 0)
            {
                chainDto = new Vec3Dto[chain.Count];
                for (int i = 0; i < chain.Count; i++) chainDto[i] = Vec3Dto.From(chain[i]);
            }
            _channel.SendPacket(new GuideCreateRequestPacket(
                Vec3Dto.From(start), Vec3Dto.From(end), RenderSettingsDto.From(settings),
                (int)shapeType, (int)constraint, (int)shapePlaneAxis,
                inverted, sides, apex == null ? null : Vec3Dto.From(apex),
                chainDto, closed));
        }

        /// <summary>Broadcast the local draft start anchor to other players.</summary>
        public void SendDraftStart(Vec3d start, GuideRenderSettings settings) =>
            _channel.SendPacket(new DraftStartPacket(Vec3Dto.From(start), RenderSettingsDto.From(settings)));

        /// <summary>Cancel the local draft.</summary>
        public void SendDraftCancel() => _channel.SendPacket(new DraftCancelPacket());

        /// <summary>Request the edit lock on a guide.</summary>
        public void SendGrab(Guid guideId) => _channel.SendPacket(new GuideGrabPacket(guideId));

        /// <summary>Release the edit lock on a guide (the server commits the drag's undo entry on receipt).</summary>
        public void SendRelease(Guid guideId) => _channel.SendPacket(new GuideReleasePacket(guideId));

        /// <summary>Cancel (not commit) our in-progress grab — see <see cref="GuideCancelGrabPacket"/>.</summary>
        public void SendCancelGrab(Guid guideId) => _channel.SendPacket(new GuideCancelGrabPacket(guideId));

        /// <summary>Move a single control point (the common grab-and-drag case).</summary>
        public void SendMovePoint(Guid guideId, int index, Vec3d position) =>
            _channel.SendPacket(new GuideUpdatePacket(
                guideId, new[] { new ControlPointEditDto(index, Vec3Dto.From(position)) }));

        /// <summary>Move several control points at once.</summary>
        public void SendMovePoints(Guid guideId, IReadOnlyList<(int index, Vec3d position)> edits)
        {
            if (edits == null || edits.Count == 0) return;
            var dto = new ControlPointEditDto[edits.Count];
            for (int i = 0; i < edits.Count; i++)
                dto[i] = new ControlPointEditDto(edits[i].index, Vec3Dto.From(edits[i].position));
            _channel.SendPacket(new GuideUpdatePacket(guideId, dto));
        }

        /// <summary>Insert a body point at the clicked position (the server derives the index).</summary>
        /// <summary>
        /// Body insert. Default: the insert that starts a grab (adopted via OnGuideAddedOrUpdated).
        /// With <paramref name="locked"/>: a lock-in-place insert — the point is created on-curve, born
        /// locked, and no grab follows (the server never leaves us holding the edit lock).
        /// </summary>
        public void SendInsertPoint(Guid guideId, Vec3d position, bool locked = false) =>
            _channel.SendPacket(new GuideInsertPointPacket(guideId, -1, Vec3Dto.From(position), locked));

        /// <summary>Dispel a guide.</summary>
        public void SendDelete(Guid guideId) => _channel.SendPacket(new GuideDeletePacket(guideId));

        /// <summary>Toggle a guide's hidden flag.</summary>
        public void SendHide(Guid guideId, bool hidden) => _channel.SendPacket(new GuideHidePacket(guideId, hidden));

        /// <summary>Set a control point's locked-constraint flag.</summary>
        public void SendLockPoint(Guid guideId, int index, bool locked) =>
            _channel.SendPacket(new GuideLockPointPacket(guideId, index, locked));

        /// <summary>Change a guide's voxel scale.</summary>
        public void SendRescale(Guid guideId, int scale) => _channel.SendPacket(new GuideRescalePacket(guideId, scale));

        /// <summary>Set a guide's projection mode and plane.</summary>
        public void SendSetProjection(Guid guideId, ProjectionMode mode, ProjectionPlane plane) =>
            _channel.SendPacket(new GuideSetProjectionPacket(
                guideId, (int)mode, (int)plane.FlattenedAxis, plane.PlaneOffset));

        /// <summary>Toggle a guide's filled flag.</summary>
        public void SendSetFilled(Guid guideId, bool filled) => _channel.SendPacket(new GuideSetFilledPacket(guideId, filled));

        /// <summary>Session 9: set a guide's equal-part division marks (purely visual).</summary>
        public void SendSetDivisions(Guid guideId, int divisions) => _channel.SendPacket(new GuideSetDivisionsPacket(guideId, divisions));

        /// <summary>Session 11: set a polygon guide's side count.</summary>
        public void SendSetSides(Guid guideId, int sides) => _channel.SendPacket(new GuideSetSidesPacket(guideId, sides));

        /// <summary>Session 11: spring a guide back to its as-placed form (SHIFT+click).</summary>
        public void SendSpringBack(Guid guideId) => _channel.SendPacket(new GuideSpringBackPacket(guideId));

        /// <summary>Undo the local player's most recent action.</summary>
        public void SendUndo() => _channel.SendPacket(new UndoRequestPacket());

        /// <summary>Redo the local player's most recently undone action.</summary>
        public void SendRedo() => _channel.SendPacket(new RedoRequestPacket());
    }
}
