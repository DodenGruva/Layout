# Layout — STATUS

> **Tier 2 — current state. REGENERATED WHOLESALE EACH SESSION, NEVER EDITED.**
>
> Editing is precisely how its predecessors drifted: `PROJECT_STATUS.md` ended up three sessions behind and
> `ARCHITECTURE.md`'s staleness banner was itself two sessions out of date. This file is kept short enough
> that **rewriting beats patching** — that is a feature, not a cost. If you find yourself amending a line
> here, regenerate the file instead.
>
> Supersedes `HANDOFF.md` and `dev/PROJECT_STATUS.md`, both frozen in `dev/archive/superseded-2026-07-30/`.

**Regenerated at:** v0.4.43, 2026-08-01 (Session 38 — `TODO` **A10.1 and A13 both closed**: the GUI layer
reviewed end to end, the doc-comment sweep finished, and a regression shipped and reverted inside the
session).

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
| **Current build** | **v0.4.43** on `beta` |
| **Last `main` release** | **v0.4.33** — `main` and `beta` were level on 2026-07-31; Sessions 37–38's ten revisions are on `beta` only |
| **Published at** | `github.com/DodenGruva/Layout` — **the repo is public; no personal paths or usernames in tracked files** |
| **Branches** | `main`, `beta`. `beta-shader` no longer exists — the shader work landed and is what the mod renders with (human-confirmed 2026-07-29) |

**The human is not a programmer and does not read code.** They validate by *playing the mod* and describing
what feels wrong in plain English, and they normally playtest **every revision as it ships**.

**Playtest position.** v0.4.39 was played and passed, which cleared Session 37's six-revision backlog.
v0.4.40 was played and is what surfaced both of this session's bug reports. **v0.4.41–v0.4.43 are in play
now with no result reported yet** — stated by the human ("while I test"), not assumed. See §6 for what to
look for in each.

### Threat model — settled 2026-07-31 (human-set)

**Layout is a public mod, so a MODIFIED CLIENT is in scope.** Findings that require one are real work and
none is closed as "won't fix". **But their likelihood is judged low, and that governs ORDER, not scope.**

That ordering has been worked all the way through: every hostile-client finding is fixed. The P0's cheap
half (validate the input) shipped in v0.4.35 and its expensive half (rewriting the scan loops) is deferred on
measured evidence — see §4 and `GOTCHAS` G31.

⚠️ **The P0 was never purely adversarial**, which is what settled its priority: the same scan loops are
reached on load from the private-guide file and the world save, so a hand-edited or corrupted file hung a
client or a server with nobody attacking anything. All three sources are now validated.

---

## 2. Wire and save state

| | |
|---|---|
| **DataVersion** | **13** (12 → 13 in Session 33, the Rectangle/Box re-gesture) |
| **Wire protocol** | **24** (23 → 24 in Session 34, the editable Players dialog) |
| **Source files** | **84** |
| **Shape catalog** | **15 types / 21 picker tiles** |

**Packet registration is append-only — never renumber.** Retired slots stay declared-but-dead as padding
(`LayoutAdminSetting` 6 and 7 are in that state, as are two `GuideBulkSyncPacket` flags since protocol 6).
See `dev/GOTCHAS.md` G1. Since v0.4.38 a request naming 6 or 7 is answered with the truth and changes
nothing — it previously ran the whole handler tail and printed a false confirmation.

**Enums crossing the wire as payload obey the same rule.** `GuideOpStatus` is carried as an int in
`GuidePlacementRejectedPacket`; `RejectedEmpty` was **appended last** at v0.4.37. Today's client ignores that
field entirely, which is *why* appending needed no protocol bump — **not** a licence to renumber. Ledger:
`dev/WIRE_HISTORY.md`.

**Coordinates are validated at every untrusted source** (v0.4.35, `src/Guide/GuideBounds.cs`): finite, and
inside the map plus 4,096 blocks of slack, with a hard ±33,554,432 backstop that applies before the engine
has reported a map size. Called at the packet boundary, in `RestoreGuide` (the private-guide import seam) and
in `LoadPayload` (world save **and** the hand-editable private-guide file). A guide that fails on load is
dropped with a logged warning rather than silently lost. `GOTCHAS` **G31**.

**Edit batches are bounded and de-duplicated**, and every mutating handler draws on a per-player
cost-weighted rate budget set far above human input (~10 edits/second in play against a 120/second
allowance). It **logs when it trips**. ⚠️ Release and cancel-grab are deliberately **exempt** — dropping the
packet that frees a lock would strand the guide. `GOTCHAS` **G35**.

**`VoxelCapWarningPacket` says how much and how many, never WHICH CAP.** That is why cap refusals are
explained by a **server-side** chat message; the packet drives only a generic HUD flash. Adding the missing
field is an append (G1) and a protocol bump — deferred, not rejected, and the chat message names the cap
today.

**Legacy encodings are read IN PLACE, never migrated.** Two-point rectangles and three-point boxes reproduce
to the voxel. Migration is not merely unnecessary but unsafe: **shapes are adopted on renderer worker
threads**, so rewriting the shared control-point list from a shape would be a data race. *(Confirmed correct
on every edit path by the Session-36 review.)*

**Private guide data** lives outside both config files and the world save:
`Layout/ClientOnlyGuides/<world-key>-<player-key>.json`, with atomic `.tmp` replacement, one `.bak`, and
`.corrupt-*` quarantine after a successful backup recovery. That quarantine catches *unparseable* files; a
parseable file carrying an out-of-range coordinate is caught by `GuideBounds` in `LoadPayload` instead.

---

## 3. What shipped recently

Full detail in the session records; `dev/sessions/INDEX.md` indexes all 30.

- **Session 38 (v0.4.40–v0.4.43)** — **the two oldest open items closed.** `TODO` **A10.1**'s remaining half:
  `GuideToolGui`, `GuideToolController` and `GuidePlayersDialog` read end to end (~8,000 lines), with all six
  trap entries living there confirmed intact and four defects fixed — the Players list appearing empty after
  a filter, an aim loop testing every guide in the world 33 times a second, a ghosted row that could light a
  tile, and unclipped player names. `TODO` **A13**'s comment sweep finished: nine wrong statements corrected,
  **and a live bug found under one of them** — a hand-written `> Sphere` bound meant seven of the fifteen
  shapes were never remembered and the preference was overwritten on disk each load. The chiselling highlight
  now lights itself after a world load (a read against an unloaded chunk is no longer cached).
  ⚠️ **One fix was a regression and was reverted the next revision** — `GOTCHAS` **R11**. New: **G36–G38**.
- **Session 37 (v0.4.34–v0.4.39)** — **`TODO` A14 emptied.** All twelve defects from the two Session-36
  reviews fixed in the agreed order, one shippable revision each: cap refusals that name their cap, the
  world-total cap's shrink escape, coordinate validation at all three untrusted sources, the two immense
  races, the lock-hoarding fix, rate limiting, the push endpoint, and the claim-validation window.
  **Two defects neither review found mattered more than several of the twelve:** a guide that voxelises to
  nothing passed every cap and was created invisible and silent (found **by the human in play**), and the
  HUD cap row read REFUSED permanently in five shipped zips (found by self-review). **A14.7 was finally
  reproduced**, correcting three claims that had been made from source-tracing alone. New: `GOTCHAS`
  **G33–G35**, **R10**.
- **Session 36 (no version shipped)** — **two code reviews.** The adversarial review briefed at
  `dev/plans/PLAN_CODE_REVIEW.md` (`TODO` A10.1) was run against v0.4.33, then an **independent review by
  another model** was evaluated and adopted in full. **Twelve confirmed defects, none fixed** — the brief
  forbids fixing while reviewing. New: `GOTCHAS` **G27–G32**. The two reviews overlap on nothing —
  correctness versus hostile-client. **The GUI layer was not read by either** *(closed in Session 38)*.
- **Session 35 (no version shipped)** — audit of the documentation overhaul against source. Five stale
  current-state claims corrected in `dev/ARCHITECTURE.md`; `dev/DocCheck.ps1` 10 → **13 checks**;
  `dev/plans/PLAN_CODE_REVIEW.md`. **`main` was levelled with `beta` at v0.4.33.**
- **Session 34 (v0.4.28–v0.4.33)** — the Session-33 polish queue in full; **send to ground** as a third tile
  in the Transform pad; momentary tiles that visibly press; **the Players dialog became editable** at
  protocol 24, with sorting, name filter, scroll pane and world totals.

---

## 4. What is open

**`dev/TODO.md` is the punch-list — this is a pointer, not a copy.**

**The review backlog is down to one item.** With A14 emptied in Session 37 and A10.1 and A13 both closed in
Session 38, what remains is **A10.2 — a performance-focused review of the NON-RENDER code.** Nobody has
looked. Session 28 measured and fixed the renderer; nothing equivalent has been done elsewhere. The two costs
removed on that axis so far were both found incidentally rather than by searching, which is the argument for
searching. **Start it after a clean playtest, not during one.**

**Four deliberate deferrals** (`TODO` **A15**) — each has a stated reason, so re-opening any is a decision
rather than a discovery:

1. **A14.7's `long`/count-based loop rewrite.** Unreachable through any validated entry point. Now deferred
   on *evidence*: the hang was reproduced 2026-08-01 and the shipped bound verified with a 4× margin.
2. **The `_settledMaterializations` concurrency gate** — one `LongRunning` task per streaming guide.
3. **The palette-lane cancellation** — self-healing on the next rebuild.
4. **`ResolveSculptCountLimit`'s wasted scan** — the review's own words: wasted work only, not a cap bypass.

Items 2 and 3 are renderer work and gated behind `GOTCHAS` **G2**'s measured-bottleneck rule.

Also open:

- **`TODO` A16 — the occupancy re-probe covers a guide's ANCHOR, not its whole extent.** Open by
  construction, recorded so it is not mistaken for a bug. A guide whose anchor chunk is resident while
  terrain further along it is not will not defer, and that part stays uncoloured until a block change nearby
  or `/layout built refresh`. Widening it means per-region attribution, deliberately not bundled into a bug
  fix.
- Naming the cap **in the HUD** still needs a field appended to `VoxelCapWarningPacket` — an append (G1) and
  a protocol bump, so polish rather than a defect while the chat message names it.
- Flagged decisions awaiting review are indexed in `dev/TODO.md`. Session 37 added seven — every tunable
  number it invented; Session 38 added two.

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
isolated count/footprint generation; claim checks stop after 128 blocks or ~1 ms per 20 ms server tick. Since
v0.4.36 that worker also *observes* cancellation, so a cancelled job gives the validator lane back at its
next seam instead of running to completion.

**The aim loop rejects by bounding box before it samples anything** (v0.4.40). Before that it walked every
guide in the mirror 33 times a second — a control-point fingerprint each, a resample on any miss, and a ray
test against up to 512 curve segments — though nothing past 12 blocks is targetable. That cost grew with the
world rather than with what was in front of the player. The padding is deliberately generous; see the
Session-38 flag in `dev/TODO.md`.

**One cost the reviews noted remains unmeasured:** `_settledMaterializations` has no concurrency gate. The
other — `GuideManager.Persist()` re-serialising the entire registry on every mutation — is unchanged in the
general case, but `BatchPersist()` now exists and the bulk path uses it, so publishing 100 private guides
writes once rather than a hundred times.

---

## 6. Known unverified claims

**What is declared but not tested.** Kept in one place so nobody re-derives it, and nobody assumes it was
checked.

1. **v0.4.41–v0.4.43 are in play with no result reported yet.** Three things have something specific to look
   for: whether the **Players dialog** is fully recovered (v0.4.41 reverted the guard that killed it);
   whether the **chiselling highlight lights itself after a world load** (v0.4.42 — if it still comes up
   dark, `TODO` A16 is the reason); and whether the **tool comes back on the shape you left it on**
   (v0.4.43 — try one of the seven that used to be forgotten). *Stated by the human, not assumed.*
2. **v0.4.40's four GUI fixes are unverified as a set.** v0.4.41 superseded that build before it was played
   through; the regression it carried was found immediately, the other four were not exercised.
3. **Whether the occupancy anchor test catches the real load ordering.** The deferral fires when a guide's
   anchor chunk is absent *at mesh time*; whether that window exists in practice cannot be settled by
   reading. *(Session 38 — `TODO` A16.)*
4. **The hard 32-chalk ceiling has never been tested against xskills itself.** The mechanism is understood
   and both attack routes are covered, but nobody has crafted a quality-bonus kit and confirmed it comes out
   32/32. Do that before trusting it. *(Session 17)*
5. **1.22.x support is DECLARED, not tested.** `modinfo.json` declares a 1.22.0 minimum and Vintage Story
   reads that as a minimum, so all of 1.22.x is nominally covered — but the code was built against **1.22.3**
   and nobody has confirmed every API used exists in 1.22.0. Smoke-test a 1.22.0/1.22.1 install. *(Session 17)*
6. **The Players list's scroll container is unverified outside the game.** It rests on reasoning rather than
   a test. If the dialog ever opens absurdly tall on a long roster, that is why, and the fix is explicit
   dialog sizing. *(`dev/GOTCHAS.md` G18)*
7. **Settled-shell streaming's remote-arrival half is unverified.** v0.3.58 makes guides above 100,000 voxels
   scaffold and stream; the "another player watches a large guide arrive" case needs a second player. Also
   open: world load with the 8M guide, and whether 100,000 is the right threshold.
8. **Session-29 block-occupancy verification items.** The diagnostic commands are hidden behind
   `"diagnosticCommands": true` in `layout-client.json` — **hidden, not deleted, precisely for this.**
9. **Whether `GOTCHAS` G4's general case is still live.** G4 says the stale-wireframe trap is fixed for the
   Move path only. A `ScaffoldFingerprint` guard now appears to close the general case too, but only one path
   was traced. Confirm before annotating G4 — an entry that says "still live" when it is not sends the next
   session hunting a fixed bug. *(Session 36)*
10. **How often the immense-sculpt race actually fired.** The defect is fixed (v0.4.36) but its window was
    never sized. Only play can say whether it was common or vanishingly rare. *(Session 37)*
11. **The exact Vintage Story maximum world size is still not known here** — but it no longer matters the way
    it did. `GuideBounds` asks the engine via `IBlockAccessor.MapSize*` and narrows to it when told, and
    carries a hard ±33.5M backstop that applies regardless. The backstop alone is what closes the hang, and
    it is now **measured** safe with a 4× margin rather than assumed.

*(Two long-standing items left this list. A14.7's hang WAS reproduced on 2026-08-01 — see `GOTCHAS` G31. And
"the UI layer has never been code-reviewed" was closed by Session 38, which read all three files end to end
and found the six traps living there intact.)*

> **Do not add an item here on an assumption.** The human playtests every revision, so shipped work is
> tested unless they say otherwise. Session 32 was recorded as unplaytested, wrongly, and the correction had
> to be made across four files — an untrue "unverified" banner would have sent a later session re-testing
> settled work. See `dev/GOTCHAS.md` R7. Items 1 and 2 above are here because the human *said* they were
> still testing.

---

## 7. Module map

Pure, dependency-light layers under a server-authoritative core. **Namespaces match folders**, with one
exception: `UndoManager` lives in `src/Systems/` as `Layout.Systems.UndoManager`.

| Folder | Role |
|---|---|
| `src/` | `LayoutModSystem` — the composition root (registers systems, item, channels, keybinds, HUD). |
| `Guide/` | Pure data: `GuideData`, `ControlPoint`, `VoxelPosition`, the pinned enums, projection/render settings, and **`GuideBounds`** (the one coordinate range check, shared by wire, restore and load). |
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
`Shapes/ShapeWireframe.cs`, `Shapes/LargeVolumeShellFallback.cs`. Accepting a coordinate from anywhere →
`Guide/GuideBounds.cs`.

**Data flow (networked):** controller/GUI/HUD → `ClientNetworkHandler.Send*` → protobuf → `ServerNetworkHandler`
→ `GuideManager` validates, persists, records undo → **broadcasts full/atomic state to everyone, originator
included** → clients apply to the local mirror → change events → `GuideRenderer` rebuilds that guide's mesh.
Rejections send a corrective full-state resync **and, since v0.4.34, an explanation** — the resync corrects
the client's *state*, and a refusal the player cannot see is the trap at `GOTCHAS` G27. **Systems communicate
by return value (`GuideOperationResult`), not events.** "Validates" now means caps, locks, claims **and input
range** (§2).

**Data flow (client-only):** the same calls route by guide ownership to `LocalGuideAuthority`, which applies
through a client-side `GuideManager`/`UndoManager`; the result re-enters the same mirror-apply events. In a
permitted mixed world the mirror holds both server and local ID sets.

---

## 8. Configuration and limits — exact values

**Server `layout.json`** (`LayoutServerConfig`; 0 or negative = unlimited; synced to clients on join):

| Key | Default | Changeable in play? |
|---|---|---|
| `configVersion` | 1 | — |
| `perGuideVoxelCap` | 500,000 | **yes** — Admin panel |
| `perPlayerTotalVoxelCap` | 1,000,000 | **yes** — Admin panel |
| `totalVoxelCap` | 0 (unlimited) | **yes** — Admin panel |
| `maxGuidesPerPlayer` | 0 (unlimited) | **yes** — Admin panel |
| `maxGuidesWorldWide` | 0 (unlimited) | **yes** — Admin panel |
| `allowClientOnlyMode` | false | **yes** — Admin panel |
| `undoHistoryDepth` | 50 | no — file + restart |
| `requiredPrivilege` | `""` (everyone) | no — file + restart |
| `adminCanOverrideLocks` | true | no — file + restart (withdrawn from the panel v0.4.17) |
| `enableChalkDurability` | true | no — file + restart (withdrawn from the panel v0.4.20) |

**Six settings are live-editable, five are not**, and the split is deliberate. `requiredPrivilege` was never
offered in the GUI because fumbling a privilege name there can lock every player — including the admin — out
of the tool, which is exactly what cannot then be fixed from inside the game; `undoHistoryDepth` is a memory
trade-off nobody tunes in play. The panel stages edits until **Save**; `GuideManager.ApplyCaps` makes them
live and `layout.json` is rewritten on every change. **Lowering a cap never deletes anything, and since
v0.4.34 it correctly permits the shrinks that bring a world back under a lowered cap** — all three voxel caps
are growth-only. `GOTCHAS` **G28**.

`configVersion` is 1 in any file on disk — the class default is 0 and `Normalize()` migrates it on load,
which is what carries the old generated cap defaults forward.

**The two chalk refill-channel flags are NOT here.** `allowHotbarChalkRefill` and
`allowInventoryChalkRefill` moved to the CLIENT config in v0.2.22 — they are player convenience toggles, not
server policy, since a refill costs the same powder wherever it happens. Stale keys left in an existing
`layout.json` are ignored.

**Hard-coded limits (in code, not config):** `HardVoxelCeiling` **10M** · legacy `MaxScanCells` 4M (on
Cylinder/Cone/Box it selects the surface-only fallback rather than rejecting them; Sphere and Dome carry the
same constant purely as a *filled*-scan guard, which filled volumes being retired has left inert) ·
`MaxDivisions` 256 · Polygon `MinSides` 3 / `MaxSides` 24 · Free-Shape `MaxCorners` 64 ·
`PreviewFullResVoxelCap` 8,000 (draft-ghost coarsening only) · `OccupancyBatchVoxelCeiling` 3M ·
`MaxPushedControlPoints` 1,024 · valid voxel scales {1, 2, 4, 8, 16}.

**Position is bounded separately from size, and that distinction is load-bearing.** Every scan guard above
bounds a shape's SIZE; none bounds its POSITION, and seven of the eight volume shapes hang outright at a
coordinate of 2^27 — measured, not inferred. `GuideBounds` is the position limit (§2), and it is what makes
those guards safe. `GOTCHAS` **G31**.

**A guide with zero voxels is refused outright** (v0.4.37). Every shape reports 0 when its own frame check
fails, and zero satisfies every *upper*-bound cap — so such a guide was previously created, persisted, listed
at 0 voxels and drawn as nothing, silently. `GOTCHAS` **G34**.

⚠️ **Private guides are deliberately NOT subject to server caps** — settled, do not "fix" it. Only
`HardVoxelCeiling` applies, because that is a physical limit rather than a policy. Full reasoning in
`dev/GOTCHAS.md` R1. **Publishing a private guide routes through `RestoreGuide`, which DOES apply every
cap** — consistent with R1's reasoning that caps protect *shared* resources. The endpoint additionally
requires the server to permit private guides at all, rate-limits per player, and length-checks both point
lists (v0.4.38). ⚠️ **It does NOT require an outstanding server request, and must not** — the settings page
publishes unprompted by design; `GOTCHAS` **R10**.

**Per-player overrides:** `/layout voxelcap <player> <n>` and `/layout totalvoxelcap <player> <n>` replace
the per-guide and cumulative defaults; a positive number persists in the world policy, `0` removes it.
Existing over-cap state is retained but cannot grow. **`/layout info <player>` prints the effective cap and
any override** — reach for it before writing a fix (`dev/GOTCHAS.md` G12). `/layout info` with no argument
prints the world totals against `totalVoxelCap`.

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

**Only `opacityBody` has a control** (the settings page's slider, which rebuilds every mesh behind a
debounce); the other five role opacities are file-only and read at client start. Saved values are validated
on load and **the normalised file is written straight back**, so a rejected value is destroyed, not merely
ignored — which is why validity checks here must never be bounded by a hand-written enum member
(`GOTCHAS` **G38**).

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
| `dev/sessions/` | 3 | 30 per-session records + `INDEX.md` + `TEMPLATE.md`. |
| `dev/plans/` | 3 | Design plans, including the live rendering and occupancy plans, and the reusable code-review brief. |
| `dev/history/` | 3 | `DONE.md` (delivered punch-list) and `CHANGELOG_ARCHITECTURE.md`. |
| `dev/TODO.md` | — | Open items only. |
| `dev/archive/` | 4 | **Frozen, superseded. Never cite as current.** |
| `dev/RenderIcon.ps1` | — | Renders an icon glyph to PNG at true sizes so it can be LOOKED AT before it ships. |

**If you are…**

| …doing this | …read this first |
|---|---|
| touching the renderer | `GOTCHAS` G2 + R2/R4/R5/R8, `dev/plans/PLAN_RENDER_PERFORMANCE.md` |
| adding or changing a packet | `GOTCHAS` G1, `dev/WIRE_HISTORY.md` |
| accepting ANY value from a client OR a file | `GOTCHAS` **G31** (position is bounded by `GuideBounds`, size by the scan guards — they are different things) and **G32** (client conventions are not invariants) |
| **validating a value against a pinned enum** | `GOTCHAS` **G38** — never bound it with a hand-written member name; the config is rewritten on load, so a wrong bound destroys the setting |
| **caching any world read that can fail** | `GOTCHAS` **G37** — "cannot see" is not "empty", and a chunk load is not a block change |
| **writing a debounce or "already queued" guard** | `GOTCHAS` **G36** — the pending flag must never outlive its callback — and **R11** |
| adding a packet that REFUSES something | `GOTCHAS` G27 — check a subscriber exists, or the player is told nothing |
| adding a shape, or touching a voxel counter | `GOTCHAS` **G31** and **G34** — the guards bound size, not position; and a shape that fails its frame check reports ZERO, which passes every cap |
| adding a rate limit, throttle or drop rule | `GOTCHAS` **G35** — never drop the packet that releases a resource |
| writing a "when did this last happen" field | `GOTCHAS` **G33** — `long.MinValue` overflows the subtraction |
| adding any player-facing text | `GOTCHAS` G11 (`SendIngameError` is a lang key) |
| adding text to a dialog | `GOTCHAS` G13 (bounds never grow) |
| adding a custom GUI element | `GOTCHAS` G6 (allocate `LoadedTexture` first) |
| changing caps or limits | `GOTCHAS` R1 (private guides are not capped) + **G28** (all three caps are growth-only), §8 above |
| calling anything from a mesh worker | `GOTCHAS` G30 — `BlockOccupancy` is lock-free (its comments now say so too) |
| completing queued per-player async work | `GOTCHAS` G29 — check identity before removing by key |
| debugging "X doesn't work" | `GOTCHAS` G12 — run the diagnostic before writing a fix |
| packaging a release | `GOTCHAS` G21, G25, and `CLAUDE.md`'s versioning rule |
