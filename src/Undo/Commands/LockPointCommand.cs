using System;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records toggling a control point's locked-constraint flag. Stores the guide, the point index, and the
    /// before/after lock state; undo restores the previous state, redo re-applies the new one.
    /// </summary>
    public sealed class LockPointCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly int _index;
        private readonly bool _before;
        private readonly bool _after;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public LockPointCommand(Guid guideId, int index, bool before, bool after)
        {
            _guideId = guideId;
            _index = index;
            _before = before;
            _after = after;
        }

        public bool CanUndo(GuideManager manager) => LockStateIs(manager, _after);
        public bool CanRedo(GuideManager manager) => LockStateIs(manager, _before);

        public GuideOperationResult Execute(GuideManager manager) => manager.SetPointLocked(_guideId, _index, _after);
        public GuideOperationResult Undo(GuideManager manager) => manager.SetPointLocked(_guideId, _index, _before);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool LockStateIs(GuideManager manager, bool expected)
        {
            if (!manager.TryGetGuide(_guideId, out var g)) return false;
            if (_index < 0 || _index >= g.ControlPoints.Count) return false;
            ControlPoint cp = g.ControlPoints[_index];
            if (cp.IsPhantom) return false;
            return cp.IsLocked == expected;
        }
    }
}
