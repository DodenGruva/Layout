# CLAUDE.md — Layout (Vintage Story mod)

> This file is read automatically by Claude Code at the start of every session. Keep it short and factual —
> it is a briefing, not documentation. The longer docs are referenced below; Claude Code reads them on demand.

## What this project is
Layout is a mod for **Vintage Story 1.22.3** (C# / **.NET 10**). It's a CAD-like, voxel-resolution
construction-planning tool: players place translucent geometric guide overlays in the world and build against
them by hand. The mod is visual-only — it never places, removes, or modifies blocks. Normal guides are
server-authoritative/world-shared; ClientOnlyFallback also supports private client-authoritative guides on
servers without Layout and, when permitted, alongside public guides. Status: **v0.1.x, in real play.**

## Build & run
- **Build:** `dotnet build` from the repo root (the folder with `Layout.csproj`).
- **Target:** .NET 10, Vintage Story 1.22.3.
- **Dependencies** (all ship with the game): `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll`,
  `cairo-sharp.dll` (GUI icon glyphs, since Session 10). All but the first live in `Lib/`. If a build fails on
  a missing reference, check the `HintPath` in `Layout.csproj` points at the local copies.
- **Runtime note:** `Entity.SidedPos` is obsolete in this API version — use `Pos`.
- **Package a runnable mod:** build **Release**, then zip `modinfo.json` + `modicon.png` + `assets/` +
  `Layout.dll` at the **zip root** (forward-slash entry paths), drop into `VintagestoryData/Mods`. Config
  files (`layout.json`, `layout-client.json`) appear in `ModConfig` after first run.
- **Versioning rule (standing, human-set):** EVERY revision bumps `modinfo.json` and ships as a NEW
  `Layout<version>.zip` in **`..\LayoutZips\`** (the sibling folder of this repo — human-directed
  location, holds the full 0.1.10+ history) — never overwrite an older release zip. Versions increment
  monotonically per revision. **Current: v0.1.52.**
- **Git:** `main` remains at v0.1.27; the active **`ClientOnlyFallback`** branch is pushed to
  **github.com/DodenGruva/Layout** through the v0.1.52 checkpoint. Commit/push ONLY when the human instructs.

## The documents (read these before large work)
- **`HANDOFF.md`** (repo root) — the consolidated current-state brief (scope · status · direction ·
  performance characteristics), written for external analysis; the fastest way to get oriented.
- **`ARCHITECTURE.md`** (in `dev/`, v2.9) — the authoritative plan. Its **Settled Decisions Register** lists
  locked-in design choices; **do not reopen those without the human explicitly asking.**
- **`TODO.md`** — the live punch-list: open bug, deferred requests, flagged decisions, future features.
- **`PROJECT_STATUS.md`** — where things stand and what each module does.
- **`PLAN_CLIENT_ONLY.md`** — F4's finalized implementation record and behavior matrix (candidate for 0.2.0).
- **`SESSION_9.md` / `SESSION_10.md` / `SESSION_11.md` / `SESSION_12.md` / `SESSION_13.md`** — standalone
  per-session records; SESSION_12 covers **v0.1.28–v0.1.45 ClientOnlyFallback**, and SESSION_13 covers the
  **v0.1.46–v0.1.52 optimization and interaction pass**.

## ✅ Docs verified & consolidated to v0.1.52 (2026-07-14)
The authoritative prose docs are consistent with **v0.1.52, DataVersion 8, protocol 3, 65 source files,
12 shape types / 18 tiles**. `ARCHITECTURE.md` is **v3.0**; `SESSION_12.md` is the detailed F4 record;
`SESSION_13.md` records the cap-performance and lock/drag work;
`HANDOFF.md` is the consolidated brief. Prefer source for exact identifiers, but no from-scratch doc audit is
needed before ordinary work.

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
- **Commit only when the human instructs.** Work is currently isolated on `ClientOnlyFallback`; do not merge
  or push to `main` unless the human directs it.

## Tool modes (Session 10): Create · Edit · Delete
**Create** owns all geometry (place, grab/reshape, insert, lock; right-click = cancel/lock). **Edit** is
settings-only: left-click **selects** a guide and the GUI's setting rows then act on THAT guide (no
reshaping — geometry stays in Create); this replaced the old panel-expanding "selected-guide section".
**Delete** dispels. `ToolMode` is client-only (never wired), so it's safe to reorder.

## Current priorities (detail in TODO.md; latest record in SESSION_13.md)
F4 is feature-complete and playtested through **v0.1.52** on `ClientOnlyFallback`. Normal public multiplayer
behavior remains in place. DataVersion 8 / protocol 3 add passive, non-deforming lock markers.
1. **Finish the B-S9-1 interaction regression.** First-hit voxel picking, full drag snapshots, robust curve
   fingerprints, passive lock markers, and marker/order cleanup are implemented. The human confirms that
   locking no longer shifts the guide and v0.1.52 is better, but repeated lock/drag/revert/unlock cycles need
   more playtesting before the bug is closed.
2. **Final F4 regression/release pass → v0.2.0 candidate.** Cover vanilla-server fallback, policy denial,
   mixed public/private mode, push, reconnect, and a public-only multiplayer regression.
3. **The human wants ENORMOUS fine-detail guides later** (grand domes, etc.) — needs the 3D scan
   guard / hard ceiling raised (and per-guide counts may then exceed int). Parked until asked.
4. If asked: **Roof / Tunnel** volumes; concave-safe Free-Shape fill; F3 re-constrain op. Remaining
   flagged decisions are cosmetic.

## Project layout (namespaces match folders)
`src/` contains: `Guide/` (data types), `Shapes/` (pure geometry math), `Systems/` (managers + undo),
`Network/` (packets + handlers), `UI/` (GUI + HUD + `LayoutToolIcons.cs`, the Cairo icon glyphs), `Config/`,
`Items/`, `Client/` (the tool controller), `Undo/Commands/`. Adding a new shape starts in
`Shapes/ShapeFactory.cs`. Soft-point flow behavior lives in `Shapes/SoftPointFlow.cs`. (Filenames verified
against the tree on 2026-07-14 — 65 source files.)
