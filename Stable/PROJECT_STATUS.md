# Layout — Project Status & Handoff

**Checkpoint:** **SESSION 8 COMPLETE — Layout v0.1.0 is in real-world play.** Session 8 was the first full
playtest-driven feature session: the two-mode control scheme, the four-shape catalog (arch · half-circle ·
circle · ellipse under the primitives+constraints model), absorb-or-break, soft-point flow, Tier-2 fill,
sampled-curve targeting, the tile GUI, and the Surface-exit bake — built across batches 1–5d with three
in-session playtest/fix loops. The plan is **`ARCHITECTURE.md` v2.5**. The human has deployed the mod to
**real-played game worlds**; polish continues from live use. **This is the status/handoff doc.**

---

## 0. How to use this document

The project bundle to attach when resuming:

1. **`ARCHITECTURE.md` (v2.5)** — the authoritative plan, consolidated to current state (Session-8 end).
   History was flattened into its Settled Decisions Register; the narrative changelogs live only in the
   retired v2.4 copy, which is safe to leave out of session uploads.
2. **The code bundle** — `src/` (**43 files**), `assets/layout/`, `modinfo.json`, `modicon.png`,
   `Layout.csproj`, `BUILD_INSTRUCTIONS.txt`. Latest: `Layout-v0_1_0-session8-batch5d.zip`.
3. **`OUTSTANDING_ITEMS.md`** — the live punch-list: flagged decisions awaiting review, tuning items, and
   whatever real-world play surfaces.
4. **This status doc** — what's done, why, and where the next session starts.

> Precedence: **`ARCHITECTURE.md` v2.5 is the plan.** This doc tracks *status and intent*. Source code lives
> only in the `.cs` files so the two can't drift.

> **Build dependencies (unchanged):** `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll` — all
> ship with the game; the latter two live in `Lib\` (see BUILD_INSTRUCTIONS if a HintPath misses).

> **Runnable-mod packaging (unchanged):** build, then zip `modinfo.json` + `modicon.png` + `assets\` +
> `Layout.dll` at the **zip root** into `VintagestoryData\Mods`. Server config `layout.json` and client
> `layout-client.json` appear in `ModConfig` after first run.

---

## 1. Status at a glance

- **Modules 1–7: ✅ COMPLETE** (since Session 7) — math, data, systems + undo, networking, rendering, UI,
  integration.
- **Session 8: ✅ COMPLETE** — see §3. All batches built by the human; batches through 5d playtested in-game
  across three fix loops within the session.
- **🎮 IN REAL PLAY:** the human has moved the mod from playtest worlds into **actual played game worlds**.
  From here, findings come from real construction projects, not test scenarios — expect the punch-list to
  grow from use, and treat save-compatibility as mattering from now on (DataVersion 4 saves exist in the
  wild; the pinned-enum / additive-protobuf / default-migration rules are no longer theoretical).
- **⚠️ Open verification:** batch 5d's three fixes (Surface-exit bake, proportional soft flow, ghost-font
  Delete greying) were delivered at session end — built by the human, but the *proportional flow feel* and
  the *bake's cell-side choice* want a deliberate in-play look (see OUTSTANDING_ITEMS).

---

## 2. Coding order & effort levels

| # | Module | Effort | Status |
|---|--------|--------|--------|
| 1 | Pure math layer | **Max** | ✅ COMPLETE (+ Session-8 catalog: ellipse, arc mode, fill, flow) |
| 2 | Data model | Low | ✅ COMPLETE (DataVersion **4**) |
| 3 | Systems + undo | High | ✅ COMPLETE (+ break/bake ops & commands) |
| 4 | Networking | **Max** | ✅ COMPLETE (+ additive shape fields, cancel-grab, composed edits) |
| 5 | Rendering | High | ✅ COMPLETE (slabs, camera nudge, true-scale settled guides) |
| 6 | UI | Medium | ✅ COMPLETE (**tile GUI rewrite**, Session 8) |
| 7 | Integration | High | ✅ COMPLETE (two-mode controller, Session 8) |

Namespaces match folders: `Layout`, `Layout.Guide`, `Layout.Shapes`, `Layout.Systems`, `Layout.Network`,
`Layout.Undo`, `Layout.Undo.Commands`, `Layout.UI`, `Layout.Config`, `Layout.Items`, `Layout.Client`.

---

## 3. Session 8 — what was built (batches 1–5d)

**Batches 1–2 (render & feel):** Surface guides render as **paper-thin slabs** (0.01 blocks) hugging the wall
face; volumetric anti-z-fight via a whole-mesh 0.003-block per-frame camera nudge; White grabbed highlight
made **single-voxel** nearest-claim (radius constants deleted); even-span apex claims **2 voxels**; all six
voxel-type opacities client-configurable in `layout-client.json`.

**Batch 3 (control scheme, DECIDED items B1+I2):** `ToolMode` = **Create | Delete**. Left-click priority:
release → second foot → precise point grab → **body insert+grab in one gesture** → first anchor. Right-click =
universal cancel (fresh insert-born point removed; pre-existing point snaps back — new `GuideCancelGrabPacket`
op), draft discard, idle lock toggle.

**Batch 4:** point pick radius `max(0.10, voxel)` (dead-zone shadow removed); idle right-click on a body =
**lock-in-place insert** (point born locked ON the curve via `GetPointAt`, two undo steps); no-net-movement
insert releases remove the point.

**Batch 5/5b (the capability wave):** the **shape catalog** — pinned `ShapeConstraint` enum;
`GuideShapeType.Ellipse`; Half-circle = Arch+SemiCircle (feet-only, true arc sampler, derived apex marker);
Ellipse = new closed planar primitive (diameter anchors + minor handle; intrinsic plane from the first
click's face, stored as `ShapePlaneAxis`); Circle = Ellipse+Circle. **Absorb-or-break** end-to-end
(server auto-break on insert / breaking move; `BreakConstraintCommand`; instant client preview).
**Soft-point flow** (structural = anchors+locked; everything else flows). **Fill (Tier 2)**: arch = curve
closed by the foot-to-foot chord line, region filled; ellipse = disc; caps filled-aware everywhere.
**Sampled-curve targeting** with fingerprint-cached polylines — the root fix for near-anchor grabs.
`ShapeFactory` as the single construction point; `VoxelMarch` shared quantiser; DataVersion 4; additive wire
fields; **tile GUI** with the initial-shape picker and Favorites placeholder; HUD shape label; config
defaults. (5b fixed three CS0136 local-name collisions.)

**Batch 5c (playtest fixes):** GUI main rows made **permanent** (never re-purposed) with the per-guide
controls moved to an appended **Selected-guide section** + explicit **Deselect**; Delete mode disables the
other rows; **placed guides render at true scale** (draft-only coarsening had leaked into settled guides);
**SHIFT cardinal constraint on anchor re-grabs** (reference = the guide's other anchor).

**Batch 5d (playtest fixes):** **Surface-exit bake** — leaving Surface bakes the flattened positions into the
control points (undoable; full-state broadcast), so the volumetric guide is the shape you were looking at;
returning to Surface **restores the stored plane** (floor-seed only for never-Surface guides). **Soft flow
made proportional + frame-relative** (absolute offsets rejected in playtest: a shrunk arch kept its full apex
height — soft points still *felt* like constraints; now the shape scales/rotates with its structural
baseline). **Delete-mode ghost greying** (full-size text at ~22% alpha, no lit tiles, input-guarded).

---

## 4. Implementation decisions log (Session-8 additions)

**Everything in the previous checkpoints for Modules 1–7 still holds** except where ARCH v2.5's Settled Decisions Register
supersedes it (three-mode scheme → two-mode; chord targeting → sampled curve; dropdown GUI → tiles;
absolute soft offsets → proportional). Session-8 additions:

- **Primitives + constraints, not a flat enum** — a half-circle IS an arch, a circle IS an ellipse; the
  pinned enums stay short and favorites (future) can store {type + constraint} pairs.
- **Breaks are server-authoritative, atomic, and single-undo-step**; the client's only role is a sanctioned
  mirror constraint-clear at grab start so the drag preview is instant. Break-flavoured mutations broadcast
  **full state** (the point list changed shape).
- **`ShapeFactory` is the only place shapes are constructed** (manager create/restore/load, renderer guide +
  ghost, HUD measuring). Adding a shape touches the factory + the shape file.
- **`VoxelMarch` mirrors `CatmullRomSpline.Quantize` exactly** — the Module-1 invariant (identical cell math
  everywhere, or caps and visuals disagree at boundaries) now has a shared home for non-spline samplers.
- **Soft flow runs the same class on both sides**; the server composes soft edits into the SAME
  `UpdateControlPoints` batch as the grabbed edit — one cap check, one broadcast, generic undo origins and
  cancel restoration for free.
- **Filled counting is exact, not estimated** — `GetVoxelCount(scale, filled)` generates cells on the filled
  path. Correctness over performance per the human's standing rule (see backlog §5 for the perf note).
- **The GUI's main rows never change meaning** (Session-8 playtest rule): tool defaults are permanent;
  per-guide controls live in a clearly-separated appended section with a Deselect escape.
- **Bake-on-leaving-Surface** resolves the "projection is a view, but edits are made against the view"
  tension in favour of what the player sees: Surface edits are real, and leaving Surface keeps them.

---

## 5. Tuning backlog (known, deliberate, non-blocking)

Carried and new; the full annotated list lives in `OUTSTANDING_ITEMS.md`:

1. **Item transforms / recipe balance** — carried from Session 7, still untouched.
2. **Targeting feel constants** — all named in `GuideToolController`; sampled-curve targeting may want its
   own radius tune after real play.
3. **`PreviewFullResVoxelCap` = 8,000** — now genuinely draft-only; settled guides always pay full mesh cost
   (deliberate). Watch drag hitches on huge/filled guides in real play.
4. **Filled recount per drag update** — exact filled counting runs per move packet; add a count cache if big
   filled discs drag sluggishly.
5. **Bake cell-side** — baked points land on the plane's positive-side cell; a wall whose solid side is
   positive can show baked cubes one cell into the wall at coarse scales. Fix = air-side probe (exists in
   the renderer; would need a server-side sibling).
6. **Surface sampler's proper home** — flatten still lives in the renderer (`TODO(Surface)`); moving it into
   the shape layer got *closer* (fill machinery now lives there) but wasn't done.
7. **Ghost-greying vs native disabled state** — if the API's toggle buttons expose `Enabled`, that's the
   nicer upgrade over the alpha-ghost approach.
8. **Stale color comments** in `VoxelPosition.cs` / `ControlPoint.cs`; **scroll-wheel bindings** — parked.

---

## 6. Open questions / decisions to settle

**Settled — do not reopen:** everything in ARCH v2.5's Settled Decisions Register, including the two-mode scheme,
absorb-or-break, proportional soft flow (playtest-confirmed over absolute), arch fill = chord-closed region,
tile GUI with permanent main rows, Surface-exit bake, true-scale settled guides.

**Flagged for the human's review (decided by Claude under the standing decide-and-flag rule — each is one
small change to reverse):** the full list with rationale lives in `OUTSTANDING_ITEMS.md` §Flagged. Headlines:
ellipse body-click → nearest-handle grab; ellipse body right-click → nearest-handle lock toggle; new ellipse
minor = ½ major; minor handle slides on its axis; shape read-only in the Selected-guide section; the appended
per-guide section itself (vs. removing per-guide GUI editing entirely); SHIFT re-grab reference = other
anchor; circle→ellipse is the break floor (no further break to a free closed spline in v1); Favorites
deferred to a placeholder.

**Open — real-play agenda:** whatever the played worlds surface. The human: *"There's still progress to make
and things to tweak, but it's usable now."*

---

## 7. Next session — start here

1. **Upload:** ARCHITECTURE.md (v2.5), this doc, OUTSTANDING_ITEMS.md, and the latest code bundle
   (`Layout-v0_1_0-session8-batch5d.zip`).
2. **First:** collect the real-play findings list and triage it (quick fixes vs design work vs backlog §5) —
   same rhythm as Session 8, which proved the batch → build → playtest → fix loop works well within a session.
3. **Review the flagged decisions** (§6 / OUTSTANDING_ITEMS §Flagged) whenever convenient — none block play.
4. Reference points: every tunable constant is named and commented; `BUILD_INSTRUCTIONS.txt` has the
   run/packaging checklist; `ShapeFactory` is where any new shape starts; `SoftPointFlow` is one file if the
   flow feel needs tuning.

---

## 8. Workflow notes (for the human)

- Unchanged and proven again in Session 8: batches surfaced as downloads; you build locally (`dotnet build`)
  and playtest; **runtime findings beat compile checks** (near-anchor targeting, absolute-offset flow feel,
  greying subtlety, coarsening leak — none visible to a compiler).
- The plain-English safeguard caught real issues again this session ("GUI locked into edit mode", "only
  interacted voxels transform", "apex still constrains") — keep flagging anything that *sounds* wrong.
- Session 8 also validated the **decide-and-flag** working mode: Claude makes the call, marks it, playtest
  arbitrates (absolute→proportional flow being the textbook case).
- Keep the bundle together between sessions: ARCHITECTURE v2.5 + `src/` (**43 files**) + `assets/` + mod-root
  files + `Layout.csproj` + BUILD_INSTRUCTIONS + this doc + OUTSTANDING_ITEMS. Docs stay **outside** the zip,
  uploaded separately.
