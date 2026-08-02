> 🗄️ **ARCHIVED 2026-08-01 at v0.4.49 — SUPERSEDED, DO NOT USE AS CURRENT.**
> Frozen exactly as written. Its successor is `dev/plans/PLAN_BACKGROUND_SAVE.md`.
> Kept so nothing is ever lost. Never edit this file; never cite it as current.
>
> ⚠️ **THE DESIGN BELOW IS NOT VIABLE, and was dismissed hours after it was written.** The save layer cannot
> enumerate or delete keys — `GOTCHAS` **G42** — so per-guide records need a self-maintained index and leak a
> dead key per dispelled guide forever, and bucketing saves nothing because the whole blob is rewritten
> regardless. Its **premise had also expired**: v0.4.45 stopped saving on every edit, so the per-edit cost it
> argues from no longer existed. **The MEASUREMENTS here remain valid and were reused** — ~4 ms per MB, the
> 1,940-byte real guide record, the world-size curve. Read it for those, and for why the idea is dead.

# PLAN — Per-guide persistence ("index cards")

> **Status: PROPOSED, NOT STARTED. Human-decided 2026-08-01 that it MUST be done** — "it definitely is a
> major item and it definitely needs to be done" — and equally that it is **not** to be done in the same
> breath as the fix that prompted it. This file exists so the decision survives the session that made it.
>
> **Prompted by** `TODO` A10.2's performance review (Session 39), which measured the cost of the current
> one-blob design. **v0.4.45 shipped the containment** — saves are deferred and coalesced instead of running
> per mutation — and containment is all it was. The underlying shape is untouched and is what this plan is
> about.
>
> ⚠️ **The human's reasoning is on the record and is the reason this is not "wait until it hurts":** their
> own world is small, but **Layout is a public mod and they cannot see other people's worlds or servers.**
> A cost that only appears on large, long-lived worlds is precisely the cost its author never experiences.

---

## 1. The problem in one paragraph

**Every guide in the world is stored as one blob under one key** — `layout:guidedata` in the world save on
the server, and a single JSON file per world under `Layout/ClientOnlyGuides/` for private (F4) guides.
There is no way to write one guide. Changing anything means **re-serialising every guide in the world**, so
the cost of an edit tracks *how much has ever been built* rather than *what was just changed*. That is the
defect. Everything below follows from it.

## 2. What was measured (Session 39, 2026-08-01)

Benchmarked against the mod's own types, the real `JsonSerializerSettings`, and the real UTF8 encode step.
**Validated against an actual save file** on the human's machine: a real 4-control-point guide record is
**1,940 bytes**, and the synthetic guide used for the table was 2,097 — 8% larger, so these figures are
mildly *pessimistic*, not optimistic.

| Guides in world | Blob | One save | Notes |
|---|---|---|---|
| 50 | 0.2 MB | 0.8 ms | comfortable |
| 250 | 1.2 MB | 4.2 ms | comfortable |
| 1,000 | 4.7 MB | 18.4 ms | **≈ one whole server tick** |
| 3,000 | 14.2 MB | 55.2 ms | **≈ three server ticks** |

Roughly **4 ms per megabyte of guide data**, linear. A server tick is 20 ms (50 tps).

⚠️ **These numbers EXCLUDE `IGuidePersistence.Store` itself** — they are serialisation only, so the true
cost is this plus whatever the storage layer does. On the client that addition is measured and significant
(§3). On the server `SaveGame.StoreData` is believed in-memory-until-world-save, **which is an unverified
claim inherited from a code comment, not something this review established.**

## 3. The client half is a separate cost with the same cause

Private guides persist through `ClientWorldGuidePersistence.Store`, which per call does **three real
filesystem operations**: write a temp file, copy the primary over the backup, move the temp onto the
primary. Measured on real hardware at **1.3–2.9 ms**, and — importantly — **nearly flat with file size**,
because it is dominated by the syscalls rather than the bytes.

That flatness matters for this plan: **index cards do not make the client's per-save cost smaller, they
make it apply to one card instead of the whole set.** The three-operation dance is per file, so a naive
per-card implementation could make the client *worse* on a bulk operation. See §6.

## 4. What v0.4.45 already did, so nobody redoes it

Mutations no longer write. `GuideManager.MarkDirty` sets a flag; `GuideManager.Persist` writes only when
that flag is set, and is driven from lifecycle points: the world save, a 2-second flush tick on both sides,
and shutdown. On the client, every path that drops `LocalGuideAuthority` flushes first.

**Measured effect on a 10-second sustained drag** (10 updates/sec → one flush per 2 s):

| Guides | Before | After |
|---|---|---|
| 250 | 289 ms | 14 ms |
| 1,000 | 1,258 ms | 63 ms |
| 3,000 | 3,711 ms | 186 ms |

**20× less work — by writing less often, NOT by making a write cheaper.** One flush at 3,000 guides is
still ~37–55 ms and still overruns a tick. That residue is exactly what this plan removes.

## 5. The design

**One stored record per guide, keyed by its `Guid`**, replacing the single `PersistedRoot` blob.

- **Server:** `SaveGame.StoreData($"layout:guide:{id:N}", …)` per guide, plus one small index record
  listing the live ids (needed because the savegame API offers no key enumeration).
- **Client:** one file per guide under the world's folder, or a small number of sharded files — see §6,
  because one-file-per-guide interacts badly with the backup scheme.

A dirty **set** of guide ids replaces today's single dirty **bool**. A flush writes only the cards in that
set, so a drag writes exactly one card regardless of world size. **The cost stops depending on world size,
which is the whole point** — everything else here is consequence.

## 6. The hard parts — this is why it is a redesign, not a tweak

1. **Deletion becomes explicit.** A rewritten blob drops a dispelled guide for free. Cards do not: the card
   has to be found and removed, and a missed one is a guide that resurrects on next load. The dirty set
   therefore needs to distinguish *changed* from *deleted*.
2. **Loading needs an index.** There is no "list all keys under a prefix" on the savegame API. An index
   record is the obvious answer and it is **a second thing that can disagree with reality** — an index
   naming a missing card, or a card no index names. Decide the reconciliation rule up front, and prefer
   "the cards are the truth, the index is a hint".
3. **Every existing world is a blob.** Migration must be automatic, one-way, and flawless, because the
   failure mode is losing people's work. **Read the old blob, write the cards, and do not delete the old
   blob** — leave it as a fallback for at least one release. `GOTCHAS` R1's neighbours and the
   archive-never-delete habit apply here.
4. **The client's backup scheme is built around there being one file.** `IRecoverableGuidePersistence`
   (`LoadBackup` / `RejectPrimary`) quarantines a corrupt primary and recovers the previous generation.
   Per-card that becomes per-card backups — hundreds of `.bak` files and a quarantine story per file — or a
   rethink. **Sharding (N guides per file, e.g. by id hash) is probably the right client answer**: it keeps
   the file count and the three-syscall cost bounded while still making a write proportional to a shard
   rather than the world. The server, having no such dance, can take true per-guide records.
5. **`DataVersion` does NOT necessarily move.** This changes the *container*, not the guide *record*
   schema, and `WIRE_HISTORY.md`'s DataVersion ledger is about records. Confirm before stamping anything —
   a needless bump is a migration everyone pays for and nobody needs.

## 7. What must not regress

- **No guide may be lost, ever**, including through migration, a partial write, or a crash mid-flush.
  Correctness over performance is the standing rule and it governs here absolutely.
- **World load time** must not get worse. Many small reads can be slower than one big read; measure it.
- The **client-only push** (up to 100 guides per packet) and **`/layout dispel all`** are the bulk paths
  that will exercise per-card writing hardest. They are the benchmark cases, not the drag.
- The **2-second flush** and every lifecycle flush point from v0.4.45 stay exactly as they are. This plan
  changes *what* a flush writes, never *when*.

## 8. How to know it worked

Re-run the Session 39 benchmark shape: **a save's cost must become flat in world size.** A 3,000-guide
world and a 50-guide world should cost the same to save one edited guide. If the curve is still sloped,
something is still touching every guide and the work is not done.

## 9. When to start

**After a clean playtest, and not bundled with anything else** — the same gate `TODO` A10.2 carries, for
the same reason. It is a data-integrity change to the one part of the mod whose failure is unrecoverable,
and it deserves a session that is about only this.

**Do not start it "while we are in here" during unrelated work.** That is precisely how a migration bug
gets shipped.
