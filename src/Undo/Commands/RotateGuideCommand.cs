using System;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records an F12 rotate: the whole guide turned a quarter at a time about a world axis, shape
    /// untouched. Like a translation it is its own inverse — turn the other way — but unlike one it must
    /// remember the PIVOT. A rotated shape's bounding centre is not generally where the original's was, so
    /// recomputing the pivot on the way back would land the guide somewhere new.
    /// </summary>
    public sealed class RotateGuideCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly PlaneAxis _axis;
        private readonly int _quarterTurns;
        private readonly Vec3d _pivot;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public RotateGuideCommand(Guid guideId, PlaneAxis axis, int quarterTurns, Vec3d pivot)
        {
            _guideId = guideId;
            _axis = axis;
            _quarterTurns = quarterTurns;
            // Vec3d is a mutable reference type — deep-copy, never alias (the codebase-wide rule).
            _pivot = pivot == null ? new Vec3d() : new Vec3d(pivot.X, pivot.Y, pivot.Z);
        }

        // The loose shared-guide guard, as SpringBackCommand and TranslateGuideCommand use: the guide must
        // still exist. Requiring it to be exactly where this command left it would make the command
        // uselessly brittle, and turning back by the recorded amount is meaningful from wherever it stands.
        public bool CanUndo(GuideManager manager) => manager.HasGuide(_guideId);
        public bool CanRedo(GuideManager manager) => manager.HasGuide(_guideId);

        public GuideOperationResult Execute(GuideManager manager) => Turn(manager, _quarterTurns);

        public GuideOperationResult Undo(GuideManager manager) => Turn(manager, -_quarterTurns);

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        private GuideOperationResult Turn(GuideManager manager, int quarterTurns)
        {
            // A fresh copy per call: RotateGuide takes the pivot by ref and would otherwise be handed the
            // command's own instance to keep.
            Vec3d pivot = new Vec3d(_pivot.X, _pivot.Y, _pivot.Z);
            return manager.RotateGuide(_guideId, _axis, quarterTurns, ref pivot);
        }
    }
}
