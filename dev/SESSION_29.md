# SESSION 29 — v0.3.70–v0.3.85: block occupancy, from a wrong plan to a working feature

**Checkpoint:** Layout v0.3.85 is built and packaged on the `beta-shader` branch (unmerged, as v0.3.59+
already were). DataVersion remains **12**, wire protocol remains **16** — nothing here touches either.
Packages: `Layout0.3.70.zip` … `Layout0.3.85.zip`. 78 source files.

This session delivered the feature `PLAN_BLOCK_OCCUPANCY.md` was written for: **guide voxels that already
hold world material are drawn cyan**, updating live as the player builds. Human verdict: *"the color
difference is incredibly helpful."*

Getting there required disproving most of the original plan. Three of its conclusions were wrong, one
cheaper alternative was tried and failed, and two of this session's own designs had to be corrected by the
human. That history is the point of this record — the plan file carries the final design, this carries why.

---

## 1. The arc, in order

| Version | What |
|---|---|
| 0.3.70 | Uniform face **outset**; world-solidity probe deleted entirely |
| 0.3.71 | Outset tuned to **0.0006** (5× smaller than the old inset) |
| 0.3.72–0.3.74 | Settings page + opacity slider (to test opacity as the answer); gear moved into the title bar |
| 0.3.75 | `/layout blockevents` diagnostic |
| 0.3.76–0.3.77 | `BlockOccupancy` — sub-block world reads; `/layout occupancy`, `occupancyscan` |
| 0.3.78 | Optional-argument bug fixed in three shipped commands |
| 0.3.79–0.3.80 | **The colour**, baked into the mesh; rasterisation made range-based |
| 0.3.81–0.3.82 | Live updates via `BlockChanged`; settle delay corrected |
| 0.3.83–0.3.85 | Per-batch rebuild; meshing moved off the render thread; incremental upload |

---

## 2. What the original plan got wrong

### 2a. "A one-shot scan is enough"

The plan's §4 argued no live updating was needed, because the marks do not change while the player chisels.
That assumed the mark is only wanted *while cutting*. It is not — the human needs to see, continuously,
whether material occupies a guide voxel, which is a question asked constantly while filling. `/layout scan`,
the manual-refresh staging, and the whole "nothing invalidates" argument died with it.

### 2b. The offset must not consult the world — and a rebuild would have broken it

The plan had the outset driven by a world probe. **The human caught the flaw:** over-fill the guide, and a
rebuild would see solid neighbours everywhere and flip those faces *inward* — breaking the guide at exactly
the moment it is needed. The offset is now derived from the guide's own voxel set and reads the world not at
all, which also deleted `NeighborSolidProbe`, `TrackedSolidProbe`, `_deferredSolidity` and
`GuideMeshOptions.IsNeighborSolid`, along with the millions of per-face block lookups a large guide used to
make at build time.

Corollary found while building it: face **exposure** was always decided against the guide's own voxel set,
never the world. The human asked for exactly that guarantee; it already held.

### 2c. Opacity was the cheap hypothesis, and it failed

Before committing to colour, a guide-opacity slider shipped (v0.3.72) specifically to test whether a fainter
guide would let material show through. **It does not.** That negative result is what made the colour change
necessary rather than optional, and it cost three small versions to establish.

---

## 3. Investigations that shaped the design

Recorded because each closed a question that would otherwise be re-opened.

**Chisel edits do fire `BlockChanged`.** Traced through the shipped assemblies by IL scan
(`BlockEntityChisel.UpdateVoxel` → `MarkDirty` → `GeneralPacketHandler.HandleBlockEntities` →
`TriggerBlockChanged`), then **confirmed in play** with `/layout blockevents`. Placement, breaking and
chiselling all arrive through one event. Consequence: it also fires for every other nearby block-entity
sync, so the handler must reject cheaply.

**Colour-only mesh updates are supported by the API but blocked by our own welder.** `UpdateMesh` updates
any non-null data, so a colour-only re-upload is real. But `VertexWelder` keys on (x, y, z, **colour**) — a
welded vertex holds one colour, so a voxel turning cyan next to one that stays yellow needs that shared
vertex split. Not fixable without abandoning welding, which is worth 4.00 → 1.083 vertices per quad.

**A correction to a claim made mid-session:** this does *not* mean welding is incompatible with multi-colour
guides. Guides already carry six colours and weld fine; only vertices *on* a colour boundary duplicate. What
welding forecloses is narrowly the *in-place change* of a colour.

**CLAUDE.md's renderer warning does not apply to a shader lookup.** The warning is specifically about
regrouping primitives. A per-fragment colour lookup regroups nothing. This was misapplied once in discussion
and is corrected here.

**A dense occupancy volume is infeasible; a sparse one is cheap.** 1/16 resolution over a guide's bounding
box is ~33 MB for a 40-block cube and ~512 MB for a 100-block one, growing with the cube of size.
**The human's correction — store ranges, not every coordinate** — is the resolution, and it matches how the
game already stores the data (`BlockEntityMicroBlock.VoxelCuboids` is a cuboid list, not a grid).

---

## 4. The design that shipped

**`BlockOccupancy`** (new, `src/Systems/`) answers "is there material in this 1/16 cell". One classification
per world block — empty / solid / partial — with a 4096-bit brick built **only** for partial blocks, so cost
tracks how much has been chiselled rather than the volume. Collision boxes are the source for every block
type: for a chiselled block they *are* the chiselled cuboids, so the answer is exact where it matters and no
per-block-type knowledge is needed. **Human-verified accurate on a chiselled block**, which closed the
plan's risk §7.2 and made the microblock fast path unnecessary.

Unloaded chunks read as **empty**, the opposite of the retired z-fight probe's direction: a spurious "built"
mark lies about the player's own work, a missing one merely fails to help.

**The colour is baked into the mesh**, not looked up in the shader — a deliberate deviation from the plan's
stage 2, taken because the plan's own success criterion for that stage ("reflects the world as of when the
guide was built") is met by baking through code paths that already work, whereas the shader route meant
shipping texture packing, byte-order and gamma assumptions, and a GLSL edit all untested. Its feasibility
was confirmed rather than assumed on the way past (`LoadOrUpdateTextureFromBgra` + `BindTexture2D`).

**Live updates** subscribe to `BlockChanged` and filter in cost order: a player-radius distance compare (the
human's suggestion — the cheapest possible rejection, and it stops other players' chiselling touching your
guides), then the guide's existing cull sphere, then an 80 ms settle.

**Per-batch rebuild.** A guide gets a batch context built once in the background — X-sorted voxel list, the
cross-batch occupancy set, the mesh options, and the voxel range behind each uploaded mesh. A block change
then re-meshes only the batches its X span overlaps.

> **Why that is not the Session 25–26 spatial-partitioning experiment.** Guide meshes draw in list order
> (primary, then `Auxiliary` in sequence) and every batch is a contiguous run of ONE sorted list, so the
> concatenation is the same primitive sequence wherever the boundaries fall. Moving a boundary reorders
> nothing. This is what allowed the batching to be chosen freely instead of reproducing the streaming
> pipeline's — which would have meant instrumenting the most fragile code in the mod.

**Why per-batch and not the shader lookup** (the human asked which performs better): updates happen ~2/s,
fragments tens of millions/s on a self-overlapping translucent shell. The shader route makes the cheap side
free by taxing the expensive side permanently. Per-batch adds nothing to the rendering path, carries no
byte-order/gamma/GLSL risk, and needs no second implementation for when the custom shader is off.

---

## 5. Two designs the human corrected mid-build

**The debounce was sized for a burst that does not exist.** 250 ms, chosen to swallow "a chisel burst" —
but chiselling is rate-limited to roughly twice a second, so strokes arrive far apart and the delay only
added latency. Now 80 ms, which still absorbs the several events *one* stroke produces when neighbouring
microblocks re-mark themselves.

**The stutter: 235 FPS → 45 FPS for a frame, per update (v0.3.83).** Three causes, one dominant:

1. **Meshing ran on the render thread.** `GuideMeshBuilder.Build` is pure CPU work and is already called
   from background threads by materialization. Now meshed on a worker; the render thread does an upload
   and nothing else.
2. **Small guides skipped the batch path entirely** and re-meshed whole, inline. The 100,000-voxel ceiling
   came from a measurement described as "a sub-frame blip" — but sub-frame at 60 FPS is 16 ms, and at
   235 FPS the budget is 4.2 ms. Every guide now uses the batch path.
3. The occupancy cache took a **lock per probe**, hundreds of thousands per rebuild. Now lock-free reads.

**Also corrected:** the initial context build accumulated every batch's mesh data before uploading any —
hundreds of megabytes on a large guide. Now uploaded incrementally with back-pressure (v0.3.85), while the
guide's meshes are still swapped all at once so nothing appears to re-grow.

---

## 6. Other fixes that fell out

**Three shipped commands were silently broken.** `parsers.OptionalFloat`/`OptionalInt` return their default
when the argument is absent, **not null**, so `args[0] is float` always passed. `/layout inset`,
`voxelframe` and `shaderbrightness` all claimed "no argument reports the current value" — a bare
`/layout inset` in fact SET the inset to 0 and saved it. All four optional floats now default to `NaN`,
which nothing parses to, so 0 stays a settable value (v0.3.78). Found because the same trap took out
`/layout occupancy` one version earlier.

**Rasterisation was quadratic in the wrong thing.** Testing all 4096 cell centres against every cuboid cost
4096 × cuboids per block, and a chiselled block carries dozens. Each cuboid now resolves directly to the
cell range it covers — identical semantics, exact for chisel cuboids (v0.3.80).

---

## 7. Flagged for review

1. **`OccupancyBatchVoxelCeiling = 3,000,000` is a judgement call, not a measurement** — the same species
   as `SettledStreamingVoxelThreshold`, whose guessed value caused the v0.3.83 stutter. ~140 MB resident at
   the cap. **Above it the feature silently stops updating**, with nothing said to the player. That gap
   should be closed.
2. **Coarser scales are untested.** At scale 1 a guide voxel is a chisel voxel and the mapping is exact;
   above that the voxel's centre cell is sampled. Defensible, never looked at.
3. **Shared static colour arrays are mutated while background batches build.** `ConfigureOpacities` is
   documented as "called once at client start"; the v0.3.72 opacity slider and this feature both break that.
   Transient and probably invisible, but see TODO **F11**, which would make it worse.
4. **Event noise unmeasured** in a real base (chests, furnaces, anything ticking).
5. **Client-only mode unconfirmed.** Should be free — nothing touches the server — but unverified.

---

## 8. Next

Verification items 2, 4 and 5 above are all five-minute checks in play. Item 1 is a small message.
Beyond that the plan is complete; remaining work is the deferred TODO features **F6–F11**.
