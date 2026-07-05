# Layout — Project Status & Handoff

**Checkpoint:** **SESSION 9 COMPLETE — Layout v0.1.x, in real-world play, now migrating to Claude Code.**
Session 9 extended the shape catalog (Line, Triangle ×3, Rectangle/Square), added the **Divisions** visual
overlay, and reworked soft-point flow into the **slave-regime** model so the apex no longer acts as a pin.
One core bug (**B-S9-1**, lock-in-place) remains **unresolved** and is the top priority. The plan is
**`ARCHITECTURE.md`** (see its changelog for the Session-9 delta). **This is the status/handoff doc.**

> **Fidelity note.** Session-9 sections were reconstructed from the working conversation, not regenerated from
> code on disk. Specifics tagged **⚠ verify** (file counts, DataVersion, class/packet names) should be
> confirmed against the source — a natural first task in Claude Code, which reads the files directly. Full
> Session-9 detail: `SESSION_9.md`. Punch-list: `TODO.md` (renamed from `OUTSTANDING_ITEMS.md`).

---

## 0. How to use this document

The doc set (now a Claude Code repo):

1. **`ARCHITECTURE.md`** — the authoritative plan; Settled Decisions Register + Session-9 changelog.
2. **The code** — `src/` (**~49 files** ⚠ verify: 43 at Session-8 end + ~6 new Session-9 shape/feature files),
   `assets/layout/`, `modinfo.json`, `modicon.png`, `Layout.csproj`, `BUILD_INSTRUCTIONS.txt`.
3. **`TODO.md`** — the live punch-list (renamed from `OUTSTANDING_ITEMS.md`).
4. **`SESSION_9.md`** — standalone Session-9 record (safety copy; kept separate from the merged docs).
5. **This status doc.**

> Precedence: **`ARCHITECTURE.md` is the plan.** This doc tracks *status and intent*. Source code lives only
> in the `.cs` files so the two can't drift — and in Claude Code, prefer reading the code over trusting any
> reconstructed identifier here.

> **Build dependencies (unchanged):** `VintagestoryAPI.dll`, `Newtonsoft.Json.dll`, `protobuf-net.dll` — all
> ship with the game; the latter two live in `Lib\`.

> **Runnable-mod packaging (unchanged):** build, then zip `modinfo.json` + `modicon.png` + `assets\` +
> `Layout.dll` at the **zip root** into `VintagestoryData\Mods`. Server `layout.json` + client
> `layout-client.json` appear in `ModConfig` after first run.

---

## 1. Status at a glance

- **Modules 1-7: COMPLETE.** Math, data, systems + undo, networking, rendering, UI, integration.
- **Session 8: COMPLETE.** Two-mode control scheme; four-shape catalog (arch / half-circle / circle /
  ellipse); absorb-or-break; soft-point flow (proportional); Tier-2 fill; sampled-curve targeting; tile GUI;
  Surface-exit bake.
- **Session 9: MOSTLY COMPLETE.** ⚠ verify
  - **Shapes:** Line, Triangle (+ Right / Equilateral / Isosceles), Rectangle (+ Square) — F1 first wave.
  - **Divisions:** new purely-visual equal-parts overlay (magenta marks, arc-length spacing, renderer-side).
  - **Soft-point flow reworked:** the **slave-regime** model — interior grabs slave unlocked points onto the
    curve (zero pull; apex no longer pins); structural grabs keep shape-preserving flow. Human: "MUCH better."
  - **DataVersion 4 -> 5.**
- **IN REAL PLAY:** save-compatibility matters — DataVersion 5 saves; pinned-enum / additive-protobuf /
  default-migration rules remain in force (now with the Session-9 shape/constraint/divisions additions).
- **UNRESOLVED:** **B-S9-1 lock-in-place** — targeted voxel often not the one locked; guide still
  shifts/deforms on lock. Multiple fixes tried; see `TODO.md`. **Top priority next session.**
- **DEFERRED (specced, not built):** divisions scroll-wheel (drop dropdown, keep field, wheel-to-adjust
  while focused).
- **MIGRATING TO CLAUDE CODE.** These docs were promoted to Session-9 end as part of that move. Suggested
  `CLAUDE.md`: build command + VS 1.22.3 / .NET 10 target + the Settled Decisions "do not reopen" list +
  pointers to these docs. Keep it under ~200 lines; let Claude Code read the rest on demand.

---

## 2. Coding order & effort levels

| # | Module | Effort | Status |
|---|--------|--------|--------|
| 1 | Pure math layer | **Max** | COMPLETE (+ S8 catalog; + S9 line/triangle/rectangle, divisions, slave-flow) |
| 2 | Data model | Low | COMPLETE (DataVersion **5** ⚠ verify) |
| 3 | Systems + undo | High | COMPLETE (+ break/bake ops; + S9 `SetDivisionsCommand` ⚠ verify) |
| 4 | Networking | **Max** | COMPLETE (+ additive fields; + S9 `GuideSetDivisionsPacket` ⚠ verify) |
| 5 | Rendering | High | COMPLETE (+ S9 division-mark recolor pass ⚠ verify) |
| 6 | UI | Medium | COMPLETE (+ S9 11-tile shape grid, divisions control ⚠ verify) |
| 7 | Integration | High | COMPLETE (+ S9 generalized body-click policy for non-arch shapes) |

Namespaces match folders: `Layout`, `Layout.Guide`, `Layout.Shapes`, `Layout.Systems`, `Layout.Network`,
`Layout.Undo`, `Layout.Undo.Commands`, `Layout.UI`, `Layout.Config`, `Layout.Items`, `Layout.Client`.

---

## 3. Session 9 — what was built

> Reconstructed narrative; ⚠ verify identifiers. Full detail in `SESSION_9.md`.

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
GUI shape picker is now an **11-tile grid**. ⚠ verify

**Divisions (new feature).** Per-guide visual equal-parts overlay: recolors voxels at N arc-length boundaries
(**magenta**, `VoxelRenderType.Division`), computed **renderer-side** (`DivisionMarks.Apply`) as a pure
recolor — geometry / counts / caps untouched. `GuideData.Divisions`; additive DTO fields;
`GuideSetDivisionsPacket`; `SetDivisionsCommand`; `GuideManager.SetDivisions` (clamp + persist, no cap
check); `MaxDivisions = 256`; GUI dropdown+field in both tool and per-guide sections; `DefaultDivisions`
config. (One build hiccup: the packet class was first omitted by a silent scripted-edit no-op -> 2x CS0246,
then fixed — confirm it's present and registered.) ⚠ verify

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

**Flagged for review (cheap to reverse):** Session-9 adds the regime split, triangle's 2-click+born-apex
gesture, no-break-gesture for Right/Isosceles/Square, rectangle corners as markers, magenta division color,
and the per-keystroke divisions field. Session-8's flagged list still stands. Full list + rationale in
`TODO.md` (Flagged section).

**Open — real-play agenda:** whatever the played worlds surface.

---

## 7. Next session — start here (in Claude Code)

1. **Set up the repo:** `git init` + commit the current bundle; run `/init`; trim `CLAUDE.md` to build
   command + target + Settled-Decisions "do not reopen" list + pointers to these docs.
2. **Fix B-S9-1** (ray-vs-voxel-box picking) — top open bug; blocks the "only moves when I move it" invariant.
3. **Build the divisions scroll-wheel change** (specced in `TODO.md`).
4. **Verify the ⚠ tags** in these docs against the source (Claude Code reads the files directly — ideal first
   pass, and it can correct any reconstructed identifier in place).
5. Then, if feature-shaped: **F2 Favorites** (the catalog is now 11 tiles, so it earns its place) or **N-gon**.

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
- **Verify-before-trust for the Session-9 docs specifically:** they were reconstructed, so treat ⚠-tagged
  identifiers as provisional until Claude Code checks them against the source.
