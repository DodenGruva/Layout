using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Layout.Client
{
    /// <summary>
    /// The vanilla-item gate used by client-only Layout mode.
    /// </summary>
    public static class ClientToolGate
    {
        public static bool HasRequiredItems(IClientPlayer player)
        {
            if (player?.InventoryManager == null) return false;

            ItemStack mainHand = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
            ItemStack offHand = player.InventoryManager.OffhandHotbarSlot?.Itemstack;

            return IsVanillaFlaxTwine(mainHand) && IsVanillaHammer(offHand);
        }

        private static bool IsVanillaFlaxTwine(ItemStack stack)
        {
            AssetLocation code = stack?.Collectible?.Code;
            return code != null && code.Domain == "game" && code.Path == "flaxtwine";
        }

        private static bool IsVanillaHammer(ItemStack stack)
        {
            CollectibleObject collectible = stack?.Collectible;
            return collectible?.Code != null
                && collectible.Code.Domain == "game"
                && (collectible.Tool == EnumTool.Hammer
                    || collectible.Code.Path == "hammer"
                    || collectible.Code.Path.StartsWith("hammer-", StringComparison.Ordinal));
        }
    }
}
