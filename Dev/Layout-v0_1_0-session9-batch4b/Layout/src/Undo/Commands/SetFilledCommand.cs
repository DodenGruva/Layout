using System;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records toggling a guide's filled flag (hollow vs filled). Stores the guide and the old/new state; undo
    /// restores the previous state, redo re-applies the new one.
    /// </summary>
    public sealed class SetFilledCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly bool _oldFilled;
        private readonly bool _newFilled;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public SetFilledCommand(Guid guideId, bool oldFilled, bool newFilled)
        {
            _guideId = guideId;
            _oldFilled = oldFilled;
            _newFilled = newFilled;
        }

        public bool CanUndo(GuideManager manager) => FilledIs(manager, _newFilled);
        public bool CanRedo(GuideManager manager) => FilledIs(manager, _oldFilled);

        public GuideOperationResult Execute(GuideManager manager) => manager.SetFilled(_guideId, _newFilled);
        public GuideOperationResult Undo(GuideManager manager) => manager.SetFilled(_guideId, _oldFilled);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool FilledIs(GuideManager manager, bool expected) =>
            manager.TryGetGuide(_guideId, out var g) && g.IsFilled == expected;
    }
}
