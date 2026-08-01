# PLAN — Adversarial code review (`TODO` A10.1)

> **Status: RUN ONCE — Session 36, 2026-07-31, against v0.4.33. Still current and still reusable.**
> **This document is the BRIEF for the review, not its findings.** Written 2026-07-31 at v0.4.33, at the end
> of the documentation-audit session, while the settled decisions and trap register were still loaded. Its
> findings went to `dev/TODO.md` **A14** and `dev/GOTCHAS.md` **G27–G30**, with the narrative in
> `dev/sessions/SESSION_36.md` — not here. This file is the starting point, and it should not need rewriting
> to be reused.
>
> **Run this in a FRESH session.** See §1; the reason is not context budget.
>
> **What the first run did NOT cover:** the UI layer. `GuideToolController`, `GuideToolGui` and
> `GuidePlayersDialog` were never read — roughly 7,000 lines, and the home of `GOTCHAS` G6, G13, G14, G16,
> G17 and G18. **A second run should start there**, and §4's risk ranking should be re-read as covering the
> server and renderer only.
>
> **What the first run proved about the brief itself:** §4.2's copy-path hypothesis was wrong (the path is
> correctly capped and recounts), and the two highest-value findings came from §4.4's "verify it actively
> ignores rather than falling through" and from checking a *symptom* claim — who actually consumes a
> rejection packet — rather than from §4.1's concurrency ranking. Keep §2's "a path that exists, compiles,
> and does nothing" as the signature to hunt; it produced both.
>
> ⚠️ **THE BRIEF'S LARGEST GAP: it never states a threat model, so the first run never asked what a MODIFIED
> CLIENT would do.** A second, independent review of the same tree found six defects this brief's run missed
> entirely — including the only **P0** either found: one crafted packet stops the server, because every scan
> guard bounds a shape's size and nothing bounds its position (`GOTCHAS` **G31**). The two reviews overlapped
> on **nothing**. §4's risk ranking is a *correctness* ranking; it is silent on robustness, and a run that
> follows it alone will read half the surface. **Before the next run, decide and write down whether a
> modified client is in scope** — and if it is, add a section covering input validation at every packet
> boundary, per-player rate and resource limits, authorization on every endpoint that mutates or imports,
> and time-of-check/time-of-use gaps in anything validated across ticks.
>
> ⚠️ **And state the scope of every NEGATIVE finding as narrowly as the test that produced it.** The first
> run twice generalised from one traced path and was wrong both times — dismissing the P0 as "unverified",
> and clearing the import seam as "hardened" when it was only hardened against large guides. "I tested X and
> it holds" is not "it is safe". Detail: `dev/sessions/SESSION_36.md` §6.1.

---

## 1. Why a fresh session, and in what frame of mind

The session that wrote this brief spent itself **confirming** things: auditing documents against source and
finding, correctly, that the reasoning behind this project is sound and only the bookkeeping had drifted.
That is the wrong prior to bring to an adversarial review, whose entire value comes from arriving determined
to break something.

It also wrote `GOTCHAS` **R9**, **G26**, and the corrections to `ARCHITECTURE.md`'s rendering and transform
sections. A review straying into `GuideMeshBuilder`, `GuidePalette` or the Transform path would be re-marking
that same session's homework.

**And the code was read in the wrong shape.** Nearly all of it was greps and twenty-line windows, chosen to
answer *"does this document's claim match the source?"* That is verification reading. Bug-hunting asks a
different question — *"what happens when two of these run at once?"*, *"what does this do on the path nobody
takes?"* — and fragments cannot answer it. **Read whole files.**

**Do not trust comments.** Two were found wrong on 2026-07-31 (`colorScheme`'s retired value, and a
palette-guarantee comment naming a method that does not exist), and one of them had already propagated into
a document. Comments are evidence of intent, not of behaviour.

---

## 2. Calibration — what the last reading-based pass actually found

Session 33 found **four real defects in code that had already been released**, purely by reading, during one
debugging session. They are the best available guide to what this codebase's bugs look like:

| Defect | Shape of it |
|---|---|
| An admin packet silently dropped | Handler existed; registration did not reach it |
| `Reveal All` filter that could never match | `GuideDataDto` has never carried `CreatorUid`, so the predicate compared against absent data |
| A cap bypass through private mode | A path that skipped validation another path applied |
| Eleven player-facing messages printing their own `{0}` | `SendIngameError`'s parameter is a lang key, not a format string (`GOTCHAS` **G11**) |

**Three of the four are the same failure: a code path that exists, compiles, and does nothing — or acts on
data that is not where it is assumed to be.** None would have thrown. None would have failed a build. Two
had been broken since the day they were written. **That is the signature to hunt.**

---

## 3. Do NOT re-litigate these — they are settled, with reasons

An adversarial reviewer will independently rediscover every item below and file it as a bug. Each is a
deliberate decision with reasoning behind it, and **R1 exists precisely because one of them was "fixed" once
and had to be reverted a session later.** Read `dev/GOTCHAS.md`'s Reversals section before starting.

| Will look like a bug | Why it is not | Ref |
|---|---|---|
| Private guides ignore every server cap | Caps protect shared storage and other clients' render cost; a private guide consumes neither. Only `HardVoxelCeiling` applies. | **R1** |
| The renderer never reorders or merges primitives | Guides are order-dependent translucent geometry; regrouping changes the picture. Welding was safe *because* it regroups nothing. | **G2**, R2/R4/R5/R8 |
| 3D volumes ignore `filled` | Filled volumes retired v0.2.17 — exposed-face meshing made a filled interior emit nothing at an R³ cost. | **R3** |
| The z-fight outset consults no world state | The probe was removed v0.3.70. A world-driven offset flips when a player fills the volume, and blocks welds. | **R9** |
| Free-Shape ignores its `filled` flag | Fill deferred; irregular outlines can be concave. | `ARCHITECTURE` Shapes |
| Retired enum slots still declared | Append-only wire discipline. Never reclaim. | **G1** |

**If you believe one of these IS wrong, that is a finding worth making — but make it explicitly, as a
challenge to a settled decision with new evidence, not as a routine bug report.**

---

## 4. Where the risk actually concentrates — ranked

### 4.1 Concurrency (highest, and least examined)

This is where the audit kept brushing against complexity without looking into it. Threads and cancellation
are everywhere in the render and generation path:

- **Shapes are adopted on renderer worker threads.** This is load-bearing enough that legacy encodings are
  deliberately never migrated — rewriting a shared control-point list from a shape would be a data race.
  *What else writes to a shared control-point list, and on which thread?*
- **Generation-tagged, cancellable progressive scans**, with stale generations discarded and stale workers
  quarantined by guide fingerprint. *What happens when cancellation races completion? Can two generations
  both believe they are current? Can a fingerprint collide, or repeat after an undo?*
- **The palette reference swap** (`GuidePalette` immutable, `Build` reads it once into a local). Verified
  correct for one mesh. *Is it correct across a multi-batch guide whose batches start either side of a
  swap — and is "one guide wearing two palettes" actually prevented, or only made unlikely?*
- **`OccupancyProbe` runs per body voxel on background threads**, with `BlockOccupancy` locking for it.
  *Is every path into it locked? What does it read while the world is mutating?*
- **The batch queue** (capacity 3) and the single below-normal-priority immense worker, with
  `MaxQueuedImmenseCreates = 8`. *Can backpressure block a worker that holds a lock or a lane?*

### 4.2 Cap arithmetic and the copy path

- The running total is `long`; the caps are `int`. *Where do they meet, and what happens at the boundary?*
- Over-cap state is retained but "cannot grow". *Is every growth path gated, including transform, rescale,
  fill and division changes?*
- **`GuideTransformPacket` can create a COPY.** `GOTCHAS` **G5** establishes that rotate and mirror do not
  preserve voxel count. *Does the copy path validate caps for the new guide, count it against the creator's
  cumulative allowance, and recount rather than reusing the source's cached count?* This is the single most
  specific hypothesis in this brief — the copy is a new guide born through a path that was built for
  in-place edits.
- `LocalGuideAuthority` passes all five caps as 0 explicitly, after once omitting one and silently
  inheriting its default. *Are there other places that construct a cap set positionally?*

### 4.3 Legacy encodings meeting the v0.4.15 re-gesture

Two-point rectangles and three-point boxes are **read in place and never migrated**, while the free
Rectangle is now three clicks and the Box four.

- *Does every edit path handle a Rectangle with two control points as readily as three — grab, insert,
  lock, rescale, transform, spring-back?*
- *`OriginalControlPoints` spring-back on a pre-v0.4.15 guide restores a two-point form. Is that still a
  valid shape to the current code?*
- This is recent (Session 33) and the interaction surface is wide.

### 4.4 Wire handling and privilege

- Retired `LayoutAdminSetting` **6 and 7**: the ledger states the server "no longer accepts them here, so a
  request naming one changes nothing." *Verify it actively ignores them rather than falling through.*
- `LayoutAdminConfigPacket.CanEdit` is documented as advisory only, with the server re-checking privilege on
  every request because a modified client can set any flag on its own copy. *Verify every admin handler
  re-checks — the Session-33 precedent is a packet whose handler was never reached.*
- `PlayerPolicyEditPacket` stages several caps behind one Save. *Is each field validated, or only the
  packet?* `GOTCHAS` **G15**: a GUI edit must do everything the equivalent command does.

### 4.5 A cheap, high-yield sweep

**Grep every `SendIngameError` and every player-facing message construction for `{0}`-style placeholders.**
G11 says eleven were broken and were fixed; this verifies the fix was complete and that nothing added since
reintroduced it. Mechanical, fast, and it has already paid out once.

---

## 5. Out of scope

- **Renderer performance.** That arc is closed and both levers are spent or gated (`GOTCHAS` **R8**).
  Reopen only from a new measured bottleneck.
- **Style, naming, formatting.** This codebase's comments carry design reasoning deliberately; do not
  "tidy" them.
- **The documentation set.** Audited 2026-07-31; `dev/DocCheck.ps1` guards it mechanically now.
- **Anything requiring a running game.** Findings that need a playtest go to `dev/TODO.md` as verification
  items, not as confirmed bugs — the human validates by playing, and this review does not.

---

## 6. What the output should be

1. **Findings ranked by severity**, each with a concrete failure scenario: the inputs or state that produce
   the wrong behaviour, and what the player or server actually sees. A finding without a failure scenario is
   a hunch.
2. **Separate CONFIRMED from PLAUSIBLE.** Confirmed means the path was traced end to end in source.
3. **Anything that would cost time twice becomes a `GOTCHAS` entry**, with its trigger, per the harvest rule.
4. **Open items to `dev/TODO.md`**; delivered fixes follow the normal per-revision zip discipline in
   `CLAUDE.md`.
5. **Do not fix while reviewing.** Read the whole surface first — the Session-33 pattern was several defects
   sharing one cause, and fixing the first hides the shape of the rest.
