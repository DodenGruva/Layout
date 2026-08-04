# Layout — STATUS

> **Tier 2 — current state. Regenerated wholesale each session, never amended in place.**
>
> This is the compact handoff for what is true now. Durable design lives in `dev/ARCHITECTURE.md`, traps in
> `dev/GOTCHAS.md`, open work in `dev/TODO.md`, wire history in `dev/WIRE_HISTORY.md`, and full narrative in
> `dev/sessions/`.

**Regenerated at:** v0.4.71, 2026-08-04 (Session 42 — Roundover control redesign and universal initial-placement
modifiers).

---

## 1. Product and release state

Layout is a **Vintage Story 1.22.x** mod written in C# for **.NET 10**. It is a voxel-resolution construction
planning tool: players place translucent guides and build or chisel against them by hand. Layout does not
place, remove, or modify world blocks. Public guides are server-authoritative and shared; F4 also supports
private client-authoritative guides.

| | |
|---|---|
| **Current build** | **v0.4.71** on `Codex`, tracking `origin/Codex` |
| **Last `main` release** | **v0.4.33**; Sessions 37–42 have not been merged to `main` |
| **Published repository** | `github.com/DodenGruva/Layout` — public; tracked files must contain no personal paths or usernames |
| **Do not use** | **v0.4.48** (unsafe claim shortcut, reverted in v0.4.49) or **v0.4.50–v0.4.52** (background-save defects corrected by v0.4.53–v0.4.54) |
| **Superseded controls** | **v0.4.65–v0.4.66** were playable Roundover experiments replaced in v0.4.67 (`dev/GOTCHAS.md` R13) |
| **Release artifact** | `Layout0.4.71.zip`, 42 entries / 374,949 bytes; 39/39 assets; archive SHA-256 `7E1F021BA3B09DAA1F5F977BC818224AD055520950809DD23422CE91D68FC86A` |

The human validates primarily by playing. The corrected rolling transition and exact guide/micro-block
registration from Session 41 are play-confirmed. In Session 42 the Radius field and corner-probing approaches
were rejected in play; the final profile-first Roundover was reported to work “MUCH better.” Its three-rail
wireframe was then refined from direct play feedback. Treat shipped iterations as played unless the human says
otherwise (`dev/GOTCHAS.md` R7).

⚠️ **Claim protection cannot be meaningfully tested from singleplayer.** The host holds `controlserver`, and
Layout deliberately exempts that privilege from claim validation. Use a non-admin account on a dedicated
server. `dev/sessions/SESSION_39.md` §4.

### Threat model

Layout is public, so a modified client is in scope. Coordinates and enum domains are validated at packet,
save, private-file and whole-guide mutation seams. The remaining scan-loop rewrite is a measured
defence-in-depth deferral (`dev/GOTCHAS.md` G31), not an exposed entry point.

---

## 2. Wire, save and catalog state

| | |
|---|---|
| **DataVersion** | **13** |
| **Wire protocol** | **28** |
| **Source files** | **87** |
| **Shape catalog** | **16 types / 22 picker tiles** |

Packet registration and wire enum values are append-only. Protocol 27 introduced `Roundover = 15`; protocol
28 gives its existing chain field the profile-first meaning. New Roundovers carry the open route followed by
two terminal Primary profile handles. `Closed = true` selects that constructor only at creation; persisted
closed state remains a Free-Shape property. Legacy Roundovers have one terminal Primary handle and keep their
original geometry (`dev/GOTCHAS.md` G49). Public profile-first creation is gated to protocol-28 servers;
private client-authoritative placement remains local. No packet or DataVersion was added.

Public guides persist with the world save. The server snapshots the registry on the main thread, serializes
the private copy on a worker, and prepares bytes ahead of the next save; missing prepared bytes and shutdown
fall back to a synchronous live-registry write. `/layout info` exposes the save counters and learned lead.
Private F4 guides remain in their atomic client file with backup and corrupt-file quarantine.

---

## 3. Session 42 delivered (v0.4.65–v0.4.71)

- **v0.4.65 — Radius field experiment.** Added a GUI spinner matching Sides/Divisions. Play showed that a
  visible number did not make the spatial construction intuitive; it was removed.
- **v0.4.66 — corner-probing experiment.** Inferred interior/exterior behavior from nearby material. Partial
  and chiselled blocks made inference less dependable than direct input; it was removed.
- **v0.4.67 — exact profile-first Roundover.** Click the sharp corner, two exact profile endpoints, then the
  unsnapped open sweep. Clicking the final route point again places. Both profile controls remain editable;
  legacy one-handle guides remain readable; public creation requires protocol 28.
- **v0.4.68 — material-side point placement and clear aiming.** SHIFT moves a Roundover point one guide cell
  into material (half a cell for Surface projection). The live sweep uses wireframe after the profile exists.
- **v0.4.69 — three-rail wireframe.** Replaced the floating rounded midline with both swept profile endpoints
  and the sharp-corner route. Draft, adaptive, settled and grabbed paths share the special case.
- **v0.4.70 — bypass existing guides.** Idle Create-mode CTRL+Left-click skips Layout's guide hit test and
  starts the new anchor on the actual selected block. Active draft/grab CTRL behavior is unchanged.
- **v0.4.71 — universal embedding.** First-click SHIFT embeds every guide and persists that material-side
  choice for the draft. Existing later-stage SHIFT modifiers remain; CTRL+SHIFT can bypass a guide and embed.
  Held help reads “Embed Guide” and “Bypass Grab and Ignore Existing Guides.”

The settled design is explicit construction geometry, not a radius number, inferred corner class, floating
midpoint, or snap. See `dev/GOTCHAS.md` R13 before revisiting the gesture.

---

## 4. Open work

`dev/TODO.md` is the open-item authority. The review backlog is empty. Session 40's background save, Session
41's authority/claim hardening and Session 42's Roundover controls are delivered. No implementation is queued
behind them; the next feature is the human's choice.

The largest remaining work is verification rather than a known code defect:

1. Dedicated-server non-admin claim behavior, including relevant claim replacement/resize or player-state
   change during an immense check and the fourth-change busy refusal.
2. Session 40 background-save rhythm and shutdown behavior, using `/layout info` and the server log.
3. Public/private compound Transform: one Undo and one Redo; a denied public operation must leave every
   connected client on the original geometry.
4. Cancel, disconnect or shutdown while an immense operation is deep in geometry work.

Deliberate deferrals remain under `dev/TODO.md` A15. Do not re-propose per-guide persistence “index cards”
without reading `dev/GOTCHAS.md` G42; the save API is one blob with no key enumeration or deletion.

---

## 5. Operating invariants

- Public authority validates bounds, caps, locks, claims and input domains before commit. Rejections return a
  corrective full-state resync to the requester.
- One immense public create/sculpt lane runs at a time. Geometry runs below normal priority; claim checks use
  bounded main-thread slices; cancellation reaches the deepest expensive volume loops.
- Claim snapshot bounds determine which changes can make sliced work stale, never permission. Every footprint
  block still passes through the complete engine/mod `TestAccess` chain.
- Public guide mutations mark persistence dirty rather than serializing the registry per edit. Shutdown uses
  the live registry, superseding any older background snapshot.
- Private F4 guides consume neither shared storage nor other clients' rendering, so server caps deliberately
  do not apply (`dev/GOTCHAS.md` R1). The hard physical voxel ceiling still applies.
- Renderer primitive ordering and progressive cancellation contracts remain unchanged. Read
  `dev/GOTCHAS.md` G2 and `dev/plans/PLAN_RENDER_PERFORMANCE.md` before renderer work.
- Guide model matrices are exact world translations. Anti-z-fight clearance is an exposed-face outset; never
  reintroduce a camera-relative whole-mesh displacement (`dev/GOTCHAS.md` G48).
- Profile-first Roundover compatibility depends on two terminal Primary roles, not stored closed state
  (`dev/GOTCHAS.md` G49).

---

## 6. Verification evidence and debts

### Evidence through v0.4.71

- Final Release build: **0 warnings, 0 errors**.
- `git diff --check`: pass before documentation.
- Final archive: 42 entries, root metadata and DLL, 39/39 assets, no directory or backslash-path entries,
  embedded v0.4.71 metadata, and packaged DLL matching Release.
- Session 41's disposable harness results remain: 29/29 authority/claim groups and 52/52 Roundover geometry
  groups. No permanent test project was added.
- Direct play selected the profile-first gesture over both prior experiments and drove the material-side and
  three-rail preview refinements.

### Still needs play

- Dedicated-server claim refusal and mid-validation change handling; singleplayer cannot prove this path.
- Compound Transform's public/private one-step Undo/Redo and connected-client rollback on refusal.
- Live cancellation from cancel/disconnect/shutdown during immense geometry.
- Session 40 save lead adaptation, synchronous fallback and shutdown save behavior.
- Filled Arch count/appearance after v0.4.47; guide-count HUD refusal after v0.4.46; shape preference restore
  after v0.4.43; the v0.4.40 GUI fixes as a set; xskills interaction with the 32-chalk ceiling; 1.22.0/1.22.1
  smoke tests; Players-list scrolling; and diagnostic occupancy cases retained in `dev/TODO.md`.
- Broader Roundover routes and combinations of universal embedding with every guide/projection mode remain
  normal feature testing, not known defects.

---

## 7. Documentation discipline

After a shipped version: update the session record, session index, `CHANGELOG.md`, new traps/reversals,
`dev/WIRE_HISTORY.md` if wire moved, open/delivered ledgers, then regenerate this file and run
`dev/DocCheck.ps1`. Package only after Release and archive verification. Commit and push only when the human
explicitly requests them.
