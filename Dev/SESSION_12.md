# SESSION 12 — v0.1.28 → v0.1.45: ClientOnlyFallback

> **Scope.** F4 moved from a feasibility plan to a working client-only authority, then expanded into an
> opt-in public/private overlay for Layout-enabled servers. The implementation lives on the
> `ClientOnlyFallback` branch. Code is committed and pushed through **v0.1.45** in two commits:
> `967a576` (foundation) and `46350fe` (reshape parity, presentation, undo/push safety, and persistence
> hardening). **DataVersion remains 7; protocol version is 2; source count is 64.**

---

## 1. Why this branch was restarted

An earlier AI attempt at ClientOnlyFallback had accumulated interacting errors. The broken fallback folder
was discarded, a clean copy of the working Layout v0.1.27 tree became `LayoutClientOnlyFallback`, and work
continued on the dedicated GitHub branch rather than `main`.

The original `PLAN_CLIENT_ONLY.md` was assessed before implementation. Its central recommendation—run a
client-side `GuideManager` behind a small local authority—was retained. Several product decisions changed
through playtesting; those changes are recorded below and in the now-finalized plan document.

## 2. Foundation: side-neutral GuideManager and local routing (v0.1.28–v0.1.37)

`GuideManager` had only a few true server dependencies. They were narrowed into:

- `IGuidePersistence` (`ServerGuidePersistence`, `TransientGuidePersistence`, and later client-file
  persistence);
- `IGuideBlockProbe` / `BlockAccessorGuideProbe` for the Surface air-side bake; and
- a directly supplied `ILogger`.

`LocalGuideAuthority` now runs `GuideManager` + `UndoManager` on the client. `ClientNetworkHandler` owns a
nullable local authority and routes accepted local changes back through the same mirror-apply events used by
server broadcasts. The renderer, HUD, GUI, and most of the interaction controller did not need parallel
implementations.

Authority detection became join-time state (`Detecting`, `Networked`, `Local`). A Layout bulk sync is
positive proof of a modded server; after a three-second grace period without one, channel state resolves to
local fallback. `requiredOnServer=false` allows joining servers without Layout.

The first end-to-end path covered placement, deletion, property changes, spring-back, local undo/redo, and
client persistence. Private files are keyed by server/world savegame identifier and player UID rather than
being written into the world save.

### Activation iterations

The initial plan's hotkey toggle was rejected: the expected Layout feel is “hold the tool, HUD appears,
press F for the GUI.” The vanilla gate changed as conflicts were found:

1. Wrench off-hand + Coil of Rope main-hand — Coil of Rope already owns an F menu.
2. Wrench + Cattail — functioned as an interim test.
3. **Final:** any vanilla Hammer variant in the off-hand + Flax Twine in the main hand. Damaged hammers are
   accepted.

The gate applies only when the connected server lacks Layout. A Layout-enabled server—public or private
placement—continues to require the real Layout tool.

### Persistence and early commands

Client-only guides were changed from session-memory-only to a per-world/per-player JSON file after reconnect
playtesting showed persistence was desirable. `.layout client dispel all|<radius>` provides local cleanup;
the established `/layout dispel all|<radius>` remains a public/server admin command. v0.1.37 corrected the
server command tree's required name/privilege metadata after Vintage Story reported an “Incomplete command”
programming error.

## 3. Mixed servers: policy and mode semantics

The simple “server has Layout → always public” rule was expanded after discussion:

- Server config `allowClientOnlyMode`, default **false**, decides whether private overlays are allowed.
- Client config `forceClientOnly`, default **false**, is a preference only; the server policy wins.
- On permitted servers, public and private guides are overlaid in the same mirror.
- The real Layout item remains the gate, preventing vanilla substitutes from bypassing server expectations.
- Player UID participates in local storage isolation.

Final mode commands at v0.1.45:

- `/layout private`
- `/layout public`
- `/layout client push all`

The first two replace the less intuitive `/layout client set` and `/layout server set`. The push command
retains the `client` namespace because it operates on the private client collection.

## 4. Publication protocol

Protocol version 2 appends policy, placement-mode, and publication packets. Push is server-mediated:

- permitted only after switching to public mode;
- privilege-checked and capped to 100 submitted guides per operation;
- validated through server guide-count/voxel caps plus the unconditional hard ceiling;
- assigns a new public GUID and the publishing player's UID;
- broadcasts accepted guides; and
- removes only accepted private IDs after the client receives confirmation.

v0.1.42 closed an import-specific safety gap: `RestoreGuide`, reused by push, now rejects invalid voxel
scales and enforces `HardVoxelCeiling` even when configurable caps are disabled.

v0.1.44 made publication non-undoable. Earlier, push recorded a normal `CreateGuideCommand`; Ctrl+Z could
delete the new public guide after its private original had already been removed. Push is now a committed
transfer and does not redirect the client's last-operation undo route.

## 5. Reshape parity (v0.1.38)

The early local authority handled atomic operations but not full geometry editing. v0.1.38 added the local
drag session and routed local guide operations by guide ownership:

- grab / move / release / cancel;
- body insert and insert-adoption;
- lock-in-place inserts and lock-state feedback;
- `SoftPointFlow` reflow;
- absorb-or-break constraint handling;
- drag-origin snapshots and undo records; and
- cancellation/restoration behavior matching `ServerNetworkHandler`.

This restored the normal Create-mode reshape gestures on vanilla servers and in private overlays. Existing
shape spring-back/constraint oddities were observed but explicitly parked as older behavior for a later
focused pass.

## 6. Private-guide presentation (v0.1.39–v0.1.41)

- **v0.1.39:** targeted/selected HUD text says `Private guide` for local IDs.
- **v0.1.40:** added Orange/Burnt-Orange private anchor palettes while leaving every other role color
  unchanged.
- **v0.1.41:** narrowed orange to mixed Layout-enabled servers only. On a server without Layout, local guides
  keep the normal Blue/Indigo “vanilla Layout” anchors.

The side HUD displays `Client-Only Guides` only while private placement is active. The clean F-menu GUI was
left unchanged at the human's request. Ownership color is derived at render time from the local ID set, so a
pushed guide's new server ID automatically renders Blue/Indigo.

## 7. Mixed-authority undo routing (v0.1.43)

Existing-guide mutations already routed by guide ownership, but Ctrl+Z/Y originally followed placement
mode. That was wrong when editing a blue public guide during private placement or an orange private guide
during public placement.

The client now remembers the authority of the most recently mutated guide. Create, move, insert, delete,
settings, lock, spring-back, and other real mutations update this route; selection, mode switching, grabbing
without a mutation, and push do not. Before the first mutation in a world, placement mode is the fallback.

## 8. Persistence recovery and final command polish (v0.1.45)

`ClientWorldGuidePersistence` already used `.tmp` + atomic replacement. v0.1.45 added:

- a one-generation `.bak` containing the previous complete payload;
- `IRecoverableGuidePersistence` as an optional extension (server world-save persistence is unchanged);
- automatic backup load when the primary cannot deserialize/rebuild;
- best-effort quarantine of the rejected primary as `.corrupt-*`; and
- protection against rotating a rejected primary over the known-good backup.

The normal reconnect path and new commands were playtested successfully. Deliberate corruption injection was
not separately reported.

## 9. Final player-facing behavior

### Server without Layout

- Local mode resolves automatically.
- Hold Flax Twine main-hand + Hammer off-hand.
- HUD appears immediately; F opens the normal settings GUI.
- All ordinary placement/edit/delete/undo gestures work locally.
- Guides persist in the client file and remain Blue/Indigo.

### Layout server, private mode disallowed

- Normal public Layout behavior only.
- `forceClientOnly` is denied without being erased for other servers.
- Local overlay is not loaded/displayed.

### Layout server, private mode allowed

- Real Layout tool required.
- Public guides: Blue/Indigo, server-persisted.
- Private guides: Orange/Burnt Orange, client-persisted.
- `/layout private` and `/layout public` choose where new guides go.
- Either guide type can be edited regardless of placement mode; routing follows ownership.
- `/layout client push all` publishes private guides safely.

## 10. Validation and next step

The human playtested fallback activation, GUI access, placement, guide persistence, public/private overlay,
commands, push, reshape, HUD labels, anchor palettes, reconnect, and mixed-authority undo behavior. Builds
through v0.1.45 completed with zero warnings/errors. The implementation is committed and pushed on
`ClientOnlyFallback`.

The F4 feature is complete. Before promoting the version to **0.2.0**, perform one final regression pass on:

1. a server without Layout;
2. a Layout server with `allowClientOnlyMode=false`;
3. a Layout server with it enabled, covering public/private commands, cross-authority edits, reconnect, and
   push; and
4. an ordinary public-only multiplayer session to confirm no regression.

Deferred legacy work remains unchanged: B-S9-1 lock-in-place targeting/deformation, enormous fine-detail
guides, Roof/Tunnel volumes, concave Free-Shape fill, and re-constrain.
