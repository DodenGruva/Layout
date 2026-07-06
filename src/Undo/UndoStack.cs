using System.Collections.Generic;

namespace Layout.Undo
{
    /// <summary>
    /// One player's bounded undo and redo history. Pure stack mechanics — it knows nothing about
    /// <c>GuideManager</c> or how commands apply; <c>UndoManager</c> drives the validate-and-apply flow and uses
    /// this only to pop and push commands. Both histories are capped (default 50); pushing a fresh action evicts
    /// the oldest entry beyond the cap and clears the redo history (a new action forks a new timeline).
    /// </summary>
    /// <remarks>
    /// Each history is a list used as a stack with its LAST element as the top: push = append, pop = remove last,
    /// evict-oldest = remove index 0. Counts stay tiny (≤ cap), so the O(n) removal of the oldest entry is
    /// irrelevant. Not thread-safe and not persisted — undo history is server-main-thread, session-only state.
    /// </remarks>
    public sealed class UndoStack
    {
        public const int DefaultMaxDepth = 50;

        private readonly List<IGuideCommand> _undo = new List<IGuideCommand>();
        private readonly List<IGuideCommand> _redo = new List<IGuideCommand>();
        private readonly int _maxDepth;

        public UndoStack(int maxDepth = DefaultMaxDepth)
        {
            _maxDepth = maxDepth > 0 ? maxDepth : DefaultMaxDepth;
        }

        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;

        /// <summary>
        /// Records a fresh user action: pushes it onto the undo history (evicting the oldest if over the cap) and
        /// clears the redo history, since taking a new action invalidates any previously-undone branch.
        /// </summary>
        public void PushNewAction(IGuideCommand command)
        {
            _undo.Add(command);
            if (_undo.Count > _maxDepth) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>Pops the most recent undoable command, or returns false if the undo history is empty.</summary>
        public bool TryPopUndo(out IGuideCommand command) => TryPopLast(_undo, out command);

        /// <summary>Pops the most recent redoable command, or returns false if the redo history is empty.</summary>
        public bool TryPopRedo(out IGuideCommand command) => TryPopLast(_redo, out command);

        /// <summary>Pushes a command onto the undo history (used to move a command back after a redo, or to
        /// restore one that could not be applied). Respects the cap but never clears redo.</summary>
        public void PushToUndo(IGuideCommand command)
        {
            _undo.Add(command);
            if (_undo.Count > _maxDepth) _undo.RemoveAt(0);
        }

        /// <summary>Pushes a command onto the redo history (used to move a command across after a successful
        /// undo, or to restore one that could not be applied).</summary>
        public void PushToRedo(IGuideCommand command)
        {
            _redo.Add(command);
            if (_redo.Count > _maxDepth) _redo.RemoveAt(0);
        }

        /// <summary>Drops all undo and redo history for this player.</summary>
        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        private static bool TryPopLast(List<IGuideCommand> list, out IGuideCommand command)
        {
            if (list.Count == 0)
            {
                command = null;
                return false;
            }
            int last = list.Count - 1;
            command = list[last];
            list.RemoveAt(last);
            return true;
        }
    }
}
