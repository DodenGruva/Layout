using System;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Removes a passive lock-in-place marker when it is unlocked. Undo recreates the marker at the same
    /// curve position and relocks it; redo removes it again. This prevents invisible stale markers from
    /// attracting later lock clicks or corrupting control-point order.
    /// </summary>
    public sealed class RemoveLockMarkerCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly Vec3d _position;
        private int _index;

        public Guid TargetGuideId => _guideId;

        public RemoveLockMarkerCommand(Guid guideId, int index, Vec3d position)
        {
            if (position == null) throw new ArgumentNullException(nameof(position));
            _guideId = guideId;
            _index = index;
            _position = new Vec3d(position.X, position.Y, position.Z);
        }

        public bool CanUndo(GuideManager manager)
        {
            if (!manager.TryGetGuide(_guideId, out GuideData guide)) return false;
            for (int i = 0; i < guide.ControlPoints.Count; i++)
            {
                ControlPoint point = guide.ControlPoints[i];
                if (point.IsLockMarker &&
                    GuideCommandHelpers.PositionsMatch(point.WorldPosition, _position))
                    return false;
            }
            return true;
        }

        public bool CanRedo(GuideManager manager)
        {
            if (!manager.TryGetGuide(_guideId, out GuideData guide)) return false;
            if (_index < 0 || _index >= guide.ControlPoints.Count) return false;
            ControlPoint point = guide.ControlPoints[_index];
            return point.IsLockMarker && point.IsLocked &&
                GuideCommandHelpers.PositionsMatch(point.WorldPosition, _position);
        }

        public GuideOperationResult Execute(GuideManager manager) =>
            manager.RemoveControlPoint(_guideId, _index);

        public GuideOperationResult Undo(GuideManager manager)
        {
            IGuideShape shape = manager.GetShape(_guideId);
            float t = shape != null ? shape.GetNearestT(_position) : 0f;
            GuideOperationResult inserted = manager.InsertControlPoint(
                _guideId, t, _position, isLockMarker: true);
            if (!inserted.IsSuccess) return inserted;

            _index = inserted.ControlPointIndex;
            return manager.SetPointLocked(_guideId, _index, true);
        }

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);
    }
}
