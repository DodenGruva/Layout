using System;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records toggling a guide's hidden flag. Stores the guide and the old/new visibility; undo restores the
    /// previous state, redo re-applies the new one.
    /// </summary>
    public sealed class HideGuideCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly bool _oldHidden;
        private readonly bool _newHidden;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public HideGuideCommand(Guid guideId, bool oldHidden, bool newHidden)
        {
            _guideId = guideId;
            _oldHidden = oldHidden;
            _newHidden = newHidden;
        }

        public bool CanUndo(GuideManager manager) => HiddenIs(manager, _newHidden);
        public bool CanRedo(GuideManager manager) => HiddenIs(manager, _oldHidden);

        public GuideOperationResult Execute(GuideManager manager) => manager.SetHidden(_guideId, _newHidden);
        public GuideOperationResult Undo(GuideManager manager) => manager.SetHidden(_guideId, _oldHidden);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool HiddenIs(GuideManager manager, bool expected) =>
            manager.TryGetGuide(_guideId, out var g) && g.IsHidden == expected;
    }
}
