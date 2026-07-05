# CLAUDE.md — Layout (Vintage Story mod)

> This file is read automatically by Claude Code at the start of every session. Keep it short and factual —
> it is a briefing, not documentation. The longer docs are referenced below; Claude Code reads them on demand.

## What this project is
Layout is a mod for **Vintage Story 1.22.3** (C# / **.NET 10**). It's a CAD-like, voxel-resolution
construction-planning tool: players place translucent geometric guide overlays in the world and build against
them by hand. The mod is visual-only — it never places, removes, or modifies blocks. Guides are
server-authoritative, world-shared, and persist across logout/chunk-unload. Status: **v0.1.x, in real play.**

## Build & run
- **Build:** `dotnet build` from the repo root (the folder with `Layout.csproj`).
- **Target:** .NET 10, Vintage Story 1.22.3.
- **Dependencies** (all ship with the game): `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll`.
  The latter two live in `Lib/`. If a build fails on a missing reference, check the `HintPath` in
  `Layout.csproj` points at the local copies.
- **Runtime note:** `Entity.SidedPos` is obsolete in this API version — use `Pos`.
- **Package a runnable mod:** build, then zip `modinfo.json` + `modicon.png` + `assets/` + `Layout.dll` at the
  **zip root**, drop into `VintagestoryData/Mods`. Config files (`layout.json`, `layout-client.json`) appear
  in `ModConfig` after first run.

## The documents (read these before large work)
- **`ARCHITECTURE.md`** — the authoritative plan. Its **Settled Decisions Register** lists locked-in design
  choices; **do not reopen those without the human explicitly asking.**
- **`TODO.md`** — the live punch-list: open bug, deferred requests, flagged decisions, future features.
- **`PROJECT_STATUS.md`** — where things stand and what each module does.
- **`SESSION_9.md`** — standalone record of the most recent session's work.

## ⚠ First-session task: verify the reconstructed docs
The Session-9 sections of the docs above were reconstructed from a chat transcript, not from source. Anything
tagged **`⚠ verify`** (class names, packet names, `DataVersion`, file counts) must be confirmed against the
actual code and corrected in place. Doing this pass first makes the docs trustworthy for everything after.

## How to work on this project (the human's established workflow)
- **The human is not a programmer** and does not read code. They validate by *playing the mod* and describing
  what feels wrong in plain English. Explain changes in plain language, not code walkthroughs.
- **Decide-and-flag:** when a design choice is ambiguous, make a reasonable call, implement it, and clearly
  note it for the human's review rather than blocking. Favor what feels natural in-game.
- **Correctness over performance** unless the human says otherwise.
- **Playtest beats compile-checks:** the important bugs here are runtime/feel issues a compiler can't catch.
  After changes, still run `dotnet build` to catch compile errors before the human playtests.
- Give a **short summary of the request before doing significant work**, and ask if anything is unclear.

## Current priorities (see TODO.md for detail)
1. **B-S9-1 — lock-in-place bug (UNRESOLVED, top priority).** Right-clicking to lock a point often locks the
   wrong (adjacent) voxel, and the guide visibly shifts/deforms when the lock lands. Intended: only the aimed
   voxel locks and turns red, and the guide never moves except when the user is actively moving it. Leading
   untried fix: ray-vs-voxel-box first-hit picking so the aimed cell is authoritative.
2. **Divisions scroll-wheel** (specced, not built): remove the divisions dropdown, keep the type-in field, and
   make the mouse wheel adjust the value while that field is focused (±1/notch, clamped 0..256).

## Project layout (namespaces match folders)
`src/` contains: `Guide/` (data types), `Shapes/` (pure geometry math), `Systems/` (managers + undo),
`Network/` (packets + handlers), `UI/` (GUI + HUD), `Config/`, `Items/`, `Client/` (the tool controller),
`Undo/Commands/`. Adding a new shape starts in `Shapes/ShapeFactory.cs`. Soft-point flow behavior lives in
`Shapes/SoftPointFlow.cs`. (Confirm exact filenames against the tree — some are ⚠ verify in the docs.)
