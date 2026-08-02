# CLAUDE.md — Layout (Vintage Story mod)

> Read automatically at the start of every session. **This is a briefing, not documentation** — only what is
> needed to *start work*. It holds pointers, not content, and it stays short because it is paid for every
> single session. Everything else is read on demand.

## What this project is

Layout is a mod for **Vintage Story 1.22.x** (C# / **.NET 10**): a CAD-like, voxel-resolution construction
planning tool. Players place translucent geometric guide overlays and build against them by hand.
**The mod is visual-only — it never places, removes, or modifies blocks.** Guides are normally
server-authoritative and world-shared; an F4 client-only mode supports private guides. The drawing tool is
the **Chalking Kit**, with finite, powder-refillable chalk.

**For current version, wire state, module map, limits and open work → `STATUS.md`.** Do not duplicate any
of that here; it goes stale the moment it is copied.

## If you are…

| …doing this | …read this FIRST |
|---|---|
| touching the renderer | `dev/GOTCHAS.md` **G2** (order-dependent geometry) + **R2/R4/R5/R8**, then `dev/plans/PLAN_RENDER_PERFORMANCE.md` |
| adding or changing a packet | `dev/GOTCHAS.md` **G1** (append-only, never renumber), `dev/WIRE_HISTORY.md` |
| accepting ANY value from a client OR a file | `dev/GOTCHAS.md` **G31** — `Guide/GuideBounds.cs` is the one range check; call it — and **G32**, client conventions are not server invariants |
| adding a shape, or touching a voxel counter | `dev/GOTCHAS.md` **G31** (the scan guards bound a shape's SIZE, never its POSITION) and **G34** (a shape that fails its frame check reports ZERO, and zero passes every cap) |
| adding a rate limit, throttle or drop rule | `dev/GOTCHAS.md` **G35** — never drop the packet that RELEASES a resource |
| saving state from anywhere that is not a lifecycle point | `dev/GOTCHAS.md` **G39** — `Persist()` re-serialises the WHOLE registry; mutations call `MarkDirty()`, and deferring a write obliges every drop path to flush |
| deferring a `StoreData` call to a worker or a later moment | `dev/GOTCHAS.md` **G44** — it reaches disk at the game's NEXT save, and after the last save there is no next one |
| treating a null, false or empty return as "nothing to do" | `dev/GOTCHAS.md` **G43** — it may equally mean "I tried and failed"; skipping a fallback on that reading silently loses the work |
| short-circuiting, caching or pre-filtering ANY access/claim/privilege check | `dev/GOTCHAS.md` **G40** and **R12** — `TestAccess` has seven denial reasons and only one is land claims; **G47** permits bounds to scope staleness snapshots, never to replace the exact check |
| calling `GetPointAt` or any shape accessor more than once | `dev/GOTCHAS.md` **G41** — it may rebuild the entire geometry per call; hoist it, and keep the parameter types identical |
| writing a debounce or "already queued" guard | `dev/GOTCHAS.md` **G36** — the pending flag must never outlive its callback — and **R11**, which is what happened when it did |
| caching any world read that can FAIL | `dev/GOTCHAS.md` **G37** — "cannot see" is not "empty", and a chunk load is not a block change |
| validating a value against a pinned enum | `dev/GOTCHAS.md` **G38** — never bound it with a hand-written member name; config is rewritten on load, so a wrong bound DESTROYS the setting |
| writing a "when did this last happen" field | `dev/GOTCHAS.md` **G33** — `long.MinValue` is not a safe "never"; it overflows the subtraction |
| adding a packet that REFUSES something | `dev/GOTCHAS.md` **G27** — check a subscriber exists, or the player is told nothing |
| writing anything that runs on a worker thread | `dev/GOTCHAS.md` **G30** (`BlockOccupancy` is lock-free) and **G29** (check identity before removing by key) |
| adding cancellation to long-running worker work | `dev/GOTCHAS.md` **G45** — checks between stages do not cancel the scan/materialisation inside them; carry the probe into the deepest loop and never publish a partial result |
| adding a no-op/idempotent early return at an untrusted seam | `dev/GOTCHAS.md` **G46** — validate the request's domain first, then decide whether its valid meaning changes state |
| adding any player-facing text | `dev/GOTCHAS.md` **G11** — `SendIngameError`'s parameter is a LANG KEY |
| adding text to a dialog | `dev/GOTCHAS.md` **G13** — a static text wraps, but its bounds never grow |
| adding a custom GUI element | `dev/GOTCHAS.md` **G6** — allocate the `LoadedTexture` first, or the client dies |
| adding a setting that can gate itself | `dev/GOTCHAS.md` **G9** — disable, never block |
| changing caps or limits | `dev/GOTCHAS.md` **R1** — private guides are deliberately NOT capped — and **G28**, the three caps are not symmetrical |
| touching colours or the palette | `dev/GOTCHAS.md` **G8** — read the palette once per mesh build |
| adding an optional command argument | `dev/GOTCHAS.md` **G3** — optional parsers return their DEFAULT, not null |
| debugging "X doesn't work" | `dev/GOTCHAS.md` **G12** — run the existing diagnostic before writing a fix |
| drawing or editing an icon glyph | `dev/GOTCHAS.md` **G10**, and render it with `.\dev\RenderIcon.ps1` |
| editing any file from a PowerShell script | `dev/GOTCHAS.md` **G26** — a `Get-Content`/`Set-Content` round-trip destroys every em-dash |
| packaging a release | `dev/GOTCHAS.md` **G21** and **G25**, plus the versioning rule below |
| a design question that feels settled | `dev/ARCHITECTURE.md` → **Settled Decisions Register** |

**`dev/GOTCHAS.md` is the register of things that already cost us time.** Every entry leads with its
trigger. Read the trigger list before starting anything in the table above — it is cheaper than
re-discovering the trap.

## Build & run

- **Build:** `dotnet build` from the repo root (the folder with `Layout.csproj`).
- **Target:** .NET 10, Vintage Story 1.22.x. `modinfo.json` declares the minimum game version — read it
  there rather than restating it here.
- **Dependencies** (all ship with the game): `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll`,
  `cairo-sharp.dll` (GUI icon glyphs), `VSSurvivalMod.dll` (from the game's `Mods/`).
- **`Layout.csproj` finds the install itself** — `-p:VintagestoryDir=…` → the `VINTAGE_STORY` env var → the
  platform default. A bad path fails with one clear message naming the folder it tried.
  ⚠️ **Never hard-code a path there: the repo is published** (`GOTCHAS` G22). If a game DLL is not found,
  see `GOTCHAS` G23.
- **API note:** `Entity.SidedPos` is obsolete in this API version — use `Pos`.

### Packaging and versioning (standing, human-set)

**EVERY revision bumps `modinfo.json` and ships as a NEW `Layout<version>.zip`** into **`..\Layout Zips\`**
— the sibling folder of this repo (note the space in the real folder name). **Never overwrite an older
release zip.** Versions increment monotonically, one per revision. That per-revision zip is what the human
playtests, so it is not optional bookkeeping — it is the delivery.

**The one exception: a change with no behaviour to test does not bump** (comments, docs, `.gitattributes`,
dev tooling). The rule exists so every behavioural change reaches the human as a playable zip; a build that
behaves identically has nothing to playtest, and bumping would burn a version number on a no-op release.
**If the compiled DLL behaves differently in any way, it bumps** — when in doubt, bump.
*(Human-decided 2026-07-31, on the comment-only fix to `LayoutClientConfig` and `GuideMeshBuilder`.)*

**Zip layout matters:** `modinfo.json` + `modicon.png` + `assets/` + `Layout.dll` at the **zip root**, entry
paths with **forward slashes**, no directory entries, root files first. Build it with
`System.IO.Compression.ZipFile` and explicit entry names — `Compress-Archive` is wrong here (`GOTCHAS` G21).

**Git:** `main` is the mainline, published at **github.com/DodenGruva/Layout**. **The repo is public** — no
personal paths, no personal usernames, in any tracked file. **Commit and push ONLY when the human says so.**

## How to work on this project (the human's established workflow)

- **The human is not a programmer and does not read code.** They validate by *playing the mod* and describing
  what feels wrong in plain English. **Explain changes in plain language, never as a code walkthrough.**
- **Decide-and-flag:** when a design choice is ambiguous, make a reasonable call, implement it, and note it
  clearly for review rather than blocking. Favour what feels natural in-game.
- **Correctness over performance**, unless the human says otherwise.
- **Playtest beats compile-checks.** The bugs that matter here are runtime and feel issues a compiler cannot
  catch. Still run `dotnet build` before handing over, to catch compile errors first.
- **The human playtests EVERY revision as it ships.** Treat each shipped version as tested unless they say
  otherwise, and **never write "not playtested" into the docs on an assumption** — that error had to be
  corrected across four files once already (`GOTCHAS` R7).
- **Give a short summary of the request before doing significant work**, and ask if anything is unclear.
- **DOCS ONLY ON REQUEST** (standing, human-set, token discipline): per code iteration do
  **code → build → version bump → new zip, and nothing else.** Do not touch the documents until the human
  says "update the documents" or "finalize the session".
  **Exception:** if the conversation is nearing a context trim while docs are stale, **warn the human first**
  so nothing is lost unrecorded.
  ⚠️ **That warning belongs at the END of a session or before a context trim — NOT after every revision.**
  Track the doc debt silently and present it once, complete, when the moment comes. *(Human-set 2026-08-01,
  after three consecutive iterations ended with a stale-docs note: "Any reminders more frequent than that
  become too frequent, though I appreciate the notice.")*

### When the human says "update the documents"

Run this in order. `CHANGELOG.md` was missed once and had to be backfilled a session later; the checklist is
the cheap fix.

```
1. dev/sessions/SESSION_<n>.md   — new record, from TEMPLATE.md
2. dev/sessions/INDEX.md         — one row
3. CHANGELOG.md                  — EVERY version since the last entry
4. dev/GOTCHAS.md                — any new trap or reversal
5. dev/WIRE_HISTORY.md           — only if protocol/DataVersion moved
6. dev/TODO.md                   — open items only; delivered → dev/history/DONE.md
7. STATUS.md                     — REGENERATE, do not edit
8. CLAUDE.md                     — only if a working rule or a pointer changed
9. dev/DocCheck.ps1              — run it; it must pass
```

## Tool modes: Create · Edit · Transform · Delete

**Create** owns all geometry (place, grab/reshape, insert, lock; right-click = cancel/lock).
**Edit** is settings-only: left-click **selects** a guide and the GUI's setting rows then act on that guide —
no reshaping. **Transform** selects the same way and acts on the guide as a WHOLE OBJECT: move, rotate, copy,
mirror. **Delete** dispels. `ToolMode` is client-only and never wired, so it is safe to reorder.

## The document set

**Tier 0** `CLAUDE.md` (this file) · **Tier 1** `dev/ARCHITECTURE.md`, `dev/GOTCHAS.md` ·
**Tier 2** `STATUS.md` · **Tier 3** `CHANGELOG.md`, `dev/sessions/`, `dev/plans/`, `dev/history/` ·
**Tier 4** `dev/archive/` — frozen and superseded, **never cite it as current**.

Organised by **rate of change**, not by subject: durable facts and time-stamped narrative are kept apart so
that neither drags the other out of date. `STATUS.md` §9 lists every document and what it is for.
