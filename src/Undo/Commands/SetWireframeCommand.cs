using System;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>Records switching a 3D guide between its shell and structural wireframe.</summary>
    public sealed class SetWireframeCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly bool _oldWireframe;
        private readonly bool _newWireframe;

        public Guid TargetGuideId => _guideId;

        public SetWireframeCommand(Guid guideId, bool oldWireframe, bool newWireframe)
        {
            _guideId = guideId;
            _oldWireframe = oldWireframe;
            _newWireframe = newWireframe;
        }

        public bool CanUndo(GuideManager manager) => StateIs(manager, _newWireframe);
        public bool CanRedo(GuideManager manager) => StateIs(manager, _oldWireframe);
        public GuideOperationResult Execute(GuideManager manager) =>
            manager.SetWireframe(_guideId, _newWireframe);
        public GuideOperationResult Undo(GuideManager manager) =>
            manager.SetWireframe(_guideId, _oldWireframe);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool StateIs(GuideManager manager, bool expected) =>
            manager.TryGetGuide(_guideId, out var guide) && guide.IsWireframe == expected;
    }
}
