using System;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records changing a polygon guide's side count (Session 11). Stores the guide and the old/new
    /// counts; undo restores the previous count, redo re-applies the new one. Values recorded post-clamp,
    /// so validation always compares against what was actually applied — the SetDivisionsCommand pattern.
    /// </summary>
    public sealed class SetSidesCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly int _oldSides;
        private readonly int _newSides;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public SetSidesCommand(Guid guideId, int oldSides, int newSides)
        {
            _guideId = guideId;
            _oldSides = oldSides;
            _newSides = newSides;
        }

        public bool CanUndo(GuideManager manager) => SidesAre(manager, _newSides);
        public bool CanRedo(GuideManager manager) => SidesAre(manager, _oldSides);

        public GuideOperationResult Execute(GuideManager manager) => manager.SetSides(_guideId, _newSides);
        public GuideOperationResult Undo(GuideManager manager) => manager.SetSides(_guideId, _oldSides);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool SidesAre(GuideManager manager, int expected) =>
            manager.TryGetGuide(_guideId, out var g) && g.Sides == expected;
    }
}
