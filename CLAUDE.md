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
- **Dependencies** (all ship with the game): `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll`,
  `cairo-sharp.dll` (GUI icon glyphs, since Session 10). All but the first live in `Lib/`. If a build fails on
  a missing reference, check the `HintPath` in `Layout.csproj` points at the local copies.
- **Runtime note:** `Entity.SidedPos` is obsolete in this API version — use `Pos`.
- **Package a runnable mod:** build **Release**, then zip `modinfo.json` + `modicon.png` + `assets/` +
  `Layout.dll` at the **zip root** (forward-slash entry paths), drop into `VintagestoryData/Mods`. Config
  files (`layout.json`, `layout-client.json`) appear in `ModConfig` after first run.
- **Versioning rule (standing, human-set):** EVERY revision bumps `modinfo.json` and ships as a NEW
  `Layout<version>.zip` in **`..\Layout Zips\`** (the sibling folder of this repo — human-directed
  location, holds the full 0.1.10+ history) — never overwrite an older release zip. Versions increment
  monotonically per revision (Session 10 shipped 0.1.10 → 0.1.13; Session 11 shipped 0.1.14 → 0.1.19).

## The documents (read these before large work)
- **`ARCHITECTURE.md`** — the authoritative plan. Its **Settled Decisions Register** lists locked-in design
  choices; **do not reopen those without the human explicitly asking.**
- **`TODO.md`** — the live punch-list: open bug, deferred requests, flagged decisions, future features.
- **`PROJECT_STATUS.md`** — where things stand and what each module does.
- **`SESSION_9.md` / `SESSION_10.md` / `SESSION_11.md`** — standalone per-session records (SESSION_11 is
  the most recent; it ends with the 0.1.14 playtest checklist and flagged decisions 11a–11j).

## ✅ Doc-verification pass — DONE (2026-07-05)
The Session-9 doc sections were reconstructed from a chat transcript, not from source. That first-session
verification pass is **complete**: every **`⚠ verify`** identifier (class/packet/command names, `DataVersion`,
file counts) was checked against the code and confirmed accurate — no reconstruction errors. The only stale
items were ARCHITECTURE.md's own file count (43 → **49**) and its shape-catalog snippets (§1/§2), now
corrected. The ⚠ tags in ARCHITECTURE/TODO/PROJECT_STATUS/SESSION_9 are resolved in place. Docs are
trustworthy; no need to repeat this pass.

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
- **Commit only when the human instructs**, and commit on a fork (a branch off main), not main directly.

## Tool modes (Session 10): Create · Edit · Delete
**Create** owns all geometry (place, grab/reshape, insert, lock; right-click = cancel/lock). **Edit** is
settings-only: left-click **selects** a guide and the GUI's setting rows then act on THAT guide (no
reshaping — geometry stays in Create); this replaced the old panel-expanding "selected-guide section".
**Delete** dispels. `ToolMode` is client-only (never wired), so it's safe to reorder.

## Current priorities (see TODO.md for detail; Session-11 record in SESSION_11.md)
1. **B-S9-1 — lock-in-place bug: THE HEADLINE for next session (human-confirmed).** Right-clicking to lock
   a point often locks the wrong (adjacent) voxel, and the guide visibly shifts/deforms when the lock
   lands. Intended: only the aimed voxel locks and turns red, and the guide never moves except when the
   user is actively moving it. Attempt the untried fix: **ray-vs-voxel-box first-hit picking** so the
   aimed cell is authoritative.
2. **Confirm 0.1.19 in passing** (tiny: pin/unpin wording; Fill greyed on the Free-Shape). Everything
   through **0.1.18 is playtest-confirmed** — Session 11 shipped 0.1.14 → 0.1.19 in one long iterate-and-
   confirm loop; per-version detail in `SESSION_11.md`.
3. Remaining flagged decisions are cosmetic (`SESSION_11.md` §8/§11/§13); 11a (spring-back restores
   position) was reviewed and confirmed good, 11n closed by the 0.1.19 greyed Fill.

## Project layout (namespaces match folders)
`src/` contains: `Guide/` (data types), `Shapes/` (pure geometry math), `Systems/` (managers + undo),
`Network/` (packets + handlers), `UI/` (GUI + HUD + `LayoutToolIcons.cs`, the Cairo icon glyphs), `Config/`,
`Items/`, `Client/` (the tool controller), `Undo/Commands/`. Adding a new shape starts in
`Shapes/ShapeFactory.cs`. Soft-point flow behavior lives in `Shapes/SoftPointFlow.cs`. (Filenames verified
against the tree on 2026-07-05.)
