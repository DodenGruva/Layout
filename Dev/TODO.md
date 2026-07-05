# Layout — TODO / Outstanding Items (post-Session-9)

> **Purpose.** The running punch-list. Companion to `ARCHITECTURE.md` (the plan) and `PROJECT_STATUS.md`
> (the status). Renamed from `OUTSTANDING_ITEMS.md` at the Session-8→9 migration into Claude Code.
>
> **Fidelity note (Session-9 additions).** The Session-9 content below was reconstructed from the working
> conversation, not regenerated from the code on disk. Items tagged **⚠ verify** name specifics
> (file/class/packet/command names, DataVersion, counts) that should be confirmed against the actual source.
> The narrative is reliable; identifiers may need checking. Full Session-9 detail lives in `SESSION_9.md`.

---

## ⭐ Top of the list (Session-9 end)

1. **B-S9-1 — Lock-in-place still broken (UNRESOLVED, highest priority).** See OPEN BUGS below. Blocks the
   core "a guide only ever moves when I move it" invariant. Best next step: **ray-vs-voxel-box first-hit
   picking** so the clicked cell is authoritative (untried).
2. **Divisions scroll-wheel (deferred UI request, fully specced, not built).** Remove the divisions dropdown;
   keep the type-in field; **scroll wheel adjusts the value while the field is focused** (±1/notch, clamped
   0..`MaxDivisions`). See "Deferred / small" below.
3. **Verify the ⚠ tags** in this doc and `SESSION_9.md` against the code — ideal first Claude Code task.

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

---

## Deferred / small (specced, not built)

- **Divisions scroll-wheel** ⚠ verify seam — the last recorded Session-9 request. Drop the preset dropdown;
  keep the manual type-in field; while that field is focused, mouse-wheel up/down increments/decrements
  (±1/notch, clamped 0..`MaxDivisions`, same clamp as typed input). GUI-only change; `DivisionMarks` /
  packet / command layers are untouched.

---

## Flagged decisions awaiting the human's review

Made under the standing "decide, note for review" rule; each is cheap to reverse. None block play.

**Session-9 additions:**

1. **Soft-flow regime split** (slave for interior grabs, shape-preserving for structural grabs). The decisive
   fix for "the apex acts like a pin." Human confirmed grabbing "MUCH better"; the split itself is Claude's
   call and can be revisited.
2. **Triangle gesture: 2 clicks + a born apex** (base anchors placed, apex spawned at the equilateral
   position), rather than a 3-click draft. Keeps every shape on the same two-click gesture.
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
11. **Shape read-only in the Selected-guide section** — live guides reshape by grabbing, not menus (a
    menu-driven re-constrain op is possible future work — see F3).
12. **The appended Selected-guide section itself** — per-guide GUI editing kept but demoted below the
    permanent tool rows with an explicit Deselect. Alternative: remove per-guide GUI editing entirely.
13. **SHIFT re-grab constraint reference = the guide's OTHER ANCHOR** (reproduces the drafting feel).
14. **Circle → ellipse is the break floor** — an ellipse does not break further into a free closed spline in
    v1 (terminate-at-ellipse).
15. **Bake cell-side** — baked points land on the plane's positive-side cell; on walls whose solid side is
    positive this can sit one cell into the wall at coarse scales. Upgrade: server-side air-probe mirroring
    the renderer's solid-probe.
16. **Proportional soft flow (structural-grab regime)** — deserves a stretch/shrink/rotate torture test on an
    arch with several inserted points.

---

## Known costs / tuning (deliberate, watch in real play)

- **Filled guides recount exactly per drag update** (cells generated each move packet). If big filled discs
  drag sluggishly → add a per-drag count cache. Correctness-first per the standing rule.
- **Settled guides always mesh at full resolution** (the coarsening-leak fix). Huge guides pay their real
  cost on every rebuild — watch for hitches while dragging points of 8k+-voxel guides.
- **`PreviewFullResVoxelCap` = 8,000** — draft-ghost-only; tune if huge drafts stutter.
- **Division marks add a render-side pass** ⚠ verify — a per-rebuild recolor over the shape's cells; cheap,
  but it does walk the cell list. Watch on very high division counts × large guides.
- **Ghost-greying** — if the API's toggle buttons expose an `Enabled` flag, native disabled state beats the
  alpha-ghost approach (`GuideToolGui` tile-row seam).
- Carried from Session 7: item transforms; recipe balance; scroll-wheel bindings (the divisions field is the
  first concrete use — see Deferred); stale color comments (cosmetic); Surface flatten's eventual move into
  the shape layer (`TODO(Surface)`).

---

## Future features (specced or sketched, not started)

### F1. Remaining shape catalog — ✅ FIRST WAVE DONE (Session 9)
**Built this session:** Line, Triangle (+ Right / Equilateral / Isosceles), Rectangle (+ Square). ⚠ verify
against `GuideShapeType` / `ShapeConstraint` / the shape files. **Still open / suggested:** N-gon (regular
polygon, arbitrary side count) as the natural next catalog addition. The **planar-vs-3D decision remains
SETTLED: 3D volumes (spheres, cones…) are in scope LATER**; current shapes stay planar (per-shape plane is in
the contracts via `ShapePlaneAxis`).

### F2. Favorites (the I3 sub-feature — placeholder is already in the GUI)
Star-toggle per catalog tile; persists per-player in `layout-client.json`; stores **{type + constraint}**
pairs. Now a **stronger candidate** because the catalog has grown to 11 tiles — a favorites row saves real
scrolling. Open sub-questions: star hit-target precision inside a tile; max count vs wrap; toggle-only
affordance.

### F3. Re-constrain op (idea, unrequested)
The inverse of a break: a menu action to snap a free shape back under a constraint (arch → half-circle,
ellipse → circle, triangle → equilateral, rectangle → square) with a best-fit. Natural undo pairing exists.
Park until asked.

---

## Next session — start here

1. **Fix B-S9-1** (ray-vs-voxel-box picking) — top open bug; blocks the "only moves when I move it" invariant.
2. **Build the divisions scroll-wheel change** (small, fully specced).
3. **Verify ⚠ tags** against the code (natural first Claude Code task — it reads the source directly).
4. Then, if feature-shaped: **F2 Favorites** (now that the catalog is large) or **F1's N-gon**.
5. **Walk the flagged list** with the human when convenient.

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
