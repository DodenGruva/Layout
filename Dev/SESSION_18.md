# SESSION 18 — v0.2.24 → v0.2.28: the Tapered Cylinder, and what it exposed

> The human asked for one new shape — a **Tapered Cylinder** (the windmill/tower silhouette). Delivering it
> surfaced two pre-existing defects that had nothing to do with it (an over-conservative scan guard, and a
> cap clamp that re-solved itself every tick), plus one genuine regression introduced by the new shape's
> fourth click. All are fixed and each was verified numerically against the real shape code, not by eye.
> Current build: **v0.2.28**, **DataVersion 8**, **protocol 7**, **69 C# source files** (one new).

---

## 1. The Tapered Cylinder (v0.2.24) — `GuideShapeType.TaperedCylinder = 12`

A **frustum**: the generalisation of the cylinder and the cone. The cone already shrank its radius linearly
to zero at the tip; the tapered cylinder shrinks it to a **non-zero** top radius instead. Same
centre-banded voxelisation, same lid-handle mechanics, same scan-guard family.

**Placement is FOUR clicks** — the only four-click shape in the mod (human's explicit choice over a
post-placement drag handle or a GUI number field):

1. + 2. the base diameter (the circle gesture), exactly as the cylinder
3. the **height**, projected onto the base's axis (the existing apex machinery)
4. the **rim** — its distance from the axis becomes the lid's radius

`rTop = 0` degenerates to exactly the cone; `rTop = r` to exactly the cylinder. **Verified numerically:**
both endpoints reproduce the other shapes' voxel counts to the unit (11,300 and 21,760 at r=2, scale 1).

New file: `src/Shapes/TaperedCylinderShape.cs` — the 69th source file.

### Control points (four, one more than any other shape)
| # | Role | Behaviour |
|---|---|---|
| 0, 1 | base anchors | resize/recentre; the lid keeps its height and its taper **ratio** |
| 2 | lid handle (Primary) | slides along the axis; the rim rides with it, keeping its radius |
| 3 | rim handle (Primary) | carries a **distance only** — always re-seated on the +û side of the lid ring |

**Decide-and-flag calls (human to review in play):**
- Born taper before click 4 lands: **0.6 × base radius** (`DefaultTopRatio`).
- A base resize preserves the taper **ratio**, not the absolute top width — widening a windmill's foot
  scales the whole silhouette. (Alternative: hold the top width fixed.)
- The top may **flare wider** than the base, up to `MaxTopRatio = 4×` — funnels, not just towers.

### Wiring (the checklist for any future shape)
`GuideShapeType` (+`IsVolume`) · `ShapeFactory` (all three entry points) · `DraftManager`
(`NeedsRimClick`, `_draftThird`, `AwaitingRim`, `PlaceThirdPoint`, step-back, `DraftCompletion.Rim`) ·
`GuideToolController` (4th click stage, ghost, free-air aim, cap clamp) · `GuideRenderer` (rim in the
preview key) · `GuideHud` (rim in the measure + its cache key) · `PacketTypes` (`Rim`, **protocol 6 → 7**) ·
`ClientNetworkHandler` / `LocalGuideAuthority` / `ServerNetworkHandler` / `GuideManager` (`fourthPoint`) ·
`GuideToolGui` (code/name/icon/index — **13 types, 19 tiles**) · `LayoutToolIcons` (`DrawTaperedCylinder`).

**New shared seam:** `DraftManager.ApplyPlacementPoints(shape, type, constraint, apex, rim)` — the single
place that maps "third click → CP 2, fourth click → CP 3". The ghost, the HUD measure, the cap pre-check,
and the server's create path all route through it, so they cannot drift apart. Adopted by the existing
three-click shapes too, replacing four copies of the same `MoveControlPoint(2, apex)` guard.

### Protocol 7
`GuideCreateRequestPacket.Rim` is `[ProtoMember(12)]`, append-only. A 0.2.24+ client against an older
server degrades gracefully (unknown shape id → arch fallback), but the two should be updated together.

---

## 2. The scan guard was the real size limit, not the voxel cap (v0.2.25)

**Symptom (human):** "I run into the drafting cap while my HUD says it's only around 63%."

The HUD was telling the truth. Each 3D shape carries a **scan guard** capping the lattice it will search
(`MaxScanCells = 4,000,000`), returning an "infinitely big" sentinel above it. The cylinder family
estimated its own box as **(2r + |h|) cubed** — i.e. an upright cylinder treated as a cube as tall as it is
wide *plus* its own height. For a born-height cylinder that over-counts the real box by **~7×**.

Measured against the real code at the human's configured 50,000 cap (scale 1, born height = 2r):

| Base diameter | Voxels | % of cap |
|---|---|---|
| 4.5 | 27,936 | 56% |
| 5.0 | **guard trips — drag stops dead** | — |

The guard now measures the **actual AABB the loops walk** (`CylinderShape.ScanCells`, shared by the
family). After the fix the cylinder reaches **100% of cap at a 6.0-block diameter** and the guard stays
silent until ~8.0 — so the voxel cap is the binding limit, exactly as the HUD claims.

Applied to `CylinderShape`, `ConeShape`, `TaperedCylinderShape`. **Sphere and Dome were already correct**
(their AABB genuinely is a cube) and are untouched. `MaxScanCells` itself is unchanged — the guard still
bounds real scan cost, it just measures it honestly now.

---

## 3. The cap clamp re-solved itself every tick (v0.2.26)

**Symptom (human):** "fine at 99%, lagging immensely at 100% and beyond — on the base, the height, and the
rim alike."

`ClampDraftAimToPerGuideCap` (0.2.19) bisected from scratch every tick: **14 full voxel counts per frame**.
Below the cap that was invisible — the first check ("does the cursor position fit?") passed and returned.
The moment the aim crossed the cap that check started failing and the full 12-step search ran *every tick
for as long as the cursor stayed out there*. Measured on a 6-block cylinder at 1/16 scale:

| | per tick |
|---|---|
| Under the cap | ~3 ms (1 count) |
| **Over the cap** | **42–55 ms (14 counts)** |

**Fix — `GuideToolController.CapClampTracker`:** the search now *persists* a bracket (largest reach known to
fit, smallest known to fail) and spends at most **two** checks per tick narrowing it. `_lo` only rises,
`_hi` only falls, so it converges monotonically; once the bracket is sub-voxel it stops checking entirely.
An aim inside the proven-good reach returns the cursor position outright, for **zero** checks — that is the
whole sub-cap path.

Simulated against real voxel counts (push out to 14 blocks against a 6-block limit):

| | before | after |
|---|---|---|
| Cost parked/sweeping past the cap | 42–55 ms **every tick** | **0.000 ms** |
| Worst single tick | 14 counts | 2 counts |
| Total to settle | 14/tick forever | 13 counts, once (~6 ticks) |

Settles at **99.8% of cap**. Applied to the placement ghost **and** to
`ClampDragTargetToPerGuideCap` (dragging a placed point), which had the identical structure.

> **Rejected first attempt, recorded so it is not retried:** a grow/shrink heuristic (reach ×1.12 when it
> fits, ×0.82 when it does not). Same 2-check budget and it converged, but simulation showed the ghost
> **oscillating between 71% and 98%** while sweeping — a visible pulse. A monotonic bracket has no such
> failure mode. *Simulate the feel, not just the cost.*

---

## 4. Regression: the rim stage's shrink reference was the raw height click (v0.2.27)

**Symptom (human):** "the top flares out immensely and I'm unable to adjust it… eventually exceeds the
voxel count and I can't finish the placement… very finicky."

**Self-inflicted, in v0.2.24.** The cap clamp pulls the aim toward a stationary reference, and that
reference must be the shape's *smallest* form. For the rim stage it was set to `_draft.DraftThird` — the
**raw height click**. The shape projects that click onto the axis for the height, but the *rim* measures
width as distance **from** the axis. So a height clicked a few blocks to one side made the reference itself
a wide lid: the minimum reachable top radius became that sideways offset.

Reproduced with a height click 5 blocks off-axis (base r=2):

| Clamp pulls in by | Resulting top radius |
|---|---|
| 0% — fully collapsed | **5.00 blocks** ← the floor |
| 50% / 100% | 8.00 (saturated at `MaxTopRatio`) |

Permanently over the guard, unshrinkable, unfinishable — and most of the aiming range sat saturated at the
flare limit, which is the "finicky" half.

**Fix:** new `GuideToolController.TryGetDraftLid` returns the **lid centre projected onto the axis**; the
rim stage shrinks toward that, where the taper genuinely collapses to a cone. Same case after the fix:
`rTop` sweeps smoothly 0.00 → 3.98, clamping at exactly 100% of cap, and pulling the cursor back in tracks
it one-to-one for zero checks.

### 4b. Free-air rim aiming, twice
- **v0.2.24** reused the generic free-air *height* aim, which samples the view ray at the **base's**
  distance. On a tall tower that lands nowhere near the lid and barely moves the taper — the human's
  original "it requires a block" report (the click always registered; it just did nothing useful).
- **v0.2.25** intersected the view ray with the lid's **plane**. Correct from above, **violently unstable
  from the ground**: near-parallel to the plane the intersection runs off toward the horizon, so a pixel of
  view movement swung the radius by blocks and pinned it at the flare limit.
- **v0.2.27 (shipped):** sample the view ray at the **lid's distance**. Cannot blow up at any viewing
  angle; sweeping the crosshair across the lid moves the radius smoothly and proportionally. Slightly less
  "pointing exactly at the rim", far more controllable — the right trade. A targeted block still wins.

---

## 5. Chalking Powder recipe: raw clay vessels removed (v0.2.28)

**Human report:** the recipe accepted **raw jugs**, which cannot hold liquids.

`game:jug-*` matched both `jug-{color}-fired` and `jug-{color}-raw`. Verified against the game's own
blocktypes: `clay/fired/jug.json` is `BlockLiquidContainerTopOpened`, `clay/raw/jug.json` is a plain
`Block` — it can never carry the 0.1 L of yellow dye the recipe requires. Both jug variants (powder and
flour) are now **`game:jug-*-fired`**.

The **bowl was already correct** (`bowl-*-fired`, and its own `classByType` only makes `*-fired` a liquid
container); `woodbucket` has no raw variant. So the jug was the only over-broad match. Recipe count is
unchanged at **6 permutations**: {powder, flour} × {bucket, fired bowl, fired jug}.

---

## 6. Where this leaves things

- **v0.2.28** is the current shippable build; zips `Layout0.2.24.zip` … `Layout0.2.28.zip` are all in
  `..\Layout Zips\`.
- **Protocol 7** — client and server must be updated together for tapered cylinders to survive the wire.
- **DataVersion stays 8.** A four-control-point shape needs no schema change: `GuideData` already persists
  an arbitrary `List<ControlPoint>`.
- **Not yet playtested:** the recipe fix (v0.2.28) and the rim fixes (v0.2.27) shipped after the human's
  last session. The rim behaviour is verified numerically end-to-end but has not been felt in-game.
- **Carried forward unchanged** from `SESSION_17.md` §8: the xskills 32-chalk verification, the 1.22.0/.1
  smoke test, B-S9-1, and the full F4/public multiplayer regression pass.

### Worth knowing for the next shape
Every defect in this session was **invisible to the compiler and to playtesting-by-feel** — they were
threshold and performance behaviours. Each was pinned by building a throwaway console probe against the
real `Layout.dll` (referencing `VintagestoryAPI.dll` from the install) and printing voxel counts, timings,
and simulated tick-by-tick clamp traces. That loop cost minutes and turned three vague "it feels wrong"
reports into exact numbers. **Do it again rather than guessing.**
