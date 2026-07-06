using System;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records moving one control point. Stores the guide, the point index, and the before/after positions; undo
    /// moves it back, redo moves it forward again.
    /// </summary>
    public sealed class MoveControlPointCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly int _index;
        private readonly Vec3d _before;
        private readonly Vec3d _after;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public MoveControlPointCommand(Guid guideId, int index, Vec3d before, Vec3d after)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            _guideId = guideId;
            _index = index;
            _before = new Vec3d(before.X, before.Y, before.Z);
            _after = new Vec3d(after.X, after.Y, after.Z);
        }

        // Applicable only if the guide exists, the index is a real movable point, and the point is still where
        // this command left it for the direction in question (otherwise another player moved it — skip).
        public bool CanUndo(GuideManager manager) => PointIsAt(manager, _after);
        public bool CanRedo(GuideManager manager) => PointIsAt(manager, _before);

        public GuideOperationResult Execute(GuideManager manager) => Apply(manager, _after);
        public GuideOperationResult Undo(GuideManager manager) => Apply(manager, _before);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private GuideOperationResult Apply(GuideManager manager, Vec3d position) =>
            manager.UpdateControlPoints(_guideId, new[] { new ControlPointEdit(_index, position) });

        private bool PointIsAt(GuideManager manager, Vec3d expected)
        {
            if (!manager.TryGetGuide(_guideId, out var g)) return false;
            if (_index < 0 || _index >= g.ControlPoints.Count) return false;
            ControlPoint cp = g.ControlPoints[_index];
            if (cp.IsPhantom) return false;
            return GuideCommandHelpers.PositionsMatch(cp.WorldPosition, expected);
        }
    }
}
