# SESSION 19 — v0.2.29 → v0.2.35: Tapered Cylinder stabilization and whole-shape dust

> This arc removed the last hidden size ceiling from the Tapered Cylinder, made the height-to-rim transition
> resilient to stale clicks and lag, added load-aware draft throttling, and expanded placement feedback with
> bounded zero-gravity dust. Current release candidate: **v0.2.35**, **DataVersion 8**, **protocol 7**,
> **69 C# source files**.

---

## 1. Raised and unlimited voxel caps now remain meaningful (v0.2.29)

The corrected AABB accounting in v0.2.25 still left a fixed `MaxScanCells = 4,000,000` cutoff inside
`TaperedCylinderShape`. That private limit could bind before a server's raised cap, and it still bound when
the configured cap was unlimited.

The Tapered Cylinder no longer has that fixed scan cutoff. Its practical limits are now the configured
per-guide cap when positive and the existing **10M hard rendered-voxel ceiling** in all cases. Final
server-side validation remains authoritative. The human confirmed that drafting beyond the old ~6-block
barrier works.

---

## 2. A safe height → rim transition (v0.2.30–v0.2.32)

Immediately after click 3 set the height, the rim stage read the same cursor target. Height is commonly aimed
at a distant reference block, so the born top radius could jump from its intended **60% of the base** to an
enormous flare before the player deliberately moved toward the lid.

The shipped interaction is a one-way capture:

1. Click 3 establishes height and a 60% top radius.
2. Rim input stays parked at that born radius until the original left click is genuinely released.
3. Moving the aim into a narrow annular band around the born rim captures control.
4. After capture, the rim follows the cursor normally and never relocks during that placement.

The first release latch used the held-action callback and never observed a release in actual play, leaving
the rim stuck. v0.2.32 moved the latch to the engine's real client `MouseUp` event. The human confirmed the
final interaction is much better.

---

## 3. Adaptive draft work (v0.2.31)

Large guides can make full voxel generation expensive enough that input events arrive in bursts. The input
loop still samples at roughly **33 Hz**, but expensive preview/HUD/cap work now uses the most recent completed
draft count to select a lower rate:

| Last draft count | Expensive-work interval | Approximate rate |
|---:|---:|---:|
| ≤8,000 | 30 ms | 33 Hz |
| ≤50,000 | 100 ms | 10 Hz |
| ≤200,000 | 200 ms | 5 Hz |
| >200,000 | 500 ms | 2 Hz |

This is deliberately a responsiveness/load compromise, not a new cap. Unlimited public servers also skip
live per-guide cap-clamp counting because there is no configured per-guide boundary to clamp against; exact
completion validation and the 10M hard ceiling still apply. Further performance work is deferred until the
human chooses to revisit large guides.

---

## 4. Whole-shape complementary dust (v0.2.33–v0.2.35)

The original placement effect remains: short-lived chalk flecks fall from a sampled 2D curve or a 3D
shape's base ring, accompanied by the bow-release snap. A second dust family now complements it:

- **2D shapes:** capped sites distributed along the curve; zero gravity; broad randomized sideways scatter
  with slight vertical variation, like dust kicked outward by an impact.
- **3D shapes:** capped sites distributed across the full shell; zero gravity; outward/upward drift.
- **Sampling cost:** constant-bounded and parametric—Fibonacci sphere/dome points, stratified revolution
  shells for cylinder/cone/tapered cylinder, and area-weighted box faces. The effect never generates voxels.
- **Density:** up to 32 line sites and 56 3D shell sites, with ten sites as the useful small-shape floor.

The progression was: add shell dust (v0.2.33), remove gravity and increase density (v0.2.34), then change
flat-shape dust from uniformly upward to omnidirectional ground-like scatter (v0.2.35). The human confirmed
the final result.

---

## 5. Release verification and remaining work

- **Public/private multiplayer:** tested successfully. Release v0.2.35 and wait for field reports rather
  than holding for a larger matrix.
- **Chalking Powder jug recipe:** fired jug still works; raw jug correctly does not.
- **B-S9-1 lock cycle:** broadly much better. One exact residual remains: after unlocking a voxel, trying to
  lock the immediately adjacent voxel can snap targeting back to the formerly locked voxel.
- **Still unverified:** the hard 32-chalk ceiling against xskills itself, and a VS 1.22.0/1.22.1 smoke test.
- **Deferred:** further large-guide performance work (mesh Stage B/C if needed), Roof/Tunnel volumes,
  concave-safe Free-Shape fill, F3 re-constrain, and full Free-Shape draft broadcast.

Release package: `Layout0.2.35.zip`. Protocol remains **7** and DataVersion remains **8** throughout this arc.
