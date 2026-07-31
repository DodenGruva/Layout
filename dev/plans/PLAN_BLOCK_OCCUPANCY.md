# PLAN — Block-occupancy recolour ("is there material inside this guide voxel?")

> **Status: ✅ DELIVERED — v0.3.70–v0.3.85 (SESSION_29).** All four stages are shipped and the feature is
> playtest-confirmed: *"the color difference is incredibly helpful."* Retained rather than archived because
> §7 still holds open verification items, and §2's shader-lookup route remains the recorded escape hatch if
> the workload ever becomes update-dominated.
>
> Redrafted 2026-07-26 as a **full replacement**. The first draft (2026-07-25) reached conclusions that
> playtesting disproved. Read §0 before anything else — it records what was tried, what it cost, and which
> of the original plan's conclusions are dead.
>
> **The design changed shape twice.** It began as "bake a second colour into the mesh", became "one-shot
> scan, no live updates", and is now **a per-fragment lookup in the guide shader against a sparse world
> occupancy structure**, with no mesh involvement at all. Each move was forced by a measurement or a
> playtest, not by preference.

---

## 0. What was investigated, and what it settled

Everything in this section was established between 2026-07-25 and 2026-07-26, in play or against the shipped
assemblies. It is recorded because most of it is the reason the design looks the way it does, and because
three separate conclusions in the first draft turned out to be wrong.

### 0.1 The geometric half already shipped — v0.3.70 / v0.3.71

The first draft's outset idea was correct and is **done**. Volumetric guides now push every exposed face
outward by a hair instead of probing the world and pulling faces inward.

Two findings came out of building it, both load-bearing for what follows:

- **The offset must not depend on world state.** The human's observation: a rebuild triggered after the
  player fills the volume would see solid neighbours everywhere and flip those faces *inward*, breaking the
  guide at exactly the moment it is needed. The offset is therefore derived from the guide's own voxel set
  and consults the world not at all. The solidity probe, `_deferredSolidity`, and `TrackedSolidProbe` were
  deleted with it.
- **The magnitude dropped 5x, to 0.0006** (v0.3.71, playtest-confirmed). Expected: an inset had to open a
  gap wide enough to see *past* the surface in front of it, while an outset only has to win the depth
  comparison.

**None of this is part of the remaining work.** It stands on its own and stays.

### 0.2 Opacity was tried as the cheap answer, and failed

A settings page with a guide-opacity slider shipped as v0.3.72–v0.3.74 specifically to test whether a
fainter guide would let the player see material through it.

**Human verdict after play: it does not help.** It is still too hard to tell whether a chiselled sub-block,
particularly at scale 1, sits inside a guide voxel. That is the finding that makes the colour change
necessary rather than optional, and it also sharpened the requirement — see §1.

The settings page itself stays and is where this feature's toggle now lives (§6).

### 0.3 The first draft's "one-shot scan is enough" is WRONG

The original §4 argued that a single manual `/layout scan` sufficed, because the scan result does not change
while the player chisels. That reasoning assumed the mark is only needed *while cutting*.

It is not. The human needs to see, continuously and at a glance, whether material occupies a guide voxel —
a question asked constantly while filling, not once before chiselling. A one-shot scan cannot serve that,
and neither can anything that goes stale between deliberate commands.

`/layout scan`, the manual-refresh staging, and the whole "nothing invalidates" argument are dead.

### 0.4 Rebuild cost — why the mesh cannot carry this

Measured, from `SESSION_28.md` §4.1:

| Guide size | Full synchronous rebuild |
|---|---|
| under ~100,000 voxels | sub-frame blip, invisible |
| ~500,000 voxels | visible hitch |
| 8,000,000 voxels | multi-second hang |

Above 100,000 voxels the renderer streams instead. Streaming has a floor: batches cap at 128
(`MaterializationMaximumBatches`) and pacing caps at 45 ms per batch, so **any guide over ~120,000 voxels
takes 5–6 seconds to fully reappear** regardless of size.

This feature is a scale-1 workflow *by definition* — one guide voxel must equal one chisel voxel — and
scale 1 is exactly where voxel counts explode. A house-sized hollow shell at scale 1 is already in the
hundreds of thousands. **The guides this feature exists for are the ones that cannot afford a rebuild.**

### 0.5 Per-batch rebuild was viable, and is no longer needed

Worth recording because it was nearly the design.

Materialization batches are contiguous runs of a voxel list sorted **X, then Y, then Z**
(`ProduceCleanMaterializationMeshes`), so each batch is effectively a thin slab of the guide along one axis.
A block change sits at one X and lands in one batch, occasionally two — making a targeted rebuild worth
roughly **1/128** of a full one.

The blocker was plumbing, not structure: `GuideMesh.Auxiliary` is a flat `List<MeshRef>` with no record of
which voxels produced which entry. Making batches individually rebuildable was going to be the single
largest piece of work in the feature. **§4's design removes the need for it entirely.**

### 0.6 Colour-only mesh updates: supported by the API, blocked by our own welder

`IRenderAPI.UpdateMesh(MeshRef, MeshData)` is documented as updating *any non-null data*, so a package
carrying only `Rgba` re-uploads only the colour buffer, leaving geometry alone. The cheap recolour is a real,
supported operation.

It cannot be used here. `GuideMeshBuilder.VertexWelder` keys on **(x, y, z, colour)** — a welded vertex is
one entry that several triangles point at, and the colour lives *on* that entry. Two voxels that were the
same colour at build time are sharing vertices right now; the moment one becomes "built", that shared vertex
would have to be two colours at once.

**A correction to an earlier claim in this project's discussion:** this does *not* mean welding is
incompatible with multi-coloured guides. Guides already carry five or six colours at once (body, locked,
apex, anchor, grabbed, division) and still weld to 1.083 vertices per quad. Only vertices sitting *exactly
on* a colour boundary fail to match and get duplicated; contiguous regions weld normally. Baking occupancy
colours into a mesh at build time is therefore cheap. **The narrow thing welding forecloses is changing a
colour in place after the fact.**

The escape — refusing to weld across voxel boundaries — costs the entire win. Unwelded is 4.00 vertices per
quad, welded is 1.083, and essentially all of that comes from cross-voxel sharing (on a one-voxel-thick
shell, a voxel's two exposed faces sit on opposite sides of the cube and share no corners). Not worth it.

### 0.7 CLAUDE.md's renderer warning does not apply to a shader lookup

Recorded because it was misapplied once already in discussion.

The warning is specific: guides are order-dependent translucent geometry, so whichever mesh draws first wins
the depth test, and therefore **"any change that regroups primitives changes the picture."** That is what
killed the Session 25–26 experiments — spatial partitioning and greedy merging both reshuffled submission
order.

A per-fragment colour lookup regroups nothing. Same triangles, same batches, same submission order; only the
colour a fragment resolves to changes. By CLAUDE.md's own test it is in the safe category, the one it credits
vertex welding to.

**There is a genuine constraint from the shader's own notes, though: modulate RGB only, never alpha.**
Changing alpha per fragment interacts with the order-dependent blending between overlapping guide faces.
`guide.fsh` states this explicitly where the voxel frame does its darkening.

### 0.8 The lookup's hard part is already solved and shipping

The voxel outline feature (v0.3.62) is the same shape of change and it works. `guide.vsh` already forwards
`layoutLocalPos` to the fragment stage, and `guide.fsh` already knows `layoutVoxelSizeIn` and
`layoutGridOffsetIn` — so **"which voxel cell does this pixel belong to" is computed today, every frame.**

It also establishes the disable pattern this feature's toggle should copy: `layoutVoxelSizeIn <= 0.0` returns
early and the frame costs nothing.

### 0.9 A dense occupancy volume is infeasible; a sparse one is cheap

The first instinct — a 3D grid at 1/16 resolution covering the guide's bounding box — collapses, because
guides are sparse (hollow shells) while such a grid is dense:

| Guide bounding box | Cells at 1/16 | Size at 1 bit/cell |
|---|---:|---:|
| 40 blocks cubed | 262 million | ~33 MB |
| 100 blocks cubed | 4.1 billion | ~512 MB |

It grows with the **cube** of guide size. Infeasible exactly where it is needed.

Block resolution (one entry per world block) is trivially cheap but **cannot see inside a chiselled block**,
which is precisely the human's requirement. Rejected.

**The human's correction — store ranges, not every coordinate — is the resolution, and it matches how the
game already stores the data.** `BlockEntityMicroBlock.VoxelCuboids` is a `List<uint>` of packed cuboids,
not a grid. Confirmed present in the shipped `VSSurvivalMod.dll` along with `GetVoxelMaterialAt` and
`MaterialIds`. See §3 for the structure this produces and its (small) cost.

---

## 1. The requirement, stated precisely

**The player must be able to see, at a glance and continuously, whether material occupies a given guide
voxel — at 1/16 resolution, including inside partially chiselled blocks.**

Three things follow, and each rules out a design considered earlier:

1. **Sub-block truth is mandatory.** Block-level occupancy is not enough (§0.9).
2. **It must stay current as the player works.** One-shot scans and manual refreshes are out (§0.3).
3. **It must not cost a mesh rebuild.** The guides it serves are the ones that cannot afford one (§0.4).

---

## 2. Design — the shader reads occupancy per fragment

Vertex colours are left completely alone. The guide fragment shader already knows which voxel cell each
pixel sits in (§0.8); it gains one lookup into a world-occupancy structure and tints the fragment's RGB when
the answer is "material here".

Consequences, all of them good:

- **No mesh rebuild, ever** — not on block change, not on toggle, not on scan. Welding is untouched, the
  per-batch machinery of §0.5 is unnecessary, and the v0.3.42 renderer baseline is not disturbed.
- **Nothing regroups**, so CLAUDE.md's hazard is not engaged (§0.7).
- **Updates are proportional to what changed**, not to guide size — a placed block rewrites one entry.
- **The toggle is a uniform flip** (§6), not a rebuild of every guide.

### 2.1 Resolution and guide scale

The shader samples occupancy at the fragment's own position, at 1/16 resolution, **regardless of the guide's
voxel scale**. At scale 1 that is exactly per-guide-voxel and is the case the feature is for.

At coarser scales one guide voxel spans many world cells, and a per-fragment sample will show the real
material distribution *within* the voxel rather than a single verdict for it. **This supersedes the original
plan's §5 "any / majority / all" vote**, which assumed a per-voxel decision computed on the CPU. One sample
is cheaper than any vote and arguably more informative. Flag for playtest; if it reads as noise at scale 16,
fall back to sampling the guide voxel's centre only.

### 2.2 What gets tinted

Recommendation: blend the fragment toward the "built" colour by a configurable strength, applied to all
guide fragments, RGB only (§0.7). Anchors and apex points are a handful of voxels and tinting them is
unlikely to matter. If it proves confusing in play, excluding them is a later refinement, not a design
change.

---

### 2.3 Multiplayer — the recolour is per-player, at every layer

**Nothing about this feature leaves the client.** Occupancy is read from the client's own view of the world,
assembled into a structure on the client's own GPU, and consumed by the client's own copy of the guide
shader. No packet, no server validation, no shared state.

The client already holds the sub-block data locally — it must, in order to draw chiselled blocks at all,
and it arrives through the same block-entity sync that carries the `BlockChanged` event (§3.2). So the
1/16-resolution answer needs no server round-trip.

The consequence in multiplayer: **the guide stays shared and server-authoritative; its appearance does
not.** Two players looking at the same guide see it differently if one has the recolour on. That is already
true of guide opacity, the voxel outlines, and the custom shader, so it introduces no new behaviour — just
one more entry on that list.

Two boundaries worth stating so they are not re-derived later:

- **The marks cannot be shown to anyone else.** "Point at a guide and let a teammate see which voxels are
  built" would require real protocol work and is explicitly not planned. If it is ever wanted, it is a new
  feature, not a setting.
- **Client-only mode is unaffected.** Private guides on servers without Layout behave identically, because
  there is nothing for the server to be missing (§7.7).

For contrast, this mod does have a client preference that is *not* purely local: the chalk-refill setting
became protocol-level at protocol 6, because refill policy has gameplay consequences a server must enforce.
Occupancy recolour changes pixels and nothing else, so it stays where it is.

---

## 3. The occupancy structure

Two levels, split where reality already splits.

**Level 1 — per world block**, covering the guide's bounding box. Three states: *empty*, *solid*, *partial*.
Almost every block is one of the first two and needs nothing further.

**Level 2 — detail bricks, for partial blocks only.** One 16-cubed occupancy brick per chiselled block,
sourced from `VoxelCuboids` / `GetVoxelMaterialAt`.

Cost then tracks **how much has actually been chiselled**, not the volume:

| Component | 40-block-cube guide |
|---|---:|
| Level 1 (64,000 blocks) | ~128 KB |
| Level 2 (2,000 chiselled blocks) | ~1 MB |
| **Total** | **~1.1 MB** |

Compare the dense equivalent in §0.9: ~33 MB, growing as the cube of guide size.

**On form:** ranges are the right thing to read from the world and to carry around, but a shader wants a
constant-time lookup, so cuboids are expanded into the fixed brick at upload. At half a kilobyte per
chiselled block that expansion costs nothing.

**Scope:** one structure per guide, covering that guide's bounding box, is simpler and bounded — overlapping
guides duplicate a little data and that is fine. A shared world-space structure is the alternative if
duplication ever matters.

### 3.1 Reading the world

This revives §1 of the original plan, repurposed: the two paths now feed a GPU structure rather than vertex
colours.

- **General blocks** — `Block.GetCollisionBoxes` / `GetSelectionBoxes` returning `Cuboidf[]`, which covers
  every block type (slabs, stairs, fences) with no per-type knowledge.
- **Chiselled blocks** — `BlockEntityMicroBlock.GetVoxelMaterialAt` / `VoxelCuboids`, the exact answer, and
  the case that matters most. `VSSurvivalMod` is already a dependency, so this costs nothing new.

Still true from the original plan: **the resolutions line up exactly** — a chisel voxel is 1/16 of a block
and a Layout scale-1 voxel is 1/16 of a block, so at scale 1 the correspondence is 1:1 with no approximation.

### 3.2 Keeping it current

`IClientEventAPI.BlockChanged` exists client-side — *"When a player block has been modified"* — verified
against the shipped API. It gives the position, and it fires for other players' changes too, since the
client applies them.

**RESOLVED 2026-07-26 — chisel edits DO fire it.** This was the plan's largest open question and it is
settled by tracing the call sites in the shipped assemblies (IL scan of `VintagestoryLib.dll` and
`VSSurvivalMod.dll`). The chain:

1. A chisel stroke reaches `BlockEntityChisel.UpdateVoxel`, which edits `VoxelCuboids` and calls
   `BlockEntity.MarkDirty`.
2. `MarkDirty` syncs the block entity to clients.
3. On the client, `GeneralPacketHandler.HandleBlockEntities` calls `ClientEventManager.TriggerBlockChanged`.

`ClientWorldMap.MarkBlockDirty` is a second, independent route to the same trigger. The complete set of fire
sites is `OnPlayerTryDestroyBlock`, `OnPlayerTryPlace`, `MarkBlockDirty`, `MarkBlockModified`,
`HandleBlockEntities`, `HandleExchangeBlock`, and `HandleSetBlock` — so **block placement, block breaking,
and sub-block chisel edits all arrive through the same one event.** No second mechanism is needed.

Also confirmed on the same pass: `BlockEntityChisel` derives from `BlockEntityMicroBlock` (it reaches
`VoxelCuboids` and `GetVoxelMaterialAt` as inherited members), so a single
`GetBlockEntity<BlockEntityMicroBlock>` lookup catches chiselled blocks — §3.1 is correct as written.

**✅ CONFIRMED IN PLAY 2026-07-26.** The human ran `/layout blockevents on` (v0.3.75) and reported that
**chiselling a block does fire the event.** The static trace above is no longer the basis for this — it is
an observation. Stage 3 can be designed on it.

The command remains available for re-checking (`/layout blockevents on|off`): it prints one line per event
with the position, the old and current block codes, and whether that position carries a
`BlockEntityMicroBlock`, then reports totals when switched off.

**Consequence — the event is noisier than it looks.** Because it fires on *any* block-entity sync, it also
fires for chests, furnaces, barrels and anything else with a ticking or updating block entity near the
player. The handler must therefore be cheap and reject fast: a bounding-box test against loaded guides,
before any world read. Only guides whose bounding box contains the changed position (expanded by one voxel)
need touching, which is usually zero or one guide.

---

## 4. What is dead from the first draft

| Original | Status |
|---|---|
| §1 feasibility, Paths A and B | **Alive**, repurposed — now feeds the GPU structure (§3.1) |
| §2 outset / inset sign flip | **Shipped** as v0.3.70–v0.3.71; not part of this plan |
| §2 "buried voxels need not be shown" | **Dead** — the human needs exactly that, continuously |
| §4 one-shot scan, `/layout scan` | **Dead** (§0.3) |
| §4b invalidation options | **Dead** — nothing to invalidate; no mesh carries the state |
| §5 any / majority / all vote | **Superseded** by per-fragment sampling (§2.1) |
| §6 staged delivery | **Replaced** by §5 below |
| §7.1 "green is taken" | **Alive** — a colour must still be chosen |
| §7.2 frame interaction | **Alive** — outline plus tint plus outset may be a lot at once |
| §7.3 collision vs visible material | **Alive** (§7.2 below) |
| §7.4 chunk loading | **Alive** — unloaded chunks must not read as occupied |
| §7.5 client-only mode | **Alive**, expected free |
| §7.6 client-side visual only | **Alive and still holds** — no DataVersion, protocol, or save change |

---

## 5. Staged delivery

**Stage 1 — the structure, off by default. ✅ DONE and VERIFIED (v0.3.76–v0.3.77).** `BlockOccupancy` reads
the world at 1/16 resolution, classifying each block empty / solid / partial and building a 4096-bit brick
only for partial ones. `/layout occupancy` inspects the block you are looking at and prints the 1/16 slice
at your aim height; `/layout occupancyscan <radius>` reports the solid/empty/partial split, timing and
resident size for a cube around you.

**Human verdict 2026-07-26: accurate results for a chiselled block.** Collision boxes are therefore
confirmed as a sound source at sub-block resolution, and plan risk §7.2 is much reduced — Path B
(`GetVoxelMaterialAt`) is not needed. Stage 2 can be built on this.

Still unmeasured: the resident size and scan timing in a real, heavily built area, which is what
`/layout occupancyscan` exists to report.

**Stage 2 — the shader lookup and the toggle.** Upload the structure, add the fragment lookup and the tint,
and wire the GUI toggle (§6). At this point the feature is usable but static — it reflects the world as of
when the guide was built.

**Stage 2 — the colour. ✅ DONE and CONFIRMED (v0.3.79–v0.3.80).** Delivered by BAKING the colour into the
mesh rather than the shader lookup this plan originally specified. The plan's own success criterion for this
stage was "usable but static — reflects the world as of when the guide was built", which baking achieves
through code paths that already work; the shader route's value is cheap *updates*, which is stage 3's
problem. Its feasibility was confirmed rather than assumed on the way past — `LoadOrUpdateTextureFromBgra`
carries the data and `IShaderProgram.BindTexture2D` binds it — so it remains available.

Body voxels holding material are drawn CYAN (yellow's complement, clear of every existing role colour);
anchors, apex and locked points keep theirs, and alpha is untouched so the player's opacity setting still
governs transparency. `/layout built on|off|refresh` and a switch on the gear settings page.

**Human verdict 2026-07-26: "the color difference is incredibly helpful."** That is the question the whole
plan existed to answer, and it is answered — after opacity failed at the same job (§0.2).

**Stage 3 — live updates. IN PROGRESS.**

*Stage 3a (v0.3.81):* subscribe to `BlockChanged`, invalidate the changed block, mark only the guides whose
bounds contain it, and rebuild after a 250 ms quiet period. Three filters in cost order — a player-radius
distance compare (the human's suggestion; the cheapest possible rejection, and it stops other players'
chiselling touching your guides), then the guide's cull sphere, then the debounce.

Guides above the streaming threshold are deliberately NOT rebuilt live: they re-stream over seconds rather
than rebuilding in a frame, so a live update would re-grow the whole guide every time the player paused.
They mark dirty and wait for the manual refresh, which now reports how many are waiting.

*Stage 3b (v0.3.83):* per-batch rebuild, lifting that ceiling. A large guide gets a batch context built
once on a background thread — the X-sorted voxel list, its cross-batch occupancy set, the mesh options, and
the voxel range behind each uploaded mesh. After that, a block change re-meshes only the batches whose X
span it overlaps, roughly 1/128 of the work. One context is held at a time (you work on one guide at a
time) and guides over 3M voxels are excluded, since the retained voxel list is the bulk of the memory.

**Why this is not the Session 25-26 spatial-partitioning experiment.** Guide meshes draw in list order —
primary, then `Auxiliary` in sequence — and every batch is a contiguous run of ONE sorted list. The
concatenation is therefore the same primitive sequence wherever the boundaries fall, so moving a boundary
reorders nothing. That is what allows this to choose its own batching rather than having to reproduce the
streaming pipeline's, and it is why CLAUDE.md's regrouping hazard is not engaged.

**Why per-batch rather than the shader lookup** (the human asked which performs better, 2026-07-26):
updates happen about twice a second; fragments happen tens of millions of times a second on a
self-overlapping translucent shell. The shader route makes updates near-free by adding two dependent
texture fetches to every fragment, permanently, whether or not anything changed — optimising the cheap side
by taxing the expensive one. Per-batch adds NOTHING to the rendering path. It is also lower risk (no byte
order, gamma, or GLSL unknowns) and needs no second implementation for when the custom shader is off.
The shader route stays recorded in §2 as the escape hatch if the workload ever becomes update-dominated.

**Also corrected at v0.3.82:** the settle delay was 250 ms, sized for a "chisel burst" that does not exist —
the human notes chiselling is rate-limited to about twice a second, so strokes arrive far apart and the
delay only added latency. Now 80 ms, which still absorbs the several events ONE stroke produces when
neighbouring microblocks re-mark themselves.

**Stage 4 — polish. Partly done.** The colour is settled (cyan, human-approved in play) and no strength
control proved necessary — it is a clean hue swap with alpha untouched. **The coarse-scale read is still
untested:** at scale 1 a guide voxel is a chisel voxel and the mapping is exact, but above that the voxel's
centre cell is sampled and nobody has looked at how it reads. See §7.

---

## 6. The toggle (human-requested 2026-07-26)

**The player can turn the recolour on and off from the GUI.** This is a requirement, not a nicety, and it
shapes Stage 2.

- **Where:** the settings page behind the title-bar gear (v0.3.73–v0.3.74), alongside the opacity slider.
  That page exists for exactly this kind of client display setting.
- **Persisted** in `layout-client.json` next to the opacity values, so it survives restarts.
- **Client-side only.** No protocol, no server involvement, no per-guide state — a global client preference.
- **A command for parity** (`/layout occupancy on|off`) is nearly free and matches how `/layout voxelframe`,
  `/layout shader`, and `/layout inset` already work. Worth adding.

**This is a strong argument for the §2 design.** Under a baked-colour approach, flipping the toggle would
mean rebuilding every guide — seconds of restreaming on a large one, every time the player changes their
mind. Under the shader lookup it is a uniform flip: **instant, no rebuild, no mesh touched.**

**Implementation shape:** copy the voxel frame's disable pattern exactly — an `layoutOccupancyStrengthIn`
uniform where `<= 0.0` returns early from the lookup, so the feature costs nothing per fragment when off
(`guide.fsh` already does this with `layoutVoxelSizeIn` and `layoutFrameStrengthIn`).

**When off, do not build or maintain the structure at all** — skip the world reads, skip the upload, and do
not subscribe to `BlockChanged`. Turning it on builds the structure for currently loaded guides. A player who
never enables it pays nothing beyond one boolean check.

---

## 7. Risks and open questions

1. ~~**Does chiselling fire `BlockChanged`?**~~ **CLOSED — yes, confirmed in play 2026-07-26** with
   `/layout blockevents` (v0.3.75), on top of the call-site trace in §3.2. Placement, breaking, and chisel
   edits all arrive through the one event, so Stage 3 needs no second mechanism. **The residual risk
   moved:** the event also fires for every other block-entity update nearby (chests, furnaces, anything
   ticking), so the handler must reject out-of-bounds positions before doing any work. That half has not
   been measured yet — the same command reports it if run in a busy area.
2. **`GetCollisionBoxes` is not the same as "visible material".** Some blocks have collision without
   geometry or the reverse. Check against the blocks people actually build with before treating collision
   shape as ground truth. The chiselled path (§3.1) is exact and covers the case that matters most.
3. **Unloaded chunks must not read as occupied.** The same trap the retired solidity probe had. Here the
   conservative direction is the opposite of the old one: an unknown cell should read *empty*, because a
   spurious "built" mark is a lie about the player's own work, whereas a missing one merely fails to help.
4. **Colour choice.** Green is the apex/primary marker, so the built colour must not be confusable with it,
   and it must read at 50% alpha against arbitrary stone. The toggle lowers the stakes — a colour that does
   not work can be switched off — but it does not remove the problem.
5. **Visual load.** Voxel outlines, the v0.3.70 outset, and now a tint all act on the same surfaces. Three
   signals at once may be more than needed. The toggle helps here too.
6. **GPU memory on immense guides.** §3's numbers are for a 40-block cube. A 100-block guide's level 1 alone
   is around 2 MB — still fine, but the growth is worth watching, and a guide's structure should be released
   when the guide is deleted or unloaded.
7. **Client-only mode.** Private guides on vanilla servers must behave identically. Occupancy is a purely
   client-side read, so this should be free — confirm it.
8. **No save or protocol change should be needed at any stage.** If a design step seems to require one, that
   is a signal the design has gone wrong.
