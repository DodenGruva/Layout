using System.Collections.Generic;
using Layout.Undo;

namespace Layout.Systems
{
    /// <summary>Outcome of an undo or redo request.</summary>
    public enum UndoRedoStatus
    {
        /// <summary>A command was applied; <see cref="UndoRedoOutcome.Result"/> holds its result.</summary>
        Applied,
        /// <summary>Nothing was applied — the history was empty or every remaining command was stale and skipped.</summary>
        NothingToApply,
        /// <summary>The next applicable command's mutation was rejected (e.g. over cap); it was left in place.</summary>
        Blocked
    }

    /// <summary>
    /// Result of <see cref="UndoManager.Undo"/> / <see cref="UndoManager.Redo"/>: what happened, the underlying
    /// <see cref="GuideOperationResult"/> when a command applied (or was blocked), and how many stale commands
    /// were skipped along the way.
    /// </summary>
    public readonly struct UndoRedoOutcome
    {
        public UndoRedoStatus Status { get; }

        /// <summary>The applied (or rejected) command's manager result. Meaningful when Applied or Blocked.</summary>
        public GuideOperationResult Result { get; }

        /// <summary>How many stale commands were discarded before reaching this outcome (shared-guide skips).</summary>
        public int SkippedStaleCommands { get; }

        public UndoRedoOutcome(UndoRedoStatus status, GuideOperationResult result, int skippedStaleCommands)
        {
            Status = status;
            Result = result;
            SkippedStaleCommands = skippedStaleCommands;
        }

        public bool Applied => Status == UndoRedoStatus.Applied;

        public static UndoRedoOutcome AppliedWith(GuideOperationResult result, int skipped) =>
            new UndoRedoOutcome(UndoRedoStatus.Applied, result, skipped);

        public static UndoRedoOutcome Nothing(int skipped) =>
            new UndoRedoOutcome(UndoRedoStatus.NothingToApply, default, skipped);

        public static UndoRedoOutcome BlockedBy(GuideOperationResult result, int skipped) =>
            new UndoRedoOutcome(UndoRedoStatus.Blocked, result, skipped);
    }

    /// <summary>
    /// Server-side, per-player undo/redo. Holds one <see cref="UndoStack"/> per player UID and drives the
    /// validate-then-apply flow against <see cref="GuideManager"/>. History is in-memory and session-only — it is
    /// never persisted, and a player's stacks are dropped on disconnect.
    /// </summary>
    /// <remarks>
    /// SHARED-GUIDE SAFETY. On an undo/redo, commands are popped from the top. A command whose direction-specific
    /// check (<see cref="IGuideCommand.CanUndo"/> / <see cref="IGuideCommand.CanRedo"/>) fails — because another
    /// player changed or deleted its target underneath — is discarded as stale, and the search continues to the
    /// next command, so a stale entry never blocks a still-valid one and never corrupts current state. The first
    /// command that passes its check is applied.
    ///
    /// MUTATION REJECTIONS. A command may pass its check yet still have its manager mutation rejected at apply
    /// time (e.g. an undo/redo that would push the guide back over the voxel cap because another guide has grown).
    /// In that case the command is pushed back onto the stack it came from (history is not lost) and the outcome
    /// is <see cref="UndoRedoStatus.Blocked"/>, so the caller can warn the player. The same push-back-and-warn
    /// treatment applies when the command's target guide is currently edit-locked by another player (Module 7's
    /// full-exclusivity rule — see the constructor): the command is not stale, merely not applicable right now.
    ///
    /// BROADCASTING. UndoManager does not broadcast — like the other managers it returns results. The network
    /// handler reads the outcome and, for an applied command, broadcasts the affected guide's new authoritative
    /// state (or its removal, if the guide no longer exists), which covers every command type without the handler
    /// needing to know which one ran.
    ///
    /// THREADING. Server main thread only; no locking.
    /// </remarks>
    public class UndoManager
    {
        private readonly GuideManager _guides;
        private readonly GuideLockManager _locks;      // optional; null = no lock gating (tests, tools)
        private readonly int _maxDepthPerPlayer;
        private readonly Dictionary<string, UndoStack> _stacks = new Dictionary<string, UndoStack>();

        /// <summary>
        /// <paramref name="maxDepthPerPlayer"/> normally comes from server config (<c>layout.json</c>,
        /// <c>undoHistoryDepth</c>). When <paramref name="lockManager"/> is provided, a command whose target
        /// guide is currently edit-locked by ANOTHER player is <see cref="UndoRedoStatus.Blocked"/> (pushed
        /// back, history preserved) rather than applied — the undo-path half of Module 7's full-exclusivity
        /// rule, without which undo would be a back door around the network handler's gating. The gate applies
        /// to everyone, admins included; an admin's lock override exists only on the direct-operation path.
        /// </summary>
        public UndoManager(GuideManager guideManager, int maxDepthPerPlayer = UndoStack.DefaultMaxDepth, GuideLockManager lockManager = null)
        {
            _guides = guideManager ?? throw new System.ArgumentNullException(nameof(guideManager));
            _locks = lockManager;
            _maxDepthPerPlayer = maxDepthPerPlayer > 0 ? maxDepthPerPlayer : UndoStack.DefaultMaxDepth;
        }

        /// <summary>
        /// Records a command a player just performed, making it undoable. Pushing a new action clears that
        /// player's redo history. Call this only after the corresponding mutation actually succeeded.
        /// </summary>
        public void Record(string playerUid, IGuideCommand command)
        {
            if (string.IsNullOrEmpty(playerUid) || command == null) return;
            StackFor(playerUid).PushNewAction(command);
        }

        /// <summary>
        /// Undoes the player's most recent still-valid action. Skips (discards) stale commands whose target was
        /// changed or removed by someone else; applies the first valid one and moves it to the redo history.
        /// </summary>
        public UndoRedoOutcome Undo(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid) || !_stacks.TryGetValue(playerUid, out var stack))
                return UndoRedoOutcome.Nothing(0);

            int skipped = 0;
            while (stack.TryPopUndo(out var command))
            {
                if (!command.CanUndo(_guides)) { skipped++; continue; }   // stale → discard, keep looking

                if (IsLockedByOther(command.TargetGuideId, playerUid))    // mid-edit by someone else → Blocked
                {
                    stack.PushToUndo(command);                            // history preserved; try again later
                    return UndoRedoOutcome.BlockedBy(LockBlockResult(command.TargetGuideId), skipped);
                }

                GuideOperationResult result = command.Undo(_guides);
                if (!result.IsSuccess)
                {
                    stack.PushToUndo(command);                            // couldn't apply (e.g. cap) → restore
                    return UndoRedoOutcome.BlockedBy(result, skipped);
                }

                stack.PushToRedo(command);
                return UndoRedoOutcome.AppliedWith(result, skipped);
            }

            return UndoRedoOutcome.Nothing(skipped);
        }

        /// <summary>
        /// Redoes the player's most recently undone still-valid action. Mirrors <see cref="Undo"/>: skips stale
        /// commands, applies the first valid one, and moves it back to the undo history.
        /// </summary>
        public UndoRedoOutcome Redo(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid) || !_stacks.TryGetValue(playerUid, out var stack))
                return UndoRedoOutcome.Nothing(0);

            int skipped = 0;
            while (stack.TryPopRedo(out var command))
            {
                if (!command.CanRedo(_guides)) { skipped++; continue; }

                if (IsLockedByOther(command.TargetGuideId, playerUid))    // mid-edit by someone else → Blocked
                {
                    stack.PushToRedo(command);
                    return UndoRedoOutcome.BlockedBy(LockBlockResult(command.TargetGuideId), skipped);
                }

                GuideOperationResult result = command.Redo(_guides);
                if (!result.IsSuccess)
                {
                    stack.PushToRedo(command);
                    return UndoRedoOutcome.BlockedBy(result, skipped);
                }

                stack.PushToUndo(command);
                return UndoRedoOutcome.AppliedWith(result, skipped);
            }

            return UndoRedoOutcome.Nothing(skipped);
        }

        /// <summary>Drops a player's entire undo/redo history — call on disconnect.</summary>
        public void ClearPlayer(string playerUid)
        {
            if (!string.IsNullOrEmpty(playerUid)) _stacks.Remove(playerUid);
        }

        /// <summary>Current undo depth for a player (for a HUD indicator); 0 if the player has no history.</summary>
        public int UndoDepth(string playerUid) =>
            !string.IsNullOrEmpty(playerUid) && _stacks.TryGetValue(playerUid, out var s) ? s.UndoCount : 0;

        /// <summary>Current redo depth for a player; 0 if the player has no history.</summary>
        public int RedoDepth(string playerUid) =>
            !string.IsNullOrEmpty(playerUid) && _stacks.TryGetValue(playerUid, out var s) ? s.RedoCount : 0;

        private UndoStack StackFor(string playerUid)
        {
            if (!_stacks.TryGetValue(playerUid, out var stack))
            {
                stack = new UndoStack(_maxDepthPerPlayer);
                _stacks[playerUid] = stack;
            }
            return stack;
        }

        // True when the guide is edit-locked by a player OTHER than the one undoing/redoing. The player's own
        // lock never blocks them (undoing your own move mid-grab is legitimate). Null lock manager = no gating.
        private bool IsLockedByOther(System.Guid guideId, string playerUid)
        {
            if (_locks == null) return false;
            string holder = _locks.GetHolder(guideId);
            return holder != null && holder != playerUid;
        }

        // A Blocked result carrying the (unchanged) target guide so the caller can name it in a warning.
        private GuideOperationResult LockBlockResult(System.Guid guideId) =>
            GuideOperationResult.LockedByOtherPlayer(_guides.TryGetGuide(guideId, out var g) ? g : null);
    }
}
