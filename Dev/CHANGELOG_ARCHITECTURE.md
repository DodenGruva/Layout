# Layout — Architecture Changelog (document-version history)

> **Split out of `ARCHITECTURE.md` to keep the orientation read cheap.** These are the per-revision deltas
> for the architecture document itself, v2.5 → v3.14. **Nothing here is unique:** each delta is already folded
> in place into `ARCHITECTURE.md`'s body (Settled Decisions Register, file tree, module map, persistence,
> edge cases), and each has a fuller narrative in its `SESSION_*.md` record. Kept as the audit trail — read
> it when you need to know *when* something changed, not *what* the current state is.

---

## Changelog — v3.13 → v3.14 (per-player cumulative-cap override + final release, v0.3.53; full record in `SESSION_27.md`)

- **Complementary administrator command:** `/layout totalvoxelcap <player> <number>` persistently overrides
  one original creator's cumulative public-guide voxel allowance; `0` restores the
  `perPlayerTotalVoxelCap` server default.
- **Attribution and safety:** `/layout voxelcap` remains the acting player's per-guide limit, while the new
  command follows original creator attribution. Lowering a cumulative cap never deletes guides; over-cap
  state may remain or shrink but cannot grow.
- **Complete enforcement:** ordinary and prepared immense creation, restore/push/redo, ordinary mutation,
  count-limited generation, and prepared immense sculpt commits resolve the effective creator cap.
- **Administrator visibility:** `/layout info` shows effective cumulative usage/limit and the explicit
  override; server status counts custom cumulative policies.
- **Compatibility:** world-scoped admin-policy JSON advances additively to version 2. Guide DataVersion 12
  and network protocol 16 are unchanged.
- **Final package:** v0.3.53 built with 0 warnings/errors and packaged as `Layout0.3.53.zip` with 40 entries
  and 37 assets.

## Changelog — v3.12 → v3.13 (renderer rollback + persistent visibility + creator budgets, v0.3.44 → v0.3.52; full record in `SESSION_26.md`)

- **Meshing experiments rejected (v0.3.44–v0.3.48):** same-colour greedy merging and follow-up
  curved-surface/depth/materialization variants reduced submission cost but introduced striping, splotchy
  view dependence, abrupt far-side disappearance, blurry/bright voxels, world-depth defects, delayed clean
  swaps, and worse real-play FPS.
- **Renderer baseline restored (v0.3.49):** `GuideRenderer` and `GuideMeshBuilder` returned to archived
  v0.3.42 behavior. Exposed faces, bounded organic materialization, whole-guide distance/frustum rejection,
  and render statistics remain; spatial final regions and greedy merging do not.
- **Persistent personal visibility (v0.3.50):** public `/layout off|on` and client-only `.layout off|on`
  immediately save `guideRenderingEnabled` in `layout-client.json` and restore it across reconnect/restart.
- **Capacity defaults and attribution (v0.3.50):** the per-guide default is 500,000; the new cumulative
  original-creator cap is 1,000,000 across currently existing public guides; the world-total default is
  unlimited. A narrow config migration changes only exact historical generated defaults.
- **Off-state communication and safety (v0.3.51–v0.3.52):** a saved off-state produces a login reminder.
  Chalking Kit guide actions/settings/undo/redo are blocked while hidden, attempted use flashes the recovery
  instruction, the HUD shows the same state, and disabling cancels transient draft/grab work.

## Changelog — v3.11 → v3.12 (measured guide culling + spatial settled meshes, v0.3.41 → v0.3.43; full record in `SESSION_25.md`)

- **Whole-guide camera rejection (v0.3.41):** conservative world-space bounds now combine the live view
  distance with Vintage Story's current frustum for placed guides, drafts, pending visuals, and remote
  markers. Complete off-screen guides submit no mesh.
- **Measurement seam (v0.3.42):** `.layout renderstats` exposes last-frame guide visibility, mesh batches,
  approximate submitted triangles, extras, and smoothed frame time through centralized upload/draw/delete
  tracking.
- **Spatial final geometry (v0.3.43):** immense clean final meshes are partitioned into fixed 32-block world
  regions with independent bounds. Small guides and organic growth previews keep their existing paths.
- **Boundary correctness:** all regional builders receive the complete guide occupancy set, so shared faces
  remain omitted across region boundaries. Mathematical floor division handles negative world coordinates.
- **Reload behavior:** immense saved Shells show a cheap scaffold and enter the existing below-normal
  materialization lane instead of synchronously rebuilding one monolithic mesh.
- **Measured result at this checkpoint:** the partial-view stress case fell from 128 drawn batches / 32.6M
  triangles / 7.5 ms to 33 / 7.5M / 4.4 ms, with 110 regional batches culled. Broader playtesting later
  rejected the spatial renderer; see the v3.13 entry and `SESSION_26.md`.

## Changelog — v3.10 → v3.11 (materialization completion + intrinsic HUD dimensions, v0.3.35 → v0.3.40; full record in `SESSION_24.md`)

- **Size-aware nucleation and cadence:** organic growth uses a six-site base plus one seed per 500 voxels,
  selected with deterministic spatial buckets. Batch cadence scales from about 18 ms for small guides to
  45 ms by 120,000 voxels and still yields under frame pressure.
- **Clean completion contract:** organic growth reaches the complete exact shell, remains visible for
  200 ms, then swaps atomically to a separately built clean uniform shell. The placement/sculpt scaffold
  disappears with the first growth batch.
- **Authority-aware effects:** placement particles and sound require both the final placement click/authority
  and the completed clean-shell swap, including previews that finish before placement.
- **Settled form changes:** Edit-mode Wireframe→Shell transitions use the cancellable off-thread
  materialization pipeline instead of a synchronous full-shell mesh build. A sculpt resized below the
  immense threshold now completes and clears the retained-operation latch.
- **Polygonal volume startup:** straight and tapered polygonal prisms precompute side normals and reject safe
  inner/outer annulus regions before expensive edge tests.
- **Intrinsic HUD dimensions:** sphere, dome, cylinder, cone, tapered-cylinder, and polygonal-prism families
  report local width and axial height instead of a rotated world-AABB footprint diagonal. No persistence or
  wire changes; DataVersion remains 12 and protocol remains 16.

## Changelog — v3.9 → v3.10 (moderation + bounded immense guides + organic materialization, v0.3.22 → v0.3.34; full record in `SESSION_23.md`)

- **Persistent server policy and moderation (v0.3.22–v0.3.25, protocol 14):** per-player jail and cap
  overrides, administrative inspection/cleanup commands, policy synchronization, and a comatose HUD state
  for drafts that cannot currently proceed.
- **Claim-authoritative public guides (v0.3.25):** public create/sculpt commits require access to every
  affected claimed block. Private/local guides retain their local-authority contract.
- **Bounded immense server lane (v0.3.26–v0.3.27, protocol 15):** guides above the 8,000-voxel immediate
  threshold use one below-normal-priority worker for pure generation/counting, followed by claim checks
  capped at 128 blocks or roughly 1 ms per 20 ms server tick. Explicit rejection completes the retained
  client handoff cleanly.
- **Retained progressive handoffs (v0.3.28–v0.3.32):** cancellable shape scans and bounded mesh uploads keep
  a selected-scale scaffold visible from the final click through authority and materialization. Immense
  reshape retains the old settled shell; competing immense placements/reshapes are gated; obsolete meshes
  retire across later frames.
- **View-distance and personal render control (v0.3.33, protocol 16):** conservative whole-guide bounding
  culling follows the live game view-distance setting. `/layout off|on` and `.layout off|on` skip/resume the
  complete guide render pass without deleting state.
- **Organic exact reveal (v0.3.34):** streamed deterministic multi-seed 26-neighbour growth, shaped by broad
  and detail noise plus fine grain, replaces bands and square chunks with torn/frayed spreading splotches.
  The union remains the generator's exact voxel set. Immense final sculpt generation joins the same single
  server lane, and no synchronous full-shell rebuild occurs at release.

## Changelog — v3.8 → v3.9 (action HUD + attribution + sculpting parity, v0.3.9 → v0.3.21; full record in `SESSION_22.md`)

- **Action-aware fixed HUD:** contextual Create/Sculpt/Edit/Delete headings and shape tile, active-state
  typography/outside tile accents, compact split dimensions, Total Voxels, exact cap percentage, selected
  Edit settings, fixed footprint, and the final typography/alignment cleanup.
- **Creator / Last Sculptor (DataVersion/protocol 12):** persisted friendly attribution with identical public
  and local-authority rules. Last Sculptor changes only for committed perceptible mutations; lightweight
  metadata broadcasts avoid unnecessary full-guide rebroadcasts.
- **On-demand attribution (protocol 13):** `/layout who` resolves selected, grabbed, then aimed guides on the
  invoking client. Attribution was deliberately removed from the always-visible HUD.
- **Large-guide sculpt refinement:** moving Shell grabs remain bounded wireframes, then settle into exact
  selected-scale geometry through fingerprinted background work and batched materialization. Tapered rims
  restore the placement-time flare/close constraints while sculpting.
- **Projection-transition correctness:** active Surface↔Volumetric changes translate all placed draft points
  by the signed half-voxel convention difference, fixing Arch/Half-Circle anchor loss.
- **GUI cleanup:** Edit right-click deselect, contextual selected settings, centered Fill label, and removal of
  the unused empty Edit instruction row.

## Changelog — v3.7 → v3.8 (adaptive large guides + structural form, v0.3.0 → v0.3.8; full record in `SESSION_21.md`)

- **Adaptive drafting (0.3.0–0.3.2):** expensive moving 3D poses use work/FPS-sensitive structural
  wireframes; the cursor retains selected-scale precision; settling runs one generation-safe background
  calculation and reveals the selected-scale shell in bounded pseudo-random batches. Draft HUD measurement
  yields to changing calculation glyphs while work is pending.
- **Placed metadata and giant-grab safety (0.3.3):** cached whole-block dimensions become the guide display
  name, placed hover never regenerates dimensions, and giant grabs retain their settled mesh while moving as
  wireframes. Cached metadata advanced DataVersion/protocol to 10.
- **Cancel quarantine (0.3.4–0.3.5):** right-click cancel reveals the retained settled mesh immediately;
  generation invalidation plus an authoritative-echo fingerprint prevents delayed shell regeneration and
  the observed 7→12 GB memory climb.
- **Structural topology (0.3.6):** round volumes use eight ribs; polygonal prisms use one longitudinal wire
  per polygon corner.
- **Persistent Shell/Wireframe form (0.3.7, DataVersion/protocol 11):** `IsWireframe`,
  `GuideSetWireframePacket`, and `SetWireframeCommand` provide save, public/private authority, multiplayer,
  undo, rendering, cap-count, rescale, grab, and client-default parity. 2D Hollow/Filled is unchanged; the
  same volume-tile positions contextually become Shell/Wireframe. Placement sound grows louder/lower/longer-
  ranged logarithmically with cached guide size.
- **Oversized shell fallback + icons (0.3.8):** Cylinder/Cone/Box retain their established voxelizer under
  the legacy scan budget, then switch to a surface-proportional ring/face sampler instead of rejecting valid
  oversized shells. Dedicated Shell and Wireframe Cairo glyphs replace the borrowed 2D fill icons.
- **Deferred, not built:** a cancellable chunk producer/consumer shell pipeline. Current materialization
  progressively uploads a complete precomputed result; see `SESSION_21.md` for the safe future design.

## Changelog — v3.6 → v3.7 (polygonal volumes + modifier interaction, v0.2.36 → v0.2.47; full record in `SESSION_20.md`)

- **Lock precision (0.2.36):** closed B-S9-1. The first rendered voxel hit owns a lock click; an existing
  control point wins only on its own nearest visible marker cell, so adjacent body voxels no longer snap
  back to a formerly locked point.
- **User-facing cleanup (0.2.37–v0.2.38):** Chalking Kit set-down is SHIFT+right-click; private cleanup is
  `.layout dispel all|<radius>` with no redundant `client` prefix.
- **Polygonal volumes (0.2.38, protocol 8):** appended Polygonal Prism and Tapered Polygonal Prism, reusing
  the Polygon side-count setting and adding full public/private, cap, edit, dust, GUI, and icon support.
- **Stage-aware modifiers/help (0.2.39–v0.2.43):** Line SHIFT-vertical, CTRL tapered-rim closure, and native
  held-item notes that refresh only where applicable without replaying the ground-storage hint.
- **Orientation/diagonal/rim safety (0.2.45, DataVersion/protocol 9):** persisted SHIFT flat-side alignment
  for all polygon families; CTRL+SHIFT 45-degree Line/Free-Shape diagonals; tapered rims capped to the base
  radius unless SHIFT deliberately allows flare.
- **GUI finish (0.2.47):** Create header is `Create Mode` plus the normal-size right-aligned shape name;
  the unsuccessful v0.2.46 adaptive-shrink experiment was reverted.

---

## Changelog — v3.5 → v3.6 (stabilization + placement effects, v0.2.29 → v0.2.35; full record in `SESSION_19.md`)

- **Cap semantics (0.2.29):** removed Tapered Cylinder's fixed 4M scan cutoff. Raised/unlimited server caps
  now remain meaningful; the 10M hard render ceiling still protects the client.
- **Height → rim handoff (0.2.30–0.2.32):** the top begins at the 60% default and cannot inherit the height
  click's remote cursor target. A real mouse release rearms input; entering an annular band around the born
  rim captures cursor control. The final fix listens to the engine's actual mouse-up event.
- **Adaptive draft work (0.2.31):** input remains responsive while expensive preview/HUD/cap work throttles
  by the last draft count: about 33/10/5/2 Hz across increasing voxel tiers. Unlimited public servers skip
  live per-guide cap-clamp recounts; exact final validation and the hard ceiling remain.
- **Placement effects (0.2.33–0.2.35):** retained the original falling flecks and added bounded parametric
  surface sampling for zero-gravity dust. 2D shapes scatter sideways; 3D shapes drift outward/upward across
  their full shell. No voxel generation is performed for the effect.
- **Release verification:** public/private multiplayer passed; fired jugs still craft and raw jugs do not.
  B-S9-1 is narrowed to an adjacent-voxel snap after unlocking a previous lock.

---

## Changelog — v3.4 → v3.5 (Tapered Cylinder, v0.2.24 → v0.2.28; full record in `SESSION_18.md`)

- **Tapered Cylinder (0.2.24, protocol 7):** added the four-click frustum with a height handle and independent
  rim handle. A 60% born top radius, ratio-preserving base resize, and up-to-4× flare are the shipped calls.
- **Cylinder-family sizing and cap work (0.2.25–0.2.27):** corrected AABB scan estimates, replaced repeated
  full cap bisection with a persistent bounded bracket, fixed the rim clamp's off-axis reference, and made
  free-air rim aiming stable at shallow view angles.
- **Recipe correctness (0.2.28):** Chalking Powder jug variants require `game:jug-*-fired`; raw clay jugs
  cannot hold dye and no longer match.

---

## Changelog — v3.3 → v3.4 (the seven-item backlog, v0.2.22 → v0.2.23; full record in `SESSION_17.md`)

- **Chalk refill channels: SERVER config → CLIENT preference (0.2.22, protocol 5 → 6).** Both flags moved to
  `layout-client.json` (still default false; ground-storage refill is never gated). **The subtlety:** a plain
  client-side gate would have made the HOTBAR toggle a no-op, because `ItemChalkingPowder`'s held-interact
  runs on BOTH sides and the SERVER mutates the stacks — a permissive server refills for a player who turned
  it off. So the server is *told* the preference: new **`ChalkRefillPrefsPacket`** (C→S on join, stored
  per-player, cleared on disconnect). The inventory channel needed none of this (already client-initiated).
  Server-side *integrity* validation in `OnInventoryChalkRefill` deliberately KEPT — only the *policy* check
  went. The two dead `GuideBulkSyncPacket` flags stay declared as append-only padding.
- **Hard chalk ceiling (0.2.23):** `ItemGuideTool.MaxChalk = 32` is the one source of truth. `GetChalk` reads
  the stored attribute directly and clamps; `IsChalkFull` replaced every fullness test. `GetMaxDurability`
  and `GetRemainingDurability` are overridden **without calling base**, because base **walks the
  collectible's BEHAVIORS** — the hook xskills uses to grant a crafting-quality durability bonus. Inflated
  kits self-heal on next use. 21/21 offline harness.
- **Hotbar refill-off warning removed (0.2.22):** a disabled convenience is a silent no-op, not an error.
- **Mid-draft broadcast rate AUDITED (no change):** drafting sends ~2 packets total with no per-tick traffic;
  the only continuous path (grab-and-reshape) is already ≤10 Hz and movement-gated; packets carry an edit
  array, not geometry. A remote draft renders as a single static anchor dot, never an evolving guide.
- **Publication readiness (0.2.22–0.2.23):** authorship → **Doden**; `dependencies.game` → **`"1.22.0"`**
  (a MINIMUM, so all 1.22.x); `Layout.csproj` auto-resolves the install
  (`-p:VintagestoryDir` → `VINTAGE_STORY` → platform default) with a `VerifyVintagestoryDir` guard; zero
  tracked files retain a developer username or absolute path.

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
