# Session 40 — v0.4.50 → v0.4.54

**Branch:** `beta`. **DataVersion 13. Protocol 26.** **85 source files** (one added: `BackgroundGuidePersist.cs`).

`TODO` **A18 delivered** — guide serialisation no longer runs on the server's main thread. One session about
only this, as A18 required.

---

## 1. What was wrong, and how small it had become

Session 39 had already fixed the large half: before v0.4.45 every mutation re-serialised every guide in the
world, so a reshape drag did it ten times a second. What was left was one thing — **when the world saved,
converting the whole registry to JSON ran on the main thread**: 42 ms at 3,000 guides, 113 ms at 8,000,
against a 20 ms tick. A periodic hitch rather than a crisis, but pure waste, and there by accident of
history rather than by design.

The approach was settled in `dev/plans/PLAN_BACKGROUND_SAVE.md`: the main thread deep-copies the registry
(0.52 ms at 3,000 guides, ~80× cheaper than serialising), a worker turns the copy into bytes, and the result
goes back to `StoreData` on the tick. The plan left five questions open and marked itself "a starting point,
not a finished design".

**Four of the five were answered by reading code rather than deciding anything:**

- `DeepClone` already covers everything `Persist` writes — both mutable lists, and every other field is a
  value type or a string. Traced to the bottom in §5.
- `EnqueueMainThreadTask` is on `IEventAPI` and `IServerEventAPI` extends it — verified against the shipped
  `VintagestoryAPI.dll`, not assumed.
- `MarkDirty` is an unconditional `= true`, so an edit landing mid-serialisation simply re-arms it. No
  latch risk there (`GOTCHAS` **G36**) — the latch turned out to be somewhere else entirely (§4).
- The admin policies stay on the tick. A handful of records against the registry's megabytes; threading them
  would add a second set of lifecycle obligations to save nothing measurable.

## 2. The plan was wrong about one thing, and the human's design was better than the plan's

The plan assumed the background pass had to run **ahead of** the world save, because bytes handed over
during `GameWorldSave` are what that save writes. That is half the picture. `StoreData` writes nothing to
disk — it updates the in-memory savegame blob, and the *game* flushes it at its next save. So bytes handed
over late are not lost; they simply ride the following save. That turns the trigger question from a
correctness problem into a durability one.

Both of my first two proposals were then beaten by the human's own question: **"do we know when a world save
will happen? Can we time ours a few seconds before one?"**

The answer to the first half is no — the modding API exposes `GameWorldSave`, `SaveGameLoaded` and
`SaveGameCreated`, and no clock. The autosave period is not in `serverconfig.json` (every key was dumped and
checked) and not on the API; it is a constant inside the game's own `ServerSystemAutoSaveGame` in
`VintagestoryLib.dll`.

But the second half stands anyway, because **the period can be measured** from the gap between two saves.
That is better than reading a setting even if one existed: it assumes nothing about the game's default and
adapts on its own if that constant ever changes. It also does strictly less work than either of my proposals
— one pass per autosave rather than a polling cadence — while landing *closer* to the save, which is
strictly fresher.

**The human proposed the shipped design. Both of my alternatives were worse on both axes.**

## 3. The lead: measured, then made self-correcting

v0.4.50 shipped with a 10-second lead, which the human immediately questioned. The honest answer was that
10 s was caution, not arithmetic. The actual budget:

| Step | Cost |
|---|---|
| Deep-copy the registry (on the tick) | 0.5 ms at 3,000 guides |
| Serialise (worker) | 42 ms at 3,000 · 113 ms at 8,000 |
| Hand back on the next tick | 33 ms (`TickTime` 33.333 ms) |
| **Total** | **~80 ms at 3,000 · ~150 ms at 8,000** |

About a sixth of a second, worst case — the lead was ~70× that. It was never sized for the work; it was
sized for **uncertainty in predicting the save**, which cannot be measured from outside the game.

So v0.4.51 stopped guessing. The lead starts at **3 seconds** and **widens only when a cycle proves it was
not enough** (doubling, capped at 60 s, logged). This works because overshooting is cheap — a pass that
misses its save rides the next one — so the right move is to start where we want to be and let the machine
object. Widening only, never narrowing: a lead that hunted both ways would oscillate around the boundary.

**What the lead actually costs is not what it looked like.** An edit made *before* the pass is written by the
very next save. An edit made *inside* the lead window misses that save and waits for the following one — a
whole autosave period. So the lead is the **width of the exposed window, not the size of the exposure**. My
first summary to the human got this wrong and was corrected.

## 4. Three review passes, four defects — none of them in the threading

The human asked for verification three times. Each of the first two found real bugs; the third found none,
because it went looking somewhere the first two had not.

**Pass 1 — a silent latch, and a comment that lied.** `GuideManager` refuses to start a second background
pass while one is pending. If the hand-back to the main thread ever failed, that marker would never clear,
and — because the save handler no longer wrote synchronously in the normal case — **guide edits would stop
reaching disk for the rest of the server session, in silence.** Only shutdown would have rescued it. Same
shape as **G36**, wearing a reference instead of a bool, under a comment claiming the code avoided it.
Separately, that pass found the code did *not* do what its own comment said about the opening saves of a
session: only the first wrote synchronously, so anything built between save 1 and save 2 waited for save 3.

Both collapsed into **one rule**, which is now the single guarantee the class rests on: *if no background
pass prepared this save, write on the tick.* Its value is that it does not enumerate the ways preparation can
fail — it asks only whether preparation happened. Every narrower condition considered missed at least one
case.

**Pass 2 — a null with three meanings.** ⚠️ See §6, `GOTCHAS` **G43**. This one got through *after* the
guard in pass 1 had been added specifically to prevent its class of failure.

**Pass 3 — nothing new.** It verified the two assumptions everything rests on, neither of which had been
checked: that the snapshot is private all the way down (§5), and that nothing mutates a guide off the main
thread. Both hold.

**The pattern is worth recording: every defect was in the bookkeeping that decides whether a save was
prepared. None was in the threading.** The plan warned that threading is where this project has been burned
(G29, G30) and that warning was well-founded but aimed at the wrong half.

## 5. Why the snapshot is actually safe, traced rather than assumed

The whole approach rests on the worker's copy being private. If it is not, this is a data race and the
review passes were rearranging deck chairs. Traced in full:

`GuideData.DeepClone` → `ControlPoint.Clone` → the copy constructor, which allocates a **fresh `Vec3d`**
rather than sharing the reference. `ControlPoint` has exactly one reference-typed field and every
constructor deep-copies it; every other field on `GuideData` is a value type or a string.

⚠️ **This matters more than it looks.** `ControlPoint.SetPosition` mutates the position object **in place** —
so had the clone shared that `Vec3d`, a player dragging a guide would have been writing into the coordinates
the worker was reading, with no symptom until a save came out with a point in the wrong place.

Second assumption, also unchecked until pass 3: **nothing mutates a live guide off the main thread.** The
immense create and sculpt lanes run workers, but those workers operate on a `DeepClone` too, and both lanes
collect their results on game tick listeners. So the mod has exactly one writer, on the main thread — which
is what makes the dirty flag and the in-flight marker safe with no locking at all.

## 6. Traps

⚠️ **A null return that means three different things, only one of which is good news.**
`BeginBackgroundPersist` declines for three reasons: nothing was owed, a pass is already pending, or **the
snapshot itself failed and the registry is still unwritten**. The scheduler treated all three as "this save
is prepared", which skipped the synchronous fallback for a cycle where nothing had been written — defeating
the safety net added one pass earlier for exactly that failure. The fix is to ask the registry what it still
owes (`HasUnsavedChanges`) rather than infer it from a null. → `GOTCHAS` **G43**.

⚠️ **And the mirror image, where the obvious fix would have been wrong.** The success path had the same false
report: a *faulted* serialisation still claimed the save was prepared. The tempting fix is to reuse the same
"does the registry still owe anything?" question — **which would have quietly undone the whole feature**, because
an edit arriving after the snapshot also leaves work owed, and that edit is the intended window, not a
failure. Treating it as one would force a synchronous write at nearly every save on a busy world. The signal
has to be *"did this pass store its bytes"*, not *"is the registry clean now"*.

⚠️ **Bytes handed to `StoreData` reach disk only at the game's NEXT save — and after the last save there
isn't one.** Aiming at the next save is correct only while a next save exists. On shutdown, bytes prepared in
the background go nowhere, and the `Dispose` flush lands in a blob the game has already written. Nearly cost
a session's final edits. → `GOTCHAS` **G44**.

⚠️ **A "join" obligation that was better discharged by not joining.** The plan predicted that moving the
write to a worker would oblige every drop path to *wait* for an in-flight serialisation, on top of G39's
existing flush obligation. It does not: the shutdown flush writes the **live** registry, which is always
newer than any snapshot in flight, and drops the job so its completion discards its older bytes instead of
writing them over the top. Supersession pays the obligation for nothing. ⚠️ **Without that identity check
it would be a silent rollback** — the same shape as **G29**.

⚠️ **Three of my own comments had to be corrected during review**, each stating something that had been true
when written: that the first two saves of a session write synchronously (only the first did), that a
mistimed pass is "never a stall" (the fallback *is* a stall, deliberately), and that the worker "touches
nothing on this object" (it reads two fields). Wrong thread-safety notes are exactly how **G30** happened.

## 7. What the human decided

- **The trigger**: measure the autosave rhythm and aim a short lead before the next save (§2). Theirs, and
  better than either option offered.
- **The lead**: challenged 10 s as unjustified, which it was (§3).
- **Three verification passes**, which found four real defects and one that mattered a great deal (§4).

## 8. Making it visible

⚠️ **The feature is invisible when it works** — its whole effect is the absence of a stall on a world large
enough to stall, and `PLAN_BACKGROUND_SAVE.md` §7 already recorded that the human cannot build one of those
on a server they actually play. Silence from a correct run and silence from a run that never happened look
identical.

v0.4.54 therefore adds a line to `/layout info`: how many world saves were prepared off-thread, how many
still paid on the tick, and the period and lead it settled on. The tally counts a save against the tick only
if it *actually* serialised there — an unprepared save with nothing owed costs nothing, and a counter that
cried wolf would be worse than none.

---

## Delivered

- **v0.4.50** — Guide serialisation moved off the server main thread: the tick deep-copies the registry, a
  worker converts it to JSON, and the bytes are handed back via `EnqueueMainThreadTask`. Aimed 10 s ahead of
  the predicted world save, with the period measured from the gap between two saves.
- **v0.4.51** — The lead starts at 3 s instead of 10 s and widens itself (doubling, capped at 60 s, logged)
  only when a cycle proves it was too short.
- **v0.4.52** — A save with no background pass behind it now writes on the tick. Closes a latch that could
  have stopped guide edits reaching disk for a whole server session, and covers the opening saves of a
  session, a missed aim, and shutdown under one rule.
- **v0.4.53** — A declined or faulted background pass no longer reports the save as prepared.
- **v0.4.54** — `/layout info` reports background-save state: saves prepared off-thread versus on the tick,
  the learned autosave period, and the current lead.

## Decisions

- **Aim at the save; do not poll.** `StoreData` only updates an in-memory blob, so being early costs nothing
  but freshness and being late costs one save's delay. The period is measured, not read — it assumes nothing
  about the game's default and adapts if that constant changes. **Human's design** (§2).
- **The lead self-corrects instead of being chosen.** Starting conservative would pay a permanently wide
  window against a delay nobody has measured; overshooting is soft, so start small and widen on evidence (§3).
- **One rule for the synchronous fallback**, deliberately broader than any of the specific conditions it
  replaces, because each of those missed a case (§4).
- **The admin policies and the client's private F4 guides both stay synchronous.** The first is a handful of
  records; the second is a small file write, and putting file I/O on a worker is a different and nastier
  risk than putting text conversion on one. Both flagged for review rather than assumed.
- **The durability change, stated rather than slipped in:** an edit made within the lead window before a save
  now waits for the following save. Everything older is written by the very next save, exactly as before.

## Traps

See §6 — four, plus the comment corrections. `GOTCHAS` **G43** and **G44** are the two lifted from it.

## Flagged and unverified

**Judgement calls awaiting review** (indexed in `dev/TODO.md`):

1. **The lead's starting value (3 s), its doubling, and the 60 s cap.** Invented numbers, chosen so the
   window is small and the correction converges in a save or two.
2. **The plausible-period band, 20 s to 30 minutes.** A gap outside it is not the autosave rhythm and is
   ignored. A server autosaving outside that band would simply never learn a period and would write on the
   tick every save — today's behaviour, safely.
3. **Admin policies left synchronous.**
4. **Client-side private (F4) guides left synchronous.**

**Claims not tested:**

- ⚠️ **Nothing in this session has been playtested** — stated by the human ("I'll test later"), not assumed.
  v0.4.50 through v0.4.53 were each superseded within the session; **v0.4.54 is the build to test**, and
  v0.4.50–v0.4.52 should not be used (each carries a defect fixed by a later one).
- **Whether the autosave rhythm is as steady in practice as the design assumes.** Observable: repeated
  `widening its lead` lines in the server log would say it is not, and `/layout info` shows the tally.
- **Whether shutdown is detected before the final save.** Two independent signals are checked
  (`EnumServerRunPhase.Shutdown` and `IServerAPI.IsShuttingDown`) and both would have to fail together, but
  this is reasoning, not a test. Symptom would be recent work missing after a clean `/stop`.
