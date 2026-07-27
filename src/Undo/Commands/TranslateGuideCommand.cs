using System;
using Vintagestory.API.MathTools;
using Layout.Systems;

namespace Layout.Undo.Commands
{
    /// <summary>
    /// Records an F6 Move: the whole guide slid by a fixed world delta, shape untouched. The cheapest
    /// command in the set — a translation is its own inverse with a negated delta, so nothing has to be
    /// snapshotted. One arrow click, or one whole free-move drag, is one of these.
    /// </summary>
    public sealed class TranslateGuideCommand : IGuideCommand
    {
        private readonly Guid _guideId;
        private readonly Vec3d _delta;

        /// <inheritdoc/>
        public Guid TargetGuideId => _guideId;

        public TranslateGuideCommand(Guid guideId, Vec3d delta)
        {
            _guideId = guideId;
            // Vec3d is a mutable reference type — deep-copy, never alias (the codebase-wide rule).
            _delta = delta == null ? new Vec3d() : new Vec3d(delta.X, delta.Y, delta.Z);
        }

        // The loose shared-guide guard, as SpringBackCommand uses: the guide must still exist. Requiring the
        // guide to still be exactly where this command left it would make the command uselessly brittle —
        // any later nudge by anyone would invalidate it — and sliding back by the recorded delta is
        // meaningful from wherever the guide currently stands.
        public bool CanUndo(GuideManager manager) => manager.HasGuide(_guideId);
        public bool CanRedo(GuideManager manager) => manager.HasGuide(_guideId);

        public GuideOperationResult Execute(GuideManager manager) =>
            manager.TranslateGuide(_guideId, _delta);

        public GuideOperationResult Undo(GuideManager manager) =>
            manager.TranslateGuide(_guideId, new Vec3d(-_delta.X, -_delta.Y, -_delta.Z));

        public GuideOperationResult Redo(GuideManager manager) => Execute(manager);
    }
}
