# Session 34 — v0.4.28 → v0.4.33

**Branch:** `beta`. **DataVersion 13 unchanged. Protocol 23 → 24.** **83 source files** (none added).
Every revision shipped as its own zip into `..\Layout Zips\` and was playtested by the human as it landed.

The Session-33 polish queue — all seven items — plus a new Transform action, a GUI feedback fix, and the
Players dialog becoming the place per-player limits are actually edited.

> **Read §7 before adding any variable-length text to a dialog.** A static text wraps inside its bounds but
> the bounds never grow, so a height guessed short does not clip — it overprints whatever is below. That
> shipped three times in this session before it was fixed properly.

---

## 1. The Session-33 polish queue (v0.4.28) — all seven

The list sat at the top of `TODO.md` from 2026-07-28. None of it blocked play; all of it was visible.

1. **The admin personal-override warning is gone.** The orange "your own limits are overridden" line in
   `GuideToolGui.BuildAdminSection`. It earned its keep once — a forgotten 10,000,000-voxel override was
   what made the caps look broken across six revisions in Session 33 — but the human did not want it on the
   page. **The override itself is untouched:** `LayoutAdminConfigPacket` still carries the per-player
   fields (registration is append-only), the Players dialog's Overrides tab still shows them, and
   `/layout info <player>` still reports them. Only the line went. Its dead helper `DescribeOverrides`
   went with it; `GuidePlayersDialog` has its own, still in use.

2. **The Players button is at the left edge of its row.** Save stays right. The two are not a pair: Save
   commits the staged edits above it and belongs beside them; Players opens a separate dialog and changes
   nothing.
   **The unsaved-changes marker moved to its own line above the row.** With a button at each end the gap
   between them is about 110 px, which is what "2 unsaved changes" needs at detail size — close enough that
   any future rewording would clip. A full-width line cannot clip at any wording and reads more like the
   warning it is. It costs its 18 px only while something is unsaved.

3. **The `Overridden:` line was split into one line per override.** It was a single fixed-height 18 px
   static text carrying up to three values, the player's name and a command, and ran well past the 378 px
   detail pane. Splitting it also fixed a quieter defect: **the old line named `/layout voxelcap` whatever
   the override actually was**, so an admin looking at a cumulative or guide-count override was told to
   clear it with the wrong command. (Superseded later the same session — see §5.)

4. **The rotate/tilt arrowheads were redrawn.** See §3.

5. **Down moved up one slot; a send-to-ground arrow took the bottom.** See §2.

6. **The mirror glyph was redrawn.** See §3.

7. **The gear teeth are taller and more steeply tapered.** See §3.

---

## 2. Send to ground (v0.4.28)

A third tile in the Transform pad's vertical column: **Up, Down, ⇓**. It drops the guide straight down
until its underside comes to rest on the ground beneath it.

`GuideToolController.GroundDropSixteenths` returns the drop in 1/16 units, always zero or negative.

**It reuses `TryGuideFloorSixteenths` rather than deriving the contact a second time.** That is T1's rule —
the guide's lowest voxel plane meets the surface — applied downward, and sharing the method is what stops
the CTRL free-move snap and this button ever disagreeing about where the bottom of a guide is.

Four things about the search:

- **The highest ground under the footprint wins**, not the ground under the centre. On a slope that is the
  difference between setting a house plan down on the hillside and burying half of it: the guide stops on
  the first thing it would touch, which is what dropping an object does.
- **Sampled, not swept.** A large guide's footprint is thousands of block columns and this runs on a button
  press, so the footprint is sampled on a grid of at most 32×32 points. A terrain spike thinner than the
  sample spacing can be missed, letting a corner intersect it; the alternative is a click that stalls the
  client on a hundred-block plan. **Guides up to 32 blocks across — nearly all of them — are sampled at
  every block and cannot miss anything.**
- **Material comes from collision boxes** via `BlockOccupancy`, so the guide lands flush on a slab, a stair
  or a chiselled block instead of a whole block above it. The instance is local to the call and thrown
  away: this fires once per click, and a cache kept between clicks would hold a stale picture of a world
  the player is actively building in.
- **Two resolutions keep it cheap.** Falling a cell at a time through open sky would be 2,560 queries per
  sample point; the block classification skips a whole block per query and only the block that actually
  stops the fall is examined cell by cell. A partial block that happens to be hollow at that column — the
  gap between two fence posts — correctly does not stop the fall.

The drop is rounded **toward zero** onto the guide's own voxel grid, so a coarse-scale guide comes to rest
on, or up to one voxel proud of, the surface — never sunk into it.

**Step and Mirror both sit this tile out, and the hover text says so.** Step, because the distance is not a
step — it is however far the ground happens to be. Mirror, because a mirrored guide has a *different*
underside and the drop is solved before the flip, so composing the two would land a dome its own height out
of place. **Copy does apply**, and is worth having: a copy is a duplicate of unchanged geometry, so the
drop solved for the original is exactly right for it.

⚠️ **Flagged:** this is the one arrow in a state-driven pad that ignores a lit toggle.

The GUI reaches the controller through `GuideToolGui.SetGroundDropResolver`, handed over by
`LayoutModSystem` — not a constructor argument, because the controller takes the GUI and so cannot be
built before it.

---

## 3. Icons (v0.4.28 → v0.4.33)

All three went through `dev/RenderIcon.ps1`, which grew ports of `DrawRotate`, `DrawActionMirror`,
`DrawMoveGround` and `DrawMoveArrow` so they could be looked at before shipping.

**Rotate / tilt — the arrowhead was the defect.** It was a *stroked chevron*, and a chevron's trailing leg
runs back along the direction of travel, which on a ring of this radius is very nearly the ring itself. That
leg lay on top of the arc and vanished into it, leaving only the outward leg showing: the glyph read as a
hook, or a flag with a bar across the top. The legs were also far too long for the ring — 11.9 against a
radius of 16 — which is what made the surviving one look like a bar rather than a barb.

It is now a **filled triangle**, and **the arc stops 24° short of it** so shaft and head do not fuse. Four
parameterisations were rendered across all four buttons and compared; `back 8.5 / half 4.5 / over 1.0` won.
5.2 was a blunt wedge wider than the ring it sat on; 3.8 was too faint to read the direction of at 42 px.

**Mirror.** The old glyph drew two *open* polylines — a vertical edge with a single line running out to a
point — so neither side was a closed shape, and the pair read as two arrowheads spreading apart. It is now
two closed right triangles either side of the dashed axis. **The form has to be asymmetric** or the icon
says nothing: a shape symmetric about a vertical axis looks identical to its own reflection, so the glyph
would be depicting mirroring by showing the one case where mirroring changes nothing.

**Gear.** Teeth went from 4.0 to 6.0 tall. **The height came out of the ROOT, not the tip** — the tips
already sit as far out as the Canvas zoom allows, so raising them would have pushed the silhouette outside
the tile. `rRimIn` followed the root down so the rim kept its thickness rather than thinning to a wire that
breaks up at 22 px. The taper then went 0.22 → 0.19 → **0.13**; the human could not see 0.19, correctly —
at r 25.5 it moved the tip chord by under a pixel at 42 px. Do not go much below 0.13: the tip chord is
then about 4 design units, roughly one pixel at 18 px.

---

## 4. Momentary tiles now visibly press (v0.4.29)

Human-reported: clicking a move arrow looked exactly like clicking dead panel background.

**Every momentary tile forced itself back off in the same call that ran its action** — `AddMoveTile`,
`AddRotateTile` and `AddRevealTile` all did `SetValue(false)` inside their own handler — so the lit state
existed for less than a frame and was never drawn. All three now go through one `AddMomentaryTile` helper
that lights the tile, runs the action, and lets it back up 120 ms later.

Two things this needed:

- **Every click has to count as a press**, whichever way the underlying toggle happened to flip. While a
  tile is still lit, the next click flips it *off*, and a handler that only acted on the ON edge would
  swallow every second click of a fast run of nudges. The old code got away with ignoring the OFF edge only
  because it reset synchronously, so an OFF edge never arrived from a user click.
- **Releases are timestamped, not cancelled.** A pending release that finds a newer press stands down and
  lets that press own the release. Cheaper than tracking timers, and it cannot leave a tile stuck down,
  because the newer press always scheduled its own.

Not touched: latching tiles already showed state, and `AddSmallButton` gets the vanilla pressed chrome.
**Still open:** the bare chevrons (`BareIconElement`) that expand the catalog, colour table and Admin
section have no press state — they draw no chrome at all, so one would have to be drawn from scratch.

---

## 5. The Players dialog became editable (v0.4.31 → v0.4.33, protocol 24)

Human-directed, after a scoping conversation. Delivered: the edit flow, jail/free with confirmation, and
the roster usability set. **Not** delivered (scoped out): detail panes on the Overrides/Jail tabs, and
per-player guide deletion.

### 5.1 Wire

**Protocol 23 → 24.** Two new C→S packets, both refused without `controlserver`:

- `PlayerPolicyEditPacket` — one player's three cap overrides in a single edit; 0 clears one.
- `PlayerJailPacket` — jail or free.

`PlayerRosterPacket` gained `WorldVoxelTotal` and `WorldGuideCount`, appended, so a protocol-23 server
simply sends zero.

**Two packets, not one with a flag.** Jailing is the one per-player action with immediate consequences for
somebody else's session. Folded into the staged cap edit, one Save press could retune a limit and suspend a
player at the same time.

### 5.2 The edit flow

Select a player → **Edit** turns the three cap lines into fields. **Edits stage and reach the server only on
Save**, for the same two reasons the server-settings section stages its own (v0.4.20): a cap typed digit by
digit would otherwise arrive as a series of nonsense values, each briefly real and each clamping that
player's draft preview; and an admin needs to be able to tell a typed value from a stored one.

**Every editing path answers with the whole roster**, not just the row it changed. The server is the
authority on what was actually stored, so a clamped value shows up honestly instead of leaving the dialog
displaying the number the admin typed. Edit mode stays open until that answer arrives, deliberately — that
is what makes a clamp visible rather than looking like acceptance.

⚠️ **The handlers must not only call the setters.** Each command does follow-up work the panel needs just as
much: the target's client caches its effective per-guide cap to clamp its draft preview
(`SendPlayerPolicy`), and its settings page shows its limits (`SendAdminConfig`). A GUI edit that skipped
those would leave the server correct and the affected player's client quietly stale until reconnect.
`OnPlayerJail` mirrors `OnJailCommand` in full, including the offline branch.

**Clamped, not refused.** The commands error out on a bad number because a person typed it and can read the
reply; here the reply is the roster, so an out-of-range value is pulled into range and the panel redraws
showing what the server really holds.

`RosterName(uid)` exists because the setters **remember** the name they are given — a policy on an offline
player is only readable later because `LastKnownName` was stored with it, so passing "Unknown" would
gradually erase the roster's own labels.

### 5.3 The roster view

- **Sorted by voxels descending by default**, with clickable Player / Guides / Voxels headers. Sorting is a
  client-side view choice; the server's online-first-then-name order is left alone.
- **World totals line** — what the Admin section's world-wide caps are actually measured against. Without
  it the panel showed the limits with nothing to compare them to.
- **Name filter**, debounced 350 ms, with focus and caret handed back after the recompose. Without that the
  field would take exactly one character per click.
- **Scrolling list**, 13 rows visible.
- Dialog widened **620 → 780**.
- **Each limit says `(override)` or `(server)` inline.** This replaced the separate "Overridden:" block
  from §1.3 — same information, no extra lines, and it cannot overflow.

**Rows are a custom element, not buttons**, because a button centres its one label and the numbers would
wander with the length of each name, making the sortable headers meaningless. They draw straight into the
dialog's own surface with no `LoadedTexture`, which sidesteps the v0.4.4 texture-allocation crash entirely.

⚠️ **The scroll container is wrapped in a fixed-height scope, and that is load-bearing.** The dialog sizes
itself to its children and the container is as tall as the whole roster; handed over directly, that height
is what the dialog would size itself to — a window taller than the screen, almost all of it empty space
behind a clipped list. The scope reports the window height instead. **The container still carries its true
height**, because that is what clicks are hit-tested against; clamping it would make rows unclickable the
moment they were scrolled into view. *This is the one piece that rests on reasoning rather than a test — if
the dialog ever opens absurdly tall on a long roster, this is why, and the fix is explicit dialog sizing.*

**Mouse-wheel-over-list does not scroll**, only dragging the bar. Routing the wheel to the scrollbar was not
attempted.

### 5.4 Cross-tab actions (v0.4.32)

- **Overrides tab → Edit** (right-aligned per row) hands over to the Players tab with that player selected
  and their fields open. It **clears the name filter** on the way: a filter left from an earlier search
  could exclude the very player being sent there and make the jump look like it had failed. The form is not
  duplicated on the Overrides tab — it needs the room the detail pane has, and two places to type a cap
  would mean two staging states to keep honest.
- **Jail tab → Free** (right-aligned per row) **stays on the Jail tab**, using the same two helpers
  (`AddJailButtons`, `AddJailWarning`) as the detail pane so the two routes cannot diverge. Buttons are
  right-aligned to the *armed* width so nothing shifts sideways when the confirmation appears.

Two things this required:

- ⚠️ **`_confirmJail` (bool) became `_confirmUid` (string).** A single flag would arm every row on the Jail
  tab at once — an admin aiming at one player would see every row asking for confirmation, and a stray
  second click anywhere would free the wrong one.
- ⚠️ **The roster and guide-list events had to be split into two handlers.** They shared one, and it closed
  edit mode — correct for the roster, which is the server's authoritative answer to a Save. But the
  Overrides Edit button both opens the form *and* requests that player's guides, so the guide list would
  arrive a tick later and close the form the click had just opened. `OnGuidesChanged` now only redraws.

### 5.5 Cap step sizes (v0.4.33, human-set)

`AdminCapSteps` (in `GuideToolGui.cs`) is the single home for the wheel/spinner step of every cap field:

| Field | Step |
|---|---|
| Voxels per guide | 5,000 |
| Voxels per player | 25,000 |
| Voxels in the world | 100,000 |
| Guides per player | 5 |
| Guides in the world | 100 |

The same five limits are edited from two panels, and they had independent step numbers — the Players
dialog's fields had **no step and no `IntMode` at all**, sitting on the element default. The three with a
per-player equivalent now read the same constant as their server-wide counterpart; the two world-wide ones
appear only in the Admin section, since there is no "voxels in the world" override for one person.

`IntMode` and `Interval` can only be set on the live element, so both panels push them post-compose.

---

## 6. Documentation changes made this session

- **TODO A12 (ground z-fighting) struck**, at the human's direction: the permanent face **outset** of
  v0.3.70 settled it. That change derived the offset from the guide's own voxel set and deleted the
  solidity probe, which removed the entry's third and most likely candidate cause outright.
  ⚠️ **The Session-28 order-dependence constraint was living inside A12 and is marked do-not-lose.** It has
  been moved to its own entry (**A12a**) rather than being deleted with the bug it was filed under.
- **TODO A10.4 (alternative rendering) closed as delivered** — the custom shader shipped in v0.3.60 and is
  what the mod renders with (8.2 ms → 1.8 ms on an 8M-voxel guide). Ordering and spatial culling remain
  explicitly gated behind A12a.

---

## 7. ⚠️ The measured-height trap — read before adding variable-length text to a dialog

**A static text wraps inside its bounds, but the bounds never grow to fit it.** A height guessed too small
does not clip and does not scroll — it draws straight over whatever comes next.

This shipped **three times** in this session. The human reported the visible one: the jail confirmation
warning overprinting the guide list below it, at a guessed 34 px for what is three lines. Fixing that by
measurement then caught two more of the same bug already shipped in v0.4.31 — the edit form's help text,
which would have drawn over the Save row, and the empty-roster message.

The fix is `GuidePlayersDialog.TextHeight`, which measures with
`capi.Gui.Text.GetQuantityTextLines(font, text, width, EnumLinebreakBehavior.Default)` ×
`GetLineHeight(font)`. **Every variable-length block in that dialog now goes through it.** Do the same
anywhere else rather than eyeballing a constant — the failure mode is silent and looks like a rendering bug
rather than a layout one.

---

## 8. Also fixed in passing

**`modinfo.json`'s description was mojibake on disk.** Its em-dash had been double-encoded, so the mod
description read `planning â€" place` in game and in the mod browser. Replaced with a plain ASCII hyphen and
the file rewritten byte-exact (BOM preserved, ASCII-only body). Same class of damage as the PowerShell
round-trip recorded in `SESSION_32.md` §8 — worth checking whenever a file is rewritten wholesale from
PowerShell.

---

## 9. Verification

- **Release build: 0 warnings, 0 errors** at every revision.
- Each revision shipped as `Layout0.4.2x.zip` / `Layout0.4.3x.zip` into `..\Layout Zips\`, **42 entries**,
  forward-slash entry paths, no directory entries — verified against v0.4.27's structure.
- Icons rendered at 18/22/28/42 px and inspected before shipping (`dev/RenderIcon.ps1`).
- **Playtested per revision by the human**, as always.
- **Not verified outside the game:** the scroll container's interaction with `FitToChildren` (§5.3).

---

## 10. Flagged for review

1. **Send-to-ground ignores Mirror** (§2) — the one arrow in a state-driven pad that does not respond to a
   lit toggle. Documented in its hover text.
2. **The unsaved-changes marker now costs a line** when something is unsaved (§1.2), where it previously
   shared the button row.
3. **The Overrides tab's Edit navigates away** to the Players tab and clears the filter (§5.4). The jump is
   deliberate but it is the only control in the dialog that changes tabs.
4. **Bare chevrons still have no press feedback** (§4).
5. **No mouse-wheel scrolling** in the Players list (§5.3).
6. **`MaxListedGuideRows` = 12** in the detail pane, down from the 60 the server sends — 60 lines is over a
   thousand pixels of dialog. The list is largest-first, so the dropped rows are the least interesting.
