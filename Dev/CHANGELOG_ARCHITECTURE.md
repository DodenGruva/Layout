# Layout — Architecture Changelog (document-version history)

> **Split out of `ARCHITECTURE.md` to keep the orientation read cheap.** These are the per-revision deltas
> for the architecture document itself, v2.5 → v3.3. **Nothing here is unique:** each delta is already folded
> in place into `ARCHITECTURE.md`'s body (Settled Decisions Register, file tree, module map, persistence,
> edge cases), and each has a fuller narrative in its `SESSION_*.md` record. Kept as the audit trail — read
> it when you need to know *when* something changed, not *what* the current state is.

---

## Changelog — v3.2 → v3.3 (mesh + polish, v0.2.10 → v0.2.21; full record in `SESSION_16.md`)

- **Interaction/visual polish (0.2.10–0.2.13, committed `de830b1`):** whole-guide placement dust (2D along
  the sampled curve capped at 40, 3D around the base ring capped at 24 — never the shell); the Edit-mode
  pencil icon fix; **Dome now faces the clicked surface** (the first click's face SIGN, dropped by the
  0.1.23 deterministic-axis default, is captured in `DraftManager.DraftPlaneNegative` and folded into the
  Dome `inverted` flag — floor→up, ceiling→down, wall→toward you; SHIFT still inverts); and the ground
  **z-fight inset** (grid-coplanar voxel faces nudged off the block plane, tuned in play to `0.003`).
- **Large-guide mesh Stage A — exposed-face meshing (0.2.14–0.2.16):** `GuideMeshBuilder`'s Volumetric cube
  path now builds a presence set of rendered cells, pre-counts exposed faces, allocates exactly, and emits
  ONLY faces with no neighbour (per-voxel role colours preserved). The z-fight inset was made **exposed-only**
  (flush where a neighbour exists — fixes block-line slits) and then **solidity-aware**
  (`GuideMeshOptions.IsNeighborSolid`, a world probe from `GuideRenderer` — inset only against a solid world
  block, so interior faces bordering air stay on grid; fixes filled-body interior seams). A deterministic
  mesh-count harness (throwaway console project vs the Release DLL) locks the counts and flush/inset
  invariants (15/15). Surface tile/slab paths stay on the legacy whole-box builder. **The inset can no longer
  separate two guide voxels — only a guide face from a world block face — so it produces no interior seams.**
- **Filled 3D volumes RETIRED (0.2.17):** post-Stage-A a filled interior emits no geometry, so "filled"
  looked identical to hollow at 10–100× the voxel cost. Fill is now gated off for volumes three-deep — GUI
  greys the tiles, `GuideManager` rejects/normalises, and each volume shape coerces `filled=false` at both
  voxel entry points (auto-lightening legacy `IsFilled=true` saves; no data-version change). 2D fills intact.
- **HUD + GUI (0.2.18):** `GuideHud.SetExaminedGuide` no longer wipes its measurement cache when the target
  is unchanged (hovering a huge guide was re-running the full voxel measure ~33×/s); Divisions/Sides number
  fields aligned to the icon-tile grid.
- **Draft cap-clamp restored (0.2.19):** the mid-draft "stop at the per-guide voxel cap" was an accident of
  heavy ghosts freezing, which Stage A removed. Reimplemented as a real binary-search clamp
  (`ClampDraftAimToPerGuideCap`) on the ghost preview and the completing click; Free-Shape chains excluded;
  fails open.
- **Fifth fill state (0.2.20):** a **High** kit model so pristine **Full** is reserved for a completely full
  kit — full=32 · high 22–31 · medium 11–21 · low 1–10 · empty 0 (`ItemGuideTool.FillIndexFor`).
- **Refill channels — config + inventory refill (0.2.21, protocol 4 → 5):** `allowHotbarChalkRefill` /
  `allowInventoryChalkRefill` server config (both default false; ground storage always allowed), synced to
  clients via additive `GuideBulkSyncPacket` fields. Inventory refill is a client `MouseDown` hook (cursor
  powder onto a kit slot) + the server-validated `ChalkInventoryRefillPacket` — deliberately NOT the VS
  slot-merge path (which would misroute powder into kits via auto-pickup); a lost hook-ordering race degrades
  to a harmless slot swap, never inventory corruption.

## Changelog — v3.1 → v3.2 (the Chalking Kit, F5, v0.2.0 → v0.2.9; full record in `SESSION_15.md`)

- **Reskin + ground storage (0.2.0–0.2.4):** custom model + rename; vanilla `GroundStorable`
  (`SingleCenter`) set-down behind an idle-gated CTRL+SHIFT gesture. Architectural note: the F4 input-layer
  right-click hook had made `ItemGuideTool`'s held-interact hooks unreachable in BOTH authority modes — the
  controller now steps aside for the set-down gesture (`IsGroundStoreSetDownGesture`, reading the same
  `Controls` modifiers the vanilla behavior checks).
- **Durability core (0.2.5):** `durability: 32` on the itemtype; all chalk mutation via
  `ItemGuideTool.ConsumeChalk`/`TryAddChalk` (clamped [0, max], never vanilla `DamageItem` → never breaks);
  client pre-check at the first draft click + authoritative server gate/charge in `OnCreateRequest`;
  `ItemChalkingPowder` (tap/hold refill, server-side consumption, per-entity repeat counter);
  `enableChalkDurability` config; recipes (powder + 0.1 L yellow dye → 8; the 8-powder kit craft).
- **Private-mode charge (0.2.6):** "private is private, not free" — `ChalkChargePacket` (**protocol 3 → 4**),
  client-reported on successful private creates (`LocalGuideAuthority.Create` now returns success),
  server-validated and clamped. Recipe breadth: `flour-*` joins `powder-*`; dye containers bucket/bowl/jug.
- **Fill-state rendering (0.2.7):** four shapes (`chalkbag-{full,medium,low,empty}`) each carrying its own
  texture set; thresholds full ≥22 · medium 11–21 · low 1–10 · empty 0; `OnBeforeRender` swaps the
  `MultiTextureMeshRef` per stack (lazy `ShapeTextureSource` tesselation, disposed on unload);
  `IContainedMeshSource` covers ground storage/display rendering — new **`VSSurvivalMod.dll`** reference.
- **Ground refill + effects (0.2.8):** SHIFT+right-click a stored kit pours into it in place (per-pour
  `MarkDirty(true)` re-inflates the bag live); `ChalkEffects` (side-agnostic): chalk-puff on refill and at
  both anchors on placement + the `bow-release` snap at the guide midpoint (server-broadcast for public,
  client-local for private — matching guide visibility). Fixed in passing: powder pile placement (vanilla
  GroundStorable) had been unreachable since 0.2.5; sneak-clicks not aimed at a stored kit now fall through.
- **Process:** versions 0.2.0–0.2.9 each shipped as zips in `..\Layout Zips\`; no save-format change
  (DataVersion 8; kits and saves from older versions load with full chalk).

---

## Changelog — v3.0 → v3.1 (hollow shell scaling, v0.1.53; full handoff in `SESSION_14.md`)

- **Exact shell-focused scan:** `SphericalShellScan` walks X/Y columns, analytically narrows each to the two Z
  surface bands, and applies the prior nearest/farthest predicate. Hollow Sphere/Dome preserve exact voxel
  output while work scales approximately with shell area rather than bounding-cube volume.
- **Filled paths unchanged:** filled Sphere/Dome retain the 4M candidate-cell guard and cubic scan. No save or
  wire change; DataVersion 8 / protocol 3 remain current.
- **Validation:** cell-for-cell parity across every scale, off-grid placement, inversion, wall orientations,
  and threshold counts. A 20-block hollow Dome generated 242,500 voxels in ~5 ms in isolation.
- **Human playtest:** a roughly 100-block hollow Sphere placed successfully and caused visible lag. This
  confirms the next bottleneck is `GuideMeshBuilder`'s 8-vertex/36-index full cube per voxel, one monolithic
  uploaded mesh per guide, full rebuild on every change, and no spatial culling.
- **Next architecture:** exposed-face Volumetric meshing → per-guide spatial chunk meshes with reliable
  ownership/disposal and culling → same-colour/orientation greedy merging. Keep Surface/slabs on the legacy
  path initially and never permanently coarsen settled guides without explicit human approval.

---

## Changelog — v2.9 → v3.0 (optimization + interaction correctness, v0.1.46 → v0.1.52; full record in `SESSION_13.md`)

- **Threshold-aware counts:** `IGuideShape` supports cap-limited counting. Box, Cone, Cylinder, Dome, and
  Sphere stop once rejection is certain, avoiding full voxel materialization for ordinary cap checks.
- **Natural cap clamp:** rejected over-cap releases reconcile safely, while the client binary-searches a drag
  segment to the largest acceptable size. The live shape stops at the cap instead of flickering between an
  oversized preview and server state.
- **Precise targeting and cancellation:** lock picking uses the first rendered voxel box intersected by the
  ray. Curve target caches use a complete geometry fingerprint, and both authority paths retain full pre-drag
  snapshots for exact right-click cancellation.
- **Passive lock markers:** `ControlPoint.IsLockMarker` lets an Arch display and target a lock without adding
  a shape-defining Catmull-Rom knot. Deliberate dragging promotes the marker. This additive field advances the
  save schema to DataVersion 8 and the wire protocol to 3.
- **Marker lifecycle/order:** v0.1.52 removes passive markers on unlock, cleans obsolete markers during
  restoration, ignores inactive markers during target/adoption, and inserts later points by curve position.
  Lock placement is playtest-confirmed non-deforming; the wider interaction regression remains open.

---

## Changelog — v2.8 → v2.9 (ClientOnlyFallback, v0.1.28 → v0.1.45; full record in `SESSION_12.md`)

- **Authority seam:** `GuideManager` no longer assumes a server. `IGuidePersistence`,
  `IRecoverableGuidePersistence`, a block-solidity probe, and injected logging let
  `LocalGuideAuthority` run the same validation, reshape, lock/constraint, cap, and undo machinery on the
  client. Local caps are unlimited except for the shared 10M hard safety ceiling.
- **Detection and activation:** `ClientAuthorityMode` is Detecting, Networked, or Local. A received bulk
  sync is positive proof of a Layout server; otherwise a three-second grace resolves to Local. Local fallback
  requires any vanilla Hammer variant/durability offhand + Flax Twine main hand. Networked public/private
  modes both require the registered Layout tool.
- **Mixed authority:** `ClientNetworkHandler` presents one mirror while tracking server/local guide IDs.
  Ownership, not the current placement mode, routes operations on existing guides. Placement mode controls
  only new guides. The last successful mutation authority routes Ctrl+Z/Y, so selection or mode changes do
  not redirect history.
- **Policy, commands, and publication:** server `allowClientOnlyMode` defaults false; client
  `forceClientOnly` is only honored when policy allows it. `/layout private` and `/layout public` choose
  placement authority; `.layout client dispel all|<chunk radius>` affects local guides; existing
  `/layout dispel` remains server/admin; `/layout client push all` transfers at most 100 private guides after
  server privilege/cap checks. Accepted guides get new server IDs and creator UID, then are removed locally;
  publication is committed and deliberately absent from server undo history.
- **Persistence and presentation:** local JSON is keyed by server/world + player UID under
  `Layout/ClientOnlyGuides`, written by atomic temporary replace with one `.bak` and corrupt-file quarantine.
  Vanilla-server fallback uses the normal blue/indigo anchors. Only mixed Layout servers render private
  anchors orange/burnt-orange; the compact HUD says `Client-Only Guides`, and targeted local guides say
  `Private guide`. The main GUI remains unchanged.
- **Compatibility:** public save format remains DataVersion 7. Protocol 2 adds append-only policy, mode, and
  push packets. `requiredOnServer=false` remains essential for vanilla-server fallback.

---

## Changelog — v2.7 → v2.8 (Session 11 + the 3D family, v0.1.14 → v0.1.27; full record in `SESSION_11.md`)

- **Three-click triangle (0.1.14):** free/right/isosceles triangles place anchor · anchor · height (the
  equilateral stays two-click, fully derived). `DraftManager` gained a second stored point + `NeedsApexClick`;
  draft right-click now **steps back one click** instead of discarding. This reopened the settled
  "every shape is two clicks" rule at the human's request.
- **CTRL/SHIFT remap + default-up (0.1.14):** **CTRL** is the cardinal/level snap (was SHIFT). **SHIFT**
  now (a) inverts the ghost while drafting and (b) **springs a placed guide back to its as-placed form**
  (`SpringBackCommand`; restores points + constraint from a creation snapshot — `OriginalControlPoints` /
  `OriginalConstraint`, persisted, never wired). Prerequisite shipped with it: **every shape defaults "up"**
  regardless of click order (`ShapeGeometry` frame perpendicular sign-normalised world-up).
- **Polygon (0.1.14) + Free-Shape (0.1.15):** `PolygonShape` (regular N-gon, 3–24 sides, per-guide `Sides`);
  `FreeShape` (irregular polyline, unbounded chained clicks ≤64 — click the last corner to finish open, the
  first to close the loop; `IsClosed`). Fill deferred on the Free-Shape.
- **Favorites redesign (0.1.15) + GUI polish (0.1.16–0.1.19):** the shape row is **four hard-kept pinned
  slots + a ▾ catalog fold-out** (right-click pins/unpins, never evicts); the **Current Shape chip** (F-menu
  and HUD); combined Projection+Fill and Divisions+Sides rows; Delete-mode "-ghost" tile greying; Free-Shape
  SHIFT-vertical; Fill greyed on Free-Shapes.
- **The 3D VOLUME family (0.1.20–0.1.23):** Sphere/Dome/Cylinder/Cone/Box, gated by
  `GuideShapeTypes.IsVolume`. Hollow = one-cell shell, Filled = solid, voxelised by a **cell-lattice scan**
  (not curve-marching) with a `MaxScanCells` (4M) **scan guard**. Sphere/Dome = two clicks; Cylinder/Cone/Box
  = three (reusing the triangle's apex machinery). Always Volumetric (Surface + Divisions gated off);
  deterministic up-axis `ShapeGeometry.BaseNormal`; wireframe targeting; free-air height. The
  "planar-only, 3D LATER" decision was reopened and delivered. **No new persisted/wire fields**; enum values
  appended; **DataVersion stays 7**.
- **Client-lifecycle + admin + safety (0.1.24–0.1.27):** GUI icons re-register per client start; pinned
  favorites persist (`ObjectCreationHandling.Replace`); **`/layout dispel all`** and **`/layout dispel
  <chunk radius>`** (controlserver); a **hard voxel ceiling** (`GuideManager.HardVoxelCeiling` = 10M) that
  rejects un-renderable giants regardless of caps; clear in-game create-rejection errors; the running voxel
  total widened **`int → long`**.
- **Process:** every revision bumps `modinfo.json` and ships a new `Layout<version>.zip` in the sibling
  **`..\Layout Zips\`** folder (0.1.14 → 0.1.27 this arc); older zips are never overwritten. Committed to
  `main` and pushed to GitHub through v0.1.27. Client-only mode (F4) now has a full implementation plan in
  `PLAN_CLIENT_ONLY.md` (target 0.2.0).

---

## Changelog — v2.6 → v2.7 (Session 10; full record in `SESSION_10.md`)

- **Icon-tile GUI:** every option row is now compact SQUARE icon tiles (42 px) — new
  **`UI/LayoutToolIcons.cs`** draws Cairo glyphs registered in `capi.Gui.Icons.CustomIcons`; stock
  `GuiElementToggleButton`s render them, so the exclusive-toggle plumbing is unchanged. Hover shows the
  option name. Delete-mode greying = native `Enabled=false`. New reference: `Lib\cairo-sharp.dll`.
- **Scale icons copy the game's native scheme:** an N×N grid of squares where **N is the voxel count**
  (1×1 smallest … 16×16 = a full block, drawn as ONE solid square filling the button); 4×4+ run
  edge-to-edge. Names are voxel counts, not fractions.
- **B-S10-1 FIXED (playtest-confirmed):** surface guides no longer shift behind block faces on reload —
  the air-side probe detects unloaded chunks and a 500 ms re-probe tick rebuilds once the area loads
  (see the Rendering register entry).
- **Divisions scroll-wheel (deferred Session-9 TODO) DONE (playtest-confirmed):** dropdown removed; the
  field is the native `GuiElementNumberInput` (built-in wheel + spinner buttons, `IntMode`, ±1/notch). The
  first attempt — a plain-text-field + dialog wheel override — failed (text inputs have no native wheel
  handler). **0.1.13** floors it at 0: the number input has no min, so `OnDivisionsTyped` snaps the display
  back on clamp (never shows negative or over-`MaxDivisions`).
- **Third tool mode — Edit (0.1.13):** `ToolMode` is now `Create · Edit · Delete`. In Edit the main setting
  rows act on the SELECTED guide (network senders) instead of the tool defaults, so per-guide editing no
  longer expands the panel with a separate section. Edit clicks **select only** (no reshaping); geometry
  editing stays in Create. See the Interaction-model register entry.
- **Division markers pair on off-cell boundaries (0.1.13):** `ShapeGeometry.ClaimMarkerPaired` claims the two
  near-tied cells when a boundary lands between voxels — the arch apex's even-span treatment, generalised.
- **Process:** every revision bumps `modinfo.json` and ships as a new `Layout<version>.zip` (0.1.10 → 0.1.11
  → 0.1.12 → 0.1.13 this session); older zips are never overwritten.

---

## Changelog — v2.5 → v2.6 (Session 9)

- **Shape catalog extended (F1 first wave):** **Line** (two anchors, no fill), **Triangle** (two base
  anchors + a born, draggable apex; free = scalene) with **Right / Equilateral / Isosceles** apex-derivation
  constraints, and **Rectangle** (two diagonal corners stored, other two derived) with the **Square**
  constraint. `GuideShapeType` and `ShapeConstraint` extended (pinned, append-only). New shared
  **`ShapeGeometry`** helper (planar frame + marker-claim). **DataVersion 4 → 5.** *(identifiers verified.)*
- **Body-click policy generalized:** only the **arch (free spline) family** takes body inserts; every other
  parametric shape (ellipse, line, triangle, rectangle) maps a body click to the **nearest handle**
  (left = grab, right = lock-toggle).
- **Divisions (new feature):** a per-guide, purely visual equal-parts overlay — recolors voxels at N
  arc-length boundaries (**magenta**, `VoxelRenderType.Division`), computed **renderer-side**
  (`DivisionMarks.Apply`) as a pure recolor that never touches geometry, counts, or caps.
  `GuideData.Divisions`; additive DTO fields; `GuideSetDivisionsPacket`; `SetDivisionsCommand`;
  `GuideManager.SetDivisions` (clamp + persist, no cap check); `MaxDivisions = 256`. *(identifiers verified.)*
- **Soft-point flow → slave-regime (supersedes the v2.5 proportional-only model):** an **interior grab**
  (held point unlocked, non-anchor) slaves every other unlocked point **onto the defining curve at its
  station with zero offset** — the apex genuinely contributes no pull; a **structural grab** (anchor or
  locked point) keeps the shape-preserving proportional flow. Plus a **chord-invariant phantom drop**
  (arch end-tangent phantoms derive from the anchor chord at 0.4×chord, not the neighbor knot's height), so
  interior inserts/locks no longer re-tilt the whole curve.
- **Deferred:** divisions scroll-wheel (drop the dropdown, keep the type-in field, wheel-adjusts-while-focused)
  — specced, not built. **Active follow-up:** B-S9-1 is substantially improved by first-hit picking, complete
  drag snapshots, and passive lock markers; final repeated-interaction playtesting remains.
