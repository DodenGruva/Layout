# SESSION 25 — v0.3.41–v0.3.43: measured guide culling and spatial settled meshes

**Checkpoint:** Layout v0.3.43 is built, packaged, playtested, and human-approved. DataVersion remains
**12**, protocol remains **16**, and the source tree remains **77 C# files**. Package:
`..\LayoutZips\Layout0.3.43.zip`.

## 1. Why this pass happened

An immense settled guide continued submitting all of its mesh batches whenever any part of the guide was
visible. The player specifically reported unnecessary GPU demand from guides outside the view, so the work
was staged around measurements:

1. reject complete guides outside the camera;
2. expose last-frame render statistics;
3. use those measurements to decide whether spatial subdivision was warranted.

The measurements showed that whole-guide culling worked, but a partially visible immense guide still
submitted its complete mesh. That justified the spatial pass.

## 2. Revision record

### v0.3.41 — whole-guide frustum culling

- The existing live-view-distance test was combined with Vintage Story's current default frustum culler.
- Conservative world-space bounding spheres cover placed guides, the active draft, retained pending
  placement visuals, and remote draft markers.
- A fully off-screen guide now submits no mesh. Missing transitional bounds deliberately fail open so a
  handoff cannot make geometry disappear.

### v0.3.42 — local render measurements

- `.layout renderstats` reports the last frame's visible/culled placed-guide counts, mesh batches, approximate
  submitted triangles, draft/pending batches, remote markers, and smoothed frame time.
- All `GuideRenderer` uploads, draws, and deletes pass through tracked helpers so transferred materialization
  meshes retain accurate accounting without changing ownership or disposal behavior.

### v0.3.43 — 32-block spatial final meshes

- Final clean materialization geometry for immense guides is partitioned into fixed **32×32×32-block**
  world regions. A dense region may retain multiple GPU batches for safe upload size; every batch in that
  region shares its conservative world-space culling bound.
- Small guides retain the original immediate single-mesh path. Organic growth previews remain unchanged.
  Spatial subdivision applies only to the clean final geometry that becomes the settled guide.
- Each regional mesh is independently distance/frustum tested after the whole-guide early rejection.
- The complete guide occupancy set is passed into every regional mesh build. Adjacent voxels on opposite
  sides of a region boundary therefore still suppress their shared faces; partitioning introduces neither
  an internal face nor a visual seam.
- Negative voxel coordinates use mathematical floor division, so region ownership is stable on both sides
  of world zero.
- Existing immense saved Shells now load as a cheap temporary wire scaffold and rebuild their spatial final
  meshes through the existing below-normal materialization lane. They no longer fall back to one synchronous
  monolithic mesh on reload.
- `.layout renderstats` now also reports spatial batches culled inside an otherwise visible guide.

## 3. Measured result

The same immense guide and viewing positions were used throughout.

| Build / position | Drawn mesh batches | Approx. submitted triangles | Spatial batches culled | Smoothed frame |
|---|---:|---:|---:|---:|
| v0.3.42 fully visible | 128 | 32,621,568 | — | 8.2 ms |
| v0.3.42 partly visible | 128 | 32,621,568 | — | 7.5 ms |
| v0.3.42 completely off-screen | 0 | 0 | — | 4.3 ms |
| v0.3.43 partly visible | 33 | about 7.5 million | 110 | 4.4 ms |

For the partially visible case, spatial culling reduced drawn batches by about **74%**, submitted triangles
by about **77%**, and measured smoothed frame time by about **41%** relative to the v0.3.42 partial-view
reading. The player confirmed the guide still looked correct and that the optimization worked.

The regional split produced 143 addressable batches in that pose (33 drawn + 110 culled), a modest increase
over the former 128 upload batches. That trade is intentional: it creates spatial ownership, and the partial
view now submits only the intersecting regions.

## 4. Verification

- Release build: **0 warnings, 0 errors**.
- Deterministic spatial harness: **6/6**.
  - zero and positive 32-block boundaries;
  - mathematical floor division for negative coordinates;
  - two adjacent voxels split across a region boundary;
  - guide-wide occupancy suppressing both shared boundary faces;
  - world-space regional sphere centre/radius.
- Package verification: **40 entries**, root `modinfo.json`/`Layout.dll`, forward-slash entry paths,
  packaged version **0.3.43**, and packaged DLL SHA-256 matching the Release output.
- Human playtest: whole-guide off-screen rejection and partial-guide spatial culling both approved.

## 5. Greedy face merging remains optional

The remaining mesh optimization is same-colour, coplanar greedy face merging inside each spatial region.
Its target is the **fully visible** case: reduce triangles without reducing guide resolution or culling
granularity.

It should be visually lossless, including at close range, only if a merge key preserves every rendered
distinction:

- face orientation and exact plane;
- rendered role/colour/alpha (body, anchor, primary, locked, grabbed, division);
- final z-fight inset/face position;
- hidden-guide filtering and all existing exposed-face decisions.

The guide shader uses a constant white texture, full-bright vertex colour, and no normal shading. Therefore
two adjacent coplanar faces with the same final colour and position have no intended internal visual boundary;
replacing them with one rectangle changes topology and triangle count, not the visible surface. Merging must
stop at colour, orientation, plane, inset, or occupancy boundaries. The verified draw recipe, transparent
palette, and region ownership must remain unchanged.

## 6. Start the next session here

v0.3.43 is the successful spatial-culling checkpoint. If further optimization is requested, prototype greedy
merging only in the volumetric exposed-face path and only inside one 32-block region. Compare mesh coverage
and per-colour face area against the unmerged builder before relying on visual playtesting. The expected
benefit is lower fully-visible triangle submission; partially/off-screen behavior should remain governed by
the v0.3.43 regional bounds.
