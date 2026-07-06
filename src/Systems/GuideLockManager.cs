using System;
using System.Collections.Generic;

namespace Layout.Systems
{
    /// <summary>Outcome of a lock-acquire attempt.</summary>
    public enum LockAcquireStatus
    {
        /// <summary>The guide was free; the requester now holds the lock.</summary>
        Acquired,
        /// <summary>The requester already held the lock; nothing changed (idempotent re-grab).</summary>
        AlreadyOwned,
        /// <summary>Another player holds the lock; the request was refused.</summary>
        Denied
    }

    /// <summary>
    /// Result of <see cref="GuideLockManager.TryAcquireLock"/>: the outcome plus who holds the lock now, so the
    /// network layer can broadcast the authoritative lock state (and, on a denial, show the requester who has it).
    /// </summary>
    public readonly struct LockAcquireOutcome
    {
        public LockAcquireStatus Status { get; }

        /// <summary>
        /// The player UID holding the lock after this call — the requester when acquired/already-owned, the
        /// other player when denied. Non-null for all three outcomes.
        /// </summary>
        public string HolderUid { get; }

        public LockAcquireOutcome(LockAcquireStatus status, string holderUid)
        {
            Status = status;
            HolderUid = holderUid;
        }

        /// <summary>True if the requester may now edit the guide (it acquired the lock, or already owned it).</summary>
        public bool CanEdit => Status != LockAcquireStatus.Denied;

        public override string ToString() => $"LockAcquireOutcome({Status}, holder={HolderUid ?? "none"})";
    }

    /// <summary>
    /// Server-side authority for edit locks: at most one player may hold the edit lock on a given guide at a
    /// time, the first grab wins, and a disconnecting player's locks are all released. Locks are session-only
    /// in-memory state and are never persisted — a server restart ends every session, so every lock is moot.
    /// </summary>
    /// <remarks>
    /// PURE STATE. This class touches no game API and no world positions — only guide ids and player UIDs — so
    /// it is trivially testable and has nothing that can drift against the engine. It deliberately does NOT
    /// know whether a guide actually exists: the caller checks that (via <see cref="GuideManager.HasGuide"/>)
    /// before asking for a lock. It also does NOT subscribe to the player-disconnect event itself — the
    /// entry/network layer owns that event and calls <see cref="ReleaseAllLocksForPlayer"/>, because that same
    /// caller needs the returned list of freed guides to broadcast their now-free state (and the editor's last
    /// known point position). All access is on the server's main thread, so there is no locking.
    ///
    /// "ONE LOCK PER GUIDE", NOT "ONE PER PLAYER". The constraint enforced here is per-guide: each guide has at
    /// most one holder. A single player holding locks on two guides at once is not prevented here (the client
    /// only ever grabs one at a time); <see cref="ReleaseAllLocksForPlayer"/> cleans up however many they hold.
    ///
    /// VALIDATION ROLE. Beyond granting/refusing grabs, this is the gate the network handler consults before
    /// applying any mid-edit change: an incoming move / insert / release for a guide is honoured only if it
    /// comes from the lock holder (<see cref="IsHeldBy"/>), so a player can never edit or release a guide they
    /// have not grabbed.
    /// </remarks>
    public class GuideLockManager
    {
        // Guide id -> holding player UID. Presence in the map means locked; absence means free.
        private readonly Dictionary<Guid, string> _locks = new Dictionary<Guid, string>();

        /// <summary>How many guides are currently locked (diagnostics / HUD).</summary>
        public int ActiveLockCount => _locks.Count;

        /// <summary>
        /// Attempts to grab the edit lock on a guide for a player. Grants it if the guide is free; reports an
        /// idempotent success if this player already holds it; refuses — changing nothing — if another player
        /// holds it. <see cref="LockAcquireOutcome.HolderUid"/> is always the current holder, which is what the
        /// network layer broadcasts.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="playerUid"/> is null or empty (a contract
        /// violation: the UID is server-authoritative and always present in correct callers).</exception>
        public LockAcquireOutcome TryAcquireLock(Guid guideId, string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid))
                throw new ArgumentException("Player UID must be non-empty.", nameof(playerUid));

            if (_locks.TryGetValue(guideId, out string holder))
            {
                return holder == playerUid
                    ? new LockAcquireOutcome(LockAcquireStatus.AlreadyOwned, holder)
                    : new LockAcquireOutcome(LockAcquireStatus.Denied, holder);
            }

            _locks[guideId] = playerUid;
            return new LockAcquireOutcome(LockAcquireStatus.Acquired, playerUid);
        }

        /// <summary>
        /// Releases a player's lock on a guide. Succeeds only if that player actually holds it — a player can
        /// never release someone else's lock. Returns true if a lock was released (so the caller knows to
        /// broadcast the guide as free), false if the guide was unlocked or held by a different player.
        /// </summary>
        public bool ReleaseLock(Guid guideId, string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid)) return false;
            if (_locks.TryGetValue(guideId, out string holder) && holder == playerUid)
            {
                _locks.Remove(guideId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Releases every lock held by a player — the disconnect path. Returns the guides that were freed so the
        /// caller can broadcast each as now-free. Safe to call for a player holding no locks (returns empty).
        /// </summary>
        public IReadOnlyList<Guid> ReleaseAllLocksForPlayer(string playerUid)
        {
            var freed = new List<Guid>();
            if (string.IsNullOrEmpty(playerUid)) return freed;

            // Collect first, then remove — never mutate the dictionary while enumerating it.
            foreach (var pair in _locks)
                if (pair.Value == playerUid)
                    freed.Add(pair.Key);

            for (int i = 0; i < freed.Count; i++)
                _locks.Remove(freed[i]);

            return freed;
        }

        /// <summary>
        /// Forcibly clears any lock on a guide regardless of who holds it — for when a guide is deleted out from
        /// under an editor. Returns the UID that had held it, or null if it was already free, so the caller can
        /// broadcast the freed state if someone held it.
        /// </summary>
        public string ClearLock(Guid guideId)
        {
            if (_locks.TryGetValue(guideId, out string holder))
            {
                _locks.Remove(guideId);
                return holder;
            }
            return null;
        }

        /// <summary>True if any player holds the lock on this guide.</summary>
        public bool IsLocked(Guid guideId) => _locks.ContainsKey(guideId);

        /// <summary>The UID of the player holding this guide's lock, or null if it is free.</summary>
        public string GetHolder(Guid guideId) => _locks.TryGetValue(guideId, out string holder) ? holder : null;

        /// <summary>
        /// True if the given player currently holds this guide's lock — the gate the network handler checks
        /// before applying a mid-edit update or honouring a release.
        /// </summary>
        public bool IsHeldBy(Guid guideId, string playerUid) =>
            !string.IsNullOrEmpty(playerUid) &&
            _locks.TryGetValue(guideId, out string holder) &&
            holder == playerUid;
    }
}
