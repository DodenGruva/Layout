using System;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records changing a guide's equal-part division count (Session 9 — the purely visual marks). Stores
    /// the guide and the old/new counts; undo restores the previous count, redo re-applies the new one.
    /// Values recorded post-clamp, so validation always compares against what was actually applied.
    /// </summary>
    public sealed class SetDivisionsCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly int _oldDivisions;
        private readonly int _newDivisions;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public SetDivisionsCommand(Guid guideId, int oldDivisions, int newDivisions)
        {
            _guideId = guideId;
            _oldDivisions = oldDivisions;
            _newDivisions = newDivisions;
        }

        public bool CanUndo(GuideManager manager) => DivisionsAre(manager, _newDivisions);
        public bool CanRedo(GuideManager manager) => DivisionsAre(manager, _oldDivisions);

        public GuideOperationResult Execute(GuideManager manager) => manager.SetDivisions(_guideId, _newDivisions);
        public GuideOperationResult Undo(GuideManager manager) => manager.SetDivisions(_guideId, _oldDivisions);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool DivisionsAre(GuideManager manager, int expected) =>
            manager.TryGetGuide(_guideId, out var g) && g.Divisions == expected;
    }
}
