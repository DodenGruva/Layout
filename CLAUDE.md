# CLAUDE.md — Layout (Vintage Story mod)

> This file is read automatically by Claude Code at the start of every session. Keep it short and factual —
> it is a briefing, not documentation. The longer docs are referenced below; Claude Code reads them on demand.

## What this project is
Layout is a mod for **Vintage Story 1.22.x** (C# / **.NET 10**). It's a CAD-like, voxel-resolution
construction-planning tool: players place translucent geometric guide overlays in the world and build against
them by hand. The mod is visual-only — it never places, removes, or modifies blocks. Normal guides are
server-authoritative/world-shared; the F4 client-only mode also supports private client-authoritative guides
on servers without Layout and, when permitted, alongside public guides. The tool is the **Chalking Kit**
(custom deflating 5-state model) with **finite, powder-refillable chalk durability** (F5). Large guides use
**adaptive motion wireframes, background selected-scale refinement, and bounded materialization**
(SESSION_21), on top of exposed-face meshing (SESSION_16, Stage A). 3D volumes can persist as either a
hollow **Shell** or a structural **Wireframe**.
The catalog is **15 shape types / 21 picker tiles** (SESSION_20 added straight/tapered Polygonal Prisms).
Guide voxels that already hold world material can be drawn in a "built" colour, live (SESSION_29).
Status: **v0.3.85 on the `beta-shader` branch, uncommitted** (v0.3.58 was the last `beta` build; v0.3.53
the last `main` release; the mod is public).

> **Renderer note — read before any rendering work.** Guides are **order-dependent translucent geometry**
> (Opaque stage, manual alpha blending, depth-tested, double-sided). On a hollow shell the guide overlaps
> itself at nearly every pixel, so whichever mesh batch draws first wins and writes depth. The accepted
> appearance is therefore partly a by-product of voxel emission order, and **any change that regroups
> primitives changes the picture** — this is why the Session 25–26 experiments failed. v0.3.57 vertex
> welding was safe precisely because it regroups nothing. See `SESSION_28.md` and
> `PLAN_RENDER_PERFORMANCE.md`; note that `SESSION_26.md` carries a correction banner.

## Build & run
- **Build:** `dotnet build` from the repo root (the folder with `Layout.csproj`).
- **Target:** .NET 10, Vintage Story 1.22.x (modinfo declares a 1.22.0 minimum).
- **Dependencies** (all ship with the game): `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll`,
  `cairo-sharp.dll` (GUI icon glyphs), and `VSSurvivalMod.dll` (in the game's `Mods/`; `IContainedMeshSource`
  for ground-storage fill meshes, since Session 15). **`Layout.csproj` finds the install itself** —
  `-p:VintagestoryDir=…` → the `VINTAGE_STORY` env var → the platform default — so no path needs editing;
  a bad path fails with one clear message naming the folder tried. **Never hard-code a path there: the repo
  is published.**
- **Runtime note:** `Entity.SidedPos` is obsolete in this API version — use `Pos`.
- **Package a runnable mod:** build **Release**, then zip `modinfo.json` + `modicon.png` + `assets/` +
  `Layout.dll` at the **zip root** (forward-slash entry paths), drop into `VintagestoryData/Mods`. Config
  files (`layout.json`, `layout-client.json`) appear in `ModConfig` after first run.
- **Versioning rule (standing, human-set):** EVERY revision bumps `modinfo.json` and ships as a NEW
  `Layout<version>.zip` in **`..\LayoutZips\`** (the sibling folder of this repo — human-directed location;
  holds 0.1.10–0.1.27 + the 0.2.x line; the 0.1.28–0.1.53 zips live in `Documents\ChatGPT\LayoutZips\`) —
  never overwrite an older release zip. Versions increment monotonically per revision. **Current: v0.3.85.**
  (The folder's real name has a space: `..\Layout Zips\`.)
- **Git:** `main` is the mainline, published at **github.com/DodenGruva/Layout** (the
  `ClientOnlyFallback` branch was merged via PR #1). **The repo is published at release** — no personal
  paths, no personal usernames in tracked files. Commit/push ONLY when the human instructs.

## The documents (read these before large work)
- **`HANDOFF.md`** (repo root) — the consolidated current-state brief (scope · status · direction ·
  performance characteristics), written for external analysis; the fastest way to get oriented.
- **`ARCHITECTURE.md`** (in `dev/`, v3.14) — the authoritative plan. Its **Settled Decisions Register** lists
  locked-in design choices; **do not reopen those without the human explicitly asking.** Its per-revision
  deltas live in **`CHANGELOG_ARCHITECTURE.md`** (an archive — rarely needed).
- **`TODO.md`** — the live punch-list: open bug, deferred requests, flagged decisions, future features.
- **`PLAN_RENDER_PERFORMANCE.md`** — the Session-28 rendering plan, with every measurement taken. Read
  before any renderer work; it also records why the Session 25–26 conclusions were wrong.
- **`PLAN_BLOCK_OCCUPANCY.md`** — **✅ DELIVERED (v0.3.70–v0.3.85, SESSION_29).** Fully redrafted on
  2026-07-26 after playtesting disproved three conclusions in the first draft. **Read its §0 before touching
  anything here** — it records what was tried and why the design looks as it does. Retained because the
  feature has open verification items and one escape hatch (the shader-lookup route) still on the table.
- **`PROJECT_STATUS.md`** — where things stand and what each module does.
- **`PLAN_CLIENT_ONLY.md`** — F4's finalized implementation record and behavior matrix (candidate for 0.2.0).
- **`PLAN_CHALKING_KIT.md`** — the F5 chalking-kit design rationale, now marked ✅ implemented with its
  plan-vs-shipped deltas up top.
- **`SESSION_9.md` … `SESSION_27.md`** — standalone per-session records; SESSION_12 covers **v0.1.28–v0.1.45
  ClientOnlyFallback**, SESSION_13 the **v0.1.46–v0.1.52 optimization and interaction pass**, SESSION_14 the
  **v0.1.53 hollow-shell + mesh optimization handoff** (read before continuing performance work), SESSION_15
  the **v0.2.0–v0.2.9 Chalking Kit arc**, SESSION_16 the **v0.2.10–v0.2.21 mesh + polish arc** (Stage-A
  exposed-face meshing, z-fight insets, filled-volume retirement, High fill state — read before continuing
  performance work), and SESSION_17 the **v0.2.22–v0.2.23 seven-item backlog** (refill config → client
  preference at protocol 6, the hard 32-chalk ceiling, publication readiness), and SESSION_18 the
  **v0.2.24–v0.2.28 Tapered Cylinder arc** (the 4-click frustum at protocol 7; the scan-guard and
  cap-clamp performance fixes it exposed), SESSION_19 the **v0.2.29–v0.2.35 stabilization/effects arc**
  (unlimited-cap semantics, safe rim capture, adaptive draft throttling, and whole-shape dust), and
  SESSION_20 the **v0.2.36–v0.2.47 polygonal-volume/modifier arc** (precise adjacent locks, polygonal prisms,
  stage-aware help, flat-side/diagonal/rim modifiers, and GUI cleanup), SESSION_21 the **v0.3.0–v0.3.8
  adaptive large-guide arc** (motion wireframes, background refinement/materialization, cached hover metadata,
  safe giant-grab cancellation, persistent Shell/Wireframe mode, and surface-only oversized fallbacks), and
  SESSION_22 the **v0.3.9–v0.3.21 action-aware HUD, attribution, sculpting-parity, and projection-transition
  arc**, SESSION_23 the **v0.3.22–v0.3.34 moderation, claims, and streamed immense-guide arc**, and
  SESSION_24 the **v0.3.35–v0.3.40 materialization-completion, shell-transition, and HUD-dimension arc**, and
  SESSION_25 the **v0.3.41–v0.3.43 measured frustum/spatial-culling experiment**, and SESSION_26 the
  **v0.3.44–v0.3.52 meshing experiments, renderer rollback, persistent visibility, cumulative creator cap,
  and off-state Chalking Kit lockout**, and SESSION_27 the **v0.3.53 per-player cumulative-cap override and
  final release**, and SESSION_28 the **v0.3.55–v0.3.69 rendering arc** (vertex welding, settled-shell
  streaming, the custom guide shader, voxel outlines), and SESSION_29 the **v0.3.70–v0.3.85 block-occupancy
  arc** (the face outset, the settings page, sub-block world reads, the built-voxel colour, and its live
  per-batch updates).

## ✅ Docs updated to v0.3.85 (2026-07-26)
Consistent with **v0.3.85, DataVersion 12, protocol 16, 78 source files, 15 shape types / 21 tiles**.
`SESSION_29.md` is the latest record (occupancy: `SESSION_29.md` + `PLAN_BLOCK_OCCUPANCY.md`, mesh:
`SESSION_16.md` + `SESSION_28.md`, F5: `SESSION_15.md`).

⚠️ **`ARCHITECTURE.md` (v3.14), `PROJECT_STATUS.md` and `HANDOFF.md` predate Sessions 28–29.** They are not
wrong about what they describe, but they do not know about vertex welding, settled-shell streaming, the
custom shader, or block occupancy. The session records and plans are authoritative for those. Prefer source
for exact identifiers.

⚠️ **`SESSION_26.md` carries a correction banner** — three of its claims were disproved in Session 28,
including the 140→80 FPS regression that closed the rendering arc. Do not plan renderer work from it alone.

⚠️ **The first draft of `PLAN_BLOCK_OCCUPANCY.md` was substantially wrong** and was replaced, not amended.
Its §0 records the disproved conclusions so they are not re-derived.

## How to work on this project (the human's established workflow)
- **The human is not a programmer** and does not read code. They validate by *playing the mod* and describing
  what feels wrong in plain English. Explain changes in plain language, not code walkthroughs.
- **Decide-and-flag:** when a design choice is ambiguous, make a reasonable call, implement it, and clearly
  note it for the human's review rather than blocking. Favor what feels natural in-game.
- **Correctness over performance** unless the human says otherwise.
- **Playtest beats compile-checks:** the important bugs here are runtime/feel issues a compiler can't catch.
  After changes, still run `dotnet build` to catch compile errors before the human playtests.
- Give a **short summary of the request before doing significant work**, and ask if anything is unclear.
- **DOCS ONLY ON REQUEST (standing, human-set, Session 11 — token discipline):** per code iteration do
  code → build → version bump → new zip, and NOTHING else. Do **not** update TODO/ARCHITECTURE/
  PROJECT_STATUS/SESSION files or memory until the human explicitly says to (e.g. "update the documents" /
  "finalize the session"). The playtest loop iterates fast; per-iteration doc churn wastes tokens.
  **Exception:** if the conversation is close to a context trim while docs are stale, WARN the human first
  so nothing is lost to the trim un-recorded.
- **Commit only when the human instructs.** `main` is the mainline; never push without direction.

## Tool modes (Session 10): Create · Edit · Delete
**Create** owns all geometry (place, grab/reshape, insert, lock; right-click = cancel/lock). **Edit** is
settings-only: left-click **selects** a guide and the GUI's setting rows then act on THAT guide (no
reshaping — geometry stays in Create); this replaced the old panel-expanding "selected-guide section".
**Delete** dispels. `ToolMode` is client-only (never wired), so it's safe to reorder.

## Session-28 additions (v0.3.55–v0.3.69)
**v0.3.55–v0.3.58 are on `beta`; v0.3.59–v0.3.69 are on `beta-shader`, not yet merged.**
- **Custom guide shader** (`assets/layout/shaders/guide.vsh`/`.fsh`, v0.3.60): guides no longer use
  `PreparedStandardShader`. **8M-voxel guide: 8.2 ms → 1.8 ms across the session, 78% removed.** Guides are
  now self-lit — no shadow darkening, no ambient tint — approximated back by `shaderGuideBrightness` /
  `shaderAmbientResponse`. `/layout shader off` restores the old look and cost.
  **Shader sources must be pure ASCII**; packaging refuses anything else.
- **Voxel outlines** (v0.3.62–v0.3.63): per-cell boundaries drawn procedurally in the fragment shader, no
  extra geometry. `/layout voxelframe`. The mesh's voxel scale is recorded at upload so every path gets it.
- **Sphere/dome wireframes**: 4 sectors → 8 (v0.3.64).
- **Fixes** (v0.3.65–v0.3.69): inset now reaches partial blocks, not just whole-block planes; outlines no
  longer vanish on inset layers; the cap clamp no longer builds an invisible wall from one aim direction;
  volumetric guides re-probe after terrain loads. `/layout inset` tunes the anti-z-fight gap.

## Session-29 additions (v0.3.70–v0.3.85, `beta-shader`) — full detail in `SESSION_29.md`
- **Block-occupancy recolour:** guide body voxels holding world material are drawn **cyan**, updating live
  as you build. Off by default; toggle on the GUI settings page or `/layout built on|off|refresh`.
  Client-side only — no protocol, save, or DataVersion change, and other players are unaffected.
- **`BlockOccupancy`** (`src/Systems/`) reads sub-block material at 1/16 from collision boxes: one state per
  block, a 4096-bit brick only for partial ones. **Unloaded chunks read as EMPTY** (deliberate).
- **Live updates** via `IClientEventAPI.BlockChanged` (confirmed in play to fire for chisel edits), filtered
  by player radius → guide cull sphere → 80 ms settle, then a **per-batch rebuild**: only the batches
  overlapping the changed block are re-meshed, on a worker, uploaded incrementally.
- **Face offset is now an OUTSET** (v0.3.70), derived from the guide's own voxel set and consulting the
  world not at all. The solidity probe and `_deferredSolidity` are gone. `ZFightInset` default is now
  **0.0006**, five times smaller than the old inset — playtest-settled.
- **GUI settings page** behind a gear in the title bar: guide-opacity slider, built-voxel switch, re-read.
- **Commands:** `/layout built`, `/layout occupancy`, `/layout occupancyscan <r>`, `/layout blockevents`.
- ⚠️ **Optional command arguments:** `parsers.OptionalFloat/OptionalInt` return their DEFAULT when absent,
  not null. Three shipped commands were silently broken by this. Optional floats here default to `NaN`; use
  `Supplied()` in `LayoutModSystem` rather than an `is float` test.

## Earlier Session-28 additions (v0.3.55–v0.3.58, `beta`)
- **Vertex welding** (`GuideMeshOptions.WeldVertices`, v0.3.57): guide meshes share vertices between faces
  meeting at one position with one colour. **Deduplication, not merging** — the triangle stream is provably
  identical (10/10 harness comparing both index buffers in submission order). −72.9% vertices, −58.4% mesh
  data, −41.5% frame cost on an 8M-voxel guide; human A/B found it indistinguishable. Running at ~1.0
  vertices per quad, the theoretical floor, so **this lever is spent**.
- **Settled-shell streaming** (`SettledStreamingVoxelThreshold`, v0.3.58): guides over 100,000 voxels
  scaffold and stream instead of building synchronously. Fixes a multi-second client hang on world load and
  makes large guides materialize for *other* players, not just the placer. **Not yet playtested.**
- **Commands:** `.layout renderstats` now reports vertices/indices/bytes; `/layout weld on|off` is a
  diagnostic A/B switch.

## Current priorities (detail in TODO.md; final-release record in SESSION_27.md)
F4 (client-only/private guides) and F5 (Chalking Kit durability) are both feature-complete. Public/private
multiplayer was release-tested successfully at **v0.2.35**, the fired-jug/raw-jug recipe behavior is
playtest-confirmed, and B-S9-1 adjacent-lock targeting was closed and playtest-approved in **v0.2.36**.
Mesh **Stage A** shipped and filled 3D volumes were retired. Normal public multiplayer behavior remains.
1. **Two verification debts from the backlog** — (a) the hard 32-chalk ceiling is verified offline but
   **never tested against xskills itself**; craft a quality-bonus kit and confirm it comes out 32/32.
   (b) **1.22.x support is declared, not tested** — the code was built against 1.22.3; smoke-test a
   1.22.0/1.22.1 install. See `SESSION_17.md` §8.
2. **Protect the v0.3.42 renderer baseline restored in v0.3.49.** Whole-guide distance/frustum culling and
   render statistics remain; spatial final meshes and greedy merging were rejected after real-play seams,
   fidelity defects, and an approximately 140→80 FPS regression. See `SESSION_26.md`.
3. **Field-soak the v0.3.53 final release.** Verify saved `/layout off|on` state across reconnects,
   cumulative creator budgets and `/layout totalvoxelcap` during multiplayer editing/undo, and the off-state
   Chalking Kit lockout.
4. **Performance follow-up only from a new measured bottleneck and a fidelity-preserving design.** Immense
   placement/sculpt work still uses one below-normal worker plus bounded claim ticks; do not assume spatial
   subdivision or greedy merging is the next step.
5. If asked: **Roof / Tunnel** volumes; concave-safe Free-Shape fill; F3 re-constrain op. Remaining
   flagged decisions are cosmetic.

## Project layout (namespaces match folders)
`src/` contains: `Guide/` (data types), `Shapes/` (pure geometry math), `Systems/` (managers + undo),
`Network/` (packets + handlers), `UI/` (GUI + HUD + `LayoutToolIcons.cs`, the Cairo icon glyphs), `Config/`,
`Items/`, `Client/` (the tool controller), `Undo/Commands/`. Adding a new shape starts in
`Shapes/ShapeFactory.cs`. Soft-point flow behavior lives in `Shapes/SoftPointFlow.cs`. Chalk durability
lives in `Items/ItemGuideTool.cs` (helpers + fill-state rendering) + `Items/ItemChalkingPowder.cs` (refill)
+ `Systems/ChalkEffects.cs` (puffs/snap). Large-guide work is centred in `Systems/GuideRenderer.cs`,
`Systems/DraftPreviewSpec.cs`, `Shapes/ShapeWireframe.cs`, and `Shapes/LargeVolumeShellFallback.cs`.
Sub-block world material lives in `Systems/BlockOccupancy.cs`.
(Filenames verified against the tree on 2026-07-26 — 78 source files.)
