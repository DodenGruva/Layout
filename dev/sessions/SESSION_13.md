# SESSION 13 — v0.1.46 → v0.1.52: performance and interaction correctness

> Checkpoint record for the optimization and guide-interaction pass on `ClientOnlyFallback`.
> Current build: **v0.1.52**, **DataVersion 8**, **protocol 3**, **65 C# source files**.

## 1. Why this pass happened

An external optimization proposal was reviewed before more feature work. The useful part was not broad GPU
micro-optimization; it was avoiding needless full voxel generation when Layout only needs to know whether a
guide exceeds a cap. The unsafe or low-value ideas were discarded. Work then moved into the long-standing
lock/drag interaction problem after the cap behavior was stable.

## 2. Cap-aware voxel counting (v0.1.46)

The five 3D volume shapes now expose threshold-aware counting through `IGuideShape`. Box, Cone, Cylinder,
Dome, and Sphere can stop as soon as a caller-provided limit is exceeded instead of materializing every
voxel merely to reject an oversized guide. `GuideManager` uses this path for validation while rendering still
builds the exact voxel set it needs.

This keeps the established safety rules intact while reducing wasted allocation and scanning around the
normal per-guide cap.

## 3. Oversized drag handling (v0.1.47–v0.1.48)

Playtesting found that a legal guide could be dragged far beyond the 25,000-voxel cap. The preview could
show several times the limit and the guide could disappear on release while remaining targetable.

The interaction now has two layers of protection:

1. Release reconciliation never leaves the client displaying a rejected over-cap preview.
2. Client drag clamping binary-searches the movement segment and stops the drafting shape at the largest
   acceptable size.

The second change removed the visible fight/flicker between the oversized preview and the last accepted
server state. The human confirmed that the capped drag now feels natural.

## 4. Precise targeting and complete drag cancellation (v0.1.49–v0.1.50)

The first-hit lock proposal from B-S9-1 was implemented: lock-in-place body picking now tests the crosshair
ray against rendered voxel boxes and chooses the first intersected voxel. This replaces nearest-curve-point
selection, which could choose a neighboring cell.

Curve target caches now use a full geometry fingerprint rather than a weak summary, so moving points cannot
leave stale pick geometry behind. Server and local-authority drag paths also retain complete pre-drag
snapshots. Right-click cancellation restores points, constraints, soft-point flow, and insert-born gestures
exactly instead of trying to reconstruct only part of the earlier state.

The standard reset gesture remains **Shift + left-click**. A temporary Shift + right-click alias was removed
at the human's request so that combination remains available for a future action.

## 5. Non-deforming lock markers (v0.1.51)

Arch locks previously inserted an ordinary spline knot, so the act of locking could alter the curve even
when the player had not dragged anything. `ControlPoint.IsLockMarker` now distinguishes a passive lock marker
from a shape-defining knot. Passive markers render and target normally but do not participate in the
Catmull-Rom curve until the player deliberately drags one, at which point it is promoted to a real knot.

This is an additive persisted/wire field, which advances the project to **DataVersion 8** and **protocol 3**.
Existing saves and packets default the field to false. The human confirmed that placing a lock no longer
shifts the guide.

## 6. Marker lifecycle and curve order (v0.1.52)

Further playtesting exposed two follow-on cases: an unlocked passive marker could remain as a stale snap
target, and a new grab on the right side of an arch could be inserted on the wrong side of an existing lock.
v0.1.52 adds `RemoveLockMarkerCommand`, removes obsolete passive markers on unlock/load restoration, excludes
inactive markers from adoption and targeting, and orders later lock/grab insertions by their position along
the curve.

Targeted geometry checks covered lock insertion at several scales and curve positions, plus right-side
before/after insertion ordering. The human reports this iteration is better, but has explicitly reserved
final judgment for more in-game testing.

## 7. Current status and next work

- The volume-counting optimization and natural cap clamp are implemented and playtest-confirmed.
- Lock targeting, cancellation, post-drag cache accuracy, and non-deforming placement are substantially
  improved.
- **B-S9-1 remains an active follow-up, not a closed bug.** The stale-marker and ordering fixes in v0.1.52
  need broader real-play coverage across repeated lock → drag → cancel/revert → unlock cycles.
- After that focused interaction regression, run the final F4/public multiplayer regression before naming a
  v0.2.0 candidate.
- Enormous fine-detail guides remain a separate future rendering/scale project; this pass optimized ordinary
  cap validation but did not raise the scan guard or hard ceiling.
