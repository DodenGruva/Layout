# Session 35 — no version shipped (documentation and comments only)

**Branch:** `beta`. **DataVersion 13. Protocol 24.** **83 source files** (none added).

> **No `modinfo.json` bump and no zip**, by human decision on the day: the only `src/` change was two
> corrected comments, so the compiled DLL behaves identically and there was nothing to playtest. That
> exception is now written into `CLAUDE.md`'s versioning rule, because the rule as it stood said EVERY
> revision and the question would have recurred.
>
> **`main` was brought level with `beta` at v0.4.33** during this session — the first `main` release since
> v0.3.69.

---

## 1. What this session was

An audit of the Session-34 documentation overhaul against the source, requested one day after the overhaul
landed. The overhaul's own mechanical claims were checked rather than taken on trust, and then every living
document was read against the code it describes.

**The overhaul held up.** Verified independently: the six archived files are byte-identical to their
originals after their banners; `DONE.md` is a genuine verbatim extraction (1,018 of its lines appear
unchanged in the pre-split `TODO.md`); `CHANGELOG.md` covers 0.3.54–0.4.33 with no gaps and no duplicate
headings once ranged entries are expanded; and the 54 harvested trap markers are fully accounted for — 21
still in session records, 7 in `DONE.md`, and 26 frozen in the archive, which holds exactly those 26 plus
`TODO.md`'s 7.

**Everything the audit found was one defect wearing different clothes: a claim about the PRESENT tense left
in a document whose job is durable fact.** No decision was wrong. No reasoning was wrong. The scheme had
simply not finished being scrubbed.

## 2. What was wrong, and what was done about it

`dev/ARCHITECTURE.md` — the file whose banner promises it makes no current-state claim — carried five:

| Defect | Fix |
|---|---|
| DataVersion stated as "currently 12"; build was 13 | Replaced with a pointer to `GuideData.CurrentDataVersion`. **The number was removed, not corrected** — it had no business being in a durable document. |
| Transform mode absent; described a three-mode scheme | Added the mode, a §5 flow section, and the reasoning that admitted it |
| Rectangle/Box gestures were pre-v0.4.15, in four places | Corrected; the old two-corner diagonal gesture kept as a dated amendment, since it explains *why* the re-gesture happened |
| The volumetric z-fight world probe described as live | It was removed in v0.3.70. Recorded as **R9** |
| Surface slab quoted as 0.01; the constant is 0.0025 | Now names the constant instead of quoting a number |
| A pointer to an "IMPLEMENTED callout up top" the rewrite had deleted | Replaced with the actual chalk contract |

Elsewhere: `STATUS.md` listed the two chalk refill flags under the **server** config (they moved to the
client in v0.2.22); `GOTCHAS` G8 named a method that does not exist (`Build`, not `BuildGuideMesh`);
`INDEX.md` still told readers the wire ledger was unbuilt; `WIRE_HISTORY.md` was missing DataVersion 10;
and `PLAN_RENDER_PERFORMANCE.md` read *"Status: proposed, not started"* for work delivered in Session 28 —
while its own body, thirteen lines below, said "Stage 1 result — DELIVERED v0.3.57". That file is where
`CLAUDE.md` sends anyone touching the renderer.

## 3. Making it self-policing

`DocCheck.ps1` went from ten checks to thirteen. Every defect above was of a kind the original ten could not
see: they compare `STATUS.md` against source and leave the durable tier unguarded.

- **11 — no CURRENT wire number in a Tier 1 document.** Keys on a currency word next to a literal digit, so
  historical citations survive. Banning version numbers outright would condemn the Settled Decisions
  Register, whose citations record *when* and therefore cannot rot.
- **12 — a plan's status line must not contradict its body.** Judges only the status line, so a delivered
  plan may still say a later stage is unstarted.
- **13 — no mojibake in any tracked text file.**

## 4. `main` levelled at v0.4.33

`main` had sat at v0.3.69. Before merging, three things were checked: local `main` was two steps stale
(v0.3.54 against `origin/main`'s v0.3.69) and was fast-forwarded first; `origin/main` held one commit `beta`
lacked — the PR #2 merge of `beta-shader` — whose **both parents were already in `beta`**, so it carried no
content of its own; and the shader assets were byte-identical on both sides.

That mattered because a merge only blends files when both sides changed the same file. Nothing on `main`'s
side had. **After merging, `beta` and `main` had identical tree hashes** — verified before pushing, along
with the absence of any resurrected `Dev/` path, which on a case-insensitive filesystem was the real risk of
the `Dev/` → `dev/` rename.

---

## Delivered

- **Documentation audit** — seven living documents corrected against source; see §2.
- **`GOTCHAS` R9** — the volumetric z-fight world probe, removed v0.3.70, with both reasons it must not
  return.
- **`GOTCHAS` G26** — the PowerShell encoding round-trip.
- **`DocCheck.ps1` checks 11, 12, 13**, all negative-tested by replanting the exact original faults.
- **`main` merged to v0.4.33** (§4), the first `main` release since v0.3.69.
- **Two stale source comments corrected** — `LayoutClientConfig`'s `colorScheme` (documented a retired
  scheme value that players read when hand-editing) and `GuideMeshBuilder`'s palette-guarantee comment.
- **`.gitattributes`** — pins LF in the index, verified content-neutral before committing.
- **`dev/plans/PLAN_CODE_REVIEW.md`** — the brief for `TODO` A10.1.

## Decisions

- **A change with no behaviour to test does not bump the version** (human-decided). Written into
  `CLAUDE.md` so it is not re-litigated. When in doubt, bump.
- **Remove a rotting claim rather than correct it.** `ARCHITECTURE.md` now points at
  `GuideData.CurrentDataVersion` instead of naming a number. A durable document should not make the claim at
  all, which is stricter *and* cheaper than keeping a number accurate.
- **Annotate Tier 3, never revise it.** `CHANGELOG_ARCHITECTURE.md`'s banner became partly false when
  `ARCHITECTURE.md`'s §1 and §3 were removed; it got a dated annotation, following the `SESSION_26.md`
  precedent, rather than an edit to its body.
- **Run the adversarial code review in a FRESH session.** Reasoned in `PLAN_CODE_REVIEW.md` §1; the argument
  is bias, not context budget. This session spent itself confirming, and it authored the very entries a
  review of the rendering and transform paths would be checking.
- **When a check false-positives, fix the check.** Check 12 flagged the newly written `PLAN_CODE_REVIEW.md`
  for using "delivered" in ordinary prose. It now requires the word to sit next to a version number — which
  is what makes it a status assertion rather than a sentence. The document was not contorted to satisfy it.

## Traps

⚠️ **A `Get-Content` / `Set-Content` round-trip destroys every non-ASCII character in the file.** Every
markdown file here is UTF-8 with no BOM and all of them contain em-dashes; 61 `.cs` files contain non-ASCII
too. PowerShell 5.1 decodes a BOM-less file as ANSI. This corrupted `ARCHITECTURE.md` mid-session and had to
be restored from a backup. Now **`GOTCHAS` G26**, enforced by check 13.

⚠️ **A checker must not contain the bytes it hunts.** Writing the three mojibake signature characters
literally into `DocCheck.ps1` — which had been pure ASCII until then — made PowerShell read them as ANSI and
kill the script on its own regex. The pattern is `\u` escapes now. Same rule as check 6 and personal paths.

⚠️ **`grep -P` matches BYTES, not characters, so a mojibake scan written that way false-alarms on every
correctly-encoded file.** A legitimate em-dash *is* the byte sequence `E2 80 94`, which satisfies a pattern
looking for `â` followed by a non-ASCII byte. The verification sweep reported mojibake in 100+ files
including all of `src/`; the files were fine and the sweep was wrong. Check 13 is correct because it reads
the **decoded string**, where an em-dash is one character and cannot collide. Re-run character-wise, the
tree had exactly one hit — a deliberate quotation inside backticks.

⚠️ **A negative test only proves the check catches the fault you already have.** Checks 11, 12 and 13 were
all negative-tested against replanted real defects and all passed. Check 12 then false-positived on the very
next new document written. Check 11 also failed twice before it worked — first by stripping the code spans
containing the token it needed, then by using `\W` where it needed `.` — and neither failure would have been
visible in a passing run.

## Flagged and unverified

- **Check 12's looser form is untested against future plans.** It now requires a version number adjacent to
  DELIVERED/SHIPPED. That was re-tested both ways on the known defect, but the class of documents it might
  wrongly pass has not been explored.
- **The XML doc comments on the pinned enums and config classes have not been swept.** Two were found wrong
  in passing. See `TODO` A13 — this matters more than it used to, because the overhaul deleted
  `ARCHITECTURE.md`'s module map on the grounds that source is authoritative.
- **The 1.22.0 verification debt is more exposed than it was.** `modinfo.json` declares a 1.22.0 minimum,
  the code is built against 1.22.3, and `main` is now public at v0.4.33 rather than v0.3.69. Unchanged as a
  fact; larger as a risk. `STATUS.md` §6.2.
- **No playtest occurred this session** — correctly, since nothing behavioural changed.

---

## When the session ends — the doc-update ritual

Run the checklist in `CLAUDE.md`. This session: record → `INDEX.md` → no `CHANGELOG.md` entry (nothing
shipped) → `GOTCHAS.md` (R9, G26) → no `WIRE_HISTORY.md` change → `TODO.md` → `STATUS.md` → `CLAUDE.md`
(the versioning exception and one task-index row) → `DocCheck.ps1` passes.
