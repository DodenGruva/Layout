# Layout — Session 10 Record (standalone)

> **What this is.** A self-contained record of everything that happened in **Session 10** — the first full
> session in Claude Code — kept separate from the main doc set, same pattern as `SESSION_9.md`. Written
> directly from the work as it happened (not reconstructed), so identifiers here are authoritative.
> Releases this session: **0.1.10 → 0.1.11 → 0.1.12 → 0.1.13** (one zip per revision, older zips preserved).

---

## Session 10 at a glance

1. **Doc-verification pass** — every Session-9 `⚠ verify` tag checked against source; all accurate;
   docs corrected in place (the only stale items were ARCHITECTURE.md's own file count and §1/§2 snippets).
2. **UI refinement pass (the session's main thread)** — the crude text-button GUI became a compact,
   icon-driven panel: custom-drawn glyphs on square tiles for every option row.
3. **B-S10-1 found and FIXED (playtest-confirmed)** — surface guides shifted behind block faces after reload
   (world-load race in the air-side probe).
4. **Divisions scroll-wheel** (the deferred Session-9 TODO) — the native number input; **playtest-confirmed
   working in 0.1.12**, then floored at 0 in 0.1.13.
5. **Third tool mode — Edit (0.1.13):** the setting rows act on the SELECTED guide in Edit mode, so per-guide
   editing no longer expands the panel with a separate section.
6. **Division markers pair on off-cell boundaries (0.1.13)** — the arch-apex midpoint treatment, generalised.
7. **Process rule established:** every revision bumps `modinfo.json` and ships as a NEW `Layout<version>.zip`
   — older release zips are never overwritten.

---

## Thread 1 — Icon UI pass (0.1.10 → 0.1.12)

**New file `src/UI/LayoutToolIcons.cs`** — Cairo-drawn vector glyphs registered in
`capi.Gui.Icons.CustomIcons` (registered once at client start from `LayoutModSystem.StartClientSide`). Stock
`GuiElementToggleButton`s constructed with an icon name render them, so the GUI's existing exclusive-toggle
plumbing (keys `"{row}:{i}"`, relight, inert-row guard, deferred recompose) is reused unchanged. The project
now references `Lib\cairo-sharp.dll` (added to `Layout.csproj`).

- **Shape picker:** 11 line-art glyphs (arch, half-circle, circle, ellipse, line, scalene/right/equilateral/
  isosceles triangles, rectangle, square). Right triangle carries a right-angle mark; equilateral/isosceles
  carry equal-side tick marks. **The arch glyph is an open elliptical dome** (upper half of an ellipse, no
  legs/baseline) — revised in 0.1.11 because the Catmull-Rom arch reads as a dome, not a legged archway.
- **Mode** (＋ / trash), **Projection** (iso cube / flat plane), **Fill** (hollow / solid square),
  **Visibility** (eye / slashed eye), **Plane** ("A" for Auto + iso cube with the active face filled).
- **Scale (0.1.12 final form):** copies the game's native icons — an **N×N grid of filled squares where N IS
  the voxel count** (1×1 smallest … 16×16 largest). Ascending order; names are voxel counts, not fractions.
  1×1 = a small near-dot; 2×2 mid-sized; **4×4 and 8×8 run edge-to-edge with enlarged squares**; **16×16 is
  ONE solid square filling the button** (human-requested, instead of an unreadable 16×16 mesh).
  (History: 0.1.10 shipped a bottom-fill-count design; 0.1.11 flipped to N×N grids but ascribed them
  backwards — icon 2×2 was labeled ½ block; 0.1.12 fixed the ascription: icon N×N = VoxelScale N.)
- **Compaction:** square 42-px tiles, left-aligned rows, narrower label column (74), tighter gaps; Delete-mode
  greying now uses the toggle button's native `Enabled=false` dim. Hovering any tile shows its name
  (`AddHoverText`); the header still names the current shape.
- **Divisions** is the one non-icon control: a number field (see Thread 3).

## Thread 2 — B-S10-1: surface guides shifted behind block faces after reload (FIXED, playtest-confirmed)

**Symptom:** on world load, previously placed guides rendered pushed back behind the faces of the blocks they
were placed on. **Root cause:** Surface guides only — the renderer's air-side probe
(`GuideRenderer.CountSolidProbes`) picks which side of the wall the decal hugs by reading world blocks; at
world-load the guide can mesh **before its chunks are loaded**, the probe sees all-air, and the decal lands on
the solid side. Volumetric guides were never affected (their anchors are stored half a voxel into the air
cell — reload-stable).

**Fix (renderer-only; no wire/persistence change):** `GetChunkAtBlockPos == null` during a probe marks that
guide's side *provisional* (`_deferredSurface`); a 500 ms tick (`OnReprobeTick`) rebuilds each deferred guide
once its neighbourhood is loaded (cheap one-chunk check per tick until then). Self-corrects on reload and
when approaching a far-away guide.

## Thread 3 — Divisions scroll-wheel (two attempts)

- **Attempt 1 (0.1.10):** dropdown removed; plain `AddTextInput` field + a `GuideToolGui.OnMouseWheel`
  override hit-testing `Bounds.PointInside(MouseX, MouseY)`. **Did not work in play.**
- **Attempt 2 (0.1.12):** the field is now the game's own **`GuiElementNumberInput`** (`AddNumberInput`, key
  `"div:text"`), which **declares its own `OnMouseWheel` handler** natively (the plain text input has none —
  the root cause), plus small up/down spinner buttons. Configured post-compose: `IntMode = true`,
  `Interval = 1`. Dialog-level fallback stays for hover-without-focus (element's GUI-scale-aware
  `IsPositionInside`). **Playtest-confirmed working.**
- **Floor at 0 (0.1.13):** the number input has no min, so its wheel/spinner could step to −1 and typing
  could go negative. `OnDivisionsTyped(text, fieldKey, onChanged)` now clamps and, when it clamps, snaps the
  DISPLAY back via `SetValue` (synchronously, before render — no visible −1). Applies to over-`MaxDivisions`
  too. So the field can never show a negative or over-cap value.

## Thread 4 — Third tool mode: Edit (0.1.13)

**Problem:** selecting a guide made the GUI expand with a whole appended "Selected-guide" section. **Fix:**
`ToolMode` gained **Edit** (enum reordered to `Create · Edit · Delete`; client-only, never wired). In Edit
mode the SAME setting rows (Scale / Projection / Plane / Fill / Divisions / **Visibility**) act on the
selected guide via the network senders instead of the tool defaults — the panel never grows a second section.

- **GUI (`GuideToolGui`):** `SetupDialog` is now mode-aware. Edit hides the shape picker + Favorites and
  shows a compact guide-info line + **Deselect**; the setting rows reuse the Create keys (`"scale"`,
  `"proj"`, `"plane"`, `"fill"`, `"div"`, `"vis"`) but bind the guide handlers + values (greyed with a
  "Click a guide to edit it" prompt when nothing is selected). `ResolveSelectedGuide` is now Edit-only;
  `BuildSelectedGuideSection` deleted; `RefreshSelectedGuideControls` retargeted to the main keys.
- **Controller (`GuideToolController`):** Edit left-click **SELECTS ONLY** — no grab / insert / lock — so a
  select-click can never accidentally reshape (empty click deselects). Right-click stays Create-only. All
  geometry editing (grab, insert, lock, place) remains in **Create**; **Edit is settings-only**; **Delete**
  dispels. New pencil glyph `LayoutToolIcons.ModeEdit`.
- **[Flagged]** Edit is select-only (reshaping stays in Create) — the clean split that avoids accidental
  inserts; revisit if in-play you want to reshape without switching to Create.

## Thread 5 — Division markers pair on off-cell boundaries (0.1.13)

When a division boundary (at `k/n · arc-length`) falls between two voxels, a single mark read half a cell
off. `ShapeGeometry.ClaimMarkerPaired` (new) generalises the arch apex's even-span pairing: it claims the
nearest cell AND the runner-up when their centre distances tie within 0.35 cells; boundaries that land on a
cell centre stay single. `DivisionMarks.Apply` now uses it. Pure render-side recolor, as before.

## Thread 6 — Versioning / packaging discipline

Every revision bumps `modinfo.json` and ships as a **new** `Layout<version>.zip` in `Dev/` (root-level
`Layout.dll` + `modinfo.json` + `modicon.png` + `assets/`, forward-slash entry paths). Older zips are never
overwritten (0.1.10 was overwritten once before the rule was set — hence no separate pre-icon 0.1.10).
Session N maps to version 0.1.N; mid-session revisions increment the patch (10 → 11 → 12 → 13).

---

## Open at Session-10 end

- **Confirmed in play this session:** B-S10-1 (surface reload shift); the divisions scroll wheel (native
  number input, floored at 0); Edit mode ("looks fantastic" — human).
- **Human backlog queued at session end (7 items — the next-session agenda; NONE started).** Full text +
  ordered plan in `TODO.md` ("Requested next — human backlog" / "Next session — start here"):
  1. **B-S10-2** — Surface→Volumetric bake grows INTO the block; should grow into the open air (the concrete
     bug form of flagged #15 "bake cell-side"; fix = server-side air-probe in `GuideManager.SetProjection`).
  2. **Polygon (N-gon).**
  3. **Triangle → 3-click** (anchor · anchor · height) — supersedes flagged #2; needs a two-point draft in
     `DraftManager`.
  4. **SHIFT → CTRL** for the cardinal constraint (reopens the settled SHIFT decision).
  5. **New SHIFT = spring the shape back to its original form.**
  6. **Shape picker → 3 buttons + an expand-arrow** submenu for the full catalog.
  7. **Favorites = the 3 initial slots** (remove the Favorites strip); starring fills a slot. Supersedes F2.
     Items 6+7 are one GUI design.
- **B-S9-1 (lock-in-place)** — unchanged, still the top *bug*; parked at the human's direction this session
  ("not gamebreaking"). Leading approach remains ray-vs-voxel-box picking.
- **Flagged (Session-10 additions, cheap to reverse):** Edit mode is select-only (reshaping stays in Create);
  divisions hover-scroll (vs the spec's focus-scroll); the 2×2 scale icon staying mid-sized while 4×4+ run
  edge-to-edge; tile edge = 42 px; triangle tick-mark legibility; plane-glyph (N–S vs E–W) legibility.
