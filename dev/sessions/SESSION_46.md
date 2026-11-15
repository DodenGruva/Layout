# Session 46 — v0.4.83 to v0.5.0

**Branch:** `main`. **DataVersion 14 to 18. Protocol 29 to 33.** **92 source files**.

## 1. Release and compatibility

Layout now uses 64 coordinate cells per block in Debug and Release. The existing 1/16 chisel scale remains the tool default. In Settings, **Enable 1/32 and 1/64 Resolutions** reveals two quarter tiles next to the one-voxel tile. Existing regular world and private guides import once into separate 64-cell storage. Import multiplies stored voxel steps and plane offsets by four and keeps the world-space control points and guide IDs. Previous release and experimental guide stores remain available separately. The release imports client preferences from the previous regular config when it first starts.

Public placement and guide sync use `layout-grid64`. Server and client builds must share that channel because scale, plane and translation integers use the finer units. The mod package uses the name Layout and the original construction-planning description.

## 2. Targeting and outlines

Guide placement uses the engine raycast to identify the selected face inside a chiseled block. The grid overlay follows the selected Layout scale. **Outline Targeted Voxel** starts on and uses a contrasting color from the selected block.

Grid-aligned circles use symmetric coverage. Ctrl aligns the diameter and preserves the aimed odd or even size. Even circles can have paired blue anchors and paired green rim midpoints. The opposite green midpoint is a visual reference. Legacy guides retain their recorded outline style.

New hollow 2D guides use shared-side connections by default. **Enable Thin Shapes** allows diagonal steps to meet at corners. The style travels with each guide through preview, counting, save and sync. Filled coverage stays full.

## 3. Placement timing

While placing a circle on a solid face, playtesting showed the preview hold its current diameter and then catch up. The draft cap check deferred exact work during aim motion. Once a smaller size was verified, that schedule could hold the preview even far below the cap. The isolated reproduction held a 32-cell preview until the check accepted a 41-cell aim. Bounded outline candidates now check while the aim moves. Expensive fills retain deferred checks.

## Delivered

- **v0.4.83–v0.4.86** — Isolated fine-resolution trials, chiseled-face targeting and opt-in fine scales.
- **v0.4.87–v0.4.90** — Symmetric circle coverage, targeted grid, contrasting voxel outline and compact fine-scale tiles.
- **v0.4.91–v0.4.93** — Seam-paired rim markers, Ctrl alignment with both diameter parities, and green markers on both sides.
- **v0.4.94–v0.4.96** — Live cap checks for bounded planar outlines, per-guide outline styles and final Settings labels.
- **v0.5.0** — Standard release with opt-in fine resolutions and one-time regular-guide and settings import.

## Decisions

- Keep the ordinary chisel scale as the out-of-box default and hide the finer choices behind the opt-in.
- Use separate release storage and a network channel for the 64-cell coordinate grid. Keep experimental stores separate so trial edits do not replace existing release guides.
- Save the outline style on the guide so a viewer's local toggle can affect their future drafts independently.

## Traps

- ⚠ Scale and plane fields from a 16-cell payload must be converted exactly once at import. An existing empty 64-cell registry wins over the older store. See the units rule in GOTCHAS G57.
- ⚠ A delayed cap calculation can hold the preview even when the count is far below the limit. Verify the whole aim sequence across a settled frame and renewed motion. See GOTCHAS G58.

## Flagged and unverified

- Live cross-version multiplayer behavior has no session report. The release channel and serialized coordinates were verified locally. Prior dedicated-server claim and background-save verification items remain in the project status.
