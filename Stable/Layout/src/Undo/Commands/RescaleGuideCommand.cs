using System;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records a guide rescale. Stores the guide and the old/new scales; undo restores the old scale, redo
    /// re-applies the new one. Either direction can be blocked if it would push the guide back over the voxel cap.
    /// </summary>
    public sealed class RescaleGuideCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly int _oldScale;
        private readonly int _newScale;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public RescaleGuideCommand(Guid guideId, int oldScale, int newScale)
        {
            _guideId = guideId;
            _oldScale = oldScale;
            _newScale = newScale;
        }

        public bool CanUndo(GuideManager manager) => ScaleIs(manager, _newScale);
        public bool CanRedo(GuideManager manager) => ScaleIs(manager, _oldScale);

        public GuideOperationResult Execute(GuideManager manager) => manager.Rescale(_guideId, _newScale);
        public GuideOperationResult Undo(GuideManager manager) => manager.Rescale(_guideId, _oldScale);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private bool ScaleIs(GuideManager manager, int expected) =>
            manager.TryGetGuide(_guideId, out var g) && g.VoxelScale == expected;
    }
}
