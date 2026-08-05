# Session 44 — v0.4.74 → v0.4.81

**Branch:** `Codex`. **DataVersion 13. Protocol 28.** **87 source files** (none added).

This session began as a visual refinement of the Roundover tile and chisel occupancy colours, then exposed a
shared settled-render transition behind three apparently separate symptoms: wireframe guides reverting after
a nearby block edit, one-time lighting changes after placement, and duplicate Dome placement feedback. The
same investigation also restored Dome final ordering and its intended base-rim grab affordance. The last three
revisions renamed the tool to Fillet for players and rebuilt its five-stage HUD copy and layout.

---

## 1. v0.4.74–v0.4.75 — CAD-style icon and readable occupied colours

The Roundover picker glyph became a CAD-style square with one large rounded corner. The rounded construction
edge moved to the requested side, gained a larger radius, and changed from a solid line to a broken line so
the operation reads as a fillet rather than a rounded rectangle. The first dash treatment was too dense at
tile size and was refined later in v0.4.78.

Occupied red, green and blue guide voxels now shift toward cyan. Their roles remain distinct, but the colour
change makes material-filled control voxels visibly different from the same guide cells in empty space.

## 2. v0.4.76 — stable world-edit refresh and single placement feedback

An occupancy refresh rebuilt a guide from a shallow state snapshot and allowed the settled replacement to
forget `IsWireframe`. It also reordered cells by XYZ coordinates instead of preserving the shape's canonical
primitive order. A nearby world-block change could therefore turn a wireframe guide solid, and the first
refresh after placement could change the guide's face overlap, lighting and shadowing.

The refresh snapshot is now deep, preserves wireframe state, records the rendered form, and maps occupancy
batches without reordering shape output. Settled world-edit refreshes retain both the chosen form and the
ordinary renderer's deterministic primitive order.

Large-volume placement had a second ownership split. The client used a conservative threshold estimate while
authority used the exact count; a conservative Dome false positive could leave a pending materialisation
handoff alive after authority completed the ordinary path. Exact authority completion now cancels that pending
handoff, and placement effects have one explicit owner. The result removes the duplicate sound and the paired
late lighting transition.

## 3. v0.4.77–v0.4.78 — Dome final order, base-rim grabbing and verification

Dome progressive materialisation deliberately uses an organic reveal order. That reveal order had leaked into
the final settled mesh, leaving some newly placed Domes permanently splotchy. The final transition now restores
the same X/Y/Z spatial order used by ordinary Dome generation before the settled mesh is built.

The Dome's former circumference drag affordance was also restored narrowly: an exact hit on a visible base-rim
cell maps to the nearer diameter anchor. Empty space and the upper shell do not grab, and other parametric
guides retain the exact-marker rule from v0.4.72.

Verification found a subtle order dependency in this first fix. Restoring spatial order only after marker
claiming changed 44 marker choices across nine orientation/scale cases because equal-distance cells are decided
by first occurrence. Spatial order is now restored before Dome marker claiming. A disposable 465-check harness
then passed for X/Y/Z orientations and scales 1, 4 and 16, comparing progressive final output and roles with
ordinary generation plus exact base-rim acceptance and upper-shell rejection.

The icon's broken curve became widely spaced round dots (`0.2, 2.2`) and was visually inspected at 18, 22 and
42 pixels with `dev/RenderIcon.ps1`.

## 4. v0.4.79–v0.4.81 — player-facing Fillet and a five-stage HUD

The player-facing name is now **Fillet**. Internal `Roundover` type names, saved data and protocol identifiers
remain unchanged, so the rename has no wire or persistence cost.

Placement now presents one numbered instruction at each stage:

1. **Fillet 1/5** — Select corner to fillet.
2. **Fillet 2/5** — Set first fillet side.
3. **Fillet 3/5** — Set second fillet side.
4. **Fillet 4/5** — Set first sweep-path point.
5. **Fillet 5/5** — Continue sweep path, or left-click last point again to finish.

v0.4.80 removed the redundant side measurements and SHIFT reminders from stages 2–4. v0.4.81 gives Fillet's
multi-line state text four dedicated overlay rows with one shared left bound. That keeps the final instruction
aligned without changing the unequal label/value bounds used by every other mode; those overlay rows are
cleared whenever Fillet is not active.

## 5. Verification and release artifact

- Final v0.4.81 Release build: **0 warnings, 0 errors**.
- `git diff --check`: pass before documentation.
- Dome disposable harness: **465 checks passed** across all principal orientations and representative scales.
- Fillet icon rendered and inspected at 18, 22 and 42 pixels.
- `Layout0.4.81.zip`: 42 entries / 375,333 bytes; 39/39 assets; packaged DLL matches Release; archive SHA-256
  `ACDC5D2463A38247AE00DB54D34C3A641BE07E5757FA3207A9D1C253201A91FD`.
- No DataVersion, protocol registration or saved representation changed.

---

## Delivered

- **v0.4.74–v0.4.75** — Added the CAD-style rounded-corner glyph, refined its side/radius/broken curve, and
  shifted occupied red/green/blue guide roles toward cyan.
- **v0.4.76** — Preserved wireframe state and canonical primitive order through occupancy refreshes; unified
  large-volume handoff/effect ownership to prevent duplicate sound and late visual transitions.
- **v0.4.77–v0.4.78** — Kept progressive Dome reveal order out of the settled mesh, restored exact visible
  base-rim dragging, fixed order-dependent marker ties, and opened the icon's dotted spacing.
- **v0.4.79–v0.4.81** — Renamed Roundover to Fillet for players, added the five numbered placement prompts,
  removed redundant HUD rows, and aligned the final multi-line instruction without affecting other modes.

## Decisions

- Preserve `Roundover` internally. A vocabulary improvement in the UI does not justify a save/wire migration.
- Treat progressive reveal order as animation state, never final mesh state. Settled geometry must match the
  ordinary generation path exactly, including marker-role tie behavior.
- Keep Dome circumference dragging as a narrow exact-rendered-cell exception. It restores a useful existing
  affordance without reopening nearest-handle snapping for arbitrary parametric body cells.
- Isolate Fillet's multi-line HUD text in same-bound overlay rows rather than changing the shared context-row
  label widths that other modes rely on.

## Traps

⚠️ **Progressive order can escape into settled rendering, and marker claiming can make the escape semantic.**
Restore canonical order before any first-occurrence tie breaker, not merely before final mesh emission. → G53.

⚠️ **A blank HUD label can still reserve width.** Reusing context rows with unequal label bounds staggers
multi-line prose even when their labels are empty. Same-bound state overlays avoid changing unrelated modes.
→ G54.

⚠️ **Conservative client estimates and exact authority counts cannot independently own completion effects.**
Exact completion must cancel false-positive pending work, with sound/materialisation assigned to one owner.
→ G55.

## Flagged and unverified

**Judgement calls awaiting review:**

- None. The Fillet name, instructions, reduced HUD content, icon spacing and alignment were all selected by
  the human during this session.

**Claims not tested:**

- The renderer/placement corrections are source-traced and covered by the Dome harness and icon render, but
  the final combined v0.4.81 build has not been reported from a fresh multiplayer play session.
- The pre-existing dedicated-server claim, background-save, compound-Transform and immense-cancellation
  verification debts remain unchanged in `STATUS.md` and `dev/TODO.md`.
