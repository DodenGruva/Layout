# PLAN — Move guide serialisation off the main thread

> **Status: PROPOSED, NOT STARTED.** Decided 2026-08-01 (Session 39), at the end of the session, after the
> investigation that **dismissed `TODO` A17** (per-guide "index cards"). This replaces it.
>
> **Supersedes `dev/plans/PLAN_GUIDE_PERSISTENCE.md`**, archived intact at
> `dev/archive/superseded-2026-08-01/PLAN_GUIDE_PERSISTENCE.md`. That plan's *measurements* are still good;
> its *design* was built on an assumption about the save layer that turned out to be false (§2).
>
> ⚠️ **This document is a STARTING POINT, not a finished design.** It records what was established, what was
> disproved, and the open questions — deliberately stopping short of decisions that should be made with a
> full session's attention. Do not treat §4 as settled.

---

## 1. The problem, in its current and much-reduced form

**Most of the original problem is already fixed.** Before v0.4.45, `GuideManager.Persist()` ran on every
mutation and re-serialised every guide in the world — so a reshape drag did it about ten times a second.
That is gone: mutations set a flag, and the write happens at the world's own save.

**What is left is one thing.** When that save runs, it converts the entire guide registry to JSON **on the
server's main thread**:

| Guides in world | Serialisation, on the tick |
|---|---|
| 250 | 3.4 ms |
| 1,000 | 12.6 ms |
| 3,000 | **42 ms** |
| 8,000 | **113 ms** |

A server tick is 20 ms. At three thousand guides that is a two-to-three tick stall on every autosave; at
eight thousand it is worse. It happens every few minutes rather than ten times a second, so it is a periodic
hitch and not the crisis the original was — **but it is pure waste, because none of it needs to be there.**

**It is on the main thread by accident, not by design** — it began as a cheap call at the end of a mutation
and was never revisited as the mod grew.

## 2. What the save layer actually is — and what it forbids

**Established 2026-08-01 by reading a real save file and the API, not by assumption.** These facts killed the
previous plan and constrain any future one.

- **The savegame is SQLite**, and *all* mod data lives in **one row of one table**:
  `CREATE TABLE gamedata (savegameid integer PRIMARY KEY, data BLOB)`.
- Layout's keys (`layout:guidedata`, `layout:adminpolicies`) were found **uncompressed** inside that blob in
  a real world file, so `StoreData` is a dictionary serialised into a single blob.
- **`ISaveGame` offers only `GetData(key)` and `StoreData(key, bytes)`.** There is **no key enumeration and
  no delete.**

Three consequences, all load-bearing:

1. ⚠️ **Per-guide keys are not viable.** No enumeration means we must maintain our own index; no delete means
   a dispelled guide's key can never be removed, only blanked. A world where guides come and go for years
   would accumulate dead keys forever — **a permanent leak inside the world save, traded for a periodic
   hitch.** This is what dismissed A17.
2. ⚠️ **Sharding into buckets buys nothing.** All buckets live in the same blob and the whole row is
   rewritten whenever it changes. It would reduce *our* serialisation but not the actual disk write.
3. **Therefore there is no in-save route to finer write granularity.** The only way to get it is to leave the
   world save entirely — which costs rollback consistency (§5).

## 3. The measurement that decides the approach

**A worker cannot read the live guides.** `GuideData.ControlPoints` is the *same list instance* the shape
mutates — the shared-list binding, documented on `GuideData` — and some operations rebuild it structurally
(`RestorePoints` clears and refills it; insert and remove change its length). A background thread walking
that list while a player drags would read a half-changed shape or throw outright.

So the main thread must hand the worker a **private copy**. Which makes the whole approach depend on one
number: **is copying cheaper than serialising?**

| Guides | Serialise (today, on the tick) | `DeepClone` all guides | Main-thread cost removed |
|---|---|---|---|
| 250 | 3.41 ms | 0.24 ms | 93% |
| 1,000 | 12.63 ms | 0.19 ms | 99% |
| 3,000 | 42.19 ms | **0.52 ms** | **99%** |
| 8,000 | 113.08 ms | **2.62 ms** | **98%** |

**Copying is roughly 80× cheaper.** Text conversion is dominated by formatting thousands of doubles; copying
is allocation and field assignment. At three thousand guides the tick would pay **half a millisecond**
instead of forty-two.

**`IEventAPI.EnqueueMainThreadTask(Action, string)` exists on the common event API** and is documented as
thread-safe and intended for exactly this — getting a result from a worker back onto the main thread.

## 4. Sketch of the approach — NOT settled

1. **Main thread:** deep-copy the dirty registry into a private snapshot (~0.5 ms).
2. **Worker:** serialise the snapshot to JSON and UTF8 bytes (~42 ms, off the tick, nobody waits).
3. **Main thread** (via `EnqueueMainThreadTask`): hand the bytes to `StoreData`.

**Run it ahead of the world save, not during it.** If the bytes are already sitting in the savegame
dictionary when the game autosaves, Layout costs the save nothing at all. Triggering it a few seconds after
edits settle is the obvious shape.

**Open questions for the next session — do not assume answers:**

- **What triggers the background pass?** A timer, a settle delay after the last edit, or the world-save event
  itself with the result stored for the *following* save. Each has a different staleness profile.
- **How stale may the stored bytes be?** This shifts the data-loss window from "since the last world save" to
  "since the last snapshot". Likely fine — the human's standing decision is that guide data is worth what the
  world is worth — **but it is a change and should be a stated decision, not a side effect.**
- **What happens if an edit lands while a serialisation is in flight?** One in flight at a time, re-run after,
  presumably — but the flag interaction with `MarkDirty` needs care (see `GOTCHAS` **G36**: a pending flag
  must never outlive its callback).
- **Does the deep copy need to cover everything `Persist` serialises?** `DeepClone` copies control points and
  the spring-back snapshot; confirm nothing else in `GuideData` is shared-mutable.
- **Should `LayoutAdminPolicyManager.Persist` get the same treatment?** It is far smaller and probably not
  worth it, but it sits on the same event.

## 5. What this does NOT fix, and the one thing that might still justify leaving the save

Guide data **still rides inside the world save file** — roughly 14 MB at 3,000 guides — and the game rewrites
that blob whenever it saves. Off-threading our serialisation does nothing about that.

⚠️ **Unmeasured, and it matters:** whether the *game's* write of a larger blob causes a hitch of its own. That
is Vintage Story's I/O, not ours. If it turns out to be the real cost, none of this helps and leaving the save
file is the only answer — at the price of rollback consistency, because a world restored from backup would no
longer bring its matching guides with it.

**There is no evidence it is a problem**, and every world already pays that kind of cost for its own data.
Treat it as a known unknown, not a task.

**Note on where the cost lands** (human's observation, 2026-08-01): only the **server host** holds the world
save. Public guides are persisted on the server alone — clients hold a memory mirror and never write them.
Only private F4 guides are persisted per-player, in their own files. So the save-size question concerns one
machine; the *stall* question concerns everyone, because a main-thread stall is felt as lag by every player.

## 6. Risks — read before starting

- ⚠️ **This is threading, and threading is where this project has been burned** — `GOTCHAS` **G29**, **G30**,
  and both immense-path races. Nothing here is novel enough to be casual about.
- ⚠️ **The snapshot is mandatory and its necessity is invisible at the call site.** Anyone reading
  `Persist()` sees a dictionary of records; nothing there says the lists are shared with live shapes.
- **Lifecycle:** shutdown, leaving a world and the client-only authority being dropped must all either wait
  for or cancel an in-flight serialisation. `GOTCHAS` **G39** already records that deferring writes creates a
  flush obligation on every drop path; this adds a *join* obligation on the same paths.
- **`StoreData` must happen on the main thread**, and before the game writes — the ordering is the whole
  reason for the ahead-of-save design in §4.

## 7. How to know it worked

**The tick cost of a world save should stop scaling with guide count.** Today it is ~4 ms per megabyte of
guide data on the main thread; afterwards the main thread should pay only the copy, which measured at well
under a millisecond up to three thousand guides.

⚠️ **The human cannot build a several-thousand-guide test world** on a server they actually play. Verification
therefore leans on the synthetic benchmark (`DeepClone` versus serialise, above) plus a play check that
ordinary editing and saving still behave — **not** on reproducing the stall.

## 8. Why A17 was dismissed

Recorded so it is not re-proposed. The full original is at
`dev/archive/superseded-2026-08-01/PLAN_GUIDE_PERSISTENCE.md`, and its measurements remain valid.

**The idea:** one stored record per guide, so an edit writes one guide instead of the whole world.

**Why it is dead:** §2. The save layer cannot enumerate or delete keys, so per-guide records need a
self-maintained index *and* leak a dead key per dispelled guide, forever. Bucketing avoids the leak but saves
nothing, because the whole blob is rewritten anyway.

**And the premise had already weakened.** A17 was written on "the cost of an edit grows with how much has
ever been built" — which stopped being true in v0.4.45, when edits stopped saving at all. The remaining cost
is per-save, not per-edit, and §3 shows it can be moved off the tick without touching the storage format.

⚠️ **A17 was proposed, and the human agreed to it, before the save layer had been checked.** Both of us
reasoned about a storage design from its interface rather than its implementation. The same session had
already shipped and withdrawn a claim pre-filter for exactly that reason (`GOTCHAS` **R12**, **G40**) — twice
in one session, the expensive half was the one nobody verified.
