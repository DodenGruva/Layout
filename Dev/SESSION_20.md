# SESSION 20 — v0.2.36 → v0.2.47: polygonal volumes, interaction modifiers, and lock precision

> This arc closed the final B-S9-1 adjacent-lock targeting residual, aligned ground storage and private
> commands with their normal user-facing forms, added straight and tapered polygonal prisms, introduced
> stage-aware native modifier notes, and made tapered-rim placement safe by default. Current release:
> **v0.2.47**, **DataVersion 9**, **protocol 9**, **70 C# source files**, **15 shape types / 21 picker tiles**.

---

## 1. Adjacent lock placement is precise (v0.2.36)

The remaining B-S9-1 reproduction was: lock a curve voxel, unlock it, then right-click the immediately
adjacent voxel; the former point could still win the broad point-target radius and receive the new lock.

Lock-in-place now makes the first rendered voxel entered by the ray authoritative. A control point owns a
click only when that exact visible voxel is the point's nearest rendered outline cell; otherwise the clicked
body voxel receives its own passive lock marker. Surface guides compare visible 2D cells, while Volumetric
guides compare full XYZ cells. Locked/primary/anchor roles retain deterministic precedence when two markers
share a cell.

The human confirmed the new interaction feels good. **B-S9-1 is closed.** The passive-marker design,
non-deforming placement, stale-marker cleanup, and curve-relative insertion order remain unchanged.

---

## 2. User-facing command and ground-storage cleanup (v0.2.37–v0.2.38)

- Setting down the Chalking Kit now uses **SHIFT+right-click**, matching Vintage Story's normal ground-
  storage gesture and the existing tooltip. The tool-idle guard remains, so the gesture cannot steal an
  in-progress draft or grab.
- The private/client command now mirrors the public server command with a leading dot:
  **`.layout dispel all`** and **`.layout dispel <chunk radius>`**. The obsolete `.layout client dispel …`
  prefix is gone. `/layout client push all`, `/layout private`, and `/layout public` are unchanged.

---

## 3. Polygonal Prism and Tapered Polygonal Prism (v0.2.38)

Two volume types extend the regular 2D Polygon gesture:

- **Polygonal Prism:** base axis + third-click height; straight sides.
- **Tapered Polygonal Prism:** base axis + third-click height + fourth-click top rim radius.

Both use the same 3–24 `Sides` setting as Polygon, including post-placement side-count edits. Odd/even side
changes preserve height and, for the tapered form, preserve the top/base radius ratio. They are hollow-only,
always Volumetric, cap-checked through threshold counting, available in public and private authority, and
included in whole-shell placement dust. `GuideShapeType` appends values 13 and 14; no old enum value moved.

This catalog/wire expansion advanced protocol **7 → 8**. The picker now contains **15 types / 21 tiles**.
`PolygonalPrismShape.cs` is the seventieth source file.

---

## 4. Placement constraints and live interaction notes (v0.2.39–v0.2.43)

### Constraints

- **Line:** SHIFT constrains vertically.
- **Free-Shape:** its established SHIFT-vertical and CTRL-horizontal/cardinal constraints remain.
- **Tapered volume rim:** CTRL closes the top rim to a single point, making an exact cone or pyramid.

### Native stage-aware help

The Chalking Kit now supplies Vintage Story `WorldInteraction` rows for applicable modifiers. The controller
refreshes the native held-item help when the placement stage changes, so a rim-only note does not appear at
the base stage. The shipped labels are concise and technical: **Constrain Horizontally, Constrain Vertically,
Center Apex, Invert Guide, Close Rim, Reset Guide** (later extended with the modifiers in §5).

Vintage Story normally composes held help only when the hotbar slot changes. Layout therefore refreshes the
active-slot help through the game's hotbar seam when its stage flags change. During those internal refreshes,
the inherited Chalking Kit ground-placement note is suppressed; that standard note appears only on a real
item swap. v0.2.43 fixed the localization namespace so raw keys such as `heldhelp-layout-closerim` no longer
appeared on screen.

---

## 5. Polygon orientation, diagonal constraints, and safe rim flaring (v0.2.45)

### Flat-side alignment

Holding **SHIFT** while setting the base of Polygon, Polygonal Prism, or Tapered Polygonal Prism rotates the
regular polygon so the first anchor centers a flat edge instead of a vertex. The orientation is stored as
`GuideData.FlatSideAligned`, survives save/load, public/private creation, bulk sync, undo snapshots, side-
count edits, HUD/preview measurement, cap checks, rendering, and particle placement. Old guides default to
the original vertex alignment. This additive field advances **DataVersion 8 → 9** and **protocol 8 → 9**.

### 45-degree diagonals

Holding **CTRL+SHIFT** while placing a Line or the next Free-Shape segment constrains it to a 45-degree slope
in the nearest cardinal vertical plane. SHIFT alone remains vertical; CTRL alone remains horizontal/cardinal.

### Safe tapered rims

The final rim of both tapered volume types is now bounded by the current base radius unless the player
deliberately holds **SHIFT**. SHIFT unlocks outward flare and bypasses the normal annular capture once the
height click has physically been released. CTRL still closes the rim to a point and wins if both keys are
held. Preview, HUD measurement, cap clamping, the completing click, and server creation use the same value.

The additional live notes are **Align Flat Side, Constrain Diagonally, Allow Flare**.

Numerical validation covered 3/4/5/6/7/24 sides in both vertex- and flat-aligned forms, tapered-prism
threshold counts, odd/even side reseating, and the `FlatSideAligned` wire round-trip.

---

## 6. GUI header finish (v0.2.46–v0.2.47)

An attempted adaptive font shrink for long Create-mode shape names in v0.2.46 did not read well and was
reverted. The final header simply removes the redundant `- Next guide:` phrase:

**Create Mode**                         **[right-aligned shape name]**

All names retain the normal font size and have substantially more room.

---

## 7. Release state and remaining work

- Final package: `Layout0.2.47.zip`; Release build: **0 warnings / 0 errors**.
- Public/private multiplayer had already passed at v0.2.35; the new protocol-9 client and server should be
  deployed together.
- **Closed:** B-S9-1 adjacent-lock targeting.
- **Still unverified:** the hard 32-chalk ceiling against xskills itself, and a VS 1.22.0/1.22.1 smoke test.
- **Deferred until requested:** further large-guide optimization (mesh Stage B/C), Roof/Tunnel volumes,
  concave-safe Free-Shape fill, F3 re-constrain, and full Free-Shape draft broadcast.
