# CLAUDE.md — Layout (Vintage Story mod)

> This file is read automatically by Claude Code at the start of every session. Keep it short and factual —
> it is a briefing, not documentation. The longer docs are referenced below; Claude Code reads them on demand.

## What this project is
Layout is a mod for **Vintage Story 1.22.x** (C# / **.NET 10**). It's a CAD-like, voxel-resolution
construction-planning tool: players place translucent geometric guide overlays in the world and build against
them by hand. The mod is visual-only — it never places, removes, or modifies blocks. Normal guides are
server-authoritative/world-shared; the F4 client-only mode also supports private client-authoritative guides
on servers without Layout and, when permitted, alongside public guides. The tool is the **Chalking Kit**
(custom deflating 5-state model) with **finite, powder-refillable chalk durability** (F5). Large guides now
render with **exposed-face meshing** (SESSION_16, Stage A) and 3D volumes are always hollow shells.
Status: **v0.2.x, in real play.**

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
  `Layout<version>.zip` in **`..\Layout Zips\`** (the sibling folder of this repo — human-directed location;
  holds 0.1.10–0.1.27 + the 0.2.x line; the 0.1.28–0.1.53 zips live in `Documents\ChatGPT\LayoutZips\`) —
  never overwrite an older release zip. Versions increment monotonically per revision. **Current: v0.2.23.**
- **Git:** `main` is the mainline, pushed to **github.com/DodenGruva/Layout** through **v0.2.23** (the
  `ClientOnlyFallback` branch was merged via PR #1). **The repo is published at release** — no personal
  paths, no personal usernames in tracked files. Commit/push ONLY when the human instructs.

## The documents (read these before large work)
- **`HANDOFF.md`** (repo root) — the consolidated current-state brief (scope · status · direction ·
  performance characteristics), written for external analysis; the fastest way to get oriented.
- **`ARCHITECTURE.md`** (in `dev/`, v3.4) — the authoritative plan. Its **Settled Decisions Register** lists
  locked-in design choices; **do not reopen those without the human explicitly asking.** Its per-revision
  deltas live in **`CHANGELOG_ARCHITECTURE.md`** (an archive — rarely needed).
- **`TODO.md`** — the live punch-list: open bug, deferred requests, flagged decisions, future features.
- **`PROJECT_STATUS.md`** — where things stand and what each module does.
- **`PLAN_CLIENT_ONLY.md`** — F4's finalized implementation record and behavior matrix (candidate for 0.2.0).
- **`PLAN_CHALKING_KIT.md`** — the F5 chalking-kit design rationale, now marked ✅ implemented with its
  plan-vs-shipped deltas up top.
- **`SESSION_9.md` … `SESSION_17.md`** — standalone per-session records; SESSION_12 covers **v0.1.28–v0.1.45
  ClientOnlyFallback**, SESSION_13 the **v0.1.46–v0.1.52 optimization and interaction pass**, SESSION_14 the
  **v0.1.53 hollow-shell + mesh optimization handoff** (read before continuing performance work), SESSION_15
  the **v0.2.0–v0.2.9 Chalking Kit arc**, SESSION_16 the **v0.2.10–v0.2.21 mesh + polish arc** (Stage-A
  exposed-face meshing, z-fight insets, filled-volume retirement, High fill state — read before continuing
  performance work), and SESSION_17 the **v0.2.22–v0.2.23 seven-item backlog** (refill config → client
  preference at protocol 6, the hard 32-chalk ceiling, publication readiness).

## ✅ Docs verified & consolidated to v0.2.23 (2026-07-19)
The authoritative prose docs are consistent with **v0.2.23, DataVersion 8, protocol 6, 68 source files,
12 shape types / 18 tiles**. `ARCHITECTURE.md` is **v3.4**; `SESSION_17.md` is the latest record
(mesh: `SESSION_16.md`, F5: `SESSION_15.md`); `HANDOFF.md` is the consolidated brief. Prefer source for exact
identifiers, but no from-scratch doc audit is needed before ordinary work.

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

## Current priorities (detail in TODO.md; mesh plan in SESSION_14.md §6–§7, latest record in SESSION_17.md)
F4 (client-only/private guides) and F5 (Chalking Kit durability) are both feature-complete and playtested
through **v0.2.23**. Mesh **Stage A** shipped and filled 3D volumes were retired, so worst-case voxel counts
are far lower now. The Session-16 backlog is **fully delivered**. Normal public multiplayer behavior remains.
1. **Two verification debts from the backlog** — (a) the hard 32-chalk ceiling is verified offline but
   **never tested against xskills itself**; craft a quality-bonus kit and confirm it comes out 32/32.
   (b) **1.22.x support is declared, not tested** — the code was built against 1.22.3; smoke-test a
   1.22.0/1.22.1 install. See `SESSION_17.md` §8.
2. **Large-guide mesh Stage B** (per-guide spatial chunk meshes + culling), then **Stage C** (greedy
   same-colour face merging) — only if Stage A's win isn't enough on the ~100-block sphere. Staged plan +
   invariants in `SESSION_14.md`.
3. **Finish the B-S9-1 interaction regression.** Lock placement no longer shifts the guide (v0.1.51/52), but
   repeated lock/drag/revert/unlock cycles need more playtesting before the bug is closed.
4. **The final F4/public multiplayer regression pass** (vanilla-server fallback, policy denial, mixed
   public/private, push, reconnect, public-only regression — now also the chalk pass: public + private
   charging, all three refill channels) — still owed before any release-grade stamp.
5. If asked: **Roof / Tunnel** volumes; concave-safe Free-Shape fill; F3 re-constrain op. Remaining
   flagged decisions are cosmetic.

## Project layout (namespaces match folders)
`src/` contains: `Guide/` (data types), `Shapes/` (pure geometry math), `Systems/` (managers + undo),
`Network/` (packets + handlers), `UI/` (GUI + HUD + `LayoutToolIcons.cs`, the Cairo icon glyphs), `Config/`,
`Items/`, `Client/` (the tool controller), `Undo/Commands/`. Adding a new shape starts in
`Shapes/ShapeFactory.cs`. Soft-point flow behavior lives in `Shapes/SoftPointFlow.cs`. Chalk durability
lives in `Items/ItemGuideTool.cs` (helpers + fill-state rendering) + `Items/ItemChalkingPowder.cs` (refill)
+ `Systems/ChalkEffects.cs` (puffs/snap). (Filenames verified against the tree on 2026-07-18 — 68 source
files.)
