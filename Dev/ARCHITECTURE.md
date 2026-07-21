# Layout — Architecture Document (v3.8)

**Supersedes v3.7 — adaptive large-guide drafting, safe giant-grab rollback, and persistent structural
wireframes (v0.3.0–v0.3.8).** v2.5 consolidated five
revisions into the **Settled Decisions Register** below; v2.6 folded in **Session 9** (extended shape
catalog, Divisions overlay, slave-regime flow); v2.7 folded in **Session 10** (icon-tile GUI, the B-S10-1
surface-reload fix, the divisions number input, the third **Edit** tool mode, paired division markers).
v2.8 folded in Session 11 and the post-finalize 3D work: the **three-click triangle**, the
**CTRL/SHIFT remap** (CTRL = cardinal, SHIFT = draft-invert + placed-guide spring-back) with
**every-shape-defaults-up**, the **Polygon** and **Free-Shape**, the **hard-kept favorites picker + catalog
fold-out**, a long GUI-polish loop, the **3D VOLUME family** (Sphere/Dome/Cylinder/Cone/Box — cell-lattice
scan, always Volumetric), the **`/layout dispel` admin commands**, the **hard voxel ceiling**, and the
**running-total→`long`** widening. **v2.9 folds in F4:** automatic local authority on servers without Layout,
policy-controlled private overlays on Layout servers, the side-neutral `GuideManager` seam, per-world/per-UID
client persistence, mixed ownership/undo routing, private publication, and ownership presentation. **v3.0
folds in the cap-performance and interaction pass:** threshold-aware 3D counting, natural at-cap drag
clamping, rendered-voxel first-hit picking, complete drag snapshots, robust curve-cache invalidation, and
passive non-deforming Arch lock markers. **v3.1 added the exact surface-area-oriented hollow Sphere/Dome
scanner and recorded the now-playtest-confirmed mesh frontier. v3.2 folded in F5 — the Chalking Kit: the
custom deflating 4-state model, 32-chalk durability with powder refills, ground storage + in-place ground
refill, the private-placement charge packet (protocol 4), and the placement/refill feedback effects. **v3.3
folds in the mesh + polish arc (v0.2.10–v0.2.21):** large-guide **exposed-face meshing (Stage A)** with a
solidity-aware z-fight inset, the **retirement of filled 3D volumes** (always hollow shells now), the
dome-faces-clicked-surface fix, the HUD hover re-measure fix, GUI number-field alignment, the restored
draft-cap clamp, the fifth (**High**) fill state, and **server-config-gated hotbar/inventory refill
channels** (protocol 5). **v3.4 folds in the seven-item human backlog (v0.2.22–v0.2.23):** the chalk refill
channels moved from server config to a **client preference** (protocol 6, via the new
`ChalkRefillPrefsPacket`), a **hard 32-chalk ceiling** immune to other mods' crafting-quality bonuses, the
removal of the hotbar refill-off warning, an audit of the multiplayer draft packet rate (no change needed),
and publication readiness — authorship, **all-1.22.x** targeting, and portable build paths. **v3.5 folded in
the Tapered Cylinder delta (v0.2.24–v0.2.28); v3.6 removes its fixed scan ceiling when server caps are
raised/unlimited, stabilizes the height→rim transition with release-and-annulus capture, adds voxel-count-aware
throttling for expensive draft work, and expands placement feedback with capped zero-gravity dust across 2D
curves and full 3D shells. **v3.7 closes B-S9-1 through exact rendered-cell ownership, adds straight and
tapered Polygonal Prisms, simplifies the private dispel and ground-storage gestures, introduces stage-aware
native modifier notes, persists polygon flat-side alignment, adds 45-degree Line/Free-Shape constraints, and
makes tapered-rim flare opt-in. **v3.8 adds motion-sensitive structural previews, selected-scale cursor
precision, generation-safe background refinement and batched materialization, cached placed-guide metadata,
retained-mesh cancel quarantine, canonical/persistent Shell↔Wireframe form, size-weighted placement sound,
and surface-proportional oversized Cylinder/Cone/Box fallback generation.** The register, file tree, module
map, persistence, and edge cases below are updated in place to the v0.3.8 / DataVersion 11 / protocol-11 /
74-file state; the per-revision deltas live
in **`CHANGELOG_ARCHITECTURE.md`** (indexed below).

**Where the project stands:** Layout **v0.3.8** is built, packaged, and documented locally; `main` was last
pushed through **v0.2.47** (the `ClientOnlyFallback` branch merged via PR #1).
All seven modules, the complete 2D/3D catalog, normal public multiplayer, vanilla-server local
fallback, mixed public/private operation, and the full F5 chalk system run against **VS 1.22.x** / .NET 10.
The catalog is **15 shape types / 21 picker tiles**, **DataVersion 11**, **protocol 11**, and **74 source
files**. F4 and F5 are feature-complete. The large-guide mesh Stage A remains, and v0.3’s adaptive interaction
pipeline is playtest-successful on a behemoth guide. Filled 3D interiors are retired; volumes persist as a
hollow Shell or canonical structural Wireframe.
The Session-16 backlog is fully delivered. Public/private multiplayer passed the v0.2.35 release test,
fired-jug/raw-jug crafting is confirmed, and B-S9-1 closed with a human-approved v0.2.36 playtest. Remaining:
ordinary v0.3.8 field soak and mesh Stage B (spatial chunks + culling) only if settled rendering—not drafting
calculation—proves insufficient.
Two items carry
verification debt — the chalk ceiling has not been tested against xskills itself, and 1.22.0 support is
declared but untested. See `TODO.md` and `SESSION_17.md`.

> **▶ IMPLEMENTED — client-only / server-less fallback mode (F4, v0.1.28–v0.1.45).** On a server without
> Layout, a three-second positive-proof detection window falls back to a client-side
> `LocalGuideAuthority`; Hammer offhand + Flax Twine main hand activates the normal HUD and F-menu. On a
> Layout server, private overlays are denied by default and require `allowClientOnlyMode=true`; the real
> Layout tool remains the gate in both public and private placement modes. The same side-neutral
> `GuideManager` supplies local parity, while guide ownership routes edits and last-operation authority
> routes undo/redo. Private guides persist per server/world + player UID. `PLAN_CLIENT_ONLY.md` is the final
> behavior and implementation record; `SESSION_12.md` is the version history.

> **▶ IMPLEMENTED — the Chalking Kit (F5, v0.2.0–v0.2.9; refill channels v0.2.21, moved client-side v0.2.22;
> hard 32 ceiling v0.2.23).** The tool is the
> **Chalking Kit** (custom deflating **5-state** model — full/high/medium/low/empty, FULL reserved for a
> completely full kit; mod name stays Layout) with **32-chalk durability**: completed placements cost
> 2D −1 / 3D −2 (flat, never size-scaled), nothing else costs anything, undo never refunds, and at 0 only
> NEW placement is blocked — the kit can never break (custom clamp; vanilla `DamageItem` is never called).
> **32 is a HARD ceiling** (`ItemGuideTool.MaxChalk`, v0.2.23): chalk is read straight off the stack
> attribute and clamped, and `GetMaxDurability`/`GetRemainingDurability` are overridden **without calling
> base**, because base walks the collectible's BEHAVIORS — the hook other mods (xskills) use to grant a
> crafting-quality durability bonus. An inflated kit self-heals to 32 on next use.
> **Chalking Powder** refills +4 per powder through three channels: **ground storage** (SHIFT+right-click a
> set-down kit; always allowed, the bag re-inflates live), the **hotbar** tap/hold shortcut, and a
> **cursor-onto-inventory-slot** click — the latter two are **client-preference opt-in**
> (`allowHotbarChalkRefill` / `allowInventoryChalkRefill` in `layout-client.json`, both default false; a
> player setting, not server policy as of v0.2.22). **Private placements on a
> Layout server charge too** via the client-reported, server-validated `ChalkChargePacket`; the only chalk-free
> case is a server without Layout, where no custom item can exist. Creative exempt; `enableChalkDurability`
> server config. Ground storage: SHIFT+right-click set-down, idle-gated. Placement feedback keeps the
> falling whole-guide chalk flecks and bow-release snap, plus capped zero-gravity dust: broad sideways scatter
> along 2D curves and outward/upward drift distributed across full 3D shells.
> `PLAN_CHALKING_KIT.md` carries the design rationale and plan-vs-shipped deltas; `SESSION_15.md` +
> `SESSION_16.md` are the version history.

---

## Document changelog — index

Per-revision deltas for THIS document (v2.5 → v3.8) now live in **`CHANGELOG_ARCHITECTURE.md`**. They are
not repeated here: every delta is already folded in place into the register / file tree / module map /
persistence / edge cases below, and each has a fuller narrative in its session record. Use this table to find
*when* something changed; read the body below for *what is true now*.

| Doc rev | Mod versions | Theme | Session record |
|---|---|---|---|
| v3.8 | v0.3.0 → v0.3.8 | Adaptive draft/materialization; cached hover; safe cancel; persistent Shell/Wireframe | `SESSION_21.md` |
| v3.7 | v0.2.36 → v0.2.47 | Precise locks; polygonal volumes; stage-aware help; flat/diagonal/rim modifiers | `SESSION_20.md` |
| v3.6 | v0.2.29 → v0.2.35 | Unlimited-cap tapered-cylinder semantics; safe rim capture; adaptive draft work; full-shape dust | `SESSION_19.md` |
| v3.5 | v0.2.24 → v0.2.28 | Tapered Cylinder (4-click frustum, protocol 7); scan-guard and cap-clamp fixes; raw-vessel recipe fix | `SESSION_18.md` |
| v3.4 | v0.2.22 → v0.2.23 | Refill config → client (protocol 6), hard 32-chalk ceiling, publication readiness | `SESSION_17.md` |
| v3.3 | v0.2.10 → v0.2.21 | Mesh Stage A (exposed-face), filled-volume retirement, refill channels, polish | `SESSION_16.md` |
| v3.2 | v0.2.0 → v0.2.9 | The Chalking Kit (F5): durability, powder refills, ground storage | `SESSION_15.md` |
| v3.1 | v0.1.53 | Hollow-shell scaling (`SphericalShellScan`); mesh frontier confirmed | `SESSION_14.md` |
| v3.0 | v0.1.46 → v0.1.52 | Cap performance + interaction correctness; passive lock markers | `SESSION_13.md` |
| v2.9 | v0.1.28 → v0.1.45 | ClientOnlyFallback (F4): local authority, private overlays | `SESSION_12.md` |
| v2.8 | v0.1.14 → v0.1.27 | Session 11 + the 3D volume family; CTRL/SHIFT remap; voxel ceiling | `SESSION_11.md` |
| v2.7 | — | Session 10: icon-tile GUI, Edit mode, divisions input | `SESSION_10.md` |
| v2.6 | — | Session 9: extended shape catalog, divisions overlay, slave regime | `SESSION_9.md` |

---

## Overview — what Layout is

Layout is a mod for Vintage Story 1.22.3 (C#/.NET 10) that provides a persistent, visual, voxel-resolution
construction planning tool — a CAD-like drafting assistant inside the game. Players place geometric guide
overlays in the world and use them as visual references while building by hand; the mod never places, removes,
or modifies blocks automatically. Guides are visual-only, rendered as translucent voxel meshes that are
world-shared and visible to all players on a server, persist across logout and chunk unloading, and are stored
server-side with server-authoritative networking. A guide is only *referenced off* a block at placement time,
never bound to it, and is visible-but-untargetable when the tool isn't held, so it never interferes with the
blocks underneath.

The tool is a held item with an F-key **tile menu** (Create/Edit/Delete mode, the shape picker, voxel scale
1×1×1–16×16×16 defaulting to the finest to match chisel resolution, projection, plane, fill); all interaction
uses first-person clicks and crosshair targeting rather than transform gizmos. The **shape catalog** is
**15 shape types shown as 21 picker tiles**, split into a **2D section** — arch · half-circle · circle ·
ellipse · line · triangle (+ right/equilateral/isosceles) · rectangle (+ square) · polygon (regular N-gon) ·
Free-Shape (irregular polyline) — and a **3D VOLUME section** — sphere · dome · cylinder · tapered
cylinder · polygonal prism · tapered polygonal prism · cone · box. It is
built on the **primitives+constraints** model (a half-circle is an arch under a SemiCircle constraint, a
circle is an ellipse under a Circle constraint, a square is a rectangle under a Square constraint, and the
triangle constraints derive the apex); constrained variants are **not** separate types. **Most shapes place
with a two-click gesture**, with the deliberately reopened exceptions: the free/right/isosceles triangles and
the 3D cylinder/polygonal prism/cone/box take **three clicks** (base, then a height click), the Tapered
Cylinder and Tapered Polygonal Prism take **four** (base, height, then a rim click setting the top radius),
and the Free-Shape takes
**unbounded chained clicks** (≤64). Players reshape 2D guides by grabbing points (clicking the body
inserts-and-grabs in one motion on the arch and Free-Shape families, or grabs the nearest handle on every
other parametric shape), locking points as constraints, and relying on two standing contracts:
**absorb-or-break** (a grab a constraint can absorb, it absorbs; one it cannot absorb demotes the shape to
its free parent, seamlessly and undoably) and **soft-point flow** (slave-regime: an interior grab slaves
unlocked points onto the curve with zero offset, a structural grab keeps shape-preserving proportional flow —
locking is the only thing that pins geometry). Each guide can additionally be rescaled, toggled between
volumetric (3D) and surface (flat decal) projection, set hollow or **filled** (arch family: the region closed
by the foot-to-foot chord; ellipse/polygon: the disc/interior; triangle/rectangle: the interior/box;
3D volumes: the solid), given a purely-visual **equal-parts division** overlay, and hidden or shown. **The 3D
volumes are always Volumetric** (Surface and Divisions do not apply to them) and are voxelised by a cell-
lattice shell/solid scan rather than curve-marching.

Colors: yellow body, red locked points, green apex/primary, blue anchors (indigo off-shade for a non-coplanar
far foot), white grabbed; hidden guides show only anchors at reduced opacity. In-progress drafts are
client-side until completed — only the start point is broadcast to other players during placement, and drafts
cancel on logout. Concurrent editing stays safe via full-exclusivity edit locks (first grab wins), voxel caps,
and full per-player undo/redo for all actions including deletion.

---

## Settled Decisions Register (do not reopen without cause)

The distillate of five design revisions and two playtest cycles. Each entry is a settled decision plus the
reason it won. Reversing any of these needs an explicit call from the human, not a fresh session's instinct.

### Interaction model
- **Three modes: Create | Edit | Delete** (Session 10; was two). **Create** owns ALL geometry — left-click
  priority chain: release grab → second foot (a draft outranks guide targeting) → precise point grab → body
  insert+grab in one gesture → first anchor; right-click is the universal cancel / draft discard / idle point
  lock-toggle / idle body **lock-in-place insert** (point born locked on the curve; two undo steps).
  **Edit** is settings-only: left-click **selects** the guide under the crosshair (empty click deselects) and
  the GUI's setting rows then act on that selected guide — **no grab/insert/lock**, so a select-click can
  never reshape. **Delete** dispels (left-click). This restored an Edit mode (the earlier Create/Edit/Lock/
  Dispel scheme was collapsed to two in Session 8 because modes fought the flow; the new Edit adds no geometry
  verbs, so it doesn't). Settled reason: per-guide editing via the buttons needed a home that didn't expand
  the panel with a separate section.
- **No grab snap radius.** Precision is the tool's ethos; a near-miss on the body inserts (that's intent,
  not error), and the accepted miss-case is a stray anchor + right-click.
- **Targeting tests the sampled curve, not control-point chords** (per-guide fingerprint-cached polylines).
  Chords miss the real curve at an arch's feet — the root cause of near-anchor grabs failing.
- **Absorb-or-break** is the constraint contract: half-circle feet and circle diameter anchors absorb;
  a body insert on any constrained shape, or dragging a circle's minor handle, breaks to the free parent —
  server-authoritative, seamlessly materialised, one undo step, instant client preview via a sanctioned
  mirror constraint-clear.
- **Soft-point flow — slave-regime (Session-9 settled model).** Structural = anchors + locked; everything
  else is soft. An **interior grab** (held point soft) slaves every other soft point **onto the defining
  curve at its station with zero offset** — the apex and prior-grabbed points contribute **no** pull, so the
  shape follows the hand; a **structural grab** keeps shape-preserving proportional flow (baseline-local,
  length-scaled offsets) so nudging a foot stretches rather than flattens. Locking is the only pin. History:
  absolute offsets (rejected in play) → proportional (still felt like pinning) → slave-regime (confirmed).
- **Chord-invariant arch phantoms.** The arch's end-tangent phantom drop derives from the anchor chord
  (0.4×chord), not the adjacent knot's height, so inserting or locking a point near a foot no longer
  re-tilts the whole curve.
- **Ellipse family body clicks map to the nearest handle** (grab on left, lock-toggle on right) — a
  parametric ring has nothing to insert. *(Flagged for review, like all Session-8 ellipse ergonomics.)*
- **Comatose grabs:** tool swap suspends a grab (lock + one-undo-entry drag persist server-side), never
  releases it; re-equip validates and resumes. Players need to place ladders mid-drag.
- **CTRL cardinal constraint** (Session 11, human-directed — moved from SHIFT) in two places: drafting the
  second foot (level + cardinal from the first) and re-grabbing an anchor (same snap, referenced to the
  guide's **other** anchor).
- **SHIFT is context-dependent** (Session 11 + Session 20, human-requested; supersedes "SHIFT = cardinal"):
  while drafting, it inverts applicable two-click shapes; centres a three-click triangle apex; constrains a
  Line/Free-Shape segment vertically; aligns a flat side on polygon families; and deliberately permits a
  tapered rim to flare past its base. **CTRL+SHIFT** makes a 45-degree Line/Free-Shape diagonal, while CTRL
  alone stays horizontal/cardinal and closes a tapered rim to a point. Stage-aware native held-help rows make
  the active meaning visible;
  **on a placed guide**, SHIFT+left-click **springs it
  back to its as-placed form** (points + constraint restored from the creation snapshot, one undo step).
  Prerequisite delivered with it: **every shape defaults "up"** regardless of click order (`ShapeGeometry`'s
  frame perpendicular is sign-normalised world-up; constrained triangle re-derivations preserve the apex's
  current side instead of forcing one).
- **Raycast targeting throughout:** anchors require a block target; interior points snap to blocks or move
  at retained grab-depth in air; no scroll-wheel grab-distance (scroll reserved).
- **Guides are visible but untargetable when the tool isn't held** — pure mesh draws, no selection/collision
  geometry, all interaction gated to the held-item path.

### Data & wire
- **Pinned, append-only enums** everywhere a value crosses wire or disk; **default-driven migration** via
  `DataVersion` (currently **11**: v11 added persistent `IsWireframe`; v10 added cached
  display/count/dimensions; v9 added polygon `FlatSideAligned`; v8 added passive
  `ControlPoint.IsLockMarker`; v7 added `IsClosed`
  (Free-Shape loop flag); v6 added `Sides` + the
  as-placed spring-back snapshot (`OriginalControlPoints`/`OriginalConstraint` — persisted, never wired);
  v5 added `Divisions`; v4 added `Constraint`, `ShapePlaneAxis`; v3 added `CreatorUid`; v2 added
  `Projection`/`Plane`/`IsFilled`).
- **Protobuf DTOs are the wire format; JSON is the save format** — never mixed. Packet registration is one
  fixed shared order, **append-only**. POCOs are mapped to DTOs, never sent raw.
- **The index seam:** only control-point indices cross the network/undo boundary
  (`GetNearestControlPointIndex`, edits, commands). Inserts land interior; locks prevent reshuffling mid-grab.
- **Vec3d is always deep-copied, never aliased** (mutable reference type). The one sanctioned mirror writer
  outside the network handler is the drag preview (plus its Session-8 sibling, the local constraint-clear).
- **Voxel cells are 1/16-block, lower-corner, `Floor(world·16/scale)·scale`** — one quantise convention
  everywhere (`VoxelMarch` mirrors `CatmullRomSpline.Quantize` exactly), or caps and visuals disagree.
- **The authority builds shapes** from two/three/four clicks + settings (client never sends full GuideData);
  **tool state
  is client-side** and travels with operations; `CreatorUid` is bookkeeping, never ownership, never wired.
- **Voxels are never stored** — always derived on demand from control points.

### Shapes & geometry
- **Primitives + constraint modifiers, not a flat enum of near-duplicates** (square = rectangle+constraint,
  circle = ellipse+constraint, half-circle = arch+constraint, and the triangle constraints below). Keeps
  `GuideShapeType` short and lets future favorites store {type + constraint} pairs.
- **The catalog (v0.1.23 state):** arch, half-circle, circle, ellipse, **line, triangle
  (+ right / equilateral / isosceles), rectangle (+ square), polygon (regular N-gon, side count = per-guide
  data, 3–24), Free-Shape (irregular polyline)**, and the **3D VOLUME family (v0.1.20–0.1.21): sphere, dome,
  cylinder, tapered cylinder, cone, box** (see the dedicated bullet below). **Placement is two clicks for every shape EXCEPT the
  free/right/isosceles triangles (THREE: anchor · anchor · height — SHIFT on the third click centres the
  apex on the base) and the Free-Shape (UNBOUNDED chained clicks, ≤64: click the LAST placed corner to
  finish open, the FIRST corner (≥3) to close the loop; the aim snaps onto those targets)** — the human
  explicitly reopened the old "every shape is two clicks" rule in Session 11. Right-click steps any
  multi-click draft back one click. Equilateral stays two-click (its apex is fully derived). The polygon's
  two clicks span vertex → opposite perimeter point, so the shape exactly spans the gesture and both
  anchors sit ON the outline. **Constraints are derivation rules:** the triangle apex slides (right →
  perpendicular at first click; isosceles → base bisector) or is fully derived (equilateral), and the
  rectangle's derived corners track the diagonal (square → dominant component).
- **Body-insert policy (0.1.15 revision):** the arch family AND the Free-Shape take body inserts
  (`TakesBodyInserts`); every other shape is parametric — body clicks map to the nearest handle. The
  Free-Shape's inserts are plain straight-line corners (no spline, no soft flow); its fill is DEFERRED
  (irregular outlines can be concave; the Fill toggle is currently inert on it).
- **Break gestures (v1):** equilateral-triangle apex-drag breaks to a free triangle, and circle minor-handle
  drag breaks to an ellipse (the absorb-or-break pattern). **Right / isosceles / square have no break
  gesture** — their constrained drags always absorb; they live as separate catalog tiles.
- **`ShapeFactory` is the single shape construction point** — adding a shape touches the factory + the shape.
  **`ShapeGeometry`** (Session 9) and **`VoxelMarch`** are the shared shape-layer helpers (planar frame +
  marker-claim; and the one true cell-quantise convention, respectively).
- **Centripetal Catmull-Rom (α = 0.5)**, apex at 40% of chord above the midpoint, phantom endpoints derived
  for provably vertical feet.
- **Arch-family fill = the region between the curve and the foot-to-foot chord line** (human-confirmed
  design), as a ruled surface at ≤ half-cell steps; **ellipse-family fill = the disc**; **triangle fill = the
  interior, rectangle fill = the box**; **line has no fill**. Caps count filled voxels **exactly**
  (generated, not estimated) — correctness over performance, per standing rule.
- **Circle → ellipse is the break floor** (v1): an ellipse does not break further into a free closed spline.
  Likewise a free triangle / free rectangle is the floor for its family (no further break in v1).
- **3D VOLUMES — DELIVERED (v0.1.20–0.1.23; the "planar-only, 3D LATER" decision was reopened by the human
  and shipped).** `GuideShapeTypes.IsVolume` gates the family. **Sphere / Dome** = two clicks (a diameter /
  a base diameter); **Cylinder / Cone / Box** = three clicks (base, then a height click — reusing the
  triangle's apex machinery, `NeedsApexClick`). Box is a true box (independent side lengths). **Volumes are
  always hollow rather than solid-filled (v0.2.17)** — 3D Filled is retired because the invisible interior
  was pure cost. Each volume may now persist as a complete **Shell** or canonical structural **Wireframe**
  (`IsWireframe`, v0.3.7); both use the selected scale and exact cap counts. v0.1.53 routes hollow Sphere/Dome
  through `SphericalShellScan`; normal Box retains its lattice scan and normal Cylinder/Cone remain
  centre-banded. When those three legacy bounding scans would cross their old 4M work guard, v0.3.8 switches
  to `LargeVolumeShellFallback` (face/ring work proportional to visible shell area) instead of rejecting the
  guide. Configured caps and the 10M hard ceiling remain authoritative. Volumes are **always
  Volumetric** (Surface + Divisions gated off server-side and greyed/hidden in the GUI). The base plane / axis
  comes from the clicked face; the axis is the **deterministic `ShapeGeometry.BaseNormal`** (+up regardless of
  anchor order — SHIFT is the only invert, e.g. dome → bowl). **Targeting is a wireframe** (equator/meridians,
  rings + verticals, box edges), not every shell cell — the anchors and the height handle are the reliable
  grab points. `ShapeWireframe` is the canonical selected-scale topology path; round volumes have eight ribs
  and polygonal prisms one longitudinal wire per corner. Height may be set in **free air** (no block → the handle follows the view ray; a targeted
  block wins). The original volume catalog reused `ControlPoints` + `ShapePlaneAxis`; v0.3.7 later added the
  shared persisted/wired `IsWireframe` form flag (**DataVersion/protocol 11**). Natural next volumes:
  **Roof, Tunnel**.
- **Fill is a 2D guide property (`IsFilled`); Form is a 3D guide property (`IsWireframe`).** Constraints are
  modifiers, not shape types. The same GUI positions contextually read Hollow/Filled for 2D and
  Shell/Wireframe for volumes.

### Rendering
- **The verified draw recipe:** Opaque stage + manual blend (not OIT); `PreparedStandardShader` overridden to
  full-bright; a **real white texture** (generated 2×2, asset fallback — id 0 samples garbage); the full
  pos+**uv**+rgba vertex layout with uv (0,0). Each element was a genuine independent playtest bug.
- **Single-voxel nearest-claim markers** (anchors/apex/locked/grabbed-White), precedence Locked > Primary >
  Anchor > Division; the apex claims **2 voxels on even spans** (a lone voxel reads off-center by half a
  cell). **Division marks share that even-span pairing** (Session 10, `ShapeGeometry.ClaimMarkerPaired`): a
  boundary landing between two voxels claims both, so the equal parts read even; boundaries on a cell centre
  stay single.
- **Adaptive large-guide drafting (v0.3):** cheap poses render their normal selected-scale shell. Expensive
  moving 3D poses use a structural wireframe under work/frame-pressure hysteresis; at least four selected-
  scale voxels around the cursor remain precise, stepping outward across roughly two blocks. After the settle
  delay, one generation-tagged background task builds the exact selected-scale result. Prebuilt pseudo-random
  mesh batches upload at a bounded cadence; movement invalidates stale work. Pending draft/grab measurements
  show animated calculation glyphs rather than blocking input.
- **Placed behemoth safeguards (v0.3.3–v0.3.5):** display name/count/dimensions are cached; placed hover never
  voxelizes. Giant grabs use a wireframe while retaining the settled mesh. Cancel reveals that mesh
  immediately and fingerprints/quarantines the confirming authority echo so no delayed duplicate shell build
  or stale worker result can expand afterward.
- **Surface guides render as paper-thin slabs (0.01)** hugging the wall face on the **air side** (world
  solidity probe; majority fallback) with a **plane-axis-only** inset. **Volumetric anti-z-fight is a
  per-face geometry inset** (`BlockPlaneInset = 0.003`, v0.2.10–0.2.16), not a camera nudge: a voxel face is
  pulled off a block-grid plane ONLY when it is EXPOSED **and** a solid world block sits across that plane
  (`GuideMeshOptions.IsNeighborSolid`). Faces flush against a neighbour voxel, or bordering air, stay exactly
  on grid — so the inset never opens a seam between two guide voxels, only clears a guide face from a real
  block face. **The air-side probe tolerates the world-load race
  (Session 10):** if a probed cell's chunk isn't loaded yet, the guide's side is *provisional* and a
  low-frequency re-probe tick rebuilds it once the neighbourhood loads — otherwise a guide meshed before its
  blocks arrived sank behind the face on reload (B-S10-1).
- **Settled guides always render at their true scale**; `ChooseRenderScale` coarsening (8,000-voxel cap) is
  a **draft-ghost-only** courtesy — it once leaked into placed guides and permanently degraded them.
- **Large-guide meshing — Stage A shipped (v0.2.14–v0.2.16, `SESSION_16.md`):** `GuideMeshBuilder`'s
  Volumetric cube path now does **exposed-face meshing** — it builds a presence set of rendered cells,
  pre-counts the faces with no neighbour, allocates exactly, and emits ONLY those faces (per-voxel role
  colours preserved). Interior/shared faces vanish, so a ~100-block hollow Sphere draws only its outer skin
  (and, with filled volumes retired in v0.2.17, worst-case counts dropped further). Still one uploaded mesh
  per guide, rebuilt on change; `GuideRenderer` draws all loaded meshes. **The remaining seams for very large
  guides are Stage B/C:** per-guide spatial chunk meshes + culling, then same-colour greedy face merging.
  Surface tile/slab paths stay on the legacy whole-box builder; preserve the verified draw recipe. Full
  invariants + the mesh-count harness are in `SESSION_14.md` §6–§7 and `SESSION_16.md` §2.
- **Leaving Surface bakes the flattened positions into the control points** (undoable, full-state
  broadcast): Surface-mode edits are made against the view, so the view is what leaving it keeps. Returning
  to Surface restores the stored plane; only never-Surface guides get the floor-at-anchor seed.
- **Color language:** yellow body · red locked · green primary/apex · blue anchors with the **indigo
  off-shade** on a far foot that is not level-and-cardinal (an at-a-glance "is this clean?" cue, deliberately
  shifted violet-ward away from green) · white grabbed · hidden guides = anchors only at low alpha. All six
  type opacities are client-configurable.

### Multiplayer, locks, undo
- **World-shared, no ownership.** Any player may edit or dispel any guide; grief is a server-administration
  concern, deliberately out of scope.
- **Full-exclusivity edit locks:** while held, every mutation from anyone else is rejected — geometry,
  toggles, and dispel. `adminCanOverrideLocks` (default true) lets admins override the **atomic** ops only
  (the stuck-lock remedy); geometry genuinely requires the lock. Undo respects the same gate (`Blocked`,
  history preserved).
- **One drag = one undo entry** (live ~10 Hz moves record nothing; release commits origin→final per point —
  soft-flow points included, since origins are captured generically per edited index).
- **Broadcast to everyone, the originator included**, so every mirror stays exact; rejections get a
  corrective full-state resync. **Undo/redo broadcasts generically:** full current state, or a delete if the
  guide is gone — one rule for every command type.
- **Break and bake are single undo steps carrying point snapshots** (`BreakConstraintCommand`; the
  projection command's optional pre-bake snapshot) — the constraint/projection must travel with the exact
  points it had.
- **Systems communicate by return value (`GuideOperationResult`), not events**; undo is validate-then-apply
  with stale-command skip and the `Blocked` outcome for cap-rejected-but-valid commands.
- **Implemented divergence — client-only authority (F4).** Networked public play remains server-authoritative.
  Local guides use a client-side instance of the same `GuideManager`, so locks, constraints, caps, and undo
  semantics remain real rather than becoming no-ops. On mixed servers, public and private guides coexist in
  one mirror; a guide's recorded ownership routes every existing-guide mutation, while placement mode only
  chooses the destination of a new guide. Undo/redo follows the last successful mutation authority. Full
  contract: `PLAN_CLIENT_ONLY.md`.

### Configuration, assets, GUI
- **Server `layout.json`:** perGuideVoxelCap 25,000 · totalVoxelCap 250,000 · maxGuidesPerPlayer 0 ·
  maxGuidesWorldWide 0 · undoHistoryDepth 50 · requiredPrivilege "" · adminCanOverrideLocks true ·
  **allowClientOnlyMode false** · **enableChalkDurability true** (F5)
  (0/negative = unlimited; **construction-time injection — edits need a server restart**). Caps sync to
  clients on join so the pre-check matches enforcement.
  **The two chalk refill-channel flags left this file in v0.2.22** — they are player preferences now, in
  `layout-client.json`; stale keys in an existing `layout.json` are ignored. **The running total is a `long`** (v0.1.27) so a
  caps-off server can't overflow it negative. A **hard voxel ceiling** (`GuideManager.HardVoxelCeiling`,
  10M) rejects giant guides ALWAYS, even with caps disabled. Remaining guarded 3D scans return a huge sentinel
  for over-size filled/volume paths; hollow Sphere/Dome now count exactly beyond the old guard. Do not raise
  the hard ceiling before the mesh pass: a near-ceiling monolithic mesh already lags. Server-side create
  rejections send a **clear in-game
  error** (the HUD cap-flash is keyed to a guide id that doesn't exist yet on a create, so it was silent).
- **Admin commands (v0.1.26–0.1.27):** **`/layout dispel all`** (whole world) and **`/layout dispel <chunk
  radius>`** (Chebyshev radius around the caller), both `controlserver`. Namespaced under `/layout` so they
  can't clash with other mods. Registered in `ServerNetworkHandler`; they delete + force-free locks +
  broadcast, and reset the running total.
- **F4 commands:** `/layout private` asks an allowing Layout server to make new guides local;
  `/layout public` returns new placement to server authority; `/layout client push all` publishes up to 100
  local guides after privilege/cap validation. `.layout dispel all|<chunk radius>` is intentionally a
  client command and deletes only private guides. Push is a committed ownership transfer and is not inserted
  into server undo history.
- **Client `layout-client.json`:** remembers scale / projection / fill / **shape + constraint** (validated
  pairs) / divisions / **sides** plus the six opacities and (Session 11) the **pinned favorite shape codes
  (up to FOUR since 0.1.15; hard-kept — never auto-padded)**; client-retained, never synced. **Default
  scale 1** (chisel-matched). **`forceClientOnly` defaults false** and requests private placement when an
  installed Layout server explicitly allows it; `_forceClientOnlyNote` documents that dependency.
- **The tile GUI (icon form since Session 10):** every control is a row of exclusive SQUARE ICON tiles
  (custom Cairo glyphs — `LayoutToolIcons` — rendered by stock toggle buttons; hover names the option,
  auto-sized since Session 11). **Mode-aware rows (Session 10, replacing the appended Selected-guide
  section):** in **Create** the rows are the tool defaults for the next guide (Mode — **with the 0.1.15
  Current Shape chip at its far right: a permanently-lit, guide-body-YELLOW glyph of the picked shape** —
  the shape picker — **0.1.15: FOUR hard-kept pinned slots + a ▾ catalog fold-out; right-click PINS into a
  free slot (never evicts; message when full) or UNPINS a pinned tile (drawn in the Current-Shape YELLOW
  in the catalog since 0.1.17); empty slots show placeholders; the separate Favorites strip is gone** —
  Scale, Projection+Fill (one row since 0.1.17; **Fill greys out on a Free-Shape**, where fill is
  deferred), Plane, Divisions+Sides-on-polygon (one row)); in
  **Edit** the SAME rows (plus **Visibility**, minus the shape picker) act on the
  selected guide via the network senders — greyed with a "click a guide" prompt when none is selected, with a
  compact guide-info line + **Deselect**; **Delete disables everything but Mode** (native `Enabled=false` dim
  + ghost labels). The panel therefore never grows a second section on selection. **Scale icons = the game's
  native N×N-grid scheme, N = voxel count** (16× = one solid block). **Divisions = a native number input**
  (wheel ±1, spinners, typed, floored at 0, clamped to `MaxDivisions`) — the one non-icon control. F-modal
  press-to-open, not the vanilla radial. Draft settings are **live** — mid-draft changes apply to the ghost
  and the completed guide. The Create header is simply **`Create Mode` + a normal-size right-aligned shape
  name** (v0.2.47); the redundant `- Next guide:` phrase and the rejected adaptive font shrink are gone.
- **Hotkeys are rebindable and gate-aware** (Ctrl+Z/Y never hijack other UIs). On Layout servers the real
  guide tool is required in public and private placement modes. On servers without Layout, any vanilla
  Hammer variant/durability in the offhand + Flax Twine in the main hand substitutes for it; F opens the
  unchanged GUI and the HUD appears immediately. The real tool is the **Chalking Kit** (custom deflating
  **5-state** model — full/high/medium/low/empty, Sessions 15–16): recipe **8× Chalking Powder + linen sack
  + flax twine + rope + copper nails**;
  **32-chalk durability** (F5 — see the ▶ IMPLEMENTED callout up top for the full contract). The vanilla
  Hammer + Flax Twine fallback gate cannot carry custom durability, so a server WITHOUT Layout is the one
  chalk-free mode — physically unenforceable there, by accepted design.
- **Held-item interaction notes are native and stage-aware (v0.2.39–v0.2.45):** Layout recomposes the
  active-slot help when the draft stage changes, exposing only applicable CTRL/SHIFT meanings. Internal
  refreshes suppress the inherited ground-storage note, which appears only on a real item swap.
- **Runtime is .NET 10** (VS 1.22); `Entity.SidedPos` is obsolete — use `Pos`.

---

## 1. File Structure

```
Layout/
├── modinfo.json
├── assets/
│   └── layout/
│       ├── itemtypes/
│       │   ├── guidetool.json            [Chalking Kit: durability 32, GroundStorable, chalkbag-full shape]
│       │   └── chalkingpowder.json       [S15: the refill item; powdered-sulfur look, Messy12 storable]
│       ├── recipes/grid/                 [guidetool (8-powder kit craft) + chalkingpowder (dye mixes)]
│       ├── shapes/tools/                 [chalkbag-{full,high,medium,low,empty}.json — the 5 fill states]
│       ├── textures/                     [per-state kit textures + sulfur + white]
│       └── lang/
│           └── en.json
└── src/
    ├── LayoutModSystem.cs
    ├── Items/
    │   ├── ItemGuideTool.cs              [S15: + chalk helpers, fill-state OnBeforeRender, IContainedMeshSource, ground-store gesture]
    │   └── ItemChalkingPowder.cs         [S15: tap/hold refill — hotbar or ground-stored kit in place]
    ├── Guide/                            [pure data]
    │   ├── GuideData.cs                  [DataVersion 11; + IsWireframe, cached display/count/dimensions]
    │   ├── ControlPoint.cs               [+ IsLockMarker: passive, non-deforming Arch lock]
    │   ├── VoxelPosition.cs              [VoxelRenderType: … Grabbed, Division (magenta, S9)]
    │   ├── GuideShapeType.cs             [15 pinned types through PolygonalPrism/TaperedPolygonalPrism; + IsVolume/UsesSides]
    │   ├── ShapeConstraint.cs            [None, SemiCircle, Circle, Right, Equilateral, Isosceles, Square]
    │   ├── ProjectionMode.cs
    │   ├── ProjectionPlane.cs
    │   └── GuideRenderSettings.cs
    ├── Shapes/                           [pure math]
    │   ├── IGuideShape.cs
    │   ├── CatmullRomSpline.cs
    │   ├── ArchShape.cs                  [free spline + SemiCircle arc mode + ruled fill]
    │   ├── EllipseShape.cs               [closed planar primitive; Circle = constraint]
    │   ├── LineShape.cs                  [S9: two anchors, no fill, insert no-op]
    │   ├── TriangleShape.cs              [S9/S11: base anchors + apex, THREE-click (free/right/isosceles); Right/Equilateral/Isosceles]
    │   ├── RectangleShape.cs             [S9: diagonal corners stored, other two derived; Square]
    │   ├── PolygonShape.cs               [S11: regular N-gon, MinSides 3 / MaxSides 24 (GuideData.Sides)]
    │   ├── FreeShape.cs                  [S11 (0.1.15): irregular polyline; IsClosed; MaxCorners 64; takes body inserts]
    │   ├── SphereShape.cs                [3D (0.1.20; S14 hollow shell scan / guarded filled scan)]
    │   ├── DomeShape.cs                  [3D (0.1.21; S14 exact shell + half-space clip)]
    │   ├── SphericalShellScan.cs         [S14: shared surface-area-oriented hollow Sphere/Dome scan]
    │   ├── CylinderShape.cs              [3D (0.1.21): centre-banded lateral shell; 3-click]
    │   ├── TaperedCylinderShape.cs       [3D (0.2.24): independent top radius; 4-click]
    │   ├── PolygonalPrismShape.cs        [3D (0.2.38): straight/tapered regular-polygon volumes]
    │   ├── ConeShape.cs                  [3D (0.1.21): centre-banded sloped shell; 3-click]
    │   ├── BoxShape.cs                   [3D (0.1.21): independent side lengths; exact shell; 3-click]
    │   ├── ShapeWireframe.cs             [S21: canonical structural topology → selected-scale voxels]
    │   ├── LargeVolumeShellFallback.cs   [S21: surface-only oversized Cylinder/Cone/Box fallback]
    │   ├── ShapeGeometry.cs              [S9: shared planar frame + nearest-claim marker; S11 BaseNormal deterministic up-axis]
    │   ├── ShapeFactory.cs               [the single shape construction point]
    │   ├── SoftPointFlow.cs              [S9: slave-regime (interior grabs) + proportional (structural); both sides]
    │   ├── DivisionMarks.cs              [S9: renderer-side equal-part recolor; MaxDivisions = 256]
    │   └── VoxelMarch.cs                 [shared cell marching, spline-identical quantise]
    ├── Systems/
    │   ├── GuideManager.cs
    │   ├── GuideManagerDependencies.cs  [F4: persistence/recovery, block-probe, and logging seams]
    │   ├── GuideLockManager.cs
    │   ├── DraftManager.cs
    │   ├── DraftPreviewSpec.cs           [S21: immutable generation-tagged refinement work]
    │   ├── UndoManager.cs
    │   ├── GuideRenderer.cs
    │   ├── GuideMeshBuilder.cs
    │   └── ChalkEffects.cs               [S15/S19/S20: falling flecks, shell dust, placement snap]
    ├── Network/
    │   ├── PacketTypes.cs
    │   ├── ServerNetworkHandler.cs
    │   └── ClientNetworkHandler.cs
    ├── UI/
    │   ├── GuideToolGui.cs               [icon-tile GUI (S10)]
    │   ├── LayoutToolIcons.cs            [S10: Cairo glyphs → CustomIcons registry]
    │   └── GuideHud.cs
    ├── Config/
    │   ├── LayoutServerConfig.cs
    │   └── LayoutClientConfig.cs
    ├── Client/
    │   ├── ClientAuthorityMode.cs        [F4: Detecting / Networked / Local]
    │   ├── ClientToolGate.cs             [F4: Layout tool vs. Hammer + Flax Twine]
    │   ├── ClientWorldGuidePersistence.cs [F4: world+UID JSON, atomic replace, backup recovery]
    │   ├── LocalGuideAuthority.cs        [F4: client-side GuideManager + UndoManager]
    │   └── GuideToolController.cs
    └── Undo/
        ├── IGuideCommand.cs
        ├── UndoStack.cs
        └── Commands/
            ├── CreateGuideCommand.cs
            ├── DeleteGuideCommand.cs
            ├── MoveControlPointCommand.cs
            ├── InsertControlPointCommand.cs
            ├── LockPointCommand.cs
            ├── RemoveLockMarkerCommand.cs [S13: remove/restore passive markers on unlock]
            ├── RescaleGuideCommand.cs
            ├── HideGuideCommand.cs
            ├── SetProjectionCommand.cs   [optional pre-bake point snapshot]
            ├── SetFilledCommand.cs
            ├── SetWireframeCommand.cs    [S21: persistent volume Form undo/redo]
            ├── SetDivisionsCommand.cs    [S9: old/new count; undo/redo re-applies]
            ├── SetSidesCommand.cs        [S11: old/new polygon side count]
            ├── SpringBackCommand.cs      [S11: pre/post point+constraint snapshots around a SHIFT spring-back]
            └── BreakConstraintCommand.cs
```

**74 source files** (70 through Session 20, plus Session 21’s `DraftPreviewSpec`, `ShapeWireframe`,
`LargeVolumeShellFallback`, and `SetWireframeCommand`). Historical breakdown: 43 at Session-8 end + 6 new in Session 9: LineShape, TriangleShape, RectangleShape,
ShapeGeometry, DivisionMarks, SetDivisionsCommand; + 1 in Session 10: LayoutToolIcons; + 4 in Session 11:
PolygonShape, SetSidesCommand, SpringBackCommand, FreeShape; + 5 for the 3D family (v0.1.20–0.1.21):
SphereShape, DomeShape, CylinderShape, ConeShape, BoxShape; + 5 for F4: ClientAuthorityMode,
ClientToolGate, ClientWorldGuidePersistence, LocalGuideAuthority, GuideManagerDependencies; + 1 in Session 13:
RemoveLockMarkerCommand; + 1 in Session 14: SphericalShellScan; + 2 in Session 15: ItemChalkingPowder,
ChalkEffects; + 1 in Session 20: PolygonalPrismShape). Namespaces match
folders: `Layout`, `Layout.Guide`, `Layout.Shapes`, `Layout.Systems`,
`Layout.Network`, `Layout.UI`, `Layout.Config`, `Layout.Items`, `Layout.Client`, `Layout.Undo`,
`Layout.Undo.Commands`. (`UndoManager` is the one file whose folder differs from its namespace: it lives in
`src/Systems/` as `Layout.Systems.UndoManager`.)

---

## 2. Data Model

### GuideData
Canonical persistent record for one guide. Server-side in `GuideManager`; sent to clients as needed.
**Never** stores baked voxel arrays — voxels are always derived on demand.

```
GuideData {
    Guid              Id
    GuideShapeType    ShapeType
    ShapeConstraint   Constraint        // None | SemiCircle | Circle (absorb-or-break)
    PlaneAxis         ShapePlaneAxis    // INTRINSIC plane normal / base axis (first-click face) — ellipse family + 3D volumes
    List<ControlPoint> ControlPoints
    int               VoxelScale        // 1, 2, 4, 8, or 16
    bool              IsHidden
    ProjectionMode    Projection        // Volumetric | Surface
    ProjectionPlane   Plane             // the Surface PROJECTION plane (≠ ShapePlaneAxis)
    bool              IsFilled          // 2D hollow vs filled
    bool              IsWireframe       // 3D Shell(false) vs canonical structural Wireframe(true)
    string            DisplayName       // cached human-readable whole-block dimensions
    int               CachedVoxelCount
    int               Cached{Voxel,Block}{Width,Height}
    string            CreatorUid        // nullable; bookkeeping only, never ownership, never wired
    int               DataVersion       // 11 (const CurrentDataVersion); older saves migrate by defaults
    int               Divisions          // Session 9: visual equal-parts count (0/1 = none)
    int               Sides              // Session 11: polygon side count (3–24; 0 on other shapes)
    bool              FlatSideAligned    // Session 20: polygon edge-facing orientation; false preserves old guides
    bool              IsClosed           // Session 11 (0.1.15): Free-Shape loop flag (false elsewhere)
    List<ControlPoint> OriginalControlPoints  // Session 11: as-placed snapshot for SHIFT spring-back
    ShapeConstraint   OriginalConstraint     //   (persisted, NEVER wired; null on pre-0.1.14 guides)
}
```

The same `List<ControlPoint>` instance is shared with the guide's shape — built exclusively via
`ShapeFactory`. `Constraint`, `Projection`, `Plane`, and `IsFilled` are per-guide and mutable after creation.
`GuideData.Create(...)` adopts a control-point list by reference, assigns a Guid, stamps the version;
`DeepClone()` produces a fully independent copy with the same Id (each `ControlPoint` cloned) — the snapshot
mechanism undo relies on.

### ControlPoint
```
ControlPoint {
    Vec3d   WorldPosition   // mutable reference type — always deep-copied; never aliased
    bool    IsLocked        // Red — structural (pins geometry)
    bool    IsPhantom       // math only, never rendered or grabbed
    bool    IsAnchor        // start/end — Blue — structural
    bool    IsPrimary       // apex / minor handle — Green — SOFT (flows)
    bool    IsLockMarker    // passive Arch lock; rendered/targetable but not a curve knot until dragged
}
```

### VoxelPosition
```
VoxelPosition {
    int X, Y, Z            // 1/16-block units (world × 16), lower corner, scale-independent
    VoxelRenderType Type   // Normal | Locked | Primary | Anchor | Grabbed | Division (magenta, S9)
}
```
In Surface mode the same struct renders as a paper-thin slab on the plane.

### GuideShapeType / ShapeConstraint
Pinned, append-only. Constrained variants are **not** types; fill is **not** a type.

```
enum GuideShapeType  { Arch = 0, Ellipse = 1, Line = 2, Triangle = 3, Rectangle = 4, Polygon = 5,
                       FreeShape = 6, Sphere = 7, Dome = 8, Cylinder = 9, Cone = 10, Box = 11 }
enum ShapeConstraint { None = 0, SemiCircle = 1, Circle = 2, Right = 3, Equilateral = 4, Isosceles = 5, Square = 6 }
```
`GuideShapeTypes.IsVolume(type)` classifies Sphere/Dome/Cylinder/Cone/Box (never inferred from enum ordering,
so future 2D shapes can be appended after the volumes). The 18-tile catalog (type, constraint) — **2D
section:** Arch = (Arch, None) · Half-circle = (Arch, SemiCircle) · Circle = (Ellipse, Circle) ·
Ellipse = (Ellipse, None) · Line = (Line, None) · Triangle = (Triangle, None) · Right = (Triangle, Right) ·
Equilateral = (Triangle, Equilateral) · Isosceles = (Triangle, Isosceles) · Rectangle = (Rectangle, None) ·
Square = (Rectangle, Square) · Polygon = (Polygon, None) · Free-Shape = (FreeShape, None); **3D section:**
Sphere = (Sphere, None) · Dome = (Dome, None) · Cylinder = (Cylinder, None) · Cone = (Cone, None) ·
Box = (Box, None). (Polygon side count lives in `GuideData.Sides`, not a constraint.)

### ProjectionMode / ProjectionPlane / GuideRenderSettings
Unchanged since v2: `ProjectionMode { Volumetric, Surface }`; `ProjectionPlane` = a `PlaneAxis`
(X = 0, Y = 1, Z = 2) + an offset in 1/16-block units, with `Horizontal` / `VerticalNorthSouth` /
`VerticalEastWest` factories and a `Default`; `GuideRenderSettings` bundles scale + projection + plane + fill
+ divisions (S9) for creation requests and the draft ghost.

---

## 3. Module Map

### `LayoutModSystem.cs`
Entry point and composition root: registers systems, the tool item, protocol-11 network channels, commands,
keybinds, and HUD on both sides; owns the shared instances per side; seeds `DraftManager` from client config
and persists it back. Client startup begins in Detecting, creates the local-authority/persistence stack, and
lets the network handler resolve Networked vs. Local. All keybinds are rebindable, none hard-coded.

### Items — `ItemGuideTool.cs`
Stateless glue (VS items are singletons); all interaction lives in `GuideToolController` (below). F opens the
GUI; clicks route to the controller.

### Client — authority and `GuideToolController.cs`
`ClientAuthorityMode`, `ClientToolGate`, `ClientWorldGuidePersistence`, and `LocalGuideAuthority` provide
detection, activation, durable local storage, and the client-side manager/undo stack. The controller remains
the interaction brain, ticking while the active gate is satisfied (30 ms):
- **Left-click priority chain (Create):** release grab → second foot → point grab (radius
  `max(0.10, voxel)`) → body hit (arch family: insert+grab in one gesture; ellipse family: nearest-handle
  grab) → first anchor. **Edit:** release grab → SELECT the aimed guide (empty click deselects) — select-only,
  no grab/insert/lock. **Delete:** dispel the aimed guide.
- **Right-click (Create only):** cancel grab (insert-born point removed; pre-existing point snaps back via
  `GuideCancelGrabPacket`); discard draft; idle point → lock toggle; idle body → lock-in-place insert
  (ellipse family: nearest-handle lock toggle). Edit/Delete have no right-click action.
- **Targeting** tests real points plus the **sampled curve** (`IGuideShape.SampleCurve`), cached per guide
  behind a content fingerprint (count + coordinate sum + constraint) — resampled only on change.
- **Drag:** raycast target (anchors need a block; interior points retained-depth), local mirror preview +
  ~10 Hz sends; **soft-flow preview** runs the same `SoftPointFlow` math locally; **SHIFT** on an anchor
  drag constrains to the cardinal line through the other anchor; a grab that a constraint can't absorb
  clears the mirror constraint immediately (server confirms).
- **Draft:** first click captures the intrinsic plane axis from the block face; ghost preview via the placed
  pipeline with live settings; completion sends shape + constraint + plane with the two points.
- Comatose grabs on tool swap; adopt-as-grab handshake for server-side inserts; hotkeys inert unless held.

### Shapes (pure math — only depends on `Vec3d`)

**`IGuideShape.cs`** — the seam decoupling everything from any specific shape:

```
interface IGuideShape {
    List<ControlPoint>  ControlPoints                              // the shared list (GuideData binding)
    ShapeConstraint     Constraint
    List<VoxelPosition> GetVoxelPositions(int scale, bool filled)
    int                 GetVoxelCount(int scale, bool filled)      // exact, == positions count
    float               GetNearestT(Vec3d worldPos)
    Vec3d               GetPointAt(float t)                        // lock-in-place lands ON the curve
    List<Vec3d>         SampleCurve(int samples)                   // targeting polyline (closed shapes close)
    int                 GetNearestControlPointIndex(Vec3d worldPos)
    void                InsertControlPoint(float t, Vec3d position)
    void                MoveControlPoint(int index, Vec3d newPosition)
    void                RecalculatePhantomPoints()
    bool                WouldBreakOnMove(int index)                // constraint can't absorb this drag
    bool                BreakConstraint()                          // demote to free parent, materialising
}
```

**`CatmullRomSpline.cs`** — centripetal Catmull-Rom (α = 0.5); `Evaluate`, `EvaluateTangent`, `GetArcLength`,
`SampleVoxelPositions(scale)`, `CountVoxels(scale)`. Tangent = secant, which makes the arch's phantoms give
provably vertical feet.

**`ArchShape.cs`** — the open-curve primitive over the spline; owns the shared point list. Free arch: 5-point
spine (phantom, anchor, apex/primary, anchor, phantom), apex at 40% of chord. **SemiCircle mode:** stores
only its feet ([phantom, A, B, phantom]), samples a true circular arc, synthesises the apex marker at the
arc's top; breaking materialises quarter/apex/three-quarter points ON the arc. **Fill:** the ruled region
between the curve and the foot-to-foot chord (half-circle → exact half-disc). Marker voxels by nearest-claim
(Locked > Primary > Anchor), even-span apex pairing.

**`EllipseShape.cs`** — the closed planar primitive: two diameter anchors + a minor-axis handle (Primary), no
phantoms. The frame derives per query (plane normal = `ShapePlaneAxis` projected ⊥ the major axis; robust to
arbitrary 3D anchor drags). The minor handle slides along its axis; A/B moves re-derive it. Under Circle the
handle is derived (= major radius) and dragging it is the break trigger (lossless). Body inserts are no-ops
by design. Fill = radial-fan disc.

**`ShapeFactory.cs`** — `Create` (two clicks) / `Adopt` (existing list or GuideData); the only construction
point. **`VoxelMarch.cs`** — shared point-sequence → cell marching, mirroring the spline's quantise exactly.
**`SoftPointFlow.cs`** — `Capture(pts, grabbedIndex)` folds the held point into the baseline, then selects
the regime: an **interior grab** (held point unlocked, non-anchor) slaves each soft point onto the curve at
its station with **zero offset**; a **structural grab** (anchor/lock) keeps the station + frame-local,
length-scaled offset. Reflow is non-mutating; identical code runs server-side (composed into the same edit
batch as the grabbed point) and client-side (drag preview). Three capture call sites: server move handler,
client `StartGrab`, client insert-adoption.

### Systems

**`GuideManager.cs`** — side-neutral guide authority. Registry + injected `IGuidePersistence` (versioned
JSON on the server or per-world/per-UID JSON on the client), optional recovery, injected block probe/logger;
builds shapes via the factory on create (shape/constraint/plane from the request) and
re-adopts on load/restore; validates every mutation (**form/fill-aware voxel caps**, lock, existence) and
reverts on rejection. Mutations: `CreateGuide`, `RestoreGuide`, `UpdateControlPoints` (multi-edit, atomic),
`InsertControlPoint`, `RemoveControlPoint`, `SetPointLocked`, `DeleteGuide`, `SetHidden`, `SetProjection`
(**bakes flattened positions into the points when leaving Surface**; preserves the stored plane through
Volumetric), `SetFilled` and `SetWireframe` (recount with the new value, roll back over cap), `Rescale`, `BreakConstraint` /
`RestoreConstraint`, `RestoreControlPoints`. Communicates by `GuideOperationResult` return values, not events.

**`GuideLockManager.cs`** — pure; one edit lock per guide, first grab wins; `ReleaseAllLocksForPlayer`,
`ClearLock`, `IsHeldBy`.

**`DraftManager.cs`** — client-side draft + tool state: mode, scale, projection, plane override, 2D fill,
3D form (`Wireframe`),
**shape + constraint** (the picker's target), the per-draft intrinsic plane axis, and the selected guide.
Holds only the draft's start point; settings are read live at completion. Cap pre-check builds a throwaway
shape via the factory, counted with the current 2D fill / 3D form.

**`UndoManager.cs`** — per-authority, per-player bounded stacks (server default 50; local uses the same
semantics). Validate-then-apply
with stale-command skip; `Blocked` for valid-but-cap-rejected commands (pushed back, history preserved);
returns results, never broadcasts.

**`GuideRenderer.cs`** — client-side; placed meshes rebuild on accepted change events. Shapes adopt through
the factory; settled Shells and persistent Wireframes use the true selected scale. Cheap drafts show their
normal shell. Expensive motion uses adaptive structural wireframes plus a selected-scale cursor region;
`DraftPreviewSpec` identifies one deep-copied generation, background refinement rejects stale completions,
and selected-scale materialization uploads bounded pseudo-random batches. Giant grabs retain the settled mesh
for instant cancel, and confirming echoes are fingerprint-quarantined. Surface: flatten to the **air-side** cell layer (world
solidity probe, majority fallback) as **0.01-block slabs** with a plane-axis-only inset; volumetric meshes
get a 0.003-block per-frame camera nudge. Grabbed point painted White (single voxel); hidden guides =
anchors-only at low alpha. No selection/collision geometry.

**`GuideMeshBuilder.cs`** — stateless `List<VoxelPosition>` → `MeshData`; cube and slab paths; the color
table (client-configurable alphas):

```
Yellow   (1.0, 0.85, 0.1)   Normal body          Red    (0.9, 0.15, 0.15)  Locked
Green    (0.2, 0.9, 0.3)    Primary/apex/handle  Blue   (0.2, 0.5, 1.0)    Anchors (coplanar)
Indigo   (0.45, 0.45, 1.0)  Far-foot off-shade   White  (1.0, 1.0, 1.0)    Grabbed
```
Only on a mixed Layout server, locally-owned anchors substitute orange/burnt-orange for blue/indigo. A
vanilla-server local fallback retains blue/indigo as Layout's normal visual identity. Color is derived from
current ownership/context at render time, so a published guide immediately renders blue.

### Network

**`PacketTypes.cs`** — protobuf DTOs, one fixed shared registration order, **append-only**. Enums as pinned
ints, Guids as 16 bytes, positions as three doubles, full point lists verbatim (mirrors are exact copies).

| Packet | Direction | Contents |
|---|---|---|
| `GuideBulkSyncPacket` | S→C | All guides + active caps + lock states + draft anchors, on join. (Fields 6–7 carried the server's refill-channel policy in 0.2.21; **dead since v0.2.22**, retained unwritten as append-only padding) |
| `GuideCreateRequestPacket` | C→S | Base points + settings + **shape/constraint/plane, sides, optional apex/chain/rim, and flat-side alignment** |
| `GuideCreatePacket` | S→C | Full `GuideData` (also the generic full-state broadcast) |
| `GuideUpdatePacket` | S→C, C→S | Guide ID + edit array (client sends its one; server broadcasts the composed batch incl. soft-flow edits) |
| `GuideInsertPointPacket` | S→C, C→S | Guide ID + index + position (+ `Locked` for lock-in-place) |
| `GuideCancelGrabPacket` | C→S | Cancel the grab: restore origins / remove an insert-born point |
| `GuideDeletePacket` / `GuideHidePacket` / `GuideLockPointPacket` / `GuideRescalePacket` / `GuideSetProjectionPacket` / `GuideSetFilledPacket` / `GuideSetWireframePacket` / `GuideSetDivisionsPacket` | S→C, C→S | Atomic settings ops (wireframe = 3D Form; divisions = pure visual recolor) |
| `GuideGrabPacket` / `GuideReleasePacket` / `GuideLockStatePacket` | C→S / S→C | Edit-lock lifecycle |
| `DraftStartPacket` / `DraftCancelPacket` / `DraftAnchorBroadcastPacket` / `DraftAnchorRemovePacket` | mixed | Draft lifecycle (anchor dot only) |
| `UndoRequestPacket` / `RedoRequestPacket` / `VoxelCapWarningPacket` | C→S / S→C | Undo + cap warnings |
| `ClientOnlyPolicyPacket` / `ClientOnlyModeRequestPacket` / `ClientOnlyModeResultPacket` | mixed | Server permission and private/public placement negotiation |
| `ClientGuidePushRequestPacket` / `ClientGuidePushResultPacket` | C→S / S→C | Publish up to 100 private guides and confirm accepted local removals |
| `ChalkChargePacket` (S15, protocol 4) | C→S | Self-report a completed PRIVATE placement so the server (which owns the inventory but cannot see private guides) applies the chalk charge; validated + clamped 1–2 |
| `ChalkInventoryRefillPacket` (S16, protocol 5) | C→S | Inventory-slot refill request (`InventoryId` + `SlotId`); server re-validates cursor=powder + slot=non-full kit before consuming one powder — a lost mouse-hook race degrades to a harmless swap |
| `ChalkRefillPrefsPacket` (S17, protocol 6) | C→S | The player's OWN refill-channel preferences, sent on join. Required because `ItemChalkingPowder`'s held-interact runs on both sides and the SERVER mutates the stacks — without it the hotbar toggle would be a no-op |

**`ServerNetworkHandler.cs`** — validates, calls the managers, reads results, broadcasts (or corrective
resync / cap warning). Owns: the create request (shape-aware), **auto-break** before constrained inserts and
on breaking moves (recording `BreakConstraintCommand` from a pre-break snapshot; break-flavoured mutations
broadcast **full state** because the point list changed shape), **soft-flow composition** (capture per drag
session, reflow edits folded into the same `UpdateControlPoints` batch — one cap check, one broadcast,
generic origins), the projection bake snapshot, drag coalescing (one drag = one undo entry), join/leave
cleanup (locks freed + broadcast, undo cleared, drag sessions and draft anchors dropped).

**`ClientNetworkHandler.cs`** — applies S→C packets to a combined mirror, separately tracks server/local IDs,
raises change events for renderer/HUD/GUI, and exposes ownership, locks, remote anchors, caps, policy, and
send methods. Existing-guide operations route by ownership; new placement routes by current placement mode;
undo/redo routes by last successful mutation authority. Networked writes still go only through packets;
local writes go through `LocalGuideAuthority` and then update the same mirror.

### UI

**`GuideToolGui.cs`** — the tile GUI (modal, F, press-to-open; icon form since Session 10). Every control is
a row of exclusive SQUARE ICON tiles (42 px; hover names the option, auto-sized to the text since Session
11). **Mode-aware rows (Session 10):** the Mode row (Create/Edit/Delete) is always live; the rest re-bind by
mode. **Create:** tool defaults for the next guide — the Mode row's far-right **Current Shape chip
(0.1.15: always lit, guide-body yellow, hover names the pick — visible even when the selection isn't on a
slot)** · Shape (**0.1.15: FOUR hard-kept pinned slots + a ▾
expand tile that unfolds the full 21-tile catalog — split into a **2D section and a 3D section** under
their own separators with centred "2D"/"3D" labels (0.1.22–0.1.23); pins shown as YELLOW glyphs (0.1.17);
right-click pins/unpins (never evicts; message when full); the catalog STAYS OPEN after a pick (0.1.23) —
only the ▾/▴ collapses it; selecting a shape never collapses; empty slots show faint placeholders; pins
persist in `layout-client.json`; the old Favorites strip is gone**) · Scale (native N×N voxel-count icons;
16× = one solid block) · **Projection + Fill/Form on ONE row** (Projection greys on volumes; 2D uses
Hollow/Filled; 3D contextually uses Shell/Wireframe with dedicated skin/frame icons; Fill greys on the
Free-Shape) · Plane · **Divisions (+ Sides for polygons on the same row; the whole row HIDDEN on 3D
volumes, 0.1.23)**. All the primary row labels (Mode/Shape/Scale/…) are **centre-aligned** in their column
(0.1.23). **Edit:** the SAME rows plus **Visibility** (no shape picker) act
on the SELECTED guide via the send API, with a compact guide-info line + **Deselect**; greyed with a "click
a guide" prompt when none is selected — so the panel never grows a second section. **Delete:** every row but
Mode disabled (native `Enabled=false` + ghost labels). Divisions and Sides are native
`GuiElementNumberInput`s (wheel ±1 — element-native plus a dialog-level hover fallback in `OnMouseWheel` —
spinner buttons, typed input, clamped to their ranges: 0–256 and 3–24; `OnNumberTyped` snaps the display
back on clamp, and tolerates transient under-min typing in the Sides field). Remote edits to the Edit-mode
selected guide relight the (shared-key) tiles in place; row-set changes defer a recompose (never per-frame).

**`LayoutToolIcons.cs`** (Session 10) — every GUI glyph, drawn with Cairo and registered once (client start)
in `capi.Gui.Icons.CustomIcons`: the 15 shape glyphs (the arch is an open elliptical dome — the true
Catmull-Rom silhouette; the polygon a point-up pentagon; the Free-Shape an irregular dotted-corner
outline), the picker's expand chevrons (▾/▴), the empty-slot placeholder, and (0.1.15) per-shape
**"-star"** (★-badged pinned catalog tiles) and **"-current"** (fixed guide-body-yellow, for the Current
Shape chip) wrapper variants, plus mode/projection/fill/form/visibility pairs, plane cubes (active face filled),
and the scale grids (`DrawScaleGrid(n)` + `DrawScaleFullBlock`). Uniform aspect-preserving design-box
mapping; strokes/fills take the button's tint, so normal/hover/pressed states come free.

**`GuideHud.cs`** — mode (+ the picked shape in Create) · scale · projection/plane · Fill or 3D Form · live
draft/grab dimensions when ready (animated calculation glyphs while pending) · placed guide’s cached
dimension-name/count without hover regeneration · lock and cap bar (`Cap: 62%` + ⚠ from the warning packet) · **the Current Shape chip
(0.1.16): the same always-lit yellow glyph as the F-menu's, top-right of the panel in Create mode,
recomposed on shape/mode changes.**

### Undo

**`IGuideCommand`** — `CanUndo` / `CanRedo` (direction-specific), `Execute` / `Undo` / `Redo` returning
`GuideOperationResult`; the handler mutates directly and records, so `Execute` is reached via redo.
**`UndoStack`** — pure bounded histories; new actions clear redo. **Commands:** Create / Delete (DeepClone
snapshots) · MoveControlPoint (before/after; one per drag per moved point, soft-flow included) ·
InsertControlPoint (landing index refreshed on re-insert) · LockPoint · RescaleGuide · HideGuide ·
SetProjection (**+ optional pre-bake point snapshot**; undo restores mode/plane then the points) ·
SetFilled · **SetWireframe** · **BreakConstraint** (pre-break constraint + points; undo restores both, redo re-breaks) ·
**SetSides** (S11: old/new polygon side count) · **SpringBack** (S11: pre/post point+constraint snapshots
around a SHIFT spring-back; undo restores the distorted form).
Move/insert confirm the point is still where the command left it, so they never clobber another player's edit.

---

## 4. Geometry Tiers

- **Tier 1 — hollow curve/ring.** Built.
- **Tier 2 — filled region.** Built: arch family = curve closed by the foot-to-foot chord (ruled surface);
  ellipse family = disc. Caps count filled voxels; big filled regions at scale 1 approach the per-guide cap
  by design (the warning handles it).
- **Tier 3 — curved/swept filled surface** (a surface between multiple boundary curves). **Deferred** until
  Tiers 1–2 have real-play mileage.

---

## 5. Key Interaction Flows (the three-mode scheme)

### Targeting (all clicks)
A raycast resolves to the voxel cell on the first block face at the current scale. **Anchors require a valid
block target.** Guide targeting tests real control points (precise, `max(0.10, voxel)` radius) and the
**sampled curve** for body hits. In Surface mode the clicked face auto-selects the projection plane (UI
override available); the first click of any draft also fixes the ellipse family's **intrinsic** plane.
Guides are referenced off blocks only at placement — never bound; removing the block changes nothing.

### Creating a guide (two to four clicks; Free-Shape chains)
1. Pick the shape on the F-menu tiles (or keep the remembered default). **First click:** draft starts,
   plane axis captured, `DraftStartPacket` → others see an anchor dot; the acting player gets the live ghost
   (full placed-guide pipeline: colors, scale, Surface slabs, far-foot Blue/Indigo, the picked shape).
2. Settings changed mid-draft apply live to the ghost. **CTRL** snaps level/cardinal; **SHIFT** performs the
   current stage's invert/vertical/flat-side/flare action; **CTRL+SHIFT** gives a 45-degree Line/Free-Shape
   diagonal. The live held-help rows state the applicable meaning.
3. **Completing click:** client cap pre-check (factory shape, filled-aware) → `GuideCreateRequestPacket`
   (base + settings + shape/constraint/plane + inverted/sides/apex/chain/rim/flat-side fields) → server builds via the
   factory, stores (stamping the as-placed spring-back snapshot), records `CreateGuideCommand`, broadcasts
   full state. **Three-click triangles:** the second click stores the base's far end (client-side only);
   the ghost's apex then tracks the crosshair — **SHIFT centres it on the base (0.1.15)** — and the THIRD
   click completes. **Free-Shape (0.1.15):** every click chains a corner (CTRL snaps relative to the
   PREVIOUS corner); clicking the LAST corner finishes open, the FIRST (≥3) closes the loop; the full
   chain + closed flag cross in the create request. Cylinder/Polygonal Prism/Cone/Box use a third height
   click; Tapered Cylinder/Tapered Polygonal Prism add a fourth rim-radius click. A tapered rim cannot exceed
   its base radius unless SHIFT is held; CTRL closes it to a point. Right-click steps any multi-click draft back one click
   (chains retract a corner, triangles the base end; otherwise the draft is discarded).

### Grabbing and reshaping (Create mode)
1. **Left-click a point** → grab (lock acquired, `GuideLockStatePacket` broadcast). **Left-click the body:**
   arch family → the server inserts a point at the nearest curve parameter and the client adopts it as a
   grab in one gesture (a constrained guide **breaks first** — one undo command, one full-state broadcast
   carrying both changes); ellipse family → the nearest handle is grabbed instead.
2. **Dragging:** anchors snap to block faces (CTRL → cardinal line through the other anchor; Session 11 —
   SHIFT+left-click on a guide is now spring-back-to-original instead of a grab); interior
   points move at retained depth. The client previews locally with full geometry semantics; giant guides may
   use their structural wireframe during motion while preserving selected-scale precision near the cursor.
   Preview still includes **soft-point flow** (unlocked interior points flowing proportionally with the structural baseline) and any constraint
   break (mirror constraint cleared at grab start). Throttled sends (~100 ms); the server composes the
   authoritative batch (grabbed edit + its own soft-flow reflow), cap-checks once, broadcasts to everyone.
   Dragging a circle's minor handle auto-breaks circle → ellipse on the first move.
3. **Release (left-click):** one `MoveControlPointCommand` per moved point (origin → final), lock freed.
   **Right-click instead:** cancel — origins restore authoritatively, the retained settled mesh reappears
   immediately, transient generations invalidate, and an insert-born point is removed entirely.
   Tool swap mid-drag = comatose (suspend, resume on re-equip).
4. **Idle right-click:** the first rendered voxel hit is authoritative. A point toggles only when that voxel
   is its nearest visible marker cell; an adjacent arch/Free-Shape body voxel receives its own passive lock
   marker (B-S9-1 closed in v0.2.36). Other parametric bodies map to the nearest meaningful handle.

### Editing a placed guide (Edit mode — Session 10)
Switch the Mode row to **Edit**, then **left-click a guide to select it** (empty click deselects; select-only
— no reshaping). The F-menu's Scale / Projection / Plane / Fill-or-Form / Divisions / Visibility rows drive THAT
guide through the send API (`SendRescale` / `SendSetProjection` / `SendSetFilled` / `SendSetWireframe` / `SendSetDivisions` /
`SendHide`) instead of the tool defaults — no separate panel section, so the GUI never expands. Reshaping
(grab / insert / lock) stays in **Create**.

### Projection, plane, fill/form (F-menu tiles — tool defaults in Create, the selected guide in Edit)
Volumetric ↔ Surface: switching a guide **to** Volumetric bakes the flattened positions into its points
(what you saw is what you get; single undo step; full-state broadcast); switching back **to** Surface
restores its stored plane. Plane, 2D Fill, and 3D Shell/Wireframe Form apply atomically with cap re-checks and
roll back on rejection. All rebuild every client's mesh via the normal change events.

### Delete mode
Left-click a guide → dispel. Ownership routes the request to local or server authority; the authority owns
lock validation and any admin override. The GUI's other rows disable.

### Undo / redo
Ctrl+Z / Ctrl+Y (inert unless the active tool gate is satisfied) → the authority of the last successful
mutation finds that player's most recent still-valid command. Stale ones are skipped; cap-blocked commands
are preserved and reported. Merely selecting a guide or changing public/private placement mode does not
redirect history. Publication is a committed transfer and is intentionally not undoable.

### Cross-plane behaviour
Volumetric points move freely in 3D — guides can be non-planar. Surface re-flattens the render onto the
plane; edits made while Surface are made against that view, and **leaving Surface keeps them** (the bake).
The ellipse's intrinsic plane is independent of the Surface projection plane and survives arbitrary drags
(the frame re-derives per query).

---

## 6. Serialization and Persistence

- All `GuideData` as a JSON array under a versioned root via `IWorldSaveGame.StoreData`/`GetData`;
  `GuideManager` owns a Newtonsoft `Vec3d` converter (`{x,y,z}`). Persistence on every mutation + the
  world-save event; load/save never throw out of VS event handlers. On load: version-checked (defaults
  migrate v3-era records), factory-adopted, phantoms recalculated before first sync.
- **Save format ≠ wire format:** JSON persists; protobuf DTOs travel. The paths are independent.
- **Private client persistence:** `ClientWorldGuidePersistence` stores local guides under
  `Layout/ClientOnlyGuides/<hash server-or-savegame>-<hash player UID>.json`. Writes use a temporary file
  and atomic replacement, retain one `.bak`, and quarantine unreadable primary files as `.corrupt-*` before
  attempting recovery. If the storage identity/path is unavailable, local authority remains usable with
  transient in-memory persistence for that session. This never touches the world's main save payload.

---

## 7. Concurrency and Edge Cases

- **Multiplayer model:** public guides remain world-shared and server-authoritative. Private guides are
  player-owned client data and can overlay public guides only when `allowClientOnlyMode` permits it. They are
  not visible or backed up by the server until explicitly published. Server administrators can prohibit the
  private overlay (default) but cannot inspect guides that exist only on a client.
- **Guides are non-targetable without the active gate:** pure mesh draws, no selection/collision/entity
  backing; interaction stays inside the Layout-tool path or the Hammer+Flax fallback path.
- **Disconnect mid-grab** → locks freed and broadcast; uncommitted drag never recorded; undo history cleared.
  **Mid-draft** → draft cancelled, anchor dot removed.
- **Two players grab one guide** → first packet wins; the second sees the lock state, click is a no-op.
- **Over-cap mid-edit** → server counts with the current 2D fill / 3D form before committing; rejects, warns, reverts. Clients
  pre-check to avoid jank.
- **Cross-player undo** → stale commands are skipped, never corrupting state; valid-but-rejected commands
  are `Blocked` and preserved.
- **Mixed selection/mode changes** → do not change ownership and do not reroute undo. A pushed guide receives
  a new public ID, creator UID, public anchor palette, and no inherited private undo entry.
- **Block under a guide removed** → nothing happens; guides never bind to blocks.

---

This v3.8 document is the authoritative architecture, consolidated to current state: **Layout v0.3.8 built,
packaged, and documented locally; `main` last pushed through v0.2.47**. The full 2D catalog — arches, half-circles, circles, ellipses, lines, triangles
(+ right/equilateral/isosceles), rectangles (+ square), polygons, and Free-Shapes — plus the **3D volume
family** (spheres, domes, cylinders, tapered cylinders, straight/tapered polygonal prisms, cones, boxes)
place, preview, reshape, fill/form, lock/unlock, divide, and
project onto surfaces under server or local authority against VS 1.22.3 / .NET 10, drawn with the
**Chalking Kit**'s finite, powder-refillable chalk. Status, flagged decisions, and the
punch-list live in `PROJECT_STATUS.md` and `TODO.md`; the current-state brief for external analysis lives in
`HANDOFF.md` at the repo root; F4's final behavior record lives in `PLAN_CLIENT_ONLY.md`, its implementation
history in `SESSION_12.md`, the interaction checkpoint in `SESSION_13.md`, the mesh resume plan in
`SESSION_14.md`, and the current adaptive large-guide record in `SESSION_21.md`.
