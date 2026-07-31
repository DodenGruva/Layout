# Plan — Documentation Overhaul

> **Status: ✅ COMPLETE — executed 2026-07-30 at v0.4.33, all eight phases.** Written 2026-07-29. Reviewed and corrected on
> 2026-07-30 before any file moved — see §0's re-measurement note, §7.1 (the trap baseline was understated),
> §5.2 (the doc-debt conflict), §6 checks 6 and 10, and §8's `STATUS.md` criterion. Executed as its own
> session, with **no code changes in the same commits**. Everything here is text movement; nothing in
> `src/` is touched.
>
> **Phase log.**
> - **Phase 0 — Archive. COMPLETE 2026-07-30.** Six documents frozen into
>   `dev/archive/superseded-2026-07-30/` with banners, plus `dev/archive/README.md`. Every original verified
>   **byte-identical** after its banner by direct byte comparison, not assumed. Publication-safety scan clean.
> - **Phase 1 — Harvest. COMPLETE 2026-07-30.** `dev/GOTCHAS.md`: 25 traps, 8 reversals, and an appendix
>   accounting for all 54 baseline markers. Post-harvest count re-run: **54, unchanged — nothing lost.**
> - **Phase 2 — Split `TODO.md`. COMPLETE 2026-07-30.** 1,124 → **142 lines** (target ≤150), open items
>   only. Delivered work extracted **verbatim** into `dev/history/DONE.md` (1,031 lines; 1,008 of them
>   byte-identical lines from the original, the rest being its header and section notes). All 7 of
>   `TODO.md`'s ⚠️ markers landed in `DONE.md`; repo-wide count re-run at **54, unchanged**.
> - **Phase 3 — Foldering. COMPLETE 2026-07-30.** 26 session records → `dev/sessions/`, 5 plans (this file
>   included) → `dev/plans/`, `CHANGELOG_ARCHITECTURE.md` → `dev/history/`. All via `git mv`, all recorded
>   as renames. Added `dev/sessions/INDEX.md` (26 rows + a reading order) and `dev/sessions/TEMPLATE.md`.
>   `CLAUDE.md`'s 38 now-broken references were folder-qualified mechanically; a check-10 dry run finds every
>   backticked path in the living documents resolving.
> - **Phase 4 — `ARCHITECTURE.md` rewrite. COMPLETE 2026-07-30.** 1,117 → **652 lines**. Built by verbatim
>   extraction: 42 lines newly written (the header and the two removal notes), the rest byte-identical.
>   **The Settled Decisions Register was verified character-for-character identical** before the file was
>   installed — the install aborts otherwise. Sections 1 and 3 are emptied but retained by number, so older
>   references still resolve and the removal is visible rather than a silent gap. Also dropped: a
>   version-bound closing paragraph that claimed current state and pointed at two documents being archived.
> - **Phase 5 — `STATUS.md`. COMPLETE 2026-07-30.** **265 lines** (target ~350), folding in `HANDOFF.md`
>   (753) and `dev/PROJECT_STATUS.md` (423). Both retired from the living tree — each `git rm` is gated on
>   its frozen copy existing. `dev/BUILD_INSTRUCTIONS.txt` retired likewise (its one unique item is
>   `GOTCHAS` G23). `CHANGELOG.md`'s permanently-empty `## Unreleased` heading dropped. Mined forward from
>   the two status docs: the module map, the exact configuration/limits table, and the data-flow summaries.
>   **Closes the standing doc-debt item.**
> - **Phase 6 — `CLAUDE.md` rewrite. COMPLETE 2026-07-30.** 376 → **116 lines** (acceptance ≤130), with the
>   task index. Zero per-session narrative blocks; the 16 inlined traps are now `GOTCHAS` pointers. Removed
>   two stale claims it had been carrying: `Current: v0.4.1` (build was v0.4.33) and "v0.3.59–v0.3.69 are on
>   `beta-shader`, not yet merged" — that branch no longer exists and the shader is live on `beta`.
> - **Phase 7 — `DocCheck.ps1` + `WIRE_HISTORY.md`. COMPLETE 2026-07-30.** `WIRE_HISTORY.md` built by
>   reading `RegistrationOrder()` in `PacketTypes.cs`, whose per-packet comments name the protocol version
>   each was added at — so the ledger is derived from the authority, not reassembled from prose. Six bumps
>   that added no packet are marked *(none)* rather than invented. **`DocCheck.ps1` passes**, and all ten
>   checks were negative-tested with planted faults.
>
> **ALL PHASES COMPLETE.** `dotnet build -c Release`: 0 warnings, 0 errors. `src/`, `Layout.csproj` and
> `assets/` are untouched — verified by `git status`.
>
> **Not committed.** Commits are made only when the human instructs (standing rule, `CLAUDE.md`).
>
> ⚠️ **NOTHING IS DELETED. EVER.** Every document being overhauled is **archived intact** first — see §2.
> This is a human-set constraint, not a preference to be optimised away.

---

## 0. Why — the measurements

Taken 2026-07-29 against the live tree. These are the argument; re-measure before starting.

> **Re-measured 2026-07-30 before execution.** Everything below held except the trap count, which was
> understated — see the row and §7.1. `ARCHITECTURE.md`'s §1 + §3 are exactly 355 lines and its KEEP
> sections exactly 600, so §3's arithmetic is confirmed. Total documentation is now 107,773 words; the
> ~3,800-word delta is this plan itself.

| Symptom | Measurement |
|---|---|
| Total documentation | **~104,000 words** across 30 files — likely more than the source it describes |
| `CLAUDE.md`, loaded **every session** | 376 lines, growing by a "Session-3x additions" block per session |
| `dev/TODO.md`, read often | 1,118 lines, of which **~710 (63%) are delivered/resolved archive** |
| `dev/ARCHITECTURE.md` | 1,117 lines, of which **355 (32%) are file structure + module map** — derivable from source, and the fastest-rotting content in the repo. A further ~180 lines are version preamble and a document-changelog index duplicating `CHANGELOG_ARCHITECTURE.md`. |
| Hard-won ⚠️ traps | **54 markers scattered across 12 files**, with no home and no index (the 2026-07-29 figure of 45/10 missed two files outright — §7.1) |
| Session records | **26 files** (`SESSION_9` … `SESSION_34`), no index, ~6,500 lines |

**The root cause is not size. It is that documents are organised by SUBJECT, while maintenance cost is
driven by RATE OF CHANGE.** Every current file mixes durable fact with time-stamped narrative, so every
file goes stale, and every file ends up needing a staleness banner.

**And the banners go stale too.** `ARCHITECTURE.md` says it "trails by four sessions (current build is
v0.4.0)" — it trails six, and the build is v0.4.33. A banner is itself a fact requiring maintenance.
**Needing one is the signal that content sits in the wrong tier.**

### 0.1 What we are actually optimising — in priority order

Token count is the **least** important goal. 104,000 words cost nothing unless something reads them.
The real costs, highest first:

1. **A document that is WRONG.** It does not send you to the source; it sends you somewhere else
   entirely. Unbounded cost.
2. **`CLAUDE.md`** — paid every session, unconditionally.
3. **Often-read files** (`TODO.md`) — paid most sessions.
4. **Total size** — barely matters. Twenty-six session files nobody opens are free.

This reframes the whole overhaul: cutting the module map is **a correctness move that happens to save
tokens**, not a size measure. It is not bad because it is 238 lines; it is bad because it is *guaranteed to
become false while looking authoritative*.

**Corollary — staleness is more dangerous than absence.** A missing document sends you to the source, which
is always right. Where the two conflict, prefer removing a stale section over half-updating it. (This is
what makes archiving-not-deleting safe: the archive is *loudly* marked as superseded, so it is absent for
practical purposes rather than wrong.)

---

## 1. The target scheme — organise by lifetime

| Tier | Files | Changes | Governing rule |
|---|---|---|---|
| **0 · Always loaded** | `CLAUDE.md` | Rarely | Only what is needed to *start work*. Pointers, not content. |
| **1 · Durable** | `dev/ARCHITECTURE.md`, `dev/GOTCHAS.md` | When a decision changes | **Never mentions a version or a date.** |
| **2 · Current state** | `STATUS.md` | Every session | **Regenerated wholesale, never edited.** |
| **3 · History** | `CHANGELOG.md`, `dev/sessions/`, `dev/plans/`, `dev/history/` | Append-only | **Never revised, therefore never stale.** |
| **4 · Archive** | `dev/archive/superseded-2026-07-30/` | Never | **Frozen. Read-only. Never cited as current.** |

### 1.1 File-by-file disposition

**No row below says "delete".**

| File | Action |
|---|---|
| `CLAUDE.md` | **ARCHIVE + REWRITE** to ~120 lines. Strip per-session "additions" blocks and the 16 inlined traps; replace with a task-indexed pointer table (§5.7). Keep: what the project is, build/run/package, the versioning + zip rules, the working agreement, the doc-update ritual (§5.5). |
| `HANDOFF.md` | **ARCHIVE**, then fold into new `STATUS.md`. |
| `dev/PROJECT_STATUS.md` | **ARCHIVE**, then fold into `STATUS.md`. Closes the standing doc-debt item in `TODO.md`. Its per-module descriptions are the likeliest unique content — mine them before archiving. |
| `CHANGELOG.md` | **KEEP IN PLACE, UNCHANGED.** Already a correct Tier 3 archive. Only change: drop the permanently-empty `## Unreleased` heading. |
| `dev/ARCHITECTURE.md` | **ARCHIVE + REWRITE.** See §3. |
| `dev/CHANGELOG_ARCHITECTURE.md` | **MOVE** to `dev/history/`, content untouched. Already correct. |
| `dev/TODO.md` | **ARCHIVE + SPLIT.** Open items only, target <150 lines. Delivered → `dev/history/DONE.md`. |
| `dev/SESSION_*.md` (26) | **MOVE** to `dev/sessions/`, content untouched — history is not revised. Add `INDEX.md` (§5.3). |
| `dev/PLAN_*.md` (5, incl. this one) | **MOVE** to `dev/plans/`, content untouched. |
| `dev/RenderIcon.ps1` | **KEEP.** Tooling, not documentation. |
| `BUILD_INSTRUCTIONS.txt` | **ARCHIVE — verified 2026-07-30.** Superseded (its own banner says so; it describes v0.1.0 at 35 source files). It holds **one** thing `CLAUDE.md` does not, and phase 6 must carry it across: **if a build cannot find `Newtonsoft.Json.dll` or `protobuf-net.dll`, the install may keep them in the root rather than `Lib\` — drop the `\Lib` from that one `HintPath`.** That is a real setup failure with a non-obvious fix and it exists nowhere else. Everything else in the file is either in `CLAUDE.md` already or derivable from `assets/` and `src/`. |
| — | **NEW:** `dev/GOTCHAS.md` (§5.1), `STATUS.md` (§5.2), `dev/sessions/INDEX.md` (§5.3), `dev/WIRE_HISTORY.md` (§5.4), `dev/sessions/TEMPLATE.md` (§5.6), `dev/DocCheck.ps1` (§6). |

---

## 2. Archiving — the human-set constraint

**Every document that is rewritten, split, folded or superseded is copied intact into
`dev/archive/superseded-2026-07-30/` FIRST, preserving its original filename.** The folder is a snapshot of
the doc set exactly as it stood at v0.4.33, immediately before the overhaul.

Git already preserves all of this in history, but **that is not sufficient here**: the person who most needs
the reassurance does not use `git show`, and a working-tree copy is a working-tree copy. Do it.

**The one genuine risk of archiving is that a frozen copy gets read as current.** Three mitigations, all
required:

1. **The folder name carries the verdict** — `superseded-2026-07-30`, not `old` or `backup`. The date is
   the date the snapshot was TAKEN (execution began 2026-07-30), not the date the plan was written. The
   doc-state it freezes is pinned by `v0.4.33` in the banner, so the folder name is free to mean what it
   says. A banner asserting a date on which nothing happened is the same defect §0 diagnoses.
2. **Every archived file gets a banner prepended at archive time**, before the original first line:
   ```markdown
   > 🗄️ **ARCHIVED 2026-07-30 at v0.4.33 — SUPERSEDED, DO NOT USE AS CURRENT.**
   > Frozen exactly as it stood before the documentation overhaul. Its successor is `<path>`.
   > Kept so nothing is ever lost. Never edit this file; never cite it as current.
   ```
   The banner is the **only** permitted modification to an archived file.
3. **`dev/archive/README.md`** explains what the folder is, and states the rule: *nothing outside the
   archive may link into it as a source of current truth.* `DocCheck.ps1` enforces this (§6, check 8).

Archived files are **excluded from every currency check** — they are supposed to be stale. That is the point.

---

## 3. `ARCHITECTURE.md` — back to a blueprint

**The stated intent is a blueprint. Restore exactly that.** Archive the current file, then write its
successor from it.

**KEEP** (this is the blueprint):
- Overview — what Layout is.
- **Settled Decisions Register** — the highest-value section in the file. Carried over verbatim.
- Data Model, Serialization and Persistence, Concurrency and Edge Cases.
- Geometry Tiers, Key Interaction Flows.

**DROP from the successor — derivable from source (355 lines):**
- §1 File Structure (117 lines) and §3 Module Map (238 lines). `Glob` and `Grep` answer these instantly and
  *correctly*; a hand-maintained list is guaranteed to drift and adds nothing a reader cannot get in one
  tool call. Replace with a three-line note saying exactly that.

**DROP from the successor — history in a durable file (~180 lines):**
- The "Supersedes v3.13 / v2.5 consolidated / v2.6 folded in…" preamble, and the Document Changelog Index
  (lines ~131–161). Both belong in `dev/history/CHANGELOG_ARCHITECTURE.md`, which exists for precisely this.

**DROP the staleness banner.** Once version-bound content is gone there is nothing left to trail.

**DROP the `v3.14` from the title.** Versioning the blueprint is what produced the "supersedes v3.13"
preamble. Git versions it already.

**Result:** ~1,117 → ~600 lines, needing maintenance only when a decision actually changes. Nothing is lost:
the full original sits in the archive.

---

## 4. Execution order

Eight phases. **Each is independently committable and reviewable.** Stop after any phase.

| # | Phase | Risk | Notes |
|---|---|---|---|
| 0 | **Archive** — snapshot every affected doc into `dev/archive/superseded-2026-07-30/` with banners + README | **None (pure addition)** | Do this before anything else touches a byte |
| 1 | **Harvest** — build `dev/GOTCHAS.md` from all 45 ⚠️ markers | **None (additive)** | See §7 |
| 2 | **Split `TODO.md`** — open items stay, delivered → `dev/history/DONE.md` | Low | Biggest read-cost win |
| 3 | **Foldering** — `dev/sessions/`, `dev/plans/`, `dev/history/`; write `INDEX.md` + `TEMPLATE.md` | Low | `git mv`; update pointers in `CLAUDE.md` |
| 4 | **`ARCHITECTURE.md` rewrite** (§3) | Medium | Largest single change. Review the diff carefully. |
| 5 | **`STATUS.md`** — fold in `HANDOFF` + `PROJECT_STATUS` | Medium | Closes the standing doc-debt item |
| 6 | **Rewrite `CLAUDE.md`** to ~120 lines with the task-index (§5.7) | Low | Only safe once GOTCHAS and STATUS exist to point at |
| 7 | **`DocCheck.ps1`** (§6) + `WIRE_HISTORY.md` (§5.4) | None (additive) | Makes the scheme self-policing |

**Suggested first commit: phases 0–2.** All additive or low-risk, and they let the shape be judged before
anything is restructured.

---

## 5. New file specifications

### 5.1 `dev/GOTCHAS.md` — traps and reversals (Tier 1, append-only)

Two sections. **The second may be the more valuable of the two.**

**`## Traps`** — one numbered entry per trap, newest at the bottom, citable as `GOTCHAS #12`.
**Every entry leads with its TRIGGER**, because a trap only helps if it is read *before* the mistake. The
trigger is what `CLAUDE.md` indexes.

```markdown
## G12 — A static text wraps, but its bounds never grow
**Trigger:** before adding any variable-length text to a GUI dialog.
**Trap:** a height guessed too small does not clip and does not scroll — it draws over what follows,
which looks like a rendering bug rather than a layout one.
**Do:** measure — `capi.Gui.Text.GetQuantityTextLines(font, text, width, …)` × `GetLineHeight(font)`.
See `GuidePlayersDialog.TextHeight`.
**Found:** Session 34 (shipped three times first). Detail: `dev/sessions/SESSION_34.md` §7.
```

**`## Reversals and disproved claims`** — things tried and deliberately undone, and claims later shown
false. **This prevents the most expensive failure mode there is: re-implementing something that was removed
on purpose.** It costs a whole session *and* ships a regression. v0.4.22 is the proof it happens here —
capping private guides looked like an obvious fix, was implemented, and had to be reverted in v0.4.27.
Currently each reversal is findable only by reading the session that performed it.

Seed with: private-guide caps (applied v0.4.22, reverted v0.4.27); greedy face merging (tried and rejected
v0.3.44–v0.3.48); filled 3D volumes (retired v0.2.17); the Session 25–26 spatial renderer (rolled back
v0.3.49); `SESSION_26`'s disproved 140→80 FPS claim; `PLAN_BLOCK_OCCUPANCY`'s first draft.

### 5.2 `STATUS.md` — current state (Tier 2, regenerated)

Folds in `HANDOFF.md` + `dev/PROJECT_STATUS.md`. Target ~350 lines.

1. What Layout is (5 lines); where it stands — version, branch, what is live on `main`.
2. **Wire/save state**: DataVersion, protocol, source-file count, shape count.
3. What shipped in the last 2–3 sessions.
4. What is open — **a pointer to `TODO.md`, not a copy of it.**
5. Current measured performance position, and the governing renderer constraint.
6. **Known unverified claims** (§5.8).
7. Pointer table to every other document.

**Regenerated wholesale each session, never edited.** Editing is precisely how `PROJECT_STATUS.md` drifted
three sessions behind. Keep it short enough that rewriting beats patching — that is a feature, not a cost.

**Phase 5 must REWRITE the standing doc-debt note, not tick it.** `dev/TODO.md` lines 112–119 carry a
human-requested item from 2026-07-27 that this plan overrides on two points:

| The doc-debt note says | This plan does |
|---|---|
| "then **delete** `PROJECT_STATUS.md`" | Archives it. **The no-delete constraint in §2 wins** — it is the later and more explicit human instruction. |
| Keep `HANDOFF.md` as the single current-state document | Archives `HANDOFF.md` too; `STATUS.md` is the survivor. |

Its remaining instructions still stand and must be carried out: fold across `PROJECT_STATUS.md`'s
per-module descriptions (the note and §1.1 independently identify these as the likeliest unique content),
update every reference — `CLAUDE.md`, `ARCHITECTURE.md`'s banner, `TODO.md`'s own Purpose line at line 3,
and the "Next session — start here" reference at line 1088 — and honour its closing warning: **maintaining
one fewer status doc is the goal, so do not simply move the redundancy into `STATUS.md`.**

### 5.3 `dev/sessions/INDEX.md` — the archive index (Tier 3, append-only)

One row per session. **This is what makes 26 unread files cost nothing:** the index is read, the sessions
are not, unless the index points at one. Cheapest item in the plan.

```markdown
| # | Versions | Wire | Summary |
|---|---|---|---|
| 34 | 0.4.28–0.4.33 | proto 24 | Polish queue, send-to-ground, editable Players dialog |
| 33 | 0.4.15–0.4.27 | DV 13, proto 23 | Admin settings, Players dialog, T1 snap, free-angle rectangles |
```

### 5.4 `dev/WIRE_HISTORY.md` — protocol and DataVersion ledger (Tier 3, append-only)

**A correctness aid, not tidiness.** Packet registration is append-only and renumbering is forbidden, so
"what did slot 6 used to mean" is load-bearing — and it currently exists only as prose scattered across 26
session files plus a code comment. One row per bump: version, what was added, which session, and any
retired-but-reserved slots (`LayoutAdminSetting` 6 and 7 are already in that state).

### 5.5 The doc-update ritual — into `CLAUDE.md` (Tier 0)

A numbered checklist for when the human says "update the documents". `CHANGELOG.md` was missed at the end of
Session 29 and had to be backfilled in Session 30; a checklist is the cheap fix.

```
1. dev/sessions/SESSION_<n>.md   — new record, from TEMPLATE.md
2. dev/sessions/INDEX.md         — one row
3. CHANGELOG.md                  — every version since the last entry
4. dev/GOTCHAS.md                — any new trap or reversal
5. dev/WIRE_HISTORY.md           — only if protocol/DataVersion moved
6. dev/TODO.md                   — open items only; delivered → dev/history/DONE.md
7. STATUS.md                     — regenerate
8. CLAUDE.md                     — only if a working rule or a pointer changed
9. dev/DocCheck.ps1              — run it; it must pass
```

### 5.6 `dev/sessions/TEMPLATE.md` — the session-record shape (Tier 3)

Records currently vary in shape, so harvesting them is archaeology. A fixed tail makes the next `GOTCHAS`
harvest mechanical instead of a grep-and-read. **This is the change that keeps the scheme cheap in six
months.** Four required sections:

1. **Delivered** — what shipped, per revision range.
2. **Decisions** — what was chosen and why, including anything reversed.
3. **Traps** — anything that cost time and would cost it again. *Each one is a `GOTCHAS` candidate.*
4. **Flagged & unverified** — judgement calls awaiting review, and claims not tested.

### 5.7 Task-indexed pointers in `CLAUDE.md` (Tier 0)

Index by **situation**, not by document. A reader arrives with a task, not with a wish to browse the doc
set — and this is what lets `CLAUDE.md` drop 16 inlined warnings without losing them.

```markdown
| If you are... | Read first |
|---|---|
| touching the renderer | GOTCHAS "order-dependent geometry", plans/PLAN_RENDER_PERFORMANCE.md |
| adding or changing a packet | WIRE_HISTORY.md, GOTCHAS "append-only registration" |
| adding player-facing text | GOTCHAS "SendIngameError is a lang key" |
| adding text to a dialog | GOTCHAS "static text bounds never grow" |
| adding a GUI element | GOTCHAS "allocate LoadedTexture first" |
| changing caps or limits | GOTCHAS "private guides are not capped" |
```

### 5.8 Known unverified claims

A short `STATUS.md` section listing what is *declared but not tested* — currently scattered. Seed with: the
32-chalk ceiling never tested against xskills; 1.22.0/1.22.1 support declared but built only against
1.22.3; the Players list's scroll container unverified outside the game.

---

## 6. `dev/DocCheck.ps1` — make the scheme self-policing

Everything else here is a convention, and conventions decay. This turns "easy to keep up to date" into a
command that fails. Same precedent as `RenderIcon.ps1`: dev-only, never shipped in the zip. **Mechanical
checks only — it must never try to judge prose.**

1. `modinfo.json` version matches the version quoted in `STATUS.md`.
2. Source-file count in `STATUS.md` matches `Get-ChildItem src -Recurse -Filter *.cs`.
3. `LayoutChannel.ProtocolVersion` matches `STATUS.md` and the last row of `WIRE_HISTORY.md`.
4. `CHANGELOG.md` has an entry for the current `modinfo.json` version.
5. Every `dev/sessions/SESSION_*.md` has a row in `INDEX.md`.
6. **No tracked file contains `C:\Users` or a personal username.** *(Publication safety — the repo is
   public. Highest-value check here.)* **This check runs repo-wide, `dev/archive/` INCLUDED** — the archive
   is tracked and therefore published, so a personal path in a frozen copy ships exactly as readily as one
   in a living file. Publication safety is not a currency check and the archive gets no exemption from it.
   *(Verified clean at 2026-07-30: `git grep "C:\Users"` returns nothing, and `dev/` is 37 tracked files.)*
7. **No non-ASCII byte in `modinfo.json` outside the BOM.** *(The description was double-encoded and shipped
   mojibake to players; caught in Session 34.)*
8. **No file outside `dev/archive/` links into `dev/archive/`** other than `dev/archive/README.md` itself.
9. Every file in `dev/archive/` carries the archived banner.
10. **Every pointer resolves.** Two parts, both purely mechanical:
    - Every relative markdown link and every backticked path in `CLAUDE.md`, `STATUS.md` and
      `dev/GOTCHAS.md` names a file that exists.
    - Every GOTCHAS trigger quoted in `CLAUDE.md`'s task index (§5.7) matches the text of a real
      `GOTCHAS.md` entry.

    **Resolve bare filenames by convention.** Tier 3 files moved intact in phase 3 still refer to
    `SESSION_28.md` and `PLAN_RENDER_PERFORMANCE.md` without a folder, because rewriting them would destroy
    the verbatim property that makes them trustworthy. The check must therefore resolve a bare
    `SESSION_*.md` against `dev/sessions/`, a bare `PLAN_*.md` against `dev/plans/`, and only then report a
    failure. **Living documents get no such latitude** — in `CLAUDE.md`, `STATUS.md` and `GOTCHAS.md` a path
    must be written out and must resolve as written.

    **This is the most valuable check in the list and the one the first draft omitted.** §0.1 names a
    document that "sends you somewhere else entirely" as the single highest cost in this scheme — and after
    the overhaul `CLAUDE.md` is almost nothing *but* pointers, indexed by hand-copied trigger strings.
    A broken pointer is that exact failure mode, made likelier by the very restructuring this plan performs,
    and it is catchable without judging one word of prose. Renaming a GOTCHAS entry and forgetting its index
    row is not a hypothetical: it is what §5.7 asks a future session to do by hand, every time.

Exit non-zero on any failure; print one line per problem. Apart from checks 6, 8 and 9 — which exist
*because* of the archive, not in spite of it — `dev/archive/` is excluded from every check.

---

## 7. Harvest safety protocol

Archiving (§2) makes loss impossible, but it does not make the **successor** complete. The real risk is a
trap that is archived and then never carried forward — present in the repo, absent from where anyone looks.
The Session-28 order-dependence constraint already had a near-miss: it lived inside `TODO` item A12 and had
to be rescued into its own entry when A12 was struck.

1. Baseline the count before anything moves — see §7.1, which is the authoritative figure.
2. Every one becomes a `GOTCHAS.md` entry **or** is consciously judged redundant with one, and the
   judgement is written into the harvest commit message.
3. **Session records keep their ⚠️ text.** Not duplication: the session holds the *narrative* ("here is what
   happened and how we found it"), `GOTCHAS` holds the *rule* ("do this, never that") plus a pointer back.
   Different content, different lifetime.
4. Re-run the count afterwards. It must not have dropped in any Tier 3 file.

### 7.1 The baseline — measured 2026-07-30, at v0.4.33

The 2026-07-29 figure quoted throughout the first draft of this plan (**45 across 10 files**) was **wrong**.
It missed `SESSION_9.md` and `SESSION_10.md` entirely, and understated `TODO.md` and `PROJECT_STATUS.md`.
Recorded here rather than silently corrected, because a harvest gate that starts from a short count is
precisely the near-miss this section exists to prevent — it would have declared success while four traps
in two never-inspected files sat outside the sweep.

**Count the same way both times.** Occurrences, not marker-lines:

```
grep -o "⚠" CLAUDE.md HANDOFF.md dev/*.md | wc -l
```

(As of 2026-07-30 the two metrics agree — no line carries two markers — but that is a coincidence, not a
guarantee. Note the pattern is the bare `⚠`, not `⚠️`: the marker appears both with and without its
variation selector, and matching the two-codepoint form silently misses the bare ones.)

| File | Markers |
|---|---|
| `CLAUDE.md` | 16 |
| `dev/TODO.md` | 7 |
| `dev/SESSION_34.md` | 7 |
| `HANDOFF.md` | 6 |
| `dev/SESSION_33.md` | 4 |
| `dev/SESSION_32.md` | 3 |
| `dev/SESSION_9.md` | 3 |
| `dev/PROJECT_STATUS.md` | 3 |
| `dev/SESSION_17.md` | 2 |
| `dev/ARCHITECTURE.md` | 1 |
| `dev/SESSION_10.md` | 1 |
| `dev/SESSION_26.md` | 1 |
| **Total** | **54 across 12 files** |

`dev/PLAN_DOC_OVERHAUL.md` carries a further 6 markers of its own. They are **excluded** from the
baseline — they are this document's prose, not harvestable traps. Total repo-wide is therefore 60.

**`SESSION_9.md` and `SESSION_10.md` must be read during the harvest**, not skipped as "early sessions
long superseded." They were never inspected for the 2026-07-29 list, so their four markers are the only
ones in the set whose content is entirely unreviewed.

**Known do-not-lose items** (non-exhaustive; the grep is authoritative):

- Guides are **order-dependent translucent geometry** — any change that regroups primitives changes the
  picture. (`TODO` A12a, `SESSION_28`, `CLAUDE.md`)
- **Private guides are deliberately NOT capped.** (`SESSION_33` §9)
- **`SendIngameError`'s message parameter is a LANG KEY**, not a format string. (`SESSION_33` §5)
- **Custom GUI elements must allocate their `LoadedTexture`** before `LoadOrUpdateCairoTexture`. Crashed
  v0.4.4. (`SESSION_32` §8)
- **A static text wraps but its bounds never grow.** Shipped three times in Session 34. (`SESSION_34` §7)
- **`parsers.OptionalFloat/OptionalInt` return their DEFAULT when absent, not null.** (`SESSION_29`)
- **Packet registration is append-only — never renumber.** (`SESSION_17`, `PacketTypes.cs`)
- **`SESSION_26.md` carries a correction banner**; three of its claims were disproved in Session 28.
- **`PLAN_BLOCK_OCCUPANCY.md`'s first draft was substantially wrong**, replaced not amended; §0 records it.
- **Vintage Story loads the HIGHEST version when several zips share a modid.**

---

## 8. Acceptance criteria

- [ ] `dev/archive/superseded-2026-07-30/` holds an intact copy of every overhauled document, each with the
      §2 banner, plus a `README.md`. **No original content exists only in git history.**
- [ ] The §7.1 marker count (54 across 12 files) has not decreased, counted the same way, and every
      pre-existing trap is either a `GOTCHAS` entry or a written-down judgement of redundancy.
- [ ] `CLAUDE.md` ≤ ~130 lines, contains no per-session narrative, and carries the task index.
- [ ] `dev/ARCHITECTURE.md` makes **no claim about the current state** — no current version, no date, no
      staleness banner, no "consolidated to current state" closer, and no version in its title.
      **Historical version citations in the Settled Decisions Register are correct and must stay**
      ("filled volumes retired in v0.2.17"): they record *when a decision was made or reversed*, which is
      permanent fact and cannot rot. The original criterion said "contains no version number" flatly, which
      would have condemned the single most valuable section in the file — the same blunt-criterion mistake
      as the `STATUS.md` line above. What rots is a claim about *now*, not a citation of *then*.
- [ ] `dev/TODO.md` ≤ ~150 lines and contains only open items.
- [ ] `STATUS.md` orients a reader on everything `HANDOFF.md` and `PROJECT_STATUS.md` were read *for*,
      and every fact it drops is either derivable from source in one tool call, rehomed into a Tier 1/3
      document, or sitting in the archive. **Not** "covers everything they did" — that was the original
      wording and it is arithmetically impossible: 1,176 lines do not survive into ~350. Stating it the
      honest way is what stops the criterion being quietly reinterpreted while phase 5 is in flight.
- [ ] Every session file is reachable from `dev/sessions/INDEX.md`.
- [ ] `dev/DocCheck.ps1` passes.
- [ ] `dotnet build -c Release` still clean — **no `src/` file was touched.**
- [ ] Doc commits contain no code changes.

---

## 9. Deliberately NOT doing

- **No deleting.** See §2. This overrides every efficiency argument in this document.
- **No docs site / mkdocs / generated HTML.** The audience is one human who does not read code and an AI
  reading raw markdown. Tooling adds a build step and buys nothing.
- **No auto-generated architecture from source.** Always ends up drifting or restating signatures. The
  blueprint's value is the *why*, which cannot be generated.
- **No splitting into many small files.** There is a floor: past a certain fragmentation nothing gets read.
  The scheme above is 8 living documents plus append-only archives.
- **No rewriting session records.** They are history. `SESSION_26`'s correction banner is the correct
  pattern for a record that turned out wrong — annotate, never revise.
- **No condensing old sessions.** With `INDEX.md` in place, 26 unread files cost nothing.
- **No change to the batching workflow.** Updating docs only on request is correct for the playtest loop.
  The fix for the one time `CHANGELOG` was missed is the checklist (§5.5) and `DocCheck.ps1` — not
  per-revision churn.

---

## 10. Expected result

| | Before | After |
|---|---|---|
| Always loaded per session | 376 lines | **~120** |
| Most-read on-demand file (`TODO`) | 1,118 lines | **~150** |
| Blueprint (`ARCHITECTURE`) | 1,117 lines | **~600**, no longer trailing |
| Status docs | 2 files, 1,176 lines, mutually redundant | **1 file, ~350**, regenerated |
| Traps | 45, scattered over 10 files | **1 indexed register** + a reversals section |
| Session archive | 26 files, no index | 26 files + **~30-line index** + a template |
| Originals | — | **All preserved in `dev/archive/`** |

Nothing is deleted. Nothing is lost. The only content that leaves a *living* document is either derivable
from source in one tool call, or already archived elsewhere — and now also sitting frozen in `dev/archive/`.
