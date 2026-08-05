# Session 43 — v0.4.72 → v0.4.73

**Branch:** `Codex`. **DataVersion 13. Protocol 28.** **87 source files** (none added).

This session followed an external AI critique of Sessions 41–42 with direct source verification, then
finished the exact-input control work in play. The critique was valuable but not accepted at face value: its
highest-priority Roundover finding described a misleading rejection message, while the actual mutation paths
did not reject an empty reshape at all. They could commit an invisible zero-voxel guide. A second item described
an unreachable insertion as small waste, but a modified client could make authority report success and send
remote mirrors a point that the authoritative parametric shape never accepted.

---

## 1. v0.4.72 — authority fixes from the verified critique

### Empty reshapes reject and roll back

Creation had honoured the zero-voxel rule since v0.4.37, but ordinary `UpdateControlPoints` and the isolated
immense-sculpt commit only checked upper caps. Zero passes every upper cap, so dragging a Roundover profile or
route into an invalid frame could store a count of zero and make the guide disappear.

Both mutation paths now reject zero before adopting the candidate. Ordinary edits restore their point
snapshot; immense edits retain the live guide. Public and private authorities give a Roundover-specific
message covering the actual recovery choices: smaller profile, endpoints away from the sharp corner, more
sweep room, and no direct reversal.

### Body insertion is an authority capability, not a client convention

Only Arch and Free-Shape accept arbitrary body points. The stock controller already knew that, but
`GuideManager.InsertControlPoint` accepted every shape and trusted its `void` insert method. Parametric shapes
implemented that method as a no-op, after which the manager selected an existing nearest point, reported
success, and the server broadcast an insertion that had not happened in authority.

`GuideShapeTypes.SupportsBodyInsert` is now the shared catalog rule. The server rejects an unsupported packet
before taking a lock or sampling a curve, and `GuideManager` enforces the same invariant for every public,
private and undo caller. Rejection resynchronizes the requester rather than broadcasting contradictory state.

### Small review cleanup

The unreachable null branch inside `RevalidateClaimAccess` was removed; both immense callers establish the
snapshot before invoking it. `ShapeFactory` now explains locally that Roundover's create-time `closed` argument
is the protocol-28 two-profile discriminator, not closed geometry. The claim-snapshot enumeration was left
unchanged: it is a correctness requirement, explicitly documented, and its 10,000-claim measurement remains
about 0.10–0.14 ms per capture.

## 2. v0.4.72 — exact grab means the clicked voxel

The former rule mapped a parametric guide's body click to its nearest existing handle. That had once been a
deliberate convenience, but in play it felt like snapping and contradicted the exact-input direction of the
Roundover controls.

The sampled-curve test is now only a cheap candidate finder. A Create-mode grab begins only after the view ray
actually intersects a rendered guide voxel. Arch and Free-Shape can insert and grab the exact clicked body
cell. Parametric shapes cannot invent an independent body control: only an exact hit on the coloured control
marker starts a grab, and a body hit does nothing rather than jumping elsewhere. Right-click lock forgiveness
retains its separate deliberate behavior.

The CTRL held-help label was shortened from “Bypass Grab and Ignore Existing Guides” to **“Bypass Existing
Guides.”** Its behavior is unchanged.

## 3. v0.4.73 — HUD targeting matches the new precision

The human found that the HUD still named a guide while aiming around it. Source tracing confirmed a split
standard: click-time grabbing performed the new exact voxel intersection, but the 33 Hz HUD loop still used
the older proximity envelope around sampled points and curves. Its fixed minimum body radius was 0.18 blocks;
a scale-1 guide cell is only 0.0625 blocks wide, so the HUD could report a target several cells away.

Regenerating an immense guide's entire voxel list every 30 ms would make hover mathematically exact at an
unacceptable cost. The broad HUD candidate is instead bounded to the physical cell's centre-to-corner radius,
`sqrt(3)/2 × cell edge`, with no fixed minimum. At scale 1 the candidate radius falls from 0.18 to about 0.054
blocks. The click-time exact rendered-voxel test remains authoritative. The human tested v0.4.73 and reported
the result **“Excellent.”**

## 4. Verification and release artifacts

- v0.4.72 and v0.4.73 Release builds: **0 warnings, 0 errors**.
- `git diff --check`: pass before documentation.
- `modinfo.json`: ASCII-only outside an optional BOM.
- `Layout0.4.72.zip`: 42 entries / 375,190 bytes; 39/39 assets; root files first; no directory or backslash
  entries; embedded metadata reports v0.4.72; packaged DLL matches Release; archive SHA-256
  `671060DF699D7D279F8281A043935F7782FBFC7FD8BAD1269E7C9C85CEEF11DD`.
- Final `Layout0.4.73.zip`: 42 entries / 375,184 bytes; the same structural checks pass; embedded metadata
  reports v0.4.73; packaged DLL matches Release; archive SHA-256
  `AA1A410F1AB35988D4BADC21D15D5888F0ECE58975B7EC538B994384C907FE45`.
- No permanent automated test project was added. The final targeting feel is directly play-confirmed.

---

## Delivered

- **v0.4.72** — Rejected and rolled back zero-voxel reshapes with useful feedback; made Arch/Free-Shape body
  insertion an authority-enforced capability; removed dead claim code; clarified the Roundover create flag;
  shortened the bypass tooltip; and replaced nearest-handle grab snapping with exact clicked-voxel selection.
- **v0.4.73** — Replaced the HUD's fixed targeting halos with a scale-aware physical-cell envelope while
  retaining exact click confirmation; direct play reported the result excellent.

## Decisions

- Treat external review as findings to reproduce, not conclusions to copy. The two actionable observations
  both pointed near real defects but understated or misdescribed the authority behavior.
- Exact input outranks nearest-handle convenience. A parametric body cell has no independent control point;
  doing nothing is more truthful than moving a different coloured handle the player did not click.
- Keep exact confirmation on actions and a bounded candidate test on the 33 Hz HUD loop. Exact whole-shape
  voxel regeneration there would scale with the largest guide every frame.
- Leave the claim snapshot design alone without contrary measurement. It deliberately scans claim bounds
  because the engine exposes no regional query, and its measured cost is already recorded.

## Traps

⚠️ **Zero-voxel rejection is a mutation invariant, not merely a creation invariant.** Any edit path that
recounts a candidate must reject zero and restore/retain the live guide before storing the count.

⚠️ **A `void` shape operation can report success after doing nothing.** Authority must validate whether the
shape supports body insertion before calling it; a client-side gesture rule cannot protect the wire seam.

⚠️ **Hover targeting and click targeting can silently diverge.** The 33 Hz path may use a cheap candidate,
but its envelope must be derived from the physical guide cell and every action that mutates state retains an
exact confirmation.

## Flagged and unverified

**Judgement calls awaiting review:**

- None. The exact-grab and tightened HUD behavior were directly requested and v0.4.73 was play-confirmed.

**Claims not tested:**

- A crafted unsupported insert packet is source-traced to a pre-lock resync and manager rejection; no live
  modified-client packet test was performed.
- Empty-reshape rollback covers ordinary and immense public paths plus private authority by shared manager
  logic; no dedicated-server immense Roundover rejection was reported.
