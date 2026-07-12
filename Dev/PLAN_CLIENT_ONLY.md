# Plan — Client-only fallback mode (F4)

> Implementation plan for **TODO.md F4**: let a player use Layout on a server that doesn't have the mod.
> Created after the v0.1.27 feasibility discussion. Status: **PLAN ONLY, not started.** Multi-revision
> feature; target landing as **0.2.0** (a headline feature), with intermediate dev builds for playtesting.

## Goal & scope
Let a player use Layout on a server that **doesn't** have the mod: the client becomes its own authority and
shows guides **visible only to that player**. Explicitly out of scope (impossible without the server):
world-shared guides, other players seeing them, server persistence. Guides live **in memory for the
session** (gone on disconnect). The normal networked mode is unchanged and must stay regression-free.

## The core problem
Today the client is a thin mirror: `GuideToolController`/GUI/HUD call `ClientNetworkHandler.Send*(...)`,
packets go to the server, `GuideManager` validates + persists + broadcasts, and the broadcast updates the
client mirror. On a vanilla server the `"layout"` channel never handshakes, so every `Send*` vanishes and no
broadcast returns. Client-only mode needs a **local authority** that processes those requests in-process and
updates the mirror directly.

## Architectural decision (the one real fork)
Three ways to get a local authority:

- **A. Local authority wrapping `GuideManager`** *(recommended).* A new `LocalGuideAuthority` runs a
  client-side `GuideManager` and reimplements only the reshape orchestration it needs (drag coalescing,
  break-on-move, insert-adopt, undo recording — the parts currently in `ServerNetworkHandler`). Leaves the
  server path 100% untouched; no shimming of server-only types.
- **B. Full loopback.** Run the real `ServerNetworkHandler` client-side against a fake network channel +
  fake player. Maximum reuse (zero orchestration duplication) but requires shimming `IServerPlayer`,
  `IServerNetworkChannel`, privileges, chat-command registration, join/leave — a wide, awkward surface.
- **C. Extract a shared side-agnostic orchestrator** that both `ServerNetworkHandler` and the local path
  call. Cleanest long-term, but it refactors the working multiplayer path (regression risk) and is the most
  upfront work.

**Recommendation: start with A**, and only extract to C later if the duplicated orchestration proves
painful. A concentrates all change in one new class plus mode-routing in `ClientNetworkHandler`, and keeps
the tested server path frozen.

The key reuse that makes A cheap: the pure layers (`ShapeFactory` + all shape classes, `SoftPointFlow`,
`UndoManager`, `GuideOperationResult`, the data model, `GuideRenderer`, `GuideHud`, `GuideToolGui`, most of
`GuideToolController`) are already authority-agnostic and carry over untouched.

## How each sub-problem is handled

**Mode detection.** At world-join, check whether the `"layout"` channel actually connected (VS exposes
channel connection state — Phase 0 confirms the exact API). Connected → networked mode (today's behavior).
Not connected → construct the local authority. Decided once per join.

**The routing seam.** Keep it entirely inside `ClientNetworkHandler` so nothing upstream changes: give it a
nullable `_local` authority. Every `Send*` method gets an early branch —
`if (_local != null) { _local.Handle…(…); return; }` — that processes locally and then feeds the result
back through `ClientNetworkHandler`'s **own existing receive-apply paths** (`OnCreate`/`OnUpdate`/`OnDelete`/
…), so the mirror update + events are byte-identical to a server broadcast. The renderer/HUD/GUI/controller
keep calling `_net` and reading `_net.Guides` with zero awareness of mode.

**Running `GuideManager` client-side (the one foundational refactor).** `GuideManager` only touches the
server API in ~4 spots: persistence (`SaveGame.StoreData/GetData`), the save/load events, `Logger`, and
`World.BlockAccessor` (the `ProbeAirSide` air-side bake). Narrow these:
- Inject an `IGuidePersistence` (server = SaveGame-backed + hooks `SaveGameLoaded`/`GameWorldSave`; local =
  in-memory, no events).
- Inject a block-solidity probe (server = `sapi` BlockAccessor; local = `capi` BlockAccessor — the client
  has one).
- Pass the `ILogger` directly (both `capi.Logger` and `sapi.Logger` exist).
This is contained (constructor + those 4 call sites) and leaves server behavior identical.

**Activation without the custom item.** A vanilla server can't have `layout:guidetool` (the server owns the
item registry, and registration happens at mod *load* before any server is known). In local mode, drive the
tool from a **hotkey toggle** instead of the held item: `GuideToolController.IsToolHeld()` becomes
`IsToolActive()` = held-item (networked) **or** local-mode-toggle-on. Going item-less in this mode sidesteps
a client-only item the server never heard of.

**Locks / undo.** Single player, so locks are trivial (the local player "holds" everything) —
`LocalGuideAuthority` skips lock logic entirely. Undo/redo reuses `UndoManager` client-side keyed to the
local player; the drag-coalescing (one undo entry per drag) is the main bit reimplemented from
`ServerNetworkHandler.OnRelease`/`DragSession`.

**Persistence.** In-memory for the session is the default and the natural fit for "temporary until logout."
Optional stretch: a client-side file keyed by server+world so guides survive a reconnect.

## Phased implementation (each phase builds + is testable)

- **Phase 0 — Spike / de-risk (~½ day).** Confirm: (1) how to read the channel's connected state; (2) that a
  client-registered item/recipe doesn't break joining a vanilla server (biggest unknown — if it does, commit
  to item-less); (3) `GuideManager` constructs cleanly with narrowed deps.
- **Phase 1 — Dependency-narrow `GuideManager`.** Introduce `IGuidePersistence` + block-probe injection;
  move save/load event wiring into the server composition. Networked mode must behave identically
  (regression pass).
- **Phase 2 — `LocalGuideAuthority` + routing, atomic ops first.** New class wrapping a client-side
  `GuideManager` (in-memory, generous/unlimited caps). Wire `ClientNetworkHandler` mode-routing for the
  **simple** ops: create, delete, and the property toggles (scale/fill/projection/divisions/sides), plus
  spring-back. Self-contained; proves the loop end-to-end.
- **Phase 3 — Reshape (the meat).** Grab/move/insert/lock-in-place with a minimal local drag session:
  soft-point flow (reuse `SoftPointFlow`), break-on-move, insert-adopt, and drag coalescing into one undo
  entry on release. Mirror `ServerNetworkHandler.OnUpdate/OnInsert/OnCancelGrab/OnRelease` locally.
- **Phase 4 — Undo/redo local.** Client-side `UndoManager`; verify create/delete/move/toggle/spring-back all
  undo cleanly.
- **Phase 5 — Mode detection + activation.** Wire join-time detection; add the hotkey toggle and
  `IsToolActive()`; handle the item gracefully per Phase 0's finding.
- **Phase 6 — Polish.** HUD/GUI indicator that you're in local mode; friendly messaging ("guides are local
  to you on this server"); optional client-file persistence; possibly different local caps defaults (ties
  into the human's enormous-fine-detail-guides wish).

## Risks
- **The item-on-vanilla-server unknown** (Phase 0 gates this) — mitigated by going hotkey/item-less.
- **Orchestration duplication** between `LocalGuideAuthority` and `ServerNetworkHandler` drifting over time —
  mitigated by keeping the local version lean and, if it bites, doing refactor C later.
- **Regression on the working networked path** from the `GuideManager` narrowing — mitigated by a full
  networked-mode playtest after Phase 1.
- **Scope** — multi-revision; target 0.2.0 with intermediate dev builds.

## Testing per phase
Each phase: build + a networked-mode regression check (nothing broke) **and** a local-mode check on a
genuinely vanilla server (or a local test server with the mod disabled server-side). Phase 3 is where
playtest feel matters most (reshape, undo).

## Semantic caveat (keep in view)
Local guides are single-player-visible; the settled "world-shared, server-authoritative, concurrent-edit-
with-locks" pillar does not apply in this mode (locks/caps/undo-gating become local no-ops). It's a solo
sketch layer that runs anywhere — not the collaborative tool.
