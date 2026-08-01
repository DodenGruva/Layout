# Layout — TODO / open items

> **Purpose.** The running punch-list, **open items only**. Companion to `ARCHITECTURE.md` (the blueprint),
> `GOTCHAS.md` (traps and reversals), and `STATUS.md` (where things stand right now).
>
> **Delivered work lives in `dev/history/DONE.md`** and is not repeated here. This file was 1,124 lines on
> 2026-07-30, the great majority of it finished work being re-read every time anyone opened it.
>
> **When an item is delivered, move it to `DONE.md`** — do not strike it through and leave it here. That is
> how this file grew to nine times its useful size.

---

## ⭐ Top of the list — the two oldest items are closed

**`TODO` A10.1 and A13 are both delivered** (Session 38, → `DONE.md`). The GUI layer — the last unreviewed
surface, roughly 8,000 lines — has been read end to end, and the doc-comment sweep is finished. With A14
emptied in Session 37, **the review backlog is down to one item: A10.2.**

**v0.4.40–v0.4.43 have shipped.** v0.4.39 was playtested and passed. v0.4.42 (chiselling highlights) and
v0.4.43 (shape memory) are the two with something specific to look for — see *Verification debts* below.

---

## A10.2. Perform a performance-focused code review of the NON-RENDER code

**The last of the review backlog, and still the honest answer is "nobody has looked."** Session 28 measured
and fixed the renderer; nothing equivalent has been done anywhere else.

Two things have been removed from this axis incidentally rather than by search — Session 37 made `Persist()`
batchable and put the push path on it, and Session 38's GUI review happened to walk past an aim loop that
tested every guide in the world 33 times a second. **Neither was found by looking.** That is the argument
for looking.

**Start it after a clean playtest, not during one** — it will generate changes that need testing.

## A15. What was deliberately deferred, and why

Four items, each with a stated reason. **None is "forgotten" — re-opening any of them is a decision, not a
discovery.**

1. **A14.7's `long`/count-based loop rewrite.** The coordinates that trigger it are now rejected at every
   entry point, so it is unreachable and this is defence in depth. **Deferred on evidence:** the hang was
   reproduced 2026-08-01 and `GuideBounds.HardExtent` verified safe with a 4× margin — `GOTCHAS` **G31**,
   `SESSION_37.md` §4.1. Seven of eight shapes carry the defect.
2. **The `_settledMaterializations` concurrency gate.** One `LongRunning` task per streaming guide, so a
   world load starts many at once. **Gated by `GOTCHAS` G2 and item B.1 below** — renderer work starts from a
   measured bottleneck, not a hunch. The crash it made likelier is fixed.
3. **The palette-lane cancellation** (A14.6's third part). A scheme/opacity change cancels only the settled
   lane, so a guide mid-stream can settle with a few batches in the old colours. Bounded by the producer
   queues and self-healing on the next rebuild. Same renderer gate as above.
4. **`ResolveSculptCountLimit`'s wasted scan** (A14.6's first part). The review calls it **wasted work only**
   — `CommitPreparedGuideMutation` re-validates, so it is not a cap bypass — and it is the same
   shrink-escape shape that `GOTCHAS` G28 had just been fixed for. Not worth risking that to save a scan.

**Also still open from A14.1's design:** naming the cap **in the HUD** needs a field appended to
`VoxelCapWarningPacket`. Appending is sanctioned (G1) but it bumps the protocol. The chat message names the
cap today, so this is polish.

## A16. The occupancy re-probe covers the anchor, not the whole guide

**Open by construction, not by oversight** — recorded so the next session does not mistake it for a bug.

`_deferredOccupancy` (v0.4.42) defers a guide for re-probing when its **first real anchor's** chunk is
absent at mesh time. A guide whose anchor is resident while terrain further along it is not will therefore
not defer, and that part stays uncoloured until a block change nearby or `/layout built refresh`.

It is still strictly better than before — a blind read is no longer cached, so nothing wrong is remembered
(`GOTCHAS` **G37**). Widening it means per-region attribution, which is real work and was deliberately not
bundled into a bug fix. **Do it only if play shows the gap matters.**

## A11. Open playtest focus

- **Settled-shell streaming (v0.3.58) — the remote-arrival half is unverified.** Needs a second player
  watching a large guide arrive. Also: world load with the 8M guide (scaffold → grow-in, no hang), and
  whether 100,000 voxels is the right threshold.

---

## B. Carried forward

1. **Large-guide follow-up only from a NEW measured bottleneck and a fidelity-preserving design.**
   The v0.3.43–v0.3.48 spatial/greedy path was rejected and v0.3.49 restored the v0.3.42 renderer.
   **This is gated by `GOTCHAS` G2** (order-dependent geometry) and its reversals R2/R4/R5/R8 — read those
   before proposing renderer work. Preserve: true scale, role colour/alpha, exact face
   plane/orientation/inset, guide-wide exposed-face occupancy, stable front/back visibility, world/cloud
   depth, uninterrupted materialization, and real-play FPS.
2. **Remaining flagged decisions are cosmetic** — walk them opportunistically. See the flag index below.
3. **Then, if asked:** **Roof / Tunnel** volumes · a concave-safe **Free-Shape fill** · broadcasting the
   whole Free-Shape draft chain to other players (flag 11q) · the **F3 re-constrain op**.

### F3. Re-constrain op — idea, unrequested, parked
The inverse of a break: a menu action snapping a free shape back under a constraint (arch → half-circle,
ellipse → circle, triangle → equilateral, rectangle → square) with a best-fit. Natural undo pairing exists.
**Park until asked.**

---

## Flagged decisions awaiting the human's review

Made under the standing "decide, note for review" rule. **Each is cheap to reverse; none block play.**

Compressed to an index on 2026-07-30 — the full text of every flag is in the session record named against
it, and in `dev/archive/superseded-2026-07-30/TODO.md`. Identifiers are unchanged, so nothing became
unfindable.

| Set | Flags | Full text |
|---|---|---|
| Session 8 | 7, 8, 9, 10, 11, 13, 14, 16 (12 and 15 closed — see `DONE.md`) | `history/DONE.md` |
| Session 9 | 1, 3, 4, 5, 6 (2 superseded by the three-click triangle in 0.1.14) | `sessions/SESSION_9.md` |
| Session 10 | 0a–0f — the icon UI pass; partially reviewed in play already | `sessions/SESSION_10.md` |
| Session 11 | 11a–11j, 11k–11r (11d superseded by the 0.1.15 hard-kept model) | `sessions/SESSION_11.md` §8, §11 |
| Session 31 | six flags | `sessions/SESSION_31.md` §7 |
| Session 32 | nine flags | `sessions/SESSION_32.md` §9 |
| Session 33 | six flags | `sessions/SESSION_33.md` §8 |
| Session 34 | six flags — notably send-to-ground ignoring Mirror (`GOTCHAS` G19) | `sessions/SESSION_34.md` §10 |
| Session 37 | **seven — every tunable number that session invented.** Cap-refusal throttle (4 s), HUD flash wording/duration, `GuideBounds` map slack (4,096), rate-limit capacity/refill (240 / 120 per second) and its cost weights, push cooldown (3 s) and `MaxPushedControlPoints` (1,024), claim-revalidation restart cap (3), and the immense-reshape lock exemption | `sessions/SESSION_37.md` → *Flagged and unverified* |
| **Session 38** | **two.** The targeting broad-phase padding (pick radius + ¼ of the guide's largest dimension), and surfacing `OccupancyAwaitingChunks` only in `/layout built refresh`'s reply rather than on the HUD | `sessions/SESSION_38.md` → *Flagged and unverified* |

**The most substantive still-unreviewed calls**, if you only want to look at a few:

- **Session-37 #7. The immense-reshape exemption from one-lock-per-player** — the one flag with a real
  failure mode if it is wrong, and the one worth a deliberate try in play. Reshape a very large guide,
  release, immediately grab a different one; the first reshape should still land.
- **Session-38 #1. The broad-phase padding.** If a guide you can plainly see ever refuses to be clicked —
  a long deeply-curved arch, a Free-Shape with corners spread wide — this is why, and the fix is more
  padding.
- **0f. Edit mode is SELECT-ONLY** — clicking a guide in Edit selects it for the settings rows but does not
  grab, insert or lock, so a select-click cannot accidentally reshape. All geometry editing stays in Create.
  *Alternative if wanted:* allow grabbing in Edit, with body-click = select and point-click = grab.
- **Session-9 #1. The soft-flow regime split** (slave for interior grabs, shape-preserving for structural
  grabs) — the decisive fix for "the apex acts like a pin". The human confirmed grabbing was "MUCH better",
  but the split itself was Claude's call and can be revisited.
- **Session-9 #5. Division marks are magenta** — the one hue distinct from the six existing roles.
- **Session-8 #16. Proportional soft flow** deserves a stretch/shrink/rotate torture test on an arch with
  several inserted points.

---

## Known costs and tuning — deliberate, watch in real play

Not to-dos. Standing constraints on how the moving parts are allowed to behave.

- **Filled guides recount exactly per drag update.** If big filled discs drag sluggishly, add a per-drag
  count cache — correctness first, per the standing rule.
- **Settled Shells and persistent Wireframes use true selected scale.** Adaptive coarsening is motion-only;
  the cursor neighbourhood and final-click scaffold stay precise while exact Shell refinement streams.
- **`PreviewFullResVoxelCap` = 8,000** — the cheap/full-shell moving threshold. Tune only from playtest data.
- **Immense materialization is streamed and cancellable.** Shape scans feed a capacity-three queue;
  deterministic multi-seed 26-neighbour ordering creates the torn/frayed growth. Client targets: ~750 voxels
  per upload, 8–128 batches, ~45 ms cadence, with frame-pressure backoff. **Preserve exact final occupancy;
  never upload one voxel at a time and never rebuild one growing mesh per frame.**
- **Immense public validation is intentionally serialized.** One below-normal worker does pure
  generation/counting; claim checks consume ≤128 blocks or ~1 ms per 20 ms server tick. Longer build time is
  acceptable — **server tick health is the priority.** Since v0.4.36 the worker also *observes* cancellation,
  so a cancelled job gives the lane back at its next seam instead of running to completion.
- **Whole-guide + regional culling is conservative and measured.** A bound outside live
  `viewDistance`/frustum submits nothing; an intersecting immense clean Shell then tests fixed 32-block
  regions independently. Cross-region neighbours use complete guide occupancy, so boundaries add no internal
  faces.
- **Targeting rejects by bounding box before it samples anything** (v0.4.40). The padding is deliberately
  generous — see the Session-38 flag above. Rejecting nothing costs one pass over a guide's control points,
  which is cheaper than the curve fingerprint it saves.
- **Division marks add a render-side pass** *(verified)* — `DivisionMarks.Apply` runs on every mesh rebuild,
  walking `SampleCurve(128)` then a nearest-cell claim per boundary. Cheap, but it walks the cell list.
  Watch on very high division counts × large guides.
- **A guide over 3,000,000 voxels can never have its occupancy colours updated live.** That is intended and
  reported by `OccupancyStaleGuides`; since v0.4.38 those guides are held apart so they no longer pin the
  changed-block list open (`SESSION_37.md` §8).
- Carried from Session 7: item transforms · recipe balance · scroll-wheel bindings · Surface flatten's
  eventual move into the shape layer (`TODO(Surface)`).

---

## Verification debts

**These are claims to test, not rules to follow** — `STATUS.md`'s *Known unverified claims* is the
authoritative copy. Listed here because they are genuinely open work:

- **Does the chiselling highlight light itself on a world load now?** (v0.4.42.) The fix defers a guide for
  re-probing when its anchor's chunk is absent at mesh time; whether that window exists in practice cannot
  be settled by reading. If the highlights still come up dark, see **A16** — the anchor test is the reason
  and widening it is the fix.
- **Does the tool now come back on the shape you left it on?** (v0.4.43.) Try one of the seven that used to
  be forgotten — Dome, Cylinder, Cone, Box, Tapered Cylinder, Polygonal Prism, Tapered Polygonal Prism.
- **v0.4.40's four GUI fixes are unverified as a set.** v0.4.41 superseded that build before it was played.
- The hard **32-chalk ceiling is verified offline but never tested against xskills itself.** Craft a
  quality-bonus kit and confirm it comes out 32/32.
- **1.22.x support is declared, not tested.** Built against 1.22.3; smoke-test a 1.22.0/1.22.1 install.
- The **Players list's scroll container** rests on reasoning rather than a test (`GOTCHAS` G18).
- Session-29 **block-occupancy verification items** — the diagnostic commands are hidden behind
  `"diagnosticCommands": true` in `layout-client.json`, not deleted, precisely for this.
- **Is `GOTCHAS` G4's general case still live?** G4 says the stale-wireframe trap is "fixed for the Move path
  only". `TryStartSettledShellMaterialization` now carries a `ScaffoldFingerprint` guard whose own comment
  describes and closes exactly that case, and Session 36 found every other early return in
  `OnGuideAddedOrUpdated` fingerprint-guarded or pose-matched. **One path was traced, not all of them** —
  confirm before annotating G4. An entry that says "still live" when it is not sends the next session hunting
  a fixed bug, which is R7's failure mode running the other way.
- **How often does the immense-sculpt race actually fire?** The defect is fixed (v0.4.36) but its window was
  never sized: "the worker is still counting when you re-grab". Only play can say whether it was common.

---

## Standing workflow rule (human-set — also in `CLAUDE.md`)

Ship a NEW `Layout<version>.zip` per code iteration into `..\Layout Zips\`. Update docs and commit **only**
when the human says so — and **`CHANGELOG.md` is part of that set** (missed at the end of Session 29,
backfilled in Session 30). Warn before any context trim if the docs are stale.
