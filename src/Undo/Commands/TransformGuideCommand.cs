using System;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records one compound Transform-pad action applied in place: an optional rotation, mirror and
    /// translation, as a single undo step. <c>mirrorAxis</c> is -1 for no mirror.
    /// </summary>
    /// <remarks>
    /// The forward op rotates, mirrors, then translates, so the inverse must translate back, mirror back,
    /// then rotate back — all about the same stored pivot and under one validation/rollback boundary.
    /// </remarks>
    public sealed class TransformGuideCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly Vec3d _delta;
        private readonly int _mirrorAxis;
        private readonly PlaneAxis _rotateAxis;
        private readonly int _quarterTurns;
        private readonly Vec3d _pivot;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public TransformGuideCommand(Guid guideId, Vec3d delta, int mirrorAxis, Vec3d pivot)
            : this(guideId, delta, mirrorAxis, PlaneAxis.Y, 0, pivot)
        {
        }

        public TransformGuideCommand(
            Guid guideId, Vec3d delta, int mirrorAxis,
            PlaneAxis rotateAxis, int quarterTurns, Vec3d pivot)
        {
            _guideId = guideId;
            // Vec3d is a mutable reference type — deep-copy, never alias (the codebase-wide rule).
            _delta = delta == null ? new Vec3d() : new Vec3d(delta.X, delta.Y, delta.Z);
            _mirrorAxis = mirrorAxis;
            _rotateAxis = rotateAxis;
            _quarterTurns = quarterTurns;
            _pivot = pivot == null ? new Vec3d() : new Vec3d(pivot.X, pivot.Y, pivot.Z);
        }

        public bool CanUndo(GuideManager manager) => manager.HasGuide(_guideId);
        public bool CanRedo(GuideManager manager) => manager.HasGuide(_guideId);

        public GuideOperationResult Execute(GuideManager manager)
        {
            Vec3d pivot = Pivot();
            return manager.TransformGuide(
                _guideId, _delta, _mirrorAxis, _rotateAxis, _quarterTurns,
                inverseOrder: false, ref pivot);
        }

        public GuideOperationResult Undo(GuideManager manager)
        {
            Vec3d pivot = Pivot();
            return manager.TransformGuide(
                _guideId, new Vec3d(-_delta.X, -_delta.Y, -_delta.Z),
                _mirrorAxis, _rotateAxis, -_quarterTurns,
                inverseOrder: true, ref pivot);
        }

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);

        // A fresh instance per call: TransformGuide takes the pivot by ref and would otherwise be handed
        // the command's own to keep.
        private Vec3d Pivot() => new Vec3d(_pivot.X, _pivot.Y, _pivot.Z);
    }
}
