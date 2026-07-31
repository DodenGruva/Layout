# Layout — STATUS

> **Tier 2 — current state. REGENERATED WHOLESALE EACH SESSION, NEVER EDITED.**
>
> Editing is precisely how its predecessors drifted: `PROJECT_STATUS.md` ended up three sessions behind and
> `ARCHITECTURE.md`'s staleness banner was itself two sessions out of date. This file is kept short enough
> that **rewriting beats patching** — that is a feature, not a cost. If you find yourself amending a line
> here, regenerate the file instead.
>
> Supersedes `HANDOFF.md` and `dev/PROJECT_STATUS.md`, both frozen in `dev/archive/superseded-2026-07-30/`.

**Regenerated at:** v0.4.33, 2026-07-30.

---

## 1. What Layout is, and where it stands

Layout is a mod for **Vintage Story 1.22.x** (C# / **.NET 10**): a CAD-like, voxel-resolution construction
planning tool. Players place translucent geometric guide overlays in the world and build against them by
hand. **The mod is visual-only — it never places, removes, or modifies blocks.** Guides are normally
server-authoritative and world-shared; the F4 client-only mode also supports private client-authoritative
guides on servers without Layout, and alongside public guides where permitted. The drawing tool is the
**Chalking Kit**, with finite, powder-refillable chalk durability.

| | |
|---|---|
| **Current build** | **v0.4.33** on `beta` |
| **Last `main` release** | **v0.4.33** — `beta` was merged to `main` on 2026-07-31, so the two are level. Verified against `origin/main`'s `modinfo.json`, not taken from another document |
| **Published at** | `github.com/DodenGruva/Layout` — **the repo is public; no personal paths or usernames in tracked files** |
| **Branches** | `main`, `beta`. `beta-shader` no longer exists — the shader work landed and is what the mod renders with (human-confirmed 2026-07-29) |

**The human is not a programmer and does not read code.** They validate by *playing the mod* and describing
what feels wrong in plain English, and they playtest **every revision as it ships**. Treat each shipped
version as tested unless they say otherwise.

---

## 2. Wire and save state

| | |
|---|---|
| **DataVersion** | **13** (12 → 13 in Session 33, the Rectangle/Box re-gesture) |
| **Wire protocol** | **24** (23 → 24 in Session 34, the editable Players dialog) |
| **Source files** | **83** |
| **Shape catalog** | **15 types / 21 picker tiles** |

**Packet registration is append-only — never renumber.** Retired slots stay declared-but-dead as padding
(`LayoutAdminSetting` 6 and 7 are in that state, as are two `GuideBulkSyncPacket` flags since protocol 6).
See `dev/GOTCHAS.md` G1.

**Legacy encodings are read IN PLACE, never migrated.** Two-point rectangles and three-point boxes reproduce
to the voxel. Migration is not merely unnecessary but unsafe: **shapes are adopted on renderer worker
threads**, so rewriting the shared control-point list from a shape would be a data race.

**Private guide data** lives outside both config files and the world save:
`Layout/ClientOnlyGuides/<world-key>-<player-key>.json`, with atomic `.tmp` replacement, one `.bak`, and
`.corrupt-*` quarantine after a successful backup recovery.

---

## 3. What shipped recently

Full detail in the session records; `dev/sessions/INDEX.md` indexes all 27.

- **Session 35 (no version shipped)** — audit of the documentation overhaul against source. Five stale
  current-state claims corrected in `dev/ARCHITECTURE.md`, the chalk-refill flags moved to the right config
  in this file, and `dev/plans/PLAN_RENDER_PERFORMANCE.md` no longer reads "not started" for work delivered
  in Session 28. New: `GOTCHAS` **R9** and **G26**; `dev/DocCheck.ps1` 10 → **13 checks**;
  `dev/plans/PLAN_CODE_REVIEW.md`. **`main` was levelled with `beta` at v0.4.33.**
- **Session 34 (v0.4.28–v0.4.33)** — the Session-33 polish queue in full; **send to ground** as a third tile
  in the Transform pad; momentary tiles that visibly press; **the Players dialog became editable** at
  protocol 24 (`PlayerPolicyEditPacket` with staged caps and one Save, `PlayerJailPacket` separate and
  confirmed), with sorting, name filter, scroll pane and world totals.
- **Session 33 (v0.4.15–v0.4.27)** — T1 surface snap on free-move; the **Rectangle/Box cardinal defect
  fixed** (Rectangle now 3 clicks, Box 4, Square stays 2) at DataVersion 13; the **admin server-settings
  section** with a staged Save flow; Reveal Near/All; the Players dialog; and **private guides confirmed
  uncapped** after v0.4.22 wrongly applied caps and v0.4.27 reverted it.
- **Session 32 (v0.4.2–v0.4.14)** — the in-game **settings page** behind the title-bar gear: master on/off,
  guide opacity, configurable **colour schemes** with a per-role custom palette, chiseling highlight, a
  sliding Public/Private control with Publish, and both chalk-refill shortcuts.

---

## 4. What is open

**`dev/TODO.md` is the punch-list — this is a pointer, not a copy.** In summary:

- **No open bugs.** B-S9-1 and B-S10-2 are both resolved and playtest-confirmed.
- **Nothing is queued.** The F-queue has been empty since Session 33.
- What remains is the **review backlog**: an adversarial code review — now briefed in
  `dev/plans/PLAN_CODE_REVIEW.md`, and **to be run in a fresh session** for the reasons in its §1 — and a
  performance review of the non-render code. The third item, the documentation consolidation, was
  **delivered on 2026-07-30**; `dev/plans/PLAN_DOC_OVERHAUL.md` carries the phase log and the audit note.
- **`TODO` A13** is new: sweep the XML doc comments on the pinned enums and config classes. Two were found
  wrong on 2026-07-31, and the overhaul made source the authority those documents defer to.
- Flagged decisions awaiting review are indexed in `dev/TODO.md`; all are cosmetic and none block play.

---

## 5. Performance position, and the constraint that governs it

**The rendering arc is closed and both levers are spent or gated.**

| Lever | Result |
|---|---|
| **Vertex welding** (v0.3.57) | −72.9% vertices, −58.4% mesh data, −41.5% frame cost on an 8M-voxel guide. Triangle stream provably identical. Now at ~1.0 vertices per quad — **the theoretical floor. Spent.** |
| **Custom guide shader** (v0.3.60) | 8M-voxel guide **8.2 ms → 1.8 ms, 78% removed.** Guides are self-lit as a consequence; `shaderGuideBrightness` / `shaderAmbientResponse` approximate the old look back. `/layout shader off` restores the engine path for A/B. |

> ### ⚠️ The governing constraint — read before ANY renderer work
>
> **Guides are order-dependent translucent geometry** (Opaque stage, manual alpha blending, depth-tested,
> double-sided). On a hollow shell the guide overlaps itself at nearly every pixel, so **whichever batch
> draws first wins and writes depth.** The accepted appearance is partly a by-product of voxel emission
> order, and **any change that regroups primitives changes the picture.**
>
> This is why the Session 25–26 experiments failed, and why welding was safe — it regroups nothing.
> Primitive ordering and spatial culling remain explicitly gated behind it.
> **Full statement: `dev/GOTCHAS.md` G2, with the reversals at R2/R4/R5/R8.**

**Reopen performance work only from a NEW measured bottleneck and a fidelity-preserving design.** Not from
the assumption that subdivision or greedy merging must be next — both were tried and rejected on
*appearance*, and the performance case once recorded against them was itself disproved (G2, R5).

**Standing rule for the immense-guide path: input smoothness first.** Exact visuals and numbers may settle
later, but cursor responsiveness and the server tick must not wait on them. One below-normal worker does
isolated count/footprint generation; claim checks stop after 128 blocks or ~1 ms per 20 ms server tick.

---

## 6. Known unverified claims

**What is declared but not tested.** Kept in one place so nobody re-derives it, and nobody assumes it was
checked.

1. **The hard 32-chalk ceiling has never been tested against xskills itself.** The mechanism is understood
   and both attack routes are covered, but nobody has crafted a quality-bonus kit and confirmed it comes out
   32/32. Do that before trusting it. *(Session 17)*
2. **1.22.x support is DECLARED, not tested.** `modinfo.json` declares a 1.22.0 minimum and Vintage Story
   reads that as a minimum, so all of 1.22.x is nominally covered — but the code was built against **1.22.3**
   and nobody has confirmed every API used exists in 1.22.0. Smoke-test a 1.22.0/1.22.1 install. *(Session 17)*
3. **The Players list's scroll container is unverified outside the game.** It rests on reasoning rather than
   a test. If the dialog ever opens absurdly tall on a long roster, that is why, and the fix is explicit
   dialog sizing. *(`dev/GOTCHAS.md` G18)*
4. **Settled-shell streaming's remote-arrival half is unverified.** v0.3.58 makes guides above 100,000 voxels
   scaffold and stream; the "another player watches a large guide arrive" case needs a second player. Also
   open: world load with the 8M guide, and whether 100,000 is the right threshold.
5. **Session-29 block-occupancy verification items.** The diagnostic commands are hidden behind
   `"diagnosticCommands": true` in `layout-client.json` — **hidden, not deleted, precisely for this.**

> **Do not add an item here on an assumption.** The human playtests every revision, so shipped work is
> tested unless they say otherwise. Session 32 was recorded as unplaytested, wrongly, and the correction had
> to be made across four files — an untrue "unverified" banner would have sent a later session re-testing
> settled work. See `dev/GOTCHAS.md` R7.

---

## 7. Module map

Pure, dependency-light layers under a server-authoritative core. **Namespaces match folders**, with one
exception: `UndoManager` lives in `src/Systems/` as `Layout.Systems.UndoManager`.

| Folder | Role |
|---|---|
| `src/` | `LayoutModSystem` — the composition root (registers systems, item, channels, keybinds, HUD). |
| `Guide/` | Pure data: `GuideData`, `ControlPoint`, `VoxelPosition`, the pinned enums, projection/render settings. Depends only on `Vec3d`. |
| `Shapes/` | Pure geometry math. The `IGuideShape` seam, `ShapeFactory`, 14 generator classes backing 15 enum types, progressive/cancellable voxel generation, `CatmullRomSpline`, `VoxelMarch`, `ShapeGeometry`, `SoftPointFlow`, `DivisionMarks`. |
| `Systems/` | Side-neutral `GuideManager` (authority + JSON persistence + cap validation), `GuideLockManager`, `DraftManager`, `UndoManager`, `GuideRenderer`, `GuideMeshBuilder`, `BlockOccupancy`, `GuidePalette`. |
| `Network/` | `PacketTypes` (protobuf DTOs, **append-only registration**), `ServerNetworkHandler`, `ClientNetworkHandler`. |
| `UI/` | `GuideToolGui` (the F-menu GUI **and** the settings page, its three custom elements, and `AdminCapSteps`), `GuidePlayersDialog`, `LayoutToolIcons` (Cairo glyphs), `GuideHud`. |
| `Config/` | `LayoutServerConfig` (`layout.json`), `LayoutClientConfig` (`layout-client.json`). |
| `Items/` | `ItemGuideTool` (stateless glue + chalk helpers), `ItemChalkingPowder` (refill). |
| `Client/` | `GuideToolController`, `LocalGuideAuthority`, authority mode, vanilla-item gate, per-world/per-UID private persistence. |
| `Undo/`, `Undo/Commands/` | `IGuideCommand`, `UndoStack`, and the command types (… `TranslateGuide` / `RotateGuide` / `TransformGuide`). |

**Where to start for common work:** a new shape → `Shapes/ShapeFactory.cs`. Soft-point flow →
`Shapes/SoftPointFlow.cs`. Chalk durability → `Items/ItemGuideTool.cs` + `Items/ItemChalkingPowder.cs` +
`Systems/ChalkEffects.cs`. Large-guide work → `Systems/GuideRenderer.cs`, `Systems/DraftPreviewSpec.cs`,
`Shapes/ShapeWireframe.cs`, `Shapes/LargeVolumeShellFallback.cs`.

**Data flow (networked):** controller/GUI/HUD → `ClientNetworkHandler.Send*` → protobuf → `ServerNetworkHandler`
→ `GuideManager` validates, persists, records undo → **broadcasts full/atomic state to everyone, originator
included** → clients apply to the local mirror → change events → `GuideRenderer` rebuilds that guide's mesh.
Rejections send a corrective full-state resync. **Systems communicate by return value
(`GuideOperationResult`), not events.**

**Data flow (client-only):** the same calls route by guide ownership to `LocalGuideAuthority`, which applies
through a client-side `GuideManager`/`UndoManager`; the result re-enters the same mirror-apply events. In a
permitted mixed world the mirror holds both server and local ID sets.

---

## 8. Configuration and limits — exact values

**Server `layout.json`** (`LayoutServerConfig`; construction-time injection, so **edits need a server
restart**; 0 or negative = unlimited; synced to clients on join):

| Key | Default |
|---|---|
| `configVersion` | 1 |
| `perGuideVoxelCap` | 500,000 |
| `perPlayerTotalVoxelCap` | 1,000,000 |
| `totalVoxelCap` | 0 (unlimited) |
| `maxGuidesPerPlayer` | 0 (unlimited) |
| `maxGuidesWorldWide` | 0 (unlimited) |
| `undoHistoryDepth` | 50 |
| `requiredPrivilege` | `""` (everyone) |
| `adminCanOverrideLocks` | true |
| `allowClientOnlyMode` | false |
| `enableChalkDurability` | true |

`configVersion` is 1 in any file on disk — the class default is 0 and `Normalize()` migrates it on load,
which is what carries the old generated cap defaults forward.

**The two chalk refill-channel flags are NOT here.** `allowHotbarChalkRefill` and
`allowInventoryChalkRefill` moved to the CLIENT config in v0.2.22 — they are player convenience toggles, not
server policy, since a refill costs the same powder wherever it happens. Stale keys left in an existing
`layout.json` are ignored. Five of these caps are live-editable by admins from the settings page, which stages
edits until **Save**; `GuideManager.ApplyCaps` makes them live and `layout.json` is rewritten on every change.
**Lowering a cap never deletes anything.**

**Hard-coded limits (in code, not config):** `HardVoxelCeiling` **10M** · legacy `MaxScanCells` 4M (on
Cylinder/Cone/Box it selects the surface-only fallback rather than rejecting them; Sphere and Dome carry the
same constant purely as a *filled*-scan guard, which filled volumes being retired has left inert) ·
`MaxDivisions` 256 ·
Polygon `MinSides` 3 / `MaxSides` 24 · Free-Shape `MaxCorners` 64 · `PreviewFullResVoxelCap` 8,000
(draft-ghost coarsening only) · valid voxel scales {1, 2, 4, 8, 16}.

⚠️ **Private guides are deliberately NOT subject to server caps** — settled, do not "fix" it. Only
`HardVoxelCeiling` applies, because that is a physical limit rather than a policy. Full reasoning in
`dev/GOTCHAS.md` R1.

**Per-player overrides:** `/layout voxelcap <player> <n>` and `/layout totalvoxelcap <player> <n>` replace
the per-guide and cumulative defaults; a positive number persists in the world policy, `0` removes it.
Existing over-cap state is retained but cannot grow. **`/layout info <player>` prints the effective cap and
any override** — reach for it before writing a fix (`dev/GOTCHAS.md` G12).

**Client `layout-client.json`** (`LayoutClientConfig`): `forceClientOnly` (subject to server policy), last
scale / projection / 2D fill / 3D wireframe form / shape+constraint / divisions / sides, six role opacities,
up to four hard-kept pinned favourites, and **both chalk-refill flags** (`allowHotbarChalkRefill`,
`allowInventoryChalkRefill`, both false — they live here, not in `layout.json`). Ground-storage refill
(Shift+right-click a set-down kit) is **always** allowed and ungated; the two flags only opt in the hotbar
and inventory-slot shortcuts. `guideRenderingEnabled` defaults true.
Rendering knobs: `shaderGuideBrightness` 0.78, `shaderAmbientResponse` 0.55, `voxelFrameStrength` 0.25,
`zFightInset` **0.0006** (an OUTSET since v0.3.70), `occupancyRecolour` false. Colour: `colorScheme`
(0 Default / 1 Red-Green Safe / 3 Custom; **2 is retired**) and `customColors`, seven `"#RRGGBB"` strings in
pinned role order — a missing or malformed entry falls back to Default **per role**.
`diagnosticCommands` (false) hides the development commands.

Mostly never synced, with one exception: **the hotbar refill flag is reported to the server on join** via
`ChalkRefillPrefsPacket`, because `ItemChalkingPowder`'s held-interact runs on both sides and the server is
what mutates the stacks — without it the toggle would be a silent no-op.

---

## 9. The document set

| Document | Tier | What it is |
|---|---|---|
| `CLAUDE.md` | 0 | Always loaded. How to work here, the build/versioning rules, and the task index. |
| `dev/ARCHITECTURE.md` | 1 | The blueprint, and the **Settled Decisions Register**. Changes only when a decision changes. |
| `dev/GOTCHAS.md` | 1 | Traps (indexed by trigger) and **reversals** — things deliberately undone. |
| **`STATUS.md`** | 2 | This file. Current state, regenerated. |
| `CHANGELOG.md` | 3 | The player-facing release log. |
| `dev/sessions/` | 3 | 26 per-session records + `INDEX.md` + `TEMPLATE.md`. |
| `dev/plans/` | 3 | Design plans, including the live rendering and occupancy plans. |
| `dev/history/` | 3 | `DONE.md` (delivered punch-list) and `CHANGELOG_ARCHITECTURE.md`. |
| `dev/TODO.md` | — | Open items only. |
| `dev/archive/` | 4 | **Frozen, superseded. Never cite as current.** |
| `dev/RenderIcon.ps1` | — | Renders an icon glyph to PNG at true sizes so it can be LOOKED AT before it ships. |

**If you are…**

| …doing this | …read this first |
|---|---|
| touching the renderer | `GOTCHAS` G2 + R2/R4/R5/R8, `dev/plans/PLAN_RENDER_PERFORMANCE.md` |
| adding or changing a packet | `GOTCHAS` G1, `dev/WIRE_HISTORY.md` |
| adding any player-facing text | `GOTCHAS` G11 (`SendIngameError` is a lang key) |
| adding text to a dialog | `GOTCHAS` G13 (bounds never grow) |
| adding a custom GUI element | `GOTCHAS` G6 (allocate `LoadedTexture` first) |
| changing caps or limits | `GOTCHAS` R1 (private guides are not capped), §8 above |
| debugging "X doesn't work" | `GOTCHAS` G12 — run the diagnostic before writing a fix |
| packaging a release | `GOTCHAS` G21, G25, and `CLAUDE.md`'s versioning rule |
