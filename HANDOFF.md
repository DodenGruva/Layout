# Layout — Project Handoff & Analysis Brief

> **Purpose.** A single, self-contained, current-state briefing for anyone (human or AI) picking this project
> up cold — especially for **performance / optimization analysis**. It consolidates scope, status, direction,
> and the performance-relevant mechanics. Updated 2026-07-14 against **v0.1.52** on
> `ClientOnlyFallback`. Where this file and the
> code disagree, **the code wins** — treat this as a map, then read the `.cs` files it points at.
>
> **Deeper docs:** `dev/ARCHITECTURE.md` (the authoritative plan + Settled Decisions Register),
> `dev/PROJECT_STATUS.md` (status), `dev/TODO.md` (punch-list), `dev/SESSION_9/10/11/12/13.md` (per-session
> history), `dev/PLAN_CLIENT_ONLY.md` (F4 implementation record), `CLAUDE.md` (working conventions).

---

## 1. TL;DR

**Layout** is a mod for **Vintage Story 1.22.3** (C# / **.NET 10**). It is a CAD-like, voxel-resolution
**construction-planning** tool: players place translucent geometric guide overlays in the world and build
against them by hand. **The mod is visual-only — it never places, removes, or modifies blocks.** Public
guides are server-authoritative/world-shared; ClientOnlyFallback also provides private client-authoritative
guides on servers without Layout and, when server policy permits, alongside public guides.

- **Status:** v0.1.52, **playtested in multiplayer and vanilla-server fallback**. F4 is feature-complete on
  `ClientOnlyFallback`; the cap-performance and lock/drag correctness pass is under final playtest. `main`
  remains at v0.1.27.
- **Size:** **65 source files** (`src/`), ~one asset tree, one `.csproj`.
- **Data schema:** **DataVersion 8** (additive passive-lock-marker flag; pinned enums/default migration).
- **Wire protocol:** **3** (append-only lock-marker field after the protocol-2 F4 additions).
- **Catalog:** **12 shape types**, shown as **18 picker tiles** — a full 2D family plus a **3D volume family**.
- **Active follow-up:** B-S9-1 is substantially improved; lock placement no longer deforms the guide, but
  repeated lock/drag/revert/unlock behavior still needs broader playtesting before closure.
- **Design philosophy (standing rule): correctness over performance** unless told otherwise. Several
  deliberate un-optimized paths exist by choice; see §9.

---

## 2. Build & run

- **Build:** `dotnet build Layout.csproj` from the **repo root** (the folder containing `Layout.csproj`).
- **Target:** `net10.0`, Vintage Story 1.22.3. References resolve via the `<VintagestoryDir>` csproj
  property (default `C:\Users\Zech\AppData\Roaming\Vintagestory`) — change that one line for another install.
- **Dependencies** (all ship with the game): `VintagestoryAPI.dll` (install root),
  `Newtonsoft.Json.dll`, `protobuf-net.dll`, `cairo-sharp.dll` (GUI icon glyphs) — the last three live in
  `Lib\`. None are bundled into the mod zip.
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
├── src/                        ← all 65 .cs files (see §6)
└── dev/                        ← ALL PROSE DOCS live here (NOT the code)
    ├── ARCHITECTURE.md  PROJECT_STATUS.md  TODO.md
    ├── SESSION_9.md  SESSION_10.md  SESSION_11.md  SESSION_12.md  SESSION_13.md
    ├── PLAN_CLIENT_ONLY.md  BUILD_INSTRUCTIONS.txt
```

**Gotcha for tooling:** the docs are in `dev/`; the code is one level up in `src/`. A glob rooted at `dev/`
will not see the source. The mod project was flattened to the repo root on 2026-07-06 (it used to be nested
in a versioned subfolder).

---

## 4. What the mod does (mechanics & scope)

- **The tool** is normally the held Layout item (borrows the vanilla abacus art; recipe 6 sticks + 3
  any-metal nuggets; infinite durability). On a server without Layout, the equivalent gate is **Flax Twine
  main-hand + any vanilla Hammer variant off-hand** (damage irrelevant). Interaction is entirely
  **first-person clicks + crosshair raycast** — no transform
  gizmos. Guides are **visible but untargetable when the tool is not held** (pure mesh draws, no
  selection/collision/entity backing), so they never interfere with the blocks underneath.
- **Three tool modes** (`ToolMode`, client-only, never wired): **Create** owns ALL geometry (place, grab &
  reshape, insert, lock; right-click = cancel / lock-in-place). **Edit** is settings-only: left-click
  **selects** a guide and the GUI's setting rows then act on THAT guide (no reshaping). **Delete** dispels.
- **Placement** is **two clicks for most shapes**, with deliberately-reopened exceptions: free/right/
  isosceles triangles and the 3D cylinder/cone/box take **three clicks** (base + a height click); the
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
- **Held keys:** **CTRL** = level+cardinal snap; **SHIFT** = draft-invert (while placing) or **spring the
  placed guide back to its as-placed form** (while idle over a guide). All hotkeys are rebindable and inert
  unless the tool is held (Ctrl+Z/Y never hijack other UIs).
- **Color language (authoritative table is in `GuideMeshBuilder.cs`):** yellow body · red locked · **green**
  apex/primary · public/fallback **blue** anchors (indigo off-shade) · mixed-server private **orange**
  anchors (burnt-orange off-shade) · white grabbed · magenta division marks · hidden guides = anchors only
  at low alpha. Anchor opacity applies to both ownership palettes; other role alphas remain configurable.

---

## 5. The shape catalog (12 types / 18 tiles)

Built on a **primitives + constraint-modifiers** model — constrained variants are **not** separate types,
and fill is **not** a type. `enum GuideShapeType { Arch=0, Ellipse=1, Line=2, Triangle=3, Rectangle=4,
Polygon=5, FreeShape=6, Sphere=7, Dome=8, Cylinder=9, Cone=10, Box=11 }`.
`GuideShapeTypes.IsVolume(t)` classifies the 3D family (an explicit switch, never inferred from ordering).

**2D section (13 tiles):** Arch · Half-circle (Arch+SemiCircle) · Circle (Ellipse+Circle) · Ellipse · Line ·
Triangle · Right · Equilateral · Isosceles (Triangle + constraint) · Rectangle · Square (Rectangle+Square) ·
Polygon (regular N-gon, 3–24 sides, count in `GuideData.Sides`) · Free-Shape (irregular polyline, `IsClosed`).

**3D volume section (5 tiles):** Sphere · Dome · Cylinder · Cone · Box. Hollow = a one-cell shell, Filled =
the solid. **Always Volumetric** (Surface + Divisions gated off, server-side and in the GUI). Voxelised by a
**cell-lattice scan** (not curve-marching): exact surface-crossing (sphere/box/dome) or centre-banded
(cylinder/cone). Deterministic up-axis (`ShapeGeometry.BaseNormal`); SHIFT inverts (e.g. dome→bowl).
Targeting is a **wireframe** — the anchors and the height handle are the reliable grab points.

Adding a shape starts in `Shapes/ShapeFactory.cs` (the single construction point) + a new `IGuideShape`.

---

## 6. Architecture — layered module map

Pure, dependency-light layers under a server-authoritative core. Namespaces match folders (the one exception:
`UndoManager` lives in `src/Systems/` as `Layout.Systems.UndoManager`).

| Folder | Namespace | Role |
|---|---|---|
| `src/` | `Layout` | `LayoutModSystem` — composition root (registers systems, item, channels, keybinds, HUD). |
| `Guide/` | `Layout.Guide` | Pure data: `GuideData`, `ControlPoint`, `VoxelPosition`, the pinned enums, projection/render settings. Depends only on `Vec3d`. |
| `Shapes/` | `Layout.Shapes` | Pure geometry math (no engine deps beyond `Vec3d`). `IGuideShape` seam; `ShapeFactory`; the 12 shape classes; `CatmullRomSpline`; `VoxelMarch` (the one cell-quantise convention); `ShapeGeometry`; `SoftPointFlow`; `DivisionMarks`. |
| `Systems/` | `Layout.Systems` | Side-neutral `GuideManager` (authority + JSON persistence + cap validation), persistence/block-probe seams, `GuideLockManager`, `DraftManager`, `UndoManager`, `GuideRenderer`, `GuideMeshBuilder`. |
| `Network/` | `Layout.Network` | `PacketTypes` (protobuf DTOs, fixed append-only registration), `ServerNetworkHandler`, `ClientNetworkHandler`. |
| `UI/` | `Layout.UI` | `GuideToolGui` (the F-menu icon-tile GUI), `LayoutToolIcons` (Cairo glyphs), `GuideHud`. |
| `Config/` | `Layout.Config` | `LayoutServerConfig` (`layout.json`), `LayoutClientConfig` (`layout-client.json`). |
| `Items/` | `Layout.Items` | `ItemGuideTool` — stateless glue; routes clicks to the controller. |
| `Client/` | `Layout.Client` | `GuideToolController` plus `LocalGuideAuthority`, authority mode, vanilla-item gate, and per-world/per-UID private persistence. |
| `Undo/` + `Undo/Commands/` | `Layout.Undo[.Commands]` | `IGuideCommand`, `UndoStack`, and 14 command types (Create/Delete/Move/Insert/Lock/RemoveLockMarker/Rescale/Hide/SetProjection/SetFilled/SetDivisions/SetSides/SpringBack/BreakConstraint). |

**Data-flow (networked mode):** client `GuideToolController`/GUI/HUD → `ClientNetworkHandler.Send*` →
protobuf packet → `ServerNetworkHandler` → `GuideManager` validates + persists + records undo → **broadcasts
full/atomic state to everyone (originator included)** → `ClientNetworkHandler` applies to the local mirror →
raises change events → `GuideRenderer` rebuilds that guide's mesh. Rejections send a corrective full-state
resync. Systems communicate by **return value (`GuideOperationResult`), not events**.

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
  `DataVersion` (8: `ControlPoint.IsLockMarker`; 7: `IsClosed`; 6: `Sides` + the never-wired as-placed spring-back snapshot; 5: `Divisions`;
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

Key struct: `VoxelPosition` is a `readonly struct : IEquatable<VoxelPosition>` (hashes all of X/Y/Z/Type),
generated in large quantities and de-duplicated through a `HashSet` — deliberately boxing-free in that hot
path.

---

## 8. Rendering pipeline

- **One compiled mesh per guide, rebuilt only on change** (`GuideRenderer` listens for mirror-apply change
  events). `GuideMeshBuilder` is a stateless `List<VoxelPosition>` → `MeshData` converter.
- **The verified draw recipe** (each element was a real playtest bug): Opaque stage + **manual blend** (not
  OIT); `PreparedStandardShader` forced full-bright; a **real white 2×2 texture** (texture id 0 samples
  garbage); the full **pos + uv + rgba** vertex layout with uv (0,0); color carried purely by packed vertex
  RGBA. Cube path = 8 verts / 36 indices per voxel; Surface path = one quad or a paper-thin slab per voxel.
- **Anti-z-fight:** volumetric meshes get a per-frame 0.003-block camera-relative nudge; Surface guides
  render as **0.0025-block slabs** hugging the **air-side** cell face (world-solidity probe, majority
  fallback) with a plane-axis-only inset. The air-side probe tolerates the world-load race (a re-probe tick
  rebuilds once unloaded chunks arrive — fix for B-S10-1).
- **Marker voxels** are single-voxel nearest-claim (precedence Locked > Primary > Anchor > Division); the
  apex and off-cell division boundaries claim 2 voxels on even spans so they read centered.

---

## 9. Performance characteristics & deliberate trade-offs

**This is the section for an optimization pass.** Standing rule: **correctness over performance** — several
paths below are un-optimized *on purpose*, and the human validates by playing, not profiling. Confirm any
"slow" claim in real play before optimizing; the biggest guides in real use are modest.

**Hot paths & large-quantity structures**
- **Voxel generation per guide** — up to the per-guide cap (25,000) `VoxelPosition`s, de-duplicated via
  `HashSet`. Regenerated whenever the guide changes.
- **Mesh rebuild** — a full `MeshData` rebuild for a guide on every change event (8 verts + 36 indices per
  voxel on the cube path). Settled guides mesh at **true scale, never coarsened** (see below).
- **3D volume scan** — a cell-lattice scan is **O(cells³)** in the bounding box. Guarded by
  `MaxScanCells = 4_000_000` per shape: over the guard, `GetVoxelCount` returns a huge sentinel (so the cap
  rejects instantly and the ghost coarsens) and `GetVoxelPositions` returns empty. The two disagree only in
  a regime the cap makes unreachable.
- **Targeting** samples each guide's curve (`IGuideShape.SampleCurve`) into a polyline **cached behind a
  content fingerprint** (point count + coordinate sum + constraint) — resampled only on change, not per tick.
- **Division recolor** — `DivisionMarks.Apply` runs on **every mesh rebuild** (draft ghost + placed),
  walking `SampleCurve(128)` for arc length then a nearest-cell claim per boundary. Cheap, but it walks the
  cell list; watch on very high division counts × large guides.
- **Drag** — live moves preview locally and send at **~10 Hz** (throttled); the server composes one
  authoritative batch (grabbed edit + its soft-flow reflow), cap-checks once, broadcasts once.

**Deliberate, documented trade-offs (do not "fix" without checking intent)**
- **Filled guides recount voxels exactly per drag update** (cells generated each move packet) — no per-drag
  count cache. If large filled discs drag sluggishly, a count cache is the sanctioned fix.
- **Settled guides always mesh at full resolution.** `ChooseRenderScale` coarsening (an 8,000-voxel courtesy,
  `PreviewFullResVoxelCap`) is **draft-ghost-only** — it once leaked into placed guides and permanently
  degraded them, so it is deliberately confined. Huge settled guides pay their real rebuild cost.
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

**The known future perf frontier:** the human wants **enormous fine-detail guides** (grand domes, etc.).
That needs the scan guard / hard ceiling **raised**, and then a real rendering perf pass — **chunked meshes /
LOD** — because millions of cubes per guide will not mesh acceptably as one buffer, and per-guide counts would
exceed `int`. This is the single most likely place a performance contribution is wanted. Parked until asked.

---

## 10. Multiplayer / authority / concurrency

- **Public guides remain world-shared, no ownership.** Any player may edit or dispel any public guide; grief
  is a server-administration concern, deliberately out of scope.
- **Public authority is unchanged.** The server `GuideManager` is authoritative; broadcasts go to everyone
  (originator included) and rejects trigger a corrective resync.
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
- **Admin commands (v0.1.26–0.1.27):** `/layout dispel all` and `/layout dispel <chunk radius>`
  (`controlserver`; namespaced under `/layout` so they can't clash with other mods).
- **Private commands (unchanged through v0.1.52):** `/layout private`, `/layout public`, `/layout client push all`, plus the
  client-only `.layout client dispel all|<radius>`. Push assigns new public IDs, enforces server caps and
  privilege, removes only confirmed private copies, and is deliberately not undoable.

---

## 11. Configuration & limits (exact values)

**Server `layout.json`** (`LayoutServerConfig`; construction-time injection — edits need a server restart;
0/negative = unlimited, synced to clients on join):

| Key | Default |
|---|---|
| `perGuideVoxelCap` | 25,000 |
| `totalVoxelCap` | 250,000 |
| `maxGuidesPerPlayer` | 0 (unlimited) |
| `maxGuidesWorldWide` | 0 (unlimited) |
| `undoHistoryDepth` | 50 |
| `requiredPrivilege` | "" (everyone) |
| `adminCanOverrideLocks` | true |
| `allowClientOnlyMode` | false |

**Hard-coded limits (in code, not config):** `HardVoxelCeiling` 10M · `MaxScanCells` 4M (per 3D shape) ·
`MaxDivisions` 256 · Polygon `MinSides` 3 / `MaxSides` 24 · Free-Shape `MaxCorners` 64 ·
`PreviewFullResVoxelCap` 8,000 (draft-ghost coarsening only) · valid voxel scales {1,2,4,8,16}.

**Client `layout-client.json`** (`LayoutClientConfig`; never synced): `forceClientOnly` preference (subject
to server policy), last scale / projection / fill / shape+constraint / divisions / sides, six role
opacities, and up to **four hard-kept pinned favorite shape codes**. Default scale 1.

**Private guide data** is separate from both config files and the world save:
`Layout/ClientOnlyGuides/<world-key>-<player-key>.json`, with atomic `.tmp` replacement, one `.bak`, and
`.corrupt-*` quarantine after successful backup recovery.

---

## 12. Status, open bug, and direction

**Confirmed & shipping on `ClientOnlyFallback`:** the full 2D/3D catalog plus F4 place, preview, reshape,
fill, lock/unlock, divide, project, persist, and undo in public and private authority modes. Vanilla-server
fallback, reconnect, mixed public/private overlays, commands, push, ownership HUD/palettes, and
mixed-authority undo routing were playtested through **v0.1.52**. Backup recovery is code-complete; deliberate
corruption fault injection has not separately been reported. Threshold-aware 3D counting and the natural
at-cap drag clamp are implemented and playtest-confirmed.

**Active follow-up — B-S9-1 (lock-in-place):** ray-vs-rendered-voxel first-hit picking is now implemented,
curve target caches use a full geometry fingerprint, cancel restores a complete pre-drag snapshot, and passive
lock markers no longer deform an Arch when placed. v0.1.52 also removes stale unlocked markers and orders new
lock/grab points along the curve. The human confirms that lock placement no longer shifts and that the latest
iteration is better; repeated lock → drag → revert/cancel → unlock cycles still need wider testing before the
bug is declared closed.

**Direction / roadmap (see `dev/TODO.md` for detail):**
1. **Finish the B-S9-1 interaction regression** against the v0.1.52 marker lifecycle/order changes.
2. **Final F4 regression and release packaging → v0.2.0 candidate.** Test vanilla fallback, policy denial,
   permitted mixed mode, push/reconnect, and ordinary public multiplayer.
3. **Enormous fine-detail guides** — the perf frontier in §9 (raise the guards + chunked-mesh/LOD pass).
4. **If asked:** Roof / Tunnel volumes; concave-safe Free-Shape fill (fill is currently inert on Free-Shapes);
   an F3 re-constrain op; broadcasting the whole Free-Shape draft chain to other players.

**Settled decisions — do NOT reopen without the human explicitly asking** (full list in
`dev/ARCHITECTURE.md`'s Settled Decisions Register): the primitives+constraints model; absorb-or-break;
slave-regime soft flow; world-shared/no-ownership + full-exclusivity locks; server-authoritative;
voxels-never-stored; pinned append-only enums + JSON-save/protobuf-wire split; the verified draw recipe.

---

## 13. Known documentation / code gotchas (so an analyst isn't misled)

- **Guide colours have one source of truth:** `Systems/GuideMeshBuilder.cs`. Primary/apex is Green;
  public/fallback anchors are Blue/Indigo; mixed-server private anchors are Orange/Burnt Orange. Ownership is
  render-only state from `ClientNetworkHandler`'s ID sets—never add it to `GuideData` just to color a mesh.
- **`ToolMode` is client-only and never wired**, so its enum order is safe to change (unlike the on-wire
  `GuideShapeType` / `ShapeConstraint` / projection enums, which are pinned append-only).
- **`UndoManager` folder ≠ namespace:** it lives in `src/Systems/` but is `Layout.Systems.UndoManager` —
  the one file where folder and namespace diverge.
- **The Session docs are historical.** `SESSION_9/10/11/12/13.md` are point-in-time narratives (SESSION_12
  covers F4 through v0.1.45; SESSION_13 covers v0.1.46–v0.1.52). For current state, trust
  `HANDOFF.md` / `ARCHITECTURE.md` / the code, not a mid-session
  checklist inside a session record.
- **`dev/BUILD_INSTRUCTIONS.txt`** is the original v0.1.0 first-build doc; its build/run steps are still
  valid but its file count (35) and version are historical — it now carries a header note saying so.
