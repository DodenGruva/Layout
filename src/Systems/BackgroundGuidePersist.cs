using System;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Server;

namespace Layout.Systems
{
    /// <summary>
    /// Server-side scheduler that keeps the guide blob current WITHOUT serialising it on the tick.
    /// Hooked to <c>GameWorldSave</c> by the server composition root.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS. <see cref="GuideManager.Persist"/> converts the whole registry to JSON, which cost
    /// 42 ms at three thousand guides and 113 ms at eight thousand — measured 2026-08-01, against a 20 ms
    /// server tick. It ran on the main thread by accident of history, not because anything needs it there.
    /// Deep-copying the registry instead costs ~0.5 ms, so the tick pays the copy and a worker pays the
    /// text conversion. `TODO` A18, `dev/plans/PLAN_BACKGROUND_SAVE.md`.
    ///
    /// WHY IT AIMS AT THE SAVE INSTEAD OF POLLING. Handing bytes to <c>StoreData</c> writes nothing to
    /// disk — it updates the in-memory savegame blob, which the game flushes at its next world save. So
    /// the only thing that matters is being ready BEFORE that moment, and being ready as late as possible,
    /// because later means fresher. The game gives mods no warning that a save is coming (there is no
    /// interval in `serverconfig.json` and none on the API; the period is a constant inside the game's own
    /// `ServerSystemAutoSaveGame`), but the saves are evenly spaced — so the period is MEASURED from the
    /// gap between two of them and the next pass is aimed a short lead before the one after.
    ///
    /// Measuring beats reading a setting even if one existed: it needs no assumption about the game's
    /// default and adapts on its own if that constant ever changes. The lead is measured too, in its own
    /// way — see <see cref="_leadMs"/>, which starts small and widens only if this server proves it must.
    ///
    /// WHAT IT COSTS. In steady state, one serialisation per autosave, on a worker, and never one per edit
    /// however many players are building. The durability change — deliberate, and the only one — is that an
    /// edit made inside the lead window before a save misses it and waits for the following save instead.
    /// Everything older than that window is written by the very next save, as before. That window is the
    /// thing kept small, and <see cref="_leadMs"/> is where the keeping happens.
    ///
    /// ⚠️ EVERY FAILURE OF THE AIM IS SOFT, and that is load-bearing. A mistimed pass — a manual
    /// <c>/autosavenow</c> shifting the rhythm, a lagging server, a session too young to have learned a
    /// period, a serialisation that faulted — costs at worst the old on-tick serialisation for that one
    /// save, and otherwise only staleness. Never a partial write, never a rollback, never a save silently
    /// carrying nothing. **Nothing here may be changed into something that fails hard instead**, and no
    /// path out of it may leave a save both unprepared and unwritten: that combination is the one real
    /// hazard in the design, and <see cref="OnWorldSave"/>'s fallback is the single thing standing on it.
    /// </remarks>
    public sealed class BackgroundGuidePersist : IDisposable
    {
        /// <summary>
        /// How long before the predicted save the first background pass is aimed, before experience says
        /// otherwise. See <see cref="_leadMs"/> — this is only the starting guess.
        /// </summary>
        private const long InitialLeadMs = 3_000;

        /// <summary>Ceiling on the self-widening lead, so a server that never manages to land a pass in
        /// time gives up widening rather than aiming ever further into the past.</summary>
        private const long MaxLeadMs = 60_000;

        /// <summary>Gaps outside this band are not the autosave rhythm — a manual save, a shutdown save, or
        /// a server that sat paused — and are ignored rather than learned from.</summary>
        private const long MinPlausibleIntervalMs = 20_000;
        private const long MaxPlausibleIntervalMs = 30 * 60_000;

        /// <summary>Floor on any scheduled delay, so a pass is never aimed at the save tick it was
        /// scheduled from — the snapshot belongs on some other tick than the one already saving.</summary>
        private const int MinScheduleDelayMs = 1_000;

        private readonly ICoreServerAPI _sapi;
        private readonly GuideManager _guides;

        // Every field below is written and read on the server main thread ONLY — including from the
        // continuation, which EnqueueMainThreadTask puts back on the tick before it touches any of them.
        // The worker thread itself reads only _sapi and _guides, both readonly and both thread-safe for
        // what it does with them (logging, and the job's own private snapshot).
        private long _lastSaveElapsedMs = -1;   // when the previous GameWorldSave fired
        private long _learnedIntervalMs = -1;   // the measured autosave period, -1 until two saves are seen
        private long _pendingCallbackId;        // the aimed one-shot, 0 = none scheduled
        private bool _shuttingDown;             // set the moment shutdown begins; see IsLastSaveOfTheSession
        private bool _disposed;

        /// <summary>
        /// How far ahead of the predicted save to aim. Starts at <see cref="InitialLeadMs"/> and only ever
        /// grows, when a cycle proves it was not enough.
        /// </summary>
        /// <remarks>
        /// ⚠️ THIS IS NOT SIZED FOR THE WORK, WHICH IS TINY — a snapshot (0.5 ms at three thousand guides),
        /// the serialisation (42 ms there, 113 ms at eight thousand) and one tick to hand the result back
        /// (33 ms): about a sixth of a second all told, worst case. It is sized for the UNCERTAINTY in
        /// predicting the save, which is the game's business and cannot be measured from outside.
        ///
        /// So it is not guessed. It starts small, because a short lead is the better outcome (see
        /// <see cref="OnWorldSave"/> for what the lead actually exposes), and widens when the evidence says
        /// it must. **Widening only, never narrowing** — a lead that hunted up and down would oscillate
        /// around the boundary and spend half its cycles on the wrong side of it.
        /// </remarks>
        private long _leadMs = InitialLeadMs;

        // Whether this cycle's save has bytes waiting for it. True in exactly two cases: a pass stored, or
        // a pass found nothing owed. NOT true merely because a pass ran on time — one that ran and faulted
        // leaves the save unprepared, and reading this as "was the aim early enough" is what previously let
        // a failed pass suppress the synchronous write that covers it.
        private bool _aimedPassScheduled;
        private bool _aimedPassSettled;

        /// <summary>
        /// Which save-to-save cycle we are in. Only exists so a pass can tell whether it still belongs to
        /// the cycle it was aimed at.
        /// </summary>
        /// <remarks>
        /// ⚠️ WITHOUT THIS, A SLOW PASS REPORTS SUCCESS FOR SOMEBODY ELSE'S CYCLE. A pass aimed at save N
        /// that only finishes after save N has come and gone would set <see cref="_aimedPassSettled"/>
        /// during cycle N+1 — so cycle N+1 would look prepared when nothing had run for it, skipping both
        /// the widening it had earned and the synchronous write that covers it. GOTCHAS G29 again: async
        /// work must prove it is still the current work before writing into shared state.
        /// </remarks>
        private long _cycle;

        // Session tallies, for StatusText only. Nothing reads these to make a decision.
        private int _savesPrepared;
        private int _savesOnTick;

        /// <summary>
        /// One line for <c>/layout info</c> describing what this is actually doing.
        /// </summary>
        /// <remarks>
        /// ⚠️ THE FEATURE IS INVISIBLE WHEN IT WORKS, which is the problem this solves. Its whole effect is
        /// the ABSENCE of a stall on a world large enough to stall — and the human cannot build one of
        /// those on a server they actually play (`PLAN_BACKGROUND_SAVE.md` §7), so "it seemed fine" is not
        /// evidence of anything. Silence from a correct run and silence from a run that never happened look
        /// identical. This makes the difference readable: how many saves were prepared off-thread, how many
        /// still paid on the tick, and what period and lead it settled on.
        /// </remarks>
        public string StatusText()
        {
            if (_learnedIntervalMs <= 0)
                return $"Background guide save: measuring the autosave period "
                     + $"({(_lastSaveElapsedMs >= 0 ? 1 : 0)} of 2 saves seen); writing on the tick until then.";

            int total = _savesPrepared + _savesOnTick;
            return $"Background guide save: {_savesPrepared:n0} of {total:n0} world saves prepared off-thread"
                 + $"{(_savesOnTick > 0 ? $" ({_savesOnTick:n0} paid on the tick)" : "")}; "
                 + $"autosave period {FormatSeconds(_learnedIntervalMs)}, lead {FormatSeconds(_leadMs)}"
                 + $"{(_leadMs > InitialLeadMs ? " (widened)" : "")}.";
        }

        private static string FormatSeconds(long ms) =>
            ms >= 60_000 ? $"{ms / 60_000}m {(ms % 60_000) / 1000}s" : $"{ms / 1000.0:0.#}s";

        public BackgroundGuidePersist(ICoreServerAPI sapi, GuideManager guides)
        {
            _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
            _guides = guides ?? throw new ArgumentNullException(nameof(guides));

            // See IsLastSaveOfTheSession. Subscribed rather than relying on the flag alone because this
            // fires when shutdown BEGINS, which is unambiguously before the final save.
            _sapi.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, () => _shuttingDown = true);
        }

        /// <summary>
        /// Whether the save now running is the last one — after which no later save exists to carry bytes
        /// prepared in the background.
        /// </summary>
        /// <remarks>
        /// ⚠️ GETTING THIS WRONG SILENTLY LOSES A SESSION'S LAST EDITS, which is why it is answered two
        /// ways. Aiming at the next save is only ever correct if there IS a next save: <c>StoreData</c> puts
        /// bytes in the in-memory savegame blob and the GAME decides when that reaches disk, so bytes
        /// prepared after the final save are written nowhere. On shutdown the aim is therefore abandoned
        /// and the tick pays the full serialisation — a stall nobody is left to feel.
        /// </remarks>
        private bool IsLastSaveOfTheSession => _shuttingDown || _sapi.Server.IsShuttingDown;

        /// <summary>
        /// Hooked to <c>GameWorldSave</c>. Learns the rhythm, then aims the next background pass just
        /// before the save after this one.
        /// </summary>
        /// <remarks>
        /// A SAVE STILL SERIALISES ON THE TICK WHENEVER NO BACKGROUND PASS PREPARED IT — see the rule in
        /// the body, which is the single guarantee the whole class rests on. In steady state that never
        /// fires; the cases it catches are a session's opening saves, a missed aim, a jammed job, and
        /// shutdown. A handful of stalls per server lifetime, none of them mid-play, against every autosave
        /// in between.
        ///
        /// ⚠️ DO NOT "OPTIMISE" THAT CHECK INTO A NARROWER ONE, however obviously redundant a given branch
        /// of it looks. Its value is that it does not need to enumerate the ways preparation can fail — it
        /// asks only whether preparation happened. Every specific condition it was almost written as
        /// (interval known? job in flight? shutting down?) missed at least one of the cases above.
        /// </remarks>
        public void OnWorldSave()
        {
            if (_disposed) return;

            long now = _sapi.World.ElapsedMilliseconds;
            if (_lastSaveElapsedMs >= 0)
            {
                long gap = now - _lastSaveElapsedMs;
                // Adopted outright rather than averaged or confirmed over several samples. An off-rhythm
                // save that slips through the band only mis-aims one pass, and the next real save corrects
                // it — smoothing would buy accuracy nobody can feel and hide a changed period for longer.
                if (gap >= MinPlausibleIntervalMs && gap <= MaxPlausibleIntervalMs)
                    _learnedIntervalMs = gap;
            }
            _lastSaveElapsedMs = now;

            bool preparedForThisSave = _aimedPassSettled;
            ReviewLastAim();
            CancelPending();
            _cycle++;
            _aimedPassSettled = false;

            // THE ONE RULE THAT MAKES THIS SAFE: if no background pass settled during the cycle leading
            // into this save, write on the tick, exactly as the mod always did. Persist is a no-op when
            // nothing is owed, so this costs nothing in the ordinary case — and in every extraordinary one
            // it is the reason nothing is quietly lost. It covers, without needing to tell them apart:
            //
            //   • the first saves of a session, before an interval has been measured and there is anywhere
            //     to aim — including the load's own owed write (migration, re-stamping, backup recovery;
            //     see GuideManager.LoadPayload), which is sitting in the dirty flag at exactly that moment;
            //   • an aim that missed, whether the lead was too short or the server hitched;
            //   • ⚠️ A STUCK JOB. If a pass never completes — the hand-back to the main thread failing is
            //     the only way, but it is enough — GuideManager would refuse to start another one for the
            //     rest of the session and NOTHING would reach disk until shutdown, silently. Persist drops
            //     the stuck job as it writes, so the very next world save both saves the data and clears
            //     the jam. A latched pending flag is GOTCHAS G36; this is the same trap wearing a
            //     reference instead of a bool, and this line is what stops it latching.
            //
            // ⚠️ SHUTDOWN IS NOT COVERED BY THE ABOVE and is checked separately: a pass may well have
            // settled, but any edit made after it has nowhere else to go. See IsLastSaveOfTheSession.
            bool writeHere = !preparedForThisSave || IsLastSaveOfTheSession;
            // Asked BEFORE the write, because Persist clears what it is asking about. An unprepared save
            // with nothing owed costs nothing, so it is not counted against the tick — see StatusText,
            // where a tally that cried wolf would be worse than no tally at all.
            bool paidOnTheTick = writeHere && _guides.HasUnsavedChanges;
            if (writeHere) _guides.Persist();
            if (paidOnTheTick) _savesOnTick++; else _savesPrepared++;

            if (IsLastSaveOfTheSession || _learnedIntervalMs <= 0) return;

            // Never let the lead swallow more than half the period, so a lead that has widened its way past
            // the period still schedules in the future rather than in the past.
            long delay = Math.Max(_learnedIntervalMs - _leadMs, _learnedIntervalMs / 2);
            _aimedPassScheduled = true;
            Schedule(delay);
        }

        /// <summary>
        /// Did the pass aimed at the save that just arrived finish in time? If not, the lead was too short
        /// for this server and is widened for the next cycle.
        /// </summary>
        /// <remarks>
        /// This is the whole reason the starting lead can be three seconds rather than a defensive minute.
        /// The cost of overshooting is real but soft — that cycle's bytes miss their save and ride the next
        /// one — so the safe play is to start where we want to be and let the server itself say if it needs
        /// more room, rather than pay a permanently wide window against a delay nobody has measured.
        ///
        /// Doubling rather than nudging: this should converge in a save or two, not creep across an
        /// afternoon of them, and the true value is a property of the machine that will not drift.
        /// </remarks>
        private void ReviewLastAim()
        {
            if (!_aimedPassScheduled || _aimedPassSettled || _leadMs >= MaxLeadMs)
            {
                _aimedPassScheduled = false;
                return;
            }
            _aimedPassScheduled = false;

            long widened = Math.Min(_leadMs * 2, MaxLeadMs);
            _sapi.Logger.Notification(
                "[Layout] Background guide save did not complete before the world save; widening its lead "
                + "from {0} ms to {1} ms.", _leadMs, widened);
            _leadMs = widened;
        }

        /// <summary>Runs a background pass now, if one is owed and none is already running.</summary>
        private void RunPass()
        {
            _pendingCallbackId = 0;
            if (_disposed) return;

            GuideManager.GuidePersistJob job = _guides.BeginBackgroundPersist();
            if (job == null)
            {
                // ⚠️ NO JOB HAS THREE MEANINGS AND ONLY SOME OF THEM ARE GOOD NEWS. Nothing was owed; or a
                // job is somehow still pending; or the snapshot itself failed and the registry is STILL
                // unwritten. Claiming the cycle for all three — which this line used to do — would tell
                // OnWorldSave the save was prepared when nothing had been written, and skip the very
                // fallback that covers it. So ask the registry what it still owes rather than inferring it
                // from a null.
                _aimedPassSettled = !_guides.HasUnsavedChanges;
                return;
            }

            long cycle = _cycle;

            // The house pattern for server-side background work (see ServerNetworkHandler's immense lanes),
            // minus their thread-priority juggling: those grind for seconds and must yield to the server,
            // this is tens of milliseconds and would spend more code lowering its priority than running.
            Task.Factory.StartNew(
                () =>
                {
                    job.Run();   // never throws; a fault is carried on the job and handled below
                    try
                    {
                        // EnqueueMainThreadTask is the documented thread-safe way back onto the tick.
                        _sapi.Event.EnqueueMainThreadTask(
                            () =>
                            {
                                bool stored = _guides.CompleteBackgroundPersist(job);
                                // Claim the cycle only if this pass actually stored, and only if it is
                                // still this pass's cycle to claim (see _cycle). A faulted pass leaves the
                                // save unprepared and must fall through to the synchronous write.
                                //
                                // ⚠️ "Stored" deliberately ignores edits that arrived after the snapshot.
                                // Those belong to the NEXT cycle — treating them as a failure here is the
                                // trap described on CompleteBackgroundPersist, and it would put the
                                // serialisation back on the tick for most saves on a busy world.
                                if (_cycle == cycle) _aimedPassSettled = stored;
                            },
                            "layoutguidepersist");
                    }
                    catch (Exception e)
                    {
                        // Nothing to do from a worker thread: the job stays in flight and _aimedPassSettled
                        // stays false, which is precisely the state OnWorldSave's synchronous fallback
                        // exists to clear. Logged rather than swallowed because it should never happen, and
                        // an unobserved task exception would say so far less clearly.
                        _sapi.Logger.Error(
                            "[Layout] Could not hand a background guide save back to the main thread; the "
                            + "next world save will write it directly. {0}", e);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private void Schedule(long delayMs)
        {
            if (_disposed) return;
            int clamped = (int)Math.Min(Math.Max(delayMs, MinScheduleDelayMs), int.MaxValue);
            _pendingCallbackId = _sapi.Event.RegisterCallback(_ => RunPass(), clamped);
        }

        private void CancelPending()
        {
            if (_pendingCallbackId == 0) return;
            _sapi.Event.UnregisterCallback(_pendingCallbackId);
            _pendingCallbackId = 0;
        }

        /// <summary>
        /// Stops scheduling. Deliberately does NOT flush and does NOT wait for a running worker — the
        /// caller's own <see cref="GuideManager.Persist"/> on the shutdown path does both jobs better: it
        /// writes the LIVE registry (newer than any snapshot in flight) and drops the job so its result is
        /// discarded rather than written over the top.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelPending();
        }
    }
}
