# SESSION 16 — v0.2.10 → v0.2.21: the large-guide mesh pass (Stage A), volume/visual/refill polish

> The performance arc the previous sessions kept pointing at, plus a run of playtest-driven polish. Current
> build: **v0.2.21**, **DataVersion 8**, **protocol 5**, **68 C# source files** (no new files — all edits to
> existing ones + the High fill-state asset). **All of it is committed and pushed on `main`:** v0.2.10–v0.2.13
> in `de830b1`, v0.2.14–v0.2.21 in `6a48d2f`, and this session's doc pass in `b227d7d`.

---

## 1. Interaction + visual polish (v0.2.10–v0.2.13) — committed `de830b1`

- **Whole-guide placement dust (0.2.10).** `ChalkEffects.PlacementEffects` took the whole `GuideData`:
  2D guides puff along their SAMPLED CURVE (≈ one point / half block, capped at 40, anchors full-size); 3D
  volumes puff only around their BASE RING (capped 24) — never the shell, so a 100-block sphere can't flood
  the particle system. `LocalGuideAuthority.Create` now returns the created guide so private placements emit
  identically. Box's ring is an accepted circular approximation.
- **Edit-mode pencil (0.2.10).** Snipped the stray graphite stub that hung outside the tip silhouette; added
  the eraser band.
- **Dome faces the clicked surface (0.2.11, playtest-confirmed).** The 0.1.23 "deterministic +axis" default
  cured anchor-order flips but discarded the clicked face's DIRECTION (the stored `ShapePlaneAxis` carries
  the axis, not the sign). The first click now captures the face sign (`DraftManager.DraftPlaneNegative`) and
  the controller folds it into the Dome's `inverted` flag (`EffectiveInverted`): floor → up, ceiling → down,
  either side of a wall → toward you. SHIFT still inverts relative to that default. Client-only; no wire/save
  change. Other shapes untouched (cylinder/cone/box take direction from the height click; sphere symmetric).
- **Ground z-fighting (0.2.10–0.2.13).** Voxel faces sitting exactly on a block-grid plane are inset off it
  so they never share a plane with world block faces (jarring at scale 16), plus the lowest layer's bottom
  face is always lifted (slab/chiseled tops). Grid-coplanar faces ONLY — a global shrink is the Module-7
  seam mistake. Tuned in play: 0.004 seamy → 0.001 shimmered with distance → **0.003** (the depth buffer
  resolves less separation the farther you stand).

## 2. Large-guide mesh pass — STAGE A: exposed-face meshing (v0.2.14–v0.2.16)

The SESSION_14 frontier, delivered. The confirmed bottleneck was `GuideMeshBuilder` emitting a full cube
(8 vertices / 36 indices, all six faces) per voxel into one monolithic buffer.

- **0.2.14 — exposed-face meshing.** The ordinary Volumetric cube path now builds a presence set of the
  RENDERED cells (hidden-filtered so a hidden guide's anchor cubes stay whole), **pre-counts** exactly which
  faces have no neighbour, allocates the mesh to that exact size, and emits ONLY those faces. Per-voxel role
  colours are preserved (each face belongs to its own voxel — a plan invariant). Interior faces of filled
  bodies and shell laterals vanish. The Surface tile/slab paths stay on the legacy whole-box builder per the
  staged plan. A ~100-block hollow sphere shell drops from ~12 to ~4 triangles per voxel.
- **0.2.15 — seam flush fix.** 0.2.14's inset shrank a box at EVERY grid-coplanar face, but where a
  neighbour is present that shared face is gone — so the neighbours' surviving faces each stopped short and
  opened a see-through slit along every block line. Fix: **inset a face only when it is EXPOSED**; where a
  neighbour exists the box runs flush to the boundary.
- **0.2.16 — solidity-aware clearances.** Filled/large guides still showed interior seams: stair-step skin
  faces landing on grid planes were pulled off them even when they only bordered AIR (behind which, on a
  filled body, is empty space that light shows through). Fix: `GuideMeshOptions.IsNeighborSolid` — a
  world-solidity probe from `GuideRenderer` — so a grid-coplanar exposed face is inset ONLY when a solid
  world block sits across the plane. Faces bordering air stay exactly on the grid. Same probe semantics as
  the Surface air-side vote; unloaded chunks treated conservatively.
- **A deterministic mesh-count harness** (a throwaway console project against the Release DLL, per
  SESSION_14 step 5) validates all of this OFF the game: exact face counts (single / adjacent / L / 3³ solid
  / 3³ hollow-with-cavity), block-boundary flush (vertices land exactly on the plane, no slit positions
  exist), air-vs-solid inset behaviour, hidden-guide anchor cubes, and the legacy slab/tile paths unchanged.
  15/15 green. **NOTE:** the inset now only ever separates a guide face from a WORLD block face — never two
  guide voxels — so it produces no interior seams at any scale.

## 3. Filled 3D volumes RETIRED (v0.2.17, human-directed)

Post-Stage-A, a filled volume's interior emits no geometry (every interior voxel is fully surrounded), so
"filled" looked identical to hollow (bar the cylinder cap / dome floor) at 10–100× the voxel count — pure
invisible lag, and the source of a max-size-filled-sphere "stuck to the cursor" freeze. Human call: retire
it. Gated three-deep: the GUI greys the Fill tiles on volumes (with an explanatory hover), `GuideManager`
rejects/normalises Fill on volumes (`SetFilled` + the create normaliser), and every volume shape coerces
`filled = false` at both voxel-generation entry points — the last layer **auto-lightens legacy
`IsFilled=true` volumes in old saves** (no migration, no data change). Rationale centralised in
`GuideShapeTypes.IsVolume`'s doc. 2D fills are untouched; a cylinder cap / dome floor is cheaply recovered
with a filled 2D circle at the base.

## 4. HUD + GUI fixes (v0.2.18)

- **HUD hover no longer re-measures every tick.** `GuideHud.SetExaminedGuide` is fed ~33×/s by the
  controller and was wiping the measurement cache on every call — so hovering a huge guide regenerated its
  whole voxel set 30+ times a second (the placement workload, continuously). Guard added: same target →
  keep the cache. Server-side edits still invalidate via the update event.
- **Number-field alignment.** The Divisions / Sides inputs (and their spinner arrows) now sit exactly on the
  icon rows' tile grid — each field spans two tile columns; on the polygon row the Sides label takes column
  2 and its field columns 3–4. `AddNumberControl` / `AddNumberPairControl` take `tileGap`.

## 5. Draft cap-clamp regression restored (v0.2.19)

The mid-draft "stop growing at the per-guide voxel cap" behaviour was gone. Investigation: it never had a
literal clamp — over-budget ghosts were just too heavy to rebuild, so the preview froze at its last valid
size. Stage A made huge ghosts cheap and the accidental stop vanished. Implemented the REAL mechanism,
mirroring the v0.1.48 drag clamp: `ClampDraftAimToPerGuideCap` binary-searches the aimed point back toward
its anchor to the largest size that still fits (same threshold counter the server uses), applied to the
ghost preview AND the completing click. So pulling a draft pins at the cap, and the click places AT the cap
instead of erroring. Free-Shape chains excluded (discrete clicks; the completion pre-check covers them);
fails open.

## 6. High fill-state model (v0.2.20, human asset)

A fifth kit model so the pristine FULL bag is reserved for a COMPLETELY full kit (32). Thresholds now:
**full = 32 · high 22–31 · medium 11–21 · low 1–10 · empty 0.** `ItemGuideTool.FillIndexFor` returns 0–4;
imported `chalkbag-high.json` + 5 textures (same Blender-export quirk cleanup — `null`/`string`/`#0` → linen).

## 7. Refill channels — config + inventory refill (v0.2.21)

Three refill channels, server-configured (`layout.json`), ground-only by default:

- **Ground storage** (SHIFT+right-click a set-down kit) — always allowed, ungated.
- **`allowHotbarChalkRefill`** (default false) — hold powder, right-click a hotbar kit.
- **`allowInventoryChalkRefill`** (default false) — right-click a cursor powder stack onto a kit's inventory
  slot.

Both flags **sync to clients** on join (additive fields on `GuideBulkSyncPacket`; **protocol 4 → 5**), so the
client gates the interaction and messages ("Hotbar refill is off here…") rather than dead-clicking. The
per-side value is resolved through `LayoutModSystem.{Hotbar,Inventory}ChalkRefillAllowed` (server = config,
client = synced). Hotbar gate lives in `ItemChalkingPowder.ResolveTargetKit`.

**Inventory-refill mechanism (the real design work).** VS's slot-merge path (`TryMergeStacks` /
`GetMergableQuantity`) is UNSAFE for this: to reach it, `GetMergableQuantity(kit, powder, AutoMerge)` must be
> 0 (the manual-click gate hardcodes AutoMerge), which also makes auto-pickup / shift-click route powder INTO
kits and can leave dropped powder un-collected. So instead: a client `capi.Event.MouseDown` hook detects
cursor-powder + hovered-kit (`InventoryManager.MouseItemSlot` / `CurrentHoveredSlot`), sends the target slot
(`ChalkInventoryRefillPacket { InventoryId, SlotId }`), and the server (`OnInventoryChalkRefill`)
re-validates — feature on, slot really a non-full kit, cursor really powder — before consuming one powder
and adding its chalk. **Safety net:** the server validates the cursor STILL holds powder, so if VS's default
slot-swap won the mouse-hook ordering race, validation fails and nothing happens — the worst case degrades
to a harmless swap, never a corrupted inventory.

## 8. Open items / flags for the next session

- ~~**VERIFY the inventory refill in play.**~~ **RESOLVED — human-confirmed working.** Both statically
  unconfirmable pieces hold in play: the `MouseDown` hook wins the ordering race against the default
  slot-swap, and the `InventoryID` round-trip (client `ItemSlot.Inventory.InventoryID` → server
  `GetInventory(id)`) resolves correctly. Hotbar refill confirmed too.
- **Guide updates read well to other players in multiplayer — confirmed GOOD, and since AUDITED.** A guide
  changing a few times per second reads correctly as "another player is moving that." Packet-rate audit
  (`TODO.md` §A2) found nothing to fix: drafting sends ~2 packets total and no per-tick traffic, and the
  continuous path (grab-and-reshape of a placed guide) is already capped at ≤10 Hz and skipped when the aim
  hasn't moved. Note the observation was of that reshape path — a remote DRAFT renders only a static anchor
  dot, never an evolving guide.
- ~~`main` is at v0.2.13; v0.2.14–v0.2.21 are uncommitted~~ **— DONE: committed and pushed** (`6a48d2f`
  code, `b227d7d` docs).
- **Large-guide mesh, Stage B is next** if Stage A's win isn't enough on the ~100-block sphere: per-guide
  spatial chunk meshes with reliable ownership/disposal + culling, then Stage C greedy same-colour face
  merging. Invariants and the staged plan are in `SESSION_14.md` §6–§7 (still current). Filled volumes are
  now retired, so the worst-case voxel counts are much lower than when that plan was written.
- **B-S9-1** (repeated lock/drag/revert/unlock soak) and the **F4/public multiplayer regression pass**
  (now including a chalk pass — public + private charging, all three refill channels) remain owed.
- The z-fight inset (`BlockPlaneInset = 0.003`), particle density, and snap volume are all single-constant
  tuning knobs if feel demands.
