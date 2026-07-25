# SESSION 28 — v0.3.55–v0.3.58: the rendering arc reopened, measured, and halved

**Checkpoint:** Layout v0.3.58 is built and packaged on the `beta` branch. DataVersion remains **12**, wire
protocol remains **16**. Packages: `Layout0.3.55.zip` … `Layout0.3.58.zip`.

This session reopened the renderer work `SESSION_26.md` closed, on the grounds that the evidence which
closed it was wrong. It then delivered a **41% reduction in guide frame cost with no visible change**, and
fixed a client hang nobody had traced.

---

## 1. Why Session 26's conclusion did not survive

`SESSION_26.md` retired the whole rendering arc partly because an immense guide regressed from ~140 FPS to
~80 FPS. Two corrections from the human invalidated that:

1. **The 140 → 80 regression was a double-mesh draw bug**, not a cost of greedy merging. The agent doing the
   work reported drawing two mesh sets at the time. Merging on its own appeared to help.
2. **The in-game frame cap was 238 FPS.** Session 25's 4.3 ms "off-screen" baseline was the cap, not a
   floor. True baseline is ~1.0 ms.

A double draw of translucent depth-writing geometry also explains the visual defects Session 26 attributed
to merging itself — especially "excessive brightness", which is what two coincident shells at alpha 0.5 look
like. The later "face/depth variants" were likely chasing artifacts the bug created.

**Both rejected experiments were therefore rejected on appearance, not performance.** Spatial culling
(v0.3.43) worked and was human-approved; its only objection was seams. Merging appeared to help; it was
disliked visually.

### The seams were an ordering artifact, not a meshing artifact

Recovered from commit `4d2a7ff`, the rejected v0.3.43 regional build passed **guide-wide occupancy**, one
shared **`MinimumVoxelY`**, and a **single shared mesh origin**. It added no faces and introduced no
misalignment. Geometrically it was identical to the monolithic mesh.

What changed was submission order. `GuideRenderer` draws in the Opaque stage with manual alpha blending,
depth-tested and double-sided — its own comment states *"Guide-vs-guide overlap is order-dependent."* On a
hollow shell the guide overlaps itself at nearly every pixel, so whichever batch reaches a pixel first wins
and writes depth. Today's ~128 batches are sequential slices of the organic-growth voxel list, giving one
consistent sweep. v0.3.43 regrouped them into spatial regions drawn in dictionary-key order, so the winner
rule became inconsistent and changed abruptly at region borders.

**The accepted appearance is partly a by-product of voxel emission order.** That is why every optimization
"broke the look", and it is the constraint any future renderer work must design around.

---

## 2. Measured baseline (v0.3.55, uncapped)

| Condition | Mesh data | Frame time | Guide cost |
|---|---:|---:|---:|
| No guide in view | — | 1.0 ms | — |
| 8M guide, close | 1.81 GB / 64.7M verts | 9.2 ms | 8.2 ms |
| 8M guide, distant (still drawn) | 1.81 GB / 64.7M verts | 7.1 ms | 6.1 ms |
| 500k guide (shipped cap), close | 115.6 MB / 4.0M verts | 1.7 ms | 0.7 ms |

Cost splits roughly **75% coverage-independent** (vertex fetch, vertex shading, primitive setup) and
**25% coverage-dependent** (fill and blending). An earlier claim in this session that cost was entirely
coverage-independent was wrong and was corrected from these numbers.

---

## 3. Revision record

### v0.3.55 — measure the right thing

`.layout renderstats` now reports **vertices, indices, and bytes submitted**, not just triangles. Bytes
track the actual bottleneck; vertices are what welding moves, while triangles by design do not. Per-vertex
size is derived from the arrays the `MeshData` carries, so it follows any future format change
automatically.

### v0.3.56 — frame-cap warning fix

The v0.3.55 warning was wrong twice: it trusted the `maxFps` setting (which retains its slider value —
observed as 241 — while the limiter is off), and it warned when frame time was at or *below* the cap's
budget, which fires on every fast frame. A 1.0 ms uncapped baseline was flagged as clipped. It now warns
only on **evidence**: frame time sitting in a narrow band *around* the limiter's budget, the only state
where clipping is real. Silent otherwise.

### v0.3.57 — vertex welding (Stage 1)

`GuideMeshBuilder` emitted 4 fresh vertices per quad with **zero sharing** — confirmed in play at exactly
4 vertices and 6 indices per quad. Because the vertex format carries no per-face data (no normals, uv always
`(0,0)`), any two faces meeting at one position with one colour can share a vertex.

`GuideMeshOptions.WeldVertices` (default true) enables a per-`Build` vertex cache keyed **bit-exactly** on
position and packed colour. This is **deduplication, not merging**: triangle count, positions, winding,
colours, and submission order are unchanged; only the index buffer differs.

**Required supporting change — canonical lattice derivation.** Face bounds are now computed from their own
integer voxel coordinate instead of "min corner + edge". Equal in exact arithmetic, but rounding
`(v.X/16 − ox)` to float and then adding `edge` lands up to one ULP from rounding `((v.X+scale)/16 − ox)`
directly. That last bit decided whether two adjacent voxels agreed on their shared plane, and the old form
would have blocked nearly every cross-voxel weld. Coordinates differ from v0.3.56 by **~2 micrometers in
block units** — far below anything renderable.

**Equivalence harness: 10/10.** Compares both index buffers *in submission order*, dereferencing each
corner's position bits and colour bytes — sequence equality, not set equality, because submission order is
what the Session 25–26 experiments broke. Cases: hostile far-from-origin coordinates, world zero, negative
coordinates, dense solid box, scale 4, scale 16 (every face grid-coplanar), mixed render roles, the anchor
split, hidden guides, and an alternating solidity probe producing mixed per-face insets.

**Measured result:**

| 8M guide | v0.3.55 | v0.3.57 | Change |
|---|---:|---:|---:|
| Triangles | 32,358,196 | 32,358,196 | identical |
| Indices | 97,074,588 | 97,074,588 | identical |
| Vertices | 64,716,392 | 17,522,748 | **−72.9%** |
| Mesh data | 1.81 GB | 771.4 MB | **−58.4%** |
| Cost, close | 8.2 ms | 4.8 ms | **−41.5%** |
| Cost, distant | 6.1 ms | 3.1 ms | **−49.2%** |

Vertices per quad went from 4.00 to **1.083** on the 8M guide and **1.0002** on a 500k guide. A grid surface
cannot go below 1.0, so **this lever is spent.**

Human A/B via `/layout weld on|off` from one camera position on one guide: **no perceptible difference**,
confirming the harness proof in play.

### v0.3.58 — stream loaded and remote guides; `/layout weld`

**The bug:** `RebuildGuide` was fully synchronous — voxel generation, mesh build, and upload on the main
thread with **no size check**. It runs on world load (`RebuildAll` via bulk sync) and when a guide arrives
from another player. Only the local placer's own path was ever streamed. Hence a multi-second client hang
loading a world containing a large guide, and a large guide landing on other players in one lump.

`SESSION_26.md` §3 claims the v0.3.49 rollback retained reload-scaffold behaviour. **It did not** — that
behaviour belonged to the reverted v0.3.43 spatial work and was lost with it.

**The fix:** above `SettledStreamingVoxelThreshold` a settled Volumetric volume renders its wireframe
scaffold immediately and streams the exact shell in behind it, reusing the existing settled-materialization
lane (previously reachable only from a Wireframe→Shell edit). Keyed on the **persisted
`CachedVoxelCount`** — generating the voxel set to measure it would already have paid the cost being
avoided.

Two hazards handled explicitly:

- **Placement effects do not fire.** The chalk puff/snap is raised only from the local placement path, so
  loading a world of large guides sets off no sound or particles.
- **No infinite rebuild loop.** The materialization completion/failure handlers call back into a rebuild;
  those two sites now force the synchronous path, or they would scaffold, fail, and recurse.

`/layout weld on|off` rebuilds every guide with welding toggled, for live A/B. Diagnostic only, not
persisted.

---

## 4. Flagged for review

1. **`SettledStreamingVoxelThreshold = 100,000`** is a judgement call, not a measurement. Below it a
   synchronous rebuild is a sub-frame blip; at 500k a visible hitch; at 8M the multi-second hang. Higher
   means fewer guides visibly grow in on world load; lower means smoother loading but more animating at
   once. One constant, retunable from play.
2. **Streaming on world load changes what loading looks like** — large guides now grow in rather than
   simply being present. Deliberate, and what the human asked for, but it is a visible behaviour change.

---

## 5. Verification

- Every revision: Release build **0 warnings, 0 errors**.
- Welding equivalence harness: **10/10**, re-run against the final v0.3.58 build.
- Each package: **40 entries, 37 assets**, root `modinfo.json`/`modicon.png`/`Layout.dll`, forward-slash
  entry paths. v0.3.55 packaged DLL SHA-256 verified against the Release output.
- No DataVersion, protocol, config, save, or network change in any revision. Beta builds interoperate with
  v0.3.53/0.3.54 clients and servers.

**Not yet playtested:** the v0.3.58 streaming change. The world-load half awaits a run with the 8M guide;
the remote-arrival half needs a second player and is unverified.

---

## 6. Next

`PLAN_RENDER_PERFORMANCE.md` is the live plan. Stage 1 is delivered and spent. Remaining levers, with
indices now **48% of all mesh traffic** and untouched by welding:

- **Custom shader** — under active discussion; the initial "defer" recommendation considered only vertex
  format and is being revisited (per-vertex and per-fragment execution cost, and compact per-face data, are
  the larger prizes).
- **Client performance options** — guide render distance and wireframe-at-distance, defaulting to current
  behaviour.
- **Ordering fix then spatial culling** — gated; changes the accepted appearance.
- **Greedy merging** — closed. Beyond ordering, the per-face z-fight inset caps merge runs at 16 voxels at
  scale 1, so the rejected banding is structural.

The beta branch also carries the human's A10 backlog, of which the **Box/Square cardinal-constraint bug** is
the only outstanding functional defect.
