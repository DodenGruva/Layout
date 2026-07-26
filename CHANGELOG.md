# Changelog

All notable changes to Layout will be recorded in this file going forward.

## Unreleased

## 0.3.69 - 2026-07-25

### Fixed

- Guides loaded before the surrounding terrain finished loading no longer keep provisional edge spacing.
  They are now corrected automatically once the terrain arrives.

## 0.3.68 - 2026-07-25

### Fixed

- Removed an invisible limit that could randomly stop a guide from being dragged or drawn any larger, even
  with plenty of voxel budget remaining. The size check remembered a failed reach from one aiming direction
  and wrongly applied it to every other direction.

## 0.3.66 - 0.3.67 - 2026-07-25

### Fixed

- Guide edges resting against slabs, chiselled blocks, snow layers and other partial blocks no longer
  shimmer. The spacing that prevents it previously only applied to faces sitting on whole-block boundaries,
  so raising it appeared to affect only the bottom of a guide.
- The voxel outline no longer goes missing on the layer of a guide that rests against blocks.

## 0.3.65 - 2026-07-25

### Added

- `/layout inset` adjusts the small gap between a guide and the blocks it touches, saved as `zFightInset`
  in `layout-client.json`. Raise it if guides shimmer against a surface.

## 0.3.64 - 2026-07-25

### Changed

- Sphere and dome wireframes are now divided into eight sections instead of four, making a large volume's
  form much easier to read.

## 0.3.62 - 0.3.63 - 2026-07-24

### Added

- **Voxel outlines.** Each voxel's boundary is now lightly outlined so individual cells are legible on a
  large guide surface. Strength is `voxelFrameStrength` in `layout-client.json`, adjustable live with
  `/layout voxelframe` (0 turns it off). Costs no measurable performance. Requires the custom shader.
- Outlines appear everywhere: placed guides, the draft you are drawing, materialization, and while dragging.

## 0.3.59 - 0.3.61 - 2026-07-24

### Changed

- **Guides render through a purpose-built shader.** A large guide costs roughly **a quarter** of what it did
  before this session (measured: 8.2 ms down to 1.8 ms on an 8-million-voxel guide). Guides no longer run
  through the game's full world shader, which was doing shadow, lighting and texture work that a flat
  translucent overlay never needed.
- As a result guides are self-lit: they no longer dim in shadow or tint with the time of day. Brightness and
  how much they follow ambient light are adjustable via `/layout shaderbrightness`, saved in
  `layout-client.json`. `/layout shader off` restores the old appearance at the old cost.

## 0.3.58 - 2026-07-24

### Fixed

- Large guides no longer freeze the client while a world loads. A guide above 100,000 voxels now appears as
  a wireframe scaffold immediately and grows into its full shell in the background, instead of being
  generated, meshed, and uploaded in one blocking step.
- Large guides placed by another player now materialize for everyone watching, instead of appearing all at
  once. Previously only the player who placed the guide saw it grow in.

### Added

- `/layout weld on|off` — diagnostic switch to compare welded and unwelded guide meshes in play. Not saved
  between sessions.

## 0.3.57 - 2026-07-24

### Changed

- Guide meshes now share vertices between faces that meet at the same point with the same colour, instead
  of emitting four separate corners per face. Guides look exactly the same — the triangles are unchanged —
  but a large guide sends **58% less data to the graphics card** and costs **41% less frame time**. The
  benefit scales down to every guide size and matters most on lower-end hardware.

## 0.3.56 - 2026-07-24

### Fixed

- `.layout renderstats` no longer reports a frame-rate cap when the limiter is switched off, and no longer
  warns that a fast reading is capped.

## 0.3.55 - 2026-07-24

### Added

- `.layout renderstats` now reports vertices, indices, and total mesh data submitted per frame, and warns
  when a frame-time reading is pinned to the game's frame-rate limiter.

## 0.3.54 - 2026-07-23

### Fixed

- Restored Client-Only Fallback mode on servers without Layout. Holding Flax Twine in the main hand and a
  vanilla Hammer in the off-hand now activates the Layout HUD, settings menu, and local guide interactions
  as intended.
- Prevented Layout from attempting to send chalk-refill preferences over an unavailable optional network
  channel during client-only authority initialization.
- Prevented the related disconnected-channel exception when leaving a client-only world.
