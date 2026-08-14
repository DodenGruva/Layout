# Layout — STATUS

> **Tier 2 — current state. Regenerated wholesale each session, never amended in place.**
>
> This is the compact handoff for what is true now. Durable design lives in `dev/ARCHITECTURE.md`, traps in
> `dev/GOTCHAS.md`, open work in `dev/TODO.md`, wire history in `dev/WIRE_HISTORY.md`, and full narrative in
> `dev/sessions/`.

**Regenerated at:** v0.4.82, 2026-08-14 (Session 45 — plane-aware Arch/Half-circle geometry).

---

## 1. Product and release state

Layout is a **Vintage Story 1.22.x** mod written in C# for **.NET 10**. It is a voxel-resolution construction
planning tool: players place translucent guides and build or chisel against them by hand. Layout does not
place, remove, or modify world blocks. Public guides are server-authoritative and shared; F4 also supports
private client-authoritative guides.

| | |
|---|---|
| **Current build** | **v0.4.82** on `main`, tracking `origin/main` |
| **Last `main` release** | **v0.4.82** (Session 45) |
| **Published repository** | `github.com/DodenGruva/Layout` — public; tracked files must contain no personal paths or usernames |
| **Do not use** | **v0.4.48** (unsafe claim shortcut, reverted in v0.4.49) or **v0.4.50–v0.4.52** (background-save defects corrected by v0.4.53–v0.4.54) |
| **Superseded controls** | **v0.4.65–v0.4.66** Fillet experiments (`dev/GOTCHAS.md` R13); general nearest-handle body grabs retired in v0.4.72 (R14) |
| **Release artifact** | `Layout0.4.82.zip`, 42 entries / 376,160 bytes; 39/39 assets; archive SHA-256 `8FD7AFB99583594DD1B59BD11CB409B7B7DAE2282DCFD37E779EF92AA00D4F1B` |

The human validates primarily by playing. Profile-first Fillet placement and exact targeting were reported
excellent. Treat shipped iterations as played unless the human says otherwise (`dev/GOTCHAS.md` R7).

⚠️ **Claim protection cannot be meaningfully tested from singleplayer.** The host holds `controlserver`, and
Layout deliberately exempts that privilege from claim validation. Use a non-admin account on a dedicated
server. `dev/sessions/SESSION_39.md` §4.

### Threat model

Layout is public, so a modified client is in scope. Coordinates and enum domains are validated at packet,
save, private-file and whole-guide mutation seams. Shape-specific capabilities are authority rules, not
trusted client conventions: arbitrary body insertion is accepted only for Arch and Free-Shape
(`dev/GOTCHAS.md` G51).

---

## 2. Wire, save and catalog state

| | |
|---|---|
| **DataVersion** | **14** |
| **Wire protocol** | **29** |
| **Source files** | **87** |
| **Shape catalog** | **16 types / 22 picker tiles** |

Protocol 29 appends `GuideDataDto.ArchUsesShapePlaneAxis` at protobuf field 26. For Arch/Half-circle, true
makes the already-stored `ShapePlaneAxis` define the guide's curve plane. Absent/false retains the legacy
world-vertical interpretation. DataVersion 14 records the same saved-geometry distinction. This explicit bit
prevents old guides from silently changing shape and keeps legacy data read in place on renderer workers.

Protocol 27 introduced internal `Roundover = 15`; protocol 28 gives its existing chain field the
profile-first meaning. New Fillets carry the open route followed by two terminal Primary profile handles.
`Closed = true` selects that constructor only at creation; persisted closed state remains a Free-Shape
property. Legacy one-handle Roundovers keep their original geometry (`dev/GOTCHAS.md` G49). Fillet remains a
display name; save and wire identifiers are still Roundover.

Public guides persist with the world save. The server snapshots the registry on the main thread, serializes
the private copy on a worker, and prepares bytes ahead of the next save; missing prepared bytes and shutdown
fall back to a synchronous live-registry write. `/layout info` exposes the save counters and learned lead.
Private F4 guides remain in their atomic client file with backup and corrupt-file quarantine.

---

## 3. Current control, geometry and renderer behavior

### Plane-aware Arch and Half-circle

- New Arch/Half-circle guides use the plane captured by the first click. Ground placement curves across the
  ground plane, and wall placement curves within the selected wall plane instead of always standing world-up.
- Free-arch apex placement, endpoint tangents, half-circle phantom points, sampled arc voxels, filled output,
  and voxel counts share the same deterministic in-plane frame.
- Half-circle captures its previous opening sign before a chord-defining anchor moves. A 90-degree chord turn
  therefore preserves inversion instead of losing the side encoded by the old phantoms (`dev/GOTCHAS.md` G56).
- Existing records without the compatibility bit deliberately retain the old geometry. Re-place an affected
  legacy guide for the corrected form; an explicit future conversion action is flagged for human review.

### Fillet placement

- Fillet begins with the sharp corner and two exact profile-side points, then follows an unsnapped open sweep.
  Clicking the final sweep point again places. Both profile handles remain editable.
- The HUD shows one instruction per state: **Fillet 1/5** select corner; **2/5** set first side; **3/5** set
  second side; **4/5** set the first sweep-path point; **5/5** continue or left-click the last point again to
  finish.
- SHIFT can place individual Fillet points inside material. First-click SHIFT embeds every guide and keeps
  the whole draft material-side. CTRL+Left-click from idle Create bypasses existing guides.
- The sweep preview uses three construction rails: both profile edges and the sharp-corner route. The picker
  glyph is a CAD-style square with a large, widely dotted rounded corner.

### Exact targeting, Dome and settled rendering

- A sampled curve nominates a cheap target candidate, but a Create-mode grab acts only after the ray enters a
  rendered guide cell. Arch/Free-Shape insert the exact body cell; most parametric guides require an exact
  coloured marker (`dev/GOTCHAS.md` R14).
- Dome's narrow exception maps a visible base-circumference cell to the nearer diameter anchor. Upper-shell
  cells and empty space do nothing; this does not reinstate general nearest-handle snapping.
- Any ordinary or immense reshape that voxelises to zero rejects and retains the valid prior guide. Arbitrary
  body insertion remains an authority-enforced Arch/Free-Shape capability (G50, G51).
- Occupancy refresh deep-clones guide state, preserves wireframe/rendered form, and keeps canonical primitive
  order. Progressive volume reveal order is transient and is restored before settled marker-role ties (G53).
- Exact authority completion cancels conservative pending handoff. Materialisation, sound and completion
  effects have one owner, preventing double Dome feedback (`dev/GOTCHAS.md` G55).
- Model matrices remain exact world translations. Anti-z-fight clearance is an exposed-face outset rather
  than a camera-relative whole-mesh displacement (`dev/GOTCHAS.md` G48).

---

## 4. Open work

`dev/TODO.md` is the open-item authority. The review backlog is empty. Sessions 40–45 are delivered; no
implementation is queued behind them and the next feature is the human's choice.

The largest remaining work is verification rather than a known code defect:

1. Dedicated-server non-admin claim behavior, including relevant claim replacement/resize or player-state
   change during an immense check and the fourth-change busy refusal.
2. Session 40 background-save rhythm and shutdown behavior, using `/layout info` and the server log.
3. Public/private compound Transform: one Undo and one Redo; a denied public operation must leave every
   connected client on the original geometry.
4. Cancel, disconnect or shutdown while an immense operation is deep in geometry work.
5. Live modified-client rejection of unsupported body insertion and a dedicated-server immense empty-Fillet
   rollback; both are source-traced but not separately exercised.

Deliberate deferrals remain under `dev/TODO.md` A15. Do not re-propose per-guide persistence “index cards”
without reading `dev/GOTCHAS.md` G42; the save API is one blob with no key enumeration or deletion.

---

## 5. Operating invariants

- Public authority validates bounds, caps, locks, claims, shape capabilities and input domains before commit.
  Rejections return a corrective full-state resync to the requester.
- A candidate count of zero is invalid at creation and mutation seams. In-place edits restore their snapshot;
  isolated candidates never replace the live guide (`dev/GOTCHAS.md` G50).
- One immense public create/sculpt lane runs at a time. Geometry runs below normal priority; claim checks use
  bounded main-thread slices; cancellation reaches the deepest expensive volume loops.
- Claim snapshot bounds determine which changes can make sliced work stale, never permission. Every footprint
  block still passes through the complete engine/mod `TestAccess` chain.
- Public guide mutations mark persistence dirty rather than serializing the registry per edit. Shutdown uses
  the live registry, superseding any older background snapshot.
- Private F4 guides consume neither shared storage nor other clients' rendering, so server caps deliberately
  do not apply (`dev/GOTCHAS.md` R1). The hard physical voxel ceiling still applies.
- Renderer primitive ordering is semantic. Progressive animation order must be restored before role ties and
  settled emission; occupancy refresh cannot reconstruct or reorder it (`dev/GOTCHAS.md` G2, G53).
- Conservative estimates cannot establish a second completion/effect path beside exact authority (G55).
- Frame-relative opening intent must be captured before moving an anchor that defines the frame (G56).
- Legacy encodings are read in place. Do not rewrite shared guide control points while a renderer worker
  adopts a shape.

---

## 6. Verification evidence and debts

### Evidence through v0.4.82

- Final Debug and Release builds: **0 warnings, 0 errors**.
- `git diff --check`: pass before documentation.
- Disposable Arch geometry/wire harness passed across X/Y/Z planes, both Arch constraints, inversion,
  filled/hollow output, count equivalence, 90-degree anchor turns, legacy stability, protobuf round-trip, and
  the reported ground-plane case.
- Final archive: 42 entries, root metadata/icon/DLL first, 39/39 assets, no directory or backslash-path
  entries, embedded v0.4.82 metadata, and packaged DLL matching Release.
- Release DLL SHA-256:
  `33E81B34AD84801025F3B5995F72A79D4FED7E6142B13268E6D987BB22B6FA61`.
- Archive SHA-256:
  `8FD7AFB99583594DD1B59BD11CB409B7B7DAE2282DCFD37E779EF92AA00D4F1B`.
- Session 44's Dome harness remains: **465 checks passed** across X/Y/Z orientations and scales 1/4/16.
- Session 41's disposable harness results remain: 29/29 authority/claim groups and 52/52 Fillet geometry
  groups. No permanent test project was added.

### Known unverified claims

- Dedicated-server claim refusal and mid-validation change handling; singleplayer cannot prove this path.
- Compound Transform's public/private one-step Undo/Redo and connected-client rollback on refusal.
- Live cancellation from cancel/disconnect/shutdown during immense geometry.
- Session 40 save lead adaptation, synchronous fallback and shutdown save behavior.
- Crafted unsupported insert rejection and immense public empty-Fillet rollback.
- Filled Arch count/appearance after v0.4.47; guide-count HUD refusal after v0.4.46; shape preference restore
  after v0.4.43; the v0.4.40 GUI fixes as a set; xskills interaction with the 32-chalk ceiling; 1.22.0/1.22.1
  smoke tests; Players-list scrolling; and diagnostic occupancy cases retained in `dev/TODO.md`.

---

## 7. Documentation discipline

After a shipped version: update the session record, session index, `CHANGELOG.md`, new traps/reversals,
`dev/WIRE_HISTORY.md` if wire moved, open/delivered ledgers, then regenerate this file and run
`dev/DocCheck.ps1`. Package only after Release and archive verification. Commit and push only when the human
explicitly requests them.
