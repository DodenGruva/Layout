using System;
using System.Collections.Generic;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records changing a guide's projection mode and plane together. Stores the guide and the old/new
    /// mode+plane pairs; undo restores the previous pair, redo re-applies the new one.
    /// </summary>
    public sealed class SetProjectionCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly ProjectionMode _oldMode;
        private readonly ProjectionPlane _oldPlane;
        private readonly ProjectionMode _newMode;
        private readonly ProjectionPlane _newPlane;
        // Pre-change point snapshot, present ONLY when the change baked the flattened positions into the
        // points (a Surface→Volumetric switch; see GuideManager.SetProjection). Deep-copied at record time.
        private readonly List<ControlPoint> _pointsBefore;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public SetProjectionCommand(
            Guid guideId,
            ProjectionMode oldMode, ProjectionPlane oldPlane,
            ProjectionMode newMode, ProjectionPlane newPlane,
            List<ControlPoint> pointsBefore = null)
        {
            _guideId = guideId;
            _oldMode = oldMode;
            _oldPlane = oldPlane;
            _newMode = newMode;
            _newPlane = newPlane;
            if (pointsBefore != null)
            {
                _pointsBefore = new List<ControlPoint>(pointsBefore.Count);
                foreach (var cp in pointsBefore) _pointsBefore.Add(cp.Clone());
            }
        }

        public bool CanUndo(GuideManager manager) => ProjectionIs(manager, _newMode, _newPlane);
        public bool CanRedo(GuideManager manager) => ProjectionIs(manager, _oldMode, _oldPlane);

        // Execute/Redo re-run the projection change through the manager, which re-bakes on a
        // Surface→Volumetric switch by itself. Undo restores the mode/plane and then, when the original
        // change baked point positions, the exact pre-bake points on top (order matters: the restore must
        // come AFTER the projection flip, and a flip back TO Surface never bakes, so nothing re-mangles it).
        public GuideOperationResult Execute(GuideManager manager) => manager.SetProjection(_guideId, _newMode, _newPlane);

        public GuideOperationResult Undo(GuideManager manager)
        {
            GuideOperationResult result = manager.SetProjection(_guideId, _oldMode, _oldPlane);
            if (result.Status != GuideOpStatus.Success || _pointsBefore == null) return result;
            return manager.RestoreControlPoints(_guideId, _pointsBefore);
        }

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool ProjectionIs(GuideManager manager, ProjectionMode mode, ProjectionPlane plane)
        {
            if (!manager.TryGetGuide(_guideId, out var g)) return false;
            return g.Projection == mode && g.Plane.Equals(plane);
        }
    }
}
