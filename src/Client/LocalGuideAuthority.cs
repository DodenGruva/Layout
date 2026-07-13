using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Network;
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
        private readonly Action<GuideSetDivisionsPacket> _applyDivisions;
        private readonly Action<GuideSetSidesPacket> _applySides;
        private readonly Action<VoxelCapWarningPacket> _applyCapWarning;

        private string PlayerUid => _capi.World?.Player?.PlayerUID ?? "layout-local-player";

        public LocalGuideAuthority(
            ICoreClientAPI capi,
            Action<GuideCreatePacket> applyFull,
            Action<GuideDeletePacket> applyDelete,
            Action<GuideHidePacket> applyHide,
            Action<GuideLockPointPacket> applyLockPoint,
            Action<GuideRescalePacket> applyRescale,
            Action<GuideSetProjectionPacket> applyProjection,
            Action<GuideSetFilledPacket> applyFilled,
            Action<GuideSetDivisionsPacket> applyDivisions,
            Action<GuideSetSidesPacket> applySides,
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
            _applyDivisions = applyDivisions ?? throw new ArgumentNullException(nameof(applyDivisions));
            _applySides = applySides ?? throw new ArgumentNullException(nameof(applySides));
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
                _applyDelete(new GuideDeletePacket(id));
                removed++;
            }

            if (removed > 0) _undo.ClearPlayer(PlayerUid);
            return removed;
        }

        public void Create(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType, ShapeConstraint constraint, PlaneAxis shapePlaneAxis,
            bool inverted, int sides, Vec3d apex, IReadOnlyList<Vec3d> chain, bool closed)
        {
            GuideOperationResult result = _guides.CreateGuide(
                start, end, settings, shapeType, constraint, shapePlaneAxis, PlayerUid,
                apex, inverted, sides, chain, closed);

            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new CreateGuideCommand(result.Guide));
                ApplyFull(result.Guide);
                return;
            }

            HandleFailure(Guid.Empty, result);
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
                _applyHide(new GuideHidePacket(id, result.Guide.IsHidden));
            }
            else HandleFailure(id, result);
        }

        public void SetPointLocked(Guid id, int index, bool locked)
        {
            if (!TryGet(id, out GuideData guide)) return;
            if (index < 0 || index >= guide.ControlPoints.Count) { ApplyFull(guide); return; }
            bool before = guide.ControlPoints[index].IsLocked;
            GuideOperationResult result = _guides.SetPointLocked(id, index, locked);
            if (result.IsSuccess)
            {
                _undo.Record(PlayerUid, new LockPointCommand(id, index, before, result.Guide.ControlPoints[index].IsLocked));
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
                _applyFilled(new GuideSetFilledPacket(id, result.Guide.IsFilled));
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
            if (guide != null && _guides.HasGuide(guide.Id)) ApplyFull(guide);
            else if (guide != null) _applyDelete(new GuideDeletePacket(guide.Id));
        }

        private bool TryGet(Guid id, out GuideData guide)
        {
            if (_guides.TryGetGuide(id, out guide)) return true;
            _applyDelete(new GuideDeletePacket(id));
            return false;
        }

        private void HandleFailure(Guid id, GuideOperationResult result)
        {
            if (result.Status == GuideOpStatus.RejectedOverCap)
            {
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

        private static Vec3d FirstAnchorPosition(GuideData guide)
        {
            if (guide?.ControlPoints == null) return null;
            foreach (ControlPoint point in guide.ControlPoints)
                if (point != null && !point.IsPhantom && point.WorldPosition != null)
                    return point.WorldPosition;
            return null;
        }

        private void Error(string code, string message) => _capi.TriggerIngameError(this, code, message);
    }
}
