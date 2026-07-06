using System;
using System.Collections.Generic;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records a constraint break (Session 8): undo restores the constraint AND the exact pre-break
    /// control points (a half-circle materialises interior points on break, so the points must travel
    /// with the constraint); redo re-breaks. Snapshot semantics follow DeleteGuideCommand: the stored
    /// list is deep-copied at record time and deep-copied again on restore, so the live guide never
    /// aliases the command's copy.
    /// </summary>
    public sealed class BreakConstraintCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly ShapeConstraint _constraint;                 // what was broken
        private readonly List<ControlPoint> _pointsBefore;            // deep copy, pre-break

        public Guid TargetGuideId => _guideId;

        public BreakConstraintCommand(Guid guideId, ShapeConstraint constraint, List<ControlPoint> pointsBefore)
        {
            _guideId = guideId;
            _constraint = constraint;
            _pointsBefore = new List<ControlPoint>(pointsBefore?.Count ?? 0);
            if (pointsBefore != null)
                foreach (var cp in pointsBefore) _pointsBefore.Add(cp.Clone());
        }

        public bool CanUndo(GuideManager manager) =>
            manager.TryGetGuide(_guideId, out var g) && g.Constraint == ShapeConstraint.None;

        public bool CanRedo(GuideManager manager) =>
            manager.TryGetGuide(_guideId, out var g) && g.Constraint == _constraint;

        public GuideOperationResult Execute(GuideManager manager) => manager.BreakConstraint(_guideId);

        public GuideOperationResult Undo(GuideManager manager) =>
            manager.RestoreConstraint(_guideId, _constraint, _pointsBefore);

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);
    }
}
