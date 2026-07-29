# Session 33 — v0.4.15 → v0.4.26

**Branch:** `beta`. **DataVersion 12 → 13. Protocol 19 → 23.** **83 source files** (one added:
`src/UI/GuidePlayersDialog.cs`). Every revision was shipped as its own zip into `..\Layout Zips\` and
playtested by the human as it landed.

Two features, one long debugging arc, and a polish queue that came out of it.

---

## 1. T1 — CTRL surface-snap on free-move (v0.4.15)

Holding CTRL while a guide rides the crosshair sets it down on the surface under the crosshair instead of
riding the held view-ray depth. The contact rule was decided in Session 32 and is implemented as decided:
**the guide's lowest voxel plane meets the face.**

`GuideToolController.TryGuideFloorSixteenths` is the measurement. Three things about it matter:

- **It reads the shape's sampled outline, never the voxel set.** This runs on every tick of a drag, and
  voxelising a behemoth to find its underside is exactly the cost free-move exists to avoid. It reuses the
  cached targeting polyline, so it costs nothing new.
- **Phantom points are excluded.** An arch's phantoms sit BELOW its feet; including them would hang the
  whole guide in the air.
- **A Surface guide reads its plane offset instead.** It is drawn flattened onto that plane, so its stored
  points say nothing about where the decal actually lies.

The contact is solved first and snapped afterwards, per the decision note. Rounding the hit height to the
voxel grid before subtracting the underside would leave the guide a voxel proud of, or sunk into, the face
it is meant to rest on.

---

## 2. The Box/Square cardinal defect (v0.4.15)

**Root cause.** `RectangleShape` and `BoxShape` were the only two shapes in the catalog building their
sides from `ShapeGeometry.InPlaneAxes` — the plane's world axes. Every other shape derives its frame from
the clicks via `ShapeGeometry.TryGetFrame`. So they could only ever come out lined up north/south/east/west.

**Why it was not simply a bug to fix.** Two diagonal corners carry no rotation. The gesture had to supply
it, and the human chose (from four options) to add a click.

| | before | after |
|---|---|---|
| Rectangle | 2 clicks (diagonal corners) | 3 — corner · edge end · width |
| Square | 2 clicks (diagonal corners) | 2 — corner · edge end; **SHIFT** picks the side |
| Box | 3 clicks | 4 — corner · edge end · width · height |

**Square staying at two clicks** is the flagged call. One clicked edge fully determines a square, so a
width click would have nothing to say — exactly the reason equilateral stayed 2-click while the free
triangle went to 3. It does mean the square's two clicks changed meaning, from *opposite* corners to
*adjacent* ones.

**Legacy encoding, read in place and never rewritten.** Pre-v0.4.15 rectangles store two points and boxes
three. Both shapes resolve their frame from whichever encoding they carry, and the old form reproduces to
the voxel — verified, not asserted (21/21 harness against the built DLL, comparing full voxel sets). The
list is deliberately NOT migrated to the new form: **shapes are adopted on renderer worker threads**, so
mutating the shared control-point list from inside a shape would be a data race.

The Box's fourth click is a plain height click and inherits none of the tapered rim's machinery — no 60%
hold, no flare clamp, no release gate, no CTRL/SHIFT rim modifiers. `DraftManager.NeedsRimClick` became
`NeedsFourthClick`, with `IsTaperedRimStage` as the separate predicate for the rim-specific behaviour.

⚠️ **Behaviour change worth knowing:** dragging corner A of a rectangle now changes the edge direction
rather than sliding the whole shape, because A is an edge endpoint now, like a triangle's base anchor.

---

## 3. The Admin section (v0.4.16 → v0.4.26)

The settings page carried an explicit "deliberately client-only — server settings must not appear in a
player-facing panel" decision from Session 32. **The human reopened it.** That comment is now replaced with
one recording the reopening, so a later session does not read the old rule and undo this.

**What it holds:** the five server-wide limits (voxels per guide / per player / in the world; guides per
player / in the world) and the private-guides switch. Visible only to players with `controlserver`.

**What it deliberately does not hold:**

- `requiredPrivilege` — a free-text privilege name; fumbling it in a GUI locks every player, including the
  admin, out of the tool, which is exactly the situation you then cannot fix from inside the game.
- `undoHistoryDepth` — a memory trade-off nobody tunes in play.
- Chalk consumption — was in the panel v0.4.16–v0.4.19, **removed at the human's request** in v0.4.20.
- Admin lock-override — in the panel v0.4.16 only, **removed at the human's request** in v0.4.17.
- The per-PLAYER commands (`jail`, `free`, `limit`, `voxelcap`, `totalvoxelcap`). Each needs a player named
  first and a panel has nowhere good for a player picker. Both routes change the same state.

Retired settings keep their pinned enum values and packet fields (append-only, exactly like the dead refill
flags on `GuideBulkSyncPacket`); the server simply has no case for them.

**Changes are live, and that was verified rather than assumed.** `GuideManager.ApplyCaps` replaces the five
caps in place; a 9/9 harness proves a cap lowered in play genuinely refuses an oversized guide, that raising
it admits the same guide back, and that 0/negative both mean unlimited. `layout.json` is rewritten on every
change so it survives a restart, and a write failure is logged rather than thrown.

**Lowering a cap never deletes anything.** Caps are entry conditions checked on create and grow, so guides
already over a newly-lowered cap keep existing and rendering. `/layout dispel` remains the deliberate tool.

**Save, not live-apply (v0.4.20, human-directed).** Editing stages; the panel shows how many changes are
unsaved; Save sends the set at once. The original design sent each change on a 700 ms timer as it was
typed, which was wrong twice: a cap typed digit by digit arrived as a series of nonsense values, each
briefly real and each clamping every player's draft preview; and the admin had no way to tell an applied
value from one merely sitting in a field. After Save the fields redraw with what the server stored, so a
clamped value is visible instead of silently disagreeing.

**Also delivered:** Reveal Near / Reveal All on the Edit menu's Visibility row (v0.4.19), the Public/Private
toggle flipping on any click rather than only on its covered half (v0.4.18), and the Players dialog (v0.4.26
— three tabs over one server-built roster; see §6).

---

## 4. The six-revision cap hunt — and what it should have been

**The reported symptom:** "I set a 5,000 voxel cap and placed a 130,000 voxel guide."

**The actual cause:** a forgotten **10,000,000-voxel per-player override** on the human's own account,
which silently beat every server default. `LayoutAdminPolicyManager.EffectiveVoxelCap` returns the override
whenever one is set, and nothing on screen said one existed.

**What it cost:** six revisions and three speculative fixes aimed at a bug that was not there.

**The lesson, recorded because it is worth more than the fixes.** `/layout info <player>` already printed
the effective cap AND the override, and would have ended this in one round. Instead: proof the config was
right, proof the enforcement was right, and repeated guessing at the space between them. **Reach for the
existing diagnostic before writing a fix.** Two prior sessions' worth of admin commands existed precisely
to answer this question.

**Three wrong turns, kept as a record of what a false trail looks like:**

- Blamed creative/admin mode. Creative bypasses **chalk only**; `controlserver` bypasses **claims only**.
  Neither touches caps.
- Blamed duplicate mod zips in the Mods folder. **The human corrected this: Vintage Story loads the highest
  version.** Several zips coexisting is harmless.
- Added client and server logging plus a chat confirmation on save — which was not wasted, but was
  instrumentation added three rounds after the point where the existing diagnostic would have answered it.

**Four real bugs surfaced along the way, all of which would have bitten eventually:**

1. **Admin changes were silently dropped when the admin's own placement mode was Private** (v0.4.19).
   `SendAdminConfig` gated on the authority mode; "I place private guides" and "there is a server I can
   administer" are different questions.
2. **Reveal All could never match anything** (v0.4.19). The client filtered guides by creator, but
   `GuideDataDto` has never carried `CreatorUid` — only `CreatorName`, for the HUD. Moved server-side
   (`GuideRevealMinePacket`); adding the UID to the wire record would have broadcast every creator's UID to
   every client to serve one button.
3. **Private guides bypassed every server cap** (v0.4.22). Local authority reported the 10M hard ceiling
   unconditionally, so a player could step around any cap by moving one slider. Now they obey the server's
   caps when a Layout server is present; the no-server fallback stays unlimited. Honest-client enforcement,
   exactly like the private chalk charge.
4. **Turning private guides off left players' saved preference on Private** (v0.4.19), so they would flip
   straight back next session — `SendPlacementMode` was sent with `allowed: false`, and the client only
   updates the preference when the answer is "allowed".

---

## 5. ⚠️ The `SendIngameError` lang-key trap (v0.4.25) — read before adding a player-facing message

**`SendIngameError`'s message parameter is a LANG KEY.** Its trailing arguments are only applied when that
key resolves to a translated string. Ours never do — they are English sentences, not keys — so the string
is handed back verbatim and **every placeholder reaches the player as literal text**.

**Eleven messages were affected**, and had been since they were written: both copies each of "too large",
"per-guide limit", "cumulative limit", "world voxel budget" and "guide limit reached" (the immediate and
immense placement paths had drifted into near-identical copies), the restore-blocked message, and the
land-claim refusal, which was printing `{0}, {1}, {2}` where the coordinates should have been.

They only surfaced now because a cap finally refused something — with the 10M override in place, none of
them could ever fire.

**Every message is now fully formed before it is sent and none of these calls passes arguments.** The four
cap refusals are built in one place each, shared by both placement paths. An unsubstituted placeholder is
not a compile error and looks perfectly correct in the source, which is why this survived; there is a note
in `ServerNetworkHandler` saying so.

---

## 6. The Players dialog (v0.4.26)

`src/UI/GuidePlayersDialog.cs`, opened from the Players button beside Save. Three tabs in the human's
order: **Players**, **Overrides**, **Jail**.

- **One roster, three views.** The tabs filter the same server-built list rather than each fetching their
  own; three separate queries could disagree about someone whose policy changed between them.
- **"Known player" unions three sources** — everyone online, everyone carrying a policy, everyone with a
  guide standing. None alone is enough: an offline builder has no session, a builder on default limits has
  no policy, a jailed player who never built has no guides.
- **Read-only, deliberately.** It answers "who has what". Changing limits still goes through the commands,
  and each tab names the relevant one inline. Buttons here would mean confirmation and undo affordances for
  destructive per-player actions, which was not asked for.
- **A separate dialog, not another tool-panel page.** The tool panel is anchored right at a width driven by
  its 42 px tile grid; a player table wants to be wider and centred.
- Both requests re-check `controlserver` on arrival rather than trusting that the dialog only opens for
  admins.

⚠️ **Two caps, both flagged:** the dialog auto-sizes with no scroll pane, so it lists 30 rows per tab and
60 guides per player, summarising the rest. On a busy server the fix is a scroll pane, not a bigger number.

---

## 7. Verification

- **21/21** — Rectangle/Box geometry: legacy encodings reproduce to the voxel, rotated shapes are genuinely
  off-axis with equal diagonals, a square from one edge has four equal sides, SHIFT flips the side, and a
  width drag ignores along-edge drift.
- **9/9** — live caps: `ApplyCaps` lowering refuses, raising admits, 0 and negatives mean unlimited, and
  `LayoutServerConfig.Normalize` agrees.
- **Icons rendered before shipping** via `dev/RenderIcon.ps1` (two glyphs added to it). Reveal All was right
  first time; **Reveal Near was not** — six 6.2-unit blocks collapsed into a dashed underline at true 42 px.
  Height rescued them: the blocks are now taller than they are wide, which looks wrong in the source and
  right on screen. That is noted at the call site so nobody "fixes" it.
- Every revision built clean (0 warnings, 0 errors) and shipped as its own zip.

---

## 8. Flagged for review

1. **Square is 2 clicks, and its two clicks changed meaning** (§2). Cheap to reverse.
2. **Private guides now obey server caps** (§4, item 3). A behaviour change to a settled design, made under
   decide-and-flag because the alternative is a silent cap bypass. One-line revert.
3. **Dragging rectangle corner A now changes the edge direction** rather than sliding the shape (§2).
4. **T1 aimed at a wall or ceiling pushes the guide's BOTTOM to that face** — the accepted consequence of
   the decided contact rule, but it reads oddly and is worth confirming in play.
5. **Panel height.** The settings page roughly doubled with Admin open, and with the custom colour table
   open as well it is around 860 px. Both are collapsed by default and reset on every arrival, so reaching
   that takes two deliberate clicks — but a short screen may still overflow.
6. **The Players dialog's layout maths is unverified on screen** — column widths and the detail pane's
   height against the player list are the kind of thing that only looks right or wrong in game.
