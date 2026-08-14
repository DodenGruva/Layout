# Session 45 — v0.4.82

**Date:** 2026-08-14. **Branch:** `main`. **DataVersion 13 → 14. Protocol 28 → 29.**
**87 source files** (none added).

## 1. The first-click plane was captured, then discarded for arches

The reported symptom was exact: place Arch or Half-circle on the ground in 2D mode and the result appears as
a line, as though viewed from above. The controller already captured the clicked plane in `ShapePlaneAxis`,
but `ShapeFactory` did not pass that axis into `ArchShape`. Arch then used world Y for its free-arch apex,
phantom tangents, and constrained half-circle arc. A horizontal guide therefore stood vertically and its 2D
projection collapsed edge-on.

New Arch/Half-circle construction now receives the stored axis and obtains a deterministic in-plane frame
from `ShapeGeometry.TryGetFrame`. Free-arch apex placement, end tangents, half-circle phantom points, sampled
arc voxels, filled output, and count generation all use the same in-plane perpendicular. Ground guides curve
across X/Z; wall guides curve within their selected wall plane.

## 2. Review found a second half-circle orientation defect

Half-circle's two persisted feet define its chord, while the derived phantom points encode which side of that
chord the arch opens toward. The first implementation rebuilt the phantoms after moving a foot and attempted
to recover the previous side in the new frame. If the anchor move turned the chord by 90 degrees, the old
opening vector was orthogonal to the new perpendicular, so the sign could collapse to zero and invert.

`MoveControlPoint` now captures the old opening sign before mutating either chord foot and explicitly carries
it into phantom recalculation. The review harness includes a 90-degree chord turn and verifies that both the
opening and the inverted/non-inverted choice survive. This reusable ordering rule is recorded as
`dev/GOTCHAS.md` G56.

## 3. Compatibility is explicit rather than inferred

Old records often contain a `ShapePlaneAxis`, but Arch historically ignored it. Automatically assigning the
new meaning would twist existing saved guides after an update. New guides therefore set
`ArchUsesShapePlaneAxis = true`; old data and old peers leave it false and retain byte-for-byte-equivalent
world-vertical generation.

The flag is copied by `GuideData.DeepClone`, appended to `GuideDataDto` as protobuf field 26, and included in
renderer and draft-handoff fingerprints so a geometry-meaning change cannot reuse stale visuals. Creation on
both client and authority sets it from the actual constructed shape, including the unknown-type fallback.
DataVersion moved to 14 and protocol to 29 because the saved/wire meaning changed even though no packet type
was appended. No migration rewrites control points on renderer workers.

## 4. Verification and release

- Debug and Release builds passed with **0 warnings and 0 errors**.
- A disposable geometry/wire harness covered X/Y/Z intrinsic planes; Arch and Half-circle; inverted and
  non-inverted; filled and hollow; voxel-count agreement; 90-degree anchor turns; legacy output stability;
  protobuf round-trip; and the reported ground case (one Y layer with multiple Z cells).
- `git diff --check` passed, and `modinfo.json` contains no non-ASCII bytes outside its optional BOM.
- `Layout0.4.82.zip` contains 42 entries and all 39 assets. Its embedded metadata is v0.4.82 and its DLL
  matches the Release output.
- Release DLL SHA-256: `33E81B34AD84801025F3B5995F72A79D4FED7E6142B13268E6D987BB22B6FA61`.
- Archive SHA-256: `8FD7AFB99583594DD1B59BD11CB409B7B7DAE2282DCFD37E779EF92AA00D4F1B`.

## Delivered

Ground- and wall-plane Arch/Half-circle placement now uses the plane selected by the first click. Legacy
guides remain unchanged, and constrained half-circle opening direction remains stable through anchor moves.

## Decisions

Compatibility is opt-in per guide instead of inferred from DataVersion or the mere presence of
`ShapePlaneAxis`. This protects saved builds and mixed-version data from a silent geometry change.

## Traps

Frame-relative intent must be captured before moving the anchors that define the frame. For legacy data,
read old encodings in place; do not mutate shared control points while a renderer worker adopts a shape.

## Flagged and unverified

**One judgement call awaits human review:** legacy Arch/Half-circle guides keep their old world-vertical
shape. Re-place an affected old guide to obtain the corrected form. If conversion proves valuable, provide
an explicit action rather than silently changing saved geometry.
