# Layout — Outstanding Items (post-Session-8; mod now in real play)

> **Purpose.** The running punch-list. Companion to `ARCHITECTURE.md` v2.5 (the plan) and `PROJECT_STATUS.md`
> (the status). Session 8 resolved the entire Session-7 list (ledger below); the live list is now (a)
> decisions Claude made under the standing decide-and-flag rule that await the human's review, (b) tuning and
> known-cost items, and (c) whatever **real-world play** surfaces — the human has deployed v0.1.0
> (session8-batch5d) to actually-played game worlds.

---

## Resolved in Session 8 (ledger — do not reopen without cause)

| Item | Resolution |
|------|-----------|
| **B1** insert broken | Superseded + rebuilt: left-click body = insert+grab in one gesture (batch 3); root targeting cause fixed separately (see B-S8-1). |
| **B2** White highlight blob | Single-voxel nearest-claim; radius constants deleted (batch 1). |
| **B3** even-span apex | Apex claims 2 voxels at even spans, 0.35-cell tolerance (batch 1). |
| **B4** fill inert | Tier 2 delivered: arch = curve closed by the foot-to-foot chord, region filled; ellipse = disc; caps filled-aware (batch 5). |
| **I1** opacity | All six type alphas client-configurable, defaults lowered (batch 2). |
| **I2** right-click cancel | Universal cancel + `GuideCancelGrabPacket`; insert-born points removed on cancel (batch 3). "Feels very natural" — human. |
| **I3** GUI rework | Tile rows (no dropdowns); initial-shape picker; Favorites **placeholder strip** (feature deferred); permanent main rows + appended Selected-guide section + Deselect (batches 5, 5c). |
| **I4** more shapes | First wave shipped: half-circle, circle, ellipse via the primitives+constraints model with absorb-or-break (batch 5). Remaining catalog: see F1. |
| **S8 playtest** near-anchor grabs failing | Root-caused: chord targeting missed the real curve at the feet; replaced with fingerprint-cached **sampled-curve targeting** (batch 5). |
| **S8 playtest** GUI "locked into edit mode" | Main rows permanent; per-guide section appended + Deselect (5c). |
| **S8 playtest** draft coarsening stuck | Settled guides always render at true scale; coarsening is draft-ghost-only (5c). |
| **S8 playtest** SHIFT on re-grab | Cardinal constraint on anchor drags, referenced to the guide's other anchor (5c). |
| **S8 playtest** Surface→Volumetric mismatch + plane loss | Surface-exit **bake** (undoable) + stored-plane restore (5d). |
| **S8 playtest** soft points still constraining | Offsets made **proportional + frame-relative** (absolute rejected in play): the shape scales/rotates with its structural baseline; only anchors + locked points define it (5d). |
| **S8 playtest** Delete greying too subtle | Ghost fonts (~22% alpha), no lit tiles, input guard (5d). |
| **SELECTION THREAD** | Resolved: clicking a guide in Create mode selects it for the appended GUI section; Deselect clears; Delete mode suppresses it. |

---

## OPEN BUGS

**B-S9-1 — Lock-in-place still imprecise and still deforms the guide (persists after three fix attempts).**
Symptoms: the voxel targeted by the right-click is frequently not the one locked (a nearby voxel goes red
instead), and placing the lock still visibly shifts/deforms the guide. Fixes attempted so far, each verified
present in code but insufficient in play: (a) near-point body-hit → lock-toggle conversion (s9-b2);
(b) chord-invariant phantom drop, killing the neighbor-height reflection lurch (s9-b3); (c) the slave-regime
flow model (s9-b3). Remaining suspects for next investigation: BodyPos = nearest-on-curve to the ray can land
in a cell ADJACENT to the aimed voxel (fix candidate: ray-vs-voxel-box first-hit picking so the clicked CELL
is authoritative); the phantom's neighbor-X/Z tracking (inherent to vertical feet) when locking near a foot;
and centripetal-CR reparameterisation from the inserted knot (inherent; could be masked by quantising the
render near the lock). Parked at the human's direction ("make note of this bug, but let's move on for now").

---

## Flagged decisions awaiting the human's review

Made under the standing "decide, note for review" rule; each is deliberately cheap to reverse. None block play.

1. **Ellipse body left-click → grab the NEAREST HANDLE** (a parametric ring has nothing to insert; pulling
   the ring stretches it). Reverse = make body clicks no-ops or a different mapping.
2. **Ellipse body right-click → toggle the nearest handle's LOCK** (lock-in-place has no arbitrary point to
   create on this family). Same reversal cost.
3. **New ellipse minor radius = ½ major** (a visibly-elliptical default; circles come from the Circle tile).
4. **The minor handle SLIDES ALONG its axis** — free-space drags project onto the minor axis. Alternative:
   fully free handle that re-derives the axis (looser, likely messier).
5. **Shape is read-only in the Selected-guide section** — a live guide changes shape by absorb-or-break
   grabs, not menus. (A menu-driven "convert to circle" re-constrain op is possible future work — see F3.)
6. **The appended Selected-guide section itself** — per-guide GUI editing kept, but demoted below the
   permanent tool rows with an explicit Deselect. The stronger alternative (removing per-guide GUI editing
   entirely, everything via world clicks) is one method deletion if preferred.
7. **SHIFT re-grab constraint reference = the guide's OTHER ANCHOR** (reproduces the drafting feel).
   Alternative reference: the grab's origin position.
8. **Circle → ellipse is the break floor** — an ellipse does not break further into a free closed spline in
   v1 (terminate-at-ellipse). Free closed splines would be a new primitive later.
9. **Bake cell-side** — baked points land exactly on the plane coordinate → quantise to the positive-side
   cell; on walls whose solid side is positive this can sit one cell into the wall at coarse scales.
   Upgrade path: server-side air-probe mirroring the renderer's `CountSolidProbes`.
10. **Proportional soft flow** — technically already playtest-arbitrated (the human's "use whichever feels
    more natural" + the batch-5d verdict), but the frame details (world-up-projected "up", scale-with-length)
    deserve a stretch/shrink/rotate torture test on an arch with several inserted points.

---

## Known costs / tuning (deliberate, watch in real play)

- **Filled guides recount exactly per drag update** (cells generated each move packet). If big filled discs
  drag sluggishly → add a per-drag count cache. Correctness-first per the standing rule.
- **Settled guides always mesh at full resolution** (the coarsening leak fix). Huge guides pay their real
  cost on every rebuild — watch for hitches while dragging points of 8k+-voxel guides.
- **`PreviewFullResVoxelCap` = 8,000** — now scoped to the draft ghost only; tune if huge drafts stutter.
- **Ghost-greying** — if the installed API's toggle buttons expose an `Enabled` flag, native disabled state
  beats the alpha-ghost approach (`GuideToolGui.AddTileRow` is the seam).
- Carried from Session 7: item transforms; recipe balance; scroll-wheel bindings (parked); stale color
  comments (cosmetic); Surface flatten's eventual move into the shape layer (`TODO(Surface)`).

---

## Future features (specced or sketched, not started)

### F1. Remaining shape catalog (the I4 back half)
Line · triangles (+ right/equilateral/isosceles constraints) · rectangle (+ square constraint) ·
N-gon (suggested). The **closed-polygon (corners) family is the new work** — the open-curve and closed-curve
families now exist and the plumbing (factory, constraints, fill, wire, GUI tiles) is proven. The
**planar-vs-3D-volumes decision is now SETTLED: 3D volumes (spheres, cones…) are in scope LATER** —
current shapes stay planar; keep the per-shape plane in the contracts (already done via `ShapePlaneAxis`).

### F2. Favorites (the I3 sub-feature — placeholder is already in the GUI)
Star-toggle per catalog tile; persists per-player in `layout-client.json`; stores **{type + constraint}**
pairs (the primitives+modifiers model was chosen partly for this). Open sub-questions carried from the
original spec: star hit-target precision inside a tile; max count vs wrap; toggle-only affordance.

### F3. Re-constrain op (idea, unrequested)
The inverse of a break: a menu action to snap a free shape back under a constraint (arch → half-circle,
ellipse → circle) with a best-fit. Natural undo pairing already exists. Park until asked.

---

## Next session — start here

1. **Collect the real-play findings list** and triage (quick fix / design work / tuning backlog).
2. **Walk the Flagged list** above with the human when convenient — ten quick yes/no calls.
3. If the session is feature-shaped rather than fix-shaped: **F1's polygon family** is the natural next
   block (first genuinely new control-point family since the ellipse), with F2 riding the GUI it lands in.

*End of outstanding-items handoff.*
