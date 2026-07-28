# Changelog

All notable changes to Layout will be recorded in this file going forward.

## Unreleased

## 0.4.0 - 2026-07-27

The **Transform** milestone. A guide can now be moved, turned, duplicated and flipped as a whole object,
without ever reshaping it.

### Added

- **Transform mode** replaces Move on the mode row. It covers everything you do to a finished guide as a
  single object: move it, rotate it, copy it, mirror it.
- **Rotate.** The four corners of the direction pad turn the selected guide a quarter at a time — the top
  pair spins it about the vertical, the bottom pair tips it over toward your left or right. Between the two
  you can reach any orientation.
- **Copy.** Turn on Copy and the direction buttons leave the guide alone and put a duplicate one step that
  way. Keep pressing the same direction and the copies march outward in a line rather than stacking.
  Copying with a rotate corner gives you a duplicate turned a quarter.
- **Mirror.** Flips the guide along the axis you press. On its own the guide stays put and only changes
  handedness; with Move or Copy it flips as well as travels.
- **Span distance.** Actions that land things flush — copies, and mirrored moves — step by the guide's own
  width instead of a voxel count, so a copy sits exactly beside the original. Pick any step tile to
  override it, or click the lit one again to go back to span. Available in plain Move too.

### Changed

- Copying costs chalk and counts against your voxel budget, like any other placement — so unlike moving,
  rotating and mirroring, a copy can be refused.
- The HUD names the current combination ("Transform · Copy + Mirror"), so a direction button never does
  something unexpected.
- **Development diagnostic commands are no longer registered.** `renderstats`, `weld`, `occupancy`,
  `occupancyscan` and `blockevents` reported internals and were never meant for players. Set
  `"diagnosticCommands": true` in `layout-client.json` to bring them back.
- Removed panel text that overflowed the tool window on the Transform and Settings pages.

### Fixed

- A large guide's wireframe no longer stays behind at its old position while the guide rebuilds at the new
  one.
- The bottom of a moving guide's wireframe is drawn at the guide's own voxel scale, stepping up to the
  coarse scale gradually, so it can be lined up precisely. Previously the fine detail was hidden inside the
  block-sized wireframe over the top of it.
- Moving a large guide waits before rebuilding it, so a run of nudges is not interrupted by a rebuild
  between each one.
- The Transform controls grey out when no guide is selected instead of disappearing.

## 0.3.89 - 2026-07-27

### Fixed

- The bottom of a moving guide's wireframe is now genuinely drawn at the guide's own voxel scale, stepping
  up to the coarse scale gradually. The fine detail was previously hidden inside the block-sized wireframe
  drawn over the top of it.
- A coarse wireframe no longer draws its voxel outlines at the wrong size, which made block-sized cubes look
  like fine voxels.

## 0.3.88 - 2026-07-27

### Fixed

- A large guide's wireframe no longer stays behind at its old position while the guide rebuilds at the new
  one.

### Changed

- The Move controls now grey out when no guide is selected, instead of disappearing.

## 0.3.87 - 2026-07-27

### Changed

- Moving a large guide waits 2.5 seconds before rebuilding it, so a run of nudges is not interrupted by a
  rebuild between each one. While it waits, the bottom of the wireframe is drawn at the guide's own scale so
  you can line it up precisely.
- Removed explanatory text that overflowed the tool window on the Move and Settings pages.

## 0.3.86 - 2026-07-27

### Added

- **Move mode.** A fourth tool mode beside Create, Edit and Delete. Select a guide and slide the whole thing
  without changing its shape — for the guide that turns out to be one voxel off after a long session.
- Arrows in the tool panel move the selected guide one step at a time, in steps of 1, 2, 4, 8 or 16 of that
  guide's own voxels. Arrows are read from where you are standing: the top arrow pushes the guide away from
  you. Up and Down are separate.
- Free-move: arm the crosshair button, close the panel, and the guide follows your aim snapped to its own
  voxel scale. Left-click places it, right-click puts it back.
- Locked points travel with the guide, and moving costs no chalk. A move can still be refused by a land
  claim at the destination, and undoes in one step.

## 0.3.83 - 0.3.85 - 2026-07-26

### Fixed

- Built-voxel colours no longer cause a frame stutter when you place or chisel a block near a guide. Only
  the affected part of a guide is rebuilt, and the work happens in the background.

## 0.3.81 - 0.3.82 - 2026-07-26

### Added

- Built-voxel colours update live as you build, instead of only when refreshed by hand.

## 0.3.79 - 0.3.80 - 2026-07-26

### Added

- **Built voxels.** Guide voxels that already hold material are drawn cyan, so you can see at a glance how
  much of a guide you have filled in. Off by default — turn it on with the gear in the tool panel, or
  `/layout built on`. `/layout built refresh` re-reads the world by hand.

## 0.3.78 - 2026-07-26

### Fixed

- `/layout inset`, `/layout voxelframe` and `/layout shaderbrightness` now report the current value when
  used with no argument, as they always claimed to. They were instead silently setting it to 0 and saving
  that.

## 0.3.76 - 0.3.77 - 2026-07-26

### Added

- `/layout occupancy` and `/layout occupancyscan` report what Layout reads as material in the world,
  including inside chiselled blocks.

## 0.3.75 - 2026-07-26

### Added

- `/layout blockevents` reports the block changes Layout is receiving — a diagnostic for the built-voxel
  colouring.

## 0.3.72 - 0.3.74 - 2026-07-26

### Added

- **Settings page** in the tool panel, behind a gear in the title bar. Guide opacity can be changed in play
  instead of editing `layout-client.json` and restarting.

## 0.3.70 - 0.3.71 - 2026-07-26

### Changed

- Guide faces now sit a hair outside their voxel instead of inside it, so a guide stays visible against a
  surface you have just chiselled flat to it. The gap is five times smaller than before.

### Fixed

- Rebuilding a guide after filling its volume can no longer flip its faces inward and hide it — exactly when
  the guide is most needed.

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
