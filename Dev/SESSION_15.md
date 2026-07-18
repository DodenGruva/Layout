# SESSION 15 — v0.2.0 → v0.2.9: the Chalking Kit (F5 delivered)

> The reskin, ground storage, and the full **F5 chalk-durability system** — designed in
> `PLAN_CHALKING_KIT.md`, now **implemented and playtested**. Current build: **v0.2.9**, **DataVersion 8**,
> **protocol 4**, **68 C# source files**. v0.2.0–v0.2.4 were committed as `9b94026`; this session's
> v0.2.5–v0.2.9 work is committed with this doc pass. All on `main`.

---

## 1. v0.2.0–v0.2.4 — reskin + ground storage (recap; committed earlier as `9b94026`)

- **Chalking Kit reskin (0.2.0):** the human's custom 3D model replaced the borrowed abacus art; item
  renamed **Chalking Kit** (mod stays **Layout**); gui/tp transforms tuned over several iterations; version
  promoted to 0.2.x at the human's direction.
- **Ground storage (0.2.1–0.2.3):** CTRL+SHIFT+right-click a block's top face with the kit **idle** sets it
  down (vanilla `GroundStorable`, `SingleCenter`); empty-hand right-click picks it up. Root cause of two
  failed attempts: since F4, `GuideToolController.OnInWorldAction` consumes right-clicks at the INPUT layer
  in both authority modes, so `ItemGuideTool.OnHeldInteractStart` had become unreachable — the controller
  now steps aside for the set-down gesture (`IsGroundStoreSetDownGesture`: real item + idle + CTRL+SHIFT
  from `Controls`, the same source the vanilla behavior checks).
- **0.2.4:** `groundStorageTransform`; the model's dead texture refs (`#0`/`null`/`string`) mapped to linen.

## 2. v0.2.5 — F5 chalk durability + Chalking Powder

**The kit has 32 chalk** (`durability: 32` in the itemtype; the vanilla durability bar is the meter).

- **Costs:** completed placement only — 2D **−1**, 3D volume **−2** (flat, never size-scaled). Reshaping,
  editing, dispelling, cancelled drafts, undo/redo: free. **No refund on undo** (players could cheese free
  chalk); redo re-creates via the command path so it never double-charges.
- **NO LOCKOUT, kit never breaks:** all chalk mutation goes through `ItemGuideTool.ConsumeChalk` /
  `TryAddChalk`, which write the durability attribute directly clamped to [0, max] — the vanilla
  `DamageItem` path (which destroys tools at 0) is never called. At 0 only NEW placement is blocked:
  client pre-check at the **first draft click** + authoritative server gate in `OnCreateRequest`, both with
  a clear "out of chalk" error.
- **Exemptions:** creative/spectator never consume; server config **`enableChalkDurability`** (default
  true) disables the whole mechanic; kits from older saves load full (unset attribute = max).
- **Chalking Powder** (`chalkingpowder`, class `LayoutChalkingPowder`): stacks 64, vanilla powdered-sulfur
  look (`game:item/food/flour` shape + crushed-sulfur texture), ground-storable (Messy12).
  **Refill:** hold powder, right-click — **tap = +4 chalk** to the first non-full kit anywhere in the
  hotbar (offhand included), **hold = pours** (~3/s, 0.45 s tap grace) until full or out. Server-side
  consumption; per-entity repeat counter (items are singletons).
- **Recipes:** 8 powder ringed around a container holding ≥**0.1 L yellow dye** → **8 Chalking Powder**
  (container survives; only the litres are consumed). The kit: plus-shape — **8× Chalking Powder** (top),
  Flax Twine (left), **Linen Sack** (centre), Rope (right), Copper Nails & Strips (bottom) — the 8-powder
  cost equals the 32 chalk a fresh kit contains.

## 3. v0.2.6 — private-mode chalk + recipe breadth (human-directed changes to the plan)

- **Private placement on a Layout server now SPENDS chalk** — "private is private, not free," superseding
  the plan's local-no-op recommendation. Mechanism: the server owns the inventory but cannot see private
  guides, so on a successful private create the client reports it via the new **`ChalkChargePacket`**
  (**protocol 3 → 4**); the server validates everything checkable (feature on, game mode, real kit held,
  `allowClientOnlyMode`) and clamps the cost to 1–2. A dishonest client could only skip its own charge —
  unpreventable for an invisible guide by construction. The accepted caveat stands: on a server WITHOUT
  Layout there is no channel and no real kit item, so no charge is possible.
- `LocalGuideAuthority.Create` now returns success so rejected placements never charge; the out-of-chalk
  gate covers private drafts too.
- **"Any powder" widened:** vanilla `powder-*` **plus all 7 `flour-*` grains**; dye containers: **bucket,
  fired bowls, jugs** (every vanilla vessel that can carry dye into a grid). 6 recipe permutations.
- **1-chalk-volume placement allowed** (costs 2, clamps to 0) — "any remaining chalk can mark" beats
  "you have chalk but can't use it"; human deferred to this recommendation.

## 4. v0.2.7 — fill-state models (the deflating bag)

The human modelled **four states with per-state textures**: Full (clean) · Medium (lightly chalked) ·
Low (medium chalked) · Empty (heavily chalked) — the bag deflates and gets grimier as chalk is used.

- **Thresholds (of 32): full ≥ 22 · medium 11–21 · low 1–10 · empty 0.**
- Assets: `shapes/tools/chalkbag-{full,medium,low,empty}.json` + 20 state textures; each shape carries its
  own texture set (the settled "tie textures into each model" call). The same Blender-export quirks as
  0.2.0 (`null`/`string`/`#0` refs) were remapped to each state's linen. The old single-state model and its
  unsuffixed textures were retired (`sulfur.png`/`white.png` stay).
- Wiring: the itemtype's default shape is the FULL model; `ItemGuideTool.OnBeforeRender` swaps
  `ItemRenderInfo.ModelRef` per stack for the other three (lazily tesselated via `ShapeTextureSource`,
  cached, disposed in `OnUnloaded`) — covers hotbar/GUI/hands/dropped. **Ground storage** shows the state
  too via **`IContainedMeshSource`** (`GenMesh` + fill-keyed `GetMeshCacheKey`) — this added the
  **VSSurvivalMod.dll** project reference. Gotcha for posterity: inside the item class the name `Shape`
  is the inherited `CompositeShape` property — the shape type must be fully qualified.

## 5. v0.2.8 — ground refill + feedback effects

- **Ground refill:** SHIFT+right-click a **ground-stored kit** with powder refills it in place (tap/hold,
  same rhythm); the storage is redrawn per pour so **the bag re-inflates live**. Aiming at the block under
  the pile also works (vanilla-style up-check). Precedence: stored kit → refill; sneak elsewhere → vanilla
  powder-pile placement (**which had been silently broken since 0.2.5** — our override ate every
  sneak-click; now it falls through); plain right-click → hotbar refill.
- **`ChalkEffects`** (new, side-agnostic): a chalk-yellow dust puff + the placement snap. **Every refill
  pour puffs** (at the stored kit, or at the chest for hand refills). **Every completed placement** plays
  the **`sounds/bow-release` twang** at the guide's midpoint (a real chalk line IS a snapped taut string)
  + a puff at each anchor. Public placements broadcast from the server; private placements play
  client-side only — matching the guide's visibility. Deliberately independent of durability (feedback,
  not chalk): fires for creative and durability-disabled servers too.

## 6. v0.2.9 — transform polish (human-tuned values)

`groundTransform` normalised (origin 0, scale 3), `groundStorageTransform` centred (translation x 0.02,
origin 0.465/0.5/0.465, y-rotation 90), `guiTransform` translation y −3. Committed with this doc pass.

## 7. Held / open

- **Cursor-stack refill** (right-click powder onto the kit icon in inventory) — **held at the human's
  direction**: they may instead REQUIRE refills to happen on the ground (decision after playtesting the
  ground refill). The code split (hotbar branch vs ground branch in `ItemChalkingPowder.ResolveTargetKit`)
  makes either outcome a small change.
- Effects tuning (puff density/size, snap volume 0.55) is first-guess — dial by feel.
- The deferred legacy agenda is unchanged: the **large-guide mesh pass** (SESSION_14 §6 staged plan) is
  still the top technical priority; **B-S9-1** still needs its broader lock/drag/revert/unlock soak test;
  the final F4/public multiplayer regression pass is still owed before any release-grade stamp.

## 8. Validation

Human playtest confirmed through 0.2.8: reskin + transforms, ground storage set-down/pickup, chalk
spend/gate/no-lockout, refills (hand + ground, tap + hold), recipes, fill-state models in all display
contexts, particles and snap sound ("Fantastic"). Builds 0.2.5–0.2.9: 0 warnings / 0 errors. Zips for every
revision in `..\Layout Zips\`.
