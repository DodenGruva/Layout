# PLAN — Guide render performance (post-v0.3.54, `beta` branch)

> **Status:** proposed, not started. Written 2026-07-24 after re-examining the Session 25–26 rendering arc
> with corrected measurements from the human. Supersedes `SESSION_26.md` §7's conclusion that the renderer
> is finished — see §1, which invalidates the performance evidence that closed that arc.

---

## 1. What the corrected measurements actually say

Session 26 rolled back the whole rendering arc partly on the grounds that an immense guide regressed from
~140 FPS to ~80 FPS. Two corrections from the human change that reading:

1. **The 140 → 80 regression was very likely a double-mesh draw bug**, not a cost of greedy merging. The
   agent doing the work reported drawing two mesh sets at the time. Greedy merging on its own appeared to
   *help*.
2. **The in-game frame cap was 238 FPS.** Session 25's 4.3 ms "off-screen" baseline is therefore the cap,
   not a floor. The true no-guide frame time is about **2.9 ms** (~350 FPS uncapped).

## 1a. Measured baseline (v0.3.55, uncapped, 2026-07-24)

Taken with the Stage 0 instrumentation. **These supersede every earlier figure in Sessions 25–27**, all of
which were taken against a capped baseline.

| Condition | Mesh data | Frame time | Guide cost |
|---|---:|---:|---:|
| No guide in view | — | 1.0 ms | — |
| 8M guide, close | 1.81 GB / 64.7M verts | 9.2 ms | **8.2 ms** |
| 8M guide, distant (still drawn) | 1.81 GB / 64.7M verts | 7.1 ms | **6.1 ms** |
| 500k guide (shipped cap), close | 115.6 MB / 4.0M verts | 1.7 ms | **0.7 ms** |

**Correction to the earlier reading in this plan.** Screen coverage is *not* free — the same guide costs
8.2 ms filling the screen and 6.1 ms as a distant speck. The split is roughly:

- **~6.1 ms (75%) coverage-independent** — vertex fetch, vertex shading, primitive setup. Scales with mesh
  size. This is what Stages 1 and 2 attack.
- **~2.1 ms (25%) coverage-dependent** — fill and blending. Scales with screen area and overdraw depth.
  Untouched by mesh optimization; only Stage 3/4 (drawing less) helps here.

Geometry still dominates at 3:1, so the plan's priorities hold, but expected gains must be applied to the
6.1 ms term only — not the whole 8.2 ms.

**Zero vertex sharing is confirmed.** 64,716,392 vertices against 97,074,588 indices is exactly 4 vertices
and 6 indices per quad, so every corner is currently duplicated across all faces that touch it. Stage 1's
premise is verified, not assumed.

**Byte accounting verified:** 64,716,392 × 24 B + 97,074,588 × 4 B = 1.94 GB, matching the reported 1.81 GiB.

Both rejected optimizations were therefore rejected on **appearance**, not performance:

| Experiment | Performance | Actual reason it was dropped |
|---|---|---|
| Spatial culling (v0.3.43) | Large, measured, human-confirmed | Visible seams at region borders |
| Greedy merging (v0.3.44–48) | Apparent gain | Disliked look; later defects likely from the double-draw bug |

Both visual failures share one root cause, confirmed by reading the rejected code: **guides are
order-dependent translucent geometry.** [`GuideRenderer.cs`](../src/Systems/GuideRenderer.cs) draws in the
Opaque stage with manual alpha blending, depth-tested and double-sided; its own comment states
*"Guide-vs-guide overlap is order-dependent."* The v0.3.43 regional build passed guide-wide occupancy, a
shared `MinimumVoxelY`, and a single shared mesh origin, so it added no faces and introduced no
misalignment — the seams were a draw-order artifact, not a meshing artifact. Anything that regroups
primitives changes the picture.

## 2. The bottleneck, quantified

At ~32.6M triangles the mesh is roughly 65M vertices. Each vertex is position (12 B) + UV (8 B) + RGBA
(4 B) = **24 bytes**, plus 4-byte indices:

```
vertices  65.2M x 24 B  = 1.56 GB
indices   32.6M x 3 x 4 B = 0.39 GB
                          --------
per frame                  ~1.96 GB
```

At 4.3 ms that is roughly **450–550 GB/s sustained**, which is at the limit of what a modern discrete GPU
delivers. This single number explains every observation: coverage-independent cost, spatial culling working
spectacularly, and merging helping.

**Why this matters for the public release.** Bandwidth scales down with hardware. A player on integrated
graphics with ~100 GB/s hits the same wall at roughly a fifth the guide size — around 1.6M voxels — and pays
over a millisecond per guide at the shipped 500,000 default cap. Multiple guides in view compound it. The
optimizations below are flat multipliers that benefit every player at every guide size.

## 3. Levers, ranked

| # | Lever | Effect | Visual risk |
|---|---|---|---|
| 1 | **Vertex welding** — share vertices between adjacent faces instead of emitting 4 fresh per quad | ~40–50% fewer bytes | **None — pixel-identical by construction** |
| 2 | **Lean vertex format** — custom shader, drop the always-`(0,0)` UV and the texture sample | ~33% further | Low; fog is the one risk |
| 3 | **Client performance options** — let low-end players opt into cheaper distant guides | Large where enabled | None by default |
| 4 | **Ordering fix + re-land spatial culling** | Large in partial view | High — changes the accepted look |
| 5 | ~~Greedy merging~~ | — | Do not attempt (§8) |

Levers 1 and 2 multiply: combined, an estimated **60–65%** reduction with no change to what anyone sees.
That is the whole plan's centre of gravity.

---

## 4. Stage 0 — measure first (v0.3.55)

Small, no visual risk, and it is what makes every later stage provable on hardware we do not own.

- Extend `.layout renderstats` to report, for the last frame: **vertex count**, **index count**, and
  **estimated bytes submitted**. Bytes are the metric that actually tracks the bottleneck; triangle counts
  proved misleading in Sessions 25–26.
- Report the frame cap alongside smoothed frame time, or warn when readings sit at the cap. Session 25's
  headline comparison was clipped by the cap and nobody noticed for two sessions.
- Document in `HANDOFF.md` that render measurements must be taken **uncapped**.

**Exit criteria:** baseline numbers recorded for the 8M guide and for a default-cap 500k guide, uncapped.

## 5. Stage 1 — vertex welding (v0.3.56)

The highest value-to-risk step in the plan.

Today [`GuideMeshBuilder.AddQuad`](../src/Systems/GuideMeshBuilder.cs) emits **4 fresh vertices per quad**
with no sharing. Because the vertex format carries no per-face data — no normals, and UV is always `(0,0)` —
**any two faces meeting at the same position with the same colour can share a vertex.** On a stair-stepped
shell most corners are shared by 2–4 faces.

- Maintain a per-batch map from (exact position, packed colour) to vertex index; call `AddVertex` only on a
  miss, and `AddIndex` against the existing vertex otherwise.
- **Triangles, winding, order, and colours are unchanged.** Only the index buffer differs. The output is
  pixel-identical — this is deduplication, not merging.
- Faces carrying a `BlockPlaneInset` have genuinely different corner positions and simply will not weld.
  Keying on exact position preserves that automatically; do not round.

**The risk is CPU build time, not appearance.** A naive global weld is ~65M dictionary operations on the 8M
guide — seconds of work. Mitigations, in order of preference:

1. Weld **within each materialization batch only**. Batches are spatially coherent, so most sharing is
   still captured, and per-batch cost stays in the tens of milliseconds.
2. Key on a packed 64-bit lattice coordinate plus a small colour index rather than floats.
3. If build time still regresses materialization, skip welding above a voxel threshold and record it.

`NewMeshForFaces` pre-allocates exactly 4 vertices per face; welding only ever uses fewer, so no
reallocation is possible. Over-allocation is acceptable.

**Verification:** extend the existing deterministic mesh harness to assert that welded and unwelded builds
produce the **same triangle set** (same positions and colours, order-insensitive) for a fixed input. That is
a proof of visual equivalence, not a playtest impression. Then confirm materialization still streams
smoothly on the 8M guide.

**Exit criteria:** bytes/frame down ~40%+, triangle set proven identical, materialization cadence unchanged,
human sees no difference in play.

### Stage 1 result — DELIVERED v0.3.57, measured in play 2026-07-24

| 8M guide | Before (v0.3.55) | After (v0.3.57) | Change |
|---|---:|---:|---:|
| Triangles | 32,358,196 | 32,358,196 | **identical** |
| Indices | 97,074,588 | 97,074,588 | **identical** |
| Vertices | 64,716,392 | 17,522,748 | **−72.9%** |
| Mesh data read | 1.81 GB | 771.4 MB | **−58.4%** |
| Guide cost, close | 8.2 ms | 4.8 ms | **−41.5%** |
| Guide cost, distant (geometry only) | 6.1 ms | 3.1 ms | **−49.2%** |

Triangle and index counts came back byte-identical in play, which is the field confirmation of the
harness's equivalence proof.

**Welding is running at essentially its theoretical limit.** Vertices per quad went from exactly 4.00 to
1.083 on the 8M guide and to 1.0002 on a 500k guide — a grid surface cannot do better than 1.0. There is no
further tuning available here; this lever is spent.

Equivalence harness: **10/10**, comparing both index buffers in submission order and dereferencing each
corner's position bits and colour bytes. Cases covered hostile far-from-origin coordinates, negative
coordinates, scale 4 and 16, mixed render roles, the anchor split, hidden guides, and an alternating
solidity probe producing mixed per-face insets.

**Consequence for the rest of this plan: indices now dominate.** At 4 bytes each they are 370 MB of the
remaining 771 MB — **48% of all traffic** — and neither welding nor a leaner vertex format touches them.
See the revised §6.

## 6. Stage 2 — custom shader — REINSTATED AND REFRAMED (2026-07-24)

**This stage was briefly deferred on a bad analysis, then reinstated after an external developer pushed
back. They were right.** The deferral treated "custom shader" as purely a *vertex format* change — drop the
always-`(0,0)` UV, maybe shrink positions — worth ~0.5 ms now that indices dominate. That framing missed the
larger point entirely.

### What guides actually pay for today

Guides render through `PreparedStandardShader`, Vintage Story's **full world shader**. `RgbaLightIn`,
`NormalShaded`, `ExtraGodray` and a white texture are set to neutralise its effects — but **uniforms do not
remove instructions**. Reading the shipped sources (`assets/game/shaders/standard.vsh` / `.fsh`), every
guide vertex and fragment still executes:

**Per vertex — 17.5M times per frame on the 8M guide:**

- `applyVertexWarping` + `applyGlobalWarping` (wind/noise vertex animation)
- `applyLight(...)` and `getFogLevel(...)`
- `calcShadowMapCoords(viewMatrix, worldPos)` — unconditional shadow-map coordinate work
- `unpackNormal(flags)`, `normalize`, and a `mat4` multiply for a normal **nothing ever reads**
- a distance-fade term and an optional spheres-fog term

**Per fragment, over a guide that can cover the screen several layers deep:**

- `texture(tex, uv)` — sampling a white texture to multiply by 1
- `applyFogAndShadow(...)` — includes **shadow map sampling**
- `getSkyMurkiness()` / `getUnderwaterMurkiness()`
- glow mixing, damage-effect noise, and overlay-texture branches
- under `SSAOLEVEL > 0`, **two additional full-screen render targets written per fragment**

A guide needs: transform the position, pass the colour through, apply fog. Everything above is waste, and it
scales with exactly the two terms measured in §1a.

### Why this is the bigger prize

The §1a split attributed ~3.1 ms to "geometry" and ~1.7 ms to "fill". Both were assumed to be data
movement. That assumption is wrong: 771 MB in 3.1 ms is only ~260 GB/s, far below what this GPU delivers, so
a real share of the geometry term is **vertex shader execution**, not fetch. And the fill term is
fragment-shader execution plus blending, not blending alone.

Neither is reachable by any amount of format tuning. Both are reachable by a shader that does less.

There is also a third prize the deferral missed: with a custom shader, faces need not be sent as vertices at
all. `MeshData` exposes `CustomBytes`/`CustomShorts`/`CustomInts` **and per-attribute `*Instanced` flags**,
so a compact per-face record (position, orientation, colour — around 12 bytes) with corners generated in the
shader would replace today's ~50 bytes per face **and eliminate the index buffer** — the 370 MB that §10
correctly identifies as untouchable by welding or by a leaner vertex format.

### Verified API surface

- `capi.Shader.NewShaderProgram()`, `RegisterFileShaderProgram(name, program)`, `ReloadShaders()`
- `IShaderProgram`: `Use()`, `Stop()`, `Compile()`, `LoadError`, `Uniform(...)`, `UniformMatrix(...)`,
  `BindTexture2D(...)`, `AssetDomain`, `PassId`/`PassName`
- Shader assets live at `assets/layout/shaders/*.vsh` / `*.fsh`; re-register on `capi.Event.ReloadShader`

### Concrete risks, now identified rather than guessed

1. **Multiple render targets.** The Opaque stage binds more than one output. `standard.fsh` writes
   `outColor` (location 0) **and `outGlow` (location 1)**, plus SSAO targets 2–3 under `SSAOLEVEL > 0`. A
   custom shader that writes only `outColor` leaves the others undefined — expect glow/godray or SSAO
   artifacts. Must write `outGlow` explicitly and handle the SSAO case.
2. **Preprocessor defines.** `SSAOLEVEL`, `BLOOM`, `SHINYEFFECT` and friends are injected by the engine.
   Whether they reach a mod's registered shader needs confirming before relying on `#if` blocks.
3. **Attribute locations shift when arrays are omitted.** The Module-7 "solid black arch" finding recorded
   in `GuideMeshBuilder` — that dropping the uv array made rgba bytes land in the uv slot — implies VS
   assigns locations by which arrays are present, not fixed. With a custom shader this is harmless *if the
   declared locations match*, but it must be verified, not assumed.
4. **Fog.** Guides visibly fade with distance today. The custom shader must reproduce that or distant guides
   change appearance. This remains the main fidelity risk.

### Sequence

**Stage 2a — minimal shader, same mesh layout minus the UV.** Trivial vertex and fragment work, fog
replicated, `outGlow` written. This is primarily a **measurement**: it isolates how much of the 4.8 ms is
shader execution rather than data movement. If a large share disappears, 2b is clearly worth the harder
engineering; if almost nothing moves, the cost really is bandwidth and the stage stops here having cost one
iteration.

**Stage 2b — per-face records with shader-generated corners.** Only if 2a lands cleanly *and* its
measurement justifies it. This is the one that reaches the index data, and the one most likely to need raw
GL alongside VS's renderer — i.e. the one that may prove impractical.

Built on the **`beta-shader`** branch, merged to `beta` only once measured and fog-checked. If abandoned,
the branch is deleted and `beta` was never touched.

### Original Stage 2 design (retained for reference)

Every vertex currently carries a UV of exactly `(0, 0)` — `AddQuad`, `AddBox`, and `AddTile` all hardcode
it — because VS's standard shader demands that layout (the Module-7 "solid black arch" finding at
[`GuideMeshBuilder.cs:711`](../src/Systems/GuideMeshBuilder.cs)). **A third of the bandwidth is spent
reading zeros.**

- Add a minimal custom shader for unlit vertex-coloured geometry: position + RGBA in, colour out.
  Registered via `capi.Shader.RegisterFileShaderProgram` with a re-register on `capi.Event.ReloadShader`,
  assets under `assets/layout/shaders/`.
- Build meshes with `withUv: false`. Drops 8 B/vertex and removes the white-texture sample entirely
  (`_whiteTex` and its bind can go).
- **Verify the attribute binding first.** VS's mesh upload uses a fixed attribute-location convention; the
  custom shader's declared locations must match a no-UV `MeshData`. Confirm this on a small guide before
  converting the immense path.

**The one real fidelity risk is fog.** `PreparedStandardShader` currently configures fog, shadow, and
ambient uniforms for guides, and guides visibly fade with distance. A custom shader must replicate the fog
term exactly or distant guides will change appearance. Treat fog as a required feature of the shader, not an
afterthought, and A/B it against the Stage 1 zip at long range in clear and foggy weather.

Optional follow-on, only if Stage 2 lands cleanly: store positions as fixed-point integers instead of
floats. Guide positions are origin-relative and always on 1/16 boundaries, so this is **exact, not
approximate**, and takes 24 B/vertex down toward 10 B. Treat as a separate revision.

**Exit criteria:** further ~33% off bytes/frame, side-by-side screenshots identical at close range and at
distance including fog, no shader reload issues on world re-entry (see the v0.1.24 icon-registration
lesson — client lifecycle matters).

## 7. Stage 3 — client performance options (v0.3.58)

Directly serves the public-release concern: give low-end players knobs instead of changing everyone's look.
New keys in `layout-client.json`, **all defaulting to current behaviour** so no existing player sees a
change:

- `guideRenderDistance` — cap guide rendering at a distance shorter than the terrain view distance.
  Default 0 = follow view distance, exactly as now.
- `simplifyDistantGuides` — beyond that distance, draw the structural wireframe instead of the full shell.
  Default false. Reuses the existing wireframe path, so this is configuration rather than new rendering.

Because guide cost is coverage-independent, a guide 200 blocks away costs the same as one filling the
screen. It is the worst value-per-pixel in the system and the best thing a struggling player can turn off.

Document both in the mod description so players who report stutter have a first answer.

## 8. Stage 4 — ordering and spatial culling (only if still needed)

Do not start this until Stages 1–3 are measured. They may remove enough cost that this is unnecessary, and
this is the only part of the plan that changes the appearance the human has twice rolled back to protect.

If pursued, the order is non-negotiable:

1. **Ordering fix first, shipped alone, behind a `.layout` toggle.** Sort mesh batches front-to-back by
   distance each frame. Cost is negligible. It replaces today's accidental order — sequential slices of the
   organic-growth voxel list — with one consistent rule across the whole guide. This is what makes spatial
   culling visually viable. It is also a global look change, so it needs the human's eye on a live toggle
   before anything is built on it. **If the sorted look is rejected, Stage 4 ends here.**
2. **Then re-land spatial culling**, recovering the v0.3.43 implementation from commit `4d2a7ff`. It was
   measured, human-approved in play, and its only objection was the seam that step 1 addresses. Note that
   its recorded 4.4 ms partial-view result was clipped by the frame cap — the real win was larger.

**Do not attempt greedy merging.** Beyond the ordering problem, the per-face z-fight inset is applied only
to faces on block-grid planes with a solid block behind them, so faces on the same plane genuinely sit at
different positions every 16 voxels at scale 1. A correct merge key must include the inset, which caps merge
runs at 16 and reproduces the banding that was rejected. That is structural, not a tuning failure.

**Whatever gets rebuilt, add a hard guard that only one mesh set per guide can ever be drawn.** That bug
cost an entire arc, a rollback, and the credibility of the performance evidence.

---

## 9. Working rules for this plan

- **Scope:** client-side rendering only. **DataVersion 12 and protocol 16 are unchanged at every stage.**
  No save-format or wire changes, so beta builds interoperate with v0.3.53/0.3.54 clients and servers.
- **Branch:** `beta`. Each stage is one revision: code → `dotnet build` → bump `modinfo.json` → new
  `Layout<version>.zip`. Docs and commits only when the human says so.
- **Keep every stage's zip.** A/B comparison against the previous stage is the verification method, and the
  cap-clipped Session 25 measurements show why single-number comparisons are not enough.
- **Each stage is independently revertable.** If a stage fails, drop that stage; it does not invalidate the
  ones before it.
- **Test uncapped**, and test at least once on the weakest hardware available. A beta-branch tester on
  integrated graphics would be worth more than any synthetic measurement — that is the population this plan
  exists for.

## 10. Expected outcome

Measured, not projected — Stage 1 is delivered:

| Stage | Bytes/frame, 8M guide | Geometry term | Total 8M cost | Visual risk |
|---|---:|---:|---:|---|
| v0.3.55 baseline (measured) | 1.81 GB | 6.1 ms | **8.2 ms** | — |
| **v0.3.57 welding (measured)** | **771 MB** | **3.1 ms** | **4.8 ms** | **none** |
| Stage 2, if ever built | ~537 MB | ~2.2 ms | ~3.9 ms | fog |

**Stage 1 alone removed 41% of the total cost and 58% of the mesh traffic, with nothing visible changed.**

**Where the remaining cost sits (8M guide, 4.8 ms):**

- ~1.7 ms fill and blending — only Stage 3 or 4 reaches it
- ~1.6 ms vertex traffic (401 MB) — Stage 2 could roughly halve this
- ~1.5 ms index traffic (370 MB) — **nothing in this plan touches it**

Index count follows triangle count, so only merging reduces it, and merging is closed for the reasons in §8.
That 370 MB is the floor for a guide of this size.

**What this means for low-end hardware**, which is why the work was done. Scaling by bandwidth ratio, a
player at roughly a fifth of this GPU's throughput now pays about **1.0 ms for a shipped-default 500k guide,
down from 2.4 ms** — around 1.4 ms per guide returned on a 16.7 ms budget, multiplied by every guide in
view. That benefit is unconditional: no setting to find, no look to accept, and it scales down to every
guide size rather than only the extreme case.
