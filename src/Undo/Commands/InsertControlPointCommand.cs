using System;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records inserting a body point. Stores the guide, the inserted position, and the index it landed at; undo
    /// removes that point, redo re-inserts it at the same spot. The landing index is refreshed each time the point
    /// is (re-)inserted, so a following undo always removes the right one.
    /// </summary>
    public sealed class InsertControlPointCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly Vec3d _position;
        private readonly bool _isLockMarker;
        private int _insertedIndex;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public InsertControlPointCommand(Guid guideId, int insertedIndex, Vec3d insertedPosition,
            bool isLockMarker = false)
        {
            if (insertedPosition == null) throw new ArgumentNullException(nameof(insertedPosition));
            _guideId = guideId;
            _insertedIndex = insertedIndex;
            _position = new Vec3d(insertedPosition.X, insertedPosition.Y, insertedPosition.Z);
            _isLockMarker = isLockMarker;
        }

        // Undo (remove) needs the inserted point still present, unchanged, and removable (a plain body point).
        public bool CanUndo(GuideManager manager)
        {
            if (!manager.TryGetGuide(_guideId, out var g)) return false;
            if (_insertedIndex < 0 || _insertedIndex >= g.ControlPoints.Count) return false;
            ControlPoint cp = g.ControlPoints[_insertedIndex];
            if (cp.IsPhantom || cp.IsAnchor || cp.IsPrimary) return false;
            return GuideCommandHelpers.PositionsMatch(cp.WorldPosition, _position);
        }

        // Redo (re-insert) just needs the guide present.
        public bool CanRedo(GuideManager manager) => manager.HasGuide(_guideId);

        public GuideOperationResult Execute(GuideManager manager)
        {
            IGuideShape shape = manager.GetShape(_guideId);
            float t = shape != null ? shape.GetNearestT(_position) : 0f;
            GuideOperationResult result = manager.InsertControlPoint(
                _guideId, t, _position, _isLockMarker);
            if (result.IsSuccess) _insertedIndex = result.ControlPointIndex;   // keep the landing index fresh
            return result;
        }

        public GuideOperationResult Undo(GuideManager manager) => manager.RemoveControlPoint(_guideId, _insertedIndex);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);
    }
}
