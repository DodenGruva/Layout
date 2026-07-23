# SESSION 26 — v0.3.44–v0.3.52: meshing experiments, rollback, persistent visibility, and voxel budgets

**Checkpoint:** Layout v0.3.52 is built, packaged, playtested, and human-approved. DataVersion remains
**12**, wire protocol remains **16**, and the source tree remains **77 C# files**. The runnable package is
`..\LayoutZips\Layout0.3.52.zip`.

This record supersedes Session 25's conclusion that the v0.3.43 32-block spatial final meshes should remain
the settled renderer. Session 25's measurements are still valid for that particular camera position, but
broader playtesting exposed visible region seams and established that the overall visual/performance trade
was worse than the v0.3.42 renderer.

---

## 1. Why this arc happened

Session 25 showed that independently culling 32-block portions of an immense guide could sharply reduce
submitted geometry in a partial-view test. The follow-up goal was to reduce the fully visible cost with
same-colour greedy face merging while keeping close-range guide fidelity.

The experiments succeeded at reducing mesh batches and triangles, but they did not preserve Layout's visual
contract:

- v0.3.43's spatial ownership boundaries produced visible seams across immense domes.
- Early greedy passes tended to form thin horizontal or vertical strips. On curved guides, orientation could
  alternate between layers, producing obvious bands and a splotchy view-dependent appearance.
- Later face/depth variants improved uniformity but introduced abrupt opposite-side disappearance, blurry or
  imprecise voxel edges, excessive brightness, and clouds drawing in front of guides.
- The more elaborate materialization path delayed the clean-shell swap. In the worst immense-guide test,
  materialization progress paused for long intervals.
- Most importantly, the player's established immense-guide result regressed from roughly **140 FPS** on the
  earlier baseline to roughly **80 FPS** after the later experiments.

Correctness and visual fidelity take precedence over a synthetic triangle-count win, so the experimental
path was retired instead of being tuned indefinitely.

---

## 2. v0.3.44–v0.3.48 — experimental, then rejected

These versions explored same-colour coplanar merging, distance-dependent detail, curved-surface safeguards,
more uniform planar merging, depth behavior, and materialization scheduling. Each revision was built and
packaged for playtesting, but none is part of the accepted current architecture.

The lasting lesson is that a guide is not merely translucent geometry. Per-voxel precision, stable
front/back visibility, consistent curved-surface rhythm, clouds/world depth interaction, and uninterrupted
materialization all contribute to the intended appearance. Future renderer work must compare against the
accepted v0.3.42 visual baseline in real play, not only against batch and triangle counters.

---

## 3. v0.3.49 — exact renderer rollback

v0.3.49 restored `GuideRenderer` and `GuideMeshBuilder` to the archived v0.3.42 behavior:

- one clean final mesh partition set per guide (normally about 128 upload batches for an immense shell);
- exposed-face meshing with the established per-face inset and role colours;
- whole-guide live-view-distance and camera-frustum rejection;
- `.layout renderstats` instrumentation;
- the established bounded organic materialization and reload-scaffold behavior.

The 32-block spatial final meshes and every v0.3.44–v0.3.48 greedy/depth experiment were removed. Source
decompilation was compared with the archived v0.3.42 assemblies; the relevant renderer/builder logic matched
apart from normal assembly informational-version metadata.

This is the current settled renderer. Spatial subdivision and greedy merging are not queued follow-ups.
Revisit rendering only when a new measured bottleneck and a fidelity-preserving design justify another
experiment.

---

## 4. v0.3.50 — persistent visibility and cumulative creator budgets

### Personal rendering state

`layout-client.json` now stores:

```json
"guideRenderingEnabled": true
```

Both public `/layout off|on` and client-only `.layout off|on` update that setting immediately. The preference
survives disconnects, world changes, and game restarts, so a player who logs out with Layout off returns with
it still off.

### Server cap defaults

`layout.json` now defaults to:

| Setting | Default | Meaning |
|---|---:|---|
| `perGuideVoxelCap` | 500,000 | Maximum voxels in one public guide |
| `perPlayerTotalVoxelCap` | 1,000,000 | Maximum voxels across one creator's currently existing public guides |
| `totalVoxelCap` | 0 | World-wide voxel total is unlimited unless an administrator sets it |

The cumulative cap follows the guide's original `CreatorUid`, not the latest editor. Deleting a guide frees
that creator's budget. Existing over-cap guides still load and may be preserved or reduced, but they cannot
grow until usage is back within the limit. The check covers ordinary and immense creation, mutations,
restore/undo, and asynchronous exact-count paths; it cannot be bypassed by choosing a different operation.
Private client-authoritative guides remain outside the public server budget.

Config schema marker `configVersion: 1` performs a narrow migration: only the exact historical generated
defaults (`25,000` per guide and `250,000` world-wide) become the new defaults. Any administrator-selected
custom values are preserved field by field.

A focused manager smoke test reached the exact cap, rejected the next growth, kept different creators
independent, freed budget on deletion, and confirmed restore could not bypass the cap:

`PASS guide=101 cap=202 alice=202 bob=101`

---

## 5. v0.3.51–v0.3.52 — visible off-state and Chalking Kit lockout

If the saved preference is off when a world finishes loading, chat now says:

> [Layout] Guide rendering is currently off. Use /layout on to turn it back on (or .layout on in client-only mode).

While rendering is off, the Chalking Kit cannot invisibly create or alter guide state:

- primary and secondary tool actions are consumed;
- the F settings menu, Ctrl+Z undo, and Ctrl+Y redo are blocked;
- an attempted action flashes the same instruction through the in-game warning channel;
- aim/target work stops;
- turning Layout off cancels an active draft or grab, releases transient state, clears selection and previews,
  and closes the settings GUI.

The held-tool HUD switches to:

```text
Layout is Off
Guide rendering is off.
Use /layout on
to turn it back on.
```

Ground-storage interaction still falls through before guide-tool handling, so storage/refill behavior is not
mistaken for invisible guide use.

---

## 6. Verification and release state

- Every v0.3.44–v0.3.52 revision built successfully with **0 warnings and 0 errors**.
- `Layout0.3.52.zip` contains **40 entries**, including **37 assets**, with the DLL and manifest at the zip
  root and forward-slash entry paths.
- Packaged v0.3.52 DLL SHA-256:
  `A6065FCDD521A23D368D679A02D6F3EA33B9AFC9C45DDFCB8FB2E780F905EC35`
- Human playtesting approved the v0.3.49 visual/performance rollback, v0.3.50 persistence and budgets,
  v0.3.51 login reminder, and v0.3.52 Chalking Kit warning/HUD behavior.

---

## 7. Current architecture and next work

The accepted performance stack is now:

1. exposed-face meshing;
2. adaptive motion wireframes and selected-scale background refinement;
3. bounded organic materialization with reload scaffolds;
4. whole-guide distance/frustum culling;
5. local render statistics for evidence gathering.

Do not reintroduce spatial mesh regions or greedy face merging as an assumed next step. Any future renderer
proposal must preserve the v0.3.42 appearance up close and at distance, compare real frame rate as well as
submission counts, and be easy to disable or revert.

The ordinary next-session agenda returns to the standing backlog: the xskills 32-chalk verification,
Vintage Story 1.22.0/1.22.1 smoke testing, and requested features or field reports.
