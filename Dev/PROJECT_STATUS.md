# Layout — Project Status & Handoff

**Checkpoint:** **SESSION 10 — Layout v0.1.13, in real-world play, first full Claude Code session.**
Session 10 verified the reconstructed Session-9 docs against source (all accurate), replaced the text-button
GUI with a compact **icon-tile UI** (custom Cairo glyphs; native-style N×N scale icons), **fixed B-S10-1**
(surface guides shifting behind block faces on reload — playtest-confirmed), rebuilt the **divisions
scroll-wheel** on the game's native number input (**confirmed working**; floored at 0), added a **third tool
mode — Edit** (the setting rows edit the selected guide, so per-guide editing no longer expands the panel),
and **paired division markers** on off-cell boundaries. Releases: 0.1.10 → 0.1.11 → 0.1.12 → 0.1.13, one zip
per revision (never overwritten). **B-S9-1** (lock-in-place) remains **unresolved** by the human's choice
("not gamebreaking") and is still the top open bug. The plan is **`ARCHITECTURE.md`**. **This is the
status/handoff doc.** Session-10 detail: `SESSION_10.md`.

> **Fidelity note.** Session-9 sections were reconstructed from the working conversation, not regenerated from
> code on disk. **Verified against source 2026-07-05:** all specifics formerly tagged **⚠ verify** (file
> count, DataVersion, class/packet/command names) checked out — no reconstruction errors; the ⚠ tags are
> resolved in place. Full Session-9 detail: `SESSION_9.md`. Punch-list: `TODO.md` (renamed from
> `OUTSTANDING_ITEMS.md`).

---

## 0. How to use this document

The doc set (now a Claude Code repo):

1. **`ARCHITECTURE.md`** — the authoritative plan; Settled Decisions Register + Session-9 changelog.
2. **The code** — `src/` (**49 files**, verified: 43 at Session-8 end + 6 new Session-9 files — LineShape,
   TriangleShape, RectangleShape, ShapeGeometry, DivisionMarks, SetDivisionsCommand),
   `assets/layout/`, `modinfo.json`, `modicon.png`, `Layout.csproj`, `BUILD_INSTRUCTIONS.txt`.
3. **`TODO.md`** — the live punch-list (renamed from `OUTSTANDING_ITEMS.md`).
4. **`SESSION_9.md`** / **`SESSION_10.md`** — standalone per-session records (kept separate from the merged
   docs; SESSION_10 also records the Session-10 file additions: `UI/LayoutToolIcons.cs`, → **50 files**).
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
- **IN REAL PLAY:** save-compatibility matters — DataVersion 5 saves; pinned-enum / additive-protobuf /
  default-migration rules remain in force (now with the Session-9 shape/constraint/divisions additions).
  Session 10 touched no wire/persistence surface (renderer/GUI/icons only).
- **UNRESOLVED:** **B-S9-1 lock-in-place** — targeted voxel often not the one locked; guide still
  shifts/deforms on lock. Multiple fixes tried; see `TODO.md`. **Parked by the human ("not gamebreaking");
  still the top open bug.**

---

## 2. Coding order & effort levels

| # | Module | Effort | Status |
|---|--------|--------|--------|
| 1 | Pure math layer | **Max** | COMPLETE (+ S8 catalog; + S9 line/triangle/rectangle, divisions, slave-flow) |
| 2 | Data model | Low | COMPLETE (DataVersion **5**, verified) |
| 3 | Systems + undo | High | COMPLETE (+ break/bake ops; + S9 `SetDivisionsCommand` ✓) |
| 4 | Networking | **Max** | COMPLETE (+ additive fields; + S9 `GuideSetDivisionsPacket`, registered ✓) |
| 5 | Rendering | High | COMPLETE (+ S9 `DivisionMarks.Apply` recolor pass ✓) |
| 6 | UI | Medium | COMPLETE (+ S9 11-tile shape grid, divisions dropdown+field ✓) |
| 7 | Integration | High | COMPLETE (+ S9 generalized body-click policy for non-arch shapes) |

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

The **human queued a 7-item backlog at Session-10 end** — full text + ordered plan in `TODO.md`
("Requested next — human backlog" / "Next session — start here"). Headline order:

1. **B-S10-2** — Surface→Volumetric bake grows into the block; make it grow into the open air (self-contained
   bug; server-side air-probe in `GuideManager.SetProjection`).
2. **Shape picker → 3 buttons + expand arrow**, and **Favorites = those 3 slots** (one GUI design, do together;
   supersedes F2 + the Favorites placeholder).
3. **Triangle → 3-click** (anchor · anchor · height) — needs a two-point draft in `DraftManager`; supersedes
   the 2-click+born-apex flag.
4. **SHIFT → CTRL** for the cardinal constraint, then **SHIFT = spring back to the original shape**.
5. **Polygon (N-gon).**
6. **B-S9-1** (lock-in-place) — still the top *bug*, parked by the human; slot it in when they want it.
7. **Walk the remaining flagged list** with the human when convenient.

Everything through 0.1.13 is confirmed in play except the three 0.1.13 items (Edit mode "looks fantastic";
marker pairing + the 0-floor await a look) — see `SESSION_10.md`.

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
