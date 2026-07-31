# Layout — Gotchas: traps and reversals

> **Tier 1 — durable, append-only.** Never mentions a version except as evidence. Entries are added, never
> renumbered and never renumbered *away*: `G7` means `G7` forever, so `CLAUDE.md` and session records can
> cite it. If an entry is superseded, annotate it in place — never revise or delete it.
>
> **Every entry leads with its TRIGGER**, because a trap only helps if it is read *before* the mistake.
> The trigger is what `CLAUDE.md`'s task index points at.

---

## Traps

### G1 — Packet registration is append-only. Never renumber.
**Trigger:** before adding, removing or reordering anything in `PacketTypes.cs`.
**Trap:** slots are positional on both sides of the wire. Renumbering silently reinterprets every packet
sent by a client on the old numbering — it does not throw, it misreads.
**Do:** append. Leave retired slots **declared but dead** as padding rather than reclaiming them.
`LayoutAdminSetting` 6 and 7 are already in that state, and two `GuideBulkSyncPacket` flags have been dead
padding since protocol 6.
**Found:** Session 17. Detail: `dev/sessions/SESSION_17.md`, and the ledger in `dev/WIRE_HISTORY.md`.

### G2 — Guides are order-dependent translucent geometry. Regrouping changes the picture.
**Trigger:** before ANY change that alters the order or grouping of emitted primitives — meshing, batching,
culling, sorting, spatial subdivision, greedy merging.
**Trap:** guides render in the Opaque stage with manual alpha blending, depth-tested and double-sided. On a
hollow shell the guide overlaps itself at nearly every pixel, so **whichever batch draws first wins and
writes depth.** The accepted appearance is therefore partly a by-product of voxel emission order. This is
not a tolerance you can measure your way past — it is what the mod looks like.
**Do:** treat "does this regroup primitives?" as the gate. Deduplication is safe; merging is not. v0.3.57
vertex welding was safe *precisely because it regroups nothing* — verified by a harness comparing both index
buffers in submission order, 10/10 identical.
**Reopen only from a NEW measured bottleneck**, never from a plausible-sounding idea.
**Found:** Session 28, after Sessions 25–26 failed on exactly this. Detail: `dev/sessions/SESSION_28.md`,
`dev/plans/PLAN_RENDER_PERFORMANCE.md`, and the standing constraint at `TODO` A12a.

### G3 — `parsers.OptionalFloat` / `OptionalInt` return their DEFAULT when absent, not null.
**Trigger:** before adding an optional argument to any chat command.
**Trap:** an `args[0] is float` test **always passes**, so "was it supplied?" cannot be asked that way.
A bare `/layout inset` SET the inset to 0 and saved it. Three shipped commands were silently broken this way.
**Do:** default every optional float to `NaN` and go through `LayoutModSystem.Supplied()`. Never reintroduce
the `is float` idiom.
**Found:** Session 29. Detail: `dev/sessions/SESSION_29.md`.

### G4 — `OnGuideAddedOrUpdated` starts materialization before it reaches `RebuildGuide`.
**Trigger:** before touching guide update/rebuild paths, or when a guide renders at a pose it no longer has.
**Trap:** the early return never touches the scaffold mesh, leaving a **stale wireframe at the guide's old
pose** while the shell streams at the new one. It looks like a renderer bug; it is a control-flow one.
**Do:** know that this is **fixed for the Move path only** — a held guide goes straight to the rebuild.
**The general case is still live.** If you see a ghost at an old pose, this is your first suspect.
**Found:** Session 30. Detail: `dev/sessions/SESSION_30.md` §5 and §7.

### G5 — Rotate and mirror do NOT preserve the voxel count. Only translation may reuse it.
**Trigger:** before caching, reusing or trusting a guide's voxel count across a transform.
**Trap:** the in-plane frame sign-normalises m̂ toward world up, so a polygon can **re-phase** under
rotation or reflection and genuinely occupy a different number of voxels. A reused count is then wrong, and
it is wrong in the direction that matters — caps and budgets read it.
**Do:** recount in full after rotate and mirror. `TranslateGuide` is the **only** transform that may reuse
the cached count, and it earns that by enforcing whole-voxel deltas and refusing anything finer — which is
what makes the count provably unchanged.
**Also:** undo must store the **pivot** — a transformed shape's bounding centre is not where the original's
was.
**Found:** Session 31. Detail: `dev/sessions/SESSION_31.md`.

### G6 — Custom GUI elements must allocate their `LoadedTexture` before use.
**Trigger:** before writing any custom `GuiElement` that draws with Cairo.
**Trap:** `capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag, ref tex)` writes **into** the instance and
does **not** create one. A null reference throws inside the engine and takes the client down. **It builds
clean.** This crashed v0.4.4.
**Do:** allocate the `LoadedTexture` yourself first.
**Found:** Session 32. Detail: `dev/sessions/SESSION_32.md` §8.

### G7 — Do not recompose a dialog on click if any element is supposed to slide.
**Trigger:** before adding animation or transition to a GUI element.
**Trap:** a recompose rebuilds the element **at its new resting position**, so the slide never happens. You
get an instant jump and no error.
**Do:** update in place instead. That is why the Public/Private control's status line is updated rather than
recomposed.
**Found:** Session 32. Detail: `dev/sessions/SESSION_32.md`.

### G8 — Read the colour palette ONCE per mesh build. Never mutate it in place.
**Trigger:** before touching `GuidePalette`, colour configuration, or anything read by a mesh builder.
**Trap:** the colour arrays used to be static and mutated in place while materialization batches build on
background workers. A change part-way through a large guide's build could be seen by some batches and not
others — **one guide wearing two palettes** until the rebuild settled.
**Do:** `GuidePalette` is now immutable and swapped by a single aligned reference write;
`GuideMeshBuilder.Build` reads that reference **once into a local** and uses that instance for every
voxel, so a batch is always internally consistent — old palette or new, never a mixture. No lock on either
side. **Do not reintroduce in-place mutation.**
**Note the name:** a comment in `GuideMeshBuilder.cs` still calls that method `BuildGuideMesh`. There is no
such method — the entry point is `Build`.
**Note:** this hazard was **fixed, not inherited** — older notes that describe it as an open risk (including
`PLAN_BLOCK_OCCUPANCY.md` §7.4's framing) predate the fix.
**Found:** flagged in the F11 plan, fixed in Session 32. Detail: `dev/sessions/SESSION_32.md`.

### G9 — A settings page that can gate itself needs an escape hatch.
**Trigger:** before adding any setting that can disable the thing you set it from.
**Trap:** the pre-existing rendering lockout **closed the dialog** when guides were switched off, stranding
the player on a chat command with no way back through the UI.
**Do:** gate by **DISABLE, not BLOCK** — v0.4.3 changed exactly this. Anything added to the settings page
that can turn itself off needs the same treatment.
**Found:** Session 32. Detail: `dev/sessions/SESSION_32.md`, `TODO` F9.

### G10 — Icon glyph radii are bounded by the Canvas zoom.
**Trigger:** before drawing or editing a `LayoutToolIcons` glyph.
**Trap:** design box 60 at 1.12 zoom means anything past **~26.8 from centre falls outside the tile** and is
simply not drawn. Do not raise the tips without lowering the zoom.
**Do:** render it and LOOK at it — `.\dev\RenderIcon.ps1` draws a glyph at true sizes plus magnified copies.
Icons cannot be compile-checked and a correct glyph is still an unreadable smudge at 18–22 px.
**Found:** Session 32. Detail: `dev/sessions/SESSION_32.md` §7.

### G11 — `SendIngameError`'s message parameter is a LANG KEY, not a format string.
**Trigger:** before adding or editing ANY player-facing message.
**Trap:** the trailing arguments are applied **only when the key resolves**. An English sentence never
resolves, so the string is handed back verbatim and **every `{0}` reaches the player as literal text**.
**Eleven shipped messages** were printing their own placeholders — since the day they were written — and it
only surfaced when a cap finally refused something.
**Do:** build the whole message first and pass **no** arguments.
**Found:** Session 33. Detail: `dev/sessions/SESSION_33.md` §5.

### G12 — Reach for the existing diagnostic before writing a fix.
**Trigger:** before the first speculative fix to any "X does not work" report.
**Trap:** a "the voxel cap does not work" report cost **six revisions and three speculative fixes**. The
cause was a forgotten per-player override, and `/layout info <player>` prints exactly that, alongside the
effective cap. The admin commands from Sessions 23/27 exist to answer these questions.
**Do:** ask the mod what it thinks the state is before deciding what is wrong with it.
**Found:** Session 33. Detail: `dev/sessions/SESSION_33.md` §4.

### G13 — A static text wraps inside its bounds, but the bounds never grow to fit it.
**Trigger:** before adding any variable-length text to a GUI dialog.
**Trap:** a height guessed too small does **not** clip and does **not** scroll — it draws straight over
whatever comes next, which looks like a rendering bug rather than a layout one. This shipped **three times**
in one session: the jail confirmation warning at a guessed 34 px for three lines, plus two more of the same
bug already shipped in v0.4.31.
**Do:** measure — `capi.Gui.Text.GetQuantityTextLines(font, text, width, …)` × `GetLineHeight(font)`, as
`GuidePlayersDialog.TextHeight` does.
**Found:** Session 34. Detail: `dev/sessions/SESSION_34.md` §7.

### G14 — Every click must count as a press, whichever way the toggle flipped.
**Trigger:** before wiring a momentary (non-latching) GUI tile.
**Trap:** a handler that only acts on the ON edge **swallows every second click** of a fast run. Separately,
tiles that reset themselves in the same call that runs the action never survive a frame, so the lit state is
never visible.
**Do:** act on the press, not on the resulting state; let the lit state outlive the call.
**Found:** Session 34 (v0.4.29). Detail: `dev/sessions/SESSION_34.md`.

### G15 — A GUI edit must do everything the equivalent command does, not just call the setters.
**Trigger:** before adding a GUI path that duplicates an existing admin command.
**Trap:** each command does follow-up sends the panel needs just as much — the target's client caches its
effective per-guide cap to clamp its draft preview (`SendPlayerPolicy`), and its settings page shows its
limits (`SendAdminConfig`). A GUI edit that skipped those left **the server correct and the affected
player's client quietly stale until reconnect.**
**Do:** mirror the command in full, including its offline branch — `OnPlayerJail` mirrors `OnJailCommand`
exactly.
**Note the deliberate divergence:** commands **error out** on a bad number, because a person typed it and can
read the reply. The GUI **clamps** instead, because there the reply is the roster.
**Found:** Session 34. Detail: `dev/sessions/SESSION_34.md` §5.

### G16 — Per-row confirmation needs a row identity, not a boolean.
**Trigger:** before adding a confirm step to anything that appears once per row.
**Trap:** a single `bool` arms **every row at once** — an admin aiming at one player sees every row asking
for confirmation, and a stray second click anywhere frees the wrong one.
**Do:** store the identity of the armed row. `_confirmJail` (bool) became `_confirmUid` (string).
**Found:** Session 34. Detail: `dev/sessions/SESSION_34.md` §5.4.

### G17 — Do not share one handler between the roster event and the guide-list event.
**Trigger:** before consolidating two GUI refresh handlers that look alike.
**Trap:** they shared one, and it closed edit mode — correct for the roster, which is the server's
authoritative answer to a Save. But the Overrides **Edit** button both opens the form *and* requests that
player's guides, so the guide list arrived a tick later and **closed the form the click had just opened.**
**Do:** keep them split. `OnGuidesChanged` only redraws.
**Found:** Session 34. Detail: `dev/sessions/SESSION_34.md` §5.4.

### G18 — A scroll container inside a self-sizing dialog needs a fixed-height scope.
**Trigger:** before adding a scroll pane to any dialog that sizes itself to its children.
**Trap:** the container is as tall as the **whole** roster. Handed over directly, that height is what the
dialog sizes itself to — a window taller than the screen, almost all of it empty space behind a clipped list.
**Do:** wrap it in a fixed-height scope so the scope reports the *window* height. **The container must still
carry its true height**, because that is what clicks are hit-tested against — clamping it makes rows
unclickable the moment they scroll into view.
⚠️ **This is the one piece that rests on reasoning rather than a test.** If the dialog ever opens absurdly
tall on a long roster, this is why, and the fix is explicit dialog sizing. Also listed under
`STATUS.md` → Known unverified claims.
**Found:** Session 34. Detail: `dev/sessions/SESSION_34.md` §5.

### G19 — Send-to-ground deliberately ignores Step and Mirror. Copy does apply.
**Trigger:** before "fixing" the Transform pad's send-to-ground arrow to respect a lit toggle.
**Trap:** it is the one arrow in a state-driven pad that ignores a lit toggle, so it reads as a bug.
It is not: the drop is **solved before a flip would change the underside**, so applying Mirror to it would
be meaningless, and Step would fight the solve.
**Do:** leave it. `GuideToolController.GroundDropSixteenths` reuses `TryGuideFloorSixteenths` precisely so it
can never disagree with CTRL free-move about where a guide's underside is.
**Found:** Session 34, and flagged there for review. Detail: `dev/sessions/SESSION_34.md` §3.

### G20 — Vintage Story loads the HIGHEST version when several zips share a modid.
**Trigger:** when a build appears not to have taken effect.
**Trap:** multiple Layout zips in `Mods` are **harmless** — do not chase that as a cause. The game picks the
highest version, not the newest file.
**Found:** human-corrected. Every revision ships as a new zip, so this situation is the norm here, not an
accident.

### G21 — `Compress-Archive` produces a zip Vintage Story will not load correctly.
**Trigger:** before packaging a release zip.
**Trap:** Windows PowerShell 5.1's `Compress-Archive` writes **backslash** entry paths and directory
entries. Mod zips need **forward slashes, no directory entries, root files first.**
**Do:** build the zip through `System.IO.Compression.ZipFile` with explicit entry names.
**Also:** `modinfo.json` + `modicon.png` + `assets/` + `Layout.dll` go at the **zip root**, and none of the
game DLLs are ever included.

### G22 — Never hard-code a Vintage Story path in `Layout.csproj`. The repo is published.
**Trigger:** before editing anything path-shaped in the build.
**Trap:** the repository is public. A personal path or username in a tracked file ships to everyone.
**Do:** nothing — the project already resolves the install itself, in order: `-p:VintagestoryDir=…` → the
`VINTAGE_STORY` env var → the platform default. A bad path fails with one clear message naming the folder it
tried. If the game is somewhere unusual, set the env var; do not edit the file.
**Enforced by:** `dev/DocCheck.ps1` check 6, which runs repo-wide including `dev/archive/`.

### G23 — If the build cannot find `Newtonsoft.Json.dll` or `protobuf-net.dll`, check `Lib\`.
**Trigger:** when a build fails on a missing game DLL.
**Trap:** both normally live in the install's `Lib\` subfolder, which is what the project expects — but some
installs keep them **directly in the install root** instead.
**Do:** remove the `\Lib` from that one `HintPath`. Nothing else needs changing.
**Found:** the original first-build notes, now at
`dev/archive/superseded-2026-07-30/BUILD_INSTRUCTIONS.txt`. This entry exists because that was the **only**
unique content in that file, and it would otherwise have been archived out of reach.

### G24 — Shader sources must be pure ASCII.
**Trigger:** before editing `assets/layout/shaders/guide.vsh` / `.fsh`.
**Trap:** packaging refuses anything else.
**Found:** Session 28.

### G25 — Watch for double-encoded UTF-8 in `modinfo.json`.
**Trigger:** before editing `modinfo.json`, and before any release.
**Trap:** the description was double-encoded and **shipped mojibake to players**. The file legitimately
carries a BOM, which makes a byte-level eyeball check ambiguous.
**Do:** no non-ASCII bytes in `modinfo.json` outside the BOM.
**Enforced by:** `dev/DocCheck.ps1` check 7.
**Found:** Session 34.

### G26 — Never round-trip a repo file through `Get-Content` + `Set-Content`. It eats every em-dash.
**Trigger:** before editing ANY tracked file from PowerShell — a script, a bulk rename, a quick fix.
**Trap:** every markdown file here is **UTF-8 with no BOM**, and every one contains non-ASCII: em-dashes,
the ⚠ trap marker, `×`, `≤`, arrows. **61 of the `.cs` files do too.** Windows PowerShell 5.1 decodes a
BOM-less file as the system ANSI codepage, so `Get-Content -Raw` hands you mojibake and `Set-Content` writes
it back — **the whole file, in one silent pass.** It builds clean, it renders, and nothing complains. This
already shipped to players once in `modinfo.json` (**G25**) and it happened again to `ARCHITECTURE.md`
during the 2026-07-30 documentation audit.
**Do:** `[System.IO.File]::ReadAllText(...)` and `WriteAllText(...)` — `ReadAllText` detects UTF-8 correctly
with or without a BOM. Better still, use the editing tools rather than a shell round-trip; they are not
subject to this at all.
**Never hand-repair mojibake.** Restore the file. A corrupted pass mangles characters you will not notice,
and patching the visible ones leaves the rest.
**Also, keep `dev/DocCheck.ps1` PURE ASCII.** Writing the three mojibake lead characters into it literally
is what broke check 13 the first time it was added: the script had no non-ASCII bytes until then,
PowerShell read the new ones as ANSI, and it died parsing its own regex. The pattern is written as `\u`
escapes for exactly this reason — **a checker must not contain the bytes it hunts**, the same rule as
check 6 and personal paths.
**Enforced by:** `dev/DocCheck.ps1` check 13, across every tracked text file. Mojibake inside a markdown
code span is exempt — `SESSION_34.md` quotes the bug it fixed, and a quoted defect is data, not damage.
**Found:** Session 34 (as `modinfo.json` only); generalised 2026-07-30 after it recurred.

---

## Reversals and disproved claims

**This section prevents the most expensive failure mode there is: re-implementing something that was removed
on purpose.** It costs a whole session *and* ships a regression. Everything here was tried, or believed, and
then deliberately undone — and each one looked like an obvious improvement at the time.

### R1 — Private guides are NOT capped. Applied v0.4.22, reverted v0.4.27.
**Do not re-apply server caps to private guides.** It looks like a bypass and is not one.

The reasoning that was **wrong**: an analogy to chalk — *"private is private, not free"*, the rule that makes
a private placement spend chalk on a Layout server. The analogy does not hold. **Chalk is an inventory item
the server owns; caps are a storage and render budget.** Caps protect *shared* resources — server storage and
other clients' render cost — and a private guide, stored on the placer's own machine and invisible to
everyone else, consumes neither.

Only `HardVoxelCeiling` (10M) applies, because that is a physical limit rather than a policy.
`LocalGuideAuthority` passes all five caps as 0 **explicitly** — it had been omitting `perPlayerTotalVoxelCap`
and silently inheriting its 1,000,000 default.

Settled at the human's direction, on the merits, not as a preference. Full reasoning:
`dev/sessions/SESSION_33.md` §9.

### R2 — Greedy face merging. Tried and rejected, v0.3.44–v0.3.48.
Rejected on **appearance**, not performance — see G2. Real-play seams and fidelity defects.
The performance case originally recorded against it does not stand (R5), so do not reopen it believing the
measurement was the problem; the measurement was never the objection.

### R3 — Filled 3D volumes. Retired v0.2.17.
Replaced by hollow **Shell** and structural **Wireframe** persistence. Detail:
`dev/sessions/SESSION_16.md`.

### R4 — The Session 25–26 spatial renderer. Rolled back v0.3.49.
Restored the v0.3.42 renderer baseline. Whole-guide distance/frustum culling and render statistics were
**kept**; spatial final meshes and greedy merging were **rejected**. Both regroup primitives (G2).
Detail: `dev/sessions/SESSION_25.md`, `dev/sessions/SESSION_26.md`.

### R5 — `SESSION_26`'s 140→80 FPS regression. **Disproved.**
It was a **double-mesh draw bug** — the agent doing the work was drawing two mesh sets. Merging on its own
appeared to *help*. Separately, **every frame-time figure in Sessions 25–27 was measured against a 238 FPS
frame cap**: the "4.3 ms off-screen" baseline is the cap, not a floor, and the true baseline is ~1.0 ms.

`SESSION_26.md` carries a correction banner and must not be used to plan renderer work on its own. That
banner is **the correct pattern for a record that turned out wrong — annotate, never revise.**
Disproved in Session 28.

### R6 — `PLAN_BLOCK_OCCUPANCY.md`'s first draft. Substantially wrong; replaced, not amended.
Playtesting disproved three of its conclusions. Its **§0 records what was tried and why the design looks as
it does** — read that before touching occupancy, so the disproved conclusions are not re-derived.
Redrafted 2026-07-26.

### R7 — "Session 32 was not playtested." **Wrong, and it was written into four files.**
The human playtests **every** iteration as it ships — that is what the per-revision zip is for. Session 32
recorded the opposite, wrongly, and it had to be corrected across four documents.

An untrue "unverified" banner is not a harmless caution: it would have sent a later session **re-testing
settled work**. Treat each shipped version as tested unless the human says otherwise, and never write "not
playtested" into the docs on the assumption it wasn't.

### R8 — The rendering arc is closed, and the two remaining levers are spent or gated.
Not a reversal, but the same failure mode. **Vertex welding is at the theoretical floor** (~1.0 vertices per
quad) — that lever is spent, there is nothing left in it. The **custom shader** took an 8M-voxel guide from
8.2 ms to 1.8 ms (78% removed) and is what the mod renders with today. Primitive ordering and spatial culling
remain **explicitly gated behind G2**.

Reopen performance work only from a **new measured bottleneck and a fidelity-preserving design** — not from
the assumption that subdivision or merging must be next.

### R9 — The volumetric z-fight world probe. Removed v0.3.70; do not bring it back.
**`GuideMeshOptions.IsNeighborSolid`** — the world-solidity probe that decided, per face, whether a guide
voxel's face needed clearing off a block-grid plane — **is gone.** The face offset is now a uniform outward
push decided by the guide's own voxel set, and the world is deliberately not consulted.

Re-adding a world probe here looks obviously right (why push a face out where there is nothing to z-fight
against?) and is wrong twice over:

- **It flips on rebuild.** A player filling the volume the guide sits in changes the probe's answer, so the
  face offsets invert underneath them — a rendering change caused by building, which is the one thing a
  planning overlay must not do.
- **It breaks welding.** A probe-driven offset varies face to face, so coplanar faces of adjacent voxels
  disagree and the v0.3.57 welder cannot share them. A uniform offset lets every one of those welds happen.

**`GuideMeshOptions.OccupancyProbe` is NOT this returning.** It asks about a voxel's OWN cell rather than its
neighbour, and it feeds **colour only, never geometry** — so a rebuild simply re-reads the truth and the
failure mode above cannot occur. The distinction is the whole reason it was allowed to exist.

**The Surface-mode air-side probe is a different thing again, and is still live.** It picks which side of a
wall a flat decal hugs. Only the volumetric one was retired.

---

## Appendix — markers judged NOT to be traps

Recorded under the harvest protocol (`dev/plans/PLAN_DOC_OVERHAUL.md` §7 step 2), which requires that every
pre-existing ⚠️ marker become an entry above **or** carry a written judgement of why it did not. Baseline:
**54 markers across 12 files**, measured 2026-07-30 (§7.1).

> **Re-running this count after the overhaul.** The baseline figure is a *pre-overhaul* measurement and will
> never reproduce against the living tree again — four of its twelve files are now in `dev/archive/`, and
> `CLAUDE.md`'s sixteen dissolved into the task index by design. **Where the 54 went, verified by count:**
> 21 still stand in the session records, 7 moved verbatim into `dev/history/DONE.md`, and the remaining 26
> (`CLAUDE.md` 16, `HANDOFF.md` 6, `PROJECT_STATUS.md` 3, `ARCHITECTURE.md` 1) sit frozen in the archive,
> which holds **33** — those 26 plus `TODO.md`'s 7. Nothing is unaccounted for.
> **A future harvest counts session records and `DONE.md`.** Markers in `GOTCHAS.md`, `STATUS.md`,
> `WIRE_HISTORY.md`, `INDEX.md`, `TEMPLATE.md` and the plans are living-document prose quoting rules — not
> traps awaiting harvest — and are excluded, exactly as `PLAN_DOC_OVERHAUL.md`'s own were (§7.1).

| Source | Markers | Judgement |
|---|---|---|
| `CLAUDE.md` | 16 | The largest single source, and the whole reason the task index (§5.7) can replace inlined warnings. → **G13**, **G11**, **G12**, **G20**, **G6**, **G19**, **G14**, **G15**, **G18**, **G4**, **G3** (11 traps); → **R5**, **R6**, **R1** (3 reversals); 1 doc-staleness pointer at `ARCHITECTURE.md`/`PROJECT_STATUS.md`, **dissolved** — both are archived and their successors carry no version; 1 note that seven cosmetic/UI items were queued in `TODO.md`, **stale** — all seven shipped in v0.4.28. |
| `SESSION_9.md` | 3 | **Not traps.** `⚠ verify` doc-audit tags, all **resolved 2026-07-05** — every tagged identifier was checked against source and confirmed accurate. Historical bookkeeping about a verification pass, not a rule. These two files were never inspected for the original 2026-07-29 trap list; they were read in full for this harvest. |
| `SESSION_10.md` | 1 | **Not a trap.** Same resolved `⚠ verify` pass. |
| `ARCHITECTURE.md` | 1 | **Dissolved by the overhaul.** The staleness banner ("trails by four sessions"). Once version-bound content leaves the blueprint there is nothing left to trail — see §3 of the plan. |
| `PROJECT_STATUS.md` | 3 | 1 staleness banner (dissolved as above), 1 reference to the Session-9 verification pass, 1 **verification debt** → rehomed to `STATUS.md` → *Known unverified claims*, not a trap. |
| `HANDOFF.md` | 6 | 2 doc-staleness pointers (dissolved — both files are archived), 1 performance-analyst warning → **R5**, 1 private-guide cap note → **R1**, 1 stale-wireframe note → **G4**, 1 palette note → **G8**. |
| `TODO.md` | 7 | 1 append-only note → **G1**, 1 palette hazard → **G8** (recorded there as fixed), 1 icon-radius bound → **G10**, 1 settings-gate note → **G9**, 2 **verification debts** → `STATUS.md`, 1 shared-static note superseded by the fix. |
| `SESSION_17.md` | 2 | Both **verification debts** (the 32-chalk ceiling untested against xskills; 1.22.0/1.22.1 declared but not tested) → `STATUS.md` → *Known unverified claims*. Genuinely open, but they are **claims to test**, not rules to follow. |
| `SESSION_26.md` | 1 | The correction banner itself → **R5**. |
| `SESSION_32.md` | 3 | → **G7**, **G8**, **G10**. |
| `SESSION_33.md` | 4 | → **G11**; 1 rectangle drag-behaviour change (a delivered UX change, documented in the record and the blueprint's interaction flows — not a trap); 1 Players-dialog row/guide cap **superseded by v0.4.31**, which added the scroll pane it asked for; 1 → **R1**. |
| `SESSION_34.md` | 7 | → **G19**, **G15**, **G18**, **G16**, **G17**, **G13**; 1 bookkeeping note recording that the order-dependence constraint was moved to its own `TODO` entry (**A12a**) rather than deleted with the bug it was filed under — the constraint itself is **G2**. |

**Session records keep their ⚠️ text.** This is not duplication: the session holds the *narrative* — here is
what happened and how we found it — while this file holds the *rule*, plus a pointer back. Different content,
different lifetime.
