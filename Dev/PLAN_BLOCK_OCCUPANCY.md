# PLAN — Block-occupancy overlay ("what to chisel, what to keep")

> **Status:** proposed, not started. Written 2026-07-25. Feasibility **verified by reflection against the
> shipped assemblies** before any design work — see §1, which was the question that could have killed it.
>
> **Read §4 before §4b.** This plan was drafted twice and simplified twice, both times by the human
> correcting an assumption of mine. It is now a **one-shot scan**, not a live overlay: fill the area, scan
> once, chisel until marked voxels appear, stop. Roughly two thirds of the original design — the buried-voxel
> x-ray and the whole automatic-invalidation stage — turned out to be solving problems the real workflow
> does not have. Build the small version.

---

## 1. Feasibility — CONFIRMED

The idea needs sub-block material data on the client. The human's reasoning that it must exist — *you can
crawl through sub-block openings and water flows into sub-block spaces* — is correct, and that data is
exposed through two independent mechanisms.

### Path A — general, core API, no new dependency

```
Block.GetCollisionBoxes(IBlockAccessor, BlockPos) -> Cuboidf[]     // block-local, 0..1
Block.GetSelectionBoxes(IBlockAccessor, BlockPos) -> Cuboidf[]
Cuboidf.Contains(double, double, double) -> bool
Cuboidf.ContainsOrTouches(...)
```

This is exactly the mechanism behind crawling through a chiselled gap: the collision shape *is* the
sub-block form. It works for **every** block type — slabs, stairs, fences, ladders, chiselled blocks — with
no knowledge of any of them. A voxel-centre point test against the returned cuboids answers "is there
material here" at arbitrary resolution.

### Path B — exact, for chiselled blocks specifically

```
BlockEntityMicroBlock.GetVoxelMaterialAt(Vec3i) -> int    // material id per 1/16 voxel
BlockEntityMicroBlock.MaterialIds               -> int[]
BlockEntityMicroBlock.VoxelCuboids              -> List<uint>
```

Reached via `IBlockAccessor.GetBlockEntity<T>(BlockPos)`. **`VSSurvivalMod` is already a Layout dependency**
(`IContainedMeshSource`, since Session 15), so this costs nothing new.

**The resolutions line up exactly.** A chisel voxel is 1/16 of a block; a Layout scale-1 voxel is 1/16 of a
block. At scale 1 the correspondence is 1:1 with no approximation anywhere.

**Recommended:** Path A as the general answer, with Path B as a precise fast path when the block entity is a
micro-block. Path A alone is enough to ship; Path B removes any doubt about half-chiselled blocks, which are
the exact case where the feature earns its keep.

---

## 2. The actual workflow — and why it makes this cheap

**Corrected after the first draft misread the intent.** The first version assumed occupancy meant "material
in the way, chisel it out". It is the opposite, and the real workflow is simpler:

1. Place a guide. It is a statement of **what should exist**.
2. **Over-fill** the whole area with solid blocks, burying the guide.
3. **Chisel back down** until you reach the guide surface.
4. A guide voxel that is now **filled with material** is *correct* — stop chiselling there.

So the two states are:

| Guide voxel's own cell | Meaning | Read |
|---|---|---|
| empty | planned, not yet realised | the normal palette (yellow etc.) |
| solid | planned **and built** — stop | the "done" colour, slightly **outset** |

There is no third "material to remove" state, and no separate "material to add" state — unbuilt *is* the
default appearance. That simplification removes most of the design surface the first draft worried about.

### The rendering problem dissolves

The first draft's main concern was that a filled guide voxel sits inside an opaque block, fails the depth
test, and cannot be shown — needing an x-ray pass to reveal buried material.

**Buried voxels do not need to be shown.** The player only cares about the voxel they have just exposed by
chiselling, which is by definition at the material surface and therefore visible. Everything still buried is
work not yet done and can stay hidden. No x-ray pass, no second draw state, no interaction with
order-dependent translucency.

### The outset is the inset with the opposite sign

This is the neat part. Today, a guide face flush against a block gets pulled *away* from it by
`BlockPlaneInset` (v0.3.66) so it does not z-fight. A filled guide voxel has exactly the same geometry
problem in reverse: its face is coplanar with the chiselled surface, which is precisely the z-fighting the
human currently reads as "there might be a guide voxel in here somewhere".

Pushing that face *outward* instead of inward makes it sit proudly in the air by a hair — unambiguous, no
fighting, and it reuses the per-face offset machinery already in `GuideMeshBuilder`. One sign flip chosen
per voxel by whether its own cell is solid.

### The probe is nearly free

The renderer already calls a solidity probe per exposed face for the z-fight inset. This needs one more
question per voxel — **is my own cell solid** — from the same accessor, with the same conservative
unloaded-chunk handling and the same `_deferredSolidity` re-probe queue added in v0.3.69.

Path A (`GetCollisionBoxes`) covers every block type; Path B (`GetVoxelMaterialAt`) is the exact answer for
chiselled blocks, which is the case that matters most here since chiselling is the whole point.

---

## 4. A one-shot scan is enough — there is no live overlay to maintain

**This supersedes the "keeping it current" section below, which is retained only as a record of a problem
that turned out not to exist.**

The workflow does not need continuous updating, because **the scan result does not change while the player
chisels**. Fill the area, scan once, and every guide voxel is inside material — all marked. The material
being removed from then on is *excess*, which is not guide voxels; the guide voxels stay filled right up to
the moment the player would be cutting too deep. Nothing invalidates.

That deletes the entire hardest part of this plan: no block-change subscriptions, no debounced dirty
regions, no bounded re-probe lane running in the background, no stutter while chiselling.

**Rescanning at the end is a verification pass.** Any guide voxel that comes back *unfilled* is somewhere
the player cut too deep or never filled at all. That is a correctness check the mod cannot currently offer
in any form, and it falls out of the same one-shot command for free.

**Two consequences worth designing around:**

- **A stale mark after over-chiselling is acceptable**, and is the mechanism behind the verification pass
  above. Do not treat it as a bug to engineer away.
- **The scan is expensive once**, not cheap continuously: an 8M-voxel guide means 8M probes plus a full mesh
  rebuild. That is fine for a deliberate command, but it must run through the existing bounded
  materialization lane rather than blocking the main thread. v0.3.58's scaffold-and-stream path already
  handles a rebuild of that size gracefully and should be reused as-is.

### Note on which half does the work

If the player fills everything before scanning, **every** guide voxel is marked, so the colour distinguishes
nothing at that moment and the **outset is doing all of the real work** — it is what removes the z-fighting
and makes "stop here" unambiguous. The colour earns its place in the other two cases: partial fills (built
vs still to do) and the verification pass. Worth knowing that if the new colour turns out to clash with the
existing palette, dropping it and keeping the outset alone still delivers most of the value.

---

## 4b. Superseded — the invalidation problem (kept as a record)

Every existing `VoxelRenderType` is a property of the **guide** — anchors, locks, apex, divisions. They
change only when the guide changes. This one is a property of the **world**, which changes with every block
placed or chiselled. That is a new kind of dependency for this renderer and it is where the design effort
belongs.

Constraints already established elsewhere in this project apply directly:

- **Never probe a whole large guide synchronously.** An 8M-voxel guide cannot be re-probed on a block
  change, or on a timer, or at all in one go. The bounded materialization lane exists for exactly this class
  of work and should be reused.
- **The mesh is the cache.** Occupancy is baked into vertex colours at build time, so a change means
  rebuilding affected geometry — not re-colouring in place.
- **Rebuild granularity should follow the existing batches.** The final clean mesh is already partitioned
  into ~128 batches; rebuilding one batch is ~1/128 of the cost of rebuilding the guide.

Invalidation options, cheapest first:

1. **Manual refresh** — `/layout scan` or a tool action. Trivially correct, zero background cost, and
   perfectly usable: you chisel for a while, then refresh. **Start here.**
2. **Debounced dirty regions** — subscribe to block-change events, mark the affected batch, rebuild after a
   short quiet period. The natural second step once the feature proves itself.
3. **Live per-change updates** — almost certainly not worth it, and the failure mode is a stutter every time
   the player swings a chisel, which is precisely when they least want one.

---

## 5. Scale behaviour

At scale 1 the mapping is exact. At coarser scales one guide voxel spans 2³, 4³, 8³ or 16³ world cells and
needs a rule:

- **any** — highlight if any world cell is occupied (conservative; a guide voxel touching a single chisel
  voxel lights up)
- **majority** — highlight if most are occupied (reads calmly, hides small obstructions)
- **all** — highlight only if fully solid (misses almost everything useful)

Recommend **any** at first, since a partially obstructed voxel is still a voxel you must act on, and revisit
if it proves noisy at scale 16. Note the probe cost multiplies by the same factor: a scale-16 guide voxel is
4096 world cells, so sample rather than exhaust — the cuboid test in Path A answers a whole region at once
and should be preferred over per-cell iteration at coarse scales.

---

## 6. Staged delivery

**Stage 1 — the whole idea, manual refresh.** One new `VoxelRenderType` ("built"), its colour, the outward
face offset, Path A own-cell probing, `/layout scan` to refresh, and a client config for the colour and an
on/off. No automatic invalidation. This is enough to answer the only question that matters: *does it
actually help you chisel?*

**Stage 2 — precision.** Path B fast path via `GetVoxelMaterialAt` for micro-blocks, so a half-chiselled
block reports per-voxel truth rather than a whole-block approximation. Purely additive.

**Stage 3 — DROPPED.** Automatic invalidation was the largest and riskiest part of this plan and it is not
needed; see §4. Do not build it speculatively. Reopen only if real use shows the one-shot scan is genuinely
insufficient — and note that "the mark went stale after I over-chiselled" is not that evidence, since that
is the verification pass working as intended.

---

## 7. Risks and open questions

1. **Green is already taken.** `VoxelRenderType.Primary` — the apex/primary control point — is green today.
   Only one new hue is needed rather than two, but it must not be confusable with the apex marker, and it
   must read at 50% alpha against arbitrary stone. Reusing green exactly would make an apex marker
   indistinguishable from built material.
2. **The frame interaction.** v0.3.62's voxel outline already marks cell boundaries. An outset *and* a
   colour change *and* a frame may be more signal than needed at once — though here the outset has a second
   job beyond legibility (killing the z-fight against the chiselled surface), so it is likely to stay.
3. **`GetCollisionBoxes` is not the same as "visible material".** Some blocks have no collision but do have
   geometry (and vice versa). Worth checking against the blocks people actually chisel before treating
   collision shape as ground truth.
4. **Chunk loading.** The same trap as the z-fight probe: unloaded chunks must not be reported as occupied.
   The `_deferredSolidity` re-probe queue added in v0.3.69 is the pattern to reuse.
5. **Client-only mode.** Private guides on vanilla servers must behave identically; occupancy is a purely
   client-side read, so this should be free, but confirm it.
6. **This is a client-side visual only.** No DataVersion, protocol, or save change should be needed at any
   stage. If a design step seems to require one, that is a signal the design has gone wrong.
