# SESSION 24 — v0.3.35–v0.3.40: materialization completion, shell transitions, and intrinsic HUD dimensions

**Checkpoint:** Layout v0.3.40 is built and packaged for playtesting. DataVersion remains **12**, protocol
remains **16**, the catalog remains **15 shape types / 21 picker tiles**, and the source tree remains
**77 files**. This session refined the streamed immense-guide pipeline delivered in Session 23; it introduced
no persisted fields, packet types, or enum values.

---

## 1. What this session delivered

The organic materialization path now has a complete visual contract rather than ending at its splotchy
growth mesh:

1. Generate the exact selected-scale occupancy off-thread.
2. Keep the temporary wireframe/scaffold until the first organic batch is uploaded.
3. Stream deterministic multi-site neighbour growth across the complete shape.
4. Leave the fully grown organic shell visible for **200 ms**.
5. Swap atomically to a separately generated, clean and uniform final shell.
6. Emit placement particles and sound only after both final placement authority and the clean-shell swap.

That same chain now covers an already-placed guide changed from **Wireframe** to **Shell** in Edit mode.
This removes the synchronous full-shell build that previously caused a visible lag spike.

The session also fixed the retained immense-operation latch when a sculpt is resized below the 8,000-voxel
threshold, removed unusually long straight/tapered polygonal-prism generation delays, accelerated small-guide
materialization, and corrected the HUD width/height shown for round and polygonal 3D volumes.

---

## 2. Materialization behavior

### Nucleation

The number of growth origins is now:

`min(voxel count, 6 + ceil(voxel count / 500))`

The six-site base prevents small shells from spending too long expanding from too few points. The
voxel-proportional term gives immense guides more sites as their surface grows. Deterministic spatial buckets
select well-separated sites without the former candidate-by-candidate distance scan becoming its own large
cost.

### Timing

The normal upload interval scales with exact voxel count:

- about **18 ms** for a small materialized guide;
- increasing smoothly with size;
- **45 ms** by 120,000 voxels and above;
- still multiplied under frame pressure.

Growth remains tied to actual batch readiness: it cannot visually outrun generation, and the final clean
shell cannot appear until every organic batch has been accepted. The separate 200 ms completion hold lets
the player see the fully covered growth before its texture becomes uniform.

### Scaffolds and effects

The wireframe is a calculation/authority scaffold, not part of the growth effect. It disappears immediately
before the first organic batch becomes visible.

A draft is allowed to finish growing and reach its clean preview before its final placement click. In that
case, particle/sound feedback remains pending. Effects run only when these two conditions are both true:

- the guide has received final placement authority; and
- its clean shell has replaced the completed growth mesh.

The server no longer emits an immediate whole-guide effect for an immense create; the client that owns the
retained visual emits it at the correct completion point.

---

## 3. Clean-shell ownership and cancellation

Organic growth meshes and clean final meshes are distinct lockstep sets. The clean mesh is prepared while
hidden, so the final swap does not synchronously rebuild the entire guide on the render thread.

Draft placement, pending-authority placement, immense sculpt replacement, and settled Wireframe→Shell
transitions use generation IDs/fingerprints and cancellation. A superseded worker cannot publish an old
shape over a newer pose or form. Old GPU meshes continue to retire incrementally across frames.

For Wireframe→Shell Edit:

- the existing wireframe remains visible while exact shell calculation begins;
- the wireframe hides at the first organic upload;
- growth completes and holds for 200 ms;
- the clean shell swaps in;
- changing the guide again cancels/quarantines the obsolete transition.

For a formerly immense sculpt that is released after being resized below the immense threshold, the ordinary
settled rebuild now explicitly completes the retained transaction. This clears the operation gate instead of
leaving future guide actions stuck behind “Wait for the immense guide to finish.”

---

## 4. Polygonal-prism generation

The long pause before materialization on **Polygonal Prism** and **Tapered Polygonal Prism** came from repeated
side-distance/trigonometric work across their full bounding boxes, especially the large empty corners of
tapered candidates.

The shared polygonal scanner now:

- precomputes polygon side normals;
- derives safe inner and outer annulus limits;
- rejects cells definitely away from the shell before detailed edge tests;
- retains the exact existing shell predicate at the boundary.

The optimization changes calculation cost, not visible geometry or count semantics.

---

## 5. Intrinsic HUD dimensions

The old measurement path derived “width” from the diagonal of a world-axis XZ bounding box. For circular
footprints this made a correct 83-block dome appear roughly `83 × √2 = 117` blocks wide. Tapered cylinders
and other rotated/radial volumes had the same conceptual error.

`IIntrinsicGuideExtent` now lets volume shapes report the dimensions players used to define them:

- Sphere: diameter × diameter
- Dome: diameter × radius
- Cylinder and Cone: diameter × axial height
- Tapered Cylinder: widest diameter × axial height
- Straight/Tapered Polygonal Prism: widest defining diameter × axial height

`GuideMeshBuilder.MeasureShapeExtent` prefers those intrinsic values and retains the bounds-based fallback
for shapes that do not implement the interface. Draft, grabbed, placed, and cached server metadata now use
the same measurement path. Existing guides are recalculated through the normal load/count cache refresh; no
save migration is required.

---

## 6. Compatibility and files

No compatibility surface changed:

- **DataVersion:** 12
- **Protocol:** 16
- **Source files:** 77
- **Catalog:** 15 shapes / 21 tiles

Principal implementation files:

- `src/Systems/GuideRenderer.cs` — clean-shell staging/swap, effect gate, form-transition pipeline, timing,
  cancellation, and retained-operation completion.
- `src/Shapes/ProgressiveVoxelGeneration.cs` — base-plus-proportional nucleation and spatial seed selection.
- `src/Shapes/PolygonalPrismShape.cs` — optimized straight/tapered shell scan.
- `src/Shapes/IGuideShape.cs` and the six volume shape files — intrinsic extent contract.
- `src/Systems/GuideMeshBuilder.cs` — shared intrinsic HUD/cache measurement.
- client/server network handlers and the tool controller — final authority/effect and live measurement
  coordination.

---

## 7. Verification and package

- Release build: **succeeded with 0 warnings and 0 errors**.
- Package: `..\LayoutZips\Layout0.3.40.zip`
- Zip entries: **43**
- Packaged `modinfo.json`: **0.3.40**
- SHA-256: `0417373C0FC79CA7F223638DB2A7ED17C603AF7BA549B322F066B2A227910C62`

The final judge remains in-game playtesting. Focus on:

1. Small dome/sphere materialization speed and coverage.
2. Immense growth reaching every cell, holding for 200 ms, then becoming uniformly clean.
3. No wireframe visible beneath active growth.
4. No placement particles before the final click, even if the clean preview is already ready.
5. Wireframe→Shell Edit transitions without a synchronous lag spike.
6. Repeated immense→small sculpt releases without the operation gate sticking.
7. Straight and tapered polygonal prisms beginning materialization promptly.
8. Correct HUD dimensions for every 3D family and every plane/orientation.

---

## 8. Resume point

Start from **v0.3.40** and this file. The intended final visual sequence is:

`scaffold → organic exact growth → full-growth 200 ms hold → clean uniform shell → placement effect`

The placement effect additionally requires final placement authority; the sequence may stop at the clean
preview indefinitely while the player remains mid-draft. Revisit the constants only from playtest feel.
Stage B/C remains deferred unless already-settled, in-range rendering is measured as the next bottleneck.
