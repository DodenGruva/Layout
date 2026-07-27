# Layout — Project Status & Handoff

> ⚠️ **THIS FILE TRAILS BY THREE SESSIONS (last refreshed at v0.3.53; current build is v0.3.89).** What it
> describes is still accurate, but it does not know about **Session 28** (vertex welding, settled-shell
> streaming, the custom guide shader, voxel outlines), **Session 29** (block-occupancy recolour, the face
> outset, the GUI settings page) or **Session 30** (F6 Move mode, protocol 17, 79 source files).
> For current state read `HANDOFF.md` and `SESSION_28/29/30.md`; for the player-facing log read
> `CHANGELOG.md`. Prefer source for exact identifiers.

**Checkpoint: Layout v0.3.53 final release built, packaged, and documented** (the `ClientOnlyFallback` branch merged via
PR #1). **F4** is
implemented and playtested: automatic client-only authority on servers without Layout; opt-in private
overlays on Layout servers; per-world/per-UID client persistence; placement/reshape/settings/undo parity;
public/private commands and publication; ownership cues; mixed-authority undo routing; backup-recovery
hardening. **F5 is implemented and playtested:** the tool is the **Chalking Kit** — custom deflating
**5-state** model (full/high/medium/low/empty), 32-chalk durability (2D −1 / 3D −2, completed placements
only, no lockout at 0, kit can never break; **32 is a HARD ceiling as of v0.2.23** — immune to other mods'
crafting-quality bonuses), **Chalking Powder** refills (ground storage always; hotbar + inventory-slot
channels are **client-preference** opt-in as of v0.2.22), private placements charge via `ChalkChargePacket`,
ground storage, whole-guide chalk-puff/snap feedback. **The large-guide mesh pass Stage A shipped, and the
v0.3 immense-guide path now uses motion wireframes, selected-scale cursor precision, cancellable progressive
generation, retained authority handoffs, organic streamed materialization, and frame-aware mesh retirement.**
Public immense-guide validation runs in one bounded low-priority server lane; whole-guide frustum rejection
and persistent `/layout off|on` prevent unwanted draws. The off-state blocks Chalking Kit guide actions and
explains `/layout on` in chat, warnings, and the HUD. Filled 3D interiors are
retired; volumes may persist as Shell or structural Wireframe. Targets **all of VS
1.22.x**; the repo is publication-clean (no personal paths/usernames tracked).
**DataVersion 12; protocol 16; 77 source files; 15 shape types / 21 picker tiles.** Release zips:
`..\LayoutZips\` (through `Layout0.3.53.zip`). **Top tasks:** field-soak v0.3.53 persistence,
cumulative creator-budget overrides, and off-state tool gating; retain the two verification debts (32-chalk ceiling
vs xskills, and VS 1.22.0/1.22.1 support). Spatial meshes and greedy merging were rejected and rolled back.
**B-S9-1 closed in v0.2.36.** Standing rule: ship a zip per code iteration; update docs / commit ONLY on the
human's say-so. Detail lives in **`SESSION_12.md`–`SESSION_27.md`**,
`PLAN_CLIENT_ONLY.md`, `PLAN_CHALKING_KIT.md`, and `TODO.md`; the authoritative plan is
**`ARCHITECTURE.md`** (v3.14).

---

## 0. How to use this document

The doc set (now a Claude Code repo):

1. **`ARCHITECTURE.md`** — the authoritative plan (v3.14); Settled Decisions Register updated through v0.3.53.
2. **The code** — `src/` (**77 files**: 74 through Session 22, plus Session 23’s
   `ProgressiveVoxelGeneration`, `GuideClaimAccessValidator`, and `LayoutAdminPolicyManager`; historical
   breakdown: 43 at Session-8 end + 6 Session-9 — LineShape, TriangleShape,
   RectangleShape, ShapeGeometry, DivisionMarks, SetDivisionsCommand; + 1 Session-10 — LayoutToolIcons;
   + 4 Session-11 — PolygonShape, SetSidesCommand, SpringBackCommand, FreeShape; + 5 the 3D family
   (v0.1.20–0.1.21) — SphereShape, DomeShape, CylinderShape, ConeShape, BoxShape; + 5 F4 —
   ClientAuthorityMode, ClientToolGate, ClientWorldGuidePersistence, LocalGuideAuthority,
   GuideManagerDependencies; + 1 Session-13 — RemoveLockMarkerCommand; + 1 Session-14 —
   SphericalShellScan; + 2 Session-15 — ItemChalkingPowder, ChalkEffects; Session-16 added no new files —
   all edits to existing ones plus the High fill-state asset; + 1 Session-18 — TaperedCylinderShape; + 1
   Session-20 — PolygonalPrismShape),
   `assets/layout/`, `modinfo.json`, `modicon.png`, `Layout.csproj`, `BUILD_INSTRUCTIONS.txt`.
3. **`TODO.md`** — the live punch-list (renamed from `OUTSTANDING_ITEMS.md`).
4. **`SESSION_9.md`** … **`SESSION_27.md`** — standalone records (SESSION_12 runs through v0.1.45;
   SESSION_13 covers v0.1.46–v0.1.52; SESSION_14 is the v0.1.53 shell/mesh handoff; SESSION_15 is the
   v0.2.0–v0.2.9 Chalking Kit arc; SESSION_16 is the v0.2.10–v0.2.21 mesh + polish arc; SESSION_17 is the
   v0.2.22–v0.2.23 seven-item backlog; SESSION_18 is the v0.2.24–v0.2.28 Tapered Cylinder arc;
   SESSION_19 is the v0.2.29–v0.2.35 stabilization/effects arc; SESSION_20 is the
   v0.2.36–v0.2.47 polygonal-volume/modifier arc; SESSION_21 is the v0.3.0–v0.3.8 adaptive large-guide and
   persistent structural-wireframe arc; SESSION_22 is the v0.3.9–v0.3.21 HUD/attribution/sculpting arc;
   SESSION_23 is the v0.3.22–v0.3.34 moderation/claims/immense-guide/materialization arc; SESSION_24 is the
   v0.3.35–v0.3.40 materialization-completion, clean-shell-transition, and HUD-dimension arc; SESSION_25 is
   the v0.3.41–v0.3.43 spatial experiment; SESSION_26 records v0.3.44–v0.3.52, the renderer rollback,
   persistent visibility, cumulative creator cap, and off-state Chalking Kit lockout; SESSION_27 records
   v0.3.53's per-player cumulative-cap override and final release).
5. **`HANDOFF.md`** (repo root) — the consolidated current-state brief for external AI analysis
   (scope / status / direction / performance characteristics).
6. **`PLAN_CLIENT_ONLY.md`** — F4's finalized implementation record and behavior matrix.
7. **`PLAN_CHALKING_KIT.md`** — F5's design rationale, marked implemented with plan-vs-shipped deltas.
8. **This status doc.**

> Precedence: **`ARCHITECTURE.md` is the plan; `HANDOFF.md` (repo root) is the consolidated brief.** This
> doc tracks *status and intent*. The source of truth is the `.cs` files — prefer reading the code over any
> identifier quoted in prose.

> **Build dependencies:** `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll`, `cairo-sharp.dll`
> (Session 10, icon glyphs), and `VSSurvivalMod.dll` (Session 15, `IContainedMeshSource` for ground-storage
> fill meshes; lives in the game's `Mods\`) — all ship with the game; Newtonsoft/protobuf/cairo live in `Lib\`.

> **Runnable-mod packaging:** build, then zip `modinfo.json` + `modicon.png` + `assets\` + `Layout.dll` at
> the **zip root** into `VintagestoryData\Mods`. Server `layout.json` + client `layout-client.json` appear in
> `ModConfig` after first run. **Versioning rule (Session 10, standing):** every revision bumps
> `modinfo.json` and ships as a NEW `Layout<version>.zip` — never overwrite an older release zip.

---

## 1. Status at a glance

- **Modules 1-7: COMPLETE.** Math, data, systems + undo, networking, rendering, UI, integration.
- **Session 8: COMPLETE.** Two-mode control scheme; four-shape catalog (arch / half-circle / circle /
  ellipse); absorb-or-break; soft-point flow (proportional); Tier-2 fill; sampled-curve targeting; tile GUI;
  Surface-exit bake.
- **Session 9: MOSTLY COMPLETE.** *(identifiers verified against source.)*
  - **Shapes:** Line, Triangle (+ Right / Equilateral / Isosceles), Rectangle (+ Square) — F1 first wave.
  - **Divisions:** new purely-visual equal-parts overlay (magenta marks, arc-length spacing, renderer-side).
  - **Soft-point flow reworked:** the **slave-regime** model — interior grabs slave unlocked points onto the
    curve (zero pull; apex no longer pins); structural grabs keep shape-preserving flow. Human: "MUCH better."
  - **DataVersion 4 -> 5.**
- **Session 10 (in Claude Code, v0.1.10–0.1.13): UI + fixes.** Detail in `SESSION_10.md`.
  - **Doc verification:** every Session-9 `⚠ verify` tag checked against source — all accurate.
  - **Icon UI pass:** compact square icon tiles for every option row (new `UI/LayoutToolIcons.cs`, Cairo
    glyphs via `CustomIcons`; → **50 source files** at Session-10 end, now 59); native-style N×N scale icons (N = voxel count; 16× =
    one solid block); hover tooltips; native `Enabled=false` Delete-mode greying.
  - **B-S10-1 FIXED (playtest-confirmed):** surface guides no longer shift behind block faces on reload
    (unloaded-chunk-aware air-side probe + 500 ms re-probe tick).
  - **Divisions scroll-wheel DONE (confirmed in play):** native `GuiElementNumberInput` (built-in wheel +
    spinners); **floored at 0** in 0.1.13.
  - **Third mode — Edit (0.1.13):** the setting rows edit the SELECTED guide (Scale/Projection/Plane/Fill/
    Divisions/Visibility) — no appended section, no panel expansion; Edit clicks select-only, geometry stays
    in Create. `ToolMode` = Create · Edit · Delete.
  - **Division markers pair (0.1.13):** off-cell boundaries claim both near-tied voxels (arch-apex treatment,
    generalised via `ShapeGeometry.ClaimMarkerPaired`).
- **Session 11, batch 1 (v0.1.14): the whole human backlog — playtest-CONFIRMED same session.**
  - **B-S10-2 fixed & confirmed:** air-side-probed Surface→Volumetric bake (volume grows into the air).
  - **Triangle → three-click** (free/right/isosceles; equilateral stays 2-click); draft right-click steps
    back per-click. Confirmed.
  - **CTRL = cardinal constraint; SHIFT = draft invert + placed-guide spring-back** (as-placed snapshot,
    one undo step); all shapes default "up" regardless of click order. Confirmed.
  - **Polygon (N-gon):** 3–24 sides, per-guide `Sides` + number row, cap-checked SetSides, undoable.
    Confirmed.
  - **Tweaks:** auto-sized tile tooltips; Surface slabs 0.01 → 0.0025.
  - Picker worked but its favorites flow was redesigned on feedback (→ batch 2).
- **Session 11, batches 3–5 (v0.1.16–v0.1.19): rapid GUI polish — playtest-CONFIRMED**
  (`SESSION_11.md` §13/§15): Sides hard floor · 5-wide catalog + white separator ·
  favorites as YELLOW glyphs · combined Projection+Fill and Divisions+Sides rows · title-bar dead space
  removed · right-aligned header shape name · HUD chip · Delete-mode "-ghost" tile greying · Free-Shape
  SHIFT-vertical · pin/unpin vocabulary · Fill greyed on Free-Shapes · zips → `..\LayoutZips\`.
- **Session 11, batch 2 (v0.1.15): playtest feedback + three new features — playtest-CONFIRMED
  ("This was tested. Good work.").** Detail in `SESSION_11.md` §10.
  - **Favorites HARD-KEPT:** four slots; starring never evicts (message when full); right-click unstars
    (slot or catalog); ★ badges; empty-slot placeholders; no auto-padding of existing configs.
  - **Current Shape chip:** permanently-lit yellow glyph of the picked shape on the Mode row.
  - **SHIFT-centred apex** on the triangle's third click (perpendicular bisector, live ghost).
  - **Free-Shape:** irregular polyline, chained clicks; click-last = open, click-first = close (≥3);
    right-click steps back a corner; CTRL snaps to the previous corner; body inserts like the arch; fill
    deferred; 64-corner cap. `IsClosed` → **DataVersion 7**; additive Chain/Closed on the create request.
- **Post-finalize — the 3D VOLUME family + hardening (v0.1.20–v0.1.27): playtest-CONFIRMED through 0.1.26.**
  Detail in `SESSION_11.md` §16–§17.
  - **3D volumes (0.1.20–0.1.21):** Sphere/Dome/Cylinder/Cone/Box — `GuideShapeTypes.IsVolume`; hollow =
    one-cell shell, filled = solid, via a **cell-lattice scan** (`MaxScanCells` 4M guard); Sphere/Dome
    2-click, Cylinder/Cone/Box 3-click; always Volumetric; deterministic up-axis `ShapeGeometry.BaseNormal`;
    wireframe targeting; own 3D catalog section. **At initial delivery** there were no new persisted/wire
    fields (DataVersion 7); v0.3.7 later added shared persistent `IsWireframe` form state (version 11).
  - **3D fixes (0.1.22–0.1.23):** `SetShape` whitelist, centred 2D/3D labels, catalog stays expanded,
    Divisions row hidden on volumes, height-inversion fix, free-air height.
- **Client-lifecycle + admin + safety (0.1.24–0.1.27):** icons re-register per client start; favorites
    persist (`ObjectCreationHandling.Replace`); **`/layout dispel all` / `/layout dispel <radius>`**
    (controlserver); **hard voxel ceiling** (`HardVoxelCeiling` 10M) rejects un-renderable giants regardless
    of caps; clear in-game create-rejection errors; running voxel total widened `int → long`.
- **Session 12 — Client-only fallback (v0.1.28–v0.1.45): playtest-CONFIRMED.** Detail in
  `SESSION_12.md`; final behavior in `PLAN_CLIENT_ONLY.md`.
  - **Vanilla-server fallback:** after a three-second detection grace, the client runs a local
    `GuideManager`; Hammer (any vanilla variant or durability) in the offhand + Flax Twine in the main hand
    activates the normal HUD and F-menu. These private-by-necessity guides retain the normal blue/indigo
    anchor palette.
  - **Mixed Layout servers:** `allowClientOnlyMode` defaults false. When enabled, `/layout private` and
    `/layout public` select where new guides go; the real Layout tool is required in both modes. Public and
    private guides overlay in one controller and renderer; mixed-server private anchors are orange, and the
    compact HUD/target readout identifies private work without changing the main GUI.
  - **Persistence and commands:** private guides save per world/server + player UID under
    `Layout/ClientOnlyGuides`, with atomic replace, one backup, and corrupt-file quarantine. Client commands
    are `.layout dispel all|<radius>`; `/layout client push all` publishes up to 100 accepted guides
    as a committed, non-undoable transfer.
  - **Parity and hardening:** local create/edit/delete/locks/constraints/reshape/settings/undo use the same
    side-neutral `GuideManager`; ownership routes existing-guide mutations and last-operation authority
    routes undo/redo. Protocol 2 adds append-only policy/mode/push packets; DataVersion remains 7.
- **Session 13 — cap performance + interaction correctness (v0.1.46–v0.1.52): playtest IN PROGRESS.** Detail
  in `SESSION_13.md`.
  - **Cap validation:** five 3D volume shapes support threshold-aware counting; validation can stop once a
    cap is exceeded instead of building the full voxel set.
  - **Drag cap:** release reconciliation plus a client binary-search clamp keeps live resizing at the largest
    acceptable size without the former over-cap disappearance or flicker. Human-confirmed natural feel.
  - **Target/cancel correctness:** first-hit rendered-voxel picking, full geometry cache fingerprints, and
    complete pre-drag snapshots address adjacent-cell locks, stale post-drag targets, and inconsistent cancel.
  - **Non-deforming locks:** passive Arch lock markers do not become curve knots until actively dragged.
    v0.1.52 removes stale unlocked markers and preserves curve-relative insertion order. This advances the
    additive schema to DataVersion 8 and protocol 3.
- **Session 14 — hollow-shell scaling (v0.1.53): playtest-CONFIRMED; mesh work next.** Detail and resume plan
  in `SESSION_14.md`.
  - **Generator:** `SphericalShellScan` analytically narrows each X/Y column to its Z shell bands and then
    applies the old exact predicate. Hollow Sphere/Dome no longer hit the 4M cubic candidate-cell guard.
  - **Validation:** exact voxel-set parity across all scales/orientations/inversion; 20-block hollow Dome =
    242,500 voxels in ~5 ms in isolation; Release build clean.
  - **Human playtest:** a roughly 100-block hollow Sphere placed successfully and caused visible lag.
  - **Confirmed frontier:** `GuideMeshBuilder` still emits 8 vertices + 36 indices for every voxel, shared
    faces included, as one uploaded mesh per guide. Next pass is exposed faces → chunks/culling → greedy
    same-colour merging. Filled Sphere/Dome retain their old guarded cubic path.
- **Session 15 — the Chalking Kit, F5 (v0.2.0–v0.2.9): playtest-CONFIRMED.** Detail in `SESSION_15.md`.
  - **Reskin + ground storage (0.2.0–0.2.4; gesture updated in 0.2.37):** custom model, item renamed Chalking Kit,
    SHIFT+right-click set-down
    (idle-gated; the F4 input hook now steps aside for the gesture — it had made the item's interact hooks
    unreachable).
  - **Durability (0.2.5–0.2.6):** 32 chalk; 2D −1 / 3D −2 on completed placements only; no refunds; NO
    lockout at 0 and the kit can never break (custom clamp, never vanilla `DamageItem`); creative exempt;
    `enableChalkDurability` config; **private placements charge** via client-reported `ChalkChargePacket`
    (**protocol 3 → 4**); Chalking Powder + recipes (8× powder/flour + 0.1 L yellow dye → 8; kit = 8 powder
    + sack/twine/rope/nails).
  - **Fill-state models (0.2.7, extended to five in 0.2.20):** human-made deflating models with progressively
    chalkier textures (full=32 · high 22–31 · medium 11–21 · low 1–10 · empty 0), rendered everywhere via
    `OnBeforeRender` + `IContainedMeshSource` (new `VSSurvivalMod.dll` reference).
  - **Ground refill + feedback (0.2.8):** SHIFT+right-click a stored kit refills in place (bag re-inflates
    live); chalk-puff particles on refill/placement; the bow-release chalk-line snap on placement.
- **Session 16 — the mesh + polish arc (v0.2.10–v0.2.21): playtest-CONFIRMED. Detail in `SESSION_16.md`.**
  - **Interaction/visual (0.2.10–0.2.13, committed `de830b1`):** whole-guide placement dust, pencil-icon fix,
    **Dome faces the clicked surface**, and the tuned ground **z-fight inset** (`0.003`).
  - **Large-guide mesh Stage A (0.2.14–0.2.16):** exposed-face meshing (emit only faces with no neighbour),
    made exposed-only then solidity-aware so it never seams between voxels; deterministic mesh-count harness.
  - **Filled 3D volumes RETIRED (0.2.17):** volumes are always hollow shells now (GUI grey + server
    normalise + per-shape coercion; legacy filled saves auto-lighten).
  - **HUD/GUI (0.2.18):** hover no longer re-measures every tick; number fields aligned to the tile grid.
  - **Draft cap clamp restored (0.2.19);** **fifth High fill state (0.2.20);** **refill channels — config +
    inventory-slot refill (0.2.21, protocol 4 → 5):** hotbar/inventory refill were server-config opt-in
    (ground storage always allowed), synced to clients; inventory refill via a MouseDown hook + validated
    `ChalkInventoryRefillPacket`. *(The two flags became CLIENT preferences in v0.2.22 — see Session 17.)*
  - **Verified in play (human-confirmed):** the v0.2.21 inventory and hotbar refills both work.
- **Session 17 — the seven-item human backlog (v0.2.22–v0.2.23): DELIVERED. Detail in `SESSION_17.md`.**
  - **Refill channels → CLIENT preference (0.2.22, protocol 5 → 6):** both flags moved to
    `layout-client.json`. The server is *told* the player's choice via the new `ChalkRefillPrefsPacket`,
    because `ItemChalkingPowder`'s held-interact runs on both sides and the SERVER mutates the stacks —
    a permissive server would have made the hotbar toggle a silent no-op.
  - **Hard 32-chalk ceiling (0.2.23):** `ItemGuideTool.MaxChalk`; chalk read straight off the attribute and
    clamped; `GetMaxDurability`/`GetRemainingDurability` overridden *without* base, since base walks the
    collectible BEHAVIORS that xskills uses to grant crafting-quality durability. 21/21 offline harness.
  - **Hotbar refill-off warning removed (0.2.22);** **draft packet-rate AUDITED** — already ≤10 Hz and
    movement-gated, drafting sends ~2 packets total, no change needed.
  - **Publication readiness:** authorship → Doden; targets all VS 1.22.x; `Layout.csproj` auto-resolves the
    install; zero tracked files carry a personal path or username.
  - **⚠️ Verification debt:** the chalk ceiling is untested against xskills itself, and 1.22.0 support is
    declared but never run.
- **Session 18 — Tapered Cylinder (v0.2.24–v0.2.28): DELIVERED.** Four-click frustum; protocol 7 rim field;
  corrected scan accounting, persistent cap clamp, stable free-air rim aiming, and fired-only jug recipes.
- **Session 19 — stabilization/effects (v0.2.29–v0.2.35): DELIVERED and playtest-confirmed.** Unlimited-cap
  tapered scan semantics; release-and-annulus rim capture; adaptive 33→2 Hz draft work; zero-gravity dust
  across 2D curves and full 3D shells; public/private release check passed.
- **Session 20 — polygonal volumes/modifiers (v0.2.36–v0.2.47): DELIVERED.** B-S9-1 closed; Chalking Kit
  set-down is SHIFT+right-click; private cleanup is `.layout dispel`; Polygonal Prism and Tapered Polygonal
  Prism added; native stage-aware modifier notes; flat-side polygon alignment; 45-degree Line/Free-Shape
  diagonals; default-safe tapered rims with SHIFT flare; simplified Create header. DataVersion/protocol 9.
- **Session 21 — adaptive large guides/structural form (v0.3.0–v0.3.8): DELIVERED and playtested.** Cheap
  drafts preserve selected-scale shells; expensive motion uses adaptive wireframes with a precise cursor
  neighbourhood; exact shells refine off-thread and materialize in batches. Placed hover is cached, giant
  grab cancel retains/restores the settled mesh safely, wire topology gained shape-appropriate ribs, and 3D
  guides can persist as Shell or Wireframe. Size-weighted placement sound, dedicated form icons, and
  surface-proportional oversized Cylinder/Cone/Box fallback shipped. DataVersion/protocol 11.
- **Session 22 — action HUD/attribution/sculpting parity (v0.3.9–v0.3.21): DELIVERED and playtested.**
  Fixed-footprint action-aware HUD and contextual tile; Creator/Last Sculptor with `/layout who`; Edit
  right-click deselect and selected-setting display; selected-scale settled materialization while sculpting;
  tapered-rim flare parity; active Surface↔Volumetric anchor translation; final GUI/HUD typography cleanup.
  DataVersion 12; protocol 13.
- **Session 23 — moderation/claims/immense-guide execution (v0.3.22–v0.3.34): DELIVERED; field test next.**
  Persistent jail/cap policy and moderation; claim-authoritative public geometry; bounded single-lane
  immense server validation; cancellable progressive shape scans; retained placement/sculpt scaffolds;
  exact organic multi-seed materialization; frame-spread mesh retirement; live view-distance culling; and
  personal `/layout off|on` rendering control. DataVersion remains 12; protocol 16.
- **Session 24 — materialization completion and intrinsic dimensions (v0.3.35–v0.3.40): DELIVERED; field test next.**
  Base-plus-proportional nucleation, size-aware upload cadence, a full-growth 200 ms hold, and a separately
  generated uniform final shell complete the organic reveal. Scaffolds vanish at the first growth batch;
  placement effects wait for both final authority and the clean-shell swap. Wireframe→Shell Edit changes use
  the same cancellable materialization chain, below-threshold sculpt releases no longer leave the immense
  operation latch set, polygonal prism scans reject safe non-wall regions early, and round/polygonal volume
  HUD dimensions now use intrinsic width/height rather than a world-AABB diagonal. DataVersion remains 12;
  protocol remains 16.
- **Session 25 — measured guide culling and spatial settled meshes (v0.3.41–v0.3.43): HISTORICAL; renderer conclusion superseded.**
  Whole-guide camera-frustum rejection, `.layout renderstats`, and 32-block independently culled clean final
  meshes shipped without persistence or protocol changes. Cross-region neighbour checks retain the complete
  guide occupancy, and immense saved Shells reload through a background scaffold/materialization path.
  Human-measured partial view fell from 128 to 33 drawn batches, 32.6M to 7.5M triangles, and 7.5 to 4.4 ms,
  with 110 regional batches culled. DataVersion remains 12; protocol remains 16.
- **Session 26 — meshing experiments, rollback, persistence, and budgets (v0.3.44–v0.3.52): DELIVERED and playtested.**
  Greedy/depth experiments were rejected after seams, view-dependent fidelity problems, delayed clean swaps,
  and an approximately 140→80 FPS real-play regression. v0.3.49 restored the v0.3.42 renderer. v0.3.50
  persists personal rendering state, raises the per-guide default to 500,000, adds a 1,000,000 cumulative
  original-creator cap, and makes the default world total unlimited with narrow config migration. v0.3.51
  adds the saved-off login reminder; v0.3.52 blocks Chalking Kit guide actions/settings/undo/redo while off
  and explains recovery in warnings and the HUD.
- **Session 27 — cumulative-cap override and final release (v0.3.53): DELIVERED and verified.**
  `/layout totalvoxelcap <player> <number>` persistently overrides one original creator's cumulative
  public-guide allowance; `0` restores the server default. Every ordinary/immense/restore path resolves the
  effective creator cap, `/layout info` exposes usage/effective/override state, and the additive admin-policy
  format advances to version 2 without changing DataVersion 12 or protocol 16.
- **IN REAL PLAY:** save-compatibility matters — DataVersion **12** saves (v12: attribution; v11: `IsWireframe`; v10: cached
  display/count/dimensions; v9: polygon
  `FlatSideAligned`; v8: passive `IsLockMarker`; v7:
  IsClosed; v6: Sides + the
  never-wired spring-back snapshot); pinned-enum / additive-protobuf / default-migration rules remain in
  force. Session 11's and the 3D family's wire additions are all additive and registered append-only.
- **CLOSED:** **B-S9-1 lock-in-place.** v0.2.36 assigns a point only its nearest visible rendered marker
  cell; the adjacent first-hit body voxel receives its own passive marker. Human-confirmed in play.

---

## 2. Coding order & effort levels

| # | Module | Effort | Status |
|---|--------|--------|--------|
| 1 | Pure math layer | **Max** | COMPLETE (+ 2D catalog; + 3D volumes including tapered cylinder and straight/tapered polygonal prisms; + exact hollow Sphere/Dome scanner) |
| 2 | Data model | Low | COMPLETE (DataVersion **12**: attribution + persistent form + cached metadata; v9 polygon orientation; earlier passive markers/Sides/snapshot/IsClosed) |
| 3 | Systems + undo | High | COMPLETE (+ public/private `SetWireframe`; canonical form counts; adaptive draft state; cancel quarantine) |
| 4 | Networking | **Max** | COMPLETE (protocol **16**; policy sync, async placement rejection, and personal rendering state appended after attribution/who) |
| 5 | Rendering | High | FUNCTIONAL: exposed faces + adaptive wireframes + retained final-click/sculpt handoffs + streamed exact organic growth + whole-guide frustum rejection; v0.3.43 spatial meshes and later greedy experiments were reverted in v0.3.49 |
| 6 | UI | Medium | COMPLETE (21-tile catalog, favorites fold-out, stage-aware help, fixed action HUD, contextual shape/setting display, calculating dots) |
| 7 | Integration | High | COMPLETE (+ CTRL/SHIFT stage modifiers, 2/3/4-click routing; + F4 authority, private persistence/commands, mixed-mode HUD) |

Namespaces match folders: `Layout`, `Layout.Guide`, `Layout.Shapes`, `Layout.Systems`, `Layout.Network`,
`Layout.Undo`, `Layout.Undo.Commands`, `Layout.UI`, `Layout.Config`, `Layout.Items`, `Layout.Client`.

---

## 3. Session 9 — what was built

> Full detail in `SESSION_9.md`.

**Soft-point flow -> slave-regime (the long thread).** After Session-8's proportional flow, the apex and
prior-grabbed points *still* behaved like pins. Iterated: (1) flow fires on **every** drag with the **held
point in the baseline** (+ the insert-adoption path captures flow — the canonical "insert between apex and
anchor" case); (2) **curved baseline** (centripetal CR through anchors+locked+held) instead of chords;
(3) **slave-regime** — the settled model: **interior grab** slaves every unlocked point onto the curve at its
station with **zero offset** (apex contributes no pull; it *settles onto* the hand-defined curve), while
**structural grab** keeps shape-preserving proportional flow. Plus a **chord-invariant phantom drop**
(0.4xchord, replacing neighbor-height reflection) to stop near-foot inserts from re-tilting the whole arch.

**New shapes (F1 first wave).** Line (2 anchors, no fill); Triangle (base anchors + born apex; Right /
Equilateral / Isosceles as apex-derivation constraints; Equilateral breaks to free on apex drag); Rectangle
(diagonal corners stored, other two derived; Square constraint). New shared `ShapeGeometry` helper;
`GuideShapeType` + `ShapeConstraint` extended; **DataVersion 5**; body-click policy generalized from
"ellipse" to "any non-arch parametric shape" (nearest-handle grab/lock; only arch takes body inserts);
GUI shape picker is now an **11-tile grid** (four per row). *(verified.)*

**Divisions (new feature).** Per-guide visual equal-parts overlay: recolors voxels at N arc-length boundaries
(**magenta**, `VoxelRenderType.Division`), computed **renderer-side** (`DivisionMarks.Apply`) as a pure
recolor — geometry / counts / caps untouched. `GuideData.Divisions`; additive DTO fields;
`GuideSetDivisionsPacket`; `SetDivisionsCommand`; `GuideManager.SetDivisions` (clamp + persist, no cap
check); `MaxDivisions = 256`; GUI dropdown+field in both tool and per-guide sections; `DefaultDivisions`
config. (The build-hiccup note about the packet class once being omitted is now moot: **verified present in
`PacketTypes.cs` and registered last in `RegistrationOrder()`.**)

---

## 4. Implementation decisions log (Session-9 additions)

**Everything in the Modules-1-7 and Session-8 checkpoints still holds** except where superseded. Session-9:

- **Slave-regime soft flow (supersedes S8 proportional-only).** Interior grabs -> unlocked points slaved onto
  the curve, zero offset (the apex is genuinely ignored). Structural grabs -> shape-preserving proportional
  flow (unchanged). This is the settled answer to "the apex still acts like a lock."
- **Chord-invariant phantom drop.** Arch end-tangent phantoms derive from the anchor chord (0.4xchord), not
  the neighbor knot's height — so interior inserts/locks no longer re-tilt the whole curve. Fresh arch is
  pixel-identical.
- **Constraints as apex/corner derivation rules, extended to polygons.** Triangle Right/Equilateral/Isosceles
  and Rectangle Square follow the ellipse-Circle pattern (absorb by sliding/resizing; Equilateral additionally
  breaks-to-free on apex drag). Right/Isosceles/Square have **no break gesture** in v1.
- **Body-click policy is now shape-family-general:** only the **arch (free spline) family** takes body
  inserts; every other parametric shape maps body clicks to the **nearest handle** (grab / lock).
- **Divisions are a pure render-side recolor** — never touch geometry, counts, or caps. Chosen so the feature
  couldn't destabilize the cap/undo/wire machinery.
- **`ShapeGeometry` joins `ShapeFactory` / `VoxelMarch`** as shared shape-layer infrastructure (planar frame +
  marker-claim, extracted from the ellipse).

---

## 5. Tuning backlog

Full annotated list in `TODO.md`. Headlines: v0.3.53 final-release multiplayer/persistence field soak; the
xskills and oldest-1.22.x verification debts; filled 2D recount per drag; division-mark recolor; carried
Session-7 cosmetic items. The cancellable streamed immense pipeline and selected-scale Shell/Wireframe
semantics are delivered.

---

## 6. Open questions / decisions to settle

**Settled — do not reopen:** everything in `ARCHITECTURE.md`'s Settled Decisions Register, plus the Session-9
**slave-regime flow** (playtest-confirmed "MUCH better") and the **chord-invariant phantom drop**.

**Closed interaction follow-up:** **B-S9-1 lock-in-place.** v0.2.36 made the first rendered voxel hit
authoritative and limited a point to its nearest visible marker cell. The formerly locked cell can no longer
claim its immediate neighbor, and the human approved the result in play. See `SESSION_20.md`.

**Flagged for review (cheap to reverse):** Session-10 adds Edit-mode select-only, divisions hover-scroll,
the scale-icon/tile-proportion calls; Session-9 adds the regime split, triangle's 2-click+born-apex gesture,
no-break-gesture for Right/Isosceles/Square, rectangle corners as markers, magenta division color, the
per-keystroke divisions field; Session-8's list still stands. Full list + rationale in `TODO.md`.

**Open — real-play agenda:** field-soak saved `/layout off|on` state across reconnect/restart, cumulative
creator budgets during multiplayer mutation/undo/delete, and the off-state Chalking Kit lockout. Preserve the
v0.3.42 renderer baseline restored in v0.3.49; spatial/greedy work is not a queued follow-up. The
v0.2.35 public/private multiplayer release check passed, so broader regression coverage can follow field
reports rather than block release.

---

## 7. Next session — start here

**The current checkpoint is v0.3.53 final release built, packaged, and documented.**
Read `SESSION_27.md` first for the final capacity-policy state, then `SESSION_26.md`, `SESSION_25.md`, `SESSION_24.md`,
`SESSION_23.md`, `SESSION_21.md`, and
`SESSION_14.md` for
the historical adaptive path and optional mesh plan.

1. **Field-soak v0.3.53.** Exercise saved rendering state in public/private/fallback modes,
   `/layout totalvoxelcap` through create/edit/delete/undo, and every blocked Chalking Kit input while off.
2. **Performance only from new evidence.** The v0.3.43–v0.3.48 spatial/greedy path was rejected. Preserve
   the v0.3.42 renderer's appearance and compare real FPS, not only mesh counters, in any future proposal.
3. **Preserve the settled invariants.** Input and server ticks remain responsive; final-click scaffolds do
   not disappear; organic batches preserve exact occupancy; small guides stay immediate; persistent wires
   and settled shells remain true selected scale.
4. **If asked:** Roof / Tunnel volumes; concave-safe Free-Shape fill; broadcasting the whole Free-Shape draft
   chain (11q); the F3 re-constrain op. Remaining flagged decisions (11a–11r, 16a–16d) are cosmetic.

---

## 8. Workflow notes

The working conventions are in `CLAUDE.md` ("How to work on this project"). In short: the human is not a
programmer and validates by **playing** the mod and describing feel in plain English — the biggest wins
("the apex acts like a pin", "the voxel I target isn't the one that locks") came that way, so keep flagging
anything that *sounds* wrong. **Decide-and-flag** on ambiguity; playtest arbitrates. Work on `main` unless the
human explicitly requests a feature branch. Ship a zip per code
iteration; update docs / commit only on the human's say-so.
