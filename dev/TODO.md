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

## ⭐ Top of the list — NOTHING IS QUEUED

The F-queue has been empty since Session 33. The Session-33 polish queue shipped in full (v0.4.28) and the
Session-34 Players-dialog work is done. What remains is the review backlog in **A10** and the "if asked"
list in **B**.

**No open bugs.** B-S9-1 and B-S10-2 are both resolved and playtest-confirmed; kept as regression coverage
in `DONE.md`.

---

## A10. Review backlog — the long-standing one

1. **Perform an adversarial code review.** — **OPEN.** Session 33 raises the value of this considerably:
   four real bugs in shipped code were found just by *reading*, during one debugging session — a silently
   dropped admin packet, a Reveal All filter that could never match, a cap bypass via private mode, and
   eleven player-facing messages printing their own format placeholders (`GOTCHAS` G11).
2. **Perform a performance-focused code review of the NON-RENDER code.** — **PARTLY ADDRESSED.** Session 28
   measured and fixed the renderer; nothing equivalent has been done elsewhere.

*(The third item — consolidate the documentation — was delivered on 2026-07-30 and has moved to
`dev/history/DONE.md`, per the rule at the top of this file.)*

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

**The most substantive still-unreviewed calls**, if you only want to look at a few:

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
  acceptable — **server tick health is the priority.**
- **Whole-guide + regional culling is conservative and measured.** A bound outside live
  `viewDistance`/frustum submits nothing; an intersecting immense clean Shell then tests fixed 32-block
  regions independently. Cross-region neighbours use complete guide occupancy, so boundaries add no internal
  faces.
- **Division marks add a render-side pass** *(verified)* — `DivisionMarks.Apply` runs on every mesh rebuild,
  walking `SampleCurve(128)` then a nearest-cell claim per boundary. Cheap, but it walks the cell list.
  Watch on very high division counts × large guides.
- Carried from Session 7: item transforms · recipe balance · scroll-wheel bindings · Surface flatten's
  eventual move into the shape layer (`TODO(Surface)`).

---

## Verification debts

**These are claims to test, not rules to follow** — `STATUS.md`'s *Known unverified claims* is the
authoritative copy. Listed here because they are genuinely open work:

- The hard **32-chalk ceiling is verified offline but never tested against xskills itself.** Craft a
  quality-bonus kit and confirm it comes out 32/32.
- **1.22.x support is declared, not tested.** Built against 1.22.3; smoke-test a 1.22.0/1.22.1 install.
- The **Players list's scroll container** rests on reasoning rather than a test (`GOTCHAS` G18).
- Session-29 **block-occupancy verification items** — the diagnostic commands are hidden behind
  `"diagnosticCommands": true` in `layout-client.json`, not deleted, precisely for this.

---

## Standing workflow rule (human-set — also in `CLAUDE.md`)

Ship a NEW `Layout<version>.zip` per code iteration into `..\Layout Zips\`. Update docs and commit **only**
when the human says so — and **`CHANGELOG.md` is part of that set** (missed at the end of Session 29,
backfilled in Session 30). Warn before any context trim if the docs are stale.
