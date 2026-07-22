# SESSION 22 — v0.3.9 → v0.3.21: action-aware HUD, guide attribution, and sculpting parity

> This session turned the HUD from a passive settings summary into a compact action indicator, added durable
> creator/last-modifier attribution without keeping it permanently on screen, and brought large-guide
> sculpting closer to initial placement. Current checkpoint: **v0.3.21**, **DataVersion 12**, **protocol 13**,
> **74 C# source files**, **15 shape types / 21 picker tiles**.

## 1. Delivered iteration arc

- **v0.3.9–v0.3.12 — action-aware HUD and attribution foundation.** The fixed-size HUD now distinguishes
  Create, Sculpt, Creating, Sculpting, Edit, Editing, Delete, and Deleting. The 42-pixel tile retains the GUI
  tile size and shows the active or targeted shape; its outside accent communicates an active operation.
  Dimensions occupy separate horizontal/vertical rows and include both voxels and whole blocks, followed by
  Total Voxels and cap percentage. Creator and Last Sculptor became persisted guide metadata
  (**DataVersion/protocol 12**). Creator is immutable; Last Sculptor updates only after a committed perceptible
  change, including geometry, scale, projection, form/fill, divisions/sides, locks, visibility, undo, and redo.
  Hover, selection, rejected operations, no-ops, and cancelled grabs do not claim authorship.
- **v0.3.13 — attribution on demand.** Constant Creator/Last Sculptor HUD rows were removed to recover space.
  `/layout who` reports the selected Edit guide first, then a grabbed or crosshair-targeted guide, including
  its exact shape, private marker, Creator, and Last Sculptor. A server command asks only the invoking client
  to resolve its local target through `GuideWhoQueryPacket`, advancing the protocol to **13**; vanilla-server
  local authority has an equivalent client command.
- **v0.3.14–v0.3.15 — compact HUD and sculpting parity.** The shape caption was removed; the contextual tile
  became the sole shape identity. The panel was narrowed while retaining a fixed footprint, then restored to
  the normal small-text size with 75% of the original dialog padding. Right-click in Edit clears the selected
  guide without mutation. Large Shell guides now use the same move/settle rhythm when sculpted as when first
  drafted: a bounded motion wireframe, then generation-safe selected-scale background refinement revealed in
  batches. Tapered Cylinder and Tapered Polygonal Prism rim sculpting again clamps to the base radius unless
  SHIFT deliberately permits flare. Selected Edit HUD settings come from the guide, not next-guide defaults.
- **v0.3.16 — projection-transition and GUI fixes.** Switching an active draft between Surface and Volumetric
  now translates every already-placed point by the correct signed half-voxel offset. Arch and Half-Circle
  anchors therefore remain aligned with later live points instead of being overwritten or appearing to turn
  surface again. The GUI centers Fill beneath its tile and gives the Edit header its available width.
- **v0.3.17–v0.3.21 — final HUD/GUI typography pass.** State headings are centered in the space beside the
  unchanged tile. Static labels are subdued while values remain white, dimensions use a shared centered-arrow
  column, and calculation dots animate without shifting value starts. The rejected horizontal rule was
  removed. Label columns were widened to prevent punctuation wrapping (`Scale:`, `Total Voxels:`, `Cap:`),
  the empty Edit instruction was removed with its dead row, and the below-tile settings row uses the full
  panel width so `Volumetric · Wireframe` displays completely.

## 2. Current HUD contract

1. **The footprint is stable.** Crosshair targets and draft state never expand or contract the panel.
2. **The top line describes the available or active action.** Inactive states are plain; active Creating,
   Sculpting/Editing, and Deleting states use italic colored text plus the matching outside tile outline.
3. **The tile is contextual.** Create/Creating shows the next shape; targeting, sculpting, editing, or deleting
   shows that guide's shape. Edit/Delete show their pencil/trash icons only when no guide is targeted.
4. **Settings remain concise.** Scale has its own row; projection and form share one full-width row. Surface
   omits the obsolete auto-plane descriptors. Edit reads the selected guide's real settings.
5. **Measurements are exact when ready and non-blocking when pending.** Width and height each show voxels and
   blocks, followed by Total Voxels and cap percentage. Only a true over-cap result adds `OVER CAP`.
6. **Attribution is deliberately off-HUD.** `/layout who` supplies it when a player actually needs it.

## 3. Attribution and compatibility

- `GuideData` stores `CreatorName`, `LastSculptorUid`, and `LastSculptorName`; `CreatorUid` remains the
  authoritative ownership/cap identity. Friendly names are display snapshots and grant no permissions.
- `GuideDataDto` carries friendly names. `GuideHudMetadataPacket` incrementally refreshes Last Sculptor and
  cached measurement fields after a visible mutation without rebroadcasting full guide state when unnecessary.
- Old saves load with unknown attribution. Creator cannot be reconstructed safely; the first later committed
  mutation establishes Last Sculptor. New and published private guides initialize both fields to their creator.
- Public server authority and private/local authority apply the same perceptible-change rule.

## 4. Large-guide sculpting contract

- Motion remains bounded and responsive; settled precision is calculated off-thread.
- Every refinement is generation/fingerprint guarded. Stale work cannot replace a newer pose or reappear after
  cancel/release.
- Volumetric results materialize in bounded batches; Surface results flatten through the established slab path.
- The retained settled mesh still provides instant visual rollback on cancel.
- Tapered-rim sculpting shares initial placement's CTRL close-rim and SHIFT allow-flare behavior.

## 5. Release/compatibility checkpoint

- Current package: `Layout0.3.21.zip`; Release build: **0 warnings / 0 errors**.
- Current data schema: **12** (friendly creator/last-sculptor attribution; v11 persistent form; v10 cached
  measurements; v9 flat-side alignment).
- Current protocol: **13** (v13 targeted `/layout who` query; v12 attribution metadata; v11 wireframe state).
- Source/catalog count remains **74 C# files**, **15 shape types / 21 picker tiles**.
- This documentation finalizes the complete v0.3.9–v0.3.21 playtest-driven iteration for commit and push.
