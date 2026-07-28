using System;
using Vintagestory.API.MathTools;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records one compound Transform-pad action applied in place: an optional mirror and an optional
    /// translation, as a single undo step. <c>mirrorAxis</c> is -1 for no mirror.
    /// </summary>
    /// <remarks>
    /// The forward op mirrors THEN translates, so the inverse must translate back THEN mirror — and about
    /// the same stored plane. A reflection is its own inverse only while its plane stays put; reflecting
    /// about the moved guide's new centre would land it somewhere else entirely.
    /// </remarks>
    public sealed class TransformGuideCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly Vec3d _delta;
        private readonly int _mirrorAxis;
        private readonly Vec3d _pivot;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public TransformGuideCommand(Guid guideId, Vec3d delta, int mirrorAxis, Vec3d pivot)
        {
            _guideId = guideId;
            // Vec3d is a mutable reference type — deep-copy, never alias (the codebase-wide rule).
            _delta = delta == null ? new Vec3d() : new Vec3d(delta.X, delta.Y, delta.Z);
            _mirrorAxis = mirrorAxis;
            _pivot = pivot == null ? new Vec3d() : new Vec3d(pivot.X, pivot.Y, pivot.Z);
        }

        public bool CanUndo(GuideManager manager) => manager.HasGuide(_guideId);
        public bool CanRedo(GuideManager manager) => manager.HasGuide(_guideId);

        public GuideOperationResult Execute(GuideManager manager)
        {
            Vec3d pivot = Pivot();
            return manager.TransformGuide(_guideId, _delta, _mirrorAxis, ref pivot);
        }

        public GuideOperationResult Undo(GuideManager manager)
        {
            // Step 1: take the translation back off, about nothing in particular.
            if (_delta.X != 0 || _delta.Y != 0 || _delta.Z != 0)
            {
                Vec3d none = Pivot();
                GuideOperationResult moved = manager.TransformGuide(
                    _guideId, new Vec3d(-_delta.X, -_delta.Y, -_delta.Z), -1, ref none);
                if (!moved.IsSuccess) return moved;
            }
            // Step 2: reflect back about the ORIGINAL plane.
            if (_mirrorAxis < 0) return manager.TryGetGuide(_guideId, out var g)
                ? GuideOperationResult.Success(g, 0)
                : GuideOperationResult.NotFound();

            Vec3d pivot = Pivot();
            return manager.TransformGuide(_guideId, null, _mirrorAxis, ref pivot);
        }

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        // A fresh instance per call: TransformGuide takes the pivot by ref and would otherwise be handed
        // the command's own to keep.
        private Vec3d Pivot() => new Vec3d(_pivot.X, _pivot.Y, _pivot.Z);
    }
}
