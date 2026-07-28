# Layout — Project Handoff & Analysis Brief

> **Purpose.** A single, self-contained, current-state briefing for anyone (human or AI) picking this project
> up cold — especially for **performance / optimization analysis**. It consolidates scope, status, direction,
> and the performance-relevant mechanics. Updated 2026-07-27 against the built/package checkpoint
> **v0.4.1 on the `beta` branch** (v0.3.53 is live on `main`). Where this file and the code
> disagree, **the code wins** — treat this as a map, then read the `.cs` files it points at.
>
> ⚠️ **`dev/ARCHITECTURE.md` (v3.14) and `dev/PROJECT_STATUS.md` predate Sessions 28–31** and do not know
> about vertex welding, the custom shader, block occupancy, or Transform mode. This file and the session records
> are ahead of them.
>
> **Deeper docs:** `CHANGELOG.md` (player-facing release log), `dev/ARCHITECTURE.md` (the authoritative
> plan + Settled Decisions Register, v3.14), `dev/PROJECT_STATUS.md` (status), `dev/TODO.md` (punch-list),
> `dev/SESSION_9/…/31.md` (per-session history), `dev/PLAN_RENDER_PERFORMANCE.md` (the live rendering plan +
> measurements), `dev/PLAN_BLOCK_OCCUPANCY.md` (the delivered occupancy feature + its disproved first draft),
> `dev/PLAN_CLIENT_ONLY.md` (F4 record), `dev/PLAN_CHALKING_KIT.md` (F5 rationale + deltas),
> `CLAUDE.md` (working conventions).

> ## ⚠️ Performance analysts: start here, not at §9
>
> §9 and §12 below predate Session 28 and repeat two claims that were **disproved**:
>
> - The "approximately 140→80 FPS regression" that closed the rendering arc was a **double-mesh draw bug**,
>   not a cost of greedy merging. Both rejected experiments were rejected on **appearance**, not performance.
> - Every frame-time figure in Sessions 25–27 was measured against a **238 FPS frame cap**. The "4.3 ms
>   off-screen" baseline is the cap, not a floor; the true baseline is ~1.0 ms.
>
> **Current measured position (v0.3.57, uncapped, 8M-voxel guide):** 4.8 ms close / 3.1 ms distant against a
> 1.0 ms empty baseline, on 771 MB of mesh data per frame. Vertex welding took that from 8.2 ms / 1.81 GB
> with a provably identical triangle stream.
>
> **The governing constraint:** guides are **order-dependent translucent geometry** — Opaque stage, manual
> alpha blending, depth-tested, double-sided. On a hollow shell the guide overlaps itself at nearly every
> pixel, so whichever batch draws first wins and writes depth. The accepted appearance is partly a
> by-product of voxel emission order, and **any change that regroups primitives changes the picture**.
>
> Read `dev/SESSION_28.md` and `dev/PLAN_RENDER_PERFORMANCE.md` before proposing renderer work.

---

## 1. TL;DR

**Layout** is a mod for **Vintage Story 1.22.x** (C# / **.NET 10**). It is a CAD-like, voxel-resolution
**construction-planning** tool: players place translucent geometric guide overlays in the world and build
against them by hand. **The mod is visual-only — it never places, removes, or modifies blocks.** Public
guides are server-authoritative/world-shared; ClientOnlyFallback also provides private client-authoritative
guides on servers without Layout and, when server policy permits, alongside public guides.

- **Status:** **v0.4.1** built and packaged; v0.3.53 is the live `main` release. Sessions 30–31 added the
  **Transform** tool mode — move, rotate, copy and mirror a whole guide without touching its shape, from a
  state-driven direction pad (see §4). Sessions 28–29 added a
  **custom guide shader** (guide frame cost 8.2 ms → 1.8 ms on an 8M-voxel guide), **vertex welding**
  (−72.9% vertices, no visible change), **settled-shell streaming**, procedural **voxel outlines**, and the
  **block-occupancy recolour** — guide voxels already holding world material are drawn cyan and update live
  as the player builds. See §8, §9, `dev/SESSION_28.md`, `dev/SESSION_29.md`.
  The earlier v0.3 arc is playtest-driven: behemoth motion stays
  wireframe-cheap; immense placement and final sculpt validation use a single low-priority server lane;
  selected-scale shells stream to the client in bounded, organic neighbour-growth batches; old GPU batches
  retire across frames; and persistent Shell/Wireframe mode works. The HUD is fixed-size and action-aware;
  guide attribution is available through `/layout who`; active projection switches preserve anchors;
  whole-guide frustum rejection and render statistics are present. Personal `/layout off|on` state persists;
  while it is off, chat/HUD explain how to restore it and the Chalking Kit cannot invisibly mutate guides.
  F4 (client-only / private guides) and F5 (**the Chalking Kit**: finite chalk
  durability + powder refills + deflating **5-state** models) are both feature-complete. The large-guide
  **mesh pass Stage A (exposed-face meshing) has shipped** and filled 3D interiors are retired. Volumes may
  persist as their hollow **Shell** or canonical structural **Wireframe**.
- **Size:** **81 source files** (`src/`), ~one asset tree (now including `assets/layout/shaders/`), one
  `.csproj`.
- **Data schema:** **DataVersion 12** (creator/Last Sculptor attribution; v11 `IsWireframe`; v10 cached metadata).
  Move changes nothing here — a translated guide persists through the fields it already had.
- **Wire protocol:** **19** (whole-guide rotation and the compound transform/copy; 17 whole-guide
  translation; 16 personal render state; explicit immense-placement
  rejection; player moderation policy; `/layout who` query; v12 attribution metadata; v11 wireframe state;
  protocol 9 polygon orientation; earlier append-only fields remain compatible with matching builds).
- **Catalog:** **15 shape types**, shown as **21 picker tiles** — a full 2D family plus an eight-volume 3D family.
- **The tool:** the **Chalking Kit** — 32-chalk durability (2D −1 / 3D −2, completed placements only; no
  lockout at 0, the kit can never break). **32 is a HARD ceiling** (`ItemGuideTool.MaxChalk`, v0.2.23):
  chalk is read straight off the stack attribute and clamped, and `GetMaxDurability`/`GetRemainingDurability`
  are overridden *without calling base*, because base walks the collectible's BEHAVIORS — the hook other mods
  (xskills) use to grant crafting-quality durability. Refilled with **Chalking Powder** (+4 each) via three
  channels: ground-stored kit in place (always allowed) plus hotbar tap/hold and cursor-onto-inventory-slot,
  the latter two **client-preference opt-in** (`layout-client.json`, v0.2.22 — a player setting, not server
  policy). Private placements charge via a client-reported, server-validated packet; creative exempt;
  `enableChalkDurability` server config. Five fill-state models (full=32 · high 22–31 · medium 11–21 ·
  low 1–10 · empty 0) render in every context including ground storage.
- **Closed in v0.2.36:** B-S9-1 adjacent-lock targeting. Exact rendered-cell ownership prevents a formerly
  locked marker from shadowing its neighbor. Two unrelated verification debts remain: xskills itself and a
  VS 1.22.0/1.22.1 smoke test.
- **Current performance state:** interaction-side large-guide work was rebuilt in v0.3.0–v0.3.40. Motion
  uses bounded/adaptive wireframes, the cursor keeps selected-scale precision, exact occupancy calculates
  off-thread, and connected mold-like splotches stream through bounded queues. Public immense count/footprint
  work is isolated from the server tick; claim checks consume at most 128 blocks / about 1 ms per tick. Placed
  hover uses cached metadata and whole guides outside view distance/frustum are not drawn. v0.3.43's spatial
  meshes and v0.3.44–v0.3.48's greedy/depth experiments were rejected after visible seams, fidelity defects,
  and an approximately 140→80 FPS real-play regression; v0.3.49 restored the v0.3.42 renderer. See §9 and
  `dev/SESSION_21.md`–`SESSION_27.md`.
- **Design philosophy (standing rule): correctness over performance** unless told otherwise. Several
  deliberate un-optimized paths exist by choice; see §9.

---

## 2. Build & run

- **Build:** `dotnet build Layout.csproj` from the **repo root** (the folder containing `Layout.csproj`).
- **Target:** `net10.0`, Vintage Story 1.22.x. References resolve via the `<VintagestoryDir>` csproj
  property, which auto-detects the install: `-p:VintagestoryDir=…` → the `VINTAGE_STORY` env var → the
  platform default (`%APPDATA%\Vintagestory` on Windows, `~/.local/share/vintagestory` otherwise). No edit is
  needed for a normal install, and a wrong/missing path fails with one clear message naming the folder tried.
- **Dependencies** (all ship with the game): `VintagestoryAPI.dll` (install root), `Newtonsoft.Json.dll`,
  `protobuf-net.dll`, `cairo-sharp.dll` (GUI icon glyphs) in `Lib\`, and `VSSurvivalMod.dll` (in the game's
  `Mods\`; `IContainedMeshSource` for ground-storage fill meshes). None are bundled into the mod zip.
- **Package a runnable mod:** build **Release**, then zip `modinfo.json` + `modicon.png` + `assets/` +
  `Layout.dll` at the **zip root** (forward-slash entry paths); drop into `VintagestoryData/Mods`. Config
  files (`layout.json`, `layout-client.json`) appear in `ModConfig` after first run.
- **Versioning rule (standing):** every revision bumps `modinfo.json` and ships as a new
  `Layout<version>.zip` in the **sibling `..\LayoutZips\`** folder — older zips are never overwritten.
- **Runtime note:** `Entity.SidedPos` is obsolete in this API version — use `Pos`.

---

## 3. Repository layout (note the nesting)

```
Layout/                         ← repo root = git root; holds the MOD CODE
├── CLAUDE.md                   ← working conventions (read at session start)
├── HANDOFF.md                  ← THIS FILE
├── Layout.csproj  modinfo.json  modicon.png
├── assets/layout/              ← itemtypes, textures, lang
├── assets/layout/shaders/      ← guide.vsh / guide.fsh — MUST be pure ASCII
├── CHANGELOG.md                ← player-facing release log (part of the doc-update set)
├── src/                        ← all 81 .cs files (see §6)
└── dev/                        ← ALL PROSE DOCS live here (NOT the code)
    ├── ARCHITECTURE.md  PROJECT_STATUS.md  TODO.md
    ├── SESSION_9.md … SESSION_27.md  SESSION_28.md … SESSION_31.md
    ├── CHANGELOG_ARCHITECTURE.md   ← ARCHITECTURE.md's per-revision deltas (archive)
    ├── PLAN_CLIENT_ONLY.md  PLAN_CHALKING_KIT.md  BUILD_INSTRUCTIONS.txt
    ├── PLAN_RENDER_PERFORMANCE.md  ← Session-28 rendering plan + every measurement taken
    └── PLAN_BLOCK_OCCUPANCY.md     ← delivered; its §0 records the disproved first draft
```

**Gotcha for tooling:** the docs are in `dev/`; the code is one level up in `src/`. A glob rooted at `dev/`
will not see the source. The mod project was flattened to the repo root on 2026-07-06 (it used to be nested
in a versioned subfolder).

**Folder case, now fixed (2026-07-26):** git used to track this folder as `Dev/` while the working copy on
Windows was `dev` and all prose wrote `dev/`. Windows' case-insensitivity hid the split, but on Linux and
GitHub the capitalised name was the real one, and a doc added as `dev/…` would have created a second folder.
Renamed in git to lowercase throughout, so disk, index and prose now agree. Historical note only —
`dev/SESSION_10.md` still contains one pre-flattening `Dev/` reference that means the old repo root.

---

## 4. What the mod does (mechanics & scope)

- **The tool** is normally the held **Chalking Kit** item (custom deflating **5-state** model; recipe: 8×
  Chalking Powder + linen sack + flax twine + rope + copper nails; **32-chalk durability**, F5 — completed
  placements cost 2D −1 / 3D −2, refills via Chalking Powder [8× any powder/flour + 0.1 L yellow dye → 8],
  no lockout at 0, never breaks, and 32 is a hard ceiling no crafting-quality mod can raise; also
  **ground-storable**: SHIFT+right-click sets it down, SHIFT+right-click with powder refills it in
  place — and, where the PLAYER opts in via `layout-client.json`, a hotbar tap/hold or a
  cursor-onto-inventory-slot refill). On a server without Layout, the equivalent gate is
  **Flax Twine main-hand + any vanilla Hammer variant off-hand** (damage irrelevant; no chalk there — a
  custom item cannot exist on a vanilla server). Interaction is entirely **first-person clicks + crosshair
  raycast** — no transform gizmos. Guides are **visible but untargetable when the tool is not held** (pure mesh draws, no
  selection/collision/entity backing), so they never interfere with the blocks underneath.
- **Four tool modes** (`ToolMode`, client-only, never wired): **Create** owns ALL geometry (place, grab &
  reshape, insert, lock; right-click = cancel / lock-in-place). **Edit** is settings-only: left-click
  **selects** a guide and the GUI's setting rows then act on THAT guide (no reshaping); right-click deselects.
  **Move** (F6, v0.3.86) selects the same way and then translates the whole guide — never reshaping it —
  either by clicking the GUI's direction pad or by arming free-move and dragging on the crosshair.
  **Delete** dispels. The fixed HUD names the current action, uses a contextual shape tile, and shows exact
  settings/dimensions/count/cap without resizing. `/layout who` reports Creator and Last Sculptor on demand.
- **Transform mechanics (Sessions 30–31).** The panel's **direction pad is STATE-DRIVEN**: three latching
  toggles — Move, Copy, Mirror — decide what its six arrows and four rotate corners do. Move and Copy are
  mutually exclusive (Move+Copy is just Copy), Mirror is independent, and at least one is always lit, so the
  pad is never inert. The five reachable actions are move, move+mirror, copy, copy+mirror, and mirror in
  place; a rotate corner with Copy lit yields a duplicate turned a quarter. The HUD names the compound
  ("Transform · Copy + Mirror") because a copy costs chalk and the other actions do not.
  - **Distance.** Steps are whole multiples of the guide's own voxel scale (×1/×2/×4/×8/×16); the authority
    refuses anything finer (§7). **Span** instead steps by the guide's own extent along the pressed axis, so
    a copy lands flush. Span is the DEFAULT for copies and mirrored moves and merely AVAILABLE for a plain
    Move, which keeps its one-voxel nudge; clicking a lit step tile toggles back to span. Repeating one copy
    direction marches outward (1, 2, 3 spans) so a run lays a line rather than a stack.
  - **Direction.** The four horizontal arrows resolve against the player's facing snapped to the nearest
    world axis (the panel holds the mouse cursor, so the facing cannot drift between clicks); Up/Down are
    world vertical. Rotation is quarter turns only — the top corners spin about the vertical, the bottom
    corners tip about the axis running away from the player.
  - **Cost.** Locked points and the as-placed spring-back snapshot travel with the guide. Move, rotate and
    mirror charge no chalk and cannot breach a voxel cap, but their destination IS re-checked against land
    claims. **A COPY is a placement**: it charges chalk, counts against the cumulative creator cap, and can
    therefore be refused where the others cannot.
  - **Mirror needs only one axis choice per press.** Quarter turns about two axes reach all 24 orientations,
    and adding any single reflection generates all 48 — so mirroring about the guide's OWN CENTRE plane,
    composed with rotate, reaches everything. It also leaves the guide where it stands. Consequence: with
    Mirror alone, left/right (and away/toward, up/down) are the same mirror.
- **Placement** is **two clicks for most shapes**, with deliberately-reopened exceptions: free/right/
  isosceles triangles and Cylinder/Polygonal Prism/Cone/Box take **three clicks** (base + height);
  Tapered Cylinder and Tapered Polygonal Prism take a **fourth** click for the top radius; the
  Free-Shape takes **unbounded chained clicks** (≤64). Right-click steps a multi-click draft back one click.
- **Reshaping (2D):** grab a point to move it; click the body to insert-and-grab (arch + Free-Shape
  families) or grab the nearest handle (every other parametric shape); lock points as constraints. Two
  standing contracts govern feel:
  - **Absorb-or-break:** a grab a constraint can absorb, it absorbs; one it cannot absorb demotes the shape
    to its free parent (half-circle→arch, circle→ellipse, equilateral→free triangle), seamlessly & undoably.
  - **Soft-point flow (slave-regime):** an interior grab slaves unlocked points onto the defining curve with
    zero offset (the hand leads); a structural grab (anchor/lock) keeps shape-preserving proportional flow.
    **Locking is the only thing that pins geometry.**
- **Per-guide settings:** voxel scale (1/2/4/8/16 sixteenths; default 1 = chisel resolution), Volumetric↔
  Surface projection, hollow↔filled, a purely-visual **equal-parts Divisions** overlay, hidden↔shown.
- **Held keys are stage-aware:** **CTRL** = level/cardinal snap, or close a tapered rim to a point; **SHIFT**
  = vertical Line/Free-Shape, draft invert where applicable, flat-side polygon alignment, deliberate tapered
  rim flare, or reset a placed guide; **CTRL+SHIFT** = a 45-degree Line/Free-Shape diagonal. Native held-item
  notes appear only where a modifier applies. All hotkeys are rebindable and inert unless the tool is held.
- **Color language (authoritative table is in `GuideMeshBuilder.cs`):** yellow body · red locked · **green**
  apex/primary · public/fallback **blue** anchors (indigo off-shade) · mixed-server private **orange**
  anchors (burnt-orange off-shade) · white grabbed · magenta division marks · hidden guides = anchors only
  at low alpha. Anchor opacity applies to both ownership palettes; other role alphas remain configurable.

---

## 5. The shape catalog (15 types / 21 tiles)

Built on a **primitives + constraint-modifiers** model — constrained variants are **not** separate types,
and fill is **not** a type. `enum GuideShapeType { Arch=0, Ellipse=1, Line=2, Triangle=3, Rectangle=4,
Polygon=5, FreeShape=6, Sphere=7, Dome=8, Cylinder=9, Cone=10, Box=11, TaperedCylinder=12,
PolygonalPrism=13, TaperedPolygonalPrism=14 }`.
`GuideShapeTypes.IsVolume(t)` classifies the 3D family (an explicit switch, never inferred from ordering).

**2D section (13 tiles):** Arch · Half-circle (Arch+SemiCircle) · Circle (Ellipse+Circle) · Ellipse · Line ·
Triangle · Right · Equilateral · Isosceles (Triangle + constraint) · Rectangle · Square (Rectangle+Square) ·
Polygon (regular N-gon, 3–24 sides, count in `GuideData.Sides`) · Free-Shape (irregular polyline, `IsClosed`).

**3D volume section (8 tiles):** Sphere · Dome · Cylinder · Tapered Cylinder · Polygonal Prism · Tapered
Polygonal Prism · Cone · Box. The polygonal pair shares Polygon's editable 3–24 side setting. **Volumes are always a one-cell
hollow shell — Filled is retired for the 3D family (v0.2.17):** post-exposed-face-meshing a filled interior
draws nothing, so it was pure invisible voxel cost; every volume shape now coerces `filled=false`, which also
auto-lightens legacy filled saves. **Always Volumetric** (Surface + Divisions gated off, server-side and in
the GUI). Box uses a cell-lattice scan; hollow Sphere/Dome use the exact surface-area-oriented
`SphericalShellScan`; Cylinder/Cone remain centre-banded. Deterministic up-axis (`ShapeGeometry.BaseNormal`);
the first click's face direction orients a Dome (floor→up, ceiling→down, wall→toward you) and SHIFT inverts
(e.g. dome→bowl). Targeting is a **wireframe** — the anchors and the height handle are the reliable grab
points. A cylinder cap / dome floor is recovered cheaply with a filled 2D circle at the base.

Adding a shape starts in `Shapes/ShapeFactory.cs` (the single construction point) + a new `IGuideShape`.

---

## 6. Architecture — layered module map

Pure, dependency-light layers under a server-authoritative core. Namespaces match folders (the one exception:
`UndoManager` lives in `src/Systems/` as `Layout.Systems.UndoManager`).

| Folder | Namespace | Role |
|---|---|---|
| `src/` | `Layout` | `LayoutModSystem` — composition root (registers systems, item, channels, keybinds, HUD). |
| `Guide/` | `Layout.Guide` | Pure data: `GuideData`, `ControlPoint`, `VoxelPosition`, the pinned enums, projection/render settings. Depends only on `Vec3d`. |
| `Shapes/` | `Layout.Shapes` | Pure geometry math (no engine deps beyond `Vec3d`). `IGuideShape` seam; `ShapeFactory`; 14 shape-generator classes backing 15 enum types; progressive/cancellable voxel generation and organic neighbour-growth ordering; `CatmullRomSpline`; `VoxelMarch`; `ShapeGeometry`; `SoftPointFlow`; `DivisionMarks`. |
| `Systems/` | `Layout.Systems` | Side-neutral `GuideManager` (authority + JSON persistence + cap validation), persistence/block-probe seams, `GuideLockManager`, `DraftManager`, `UndoManager`, `GuideRenderer`, `GuideMeshBuilder`. |
| `Network/` | `Layout.Network` | `PacketTypes` (protobuf DTOs, fixed append-only registration), `ServerNetworkHandler`, `ClientNetworkHandler`. |
| `UI/` | `Layout.UI` | `GuideToolGui` (the F-menu icon-tile GUI), `LayoutToolIcons` (Cairo glyphs), `GuideHud`. |
| `Config/` | `Layout.Config` | `LayoutServerConfig` (`layout.json`), `LayoutClientConfig` (`layout-client.json`). |
| `Items/` | `Layout.Items` | `ItemGuideTool` — stateless glue; routes clicks to the controller. |
| `Client/` | `Layout.Client` | `GuideToolController` plus `LocalGuideAuthority`, authority mode, vanilla-item gate, and per-world/per-UID private persistence. |
| `Undo/` + `Undo/Commands/` | `Layout.Undo[.Commands]` | `IGuideCommand`, `UndoStack`, and 17 command types (… /SpringBack/BreakConstraint/TranslateGuide/RotateGuide/TransformGuide). |

**Data-flow (networked mode):** client `GuideToolController`/GUI/HUD → `ClientNetworkHandler.Send*` →
protobuf packet → `ServerNetworkHandler` → `GuideManager` validates + persists + records undo → **broadcasts
full/atomic state to everyone (originator included)** → `ClientNetworkHandler` applies to the local mirror →
  raises change events → `GuideRenderer` rebuilds that guide's mesh. Immense creates/sculpts instead isolate
  pure count/footprint work on one low-priority worker and return to the tick thread for bounded claim checks
  and commit. Rejections send a corrective full-state resync. Systems communicate by **return value
  (`GuideOperationResult`), not events**.

**Data-flow (client-only mode):** the same UI/controller calls → `ClientNetworkHandler` routes by guide
ownership → `LocalGuideAuthority` applies through a client-side `GuideManager`/`UndoManager` → the accepted
result re-enters the same mirror-apply events → renderer/HUD rebuild normally. In a permitted mixed world,
the mirror contains both server and local ID sets; existing-guide edits route by ownership, while placement
mode decides only where a new guide is created.

---

## 7. Data & wire model (the invariants)

- **Voxels are NEVER stored.** A guide is fully defined by control points + settings; the voxel set is always
  **derived on demand** by the shape layer. Caps are enforced by *counting via the shape*, never a field.
- **Pinned, append-only enums** anywhere a value crosses wire or disk. **Default-driven migration** via
  `DataVersion` (12: creator/Last Sculptor attribution; 11: `IsWireframe`; 10: cached display/count/dimensions; 9: `FlatSideAligned`; 8:
  `ControlPoint.IsLockMarker`; 7: `IsClosed`; 6: `Sides` + the never-wired as-placed spring-back snapshot; 5: `Divisions`;
  4: `Constraint`/`ShapePlaneAxis`; 3: `CreatorUid`; 2: `Projection`/`Plane`/`IsFilled`).
- **Save format ≠ wire format.** JSON (Newtonsoft, custom `Vec3d` converter) is the **save**; protobuf DTOs
  are the **wire**. The paths are independent; POCOs are mapped to DTOs, never sent raw. Packet registration
  is one fixed shared order, append-only.
- **The index seam:** only control-point *indices* cross the network/undo boundary. Inserts land interior;
  locks prevent mid-grab reshuffling.
- **`Vec3d` is a mutable reference type and is ALWAYS deep-copied, never aliased** (see `ControlPoint`
  remarks). The entire undo system depends on this; snapshots must not alias the live list.
- **Voxel cells are 1/16-block, lower-corner, `Floor(world·16/scale)·scale`** — one quantise convention
  everywhere (`VoxelMarch` mirrors the spline's quantise exactly), or caps and visuals disagree.
- **Authorities build shapes** from two/three clicks + settings: the server does so for public placement and
  `LocalGuideAuthority` does so for private placement. Full private snapshots cross the wire only for the
  explicit `/layout client push all` publication operation. **Tool state is client-side.** `CreatorUid` is
  public-server bookkeeping, never ownership, and ordinary guide DTOs still do not wire it.

- **A whole-guide translation is quantise-exact by construction (F6, v0.3.86).** `GuideManager.TranslateGuide`
  ENFORCES that the delta is a whole number of the guide's own voxels and refuses anything finer. Because
  cells quantise to `Floor(world·16/scale)·scale`, an exact multiple of the scale remaps every cell
  one-for-one, so the voxel count provably cannot change — the cached count is reused instead of rescanning
  a behemoth, and no cap check is performed at all. A sub-voxel delta would move cells by a WHOLE cell
  wherever it crossed a quantise boundary and by nothing elsewhere, deforming the shell. The wire carries
  the delta as integers in 1/16 units, so a fractional nudge cannot even be expressed. Land claims are
  still re-checked: the destination is new ground. Surface guides translate their `Plane.PlaneOffset` too,
  or moving along the flattened axis would look like nothing happened.
- **A ROTATION OR MIRROR DOES NOT GUARANTEE THE COUNT, and must not reuse the cache (Session 31).** The
  lattice still maps onto itself at 90 degrees, but the shape is regenerated from control points rather than
  from moved cells, and `ShapeGeometry.TryGetFrame` sign-normalises m̂ toward world up — so a polygon-family
  guide can return with its vertices in a different phase and a slightly different count. `RotateGuide` and
  `TransformGuide` therefore recount and re-check caps in full, exactly as `Rescale` does. Only
  `TranslateGuide` may skip that.
- **Orientation state travels with the geometry.** A quarter turn remaps `ShapePlaneAxis` (the axis set maps
  onto itself) and re-derives a Surface `Plane`; an axis-aligned MIRROR needs neither, because a reflection
  maps every world axis onto itself — only the plane offset can move. Volumes need no per-shape work either
  way: a volume's rise is `BaseNormal(û, ShapePlaneAxis)` with its SIGN taken from which side the apex
  control point sits on, so rotating or reflecting the apex carries the facing automatically.
- **Undo stores the pivot.** A transformed shape's bounding centre is not generally where the original's
  was, so recomputing it on the way back would drift the guide. `TransformGuideCommand` also reverses in
  order — translate back, THEN reflect about the stored plane — since a reflection is its own inverse only
  while its plane stays put.

Key struct: `VoxelPosition` is a `readonly struct : IEquatable<VoxelPosition>` (hashes all of X/Y/Z/Type),
generated in large quantities and de-duplicated through a `HashSet` — deliberately boxing-free in that hot
path.

---

## 8. Rendering pipeline — Stage A shipped

- **Compiled mesh assets per guide, rebuilt only on change** (`GuideRenderer` listens for mirror-apply change
  events). Ordinary guides retain one mesh; an immense materialization temporarily/settled may own bounded
  primary + auxiliary batches. `GuideMeshBuilder` is a stateless `List<VoxelPosition>` → `MeshData` converter.
- **The verified draw recipe** (each element was a real playtest bug): Opaque stage + **manual blend** (not
  OIT); `PreparedStandardShader` forced full-bright; a **real white 2×2 texture** (texture id 0 samples
  garbage); the full **pos + uv + rgba** vertex layout with uv (0,0); color carried purely by packed vertex
  RGBA.
- **Custom guide shader (v0.3.60, `assets/layout/shaders/guide.vsh`/`.fsh`):** guides no longer use
  `PreparedStandardShader` by default. Drops vertex warping, shadow-map coords, per-vertex light mixing and
  the 3×3 PCF shadow lookup — **8.2 ms → 1.8 ms on an 8M-voxel guide across Session 28**. Guides become
  genuinely self-lit (no shadow darkening), approximated back by `shaderGuideBrightness` /
  `shaderAmbientResponse`. `/layout shader off` restores the old path. **Shader sources must be pure ASCII.**
  A compile failure is non-fatal: the loader logs and falls back to the standard shader.
- **Voxel outlines (v0.3.62–v0.3.63):** per-cell boundaries drawn procedurally in the fragment shader from
  mesh-local position — no extra geometry. `/layout voxelframe`. Notably, the fragment stage therefore
  already knows which voxel cell a pixel belongs to, and identifies the face-normal axis by its screen-space
  derivative rather than a normal (the mesh format carries none).
- **Vertex welding (v0.3.57, `GuideMeshOptions.WeldVertices`):** faces meeting at one position **with one
  colour** share a vertex. **Deduplication, not merging** — the triangle stream is provably identical.
  −72.9% vertices, −58.4% mesh data, −41.5% frame cost on an 8M guide; at ~1.0 vertices per quad this lever
  is spent. **Colour is part of the weld key**, which is why a colour cannot be changed in place afterwards
  (see §8's occupancy note).
- **Block-occupancy recolour (v0.3.79+):** body voxels whose own cell holds world material are drawn CYAN.
  The colour is **baked into vertex data**, so updates re-mesh rather than re-tint. Live updates re-mesh only
  the affected **batch**: a guide gets a batch context built once in the background (X-sorted voxel list,
  cross-batch occupancy set, mesh options, and the voxel range behind each uploaded mesh), after which a
  block change re-meshes only the batches its X span overlaps, on a worker, uploaded incrementally.
  > This is **not** the rejected Session 25–26 spatial partitioning. Guide meshes draw in list order
  > (primary, then `Auxiliary` in sequence) and every batch is a contiguous run of ONE sorted list, so the
  > concatenation is the same primitive sequence wherever the boundaries fall. Moving a boundary reorders
  > nothing — which is what makes re-meshing one batch safe.
- **Exposed-face meshing (Stage A, v0.2.14–v0.2.16):** the Volumetric cube path builds a presence set of
  rendered cells, pre-counts the faces with no neighbour, allocates exactly, and emits **only those faces**
  (4 verts / 6 indices each) — per-voxel role colours preserved. Interior and shared faces vanish, so a
  hollow shell draws only its skin. The Surface tile/slab path stays on the legacy whole-box builder.
- **Anti-z-fight is a per-face geometry OUTSET** (`BlockPlaneInset = 0.0006`; the name is historical), not a
  camera nudge. **Rewritten in v0.3.70 — the pre-v0.3.70 description below is obsolete.** Every EXPOSED face
  of a volumetric guide is pushed a hair OUT of its voxel; faces shared with a neighbouring guide voxel stay
  exactly flush, so no interior seam is possible. The offset is a property of the **guide alone** — exposure
  was always decided against the guide's own voxel set, and since v0.3.70 nothing consults the world either.
  `GuideMeshOptions.IsNeighborSolid`, `NeighborSolidProbe`, `TrackedSolidProbe` and `_deferredSolidity` are
  all deleted, along with the per-face world block lookup a large guide used to make for every exposed face.
  > **Why the direction flipped.** The old inset pulled a face away from a solid world block. That is exactly
  > backwards for the workflow this serves — over-fill a guide with material, then chisel back down to it —
  > because the guide face is then coplanar with the surface just cut and must sit PROUD of it. Critically, a
  > world-driven offset also *cannot* be rebuilt safely: a rebuild after the player fills the volume would see
  > solid neighbours everywhere and flip those faces inward, breaking the guide exactly when it is needed.
  > The magnitude fell 5× with the direction (0.003 → 0.0006): an inset had to open a gap wide enough to see
  > past the surface in front of it, an outset only has to win the depth comparison. Both playtest-settled.
  Surface guides are untouched by this and additionally render as thin slabs
  hugging the **air-side** cell face (world-solidity probe, majority fallback); the probe tolerates the
  world-load race (a re-probe tick rebuilds once unloaded chunks arrive — fix for B-S10-1). A deterministic
  mesh-count harness locks the face counts and flush/inset invariants (15/15).
- **Marker voxels** are single-voxel nearest-claim (precedence Locked > Primary > Anchor > Division); the
  apex and off-cell division boundaries claim 2 voxels on even spans so they read centered.
- **Whole-guide culling (v0.3.33/v0.3.41–v0.3.42, restored v0.3.49):** conservative sampled bounds reject an
  entire guide beyond live `viewDistance` or outside Vintage Story's current camera frustum. v0.3.43's
  32-block spatial final meshes were later removed because their boundaries produced visible seams.
- **Adaptive/streamed draft pipeline (v0.3):** cheap poses retain the selected-scale shell. Expensive moving poses use
  a canonical structural wireframe under work/frame-pressure hysteresis; a four-selected-voxel cursor region
  remains precise and transitions outward across roughly two blocks. After settling, one generation-tagged
  background calculation discovers exact selected-scale occupancy. Several separated seeds then grow only
  through neighbouring voxels under layered noise, streaming torn/mold-like batches through a capacity-three
  queue. Main-thread upload cadence is bounded and backs off under frame pressure. Movement invalidates stale
  generations; matching placement/sculpt authority echoes adopt the existing work instead of rebuilding it.
- **Persistent structural form (v0.3.7):** a 3D guide may be saved as Shell or Wireframe. Persistent wires
  use canonical topology at the selected scale and have their own exact count/cap semantics; they are distinct
  from the temporary adaptive/coarse preview used while moving a Shell guide.
- **Whole-guide translation is a MODEL-MATRIX change, not a mesh change (F6, v0.3.86).** Every guide mesh is
  already drawn through a per-mesh translation, so the free-move preview offsets that number and nothing
  else — no re-meshing, no re-upload, and **no regrouping of primitives**, which is the standing renderer
  constraint. An 8M-voxel guide previews its drag exactly as cheaply as a small one. The cull centre and the
  voxel-frame origin follow the offset, or a dragged guide would be culled against where it used to be.
- **Move materialization hold (v0.3.87–v0.3.89).** A nudged guide that streams (`ShouldStreamSettledShell`)
  parks on its wireframe scaffold for **2.5 s**, restarting on every nudge, so consecutive nudges do not each
  discard a part-built shell. While held, the bottom of the scaffold is rebuilt in **graduated precision
  layers** — finest at the lowest point of the shape's curve, one valid scale coarser per layer until it
  meets the scaffold's own scale — mirroring the dragged-point precision bands, but measuring HEIGHT rather
  than radius. **The coarse wire is CUT AWAY under the band, not overdrawn:** guides are order-dependent
  translucent geometry, so a block-sized coarse cube drawn first wins the depth test and hides the fine
  geometry inside it entirely (the v0.3.88 defect). `ShowMovingDraft` cuts the same kind of hole.
  Scaffold meshes now record the scale they were built at, so the voxel outline stops drawing a 1/16 grid
  across full-block cubes.
- ⚠️ **`OnGuideAddedOrUpdated` starts the shell materialization BEFORE reaching `RebuildGuide`**, and that
  early return never touches the scaffold mesh — leaving a stale wireframe at the guide's OLD pose while the
  shell streams at the new one. Fixed for the Move path only (a held guide goes straight to the rebuild);
  **the general case is still live.** See `dev/SESSION_30.md` §5/§7.

---

## 9. Performance characteristics & deliberate trade-offs

**The interaction and authority spikes have dedicated immense-guide paths.** Stage A reduced steady mesh cost;
v0.3 removed repeated full shape/HUD calculations from motion, v0.3.26–v0.3.40 moved public immense
count/footprint work off the server tick and made placement/sculpt visuals stream through bounded queues.
Standing rule is **input smoothness first for this path**: exact visuals and numbers may settle later, but
cursor/input responsiveness and the server tick must not wait on them. v0.3.41–v0.3.42 measured and removed
whole-guide off-screen GPU submissions. The later spatial/greedy path improved isolated submission numbers
but failed real-play visual and frame-rate validation, so v0.3.49 restored the v0.3.42 implementation.

**Hot paths & large-quantity structures**
- **Voxel generation per guide** — up to the configured cap or unconditional 10M ceiling. During drafts it
  is generation-tagged and deferred until settling when expensive. Sphere/Dome use `SphericalShellScan`;
  oversized Cylinder/Cone/Box switch from their legacy scan to `LargeVolumeShellFallback`, whose work scales
  with rings/faces rather than empty bounding volume.
- **Immense public validation (v0.3.26/v0.3.34)** — creates and final volume sculpts share one below-normal
  worker for isolated exact count + claim-footprint generation. Claim API calls remain on the server thread
  and stop after 128 blocks or about 1 ms per 20 ms tick. Small candidates (≤8,000 voxels) retain the immediate
  path.
- **Organic materialization (v0.3.34–v0.3.40)** — exact occupancy feeds deterministic multi-seed
  26-neighbour growth. Seed count is `6 + ceil(voxels / 500)` (bounded by occupancy), so both small and
  immense shells establish enough independent fronts. Uploads target 750 voxels, cap at 128 batches, and
  scale from about 18 ms for small guides to 45 ms by 120,000 voxels, with additional frame-pressure
  backoff. The scaffold disappears with the first organic batch; complete growth holds for 200 ms before a
  separately built clean, uniform shell replaces it. Placement particles/sound wait for both that clean
  swap and final placement authority.
- **Settled form transitions (v0.3.39)** — Edit-mode Wireframe→Shell changes use the same cancellable
  off-thread materialization path instead of synchronously building the full shell. The wireframe remains
  only until the first organic batch.
- **Intrinsic HUD dimensions (v0.3.40)** — round and polygonal volumes report their local defining width and
  axial height rather than the diagonal of a rotated world-axis AABB. An 83-block dome therefore reports
  83 blocks wide, not 117.
- **Rendering instrumentation and rollback (v0.3.41–v0.3.49)** — `.layout renderstats` reports last-frame
  visibility, draw batches, approximate triangles, extras, and smoothed frame time. v0.3.43 measured a strong
  partial-view spatial result, but broader tests exposed chunk seams. Greedy/depth follow-ups added bands,
  unstable far-side visibility, blur/brightness/depth defects, slow clean swaps, and worse real FPS. The
  accepted implementation is therefore the exact v0.3.42 renderer plus later non-rendering features.
- **Mesh rebuild** — a full `MeshData` rebuild for a guide on every change event. The cube path now emits
  **only exposed faces** (4 verts / 6 indices each, no interior/shared faces), pre-counted and exactly
  allocated. Settled guides mesh at **true scale, never coarsened** (see below).
- **Hollow Sphere/Dome generation (v0.1.53)** — scans X/Y columns and only the analytically bounded Z shell
  bands, then applies the legacy exact predicate. A 20-block hollow dome produced 242,500 voxels in ~5 ms in
  isolated validation; a roughly 100-block Sphere places and now draws only its shell.
- **3D volumes are hollow-only (v0.2.17).** Filled interiors are retired for the volume family, so the old
  filled-Sphere/Dome cubic-scan lag path is gone; Box retains its lattice scan (shell). Do not reintroduce
  filled volumes.
- **Placed-guide hover is metadata-only (v0.3.3).** Human-readable names are cached whole-block dimensions;
  cached count feeds the cap bar, and the placed dimension field is blank. Looking at a behemoth cannot
  trigger voxel generation. Active draft/grab measurements show changing calculation glyphs until ready.
- **Targeting** samples each guide's curve (`IGuideShape.SampleCurve`) into a polyline **cached behind a full
  per-coordinate geometry fingerprint** — resampled only on actual geometry change, not per tick.
- **Division recolor** — `DivisionMarks.Apply` runs on **every mesh rebuild** (draft ghost + placed),
  walking `SampleCurve(128)` for arc length then a nearest-cell claim per boundary. Cheap, but it walks the
  cell list; watch on very high division counts × large guides.
- **Drag** — ordinary live moves preview locally and send at **~10 Hz** (throttled). A behemoth is a local
  wireframe transaction and sends only its final pose; the server validates that isolated candidate off-thread,
  while the client retains/materializes the working pose without a synchronous release rebuild.

**Deliberate, documented trade-offs (do not "fix" without checking intent)**
- **Filled 2D guides recount voxels exactly per drag update** (cells generated each move packet) — no
  per-drag count cache. If large filled discs drag sluggishly, a count cache is the sanctioned fix. (3D
  volumes are hollow-only now, so this is a 2D-fill concern.)
- **Settled Shell guides resolve at true selected scale; persistent Wireframes also use true selected scale.**
  Adaptive coarsening is interaction-only. Cancel reveals the retained settled mesh and fingerprints the
  confirming authority echo so it cannot launch a delayed duplicate rebuild.
- **Exact voxel counting** (generated, not estimated) everywhere caps are enforced — correctness-first.
- **Cylinder/cone diagonal shells may run 2 cells thick** at odd orientations (centre-banded, not exact) —
  an accepted v1 trade; sphere/dome/box are exact.
- **Persistence writes on every mutation.** Public guides update the world-save blob; private guides atomically
  replace their client JSON file and retain one prior `.bak` generation.

**Safety limits that also bound cost**
- `GuideManager.HardVoxelCeiling = 10_000_000` rejects un-renderable giant guides **always** (create +
  rescale/fill/drag), even with caps disabled — without it a caps-off server could store invisible giants
  (the scan-guard sentinel) that silently max the world total.
- The **running world-voxel total is a `long`** (v0.1.27) so a caps-off server can't overflow it negative
  (which would read as "under budget" and disable cap checks).

**Mesh plan (full staged handoff in `dev/SESSION_14.md` §6–§7; Stage-A record in `dev/SESSION_16.md` §2):**

1. ~~Add an optimized **Volumetric exposed-face** builder~~ **— DONE (Stage A, v0.2.14–v0.2.16):** neighbour
   lookup, omit shared/interior faces, preserve role colours and private/public anchor shades; Surface/slab
   stays on the legacy path. Solidity-aware z-fight inset; deterministic mesh-count harness (15/15).
2. **Stage B spatial chunks — TRIED in v0.3.43, REVERTED in v0.3.49.** Region boundaries caused visible
   seams on immense curved guides despite correct cross-boundary occupancy.
3. **Stage C1 whole-guide distance/frustum culling — DONE (v0.3.41); per-region culling reverted.**
   **Stage C2 greedy merging — TRIED and REJECTED (v0.3.44–v0.3.48)** after fidelity and performance
   regressions. It is not the assumed next optimization.
4. ~~Temporary coarse interaction previews + async CPU builds~~ **DONE differently in v0.3:** adaptive
   wireframes, selected-scale cursor precision, generation-safe background refinement, streamed producer/
   consumer queues, organic neighbour growth, and retained placement/sculpt handoffs.

Future rendering work requires a new measured bottleneck and a design that preserves the v0.3.42 appearance
and real-play frame rate. Do not begin by raising `HardVoxelCeiling`.

---

## 10. Multiplayer / authority / concurrency

- **Public guides remain world-shared, no ownership.** Any player may edit or dispel any public guide; grief
  is a server-administration concern, deliberately out of scope.
- **Public authority is unchanged.** The server `GuideManager` is authoritative; broadcasts go to everyone
  (originator included) and rejects trigger a corrective resync.
- **Public geometry is claim-aware (v0.3.25).** The server checks the exact touched-block footprint with
  `BuildOrBreak`; Surface boundary cells protect both adjacent blocks. Existing forbidden overlaps may only
  shrink their denied set. `controlserver` bypasses; cleanup remains possible.
- **Private authority is client-local.** On a server without Layout it activates automatically. On a Layout
  server it exists only when `allowClientOnlyMode=true`; the default is false. Local guide files are keyed by
  world/server identifier + player UID and never enter the main world save.
- **Mixed mode:** `/layout private` and `/layout public` choose the destination of new guides. Existing-guide
  operations route by ownership, and Ctrl+Z/Y follows the most recently mutated authority. Public anchors
  are Blue/Indigo; private anchors are Orange/Burnt Orange only in the mixed environment.
- **Full-exclusivity edit locks** (`GuideLockManager`, first grab wins): while held, every mutation from
  anyone else is rejected. `adminCanOverrideLocks` (default true) lets `controlserver` admins override the
  **atomic** ops only (the stuck-lock remedy); geometry genuinely requires the lock.
- **Undo/redo** is per-authority and per-player: public history is server-side (bounded, default 50,
  session-only), and private history lives in the local authority. Both use validate-then-apply with stale
  command skip and a `Blocked` outcome for cap-rejected-but-valid commands. **One drag = one undo entry.**
  Ctrl+Z/Y follows the most recently mutated authority; mode or selection changes do not redirect it.
- **Admin/moderation commands:** the original `/layout dispel all|<chunk radius>` plus v0.3.22–v0.3.23
  `/layout jail|free|limit|voxelcap|info|jailroster|top`, v0.3.53 `/layout totalvoxelcap`, and targeted
  player/guide dispel. Policies persist
  per world and online changes refresh the affected client immediately.
- **Personal visibility:** `/layout off|on` (or `.layout off|on` in client-only fallback) disables/enables
  the entire render pass for only the invoking player and persists in `layout-client.json`. On login, chat
  explains a saved off-state. While off, guide state remains authoritative but Chalking Kit guide actions,
  settings, undo, and redo are blocked; the HUD and attempted-use warning explain `/layout on`.
- **Private commands (unchanged through v0.1.53):** `/layout private`, `/layout public`, `/layout client push all`, plus the
  client-only `.layout dispel all|<radius>`. Push assigns new public IDs, enforces server caps and
  privilege, removes only confirmed private copies, and is deliberately not undoable.

---

## 11. Configuration & limits (exact values)

**Server `layout.json`** (`LayoutServerConfig`; construction-time injection — edits need a server restart;
0/negative = unlimited, synced to clients on join):

| Key | Default |
|---|---|
| `configVersion` | 1 (narrow migration marker) |
| `perGuideVoxelCap` | 500,000 |
| `perPlayerTotalVoxelCap` | 1,000,000 (all current public guides attributed to one original creator) |
| `totalVoxelCap` | 0 (unlimited) |
| `maxGuidesPerPlayer` | 0 (unlimited) |
| `maxGuidesWorldWide` | 0 (unlimited) |
| `undoHistoryDepth` | 50 |
| `requiredPrivilege` | "" (everyone) |
| `adminCanOverrideLocks` | true |
| `allowClientOnlyMode` | false |
| `enableChalkDurability` | true |
| `allowHotbarChalkRefill` | false (ground-storage refill is always allowed; this opts in the hotbar shortcut) |
| `allowInventoryChalkRefill` | false (opts in cursor-onto-inventory-slot refill) |

**Per-player administrative overrides:** `/layout voxelcap <player> <number>` replaces the per-guide
default for the acting player; `/layout totalvoxelcap <player> <number>` replaces the cumulative allowance
for guides attributed to that original creator. A positive number persists in the world policy; `0` removes
it and restores the matching server default. Existing over-cap state is retained but cannot grow.

**Hard-coded limits (in code, not config):** `HardVoxelCeiling` 10M · legacy `MaxScanCells` 4M selects the
surface-only fallback for oversized Cylinder/Cone/Box rather than rejecting them (Tapered Cylinder already
uses surface-oriented generation) ·
`MaxDivisions` 256 · Polygon `MinSides` 3 / `MaxSides` 24 · Free-Shape `MaxCorners` 64 ·
`PreviewFullResVoxelCap` 8,000 (draft-ghost coarsening only) · valid voxel scales {1,2,4,8,16}.

**Client `layout-client.json`** (`LayoutClientConfig`): `forceClientOnly` preference (subject
to server policy), last scale / projection / 2D fill / 3D wireframe form / shape+constraint / divisions / sides, six role
opacities, and up to **four hard-kept pinned favorite shape codes**. Default scale 1. Since v0.2.22 it also
holds **`allowHotbarChalkRefill`** and **`allowInventoryChalkRefill`** (both default false; ground-storage
refill is always allowed and ungated) — player convenience toggles, moved here from the server config.
Mostly never synced, with one exception: the hotbar flag is **reported to the server on join** via
`ChalkRefillPrefsPacket`, because `ItemChalkingPowder`'s held-interact runs on both sides and the server is
what mutates the stacks — without it the toggle would be a no-op.
`guideRenderingEnabled` defaults true and is saved immediately by either public or client-only on/off command.
Sessions 28–29 added `shaderGuideBrightness` (0.78), `shaderAmbientResponse` (0.55), `voxelFrameStrength`
(0.25), `zFightInset` (**0.0006** — an OUTSET since v0.3.70, see §8), and `occupancyRecolour` (false).
All are tunable live and reachable from the **GUI settings page** behind a gear in the tool panel's title bar
(guide-opacity slider, built-voxel switch, re-read button), as well as by command.

⚠️ **Optional command arguments:** `parsers.OptionalFloat` / `OptionalInt` return their DEFAULT when the
argument is absent, **not null**, so an `args[0] is float` test always passes. This silently broke three
shipped commands — a bare `/layout inset` SET the inset to 0 and saved it. Every optional float now defaults
to `NaN` and goes through `LayoutModSystem.Supplied()`. Do not reintroduce the `is float` idiom.

**Private guide data** is separate from both config files and the world save:
`Layout/ClientOnlyGuides/<world-key>-<player-key>.json`, with atomic `.tmp` replacement, one `.bak`, and
`.corrupt-*` quarantine after successful backup recovery.

---

## 12. Status, open bug, and direction

**Confirmed & shipping (merged to `main` via PR #1):** the full 2D/3D catalog plus F4 place, preview,
reshape, fill, lock/unlock, divide, project, persist, and undo in public and private authority modes.
Vanilla-server fallback, reconnect, mixed public/private overlays, commands, push, ownership HUD/palettes, and
mixed-authority undo routing were playtested through the F4 arc and carried forward. Backup recovery is
code-complete; deliberate corruption fault injection has not separately been reported. Threshold-aware 3D
counting and the natural at-cap drag clamp are implemented and playtest-confirmed (the mid-draft cap clamp was
re-implemented in v0.2.19). Hollow Sphere/Dome shell scanning is exact-equivalent to the legacy predicate; the
full F5 chalk system and the Stage-A mesh pass are playtested through **v0.2.23**.

**B-S9-1 closed (v0.2.36):** ray-vs-rendered-voxel first-hit picking, full geometry fingerprints, complete
pre-drag snapshots, passive non-deforming markers, stale-marker cleanup, and curve-relative insertion order
remain. The last adjacent-lock residual was removed by assigning a point only its own nearest visible marker
cell; an adjacent first-hit body cell now receives a distinct passive marker. Human-confirmed in play.

**Direction / roadmap (see `dev/TODO.md` for detail):**
1. **Two verification debts from the (fully delivered) Session-16 backlog** — see `dev/SESSION_17.md` §8:
   (a) the hard 32-chalk ceiling is verified by an offline harness but **never tested against xskills
   itself**; (b) **1.22.x support is declared, not tested** — the code was built against 1.22.3, so nothing
   confirms every API used exists in 1.22.0.
2. **Protect the v0.3.42 renderer baseline restored in v0.3.49.** Spatial chunks and greedy merging were
   deliberately rejected — but read `SESSION_28.md` **with** `SESSION_26.md`: three of Session 26's claims,
   including the 140→80 FPS regression that closed the arc, were later disproved. `SESSION_26.md` carries a
   correction banner. The operative test for new renderer work is **"does it regroup primitives?"**
3. **Field-soak the v0.3.53 final release.** Cover persistent visibility across reconnect/restart,
   `/layout totalvoxelcap` during multiplayer mutation/undo/delete, and the off-state Chalking Kit lockout.
3b. **Block-occupancy verification (Session 29).** Four quick in-play checks remain: the read at voxel
   scales above 1 (exact at scale 1, centre-sampled above it, never looked at); client-only mode; that
   unloaded chunks reading as EMPTY does not leave returning players with wrongly-unbuilt guides; and how
   noisy `BlockChanged` really is in a built-up base. One known gap: above `OccupancyBatchVoxelCeiling`
   (3M voxels) the feature stops updating **silently**.
4. **Keep the broader multiplayer matrix as future regression coverage.** The v0.2.35 public/private pass
   succeeded and is not a release blocker.
5. **The queued feature set (`dev/TODO.md`).** **F6 Move, F7 mirror, F8 copy and F12 rotate are ALL
   DELIVERED (Sessions 30–31, v0.3.86–v0.4.0)** — the Transform category is complete. What remains:
   **F9** in-game settings panel (which subsumes T2's formatting/hover work), **F10** redraw the gear glyph,
   **F11** colour-blind-safe palette, and **T1** CTRL surface-snap on free-move (contact rule decided — the
   guide's lowest voxel plane meets the surface — but not built).
6. **If asked:** Roof / Tunnel volumes; concave-safe Free-Shape fill (fill is currently inert on Free-Shapes);
   an F3 re-constrain op; broadcasting the whole Free-Shape draft chain to other players.

**Settled decisions — do NOT reopen without the human explicitly asking** (full list in
`dev/ARCHITECTURE.md`'s Settled Decisions Register): the primitives+constraints model; absorb-or-break;
slave-regime soft flow; world-shared/no-ownership + full-exclusivity locks; server-authoritative;
voxels-never-stored; pinned append-only enums + JSON-save/protobuf-wire split; the verified draw recipe.

---

## 13. Known documentation / code gotchas (so an analyst isn't misled)

- **Guide colours have one source of truth:** `Systems/GuideMeshBuilder.cs`. Primary/apex is Green;
  public/fallback anchors are Blue/Indigo; mixed-server private anchors are Orange/Burnt Orange; "built"
  (occupancy) is Cyan. Ownership is render-only state from `ClientNetworkHandler`'s ID sets—never add it to
  `GuideData` just to color a mesh.
- **Those colour arrays are static and mutated in place.** `ConfigureOpacities` is documented as "called once
  at client start, before any mesh is built" — the v0.3.72 opacity slider and the occupancy toggle both break
  that assumption while background materialization batches are building. Transient and so far invisible, but
  it is a real hazard and `dev/TODO.md` **F11** would make it worse.
- **A colour cannot be changed in place after meshing.** Vertex welding keys on (x, y, z, **colour**), so a
  voxel changing colour beside one that does not requires the shared vertex to split — a topology change.
  This is why occupancy updates re-mesh a batch rather than re-uploading a colour buffer, even though
  `IRenderAPI.UpdateMesh` explicitly supports partial (non-null-only) updates. Note this does NOT mean
  welding is incompatible with multi-colour guides: guides already carry seven colours and weld to ~1.08
  vertices per quad; only vertices *on* a colour boundary duplicate.
- **`ToolMode` is client-only and never wired**, so its enum order is safe to change (unlike the on-wire
  `GuideShapeType` / `ShapeConstraint` / projection enums, which are pinned append-only).
- **`UndoManager` folder ≠ namespace:** it lives in `src/Systems/` but is `Layout.Systems.UndoManager` —
  the one file where folder and namespace diverge.
- **The Session docs are historical.** `SESSION_9`…`SESSION_29.md` are point-in-time narratives (SESSION_12
  covers F4 through v0.1.45; SESSION_13 covers v0.1.46–v0.1.52; SESSION_14 is the v0.1.53 mesh handoff;
  SESSION_15 is the v0.2.0–v0.2.9 Chalking Kit arc; SESSION_16 is the v0.2.10–v0.2.21 mesh + polish arc;
  SESSION_17 is the v0.2.22–v0.2.23 seven-item backlog; SESSION_18/19 cover the Tapered Cylinder and dust;
  SESSION_20 covers v0.2.36–v0.2.47 polygonal volumes/modifiers; SESSION_21 covers the v0.3.0–v0.3.8
  adaptive large-guide and persistent-wireframe arc; SESSION_22 covers the v0.3.9–v0.3.21 HUD,
  attribution, sculpting-parity, and projection-transition arc; SESSION_23 covers v0.3.22–v0.3.34
  moderation, claims, bounded immense validation, organic materialization, and render controls; SESSION_24
  covers v0.3.35–v0.3.40 materialization completion, clean-shell transitions, timing, polygonal scan
  optimization, and intrinsic HUD dimensions; SESSION_25 covers the v0.3.41–v0.3.43 spatial experiment;
  SESSION_26 records v0.3.44–v0.3.52, the renderer rollback, persistent visibility, cumulative creator caps,
  and the off-state tool lockout; SESSION_27 records v0.3.53's cumulative-cap override and final release;
  SESSION_28 records the v0.3.55–v0.3.69 rendering arc — welding, settled-shell streaming, the custom shader,
  voxel outlines; SESSION_29 records the v0.3.70–v0.3.85 block-occupancy arc; SESSION_30 records the
  v0.3.86–v0.3.89 F6 Move arc; SESSION_31 records the v0.3.90–v0.4.0 Transform arc — rotate, copy,
  mirror, the state-driven pad). For
  current state, trust `HANDOFF.md` / the code, not a mid-session checklist inside a
  session record — and note that `ARCHITECTURE.md` and `PROJECT_STATUS.md` now trail this file by two
  sessions.
- **`dev/BUILD_INSTRUCTIONS.txt`** is the original v0.1.0 first-build doc; its build/run steps are still
  valid but its file count (35) and version are historical — it now carries a header note saying so.
