# SESSION 23 — v0.3.22 → v0.3.34: moderation, claim-aware guides, and streamed immense-guide work

> This session added persistent per-player moderation and capacity controls, made suspended drafts remain
> visible in the HUD, taught every public-guide geometry path to respect Vintage Story build permissions,
> and replaced immense placement/sculpt stalls with bounded client/server pipelines and organic materialization.
> Current checkpoint: **v0.3.34**, **DataVersion 12**, **protocol 16**, **77 C# source files**, **15 shape
> types / 21 picker tiles**.

## 1. Delivered iteration arc

- **v0.3.22–v0.3.23 — persistent public-guide administration.** Layout gained world-scoped player policies
  for jail state, concurrent-guide overrides, and per-guide voxel-cap overrides. The server synchronizes a
  player's effective voxel cap and jail state at join and whenever an administrator changes it, advancing the
  protocol to **14**. The command set now includes `/layout jail`, `/layout free`, `/layout limit`,
  `/layout voxelcap`, `/layout info`, `/layout jailroster`, and `/layout top`; administrative dispel also accepts
  creator and guide-ID targets. Policies resolve online and known offline players and persist independently of
  guide data under `layout:adminpolicies`.
- **v0.3.24 — comatose-draft HUD.** Swapping away from the Chalking Kit no longer hides the HUD when a draft
  is still active. The last settled measurements remain visible as a faint reminder, the status becomes
  `Creating - Paused`, all text/accent treatment is desaturated and greatly reduced in opacity, and re-equipping
  resumes the normal live HUD. The world-space preview still disappears while unequipped; only the reminder
  remains. Cancelling or completing the draft clears the paused state normally.
- **v0.3.25 — claim-aware public guides and complete paused icon treatment.** Public guide creation and every
  public geometry mutation now use Vintage Story's authoritative land-claim permission system. The shape icon
  received its own forced-grey paused glyph so it cannot remain yellow merely because custom icon rendering
  ignores disabled-button tint. No red or invalid live draft preview was added in this revision.
- **v0.3.26–v0.3.27 — immense placement leaves the server tick.** Large public volume candidates are isolated,
  counted, and reduced to a claim footprint on one below-normal worker. Claim checks return to the tick thread
  under a 128-check / 1 ms budget. Small guides retain the immediate path. An explicit placement-rejection
  packet (protocol **15**) lets the client discard a retained provisional visual immediately.
- **v0.3.28–v0.3.32 — progressive placement handoff.** Expensive volume shapes gained cancellable progressive
  scanners. The client keeps a selected-scale wire scaffold visible, streams bounded preview/clean meshes,
  adopts matching authority echoes without rebuilding, prevents a second placement from replacing an active
  immense job, and preserves a scaffold when the final click outruns draft refinement. Old materialization
  meshes retire over later frames rather than being destroyed in one spike.
- **v0.3.33 — culling and personal rendering control.** Placed guides and remote markers are culled against
  the player's live Vintage Story `viewDistance`, using conservative guide bounds so a huge guide remains
  visible while any part intersects range. `/layout off` and `/layout on` personally disable/enable the entire
  Layout render pass without deleting state; `.layout off|on` provides the client-only fallback. Protocol is
  **16**.
- **v0.3.34 — organic growth and bounded immense sculpting.** Square/hash-bucket reveal was replaced by
  deterministic multi-seed neighbour growth. Smooth broad/detail noise plus fine grain produces torn fronts,
  tendrils, bays, merging islands, and mold-like coverage while preserving the exact final voxel set. Immense
  sculpt release no longer rebuilds a full shell synchronously: the client keeps its working visual and streams
  the result, while the server validates the isolated final candidate through the same single low-priority lane.

## 2. Administrative command contract

All moderation commands require `controlserver` and apply to **public** Layout state. Private guides remain
local to their owner until publication.

### Player restrictions

- `/layout jail <player>` blocks every public mutation by that player, including placement, editing,
  undo/redo, and publishing private guides. If the player is online, Layout cancels their active public
  activity, releases their edit locks, removes their public draft anchor, clears their session undo history,
  and immediately refreshes the client policy state.
- `/layout free <player>` restores public access. It deliberately leaves that player's guide-limit and voxel-cap
  overrides intact.
- `/layout limit <player> <number>` sets that player's concurrent public-guide cap. `0` removes the personal
  override and restores the server default. Lowering the cap below current usage never deletes guides; it
  prevents guide creation/restoration until usage is back under the effective limit.
- `/layout voxelcap <player> <number>` sets that player's per-guide voxel cap. `0` restores the server default.
  A personal cap never bypasses the world-total cap or the absolute ten-million-voxel safety ceiling. An actor
  with a lower cap may preserve or shrink an already-legitimate oversized guide instead of corrupting totals
  or trapping it permanently.

### Inspection and cleanup

- `/layout info` reports current public guide count, total guide voxels, configured limits, policy counts,
  active edit locks, and the largest guide with its anchor coordinates and short ID.
- `/layout info <player>` reports jail state, current guide/voxel usage, effective limits and overrides, and
  whether the player is online.
- `/layout jailroster` lists every jailed player, including offline entries and current public-guide count.
- `/layout top guides` lists the ten largest public guides with shape, voxel count, creator, in-world anchor
  coordinates, and a short usable ID. `/layout top players` lists the ten highest-usage creators by voxels and
  guide count and marks jailed entries.
- `/layout dispel player <player>` removes every public guide attributed to that creator.
  `/layout dispel guide <id>` accepts a full ID or an unambiguous prefix of at least four characters. Existing
  `/layout dispel all` and chunk-radius forms remain available.

Administrative deletion force-clears locks and transient drag state before broadcasting the removal. Policy
records store player UIDs as authority and retain only last-known names for human-readable commands/output.

## 3. Claim-protection contract

### What is protected

1. **Only public guides are claim-gated.** A private guide is a client-only overlay and cannot clutter another
   player's world. Publishing it is the point at which the server validates claims.
2. **The server is authoritative.** Validation calls the world claim API with `BuildOrBreak`; client checks are
   never trusted because Vintage Story's client-side access test is permissive by design.
3. **The exact rendered footprint is checked.** Layout voxelizes the candidate guide at its selected scale,
   deduplicates the world blocks touched by those cells, and requires build permission for every one. Anchor-only
   validation would miss arches, shells, and projections crossing a claim; bounding-box validation would wrongly
   reject hollow guides that merely surround one.
4. **Surface block faces protect both sides.** When a Surface tile lies exactly on a block boundary, both adjacent
   blocks are checked. This prevents a guide attached to the outside face of a claimed block from passing due to
   coordinate rounding.

### Covered authority paths

The centralized manager gate covers new placement, private-guide publication, control-point movement and
insertion/removal, constraint breaks/restores, rescaling, projection/plane changes, fill and wireframe changes,
polygon side changes, spring-back, and guide restoration through undo or redo. Claim denial rolls the attempted
mutation back before persistence or broadcast, preserves blocked undo/redo history for retry, resynchronizes an
optimistic client, and names the first denied block as `x, y, z` in the error.

The first public draft anchor also receives an immediate server check for useful early feedback. This is only an
early rejection; final placement still validates the complete generated shape. Deliberately deferred: a red or
otherwise invalid live preview while aiming through claimed land.

### Existing guides and cleanup

- Existing saved guides are never purged on load, and creating a new land claim does not silently delete an
  overlapping guide.
- A legacy/newly-claimed overlap may be progressively reduced: the candidate denied-block set must be a proper
  subset of the guide's previous denied-block set. It cannot retain the same forbidden footprint, exchange one
  forbidden block for another, or grow into new protected blocks.
- Dispel/delete and gesture release/cancellation remain cleanup paths. A player can therefore remove a guide or
  restore an in-flight gesture even if a claim was created while they were editing; claim owners are never left
  with an undeletable overlay.
- Players with `controlserver` bypass the claim gate for both ordinary tool operations and administrative cleanup.

## 4. Comatose-draft HUD contract

- Unequipping during an active draft preserves the HUD and its last settled dimensions, voxel count, and cap
  readout. It does not continue expensive aim/voxel work in the background.
- Paused text, labels, tile outline, and status use a grey low-opacity palette. The current-shape icon uses a
  dedicated `-current-paused` custom glyph that forces grey at low alpha; disabling the stock toggle alone was
  insufficient because custom icon faces do not inherit its grey treatment.
- Re-equipping clears the comatose flag before reopening/resuming the live guide preview. Unequipping with no
  active draft retains the previous behavior and closes the HUD completely.

## 5. Immense-guide execution contract

- **Small remains immediate.** A candidate at or below 8,000 voxels uses the established synchronous path so
  an inexpensive guide still appears immediately.
- **One server geometry worker.** Immense creates and final immense sculpts share one below-normal execution
  lane. Pure count/footprint work may run there; live manager collections, claims, commit, persistence, undo,
  broadcasts, and inventory mutation remain on the server thread.
- **Bounded claims.** At most 128 claim blocks and approximately 1 ms are spent per 20 ms tick. The authority
  lock remains held during an immense sculpt validation, but the acting client has already left its gesture.
- **One client materialization lane.** A new placement or reshape is refused while another immense visual is
  still materializing. Generation results carry pose/generation fingerprints, and cancellation makes stale
  producers stop instead of joining a newer pose.
- **No release cliff.** Final placement can adopt the already-refined draft; final sculpt can promote its
  materialized grab meshes. Neither seam performs full selected-scale voxelization/upload on the render thread.
  Superseded GPU batches are retired incrementally.

## 6. Organic materialization contract

- Several deterministic, well-separated seeds nucleate across the complete shell. Functional marker voxels may
  seed a few regions, but division marks cannot dominate the pattern.
- Every later voxel is reached through a face/edge/corner neighbour at the selected voxel scale. This is actual
  connected growth, not a shuffle of rectangular scan tiles or independent hash buckets.
- Smooth 9-cell and 4-cell noise bends the growth fronts; fine coordinate grain adds tears and branches.
  Growth batches stream to the bounded mesh queue as soon as each step is ready.
- The selected-scale wire scaffold remains the continuity visual while exact occupancy is discovered. Clean
  final meshes replace the deliberately ragged preview only after every bounded clean batch is ready.
- Verification covered 9,600 negative-coordinate planar voxels and a 7,720-voxel hollow shell: every exact
  voxel appeared once, with 18 and 16 true nucleation points respectively.

## 7. View-range and visibility controls

- Culling reads the game's live `viewDistance` client setting. Conservative sampled bounds use an enclosing
  sphere; a guide is skipped only when the whole bound is outside range.
- `/layout off` and `/layout on` are personal commands. Off suppresses placed guides, local drafts, retained
  materializations, grab visuals, and remote draft markers without deleting any guide or changing authority.
  Work resumes when rendering is enabled. Client-only worlds use `.layout off|on`.

## 8. Persistence and compatibility

- **DataVersion remains 12.** Claim checks derive from current geometry and world claims; no guide record fields
  were added.
- **Protocol is 16.** Protocol 14 added `PlayerGuidePolicyPacket`; protocol 15 added explicit immense-placement
  rejection; protocol 16 added the personal render-enable packet. Registration remains append-only.
- Administrative policies use their own versioned, world-scoped save payload at `layout:adminpolicies`.
  Empty policies are removed; jail, guide-limit, and voxel-cap fields may coexist on one UID.
- Existing guides and saves load unchanged. Existing server defaults remain the fallback whenever a personal
  override is zero or absent.

## 9. Release checkpoint

- Current package: `Layout0.3.34.zip` in `Documents/ChatGPT/LayoutZips`.
- Release build: **0 warnings / 0 errors**.
- Package verification: **40 ZIP entries**, including all **37 asset files**; `Layout.dll`, `modinfo.json`, and
  `modicon.png` are at the ZIP root; all archive paths use forward slashes; the packaged DLL SHA-256 matches the
  Release output.
- In-game multiplayer testing against owned, shared, and denied claims remains recommended, particularly
  Surface boundaries and progressive retreat. The immediate playtest priority is immense placement/sculpt
  smoothness, the organic reveal silhouette and cadence, view-distance culling, and `/layout off|on`.
