# Layout — Architecture Document (v2.7)

**Supersedes v2.6 — Session-10 delta.** v2.5 consolidated five revisions into the **Settled Decisions
Register** below; v2.6 folded in **Session 9** (extended shape catalog, Divisions overlay, slave-regime
flow). v2.7 folds in **Session 10**: the **icon-tile GUI** (custom Cairo glyphs, native-style scale icons,
compact square tiles), the **B-S10-1 fix** (surface-guide reload shift — unloaded-chunk-aware air-side
probe), the **divisions scroll-wheel** on the game's native number input, a **third tool mode — Edit**
(the setting rows act on the selected guide, replacing the appended per-guide section), and **paired
division markers** on off-cell boundaries. The register and flows below are updated in place; the changelogs
under this header are the quick deltas.

> **Fidelity note (v2.6).** The Session-9 additions were reconstructed from the working conversation during
> the Claude Code migration, not regenerated from source. **Verification pass completed 2026-07-05:** every
> Session-9 identifier (new class / packet / enum-member names, DataVersion, file count) was checked against
> the code on disk and confirmed accurate — the reconstruction held up. The former **⚠ verify** tags have
> been resolved in place here and in `PROJECT_STATUS.md` / `TODO.md` / `SESSION_9.md`. The one stale item was
> this document's own file count and shape-catalog snippets (§1/§2), corrected below.

**Where the project stands:** Layout v0.1.x is a built, playtested mod **deployed to real-played game worlds**
(through Session 9). All seven modules, the Session-8 capability wave, and the Session-9 catalog/divisions/
slave-flow work are against VS 1.22.3 / .NET 10. **One open bug:** lock-in-place (B-S9-1) is unresolved —
the targeted voxel is often not the one locked and the guide still shifts; it is the top priority and does
**not** have a settled design here yet (leading approach: ray-vs-voxel-box cell picking). See `TODO.md`.

---

## Changelog — v2.6 → v2.7 (Session 10; full record in `SESSION_10.md`)

- **Icon-tile GUI:** every option row is now compact SQUARE icon tiles (42 px) — new
  **`UI/LayoutToolIcons.cs`** draws Cairo glyphs registered in `capi.Gui.Icons.CustomIcons`; stock
  `GuiElementToggleButton`s render them, so the exclusive-toggle plumbing is unchanged. Hover shows the
  option name. Delete-mode greying = native `Enabled=false`. New reference: `Lib\cairo-sharp.dll`.
- **Scale icons copy the game's native scheme:** an N×N grid of squares where **N is the voxel count**
  (1×1 smallest … 16×16 = a full block, drawn as ONE solid square filling the button); 4×4+ run
  edge-to-edge. Names are voxel counts, not fractions.
- **B-S10-1 FIXED (playtest-confirmed):** surface guides no longer shift behind block faces on reload —
  the air-side probe detects unloaded chunks and a 500 ms re-probe tick rebuilds once the area loads
  (see the Rendering register entry).
- **Divisions scroll-wheel (deferred Session-9 TODO) DONE (playtest-confirmed):** dropdown removed; the
  field is the native `GuiElementNumberInput` (built-in wheel + spinner buttons, `IntMode`, ±1/notch). The
  first attempt — a plain-text-field + dialog wheel override — failed (text inputs have no native wheel
  handler). **0.1.13** floors it at 0: the number input has no min, so `OnDivisionsTyped` snaps the display
  back on clamp (never shows negative or over-`MaxDivisions`).
- **Third tool mode — Edit (0.1.13):** `ToolMode` is now `Create · Edit · Delete`. In Edit the main setting
  rows act on the SELECTED guide (network senders) instead of the tool defaults, so per-guide editing no
  longer expands the panel with a separate section. Edit clicks **select only** (no reshaping); geometry
  editing stays in Create. See the Interaction-model register entry.
- **Division markers pair on off-cell boundaries (0.1.13):** `ShapeGeometry.ClaimMarkerPaired` claims the two
  near-tied cells when a boundary lands between voxels — the arch apex's even-span treatment, generalised.
- **Process:** every revision bumps `modinfo.json` and ships as a new `Layout<version>.zip` (0.1.10 → 0.1.11
  → 0.1.12 → 0.1.13 this session); older zips are never overwritten.

---

## Changelog — v2.5 → v2.6 (Session 9)

- **Shape catalog extended (F1 first wave):** **Line** (two anchors, no fill), **Triangle** (two base
  anchors + a born, draggable apex; free = scalene) with **Right / Equilateral / Isosceles** apex-derivation
  constraints, and **Rectangle** (two diagonal corners stored, other two derived) with the **Square**
  constraint. `GuideShapeType` and `ShapeConstraint` extended (pinned, append-only). New shared
  **`ShapeGeometry`** helper (planar frame + marker-claim). **DataVersion 4 → 5.** *(identifiers verified.)*
- **Body-click policy generalized:** only the **arch (free spline) family** takes body inserts; every other
  parametric shape (ellipse, line, triangle, rectangle) maps a body click to the **nearest handle**
  (left = grab, right = lock-toggle).
- **Divisions (new feature):** a per-guide, purely visual equal-parts overlay — recolors voxels at N
  arc-length boundaries (**magenta**, `VoxelRenderType.Division`), computed **renderer-side**
  (`DivisionMarks.Apply`) as a pure recolor that never touches geometry, counts, or caps.
  `GuideData.Divisions`; additive DTO fields; `GuideSetDivisionsPacket`; `SetDivisionsCommand`;
  `GuideManager.SetDivisions` (clamp + persist, no cap check); `MaxDivisions = 256`. *(identifiers verified.)*
- **Soft-point flow → slave-regime (supersedes the v2.5 proportional-only model):** an **interior grab**
  (held point unlocked, non-anchor) slaves every other unlocked point **onto the defining curve at its
  station with zero offset** — the apex genuinely contributes no pull; a **structural grab** (anchor or
  locked point) keeps the shape-preserving proportional flow. Plus a **chord-invariant phantom drop**
  (arch end-tangent phantoms derive from the anchor chord at 0.4×chord, not the neighbor knot's height), so
  interior inserts/locks no longer re-tilt the whole curve.
- **Deferred:** divisions scroll-wheel (drop the dropdown, keep the type-in field, wheel-adjusts-while-focused)
  — specced, not built. **Unresolved:** B-S9-1 lock-in-place.

---

## Overview — what Layout is

Layout is a mod for Vintage Story 1.22.3 (C#/.NET 10) that provides a persistent, visual, voxel-resolution
construction planning tool — a CAD-like drafting assistant inside the game. Players place geometric guide
overlays in the world and use them as visual references while building by hand; the mod never places, removes,
or modifies blocks automatically. Guides are visual-only, rendered as translucent voxel meshes that are
world-shared and visible to all players on a server, persist across logout and chunk unloading, and are stored
server-side with server-authoritative networking. A guide is only *referenced off* a block at placement time,
never bound to it, and is visible-but-untargetable when the tool isn't held, so it never interferes with the
blocks underneath.

The tool is a held item with an F-key **tile menu** (Create/Edit/Delete mode, the shape picker, voxel scale
1×1×1–16×16×16 defaulting to the finest to match chisel resolution, projection, plane, fill); all interaction
uses first-person clicks and crosshair targeting rather than transform gizmos. The **shape catalog** is
arch · half-circle · circle · ellipse, built on the primitives+constraints model (a half-circle is an arch
under a SemiCircle constraint; a circle is an ellipse under a Circle constraint); every shape is placed with
the same two-click gesture. Players reshape guides by grabbing points (clicking the body inserts-and-grabs in
one motion on the arch family, or grabs the nearest handle on the ellipse family), locking points as
constraints, and relying on two standing contracts: **absorb-or-break** (a grab a constraint can absorb, it
absorbs; one it cannot absorb demotes the shape to its free parent, seamlessly and undoably) and **soft-point
flow** (unlocked interior points flow proportionally with the structural anchors+locks — locking is the only
thing that pins geometry). Each guide can additionally be rescaled, toggled between volumetric (3D) and
surface (flat decal) projection, set hollow or **filled** (arch family: the region closed by the foot-to-foot
chord; ellipse family: the disc), and hidden or shown.

Colors: yellow body, red locked points, green apex/primary, blue anchors (indigo off-shade for a non-coplanar
far foot), white grabbed; hidden guides show only anchors at reduced opacity. In-progress drafts are
client-side until completed — only the start point is broadcast to other players during placement, and drafts
cancel on logout. Concurrent editing stays safe via full-exclusivity edit locks (first grab wins), voxel caps,
and full per-player undo/redo for all actions including deletion.

---

## Settled Decisions Register (do not reopen without cause)

The distillate of five design revisions and two playtest cycles. Each entry is a settled decision plus the
reason it won. Reversing any of these needs an explicit call from the human, not a fresh session's instinct.

### Interaction model
- **Three modes: Create | Edit | Delete** (Session 10; was two). **Create** owns ALL geometry — left-click
  priority chain: release grab → second foot (a draft outranks guide targeting) → precise point grab → body
  insert+grab in one gesture → first anchor; right-click is the universal cancel / draft discard / idle point
  lock-toggle / idle body **lock-in-place insert** (point born locked on the curve; two undo steps).
  **Edit** is settings-only: left-click **selects** the guide under the crosshair (empty click deselects) and
  the GUI's setting rows then act on that selected guide — **no grab/insert/lock**, so a select-click can
  never reshape. **Delete** dispels (left-click). This restored an Edit mode (the earlier Create/Edit/Lock/
  Dispel scheme was collapsed to two in Session 8 because modes fought the flow; the new Edit adds no geometry
  verbs, so it doesn't). Settled reason: per-guide editing via the buttons needed a home that didn't expand
  the panel with a separate section.
- **No grab snap radius.** Precision is the tool's ethos; a near-miss on the body inserts (that's intent,
  not error), and the accepted miss-case is a stray anchor + right-click.
- **Targeting tests the sampled curve, not control-point chords** (per-guide fingerprint-cached polylines).
  Chords miss the real curve at an arch's feet — the root cause of near-anchor grabs failing.
- **Absorb-or-break** is the constraint contract: half-circle feet and circle diameter anchors absorb;
  a body insert on any constrained shape, or dragging a circle's minor handle, breaks to the free parent —
  server-authoritative, seamlessly materialised, one undo step, instant client preview via a sanctioned
  mirror constraint-clear.
- **Soft-point flow — slave-regime (Session-9 settled model).** Structural = anchors + locked; everything
  else is soft. An **interior grab** (held point soft) slaves every other soft point **onto the defining
  curve at its station with zero offset** — the apex and prior-grabbed points contribute **no** pull, so the
  shape follows the hand; a **structural grab** keeps shape-preserving proportional flow (baseline-local,
  length-scaled offsets) so nudging a foot stretches rather than flattens. Locking is the only pin. History:
  absolute offsets (rejected in play) → proportional (still felt like pinning) → slave-regime (confirmed).
- **Chord-invariant arch phantoms.** The arch's end-tangent phantom drop derives from the anchor chord
  (0.4×chord), not the adjacent knot's height, so inserting or locking a point near a foot no longer
  re-tilts the whole curve.
- **Ellipse family body clicks map to the nearest handle** (grab on left, lock-toggle on right) — a
  parametric ring has nothing to insert. *(Flagged for review, like all Session-8 ellipse ergonomics.)*
- **Comatose grabs:** tool swap suspends a grab (lock + one-undo-entry drag persist server-side), never
  releases it; re-equip validates and resumes. Players need to place ladders mid-drag.
- **CTRL cardinal constraint** (Session 11, human-directed — moved from SHIFT) in two places: drafting the
  second foot (level + cardinal from the first) and re-grabbing an anchor (same snap, referenced to the
  guide's **other** anchor).
- **SHIFT is context-dependent** (Session 11, human-requested; supersedes "SHIFT = cardinal"): **while
  drafting**, holding SHIFT inverts the ghost upside-down (arch opens downward; equilateral apex mirrors
  below the base) and the completing click bakes it — except on a THREE-click triangle's apex stage, where
  SHIFT **centres the apex on the base** (0.1.15), and on a **Free-Shape chain**, where SHIFT pins the next
  segment **VERTICAL** off the previous corner (0.1.16; CTRL stays horizontal, SHIFT wins if both);
  **on a placed guide**, SHIFT+left-click **springs it
  back to its as-placed form** (points + constraint restored from the creation snapshot, one undo step).
  Prerequisite delivered with it: **every shape defaults "up"** regardless of click order (`ShapeGeometry`'s
  frame perpendicular is sign-normalised world-up; constrained triangle re-derivations preserve the apex's
  current side instead of forcing one).
- **Raycast targeting throughout:** anchors require a block target; interior points snap to blocks or move
  at retained grab-depth in air; no scroll-wheel grab-distance (scroll reserved).
- **Guides are visible but untargetable when the tool isn't held** — pure mesh draws, no selection/collision
  geometry, all interaction gated to the held-item path.

### Data & wire
- **Pinned, append-only enums** everywhere a value crosses wire or disk; **default-driven migration** via
  `DataVersion` (currently **7**: v7 added `IsClosed` (Free-Shape loop flag); v6 added `Sides` + the
  as-placed spring-back snapshot (`OriginalControlPoints`/`OriginalConstraint` — persisted, never wired);
  v5 added `Divisions`; v4 added `Constraint`, `ShapePlaneAxis`; v3 added `CreatorUid`; v2 added
  `Projection`/`Plane`/`IsFilled`).
- **Protobuf DTOs are the wire format; JSON is the save format** — never mixed. Packet registration is one
  fixed shared order, **append-only**. POCOs are mapped to DTOs, never sent raw.
- **The index seam:** only control-point indices cross the network/undo boundary
  (`GetNearestControlPointIndex`, edits, commands). Inserts land interior; locks prevent reshuffling mid-grab.
- **Vec3d is always deep-copied, never aliased** (mutable reference type). The one sanctioned mirror writer
  outside the network handler is the drag preview (plus its Session-8 sibling, the local constraint-clear).
- **Voxel cells are 1/16-block, lower-corner, `Floor(world·16/scale)·scale`** — one quantise convention
  everywhere (`VoxelMarch` mirrors `CatmullRomSpline.Quantize` exactly), or caps and visuals disagree.
- **The server builds shapes** from two clicks + settings (client never sends full GuideData); **tool state
  is client-side** and travels with operations; `CreatorUid` is bookkeeping, never ownership, never wired.
- **Voxels are never stored** — always derived on demand from control points.

### Shapes & geometry
- **Primitives + constraint modifiers, not a flat enum of near-duplicates** (square = rectangle+constraint,
  circle = ellipse+constraint, half-circle = arch+constraint, and the triangle constraints below). Keeps
  `GuideShapeType` short and lets future favorites store {type + constraint} pairs.
- **The catalog (v0.1.23 state):** arch, half-circle, circle, ellipse, **line, triangle
  (+ right / equilateral / isosceles), rectangle (+ square), polygon (regular N-gon, side count = per-guide
  data, 3–24), Free-Shape (irregular polyline)**, and the **3D VOLUME family (v0.1.20–0.1.21): sphere, dome,
  cylinder, cone, box** (see the dedicated bullet below). **Placement is two clicks for every shape EXCEPT the
  free/right/isosceles triangles (THREE: anchor · anchor · height — SHIFT on the third click centres the
  apex on the base) and the Free-Shape (UNBOUNDED chained clicks, ≤64: click the LAST placed corner to
  finish open, the FIRST corner (≥3) to close the loop; the aim snaps onto those targets)** — the human
  explicitly reopened the old "every shape is two clicks" rule in Session 11. Right-click steps any
  multi-click draft back one click. Equilateral stays two-click (its apex is fully derived). The polygon's
  two clicks span vertex → opposite perimeter point, so the shape exactly spans the gesture and both
  anchors sit ON the outline. **Constraints are derivation rules:** the triangle apex slides (right →
  perpendicular at first click; isosceles → base bisector) or is fully derived (equilateral), and the
  rectangle's derived corners track the diagonal (square → dominant component).
- **Body-insert policy (0.1.15 revision):** the arch family AND the Free-Shape take body inserts
  (`TakesBodyInserts`); every other shape is parametric — body clicks map to the nearest handle. The
  Free-Shape's inserts are plain straight-line corners (no spline, no soft flow); its fill is DEFERRED
  (irregular outlines can be concave; the Fill toggle is currently inert on it).
- **Break gestures (v1):** equilateral-triangle apex-drag breaks to a free triangle, and circle minor-handle
  drag breaks to an ellipse (the absorb-or-break pattern). **Right / isosceles / square have no break
  gesture** — their constrained drags always absorb; they live as separate catalog tiles.
- **`ShapeFactory` is the single shape construction point** — adding a shape touches the factory + the shape.
  **`ShapeGeometry`** (Session 9) and **`VoxelMarch`** are the shared shape-layer helpers (planar frame +
  marker-claim; and the one true cell-quantise convention, respectively).
- **Centripetal Catmull-Rom (α = 0.5)**, apex at 40% of chord above the midpoint, phantom endpoints derived
  for provably vertical feet.
- **Arch-family fill = the region between the curve and the foot-to-foot chord line** (human-confirmed
  design), as a ruled surface at ≤ half-cell steps; **ellipse-family fill = the disc**; **triangle fill = the
  interior, rectangle fill = the box**; **line has no fill**. Caps count filled voxels **exactly**
  (generated, not estimated) — correctness over performance, per standing rule.
- **Circle → ellipse is the break floor** (v1): an ellipse does not break further into a free closed spline.
  Likewise a free triangle / free rectangle is the floor for its family (no further break in v1).
- **3D VOLUMES — DELIVERED (v0.1.20–0.1.23; the "planar-only, 3D LATER" decision was reopened by the human
  and shipped).** `GuideShapeTypes.IsVolume` gates the family. **Sphere / Dome** = two clicks (a diameter /
  a base diameter); **Cylinder / Cone / Box** = three clicks (base, then a height click — reusing the
  triangle's apex machinery, `NeedsApexClick`). Box is a true box (independent side lengths). **Hollow = a
  one-cell shell, Filled = the solid**, voxelised by a **cell-lattice scan** (not curve-marching): exact
  surface-crossing (sphere/box/dome) or centre-banded (cylinder/cone), with a `MaxScanCells` **scan guard**
  that short-circuits absurd fine-scale sizes so the cap rejects them without a freeze. Volumes are **always
  Volumetric** (Surface + Divisions gated off server-side and greyed/hidden in the GUI). The base plane / axis
  comes from the clicked face; the axis is the **deterministic `ShapeGeometry.BaseNormal`** (+up regardless of
  anchor order — SHIFT is the only invert, e.g. dome → bowl). **Targeting is a wireframe** (equator/meridians,
  rings + verticals, box edges), not every shell cell — the anchors and the height handle are the reliable
  grab points. Height may be set in **free air** (no block → the handle follows the view ray; a targeted
  block wins). No new persisted/wire fields — volumes reuse `ControlPoints` + `ShapePlaneAxis`; enum values
  appended, **DataVersion stays 7**. Natural next volumes: **Roof, Tunnel**.
- **Fill is a guide property (`IsFilled`), constraints are modifiers — neither is a shape type.**

### Rendering
- **The verified draw recipe:** Opaque stage + manual blend (not OIT); `PreparedStandardShader` overridden to
  full-bright; a **real white texture** (generated 2×2, asset fallback — id 0 samples garbage); the full
  pos+**uv**+rgba vertex layout with uv (0,0). Each element was a genuine independent playtest bug.
- **Single-voxel nearest-claim markers** (anchors/apex/locked/grabbed-White), precedence Locked > Primary >
  Anchor > Division; the apex claims **2 voxels on even spans** (a lone voxel reads off-center by half a
  cell). **Division marks share that even-span pairing** (Session 10, `ShapeGeometry.ClaimMarkerPaired`): a
  boundary landing between two voxels claims both, so the equal parts read even; boundaries on a cell centre
  stay single.
- **Surface guides render as paper-thin slabs (0.01)** hugging the wall face on the **air side** (world
  solidity probe; majority fallback) with a **plane-axis-only** inset; volumetric anti-z-fight via a
  whole-mesh 0.003-block camera nudge per frame. **The air-side probe tolerates the world-load race
  (Session 10):** if a probed cell's chunk isn't loaded yet, the guide's side is *provisional* and a
  low-frequency re-probe tick rebuilds it once the neighbourhood loads — otherwise a guide meshed before its
  blocks arrived sank behind the face on reload (B-S10-1).
- **Settled guides always render at their true scale**; `ChooseRenderScale` coarsening (8,000-voxel cap) is
  a **draft-ghost-only** courtesy — it once leaked into placed guides and permanently degraded them.
- **Leaving Surface bakes the flattened positions into the control points** (undoable, full-state
  broadcast): Surface-mode edits are made against the view, so the view is what leaving it keeps. Returning
  to Surface restores the stored plane; only never-Surface guides get the floor-at-anchor seed.
- **Color language:** yellow body · red locked · green primary/apex · blue anchors with the **indigo
  off-shade** on a far foot that is not level-and-cardinal (an at-a-glance "is this clean?" cue, deliberately
  shifted violet-ward away from green) · white grabbed · hidden guides = anchors only at low alpha. All six
  type opacities are client-configurable.

### Multiplayer, locks, undo
- **World-shared, no ownership.** Any player may edit or dispel any guide; grief is a server-administration
  concern, deliberately out of scope.
- **Full-exclusivity edit locks:** while held, every mutation from anyone else is rejected — geometry,
  toggles, and dispel. `adminCanOverrideLocks` (default true) lets admins override the **atomic** ops only
  (the stuck-lock remedy); geometry genuinely requires the lock. Undo respects the same gate (`Blocked`,
  history preserved).
- **One drag = one undo entry** (live ~10 Hz moves record nothing; release commits origin→final per point —
  soft-flow points included, since origins are captured generically per edited index).
- **Broadcast to everyone, the originator included**, so every mirror stays exact; rejections get a
  corrective full-state resync. **Undo/redo broadcasts generically:** full current state, or a delete if the
  guide is gone — one rule for every command type.
- **Break and bake are single undo steps carrying point snapshots** (`BreakConstraintCommand`; the
  projection command's optional pre-bake snapshot) — the constraint/projection must travel with the exact
  points it had.
- **Systems communicate by return value (`GuideOperationResult`), not events**; undo is validate-then-apply
  with stale-command skip and the `Blocked` outcome for cap-rejected-but-valid commands.

### Configuration, assets, GUI
- **Server `layout.json`:** perGuideVoxelCap 25,000 · totalVoxelCap 250,000 · maxGuidesPerPlayer 0 ·
  maxGuidesWorldWide 0 · undoHistoryDepth 50 · requiredPrivilege "" · adminCanOverrideLocks true
  (0/negative = unlimited; **construction-time injection — edits need a server restart**). Caps sync to
  clients on join so the pre-check matches enforcement. **The running total is a `long`** (v0.1.27) so a
  caps-off server can't overflow it negative. A **hard voxel ceiling** (`GuideManager.HardVoxelCeiling`,
  10M) rejects un-renderable giant guides ALWAYS, even with caps disabled — the 3D scan guard returns a
  huge sentinel count for over-size volumes, and without this ceiling a caps-off server could create
  invisible giants that silently max the world total. Server-side create rejections send a **clear in-game
  error** (the HUD cap-flash is keyed to a guide id that doesn't exist yet on a create, so it was silent).
- **Admin commands (v0.1.26–0.1.27):** **`/layout dispel all`** (whole world) and **`/layout dispel <chunk
  radius>`** (Chebyshev radius around the caller), both `controlserver`. Namespaced under `/layout` so they
  can't clash with other mods. Registered in `ServerNetworkHandler`; they delete + force-free locks +
  broadcast, and reset the running total.
- **Client `layout-client.json`:** remembers scale / projection / fill / **shape + constraint** (validated
  pairs) / divisions / **sides** plus the six opacities and (Session 11) the **pinned favorite shape codes
  (up to FOUR since 0.1.15; hard-kept — never auto-padded)**; client-retained, never synced. **Default
  scale 1** (chisel-matched).
- **The tile GUI (icon form since Session 10):** every control is a row of exclusive SQUARE ICON tiles
  (custom Cairo glyphs — `LayoutToolIcons` — rendered by stock toggle buttons; hover names the option,
  auto-sized since Session 11). **Mode-aware rows (Session 10, replacing the appended Selected-guide
  section):** in **Create** the rows are the tool defaults for the next guide (Mode — **with the 0.1.15
  Current Shape chip at its far right: a permanently-lit, guide-body-YELLOW glyph of the picked shape** —
  the shape picker — **0.1.15: FOUR hard-kept pinned slots + a ▾ catalog fold-out; right-click PINS into a
  free slot (never evicts; message when full) or UNPINS a pinned tile (drawn in the Current-Shape YELLOW
  in the catalog since 0.1.17); empty slots show placeholders; the separate Favorites strip is gone** —
  Scale, Projection+Fill (one row since 0.1.17; **Fill greys out on a Free-Shape**, where fill is
  deferred), Plane, Divisions+Sides-on-polygon (one row)); in
  **Edit** the SAME rows (plus **Visibility**, minus the shape picker) act on the
  selected guide via the network senders — greyed with a "click a guide" prompt when none is selected, with a
  compact guide-info line + **Deselect**; **Delete disables everything but Mode** (native `Enabled=false` dim
  + ghost labels). The panel therefore never grows a second section on selection. **Scale icons = the game's
  native N×N-grid scheme, N = voxel count** (16× = one solid block). **Divisions = a native number input**
  (wheel ±1, spinners, typed, floored at 0, clamped to `MaxDivisions`) — the one non-icon control. F-modal
  press-to-open, not the vanilla radial. Draft settings are **live** — mid-draft changes apply to the ghost
  and the completed guide.
- **Hotkeys are rebindable and inert unless the tool is held** (Ctrl+Z/Y never hijack other UIs). Item art
  borrows the vanilla abacus; recipe 6 sticks + 3 any-metal nuggets; infinite durability.
- **Runtime is .NET 10** (VS 1.22); `Entity.SidedPos` is obsolete — use `Pos`.

---

## 1. File Structure

```
Layout/
├── modinfo.json
├── assets/
│   └── layout/
│       ├── itemtypes/
│       │   └── guidetool.json
│       ├── textures/
│       │   └── items/
│       │       └── guidetool.png
│       └── lang/
│           └── en.json
└── src/
    ├── LayoutModSystem.cs
    ├── Items/
    │   └── ItemGuideTool.cs
    ├── Guide/                            [pure data]
    │   ├── GuideData.cs                  [DataVersion 5]
    │   ├── ControlPoint.cs
    │   ├── VoxelPosition.cs              [VoxelRenderType: … Grabbed, Division (magenta, S9)]
    │   ├── GuideShapeType.cs             [Arch, Ellipse, Line, Triangle, Rectangle]
    │   ├── ShapeConstraint.cs            [None, SemiCircle, Circle, Right, Equilateral, Isosceles, Square]
    │   ├── ProjectionMode.cs
    │   ├── ProjectionPlane.cs
    │   └── GuideRenderSettings.cs
    ├── Shapes/                           [pure math]
    │   ├── IGuideShape.cs
    │   ├── CatmullRomSpline.cs
    │   ├── ArchShape.cs                  [free spline + SemiCircle arc mode + ruled fill]
    │   ├── EllipseShape.cs               [closed planar primitive; Circle = constraint]
    │   ├── LineShape.cs                  [S9: two anchors, no fill, insert no-op]
    │   ├── TriangleShape.cs              [S9: base anchors + born apex; Right/Equilateral/Isosceles]
    │   ├── RectangleShape.cs             [S9: diagonal corners stored, other two derived; Square]
    │   ├── ShapeGeometry.cs              [S9: shared planar frame + nearest-claim marker helper]
    │   ├── ShapeFactory.cs               [the single shape construction point]
    │   ├── SoftPointFlow.cs              [S9: slave-regime (interior grabs) + proportional (structural); both sides]
    │   ├── DivisionMarks.cs              [S9: renderer-side equal-part recolor; MaxDivisions = 256]
    │   └── VoxelMarch.cs                 [shared cell marching, spline-identical quantise]
    ├── Systems/
    │   ├── GuideManager.cs
    │   ├── GuideLockManager.cs
    │   ├── DraftManager.cs
    │   ├── UndoManager.cs
    │   ├── GuideRenderer.cs
    │   └── GuideMeshBuilder.cs
    ├── Network/
    │   ├── PacketTypes.cs
    │   ├── ServerNetworkHandler.cs
    │   └── ClientNetworkHandler.cs
    ├── UI/
    │   ├── GuideToolGui.cs               [icon-tile GUI (S10)]
    │   ├── LayoutToolIcons.cs            [S10: Cairo glyphs → CustomIcons registry]
    │   └── GuideHud.cs
    ├── Config/
    │   ├── LayoutServerConfig.cs
    │   └── LayoutClientConfig.cs
    ├── Client/
    │   └── GuideToolController.cs
    └── Undo/
        ├── IGuideCommand.cs
        ├── UndoStack.cs
        └── Commands/
            ├── CreateGuideCommand.cs
            ├── DeleteGuideCommand.cs
            ├── MoveControlPointCommand.cs
            ├── InsertControlPointCommand.cs
            ├── LockPointCommand.cs
            ├── RescaleGuideCommand.cs
            ├── HideGuideCommand.cs
            ├── SetProjectionCommand.cs   [optional pre-bake point snapshot]
            ├── SetFilledCommand.cs
            ├── SetDivisionsCommand.cs    [S9: old/new count; undo/redo re-applies]
            └── BreakConstraintCommand.cs
```

**59 source files** (43 at Session-8 end + 6 new in Session 9: LineShape, TriangleShape, RectangleShape,
ShapeGeometry, DivisionMarks, SetDivisionsCommand; + 1 in Session 10: LayoutToolIcons; + 4 in Session 11:
PolygonShape, SetSidesCommand, SpringBackCommand, FreeShape; + 5 for the 3D family (v0.1.20–0.1.21):
SphereShape, DomeShape, CylinderShape, ConeShape, BoxShape). Namespaces match
folders: `Layout`, `Layout.Guide`, `Layout.Shapes`, `Layout.Systems`,
`Layout.Network`, `Layout.UI`, `Layout.Config`, `Layout.Items`, `Layout.Client`, `Layout.Undo`,
`Layout.Undo.Commands`. (`UndoManager` is the one file whose folder differs from its namespace: it lives in
`src/Systems/` as `Layout.Systems.UndoManager`.)

---

## 2. Data Model

### GuideData
Canonical persistent record for one guide. Server-side in `GuideManager`; sent to clients as needed.
**Never** stores baked voxel arrays — voxels are always derived on demand.

```
GuideData {
    Guid              Id
    GuideShapeType    ShapeType
    ShapeConstraint   Constraint        // None | SemiCircle | Circle (absorb-or-break)
    PlaneAxis         ShapePlaneAxis    // the ellipse family's INTRINSIC plane normal (first-click face)
    List<ControlPoint> ControlPoints
    int               VoxelScale        // 1, 2, 4, 8, or 16
    bool              IsHidden
    ProjectionMode    Projection        // Volumetric | Surface
    ProjectionPlane   Plane             // the Surface PROJECTION plane (≠ ShapePlaneAxis)
    bool              IsFilled          // hollow vs filled (Tier 2, built)
    string            CreatorUid        // nullable; bookkeeping only, never ownership, never wired
    int               DataVersion       // 6 (const CurrentDataVersion); older saves migrate by defaults
    int               Divisions          // Session 9: visual equal-parts count (0/1 = none)
    int               Sides              // Session 11: polygon side count (3–24; 0 on other shapes)
    bool              IsClosed           // Session 11 (0.1.15): Free-Shape loop flag (false elsewhere)
    List<ControlPoint> OriginalControlPoints  // Session 11: as-placed snapshot for SHIFT spring-back
    ShapeConstraint   OriginalConstraint     //   (persisted, NEVER wired; null on pre-0.1.14 guides)
}
```

The same `List<ControlPoint>` instance is shared with the guide's shape — built exclusively via
`ShapeFactory`. `Constraint`, `Projection`, `Plane`, and `IsFilled` are per-guide and mutable after creation.
`GuideData.Create(...)` adopts a control-point list by reference, assigns a Guid, stamps the version;
`DeepClone()` produces a fully independent copy with the same Id (each `ControlPoint` cloned) — the snapshot
mechanism undo relies on.

### ControlPoint
```
ControlPoint {
    Vec3d   WorldPosition   // mutable reference type — always deep-copied; never aliased
    bool    IsLocked        // Red — structural (pins geometry)
    bool    IsPhantom       // math only, never rendered or grabbed
    bool    IsAnchor        // start/end — Blue — structural
    bool    IsPrimary       // apex / minor handle — Green — SOFT (flows)
}
```

### VoxelPosition
```
VoxelPosition {
    int X, Y, Z            // 1/16-block units (world × 16), lower corner, scale-independent
    VoxelRenderType Type   // Normal | Locked | Primary | Anchor | Grabbed | Division (magenta, S9)
}
```
In Surface mode the same struct renders as a paper-thin slab on the plane.

### GuideShapeType / ShapeConstraint
Pinned, append-only. Constrained variants are **not** types; fill is **not** a type.

```
enum GuideShapeType  { Arch = 0, Ellipse = 1, Line = 2, Triangle = 3, Rectangle = 4 }
enum ShapeConstraint { None = 0, SemiCircle = 1, Circle = 2, Right = 3, Equilateral = 4, Isosceles = 5, Square = 6 }
```
The eleven-tile catalog (type, constraint): Arch = (Arch, None) · Half-circle = (Arch, SemiCircle) ·
Circle = (Ellipse, Circle) · Ellipse = (Ellipse, None) · Line = (Line, None) · Triangle = (Triangle, None) ·
Right = (Triangle, Right) · Equilateral = (Triangle, Equilateral) · Isosceles = (Triangle, Isosceles) ·
Rectangle = (Rectangle, None) · Square = (Rectangle, Square).

### ProjectionMode / ProjectionPlane / GuideRenderSettings
Unchanged since v2: `ProjectionMode { Volumetric, Surface }`; `ProjectionPlane` = a `PlaneAxis`
(X = 0, Y = 1, Z = 2) + an offset in 1/16-block units, with `Horizontal` / `VerticalNorthSouth` /
`VerticalEastWest` factories and a `Default`; `GuideRenderSettings` bundles scale + projection + plane + fill
+ divisions (S9) for creation requests and the draft ghost.

---

## 3. Module Map

### `LayoutModSystem.cs`
Entry point and composition root: registers systems, the tool item, network channels, keybinds, and HUD on
both sides; owns the single shared instances per side; seeds `DraftManager` from client config
(scale/projection/fill/shape) and persists them back. All keybinds are rebindable, none hard-coded.

### Items — `ItemGuideTool.cs`
Stateless glue (VS items are singletons); all interaction lives in `GuideToolController` (below). F opens the
GUI; clicks route to the controller.

### Client — `GuideToolController.cs`
The interaction brain, per-tick while held (30 ms):
- **Left-click priority chain (Create):** release grab → second foot → point grab (radius
  `max(0.10, voxel)`) → body hit (arch family: insert+grab in one gesture; ellipse family: nearest-handle
  grab) → first anchor. **Edit:** release grab → SELECT the aimed guide (empty click deselects) — select-only,
  no grab/insert/lock. **Delete:** dispel the aimed guide.
- **Right-click (Create only):** cancel grab (insert-born point removed; pre-existing point snaps back via
  `GuideCancelGrabPacket`); discard draft; idle point → lock toggle; idle body → lock-in-place insert
  (ellipse family: nearest-handle lock toggle). Edit/Delete have no right-click action.
- **Targeting** tests real points plus the **sampled curve** (`IGuideShape.SampleCurve`), cached per guide
  behind a content fingerprint (count + coordinate sum + constraint) — resampled only on change.
- **Drag:** raycast target (anchors need a block; interior points retained-depth), local mirror preview +
  ~10 Hz sends; **soft-flow preview** runs the same `SoftPointFlow` math locally; **SHIFT** on an anchor
  drag constrains to the cardinal line through the other anchor; a grab that a constraint can't absorb
  clears the mirror constraint immediately (server confirms).
- **Draft:** first click captures the intrinsic plane axis from the block face; ghost preview via the placed
  pipeline with live settings; completion sends shape + constraint + plane with the two points.
- Comatose grabs on tool swap; adopt-as-grab handshake for server-side inserts; hotkeys inert unless held.

### Shapes (pure math — only depends on `Vec3d`)

**`IGuideShape.cs`** — the seam decoupling everything from any specific shape:

```
interface IGuideShape {
    List<ControlPoint>  ControlPoints                              // the shared list (GuideData binding)
    ShapeConstraint     Constraint
    List<VoxelPosition> GetVoxelPositions(int scale, bool filled)
    int                 GetVoxelCount(int scale, bool filled)      // exact, == positions count
    float               GetNearestT(Vec3d worldPos)
    Vec3d               GetPointAt(float t)                        // lock-in-place lands ON the curve
    List<Vec3d>         SampleCurve(int samples)                   // targeting polyline (closed shapes close)
    int                 GetNearestControlPointIndex(Vec3d worldPos)
    void                InsertControlPoint(float t, Vec3d position)
    void                MoveControlPoint(int index, Vec3d newPosition)
    void                RecalculatePhantomPoints()
    bool                WouldBreakOnMove(int index)                // constraint can't absorb this drag
    bool                BreakConstraint()                          // demote to free parent, materialising
}
```

**`CatmullRomSpline.cs`** — centripetal Catmull-Rom (α = 0.5); `Evaluate`, `EvaluateTangent`, `GetArcLength`,
`SampleVoxelPositions(scale)`, `CountVoxels(scale)`. Tangent = secant, which makes the arch's phantoms give
provably vertical feet.

**`ArchShape.cs`** — the open-curve primitive over the spline; owns the shared point list. Free arch: 5-point
spine (phantom, anchor, apex/primary, anchor, phantom), apex at 40% of chord. **SemiCircle mode:** stores
only its feet ([phantom, A, B, phantom]), samples a true circular arc, synthesises the apex marker at the
arc's top; breaking materialises quarter/apex/three-quarter points ON the arc. **Fill:** the ruled region
between the curve and the foot-to-foot chord (half-circle → exact half-disc). Marker voxels by nearest-claim
(Locked > Primary > Anchor), even-span apex pairing.

**`EllipseShape.cs`** — the closed planar primitive: two diameter anchors + a minor-axis handle (Primary), no
phantoms. The frame derives per query (plane normal = `ShapePlaneAxis` projected ⊥ the major axis; robust to
arbitrary 3D anchor drags). The minor handle slides along its axis; A/B moves re-derive it. Under Circle the
handle is derived (= major radius) and dragging it is the break trigger (lossless). Body inserts are no-ops
by design. Fill = radial-fan disc.

**`ShapeFactory.cs`** — `Create` (two clicks) / `Adopt` (existing list or GuideData); the only construction
point. **`VoxelMarch.cs`** — shared point-sequence → cell marching, mirroring the spline's quantise exactly.
**`SoftPointFlow.cs`** — `Capture(pts, grabbedIndex)` folds the held point into the baseline, then selects
the regime: an **interior grab** (held point unlocked, non-anchor) slaves each soft point onto the curve at
its station with **zero offset**; a **structural grab** (anchor/lock) keeps the station + frame-local,
length-scaled offset. Reflow is non-mutating; identical code runs server-side (composed into the same edit
batch as the grabbed point) and client-side (drag preview). Three capture call sites: server move handler,
client `StartGrab`, client insert-adoption.

### Systems

**`GuideManager.cs`** — server-side single authority. Registry + JSON persistence (versioned root, Newtonsoft
`Vec3d` converter); builds shapes via the factory on create (shape/constraint/plane from the request) and
re-adopts on load/restore; validates every mutation (**filled-aware voxel caps**, lock, existence) and
reverts on rejection. Mutations: `CreateGuide`, `RestoreGuide`, `UpdateControlPoints` (multi-edit, atomic),
`InsertControlPoint`, `RemoveControlPoint`, `SetPointLocked`, `DeleteGuide`, `SetHidden`, `SetProjection`
(**bakes flattened positions into the points when leaving Surface**; preserves the stored plane through
Volumetric), `SetFilled` (recounts with the new value, rolls back over cap), `Rescale`, `BreakConstraint` /
`RestoreConstraint`, `RestoreControlPoints`. Communicates by `GuideOperationResult` return values, not events.

**`GuideLockManager.cs`** — pure; one edit lock per guide, first grab wins; `ReleaseAllLocksForPlayer`,
`ClearLock`, `IsHeldBy`.

**`DraftManager.cs`** — client-side draft + tool state: mode, scale, projection, plane override, fill,
**shape + constraint** (the picker's target), the per-draft intrinsic plane axis, and the selected guide.
Holds only the draft's start point; settings are read live at completion. Cap pre-check builds a throwaway
shape via the factory, counted filled-aware.

**`UndoManager.cs`** — server-side, per-player bounded stacks (default 50), session-only. Validate-then-apply
with stale-command skip; `Blocked` for valid-but-cap-rejected commands (pushed back, history preserved);
returns results, never broadcasts.

**`GuideRenderer.cs`** — client-side; one compiled mesh per guide, rebuilt only on change (every
mirror-apply raises the change event → rebuild). Shapes adopted via the factory; **settled guides always
mesh at true scale**; the draft ghost may coarsen above `PreviewFullResVoxelCap` (≈ 8,000) and carries the
picked shape/constraint/plane in its rebuild key. Surface: flatten to the **air-side** cell layer (world
solidity probe, majority fallback) as **0.01-block slabs** with a plane-axis-only inset; volumetric meshes
get a 0.003-block per-frame camera nudge. Grabbed point painted White (single voxel); hidden guides =
anchors-only at low alpha. No selection/collision geometry.

**`GuideMeshBuilder.cs`** — stateless `List<VoxelPosition>` → `MeshData`; cube and slab paths; the color
table (client-configurable alphas):

```
Yellow   (1.0, 0.85, 0.1)   Normal body          Red    (0.9, 0.15, 0.15)  Locked
Green    (0.2, 0.9, 0.3)    Primary/apex/handle  Blue   (0.2, 0.5, 1.0)    Anchors (coplanar)
Indigo   (0.45, 0.45, 1.0)  Far-foot off-shade   White  (1.0, 1.0, 1.0)    Grabbed
```

### Network

**`PacketTypes.cs`** — protobuf DTOs, one fixed shared registration order, **append-only**. Enums as pinned
ints, Guids as 16 bytes, positions as three doubles, full point lists verbatim (mirrors are exact copies).

| Packet | Direction | Contents |
|---|---|---|
| `GuideBulkSyncPacket` | S→C | All guides + active caps + lock states + draft anchors, on join |
| `GuideCreateRequestPacket` | C→S | Two points + settings + **shape + constraint + plane axis** |
| `GuideCreatePacket` | S→C | Full `GuideData` (also the generic full-state broadcast) |
| `GuideUpdatePacket` | S→C, C→S | Guide ID + edit array (client sends its one; server broadcasts the composed batch incl. soft-flow edits) |
| `GuideInsertPointPacket` | S→C, C→S | Guide ID + index + position (+ `Locked` for lock-in-place) |
| `GuideCancelGrabPacket` | C→S | Cancel the grab: restore origins / remove an insert-born point |
| `GuideDeletePacket` / `GuideHidePacket` / `GuideLockPointPacket` / `GuideRescalePacket` / `GuideSetProjectionPacket` / `GuideSetFilledPacket` / `GuideSetDivisionsPacket` (S9) | S→C, C→S | The atomic ops (divisions = pure visual recolor) |
| `GuideGrabPacket` / `GuideReleasePacket` / `GuideLockStatePacket` | C→S / S→C | Edit-lock lifecycle |
| `DraftStartPacket` / `DraftCancelPacket` / `DraftAnchorBroadcastPacket` / `DraftAnchorRemovePacket` | mixed | Draft lifecycle (anchor dot only) |
| `UndoRequestPacket` / `RedoRequestPacket` / `VoxelCapWarningPacket` | C→S / S→C | Undo + cap warnings |

**`ServerNetworkHandler.cs`** — validates, calls the managers, reads results, broadcasts (or corrective
resync / cap warning). Owns: the create request (shape-aware), **auto-break** before constrained inserts and
on breaking moves (recording `BreakConstraintCommand` from a pre-break snapshot; break-flavoured mutations
broadcast **full state** because the point list changed shape), **soft-flow composition** (capture per drag
session, reflow edits folded into the same `UpdateControlPoints` batch — one cap check, one broadcast,
generic origins), the projection bake snapshot, drag coalescing (one drag = one undo entry), join/leave
cleanup (locks freed + broadcast, undo cleared, drag sessions and draft anchors dropped).

**`ClientNetworkHandler.cs`** — applies S→C packets to the mirror, raises change events for renderer/HUD/GUI,
exposes read-only views (mirror, lock holders, remote anchors, synced caps) and every send method. Never
writes server state.

### UI

**`GuideToolGui.cs`** — the tile GUI (modal, F, press-to-open; icon form since Session 10). Every control is
a row of exclusive SQUARE ICON tiles (42 px; hover names the option, auto-sized to the text since Session
11). **Mode-aware rows (Session 10):** the Mode row (Create/Edit/Delete) is always live; the rest re-bind by
mode. **Create:** tool defaults for the next guide — the Mode row's far-right **Current Shape chip
(0.1.15: always lit, guide-body yellow, hover names the pick — visible even when the selection isn't on a
slot)** · Shape (**0.1.15: FOUR hard-kept pinned slots + a ▾
expand tile that unfolds the full 18-shape catalog — split into a **2D section and a 3D section** under
their own separators with centred "2D"/"3D" labels (0.1.22–0.1.23); pins shown as YELLOW glyphs (0.1.17);
right-click pins/unpins (never evicts; message when full); the catalog STAYS OPEN after a pick (0.1.23) —
only the ▾/▴ collapses it; selecting a shape never collapses; empty slots show faint placeholders; pins
persist in `layout-client.json`; the old Favorites strip is gone**) · Scale (native N×N voxel-count icons;
16× = one solid block) · **Projection + Fill on ONE row** (0.1.17; Projection greys on volumes, Fill greys
on the Free-Shape) · Plane · **Divisions (+ Sides for polygons on the same row; the whole row HIDDEN on 3D
volumes, 0.1.23)**. All the primary row labels (Mode/Shape/Scale/…) are **centre-aligned** in their column
(0.1.23). **Edit:** the SAME rows plus **Visibility** (no shape picker) act
on the SELECTED guide via the send API, with a compact guide-info line + **Deselect**; greyed with a "click
a guide" prompt when none is selected — so the panel never grows a second section. **Delete:** every row but
Mode disabled (native `Enabled=false` + ghost labels). Divisions and Sides are native
`GuiElementNumberInput`s (wheel ±1 — element-native plus a dialog-level hover fallback in `OnMouseWheel` —
spinner buttons, typed input, clamped to their ranges: 0–256 and 3–24; `OnNumberTyped` snaps the display
back on clamp, and tolerates transient under-min typing in the Sides field). Remote edits to the Edit-mode
selected guide relight the (shared-key) tiles in place; row-set changes defer a recompose (never per-frame).

**`LayoutToolIcons.cs`** (Session 10) — every GUI glyph, drawn with Cairo and registered once (client start)
in `capi.Gui.Icons.CustomIcons`: the 13 shape glyphs (the arch is an open elliptical dome — the true
Catmull-Rom silhouette; the polygon a point-up pentagon; the Free-Shape an irregular dotted-corner
outline), the picker's expand chevrons (▾/▴), the empty-slot placeholder, and (0.1.15) per-shape
**"-star"** (★-badged pinned catalog tiles) and **"-current"** (fixed guide-body-yellow, for the Current
Shape chip) wrapper variants, plus mode/projection/fill/visibility pairs, plane cubes (active face filled),
and the scale grids (`DrawScaleGrid(n)` + `DrawScaleFullBlock`). Uniform aspect-preserving design-box
mapping; strokes/fills take the button's tint, so normal/hover/pressed states come free.

**`GuideHud.cs`** — mode (+ the picked shape in Create) · scale · projection/plane · fill · live ↔/↕
dimensions in voxels and blocks (draft and examined guide, factory-built shapes, fill-aware) · examined
guide's id, lock, count, cap bar (`Cap: 62%` + ⚠ from the warning packet) · **the Current Shape chip
(0.1.16): the same always-lit yellow glyph as the F-menu's, top-right of the panel in Create mode,
recomposed on shape/mode changes.**

### Undo

**`IGuideCommand`** — `CanUndo` / `CanRedo` (direction-specific), `Execute` / `Undo` / `Redo` returning
`GuideOperationResult`; the handler mutates directly and records, so `Execute` is reached via redo.
**`UndoStack`** — pure bounded histories; new actions clear redo. **Commands:** Create / Delete (DeepClone
snapshots) · MoveControlPoint (before/after; one per drag per moved point, soft-flow included) ·
InsertControlPoint (landing index refreshed on re-insert) · LockPoint · RescaleGuide · HideGuide ·
SetProjection (**+ optional pre-bake point snapshot**; undo restores mode/plane then the points) ·
SetFilled · **BreakConstraint** (pre-break constraint + points; undo restores both, redo re-breaks) ·
**SetSides** (S11: old/new polygon side count) · **SpringBack** (S11: pre/post point+constraint snapshots
around a SHIFT spring-back; undo restores the distorted form).
Move/insert confirm the point is still where the command left it, so they never clobber another player's edit.

---

## 4. Geometry Tiers

- **Tier 1 — hollow curve/ring.** Built.
- **Tier 2 — filled region.** Built: arch family = curve closed by the foot-to-foot chord (ruled surface);
  ellipse family = disc. Caps count filled voxels; big filled regions at scale 1 approach the per-guide cap
  by design (the warning handles it).
- **Tier 3 — curved/swept filled surface** (a surface between multiple boundary curves). **Deferred** until
  Tiers 1–2 have real-play mileage.

---

## 5. Key Interaction Flows (the three-mode scheme)

### Targeting (all clicks)
A raycast resolves to the voxel cell on the first block face at the current scale. **Anchors require a valid
block target.** Guide targeting tests real control points (precise, `max(0.10, voxel)` radius) and the
**sampled curve** for body hits. In Surface mode the clicked face auto-selects the projection plane (UI
override available); the first click of any draft also fixes the ellipse family's **intrinsic** plane.
Guides are referenced off blocks only at placement — never bound; removing the block changes nothing.

### Creating a guide (two clicks; free/right/isosceles triangles take three)
1. Pick the shape on the F-menu tiles (or keep the remembered default). **First click:** draft starts,
   plane axis captured, `DraftStartPacket` → others see an anchor dot; the acting player gets the live ghost
   (full placed-guide pipeline: colors, scale, Surface slabs, far-foot Blue/Indigo, the picked shape).
2. Settings changed mid-draft apply live to the ghost. **CTRL** snaps the aimed foot level-and-cardinal
   (Session 11 — moved from SHIFT); **SHIFT** live-inverts the ghost upside-down (arch family +
   equilateral).
3. **Completing click:** client cap pre-check (factory shape, filled-aware) → `GuideCreateRequestPacket`
   (points + settings + shape/constraint/plane + Session-11 inverted/sides/apex) → server builds via the
   factory, stores (stamping the as-placed spring-back snapshot), records `CreateGuideCommand`, broadcasts
   full state. **Three-click triangles:** the second click stores the base's far end (client-side only);
   the ghost's apex then tracks the crosshair — **SHIFT centres it on the base (0.1.15)** — and the THIRD
   click completes. **Free-Shape (0.1.15):** every click chains a corner (CTRL snaps relative to the
   PREVIOUS corner); clicking the LAST corner finishes open, the FIRST (≥3) closes the loop; the full
   chain + closed flag cross in the create request. Right-click steps any multi-click draft back one click
   (chains retract a corner, triangles the base end; otherwise the draft is discarded).

### Grabbing and reshaping (Create mode)
1. **Left-click a point** → grab (lock acquired, `GuideLockStatePacket` broadcast). **Left-click the body:**
   arch family → the server inserts a point at the nearest curve parameter and the client adopts it as a
   grab in one gesture (a constrained guide **breaks first** — one undo command, one full-state broadcast
   carrying both changes); ellipse family → the nearest handle is grabbed instead.
2. **Dragging:** anchors snap to block faces (CTRL → cardinal line through the other anchor; Session 11 —
   SHIFT+left-click on a guide is now spring-back-to-original instead of a grab); interior
   points move at retained depth. The client previews locally at full fidelity — including **soft-point
   flow** (unlocked interior points flowing proportionally with the structural baseline) and any constraint
   break (mirror constraint cleared at grab start). Throttled sends (~100 ms); the server composes the
   authoritative batch (grabbed edit + its own soft-flow reflow), cap-checks once, broadcasts to everyone.
   Dragging a circle's minor handle auto-breaks circle → ellipse on the first move.
3. **Release (left-click):** one `MoveControlPointCommand` per moved point (origin → final), lock freed.
   **Right-click instead:** cancel — origins restored server-side, an insert-born point removed entirely.
   Tool swap mid-drag = comatose (suspend, resume on re-equip).
4. **Idle right-click:** on a point → lock toggle; on an arch-family body → **lock-in-place insert** (a
   point born locked exactly ON the curve; two undo steps); on an ellipse-family body → nearest-handle lock
   toggle.

### Editing a placed guide (Edit mode — Session 10)
Switch the Mode row to **Edit**, then **left-click a guide to select it** (empty click deselects; select-only
— no reshaping). The F-menu's Scale / Projection / Plane / Fill / Divisions / Visibility rows now drive THAT
guide through the send API (`SendRescale` / `SendSetProjection` / `SendSetFilled` / `SendSetDivisions` /
`SendHide`) instead of the tool defaults — no separate panel section, so the GUI never expands. Reshaping
(grab / insert / lock) stays in **Create**.

### Projection, plane, fill (F-menu tiles — tool defaults in Create, the selected guide in Edit)
Volumetric ↔ Surface: switching a guide **to** Volumetric bakes the flattened positions into its points
(what you saw is what you get; single undo step; full-state broadcast); switching back **to** Surface
restores its stored plane. Plane and Fill apply atomically with cap re-checks (turning Fill on can be
rejected over cap and rolls back). All rebuild every client's mesh via the normal change events.

### Delete mode
Left-click a guide → dispel (no client-side lock pre-check — the server owns exclusivity and the admin
override). The GUI's other rows disable.

### Undo / redo
Ctrl+Z / Ctrl+Y (inert unless the tool is held) → the server finds the player's most recent still-valid
command (stale ones skipped), applies it, broadcasts full current state or a delete — one rule for every
command type, breaks and bakes included. Cap-blocked commands are pushed back and reported.

### Cross-plane behaviour
Volumetric points move freely in 3D — guides can be non-planar. Surface re-flattens the render onto the
plane; edits made while Surface are made against that view, and **leaving Surface keeps them** (the bake).
The ellipse's intrinsic plane is independent of the Surface projection plane and survives arbitrary drags
(the frame re-derives per query).

---

## 6. Serialization and Persistence

- All `GuideData` as a JSON array under a versioned root via `IWorldSaveGame.StoreData`/`GetData`;
  `GuideManager` owns a Newtonsoft `Vec3d` converter (`{x,y,z}`). Persistence on every mutation + the
  world-save event; load/save never throw out of VS event handlers. On load: version-checked (defaults
  migrate v3-era records), factory-adopted, phantoms recalculated before first sync.
- **Save format ≠ wire format:** JSON persists; protobuf DTOs travel. The paths are independent.

---

## 7. Concurrency and Edge Cases

- **Multiplayer model (settled):** world-shared, no ownership, no private guides; edit-locks prevent only
  simultaneous edits; grief is a server-administration concern, out of scope.
- **Guides are non-targetable without the tool:** pure mesh draws, no selection/collision/entity backing;
  all interaction inside the held-item path; nothing registers with engine picking.
- **Disconnect mid-grab** → locks freed and broadcast; uncommitted drag never recorded; undo history cleared.
  **Mid-draft** → draft cancelled, anchor dot removed.
- **Two players grab one guide** → first packet wins; the second sees the lock state, click is a no-op.
- **Over-cap mid-edit** → server counts (filled-aware) before committing; rejects, warns, reverts. Clients
  pre-check to avoid jank.
- **Cross-player undo** → stale commands are skipped, never corrupting state; valid-but-rejected commands
  are `Blocked` and preserved.
- **Block under a guide removed** → nothing happens; guides never bind to blocks.

---

This v2.6 document is the authoritative plan, consolidated to current state: **Layout v0.1.x, in real play**
on live game worlds. Arches, half-circles, circles, ellipses, lines, triangles (+ right/equilateral/isosceles),
and rectangles (+ square) place, preview, reshape, fill, lock/unlock, divide, and project onto surfaces in
live multiplayer against VS 1.22.3 / .NET 10. History lives in the v2.4 copy; status, flagged decisions, and
the punch-list live in `PROJECT_STATUS.md` and `TODO.md` (renamed from `OUTSTANDING_ITEMS.md`).
