using System;
using System.Collections.Generic;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records a SHIFT spring-back (Session 11): the guide's control points + constraint were rewritten
    /// wholesale back to the as-placed snapshot. Stores independent deep copies of the pre- and
    /// post-spring state; undo restores the distorted form, redo re-applies the pristine one — both
    /// through <see cref="GuideManager.RestoreConstraint"/>, the wholesale-rewrite seam.
    /// </summary>
    public sealed class SpringBackCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly ShapeConstraint _beforeConstraint;
        private readonly List<ControlPoint> _beforePoints;
        private readonly ShapeConstraint _afterConstraint;
        private readonly List<ControlPoint> _afterPoints;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public SpringBackCommand(
            Guid guideId,
            ShapeConstraint beforeConstraint, List<ControlPoint> beforePoints,
            ShapeConstraint afterConstraint, List<ControlPoint> afterPoints)
        {
            _guideId = guideId;
            _beforeConstraint = beforeConstraint;
            _beforePoints = CloneList(beforePoints);
            _afterConstraint = afterConstraint;
            _afterPoints = CloneList(afterPoints);
        }

        private static List<ControlPoint> CloneList(List<ControlPoint> points)
        {
            var copy = new List<ControlPoint>(points?.Count ?? 0);
            if (points != null)
                foreach (var cp in points) copy.Add(cp == null ? new ControlPoint() : cp.Clone());
            return copy;
        }

        // The loose shared-guide guard: the guide must still exist. Point-for-point matching would make
        // the command uselessly brittle (any later grab invalidates it); a stale spring-back landing on a
        // further-edited guide simply restores the recorded form, which is what the player asked to unwind.
        public bool CanUndo(GuideManager manager) => manager.HasGuide(_guideId);
        public bool CanRedo(GuideManager manager) => manager.HasGuide(_guideId);

        public GuideOperationResult Execute(GuideManager manager) =>
            manager.RestoreConstraint(_guideId, _afterConstraint, _afterPoints);

        public GuideOperationResult Undo(GuideManager manager) =>
            manager.RestoreConstraint(_guideId, _beforeConstraint, _beforePoints);

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);
    }
}
