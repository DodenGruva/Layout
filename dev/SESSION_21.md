# SESSION 21 — v0.3.0 → v0.3.8: adaptive large-guide drafting and structural wireframes

> This session replaced the large-3D-guide interaction bottleneck. The player’s non-negotiable requirement
> was smooth input: visual and numeric precision may arrive after a short settle, but drafting, grabbing, and
> cancelling must not freeze the client. Current checkpoint: **v0.3.8**, **DataVersion 11**, **protocol 11**,
> **74 C# source files**, **15 shape types / 21 picker tiles**.

## 1. Delivered iteration arc

- **v0.3.0 — motion-sensitive draft work.** Large 3D drafts stop regenerating a complete selected-scale shell
  on every aimed cell. While moving, expensive poses use a bounded structural wireframe; after a quiet delay,
  one generation-safe background task calculates the final shell. HUD dimensions/count show changing
  calculation glyphs instead of forcing synchronous measurement. Frame time is a secondary pressure signal,
  and stale worker results are discarded when the pose changes.
- **v0.3.1 — preserve small-guide quality.** Cheap 2D and 3D drafts retain their ordinary selected-scale
  shell. Wireframe/coarsening is an adaptive fallback, not a universal style. Hysteresis coarsens quickly
  under pressure and recovers gradually after healthy updates.
- **v0.3.2 — precision at the hand; materialization at rest.** If a moving wireframe must coarsen, voxels near
  the cursor remain at the selected scale for at least a four-voxel radius, then step outward through
  available scales over roughly two blocks. Settling no longer walks the visible 16→8→4→2→1 ladder: the
  selected-scale shell is calculated once off-thread and revealed in bounded, pseudo-random mesh batches,
  reading as materializing from the air.
- **v0.3.3 — cached placed-guide identity and safe giant grabs.** A placed guide’s human-readable name is its
  cached whole-block dimensions; its placed-hover dimension line is blank, so merely looking at a behemoth
  never re-voxelizes it. Live draft/grab dimensions remain meaningful. Grabbing a guide above the full-shell
  interaction budget immediately uses the lightweight wireframe while retaining the settled mesh for cancel.
  Cached dimensions/count/name became authoritative metadata (**DataVersion/protocol 10**).
- **v0.3.4–v0.3.5 — cancellation hardening.** Right-click cancel restores the retained settled mesh instantly
  rather than generating the original shell again. The later authoritative echo is fingerprinted and
  quarantined when it confirms the already-restored state, preventing the delayed expansion/rebuild that had
  driven client memory from roughly 7 GB toward 12 GB and forced Task Manager termination. Cancelled worker
  generations cannot reappear afterward.
- **v0.3.6 — more legible structural topology.** Volume wireframes gained a sensible number of longitudinal
  wires. A polygonal prism has exactly one vertical wire per polygon corner (six sides → six corner wires);
  round volumes use eight evenly spaced ribs.
- **v0.3.7 — persistent Shell/Wireframe guides and weighted placement sound.** For a 3D selection, the former
  Hollow/Filled positions become **Shell/Wireframe**. Wireframe is real guide state, not a transient renderer
  trick: it survives save/load, public/private authority, multiplayer sync, undo/redo, grabbing, rescaling,
  cap accounting, and default-setting persistence. Persistent wires render at the player’s selected voxel
  scale. `IsWireframe` advanced **DataVersion/protocol 10→11**. Placement sound now scales logarithmically
  from the familiar small-guide snap toward a louder, longer-ranged, lower-pitched snap for huge guides.
- **v0.3.8 — remove obsolete placement walls; dedicated icons.** Cylinder, Cone, and Box keep their established
  lattice/centre-band result while their legacy scan fits. If that cubic bounding scan would cross its old
  safety guard, they switch to a surface-proportional ring/face sampler instead of refusing a valid shell;
  normal voxel caps and the unconditional hard ceiling still apply. The contextual 3D tiles now use distinct
  icons: an enclosing faced skin for Shell and an open corner-strutted frame for Wireframe.

## 2. Current interaction contract

1. **Small/cheap drafts look normal.** They start and remain at the selected scale; no wireframe is shown
   merely because the shape is 3D.
2. **Motion gets bounded work.** An expensive moving pose uses its structural wireframe and adapts scale only
   as needed by observed work/frame pressure.
3. **The cursor stays precise.** The immediate cursor neighbourhood remains selected-scale even when the rest
   of the moving wireframe is coarser.
4. **Rest materializes precision.** After the settle delay, one current-generation background calculation
   builds the selected-scale result. The main thread uploads prebuilt batches at a bounded cadence.
5. **Numbers never outrank input.** Draft/grab measurements may show calculation glyphs until exact metadata
   is ready. Hovering a placed guide uses cached name/count and never regenerates geometry.
6. **Cancel is an instant visual rollback.** It reveals the retained settled mesh and invalidates transient
   work; a confirming network/local-authority echo must not trigger the same expensive rebuild again.
7. **Persistent Wireframe is exact.** It uses canonical shape topology at the selected scale and its own exact
   voxel count. Adaptive/coarse wireframes remain temporary interaction representations for Shell guides.

## 3. Architecture added this session

- `DraftPreviewSpec` is the immutable/deep-copied work description and generation key.
- `GuideRenderer` owns moving-preview policy, smoothed frame time, one background refinement at a time,
  generation-safe completion, cursor precision meshes, and randomized materialization queues.
- `ShapeWireframe` is the canonical topology→voxel path shared by persistent wires, counts, draft refinement,
  placed rendering, and cap checks.
- `LargeVolumeShellFallback` is the oversized Cylinder/Cone/Box surface-only fallback; its work scales with
  visible shell area instead of empty bounding volume.
- `GuideData` now stores cached count/dimensions/display name plus `IsWireframe`; DTOs mirror them additively.
- `GuideSetWireframePacket` and `SetWireframeCommand` provide multiplayer/local-authority and undo parity.
- The GUI’s 2D Hollow/Filled behavior is unchanged. Only volume shapes contextually relabel and redraw that
  pair as Form: Shell/Wireframe.

## 4. Playtest findings that drove the design

- The original full calculation path lagged during drafting but not after the mesh was already rendered,
  confirming CPU-side shape/guide work—not steady rendering—as the main interaction bottleneck.
- v0.3.0 produced a very large performance improvement; universal full-block/coarse presentation was judged
  unnecessarily clunky, leading to the cheap-guide and selected-scale restoration in v0.3.1.
- The precision-region/materialization behavior in v0.3.2 was described as intuitive and natural, and a
  “behemoth” guide playtest worked well.
- Hover measurement and cancel both exposed separate accidental regeneration paths. The first caused massive
  spikes; the second caused a delayed expanding shell plus unbounded-looking memory growth after the visual
  rollback. Both are now cache/retained-mesh paths rather than geometry rebuild paths.
- The persistent Shell/Wireframe build playtested well. v0.3.8 closes the remaining shape-local scan-guard
  mismatch and replaces the temporary borrowed Hollow/Filled artwork.

## 5. Deferred: streamed shell calculation pipeline

The potentially stronger long-term design discussed this session is deliberately **not implemented**.
Today, each shape’s `GetVoxelPositions` contract returns the complete shell before materialization begins;
the renderer progressively uploads already-calculated mesh batches. A future implementation could instead
use a cancellable producer/consumer pipeline: shape scans emit bounded spatial chunks, background meshing
consumes completed chunks with neighbour/halo information for correct exposed-face culling, and the main
thread uploads them under backpressure and an FPS budget. The cursor chunk could be prioritized and
unfinished work discarded as soon as movement resumes. Exact HUD dimensions/count would remain pending until
enumeration completes.

Do **not** implement that as one renderer upload per voxel or by rebuilding one ever-growing mesh every frame;
both would trade calculation latency for excessive upload/allocation cost. Revisit only if calculate-first
materialization still leaves noticeable delay or background CPU pressure on extreme guides.

## 6. Release/compatibility checkpoint

- Current package: `Layout0.3.8.zip`; Release build: **0 warnings / 0 errors**.
- Current data schema: **11** (`IsWireframe`; v10 cached display/count/dimensions; v9 flat-side alignment).
- Current protocol: **11** (appended persistent wireframe state/operation; registration remains append-only).
- Existing saves default to Shell (`IsWireframe=false`) and acquire cached metadata through normal migration.
- Public and private/local authority use the same guide-manager/count/render behavior.
- No commit or push was requested during this iteration arc; documentation records the local built/package
  checkpoint, not a claim that v0.3.8 is already published to `main`.
