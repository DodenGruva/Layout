# Layout - Architecture

> **Tier 1 - durable.** This is the blueprint: what Layout is, what was decided, and why. It changes
> when a DECISION changes, not when the build does.
>
> **It states no current version, no date, and carries no staleness banner - deliberately.** Its
> predecessor carried all three and all three went stale, including the banner announcing the staleness.
> A banner is itself a fact requiring maintenance, and needing one is the signal that content sits in the
> wrong document. **For where things stand right now, read `STATUS.md`** - that is its whole job, and it
> is regenerated rather than edited so it cannot drift.
>
> Version numbers DO appear below, in the Settled Decisions Register, and they belong there: they cite
> **when a decision was made or reversed**, which is permanent historical fact. What this document must
> never contain is a claim about the CURRENT state, because that is what rots.
>
> **The Settled Decisions Register is the highest-value section here.** Do not reopen a settled decision
> without the human explicitly asking.
>
> Sections 1 and 3 are retained but emptied - see the notes in place. Numbering is unchanged so older
> references keep resolving. This document's own revision history lives in
> `dev/history/CHANGELOG_ARCHITECTURE.md`.

---

## Overview — what Layout is

Layout is a mod for Vintage Story 1.22.x (C#/.NET 10) that provides a persistent, visual, voxel-resolution
construction planning tool — a CAD-like drafting assistant inside the game. Players place geometric guide
overlays in the world and use them as visual references while building by hand; the mod never places, removes,
or modifies blocks automatically. Guides are visual-only, rendered as translucent voxel meshes that are
world-shared and visible to all players on a server, persist across logout and chunk unloading, and are stored
server-side with server-authoritative networking. A guide is only *referenced off* a block at placement time,
never bound to it, and is visible-but-untargetable when the tool isn't held, so it never interferes with the
blocks underneath.

The tool is a held item with an F-key **tile menu** (Create/Edit/Transform/Delete mode, the shape picker, voxel scale
1×1×1–16×16×16 defaulting to the finest to match chisel resolution, projection, plane, fill); all interaction
uses first-person clicks and crosshair targeting rather than transform gizmos. The **shape catalog** is
**16 shape types shown as 22 picker tiles**, split into a **2D section** — arch · half-circle · circle ·
ellipse · line · triangle (+ right/equilateral/isosceles) · rectangle (+ square) · polygon (regular N-gon) ·
Free-Shape (irregular polyline) — and a **3D VOLUME section** — Fillet · sphere · dome · cylinder · tapered
cylinder · polygonal prism · tapered polygonal prism · cone · box. It is
built on the **primitives+constraints** model (a half-circle is an arch under a SemiCircle constraint, a
circle is an ellipse under a Circle constraint, a square is a rectangle under a Square constraint, and the
triangle constraints derive the apex); constrained variants are **not** separate types. **Most shapes place
with a two-click gesture**, with the deliberately reopened exceptions: the free/right/isosceles triangles,
the free Rectangle, and the 3D cylinder/polygonal prism/cone take **three clicks**, the Box and the Tapered
Cylinder / Tapered Polygonal Prism take **four** (the tapered pair's fourth click is a rim setting the top
radius; the Box's is an ordinary height), the Free-Shape takes **unbounded chained clicks** (≤64), and
Fillet records a corner and two profile-side points before chaining its open sweep path. `DraftManager.NeedsApexClick`,
`NeedsFourthClick`, and the chained-shape state are the authority
on which shape takes how many. Players reshape guides by grabbing exact rendered cells: clicking the body
inserts-and-grabs in one motion on the Arch and Free-Shape families; parametric shapes otherwise require an
exact coloured marker, with one narrow Dome exception that maps an exact visible base-rim cell to the nearer
diameter anchor. They lock points as constraints and rely on two standing contracts:
**absorb-or-break** (a grab a constraint can absorb, it absorbs; one it cannot absorb demotes the shape to
its free parent, seamlessly and undoably) and **soft-point flow** (slave-regime: an interior grab slaves
unlocked points onto the curve with zero offset, a structural grab keeps shape-preserving proportional flow —
locking is the only thing that pins geometry). Each guide can additionally be rescaled, toggled between
volumetric (3D) and surface (flat decal) projection, set hollow or **filled** (arch family: the region closed
by the foot-to-foot chord; ellipse/polygon: the disc/interior; triangle/rectangle: the interior/box;
3D volume fill is retired), given a purely-visual **equal-parts division** overlay, and hidden or shown. **The
3D volumes are always Volumetric** (Surface and Divisions do not apply to them) and are voxelised by a cell-
lattice shell/solid scan rather than curve-marching.

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
- **Four modes: Create | Edit | Transform | Delete** (Session 10 made three, from two; **Transform** joined
  them across v0.3.86–v0.4.0). **Create** owns ALL geometry — left-click
  priority chain: release grab → second foot (a draft outranks guide targeting) → precise point grab → body
  insert+grab in one gesture → first anchor; right-click is the universal cancel / draft discard / idle point
  lock-toggle / idle body **lock-in-place insert** (point born locked on the curve; two undo steps).
  **Edit** is settings-only: left-click **selects** the guide under the crosshair (empty click deselects) and
  the GUI's setting rows then act on that selected guide — **no grab/insert/lock**, so a select-click can
  never reshape. Right-click also clears the current Edit selection without mutation, matching Create's
  cancel/backtrack gesture. **Transform** selects a guide exactly as Edit does and acts on it as a WHOLE
  OBJECT — move, rotate, copy, mirror — so it reshapes nothing either. **Delete** dispels (left-click). This
  restored an Edit mode (the earlier Create/Edit/Lock/
  Dispel scheme was collapsed to two in Session 8 because modes fought the flow; the new Edit adds no geometry
  verbs, so it doesn't). Settled reason: per-guide editing via the buttons needed a home that didn't expand
  the panel with a separate section. **The same reasoning admitted Transform:** whole-object operations are
  not reshaping, so they earn a mode without reopening the Session-8 objection. `ToolMode` is client-only and
  never wired, so the set is safe to extend or reorder.
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
- **SHIFT is context-dependent** (Session 11 + Session 20, human-requested; supersedes "SHIFT = cardinal"):
  while drafting, it inverts applicable two-click shapes; centres a three-click triangle apex; constrains a
  Line/Free-Shape segment vertically; aligns a flat side on polygon families; and deliberately permits a
  tapered rim to flare past its base. **CTRL+SHIFT** makes a 45-degree Line/Free-Shape diagonal, while CTRL
  alone stays horizontal/cardinal and closes a tapered rim to a point. Stage-aware native held-help rows make
  the active meaning visible;
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
  `DataVersion`. **`GuideData.CurrentDataVersion` is the authority on which version is current** and
  `dev/WIRE_HISTORY.md` carries the ledger — this list records only what each bump ADDED, which is permanent:
  v13 re-gestured Rectangle and Box (Rectangle to three clicks, Box to four, Square unchanged at two, legacy
  encodings read in place); v12 added creator/Last Sculptor attribution; v11 added persistent `IsWireframe`; v10 added cached
  display/count/dimensions; v9 added polygon `FlatSideAligned`; v8 added passive
  `ControlPoint.IsLockMarker`; v7 added `IsClosed`
  (Free-Shape loop flag); v6 added `Sides` + the
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
- **The authority builds shapes** from two/three/four clicks + settings (client never sends full GuideData);
  **tool state
  is client-side** and travels with operations; `CreatorUid` is bookkeeping, never ownership, never wired.
  `CreatorName` is an immutable display snapshot; Last Sculptor identifies the most recent committed visible
  mutation and deliberately ignores hover, selection, no-ops, rejection, and cancellation.
- **Voxels are never stored** — always derived on demand from control points.

### Shapes & geometry
- **Primitives + constraint modifiers, not a flat enum of near-duplicates** (square = rectangle+constraint,
  circle = ellipse+constraint, half-circle = arch+constraint, and the triangle constraints below). Keeps
  `GuideShapeType` short and lets future favorites store {type + constraint} pairs.
- **The catalog (v0.1.23 state):** arch, half-circle, circle, ellipse, **line, triangle
  (+ right / equilateral / isosceles), rectangle (+ square), polygon (regular N-gon, side count = per-guide
  data, 3–24), Free-Shape (irregular polyline)**, and the **3D VOLUME family (v0.1.20–0.1.21): sphere, dome,
  cylinder, tapered cylinder, cone, box** (see the dedicated bullet below). **Placement is two clicks for every shape EXCEPT the
  free/right/isosceles triangles (THREE: anchor · anchor · height — SHIFT on the third click centres the
  apex on the base) and the Free-Shape (UNBOUNDED chained clicks, ≤64: click the LAST placed corner to
  finish open, the FIRST corner (≥3) to close the loop; the aim snaps onto those targets)** — the human
  explicitly reopened the old "every shape is two clicks" rule in Session 11. Right-click steps any
  multi-click draft back one click. Equilateral stays two-click (its apex is fully derived). The polygon's
  two clicks span vertex → opposite perimeter point, so the shape exactly spans the gesture and both
  anchors sit ON the outline. **Constraints are derivation rules:** the triangle apex slides (right →
  perpendicular at first click; isosceles → base bisector) or is fully derived (equilateral), and the
  rectangle's derived corners track the diagonal (square → dominant component).
  **Amended v0.4.15 (DataVersion 13) — the Rectangle/Box re-gesture.** The two-corner *diagonal* gesture
  above could only ever produce a world-axis-aligned rectangle, which is the defect the re-gesture fixed.
  The **free Rectangle now takes THREE clicks** — corner A, the far end of one EDGE (this is what buys it a
  free rotation), then the width — and the **Box takes FOUR**, its width click having pushed its height
  click out to fourth. **Square stays two clicks** (one clicked edge already settles it) and so does
  Equilateral. Guides placed before v0.4.15 stored only the two diagonal corners and are **still read that
  way, never migrated** — see `RectangleShape` and `dev/WIRE_HISTORY.md`. The overview above states the
  current click counts; the paragraph here records the gesture this replaced.
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
  a base diameter); **Cylinder / Cone** = three clicks (base, then a height click — reusing the
  triangle's apex machinery, `NeedsApexClick`); **Box** = four since v0.4.15 (it inherits the Rectangle's
  three-click base, then a height click — it was three while its base was the two-corner diagonal gesture).
  Box is a true box (independent side lengths). **Volumes are
  always hollow rather than solid-filled (v0.2.17)** — 3D Filled is retired because the invisible interior
  was pure cost. Each volume may now persist as a complete **Shell** or canonical structural **Wireframe**
  (`IsWireframe`, v0.3.7); both use the selected scale and exact cap counts. v0.1.53 routes hollow Sphere/Dome
  through `SphericalShellScan`; normal Box retains its lattice scan and normal Cylinder/Cone remain
  centre-banded. When those three legacy bounding scans would cross their old 4M work guard, v0.3.8 switches
  to `LargeVolumeShellFallback` (face/ring work proportional to visible shell area) instead of rejecting the
  guide. Configured caps and the 10M hard ceiling remain authoritative. Volumes are **always
  Volumetric** (Surface + Divisions gated off server-side and greyed/hidden in the GUI). The base plane / axis
  comes from the clicked face; the axis is the **deterministic `ShapeGeometry.BaseNormal`** (+up regardless of
  anchor order — SHIFT is the only invert, e.g. dome → bowl). **Targeting is a wireframe** (equator/meridians,
  rings + verticals, box edges), not every shell cell — the anchors and the height handle are the reliable
  grab points. `ShapeWireframe` is the canonical selected-scale topology path; round volumes have eight ribs
  and polygonal prisms one longitudinal wire per corner. Height may be set in **free air** (no block → the handle follows the view ray; a targeted
  block wins). The original volume catalog reused `ControlPoints` + `ShapePlaneAxis`; v0.3.7 later added the
  shared persisted/wired `IsWireframe` form flag (**DataVersion/protocol 11**). Natural next volumes:
  **Roof, Tunnel**.
- **Fill is a 2D guide property (`IsFilled`); Form is a 3D guide property (`IsWireframe`).** Constraints are
  modifiers, not shape types. The same GUI positions contextually read Hollow/Filled for 2D and
  Shell/Wireframe for volumes.

### Rendering
- **The verified draw recipe:** Opaque stage + manual blend (not OIT); `PreparedStandardShader` overridden to
  full-bright; a **real white texture** (generated 2×2, asset fallback — id 0 samples garbage); the full
  pos+**uv**+rgba vertex layout with uv (0,0). Each element was a genuine independent playtest bug.
- **Single-voxel nearest-claim markers** (anchors/apex/locked/grabbed-White), precedence Locked > Primary >
  Anchor > Division; the apex claims **2 voxels on even spans** (a lone voxel reads off-center by half a
  cell). **Division marks share that even-span pairing** (Session 10, `ShapeGeometry.ClaimMarkerPaired`): a
  boundary landing between two voxels claims both, so the equal parts read even; boundaries on a cell centre
  stay single.
- **Adaptive large-guide drafting and sculpting (v0.3–v0.3.40):** cheap poses render their normal
  selected-scale shell. Expensive moving 3D poses use a structural wireframe under work/frame-pressure
  hysteresis; at least four selected-scale voxels around the cursor remain precise, stepping outward across
  roughly two blocks. Generation-tagged, cancellable progressive scans produce the exact selected-scale
  result off-thread, and stale generations are discarded. The final click retains a selected-scale scaffold
  while server authority validates; an immense sculpt retains its old settled mesh plus the moving
  wireframe until replacement batches are ready. Pending draft/grab measurements show animated calculation
  dots rather than blocking input.
- **Organic materialization (v0.3.34–v0.3.40):** exact voxels are reordered—not approximated—by
  deterministic multi-seed 26-neighbour growth. A six-site base plus one site per 500 voxels establishes
  enough fronts for small guides while scaling with immense shells. Two smooth noise scales and fine grain
  create torn/frayed fronts instead of planar bands or square chunks. The producer streams bounded batches
  into a capacity-three queue; the client consumes about 750 voxels per batch (bounded 8–128 batches), with
  upload spacing that scales from about 18 ms for small guides to 45 ms by 120,000 voxels and backs off under
  frame pressure. The wire scaffold disappears at the first organic batch. Once the full exact occupancy is
  visible, it holds for 200 ms before an independently generated, clean uniform shell swaps in atomically.
  Placement particles and sound wait for both final placement authority and that clean-shell swap.
- **Settled form materialization (v0.3.39):** Edit-mode Wireframe→Shell changes enter the same
  generation-tagged, cancellable materialization chain instead of synchronously rebuilding the full shell.
  The existing wireframe is retained only until the first organic batch. Replacing or cancelling a
  transition quarantines stale workers by guide fingerprint.
- **Intrinsic volume dimensions (v0.3.40):** sphere, dome, cylinder, cone, tapered cylinder, and polygonal
  prism families expose shape-local width and axial height to the HUD/cache layer. Displayed dimensions no
  longer derive width from the diagonal of a rotated world-axis footprint AABB.
- **Placed behemoth safeguards (v0.3.3–v0.3.34):** display name/count/dimensions are cached; placed hover
  never voxelizes. Placement and reshape authority echoes adopt the retained work visual instead of
  triggering a synchronous full-shell rebuild. Old meshes retire incrementally across frames. Cancel reveals
  the retained settled mesh immediately, and fingerprints/quarantines stale worker or confirming-echo work.
  A second immense placement/reshape is gated while one is active, avoiding competing client and server
  pipelines.
- **Surface guides render as paper-thin slabs** (`GuideRenderer.SurfaceSlabThicknessWorld`) hugging the wall face on the **air side** (world
  solidity probe; majority fallback) with a **plane-axis-only** inset. **The air-side probe tolerates the
  world-load race (Session 10):** if a probed cell's chunk isn't loaded yet, the guide's side is *provisional*
  and a low-frequency re-probe tick rebuilds it once the neighbourhood loads — otherwise a guide meshed
  before its blocks arrived sank behind the face on reload (B-S10-1). **This probe is Surface-only and still
  live** — do not confuse it with the volumetric one below, which is gone.
- **Volumetric anti-z-fight is a UNIFORM per-face OUTSET decided by the guide's own voxel set — the world is
  deliberately not consulted (v0.3.70).** Every EXPOSED face is pushed outward by the same amount, growing
  the voxel box by a hair; faces shared with a neighbouring guide voxel take exactly 0 and stay flush,
  because any offset there reopens the v0.2.14 seam.
  **Two reasons the world must not influence it**, both learned the hard way:
  a rebuild after the player filled the volume would flip face offsets inward, and a probe-driven offset
  varied face to face, which blocked welds the v0.3.57 vertex welder could otherwise share.
  **History:** v0.2.10–0.2.16 made it an INSET gated on a world-solidity probe
  (`GuideMeshOptions.IsNeighborSolid`), pulling a face back only where a real block sat across the plane.
  v0.3.70 **removed the probe entirely** and flipped the direction. An outset needs far less displacement
  than an inset — an inset had to open a visible gap to escape the surface behind it, while an outset only
  has to win the depth comparison. The magnitude is the client's `zFightInset` (the key keeps the historical
  name); `GuideMeshBuilder.BlockPlaneInset` holds it and `STATUS.md` records the current default.
  **The model transform must remain an exact world translation.** A former 0.003-block pull of the entire
  mesh toward the camera shifted guide cells off Vintage Story's exact 1/16 micro-block lattice. Direct
  playtest confirmed that deleting that pull completely restored registration; a 0.0001 face outset then
  worked without shimmer and 0.0002 was selected as a small-buffer default. Clearance belongs on exposed
  faces only, never on the whole mesh (`dev/GOTCHAS.md` G48).
  **Do not reintroduce a world probe here** — `dev/GOTCHAS.md` **R9**. `GuideMeshOptions.OccupancyProbe` is
  not one: it asks about a voxel's OWN cell and feeds colour only, never geometry.
- **Settled guides always render at their true scale**; `ChooseRenderScale` coarsening (8,000-voxel cap) is
  a **draft-ghost-only** courtesy — it once leaked into placed guides and permanently degraded them.
- **Settled order is canonical, never progressive reveal order.** Organic volume materialisation may reorder
  cells while revealing them, but finalisation restores the ordinary generator's order before marker-role
  claiming and mesh emission. Occupancy refresh snapshots preserve both that order and `IsWireframe`; exact
  authority completion cancels conservative pending materialisation so only one path owns effects (`G53`,
  `G55`).
- **Large-guide meshing — exposed faces + whole-guide culling (v0.2.14–v0.3.42; restored v0.3.49):**
  `GuideMeshBuilder`'s
  Volumetric cube path now does **exposed-face meshing** — it builds a presence set of rendered cells,
  pre-counts the faces with no neighbour, allocates exactly, and emits ONLY those faces (per-voxel role
  colours preserved). Interior/shared faces vanish, so a ~100-block hollow Sphere draws only its outer skin
  (and, with filled volumes retired in v0.2.17, worst-case counts dropped further). `GuideRenderer`
  conservatively rejects whole guides outside live `viewDistance` or Vintage Story's current frustum.
  v0.3.43's 32-block final regions measured well from one partial view but produced visible curved-guide
  seams. v0.3.44–v0.3.48 greedy/depth experiments then introduced bands, unstable far-side visibility,
  blur/brightness/depth defects, slow clean swaps, and worse real-play FPS. v0.3.49 restored the exact
  v0.3.42 renderer; spatial subdivision and greedy merging are not queued architecture.
  Surface tile/slab paths stay on the legacy whole-box builder; preserve the verified draw recipe. Full
  invariants + the mesh-count harness are in `SESSION_14.md` §6–§7 and `SESSION_16.md` §2.
- **Leaving Surface bakes the flattened positions into the control points** (undoable, full-state
  broadcast): Surface-mode edits are made against the view, so the view is what leaving it keeps. Returning
  to Surface restores the stored plane; only never-Surface guides get the floor-at-anchor seed.
- **Mid-draft Surface↔Volumetric switching preserves placed anchors (v0.3.16).** Every already-placed point
  translates along the first-click face normal by the signed half-voxel difference between the two coordinate
  conventions, so later live points and earlier anchors remain on the same lattice.
- **Color language:** yellow body · red locked · green primary/apex · blue anchors with the **indigo
  off-shade** on a far foot that is not level-and-cardinal (an at-a-glance "is this clean?" cue, deliberately
  shifted violet-ward away from green) · white grabbed · hidden guides = anchors only at low alpha. Occupied
  red/green/blue cells shift toward cyan so material-filled control voxels remain distinguishable. All six
  type opacities are client-configurable.
- **Personal render control (protocol 16):** `/layout off` and `/layout on` (with `.layout off|on` for the
  local command path) disable/enable the entire Layout render pass for that player. The preference is saved
  immediately in `layout-client.json` and restored across reconnects/restarts. Guide state, authority, and
  other players are unaffected. A saved off-state produces a login reminder; Chalking Kit guide actions,
  settings, undo, and redo are blocked with matching HUD/warning guidance, and disabling cancels transient
  draft/grab state so invisible work cannot continue.

### Multiplayer, locks, undo
- **World-shared with server policy, not private ownership.** Public guides remain collaborative, while
  persistent per-player limits/jail policy and administrator moderation commands can restrict creation or
  remove abusive guides. Creator identity is bookkeeping/cap attribution; it does not make public guides
  privately owned.
- **Claim-authoritative public geometry:** before a public create or sculpt commits, the server verifies
  access to every affected block claim using the game's claim authority. Small guides use the immediate
  pathway. Immense guides use one below-normal-priority worker for pure generation/counting and a bounded
  main-thread claim pass (up to 128 block checks or roughly 1 ms per 20 ms tick). Only one immense
  create/sculpt lane runs at a time, keeping the server responsive; edits retain their exclusivity lock until
  the asynchronous decision completes. Since v0.4.57 cancellation reaches every volume's active count,
  exact voxel generation, large-volume fallback march, marker pass and footprint collapse; cancellation
  abandons the whole result rather than publishing partial geometry (`GOTCHAS` G45). Sliced claim work is
  guarded by an immutable structural snapshot of the relevant built-in claim/player authorization state,
  compared before each later slice and after the final slice. Its spatial scope is the exact built block
  footprint, including Surface projection's adjacent checks: outside claims are ignored, intersecting claim
  geometry is clipped to the footprint, and a fourth relevant change after three restarts fails closed. The
  snapshot detects staleness only; it never replaces exact per-block `TestAccess`, because other mods remain
  an opaque denial source (`GOTCHAS` G40, G47 and R12).
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
- **Implemented divergence — client-only authority (F4).** Networked public play remains server-authoritative.
  Local guides use a client-side instance of the same `GuideManager`, so locks, constraints, caps, and undo
  semantics remain real rather than becoming no-ops. On mixed servers, public and private guides coexist in
  one mirror; a guide's recorded ownership routes every existing-guide mutation, while placement mode only
  chooses the destination of a new guide. Undo/redo follows the last successful mutation authority. Full
  contract: `PLAN_CLIENT_ONLY.md`.

### Configuration, assets, GUI
- **Server `layout.json`:** configVersion 1 · perGuideVoxelCap 500,000 · **perPlayerTotalVoxelCap 1,000,000**
  (current public guides attributed to one original creator) · totalVoxelCap 0 · maxGuidesPerPlayer 0 ·
  maxGuidesWorldWide 0 · undoHistoryDepth 50 · requiredPrivilege "" · adminCanOverrideLocks true ·
  **allowClientOnlyMode false** · **enableChalkDurability true** (F5)
  (0/negative = unlimited; **construction-time injection — edits need a server restart**). Caps sync to
  clients on join so the pre-check matches enforcement. Existing exact historical generated defaults migrate
  to 500,000 per guide and an unlimited world total; other administrator-selected values are preserved.
  `/layout voxelcap <player> <number>` persistently overrides only the acting player's per-guide limit;
  `/layout totalvoxelcap <player> <number>` persistently overrides the named original creator's cumulative
  allowance. `0` removes either override and restores its server default. Lowering a cumulative allowance
  never deletes existing guides; they may remain or shrink but cannot grow while still over cap.
  **The two chalk refill-channel flags left this file in v0.2.22** — they are player preferences now, in
  `layout-client.json`; stale keys in an existing `layout.json` are ignored. **The running total is a `long`** (v0.1.27) so a
  caps-off server can't overflow it negative. A **hard voxel ceiling** (`GuideManager.HardVoxelCeiling`,
  10M) rejects giant guides ALWAYS, even with caps disabled. Remaining guarded 3D scans return a huge sentinel
  for over-size filled/volume paths; hollow Sphere/Dome now count exactly beyond the old guard. Do not raise
  the hard ceiling before the mesh pass: a near-ceiling monolithic mesh already lags. Server-side create
  rejections send a **clear in-game
  error** (the HUD cap-flash is keyed to a guide id that doesn't exist yet on a create, so it was silent).
- **Admin commands (v0.1.26–0.1.27):** **`/layout dispel all`** (whole world) and **`/layout dispel <chunk
  radius>`** (Chebyshev radius around the caller), both `controlserver`. Namespaced under `/layout` so they
  can't clash with other mods. Registered in `ServerNetworkHandler`; they delete + force-free locks +
  broadcast, and reset the running total.
- **F4 commands:** `/layout private` asks an allowing Layout server to make new guides local;
  `/layout public` returns new placement to server authority; `/layout client push all` publishes up to 100
  local guides after privilege/cap validation. `.layout dispel all|<chunk radius>` is intentionally a
  client command and deletes only private guides. Push is a committed ownership transfer and is not inserted
  into server undo history.
- **Client `layout-client.json`:** remembers scale / projection / fill / **shape + constraint** (validated
  pairs) / divisions / **sides** plus the six opacities and (Session 11) the **pinned favorite shape codes
  (up to FOUR since 0.1.15; hard-kept — never auto-padded)**; client-retained, never synced. **Default
  scale 1** (chisel-matched). **`forceClientOnly` defaults false** and requests private placement when an
  installed Layout server explicitly allows it; `_forceClientOnlyNote` documents that dependency.
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
  compact guide-info line + **Deselect**; **Transform** selects the same way and swaps the setting rows for
  the state-driven direction pad (move / rotate / copy / mirror / send-to-ground);
  **Delete disables everything but Mode** (native `Enabled=false` dim
  + ghost labels). The panel therefore never grows a second section on selection. **Scale icons = the game's
  native N×N-grid scheme, N = voxel count** (16× = one solid block). **Divisions = a native number input**
  (wheel ±1, spinners, typed, floored at 0, clamped to `MaxDivisions`) — the one non-icon control. F-modal
  press-to-open, not the vanilla radial. Draft settings are **live** — mid-draft changes apply to the ghost
  and the completed guide. The Create header is simply **`Create Mode` + a normal-size right-aligned shape
  name** (v0.2.47); the redundant `- Next guide:` phrase and the rejected adaptive font shrink are gone.
- **Hotkeys are rebindable and gate-aware** (Ctrl+Z/Y never hijack other UIs). On Layout servers the real
  guide tool is required in public and private placement modes. On servers without Layout, any vanilla
  Hammer variant/durability in the offhand + Flax Twine in the main hand substitutes for it; F opens the
  unchanged GUI and the HUD appears immediately. The real tool is the **Chalking Kit** (custom deflating
  **5-state** model — full/high/medium/low/empty, Sessions 15–16): recipe **8× Chalking Powder + linen sack
  + flax twine + rope + copper nails**;
  **32-chalk durability** (F5). The contract: a completed server-authoritative placement spends chalk (2D −1,
  3D volume −2), a kit at 0 cannot place NEW guides while editing and dispelling stay open, creative-mode
  players never consume, and `ItemGuideTool.MaxChalk` is the ceiling rather than the engine's durability —
  deliberately, so no third-party behaviour can inflate it. Server flag `enableChalkDurability`; detail in
  `dev/sessions/SESSION_15.md`. The vanilla
  Hammer + Flax Twine fallback gate cannot carry custom durability, so a server WITHOUT Layout is the one
  chalk-free mode — physically unenforceable there, by accepted design.
- **Held-item interaction notes are native and stage-aware (v0.2.39–v0.2.45):** Layout recomposes the
  active-slot help when the draft stage changes, exposing only applicable CTRL/SHIFT meanings. Internal
  refreshes suppress the inherited ground-storage note, which appears only on a real item swap.
- **Runtime is .NET 10** (VS 1.22); `Entity.SidedPos` is obsolete — use `Pos`.

---


---

## 1. File Structure - REMOVED, derivable from source

**This section was 117 hand-maintained lines listing the contents of `src/`. It is gone on purpose.**

`Glob` and `Grep` answer this instantly and *correctly*. A hand-maintained file list is guaranteed to
drift, and it drifts while still looking authoritative - which is the expensive kind of wrong. It was
already the fastest-rotting content in the repository.

```
Glob   src/**/*.cs           - the whole tree
Glob   src/Shapes/*.cs       - one area
Grep   "class GuideManager"  - where something lives
```

Namespaces match folders. `src/` holds `Guide/` (data types), `Shapes/` (pure geometry math),
`Systems/` (managers, renderer, undo), `Network/` (packets + handlers), `UI/`, `Config/`, `Items/`,
`Client/` (the tool controller) and `Undo/Commands/`. That much is durable; the file list was not.

The frozen original is in `dev/archive/`.

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
    PlaneAxis         ShapePlaneAxis    // INTRINSIC plane normal / base axis (first-click face) — ellipse family + 3D volumes
    List<ControlPoint> ControlPoints
    int               VoxelScale        // 1, 2, 4, 8, or 16
    bool              IsHidden
    ProjectionMode    Projection        // Volumetric | Surface
    ProjectionPlane   Plane             // the Surface PROJECTION plane (≠ ShapePlaneAxis)
    bool              IsFilled          // 2D hollow vs filled
    bool              IsWireframe       // 3D Shell(false) vs canonical structural Wireframe(true)
    string            DisplayName       // cached human-readable whole-block dimensions
    int               CachedVoxelCount
    int               Cached{Voxel,Block}{Width,Height}
    string            CreatorUid        // nullable; bookkeeping/cap identity, never client-visible ownership
    string            CreatorName       // immutable friendly-name snapshot; display only
    string            LastSculptorUid    // most recent committed visible modifier; server/local bookkeeping
    string            LastSculptorName   // friendly display snapshot used by /layout who
    int               DataVersion       // stamped from GuideData.CurrentDataVersion; older saves migrate by defaults
    int               Divisions          // Session 9: visual equal-parts count (0/1 = none)
    int               Sides              // Session 11: polygon side count (3–24; 0 on other shapes)
    bool              FlatSideAligned    // Session 20: polygon edge-facing orientation; false preserves old guides
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
    bool    IsLockMarker    // passive Arch lock; rendered/targetable but not a curve knot until dragged
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
enum GuideShapeType  { Arch = 0, Ellipse = 1, Line = 2, Triangle = 3, Rectangle = 4, Polygon = 5,
                       FreeShape = 6, Sphere = 7, Dome = 8, Cylinder = 9, Cone = 10, Box = 11,
                       TaperedCylinder = 12, PolygonalPrism = 13, TaperedPolygonalPrism = 14,
                       Roundover = 15 }
enum ShapeConstraint { None = 0, SemiCircle = 1, Circle = 2, Right = 3, Equilateral = 4, Isosceles = 5, Square = 6 }
```
`GuideShapeTypes.IsVolume(type)` explicitly classifies the nine volume types (never inferred from enum ordering,
so future 2D shapes can be appended after the volumes). The 22-tile catalog (type, constraint) — **2D
section:** Arch = (Arch, None) · Half-circle = (Arch, SemiCircle) · Circle = (Ellipse, Circle) ·
Ellipse = (Ellipse, None) · Line = (Line, None) · Triangle = (Triangle, None) · Right = (Triangle, Right) ·
Equilateral = (Triangle, Equilateral) · Isosceles = (Triangle, Isosceles) · Rectangle = (Rectangle, None) ·
Square = (Rectangle, Square) · Polygon = (Polygon, None) · Free-Shape = (FreeShape, None); **3D section:**
Fillet = (Roundover, None) · Sphere = (Sphere, None) · Dome = (Dome, None) · Cylinder = (Cylinder, None) · Tapered Cylinder =
(TaperedCylinder, None) · Polygonal Prism = (PolygonalPrism, None) · Tapered Polygonal Prism =
(TaperedPolygonalPrism, None) · Cone = (Cone, None) · Box = (Box, None). (Polygon side count lives in
`GuideData.Sides`, not a constraint.)

### ProjectionMode / ProjectionPlane / GuideRenderSettings
Unchanged since v2: `ProjectionMode { Volumetric, Surface }`; `ProjectionPlane` = a `PlaneAxis`
(X = 0, Y = 1, Z = 2) + an offset in 1/16-block units, with `Horizontal` / `VerticalNorthSouth` /
`VerticalEastWest` factories and a `Default`; `GuideRenderSettings` bundles scale + projection + plane + fill
+ divisions (S9) for creation requests and the draft ghost.

---


---

## 3. Module Map - REMOVED, derivable from source

**This section was 238 lines describing what each module does, class by class.** It is gone for the
same reason as section 1, only more so: it restated responsibilities that the source states
authoritatively, and it was the single largest maintenance liability in the document.

What is NOT derivable - the *why* behind a design, the constraints, the things that will bite you - is
kept, and lives in three places:

- **The Settled Decisions Register above** - locked-in design choices and their reasoning.
- **`dev/GOTCHAS.md`** - traps, and reversals of things deliberately undone.
- **The session records** (`dev/sessions/`, indexed by `INDEX.md`) - how each decision was arrived at.

The frozen original is in `dev/archive/`.

---

## 4. Geometry Tiers

- **Tier 1 — hollow curve/ring.** Built.
- **Tier 2 — filled region.** Built: arch family = curve closed by the foot-to-foot chord (ruled surface);
  ellipse family = disc. Caps count filled voxels; big filled regions at scale 1 approach the per-guide cap
  by design (the warning handles it).
- **Tier 3 — curved/swept filled surface** (a surface between multiple boundary curves). **Deferred** until
  Tiers 1–2 have real-play mileage.

---

## 5. Key Interaction Flows (the four-mode scheme)

### Targeting (all clicks)
A raycast resolves to the voxel cell on the first block face at the current scale. **Anchors require a valid
block target.** Guide targeting tests real control points (precise, `max(0.10, voxel)` radius) and the
**sampled curve** for body hits. In Surface mode the clicked face auto-selects the projection plane (UI
override available); the first click of any draft also fixes the ellipse family's **intrinsic** plane.
Guides are referenced off blocks only at placement — never bound; removing the block changes nothing.

### Creating a guide (two to four clicks; Free-Shape chains)
1. Pick the shape on the F-menu tiles (or keep the remembered default). **First click:** draft starts,
   plane axis captured, `DraftStartPacket` → others see an anchor dot; the acting player gets the live ghost
   (full placed-guide pipeline: colors, scale, Surface slabs, far-foot Blue/Indigo, the picked shape).
2. Settings changed mid-draft apply live to the ghost. **CTRL** snaps level/cardinal; **SHIFT** performs the
   current stage's invert/vertical/flat-side/flare action; **CTRL+SHIFT** gives a 45-degree Line/Free-Shape
   diagonal. The live held-help rows state the applicable meaning.
3. **Completing click:** client cap pre-check (factory shape, filled-aware) → `GuideCreateRequestPacket`
   (base + settings + shape/constraint/plane + inverted/sides/apex/chain/rim/flat-side fields) → server builds via the
   factory, stores (stamping the as-placed spring-back snapshot), records `CreateGuideCommand`, broadcasts
   full state. **Three-click triangles:** the second click stores the base's far end (client-side only);
   the ghost's apex then tracks the crosshair — **SHIFT centres it on the base (0.1.15)** — and the THIRD
   click completes. **Free-Shape (0.1.15):** every click chains a corner (CTRL snaps relative to the
   PREVIOUS corner); clicking the LAST corner finishes open, the FIRST (≥3) closes the loop; the full
    chain + closed flag cross in the create request. **Fillet:** click the sharp corner, first profile side and
    second profile side, then chain an unsnapped open sweep path; clicking its last point again finishes. The
    profile endpoints define the rolling corner directly, and route corners are sculpted transitions rather
    than mitres. Internal save/wire identifiers remain `Roundover` for compatibility.
   Cylinder/Polygonal Prism/Cone use a third height
   click; Tapered Cylinder/Tapered Polygonal Prism add a fourth rim-radius click. **The free Rectangle's
   third click is a WIDTH** (its second having set one edge, v0.4.15), and **the Box's fourth click is its
   height** — an ordinary height, which must not inherit the tapered rim's flare clamp or CTRL/SHIFT
   modifiers. A tapered rim cannot exceed
   its base radius unless SHIFT is held; CTRL closes it to a point. Right-click steps any multi-click draft back one click
   (chains retract a corner, triangles the base end; otherwise the draft is discarded).

### Grabbing and reshaping (Create mode)
1. **Left-click an exact rendered cell** → grab (lock acquired, `GuideLockStatePacket` broadcast). **Body
   cells:** Arch/Free-Shape insert the exact clicked cell and adopt it as a grab in one gesture (a constrained
   guide **breaks first** — one undo command, one full-state broadcast carrying both changes). Other
   parametric bodies do nothing unless the cell is a coloured control marker. Dome alone maps an exact visible
   base-circumference cell to the nearer diameter anchor; upper-shell cells and empty space do not grab.
2. **Dragging:** anchors snap to block faces (CTRL → cardinal line through the other anchor; Session 11 —
   SHIFT+left-click on a guide is now spring-back-to-original instead of a grab); interior
   points move at retained depth. The client previews locally with full geometry semantics; giant guides may
   use their structural wireframe during motion while preserving selected-scale precision near the cursor.
   Preview still includes **soft-point flow** (unlocked interior points flowing proportionally with the structural baseline) and any constraint
   break (mirror constraint cleared at grab start). Throttled sends (~100 ms); the server composes the
   authoritative batch (grabbed edit + its own soft-flow reflow), cap-checks once, broadcasts to everyone.
   Dragging a circle's minor handle auto-breaks circle → ellipse on the first move.
3. **Release (left-click):** one `MoveControlPointCommand` per moved point (origin → final), lock freed.
   **Right-click instead:** cancel — origins restore authoritatively, the retained settled mesh reappears
   immediately, transient generations invalidate, and an insert-born point is removed entirely.
   Tool swap mid-drag = comatose (suspend, resume on re-equip).
4. **Idle right-click:** the first rendered voxel hit is authoritative. A point toggles only when that voxel
   is its nearest visible marker cell; an adjacent arch/Free-Shape body voxel receives its own passive lock
   marker (B-S9-1 closed in v0.2.36). Other parametric bodies map to the nearest meaningful handle.

### Editing a placed guide (Edit mode — Session 10)
Switch the Mode row to **Edit**, then **left-click a guide to select it** (empty click deselects; select-only
— no reshaping). The F-menu's Scale / Projection / Plane / Fill-or-Form / Divisions / Visibility rows drive THAT
guide through the send API (`SendRescale` / `SendSetProjection` / `SendSetFilled` / `SendSetWireframe` / `SendSetDivisions` /
`SendHide`) instead of the tool defaults — no separate panel section, so the GUI never expands. Reshaping
(grab / insert / lock) stays in **Create**.

### Projection, plane, fill/form (F-menu tiles — tool defaults in Create, the selected guide in Edit)
Volumetric ↔ Surface: switching a guide **to** Volumetric bakes the flattened positions into its points
(what you saw is what you get; single undo step; full-state broadcast); switching back **to** Surface
restores its stored plane. Plane, 2D Fill, and 3D Shell/Wireframe Form apply atomically with cap re-checks and
roll back on rejection. All rebuild every client's mesh via the normal change events.

### Transforming a placed guide (Transform mode — v0.3.86–v0.4.0; transactional hardening v0.4.55)
Select a guide exactly as in Edit, then act on it as a **whole object**: move, rotate, copy, mirror. Nothing
here reshapes, so the mode adds no geometry verbs — the same argument that let Edit exist.

- **One compound pad action is one message** (`GuideTransformPacket`, protocol 19): an optional mirror, an
  optional rotation and an optional translation together, applied in place or to a fresh **copy**. One
  server operation, one validation, **one undo step** — a compound action must not decompose into several.
- **That promise is enforced at the manager transaction boundary since v0.4.55.** Public and F4-private
  authority snapshot both point lists, shape axis and projection plane; apply rotate → mirror → translate;
  validate the final bounds, caps and claims once; then commit once or restore exactly. Undo applies the
  inverse in reverse order under the same boundary. A failed compound action cannot leave only rotation
  committed or another client on stale geometry.
- **The delta is whole voxels, as integers in 1/16 units.** A fractional nudge is not expressible by
  construction, which is what lets a pure translation reuse the cached voxel count.
- **The pivot never crosses the wire.** The authority derives it from the guide, so the two sides cannot
  disagree and a client cannot nominate one that would put the guide somewhere it should not go.
- **Rotate and mirror do NOT preserve the voxel count** — a shape can re-phase on the lattice and genuinely
  occupy a different number of voxels. Only translation may reuse the count, and undo must store the pivot.
  Full statement: `dev/GOTCHAS.md` **G5**.
- **Send-to-ground deliberately ignores Step and Mirror** while Copy applies — it reads as a bug and is not
  one. `dev/GOTCHAS.md` **G19**.

### Delete mode
Left-click a guide → dispel. Ownership routes the request to local or server authority; the authority owns
lock validation and any admin override. The GUI's other rows disable.

### Undo / redo
Ctrl+Z / Ctrl+Y (inert unless the active tool gate is satisfied) → the authority of the last successful
mutation finds that player's most recent still-valid command. Stale ones are skipped; cap-blocked commands
are preserved and reported. Merely selecting a guide or changing public/private placement mode does not
redirect history. Publication is a committed transfer and is intentionally not undoable.

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
- **Private client persistence:** `ClientWorldGuidePersistence` stores local guides under
  `Layout/ClientOnlyGuides/<hash server-or-savegame>-<hash player UID>.json`. Writes use a temporary file
  and atomic replacement, retain one `.bak`, and quarantine unreadable primary files as `.corrupt-*` before
  attempting recovery. If the storage identity/path is unavailable, local authority remains usable with
  transient in-memory persistence for that session. This never touches the world's main save payload.

---

## 7. Concurrency and Edge Cases

- **Multiplayer model:** public guides remain world-shared and server-authoritative. Private guides are
  player-owned client data and can overlay public guides only when `allowClientOnlyMode` permits it. They are
  not visible or backed up by the server until explicitly published. Server administrators can prohibit the
  private overlay (default) but cannot inspect guides that exist only on a client.
- **Guides are non-targetable without the active gate:** pure mesh draws, no selection/collision/entity
  backing; interaction stays inside the Layout-tool path or the Hammer+Flax fallback path.
- **Disconnect mid-grab** → locks freed and broadcast; uncommitted drag never recorded; undo history cleared.
  **Mid-draft** → draft cancelled, anchor dot removed.
- **Two players grab one guide** → first packet wins; the second sees the lock state, click is a no-op.
- **Over-cap mid-edit** → server counts with the current 2D fill / 3D form before committing; rejects, warns, reverts. Clients
  pre-check to avoid jank.
- **Cross-player undo** → stale commands are skipped, never corrupting state; valid-but-rejected commands
  are `Blocked` and preserved.
- **Mixed selection/mode changes** → do not change ownership and do not reroute undo. A pushed guide receives
  a new public ID, creator UID, public anchor palette, and no inherited private undo entry.
- **Block under a guide removed** → nothing happens; guides never bind to blocks.
