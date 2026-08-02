# Layout — STATUS

> **Tier 2 — current state. Regenerated wholesale each session, never amended in place.**
>
> This is the compact handoff for what is true now. Durable design lives in `dev/ARCHITECTURE.md`, traps in
> `dev/GOTCHAS.md`, open work in `dev/TODO.md`, wire history in `dev/WIRE_HISTORY.md`, and full narrative in
> `dev/sessions/`.

**Regenerated at:** v0.4.59, 2026-08-02 (Session 41 — independent review hardening, transactional Transform,
deep immense-operation cancellation, and footprint-bounded claim consistency).

---

## 1. Product and release state

Layout is a **Vintage Story 1.22.x** mod written in C# for **.NET 10**. It is a voxel-resolution construction
planning tool: players place translucent guides and build against them by hand. Layout is visual-only and
does not place, remove, or modify world blocks. Public guides are server-authoritative and shared; F4 also
supports private client-authoritative guides.

| | |
|---|---|
| **Current build** | **v0.4.59** on `codex/review-hardening-v055` |
| **Last `main` release** | **v0.4.33**; Sessions 37–41 have not been merged to `main` |
| **Published repository** | `github.com/DodenGruva/Layout` — public; tracked files must contain no personal paths or usernames |
| **Do not use** | **v0.4.48** (unsafe claim shortcut, reverted in v0.4.49) or **v0.4.50–v0.4.52** (background-save defects corrected by v0.4.53–v0.4.54) |
| **Release artifact** | `Layout0.4.59.zip`, 42 entries / 368,115 bytes; 39/39 assets; packaged DLL SHA-256 `50C92417D16BE059104E46E0C4B40479CF61C1BC07ADC6A0390EE16C39E57307` |

The human validates primarily by playing. v0.4.42's chiselling highlights were confirmed good, and reload
after editing was confirmed around v0.4.45. The human explicitly said they were away during the v0.4.55 and
v0.4.57 implementation passes. No dedicated-server result has been reported for v0.4.58–v0.4.59's claim
consistency changes. See §6 for focused verification debt rather than inferring playtest status elsewhere.

⚠️ **Claim protection cannot be meaningfully tested from singleplayer.** The host holds `controlserver`, and
Layout deliberately exempts that privilege from claim validation. Use a non-admin account on a dedicated
server. `dev/sessions/SESSION_39.md` §4.

### Threat model

Layout is public, so a modified client is in scope. Likelihood controls priority, not whether a finding is
real. Every hostile-client item from the Session-36 reviews is closed. Coordinate range checks protect packet,
save and private-file seams as well as whole-guide mutations; the remaining scan-loop rewrite is a measured
defence-in-depth deferral (`dev/GOTCHAS.md` G31).

---

## 2. Wire, save and inventory state

| | |
|---|---|
| **DataVersion** | **13** |
| **Wire protocol** | **26** |
| **Source files** | **86** |
| **Shape catalog** | **15 types / 21 picker tiles** |

Packet registration and wire enum values are append-only. Retired slots remain declared padding. No wire or
save-format value changed in Session 41; `dev/WIRE_HISTORY.md` remains the ledger.

Coordinates are checked at every untrusted source and after every whole-guide operation that synthesizes a
new position. The shared authority is `src/Guide/GuideBounds.cs`. Projection mode, axis and plane offsets use
the same rule, including dormant Volumetric plane state that later movement still carries.

Public guides persist with the world save. The server snapshots the live registry on the main thread, converts
that private copy to JSON on a worker, and returns bytes ahead of the next save. If no prepared bytes exist,
the save writes synchronously; shutdown also writes the live registry synchronously. `/layout info` exposes
prepared/fallback save counts and lead adaptation. Private F4 guides remain in their separate atomic client
file with backup and corrupt-file quarantine.

---

## 3. Session 41 delivered (v0.4.55–v0.4.59)

- **v0.4.55 — whole-guide hardening.** Move, Rotate, Transform, Copy and projection changes validate the
  finished point/plane state before scan, claim check, save or broadcast. Combined rotate + mirror + move is
  one transaction, one rollback boundary, one full-state broadcast and one Undo/Redo step for both public and
  private authority.
- **v0.4.56 — hostile no-op validation.** Invalid mirror sentinels and active rotation axes are rejected
  before the legitimate no-change fast path. Rotation axis is intentionally ignored when normalized turns are
  zero, matching the packet contract.
- **v0.4.57 — deep cancellation.** Cancelling an active immense create/sculpt reaches threshold counting,
  exact voxel generation, fallback marching, marker assignment and claim-footprint collapse for all eight
  volume variants. Partial results never acquire an exact/empty/over-cap meaning (`dev/GOTCHAS.md` G45).
- **v0.4.58 — structural claim consistency.** The count-only change detector was replaced by an immutable
  snapshot of observable built-in claim/player authorization state. Equal-count replacement, in-place resize
  and relevant player-state changes restart sliced validation. The snapshot is checked before each later
  slice and after the final slice; a fourth relevant change after three restarts fails closed as busy.
- **v0.4.59 — footprint-bounded claim consistency.** Snapshot state is limited to claims intersecting the
  exact block footprint, including Surface-adjacent checks. Intersecting geometry is clipped to the bound;
  distant claims cannot restart the operation and are not copied or permission-tested. The API has no regional
  query, so all claim-area bounds still receive a cheap intersection test. Exact per-block `TestAccess` is
  unchanged, preserving privilege, life-state and other-mod denials (`dev/GOTCHAS.md` G40, G47 and R12).

### Measured claim-snapshot cost

Focused Release harness, 10,000 synthetic claims, one relevant claim:

| Design | Time per capture | Allocation | Correctness |
|---|---:|---:|---|
| Former count only | ~3.3 ns | 0 B | Unsound: misses equal-count replacement and in-place resize |
| v0.4.58 global structural snapshot | 2.50–3.10 ms | 4.28 MB | Correct but needlessly global |
| v0.4.59 footprint-bounded snapshot | 0.10–0.14 ms | ~820 B | Correct for relevant state; distant changes ignored |

The existing exact access walk remains separately bounded to at most 128 blocks or about 1 ms per 20 ms
server tick. These are harness measurements, not a dedicated-server frame trace.

---

## 4. Open work

`dev/TODO.md` is the open-item authority. The review backlog is empty. Session 40's background-save work and
Session 41's review/cancellation/claim work are delivered; the next implementation is whatever the human
chooses.

The largest remaining work is verification rather than a known code defect:

1. Dedicated-server non-admin claim behavior, including a relevant claim replacement/resize or player-state
   change during an immense check and the fourth-change busy refusal.
2. Session 40 background-save rhythm and shutdown behavior in play, using `/layout info` and the server log.
3. Public and private compound Transform: one Undo and one Redo; a denied public operation must leave every
   client on the original geometry.
4. Cancel/disconnect/shutdown while a live immense operation is inside count/materialization/footprint work.

Deliberate deferrals remain under `dev/TODO.md` A15. Do not re-propose per-guide persistence “index cards”
without reading `dev/GOTCHAS.md` G42; the save API is one blob with no key enumeration/deletion.

---

## 5. Operating invariants

- Public authority validates bounds, caps, locks, claims and input domains before commit. Rejections return a
  corrective full-state resync to the requester.
- One immense public create/sculpt lane runs at a time. Pure geometry works below normal priority; claims are
  checked in bounded main-thread slices; cancellation is cooperative inside the deepest expensive loops.
- Claim snapshot bounds determine which changes can make sliced work stale. They never determine permission.
  Every footprint block still passes through the full engine/mod `TestAccess` chain.
- The final sculpt commit repeats privilege and jail checks. Player-global claim snapshot state also includes
  identity, game mode, alive state, build privilege, role privilege and groups.
- Public guide mutations mark persistence dirty; they do not serialize the whole registry per edit. Shutdown
  uses the live registry, superseding any older background snapshot.
- Private F4 guides consume neither shared storage nor other clients' rendering, so server caps deliberately
  do not apply (`dev/GOTCHAS.md` R1). The hard physical voxel ceiling still applies.
- The renderer's order-dependent geometry and progressive cancellation contracts remain unchanged. Read
  `dev/GOTCHAS.md` G2 and `dev/plans/PLAN_RENDER_PERFORMANCE.md` before renderer changes.

---

## 6. Verification evidence and debts

### Evidence for v0.4.59

- Debug and Release builds: **0 warnings, 0 errors**.
- Focused disposable harness: **29/29 groups**, including 648 compound Transform/Undo/Redo combinations;
  ordinary/cancellable count and exact-voxel equivalence; prompt cancellation in all volume variants;
  relevant equal-count replacement, resize and authorization changes; min/max boundary semantics; clipped
  outside extensions; projection-adjacent cells; and distant add/remove/resize exclusion.
- `git diff --check`: pass.
- `modinfo.json`: ASCII check pass.
- Release archive: root metadata first, root DLL, 39/39 assets, no directory entries or backslash paths,
  embedded v0.4.59 metadata, and packaged DLL hash recorded in §1.

### Still needs play

- Dedicated-server claim refusal and mid-validation change handling. Singleplayer cannot prove this path.
- Compound Transform's public/private one-step Undo/Redo feel and connected-client rollback on refusal.
- Live cancellation from cancel, disconnect and shutdown while an immense worker is deep in geometry.
- Session 40 save lead adaptation, synchronous fallback and shutdown save behavior.
- Filled Arch count/appearance after v0.4.47; guide-count HUD refusal after v0.4.46; shape preference restore
  after v0.4.43; the v0.4.40 GUI fixes as a set; xskills interaction with the 32-chalk ceiling; 1.22.0/1.22.1
  smoke tests; Players-list scrolling; and the diagnostic occupancy cases retained in `dev/TODO.md`.

No permanent automated test project was added. The focused harness is disposable and outside the public
repository, so its results are evidence for this change set, not a continuing regression suite.

---

## 7. Documentation discipline

After any shipped version: update the session record, session index, `CHANGELOG.md`, new traps/reversals,
open/delivered ledgers, then regenerate this file and run `dev/DocCheck.ps1`. Update
`dev/WIRE_HISTORY.md` only when wire protocol or DataVersion changes. Package only after Debug/Release and
archive verification. Commit and push only when the human explicitly requests them.
