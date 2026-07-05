using System;
using Vintagestory.API.MathTools;
using Layout.Systems;

namespace Layout.Undo
{
    /// <summary>
    /// One undoable/redoable guide action. Each command stores enough to fully reconstruct both its forward
    /// effect and the pre-action state, and applies them through <see cref="GuideManager"/> so that the server
    /// stays the single authority.
    /// </summary>
    /// <remarks>
    /// RETURN VALUES, NOT VOID. Each apply method returns the <see cref="GuideOperationResult"/> of the
    /// GuideManager call it made, so the caller (UndoManager, then the network handler) can broadcast the
    /// resulting state and surface a rejection — the same return-value approach GuideManager itself uses instead
    /// of firing events.
    ///
    /// DIRECTION-SPLIT SAFETY CHECK. The v2 plan specified a single <c>CanApply</c>; this splits it into
    /// <see cref="CanUndo"/> and <see cref="CanRedo"/>, because for create/delete the precondition flips with
    /// direction — a creation can only be undone while its guide still exists, but can only be redone while it
    /// does not. Both checks are the shared-guide guard: if another player has changed or removed the target
    /// since this command was recorded, the relevant check returns false and UndoManager skips the command
    /// rather than corrupting the current state.
    ///
    /// EXECUTE vs REDO. <see cref="Execute"/> is the forward action; <see cref="Redo"/> is the silent re-apply
    /// and simply delegates to Execute. In this codebase the initial action is performed by the network handler
    /// directly (so it can react to the result) and the command is then recorded for later undo/redo, so Execute
    /// is reached via the redo path.
    /// </remarks>
    public interface IGuideCommand
    {
        /// <summary>
        /// The id of the guide this command mutates. Every command targets exactly one guide, so exposing the
        /// id lets <see cref="Layout.Systems.UndoManager"/> check the edit lock BEFORE applying — a command
        /// aimed at a guide currently locked by another player is Blocked (Module 7's full-exclusivity rule),
        /// never applied through the back door.
        /// </summary>
        Guid TargetGuideId { get; }

        /// <summary>True if this command's <see cref="Undo"/> can be safely applied to the current state.</summary>
        bool CanUndo(GuideManager manager);

        /// <summary>True if this command's <see cref="Redo"/> can be safely applied to the current state.</summary>
        bool CanRedo(GuideManager manager);

        /// <summary>Performs the forward action through the manager and returns its result.</summary>
        GuideOperationResult Execute(GuideManager manager);

        /// <summary>Reverses the action through the manager and returns its result.</summary>
        GuideOperationResult Undo(GuideManager manager);

        /// <summary>Silently re-applies the forward action (mirrors <see cref="Execute"/>) and returns its result.</summary>
        GuideOperationResult Redo(GuideManager manager);
    }

    /// <summary>Small shared helpers for command preconditions.</summary>
    internal static class GuideCommandHelpers
    {
        /// <summary>
        /// True if two positions are the same within a tight tolerance. Used by the move/insert commands to
        /// detect whether the targeted point is still where this command left it (if not, another player moved
        /// it and the command must skip rather than clobber their change). Control-point positions are stored
        /// exactly, so the tolerance only guards against incidental floating-point noise.
        /// </summary>
        public static bool PositionsMatch(Vec3d a, Vec3d b, double epsilon = 1e-6)
        {
            if (a == null || b == null) return false;
            return System.Math.Abs(a.X - b.X) <= epsilon
                && System.Math.Abs(a.Y - b.Y) <= epsilon
                && System.Math.Abs(a.Z - b.Z) <= epsilon;
        }
    }
}
