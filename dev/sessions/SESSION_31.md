# SESSION 31 — v0.3.90–v0.4.0: Transform (rotate, copy, mirror)

**Checkpoint:** Layout **v0.4.0** built and packaged on the `beta` branch. **DataVersion remains 12**;
**wire protocol 17 → 19** (two new packets). Packages: `Layout0.3.90.zip` … `Layout0.4.0.zip`.
**81 source files** (two added: `RotateGuideCommand.cs`, `TransformGuideCommand.cs`).

Session 30 delivered Move. This session finished the category around it: **F12 rotate**, **F8 copy**,
**F7 mirror**, and the rename of the mode from Move to **Transform** that houses all four. The version
bump to 0.4.0 is the human's call, marking the milestone.

Playtest verdicts along the way: rotate *"was good"*, the pad *"tested very well"*, the final pass
*"tested well"*.

---

## 1. The arc, in order

| Version | What |
|---|---|
| 0.3.90 | Rotate + the Move→Transform rename; new bracket-and-dot mode glyph |
| 0.3.91 | Centre dot to 4 units; rotate glyphs redrawn (they were malformed) |
| 0.3.92 | Development diagnostic commands hidden behind a config flag |
| 0.3.93 | The state-driven pad: Move / Copy / Mirror toggles, span step, copy+rotate |
| 0.3.94 | Span button removed — span became the default of the step row |
| 0.3.95 | Copy runs march outward; span made reachable from plain Move |
| 0.4.0 | Milestone version |

---

## 2. Why the mode is called Transform

Move, Copy, Mirror and Rotate share one boundary: they act on a **whole guide as a single object without
changing its shape**. Create owns geometry, Edit owns settings, Transform owns the object itself.

"Move" had been doing double duty as both the category and one action. The category took the new name and
the panel's buttons kept the plain verbs, so nothing became harder to find. The human chose Transform over
the recommended "Arrange" on the grounds that a player precise enough to want this is technically inclined
enough to read it as clear rather than as jargon. "Manipulate" was rejected as the longest, the most
clinical, and the one that draws the fuzziest line against Create.

`ToolMode` is client-only and never crosses wire or disk, so the rename cost nothing but its call sites.

---

## 3. The design that emerged: a state-driven pad

The first plan was separate Copy and Mirror buttons on their own row. **The human proposed something
better:** make Copy and Mirror *modes* that change what the direction pad does. A bare Copy button always
leaves "where does the copy go?" unanswered; routing it through the pad answers it with a control that
already exists.

| Move | Copy | Mirror | A direction button does |
|---|---|---|---|
| ✓ | | | translate one step |
| ✓ | | ✓ | translate one step and flip |
| | ✓ | | duplicate, offset one step |
| | ✓ | ✓ | duplicate, offset one step, flipped |
| | | ✓ | flip in place |

Tightened by one rule: **Move and Copy are mutually exclusive** (Move+Copy is just Copy, since the copy is
what lands at the offset), **Mirror is independent**, and **at least one must be lit** — so the pad is never
in a state where it does nothing.

**One combination was missing from the proposal and was added: Copy + Rotate**, on the pad's corners — a
duplicate turned a quarter, which is the four-corner-towers gesture in one click.

### Why mirror needs only one button

Quarter turns about two axes reach all 24 axis-aligned orientations. A reflection is the one thing no
rotation can produce — but adding **any single** reflection to that set generates all 48. So three per-axis
mirror buttons would only add new ways to reach results one button plus the rotate buttons already reach.
Mirroring about the guide's **own centre plane** also leaves it where it stands, so it composes with move
and rotate instead of displacing the guide as a side effect.

Consequence worth expecting: in Mirror-only mode, left and right are the same mirror, as are away/toward
and up/down. The six buttons give three distinct results, in mirrored pairs. In the compound states all six
stay distinct because the translation still has a direction.

What mirror is actually for is narrower than it sounds: on a sphere, box, cylinder, dome, regular polygon,
rectangle, circle or ellipse it is either a no-op or the same as a rotation. Its value is concentrated in
**Free-Shapes, scalene/right triangles, and anything hand-sculpted asymmetrically**.

### Span

Copy's real use is the symmetric build — do the left wing, produce the right beside it. For that the copy
must land exactly one guide-width away, which no voxel-count step will be. **Span** steps by the guide's own
extent along the pressed axis, rounded up to a whole number of its voxels so the copy sits flush.

It shipped as a button (0.3.93), then the human replaced it with better behaviour (0.3.94): span is the
**default** of the step row for actions that land things flush, no tile is lit, the label reads "Span", any
step tile overrides it, and clicking the lit tile hands it back. One fewer control and the common case is
automatic.

Then (0.3.95) span was made **available** everywhere something travels while still only **defaulting** on
where flush placement is the point — plain Move keeps its one-voxel nudge, since that is the case the mode
was built for, but can reach span with one click. The two concepts are separate properties
(`SpanStepAvailable` vs `SpanStepDefault`) for exactly that reason.

### Copy runs

Pressing the same copy direction repeatedly lays a **line** — one span out, then two, then three — rather
than stacking every duplicate in one place. The count is kept client-side against (guide, direction) rather
than by re-selecting each new copy, so the original stays selected and stays what everything is measured
from, and the gesture needs no round trip to learn the new guide's id. Any change of guide, direction,
distance or action starts a fresh run.

---

## 4. What made rotate possible, and mirror cheap

**Checked before writing any rotate code:** a volume's rise direction is `BaseNormal(û, ShapePlaneAxis)`
with its **sign taken from which side the apex control point sits on** (`DomeShape.TryGetFrame`). So
rotating the apex carries the facing automatically and the canonical sign-flip is absorbed. That is what
made domes, cones and prisms turn correctly with no per-shape work — the thing `TODO.md` had predicted would
be the hard part.

Rotate therefore needed: rotate the points and the spring-back snapshot together, remap `ShapePlaneAxis`
(a quarter turn maps the axis set onto itself), re-derive the Surface plane, re-adopt, recount, claim-check,
roll back.

**Mirror then came almost free, and the TODO's "harder than F6, expect per-shape work" estimate — written
before rotate existed — was simply wrong by then.** An axis-aligned reflection maps every world axis onto
*itself*, so `ShapePlaneAxis` and a Surface plane's flattened axis are both unchanged; only the plane's
offset can move. Constraints survive too (a right triangle reflects to a right triangle). `MirrorGuide` is
`RotateGuide` with a different point transform and no axis remap at all.

**Copy needed no geometry work.** `RestoreGuide` was already the adopt-a-whole-record seam used by
client-only push, and it already enforces guide-count caps, the hard ceiling, per-guide and creator-total
voxel caps, and land claims. A copy is `DeepClone()` with a fresh `Guid`, transformed, through that.

**Quarter turns only, on purpose.** At 90 degrees the voxel lattice maps onto itself, so a rotation is as
clean as a translation. Arbitrary angles would leave control points off the lattice and the shell would
resample rather than turn. The pivot is snapped to the guide's own voxel scale for the same reason.

**Unlike a translation, rotation and mirroring do NOT guarantee the voxel count.** The lattice maps cleanly,
but the shape is regenerated from control points and the in-plane frame (`ShapeGeometry.TryGetFrame`)
sign-normalises m̂ toward world up — so a polygon-family guide can come back re-phased with a slightly
different count. Both therefore recount and re-check caps in full, as `Rescale` does. Only `TranslateGuide`
may reuse the cached count.

**Undo stores the pivot.** A transformed shape's bounding centre is not generally where the original's was,
so recomputing it on the way back would drift the guide. `TransformGuideCommand` also reverses in the right
order — translate back, *then* reflect about the stored plane — because a reflection is its own inverse only
while its plane stays put.

---

## 5. Two defects found and fixed

**Malformed rotate glyphs (0.3.91).** Two real faults, not just bad taste. The arrowhead was built from
hand-picked numbers with mismatched legs — one ran a full unit back, the other 15% of one — at a guessed
rotation, so it was lopsided by construction; it now computes the true tangent at the sweep's end and draws
a symmetric chevron mirrored about it. And the ring was stroked as an arc under a non-uniform `Scale`, which
squashes the pen with the path, so the flat turntable ellipse pinched thin where it curved hardest; it is
now walked as a polyline in pixel space. A sense error surfaced in the same pass: Cairo's y runs downward,
so increasing the sweep angle draws *clockwise*, and the mirror flag had been backwards.

**Rotation senses were backwards on the first write, caught by hand before shipping.** A positive right-hand
turn about +Y sends east→north, which is *anticlockwise* from above; and a positive turn about the forward
axis carries the guide's top toward the player's *right*, not their left. Both corrected in 0.3.90 before
packaging.

---

## 6. Development commands hidden (0.3.92)

`renderstats`, `weld`, `occupancy`, `occupancyscan` and `blockevents` report internals and are not things a
player is meant to operate; `blockevents` actively spams chat. They now register only when
**`"diagnosticCommands": true`** is set in `layout-client.json`.

**Hidden rather than deleted deliberately:** `SESSION_29.md` §7 leaves verification items open that only
these commands can perform (measuring `BlockChanged` noise in a busy base, sizing the occupancy cache).
Deleting them would have closed off that work. The flag is noted on those TODO items at the point of need.

Everything a player legitimately tunes — `built`, `inset`, `voxelframe`, `shaderbrightness`, `shader`,
`on`/`off`, `dispel`, `who` — is unaffected. `shader` was kept deliberately: it reads like a diagnostic but
is the fallback if the custom shader misbehaves on some hardware. All fifteen server subcommands are admin
moderation or genuine player commands and were left alone.

---

## 7. Flagged for review

1. **A refused copy still advances the run counter**, so the next one lands two spans out with a gap.
   Refusal is an error state (chalk, cap, claim) and changing direction resets it, but it is not
   self-healing.
2. ~~`OnGuideAddedOrUpdated` starts the shell materialization before reaching `RebuildGuide`, stranding a
   stale scaffold.~~ **FIXED GENERALLY in v0.4.1** — see §9.
3. **Polygon-family re-phasing under rotate and mirror is unverified.** The mechanism is understood (see §4)
   and the count is recalculated correctly; what is unknown is whether the visible result surprises anyone.
4. **The wireframe floor band keys on the lowest point of the shape's defining curve** — right for a dome,
   cylinder and prism; unverified on sphere and box.
5. ~~Mirror on a Free-Shape is the least tested.~~ **VERIFIED IN PLAY (2026-07-27):** the human tested
   Free-Shape mirroring extensively and reported no problems. That is the case mirror exists for — an
   irregular hand-drawn polyline is the shape a reflection cannot be faked on with a rotation — so this
   closes the main risk the feature carried.
6. **T1 and T2 remain open** in `TODO.md`: CTRL surface-snap on free-move (contact rule decided, not built)
   and the settings-page formatting plus hover text.

---

## 9. v0.4.1 — the stale-scaffold defect, fixed generally

Session 30 found that `OnGuideAddedOrUpdated` calls `TryStartSettledShellMaterialization` **before** it ever
reaches `RebuildGuide`, and that call succeeds off `mesh.RenderedWireframe` — "is a wireframe showing?" —
which is true whenever the guide is already mid-stream from an earlier change. It then starts the shell for
the NEW state and returns, and `RebuildGuide` is the only thing that re-uploads the scaffold. Result: the
shell streams at the new pose while the wireframe stays drawn at the old one.

Session 30 fixed only the Transform path (a held guide goes straight to the rebuild) and left the general
case open, because reordering that handler changes which branch every guide update takes — including the
immense-sculpt and pending-placement branches above it — and the renderer carries the "protect the v0.3.42
baseline" rule.

**The contained fix, found on re-reading:** the renderer already fingerprints a guide's exact shape and
position, and a running build records the fingerprint it is building. **The scaffold MESH recorded nothing.**
`GuideMesh.ScaffoldFingerprint` now stores the guide state the uploaded wireframe depicts, set wherever
`RebuildSettledScaffold` raises one, and `TryStartSettledShellMaterialization` refuses when it does not match
the guide in hand. A refusal sends the caller down `RebuildGuide`, which raises a fresh scaffold and calls
back in — at which point the fingerprints agree and the stream starts properly.

Nothing was reordered; one precondition became honest. The pre-existing
"already streaming this exact pose → return true" check still comes FIRST, so a redundant full-state
broadcast leaves a running stream alone and does not restart the animation.

Flows checked by hand: first load of a big guide; an edit landing mid-stream (the defect); a redundant
re-broadcast; an edit after the stream completes; the materialization-failure path; a small synchronous
guide; and a persistent Wireframe guide switched to Shell — **which turns out to have had the same defect**
and is fixed by the same change.

## 8. Next

The Transform category is complete: **F6, F7, F8 and F12 are all delivered.** What remains queued is
**F9** (in-game settings panel, which subsumes T2), **F10** (redraw the gear glyph), **F11** (colour-blind
palette), and **T1**. The doc-merge debt — folding `PROJECT_STATUS.md` into `HANDOFF.md` — is also still open.
