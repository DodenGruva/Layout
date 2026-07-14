# Layout — TODO / Outstanding Items (current: v0.1.52)

> **Purpose.** The running punch-list. Companion to `ARCHITECTURE.md` (the plan), `PROJECT_STATUS.md` (the
> status), and `HANDOFF.md` (the consolidated current-state brief).

---

## ⭐ Top of the list (v0.1.52)

1. **Finish B-S9-1 interaction regression.** v0.1.49–v0.1.52 implemented first-hit voxel picking, robust
   curve-cache invalidation, complete drag snapshots, passive non-deforming Arch lock markers, stale-marker
   removal, and curve-relative insertion ordering. The human confirms that locks no longer shift and the
   latest behavior is better. Test repeated lock → drag → cancel/revert → unlock → relock cycles before
   declaring the bug closed. See OPEN BUGS and `SESSION_13.md`.
2. **Final F4/public multiplayer regression → v0.2.0 candidate.** Test vanilla-server fallback, server
   policy denial, mixed public/private overlays, reconnect persistence, publication, commands, and undo/redo
   around ownership changes. The feature itself is implemented and playtested through v0.1.52.
3. **★ Enormous fine-detail guides (human wants this).** The human wants players to build grand structures
   at fine detail (huge domes etc.). Blocked today by the 3D **scan guard** (`MaxScanCells ≈ 4M`, per
   shape) and the **hard voxel ceiling** (`GuideManager.HardVoxelCeiling = 10M`) that reject un-renderable
   giants. Raising them needs care: per-guide voxel COUNTS would then exceed `int` (the running total is
   already `long` as of v0.1.27), and rendering millions of cubes needs a perf pass (chunked meshes / LOD).
4. Remaining flagged decisions (11a–11r, 16a–16d in `SESSION_11.md`) are cosmetic — walk them
   opportunistically.

**Standing workflow rule (human-set — also in CLAUDE.md):** ship a NEW zip per code iteration into
`..\LayoutZips\`, but update docs / commit ONLY when the human says so. Warn before any context trim if
the docs are stale.

---

## Implemented in Session 13 (v0.1.46 → v0.1.52) — full detail in `SESSION_13.md`

- **Threshold-aware 3D counting:** Box/Cone/Cylinder/Dome/Sphere cap checks can stop once the threshold is
  exceeded instead of building an entire rejected voxel set.
- **Natural drag cap:** an over-cap release safely reconciles, and client drag binary-search clamping stops
  at the largest accepted size without flicker. Playtest-confirmed.
- **Interaction restoration:** exact rendered-voxel first-hit lock picking, full geometry cache fingerprints,
  and complete pre-drag snapshots improve lock targeting and make right-click cancellation reliable.
- **Non-deforming Arch locks:** passive `IsLockMarker` control points do not alter the curve until deliberately
  dragged. DataVersion is now **8** and protocol is **3**. The human confirms lock placement no longer shifts.
- **v0.1.52 follow-up:** unlocked passive markers are removed and new lock/grab insertions preserve their
  position along the curve. This latest behavior is better but remains under wider playtest; B-S9-1 is not
  yet marked closed.

---

## Resolved in Session 12 (v0.1.28 → v0.1.45) — full detail in `SESSION_12.md`

F4 is implemented and playtested on `ClientOnlyFallback`: automatic local authority when the server lacks
Layout; policy-controlled private overlays on Layout servers; Hammer + Flax Twine vanilla-server activation;
the unchanged Layout tool on mixed servers; per-world/per-player persistence with backup recovery; normal
create/edit/reshape/settings/undo parity; `.layout client dispel`; `/layout private`, `/layout public`, and
`/layout client push all`; private HUD/target indicators; mixed-server orange private anchors; ownership and
last-operation routing; and protocol-2 policy/mode/publication messages. DataVersion remains **7** and the
source count is **64**. See `PLAN_CLIENT_ONLY.md` for the final behavior matrix and architectural record.

The reported pre-drag restoration and lock/constraint oddities predate F4 and remain parked for a later
interaction-focused pass; they are not ClientOnlyFallback regressions.

---

## Resolved after Session 11's finalize (v0.1.20 → v0.1.27) — condensed; full detail in `SESSION_11.md`

The human pivoted to **3D volumes** (long parked as "LATER"), then iterated fixes. Committed to `main` and
pushed to GitHub through v0.1.27.

- **v0.1.20 Sphere · v0.1.21 Dome/Cylinder/Cone/Box** (the **3D volume family**, playtest-CONFIRMED):
  `GuideShapeTypes.IsVolume`; hollow = one-cell shell, filled = solid, via a **cell-lattice scan** (exact
  for sphere/box/dome, centre-banded for cylinder/cone) with a `MaxScanCells` **scan guard**. Sphere/Dome
  = 2-click; Cylinder/Cone/Box = 3-click (base + height, reusing `NeedsApexClick`). Always Volumetric;
  Surface + Divisions gated off. Wireframe targeting; deterministic up-axis. Own **3D catalog section**.
- **v0.1.22–0.1.23 (3D fixes, CONFIRMED):** `SetShape` whitelist fix (3D shapes no longer fall back to
  Arch); centred **2D/3D section labels**; catalog stays expanded after a pick; centred row labels;
  Divisions row hidden on volumes; **height-inversion fix** (`ShapeGeometry.BaseNormal` — axis stops
  flipping when the 2nd base point crosses sides); **free-air height** for Cylinder/Cone/Box.
- **v0.1.24–0.1.25 (client-lifecycle fixes, CONFIRMED):** GUI icons re-register per client start (fix
  blank tiles after exit-to-title → re-enter — was a process-static guard vs. a fresh `CustomIcons` dict);
  pinned favorites now persist (save-on-change + the real bug: `ObjectCreationHandling.Replace` so
  Newtonsoft stops appending saved pins to the default list, which `Normalize` then trimmed off); standalone
  Divisions field narrowed to the 76 px polygon width.
- **v0.1.26–0.1.27 (admin + safety, CONFIRMED through 0.1.26):** **`/layout dispel all`** and
  **`/layout dispel <chunk radius>`** (controlserver; namespaced under `/layout` in 0.1.27 so it can't
  clash); a **hard voxel ceiling** (`HardVoxelCeiling = 10M`) that rejects un-renderable giant guides
  regardless of caps — no more invisible guides silently maxing the world total; **clear in-game errors**
  on server-side placement rejection (were silent — the flash was tied to a not-yet-existent guide id); the
  running voxel total widened `int → long` (can't overflow-wrap negative on a caps-off server).

**No new persisted/wire fields** for the volumes — they reuse `ControlPoints` + `ShapePlaneAxis`; the
enum values are appended. DataVersion stays **7**. New files: `SphereShape`, `DomeShape`, `CylinderShape`,
`ConeShape`, `BoxShape` → **59 source files**.

---

## Resolved in Session 11 (v0.1.14 → v0.1.19) — condensed; full per-version detail in `SESSION_11.md`

The queued backlog, the Free-Shape, and a long GUI-polish loop — all playtest-CONFIRMED.

- **The Session-10 backlog (0.1.14):** B-S10-2 air-side bake fix; three-click triangle; **CTRL = cardinal
  constraint**, **SHIFT = draft-invert + placed-guide spring-back** (all shapes default "up"); **Polygon**
  (N-gon, 3–24 sides); auto-size tooltips; thinner Surface slabs. DataVersion 5 → **6**.
- **Favorites + Free-Shape (0.1.15):** hard-kept 4-slot favorites (star/unstar, never evict); the yellow
  **Current Shape chip**; SHIFT-centred triangle apex; the **Free-Shape** irregular polyline (chained
  clicks; click-last = open, click-first = close; body inserts like the arch; fill deferred). `IsClosed`
  → DataVersion **7**.
- **GUI polish (0.1.16–0.1.19):** Sides floor at 3; 5-wide catalog + separator; favorites as YELLOW glyphs;
  Projection+Fill and Divisions+Sides on shared rows; HUD Current Shape chip; Delete-mode "-ghost" tile
  greying; Free-Shape SHIFT = vertical segment; pin/unpin wording; Fill greyed on Free-Shapes; zips moved
  to `..\Layout Zips\`.

---

## Resolved in Session 10 (0.1.10 → 0.1.13) — full record in `SESSION_10.md`

Icon-tile UI pass (`LayoutToolIcons`, Cairo glyphs; native N×N scale icons); third **Edit** tool mode
(per-guide settings without the panel expanding); **B-S10-1 fixed** (Surface guides no longer sink behind
the block face on reload — unloaded-chunk-aware air probe + re-probe tick); divisions scroll-wheel on the
native number input, floored at 0; division markers pair on off-cell boundaries.

---

## OPEN BUGS

**B-S9-1 — Lock-in-place interaction regression (ACTIVE FOLLOW-UP; substantially improved in v0.1.49–v0.1.52).**

The original symptoms were an adjacent voxel turning red and the Arch visibly shifting merely because a lock
was placed. The following fixes are now implemented:

- **Rendered-voxel first-hit picking:** the clicked cell, not nearest-point-on-curve, is authoritative.
- **Full geometry cache fingerprint:** post-drag targeting cannot reuse stale curve voxels.
- **Complete pre-drag snapshots:** right-click cancel restores points, constraints, soft flow, and inserted
  gesture state for both server and local authority.
- **Passive lock markers:** locking an Arch no longer inserts an active Catmull-Rom knot. The marker promotes
  only when deliberately dragged. Human-confirmed: placing a lock no longer shifts the guide.
- **Marker lifecycle/order (v0.1.52):** unlock removes passive markers; restored old data is cleaned; inactive
  markers are not adopted or targeted; later inserts are ordered by curve position so right-side grabs do not
  jump toward the apex.

The human reports v0.1.52 is better but wants more testing. Before closing B-S9-1, exercise repeated
lock → drag → cancel/revert → unlock → relock cycles at several scales and on both sides of an Arch. Record a
precise reproduction for any residual jump rather than replacing the confirmed passive-marker design.

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
  first concrete use — see Deferred); Surface flatten's eventual move into the shape layer (`TODO(Surface)`).

---

## Feature ledger (delivered and future)

### F1. Remaining shape catalog — ✅ FIRST WAVE DONE (Session 9)
**Built this session:** Line, Triangle (+ Right / Equilateral / Isosceles), Rectangle (+ Square). *(Verified
against `GuideShapeType` = {Arch,Ellipse,Line,Triangle,Rectangle}, `ShapeConstraint` =
{None,SemiCircle,Circle,Right,Equilateral,Isosceles,Square}, and the shape files — all present.)*
**SECOND WAVE (Session 11): Polygon (regular N-gon, 3–24 sides) — DONE (0.1.14) + Free-Shape (0.1.15).**
**THIRD WAVE (post-Session-11): the 3D VOLUME family — DONE (0.1.20–0.1.23): Sphere, Dome, Cylinder,
Cone, Box.** The "planar-only, 3D LATER" decision has been **reopened and delivered** — see the resolved
section near the top. Natural next volumes if wanted: **Roof, Tunnel** (a walk-through extruded arch).

### F2. Favorites — ✅ DELIVERED AND CONFIRMED (Session 11, 0.1.15)
The picker has four hard-kept pinned slots + a ▾ catalog fold-out; right-click pins/unpins and never evicts an
existing favorite. Persisted per-player in `layout-client.json` as shape codes (each code = a
{type + constraint} pair).

### ★ MAJOR — F4. Client-only / server-less fallback mode — ✅ DELIVERED (v0.1.28–v0.1.45)

The implemented design deliberately differs from the original feasibility sketch: it uses a tangible
vanilla-item gate (Hammer offhand + Flax Twine main hand) on servers without Layout, persists private guides
per world/server + player UID, and retains normal locks/caps/undo semantics through a client-side instance of
the same `GuideManager`. On Layout servers, private mode is denied by default unless the server enables
`allowClientOnlyMode`; mixed-mode players keep using the real Layout tool. Public and private guides share
one renderer/controller, ownership routes edits, and last-operation authority routes undo/redo.

The finalized behavior matrix, command/config contract, persistence/recovery details, publication semantics,
protocol rules, and remaining validation are in `PLAN_CLIENT_ONLY.md`. The only remaining F4 work is the
release-candidate regression pass listed below.

### F3. Re-constrain op (idea, unrequested)
The inverse of a break: a menu action to snap a free shape back under a constraint (arch → half-circle,
ellipse → circle, triangle → equilateral, rectangle → square) with a best-fit. Natural undo pairing exists.
Park until asked.

---

## Next session — start here

**F4 is feature-complete and playtested through v0.1.52 on `ClientOnlyFallback`.** The agenda:

1. **Finish B-S9-1 interaction testing.** Focus on repeat lock/drag/cancel-or-revert/unlock cycles and
   curve-relative targeting around multiple markers. The latest iteration is improved, not yet declared final.
2. **Final F4/public multiplayer regression → v0.2.0 candidate.** Cover vanilla-server fallback, policy
   denial, mixed public/private placement and editing, reconnect persistence, publication, commands, and
   undo/redo around ownership boundaries.
3. **Enormous fine-detail guides (human wants this):** raise the scan guard / hard ceiling + a rendering
   perf pass (chunked meshes / LOD).
4. Then, if asked: **Roof / Tunnel** volumes; a concave-safe **Free-Shape fill**; broadcasting the whole
   Free-Shape draft chain to other players (11q); **F3 re-constrain op**.

**Workflow reminders:** every code iteration ships a NEW `Layout<version>.zip` into `..\LayoutZips\`;
docs are updated ONLY when the human says so; commits/pushes only when the human instructs. Continue on
`ClientOnlyFallback`; do not merge to `main` without explicit direction.

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
