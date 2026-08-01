# Session 37 — v0.4.34 → v0.4.39

**Branch:** `beta`. **DataVersion 13. Protocol 24.** **84 source files** (one added — `src/Guide/GuideBounds.cs`).

> The session that emptied `TODO` A14. Session 36 found twelve defects and was forbidden to fix any of them;
> this session fixed all twelve, in the agreed order, one shippable revision at a time. Two more defects
> arrived en route — one the human found in play, one this session found reviewing its own work — and both
> turned out to matter more than several of the twelve.

---

## 1. The arc

Six revisions, each built, versioned and zipped before the next began, per the standing workflow rule.

| Version | What it closed |
|---|---|
| **v0.4.34** | A14.1 (silent cap refusals) · A14.2 (world-total cap had no shrink escape) |
| **v0.4.35** | A14.7, validation half — `GuideBounds` at all three untrusted sources |
| **v0.4.36** | A14.3 (both halves) · A14.4 (the throw) · A14.10 (lock hoarding) · A14.9's tuning-free half · A14.6's positional constructor |
| **v0.4.37** | **The zero-voxel guide** — found in play by the human, not by either review |
| **v0.4.38** | A14.5 · A14.8 · A14.11 · the retired-admin-setting reply · A14.9's rate limiting |
| **v0.4.39** | Five defects found by this session reviewing its own work, including one shipped in all five earlier zips |

The human deferred playtesting until the end and asked for work that did not need them, which is where §8
and §9 came from. That turned out to be the most productive stretch of the session.

## 2. A14.1 — the silent cap refusals

Nine server call sites refused an edit on a voxel cap. Two also sent chat text; **seven sent only
`VoxelCapWarningPacket`, which has no subscriber anywhere in the tree** (G27). One of the seven is
`HandleNonSuccessToggle`, which alone serves a dozen operations.

All nine now go through one `SendCapRefusal`. Three things settled here:

- **It has to be server-side.** `VoxelCapWarningPacket` carries a count and a limit and **never says which
  cap fired**, so the client cannot name it. The server already had that if/else cascade — twice, in the two
  placement paths, and nowhere else. It is now in one place and the four wording helpers take plain ints
  rather than a `GuideOperationResult`, so the immense paths (which hold a geometry result, not an operation
  result) can use them too.
- **The throttle was mandatory and then wrong.** A refused *drag* re-fires roughly ten times a second, so
  the chat line is suppressed for 4 s per identical message. ⚠️ Shipped in v0.4.34 applied to **every** path,
  including placement — so a player who placed, was refused, adjusted and placed again inside four seconds
  was told nothing the second time. Corrected in v0.4.37: `SendCapRefusal` takes `throttle`, false for the
  two deliberate one-shot placement paths, true for the continuous ones. **The packet is never throttled** —
  it was already flowing at that rate and it is what the HUD reads.
- **The dead event is now wired**, and the HUD's cap row flashes `REFUSED — over cap` for 2.5 s. Generic on
  purpose. It also covers **private** guides, where `LocalGuideAuthority` raises the same event and there is
  no server to send chat. ⚠️ This is also where §9's shipped bug lived.

## 3. A14.2 — one missing clause was actually two

`WouldExceedCaps`'s world-total branch lacked the growth-only clause its two siblings have. That is what the
finding said, and fixing only that would have been **wrong**.

`CountForCaps` compounds it, and had to be fixed in the same breath. Its world-total branch clamps the scan
limit to the remaining budget; when the world is already over a newly-lowered cap that remaining budget is
**zero**, so `CountUpTo` returns the `Exceeded(0)` sentinel — **1** — rather than a real count. Add the
growth-only clause alone and that sentinel now *passes* the check (1 is less than the guide's current count),
and gets stored as the guide's voxel count. The cap fix would have corrupted the running totals.

Both siblings already carry the guard (`if (playerTotal <= playerTotalCap)`, `if (perGuideCap > 0 &&
!existingExceedsActorCap)`). The world-total branch had neither half. It now has both.

## 4. A14.7 — the validation half, and then the reproduction

**`src/Guide/GuideBounds.cs`** — one static helper, finite and inside the world plus 4,096 blocks of slack,
with a hard ±33,554,432 backstop that applies before the world has reported anything. Called at all three
untrusted sources the finding names: the packet boundary (create, edit, insert, draft anchor, free-shape
chain), `RestoreGuide` (which is also the private-guide push import seam), and `LoadPayload` (the world save
**and** the hand-editable private-guide file). A guide that fails on load is dropped with a logged warning
rather than silently lost or hanging the scan.

### 4.1 The reproduction — and three things it corrected

G12 says run the diagnostic before writing the fix, and `STATUS.md` §6.9 carried the debt: the hang had never
been reproduced, four of seven shape paths were inferred rather than traced, and sphere/dome was predicted to
fail differently. The human asked for work that did not need them, so this was finally settled with a
throwaway harness — one process per shape so a real hang can be killed — calling each shape's threshold
counter directly at a crafted coordinate.

| coordinate | Box | Cylinder | Cone | TaperedCyl | PolyPrism | TaperedPoly | Sphere | Dome |
|---|---|---|---|---|---|---|---|---|
| 512 (control) | ok | ok | ok | ok | ok | ok | ok | ok |
| **33,554,432** (`HardExtent`) | ok | ok | ok | ok | ok | ok | ok | ok |
| 134,217,720 | ok | ok | ok | ok | ok | ok | ok | ok |
| **134,217,728** (2^27) | **32** | HANG | HANG | HANG | HANG | HANG | HANG | HANG |

**The hang is real: seven of eight shapes never return.** The cliff is exactly 2^27, where the coordinate
times 16 reaches `int.MaxValue + 1`. Nothing gradual — correct one step below, dead on the boundary.

Three corrections to what A14.7 and G31 asserted:

1. ⚠️ **`BoxShape` does NOT hang.** It was named as one of the two shapes *traced end to end in source and
   confirmed*. It returns **32** — a nonsense count for a four-block box, but it terminates. A wrong-answer
   bug, not a hang. **A trace that predicts a behaviour is not an observation of it.**
2. ⚠️ **Sphere and Dome hang; they do not throw.** Both were predicted to "fail differently" via a `checked`
   multiply that "probably throws first". They do not throw.
3. **The four inferred shapes are confirmed** — Cone, TaperedCylinder and both prisms all hang.

And the part that matters for the shipped fix: **`GuideBounds.HardExtent` is verified safe with a 4× margin.**

The `long`/count-based loop rewrite stays deferred — now on evidence rather than inference. It is unreachable
through any validated entry point, so it is defence in depth, not urgent.

## 5. A14.3 and A14.4 — the two races

**A14.3, both halves.** The identity check (G29) is the corruption fix: `CompleteActiveImmenseSculpt` removed
the player's pending entry by uid with no identity check, so a cancelled worker finishing late deleted the
entry belonging to that player's *newer* reshape. After that nothing could find the second reshape by uid —
release never registered, cancel did nothing, and it snapped back silently. `CompleteActiveImmenseCreate` had
the same shape against a `HashSet<string>`, which has no identity to check, so it now asks whether the queue
still holds a live entry for that player.

The second half: **cancelling never stopped the worker.** `Cancelled` was written and read on the tick thread
only, *after* the task completed, so the geometry ran to completion with the single validator lane occupied
however long after the player gave up. It is now `volatile` and the workers check it — before the count,
between the count and the footprint, and every 2,048 cells inside `BuildFootprint`.

⚠️ `BuildFootprint` returns **null**, not an empty list, when it abandons. An empty footprint reads as "no
blocks to check", which would skip claim validation entirely — a security hole in place of a cancellation.

**A14.4, the throw.** `GetOrBuild`'s `TryAdd` loser re-read `_blocks[key]`, which `Invalidate` may have
removed from the main thread, throwing on a mesh worker. It now returns the entry it just computed itself —
the same answer, and no dictionary read at all. (G30 proposed `GetOrAdd`; returning `built` is simpler and
avoids the redundant second read entirely.)

## 6. A14.10 and A14.9

**A14.10.** `ReleaseOtherLocksForPlayer` enforces one guide per player; the network handler calls it on every
grab and broadcasts whatever came free. ⚠️ **One deliberate exemption:** a player's in-flight immense reshape
keeps its lock. That lock belongs to work already underway and ends by itself; yanking it would abandon a
reshape the player legitimately started and already released.

**A14.9** was split, because its two halves have very different risk. The tuning-free half shipped in
v0.4.36: no-op guards on `SetHidden`/`SetDivisions` (both assigned and `Persist()`ed unconditionally, so
spamming an unchanged value was an unbounded persist-and-broadcast loop), an edit batch bounded by the
guide's own point count, and duplicate indexes rejected rather than silently last-wins.

The rate limiting itself waited until v0.4.38 and is deliberately far above anything a human produces: a
drag sends ~10 edits/second, the sustained allowance is 120 with a two-second burst. **It logs when it
trips**, because a silently dropped edit is miserable to diagnose and the number was chosen without
measurement.

## 7. The zero-voxel guide — found in play, by the human

Not in either review's findings, and reachable in about thirty seconds of ordinary play.

Every shape reports **zero voxels** when its own frame check fails (`BoxShape`'s `MinSide`, and the same idiom
in its siblings), with an empty voxel set to match. **Zero passes every cap** — under a 5,000 per-guide cap,
under the player total, under the world total, under the hard ceiling. So the guide was accepted, persisted,
listed at 0 voxels, and drawn as nothing, with no message: from the server's point of view nothing had gone
wrong.

`GuideOpStatus.RejectedEmpty` was **appended last** (G1 — the value crosses the wire in
`GuidePlacementRejectedPacket`, and today's client ignoring the field is why appending is safe, not a licence
to renumber). Enforced at all three creation entry points.

⚠️ **This is also A14.8's stated attack vector, reached by accident.** The second review had it as *"a guide
with degenerate geometry costs zero voxels, so it evades the voxel budget entirely"* — filed as something a
hostile client would do deliberately. The human did it by drafting normally.

Existing zero-voxel guides in a save are **not** removed on load. A mod that quietly deletes your work at
startup is the worse surprise; `/layout dispel` is the deliberate tool.

## 8. A14.5, A14.8, A14.11, and the retired admin settings

**A14.5.** A guide over `OccupancyBatchVoxelCeiling` can never be serviced live, so it never left
`_occupancyDirty` — and `_occupancyChangedBlocks` is cleared only when that set *empties*. One such guide near
the player meant the list grew by every qualifying block change for the session, re-scanned every 50 ms. Those
guides now retire to a separate `_occupancyPermanentlyStale` set: still counted, still reported by
`OccupancyStaleGuides`, no longer holding the list open.

**A14.8**, four parts: gate on `allowClientOnlyMode`; a per-player push cooldown; a length check on both
pushed point lists; and `BatchPersist`, so publishing 100 guides writes the save **once** instead of a hundred
times. The zero-voxel half was already closed by §7.

⚠️ **One part of A14.8 was rejected on the merits** — see Decisions.

**A14.11.** The tiled claim walk now captures a synthesised claim revision (the engine exposes none — the
claim *count* is the proxy) and re-walks if it changed, up to three times. **Honest limit:** it notices a
claim being added or removed, not an existing one being resized in place.

**The retired admin settings.** A request naming slot 6 or 7 fell through the switch's `default:` and then ran
the entire tail — `layout.json` rewritten, the change logged, and the admin told *"EnableChalkDurability is
now unlimited"* for a setting that had not changed and is not a number. It now answers with the truth and
changes nothing. **The enum members stay exactly where they are.**

## 9. The self-review — and the bug that shipped five times

With the human deferring playtest, this session reviewed its own diff. It found five defects, all introduced
this session, and one of them had shipped in every zip:

⚠️ **The HUD cap row read `REFUSED — over cap` permanently, in v0.4.34 through v0.4.38.** The "last refused"
timestamp was seeded with `long.MinValue` as an obvious "infinitely long ago". `ElapsedMilliseconds -
long.MinValue` **overflows `long` and wraps to a large negative number**, which is always inside the flash
window. So the row showed a refusal from the moment the HUD opened and never stopped.

The other four:

- ⚠️ **Release and cancel-grab were rate-limited.** Those are the packets that *give back* a lock. Dropping
  one strands a guide nobody else can edit — the rate limiter would have been manufacturing exactly the
  lock-hoarding A14.10 had just fixed. Both are now exempt.
- **Draft-start was not rate-limited** and it broadcasts to every player. Now gated.
- **Out-of-world rejections left the client hanging** — no placement-rejected reply, no resync, so a refused
  edit sat on screen looking accepted. All three sites corrected.
- **Deleted guides never left `_occupancyPermanentlyStale`**, inflating the stale count for the session.

The HUD bug would have been obvious in seconds of play and survived five shipped versions because nobody ran
it. That is `CLAUDE.md`'s "playtest beats compile-checks" stated as a measurement rather than a principle.

---

## Delivered

- **v0.4.34** — every cap refusal now names which cap fired, through one server-side path, throttled per
  player on the continuous paths; the dead `VoxelCapWarningReceived` event wired to a HUD flash; the
  world-total cap gained its growth-only clause **and** the matching counting-side shrink escape. (A14.1, A14.2)
- **v0.4.35** — `GuideBounds`: finite + in-world validation at the packet boundary, `RestoreGuide` and
  `LoadPayload`, with a hard ±33.5M backstop. Corrupt guides dropped on load with a logged warning. (A14.7,
  validation half)
- **v0.4.36** — identity checks before removing a pending job by player key; immense workers now observe
  cancellation; `BlockOccupancy.GetOrBuild` no longer throws on a mesh worker; one edit lock per player;
  no-op toggle guards, edit-batch bounds and duplicate-index rejection; the last positional cap constructor
  converted to named arguments. (A14.3, A14.4, A14.10, A14.9 part, A14.6 part)
- **v0.4.37** — a guide that voxelises to nothing is refused with a clear message at all three creation
  entry points; placement cap refusals are no longer throttled. (playtest finding; closes A14.8's zero-voxel
  vector)
- **v0.4.38** — permanently-stale occupancy guides no longer pin the changed-block list; the private-guide
  push endpoint gated, cooldowned, length-checked and batched to one save; claim revalidation when claims
  change mid-walk; retired admin settings answer truthfully; per-player cost-weighted rate limiting.
  (A14.5, A14.8, A14.11, A14.9 remainder)
- **v0.4.39** — five self-review fixes, including the HUD cap row that read REFUSED permanently in every
  earlier zip of this session.

## Decisions

- **`TODO` A14 is empty.** All twelve findings are closed or explicitly deferred with a stated reason. Four
  deferrals stand: A14.7's loop rewrite, the `_settledMaterializations` concurrency gate, the palette-lane
  cancellation, and `ResolveSculptCountLimit`'s wasted scan. The first is now deferred on *evidence* (§4.1);
  the two renderer items are gated by G2 and TODO B.1, which require a measured bottleneck; the last is
  "wasted work only" by the review's own words and shares the shrink-escape shape §3 had just fixed.
- **REVERSED — "gate the push endpoint on an outstanding server request".** The second review proposed that
  the server accept `ClientGuidePushPacket` only after sending `ClientGuidePushRequestPacket`. **Wrong for
  this codebase.** The settings page's "Publish Private Guides" button sends unprompted by design, and
  `ClientNetworkHandler` documents the request packet as *"the server's way of asking, never a permission
  token"*. Implementing it would have broken the shipped button. A per-player cooldown addresses the same
  unbounded-upload concern without it. → `GOTCHAS` **R10**.
- **A14.2 needed two clauses, not the one the finding named.** Fixing only the check would have accepted the
  `Exceeded(0)` sentinel as a real count and corrupted the running totals. §3.
- **The rate limit is generous and loud rather than tight and silent.** 120/second sustained against a
  legitimate ~10/second, and it logs when it trips. A rate limiter that misfires looks like "the mod ignores
  my edits", which is far worse than one that lets an attacker through slowly.
- **The in-flight immense reshape is exempt from the one-lock-per-player rule.** §6.
- **Zero-voxel guides already in a save are left alone.** §7.
- **`GuideBounds` is static with a settable world size**, because its three call sites sit in three layers
  that share no object. The hard backstop applies before `UseWorldSize` is ever called, so failing to learn
  the world size can never re-open the hang.
- **`Cancelled` is a `volatile bool`, not a `CancellationToken`.** The workers only need to *observe*
  cancellation; a token would add disposal lifetime to a per-job object for no gain, and the flag already
  existed and was already set at every cancel site.

## Traps

⚠️ **`long.MinValue` is not a safe "never" sentinel for a timestamp.** `ElapsedMilliseconds - long.MinValue`
overflows and wraps negative, so every "has it been less than N ms" test reads true. Shipped in five
versions. Use 0, and test for it explicitly. → **G33**.

⚠️ **A shape that fails its own frame check reports ZERO voxels, and zero passes every cap.** Not a cap
bypass by a hostile client — a normal placement gesture with the two clicks too close together. → **G34**.

⚠️ **Never rate-limit the packet that RELEASES a resource.** Rate limiting release and cancel-grab would have
manufactured the exact lock-hoarding A14.10 was fixing, in the same revision. → **G35**.

⚠️ **A trace that predicts a behaviour is not an observation of it.** `BoxShape` was recorded as traced end to
end and confirmed hanging. It does not hang. §4.1, and G12 restated.

⚠️ **Returning an empty collection to signal "abandoned" can be a security hole.** An empty claim footprint
reads as "no blocks to check". `BuildFootprint` returns null. §5.

⚠️ **The reply is part of the behaviour.** The retired admin settings changed no state, and the wire ledger's
"changes nothing" was true of state and false of the reply — which is the only part an admin sees. §8.

## Flagged and unverified

**Judgement calls awaiting review** — all cheap to reverse, none blocking play:

1. **Cap-refusal throttle window: 4 seconds**, per identical message, continuous paths only.
2. **HUD flash wording and duration** — `REFUSED — over cap`, 2.5 s, deliberately generic.
3. **`GuideBounds` slack: 4,096 blocks past the map edge.** Chosen so nothing legitimate can ever be refused.
4. **Rate limit: 240 capacity, 120/second refill**, with the per-packet cost weights.
5. **Push cooldown: 3 seconds. `MaxPushedControlPoints`: 1,024** (a Free-Shape's own limit is 64).
6. **Claim revalidation capped at 3 restarts.**
7. **The immense-reshape exemption from the one-lock rule** — the one item here with a real failure mode if
   wrong, and the one worth watching in play.

**Claims not tested:** every version from v0.4.34 to v0.4.39 is **unplaytested at the time of writing** —
the human deliberately deferred testing and asked for work that did not require them. This is a statement of
fact from the human, not an assumption (R7): they said so explicitly. It is the one entry in this section
that should be deleted the moment they report back.

⚠️ Six revisions stacked without a playtest also means that if something is wrong, isolating *which* revision
introduced it is harder than it would have been one at a time. Flagged to the human at the time; they chose
to continue, which is their call to make.
