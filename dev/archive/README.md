# `dev/archive/` — frozen documents

> 🗄️ **Everything under this folder is SUPERSEDED and FROZEN. None of it is current.**

## What this folder is

When a document is rewritten, split, folded into another, or otherwise superseded, **a copy of it is placed
here first, intact, before anything else touches it.** Nothing in this project's documentation is ever
deleted — that is a standing, human-set constraint, not a preference to be optimised away.

Git history already preserves every past version, but that is deliberately **not** considered sufficient
here: the person who most needs the reassurance does not use `git show`, and a working-tree copy is a
working-tree copy.

## The rule

**Nothing outside this folder may link into it as a source of current truth.** This README is the only file
permitted to reference the archive's contents.

If you have arrived at a file in here from somewhere else, that link is a bug — the living document should
have pointed you at a living successor. `dev/DocCheck.ps1` enforces this.

Read a file in here only when you want to know **what a document used to say**. Never to find out what is
true now.

## Why a frozen copy is safe

A superseded document is dangerous only if it can be mistaken for a current one. Three things prevent that,
and all three are required:

1. **The folder name carries the verdict** — `superseded-<date>`, never `old` or `backup`.
2. **Every file carries a banner** as its first lines, prepended at archive time, stating the date, the
   version it was frozen at, and its successor.
3. **This README**, stating the rule above.

**The banner is the only permitted modification to an archived file.** Beyond it, every original byte is
preserved exactly — verified by byte comparison at archive time, not assumed.

Archived files are excluded from every **currency** check, because they are supposed to be stale; that is
the entire point. They are **not** excluded from **publication-safety** checks — this repository is public,
and a personal path in a frozen copy ships exactly as readily as one in a living file.

---

## `superseded-2026-07-30/` — the documentation overhaul

Snapshot of the documentation set exactly as it stood at **v0.4.33**, immediately before the overhaul
planned in `dev/plans/PLAN_DOC_OVERHAUL.md`. Taken 2026-07-30.

| Frozen file | Superseded by |
|---|---|
| `CLAUDE.md` | `CLAUDE.md` — rewritten, task-indexed, per-session narrative removed |
| `HANDOFF.md` | `STATUS.md` |
| `PROJECT_STATUS.md` | `STATUS.md` |
| `ARCHITECTURE.md` | `dev/ARCHITECTURE.md` — rewritten as a blueprint; file structure and module map dropped as derivable from source |
| `TODO.md` | `dev/TODO.md` (open items) + `dev/history/DONE.md` (delivered) |
| `BUILD_INSTRUCTIONS.txt` | the *Build and run* section of `CLAUDE.md` |

### What was deliberately NOT archived here, and why

Recorded so that an absence reads as a decision rather than an oversight:

- **`CHANGELOG.md`** — kept in place and effectively unchanged. It was already a correct append-only
  history; the overhaul only dropped a permanently-empty `## Unreleased` heading. Not rewritten, split,
  folded or superseded, so there is nothing to freeze.
- **`dev/CHANGELOG_ARCHITECTURE.md`**, **`dev/SESSION_*.md`**, **`dev/PLAN_*.md`** — **moved**, with their
  content untouched, into `dev/history/`, `dev/sessions/` and `dev/plans/`. A file that survives intact at a
  new path has not been superseded and needs no frozen twin; archiving it would create two identical copies
  and make it ambiguous which one is live.
- **`dev/RenderIcon.ps1`** — tooling, not documentation.

Session records in particular are **history, and history is not revised.** Where one turned out to be wrong,
the correct treatment is to annotate it — see the correction banner on `SESSION_26.md` — never to rewrite it
and never to retire it here.
