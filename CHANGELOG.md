# Changelog

All notable changes to Layout will be recorded in this file going forward.

## Unreleased

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
