using System;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records the creation of a guide. Undo deletes it; redo re-creates it exactly from a snapshot taken at
    /// creation time (same id, points, and settings).
    /// </summary>
    public sealed class CreateGuideCommand : IGuideCommand
    {
        private readonly GuideData _snapshot;

        /// <inheritdoc/>
        public Guid TargetGuideId => _snapshot.Id;

        /// <param name="createdGuide">The guide as created. Deep-copied so later edits cannot disturb the snapshot.</param>
        public CreateGuideCommand(GuideData createdGuide)
        {
            if (createdGuide == null) throw new ArgumentNullException(nameof(createdGuide));
            _snapshot = createdGuide.DeepClone();
        }

        // Undo (delete) needs the guide present; redo (re-create) needs it absent.
        public bool CanUndo(GuideManager manager) => manager.HasGuide(_snapshot.Id);
        public bool CanRedo(GuideManager manager) => !manager.HasGuide(_snapshot.Id);

        public GuideOperationResult Execute(GuideManager manager) => manager.RestoreGuide(_snapshot);
        public GuideOperationResult Undo(GuideManager manager) => manager.DeleteGuide(_snapshot.Id);
        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);
    }
}
