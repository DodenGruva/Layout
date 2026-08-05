# Changelog

All notable changes to Layout will be recorded in this file going forward.

## 0.4.73 - 2026-08-04

### Fixed

- **The HUD no longer targets empty space around a fine guide.** Its hover envelope now scales with the
  physical guide-cell size instead of using fixed 0.10/0.18-block minimums. At the finest scale the body
  radius falls from 0.18 blocks to about 0.054; clicks still require an exact rendered-voxel hit.

## 0.4.72 - 2026-08-04

### Changed

- **Grabbing no longer snaps a parametric guide's body to its nearest handle.** Arch and Free-Shape insert and
  grab the exact clicked body voxel. Other shapes begin a grab only from the exact coloured control voxel;
  clicking an unrelated body cell does nothing.
- The held-help label now reads **“Bypass Existing Guides.”**

### Fixed

- **A reshape that makes a guide voxelise to nothing is rejected and rolled back.** Roundover explains how
  to recover instead of disappearing as an invisible zero-voxel guide. Ordinary, immense, public and private
  mutation paths share the guard.
- Unsupported body-insertion requests are rejected before taking a guide lock or broadcasting state. A
  modified client can no longer make remote clients insert a point that the authoritative parametric shape
  ignored.
- Clarified Roundover's create-time use of the shared `closed` argument and removed an unreachable claim-
  snapshot initialization branch.

## 0.4.71 - 2026-08-04

### Added

- **SHIFT on the first click embeds any new guide into the material.** The whole draft remembers that choice,
  so later points stay on the same material side without holding SHIFT again. Roundover still allows SHIFT on
  individual later points when the draft did not begin embedded.

### Changed

- The placement tooltip now says **“Embed Guide.”** Existing later-stage SHIFT controls, including vertical,
  inverted and flat-side placement, keep their prior meanings.

## 0.4.70 - 2026-08-04

### Added

- **CTRL+Left-click can start a new guide through an existing guide.** In idle Create mode it bypasses grabbing
  and ignores Layout guide targeting, using the real block behind the translucent guide. Active draft and grab
  controls are unchanged. The held-help tooltip reads “Bypass Grab and Ignore Existing Guides.”

## 0.4.69 - 2026-08-03

### Changed

- **Roundover's wireframe preview now shows three construction rails:** both clicked profile edges swept along
  the route, plus the sharp-corner route itself. This replaces the floating midpoint wire and makes interior
  sweep placement readable against the corner being followed.

## 0.4.68 - 2026-08-03

### Added

- **SHIFT places a Roundover point inside the targeted material** by one guide cell, allowing exterior-corner
  profiles to occupy the block instead of landing outside its face.

### Changed

- The live Roundover sweep preview uses wireframe after the profile is established, keeping the corner under
  the crosshair visible.

## 0.4.67 - 2026-08-03

### Changed

- **Roundover placement is now profile-first.** Click the sharp corner, click the two exact profile endpoints,
  then click the sweep path; click the final path point again to place. Neither profile nor path points snap.
- Both profile legs remain editable. Older one-handle Roundovers retain their original geometry. Public
  profile-first placement requires protocol 28; private placement remains local.
- Removed the Radius-field and automatic corner-probing experiments from 0.4.65–0.4.66.

## 0.4.66 - 2026-08-03

### Added

- Tried automatic interior/exterior Roundover selection by probing material around the first guide point.
  **Withdrawn in 0.4.67:** partial and chiselled block layouts made inference less useful than exact player
  input.

## 0.4.65 - 2026-08-03

### Added

- Tried a GUI Radius field with spinner controls for Roundover. **Withdrawn in 0.4.67:** the number exposed the
  setting but did not make the spatial construction more intuitive.

## 0.4.64 - 2026-08-02

### Added

- **Roundover Path**, a constant-radius sculpted corner that follows an open route through arbitrary turns.
  Corners use smooth rolling-ball transitions rather than mitres; a final perpendicular handle selects the
  radius and quadrant. Public placement is gated to protocol-27 servers while private placement remains local.

### Fixed

- **Guide voxels now register exactly with Vintage Story's 1/16 micro-block lattice.** Removed a legacy
  0.003-block camera-relative translation that shifted the entire rendered guide off its mathematically exact
  coordinates and made the apparent error change with view angle.
- The anti-z-fight face outset now defaults to the playtest-confirmed `0.0002` blocks. `0.0001` was already
  stable after removing the whole-mesh shift; the extra `0.0001` is a small safety buffer. `/layout inset`
  help now reports the real default.

## 0.4.59 - 2026-08-02

### Changed

- **Very large guide claim snapshots now track only claims that can touch the guide.** Layout still examines
  each claim's bounds because the game exposes no regional claim query, but distant claims are no longer
  copied, permission-tested, compared, or allowed to restart an operation. Exact per-block access checks are
  unchanged, so other mods and every non-claim refusal still apply.
- On a focused 10,000-claim benchmark with one relevant claim, the bounded snapshot took about 0.10-0.14 ms
  and allocated about 820 bytes, versus 2.50-3.10 ms and 4.28 MB for the global snapshot introduced in
  0.4.58.

## 0.4.58 - 2026-08-02

### Fixed

- **A very large guide can no longer commit against a stale claim layout merely because the number of claims
  stayed the same.** Equal-count claim replacement, in-place resizing, and relevant player authorization
  changes now restart validation. Changes are checked before every slice and once more before commit.
- After three claim-state restarts, continued churn now refuses the operation as temporarily busy instead of
  eventually accepting a result validated against changing permissions.
- Very large reshapes re-check build privilege and jail state immediately before committing.

## 0.4.57 - 2026-08-02

### Fixed

- **Cancelling a very large guide now stops the geometry work already running for it.** Previously a cancelled
  placement or reshape was discarded safely, but its counting and voxel generation could continue in the
  background and keep Layout's single large-guide validation lane occupied. Cancellation now reaches every
  3D volume scan, its large-shape fallback, marker work and land-claim footprint preparation.

## 0.4.56 - 2026-08-02

### Fixed

- Malformed Transform requests are now rejected even when their valid parts would amount to no movement.
  This only affects invalid data from a modified client; normal no-op Transform actions behave as before.

## 0.4.55 - 2026-08-01

### Fixed

- **Move and Transform can no longer carry an otherwise valid guide outside the safe world-coordinate range.**
  The resulting guide and projection plane are checked before any voxel scan, claim check, save or broadcast.
- **A combined rotate, mirror and move is now genuinely one operation.** It validates once, either commits
  completely or changes nothing, broadcasts one consistent result, and takes one Undo/Redo step for both public
  and private guides. A refusal can no longer leave only the rotation committed or other players seeing stale
  geometry.
- Undefined projection modes/axes and unsafe plane offsets are rejected at packet, restore, load and mutation
  boundaries instead of entering live or saved guide state.

## 0.4.54 - 2026-08-01

### Added

- **`/layout info` now reports how guide saving is behaving** — how many world saves had their guide data
  prepared in the background, how many had to do the work on the spot, and the save rhythm it has settled
  into. The improvement below is invisible when it works, so this is how you can tell that it is.

## 0.4.53 - 2026-08-01

### Fixed

- A background guide save that could not start, or that failed partway, no longer counts as having saved.
  Previously the next world save would skip its own write in that case, and the affected edits waited for
  the save after it.

## 0.4.52 - 2026-08-01

### Fixed

- **A world save with no guide data prepared for it now writes that data immediately.** Without this, a
  background save that got stuck could have stopped guide edits reaching the disk for the rest of a server
  session, with nothing to show for it. Also covers the first saves after a server starts, which happen
  before the mod has worked out the save rhythm.

## 0.4.51 - 2026-08-01

### Changed

- Guide data is now prepared about 3 seconds before a world save rather than 10, so less recent building is
  left waiting for the following save. If a server needs more time than that, the mod notices and gives
  itself more, rather than the figure being a fixed guess.

## 0.4.50 - 2026-08-01

### Changed

- **The server no longer pauses to write guide data when the world saves.** Converting every guide in the
  world into saveable text took 42 milliseconds on a world with 3,000 guides — more than two server ticks,
  felt by everyone as a stutter — and it happened on every autosave. That work now happens on a separate
  thread, a few seconds ahead of the save, so the data is already waiting when the world writes itself. The
  server keeps a copy of the guides for the background thread to work from, which costs about half a
  millisecond.
- **One deliberate trade:** a guide edit made in the last few seconds before an autosave now waits for the
  next one instead of catching that save. Anything older is saved exactly as promptly as before. Shutdown is
  unaffected — the server always writes guide data properly on its way out.

## 0.4.49 - 2026-08-01

### Reverted

- **The land-claim shortcut added in 0.4.48 has been removed.** It skipped the full permission check when a
  guide was nowhere near a land claim — but the game refuses building for several reasons besides claims
  (a missing privilege, a dead player, another mod protecting the area), and the shortcut only understood
  claims. Claim protection is back to checking every block, exactly as it did up to 0.4.47.

## 0.4.48 - 2026-08-01

### Changed

- Faster land-claim checking while dragging a guide. **Withdrawn in 0.4.49 — do not use this build.**

## 0.4.47 - 2026-08-01

### Fixed

- **Filled arches are much cheaper to edit.** Working out how many voxels a filled arch contains rebuilt the
  arch's curve from scratch thousands of times over, every time — and that happens about ten times a second
  while you drag one. The curve is now built once. The resulting count is unchanged: verified identical
  across 576 arch shapes and every other shape in the mod.

## 0.4.46 - 2026-08-01

### Fixed

- **Hitting a guide-count limit now shows on the HUD**, not only in chat. The limits on how many guides you
  or the world may have took a different route from the voxel limits and never told the HUD anything, so the
  cap row sat unchanged while chat explained the refusal.

### Changed

- **Guides are now saved when the world saves**, rather than on a timer of their own. Guide data is worth
  what the rest of the world is worth — if a crash costs you five minutes of building, it costs you the same
  five minutes of guides, and nothing more.
- Private (F4) guides are written at most once a minute while you edit, and always when you leave a world,
  quit, or switch modes.

## 0.4.45 - 2026-08-01

### Fixed

- **Editing guides no longer gets slower as your world fills up.** Every single edit re-saved *every guide
  in the world*, and dragging a control point does that about ten times a second. On a world with a thousand
  guides one drag update took longer than the server's entire time budget for a tick. Saving now happens at
  the points that need it, coalesced — about twenty times less work while dragging, and none at all when
  nothing has changed.
- **Opening the Players dialog no longer stalls on a busy server.** It walked the whole guide list twice for
  every player listed; it now counts everyone in a single pass.
- Removed two guide-size thresholds in the drag code that could never be reached.

### Changed

- Cap refusal wording on the HUD is now in Title Case — `REFUSED — Guide Cap`, `REFUSED — Too Large`.

## 0.4.44 - 2026-08-01

### Fixed

- **The HUD now says which limit refused you.** The cap row could only ever say "over cap" while the chat
  message named the actual limit. It now reads `REFUSED — Guide Cap`, `Your Total`, `World Full` or
  `Too Large`, matching what chat says. Older servers, which cannot say which limit fired, still get the
  general wording.

## 0.4.43 - 2026-08-01

### Fixed

- **The tool remembers every shape again.** Seven of the fifteen — Dome, Cylinder, Cone, Box, Tapered
  Cylinder, Polygonal Prism and Tapered Polygonal Prism — were reset to Arch every time the game started,
  and the saved setting was overwritten as it happened, so the choice was gone rather than just ignored.

## 0.4.42 - 2026-08-01

### Fixed

- **Chiseling highlights now light up by themselves after loading into a world**, instead of staying dark
  until you refreshed them by hand. A guide built while the ground around it was still loading recorded
  "nothing is built here" and kept that answer for the rest of the session; it now looks again as the
  ground arrives.
- `/layout built refresh` says when guides are still waiting for their terrain, rather than staying silent
  about it.

## 0.4.41 - 2026-08-01

### Fixed

- **The Players dialog worked once and then went dead** — tabs stayed pressed in and did nothing, and no
  player could be selected. Introduced in 0.4.40 and reverted. Clicking still no longer pulls your typing
  cursor back to the filter box, which was the part of that change worth keeping.

## 0.4.40 - 2026-08-01

### Fixed

- **The Players list no longer looks empty after you filter a long roster.** It kept your scroll position
  without checking the shorter list still reached that far, so the rows were drawn above the window and it
  read as though the filter had matched nobody.
- **Aiming is cheaper in worlds with many guides.** Every guide anywhere in the world was being tested
  against your crosshair thirty-three times a second, however far away it was.
- A greyed-out row could light up one of its buttons when another player changed the selected guide.
- A long player name no longer runs through the numbers next to it in the Players list.

## 0.4.39 - 2026-08-01

### Fixed

- **The cap readout in the HUD no longer says "REFUSED — over cap" when nothing was refused.** It said so
  from the moment the HUD opened, in every version from 0.4.34 to 0.4.38.
- **A refused edit that lands outside the world now corrects your screen.** Previously the guide could stay
  where you dragged it even though the server had rejected the move.
- **Releasing a guide can no longer be dropped when you are editing very fast**, which could have left a
  guide locked until you disconnected.

## 0.4.38 - 2026-08-01

### Added

- **A limit on how fast Layout will accept edits from one player.** Set far above anything you can do by
  hand — dragging sends about ten edits a second and the allowance is 120 — so ordinary play never meets it.

### Fixed

- **Publishing private guides no longer re-saves every guide in the world once per guide.** Publishing 100
  guides wrote the world's guide data 100 times; it now writes once.
- **Publishing is limited to one batch at a time**, and a server that has private guides turned off no
  longer accepts them.
- **A very large guide no longer makes the chiselling colour update slow down over time.** Blocks you
  changed were being remembered forever and re-checked several times a second.
- **A guide can no longer settle across land that became protected while it was being checked.** Large
  guides are checked a piece at a time; if the claims change mid-check, it now checks again.
- **The two retired admin settings no longer report a change that never happened.** Asking for one used to
  rewrite `layout.json` and announce "EnableChalkDurability is now unlimited". It now ignores the request
  and tells you the truth.

## 0.4.37 - 2026-08-01

### Fixed

- **A guide placed with its two clicks too close together is now refused, with a message saying so.**
  Before, it was accepted as a guide with no voxels at all: invisible, listed at 0 voxels, and completely
  silent — and because zero is under every limit, it passed even a very small voxel cap.
- **Being refused twice in a row now tells you twice.** A second refusal within a few seconds was being
  suppressed. Repeated refusals while *dragging* are still grouped, since those arrive ten times a second.

## 0.4.36 - 2026-08-01

### Changed

- **Grabbing a guide now releases the one you had grabbed before.** In normal play you never notice — the
  game already only lets you grab one at a time. A guide you are still having validated is left alone.

### Fixed

- **Cancelling a reshape of a very large guide now actually stops the work.** It used to run to completion
  in the background, holding up everyone else's large guides.
- **Cancelling a large reshape and immediately reshaping again no longer loses the second reshape.** It
  used to snap back with no explanation.
- **A rare graphical hiccup when chiselling near a guide.** A background rebuild could fail and fall back
  to a slower one, which showed as a stutter.
- **Hiding a guide that is already hidden, or setting divisions to what they already are, no longer saves
  and re-broadcasts to everyone.**

## 0.4.35 - 2026-07-31

### Fixed

- **Guide positions are now checked before anything is drawn from them.** A guide file edited by hand, or a
  damaged world save, could previously lock up the game while loading. A guide with an impossible position
  is now skipped, and a note is written to the log so you know one was.

## 0.4.34 - 2026-07-31

### Fixed

- **Being refused because of a voxel limit now tells you which limit you hit.** Reshaping, rescaling,
  hiding, filling, rotating, moving and a dozen other actions used to just spring back in silence. Repeated
  refusals from a single drag are grouped so they cannot flood the chat.
- **The cap readout in the HUD now flashes when something is refused**, instead of sitting unchanged.
- **Lowering the world voxel limit below what the world is already using no longer freezes every guide.**
  It used to refuse every edit, including the shrinking and dispelling that would have brought the world
  back under the limit.

## 0.4.33 - 2026-07-29

### Changed

- **The limit fields step in useful amounts now.** A scroll or a spinner click moves voxels-per-guide by
  5,000, voxels-per-player by 25,000, voxels-in-the-world by 100,000, guides-per-player by 5, and
  guides-in-the-world by 100. The per-player fields in the Players window step by the same amounts.

## 0.4.32 - 2026-07-29

### Added

- **An Edit button on each row of the Overrides tab.** It takes you to the Players tab with that player
  already selected and their limits open for editing.
- **A Free button on each row of the Jail tab**, with the same confirmation the Players tab uses. Freeing
  from there keeps you on the Jail tab.

### Fixed

- **The jail confirmation message no longer draws over the guide list beneath it.**

## 0.4.31 - 2026-07-29

### Added

- **Player limits can be changed from the Players window.** Select someone and press **Edit** to change
  their per-guide cap, their total voxel cap and their guide limit. Nothing is sent until you press Save,
  and the fields then redraw with what the server actually stored. **Clear all** resets all three to the
  server's own limits. `0` in any field means "no override".
- **Jail and Free are in the window too**, on the selected player, behind a second confirming press that
  spells out what will happen. The commands still work exactly as before.
- **The player list now sorts, filters and scrolls.** Click **Player**, **Guides** or **Voxels** to sort by
  that column, click again to reverse it; it opens on the largest builders first. Type in the filter box to
  find someone by name. Jailed players are shown in orange, offline players dimmed, and anyone with their
  own limits is marked with a `*`.
- **A world total** at the top of the window: how many voxels and guides exist on the server altogether —
  the numbers the world-wide limits are measured against.

### Changed

- Each of a player's limits now says whether it came from the server or from an override set just for them.
- The Players window is wider, to fit the table and the details side by side.

## 0.4.29 - 0.4.30 - 2026-07-29

### Fixed

- **Buttons that do something now visibly press when you click them.** The Transform pad's move arrows and
  rotate corners, and the Reveal Near / Reveal All buttons, gave no feedback at all — clicking one looked
  the same as clicking the panel background.

### Changed

- The teeth on the settings gear are taller and more sharply tapered.

## 0.4.28 - 2026-07-29

### Added

- **A "send to ground" arrow in Transform mode**, below the Down arrow. It drops the guide straight down
  until it comes to rest on the ground below — the bottom of the guide meets the surface, the same way
  holding CTRL does during a free-move. It settles on the highest ground underneath, so nothing ends up
  buried, and it lands flush on slabs, stairs and chiselled blocks rather than a whole block above them.
  With Copy switched on, the copy is what lands.

### Changed

- **The rotate and tilt icons were redrawn.** Their arrowhead was merging into the ring it sat on, so the
  glyph read as a hook rather than an arrow.
- **The mirror icon was redrawn** so that each side of the line is a real shape and the reflection is
  visible.
- The Down arrow in the Transform pad moved up one space, so the vertical column runs Up, Down, ground.
- The Admin section no longer warns you when your own limits are overridden. `/layout info <you>` and the
  Players window's Overrides tab still report it.
- The Players button sits at the left of its row; the unsaved-changes notice moved to its own line.

### Fixed

- **The mod's own description was displaying a garbled character** where a dash should have been.
- In the Players window, a player's overrides are listed one per line instead of one long line that ran off
  the edge of the panel — and each line now names the command that clears *that* override, rather than
  naming `/layout voxelcap` for all three.

## 0.4.27 - 2026-07-28

### Fixed

- **Private guides are no longer limited in size.** They were quietly capped at a cumulative million
  voxels — a handful of large guides — even though every other limit on them read as unlimited. Private
  guides are stored on your own machine and nobody else can see them, so the server's limits do not apply
  to them; the only remaining bound is the size beyond which a guide cannot be drawn at all.

## 0.4.26 - 2026-07-28

### Added

- **A Players list for admins.** A **Players** button beside Save on the settings page opens a window with
  three tabs. **Players** lists everyone Layout knows about — click one to see their limits, how much of
  each they are using, and their guides largest-first with sizes and coordinates. **Overrides** lists only
  the players whose limits differ from the server's, and what each one is set to. **Jail** lists who is
  currently suspended and how many guides they still have standing.

## 0.4.24 - 0.4.25 - 2026-07-28

### Fixed

- **Limit messages were printing `{0:n0}` instead of the actual number.** Eleven messages were affected,
  including every "that guide is too large" and "guide limit reached" warning, and the land-claim refusal
  that was showing `{0}, {1}, {2}` where the coordinates should have been.
- **The Admin panel now warns you when your own limits are overridden.** A personal override beats every
  cap in the panel, and the page was showing the server's numbers as though they applied to you. If you
  have one, an orange line now says so above the settings, with the command to clear it.

## 0.4.22 - 0.4.23 - 2026-07-28

### Added

- The running version is shown at the foot of the settings page, and written to the log at startup.

## 0.4.20 - 0.4.21 - 2026-07-28

### Changed

- **Admin settings now need a Save.** Editing a limit stages the change and the panel shows how many are
  unsaved; nothing reaches the server until Save is pressed. The fields then redraw with what the server
  actually stored, so a value it adjusted is visible rather than silently different from what you typed.
  Saving reports back in chat what each setting became.
- Chalk consumption was removed from the Admin panel. It is still in `layout.json`.

## 0.4.16 - 0.4.19 - 2026-07-28

### Added

- **Admin settings on the settings page.** An **Admin - Server Settings** section, visible only to admins,
  holding the five voxel and guide limits plus the private-guides switch. Changes apply to the running
  server and are written to its config file. Per-player commands (`/layout jail`, `limit`, `voxelcap`)
  are unchanged.
- **Reveal Near and Reveal All**, two new tiles on the Edit menu's Visibility row. Reveal Near un-hides
  every hidden guide within 6 blocks of you, whoever made it; Reveal All un-hides every hidden guide you
  made, anywhere. Both stay available when no guide is selected, which is when you need them.

### Fixed

- Admin changes were silently discarded if your own placement mode was set to Private.
- Turning private guides off left players' saved preference on Private, so they would flip back to it.

## 0.4.15 - 2026-07-28

### Added

- **CTRL while free-moving sets a guide down on the surface you are aiming at**, instead of keeping it at
  the distance it was picked up from. The bottom of the guide meets the face under the crosshair.

### Changed

- **Rectangles, squares and boxes can be placed at any angle.** They were the only shapes locked to
  north/south/east/west. The gesture changed to supply the missing rotation: a **rectangle** is now three
  clicks — a corner, the far end of one edge, then the width — and a **box** is four, with the height last.
  A **square** stays two clicks, since one edge already settles it, and SHIFT picks which side of that edge
  it sits on. Rectangles and boxes placed before this update keep their original shape exactly.

## 0.4.12 - 0.4.14 - 2026-07-28

### Added

- **Pick your own guide colours.** Choose **Custom** under Colors and a table appears with the seven parts of
  a guide — body, locked point, apex, anchor, private anchor, division mark and the chiseling highlight.
  Click the one you want to change, then click a colour from the grid. Starts from whichever palette you were
  already using, so you are adjusting rather than starting from scratch. **Reset colors** puts it all back.
- The colour table folds away with the arrow beside the Colors box, and starts folded each time you open the
  settings page.

### Changed

- Colour names in the interface now use the American spelling, to match the rest of the game.

## 0.4.8 - 0.4.11 - 2026-07-28

### Added

- **Colour schemes.** A new **Colors** setting with a **Red-Green Safe** palette alongside the default one.
  Locked points and apex points are red and green by default, which is exactly the pair red-green colour
  blindness cannot tell apart; the new palette moves them to colours that stay distinct. Changing the setting
  redraws every guide.

### Changed

- **The settings gear is a proper gear now** — a spoked wheel with teeth, drawn a little larger so the spokes
  are actually visible.

## 0.4.6 - 0.4.7 - 2026-07-28

### Added

- **Publish.** A button on the settings page that uploads all your private guides to the server in one press.
  Previously this needed you to switch to public mode first and then run a chat command in the right order;
  now the button does both.
- The settings page now tells you whether the server you are on **allows or denies private guides**, right
  above the switch. On a server that denies them, sliding to Private shows you the option and then slides
  back, and the notice flashes so you can see why.

## 0.4.2 - 0.4.5 - 2026-07-28

The settings page grew from a handful of controls into a real panel.

### Added

- **Layout: On / Off** at the top of the settings page — hide or show all your guides without a chat command.
- **Public / Private** is now a sliding switch showing both options, in the same blue and orange as the guide
  anchors themselves.
- **Chalking Kit refill shortcuts** can be turned on from the settings page. Both were previously only
  reachable by hand-editing a file, so most players never found them.
- **Every setting now explains itself** when you hover over it.

### Changed

- The settings page is grouped into **Appearance** and **Behaviour** sections.
- "Show built voxels" is now called **Chiseling Highlight**.
- The opacity bar is shorter, with its **Reset** button beside it.
- Removed the "Back to tool" and "Re-read the world" buttons — the gear returns you to the tool, and the
  world re-read is still available as a command.

### Fixed

- **Turning guides off no longer locks you out of the tool panel.** The panel stays open with everything
  greyed out except the settings page, so you can turn them back on. It also no longer slams shut the moment
  you flip the switch.
- Fixed a crash that closed the game when opening the settings page in 0.4.4.

## 0.4.1 - 2026-07-27

### Fixed

- A large guide that is edited while it is still growing in no longer leaves its wireframe behind at the old
  position. The wireframe now rebuilds whenever the guide it depicts has changed, instead of only when the
  guide was being moved. This also covers switching a guide from Wireframe to Shell, which had the same
  problem.

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
