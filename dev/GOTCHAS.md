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
**Do:** know that **this is now closed, and the entry is kept for the pattern, not the bug.** Every early
exit in `OnGuideAddedOrUpdated` is either **pose-matched** — it skips the rebuild only when
`RenderFingerprint` proves the geometry identical, control-point positions included — or ends in
`RebuildGuide`. The load-bearing guard is `mesh.ScaffoldFingerprint`: it is written in exactly one place,
beside `RenderedWireframe = true`, and **fails closed** — when it cannot prove the wireframe on screen
depicts the current guide it declines and sends the caller down `RebuildGuide`.
⚠️ **The wording here read "fixed for the Move path only — the general case is still live" until Session
39**, three sessions after the general case was actually closed. **An entry that says "still live" when it
is not sends the next session hunting a fixed bug** — R7's failure mode running the other way. Confirmed by
tracing every early return, not by tracing one.
**Found:** Session 30. **Closed:** verified Session 39. Detail: `dev/sessions/SESSION_30.md` §5 and §7.

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

### G27 — A packet the server sends is not feedback the player sees. Check for a subscriber.
**Trigger:** before relying on any S→C packet to tell a player *why* something was refused — and before
assuming an existing one already does.
**Trap:** `ClientNetworkHandler` raises `VoxelCapWarningReceived` for every `VoxelCapWarningPacket`, and
**nothing in the tree has ever subscribed to it.** The whole codebase holds three occurrences: the
declaration, the invoke, and a comment saying it is "deliberately left to the HUD/tool". The server sends
that packet from **nine** call sites; two of them also send chat text, so placements explain themselves —
the other seven do not, and one of those seven is `HandleNonSuccessToggle`, which alone serves a dozen
operations. The player's guide silently springs back and they are told nothing.
**It hid because a neighbouring path works.** `LockStateChanged` is declared eleven lines above, named in
the same comment, and has three real subscribers. And `DraftManager.TryComplete` pre-checks the per-guide
cap CLIENT-side, so the common case — drafting something too big — is refused locally and never needs the
event. What has no pre-check is the cumulative-player cap, the world-total cap, and **every edit**.
**Do:** when you add a rejection packet, grep for a subscriber before believing the loop is closed. An
event with no listener does not throw, does not log, and does not fail a build.
**Found:** Session 36, by verifying a symptom claim rather than the code path. Detail:
`dev/sessions/SESSION_36.md` §3.
**FIXED v0.4.34.** All nine sites go through one `SendCapRefusal`, which names the cap server-side (the
packet cannot — it carries a count and a limit and never says *which*), and the event now drives a HUD
flash. ⚠️ **The throttle it needed was itself a trap:** applied to placement as well as dragging, it
silenced the second of two deliberate placements inside four seconds. Corrected v0.4.37 — throttle the
continuous paths, never the one-shot ones. `SESSION_37.md` §2.

### G28 — The three voxel caps are NOT symmetrical. Only two allow a shrink.
**Trigger:** before touching cap arithmetic in `GuideManager` — `WouldExceedCaps`, `CountForCaps`,
`WouldExceedPlayerTotal`.
**Trap:** the per-guide check is growth-only (`&& newCount > currentForId`) and `WouldExceedPlayerTotal` is
growth-only (`&& projected > currentTotal`). **The world-total check is neither.** So when
`_totalVoxels` already exceeds `totalVoxelCap`, every mutation of every guide is refused — including the
ones that would reduce the total. `CountForCaps` compounds it: it lacks the `if (playerTotal <=
playerTotalCap)` guard its per-player sibling has, so the counting limit collapses to 0 and `CountUpTo`
returns the `Exceeded(0)` sentinel — **1** — which is the number the rejection then reports.
This contradicts `ApplyCaps`'s own documented promise that over-cap guides "keep existing and keep
rendering; they simply cannot grow".
**Why it has never been hit:** `perGuideVoxelCap` (500,000) and `perPlayerTotalVoxelCap` (1,000,000) are on
by default and are the two correct ones. `totalVoxelCap` defaults to **0 = unlimited**, so the check is
skipped entirely until an admin sets it — one row in the settings panel.
**Do:** keep the three symmetrical. A cap is a budget, and a budget that forbids shrinking is not one.
**Found:** Session 36.
**FIXED v0.4.34 — and it took BOTH clauses, not the one the finding named.** ⚠️ Fixing only
`WouldExceedCaps` would have been actively worse: with `CountForCaps` still clamping the scan limit to zero,
the check would then have *accepted* the `Exceeded(0)` sentinel — 1 — as the guide's real voxel count and
written it into the running totals. **When two functions share a cap rule, they share its exceptions.**
`SESSION_37.md` §3.

### G29 — Removing a per-player pending entry BY KEY after async work can delete someone else's newer entry.
**Trigger:** before completing or cancelling any queued per-player job that outlives the request that made
it — the immense create and sculpt lanes, and anything added beside them.
**Trap:** `CompleteActiveImmenseSculpt` does `_pendingImmenseSculpts.Remove(completed.PlayerUid)` with no
identity check. Cancel a sculpt (an ordinary right-click) and the entry is dropped while the worker is still
running; grab and reshape again and a NEW entry is created; the old worker then finishes and removes the new
one. The second sculpt still runs and commits, but with no entry it is invisible to `OnRelease` and
`OnCancelGrab`, so the release path frees the lock and its tick throws the work away. The reshape snaps back
with no message. `CompleteActiveImmenseCreate` has the identical omission against
`_playersWithPendingImmenseCreate`.
**Do:** check identity before removing — `if (map.TryGetValue(uid, out var cur) && ReferenceEquals(cur,
completed))`. **The correct pattern is already in this repo:**
`GuideRenderer.FinishSettledShellMaterialization` does exactly that, and is the thing to copy.
**Found:** Session 36.
**FIXED v0.4.36.** The sculpt lane checks reference identity. The create lane could not — its key lives in a
`HashSet<string>`, which has no identity to check — so it asks instead whether the queue still holds a live
entry for that player. **A set keyed by player alone cannot answer "is this still mine"; something else has
to.** The same revision made cancellation actually reach the worker (`volatile`, checked at each seam);
before it, cancelling stopped nothing. `SESSION_37.md` §5.

### G30 — `BlockOccupancy` is LOCK-FREE. Three comments still say it locks.
**Trigger:** before calling `BlockOccupancy` from a new thread, or reasoning about whether a call site is
safe.
**Trap:** the class remarks say "THREAD SAFE, by one lock around the cache" and describe the cost of
"millions of acquisitions"; `GuideMeshBuilder.cs` says "`BlockOccupancy` locks for this";
`GuideRenderer.cs` says "(BlockOccupancy locks)". **The lock was removed in v0.3.84** and replaced by a
`ConcurrentDictionary` — only the field comment records it. The overhaul made source the authority those
documents defer to, so a wrong comment is now the thing a reader is told to trust (`TODO` A13).
**And it is not fully safe.** `GetOrBuild`'s `TryAdd` loser reads `_blocks[key]`, assuming the winner's entry
is still present — but `Invalidate` can have removed it from the main thread between the two, so the
indexer throws `KeyNotFoundException` on a mesh worker. `GetOrAdd` closes it.
**Do:** fix the comments with the code, not separately. Both halves are one trap: the comment is why the
next reader will not look.
**Found:** Session 36.
**FIXED v0.4.36 — but not with `GetOrAdd`.** The loser now returns the entry it just built itself: same
answer, and no second dictionary read to race against at all. If a newer world state has since replaced it,
one mesh build in slightly stale colours is nothing beside an abandoned materialization and a visible
rebuild hitch. **The cheapest fix for a read-after-write race is often not to read.**
⚠️ The class remarks were corrected with it; the two *other* wrong comments (`GuideMeshBuilder.cs`,
`GuideRenderer.cs`) are part of `TODO` A13's sweep and are still open. `SESSION_37.md` §5.

### G31 — A scan guard bounds the shape's SIZE. Nothing bounds its POSITION.
**Trigger:** before touching any volume voxel counter, its scan guard, or anything that turns a world
coordinate into an `int` cell index — and before adding a new shape.
**Trap:** every volume scanner checks that the shape is not too *big* to scan, then walks
`for (int i = AlignDown(lo, scale); i <= AlignDown(hi, scale); i += scale)` over **absolute** cell indices.
`AlignDown` is `(int)Math.Floor(world * 16.0 / scale) * scale` — an unchecked double→int conversion and an
unchecked multiply. **No packet boundary validates that a coordinate is finite or inside the world**, so a
one-block shape at a crafted coordinate passes every size guard and enters a loop whose upper bound sits
next to `int.MaxValue`; `i += scale` wraps negative, the condition stays true, and it never terminates. The
`count > stopAfter` escape never fires either, because the wrapped cells fail the range test before
anything is counted. This runs **synchronously on the server tick thread** —
`TryQueueImmenseCreate` → `CountUpTo` — from one create request by any connected client.
**`CylinderShape` has the BETTER guard and it does not help.** It measures the real scan box in `long`,
after an earlier over-counting bug, and still misses this: the overflow is in the absolute value, not the
span. **A better guard of the wrong quantity is still the wrong guard.**
**Do:** validate finite and world-bounded coordinates **at the packet boundary** — that is the cheap fix and
it closes every shape at once — and separately move the scans to `long` or count-based iteration.
**Added after the entry was first written:** the wire is **not the only untrusted coordinate source**, and
the other two are not adversarial at all. `GuideManager.Load()` → `LoadPayload` → `ExactVoxelCount` reaches
the same loops from **the private-guide file** (`Layout/ClientOnlyGuides/*.json`, hand-editable — hangs the
player's own client on world load) and from **the world save blob** (hangs the server at start). So the
validation belongs in **`RestoreGuide` and `LoadPayload` as well as at the packet boundary**, and this trap
has a corruption-robustness half that stands whatever a project decides about hostile clients.
**World size does not close it.** The mod's own code assumes ±33.5M blocks is "far beyond any world"
(`BlockOccupancy.Key`) and the dangerous coordinate is ~134M — but the number arrives in a packet or a file,
never from the world, and nothing compares it to the map. Ask the engine for the real bounds; the mod never
has (`WorldManager` is used only for save-game data).
**Found:** Session 36, by an independent review, re-verified there over three passes after that session
first dismissed it. Detail: `dev/sessions/SESSION_36.md` §6 and §6.1.

**VALIDATION FIXED v0.4.35.** `src/Guide/GuideBounds.cs` — finite, and inside the map plus 4,096 blocks of
slack, with a hard ±33,554,432 backstop that applies before the engine has reported anything. Called at the
packet boundary **and** `RestoreGuide` **and** `LoadPayload`, so all three untrusted sources are closed. A
guide that fails on load is dropped with a logged warning.

**REPRODUCED 2026-08-01** (Session 37 §4.1), which corrects three things this entry asserted:

| | |
|---|---|
| The hang | **Real. Seven of eight volume shapes never return** at 2^27 = 134,217,728 — exactly where `world * 16` reaches `int.MaxValue + 1`. Correct one step below, dead on the boundary |
| ⚠️ `BoxShape` | **Does NOT hang.** It returns 32 — a nonsense count for a four-block box, but it terminates. It was recorded above as traced end to end and confirmed. **A trace that predicts a behaviour is not an observation of it** |
| ⚠️ Sphere / Dome | **Hang. They do not throw.** The `checked`-multiply prediction was wrong |
| Cone, TaperedCylinder, both prisms | Inferred; now **confirmed** hanging |
| `HardExtent` | **Verified safe with a 4× margin** — every shape answers correctly there |

**Still open:** the `long`/count-based loop rewrite, now deferred on *evidence* rather than inference. It is
unreachable through any validated entry point, so it is defence in depth. The harness is a throwaway kept
outside the repo; §4.1 records how to rebuild it.

### G32 — "The client only ever does X" is not an invariant.
**Trigger:** before relying on client behaviour to bound anything the server allocates, holds or enforces.
**Trap:** `GuideLockManager` enforces one holder **per guide** and leaves the per-**player** limit to a
parenthetical in its own remarks — *"A single player holding locks on two guides at once is not prevented
here (the client only ever grabs one at a time)"*. `OnGrab` never releases the previous lock, so a modified
client can lock every guide in the world and hold them until it disconnects. Meanwhile `DragFor` replaces
the player's single drag session, so the two halves already disagree about how many guides one player can be
editing. The admin lock-override covers dispel and the toggles but **explicitly not geometry edits**, so the
remedy is partial.
**The same shape appears elsewhere:** unbounded edit arrays and no request rate anywhere
(`TODO` A14.9), and an import endpoint that trusts a request it never verifies was sent (A14.8).
**Do:** when a comment says the client only does X, treat that as the thing to enforce server-side, not as
a reason not to. Related: **G27**, where one side of the wire relies on a listener the other never had.
**Found:** Session 36, by an independent review.
**FIXED v0.4.36.** `ReleaseOtherLocksForPlayer` enforces one guide per player, called on every grab.
⚠️ **One exemption, and it is not a loophole:** a player's in-flight immense reshape keeps its lock. That
lock is held by work already running and ends by itself; yanking it would abandon a reshape the player
legitimately started and already released. The unbounded arrays and missing rate limits named below were
fixed across v0.4.36 and v0.4.38. `SESSION_37.md` §6.

### G33 — `long.MinValue` is not a safe "never" sentinel for a timestamp. It overflows the subtraction.
**Trigger:** before seeding any "when did this last happen" field with a value meaning *never*, and before
writing `now - lastX < window`.
**Trap:** `long.MinValue` reads as the obvious "infinitely long ago", so *"has this happened in the last
2.5 seconds"* looks trivially false. It is the opposite. `ElapsedMilliseconds - long.MinValue` **overflows
`long` and wraps to a large NEGATIVE number**, which is less than any window, so the test reads **true** —
forever, until the field is first written for real.
**What it cost:** `GuideHud`'s cap row read `REFUSED — over cap` from the moment the HUD opened, in
**v0.4.34 through v0.4.38** — five shipped versions. It builds clean, it is one line, and it is invisible to
anything but running it.
**Do:** use **0** and test for it explicitly (`_lastX > 0 && now - _lastX < window`). `ElapsedMilliseconds`
only ever counts up from zero, so zero is unambiguous and cannot overflow anything.
**Found:** Session 37, by reviewing its own diff — not by any compiler, and not by five rounds of shipping.
`dev/sessions/SESSION_37.md` §9.

### G34 — A shape that fails its own frame check reports ZERO voxels, and zero passes every cap.
**Trigger:** before adding a cap, a budget or a voxel-count check — and before assuming the count reaching
one is a real measurement.
**Trap:** every shape's counter returns **0** when its frame check fails — `BoxShape`'s `MinSide`, and the
same idiom in its siblings — with an empty voxel set to match. Cap checks are all *upper* bounds, so **zero
satisfies all of them**: the per-guide cap, the creator total, the world total, and `HardVoxelCeiling`. The
guide is therefore accepted, persisted, listed at 0 voxels, drawn as nothing, and **no message is sent**,
because from the server's point of view nothing was refused.
⚠️ **This is not an exotic input.** Place a guide with the two clicks too close together. The human hit it
in ordinary play inside a minute, having set a 5,000 per-guide cap and expecting the cap to be what failed.
⚠️ **It is also A14.8's stated hostile-client vector, reached by accident** — the second review filed
"degenerate geometry costs zero voxels, so it evades the voxel budget entirely" as something an attacker
would craft.
**Do:** treat zero as its own rejection, not as a small number. `GuideOpStatus.RejectedEmpty`, enforced at
all three creation entry points. A guide made of nothing is not a guide.
**Found:** Session 37, in play, by the human. Neither code review found it. `SESSION_37.md` §7.

### G35 — Never rate-limit the packet that RELEASES a resource.
**Trigger:** before adding any throttle, budget or drop rule to a message handler.
**Trap:** a rate limiter drops packets. Applied indiscriminately it drops the ones that *free* things —
`GuideReleasePacket`, `GuideCancelGrabPacket` — and a dropped release leaves the player holding an edit lock
nobody else can take until they disconnect. **The limiter then manufactures the exact defect it shares a
revision with:** v0.4.36 fixed lock hoarding (G32) and v0.4.38's first draft of the rate limit re-created it.
**Do:** split handlers by what they do to resources, not by how expensive they look. Acquire, mutate and
broadcast pay; **release and cancel are exempt.** They are also self-limiting — releasing a guide you do not
hold is a dictionary lookup and nothing else.
**Do also:** make a limiter **loud**. A silently dropped edit reads to a player as "the mod ignored me" and
is miserable to diagnose, so ours logs when it trips. If that line ever appears in ordinary play, the number
is wrong, not the player.
**Found:** Session 37, by reviewing its own diff before shipping. `SESSION_37.md` §9.

### G36 — A "work is already queued" flag must never outlive its callback.
**Trigger:** before touching any debounce, coalescing guard or deferred-recompose scheduler — anywhere a
boolean means "something is already scheduled, so do nothing".
**Trap:** the guard's whole job is to make later callers cheap no-ops. If its callback can ever return
early — a moved deadline, a changed generation, a stale-check — **without either clearing the flag or
scheduling another callback**, the flag latches true with nothing in flight, and from that instant every
caller takes the do-nothing path. Nothing throws and nothing logs.
**On a GUI it is invisible until a human clicks.** `GuidePlayersDialog` went dead after its first click in
v0.4.40: tabs stayed pressed in because the redraw that repaints them never ran, and selecting a player
changed state and not pixels.
**Do:** make the clear unconditional — the shipped guard sets the flag, registers one callback, and that
callback *always* clears it, with an `IsOpened()` check to neutralise a late fire. If a deadline genuinely
has to move, the early-return branch must schedule the remainder; it may never simply return.
**Do also:** weigh what the optimisation buys. This one was chasing a third of a second on one uncommon
interleaving, inside a panel that already worked. See **R11**.
**Found:** Session 38, in play by the human. `SESSION_38.md` §3.

### G37 — "I cannot see it" cached as "there is nothing there" freezes forever.
**Trigger:** before caching the result of any world read that can fail because data is not resident —
unloaded chunks above all.
**Trap:** an unloaded chunk reads as air. Answering "empty" for the build in hand is correct; **writing it
into a cache is not**, because it is a fact about what was loaded, not about the world. It only stays safe
if something invalidates the entry when the data arrives — and block-change invalidation does not, because
**a chunk load is not a block change**. `BlockOccupancy` did exactly this from v0.3.79 to v0.4.41: a guide
meshed while terrain streamed in recorded "no material anywhere" and kept it for the session, so the
chiselling highlight never lit after a world load and only a full cache clear brought it back.
**Do:** keep "cannot see" and "empty" as different values — `TryRead` returns false rather than an empty
entry — and use the answer without storing it.
**Do also:** something must ask again. The renderer already had `_deferredSurface` + `OnReprobeTick` for the
identical race on Surface decal sides; occupancy joined it (`_deferredOccupancy`) rather than growing a
second mechanism.
**Found:** Session 38, in play by the human. `SESSION_38.md` §4.

### G38 — Never bound a pinned enum with a hand-written member name.
**Trigger:** before writing any validity check of the form `value > (int)SomeEnum.LastOne`, in config
normalisation, wire validation or a parser.
**Trap:** it is correct the day it is written and silently wrong the day the enum grows.
`LayoutClientConfig.Normalize` used `> GuideShapeType.Sphere` from 0.1.20, when Sphere was the newest
shape; the whole 3D volume family was appended after it. Sphere is 7 and the enum reaches 14, so **seven of
fifteen shapes failed the check** and were reset to Arch. **Worse than ignored — overwritten:** the
normalised config is written straight back to disk on load, so the player's choice was destroyed every
launch, in silence.
**Do:** `Enum.IsDefined`, or a predicate that derives its own range. A little reflection on a once-per-load
path is worth a check that cannot go stale. A repo-wide sweep after the fix found no other instance.
**Found:** Session 38, by `TODO` A13's comment sweep — the bug was under a comment that listed the same
enum's values incompletely, which is the same drift wearing a different hat. `SESSION_38.md` §5.

### G39 — A save that re-serialises EVERYTHING must never run per mutation.
**Trigger:** before calling `GuideManager.Persist` from anywhere that is not a lifecycle point, and before
writing any "save the state" call inside an edit path.
**Trap:** `Persist()` serialises the **whole guide registry**, so its cost tracks **how much has ever been
built**, not what just changed. Called per mutation it looked fine forever, because a reshape drag sends
about ten updates a second and a test world holds a handful of guides. Measured at ~4 ms per megabyte:
**18 ms per drag update at 1,000 guides — a whole 20 ms server tick — and 55% of the server's entire main
thread at 3,000.** Invisible on the machine it was written on; crippling on a world two months old.
**Do:** mutations call `MarkDirty()`; the write happens at the world save, and at shutdown. **Never add a
periodic timer on the server** — human-set, `SESSION_39.md` §1: guide data is worth exactly what the world
it describes is worth, and a separate cadence only makes it more durable at a permanent cost.
⚠️ **AND THE SECOND HALF, which is the easier one to miss.** Deferring the write creates an obligation on
**every path that DROPS the owner**. `LocalGuideAuthority` is discarded by plain assignment (`_local = null`)
with no disposal — safe only while every mutation wrote immediately. `EndWorldSession`, `RemoveLocalOverlay`
and client shutdown all flush first now. A loader owes a write too: migration, re-stamping and backup
recovery all changed state that used to reach disk by accident of the unconditional write.
**Found:** Session 39, `TODO` A10.2. Fixed v0.4.45–v0.4.46. ⚠️ **Do not reach for per-guide storage** — see
**G42**. Detail: `SESSION_39.md` §1 and §9.
**The follow-on shipped in Session 40** (`TODO` A18, v0.4.50–v0.4.54): the one remaining flush no longer
serialises on the main thread — the tick deep-copies the registry and a worker does the text. `Persist()` is
still the synchronous whole-registry write and this entry still governs it; what changed is that the routine
world save no longer calls it. **The drop-path obligation above did NOT become a "wait for the worker"
obligation** — see **G44** for why supersession pays it instead, and **G43** for the trap that cost the most
in getting there.

### G40 — A cheap filter in front of a permission check must be certain about the WHOLE check.
**Trigger:** before short-circuiting, caching or pre-filtering **any** access, claim or privilege decision.
**Trap:** you verify the half you can see and assume the half you cannot. A bounding-box pre-filter for the
claim check tested `ILandClaimAPI.All` and skipped the exact check when nothing intersected — but
`TestAccess` answers with **seven** responses and only one comes from that list:
`Granted` · **`LandClaimed`** · `NoPrivilege` · `InSpectatorMode` · `InGuestMode` · `PlayerDead` ·
**`DeniedByMod`**. So *"no claim overlaps"* never meant *"permission would be granted"*: a player without
the build privilege, a dead player, or land protected by **another mod** would all have been waved through.
**The geometry had been verified across 5,400 configurations. The premise had never been checked at all.**
**Do:** enumerate every way the real check can say no, in the API docs, before writing anything in front of
it. If even one reason is unknowable from outside — `DeniedByMod` is, by construction — **there is no sound
filter**, and correctness-over-performance decides it.
**Also:** a safety bound that passes with **zero margin** has not really passed. The containment test's
first run put voxels exactly on the box's edge, which only holds if the other system treats that edge as
inside. Slack goes on last, after every union.
**Found:** Session 39, shipped v0.4.48 and withdrawn v0.4.49 — see **R12**. Detail: `SESSION_39.md` §3.

### G41 — A shape accessor may rebuild its whole geometry on every call.
**Trigger:** before calling `IGuideShape.GetPointAt`, or any similar-looking accessor, more than once in a
row — above all inside a loop.
**Trap:** it reads like a cheap lookup and is not. `ArchShape.GetPointAt` calls `BuildSpline()`, which
allocates two Lists and a `CatmullRomSpline` that deep-copies its points. `SampleBodyCells`'s filled path
runs up to **8,193** rules and called it once per rule, so counting one filled arch rebuilt the same
constant spline thousands of times — **1.55 ms and 3.2 MB for a small arch, ten times a second while
dragging**.
**Do:** hoist the geometry out of the loop and evaluate against it. ⚠️ **Keep the parameter types and
clamping identical when you do** — `RulePointAt` still takes a `float` because evaluating the caller's
double directly shifts sampled positions in the last bits, and this count feeds the cap check that
`IGuideShape.GetVoxelCount` requires to agree exactly with what renders.
**Verify by comparing builds, not by argument:** the pre-fix DLL was extracted from the shipped zip and run
beside the new one over 720 configurations across all fifteen shapes. Every count identical.
**Found:** Session 39, `TODO` A10.2. Fixed v0.4.47. Detail: `SESSION_39.md` §2.

### G42 — The world save is ONE blob, and `StoreData` can neither enumerate nor delete.
**Trigger:** before designing anything around how guides (or any mod data) are stored in the world save —
above all any scheme with more than one key.
**Trap:** `ISaveGame` looks like a key-value store and is not one at the storage layer. Established
2026-08-01 by reading a real `.vcdbs` and the API:
- The savegame is SQLite, and **all mod data lives in one row of one table** —
  `CREATE TABLE gamedata (savegameid integer PRIMARY KEY, data BLOB)`. Layout's keys were found
  **uncompressed** inside that blob, so `StoreData` is a dictionary serialised into a single blob.
- **The API is only `GetData(key)` and `StoreData(key, bytes)`.** No key enumeration. **No delete.**

**So:** ⚠️ **per-guide keys are not viable** — no enumeration means maintaining an index, and no delete means
a dispelled guide's key can only be blanked, never removed, so dead keys accumulate in the world save
forever. ⚠️ **Sharding into buckets buys nothing** — every bucket sits in the same blob and the whole row is
rewritten regardless. **There is no in-save route to finer write granularity at all**; the only way to get it
is to leave the world save, which costs rollback consistency (a restored world would no longer bring its
matching guides).
**Do:** treat the save as one blob that is rewritten whole. Optimise **when** and **on which thread** it is
built, not how it is keyed. `dev/plans/PLAN_BACKGROUND_SAVE.md`.
**Found:** Session 39 — **after** a whole plan (`TODO` A17, per-guide "index cards") had been written and
agreed on the assumption that the interface implied the implementation. It did not. The same session had
already shipped and withdrawn the claim pre-filter for the identical reason (**G40**, **R12**).

### G43 — "It returned nothing" can mean "there was nothing to do" OR "I tried and failed."
**Trigger:** before treating a null / false / empty return as success — above all from a `Begin*` or `Try*`
that can decline for more than one reason, and anywhere the caller then SKIPS a fallback because of it.
**Trap:** `GuideManager.BeginBackgroundPersist` returns null in three cases — nothing was owed, a pass is
already pending, or **the snapshot itself threw and the registry is still unwritten**. The scheduler read all
three as "this save is prepared" and skipped the synchronous write that covers an unprepared save. One of the
three is good news; the caller acted as though all of them were.
**And the mirror image, on the success path.** A serialisation that *faulted* still reported the save as
prepared. ⚠️ **The obvious fix there is wrong**: reusing "does the registry still owe anything?" would have
counted an edit arriving AFTER the snapshot as a failed pass — that edit is the intended staleness window,
not a failure — and forced an on-tick serialisation at nearly every save on a busy world, undoing the whole
feature. The right signal is *"did this pass store its bytes"*, not *"is the registry clean now"*.
**Do:** ask the state, not the return — `HasUnsavedChanges` exists for exactly this and is the same condition
`Persist` uses, so the two cannot drift. When a return value must carry an outcome, give it one; do not
overload absence.
**Found:** Session 40, in the second of three review passes — **after** a guard against this class of failure
had been added in the first. **G36** is the same family: this is a "nothing is owed" flag latching true with
work still outstanding. `SESSION_40.md` §6.

### G44 — `StoreData` reaches disk at the game's NEXT save. After the last one, there is no next.
**Trigger:** before deferring any `SaveGame.StoreData` call — to a worker, a timer, or anything later than
the `GameWorldSave` that prompted it.
**Trap:** `StoreData` writes nothing itself; it updates the in-memory savegame blob and the GAME flushes it
when it next saves (**G42** — one blob, rewritten whole). That is what makes preparing bytes early safe, and
it is exactly what makes preparing them late on the **final** save silently useless: nothing follows to carry
them, and a flush in `Dispose` lands in a blob already written to disk. Guide edits from the last stretch of
a session would vanish on a clean shutdown, with no error anywhere.
**Do:** detect the last save and write on the tick there — a stall at shutdown costs nobody anything. Check
it two ways: `IServerAPI.IsShuttingDown`, plus a flag set from
`sapi.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, …)`, which fires when shutdown BEGINS and so cannot
be late. ⚠️ **Do not rely on `Dispose` as the safety net for this** — it runs after the game has written.
**Do also:** the same shutdown flush is how the "wait for the worker" obligation is discharged without
waiting. It writes the LIVE registry, always newer than any snapshot in flight, and drops the job so its
completion discards its own older bytes. ⚠️ **The identity check that makes it discard is not optional** —
without it the completing job writes stale bytes over newer ones, a silent rollback. Same shape as **G29**.
**Found:** Session 40, tracing the shutdown path after the first build already worked. `SESSION_40.md` §6.

### G45 — Cancellation at stage boundaries does not cancel the expensive stage between them.
**Trigger:** before calling any long-running scan, materialisation, serialisation or allocation from a worker
that is described as cancellable.
**Trap:** the immense create/sculpt worker checked its volatile flag before and after `CountUpTo`, and
`BuildFootprint` checked it while collapsing voxels. Cancelling safely discarded the result, but the count
already running could not stop. Worse, `BuildFootprint` called ordinary `GetVoxelPositions` first, so the
entire exact voxel list was materialised before its first collapse-loop cancellation check ran.
**The first fix repeated the same mistake one level deeper.** Cancellation was threaded through every
threshold counter and its scan loops, then an adversarial review traced the next stage and found exact voxel
generation still outside the cancellable boundary. A flag at both ends of a call says nothing about the work
inside the call.
**Do:** trace every expensive substage and carry one cheap probe into the deepest repeated loop. Preserve the
ordinary API with an additive cancellable seam when other callers share the code. On cancellation, throw or
return an unmistakable abandoned state; **never return a partial count/list that can be read as exact, over-cap
or empty.** Keep the worker's existing identity checks too — stopping old work sooner does not make removal by
key safe (`G29`).
**Found and fixed v0.4.57, Session 41 §4.** All eight volume variants now interrupt threshold counting, exact
generation, large-volume fallback marching and marker claiming; footprint collapse retains its own checks.

### G46 — Validate request fields before an idempotent/no-op early return.
**Trigger:** before adding a “nothing changes, return success” fast path to a method that accepts values from a
packet, file, command or any other untrusted seam.
**Trap:** v0.4.55's transactional `TransformGuide` first decided whether rotation, mirror or translation would
change the guide, then returned success for a no-op, and only after that validated the mirror/rotation values.
A crafted `mirrorAxis = -2` therefore reported success whenever the rest of the action was inert. No geometry
changed, but the authority had accepted a value its contract explicitly rejected and a future caller could
legitimately attach effects to that false success.
**Do:** validate the request's domain first; only then decide whether its valid meaning is idempotent. This is
different from validating the resulting state — both are owed when raw input can be malformed.
**Found by the v0.4.55 adversarial harness; fixed v0.4.56, Session 41 §4.**

### G47 — Scope a consistency snapshot with bounds; never use those bounds as the permission answer.
**Trigger:** before snapshotting global spatial state to protect sliced work from changes, or before using a
bounding box to reduce land-claim work.
**Trap:** v0.4.58 correctly replaced the immense validator's claim-count surrogate with structural state, but
the first implementation copied and permission-tested every claim on every comparison. Claims wholly outside
the guide cannot affect an exact access result, so this was safe but needlessly global. The tempting shortcut
in the other direction is worse: v0.4.48 used a bounding box to skip `TestAccess` itself and bypassed player-
global and other-mod denial reasons (`R12`, `G40`).
**Do:** derive a 3D bound from the exact block footprint, including projection-adjacent cells; apply the
engine's minimum-inclusive/maximum-exclusive Cuboidi convention and internal Y coordinates; clip relevant
claim geometry to the bound so outside-only changes do not restart work. Use that snapshot only to decide
whether sliced validation became stale. Keep the exact per-block `TestAccess` pass unchanged.
**Cost boundary:** the installed API exposes `Claims.All` but no regional query, so every claim area's bounds
still receive a cheap intersection test. Distant claims receive no permission test, copy or comparison and
cannot trigger a restart. In the Session-41 focused 10,000-claim harness, this reduced snapshot capture from
2.50–3.10 ms / 4.28 MB to 0.10–0.14 ms / about 820 bytes with one relevant claim.
**Found and fixed v0.4.58–v0.4.59, Session 41 §5.**

### G48 — Never move the whole guide mesh to solve z-fighting.
**Trigger:** before changing `GuideRenderer.SetModelMatrix`, adding camera-relative guide displacement, or
trying to cure guide/block shimmer outside `GuideMeshBuilder`'s exposed-face clearance.
**Trap:** Layout once pulled every guide mesh 0.003 blocks toward the camera. The raw guide cells and Vintage
Story micro-blocks both use exact multiples of 1/16, but the later model translation moved the rendered guide
off that lattice. The error looked angle-dependent and encouraged ever-larger face inset values; 0.003 alone
was 4.8% of a scale-1 micro-block.
**Do:** keep the model matrix at the exact world translation. Use the uniform per-exposed-face outset only;
never add a second whole-mesh compensation. After the camera pull was removed in play, 0.0001 worked cleanly
and 0.0002 was selected as the small-buffer default (0.32% of a scale-1 micro-block).
**Found and confirmed in play v0.4.63–v0.4.64, Session 41 §8.**

### G49 — Profile-first Roundovers are identified by control-point roles, not `IsClosed`.
**Trigger:** before changing Roundover chain parsing, terminal control-point roles, `ShapeFactory`'s `closed`
argument, or `GuideData.IsClosed` handling.
**Trap:** the profile-first create request reuses `Closed = true` to select the two-profile constructor because
the shared packet has no dedicated encoding flag. The created geometry is still an open sweep, and
`GuideManager` persists closed state only for Free-Shape. New Roundovers therefore identify themselves later
by two terminal Primary control points; legacy Roundovers have one terminal Primary handle and must retain
their original constant-radius interpretation.
**Do:** preserve both terminal Primary roles and detect the representation from them when adopting saved/wire
control points. Treat `closed` only as the create-seam discriminator. Never rewrite old guides or infer the
representation from `GuideData.IsClosed`.
**Introduced v0.4.67, Session 42 §2.**

### G50 — Zero-voxel rejection applies to every recounting mutation, not only creation.
**Trigger:** after moving/replacing control points, committing isolated geometry, changing scale/fill, or any
other operation that recounts an existing guide.
**Trap:** zero passes every upper cap. Creation rejected empty guides, but ordinary and immense reshape paths
stored count zero as success. An invalid Roundover therefore disappeared while remaining in the registry,
instead of retaining its valid prior form.
**Do:** immediately after counting, reject `count <= 0` before cap/access checks or adoption. An in-place
mutation must restore its complete pre-edit snapshot; an isolated candidate must retain the live object.
Return `RejectedEmpty`, resynchronize optimistic mirrors, and give shape-appropriate recovery guidance.
**Found and fixed v0.4.72, Session 43 §1.**

### G51 — A client gesture rule is not an authority capability check.
**Trigger:** before accepting an insert/update/toggle packet whose operation is implemented only by some
shapes or modes.
**Trap:** the stock controller sent body inserts only for Arch and Free-Shape, but authority accepted the
packet for every shape. Parametric `InsertControlPoint` methods were intentionally no-ops; the manager then
picked an existing nearest point, reported success, and the server broadcast an insertion that its own shape
did not contain. Remote mirrors could diverge from authority.
**Do:** define the capability once in the shape catalog and enforce it in the manager as well as the network
handler. Reject before locks or expensive accessors, and resynchronize instead of broadcasting. Never infer
authority safety from what today's stock client happens to send (`G32`).
**Found and fixed v0.4.72, Session 43 §1.**

### G52 — Hover candidates and click authority must not describe different targets.
**Trigger:** before changing `GuideToolController.FindTarget`, its 33 Hz HUD caller, or exact click targeting.
**Trap:** a click required an exact rendered voxel, but the HUD still used fixed 0.10/0.18-block proximity
floors around the sampled curve. A scale-1 cell is only 0.0625 blocks wide, so the HUD named a guide while the
crosshair sat several cells into empty space. Running full exact voxel generation every 30 ms would fix the
picture by creating a worse immense-guide cost.
**Do:** keep the per-tick pass a cheap candidate, but derive its radius from the physical guide cell
(`sqrt(3)/2 × edge`) with no block-sized floor. Every mutating action retains exact rendered-cell confirmation.
**Found and confirmed in play v0.4.73, Session 43 §3.**

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

### R10 — "Gate the private-guide push on an outstanding server request." Proposed and rejected, v0.4.38.
**Do not require `ClientGuidePushRequestPacket` before accepting `ClientGuidePushPacket`.**

Session 36's second review filed it as a defect that *"nothing tracks whether the server ever sent
`ClientGuidePushRequestPacket`"*, and the obvious fix is to demand one. **It would break the shipped GUI.**
The settings page's **Publish Private Guides** button calls `SendPrivateGuidePush` directly, with no request
from the server, by design — and `ClientNetworkHandler` says so in as many words: the request packet is
*"the server's way of asking, never a permission token"*, because the handler re-validates client-only mode
and privilege on receipt regardless of how the push started.

The finding underneath it is real — **100 guides per packet and unlimited packets** — so it was answered with
a **per-player cooldown** instead, which bounds the same thing without inventing a permission the protocol
never had.

⚠️ **The wider lesson, and the reason this is here rather than in a session file:** the second review's
findings were re-verified against this tree before acting precisely because its line anchors were known to be
off. This one was correct about the *symptom* and wrong about the *remedy* — a category the re-verification
step had not been looking for. Check a proposed fix against the code it would change, not only the finding
against the code it describes.

### R11 — "Earliest request wins" in the Players dialog's redraw guard. Applied v0.4.40, reverted v0.4.41.
**Do not replace `GuidePlayersDialog.DeferRecompose`'s first-wins guard with a deadline scheme.** It has
been tried; it shipped a dead panel to the human.

The reasoning that was **right**: the panel batches redraws at 30 ms and the filter debounces at 350 ms, so
a row click made shortly after a keystroke was folded into the filter's later redraw — it waited out the
debounce *and* arrived carrying the filter's focus request, which is the one thing `_restoreFilterFocus`
exists to prevent. That really happens.

The reasoning that was **wrong**: that the fix needed a moving deadline. Pulling a deadline in requires a
second callback, because the one in flight is scheduled for the later time — and the new callback's
stand-down branch returned without clearing the pending flag, latching the dialog dead after one click
(**G36**).

**The focus half never needed any of it.** Clearing `_restoreFilterFocus` in each click path fixes the
behaviour that mattered and cannot latch anything; that is what shipped. What remains unfixed is up to a
third of a second of latency on one uncommon interleaving — **an acceptable price, and not worth a second
attempt at this.** If it is ever revisited, the scheme must reschedule the remainder rather than return.

**Detail:** `SESSION_38.md` §3.

### R12 — A bounding-box pre-filter in front of the claim check. Applied v0.4.48, reverted v0.4.49.
**Do not skip `GuideClaimAccessValidator`'s exact footprint check on the strength of a bounding box tested
against `ILandClaimAPI.All`.** It has been tried, it shipped, and it was a permissions hole.

The reasoning that was **right**, and still is: `SESSION_23.md` §3 rejected bounding-box validation because
*"bounding-box validation would wrongly reject hollow guides that merely surround one"* — a dome placed
around a claimed block has it inside the box while no voxel touches it. **That rejects the box as the
ANSWER.** As a **negative filter** the objection does not apply: a box that intersects a claim falls through
to the exact check, so the dome case is unaffected, and only guides nowhere near a claim take the shortcut.
That distinction is sound and should not be re-litigated.

The geometry was **right too, and verified**: 5,400 configurations — every shape, six sizes, five scales,
filled and hollow, three orientations, both projections — every voxel inside the box with four blocks to
spare, after a first run that passed with zero margin was tightened. Measured 1,800× cheaper than the work
it replaced, still ahead at 5,000 claims.

The reasoning that was **wrong**: that land claims are what `TestAccess` decides on. They are one of
**seven** reasons it can refuse, and `DeniedByMod` — any other mod, any position — cannot be enumerated from
outside at all. A player lacking the build privilege, a dead player, or another mod's protected area would
every one have been permitted. **The half that looked hard was proven exhaustively; the half that looked
obvious was never checked.** See **G40**.

**If it is ever revisited:** a single `TestAccess` probe settles the player-global reasons (privilege,
spectator, guest, dead) cheaply, and the box handles `LandClaimed` — but **`DeniedByMod` needs an answer
first**, and there may not be one. Do not start from the box; `TryConservativeFootprintRect` is retained,
unwired, precisely so that the geometry is not the reason to start.

**Detail:** `SESSION_39.md` §3. ⚠️ Related but separate: claim protection **cannot be tested from a
singleplayer world** — the host holds `controlserver` and Layout exempts it deliberately (§4).

### R13 — Roundover is profile-first, not Radius-field or block-probed. Tried v0.4.65–v0.4.66, replaced v0.4.67.
**Do not reintroduce either discarded control scheme as the default Roundover gesture.** Both were built and
evaluated in play.

The GUI Radius field made the parameter visible but left the spatial relationship indirect; the human found
the controls less intuitive. Automatic inside/outside-corner selection from nearby material looked simpler,
but partial and chiselled blocks made the inference less dependable than the player's intended cut.

The settled gesture records construction geometry directly: sharp corner, first profile endpoint, second
profile endpoint, then the unsnapped sweep path. It does not ask the player to click the floating arc midpoint,
and it does not remove precision through positional or angular snapping. The human reported this works “MUCH
better.” SHIFT material-side placement and the three-rail wireframe solve the exterior/interior visibility
problems without changing that explicit geometry.

**Detail:** `SESSION_42.md` §§1–3.

### R14 — Parametric body grabs do NOT snap to the nearest handle. Retired v0.4.72.
The former Session-8/9 decision treated a parametric outline click as intent to move whichever existing
handle was nearest. It made otherwise rigid shapes easy to reshape, but the player clicked one visible guide
cell and a different coloured control point moved. In the exact-input control scheme that is snapping, not
convenience.

Create-mode grab now requires the view ray to enter the rendered cell. Arch and Free-Shape can insert that
exact body cell because their geometry supports arbitrary points. A parametric body cell has no independent
control, so it does nothing; only an exact hit on one of its coloured marker cells grabs that handle. Do not
restore nearest-handle grab mapping as a fallback. Right-click locking retains its separate forgiving intent.

The HUD's candidate envelope was tightened in v0.4.73 to match this precision without regenerating immense
voxel sets every tick. The human reported the final result excellent.

**Detail:** `SESSION_43.md` §§2–3.

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
