# Session 36 — no version shipped

**Branch:** `beta`. **DataVersion 13. Protocol 24.** **83 source files** (none added). **No code changed.**

> The adversarial code review briefed in `dev/plans/PLAN_CODE_REVIEW.md` (`TODO` A10, item 1), run in a
> fresh session as its §1 requires. Read-only: no fix was applied, per the brief's §6.5 — "do not fix while
> reviewing… fixing the first hides the shape of the rest."

---

## 1. How the review was run

The brief's §1 asks for whole files rather than greps, on the grounds that fragments answer "does this match
the document?" and not "what happens when two of these run at once?". That was followed.

**Read whole:** `ServerNetworkHandler` (3.6k lines), `GuideManager` (2.1k), `GuideRenderer` (5.3k),
`PacketTypes` (1.4k), `BlockOccupancy`, `GuidePalette`, `RectangleShape`, `ShapeFactory`, `DivisionMarks`,
`LayoutServerConfig`. **Read in part:** `BoxShape`, `PolygonShape`, `GuideMeshBuilder` (palette section),
`LocalGuideAuthority` (construction), `DraftManager` (cap pre-check), `LayoutModSystem` (composition root),
`GuideHud` (cap readout), `LayoutClientConfig` (occupancy default).

**Not read at all:** `GuideToolController`, `GuideToolGui` (beyond two greps), `ClientNetworkHandler`
(beyond the cap-warning path), `GuidePlayersDialog`, `UndoManager` and the `Undo/Commands/`, most individual
shape files. **The findings below are therefore weighted toward authority, wire and renderer**, and the GUI
is essentially unreviewed. That is a coverage gap, not a clean bill.

## 2. What was found — ranked

Full failure scenarios in `TODO.md` A14. In brief:

1. **Cap refusals on edits are completely silent.** `ClientNetworkHandler.VoxelCapWarningReceived` is
   declared and raised, and **nothing subscribes to it** — the whole tree has three occurrences: declaration,
   invoke, and a comment. The server sends `VoxelCapWarningPacket` from **nine** call sites; two also send
   chat text, seven do not, and the seventh is `HandleNonSuccessToggle`, which alone serves a dozen
   operations. The player's guide silently springs back with no explanation.
2. **The world-total voxel cap has no shrink escape.** `WouldExceedCaps`'s per-guide check is growth-only
   and `WouldExceedPlayerTotal` is growth-only; the world-total check is neither. Lower `totalVoxelCap`
   below current usage and every edit in the world is refused — including the shrinking ones that would fix
   it. Compounds with (1): refused, and silently.
3. **An immense sculpt can be disowned and then silently discarded.** `CompleteActiveImmenseSculpt` removes
   `_pendingImmenseSculpts[uid]` with no identity check, so it can delete a *newer* pending sculpt.
   Right-click-cancel then re-grab is enough.
4. **`BlockOccupancy.GetOrBuild` can throw on a mesh worker thread** — the `TryAdd` loser reads
   `_blocks[key]`, which `Invalidate` can have removed.
5. **`_occupancyChangedBlocks` never clears** while a guide over the 3M batch ceiling sits permanently dirty.
6. Lower: sculpt count-limit waste, positional server cap construction, palette lanes not cancelled on a
   scheme swap, and the retired admin settings 6/7 printing a false confirmation.

## 3. The verification passes, and why they mattered

The human asked twice for the findings to be verified. Both passes changed the report, which is the argument
for asking.

**Pass one** found the largest defect in the whole review. Checking a *symptom claim* — I had written that a
refused edit shows the player a cap warning — meant asking who consumes `VoxelCapWarningPacket`. Nobody does.
The original claim was wrong in the direction that mattered: not "the player sees a confusing number" but
"the player sees nothing at all." It also upgraded finding (3), because re-reading `OnCancelGrab` showed the
trigger is an ordinary right-click, not an admin edge case.

**Pass two** corrected three numbers (nine send sites, not twelve), established that the retired-setting
defect is **unreachable from the shipped client**, and pinned the preconditions: occupancy colouring is
`false` by default, so findings (4) and (5) need it switched on, and `totalVoxelCap` is `0` by default, so
(2) needs an admin to set it.

**And it found why (1) survived.** `DraftManager.TryComplete` pre-checks the cap **client-side** and refuses
before sending, so the common case — drafting something too big — never reaches the server and never needs
the event. But that pre-check only knows `_perGuideVoxelCap`. The cumulative-player cap, the world-total cap,
and **every edit rejection** have no pre-check and depend entirely on the dead event. The mechanism looked
wired because a neighbouring one was — `LockStateChanged`, declared eleven lines above it and paired with it
in the same "deliberately left to the HUD/tool" comment, has three real subscribers.

That is the Session-33 signature the brief said to hunt (§2): **a path that exists, compiles, and does
nothing**, hidden behind a path that works.

## 4. What came back clean

Recorded because the brief asked, and because a verified negative is worth as much as a finding.

- **The Transform COPY path** — the brief's single most specific hypothesis (§4.2) — **does not hold.**
  `CopyGuide` routes through `RestoreGuide`, which applies guide-count caps, the hard ceiling, the per-guide
  cap, the creator-total cap **against the copier's uid**, the world total and claim access, and **recounts**
  rather than reusing `CachedVoxelCount`. G5 is honoured. All three of the brief's questions answer yes.
- **The §4.5 placeholder sweep is clean.** Every `{0}`-style placeholder in `src/` is inside a `Logger.*`
  call, a `string.Format`, or G11's own explanatory comment. No player-facing message carries one.
- **Admin privilege re-checks: no unreached handler.** Every entry point re-checks server-side.
  `LayoutAdminConfigPacket.CanEdit` is written and never read by the server.
- **The private-guide push import seam is hardened.** Scale validated, `Sides` clamped in the adopt
  constructors, `Divisions` clamped at 256, unknown shape types fall back to Arch, and the loop runs inside
  `WithPlayerVoxelCap`. **Publishing a private guide does not bypass caps** — checked specifically, given R1.

  > ⚠️ **CORRECTED LATER THE SAME DAY — see §7.** This is true only of the vector it was tested against
  > (oversized geometry evading the voxel caps). It is **not** true generally: the endpoint requires no
  > authorization at all, and a guide with degenerate geometry costs **zero** voxels, so it evades the voxel
  > budget entirely while both guide-count caps are unlimited by default. "Hardened against large guides"
  > was the finding; "hardened" was the overreach. Open as `TODO` A14.8.
- **Legacy encodings hold up (§4.3).** `RectangleShape` and `BoxShape` both branch on point count; insert is
  a no-op for both; `RemoveControlPoint` refuses anchors and Primaries, covering every stored point in both
  forms; spring-back replaces the list wholesale and re-adopts, so restoring a two-point form is valid.

**A suspicion withdrawn after tracing it:** `_releasedGrabFingerprint = _grabPoseFingerprint` in
`OnGuideAddedOrUpdated` looked like it recorded the wrong fingerprint. It does not — `RebuildGuide` on the
line above updates `_grabPoseFingerprint` to the authority's value first. Filing it would have cost a session.

## 5. `GOTCHAS` G4 may now be stale

G4 says the stale-wireframe control-flow trap is "fixed for the Move path only. **The general case is still
live.**" `TryStartSettledShellMaterialization` now carries a `mesh.ScaffoldFingerprint != fingerprint` guard
whose own comment describes and closes exactly that case, and every other early return in
`OnGuideAddedOrUpdated` is fingerprint-guarded or pose-matched.

**This is not asserted.** One path was traced, not all of them, and R7 is the standing cost of writing a
status claim on reasoning alone. It is filed as a verification item, not as a correction to G4.

## 6. A second review, from another model — and what it caught that this one did not

The human supplied an independent code review of the same tree by a different AI, and asked for it to be
evaluated. **All six of its findings are valid**, and they are almost entirely disjoint from the six above.

**The two reviews used different threat models.** This one asked *"does the code do what it intends?"* and
found correctness defects. That one asked *"does the intent survive a hostile client?"* and found robustness
defects. Neither question subsumes the other, and running only one of them leaves half the surface unread.

Its findings, all re-verified against source here:

1. **[P0] Crafted coordinates hang the server.** No packet boundary validates that a coordinate is finite or
   inside the world. Every volume scan guard bounds the shape's **size** and never its **position**, so a
   one-block shape at a crafted coordinate passes the guard and enters a loop whose `int` bounds overflow.
   Traced and confirmed in `BoxShape` and `CylinderShape`. Reached synchronously on the server tick thread
   from a single create request. → `TODO` A14.7, `GOTCHAS` **G31**
2. **[P1] The push endpoint accepts unrequested uploads.** → A14.8; corrects §4 above.
3. **[P1] No rate limiting anywhere.** Unbounded edit arrays, no per-player rate, and no-op toggles that
   still persist and broadcast. → A14.9
4. **[P1] One player can hoard locks on every guide.** → A14.10, `GOTCHAS` **G32**
5. **[P2] Cancelling an immense operation does not stop its worker.** → folded into A14.3
6. **[P2] Time-of-check/time-of-use in the tiled claim validation.** → A14.11

### 6.1 Where this session's own analysis was wrong

Worth recording precisely, because the same error was made three times in different places.

**I generalised from a sample of one, twice, in opposite directions.** Asked to evaluate finding 1, I traced
the *sphere* path, found it did not fail as described, and downgraded a correct P0 to "unverified mechanism".
On re-check, `BoxShape` fails exactly as described — a genuine non-terminating loop on the tick thread — and
`CylinderShape` too. Then, correcting that, I said the sphere path "does not fail this way"; it contains the
same loop and merely has a `checked` multiply that likely throws first. **Fails differently is not safe.**

**The same error produced the "hardened" verdict on the push seam** (§4, now annotated): true of the vector
tested, written as though general.

The lesson is not "check more carefully" — it is that **a negative finding needs its scope stated as
narrowly as the test that produced it.** "I tested X and it holds" is a different claim from "it is safe",
and only the first one was earned.

### 6.2 What the other review got wrong or left loose

Not much, and none of it changed a verdict:

- Its file paths point at a different working copy and its line anchors are off by 2, 2 and 22 against this
  tree. Re-locate before acting; the methods and reasoning are right.
- Its finding 5 cites "started with `CancellationToken.None`" as the evidence. That is true of the *client*
  draft and grab workers too, so it is not the discriminating fact — the real defect is that the immense
  worker bodies never **check** a token, while the client bodies call `ThrowIfCancellationRequested`.
- **It states no threat model.** Every finding but 1 and 6 assumes a modified client, which changes
  prioritisation a great deal, and it should have said so.
- It never engages with `GOTCHAS`. It happens not to re-litigate a settled decision, which is luck rather
  than method — its fix for finding 2 ("require `allowClientOnlyMode`") brushes against R1's reasoning about
  what caps are for, and needs care rather than direct application.

---

## Delivered

Nothing shipped. No code changed, so no version bump and no zip — per `CLAUDE.md`'s exception for a change
with no behaviour to test.

- **(no version)** — `TODO` A10 item 1 delivered: the adversarial code review was performed against v0.4.33.
  Findings filed to `TODO.md` A14 and `GOTCHAS.md` G27–G30.
- **(no version)** — an independent review by another model was evaluated, re-verified over three passes,
  and adopted in full. Six further findings to `TODO.md` A14.7–A14.11 and `GOTCHAS.md` G31–G32.

## Decisions

- **Report, do not fix.** The brief's §6.5 forbids fixing while reviewing, on the Session-33 precedent that
  several defects shared one cause and fixing the first hid the shape of the rest. Held to, even though
  finding (1) is a one-line wire-up.
- **Fix order changed once the second review landed: the P0 goes first.** A single unauthenticated packet
  that stops the server outranks everything else. The missing subscriber (A14.1) stays second — it is the
  smallest change left and it converts the remaining cap bugs from invisible to reportable.
- **Both reviews are one backlog, not competing ones.** They overlap on nothing. Treating either as "the"
  review would leave half the surface unread, and A14 is numbered as a single sequence for that reason.
- **The other model's findings were adopted on their merits, after independent re-verification** — not
  taken on trust, and not discounted for having come from elsewhere. Three of its six were re-checked
  against source line by line; the P0 was checked three times, because this session's reading of it changed
  twice.
- **THREAT MODEL SETTLED (human-set, 2026-07-31): Layout is a public mod, so a modified client IS in
  scope** — the hostile-client findings are real work and none is closed as "won't fix". **But the human
  judges the likelihood low, and that governs order rather than scope.** None can fire by accident; A14.7
  needs a deliberately crafted coordinate near 134 million, and a stray `NaN` cannot reach it because
  `(int)NaN` collapses to zero. So they queue behind the defects that occur in ordinary play, and A14.7's
  cheap half (boundary validation) is taken now while its expensive half (the loop rewrite) waits.
  **This closes the open question raised under "Flagged and unverified" below.**
- **A14.1's feedback design — chat first, HUD second, wire change deferred** (Claude's call, decide-and-flag).
  The deciding fact is that `VoxelCapWarningPacket` carries a count and a limit but **never says which cap
  fired**, so the client cannot name it and the message has to come from the server, reusing the four
  helpers the placement paths already use. Throttling is mandatory: a refused drag fires ~10×/second.
  Full text at `TODO` A14.1.
- **Six `GOTCHAS` entries, not one per finding.** G27–G32 each generalise past their own bug: a raised event
  with no listener, three caps that are not symmetrical, remove-by-key across an async boundary, a lock-free
  cache three comments claim is locked, a guard that bounds the wrong quantity, and a client convention
  relied on as a server invariant. The other eight findings are bugs to fix, not rules to remember, and
  stayed in `TODO`.
- **`PLAN_CODE_REVIEW.md` gets a status line and nothing else.** Its own header says it "should not need
  rewriting to be reused", and the findings belong in `TODO`/`GOTCHAS` by its §6.3–6.4. Only "NOT STARTED"
  became untrue.

## Traps

⚠️ **A packet the server sends is not feedback the player sees.** `VoxelCapWarningPacket` has been sent from
nine call sites since the caps existed, and the client event it raises has never had a subscriber. Nothing
throws, nothing logs, and the neighbouring event in the same file *is* wired, which is what made it look
done. → **G27**

⚠️ **The three voxel caps are not symmetrical.** Per-guide and per-player both allow a change that does not
grow the guide; the world total does not. The two that are enabled by default are the correct ones, which is
exactly why nobody has hit it. → **G28**

⚠️ **Removing a per-player pending entry by key, after async work completes, can delete somebody else's
newer entry.** `CompleteActiveImmenseSculpt` does; `FinishSettledShellMaterialization`, twenty files away,
does the same job correctly with `ReferenceEquals`. → **G29**

⚠️ **`BlockOccupancy` has been lock-free since v0.3.84 and three comments still say it locks** — including
its own class remarks, which describe the cost of "millions of acquisitions" that are no longer paid. One of
those comments is what a reader is told to trust when deciding whether a new call site is safe. → **G30**

⚠️ **Every volume scan guard bounds the shape's SIZE and none of them bounds its POSITION**, while the loops
they guard index absolute coordinates. `CylinderShape`'s is the *better* guard — it measures the real scan
box in `long`, after an earlier over-counting bug — and it does not help, because the overflow is in the
absolute value, not the span. A better guard of the wrong quantity is still the wrong guard. → **G31**

⚠️ **"The client only ever does X" is not an invariant.** `GuideLockManager` enforces a per-*guide*
constraint and leaves the per-*player* one to a parenthetical: "the client only ever grabs one at a time".
A modified client does not. Same species as G27 — one side of the wire relying on a convention held by the
other. → **G32**

⚠️ **A negative finding is only as broad as the test that produced it.** Twice this session a conclusion was
generalised from one traced path and was wrong both times — once dismissing a real P0, once clearing the
push seam. The remedy is not "read more carefully" but "state the scope you actually tested". See §6.1.

## Flagged and unverified

**Judgement calls awaiting review** — none. This session made no design choices; it only read.

**Claims not tested** (→ `STATUS.md` *Known unverified claims*):

1. **Whether `GOTCHAS` G4's general case is still live.** See §5. Needs either a full trace of
   `OnGuideAddedOrUpdated`'s early returns or a playtest that tries to reproduce a ghost at an old pose.
2. **How often finding (3)'s race actually fires.** The code defect is confirmed by reading; the window is
   "the worker is still counting when you re-grab", which only play can size.
3. **The GUI layer is unreviewed.** `GuideToolController`, `GuideToolGui` and `GuidePlayersDialog` were not
   read. `TODO` A10 item 1 is delivered for the authority, wire and renderer — **not for the UI.**
4. **A14.7's hang has not been reproduced.** It is confirmed by tracing `BoxShape` and `CylinderShape`, but
   nobody has placed a box at the crafted coordinate and watched a server stop. Four of the seven shape
   paths named in the original report are **inferred from a shared helper and idiom, not traced** — and the
   sphere/dome path contains the same loop but a `checked` multiply that probably throws first, so its
   failure mode differs and is not proven safe. **Reproduce once before writing the fix** (`GOTCHAS` G12);
   reading will not settle the remaining shapes.
5. ~~**The mod has no stated threat model.**~~ **ANSWERED the same day — see Decisions above.** Public mod,
   modified clients in scope, likelihood judged low; it governs order, not scope. No longer an open question,
   and deliberately **not** carried into `STATUS.md`'s unverified list.

⚠️ Nothing here says "not playtested" about shipped behaviour. No version shipped this session, so R7 does
not arise: there is nothing new to have played.
