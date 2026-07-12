# SESSION 11 — v0.1.14 → v0.1.27: the backlog, the Free-Shape, the GUI polish, the 3D family, and hardening

> §1–§15 = v0.1.14–0.1.19 (backlog + polish). **§16 = the 3D volume family, v0.1.20–0.1.23** (Sphere, Dome,
> Cylinder, Cone, Box). **§17 = v0.1.24–0.1.27** (client-lifecycle bug fixes, `/layout dispel` admin
> commands, the oversize-guide hard ceiling, and the running-total `long` widening). All on `main`, pushed
> to GitHub through v0.1.27.

> **Scope.** The human confirmed 0.1.13 fully working and asked for all five queued backlog items plus the
> two small tweaks → **0.1.14**. They **playtested it the same session**: everything confirmed except the
> favorites flow, which they redesigned; plus a centered-apex refinement, a brand-new **Free-Shape**, and a
> **Current Shape** indicator → **0.1.15** (§10). They **playtested THAT too** ("This was tested. Good
> work.") and queued ten polish items → **0.1.16** (§13). Everything compiled clean (`0 Warning(s)
> 0 Error(s)`, Debug + Release). Release zips live in **`..\Layout Zips\`** (human-directed, holds the full
> 0.1.10+ history). 0.1.16 is NOT yet playtest-confirmed; checklist in §14.

---

## 1. B-S10-2 FIXED (code-complete): Surface→Volumetric bake now grows into the AIR

The bake in `GuideManager.SetProjection` used to flatten points to the exact plane coordinate, which
quantised into the plane's positive-side cell regardless of which side was solid — on a negative-facing wall
the volume grew INTO the block. Now a server-side solidity probe (`ProbeAirSide`, the mirror of the
renderer's decal-side vote) checks the world half a block out on each side of the plane at every control
point, and the bake nudges every point **half a voxel into the airier side** — exactly where a fresh
Volumetric placement against that face puts its anchors. Ties (free-floating planes, unloaded chunks) fall
back to the old positive-side behaviour, which is harmless there. No wire change; the existing undo snapshot
and full-state broadcast already covered the rewritten points.

## 2. Small tweaks (human-requested)

- **Hover tooltips auto-size.** `AddIconTile` now uses `AddAutoSizeHoverText` (max 260 px) instead of a
  fixed 200 px box — no more empty space around short names.
- **Surface slabs 75% thinner.** `GuideRenderer.SurfaceSlabThicknessWorld` 0.01 → **0.0025**. The
  anti-z-fight `SurfacePlaneInset` (0.004) interaction survives (`t = min(thickness, edge − 2·inset)`), but
  **eyeball it in play at scale 1** per the human's own caution.

## 3. CTRL is the cardinal constraint now; SHIFT does two new things

- **CTRL** (was SHIFT): the level+cardinal snap while drafting a foot, and on anchor re-grabs (referenced to
  the guide's other anchor). One-for-one move; `CtrlHeld()` sits beside `ShiftHeld()` in the controller.
- **SHIFT while drafting = INVERT.** Holding SHIFT live-flips the ghost upside-down; the completing click
  bakes it. Meaningful for the shapes that derive an "up": the arch family (apex/arc born below the chord,
  phantoms above the feet so the curve still departs them vertically — toward its own body) and the
  equilateral triangle (apex mirrored below the base). Symmetric shapes (ellipse/circle, rectangle/square,
  line, polygon) ignore it.
- **SHIFT+left-click a placed guide = SPRING BACK.** The guide snaps back to its **as-placed form**: control
  points AND constraint restored from a snapshot taken at creation (a broken circle springs back to a
  circle; inserted points vanish). One undo step (`SpringBackCommand`); full-exclusivity lock gate applies.
  Guides placed before 0.1.14 have no snapshot and get an in-game error saying so.
- **Prerequisite delivered — every shape defaults "up".** `ShapeGeometry.TryGetFrame`'s in-plane
  perpendicular is now sign-normalised (world-up-biased; deterministic +Z/+X bias when the shape lies flat),
  so click order can no longer flip a fresh triangle. Constrained triangle re-derivations (`DeriveApex`)
  became SIGNED (side-preserving) instead of absolute-value — an inverted triangle stays inverted when its
  base is dragged, and existing guides keep whichever side their apex is already on.

**Persistence trick worth knowing:** the arch family's opening direction is carried entirely by the STORED
geometry (interior points below the anchors ⇒ opens down; for the semicircle, whose spine has no interior
points, the phantoms carry the side — they sit above the feet on an inverted arc). No new field, so it
persists, syncs, and survives every phantom re-derivation for free. `TryGetArcFrame` reads the phantom side
and mirrors the arc.

**Spring-back data:** `GuideData.OriginalControlPoints` + `OriginalConstraint`, stamped in
`GuideData.Create` (after the three-click apex is applied), deep-copied by `DeepClone`, persisted in the
save, **deliberately never wired** (clients only request by id — `GuideSpringBackPacket`). **DataVersion 5 →
6** (default-driven migration: old records load with a null snapshot).

## 4. Triangle → THREE-click placement (anchor · anchor · height)

Free, Right, and Isosceles triangles now place with three clicks: two base anchors, then a third click sets
the apex (the ghost's apex tracks the crosshair between clicks 2 and 3, through the constraint's own
projection — Isosceles slides on the bisector, Right on the perpendicular at the first anchor).
**Equilateral stays two-click** — its apex is fully derived, so a third click would have nothing to decide.

- `DraftManager` grew the second stored point (`PlaceSecondPoint` / `DraftSecond` / `AwaitingApex`), a
  `NeedsApexClick(shape, constraint)` rule, and an apex-aware `TryCompleteDraft`.
- **Right-click now steps the draft BACK one click** instead of always discarding: an awaited apex retracts
  to "base end not placed"; the next right-click (or any two-click draft) discards, exactly as before. This
  generalises the old behaviour — for two-click drafts nothing changed.
- The apex crosses the wire as an additive `Apex` field on the create request; the server applies it via
  `MoveControlPoint(2, …)` **before** building the record, so the spring-back snapshot captures the true
  placed form. Mid-draft shape switches drop a stored base point when the new shape isn't three-click.
- HUD draft measurement is stage-aware (measures the real triangle while the apex is being aimed).

## 5. Polygon (N-gon) — the 12th catalog entry

`PolygonShape`: a regular N-gon (**3–24 sides**, default 6) inscribed in a circle, all geometry derived per
query from just the two stored anchors + N. **Gesture:** the first click is a VERTEX; the second is the
perimeter point diametrically opposite (a vertex for even N, the far edge's midpoint for odd N) — so the
shape exactly spans the two clicks, both anchors sit ON the outline, and "which way it points" is simply
where you put the first click. Circumradius r = |AB| / (1 + cos(π/N)) for odd N, |AB|/2 for even. Grabbing
either anchor absorbs as resize/rotate (the ellipse pattern); body clicks map to the nearest anchor via the
existing non-arch policy — zero controller changes needed. Fill = centroid fan. N=3 degenerates to a correct
equilateral triangle, N=4 to a diagonal-placed square (both also exist as constraint tiles; harmless
overlap).

- **Side count is per-guide data** (`GuideData.Sides`), not a constraint: a **Sides** number row (same
  native input + wheel + spinners as Divisions, clamped 3–24) appears in Create when the polygon is the next
  shape, and in Edit when the selected guide is one. Changing it re-derives the outline in place
  (`GuideManager.SetSides` — cap-checked with revert, one `SetSidesCommand` undo step,
  `GuideSetSidesPacket` both ways, additive `Sides` on the guide DTO).
- New pentagon glyph; `DefaultSides` remembered in the client config.

## 6. Shape picker rework + favorites = the three slots

The 11-tile grid is gone from the main panel. The **Shape row is now 3 pinned favorite tiles + a ▾ tile**
that folds the full 12-shape catalog out beneath (4 per row). **Right-click any catalog tile to pin it into
slot 1** (older pins shift right, the third drops off; hover text says so). Left-click selects as ever —
selecting from the catalog folds it away again, so the panel always returns to its compact form. When the
current shape isn't one of the three favorites, no slot is lit and the ▾ tile's hover names it. Pins persist
per-player as shape codes in `layout-client.json` (`favoriteShapes`, default `arch · halfcircle · circle`),
validated/deduped/re-padded at load, written on shutdown with the other remembered settings. The GUI takes
the client config in its constructor now. The old "Favorites strip — soon" placeholder row is deleted;
**F2 is delivered in this form.**

Right-clicks on tiles ride a small `StarrableToggleButton` subclass (swallows right press/release so
nothing falls through to the world or the toggle).

## 7. Wire / persistence surface (all additive, both sides registered in order)

- `GuideCreateRequestPacket` + `Inverted`(7), `Sides`(8), `Apex`(9).
- `GuideDataDto` + `Sides`(14). The spring-back snapshot NEVER crosses the wire.
- New packets appended: `GuideSetSidesPacket`, `GuideSpringBackPacket`.
- `GuideData`: `Sides`, `OriginalControlPoints`, `OriginalConstraint`; **DataVersion 6**.
- New files: `Shapes/PolygonShape.cs`, `Undo/Commands/SetSidesCommand.cs`,
  `Undo/Commands/SpringBackCommand.cs` → **53 source files**.

## 8. Claude's judgment calls (decide-and-flag — review in play)

11a. **Spring-back restores the guide to WHERE it was placed**, not just its shape — if you moved a guide
     across the build by dragging anchors, SHIFT+click returns it there. Literal reading of
     "initially-placed form"; Ctrl+Z un-does the spring if it surprises you.
11b. **Spring-back also un-does a Surface-exit bake's positions** (the snapshot predates it) while keeping
     the current projection mode — an edge case that could re-surface the old wrong-side quantisation on
     such a guide. Cosmetic, undoable, deemed acceptable.
11c. **Spring-back skips the voxel-cap check** (it reuses the wholesale-restore seam, like undo). A
     pathological rescale-then-spring could exceed a cap; accepted for v1.
11d. ~~**Pinning favorites: newest pin takes slot 1**, older pins shift right, the third drops off.~~
     **SUPERSEDED in 0.1.15 (human-directed):** favorites are HARD-KEPT — see §10a.
11e. **The catalog folds shut when you select from it** (submenu semantics); it stays open while pinning.
     The expanded/collapsed state resets to collapsed each time the dialog opens.
11f. **Inverted-arch phantom rule changes one legacy edge case:** an OLD arch hand-dragged so its body sits
     below its feet will now re-derive its foot tangents UPWARD (toward the body) on the next edit — almost
     certainly what that shape should always have done, but it is a behaviour change.
11g. **Sides changes are NOT restored by spring-back** (sides is a setting like scale, not geometry
     distortion).
11h. **Triangle right-click step-back** (11 → base → gone) rather than discard-all in one click.
11i. **SHIFT invert is live-while-held** on the ghost; the state at the completing click is what bakes.
11j. **N=3/N=4 polygons overlap the equilateral/square constraint tiles** — left as-is (different gestures,
     same shapes; no confusion expected).

## 9. 0.1.14 playtest — RESULTS (same session)

The human played 0.1.14 and confirmed: **B-S10-2 fixed** ("This was fixed"), **CTRL + SHIFT
invert/spring-back working**, **three-click triangle working**, **Polygon working**. The picker worked but
its favorites flow was redesigned (→ §10); flag **11d is superseded** by the hard-kept model. The 0.1.14
checklist is retired; the open checklist is §12.

---

## 10. v0.1.15 — the picker redesign + three new requests (same session; human-directed)

### 10a. Favorites are HARD-KEPT now, four slots, star badges

The 0.1.14 "newest pin evicts the oldest" flow felt unintuitive in play. Redesigned to the human's spec:
**four** slots; **starring never evicts** — right-clicking a catalog shape fills the first FREE slot, and
when all four are taken you get an in-game message telling you to unstar something first. **Unstar =
right-click** a starred tile (works on the slot itself AND on the catalog tile, which now wears a small
**★ badge** so pins are visible at a glance). Slots can sit empty (a faint placeholder square with a hover
hint). Fresh installs default to arch · half-circle · circle · line; an existing config keeps exactly its
own pins (no auto-padding — hard-kept means hard-kept). Star badges are drawn as per-shape "-star" glyph
variants registered alongside the base glyphs.

### 10b. SHIFT centres the triangle's apex

Holding SHIFT while aiming/placing the THIRD click of a three-click triangle projects the apex onto the
base's perpendicular bisector — live on the ghost — so the free-triangle tool makes a centred (isosceles)
triangle in one gesture. Height still comes from the click. No conflict with SHIFT-invert (which only ever
applied to two-click shapes).

### 10c. The Free-Shape (the 13th catalog entry)

An irregular polyline: **every click chains another straight segment**. Finishing is position-based (the
practical double-click): **click the corner you just placed → the shape finishes OPEN**; **click the very
first corner (≥3 placed) → it CLOSES into a loop** — the aim snaps onto the first corner when you get near,
so the ghost visibly shows the loop before you commit. **Right-click steps back one corner at a time**
(the Session-11 step-back rule, generalised). **CTRL** snaps each new segment level-and-cardinal relative
to the PREVIOUS corner. Corner cap: 64.

After placement it's the arch's vertex-based cousin: every clicked corner is a grabbable, lockable ANCHOR;
**clicking a segment inserts a new corner there and grabs it** (the Free-Shape joins the arch as the only
shapes taking body inserts — `TakesBodyInserts` in the controller); segments stay perfectly straight, no
soft-flow, moving a corner moves only that corner. Spring-back / divisions / scale / Surface / undo all
inherited. **Fill is deferred** (human-directed — irregular outlines can be concave, which the current fill
methods would get wrong; the Fill toggle is currently inert on this shape).

Data/wire: `GuideData.IsClosed` (**DataVersion 7**), additive `IsClosed` on the guide DTO, additive
`Chain`/`Closed` on the create request (the full corner list crosses once, at creation). New
`Shapes/FreeShape.cs` (**54 source files**); the draft ghost got a chain preview path
(`SetDraftChainPreview`, sharing the placed-guide pipeline via the extracted `UploadPreviewMesh`).

### 10d. The Current Shape chip

A tile that is **permanently lit and draws the picked shape's glyph in guide-body YELLOW**, so a selection
that isn't on the favorite slots is visible without reading anything. Lives at the far right of the Mode
row; hover says "Current shape: X"; clicks are swallowed (it's a status light). Hidden in Edit mode
(the tool's next-guide shape isn't what Edit is about), dimmed in Delete. The yellow comes from per-shape
"-current" glyph variants that ignore the button tint.

## 11. Additional judgment calls (0.1.15 — review in play)

11k. **The chip sits at the FAR RIGHT of the Mode row**, not literally wedged against the + button —
     between + and the pencil it would read as a fourth mode. One line to move if it feels wrong.
11l. **Free-Shape finishing is position-based, not timer-based:** "double-click" = click the last corner
     again (radius ≈ max(0.25 blocks, 1.5 voxel cells)). A too-few-corners click on an existing corner is
     swallowed rather than stacking a duplicate.
11m. **Free-Shape corner cap = 64** (in-game message when hit); keeps targeting and undo snapshots sane.
11n. **The Fill toggle stays visible but does nothing on a Free-Shape** (fill deferred) — flag if that
     should grey out instead.
11o. **Upgrading from 0.1.14 keeps your existing 3 pins + 1 empty slot** (no auto-added fourth); only
     brand-new configs get the arch/half-circle/circle/line default.
11p. **The ★ badge shows only in the expanded catalog** — the four slot tiles don't wear it (they're all
     favorites by definition).
11q. **Other players still see only your FIRST corner dot** while you draft a Free-Shape chain (the
     existing draft-anchor broadcast; extending it to the whole chain is future work if wanted).
11r. **N=3/N=4 polygon–constraint-tile overlap (11j) unchanged**; the Free-Shape can also draw triangles/
     rectangles by hand — deliberate freedom, not a conflict.

## 12. 0.1.15 playtest — RESULTS (same session)

**"This was tested. Good work."** — everything confirmed. Follow-up refinements requested → §13.

---

## 13. v0.1.16 — playtest refinements (human-directed, same session)

1. **Sides field hard floor.** The native number input's spinner/wheel has no minimum, so the field could
   DISPLAY 2, 1, 0… below the polygon's floor. Fixed: the typed-change handler now tracks each field's
   last APPLIED value; a change that is exactly one below it is a spinner/wheel DECREMENT and hard-stops —
   value re-clamped to 3 and the display snapped back. Genuine typing ("1" on the way to "12") still passes
   through untouched.
2. **Catalog five wide** (was four): 13 shapes → rows of 5·5·3, exactly the width of the slots row.
3. **A white separator line** (2 px, ~55% alpha) between the favorite slots and the unfolded catalog.
4. **★ badge repositioned** to the tiles' UPPER-RIGHT corner, tucked close to the edge (drawn un-zoomed so
   it can hug the corner without clipping) — clear of every glyph, including the Line's corner dot.
5. **Create header split:** "Create mode — next guide:" stays left; the shape NAME is right-aligned to the
   panel edge, sitting naturally above the Current Shape chip.
6. **The HUD got the Current Shape chip too** — same always-lit yellow glyph, top-right of the dimensions
   panel (the first two text lines narrow so they never run under it); recomposes on shape/mode changes.
7. **Delete mode greys the TILES now, not just the labels.** Root cause: the stock toggle button's
   `Enabled=false` dims its chrome but not a custom ICON face. Every layout glyph now has a registered
   "-ghost" variant (~quarter alpha) and every disabled tile — rows, slots, catalog, chevron, chip —
   composes with it.
8. **Free-Shape SHIFT = vertical segment:** the next corner pins to the column straight above/below the
   previous corner (X/Z locked, height from the aim) — the vertical partner to CTRL's horizontal
   level-and-cardinal snap. Live on the ghost and at the click.
9. **Release zips moved to `..\Layout Zips\`** (the human's standing location — it already held the
   0.1.10–0.1.13 history; 0.1.14/0.1.15 moved in, 0.1.16 born there). The packaging default is that folder
   from now on.

**Judgment calls (16a–16d):**

16a. **The Sides floor heuristic has one edge:** if the field reads exactly 3 and you TYPE "2" (aiming for
     20–24), it's indistinguishable from a spinner decrement and snaps to 3 — type the other digit first
     or wheel up instead. Rare enough to accept.
16b. **The HUD chip shows in Create mode only**, mirroring the F-menu chip's visibility rule.
16c. **SHIFT beats CTRL** when both are held on a Free-Shape segment.
16d. **The 5-wide catalog's last row holds 3 tiles** (13 shapes) — left as-is until the catalog grows.

## 15. v0.1.17 – v0.1.19 — rapid GUI polish (all human-directed; 0.1.16–0.1.18 playtest-CONFIRMED)

**0.1.16 and each subsequent build were playtested and confirmed in-session**; each round produced the next
batch. New standing workflow rule adopted at session end (now in CLAUDE.md): **ship a zip per iteration but
do NOT update docs until the human says so.**

**0.1.17:** Fill moved onto the Projection row (compact second label in the spare tile slots) · Sides moved
onto the Divisions row · dead space under the title bar removed (the child area is already inset by the
dialog padding — starting a full TitleBarHeight down double-counted) · favorites in the catalog now
highlight the WHOLE glyph in the Current-Shape yellow instead of the (too-small) corner ★.

**0.1.18:** the paired row's second tile group snaps to the shared column grid (Fill sat 4 px left) ·
number fields narrowed 90→76 so the "Sides" label never wraps · text pass: "Empty Slot - Right-Click a
shape below to pin it." · Edit mode reads "Edit Mode - Select a guide to edit it." / "Edit the selected
guide using the settings below." (the selected-info line still replaces the second line when a guide IS
selected) · periods + Mode-style capitalisation across the header/hover sentences.

**0.1.19 (unplayed):** picker vocabulary unified to **pin/unpin** everywhere (hovers + the slots-full
message) · **the Fill tiles grey out when the Free-Shape is picked/selected** (fill is deferred there —
human: rare-use tool, greying beats building concave fill), with an explanatory hover. Closes flag 11n.

**Playtest checklist for 0.1.19:** pin/unpin wording shows in all three hover spots + the slots-full
message; Fill greys (tiles + label, ghost glyphs) when Free-Shape is the next shape in Create AND when a
placed Free-Shape is selected in Edit, and comes back for other shapes.

---

## 14. Playtest checklist for 0.1.16 — ✅ CONFIRMED (see §15)

1. **Sides:** spinner/wheel down stops at 3 and the field never displays less; typing 10–24 still works;
   max still 24.
2. **Picker:** catalog is five wide with the white line under the slots row; ★ badges sit in the tiles'
   upper-right corners without touching the glyphs.
3. **Header:** in Create, the shape name hugs the right edge above the chip and updates with every pick.
4. **HUD:** the yellow chip appears top-right of the dimensions panel in Create, matches the F-menu chip,
   disappears in Edit/Delete, and never overlaps the Mode/Scale lines.
5. **Delete mode:** every tile (scale, projection, plane, fill, shape slots, chevron, chip, open catalog)
   reads clearly greyed, and none respond to clicks.
6. **Free-Shape:** SHIFT while chaining pins the segment perfectly vertical off the previous corner; CTRL
   still pins it level; SHIFT wins if both held.
7. **Regression:** favorites star/unstar, the Free-Shape finish gestures, and spring-back all behave as in
   0.1.15.

---

## 16. Post-finalize — the 3D shape family (v0.1.20 → v0.1.23)

After the Session-11 finalize the human pivoted to **3D volumes** (long parked as "in scope LATER"). Shipped
code+zip only per the standing rule; documented here in the first doc pass after, then **committed to `main`
and pushed to GitHub** (github.com/DodenGruva/Layout) through v0.1.23.

### 16a. v0.1.20 — Sphere (playtest-CONFIRMED)
`SphereShape`: two clicks = a diameter, centre/radius derived. The first volumetric sampler — instead of
marching a curve it **scans the cell lattice** in the bounding box and keeps cells by predicate: *shell* =
the surface crosses the cell (nearest-corner distance ≤ R ≤ farthest-corner), *solid* = the cell touches
the ball. Exact, hole-free, one cell thick. A **scan guard** (`MaxScanCells ≈ 4M`) short-circuits absurd
fine-scale sizes: `GetVoxelCount` returns a huge number so the cap rejects instantly and the ghost coarsens,
`GetVoxelPositions` returns empty — the two disagree only in a regime the cap makes unreachable. Targeting
is a **wireframe** (equator + two meridians). Volumes are forced **Volumetric**; Surface + Divisions gated
server-side and greyed in the GUI.

### 16b. v0.1.21 — Dome, Cylinder, Cone, Box (playtest-CONFIRMED after 0.1.22/0.1.23)
`GuideShapeTypes.IsVolume` classifies the family. **Dome** = 2-click half-sphere over the base diameter,
rising out of the clicked plane (SHIFT → bowl); stores the two base anchors + a side-remembering apex; shell
= the sphere scan clipped to the apex's half-space. **Cylinder / Cone** = 3-click (base diameter, then a
height click projected onto the axis; centre-banded lateral shell). **Box** = 3-click (two diagonal base
corners = the rectangle gesture, then height); a true box with independent side lengths; shell = the sphere's
solid-minus-eroded recipe in box-local coordinates. New **3D catalog section** under a second white
separator. The 3-click volumes reuse the triangle's `NeedsApexClick` height machinery; SHIFT-centring stays
triangle-only.

### 16c. v0.1.22 — first fixes
3D shapes were falling back to a plain Arch — the `SetShape` whitelist still omitted Dome/Cylinder/Cone/Box;
added. **"2D" / "3D" section labels** placed left of each catalog group.

### 16d. v0.1.23 — 3D GUI + placement fixes (pending final playtest)
- **Catalog stays expanded** after picking a shape (only the ▾/▴ tile collapses it).
- **Section labels centred** in their column (Mode, Shape, Scale, 2D/3D, …).
- **Divisions row hidden entirely** on 3D volumes (like Sides being polygon-only), not just greyed.
- **Height-inversion fix** (Dome/Cylinder/Cone): the axis was `Cross(u, m)`, whose sign flipped when the
  second base anchor crossed to the other side of the first — inverting the volume into the floor. Replaced
  with a deterministic **`ShapeGeometry.BaseNormal`** (+up regardless of anchor order; SHIFT stays the only
  invert). Same class of bug as the 2D default-up fix. Box/Sphere were never affected.
- **Free-air height** for Cylinder/Cone/Box: with no block under the crosshair the height handle follows
  the view ray at the base's distance (looking up/down grows/shrinks it, the shape projecting it onto its
  axis); a targeted block still takes priority. `BuildSettings` made null-`blockSel` safe (falls back to the
  draft's captured plane axis).

**Data/wire:** none new — volumes reuse `ControlPoints` + `ShapePlaneAxis`; enum values appended;
**DataVersion stays 7**. **59 source files** (+ SphereShape, DomeShape, CylinderShape, ConeShape, BoxShape).

### 16e. Flagged / worth watching (3D)
- **Wireframe targeting** (all volumes): clicking the shell between wireframe lines misses the body — the
  anchors and the height handle are the reliable grab points. Fine for v1.
- **Cap vs. resolution is real in 3D**: a shell grows with R², a solid with R³. Fine-scale volumes cap out
  small (~2.8-block sphere radius at scale 1); the "coarsen your scale" cap warning is the intended guide.
- **Free-air height depth = base-centre distance** — the aim traces a sphere around the eye and projects to
  height. Intuitive enough; revisit if it feels floaty in play.
- **Cylinder/cone diagonal shells may run 2 cells thick** (centre-banded, not exact) at odd orientations —
  the accepted v1 trade; sphere/dome/box are exact.

### 16f. Queued small tweak
- **Narrow the standalone Divisions field to the polygon width** (76 px, matching `AddNumberPairControl`);
  it is 90 px in `AddNumberControl` today. See `TODO.md` "Quick GUI tweaks".

---

## 17. v0.1.24 → v0.1.27 — client-lifecycle bugs, admin commands, oversize safety

Post-3D-family bug hunt (from playtesting) plus two requested features. Committed to `main` and pushed to
GitHub through v0.1.27.

### 17a. v0.1.24 — exit-to-title fixes + Divisions width
- **Blank GUI tiles after exit-to-title → re-enter (B-24-1).** `LayoutToolIcons` gated registration on a
  process-**static** `_registered` flag that outlived the client; on re-entry `capi.Gui.Icons.CustomIcons`
  is a fresh empty dictionary, so registration was skipped and tiles rendered blank. Fixed: gate on the
  LIVE dictionary (`reg.ContainsKey(Arch)`) and re-register each client start.
- **Pins save-on-change (partial B-24-2).** Star/unstar now persists the client config immediately instead
  of relying on `Dispose` firing on exit-to-title (wired a save `Action` into `GuideToolGui`). Narrowed the
  standalone Divisions field to the 76 px polygon width.

### 17b. v0.1.25 — the real favorites-persistence bug
Pins STILL reverted after 0.1.24 — the bug was on LOAD, not save. `FavoriteShapes` is pre-initialised to the
four defaults, and Newtonsoft (default `ObjectCreationHandling.Auto`) **appends** the saved pins to that
existing list rather than replacing it → `[defaults…, saved…]`; `Normalize` then caps at 4 keeping the first
four (the defaults). The saved pins were on disk the whole time; the load discarded them. Fixed with
`[JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]`.

### 17c. v0.1.26 — admin commands + the invisible-giant root cause
A heavily version-swapped test world silently refused all placements. Diagnosed as the **total voxel cap
(250k) maxed**, which was silent because the cap-warning flash is tied to the would-be guide's id (never
displayed). Root cause of the max-out: with per-guide cap set to 0, **enormous 3D guides could be created
even though the scan guard makes them un-renderable** — `GetVoxelCount` returns a ~536M sentinel and
`GetVoxelPositions` returns empty, so invisible giants got stored, each dumping ~536M onto the world total.
Fixes:
- **`/dispel all`** and **`/dispel <chunk radius>`** admin commands (controlserver) — cleanup.
- **`GuideManager.HardVoxelCeiling = 10M`**: any guide over it is rejected REGARDLESS of caps (create +
  rescale/fill/drag), so invisible giants can't be made and can't pollute the total.
- **Clear in-game errors** on server-side create rejection (too-large vs. world-budget), replacing the
  invisible flash.

### 17d. v0.1.27 — namespacing + overflow safety
- **`/dispel` → `/layout dispel`** (a `/layout` command group with a `dispel` subcommand) so it can't clash
  with other mods.
- **Running voxel total `int → long`** (`GuideManager._totalVoxels`). Each guide's own count fits an int
  (hard ceiling), but the SUM on a caps-off server could pass int's ~2.1B and wrap negative (which reads as
  "under budget" and disables the cap checks). A `long` can't overflow in practice. Groundwork for the
  human's wish to allow **enormous fine-detail guides** later (which will also need the scan guard / hard
  ceiling raised and a rendering perf pass).

**Data/wire:** no changes — DataVersion stays **7**; no new packets (commands are server-local). **59 source
files.**
