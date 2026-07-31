# SESSION 30 — v0.3.86–v0.3.89: F6 Move mode

**Checkpoint:** Layout v0.3.89 built and packaged on the `beta` branch. **DataVersion remains 12**;
**wire protocol 16 → 17** (one new packet). Packages: `Layout0.3.86.zip` … `Layout0.3.89.zip`.
79 source files (one added: `Undo/Commands/TranslateGuideCommand.cs`).

Delivered **F6** from the TODO's four-feature queue: a fourth tool mode that slides a whole guide without
touching its shape. Motivating case, in the human's words: a guide sculpted over a long session that turns
out to be one voxel off, where the only previous recovery was to reshape it point by point or start over.

Playtest verdict after the first build: *"Seems to work well."* Three follow-up rounds then fixed a
rendering defect that made the feature look broken on immense guides.

---

## 1. The arc, in order

| Version | What |
|---|---|
| 0.3.86 | Move mode: enum, packet, authority op, undo command, GUI pad, free-move |
| 0.3.87 | 2.5 s materialization hold while moving; precise wireframe floor; panel text removed |
| 0.3.88 | **Fix:** stale wireframe left at the old position; Move controls grey out instead of vanishing |
| 0.3.89 | **Fix:** the precise floor was invisible inside the coarse wire; graduated scale transition |

---

## 2. Design decisions the human made up front

Asked before building, because each changed the work materially:

1. **Input is the GUI, not a gesture.** Select Move, select a guide, then click arrows in the panel. A
   free-move toggle arms a crosshair drag as the alternative. (The alternatives offered were keyboard
   nudge hotkeys and a grab-style drag; both were declined.)
2. **Arrows read from the player's facing**, snapped to the nearest world axis — the top arrow pushes the
   guide away from you. The panel holds the mouse cursor, so the facing physically cannot drift between two
   clicks of the pad. Confirmed in play, including while standing diagonally.
3. **A step is never finer than the guide's own voxel**, multiplied by ×1/×2/×4/×8/×16. Free-move snaps to
   ×1 of the guide's scale.
4. **Locked points travel with the guide.**

---

## 3. What made it cheap

A guide stores no voxels — it is control points plus settings — so a move is "add a delta to every point".
Three existing seams did nearly all the work:

- **`GuideManager.RestoreControlPoints`** is the established wholesale-rewrite seam (re-adopt the shape,
  recalculate phantoms, re-count, re-check claims, roll back on refusal). `TranslateGuide` is that method
  with an offset.
- **Spring-back** (`OnSpringBack` + `SpringBackCommand`) is the template for a whole-guide, undoable,
  networked op with a full-state broadcast. Move copies its wiring almost line for line.
- **The per-mesh model matrix** already positions every guide mesh, so the free-move preview is a change to
  one translation and nothing else — no re-meshing, no re-upload, no regrouping of primitives. An
  8M-voxel guide previews its drag exactly as cheaply as a small one.

**The count cannot change.** `TranslateGuide` *enforces* that the delta is a whole number of the guide's own
voxels and refuses anything finer. Cells quantise to `Floor(world*16/scale)*scale`, so an exact multiple of
the scale remaps every cell one-for-one — which means the cached count is reused instead of rescanning a
behemoth, and no cap check is needed at all. A sub-voxel delta would instead move cells by a whole cell
wherever it crossed a quantise boundary and by nothing elsewhere, deforming the shell. It is refused, not
rounded. The wire carries the delta as **integers in 1/16 units**, so a fractional nudge cannot even be
expressed.

Claims *are* re-checked: the destination is new ground and a move is a placement.

---

## 4. A TODO worry that turned out not to exist

`TODO.md` F6 warned that "Session-20 adjacent locks tie a point to a neighbouring guide. Moving the guide
breaks that relationship", and recommended refusing to move a guide with live locks.

**There is no such relationship.** `IsLocked`/`IsLockMarker` are plain per-control-point flags with no
reference to anything outside their own guide; B-S9-1 was a click-*targeting* defect, not stored data.
Locks therefore travel with the guide and no guide is ever refused a move for having them — which matters,
because a long-sculpted guide is exactly the kind that has locks all over it, and that is the guide this
feature exists for.

One real consequence: `UpdateControlPoints` refuses to touch locked points, so the translate needed its own
seam rather than reusing that one.

`OriginalControlPoints` **does** move with the guide. Translate only the live points and a later spring-back
teleports the guide back to where it used to stand.

---

## 5. The rendering defect, and why it took three rounds

Reported after v0.3.87: on a 2M-voxel guide, *"the actual wireframe does not move, but the streaming
materializing shape DOES move"*.

**Root cause (fixed 0.3.88).** `OnGuideAddedOrUpdated` calls `TryStartSettledShellMaterialization` **before**
it ever reaches `RebuildGuide`. That call succeeds off the existing `RenderedWireframe` flag — true, because
the previous nudge had just raised a scaffold — and returns early **without touching the scaffold mesh**. So
the shell streamed at the new position while the wireframe sat at the old one with its old origin.

The same early return is why the precise floor band never appeared: it lives in the rebuild, and the rebuild
was being skipped entirely. It was not building wrong; it was never building. A guide under a move hold now
goes straight to `RebuildGuide`.

> That early return looks capable of stranding a stale wireframe outside Move mode too, on any edit to a
> guide currently showing a scaffold. **Not fixed** — the Move path was fixed specifically. See §7.

**Second defect (fixed 0.3.89).** With the band finally building, the human reported the wireframe was *"a
full block in volume, but made up of the 1/16 scale voxels"*. Two causes:

1. **The fine voxels were invisible inside the coarse ones.** The band was *added* but the block-sized
   coarse cubes were never *removed*, and guides are order-dependent translucent geometry — the primary mesh
   draws first and wins the depth test. `ShowMovingDraft` has always removed its coarse cells inside the
   precision radius for exactly this reason; the floor band now cuts the same kind of hole.
2. **The voxel outline drew the wrong grid.** `UploadOrReplace` recorded no scale, so the frame fell back to
   the *guide's* scale and painted a 1/16 lattice across full-block cubes — which is precisely what "a full
   block made of 1/16 voxels" looks like. The scaffold now records the scale it was built at;
   `ApplyMeshVoxelFrame` already prefers a mesh's own scale over the group's.

**Then, on request: a graduated transition**, mirroring the dragged-point precision bands. Layers step
through the valid scales — finest at the bottom, one scale coarser per layer — until they meet the
scaffold's own scale, each layer a separate mesh at its own scale, overlapping by half a cell so no wire
goes missing at a seam. The only difference from the draft version is what it measures: height above the
shape's lowest point rather than radius from an aim point, so the precision sits where the guide is being
lined up. Control-point markers are re-claimed per layer at that layer's scale, so a base anchor is drawn
precisely rather than as a block-sized blob.

---

## 6. The 2.5-second hold

A moved immense guide drops to its wireframe and would otherwise start streaming its shell back immediately —
right for a settling guide, wrong for a moving one, because the next nudge throws the part-built shell away.
Each nudge now restarts a 2.5 s hold; the shell rebuilds only once the player has stopped.

Only guides that actually stream are held (`ShouldStreamSettledShell`). A small guide rebuilds in well under
a frame and never shows a wireframe, so holding one would buy nothing and cost an extra rebuild on release.

A committed free-move also keeps its render offset until the authority answers (5 s safety net), so a remote
server's round trip cannot make the guide snap back and then jump forward again.

---

## 7. Flagged for review

1. **The `OnGuideAddedOrUpdated` ordering is still wrong in the general case** (§5). Any edit to a guide
   currently showing a scaffold can leave that scaffold at the stale pose. Fixed for Move only, deliberately:
   the renderer carries an explicit "protect the v0.3.42 baseline" rule and this is cheap to change and
   expensive to get wrong.
2. **The floor band keys on the lowest point of the shape's defining curve.** Correct for a dome, cylinder
   and prism, whose curve is a base ring. **Unverified on sphere and box**, whose curves are not.
3. **Band height is one block, of which only the bottom quarter is true scale.** The dragged-point bands use
   roughly two blocks; this may read as a steeper step-down than the drafting feel it is modelled on.
4. **Settings-page prose was deleted, not relocated.** The Move tiles kept theirs as hover text; the
   settings controls now have no explanation at all. The human's stated intent is a mouse-over box later.
5. **Free-move targeting is suppressed during a drag.** The guide draws offset but its data has not moved,
   so a raycast would report hits where it used to be. Nothing is targetable mid-drag anyway.
6. **Immense moves run the claim check synchronously**, like every other whole-guide op (rescale, wireframe
   toggle). Consistent rather than new, but the off-thread immense lane exists if it hitches.

---

## 8. Next

F6 is complete. The recommended order in `TODO.md` puts **F8 (copy a guide)** next — it is nearly free now
that Move exists, and the two compose: copy, then nudge into place. Then **F9** (settings panel), then
**F7** (mirror/flip), which shares this mode but not its difficulty.
