# Layout — TODO / Outstanding Items (post-Session-9)

> **Purpose.** The running punch-list. Companion to `ARCHITECTURE.md` (the plan) and `PROJECT_STATUS.md`
> (the status). Renamed from `OUTSTANDING_ITEMS.md` at the Session-8→9 migration into Claude Code.
>
> **Fidelity note (Session-9 additions).** The Session-9 content below was reconstructed from the working
> conversation, not regenerated from the code on disk. **Verified against source 2026-07-05:** the specifics
> formerly tagged **⚠ verify** (file/class/packet/command names, DataVersion, counts) were all confirmed
> accurate; the tags are resolved in place. Full Session-9 detail lives in `SESSION_9.md`.

---

## ⭐ Top of the list (Session-11 end, v0.1.19)

1. **B-S9-1 — Lock-in-place (UNRESOLVED; the headline for next session, human-confirmed).** Right-clicking
   to lock often locks an adjacent voxel and the guide visibly shifts. Untried fix: **ray-vs-voxel-box
   first-hit picking** so the aimed cell is authoritative. See OPEN BUGS.
2. **Playtest-confirm 0.1.19** (tiny: pin/unpin wording; Fill greys out on the Free-Shape). Everything
   through **0.1.18 is playtest-CONFIRMED** (human-verified in-session, iteratively).
3. Remaining flagged decisions (11a–11r, 16a–16d in `SESSION_11.md`): 11a (spring-back restores POSITION)
   was reviewed and **confirmed good**; 11n (Fill on Free-Shape) resolved by greying the button in 0.1.19.
   The rest are cosmetic — walk them opportunistically.

**Standing workflow rule (session end, human-set — also in CLAUDE.md):** ship a NEW zip per code
iteration, but do NOT update docs (TODO/ARCHITECTURE/PROJECT_STATUS/SESSION/memory) until the human says
so. Warn the human before any context trim if the docs are stale.

---

## Resolved in Session 11, batches 3–5 (0.1.16 → 0.1.19) — `SESSION_11.md` §13/§15
## (0.1.16–0.1.18 playtest-CONFIRMED; 0.1.19 pending)

- **0.1.16:** Sides hard floor at 3 · catalog 5-wide + white separator · corner ★ badges · Create header
  shape name right-aligned · the HUD's Current Shape chip · Delete mode greys every TILE ("-ghost" glyph
  variants) · Free-Shape SHIFT = vertical segment · zips relocated to `..\Layout Zips\` (0.1.10+ history).
- **0.1.17:** Fill onto the Projection row · Sides onto the Divisions row · title-bar dead space removed ·
  favorites shown as YELLOW glyphs (the ★ was too small).
- **0.1.18:** Fill tiles snapped to the column grid · number fields narrowed (no "Sides" wrap) · Edit-mode
  text rewrite + periods/capitalisation pass.
- **0.1.19:** pin/unpin vocabulary everywhere · Fill greyed out on the Free-Shape (closes flag 11n).

---

## Resolved in Session 11, second batch (0.1.15) — record in `SESSION_11.md` §10; **playtest-CONFIRMED**
## ("This was tested. Good work." — refinements it spawned are the 0.1.16 batch above)

- **Favorites hard-kept (human redesign after playing 0.1.14):** FOUR slots; starring fills a free slot and
  never evicts (message when full); right-click unstars (slot or catalog); ★ badges on starred catalog
  tiles; empty slots show placeholders; no auto-padding of existing configs.
- **Current Shape chip:** permanently-lit, guide-body-yellow glyph of the picked shape at the far right of
  the Mode row — you can see an off-slot selection without reading anything.
- **SHIFT on the triangle's apex click = centred apex** (perpendicular bisector, live on the ghost).
- **Free-Shape (13th catalog entry):** chained straight segments; click-last finishes open / click-first
  (≥3) closes; right-click steps back a corner; CTRL snaps to the previous corner; body inserts like the
  arch; fill deferred; corner cap 64. `IsClosed` → **DataVersion 7**; additive Chain/Closed on the create
  request; **54 source files** (+ FreeShape.cs).

---

## Resolved in Session 11, first batch (0.1.14) — full record in `SESSION_11.md`; **playtest-CONFIRMED**
## (bake, keys, triangle, polygon; picker superseded by the 0.1.15 redesign above)

**The whole Session-10 human backlog, plus both small tweaks:**

- **B-S10-2 FIXED (code-complete):** the Surface→Volumetric bake probes world solidity on both sides of the
  plane (server-side mirror of the renderer's decal probe) and bakes points half a voxel into the AIR side —
  the volume grows out of the wall now, not into it. (`GuideManager.ProbeAirSide`; flagged item #15 closed.)
- **Shape picker = 3 pinned favorite slots + ▾ catalog fold-out; starring = right-click a catalog tile.**
  Pins persist in `layout-client.json`. Favorites strip deleted; **F2 delivered in this form.**
- **Triangle = three clicks** (anchor · anchor · height; ghost apex tracks the crosshair; right-click steps
  the draft back one click). Equilateral stays two-click. Supersedes flagged decision #2.
- **CTRL = the cardinal constraint** (was SHIFT), drafting + anchor re-grabs.
- **SHIFT = invert while drafting** (upside-down arch / equilateral; live on the ghost) **and spring-back on
  a placed guide** (SHIFT+left-click restores the as-placed points + constraint from a creation snapshot;
  one undo step; old-save guides politely refuse). Prerequisite shipped: **all shapes default "up"**
  regardless of click order (sign-normalised frames; side-preserving constraint re-derivation).
- **Polygon (N-gon):** vertex → opposite-perimeter-point gesture, 3–24 sides as per-guide data with a Sides
  number row (Create + Edit), cap-checked `SetSides`, undoable, own glyph. F1's "next catalog addition" done.
- **Tooltips auto-size** (`AddAutoSizeHoverText`); **Surface slabs 0.01 → 0.0025** (75% thinner).
- **DataVersion 5 → 6** (`Sides` + the never-wired spring-back snapshot). Wire: additive create-request
  fields + `GuideSetSidesPacket` + `GuideSpringBackPacket`, appended in order. **53 source files** (+3).

---

## Resolved in Session 10 (0.1.10 → 0.1.13) — full record in `SESSION_10.md`

**Edit mode — per-guide editing without the GUI expanding (0.1.13).** New third `ToolMode.Edit` (enum now
`Create · Edit · Delete`, client-only). In Edit mode the main setting rows (Scale/Projection/Plane/Fill/
Divisions/**Visibility**) act on the SELECTED guide via the network senders instead of the tool defaults —
so the panel no longer grows a separate section. Edit left-click **selects only** (no grab/insert/lock);
geometry editing stays in Create; Delete dispels. (`BuildSelectedGuideSection` removed; `ResolveSelectedGuide`
is Edit-only; pencil glyph `LayoutToolIcons.ModeEdit`.)

**Division markers pair on off-cell boundaries (0.1.13).** When a boundary lands between two voxels a lone
mark read half a cell off; `ShapeGeometry.ClaimMarkerPaired` (new) claims BOTH near-tied cells — the arch
apex's even-span midpoint treatment, generalised. `DivisionMarks.Apply` uses it. Pure render-side recolor.

**Divisions floored at 0 (0.1.13).** The native number input has no min; `OnDivisionsTyped` now snaps the
display back when it clamps, so the field can never go negative (or over `MaxDivisions`).

**B-S10-1 — Surface guides shift behind the block face after reload. FIXED (playtest-confirmed).** On
world-load, a Surface guide could render before its surrounding blocks loaded; the air-side probe
(`GuideRenderer.CountSolidProbes`) then saw all-air and picked the wrong layer, sinking the decal behind the
face it was placed on. Fix: the probe now detects unloaded chunks (`GetChunkAtBlockPos == null`) and marks
the guide's side *provisional*; a low-frequency re-probe tick (`OnReprobeTick`, 500 ms) rebuilds each
deferred guide once its neighbourhood loads — no wire/persistence change, self-corrects on reload and when
walking into range. (Volumetric guides were never affected: their anchor is stored half a voxel into the air
cell, reload-stable.)

**Icon UI pass (0.1.10–0.1.12).** All option rows are now compact square icon tiles drawn by the new
`src/UI/LayoutToolIcons.cs` (Cairo glyphs in `capi.Gui.Icons.CustomIcons`; project now references
`cairo-sharp.dll`): the 11-shape picker, Mode, Projection, Plane, Fill, Visibility, and Scale. Scale copies
the game's native icons — an N×N grid where **N is the voxel count** (1×1 smallest … 16×16 = full block,
drawn as one solid square); 4×4+ run edge-to-edge. Arch glyph = open elliptical dome (matches the real
Catmull-Rom curve). Tiles show their name on hover; Delete-mode greying uses native `Enabled=false`.

---

## OPEN BUGS

**B-S9-1 — Lock-in-place imprecise and still deforms the guide (UNRESOLVED after multiple Session-9 fixes).**
Symptoms (confirmed still present in play at Session-9 end): the voxel targeted by the right-click is
frequently **not** the one locked (an adjacent voxel goes red), and placing the lock still **visibly
shifts/deforms** the guide. Intended behavior (human, restated): only the targeted voxel locks and turns red;
the guide must **never** move except when the user is actively moving it.

Fixes attempted, each verified in code but insufficient in play:
- (s9) near-point body-hit → lock-toggle conversion.
- (s9) **chord-invariant phantom drop** — killed the neighbor-height reflection lurch (whole-arch tilt from a
  near-foot insert). Helped, didn't fully resolve.
- (s9) **slave-regime flow model** — removed soft-point pull. Helped grabbing feel; didn't resolve the lock
  shift.

**Leading un-tried suspect:** body hits resolve to *nearest-point-on-curve to the ray*, which can land in a
cell **adjacent** to the aimed voxel → fix candidate is **ray-vs-voxel-box first-hit picking** so the clicked
CELL is authoritative. Secondary: phantom neighbor-X/Z tracking when locking near a foot; centripetal-CR
reparameterization from the inserted knot. Parked at the human's direction ("make note of this bug, but let's
move on for now").

~~**B-S10-2 — Surface→Volumetric bake grows the WRONG way (into the block).**~~ **FIXED in 0.1.14 and
PLAYTEST-CONFIRMED ("This was fixed" — human, same session).** The bake runs exactly the fix this entry
proposed: a server-side solidity probe mirroring the renderer's `CountSolidProbes` picks the air side, and
baked points land half a voxel into it (`GuideManager.ProbeAirSide`). (Was flagged item #15 — closed.)

---

## Requested next — human backlog (queued Session-10 end) — ✅ ALL DELIVERED IN 0.1.14

Every item below shipped in Session 11 (see the Resolved section above and `SESSION_11.md`; awaiting
playtest). Kept for the record of what was asked:

- **F1b — Polygon (N-gon).** Implement the regular-polygon shape (arbitrary side count) — the next catalog
  addition already sketched in F1 below. Same two-click gesture + a side-count control (like Divisions).
- **Triangle → THREE-click placement.** Change the triangle gesture from *2 clicks + a born apex* to
  **anchor · anchor · height-adjust** (place the base with two clicks, then a third click/drag sets the apex
  height). **Supersedes flagged decision #2** (the 2-click born-apex call) and touches `ShapeFactory` /
  `TriangleShape` / the draft state machine (`DraftManager` currently stores a single start point — a
  three-click draft needs a second stored point). Note: this makes the triangle the only non-two-click shape,
  reopening the "every shape is the same two-click gesture" settled decision — the human is explicitly asking.
- **Swap the SHIFT constraint to CTRL.** The cardinal/level snap (drafting the second foot; re-grabbing an
  anchor) currently rides **SHIFT** (`ShiftHeld()` in `GuideToolController`); move it to **CTRL**. Frees SHIFT
  for the next item. (Settled "SHIFT cardinal constraint" decision — human-reopened.)
- **New SHIFT function — context-dependent (spring-back + placement invert).** With SHIFT freed from the
  cardinal constraint (moved to CTRL above), it does two things depending on when it's held:
  - **During initial placement (drafting):** SHIFT **inverts the shape upside-down** — e.g. an arch opens
    downward instead of up; the height/apex is mirrored across the base. A live toggle on the ghost.
  - **After placement:** SHIFT **springs the shape back to its initially-placed form** (undo soft-flow / hand
    distortions back to the pristine geometry). One undo step.
  - **Prerequisite — all shapes default to "up."** Today some shapes (notably triangles) flip upside-down
    depending on which direction the base anchors are placed (the in-plane frame's perpendicular sign follows
    anchor order). Make the default orientation **consistently "up"** (world-up-biased) for every shape,
    regardless of placement direction; SHIFT-at-placement is then the *only* way to get the inverted
    orientation. Touches the apex/height derivation (`ShapeGeometry.TryGetFrame` / `InPlaneAxes` sign choice
    in `TriangleShape` and friends).
  - Open Qs to settle when built: "original" for spring-back = as-first-placed, or the last clean parametric
    form? Spring-back scope = whole guide or grabbed region only? Live-while-held vs one-shot snap?
- **Shape picker → 3 buttons + an "expand" arrow.** Reduce the initial shape row from 11 tiles to **3 shape
  buttons + a 4th arrow/▾ tile** that opens a submenu (fly-out or expanded grid) with the full catalog.
  Keeps the panel compact. (Reworks the `AddIconGrid` "shape" row in `GuideToolGui`.)
- **Favorites = the 3 initial slots (remove the Favorites strip).** Delete the Favorites placeholder row;
  instead, **starring a shape puts it into one of the 3 initial shape-picker slots** (from the submenu
  above). Persist per-player in `layout-client.json` as `{type + constraint}` triples. **Supersedes F2** and
  the Favorites-placeholder decision — the two GUI items (this + the picker rework) are one design.

**Two small tweaks the human added afterward:**

- **Hover tooltip box is too wide for its text.** The per-tile hover description reserves a lot of empty
  space. In `GuideToolGui.AddIconTile` the hover is `c.AddHoverText(hover, WhiteDetailText(), 200, bounds, key)`
  — the `200` is a fixed max width. **Fix:** either drop that width to fit (≈ the longest option name) or
  switch to `AddAutoSizeHoverText(...)` (confirmed to exist) so the box sizes to the text.
- **Surface voxel slabs too thick — reduce thickness by 75%.** `GuideRenderer.SurfaceSlabThicknessWorld`
  is currently `0.01f`; take it to **`0.0025f`** (¼ of current). Sanity-check afterward that the anti-z-fight
  `SurfacePlaneInset` (`0.004f`) still holds the thinner slab off the wall cleanly at scale 1
  (`t = min(thickness, edge − 2·inset)` keeps it valid, but eyeball it in play).

- ~~**Divisions scroll-wheel.**~~ **DONE — confirmed in play (0.1.12; floored at 0 in 0.1.13).** Dropdown
  removed. Attempt 1 (0.1.10: plain text field + dialog `OnMouseWheel` with a raw `Bounds.PointInside`
  hit-test) did not work — text inputs have no native wheel handler. Attempt 2: the field in
  `GuideToolGui.AddDivisionsControl` is the game's native **`GuiElementNumberInput`** (own built-in wheel +
  up/down spinner buttons; `IntMode = true`, `Interval = 1`); dialog-level fallback stays for
  hover-without-focus (`IsPositionInside`). 0.1.13: `OnDivisionsTyped` snaps the display back on clamp so the
  field can't go below 0 (or over `MaxDivisions`). GUI-only; `DivisionMarks` / packet / command untouched.

---

## Flagged decisions awaiting the human's review

Made under the standing "decide, note for review" rule; each is cheap to reverse. None block play.

**Session-11 additions — full text in `SESSION_11.md` §8 (11a–11j) and §11 (11k–11r):** spring-back
restores the as-placed POSITION too (11a) · spring-back vs a baked Surface exit (11b) · spring-back skips
the cap check (11c) · ~~newest pin evicts (11d)~~ superseded by the 0.1.15 hard-kept model · catalog folds
shut on select (11e) · legacy below-the-feet arches re-derive phantoms toward the body (11f) · sides not
restored by spring-back (11g) · draft right-click steps back per-click (11h) · invert is live-while-held
(11i) · N=3/N=4 polygons overlap the constraint tiles (11j) · chip at the far right of the Mode row, not
touching + (11k) · Free-Shape finish is position-based, ~1.5-cell snap (11l) · 64-corner cap (11m) · Fill
toggle inert on Free-Shapes (11n) · no auto-added 4th pin on upgrade (11o) · ★ badge catalog-only (11p) ·
others still see only your first chain corner (11q) · Free-Shape can hand-draw triangles/rectangles —
deliberate (11r).

**Session-10 additions (the icon UI pass; partially reviewed in play already):**

0f. **Edit mode is SELECT-ONLY** — clicking a guide in Edit selects it for the button-driven settings but
    does NOT grab/insert/lock (so a select-click can't accidentally reshape). All geometry editing stays in
    Create. Alternative if you want it: allow grabbing in Edit too, with body-click = select and point-click
    = grab to keep insert from firing on a select.
0a. **Divisions wheel works on hover** (point at the field and scroll), not only while focused — more
    discoverable than the original "while focused" spec. The native number input additionally responds
    when focused, and its spinner buttons are a third path.
0b. **2×2 scale icon stays mid-sized** while 4×4/8×8 run edge-to-edge (reading of the vanilla icons);
    the 16× icon is one solid square (human-confirmed choice).
0c. **Tile edge = 42 px**, label column 74 px — the "compact / native proportions" dial, one constant each.
0d. **Triangle constraint glyphs** rely on small geometry notation (right-angle mark, equal-side ticks);
    if illegible at tile size, differentiate by proportion instead.
0e. **Plane glyphs** (iso cube, active face filled) — N–S vs E–W legibility unproven in play.

**Session-9 additions:**

1. **Soft-flow regime split** (slave for interior grabs, shape-preserving for structural grabs). The decisive
   fix for "the apex acts like a pin." Human confirmed grabbing "MUCH better"; the split itself is Claude's
   call and can be revisited.
2. ~~**Triangle gesture: 2 clicks + a born apex.**~~ **Superseded (Session 11, 0.1.14):** the human reopened
   it and the THREE-click anchor·anchor·height placement is now built (Equilateral stays two-click).
3. **Right / Isosceles / Square have no break gesture in v1** — their constrained drags always absorb
   (slide/resize), so they never demote to the free parent. They live as separate catalog tiles (like
   circle/ellipse). Equilateral *does* break (apex drag → free triangle).
4. **Rectangle's derived corners are markers, not grabbable** — body clicks map to the nearest stored anchor.
5. **Division marks are magenta** (`VoxelRenderType.Division`) — the one hue distinct from the six existing
   roles. Open to review.
6. **Divisions type-in field applies per keystroke** with a changed-value guard — typing "12" briefly applies
   1 then 12, i.e. two sends / two undo steps on a selected guide. Acceptable v1; a commit-on-blur pass is the
   upgrade. (Superseded in part by the scroll-wheel request above, but the per-keystroke behavior of the
   remaining field still applies.)

**Session-8, still unreviewed (carried):**

7. **Ellipse body left-click → grab nearest handle** (parametric ring has nothing to insert).
8. **Ellipse body right-click → toggle nearest handle's lock.**
9. **New ellipse minor radius = ½ major** (visibly elliptical default; circles come from the Circle tile).
10. **Minor handle slides along its axis** (free-space drags project onto it).
11. **A placed guide's shape isn't menu-editable** — live guides reshape by grabbing, not menus (a
    menu-driven re-constrain op is possible future work — see F3). *(Session 10: Edit mode simply hides the
    shape picker rather than showing it read-only; the decision is unchanged.)*
12. ~~**The appended Selected-guide section itself.**~~ **Superseded (Session 10, 0.1.13):** the appended
    section is gone; per-guide editing now happens in the new **Edit mode**, reusing the main rows (see
    Resolved / flag 0f).
13. **Re-grab constraint reference = the guide's OTHER ANCHOR** (reproduces the drafting feel). *(Session
    11: the key is CTRL now; the reference-point decision itself is unchanged.)*
14. **Circle → ellipse is the break floor** — an ellipse does not break further into a free closed spline in
    v1 (terminate-at-ellipse).
15. ~~**Bake cell-side.**~~ **CLOSED (Session 11, 0.1.14):** became bug B-S10-2 and got exactly the proposed
    fix — the server-side air-probe bake (`GuideManager.ProbeAirSide`). Awaiting playtest.
16. **Proportional soft flow (structural-grab regime)** — deserves a stretch/shrink/rotate torture test on an
    arch with several inserted points.

---

## Known costs / tuning (deliberate, watch in real play)

- **Filled guides recount exactly per drag update** (cells generated each move packet). If big filled discs
  drag sluggishly → add a per-drag count cache. Correctness-first per the standing rule.
- **Settled guides always mesh at full resolution** (the coarsening-leak fix). Huge guides pay their real
  cost on every rebuild — watch for hitches while dragging points of 8k+-voxel guides.
- **`PreviewFullResVoxelCap` = 8,000** — draft-ghost-only; tune if huge drafts stutter.
- **Division marks add a render-side pass** *(verified)* — `DivisionMarks.Apply` is called on every mesh
  rebuild in `GuideRenderer` (both the draft ghost and placed guides), walking `SampleCurve(128)` for arc
  length then a nearest-cell claim per boundary; cheap, but it does walk the cell list. Watch on very high
  division counts × large guides.
- **Ghost-greying** — if the API's toggle buttons expose an `Enabled` flag, native disabled state beats the
  alpha-ghost approach (`GuideToolGui` tile-row seam).
- Carried from Session 7: item transforms; recipe balance; scroll-wheel bindings (the divisions field is the
  first concrete use — see Deferred); stale color comments (cosmetic); Surface flatten's eventual move into
  the shape layer (`TODO(Surface)`).

---

## Future features (specced or sketched, not started)

### F1. Remaining shape catalog — ✅ FIRST WAVE DONE (Session 9)
**Built this session:** Line, Triangle (+ Right / Equilateral / Isosceles), Rectangle (+ Square). *(Verified
against `GuideShapeType` = {Arch,Ellipse,Line,Triangle,Rectangle}, `ShapeConstraint` =
{None,SemiCircle,Circle,Right,Equilateral,Isosceles,Square}, and the shape files — all present.)*
**SECOND WAVE (Session 11): Polygon (regular N-gon, 3–24 sides) — DONE (0.1.14, awaiting playtest).** The
**planar-vs-3D decision remains SETTLED: 3D volumes (spheres, cones…) are in scope LATER**; current shapes
stay planar (per-shape plane is in the contracts via `ShapePlaneAxis`).

### F2. Favorites — ✅ DELIVERED (Session 11, 0.1.14; awaiting playtest)
Built exactly as the human redesigned it: the picker is 3 pinned slots + a ▾ catalog fold-out; right-click a
catalog tile to pin (newest pin = slot 1). Persisted per-player in `layout-client.json` as shape codes
(each code = a {type + constraint} pair). The old placeholder row is deleted.

### F3. Re-constrain op (idea, unrequested)
The inverse of a break: a menu action to snap a free shape back under a constraint (arch → half-circle,
ellipse → circle, triangle → equilateral, rectangle → square) with a best-fit. Natural undo pairing exists.
Park until asked.

---

## Next session — start here

**Everything through 0.1.18 is playtest-confirmed; 0.1.19 (pin/unpin wording + Fill greyed on Free-Shape)
awaits a quick look.** The agenda, human-set:

1. **B-S9-1 — the lock-in-place bug is the HEADLINE.** Attempt the untried fix: ray-vs-voxel-box first-hit
   picking so the aimed cell is authoritative (see OPEN BUGS for the full history of attempts).
2. Confirm 0.1.19's two tiny changes in passing.
3. Then, if asked: Free-Shape fill (needs a concave-safe method — deliberately deferred, rare-use);
   broadcasting the whole Free-Shape draft chain to other players (11q); **F3 re-constrain op**; 3D volumes
   (settled-in-scope-LATER).

**Workflow reminders:** every code iteration ships a NEW `Layout<version>.zip` into `..\Layout Zips\`;
docs are updated ONLY when the human says so; warn before a context trim if docs are stale.

---

## Resolved in Session 8 (ledger — do not reopen without cause)

| Item | Resolution |
|------|-----------|
| **B1** insert broken | Superseded + rebuilt: left-click body = insert+grab in one gesture; root targeting cause fixed separately. |
| **B2** White highlight blob | Single-voxel nearest-claim; radius constants deleted. |
| **B3** even-span apex | Apex claims 2 voxels at even spans, 0.35-cell tolerance. |
| **B4** fill inert | Tier 2: arch = curve closed by foot-to-foot chord; ellipse = disc; caps filled-aware. |
| **I1** opacity | All six type alphas client-configurable, defaults lowered. |
| **I2** right-click cancel | Universal cancel + `GuideCancelGrabPacket`; insert-born points removed on cancel. |
| **I3** GUI rework | Tile rows; initial-shape picker; Favorites placeholder; permanent main rows + Selected-guide section + Deselect. |
| **I4** more shapes | First wave: half-circle, circle, ellipse (primitives+constraints, absorb-or-break). **Polygon/line family added in Session 9 — see F1.** |
| **S8** near-anchor grabs | Fingerprint-cached sampled-curve targeting. |
| **S8** GUI "locked into edit mode" | Main rows permanent; per-guide section appended + Deselect. |
| **S8** draft coarsening stuck | Settled guides render at true scale; coarsening is draft-ghost-only. |
| **S8** SHIFT on re-grab | Cardinal constraint on anchor drags, referenced to the other anchor. |
| **S8** Surface→Volumetric mismatch + plane loss | Surface-exit bake (undoable) + stored-plane restore. |
| **S8** soft points still constraining | Proportional + frame-relative offsets. **NOTE: revisited again in Session 9 (slave-regime) — the S8 fix was not sufficient; see SESSION_9.md.** |
| **S8** Delete greying too subtle | Ghost fonts (~22% alpha), no lit tiles, input guard. |
| **SELECTION THREAD** | Clicking a guide in Create mode selects it for the Selected-guide section; Deselect clears; Delete suppresses. |

---

*End of TODO / outstanding-items handoff.*
