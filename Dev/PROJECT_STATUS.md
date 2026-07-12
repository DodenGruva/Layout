# Layout — Project Status & Handoff

**Checkpoint: Layout v0.1.27.** Everything through **v0.1.26 is playtest-CONFIRMED**; v0.1.27 awaits a
quick look. Since Session 11's finalize the arc was: the queued backlog + Free-Shape + favorites + GUI
polish (v0.1.14–0.1.19), the **3D volume family** (v0.1.20–0.1.21: Sphere/Dome/Cylinder/Cone/Box; cell-
lattice shell/solid scan, always Volumetric, deterministic up-axis), then bug/UX rounds (v0.1.22–0.1.27):
placement + 2D/3D catalog labels, height-inversion + free-air-height fixes, the icon/favorites persistence
fixes, admin **`/layout dispel`** commands, a hard voxel ceiling against un-renderable giant guides, clear
placement-rejection errors, and the running-voxel-total widened to `long`. **DataVersion 7**; **59 source
files.** Committed to `main` and pushed to **github.com/DodenGruva/Layout** through v0.1.27. Release zips
live in **`..\Layout Zips\`** (full 0.1.10+ history). **Top open bug: B-S9-1 (lock-in-place)** — attempt
ray-vs-voxel-box first-hit picking. **The human wants ENORMOUS fine-detail guides later** (raise the scan
guard / hard ceiling). Standing rule: ship a zip per iteration; update docs / commit ONLY on the human's
say-so. Per-version detail lives in **`SESSION_11.md`** and **`TODO.md`**; the plan is **`ARCHITECTURE.md`**.

---

## 0. How to use this document

The doc set (now a Claude Code repo):

1. **`ARCHITECTURE.md`** — the authoritative plan; Settled Decisions Register (updated through Session 11).
2. **The code** — `src/` (**54 files**: 43 at Session-8 end + 6 Session-9 — LineShape, TriangleShape,
   RectangleShape, ShapeGeometry, DivisionMarks, SetDivisionsCommand; + 1 Session-10 — LayoutToolIcons;
   + 4 Session-11 — PolygonShape, SetSidesCommand, SpringBackCommand, FreeShape),
   `assets/layout/`, `modinfo.json`, `modicon.png`, `Layout.csproj`, `BUILD_INSTRUCTIONS.txt`.
3. **`TODO.md`** — the live punch-list (renamed from `OUTSTANDING_ITEMS.md`).
4. **`SESSION_9.md`** / **`SESSION_10.md`** / **`SESSION_11.md`** — standalone per-session records.
5. **This status doc.**

> Precedence: **`ARCHITECTURE.md` is the plan.** This doc tracks *status and intent*. Source code lives only
> in the `.cs` files so the two can't drift — and in Claude Code, prefer reading the code over trusting any
> reconstructed identifier here.

> **Build dependencies:** `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll`, and (since
> Session 10, for the icon glyphs) `cairo-sharp.dll` — all ship with the game; all but the first live in `Lib\`.

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
    glyphs via `CustomIcons`; → **50 source files**); native-style N×N scale icons (N = voxel count; 16× =
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
- **Session 11, batches 3–5 (v0.1.16–v0.1.19): rapid GUI polish — 0.1.16–0.1.18 playtest-CONFIRMED,
  0.1.19 pending** (`SESSION_11.md` §13/§15): Sides hard floor · 5-wide catalog + white separator ·
  favorites as YELLOW glyphs · combined Projection+Fill and Divisions+Sides rows · title-bar dead space
  removed · right-aligned header shape name · HUD chip · Delete-mode "-ghost" tile greying · Free-Shape
  SHIFT-vertical · pin/unpin vocabulary · Fill greyed on Free-Shapes · zips → `..\Layout Zips\`.
- **Session 11, batch 2 (v0.1.15): playtest feedback + three new features — playtest-CONFIRMED
  ("This was tested. Good work.").** Detail in `SESSION_11.md` §10.
  - **Favorites HARD-KEPT:** four slots; starring never evicts (message when full); right-click unstars
    (slot or catalog); ★ badges; empty-slot placeholders; no auto-padding of existing configs.
  - **Current Shape chip:** permanently-lit yellow glyph of the picked shape on the Mode row.
  - **SHIFT-centred apex** on the triangle's third click (perpendicular bisector, live ghost).
  - **Free-Shape:** irregular polyline, chained clicks; click-last = open, click-first = close (≥3);
    right-click steps back a corner; CTRL snaps to the previous corner; body inserts like the arch; fill
    deferred; 64-corner cap. `IsClosed` → **DataVersion 7**; additive Chain/Closed on the create request.
- **IN REAL PLAY:** save-compatibility matters — DataVersion **7** saves (v7: IsClosed; v6: Sides + the
  never-wired spring-back snapshot); pinned-enum / additive-protobuf / default-migration rules remain in
  force. Session 11's wire additions are all additive and registered append-only.
- **UNRESOLVED:** **B-S9-1 lock-in-place** — targeted voxel often not the one locked; guide still
  shifts/deforms on lock. Multiple fixes tried; see `TODO.md`. **Parked by the human ("not gamebreaking");
  still the top open bug.**

---

## 2. Coding order & effort levels

| # | Module | Effort | Status |
|---|--------|--------|--------|
| 1 | Pure math layer | **Max** | COMPLETE (+ S8 catalog; + S9 line/triangle/rectangle, divisions, slave-flow; + S11 PolygonShape, FreeShape, default-up frames, invertible arches) |
| 2 | Data model | Low | COMPLETE (DataVersion **7**: + S11 `Sides`, spring-back snapshot, `IsClosed`) |
| 3 | Systems + undo | High | COMPLETE (+ break/bake ops; + S9 `SetDivisionsCommand`; + S11 air-side bake, `SetSides`, `SpringBackCommand`, 3-click draft state) |
| 4 | Networking | **Max** | COMPLETE (+ additive fields; + S11 `GuideSetSidesPacket`, `GuideSpringBackPacket`, create-request inverted/sides/apex) |
| 5 | Rendering | High | COMPLETE (+ S9 `DivisionMarks.Apply`; + S11 apex-aware ghost, thinner Surface slabs) |
| 6 | UI | Medium | COMPLETE (+ S11 favorites picker + catalog fold-out, Sides row, auto-size tooltips) |
| 7 | Integration | High | COMPLETE (+ S11 CTRL/SHIFT remap, spring-back gesture, 3-click routing) |

Namespaces match folders: `Layout`, `Layout.Guide`, `Layout.Shapes`, `Layout.Systems`, `Layout.Network`,
`Layout.Undo`, `Layout.Undo.Commands`, `Layout.UI`, `Layout.Config`, `Layout.Items`, `Layout.Client`.

---

## 3. Session 9 — what was built

> Reconstructed narrative; identifiers verified against source 2026-07-05. Full detail in `SESSION_9.md`.

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

Full annotated list in `TODO.md`. Headlines: filled recount per drag; settled guides mesh at full res;
`PreviewFullResVoxelCap` 8,000 (draft-only); **division-mark recolor pass** (new — cheap but walks the cell
list); ghost-greying vs native `Enabled`; carried Session-7 items (item transforms, recipe balance, stale
comments, Surface flatten's move to the shape layer).

---

## 6. Open questions / decisions to settle

**Settled — do not reopen:** everything in `ARCHITECTURE.md`'s Settled Decisions Register, plus the Session-9
**slave-regime flow** (playtest-confirmed "MUCH better") and the **chord-invariant phantom drop**.

**Unresolved bug (not a decision — a fix owed):** **B-S9-1 lock-in-place.** Top priority. Leading un-tried
approach: ray-vs-voxel-box first-hit picking so the aimed cell is authoritative. See `TODO.md`.

**Flagged for review (cheap to reverse):** Session-10 adds Edit-mode select-only, divisions hover-scroll,
the scale-icon/tile-proportion calls; Session-9 adds the regime split, triangle's 2-click+born-apex gesture,
no-break-gesture for Right/Isosceles/Square, rectangle corners as markers, magenta division color, the
per-keystroke divisions field; Session-8's list still stands. Full list + rationale in `TODO.md`.

**Open — real-play agenda:** the **human's Session-10 backlog** (7 queued items) is now the agenda — see
below and `TODO.md`.

---

## 7. Next session — start here

**Everything through 0.1.14 is confirmed in play. 0.1.15 (favorites redesign, Current Shape chip,
SHIFT-centred apex, Free-Shape) is built but UNPLAYED.**

1. **Playtest 0.1.15** against the checklist in `SESSION_11.md` §12 — the hard-kept favorites (star/unstar,
   full-list message, ★ badges), the yellow chip, the centred apex, and the whole Free-Shape flow
   (chain, step-back, finish-open, close-the-loop, corner inserts). Fix-and-reship anything that feels
   wrong (each revision = version bump + new zip, per the standing rule).
2. **Review flagged decisions 11a–11r** (`SESSION_11.md` §8 + §11) — especially 11a (spring-back restores
   the as-placed POSITION) and 11n (the Fill toggle is inert on a Free-Shape).
3. **Free-Shape follow-ups if asked:** concave-safe fill for closed outlines; broadcasting the whole draft
   chain to other players (11q).
4. **B-S9-1** (lock-in-place; ray-vs-voxel-box first-hit picking is the untried fix) — parked by the human;
   slot it in when they want it.

---

## 8. Workflow notes (for the human)

- **Chat -> Claude Code migration:** the re-upload-everything-every-session tax goes away; Claude Code reads
  files on disk on demand. Put persistent context in `CLAUDE.md` (short), let auto memory + on-demand reads
  handle the rest, and let it run `dotnet build` itself so the build->fix loop is automatic.
- **The plain-English safeguard keeps paying off** — Session 9's biggest wins ("the apex still acts like a
  lock", "the voxel I target isn't the one that locks") came from you describing feel, not reading code.
  Keep flagging anything that *sounds* wrong.
- **Decide-and-flag** remains the working mode; playtest arbitrates. Session-9's slave-regime is the latest
  case of a flagged call that play confirmed.
- **Verify-before-trust for the Session-9 docs specifically:** they were reconstructed, then verified against
  source on 2026-07-05 — the identifiers proved accurate and the ⚠ tags are resolved. Future reconstructed
  content should get the same check before being trusted.
