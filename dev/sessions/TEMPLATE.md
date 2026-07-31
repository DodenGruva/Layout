# Session &lt;n&gt; — v&lt;first&gt; → v&lt;last&gt;

**Branch:** `<branch>`. **DataVersion &lt;x&gt; [→ &lt;y&gt;]. Protocol &lt;x&gt; [→ &lt;y&gt;].** **&lt;n&gt; source files** (&lt;none/one&gt; added).

> **Why this template exists.** Session records used to vary in shape, which made harvesting them
> archaeology — the 2026-07-30 `GOTCHAS.md` harvest had to read twelve files by hand and still found two
> that had never been inspected at all. **The four sections below are required and must keep these names.**
> A fixed tail is what keeps the next harvest mechanical instead of a grep-and-read.
>
> Write freely above them: narrative, per-version detail, whatever the session needs. The tail is the
> contract; the body is yours.

---

## &lt;free-form body&gt;

Whatever the session needs — the arc, the reasoning, the per-revision detail. Number the sections so
`GOTCHAS.md` and `INDEX.md` can cite them (`SESSION_34.md` §7).

---

## Delivered

What shipped, **per revision range**. One line each. This is what `CHANGELOG.md` and `INDEX.md` are written
from, so make it factual rather than narrative.

- **v0.x.y** — …
- **v0.x.z–v0.x.w** — …

## Decisions

What was chosen and **why** — including anything reversed, and *especially* anything reversed.

A decision that reversed an earlier one is the highest-value thing in this document: it is a candidate for
`GOTCHAS.md`'s **Reversals** section, which exists to stop the next session re-implementing something that
was removed on purpose. If you reversed something, say what the original reasoning was and why it was wrong
— not just that it changed.

## Traps

Anything that cost time and would cost it again. **Each one is a `GOTCHAS.md` candidate**, so write it in a
form that can be lifted: what triggers it, what goes wrong, what to do instead.

Mark them with ⚠️ in the text. The harvest greps for that marker and the count is a gate — see
`dev/plans/PLAN_DOC_OVERHAUL.md` §7. Note the marker appears both with and without its variation selector;
either is found, because the grep matches the bare `⚠`.

**This template's own markers are not traps** and must be excluded from any harvest count, exactly as
`PLAN_DOC_OVERHAUL.md`'s were. So are the markers in `GOTCHAS.md`, `STATUS.md`, `WIRE_HISTORY.md` and
`INDEX.md` — those are living documents quoting rules, not session records recording them. **A harvest
counts session records and `history/DONE.md`.**

**Keep the ⚠️ text here even after it becomes a `GOTCHAS` entry.** That is not duplication: this file holds
the *narrative* — here is what happened and how we found it — while `GOTCHAS.md` holds the *rule*, plus a
pointer back. Different content, different lifetime.

## Flagged and unverified

Two kinds, and keep them apart:

- **Judgement calls awaiting review** — made under the standing "decide, note for review" rule. Cheap to
  reverse; none should block play. These are indexed in `dev/TODO.md`.
- **Claims not tested** — anything declared but not actually verified. These go to `STATUS.md` →
  *Known unverified claims*.

⚠️ **Do not write "not playtested" on an assumption.** The human playtests every iteration as it ships —
that is what the per-revision zip is for. Treat each shipped version as tested unless they say otherwise.
Session 32 recorded the opposite, wrongly, and it had to be corrected across four files; an untrue
"unverified" banner would have sent a later session re-testing settled work. See `GOTCHAS.md` R7.

---

## When the session ends — the doc-update ritual

Run the checklist in `CLAUDE.md`. In short: this record → `INDEX.md` → `CHANGELOG.md` → `GOTCHAS.md` →
`WIRE_HISTORY.md` (only if the wire moved) → `TODO.md` (delivered items move to `history/DONE.md`) →
regenerate `STATUS.md` → `CLAUDE.md` only if a rule or pointer changed → run `DocCheck.ps1`.
