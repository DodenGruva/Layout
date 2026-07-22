using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Network;
using Layout.Shapes;
using Layout.Systems;
using Layout.Undo.Commands;

namespace Layout.Client
{
    /// <summary>
    /// Local authority used when the connected server has no Layout network channel.
    /// It applies operations through the same GuideManager and feeds accepted changes back through the
    /// client's normal packet-apply methods, keeping the mirror, renderer, HUD, and GUI on one path.
    /// </summary>
    public sealed class LocalGuideAuthority
    {
        private readonly ICoreClientAPI _capi;
        private readonly GuideManager _guides;
        private readonly UndoManager _undo;

        private readonly Action<GuideCreatePacket> _applyFull;
        private readonly Action<GuideDeletePacket> _applyDelete;
        private readonly Action<GuideHidePacket> _applyHide;
        private readonly Action<GuideLockPointPacket> _applyLockPoint;
        private readonly Action<GuideRescalePacket> _applyRescale;
        private readonly Action<GuideSetProjectionPacket> _applyProjection;
        private readonly Action<GuideSetFilledPacket> _applyFilled;
        private readonly Action<GuideSetWireframePacket> _applyWireframe;
        private readonly Action<GuideSetDivisionsPacket> _applyDivisions;
        private readonly Action<GuideSetSidesPacket> _applySides;
        private readonly Action<GuideHudMetadataPacket> _applyHudMetadata;
        private readonly Action<GuideLockStatePacket> _applyLockState;
        private readonly Action<VoxelCapWarningPacket> _applyCapWarning;

        // Client-only mode has one editor, so it needs no contention manager. It still mirrors the server's
        // drag session so release/cancel and undo have exactly the same gesture semantics.
        private sealed class DragSession
        {
            public Guid GuideId;
            public readonly Dictionary<int, Vec3d> Origins = new Dictionary<int, Vec3d>();
            public ShapeConstraint OriginConstraint;
            public List<ControlPoint> OriginPoints;
            public int OriginVoxelCount = -1;
            public int InsertedIndex = -1;
            public SoftPointFlow SoftFlow;

            public DragSession(Guid guideId) { GuideId = guideId; }
        }

        private Guid _heldGuideId = Guid.Empty;
        private DragSession _drag;

        private string PlayerUid => _capi.World?.Player?.PlayerUID ?? "layout-local-player";
        private string PlayerName => string.IsNullOrWhiteSpace(_capi.World?.Player?.PlayerName)
            ? "Unknown" : _capi.World.Player.PlayerName;

        public LocalGuideAuthority(
            ICoreClientAPI capi,
            Action<GuideCreatePacket> applyFull,
            Action<GuideDeletePacket> applyDelete,
            Action<GuideHidePacket> applyHide,
            Action<GuideLockPointPacket> applyLockPoint,
            Action<GuideRescalePacket> applyRescale,
            Action<GuideSetProjectionPacket> applyProjection,
            Action<GuideSetFilledPacket> applyFilled,
            Action<GuideSetWireframePacket> applyWireframe,
            Action<GuideSetDivisionsPacket> applyDivisions,
            Action<GuideSetSidesPacket> applySides,
            Action<GuideHudMetadataPacket> applyHudMetadata,
            Action<GuideLockStatePacket> applyLockState,
            Action<VoxelCapWarningPacket> applyCapWarning)
        {
            _capi = capi ?? throw new ArgumentNullException(nameof(capi));
            _applyFull = applyFull ?? throw new ArgumentNullException(nameof(applyFull));
            _applyDelete = applyDelete ?? throw new ArgumentNullException(nameof(applyDelete));
            _applyHide = applyHide ?? throw new ArgumentNullException(nameof(applyHide));
            _applyLockPoint = applyLockPoint ?? throw new ArgumentNullException(nameof(applyLockPoint));
            _applyRescale = applyRescale ?? throw new ArgumentNullException(nameof(applyRescale));
            _applyProjection = applyProjection ?? throw new ArgumentNullException(nameof(applyProjection));
            _applyFilled = applyFilled ?? throw new ArgumentNullException(nameof(applyFilled));
            _applyWireframe = applyWireframe ?? throw new ArgumentNullException(nameof(applyWireframe));
            _applyDivisions = applyDivisions ?? throw new ArgumentNullException(nameof(applyDivisions));
            _applySides = applySides ?? throw new ArgumentNullException(nameof(applySides));
            _applyHudMetadata = applyHudMetadata ?? throw new ArgumentNullException(nameof(applyHudMetadata));
            _applyLockState = applyLockState ?? throw new ArgumentNullException(nameof(applyLockState));
            _applyCapWarning = applyCapWarning ?? throw new ArgumentNullException(nameof(applyCapWarning));

            IGuidePersistence persistence;
            string storagePath = null;
            try
            {
                var worldPersistence = new ClientWorldGuidePersistence(_capi);
                persistence = worldPersistence;
                storagePath = worldPersistence.FilePath;
            }
            catch (Exception e)
            {
                persistence = new TransientGuidePersistence();
                _capi.Logger.Warning(
                    "[Layout] Client-only guide persistence is unavailable; guides will last for this session only: {0}",
                    e.Message);
            }

            _guides = new GuideManager(
                persistence,
                new BlockAccessorGuideProbe(() => _capi.World?.BlockAccessor),
                _capi.Logger,
                perGuideVoxelCap: 0,
                totalVoxelCap: 0,
                maxGuidesPerPlayer: 0,
                maxGuidesWorldWide: 0);
            _guides.Load();
            _undo = new UndoManager(_guides);

            if (storagePath != null)
                _capi.Logger.Notification(
                    "[Layout] Client-only guide storage: {0} ({1} guide(s) loaded).",
                    storagePath, _guides.AllGuides.Count);
        }

        public GuideBulkSyncPacket CreateBulkSyncPacket()
        {
            GuideDataDto[] guides = _guides.AllGuides.Values
                .Select(GuideDataDto.From)
                .ToArray();
            return new GuideBulkSyncPacket(guides, GuideManager.HardVoxelCeiling, 0);
        }

        public ClientGuidePushDto[] CreatePushDtos()
        {
            return _guides.AllGuides.Values.Select(ClientGuidePushDto.From).ToArray();
        }

        public void RemovePublishedGuides(IEnumerable<Guid> acceptedIds)
        {
            if (acceptedIds == null) return;
            bool removedAny = false;
            foreach (Guid id in acceptedIds)
            {
                if (_guides.DeleteGuide(id).Status != GuideOpStatus.Success) continue;
                DropHeldGuide(id);
                _applyDelete(new GuideDeletePacket(id));
                removedAny = true;
            }
            if (removedAny) _undo.ClearPlayer(PlayerUid);
        }

        /// <summary>
        /// Dispels every local guide when <paramref name="playerPosition"/> is null; otherwise mirrors the
        /// server command's Chebyshev chunk-radius rule using each guide's first real anchor.
        /// </summary>
        public int DispelGuides(Vec3d playerPosition, int radius)
        {
            const int chunkSize = 32;
            int playerChunkX = 0;
            int playerChunkZ = 0;
            if (playerPosition != null)
            {
                playerChunkX = (int)Math.Floor(playerPosition.X / chunkSize);
                playerChunkZ = (int)Math.Floor(playerPosition.Z / chunkSize);
            }

            var targets = new List<Guid>();
            foreach (var pair in _guides.AllGuides)
            {
                if (playerPosition == null)
                {
                    targets.Add(pair.Key);
                    continue;
                }

                Vec3d anchor = FirstAnchorPosition(pair.Value);
                if (anchor == null) continue;
                int guideChunkX = (int)Math.Floor(anchor.X / chunkSize);
                int guideChunkZ = (int)Math.Floor(anchor.Z / chunkSize);
                if (Math.Abs(guideChunkX - playerChunkX) <= radius
                    && Math.Abs(guideChunkZ - playerChunkZ) <= radius)
                    targets.Add(pair.Key);
            }

            int removed = 0;
            foreach (Guid id in targets)
            {
                if (_guides.DeleteGuide(id).Status != GuideOpStatus.Success) continue;
                DropHeldGuide(id);
                _applyDelete(new GuideDeletePacket(id));
                removed++;
            }

            if (removed > 0) _undo.ClearPlayer(PlayerUid);
            return removed;
        }

        /// <summary>Returns the created guide, or null on rejection — the F5 chalk charge and the
        /// placement effects both key off it, so a rejected (over-cap) placement costs and emits nothing.</summary>
        public GuideData Create(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType, ShapeConstraint constraint, PlaneAxis shapePlaneAxis,
            bool inverted, int sides, Vec3d apex, IReadOnlyList<Vec3d> chain, bool closed,
            Vec3d rim = null, bool flatSideAligned = false)
        {
            GuideOperationResult result = _guides.CreateGuide(
                start, end, settings, shapeType, constraint, shapePlaneAxis, PlayerUid, PlayerName,
                apex, inverted, sides, chain, closed, rim, flatSideAligned);

            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new CreateGuideCommand(result.Guide));
                ApplyFull(result.Guide);
                return result.Guide;
            }

            HandleFailure(Guid.Empty, result);
            return null;
        }

        public void Grab(Guid id)
        {
            if (!TryGet(id, out GuideData guide)) return;
            if (_heldGuideId != Guid.Empty && _heldGuideId != id) CancelActiveDrag();

            _heldGuideId = id;
            _drag = new DragSession(id)
            {
                OriginConstraint = guide.Constraint,
                OriginPoints = SnapshotPoints(guide),
                OriginVoxelCount = guide.CachedVoxelCount
            };
            _applyLockState(new GuideLockStatePacket(id, PlayerUid));
        }

        public void Release(Guid id)
        {
            if (_heldGuideId != id) return;

            bool visibleChange = false;

            if (_drag != null && _drag.GuideId == id && _guides.TryGetGuide(id, out GuideData guide))
            {
                bool removedInsert = false;
                if (_drag.InsertedIndex >= 0 && _drag.InsertedIndex < guide.ControlPoints.Count)
                {
                    bool moved = _drag.Origins.TryGetValue(_drag.InsertedIndex, out Vec3d origin)
                        && !SamePosition(guide.ControlPoints[_drag.InsertedIndex].WorldPosition, origin);
                    if (!moved)
                    {
                        GuideOperationResult removed = _guides.RemoveControlPoint(id, _drag.InsertedIndex);
                        removedInsert = removed.IsSuccess;
                        if (removedInsert) ApplyFull(removed.Guide);
                    }
                }

                if (!removedInsert)
                {
                    bool promotedMarker = HasPromotedMarker(
                        _drag.OriginPoints, guide.ControlPoints);
                    visibleChange = promotedMarker || _drag.OriginConstraint != guide.Constraint;
                    if (promotedMarker)
                    {
                        _undo.Record(PlayerUid, new SpringBackCommand(
                            id, _drag.OriginConstraint, _drag.OriginPoints,
                            guide.Constraint, guide.ControlPoints));
                    }
                    else foreach (var pair in _drag.Origins)
                    {
                        int index = pair.Key;
                        if (index < 0 || index >= guide.ControlPoints.Count) continue;
                        Vec3d current = guide.ControlPoints[index].WorldPosition;
                        if (!SamePosition(current, pair.Value))
                        {
                            visibleChange = true;
                            _undo.Record(PlayerUid,
                                new MoveControlPointCommand(id, index, pair.Value, current));
                        }
                    }
                }
                visibleChange |= _drag.OriginConstraint != guide.Constraint;
            }

            _drag = null;
            // The client preview writes directly to its mirror while the local GuideManager remains
            // authoritative. Always restate that authoritative state when the gesture ends, including
            // after an over-cap final move was rejected.
            if (_guides.TryGetGuide(id, out GuideData authoritative))
            {
                if (visibleChange) StampLastSculptor(authoritative, publishIncremental: false);
                ApplyFull(authoritative);
            }
            ReleaseHeldGuide(id);
        }

        public void CancelGrab(Guid id)
        {
            if (_heldGuideId != id) return;

            bool mutated = false;
            GuideData guide = null;
            if (_drag != null && _drag.GuideId == id && _guides.TryGetGuide(id, out guide))
            {
                if (_drag.OriginPoints != null)
                {
                    mutated = _guides.RestoreConstraint(
                        id, _drag.OriginConstraint, _drag.OriginPoints,
                        _drag.OriginVoxelCount).IsSuccess;
                }
                else if (_drag.InsertedIndex >= 0)
                {
                    mutated = _guides.RemoveControlPoint(id, _drag.InsertedIndex).IsSuccess;
                }
                else if (_drag.Origins.Count > 0)
                {
                    foreach (var pair in _drag.Origins)
                    {
                        if (pair.Key < 0 || pair.Key >= guide.ControlPoints.Count) continue;
                        if (SamePosition(guide.ControlPoints[pair.Key].WorldPosition, pair.Value)) continue;
                        GuideOperationResult restoreResult = _guides.UpdateControlPoints(
                            id, new[] { new ControlPointEdit(pair.Key, ClonePos(pair.Value)) });
                        mutated |= restoreResult.IsSuccess;
                    }
                }
            }

            _drag = null;
            if (_guides.TryGetGuide(id, out GuideData restored)) ApplyFull(restored);
            ReleaseHeldGuide(id);
        }

        public void CancelActiveDrag()
        {
            if (_heldGuideId != Guid.Empty) CancelGrab(_heldGuideId);
        }

        public void MovePoints(Guid id, IReadOnlyList<(int index, Vec3d position)> edits)
        {
            if (_heldGuideId != id || edits == null || edits.Count == 0) return;
            if (!TryGet(id, out GuideData guide))
            {
                ReleaseHeldGuide(id);
                return;
            }

            DragSession session = DragFor(id);
            IGuideShape shape = _guides.GetShape(id);

            if (guide.Constraint != ShapeConstraint.None && shape != null)
            {
                for (int i = 0; i < edits.Count; i++)
                {
                    if (!shape.WouldBreakOnMove(edits[i].index)) continue;
                    var command = new BreakConstraintCommand(id, guide.Constraint, guide.ControlPoints);
                    if (_guides.BreakConstraint(id).IsSuccess)
                        _undo.Record(PlayerUid, command);
                    break;
                }
            }

            var composed = new List<ControlPointEdit>(edits.Count);
            for (int i = 0; i < edits.Count; i++)
            {
                int index = edits[i].index;
                Vec3d position = ClonePos(edits[i].position);
                composed.Add(new ControlPointEdit(index, position));

                if (guide.ShapeType == GuideShapeType.Arch && guide.Constraint == ShapeConstraint.None
                    && index >= 0 && index < guide.ControlPoints.Count)
                {
                    session.SoftFlow = session.SoftFlow ?? SoftPointFlow.Capture(guide.ControlPoints, index);
                    if (session.SoftFlow != null && session.SoftFlow.HasWork)
                        foreach (var (softIndex, softPosition) in
                                 session.SoftFlow.ComputeReflowEdits(guide.ControlPoints, index, position))
                            composed.Add(new ControlPointEdit(softIndex, softPosition));
                }
            }

            foreach (ControlPointEdit edit in composed)
            {
                if (edit.Index >= 0 && edit.Index < guide.ControlPoints.Count
                    && !session.Origins.ContainsKey(edit.Index))
                    session.Origins[edit.Index] = ClonePos(guide.ControlPoints[edit.Index].WorldPosition);
            }

            GuideOperationResult result = _guides.UpdateControlPoints(id, composed);
            if (result.IsSuccess) ApplyFull(result.Guide);
            else HandleFailure(id, result);
        }

        public void Insert(Guid id, Vec3d position, bool locked)
        {
            if (position == null || !TryGet(id, out GuideData guide)) return;
            if (_heldGuideId != Guid.Empty && _heldGuideId != id) CancelActiveDrag();

            ShapeConstraint insertOriginConstraint = guide.Constraint;
            List<ControlPoint> insertOriginPoints = SnapshotPoints(guide);
            int insertOriginVoxelCount = guide.CachedVoxelCount;

            _heldGuideId = id;
            _applyLockState(new GuideLockStatePacket(id, PlayerUid));

            if (guide.Constraint != ShapeConstraint.None)
            {
                var command = new BreakConstraintCommand(id, guide.Constraint, guide.ControlPoints);
                if (_guides.BreakConstraint(id).IsSuccess)
                    _undo.Record(PlayerUid, command);
            }

            IGuideShape shape = _guides.GetShape(id);
            float t = shape != null ? shape.GetNearestT(position) : 0f;

            if (locked)
            {
                Vec3d curvePosition = shape != null ? shape.GetPointAt(t) : ClonePos(position);
                bool marker = guide.ShapeType == GuideShapeType.Arch;
                // Passive Arch markers do not participate in the spline, so keep the exact rendered voxel
                // the player selected. Projecting back to the mathematical curve could claim its neighbor.
                Vec3d insertPosition = marker ? ClonePos(position) : curvePosition;
                GuideOperationResult inserted = _guides.InsertControlPoint(id, t, insertPosition, marker);
                if (inserted.IsSuccess)
                {
                    int index = inserted.ControlPointIndex;
                    GuideOperationResult lockResult = _guides.SetPointLocked(id, index, true);
                    if (lockResult.IsSuccess)
                    {
                        _undo.Record(PlayerUid,
                            new InsertControlPointCommand(id, index, insertPosition, marker));
                        _undo.Record(PlayerUid, new LockPointCommand(id, index, false, true));
                        StampLastSculptor(lockResult.Guide, publishIncremental: false);
                        ApplyFull(lockResult.Guide);
                    }
                    else HandleFailure(id, lockResult);
                }
                else HandleFailure(id, inserted);

                ReleaseHeldGuide(id);
                return;
            }

            GuideOperationResult result = _guides.InsertControlPoint(id, t, position);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid,
                    new InsertControlPointCommand(id, result.ControlPointIndex, position));
                _drag = new DragSession(id)
                {
                    InsertedIndex = result.ControlPointIndex,
                    OriginConstraint = insertOriginConstraint,
                    OriginPoints = insertOriginPoints,
                    OriginVoxelCount = insertOriginVoxelCount
                };
                ApplyFull(result.Guide);
            }
            else
            {
                HandleFailure(id, result);
                ReleaseHeldGuide(id);
            }
        }

        public void Delete(Guid id)
        {
            if (!_guides.TryGetGuide(id, out GuideData guide))
            {
                _applyDelete(new GuideDeletePacket(id));
                return;
            }

            var command = new DeleteGuideCommand(guide);
            GuideOperationResult result = _guides.DeleteGuide(id);
            if (result.IsSuccess)
            {
                DropHeldGuide(id);
                _undo.Record(PlayerUid, command);
                _applyDelete(new GuideDeletePacket(id));
            }
        }

        public void SetHidden(Guid id, bool hidden)
        {
            if (!TryGet(id, out GuideData guide)) return;
            bool before = guide.IsHidden;
            GuideOperationResult result = _guides.SetHidden(id, hidden);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new HideGuideCommand(id, before, result.Guide.IsHidden));
                if (before != result.Guide.IsHidden) StampLastSculptor(result.Guide);
                _applyHide(new GuideHidePacket(id, result.Guide.IsHidden));
            }
            else HandleFailure(id, result);
        }

        public void SetPointLocked(Guid id, int index, bool locked)
        {
            if (!TryGet(id, out GuideData guide)) return;
            if (index < 0 || index >= guide.ControlPoints.Count) { ApplyFull(guide); return; }

            ControlPoint target = guide.ControlPoints[index];
            if (!locked && target.IsLockMarker)
            {
                var command = new RemoveLockMarkerCommand(id, index, target.WorldPosition);
                GuideOperationResult removed = _guides.RemoveControlPoint(id, index);
                if (removed.IsSuccess)
                {
                    _undo.Record(PlayerUid, command);
                    StampLastSculptor(removed.Guide, publishIncremental: false);
                    ApplyFull(removed.Guide);
                }
                else HandleFailure(id, removed);
                return;
            }

            bool before = target.IsLocked;
            GuideOperationResult result = _guides.SetPointLocked(id, index, locked);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new LockPointCommand(id, index, before, result.Guide.ControlPoints[index].IsLocked));
                if (before != result.Guide.ControlPoints[index].IsLocked) StampLastSculptor(result.Guide);
                _applyLockPoint(new GuideLockPointPacket(id, index, result.Guide.ControlPoints[index].IsLocked));
            }
            else HandleFailure(id, result);
        }

        public void Rescale(Guid id, int scale)
        {
            if (!TryGet(id, out GuideData guide)) return;
            int before = guide.VoxelScale;
            GuideOperationResult result = _guides.Rescale(id, scale);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new RescaleGuideCommand(id, before, result.Guide.VoxelScale));
                if (before != result.Guide.VoxelScale) StampLastSculptor(result.Guide);
                _applyRescale(new GuideRescalePacket(id, result.Guide.VoxelScale));
            }
            else HandleFailure(id, result);
        }

        public void SetProjection(Guid id, ProjectionMode mode, ProjectionPlane plane)
        {
            if (!TryGet(id, out GuideData guide)) return;
            ProjectionMode beforeMode = guide.Projection;
            ProjectionPlane beforePlane = guide.Plane;
            bool bakes = beforeMode == ProjectionMode.Surface && mode == ProjectionMode.Volumetric;
            List<ControlPoint> pointsBefore = bakes ? SnapshotPoints(guide) : null;

            GuideOperationResult result = _guides.SetProjection(id, mode, plane);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid,
                    new SetProjectionCommand(id, beforeMode, beforePlane, mode, plane, pointsBefore));
                bool changed = beforeMode != result.Guide.Projection
                    || beforePlane.FlattenedAxis != result.Guide.Plane.FlattenedAxis
                    || beforePlane.PlaneOffset != result.Guide.Plane.PlaneOffset;
                if (changed) StampLastSculptor(result.Guide, publishIncremental: !bakes);
                if (bakes) ApplyFull(result.Guide);
                else _applyProjection(new GuideSetProjectionPacket(
                    id, (int)result.Guide.Projection, (int)result.Guide.Plane.FlattenedAxis,
                    result.Guide.Plane.PlaneOffset));
            }
            else HandleFailure(id, result);
        }

        public void SetFilled(Guid id, bool filled)
        {
            if (!TryGet(id, out GuideData guide)) return;
            bool before = guide.IsFilled;
            GuideOperationResult result = _guides.SetFilled(id, filled);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new SetFilledCommand(id, before, result.Guide.IsFilled));
                if (before != result.Guide.IsFilled) StampLastSculptor(result.Guide);
                _applyFilled(new GuideSetFilledPacket(id, result.Guide.IsFilled));
            }
            else HandleFailure(id, result);
        }

        public void SetWireframe(Guid id, bool wireframe)
        {
            if (!TryGet(id, out GuideData guide)) return;
            bool before = guide.IsWireframe;
            GuideOperationResult result = _guides.SetWireframe(id, wireframe);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new SetWireframeCommand(id, before, result.Guide.IsWireframe));
                if (before != result.Guide.IsWireframe) StampLastSculptor(result.Guide);
                _applyWireframe(new GuideSetWireframePacket(id, result.Guide.IsWireframe));
            }
            else HandleFailure(id, result);
        }

        public void SetDivisions(Guid id, int divisions)
        {
            if (!TryGet(id, out GuideData guide)) return;
            int before = guide.Divisions;
            GuideOperationResult result = _guides.SetDivisions(id, divisions);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new SetDivisionsCommand(id, before, result.Guide.Divisions));
                if (before != result.Guide.Divisions) StampLastSculptor(result.Guide);
                _applyDivisions(new GuideSetDivisionsPacket(id, result.Guide.Divisions));
            }
            else HandleFailure(id, result);
        }

        public void SetSides(Guid id, int sides)
        {
            if (!TryGet(id, out GuideData guide)) return;
            int before = guide.Sides;
            GuideOperationResult result = _guides.SetSides(id, sides);
            if (!result.IsSuccess) { HandleFailure(id, result); return; }
            if (result.Guide.Sides == before) return;

            _undo.Record(PlayerUid, new SetSidesCommand(id, before, result.Guide.Sides));
            StampLastSculptor(result.Guide);
            _applySides(new GuideSetSidesPacket(id, result.Guide.Sides));
        }

        public void SpringBack(Guid id)
        {
            if (!TryGet(id, out GuideData guide)) return;
            if (guide.OriginalControlPoints == null || guide.OriginalControlPoints.Count == 0)
            {
                Error("layout-nooriginal", "This guide has no recorded original form.");
                return;
            }

            ShapeConstraint beforeConstraint = guide.Constraint;
            List<ControlPoint> beforePoints = SnapshotPoints(guide);
            GuideOperationResult result = _guides.SpringBackToOriginal(id);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new SpringBackCommand(
                    id, beforeConstraint, beforePoints,
                    result.Guide.Constraint, result.Guide.ControlPoints));
                StampLastSculptor(result.Guide, publishIncremental: false);
                ApplyFull(result.Guide);
            }
            else HandleFailure(id, result);
        }

        public void Undo() => ApplyUndoRedo(_undo.Undo(PlayerUid));
        public void Redo() => ApplyUndoRedo(_undo.Redo(PlayerUid));

        private void ApplyUndoRedo(UndoRedoOutcome outcome)
        {
            if (!outcome.Applied)
            {
                if (outcome.Status == UndoRedoStatus.Blocked)
                    HandleFailure(outcome.Result.Guide?.Id ?? Guid.Empty, outcome.Result);
                return;
            }

            GuideData guide = outcome.Result.Guide;
            if (guide != null && _guides.HasGuide(guide.Id))
            {
                StampLastSculptor(guide, publishIncremental: false);
                ApplyFull(guide);
            }
            else if (guide != null) _applyDelete(new GuideDeletePacket(guide.Id));
        }

        private void StampLastSculptor(GuideData guide, bool publishIncremental = true)
        {
            if (guide == null) return;
            _guides.StampLastSculptor(guide.Id, PlayerUid, PlayerName);
            if (publishIncremental)
                _applyHudMetadata(new GuideHudMetadataPacket(guide));
        }

        private bool TryGet(Guid id, out GuideData guide)
        {
            if (_guides.TryGetGuide(id, out guide)) return true;
            _applyDelete(new GuideDeletePacket(id));
            return false;
        }

        private DragSession DragFor(Guid id)
        {
            if (_drag == null || _drag.GuideId != id) _drag = new DragSession(id);
            return _drag;
        }

        private void ReleaseHeldGuide(Guid id)
        {
            if (_heldGuideId != id) return;
            _heldGuideId = Guid.Empty;
            _applyLockState(new GuideLockStatePacket(id, null));
        }

        private void DropHeldGuide(Guid id)
        {
            if (_heldGuideId != id) return;
            _drag = null;
            ReleaseHeldGuide(id);
        }

        private void HandleFailure(Guid id, GuideOperationResult result)
        {
            if (result.Status == GuideOpStatus.RejectedOverCap)
            {
                // UpdateControlPoints rolls the authoritative guide back before returning this result.
                // Correct the optimistic client preview immediately instead of leaving an oversized,
                // non-renderable mirror around until another operation happens to refresh it.
                if (result.Guide != null) ApplyFull(result.Guide);
                Guid warningId = result.Guide?.Id ?? id;
                _applyCapWarning(new VoxelCapWarningPacket(warningId, result.VoxelCount, result.CapLimit));
                Error("layout-toolarge",
                    $"That guide is too large to render ({result.VoxelCount:n0} voxels). Make it smaller or use a coarser scale.");
            }
            else if (result.Status == GuideOpStatus.GuideNotFound)
            {
                _applyDelete(new GuideDeletePacket(id));
            }
            else if (result.Guide != null)
            {
                ApplyFull(result.Guide);
            }
        }

        private void ApplyFull(GuideData guide) =>
            _applyFull(new GuideCreatePacket(GuideDataDto.From(guide)));

        private static List<ControlPoint> SnapshotPoints(GuideData guide)
        {
            var copy = new List<ControlPoint>(guide.ControlPoints.Count);
            foreach (ControlPoint point in guide.ControlPoints)
                copy.Add(point == null ? new ControlPoint() : point.Clone());
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

        private static Vec3d FirstAnchorPosition(GuideData guide)
        {
            if (guide?.ControlPoints == null) return null;
            foreach (ControlPoint point in guide.ControlPoints)
                if (point != null && !point.IsPhantom && point.WorldPosition != null)
                    return point.WorldPosition;
            return null;
        }

        private static Vec3d ClonePos(Vec3d position) => position == null
            ? new Vec3d()
            : new Vec3d(position.X, position.Y, position.Z);

        private static bool SamePosition(Vec3d first, Vec3d second)
        {
            if (first == null || second == null) return false;
            const double epsilon = 1e-6;
            return Math.Abs(first.X - second.X) <= epsilon
                && Math.Abs(first.Y - second.Y) <= epsilon
                && Math.Abs(first.Z - second.Z) <= epsilon;
        }

        private void Error(string code, string message) => _capi.TriggerIngameError(this, code, message);
    }
}
