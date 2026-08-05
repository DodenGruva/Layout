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

## ⭐ Top of the list — THE REVIEW BACKLOG IS EMPTY

**`TODO` A10.2 is delivered** (Session 39, → `DONE.md`), and with it the last item of the review backlog.
A10.1 and A13 closed in Session 38, A14 emptied in Session 37. **Nothing in this file is now a "nobody has
looked at this" item.** Every remaining entry is either work someone chose, a decision awaiting the human,
or a claim awaiting a playtest.

**v0.4.40–v0.4.73 have shipped.** v0.4.39 passed, and v0.4.42's chiselling highlights were confirmed good in
play on 2026-08-01 — which also settled **A16** (→ `DONE.md`).

⚠️ **v0.4.48 must not be used** — it shipped a claim pre-filter that was a permissions hole and v0.4.49
reverted it. `GOTCHAS` **R12**.

**A18 is delivered** (Session 40, v0.4.50–v0.4.54 → `DONE.md`) — guide serialisation is off the server main
thread, which was the last thing A10.2 left behind. Five smaller costs were measured and deliberately not
fixed; they are recorded in `SESSION_39.md` §6 and in `DONE.md`, **not here**, because none of them is open
work.

**Session 41 is delivered** (v0.4.55–v0.4.59 → `DONE.md`): whole-guide bounds/projection hardening,
transactional compound Transform, malformed no-op rejection, and deep cancellation of active immense public
geometry. Count-only claim revision was replaced by a fail-closed structural snapshot and then bounded to the
exact guide footprint; distant claims cannot restart work and exact per-block access checks are unchanged.

**Session 42 is delivered** (v0.4.65–v0.4.71 → `DONE.md`): Roundover now takes an exact three-click profile
followed by an unsnapped sweep path; material embedding and the three-rail wireframe answer exterior/interior
placement problems. CTRL bypass and persistent first-click SHIFT embedding now apply across initial guide
placement. The Radius-field and block-probing experiments are deliberately retired (`GOTCHAS` **R13**).

**Session 43 is delivered** (v0.4.72–v0.4.73 → `DONE.md`): zero-voxel reshapes roll back, unsupported body
insertions are rejected in authority, grabs require the exact clicked cell instead of snapping parametric
bodies to nearby handles, and the HUD candidate envelope follows the physical voxel scale. v0.4.73 is directly
play-confirmed excellent; nearest-handle grab snapping is deliberately retired (`GOTCHAS` **R14**).

**Nothing is queued behind it.** The next piece of work is whatever the human chooses.

⚠️ **A17 (per-guide "index cards") was proposed, agreed, and then DISMISSED** once the save layer was
actually read. Do not re-propose it without reading `GOTCHAS` **G42** first. → `DONE.md`; the original plan
is archived intact at `dev/archive/superseded-2026-08-01/PLAN_GUIDE_PERSISTENCE.md`.

## A19. Awaiting a playtest — the background save

**v0.4.73 is the current build to test; it carries Session 40 unchanged. v0.4.50–v0.4.52 each contain a
background-save defect fixed by v0.4.53/v0.4.54.** Nothing in Session 40 has been played yet — stated by the
human, not assumed.

Two things only play can answer, both of which announce themselves:

1. **Is the autosave rhythm as steady as the design assumes?** Repeated
   `Background guide save did not complete before the world save; widening its lead` in the server log would
   say it is not. One or two such lines early on are the design self-correcting and are expected.
2. **Is shutdown detected before the final save?** Two independent signals are checked and both would have to
   fail together. The symptom would be building from the last minutes of a session missing after a clean
   `/stop` — see `GOTCHAS` **G44** for why that is the one path where a miss is unrecoverable.

**`/layout info` reports both**: saves prepared off-thread versus paid on the tick, the learned period, and
the current lead. ⚠️ Reach for it before writing a fix (`GOTCHAS` **G12**) — the whole reason it exists is
that this feature is invisible when it works.

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

*(A14.1's last leftover — naming the cap in the HUD — was **delivered** in v0.4.44/v0.4.46, protocol 25
and 26. → `DONE.md`.)*

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
| Session 38 | **two.** The targeting broad-phase padding (pick radius + ¼ of the guide's largest dimension), and surfacing `OccupancyAwaitingChunks` only in `/layout built refresh`'s reply rather than on the HUD | `sessions/SESSION_38.md` → *Flagged and unverified* |
| **Session 40** | **four.** The background save's tunable numbers — the lead's 3 s start, its doubling, and the 60 s cap; and the 20 s–30 min band outside which a save-to-save gap is not treated as the autosave rhythm. Plus two things deliberately left synchronous: the **admin policies** (a handful of records against the registry's megabytes) and the **client's private F4 guides** (a small file write, and file I/O on a worker is a nastier risk than text conversion) | `sessions/SESSION_40.md` → *Flagged and unverified* |
| Session 39 | **four.** The client's 60 s private-guide flush interval (the server's is the world's own cadence, human-set; the client has none to borrow); `VoxelCapKind.GuideCount` riding `VoxelCapWarningPacket` with numbers that are guides rather than voxels; the five costs measured and deliberately NOT fixed (§6); and the long-standing **admin bypass of claim validation**, surfaced here and left unchanged | `sessions/SESSION_39.md` → *Flagged and unverified* |
| **Session 41** | **three.** Preserve successful in-place Transform order as rotate → mirror → translate; drop malformed legacy projection records instead of normalising them; and allow three relevant claim-state restarts before the fourth change fails closed as temporarily busy | `sessions/SESSION_41.md` → *Flagged and unverified* |

**The most substantive still-unreviewed calls**, if you only want to look at a few:

- **Session-39 #4. Admins bypass claim validation entirely.** Deliberate and consistent with the mod's other
  admin overrides — but it means the one person most likely to test claim protection is exempt from it, and
  it cannot be tested from a singleplayer world at all (`SESSION_39.md` §4).

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
  acceptable — **server tick health is the priority.** Since v0.4.57 a cancelled job interrupts every volume's
  active count, exact generation, fallback scan, marker pass and footprint collapse; no partial geometry is
  published. Since v0.4.59 claim consistency snapshots are bounded to the exact footprint: the API still
  requires a cheap bounds scan of all claim areas, but distant claims are not copied, permission-tested or
  compared and cannot restart the operation. Exact per-block `TestAccess` remains authoritative. `GOTCHAS`
  **G45**, **G47**.
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

- ⚠️ **Does claim protection actually refuse a guide and remain stable during an immense check?** **This is
  the big one, and it cannot be tested from
  singleplayer** — the host holds `controlserver` and Layout exempts it deliberately, in four places
  (`SESSION_39.md` §4). Needs a non-admin account on a dedicated server. Verify an ordinary refusal, then an
  immense operation while a relevant claim is replaced/resized or player authorization changes. v0.4.59's
  bounds scope only the consistency snapshot; v0.4.48's unsafe shortcut remains reverted and exact access is
  still tested for every affected block.
- **Do filled arches still look and count right?** (v0.4.47.) The only shape whose counting code changed.
  Verified identical across 576 configurations offline; the geometry has not been seen in a world.
- **Does a guide-COUNT cap now flash on the HUD?** (v0.4.46.) ⚠️ **Unreachable with default config** — both
  count caps default to unlimited — so `maxGuidesPerPlayer` must be set in `layout.json` to exercise it.
- **Does saving still hold under the v0.4.46 cadence?** The human verified reload before that change; the
  server's timing moved to the world's own save afterwards. Also worth re-checking for private F4 guides,
  which write on their own 60 s timer plus every exit path.
- **Does the tool now come back on the shape you left it on?** (v0.4.43.) Try one of the seven that used to
  be forgotten — Dome, Cylinder, Cone, Box, Tapered Cylinder, Polygonal Prism, Tapered Polygonal Prism.
- **v0.4.40's four GUI fixes are unverified as a set.** v0.4.41 superseded that build before it was played.
- The hard **32-chalk ceiling is verified offline but never tested against xskills itself.** Craft a
  quality-bonus kit and confirm it comes out 32/32.
- **1.22.x support is declared, not tested.** Built against 1.22.3; smoke-test a 1.22.0/1.22.1 install.
- The **Players list's scroll container** rests on reasoning rather than a test (`GOTCHAS` G18).
- Session-29 **block-occupancy verification items** — the diagnostic commands are hidden behind
  `"diagnosticCommands": true` in `layout-client.json`, not deleted, precisely for this.
- *(**`GOTCHAS` G4's general case — SETTLED, Session 39.** Every early return in `OnGuideAddedOrUpdated` was
  traced, not just one: each is pose-matched by `RenderFingerprint` or ends in `RebuildGuide`, and the
  `ScaffoldFingerprint` guard fails closed. G4 is annotated closed and kept for the pattern.)*
- **How often does the immense-sculpt race actually fire?** The defect is fixed (v0.4.36) but its window was
  never sized: "the worker is still counting when you re-grab". Only play can say whether it was common.

---

## Standing workflow rule (human-set — also in `CLAUDE.md`)

Ship a NEW `Layout<version>.zip` per code iteration into `..\Layout Zips\`. Update docs and commit **only**
when the human says so — and **`CHANGELOG.md` is part of that set** (missed at the end of Session 29,
backfilled in Session 30). Warn before any context trim if the docs are stale.
