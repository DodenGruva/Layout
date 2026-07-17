# SESSION 14 — v0.1.53: hollow-shell scaling and the mesh frontier

> Resume document for the next AI. Current branch: `ClientOnlyFallback`. Current build: **v0.1.53**,
> **DataVersion 8**, **protocol 3**, **66 C# source files**. The v0.1.53 source and this documentation are
> **not committed yet**. The last pushed commit is `1461c19` (`Optimize guide limits and harden lock
> interactions`). Do not merge or push `main` without explicit human direction.

## 1. What prompted this pass

With configurable voxel caps set to 0, the human tried to place a hollow half-dome at chisel resolution and
hit a diameter of only about 10 blocks. That was not the 10-million actual-voxel hard ceiling. Sphere and
Dome were scanning their entire cubic bounding box and had a separate `MaxScanCells = 4_000_000` guard.

At scale 1, a 10-block diameter is about 160 cells on each axis: `160³ = 4,096,000`. A hollow half-dome at
that size contains only roughly 40,000 visible guide voxels, but the old generator rejected it because of the
number of candidate cells it would inspect.

## 2. Implemented in v0.1.53

New file: `src/Shapes/SphericalShellScan.cs`.

Hollow Sphere and Dome now share an exact shell-focused scanner:

1. Walk the X/Y columns of the spherical bounding square.
2. Solve the outer and inner Z intersections analytically.
3. Visit only the narrow candidate bands around the two shell crossings.
4. Apply the original nearest-point/farthest-corner predicate to every candidate, preserving the exact voxel
   set and watertight one-cell shell.
5. For Dome, apply the same exact half-space clip as before, including arbitrary wall orientation and
   inversion.

The count path and the list-producing path share this scanner. Threshold counts still stop immediately after
the requested cap is exceeded. Hollow work now follows approximately surface area rather than bounding-cube
volume.

Filled Sphere and Dome deliberately retain their old cubic scan and 4-million-cell guard. Their output really
does grow cubically, so that path requires a separate design and was outside this iteration.

## 3. Validation completed

An isolated equivalence harness compared the new output cell-for-cell with the legacy brute-force predicate.
It covered:

- Sphere and Dome;
- voxel scales 1, 2, 4, 8, and 16;
- off-grid centres;
- normal and inverted domes;
- horizontal, X-facing, and Z-facing domes; and
- threshold counts below, at, and above the exact voxel count.

Every comparison had zero missing and zero extra cells. A 20-block hollow dome at scale 1—more than 32
million old candidate cells—generated **242,500 actual guide voxels** in about **5 ms** in the isolated
check. Release build: **0 warnings, 0 errors**.

Packaged test build:

- `C:\Users\Zech\Documents\ChatGPT\LayoutZips\Layout0.1.53.zip`
- 7 correct root entries; packaged DLL matches the Release DLL.
- Zip SHA-256: `92F94F140077C9F5581CB4AF59C8A9AB376BAF6C3BD8B7CD0D9A0615FD3421B5`

## 4. Human playtest result

The human reports v0.1.53 is a **good build**. They successfully placed a hollow Sphere roughly 100 blocks
wide. It was massive and, for the first time in their testing, caused visible lag. Earlier guides could not
reach a size that stressed their high-end GPU.

This confirms two things:

1. The hollow shell generator is no longer the limiting factor.
2. The next bottleneck is exactly the expected mesh representation and rendering path.

Do not respond by simply raising `HardVoxelCeiling`. A near-ceiling guide is already capable of producing an
enormous single GPU buffer and noticeable lag.

## 5. Why the current mesh scales badly

`GuideRenderer.RebuildGuide` generates the full true-scale `List<VoxelPosition>`, then
`GuideMeshBuilder.Build` emits an independent complete cube for every voxel:

- **8 vertices + 36 indices per voxel**;
- all six faces are emitted, including faces shared by adjacent voxels;
- one `MeshData` / one uploaded `MeshRef` represents the entire guide;
- every guide change deletes and rebuilds/uploads that whole buffer;
- the render loop draws every loaded guide mesh with no per-guide or per-chunk culling; and
- settled guides intentionally render at true voxel scale. The 8,000-voxel coarsening rule is draft-only.

At millions of voxels this creates tens of millions of duplicate vertices and hundreds of millions of
indices, plus a large synchronous allocation/upload on each rebuild. The lag is not surprising.

## 6. Recommended next implementation

Start with a **chunked exposed-face mesh**, then consider greedy merging. This provides value in safe stages
and preserves exact guide geometry.

### Stage A — exposed-face volumetric meshing

1. Build a coordinate lookup for the guide's voxels.
2. For each voxel, emit only faces whose neighbouring cell is absent. Never emit faces shared by adjacent
   cells.
3. Keep colour/role boundaries correct. Quads may merge only when render colour and face orientation match;
   Anchor, Locked, Primary, Division, Grabbed, and private/public anchor shades must remain distinct.
4. Keep the existing paper-thin Surface/slab path on the old builder initially. Optimize ordinary Volumetric
   cubes first—the confirmed 100-block Sphere case.
5. Add pure mesh tests for one voxel, two adjacent voxels, solid blocks, hollow shells, role-colour boundaries,
   hidden guides, and private/public anchor palettes.

This alone removes most interior faces and reduces the per-face representation from a full cube to quads.

### Stage B — spatial chunks

1. Partition guide voxels into fixed world-space chunks (a practical first trial is 16 or 32 blocks per
   chunk; measure before settling it).
2. Replace the renderer's one `{MeshRef, Origin}` per guide with a collection of chunk meshes, each with its
   own stable nearby origin.
3. Rebuild/upload chunks independently and dispose every replaced/deleted `MeshRef` reliably.
4. Cull chunks outside the view/distance range if the Vintage Story client API exposes a dependable frustum
   test. Distance culling is a simpler fallback.
5. Be careful at chunk borders: neighbour lookup must see voxels in adjacent chunks or shared boundary faces
   will be emitted twice.

Chunking limits peak allocation/upload size and creates the seam needed for culling and later incremental
rebuilds. It does increase draw calls, so choose chunk size by measurement.

### Stage C — greedy face merging

Within each chunk, merge coplanar adjacent exposed faces that share orientation and resolved RGBA colour.
Curved voxel shells will not collapse as dramatically as boxes, but their stair-step runs still offer useful
merging. Preserve double-sided guide rendering and the established colour/opacity language.

### Stage D — interaction and distant-view policy, only if still needed

- Huge-guide drags currently trigger repeated full true-scale rebuilds. After chunking, consider rebuilding
  only affected chunks or using a temporary coarse interaction preview, restoring the exact mesh on release.
- Do **not** silently reintroduce permanent coarsening for settled guides. The human previously rejected that
  behavior because it degraded chosen voxel resolution. Any distance LOD needs explicit approval and must
  return to exact resolution near the player.
- Background CPU construction may help, but GPU upload/disposal probably remains main-thread work. Use an
  immutable guide snapshot and generation token so stale async results cannot replace newer geometry.

## 7. Invariants the mesh work must preserve

- Guides remain visual-only and never touch blocks.
- Settled guides retain their chosen voxel scale.
- Public/fallback anchors remain Blue/Indigo; mixed-server private anchors remain Orange/Burnt Orange.
- Marker precedence and the single White grabbed highlight remain correct.
- Surface projection remains a paper-thin air-side slab with anti-z-fight behavior.
- Hidden guides render only their dim anchors.
- Mesh origins remain camera-independent and near their geometry for float precision.
- Every upload replacement, guide deletion, bulk sync, and renderer disposal deletes all owned GPU meshes.
- No persistence or wire change should be necessary for mesh optimization; keep DataVersion 8 / protocol 3.

## 8. Resume checklist

1. Read `CLAUDE.md`, `HANDOFF.md`, this file, and the performance sections of `ARCHITECTURE.md` / `TODO.md`.
2. Confirm the working tree contains only the intended v0.1.53 source and documentation changes.
3. Do not overwrite `Layout0.1.53.zip`; every code revision needs a new version and zip in `LayoutZips`.
4. Implement Stage A behind a clean builder seam so the legacy Surface/slab path remains available.
5. Add deterministic mesh-count tests before in-game testing.
6. Build Release with zero warnings/errors, bump the version, and package a new zip.
7. Let the human test a normal guide first, then the 20/40/roughly-100-block hollow Sphere/Dome progression.

B-S9-1 remains a separate active follow-up: lock placement no longer deforms the guide and v0.1.52 improved
marker lifecycle/order, but repeated lock/drag/revert/unlock testing was not declared finished. Do not conflate
that interaction work with the mesh pass.
