# Session 42 — v0.4.65 → v0.4.71

**Branch:** `Codex`. **DataVersion 13. Protocol 27 → 28.** **87 source files** (none added).

This session replaced Roundover Path's route-first/radius-handle gesture with direct construction geometry,
then made initial placement modifiers consistent across the guide catalog. The human evaluated each control
direction in play: a numeric Radius field and automatic inside/outside-corner probing were both rejected;
three exact profile clicks followed by the sweep path was reported to work “MUCH better.”

---

## 1. Two control experiments, both deliberately withdrawn

### v0.4.65 — Radius field

The first experiment added a Radius field/spinner to the guide GUI, matching Sides and Divisions. It made
the control more visible but not more spatially understandable: the player still had to infer how a number
mapped onto the corner in front of them. The human found the result less intuitive, so the field and its
configuration state were removed rather than carried forward as a second way to define the same geometry.

### v0.4.66 — block-probed corner choice

The second experiment inferred an interior or exterior roundover from material around the first placement
probe. It reduced explicit input, but chiselled and partial blocks made the inference less reliable than the
player's own intended profile. The human rejected it after play. The occupancy-probing path was removed in
full; it is not a dormant alternative.

## 2. v0.4.67 — profile first, sweep second

Roundover placement now uses exact clicked geometry:

1. Click the sharp corner.
2. Click the first profile endpoint.
3. Click the second profile endpoint.
4. Click points along the sweep path; click the final point again to place.

There is no positional or angular snapping. The two profile legs are the literal vectors from the sharp
corner to the clicked endpoints, so asymmetrical and non-axis-aligned profiles remain possible. The
quarter-round midpoint is derived by the shape; the player never has to click a point floating in air.

The shared chain field now contains the open sweep route followed by two terminal profile handles. Both
handles are stored as Primary control points and remain editable. `RoundoverShape` detects that role pair
when rebuilding saved/wire guides. Older Roundovers contain one terminal Primary handle and retain the
original constant-radius interpretation.

This changed the meaning of existing create fields without adding a packet, so protocol moved from 27 to
28. A profile-first create request uses `Closed = true` only as the authority-side construction discriminator;
the created Roundover remains an open sweep and `GuideData.IsClosed` remains reserved for Free-Shape. Public
placement is gated to protocol-28 servers. Private client-authoritative placement remains local.

## 3. v0.4.68–v0.4.69 — material-side placement and readable preview

Holding SHIFT while placing a Roundover point moves that exact point into the targeted material by one guide
cell (half a cell for Surface projection, whose ordinary anchor lies on the face). This solves exterior
corners where an exact click on the outside face otherwise puts the guide voxel outside the block.

Once both profile legs exist, the live sweep preview switches to wireframe so it does not hide the inside
corner being aimed at. The first wireframe used only the rounded midline and floated away from the clicked
sharp corner. v0.4.69 replaced it with three independent rails: each clicked profile endpoint swept along
the path, plus the original sharp-corner route between them. The same special case is used by draft, adaptive,
settled and grabbed Roundover wireframes; rails are marched independently so no false cross-connections appear.

## 4. v0.4.70–v0.4.71 — universal initial-placement modifiers

Idle Create-mode CTRL+Left-click bypasses Layout's own guide hit test. The ordinary game block selection still
identifies the real block behind a translucent guide, so this starts a new guide instead of grabbing the guide
under the crosshair. Existing active-draft and active-grab CTRL meanings are unchanged. The held-help tooltip
reads **“Bypass Grab and Ignore Existing Guides.”**

SHIFT on the first click now embeds every guide type. That choice is stored for the life of the draft, so all
later block-selected points remain on the material side without holding SHIFT again. Existing modifiers that
begin after the first anchor—vertical Line, invert, flat-side and similar controls—retain their meanings.
Roundover also retains per-point SHIFT embedding when its first point was not embedded. Over an existing guide,
plain SHIFT remains Spring Back; CTRL+SHIFT combines bypass with embedded new placement. The tooltip was
shortened to **“Embed Guide.”**

## 5. Verification and release artifacts

- Release build: **0 warnings, 0 errors**.
- `git diff --check`: pass before documentation.
- `Layout0.4.65.zip` through `Layout0.4.71.zip` were produced for the successive play iterations.
- Final `Layout0.4.71.zip`: 42 entries / 374,949 bytes; root metadata and DLL; 39/39 assets; no directory
  entries or backslash paths; embedded metadata reports v0.4.71; packaged DLL matches the Release DLL.
- Final archive SHA-256: `7E1F021BA3B09DAA1F5F977BC818224AD055520950809DD23422CE91D68FC86A`.
- No permanent automated test project was added. Shape and placement behavior remains validated primarily
  through the human's in-game iteration.

---

## Delivered

- **v0.4.65** — Added the GUI Radius-field control experiment for Roundover; rejected in play and removed.
- **v0.4.66** — Added automatic inside/outside corner probing; rejected in play and removed.
- **v0.4.67** — Replaced both experiments with exact three-click profile construction followed by an open
  sweep path; preserved legacy one-handle Roundovers and moved public creation to protocol 28.
- **v0.4.68** — Added per-point SHIFT material embedding for Roundover and a wireframe live sweep preview.
- **v0.4.69** — Replaced the floating midline with top, sharp-corner and bottom construction rails.
- **v0.4.70** — Added idle CTRL+Left-click guide-target bypass and its held-help tooltip.
- **v0.4.71** — Renamed the SHIFT tooltip to “Embed Guide” and extended first-click embedding, persistently,
  to every guide type without disturbing later-stage modifiers.

## Decisions

- Prefer explicit construction geometry over a numeric radius or world inference. Chiselling is already a
  precision activity, and the player knows the intended cut better than a block-occupancy heuristic.
- Do not snap profile or route points. Exact aim is part of the tool's contract; convenience must not remove
  reachable geometry.
- Preserve old Roundovers in place. Their one-handle representation is read as its original geometry, while
  the two terminal Primary roles identify the new form without rewriting saved state.
- Treat embedding as a draft origin choice for every guide. A first embedded point means the player is
  constructing within material, so later points should not silently return to the air side.
- Keep bypass scoped to idle Create mode. CTRL already has useful meanings during a draft or grab, and those
  remain intact.

## Traps

⚠️ **`Closed = true` does not make a Roundover closed.** It is a create-request discriminator selecting the
two-profile constructor. `GuideManager` persists closed state only for Free-Shape; later Roundover decoding
must use the two terminal Primary roles. Do not simplify those roles or switch persisted detection to
`GuideData.IsClosed`.

⚠️ **A single Roundover curve is not an adequate construction wireframe.** The rounded midpoint can float
away from the exact surfaces the player clicked. Keep the two swept profile endpoints and sharp-corner route
as three separately marched rails, including adaptive and grabbed wireframe paths.

⚠️ **Initial SHIFT and later SHIFT are stage-sensitive.** First-click SHIFT establishes persistent embedding;
after an ordinary first click, Roundover may still embed individual points, while other shapes retain their
existing vertical/invert/flat-side behavior. Over a guide, plain SHIFT is Spring Back unless CTRL explicitly
bypasses the guide.

## Flagged and unverified

**Judgement calls awaiting review:**

- None. Each control direction in this session came directly from play feedback.

**Claims not tested:**

- No new known defect is recorded. Broader combinations of embedded placement with every shape and projection
  mode remain ordinary feature playtesting rather than a specific unverified correctness claim.
