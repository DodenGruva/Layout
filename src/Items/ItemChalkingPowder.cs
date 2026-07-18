using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Layout.Systems;

namespace Layout.Items
{
    /// <summary>
    /// Chalking Powder — the Chalking Kit's refill (F5). Right-click while holding it: TAP applies one
    /// powder (+4 chalk), HOLD keeps pouring at a steady rate until the kit is full, the powder runs out,
    /// or the button is released. Two targets, chosen by the gesture:
    ///   • plain right-click — the first non-full kit anywhere in the hotbar (kit never needs to be held);
    ///   • SHIFT+right-click a GROUND-STORED kit — refills that kit in place, re-inflating the bag live.
    /// SHIFT+right-click anywhere else falls through to vanilla, so the powder can still be ground-stored
    /// into piles like any vanilla powder.
    /// </summary>
    /// <remarks>
    /// SIDES. Both sides run the held-interact callbacks with their own timers; only the SERVER mutates the
    /// stacks (kit durability + powder count) and emits the puff/sound — the client merely predicts whether
    /// to keep the hold alive from its synced mirror, and owns the "nothing to refill" messages. The repeat
    /// counter lives in a per-entity attribute rather than on this class: VS items are singletons shared by
    /// every player, so no per-player state may live here (same contract as <see cref="ItemGuideTool"/>).
    /// </remarks>
    public class ItemChalkingPowder : Item
    {
        /// <summary>Chalk restored per powder consumed (8 powder = one full 32-chalk kit).</summary>
        public const int ChalkPerPowder = 4;

        private const float FirstRepeatDelaySeconds = 0.45f;   // tap-friendly gap before pouring starts
        private const float RepeatIntervalSeconds = 0.3f;      // pour rate while held
        private const string AppliedAttr = "layoutChalkRefillsApplied";

        public override void OnHeldInteractStart(
            ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel,
            bool firstEvent, ref EnumHandHandling handling)
        {
            if (!firstEvent) return;

            // SHIFT+right-click somewhere that is NOT a ground-stored kit: hand the click to vanilla, so
            // the powder's own GroundStorable behavior can place/stack powder piles like any vanilla
            // powder. (Aiming at a stored kit — even a full one — always takes OUR path instead, so the
            // refill gesture can never accidentally start a powder pile on top of the kit.)
            if (byEntity.Controls.ShiftKey && !GroundKitExists(byEntity, blockSel))
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }

            handling = EnumHandHandling.PreventDefault;        // claim the interaction; no vanilla use
            byEntity.Attributes.SetInt(AppliedAttr, 0);
            ApplyRefill(slot, byEntity, blockSel, 1, notifyFailure: true);
        }

        public override bool OnHeldInteractStep(
            float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel,
            EntitySelection entitySel)
        {
            // Repeats due by now (the Start already applied one): 0 until the first-repeat delay passes,
            // then one more per interval. Each side tracks its own counter; mutation stays server-side.
            int due = secondsUsed <= FirstRepeatDelaySeconds
                ? 0
                : (int)((secondsUsed - FirstRepeatDelaySeconds) / RepeatIntervalSeconds) + 1;
            int applied = byEntity.Attributes.GetInt(AppliedAttr, 0);
            if (due > applied)
            {
                byEntity.Attributes.SetInt(AppliedAttr, due);
                if (!ApplyRefill(slot, byEntity, blockSel, due - applied, notifyFailure: false))
                    return false;                              // full or out of powder — end the pour
            }
            return true;                                       // keep holding
        }

        /// <summary>
        /// Applies up to <paramref name="times"/> single-powder refills to the gesture's target kit.
        /// Returns true while refilling can continue. Server side consumes and emits the chalk puff;
        /// client side only predicts from its synced mirror.
        /// </summary>
        private bool ApplyRefill(ItemSlot powderSlot, EntityAgent byEntity, BlockSelection blockSel,
            int times, bool notifyFailure)
        {
            IPlayer player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return false;

            ItemSlot kit = ResolveTargetKit(player, byEntity, blockSel,
                out bool kitExists, out BlockEntityGroundStorage groundStorage);
            if (kit == null)
            {
                if (notifyFailure && api is ICoreClientAPI capi)
                    capi.TriggerIngameError(this, "layout-nokit", kitExists
                        ? "The Chalking Kit is already full."
                        : "No Chalking Kit in your hotbar to refill.");
                return false;
            }

            bool applied = false;
            for (int i = 0; i < times; i++)
            {
                if (powderSlot?.Itemstack == null || powderSlot.Itemstack.StackSize <= 0) break;

                if (api.Side == EnumAppSide.Server)
                {
                    if (!ItemGuideTool.TryAddChalk(kit, ChalkPerPowder)) break;
                    powderSlot.TakeOut(1);
                    powderSlot.MarkDirty();
                    applied = true;
                }
                else
                {
                    // Client prediction only: is there still room to pour into?
                    int max = kit.Itemstack.Collectible.GetMaxDurability(kit.Itemstack);
                    if (ItemGuideTool.GetChalk(kit.Itemstack) >= max) break;
                    applied = true;
                    break;                                     // one predicted step per call is enough
                }
            }

            if (applied && api.Side == EnumAppSide.Server)
            {
                Vec3d puffPos;
                if (groundStorage != null)
                {
                    // Redraw the storage so the bag visibly re-inflates with each pour.
                    groundStorage.MarkDirty(true);
                    puffPos = new Vec3d(
                        groundStorage.Pos.X + 0.5, groundStorage.Pos.Y + 0.25, groundStorage.Pos.Z + 0.5);
                }
                else
                {
                    EntityPos p = byEntity.Pos;
                    puffPos = new Vec3d(p.X, p.Y + 1.0, p.Z);
                }
                ChalkEffects.SpawnChalkPuff(byEntity.World, puffPos);
                byEntity.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"),
                    byEntity, null, true, 16f, 0.6f);
            }
            return applied;
        }

        /// <summary>
        /// The gesture's target: SHIFT aims at a ground-stored kit (the storage comes back so it can be
        /// redrawn); otherwise the first non-full kit in the hotbar. Null when there is nothing to refill;
        /// <paramref name="kitExists"/> distinguishes "no kit" from "kit is full" for messaging.
        /// </summary>
        private ItemSlot ResolveTargetKit(IPlayer player, EntityAgent byEntity, BlockSelection blockSel,
            out bool kitExists, out BlockEntityGroundStorage groundStorage)
        {
            groundStorage = null;

            if (byEntity.Controls.ShiftKey)
            {
                kitExists = false;
                BlockEntityGroundStorage begs = GroundStorageAt(byEntity.World, blockSel);
                if (begs == null) return null;

                foreach (ItemSlot s in begs.Inventory)
                {
                    ItemStack stack = s?.Itemstack;
                    if (!(stack?.Collectible is ItemGuideTool)) continue;
                    kitExists = true;
                    if (ItemGuideTool.GetChalk(stack) < stack.Collectible.GetMaxDurability(stack))
                    {
                        groundStorage = begs;
                        return s;
                    }
                }
                return null;
            }

            return FindKitSlot(player, out kitExists);
        }

        /// <summary>True when the aimed block (or the one above it — piles report the block under them)
        /// is a ground storage holding a Chalking Kit, full or not.</summary>
        private static bool GroundKitExists(EntityAgent byEntity, BlockSelection blockSel)
        {
            BlockEntityGroundStorage begs = GroundStorageAt(byEntity?.World, blockSel);
            if (begs == null) return false;
            foreach (ItemSlot s in begs.Inventory)
                if (s?.Itemstack?.Collectible is ItemGuideTool) return true;
            return false;
        }

        private static BlockEntityGroundStorage GroundStorageAt(IWorldAccessor world, BlockSelection blockSel)
        {
            if (world == null || blockSel == null) return null;
            return world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityGroundStorage
                ?? world.BlockAccessor.GetBlockEntity(blockSel.Position.UpCopy()) as BlockEntityGroundStorage;
        }

        /// <summary>
        /// The first non-full Chalking Kit in the player's hotbar (offhand included — it is part of the
        /// hotbar inventory). Null when there is no kit or every kit is full; <paramref name="kitExists"/>
        /// distinguishes the two for messaging.
        /// </summary>
        private static ItemSlot FindKitSlot(IPlayer player, out bool kitExists)
        {
            kitExists = false;
            IInventory hotbar = player.InventoryManager?.GetHotbarInventory();
            if (hotbar == null) return null;

            foreach (ItemSlot slot in hotbar)
            {
                ItemStack stack = slot?.Itemstack;
                if (!(stack?.Collectible is ItemGuideTool)) continue;
                kitExists = true;
                if (ItemGuideTool.GetChalk(stack) < stack.Collectible.GetMaxDurability(stack)) return slot;
            }
            return null;
        }
    }
}
