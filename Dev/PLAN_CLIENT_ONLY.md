# Client-only fallback mode (F4) — implementation record

> Originally written after v0.1.27 as the phased plan for F4. **Status at v0.1.53: implemented and
> playtested on the `ClientOnlyFallback` branch; candidate headline feature for v0.2.0.** This document now
> records the final design, the behavior matrix, and the deliberate departures from the original proposal.
> Detailed F4 version history is in `SESSION_12.md`; the subsequent optimization/interaction pass is in
> `SESSION_13.md`; the v0.1.53 shell-scaling/mesh handoff is in `SESSION_14.md`.

## Goal delivered

Layout now works when the connected server does **not** have Layout installed. In that environment the
client becomes its own authority: the normal GUI, HUD, shapes, editing gestures, and undo/redo operate on
guides visible only to that player. The server never receives those guides and the world save is untouched.

The implementation also supports an explicitly permitted mixed environment on Layout-enabled servers:
public server guides and private client guides can be displayed and edited side by side.

## Final behavior matrix

| Connected server | Placement authority | Required tool gate | Anchor palette | Persistence |
|---|---|---|---|---|
| Server **without** Layout | Automatically client-only | Flax Twine in main hand + any vanilla Hammer variant in off-hand; damage does not matter | Normal Blue/Indigo | Client file, per world/server + player UID |
| Layout server, `allowClientOnlyMode=false` (default) | Public/server only | Real Layout tool | Blue/Indigo | Server world save |
| Layout server, policy allowed, public mode | Public/server | Real Layout tool | Public Blue/Indigo; overlaid private guides Orange/Burnt Orange | Server world save for public; client file for private overlay |
| Layout server, policy allowed, private mode | New guides are private/client-only | Real Layout tool | Private Orange/Burnt Orange; public guides remain Blue/Indigo | Client file for private; server world save unchanged |

The private orange palette is intentionally used **only** where public and private guides coexist. A
fallback guide on a server without Layout keeps the established yellow-body + blue-anchor visual language.

## Authority detection and routing

- `ClientAuthorityMode` has `Detecting`, `Networked`, and `Local` states.
- A real Layout bulk sync is the positive proof that the server runs Layout. Channel state alone was not
  reliable across every join path, so the client waits through a three-second grace period before falling
  back locally when no sync arrives.
- `ClientNetworkHandler` owns the routing seam. It maintains one combined read-only mirror plus separate
  server/local ID sets, so renderer, HUD, GUI, and controller continue to consume one guide collection.
- Operations on an existing guide route by **that guide's authority**, not by the current placement mode.
  Thus a player can edit an orange private guide while public placement is selected, or a blue public guide
  while private placement is selected.
- Undo/redo remembers the authority of the most recently mutated guide. Merely switching placement mode or
  looking at a guide does not redirect Ctrl+Z/Ctrl+Y.

## Local authority architecture

The implementation followed the original plan's recommended option A:

- `GuideManager` was narrowed behind `IGuidePersistence`, an injected block-solidity probe, and a direct
  logger. The normal server composition still uses savegame-backed persistence and server block access.
- `LocalGuideAuthority` runs the same `GuideManager` and `UndoManager` client-side, then feeds accepted
  changes through `ClientNetworkHandler`'s existing mirror-apply paths.
- The local path implements create, delete, hide/show, scale, projection, fill, divisions, polygon sides,
  spring-back, lock/unlock, insert, grab/move/release/cancel, soft-point flow, constraint breaking, and
  undo/redo.
- Local edit-lock packets are synthetic single-player state used to preserve the controller's existing
  insert-adoption and grabbed-point behavior; there is no local contention manager.
- Local configurable caps are unlimited, but `GuideManager.HardVoxelCeiling` still applies. Private guides
  cannot create an unrenderable scan-guard sentinel.

The duplicated drag orchestration in `LocalGuideAuthority` and `ServerNetworkHandler` is intentional. It
kept the established multiplayer handler stable. If the two paths become costly to maintain, extracting a
shared side-neutral orchestrator remains the sanctioned future refactor.

## Activation: final decision

The original plan proposed an item-less toggle. Playtesting rejected that interaction because Layout's
normal behavior is a held tool whose HUD appears immediately and whose **F** key opens the settings GUI.
The final fallback deliberately preserves that behavior with vanilla items:

- **Main hand:** `game:flaxtwine`.
- **Off-hand:** any vanilla Hammer (`EnumTool.Hammer` or a `hammer[-variant]` code), including damaged tools.
- **F:** opens the normal Layout GUI; there is no separate enable/disable toggle.

Earlier test gates (Wrench + Coil of Rope, then Wrench + Cattail) were discarded. Coil of Rope already owns
an F interaction, and the final Hammer + Flax Twine pair proved reliable. On a server that does run Layout,
even private placement requires the real Layout tool; substitute vanilla items never bypass the server's
established tool requirement.

## Policy, preference, and commands

**Server `layout.json`:**

- `allowClientOnlyMode` (default `false`) controls whether clients may load/use a private overlay while
  connected to that Layout server. Server owners therefore decide whether hidden private planning is
  permitted.

**Client `layout-client.json`:**

- `forceClientOnly` (default `false`) remembers the player's preferred placement mode. On a Layout server
  it is honored only when `allowClientOnlyMode=true`; denial does not erase the preference for other worlds.
- The config includes `_forceClientOnlyNote` explaining the server dependency.

**Commands:**

- `/layout private` — persist the preference and switch new placements to private mode when permitted.
- `/layout public` — persist the preference and switch new placements to public/server mode.
- `/layout client push all` — while in public mode, publish all loaded private guides to the server.
- `.layout dispel all` — remove every private guide in the current client world file.
- `.layout dispel <chunk radius>` — remove nearby private guides using the server command's
  Chebyshev chunk-radius rule.
- `/layout dispel all|<chunk radius>` remains the existing `controlserver` command for public guides only.

The leading dot on `.layout dispel` is intentional: it is registered as a client command and works
even when the server has no Layout command tree.

## Private persistence and recovery

Private guides are **not** written into the main world save. `ClientWorldGuidePersistence` stores them in
Layout's client data folder under `ClientOnlyGuides`, keyed by a stable hash of:

1. Vintage Story's savegame/server-world identifier; and
2. the player's UID.

This prevents guides from crossing between worlds or between accounts sharing one computer. Writes use a
temporary file followed by atomic replacement. v0.1.45 added one previous-generation `.bak`; if the primary
payload cannot deserialize or rebuild, `GuideManager` loads the backup and the rejected primary is
quarantined with a `.corrupt-*` suffix. If file persistence cannot be constructed at all, the local
authority falls back to transient session-only storage and logs a warning.

## Public/private presentation

- The minor HUD shows **Client-Only Guides** while private placement mode is active. Public mode adds no
  extra label, preserving the original HUD.
- The targeted/selected guide readout says **Private guide** for a local guide regardless of placement mode.
- On a mixed Layout server, private anchors are Orange; an off-plane/non-cardinal far anchor is Burnt Orange.
  Public anchors remain Blue/Indigo. Body, lock, apex, division, and grabbed colors are unchanged.
- On a server without Layout, private anchors remain Blue/Indigo because no public/private distinction is
  needed there.
- Ownership colors are render-time state derived from the guide ID set. They are never persisted or wired.

## Publishing private guides

`/layout client push all` is an explicit client-to-server transfer:

1. The server requests the currently loaded private snapshots (maximum 100 per push).
2. Each accepted guide receives a new server GUID and the publishing player's UID.
3. Normal server guide-count/voxel caps, the hard voxel ceiling, and privilege checks are enforced.
4. The server broadcasts the new public guide.
5. Only after the acceptance response does the client delete the matching private copy.

Publishing is a committed transfer, **not** an undoable server creation. Recording it in server undo history
would let Ctrl+Z delete the only remaining copy after the private original had been removed; v0.1.44 removed
that unsafe behavior. The new public ID naturally renders with the public Blue/Indigo palette.

## Compatibility and schema

- `modinfo.json` sets `requiredOnServer=false`.
- Layout protocol version is **3**. Protocol 2 introduced the bulk-sync `allowClientOnlyMode` policy;
  protocol 3 appends the passive lock-marker field. Older Layout servers are
  treated as not supporting mixed private mode.
- Packet registration remains append-only; policy/mode/push packets were appended.
- **DataVersion is 8.** F4 itself added no private/public ownership field to `GuideData`; ownership remains a
  client mirror classification. Version 8 is the later additive `ControlPoint.IsLockMarker` field, which
  defaults false for older saves. Public world saves remain compatible with v0.1.27.
- Current implementation size: **66 C# source files**.

## Validation status and remaining caveats

The fallback tool, GUI, placement, persistence across reconnect, public/private overlay, commands, push,
reshape, HUD labeling, palettes, and mixed-authority undo routing were playtested successfully through
v0.1.53. The backup recovery code builds cleanly and normal persistence is confirmed; deliberate corruption
fault injection has not been separately reported.

The pre-existing B-S9-1 lock/constraint and pre-drag restoration behaviors were **not** F4 regressions. The
v0.1.49–v0.1.52 focused pass implemented first-hit lock picking, full drag snapshots, passive non-deforming
lock markers, and marker lifecycle/order fixes. The human confirms major improvement but wants more repeated
interaction testing before closure. F4 remains feature-complete; after that focused check, the remaining step
before calling it v0.2.0 is a final regression/release pass.
