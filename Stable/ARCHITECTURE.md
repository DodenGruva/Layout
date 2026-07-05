# Layout — Architecture Document (v2.5)

**Supersedes v2.4 — consolidation revision.** Content is identical to v2.4's *current state*; what changed is
the document itself: the five stacked changelogs (v1→v2.4) are flattened into the **Settled Decisions
Register** below (every do-not-reopen decision survives with its one-line rationale; the batch-by-batch
narrative does not), superseded-text-plus-banner sections are rewritten as current state, and §5's flows now
describe the shipped two-mode control scheme directly. If a future session needs the historical narrative, it
lives in the v2.4 copy and in `PROJECT_STATUS.md`'s session records.

**Where the project stands:** Layout v0.1.0 is a built, playtested mod **deployed to real-played game worlds**
(end of Session 8). All seven modules plus the Session-8 capability wave (two-mode controls, the four-shape
catalog under absorb-or-break, soft-point flow, Tier-2 fill, sampled-curve targeting, the tile GUI, the
Surface-exit bake) are compile-validated against VS 1.22.3 / .NET 10 and verified in-game.

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

The tool is a held item with an F-key **tile menu** (Create/Delete mode, the shape picker, voxel scale
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
- **Two modes only: Create | Delete.** Left-click priority chain: release grab → second foot (a draft
  outranks guide targeting) → precise point grab → body insert+grab in one gesture → first anchor.
  Right-click is the universal cancel (fresh insert-born point removed; pre-existing point snaps back),
  draft discard, idle point lock-toggle, idle body **lock-in-place insert** (point born locked on the curve;
  two undo steps). Replaced the earlier Create/Edit/Lock/Dispel scheme — modes fought the flow.
- **No grab snap radius.** Precision is the tool's ethos; a near-miss on the body inserts (that's intent,
  not error), and the accepted miss-case is a stray anchor + right-click.
- **Targeting tests the sampled curve, not control-point chords** (per-guide fingerprint-cached polylines).
  Chords miss the real curve at an arch's feet — the root cause of near-anchor grabs failing.
- **Absorb-or-break** is the constraint contract: half-circle feet and circle diameter anchors absorb;
  a body insert on any constrained shape, or dragging a circle's minor handle, breaks to the free parent —
  server-authoritative, seamlessly materialised, one undo step, instant client preview via a sanctioned
  mirror constraint-clear.
- **Soft-point flow, proportional + frame-relative.** Structural = anchors + locked; everything else flows,
  keeping its arc-length station and a baseline-local, length-scaled offset. Absolute world offsets were
  built first and **rejected in play** (a shrunk arch kept its full apex height). Locking is the only pin.
- **Ellipse family body clicks map to the nearest handle** (grab on left, lock-toggle on right) — a
  parametric ring has nothing to insert. *(Flagged for review, like all Session-8 ellipse ergonomics.)*
- **Comatose grabs:** tool swap suspends a grab (lock + one-undo-entry drag persist server-side), never
  releases it; re-equip validates and resumes. Players need to place ladders mid-drag.
- **SHIFT cardinal constraint** in two places: drafting the second foot (level + cardinal from the first)
  and re-grabbing an anchor (same snap, referenced to the guide's **other** anchor).
- **Raycast targeting throughout:** anchors require a block target; interior points snap to blocks or move
  at retained grab-depth in air; no scroll-wheel grab-distance (scroll reserved).
- **Guides are visible but untargetable when the tool isn't held** — pure mesh draws, no selection/collision
  geometry, all interaction gated to the held-item path.

### Data & wire
- **Pinned, append-only enums** everywhere a value crosses wire or disk; **default-driven migration** via
  `DataVersion` (currently **4**: added `Constraint`, `ShapePlaneAxis`; v3 added `CreatorUid`).
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
  circle = ellipse+constraint, half-circle = arch+constraint). Keeps `GuideShapeType` short and lets future
  favorites store {type + constraint} pairs.
- **`ShapeFactory` is the single shape construction point** — adding a shape touches the factory + the shape.
- **Centripetal Catmull-Rom (α = 0.5)**, apex at 40% of chord above the midpoint, phantom endpoints derived
  for provably vertical feet.
- **Arch-family fill = the region between the curve and the foot-to-foot chord line** (human-confirmed
  design), as a ruled surface at ≤ half-cell steps; **ellipse-family fill = the disc**. Caps count filled
  voxels **exactly** (generated, not estimated) — correctness over performance, per standing rule.
- **Circle → ellipse is the break floor** (v1): an ellipse does not break further into a free closed spline.
- **3D volumes (spheres, cones…) are in scope LATER; current shapes stay planar** — the per-shape intrinsic
  plane (`ShapePlaneAxis`, captured from the first click's face) is already in the contracts for that future.
- **Fill is a guide property (`IsFilled`), constraints are modifiers — neither is a shape type.**

### Rendering
- **The verified draw recipe:** Opaque stage + manual blend (not OIT); `PreparedStandardShader` overridden to
  full-bright; a **real white texture** (generated 2×2, asset fallback — id 0 samples garbage); the full
  pos+**uv**+rgba vertex layout with uv (0,0). Each element was a genuine independent playtest bug.
- **Single-voxel nearest-claim markers** (anchors/apex/locked/grabbed-White), precedence Locked > Primary >
  Anchor; the apex claims **2 voxels on even spans** (a lone voxel reads off-center by half a cell).
- **Surface guides render as paper-thin slabs (0.01)** hugging the wall face on the **air side** (world
  solidity probe; majority fallback) with a **plane-axis-only** inset; volumetric anti-z-fight via a
  whole-mesh 0.003-block camera nudge per frame.
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
  clients on join so the pre-check matches enforcement.
- **Client `layout-client.json`:** remembers scale / projection / fill / **shape + constraint** (validated
  pairs) plus the six opacities; client-retained, never synced. **Default scale 1** (chisel-matched).
- **The tile GUI:** every control is a row of exclusive toggle tiles; the **main rows are permanent tool
  defaults and never change meaning** (Mode, the shape picker, the Favorites placeholder strip, Scale,
  Projection, Plane, Fill); selecting a guide appends a separate Selected-guide section (own tiles +
  **Deselect**); **Delete mode ghosts everything but Mode** (~22% alpha, unlit, input-guarded). F-modal
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
    │   ├── GuideData.cs                  [DataVersion 4]
    │   ├── ControlPoint.cs
    │   ├── VoxelPosition.cs
    │   ├── GuideShapeType.cs             [Arch, Ellipse]
    │   ├── ShapeConstraint.cs            [None, SemiCircle, Circle]
    │   ├── ProjectionMode.cs
    │   ├── ProjectionPlane.cs
    │   └── GuideRenderSettings.cs
    ├── Shapes/                           [pure math]
    │   ├── IGuideShape.cs
    │   ├── CatmullRomSpline.cs
    │   ├── ArchShape.cs                  [free spline + SemiCircle arc mode + ruled fill]
    │   ├── EllipseShape.cs               [closed planar primitive; Circle = constraint]
    │   ├── ShapeFactory.cs               [the single shape construction point]
    │   ├── SoftPointFlow.cs              [proportional frame-relative flow; runs on both sides]
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
    │   ├── GuideToolGui.cs               [tile GUI]
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
            └── BreakConstraintCommand.cs
```

**43 source files.** Namespaces match folders: `Layout`, `Layout.Guide`, `Layout.Shapes`, `Layout.Systems`,
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
    int               DataVersion       // 4; older saves migrate by defaults
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
    VoxelRenderType Type   // Normal | Locked | Primary | Anchor | Grabbed
}
```
In Surface mode the same struct renders as a paper-thin slab on the plane.

### GuideShapeType / ShapeConstraint
Pinned, append-only. Constrained variants are **not** types; fill is **not** a type.

```
enum GuideShapeType  { Arch = 0, Ellipse = 1 }
enum ShapeConstraint { None = 0, SemiCircle = 1, Circle = 2 }
```
The four-entry catalog: Arch = (Arch, None) · Half-circle = (Arch, SemiCircle) · Ellipse = (Ellipse, None) ·
Circle = (Ellipse, Circle).

### ProjectionMode / ProjectionPlane / GuideRenderSettings
Unchanged since v2: `ProjectionMode { Volumetric, Surface }`; `ProjectionPlane` = a `PlaneAxis`
(X = 0, Y = 1, Z = 2) + an offset in 1/16-block units, with `Horizontal` / `VerticalNorthSouth` /
`VerticalEastWest` factories and a `Default`; `GuideRenderSettings` bundles scale + projection + plane + fill
for creation requests and the draft ghost.

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
  grab) → first anchor. **Delete:** dispel the aimed guide.
- **Right-click:** cancel grab (insert-born point removed; pre-existing point snaps back via
  `GuideCancelGrabPacket`); discard draft; idle point → lock toggle; idle body → lock-in-place insert
  (ellipse family: nearest-handle lock toggle).
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
**`SoftPointFlow.cs`** — capture (station + frame-local, length-scaled offset per soft point, from pre-move
positions, once per drag) and non-mutating reflow; identical code runs server-side (composed into the same
edit batch as the grabbed point) and client-side (drag preview).

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
| `GuideDeletePacket` / `GuideHidePacket` / `GuideLockPointPacket` / `GuideRescalePacket` / `GuideSetProjectionPacket` / `GuideSetFilledPacket` | S→C, C→S | The atomic ops |
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

**`GuideToolGui.cs`** — the tile GUI (modal, F, press-to-open). Every control is a row of exclusive toggle
tiles. **Main rows are permanent tool defaults:** Mode (Create/Delete) · Shape (the initial-shape picker:
Arch · Half-circle · Circle · Ellipse) · the **Favorites placeholder strip** (inset well + "coming soon";
the feature is deferred) · Scale · Projection · Plane (with Auto in the tool context) · Fill. Selecting a
guide appends a separate **Selected-guide section** (own scale/projection/plane/fill/visibility tiles acting
via the send API, its own lock-state header, and a **Deselect** button); shape is shown read-only there
(live guides reshape by grabbing). **Delete mode ghosts every row but Mode** (~22%-alpha ghost fonts, no lit
tiles, input guard). Remote edits to the selected guide relight tiles in place; row-set changes defer a
recompose (never per-frame).

**`GuideHud.cs`** — mode (+ the picked shape in Create) · scale · projection/plane · fill · live ↔/↕
dimensions in voxels and blocks (draft and examined guide, factory-built shapes, fill-aware) · examined
guide's id, lock, count, cap bar (`Cap: 62%` + ⚠ from the warning packet).

### Undo

**`IGuideCommand`** — `CanUndo` / `CanRedo` (direction-specific), `Execute` / `Undo` / `Redo` returning
`GuideOperationResult`; the handler mutates directly and records, so `Execute` is reached via redo.
**`UndoStack`** — pure bounded histories; new actions clear redo. **Commands:** Create / Delete (DeepClone
snapshots) · MoveControlPoint (before/after; one per drag per moved point, soft-flow included) ·
InsertControlPoint (landing index refreshed on re-insert) · LockPoint · RescaleGuide · HideGuide ·
SetProjection (**+ optional pre-bake point snapshot**; undo restores mode/plane then the points) ·
SetFilled · **BreakConstraint** (pre-break constraint + points; undo restores both, redo re-breaks).
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

## 5. Key Interaction Flows (the two-mode scheme)

### Targeting (all clicks)
A raycast resolves to the voxel cell on the first block face at the current scale. **Anchors require a valid
block target.** Guide targeting tests real control points (precise, `max(0.10, voxel)` radius) and the
**sampled curve** for body hits. In Surface mode the clicked face auto-selects the projection plane (UI
override available); the first click of any draft also fixes the ellipse family's **intrinsic** plane.
Guides are referenced off blocks only at placement — never bound; removing the block changes nothing.

### Creating a guide (any shape — same two clicks)
1. Pick the shape on the F-menu tiles (or keep the remembered default). **First click:** draft starts,
   plane axis captured, `DraftStartPacket` → others see an anchor dot; the acting player gets the live ghost
   (full placed-guide pipeline: colors, scale, Surface slabs, far-foot Blue/Indigo, the picked shape).
2. Settings changed mid-draft apply live to the ghost. **SHIFT** snaps the second foot level-and-cardinal.
3. **Second click:** client cap pre-check (factory shape, filled-aware) → `GuideCreateRequestPacket` (two
   points + settings + shape/constraint/plane) → server builds via the factory, stores, records
   `CreateGuideCommand`, broadcasts full state. Right-click at any point discards the draft.

### Grabbing and reshaping (Create mode)
1. **Left-click a point** → grab (lock acquired, `GuideLockStatePacket` broadcast). **Left-click the body:**
   arch family → the server inserts a point at the nearest curve parameter and the client adopts it as a
   grab in one gesture (a constrained guide **breaks first** — one undo command, one full-state broadcast
   carrying both changes); ellipse family → the nearest handle is grabbed instead.
2. **Dragging:** anchors snap to block faces (SHIFT → cardinal line through the other anchor); interior
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

### Projection, plane, fill (F-menu tiles — tool defaults or the Selected-guide section)
Volumetric ↔ Surface: switching a guide **to** Volumetric bakes the flattened positions into its points
(what you saw is what you get; single undo step; full-state broadcast); switching back **to** Surface
restores its stored plane. Plane and Fill apply atomically with cap re-checks (turning Fill on can be
rejected over cap and rolls back). All rebuild every client's mesh via the normal change events.

### Delete mode
Left-click a guide → dispel (no client-side lock pre-check — the server owns exclusivity and the admin
override). The GUI's other rows ghost out.

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

This v2.5 document is the authoritative plan, consolidated to current state: **Layout v0.1.0, in real play**
on live game worlds. Arches, half-circles, circles, and ellipses place, preview, reshape, fill, lock/unlock,
and project onto surfaces in live multiplayer against VS 1.22.3 / .NET 10. History lives in the v2.4 copy;
status, flagged decisions, and the punch-list live in `PROJECT_STATUS.md` and `OUTSTANDING_ITEMS.md`.
