# Layout — Session 9 Record (standalone)

> **What this is.** A self-contained record of everything that happened in **Session 9**, kept separate from
> the main doc set as a safety copy. If a doc merge goes wrong, this file is the source of truth for what
> Session 9 changed. It picks up exactly where `PROJECT_STATUS.md` (Session-8 end, v0.1.0 batch5d) left off.
>
> **Fidelity note.** This was reconstructed from the working conversation, not regenerated from the code on
> disk. **Verified against source on 2026-07-05 — every ⚠-tagged identifier checked out; nothing was wrong.**
> Confirmed against the code: `GuideShapeType` = {Arch, Ellipse, Line, Triangle, Rectangle};
> `ShapeConstraint` = {None, SemiCircle, Circle, Right, Equilateral, Isosceles, Square}; `DataVersion` = 5
> (`GuideData.CurrentDataVersion`); `SoftPointFlow.Capture(pts, grabbedIndex)` with the slave/structural
> regime split; new files `LineShape`, `TriangleShape`, `RectangleShape`, `ShapeGeometry`, `DivisionMarks`,
> `SetDivisionsCommand` (49 source files total); `GuideSetDivisionsPacket` present in `PacketTypes.cs` **and**
> registered; `DivisionMarks.Apply` (renderer-side, `MaxDivisions = 256`); `GuideManager.SetDivisions`;
> `DefaultDivisions` in `LayoutClientConfig`; `VoxelRenderType.Division` (magenta). The inline ⚠ tags below
> are left struck-through-in-spirit (removed) with the confirmed identifier noted.

---

## Session 9 at a glance

Session 9 continued directly in the same conversation after the Session-8 handoff docs were written, so the
uploaded Session-8 docs do **not** reflect any of the below. Three threads:

1. **Soft-point / apex behavior — multiple further iterations** ending in the "slave-regime" model. The
   Session-8 proportional-flow fix was still not right in play; Session 9 reworked it until grabbing felt
   correct ("MUCH better" — human).
2. **New shapes shipped** — Line, Triangle (+ Right / Equilateral / Isosceles), Rectangle (+ Square). This
   is **F1 from the Session-8 "Future features" list, now built.**
3. **Divisions — a brand-new feature** — a purely visual equal-parts overlay on any guide.

Plus the **lock bug (B-S9-1)** remained **unresolved** at session end after several attempts, and a final
**deferred UI request** (divisions scroll-wheel) was logged but not yet built.

---

## Thread 1 — Soft-point / apex flow (the long-running one)

**The complaint that reopened it.** After Session-8's proportional+frame-relative flow, the apex (and any
previously-grabbed unlocked point) *still behaved like a pin* — grab an inserted point between the apex and an
anchor, and the apex resisted / the curve tried to pass through it. The human's spec, stated plainly: **while
dragging any point, the arch must be defined by anchors + locked points + the point in hand, and NOTHING
else — the apex and every other unlocked point should behave as if they don't exist.**

**The iterations (in order):**

1. **Trigger generalized** *(confirmed)* — flow previously fired only when a *structural* point was dragged;
   changed so flow fires on **every** drag, with the **held point folded into the baseline**. This fixed the
   "no flow at all when dragging a soft point" case (the apex froze because nothing recomputed it). The
   canonical case — insert between apex and anchor, then drag — required the **insert-adoption path** to also
   capture flow, which it now does. **Confirmed in code:** `SoftPointFlow.Capture(pts, grabbedIndex)` gained
   the grabbed-index parameter; three call sites exist — server move handler (`ServerNetworkHandler` line
   ~443), client `StartGrab` (~579), client insert-adoption (~700).
2. **Curved baseline** *(confirmed — `SampleBaselineCurve`, centripetal α=0.5 with mirrored phantom ends)* — the flow baseline became a **centripetal Catmull-Rom through
   anchors+locked+held** (densely sampled, mirrored phantom ends), replacing straight chord segments.
   Rationale: against straight chords an apex's captured offset encoded nearly the whole arch height, so it
   kept "pulling." This reduced but did not eliminate the resistance.
3. **Slave-regime (the settled model)** *(confirmed — `Capture` sets zero offsets on an interior grab)* — the decisive change. **Two regimes:**
   - **Interior grab** (held point is unlocked, non-anchor): the curve is defined by structural + hand only;
     every other unlocked point is **slaved directly onto that curve at its station with ZERO offset** — the
     apex genuinely contributes no pull. Visible consequence, intended and confirmed: grab a tall arch's body
     and the apex **settles onto** the curve through (anchor, hand, anchor); its height is gone unless it was
     locked first. Locking is the *only* thing that pins geometry.
   - **Structural grab** (held point is an anchor or a lock): shape-preserving proportional flow (unchanged
     from Session 8) — otherwise nudging a foot would flatten the arch to the anchor chord.

   **Flagged (Claude's call, playtest to arbitrate):** the regime split itself — anchor drags preserve shape,
   interior drags slave. Human confirmed grabbing was "MUCH better" after this.

**Related fix — chord-invariant phantom drop** *(confirmed — `ArchShape`: `drop = Max(MinPhantomDrop, 0.4·chord)`)* — the arch's phantom end-tangent points were derived
by **reflecting the neighbor knot's height**, so inserting/locking a point near a foot re-derived the phantom
and **re-tilted the whole curve** (a whole-arch lurch from one click). Changed to derive the phantom drop from
the **anchor chord** (0.4×chord), invariant under interior operations. A fresh arch is pixel-identical (apex
sits at 0.4×span, which equalled the old reflection). This was *intended* to also fix the lock-shift bug; it
helped but did **not** fully resolve B-S9-1 (see below).

---

## Thread 2 — New shapes (F1: the polygon/line family)

Shipped the remaining first-wave catalog. All use the **same two-click gesture** and route through the
existing `ShapeFactory` / constraint / fill / wire / GUI-tile plumbing.

- **Line** — two anchors; no interior points, no fill; insert is a defensive no-op. *(confirmed: `LineShape`.)*
- **Triangle** — two clicked **base anchors + a real draggable apex** born at the equilateral position; free
  = scalene. Constraints are apex-derivation rules *(confirmed: `TriangleShape`, apex = control point index 2)*:
  - **Right** — 90° at the first click; apex slides on the perpendicular at that anchor.
  - **Equilateral** — apex fully derived (√3/2·base on the bisector); dragging it **breaks to free** (the
    circle→ellipse absorb-or-break pattern).
  - **Isosceles** — apex slides on the base's perpendicular bisector.
- **Rectangle** — the two clicks are **diagonal corners** (stored); the other two derive in the intrinsic
  plane and render as markers (Primary/green). **Square** = constraint (sides track the dominant diagonal
  component). *(confirmed: `RectangleShape`; derived corners painted `VoxelRenderType.Primary`.)*

**Cross-cutting:** *(all confirmed against source)*
- New shared helper **`ShapeGeometry`** (planar-frame derivation + nearest-claim marker placement, extracted
  from the ellipse recipe).
- `GuideShapeType` extended (Line / Triangle / Rectangle appended, pinned values).
- `ShapeConstraint` extended (Right / Equilateral / Isosceles / Square appended).
- **DataVersion 4 → 5** (older saves migrate by defaults).
- **Body-click policy generalized** from "ellipse family" to "any non-arch parametric shape": body
  left-click → grab nearest handle; body right-click → toggle nearest handle's lock; only the **arch family
  (free splines)** takes body inserts.
- **GUI shape picker became an 11-tile grid** (four per row): Arch, Half-circle, Circle, Ellipse, Line,
  Triangle, Right, Equilateral, Isosceles, Rectangle, Square.
- Fill works where meaningful (triangle interior, rectangle box); ignored on lines.

---

## Thread 3 — Divisions (new feature)

A **purely visual** equal-parts reference overlay, per guide. Never affects geometry, counts, or caps.

- **What it does** *(confirmed)* — recolors the voxels at the N-equal-part boundaries **by arc length** along the
  guide's curve/perimeter. Marks are **magenta** (`VoxelRenderType.Division`, appended). Open shapes get the
  N−1 interior boundaries; closed loops get all N (the seam mark yields to the anchor marker by precedence).
- **Where it's computed** — **renderer-side**, after the shape produces its cells (`DivisionMarks.Apply`);
  it's a recolor of existing cells, so shapes/caps/counts are untouched. *(confirmed: `DivisionMarks.Apply`, called from `GuideRenderer`.)*
- **Data / wire / undo** *(confirmed)* — `GuideData.Divisions` (int; 0/1 = none); DataVersion 5 carries it;
  `RenderSettingsDto` + `GuideDataDto` additive fields; new `GuideSetDivisionsPacket` (both directions);
  new `SetDivisionsCommand`; `GuideManager.SetDivisions` (pure recolor — clamp + persist, no cap check).
  `MaxDivisions = 256` clamp. **Note:** the packet class was accidentally omitted in the first build pass
  (a scripted edit silently no-op'd) and caused two CS0246 errors; fixed by inserting the class — worth a
  glance that it's present and registered.
- **GUI** — a **preset dropdown + a type-in field** (stock VS has no editable combobox, so the "editable
  dropdown" was built as this composite). Present in both the tool-defaults section and the per-guide
  Selected-guide section. *(confirmed: `GuideToolGui.AddDivisionsControl`; the preset dropdown is still present — the scroll-wheel request that removes it is not yet built.)*
- **Config** — `DefaultDivisions` in `layout-client.json`, seeded/persisted through the mod system. *(confirmed: `LayoutClientConfig.DefaultDivisions`, clamped 0..`MaxDivisions`.)*

---

## Open at Session-9 end

### B-S9-1 — Lock-in-place still broken (UNRESOLVED — top priority)
After **several** Session-9 attempts (near-point→lock-toggle conversion; chord-invariant phantom drop;
slave-regime flow), the human reported it **still wrong** in play: the **targeted voxel is often not the one
locked** (an adjacent voxel goes red), and **the guide still shifts/deforms** when the lock lands. Intended
behavior, restated by the human: *only* the targeted voxel locks and turns red; **no** movement of the guide
ever, except when the user is actively moving it.

**Leading un-tried suspect (best next step):** targeting resolves body hits to *nearest-point-on-curve to the
ray*, which can land in a cell **adjacent** to the aimed voxel — fix candidate is **ray-vs-voxel-box first-hit
picking** so the clicked *cell* is authoritative. Secondary suspects: residual phantom neighbor-X/Z tracking
when locking near a foot; centripetal-CR reparameterization from the inserted knot. Parked at the human's
direction ("make note of this bug, but let's move on").

### Deferred UI request — Divisions scroll-wheel (logged, not built)
Human's final instruction (no action taken yet, recorded for the next build pass):
- **Remove the divisions dropdown entirely.**
- **Keep the manual type-in field.**
- **While that field is focused, mouse scroll wheel increments/decrements the value** (±1 per notch, clamped
  0..`MaxDivisions`, same as typed input).

### Flagged decisions carried forward (Session-8, still unreviewed)
The Session-8 flagged list still stands (ellipse nearest-handle grab/lock; ellipse minor = ½ major; minor
handle slides on axis; shape read-only in Selected-guide section; the appended per-guide section itself;
SHIFT re-grab reference = other anchor; circle→ellipse break floor; bake cell-side; proportional soft flow
torture-test). **New Session-9 flagged calls:** the slave/structural **regime split**; triangle's
2-click-plus-born-apex gesture; Right/Isosceles/Square having **no break gesture** in v1 (drags always
absorb); rectangle's derived corners being **markers, not grabbable**; the **magenta** division color; and the
divisions type-in field **applying per keystroke** (typing "12" briefly applies 1 then 12 → two sends/undo
steps on a selected guide).

---

## Suggested first moves next session

1. **Fix B-S9-1** with the ray-vs-voxel-box picking approach — it's the top open bug and blocks the
   "guide only moves when I move it" invariant.
2. **Build the divisions scroll-wheel change** (small, fully specced above).
3. ~~Verify every **⚠ verify** identifier in this doc against the code on disk.~~ **DONE 2026-07-05** — all
   confirmed accurate; tags resolved in place (see the fidelity note above for the confirmed-identifier list).
