# SESSION 32 — v0.4.2–v0.4.14: the settings page (F9, F10, F11, T2)

**Checkpoint:** Layout **v0.4.14** built and packaged on the `beta` branch. **DataVersion remains 12**;
**wire protocol remains 19** — nothing here touches the wire. Packages: `Layout0.4.2.zip` … `Layout0.4.14.zip`
(there is no `Layout0.4.3.zip`; that number was consumed by a broken intermediate, see §8).
**82 source files** (one added: `src/Systems/GuidePalette.cs`).

This session emptied the queued backlog around the settings page. **F9** (in-game settings panel), **T2**
(its formatting and hover descriptions), **F10** (redraw the gear glyph) and **F11** (configurable colour
scheme) are all delivered. What remains from the old queue is **T1** alone.

The page went from five controls to a two-section panel with a master switch, a colour editor, a two-sided
privacy slider and a publish action — driven entirely by human direction, one revision at a time.

---

## 1. The arc, in order

| Version | What |
|---|---|
| 0.4.2 | F9 tier 1 + T2: master on/off, private guides, chalk refills; Appearance/Behaviour sections; hover text on every row |
| 0.4.3 | Guides-off no longer closes the GUI — the tool page composes inert instead |
| 0.4.4 | Page restyle: coloured Layout: On/Off at top, Chiseling Highlight, Reset beside the slider, sliding Public/Private toggle. **Crashed on compose** |
| 0.4.5 | Crash fix: `LoadedTexture` must exist before `LoadOrUpdateCairoTexture` |
| 0.4.6 | Server privacy policy line; "Publish Private Guides" button |
| 0.4.7 | Policy line right-aligned over the toggle, shakes and flashes on refusal; Client-Only wording; button shortened |
| 0.4.8 | **F11 colour schemes** (Default / Red-Green Safe / High Contrast) + **F10 first gear redraw** (filled, 6 teeth) |
| 0.4.9 | Gear redrawn from the human's reference image: spoked wheel, 12 teeth; title-bar size 18 → 22 |
| 0.4.10 | Gear to 8 teeth, thinner strokes, nudged left |
| 0.4.11 | Gear nudged down and phased half a tooth; **High Contrast removed** |
| 0.4.12 | **Custom scheme + swatch grid** — per-role colours, human-directed against the preset-only design |
| 0.4.13 | Role chips become a captioned four-column table |
| 0.4.14 | Dropdown moved left for a collapse chevron; colour table folds; "Colour" → "Color" in player-facing text |

---

## 2. The settings page as it now stands

```
              Settings                    ⚙

  Layout: On   (green / red when off)   [ o ]

  Appearance
  ─────────────────────────────────────────
  Guide opacity
  [=========|=========]  50%    [ Reset ]
  Colors            [ Custom ▾ ]      ▾
  Chiseling Highlight                 [ o ]

  Behaviour
  ─────────────────────────────────────────
                     Server: Private Allowed
  New guides     ┌────────┬────────┐
                 │ Public │▓▓▓▓▓▓▓▓│
                 └────────┴────────┘
                            [ Publish ]
  Refill from hotbar                  [ o ]
  Refill in inventory                 [ o ]
```

**Every row carries hover text.** That is T2 discharged: the explanatory prose removed in v0.3.87 for
overflowing the dialog is back as `AddAutoSizeHoverText`, which was the agreed replacement.

**The master switch sits above both section headers** because it is not one setting among others — it gates
all of them. Its label states the current state in the state's own colour (green on, red off).

---

## 3. Guides-off became DISABLE, not BLOCK (v0.4.3)

The v0.3.52 lockout treated "rendering off" as a reason to refuse everything, including opening the panel.
Once the on/off switch lived on the settings page that became a trap: flipping it closed the dialog that
held the only way to flip it back.

- `OnToolGuiHotkey` is **no longer gated on rendering.** Opening the panel is the one thing that must keep
  working while guides are hidden.
- `OnRenderingChanged` **no longer closes the GUI.** It pushes the state in via `GuideToolGui.SetRenderingEnabled`.
- The tool page composes **fully inert** — by extending the existing `deleteMode` flag rather than threading
  a second one through nine call sites. Every use of that flag was already "ghost this control", so a row
  added later inherits the behaviour for free. The Mode row, previously always live, greys out too.
- Header reads **"Guides hidden / Turn them back on with the gear above."**
- Left-click, right-click, undo and redo stay refused; the aim loop still returns early. The *functions*
  are disabled, the *panel* is not.

Edit and Transform arrive at their existing greyed "nothing selected" state for free, because turning
rendering off already clears the selection.

---

## 4. Public/Private: a sliding two-sided control

`SlidingChoiceElement` (in `GuideToolGui.cs`). Both options are always named and always coloured — blue for
Public, orange for Private, **the anchor RGB values from `GuideMeshBuilder` verbatim at 45% alpha** — and an
opaque slider covers whichever is not in force.

**Why not a switch.** A switch says on/off, and neither public nor private is the off state of the other.

**How the animation is free.** The track is drawn once into the composed surface; only the slider is drawn
per frame, from its own small texture, at an interpolated x. No recompose, no Cairo work per frame.
⚠️ **This forbids recomposing the dialog on click** — a recompose rebuilds the element at its new resting
position and the slide never happens. That is why the status line is updated in place instead.

**Clicking a half picks that half**, rather than any click flipping it: with both options named, clicking the
one you want is the obvious gesture, and clicking the option already in force must not turn it off.

### The preference is cross-server
A server that forbids private guides refuses the request, but the preference is **kept** — reverting it would
clobber the setting for every other server the player visits. The policy line above the toggle carries the
truth instead.

### The denied-server bounce (v0.4.7)
On a denying server, clicking Private lets the slider travel all the way over, hold ~500 ms, then slide back,
**and the policy line shakes and flashes as it returns**. A control that refuses to move looks broken and
never shows the player that "Private" is the thing behind it. Moving and returning says both "this is the
option" and "you cannot have it here" in one gesture. The preference is deliberately not touched — a bounce
is a demonstration, not a setting.

`AlertTextElement` exists for that shake: a stock dynamic text draws itself at its own bounds in its own
colour and nothing outside it can offset or tint that, so this bakes the text to a texture and moves it per
frame, with a **second near-white copy** for the flash (the 2D texture draw has no colour multiplier).

---

## 5. Publish Private Guides (v0.4.6)

`ClientNetworkHandler.PublishPrivateGuides()`. The chat route is server-PULLED: `/layout client push all`
refuses unless the player is already public, then asks the client to upload. The button folds the mode
switch in, so one press does what took two commands in the right order.

- **No new packet and no protocol bump.** `ClientGuidePushPacket` may be sent unprompted — the server's
  handler re-validates client-only mode and privilege on receipt regardless of how the push started, so the
  request packet was only ever the server's way of asking, never a permission token.
- **Going public is a round trip**, and until the server answers it still has the player in its client-only
  set and would reject the upload. The push is therefore **parked** and fired from `OnPlacementMode` when the
  switch is confirmed. Cleared on any answer, so a refused switch cannot leave it armed.
- The slider moves **after** the call, from `_config.ForceClientOnly`, so a publish that cannot proceed (no
  Layout server, nothing private to send) does not show a switch that never happened.

---

## 6. F11 — the colour system

### `GuidePalette` (new file, `src/Systems/`)

The colour table moved out of `GuideMeshBuilder` into an **immutable `GuidePalette` behind one static
reference**, swapped wholesale on change.

⚠️ **This fixes the shared-static hazard the F11 plan flagged, rather than inheriting it.** The colours used
to be static arrays mutated in place while meshes build on background workers, so a change part-way through a
large guide's materialization could be seen by some batches and not others — one guide wearing two palettes
until the rebuild settled. `BuildGuideMesh` now reads the reference **once** into a local and uses that
instance for every voxel, so a batch is always internally consistent: it sees the old palette or the new one,
never a mixture. The swap is a single aligned reference write; no lock on either side.

Scheme and alpha stay independent: the scheme picks *which* colour a role is, the opacity sliders pick how
solid. `ConfigureOpacities` became `ConfigurePalette`.

### The schemes

| Value | Scheme | |
|---|---|---|
| 0 | **Default** | The shipped colour language |
| 1 | **Red-Green Safe** | Okabe-Ito based |
| 2 | *(retired)* | Was High Contrast; removed in 0.4.11 |
| 3 | **Custom** | The player's own, per role |

**Red-Green Safe** puts locked points on **vermillion** and apex points on **reddish purple** — both still
read as warm markers to normal vision, but they separate for deuteranopia and protanopia, which red/green
never can. Anchors take the palette's true blue and sky blue, private anchors its orange pair, division marks
bluish green. The built highlight moves from cyan to white-cyan because cyan would sit too close to the new
sky-blue far anchor.

**Deuteranopia-safe and protanopia-safe were merged into one preset.** The original plan listed them
separately; the two palettes would have differed only in ways neither group can see.

**Value 2 stays retired, not reused.** A `layout-client.json` written by 0.4.8 or 0.4.9 can still say 2, and
`Normalize` folds any unknown value back to Default. Give a future scheme 3+.

### Custom and the swatch grid (v0.4.12–v0.4.13)

**The human pushed back on the preset-only design and was right.** Presets answer "I cannot tell these roles
apart". They do not answer "yellow disappears against sandstone", which is a real want in a building mod that
no fixed list will ever cover. With High Contrast removed the dropdown was down to two entries, which was
itself a symptom.

- **Seven editable roles**: body, locked, apex, anchor, private anchor, division, built. Pinned order — these
  are array indices in the config.
- **Sixteen swatches, not a free picker** (human-directed). Every swatch is legible at guide alpha over
  arbitrary stone, which "any colour at all" cannot promise — a mid-grey or a deep navy vanishes at 50% over
  rock.
- **One grid shared by every role**, with a captioned four-column role table above it. Seven per-role
  controls would have added ~170 px and made comparing two roles impossible — they would never be on screen
  beside each other at the same size. The table *is* the comparison.
- **Far-anchor shades are derived, not picked**: a quarter-step toward white from the chosen anchor, so the
  pair always tracks together. Offering them separately would let them drift until the distinction stopped
  reading.
- **Grabbed stays white in every scheme.** It is not a role competing for a hue — it is a momentary override
  on one voxel meaning "this is the point in your hand".
- **Switching to Custom seeds it from whichever preset was showing**, and only when `customColors` is null,
  so a later revisit never discards colours already chosen.
- Stored as `"#RRGGBB"` strings, hand-editable and diffable. A missing or malformed entry falls back to
  Default **per role** — one bad edit costs one colour, not the palette.
- The table **folds** (v0.4.14) and **defaults to collapsed on every arrival at the settings page**; it opens
  only at the moment Custom is chosen.

---

## 7. F10 — the gear

Redrawn twice. The first attempt (0.4.8) was a filled six-tooth silhouette; the human then supplied a
**reference image of a spoked wheel-gear**, which is what shipped.

**Filled, not stroked, is the whole trick.** The original spokes-and-ring existed because detail on a stroked
outline becomes a grey smudge at icon size — each tooth would be a two-pixel box drawn with a two-pixel pen.
As a solid silhouette the same teeth are bumps on a disc edge, which survives at two pixels.

**Three separate fill passes**, not one path: rim, spokes and hub overlap, and under a single even-odd path
every overlap would CANCEL — the spokes would punch holes through the hub. Separate fills mean an overlap
paints the same pixels twice.

**The proportions were measured, not guessed.** `dev/` does not carry the harness, but the glyph was ported
to GDI+ and rendered at 18/22/28/40 px before shipping. That caught two things a compile never would:

1. **Twelve teeth were too many at 18 px** — and an early eight-tooth attempt put valleys at 2.0 px against
   2.6 px tips, teeth half again wider than the gaps, which reads as a scalloped disc rather than a gear.
2. **The spoked design does not survive 18 px at all** — the spoke gaps close and the bore fills in. Hence
   `gearSize` **18 → 22** in `GuideToolGui`.

Final: **8 teeth, 6 spokes**, tips at 25.5 and roots at 21.5 in the 60-unit design box, rim inner 17.8, hub
7.6, bore 4.6, spoke half-width 1.9. **Phased half a tooth** (v0.4.11) so a VALLEY sits at twelve and six
o'clock; with eight teeth that lands valleys on all four cardinals. The spokes are deliberately NOT rotated
to match — six spokes cannot align with eight teeth at any phase, and rotating them 30° aims a spoke straight
into the top valley.

⚠️ **Radii are bounded by the Canvas zoom.** Design box 60 at 1.12 zoom means anything past ~26.8 from the
centre falls outside the tile. Do not raise the tips without lowering the zoom.

---

## 8. Two mistakes worth recording

**The v0.4.4 crash.** `capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag, ref tex)` does **not** create
the texture when the ref is null — the platform layer writes into the instance, so a null reference throws
inside the engine (`NullReferenceException` in `ClientPlatformWindows.LoadOrUpdateCairoTexture`). It threw
the moment the settings page composed, so the whole panel was unusable, and it **built clean**. Always
`if (tex == null) tex = new LoadedTexture(capi);` first. Every custom element added this session now does.

**The lost 0.4.3 zip.** A PowerShell `Get-Content -Raw | Set-Content -Encoding utf8` round-trip over
`GuideToolGui.cs` double-encoded every non-ASCII character in the file (em-dashes, arrows, the ▾/▴ glyphs in
comments). Caught, reversed, and verified against `git diff` that no region outside the intended edits had
changed. Nothing shipped in that state, but the version number was spent. **Do not round-trip source files
through PowerShell text cmdlets** — use the editor tools, or `[System.IO.File]::ReadAllText/WriteAllText`
with an explicit encoding on both ends.

---

## 9. Flagged for the human / open

1. **The greyed-out tool page** uses Delete mode's 22%-alpha ghost font, tuned for a page where only *some*
   rows grey. With the whole page ghosted it may want to be brighter.
2. **Whether opening the panel while guides are hidden should land on the settings page** rather than the
   greyed tool page. The "always opens on the tool" rule was kept, with a pointer line.
3. **Page height.** With Custom expanded the page runs ~520 px. The collapse chevron takes the worst of it,
   but if it overflows at large GUI scale the fix is folding Appearance/Behaviour, not shrinking controls.
4. **"New guides"** is the label invented for the privacy slider (the old "Private guides" no longer fitted
   once both options were named on the control).
5. **A third policy string** — `Client-Only: Always Private` — covers the no-Layout-server case the human's
   two-string request did not.
6. **Colouring the policy line green/red** was not requested; it matches the master switch.
7. **Red-Green Safe's apex-as-purple** is the biggest departure from the mod's established colour language
   and has not been playtested.
8. **Swatch coverage.** If none of the sixteen works against some material, adjust the set rather than
   widening it blindly — the legibility constraint is what ruled out a free picker.
9. `occupancyRecolour` **keeps its British spelling permanently.** It is a JSON key; renaming it would
   silently orphan the setting in every existing player's config.

---

## 10. Files touched

| File | |
|---|---|
| `src/Systems/GuidePalette.cs` | **new** — schemes, roles, swatches, hex parsing |
| `src/Systems/GuideMeshBuilder.cs` | colour table → palette reference; `ConfigurePalette` |
| `src/UI/GuideToolGui.cs` | the settings page; `SlidingChoiceElement`, `AlertTextElement`, `ColorCellElement` |
| `src/UI/LayoutToolIcons.cs` | `DrawGear` redrawn |
| `src/Client/GuideToolController.cs` | rendering gate no longer closes the GUI or blocks the hotkey |
| `src/Network/ClientNetworkHandler.cs` | `SetClientOnlyPreference`, `PublishPrivateGuides`, parked push |
| `src/Config/LayoutClientConfig.cs` | `colorScheme`, `customColors` |
| `src/LayoutModSystem.cs` | `ApplyGuideRendering`; palette push |
