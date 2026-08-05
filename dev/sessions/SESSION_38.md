# Session 38 — v0.4.40 → v0.4.43

**Branch:** `main`. **DataVersion 13. Protocol 24.** (Neither moved.) **11 source files** (none added).

> The session that closed `TODO` **A10.1** and **A13** — the two oldest open items — and shipped a
> regression into a working panel on the way. The regression is the most useful thing in this record.

---

## 1. The GUI layer, reviewed at last (`TODO` A10.1)

Session 36's adversarial review covered the authority, the wire and the renderer, and explicitly did not
read the GUI. That left roughly 8,000 lines unexamined — `GuideToolGui` (3,609), `GuideToolController`
(3,322) and `GuidePlayersDialog` (1,042) — with `GOTCHAS` G6/G13/G14/G16/G17/G18 all living inside them.
All three were read end to end this session.

**The six known traps are genuinely closed.** Custom elements allocate their `LoadedTexture` before use
(G6); every variable-length text block is measured rather than guessed (G13); the per-row confirmation
carries a row identity (G16); the roster and guide-list refresh handlers are still split (G17); the scroll
container still carries its true height inside a fixed-height scope (G18). Nothing had rotted.

**Four defects found**, none of them crashes:

1. **The Players list went blank when a filter narrowed a long roster.** The scroll offset survives a
   recompose deliberately, so a selection click keeps its place — but nothing checked it still fitted the
   list it was about to be applied to. Filter a roster of more than 13 down to two rows and the container
   was parked hundreds of pixels above the window. It read as "the filter matched nobody".
2. **The aim loop tested every guide in the world, 33 times a second.** `FindTarget` walked the whole
   mirror — a fingerprint over each guide's control points, a resample on any miss, and a ray test against
   up to 512 curve segments — though nothing past `MaxReach` (12 blocks) is targetable at all. The cost
   grew with the world rather than with what was in front of the player, which is the shape of thing that
   is fine on the machine it was written on and miserable on a server two months old.
3. **A greyed row could light a tile.** `RefreshSelectedGuideControls` relit rows from a remote update
   without asking whether the row had been composed inert — so a volume's ghosted Projection row could show
   a live-looking selection.
4. **A long player name ran through the numbers beside it**, drawn unclipped over the Guides column.

**Rotate was raised as a fifth and is not a defect.** The four rotate corners ignore the Move/Copy/Mirror
action row that every other pad tile obeys. The human confirmed that is intended — a rotation is its own
verb, not a kind of travel — and the reasoning is now written next to `OnRotateTile` so the next review
does not re-flag it.

## 2. The fixes (v0.4.40)

The scroll clamp, the broad-phase rejection, the inert-row guard and the name clip all shipped together,
plus a fifth change that should not have: see §3.

The broad-phase deserves a note because it is the one with a real failure mode. A guide is rejected when
the view ray misses its control-point bounding box, grown by the pick radius **plus a quarter of the
guide's largest dimension**. That padding is far looser than a Catmull-Rom spline's actual overshoot, and
deliberately so: rejecting nothing costs one pass over the points, which is cheaper than the fingerprint
hash it saves, while rejecting something visible would make a guide the player can see unclickable.

## 3. ⚠️ The regression — a pending-flag that outlived its callback (v0.4.41)

The fifth change in v0.4.40 rewrote `GuidePlayersDialog.DeferRecompose`. The panel batches redraws behind a
30 ms timer, and the filter debounces at 350 ms, so a plain "already pending, do nothing" guard made a row
click land *behind* a keystroke — it waited out the debounce, and the redraw it finally got still carried
the filter's claim on the keyboard. The rewrite made the **earliest** request win instead of the first.

Its callback returned early when it found the deadline had moved — **without clearing the pending flag and
without rescheduling**. One early return and the flag was latched true with nothing in flight; every later
request then took the "already pending" path and the dialog never redrew again.

The human's report is the exact signature: *"Clicking the top tabs worked once, then broke. The top tabs
would click in as a button, but wouldn't spring back, and they wouldn't do anything. I could no longer
select a player."* A tab stays pressed because the redraw that repaints the tab row never runs; `_tab` has
changed with nothing on screen to show it; selection changes state and not pixels.

**Reverted in v0.4.41** to the guard that had shipped since v0.4.26 and cannot deadlock — its one-shot
callback always clears the flag, and its `IsOpened()` check neutralises a late fire. The half of that fix
that mattered — clicks not stealing focus back to the filter box — needed none of the cleverness and is
untouched: every click path now clears `_restoreFilterFocus` explicitly.

**The latency being chased was a third of a second on one uncommon interleaving.** That is the whole
lesson: it was not worth the risk taken in a working panel, and the code now says so where the guard lives.

## 4. The chiselling highlight and the cached blind read (v0.4.42)

Human-reported: the highlights are dark after loading into a world and need a manual rescan.

Not a regression — this had been there since the feature shipped in v0.3.79. Two things were wrong
together:

**The cache remembered what it could not see.** `BlockOccupancy.Read` answered "empty" for a block whose
chunk was not loaded — reasonable, a mesh has to draw something — and then `GetOrBuild` wrote that answer
down like any other. Nothing invalidates a cache entry when a chunk *loads*; invalidation runs off block
*changes*. So a guide meshed while terrain was still streaming in recorded "no material anywhere" and kept
it for the session. `/layout built refresh` worked because it clears the whole cache.

**Nothing asked the guides to look again.** Fixed by extending machinery already in the file for the
identical race: Surface guides have long deferred to `OnReprobeTick` when their air-side probe hit unloaded
chunks, and that tick already does the cheap "has this guide's neighbourhood arrived" check every 500 ms.
Occupancy now defers the same way, through the same tick, on the same test — `_deferredOccupancy`.

`TryRead` replaced `Read` so "cannot see" and "empty" are different return values rather than the same one.

## 5. `TODO` A13's comment sweep — and the bug under it (v0.4.43)

The sweep was meant to be comment-only. It found a live defect.

**Seven of the fifteen shapes were never remembered between sessions.**
`LayoutClientConfig.Normalize()` validated the saved shape with a hand-written upper bound of
`GuideShapeType.Sphere` — written at 0.1.20, when Sphere was the newest shape, and never moved as the whole
volume family was added after it. Sphere is 7; the enum runs to 14. Dome, Cylinder, Cone, Box, Tapered
Cylinder, Polygonal Prism and Tapered Polygonal Prism all failed the check and were reset to Arch.

And because the normalised config is written straight back to disk on load, it did not merely ignore the
preference — it **destroyed** it, every launch, silently. Now checked with `Enum.IsDefined`, which cannot go
stale when a shape is added. A sweep of the whole source found no other hand-written enum bound.

**Nine wrong statements corrected**, all in places `ARCHITECTURE.md` tells readers to trust as
authoritative:

- `LayoutServerConfig` claimed nothing reads it after startup and edits need a restart. Six settings have
  been live-editable from the Admin panel since v0.4.16. Now names which six, and which genuinely still
  need the file plus a restart, and why those were deliberately kept out of the GUI.
- The occupancy colours were documented as **static**, with live updates "the next stage" — three sessions
  after live updates shipped in the same file. Corrected in three places.
- **`/layout occupancy refresh` does not exist.** The command is `/layout built refresh`; `occupancy` is a
  hidden single-block diagnostic. Named wrong in three files, one of them written earlier the same day.
- The chalk / lock-override retirement versions were **swapped** between `PacketTypes.cs` and
  `GuideToolGui.cs`. `WIRE_HISTORY.md` settles it: slot 6 chalk v0.4.20, slot 7 lock-override v0.4.17.
- `DefaultShape` was documented as "0 = Arch, 1 = Ellipse" — the same drift that caused the bug above. It
  now points at the enum instead of re-listing values.
- `ProtocolVersion` said "there is only one version". It is 24.
- `ProjectionMode.Surface` said "thin tiles"; the builder's tile path is dormant and it renders as
  paper-thin slabs.

Each correction states what it used to claim, so the drift is visible rather than just the fix.

## 6. Verifying the sweep — which caught one of its own

Asked to verify, a pass over the session's own changes found **an error committed while doing the sweep**:
two comments asserted "stage 3 shipped v0.3.81–v0.3.84" without checking. `CHANGELOG` says live colours
arrived in 0.3.81–0.3.82 and the per-batch rebuild that stopped them stuttering in 0.3.83–0.3.85. Both
corrected.

Also confirmed in that pass: the `TryRead`/`ReadLoaded` split carries no stale coordinate reference; the
`_deferredOccupancy` set is added, drained and cleared on every path including dispose and guide removal;
no collection is mutated while enumerated; the deferral is reachable at world load (`OnGuidesBulkSynced` →
`RebuildAll` → `RebuildGuide`); `Enum.IsDefined` is used with the correct overload for an int-backed enum;
and no file took encoding damage (`GOTCHAS` G26 — the editor was used throughout, never a PowerShell
round-trip).

---

## Delivered

- **v0.4.40** — GUI review fixes: Players-list scroll clamp; broad-phase rejection in the aim loop; inert
  rows no longer relit from remote updates; player names clipped to their column. Rotate's independence
  from the action row documented as intended.
- **v0.4.41** — reverted v0.4.40's redraw-scheduler rewrite, which latched the Players dialog dead after
  its first click. The focus half of that fix retained.
- **v0.4.42** — the chiselling highlight lights itself after a world load: a read against an unloaded chunk
  is no longer cached, and guides meshed against absent terrain re-probe as it arrives.
  `/layout built refresh` now reports guides still waiting on terrain.
- **v0.4.43** — the tool remembers every shape again, not just the eight below Sphere. Plus `TODO` A13's
  comment sweep (comment-only; no behaviour).

## Decisions

- **Reverted "earliest request wins" in `DeferRecompose`** (§3). The original reasoning — a click should not
  wait out the filter's debounce — was correct and the cost was real but trivial. What was wrong was taking
  it in a working panel with a scheme that could leave state latched. Simplicity won on a control surface
  where a deadlock is invisible until a human tries to click something. **`GOTCHAS` R11.**
- **Rejected a "saw an unloaded chunk" latch in `BlockOccupancy`.** It was written, then removed before
  shipping. The probe runs on materialization workers, so the latch cannot say *which* guide was reading
  when it tripped — and the only thing to do with an unattributable signal is re-probe everything, which
  never settles: a guide whose anchor is loaded but whose far end is not would set it on every rebuild and
  so queue its own next rebuild forever. Per-guide attribution at the anchor is used instead. An
  unattributable signal is worse than none, because it invites a fix that cannot terminate.
- **The occupancy re-probe tests the guide's ANCHOR chunk**, not its full extent. Cheap, attributable, and
  identical to the test the tick already re-runs for Surface. The known cost is in *Flagged* below.
- **Rotate tiles deliberately ignore the Move/Copy/Mirror row** (human-confirmed, §1).
- **`Enum.IsDefined` over a hand-written bound** for shape validity (§5) — a check that maintains itself is
  worth a little reflection on a once-per-load path.

## Traps

⚠️ **A pending-flag must never outlive its callback.** Any "work is already queued" guard whose callback can
return early without either clearing the flag or rescheduling will latch, and every later request then
takes the fast path and does nothing. On a GUI this is invisible until a human clicks something and nothing
happens. → `GOTCHAS` **G36**, and **R11** for the reversal.

⚠️ **"I cannot see it" cached as "there is nothing there" freezes forever.** A read against an unloaded
chunk is not a fact about the world. Caching it is only safe if something invalidates it when the chunk
arrives — and block-change invalidation does not, because a chunk load is not a block change. → `GOTCHAS`
**G37**.

⚠️ **A hand-written upper bound on a pinned enum goes stale in silence.** `> GuideShapeType.Sphere` was
correct when written and wrong for seven shapes by the time anyone looked, and the failure mode was a
config value quietly overwritten on load rather than an error. Validate against the enum, never against a
member someone has to remember to move. → `GOTCHAS` **G38**.

## Flagged and unverified

**Judgement calls awaiting review** (indexed in `dev/TODO.md`):

1. **The broad-phase padding** — pick radius plus a quarter of the guide's largest dimension. Chosen to be
   far looser than any real spline overshoot. If a guide you can plainly see ever refuses to be clicked —
   a long deeply-curved arch, or a Free-Shape with corners spread wide — this is why, and the fix is more
   padding.
2. **`OccupancyAwaitingChunks` is surfaced only in `/layout built refresh`'s reply.** It could equally be a
   HUD line; it is not, on the grounds that it is transient and self-correcting.

**Claims not tested** (→ `STATUS.md`):

- **Whether the occupancy anchor test catches the real load ordering.** The deferral fires when a guide's
  anchor chunk is absent *at mesh time*. If the anchor's chunk happens to be resident while terrain further
  along the guide is not, that guide will not defer and part of it stays uncoloured until a block change
  nearby or a manual refresh. Whether that window exists in practice cannot be settled by reading — only by
  loading into a world. If highlights still come up dark on v0.4.42, this is the reason, and the fix is to
  widen the test past the anchor.
- **v0.4.40's four GUI fixes were superseded by v0.4.41 before a playtest of v0.4.40 completed.** The
  surviving four are in v0.4.41 onward and are unverified in play as a set.
