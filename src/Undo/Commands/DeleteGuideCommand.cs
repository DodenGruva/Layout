using System;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records the deletion of a guide. Stores a full snapshot taken just before deletion; undo recreates the
    /// guide exactly, redo deletes it again.
    /// </summary>
    public sealed class DeleteGuideCommand : IGuideCommand
    {
        private readonly GuideData _snapshot;

        /// <inheritdoc/>
        public Guid TargetGuideId => _snapshot.Id;

        /// <param name="guideBeforeDelete">The guide as it was immediately before deletion. Deep-copied for the snapshot.</param>
        public DeleteGuideCommand(GuideData guideBeforeDelete)
        {
            if (guideBeforeDelete == null) throw new ArgumentNullException(nameof(guideBeforeDelete));
            _snapshot = guideBeforeDelete.DeepClone();
        }

        // Undo (re-create) needs the guide absent; redo (delete) needs it present.
        public bool CanUndo(GuideManager manager) => !manager.HasGuide(_snapshot.Id);
        public bool CanRedo(GuideManager manager) => manager.HasGuide(_snapshot.Id);

        public GuideOperationResult Execute(GuideManager manager) => manager.DeleteGuide(_snapshot.Id);
        public GuideOperationResult Undo(GuideManager manager) => manager.RestoreGuide(_snapshot);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);
    }
}
