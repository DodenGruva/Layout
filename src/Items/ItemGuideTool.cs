using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Layout.Client;

namespace Layout.Items
{
    /// <summary>
    /// The Layout tool item — deliberately STATELESS glue. Vintage Story <see cref="Item"/> objects are
    /// singletons shared across every player holding one, so no per-player state may live here; that state
    /// already lives in <c>DraftManager</c> (one per client) and <see cref="GuideToolController"/> (which
    /// also owns all raycast/click logic). This class only intercepts the held-item mouse events on the
    /// client, suppresses the vanilla actions (no block breaking or placing while planning!), and forwards
    /// the click to the controller, reached through the ModSystem — the shared-instance contract.
    /// </summary>
    /// <remarks>
    /// Registered as class <c>"LayoutGuideTool"</c> in <see cref="LayoutModSystem"/>'s common
    /// <c>Start</c>, and referenced by <c>assets/layout/itemtypes/guidetool.json</c>. The server-side
    /// instance does nothing at all — every server effect of a click arrives via the Module-4 packets that
    /// the controller sends, keeping the one-authority network model intact.
    /// </remarks>
    public class ItemGuideTool : Item
    {
        private GuideToolController Controller =>
            api?.ModLoader?.GetModSystem<LayoutModSystem>()?.Controller;

        /// <summary>Left-click: suppress the vanilla attack/break and route to the mode's primary action.</summary>
        public override void OnHeldAttackStart(
            ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel,
            ref EnumHandHandling handling)
        {
            handling = EnumHandHandling.PreventDefault;
            if (!IsLocalClientPlayer(byEntity)) return;

            Controller?.OnPrimaryClick(blockSel);
        }

        /// <summary>
        /// Right-click: suppress vanilla use/place and route to the mode's secondary action — the Session-8
        /// contract: cancel the in-progress grab or draft, or toggle a point's lock when idle (Create mode).
        /// </summary>
        public override void OnHeldInteractStart(
            ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel,
            bool firstEvent, ref EnumHandHandling handling)
        {
            // CTRL+SHIFT+right-click on a block, while the tool is idle: set the kit down as ground storage.
            // Delegate to the base collectible so the GroundStorable behavior (declared in guidetool.json)
            // handles placement, instead of suppressing the click and running the tool's secondary action.
            if (blockSel != null && IsGroundStoreGesture(byEntity))
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }

            handling = EnumHandHandling.PreventDefault;
            if (!IsLocalClientPlayer(byEntity)) return;
            if (!firstEvent) return;                       // one action per click, not per held-frame

            Controller?.OnSecondaryClick(blockSel);
        }

        /// <summary>
        /// The ground-storage set-down gesture: CTRL + SHIFT both held. Read from <c>byEntity.Controls</c>
        /// (the SAME source the vanilla <c>GroundStorable</c> behavior checks — its <c>Interact</c> bails
        /// unless <c>Controls.ShiftKey</c> is set), so that whenever this gate passes, the behavior's own
        /// modifier check passes too. Reading the raw keyboard instead can diverge from these separable
        /// interaction-modifier controls and leave the delegated set-down silently doing nothing. On the
        /// client the tool must also be idle, so a mid-draft click can never become a set-down; the server
        /// has no draft state and trusts the client (these controls are network-synced).
        /// </summary>
        private bool IsGroundStoreGesture(EntityAgent byEntity)
        {
            EntityControls c = byEntity?.Controls;
            if (c == null || !c.ShiftKey || !c.CtrlKey) return false;
            if (api is ICoreClientAPI) return Controller?.IsIdle ?? true;
            return true;   // server: modifier check only; trust the client's idle gate
        }

        // Interaction runs purely client-side and only for the player actually holding OUR tool locally —
        // the server-side item and other players' client-side instances stay inert.
        private bool IsLocalClientPlayer(EntityAgent byEntity)
        {
            if (!(api is ICoreClientAPI capi)) return false;
            return byEntity is EntityPlayer ep && ep.PlayerUID == capi.World.Player.PlayerUID;
        }
    }
}
