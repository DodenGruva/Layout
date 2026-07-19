# Chalking Kit — Durability & Refill Design

> **Status: ✅ IMPLEMENTED AND PLAYTESTED — v0.2.0–v0.2.9 (F5 delivered); refill channels extended v0.2.21.**
> The reskin shipped in v0.2.0, ground storage in 0.2.1–0.2.4, the durability/refill system in 0.2.5–0.2.6,
> the fill-state models in 0.2.7, and ground refill + feedback effects in 0.2.8. A fifth **High** fill state
> (v0.2.20) and the two opt-in refill channels (v0.2.21) followed. **Full implementation record:
> `SESSION_15.md` + `SESSION_16.md`.** The body below is the original design discussion, kept as the rationale
> record.
>
> **Where the implementation deliberately DIFFERS from this plan** (all human-directed during the build):
> - The refill item is named **Chalking Powder** (not "Yellow Chalking Powder").
> - **Recipe:** `8× any powder OR flour + 0.1 L yellow dye (bucket/fired bowl/fired jug) → 8 Chalking Powder` — batches
>   of 8, not the stack+1L 1:1 conversion sketched below. Same spirit (dye amortized, ~no loss), finer grain.
> - **Private placements on a Layout server DO spend chalk** — "private is private, not free" — via a
>   client-reported, server-validated `ChalkChargePacket` (protocol 4). This supersedes the local-no-op
>   recommendation below; the no-op survives only where it is physically forced (a server without Layout has
>   no channel and no real kit item — the accepted caveat).
> - The kit's own recipe costs **8 Chalking Powder** (+ linen sack, flax twine, rope, copper nails) — its
>   craft cost equals the 32 chalk it ships full with.
> - Refill = tap +4 / hold-to-pour, targeting the hotbar **or a ground-stored kit in place** (SHIFT+
>   right-click; the bag re-inflates live). **Fill-state models** now number **five** (full=32 · high 22–31 ·
>   medium 11–21 · low 1–10 · empty 0, v0.2.20 — FULL reserved for a completely full kit) with progressively
>   chalkier textures rendering in every context, ground storage included.
> - **Formerly held open, now RESOLVED (v0.2.21):** cursor-stack refill in the inventory UI shipped, but
>   behind a server-config gate — **ground-storage refill is the only always-on channel**; the hotbar
>   shortcut (`allowHotbarChalkRefill`) and the inventory-slot click (`allowInventoryChalkRefill`) are both
>   opt-in, default false. Inventory refill uses a client MouseDown hook + a server-validated
>   `ChalkInventoryRefillPacket` (protocol 5), NOT the VS slot-merge path (which would misroute powder into
>   kits). Still owed: in-play verification of the hook ordering + InventoryID round-trip (both fail safe).

## Concept
The guide tool gets a new **3D model** and a **chalking-kit** theme: guides read as **yellow chalk lines**
snapped into the world to build against. Thematically the kit holds marking chalk — using it spends chalk
(durability), and you refill it with a crafted **Yellow Chalking Powder**. This is a **reskin + new mechanic
on the existing tool**, not a separate item.

Today the tool has **infinite durability**. This adds a finite, refillable durability system. Its purpose is
**psychological reward through material investment**, tuned to be **felt but never a deterrent to use**.

## Usage model (why the numbers are deliberately low)
The chalking kit is a **niche / occasional** tool, not a constantly-held one:
- It's for geometry that's hard to eyeball by hand — circles, arches, curves, domes. Easy builds (a box
  house) never touch it.
- Most players run a **chisel-copy mod**, so the real workflow is *chalk once → chisel the master piece →
  copy that block everywhere.* Kit usage therefore scales with the number of **unique tricky design
  elements**, not with build size — a 200-block cathedral of copied arches might use the kit only 3–5 times.
- Expected usage: **~1–5 placements per typical build**, roughly flat across build scales.

That is why durability is set **low**. At a high value the mechanic would be **vestigial** (craft once, never
think about materials again), which defeats the entire reason for adding it. Players are also assumed to run
the kit down to **0 before bothering to refill** — that is treated as the *norm*, not an edge case.

## Durability spec (agreed starting point)

| Parameter | Value | Note |
|---|---|---|
| Max durability | **32** | Low on purpose — see usage model. 16 was considered (more prominent loop) but risks a "flimsy tool" feel; 64+ makes the loop rarely fire and was rejected. |
| 2D guide placement | **−1** | flat |
| 3D volume placement | **−2** | flat — deliberately **not** scaled by guide size, to protect the "enormous fine-detail guides" direction |
| Charged when | **completed placement only** | reshaping / grabbing / locking / dispelling / cancelled drafts are **free** |
| At 0 durability | **NO LOCKOUT** | can still view / edit / dispel existing guides; only **new** placement is blocked until a top-up |
| Creative / config | **exempt** | creative mode consumes nothing; add a `layout.json` toggle to disable consumption server-wide (fits the existing caps-config pattern) |

Ship the tool **full**; keep the craft cost **modest** — it is maintained, not consumed.

## Refill item — Yellow Chalking Powder
**Recipe:** `1 stack of (any) powder + 1 L yellow dye → 1 stack Yellow Chalking Powder` (**1:1**, no loss).

- **The dye is the tint; the powder is the substrate.** That is exactly why *any* powder works — the yellow
  dye is what turns a neutral powder into chalk for *these* (yellow) guides.
- **1:1 is deliberate.** No material vanishes in mixing, so a stack in = a stack out. There is **no
  batch-restock loop by design** — one batch lasts a very long time. The intended loop is the **refill**, not
  re-crafting powder.
- Yellow dye is easy to make (and easy to make in bulk). **1 L per whole batch** amortizes its
  "inconvenience" to near-nothing per refill — which is precisely why the dye lives in the **refill** recipe,
  not the tool recipe.
- **Mechanic suggestion:** mixing a liquid (dye) with a solid (powder) is idiomatic as a **barrel recipe** in
  Vintage Story.

## Refill interaction
- **Value:** each Yellow Chalking Powder = **4 durability**. A full refill (0 → 32) = **8 powder**.
- **Gesture (recommended): tap = +1 powder (4 durability); hold = pour until full / until out / on release.**
  Identical economy, but a full refill becomes **one held gesture** instead of eight taps. Hold-to-consume is
  the native VS idiom (eating / drinking). This matters *because* players routinely run to 0, so in practice
  **every refill is a full one** — eight discrete taps every single time would grate.
- **Feedback:** a chalk-dust puff + a soft tick + the durability bar climbing per unit, so the refill reads
  as a *felt* ritual rather than an instant nothing-click.
- **Consequence (accepted):** "run to 0 is the norm" makes **carry your chalk** part of the tool's identity —
  you will routinely hit 0 and need Yellow Chalking Powder on hand. That is fine **as long as** 0 is a
  one-second speed-bump (out of chalk → quick top-up → back to building), never a wall. No-lockout + a fast
  partial refill guarantees that.

## Where the reward lives
With 1:1 and no restock treadmill, the material-investment payoff is: (1) the one-time satisfaction of mixing
a proper stack of yellow chalk, and (2) watching that stack **visibly tick down** across your whole building
career as you refill. A resource made once and slowly spent *is* investment — it just pays out on a long tail.

## Design rationale (the balance target)
Two failure modes to steer between:
- **Too trivial** → no investment felt, hollow reward (where a 128-durability tool lands given the usage
  model — the mechanic becomes decorative).
- **Too harsh** → constant grinding / mid-build friction that deters use and ruins the creative flow the
  tool exists to serve.

The chosen numbers sit deliberately on the light side of "felt": occasional refills, cheap bulk materials, no
lockout. **Tune by playtest feel, not theory** — per the project's standing rule.

## Settled vs. open

**Settled (decided in discussion):**
- 32 durability; 2D −1 / 3D −2, flat; charge on **completed placement only**; **no lockout** at 0.
- The dye lives in the **refill** recipe, not the tool recipe.
- Yellow Chalking Powder = `(any powder stack) + 1 L yellow dye → 1 stack` (**1:1**); the **refill is the
  loop** (no restock loop by design).
- ~**4 durability per powder** (8 to fill), applied by right-clicking powder onto the kit.

**Open / to decide (playtest or spec):**
- **Client-only mode (F4) — does durability apply there at all? (human decision needed).** F4 shipped in
  v0.1.53: on a server without Layout the tool is gated by a vanilla **Hammer (off-hand) + Flax Twine
  (main hand)** instead of the real Layout item. That gate can neither carry custom durability nor hold a
  custom **Yellow Chalking Powder** item — a vanilla server knows neither. **Recommendation: durability is a
  local no-op in client-only mode**, which is consistent with how locks, caps, and undo-gating already
  degrade to no-ops there; the chalking kit then stays a Layout-server mechanic (public *and* private
  placement, both of which still require the real tool). Alternatives — borrowing the Hammer's own
  durability, or a virtual client-side chalk counter with no item — add real complexity to a mode that is
  deliberately "a solo sketch layer that runs anywhere."
- Confirm the **tap-vs-hold** gesture (recommended: hybrid) and the per-unit feedback in play.
- The tool's **own craft recipe** with the new 3D model (currently 6 sticks + 3 any-metal nuggets, abacus
  art — TBD whether it changes; the dye is **not** in it).
- Define the accepted **"any powder" whitelist** (which VS powder items qualify) and confirm the powder
  **stack size** (1:1 output = input stack size).
- **Migration:** how existing infinite-durability kits already in live saves behave when this ships (start
  full? treated as still-infinite until re-crafted?).
- **HUD:** surface chalk/durability remaining + a low-chalk cue (fits the existing `GuideHud`).
- **Server config toggle** to disable durability consumption (and possibly expose the numbers), following the
  existing `layout.json` injection pattern.
- Revisit **32 ↔ 16** if the refill loop feels too rare / too frequent once played.

## Implementation touchpoints (rough, for when it is built)
- **Item:** add durability to the guide-tool item (`Items/ItemGuideTool.cs` / the itemtype JSON); currently
  infinite.
- **Charge hook:** decrement on a **successful, completed** placement in the creation path
  (`Client/GuideToolController.cs` completion → authority confirm). Never charge on cancel / edit / dispel.
  Since F4 there are **two authorities**: the networked `Network/ServerNetworkHandler.cs` path and the local
  `Client/LocalGuideAuthority.cs` path. Under the recommended client-only no-op, only the networked /
  real-tool path takes the hook — but that is the open decision above, so keep the hook behind one seam.
- **Placement gate:** block *new* placement at 0 durability while leaving edit / dispel / undo paths fully
  open (the no-lockout rule).
- **Refill:** a right-click-with-powder interaction on the item (tap +4 / hold-to-fill), consuming Yellow
  Chalking Powder, with particle + sound feedback.
- **Recipe + assets:** Yellow Chalking Powder via a barrel (liquid dye + powder) or grid recipe; a new item +
  lang entry + model/texture; the kit's new 3D model/texture.
- **Config:** optional `layout.json` durability on/off (and numbers), via the existing server-config
  construction-time injection.
- **HUD:** durability readout in `UI/GuideHud.cs`.

---

*This was the pre-implementation design record. The system is now built — see the status banner at the top
for the plan-vs-shipped deltas and `SESSION_15.md` for the implementation record.*
