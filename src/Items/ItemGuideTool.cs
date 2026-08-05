using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
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
    public class ItemGuideTool : Item, IContainedMeshSource
    {
        // --- Chalk-fill visuals: five models (empty / low / medium / high / full), each carrying its own
        // progressively-chalkier texture set, selected by remaining chalk. The itemtype's default shape IS
        // the full model; the other four are tesselated lazily via ShapeTextureSource (which inserts
        // their textures into the BLOCK atlas — the same atlas BlockEntityDisplay hands to GenMesh, and a
        // valid source for held/GUI rendering since MultiTextureMeshRef carries per-mesh texture ids).
        // Thresholds (human-confirmed, of 32): FULL is reserved for a COMPLETELY full kit (0.2.20 — the
        // pristine bag has to be earned; a refill that stops short shows High), high 22–31, medium 11–21,
        // low 1–10, empty 0.
        private const int FillHighMin = 22;
        private const int FillMediumMin = 11;
        private static readonly string[] FillShapeNames = { "empty", "low", "medium", "high", "full" };
        private const int FillIndexFull = 4;

        private MultiTextureMeshRef[] _fillMeshRefs;   // client-only; index = fill state

        /// <summary>The fill-state index (0 empty · 1 low · 2 medium · 3 high · 4 full) for a kit stack.</summary>
        public static int FillIndexFor(ItemStack stack)
        {
            int chalk = GetChalk(stack);
            if (stack != null && chalk >= MaxChalk) return FillIndexFull;
            if (chalk >= FillHighMin) return 3;
            if (chalk >= FillMediumMin) return 2;
            return chalk >= 1 ? 1 : 0;
        }

        /// <summary>Held / GUI / dropped rendering: swap in the fill-state mesh (full = default shape).</summary>
        public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack,
            EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
        {
            base.OnBeforeRender(capi, itemstack, target, ref renderinfo);

            int fill = FillIndexFor(itemstack);
            if (fill == FillIndexFull) return;            // the itemtype's own shape is the full model

            MultiTextureMeshRef mesh = GetFillMeshRef(capi, fill);
            if (mesh != null) renderinfo.ModelRef = mesh;
        }

        private MultiTextureMeshRef GetFillMeshRef(ICoreClientAPI capi, int fill)
        {
            if (_fillMeshRefs == null) _fillMeshRefs = new MultiTextureMeshRef[FillShapeNames.Length];
            if (_fillMeshRefs[fill] == null)
            {
                MeshData mesh = TesselateFillShape(capi, fill);
                if (mesh == null) return null;            // missing asset: keep the default model
                _fillMeshRefs[fill] = capi.Render.UploadMultiTextureMesh(mesh);
            }
            return _fillMeshRefs[fill];
        }

        private MeshData TesselateFillShape(ICoreClientAPI capi, int fill)
        {
            var loc = new AssetLocation("layout", "shapes/tools/chalkbag-" + FillShapeNames[fill] + ".json");
            // Fully qualified: inside this class the name "Shape" is the inherited CompositeShape property.
            Vintagestory.API.Common.Shape shape = Vintagestory.API.Common.Shape.TryGet(capi, loc);
            if (shape == null)
            {
                capi.Logger.Warning("[Layout] Missing chalk-fill shape {0}; keeping the default model.", loc);
                return null;
            }
            capi.Tesselator.TesselateShape("layout chalking kit", shape,
                out MeshData mesh, new ShapeTextureSource(capi, shape, loc.ToString()));
            return mesh;
        }

        /// <summary>Ground storage (BlockEntityDisplay family): the stored kit shows its fill state too.</summary>
        public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
            => api is ICoreClientAPI capi ? TesselateFillShape(capi, FillIndexFor(slot?.Itemstack)) : null;

        /// <summary>Display-cache key: changes with the fill state, so a re-set-down kit re-meshes.</summary>
        public string GetMeshCacheKey(ItemSlot slot) => "layout:guidetool-fill-" + FillIndexFor(slot?.Itemstack);

        public override void OnUnloaded(ICoreAPI api)
        {
            base.OnUnloaded(api);
            if (_fillMeshRefs != null)
            {
                foreach (MultiTextureMeshRef r in _fillMeshRefs) r?.Dispose();
                _fillMeshRefs = null;
            }
        }

        // --- Chalk (F5 durability): the kit's charge is ordinary item durability, but it is NEVER damaged
        // through the vanilla DamageItem path — vanilla destroys a tool at 0, and the settled design is
        // NO LOCKOUT: at 0 the kit survives and only NEW placements are blocked. All mutation goes through
        // the two helpers below, which write the stack's durability attribute directly and clamp at [0, max].
        /// <summary>Chalk consumed by a completed 2D guide placement.</summary>
        public const int ChalkCostFlat = 1;
        /// <summary>Chalk consumed by a completed 3D volume placement.</summary>
        public const int ChalkCostVolume = 2;

        /// <summary>
        /// The kit's ABSOLUTE chalk ceiling — the single source of truth, and deliberately a hard constant
        /// (v0.2.22). Must stay in step with <c>durability</c> in <c>itemtypes/guidetool.json</c>, which
        /// only seeds the vanilla value.
        /// </summary>
        /// <remarks>
        /// **Why we do not ask the engine for this.** `CollectibleObject.GetMaxDurability` walks the
        /// collectible's BEHAVIORS and lets any of them replace the value, and `GetRemainingDurability` does
        /// the same *and* defaults to that (possibly inflated) max for a stack with no stored value. That is
        /// exactly the hook other mods use — **xskills** attaches a crafting-quality behavior that raises
        /// durability on a well-crafted item — so a kit could report a max of, say, 45 and then refill to 45.
        /// The chalk economy is balanced around 32 (8 powder = one full kit), so the ceiling is ours to
        /// define, not a craft roll's. Every read below goes through <see cref="GetChalk"/>, which reads the
        /// stored attribute DIRECTLY and clamps here; the two overrides further down make the engine agree.
        /// </remarks>
        public const int MaxChalk = 32;

        /// <summary>
        /// Remaining chalk, clamped to [0, <see cref="MaxChalk"/>]. Defaults to full for a stack that has no
        /// stored value (a fresh craft, or one predating durability). Reads the raw attribute rather than
        /// <c>GetRemainingDurability</c> so no third-party behavior can inflate it.
        /// </summary>
        public static int GetChalk(ItemStack stack)
        {
            if (stack == null) return 0;
            int stored = (int)stack.Attributes.GetDecimal("durability", MaxChalk);
            return stored < 0 ? 0 : (stored > MaxChalk ? MaxChalk : stored);
        }

        /// <summary>Spends chalk, clamping at 0 — the kit is never destroyed and never goes negative.</summary>
        public static void ConsumeChalk(ItemSlot slot, int cost)
        {
            ItemStack stack = slot?.Itemstack;
            if (stack == null) return;
            stack.Attributes.SetInt("durability", Math.Max(0, GetChalk(stack) - cost));
            slot.MarkDirty();
        }

        /// <summary>
        /// Adds chalk (a powder refill), capped at <see cref="MaxChalk"/>. Returns false when the kit is
        /// already full — the caller then leaves the powder unconsumed.
        /// </summary>
        public static bool TryAddChalk(ItemSlot kitSlot, int amount)
        {
            ItemStack stack = kitSlot?.Itemstack;
            if (stack == null) return false;
            int cur = GetChalk(stack);
            if (cur >= MaxChalk) return false;
            stack.Attributes.SetInt("durability", Math.Min(MaxChalk, cur + amount));
            kitSlot.MarkDirty();
            return true;
        }

        /// <summary>True when the kit cannot take any more chalk. The one "is it full?" test.</summary>
        public static bool IsChalkFull(ItemStack stack) => GetChalk(stack) >= MaxChalk;

        // The two overrides below make the ENGINE agree with MaxChalk — the durability bar, tooltips, and any
        // other mod reading through the normal API all see 32/32. Both deliberately skip base.*: the base
        // implementations are what walk the behaviors we are overriding in the first place.

        /// <inheritdoc/>
        public override int GetMaxDurability(ItemStack itemstack) => MaxChalk;

        /// <inheritdoc/>
        public override int GetRemainingDurability(ItemStack itemstack) => GetChalk(itemstack);

        private GuideToolController Controller =>
            api?.ModLoader?.GetModSystem<LayoutModSystem>()?.Controller;

        /// <summary>
        /// Native held-item prompts for every shape modifier. The controller refreshes these rows when the
        /// placement stage changes. Inherited help (notably GroundStorable's set-down note) is included only
        /// for the engine's normal item-selection composition, never for those stage-triggered refreshes.
        /// </summary>
        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            var interactions = new List<WorldInteraction>
            {
                ModifierInteraction(ShapeModifierHelp.CtrlCardinal, "ctrl",
                    "heldhelp-layout-cardinal"),
                ModifierInteraction(ShapeModifierHelp.ShiftVertical, "shift",
                    "heldhelp-layout-vertical"),
                ModifierInteraction(ShapeModifierHelp.ShiftCenterApex, "shift",
                    "heldhelp-layout-centerapex"),
                ModifierInteraction(ShapeModifierHelp.ShiftInvert, "shift",
                    "heldhelp-layout-invert"),
                ModifierInteraction(ShapeModifierHelp.CtrlCloseRim, "ctrl",
                    "heldhelp-layout-closerim"),
                ModifierInteraction(ShapeModifierHelp.ShiftRestore, "shift",
                    "heldhelp-layout-restore"),
                ModifierInteraction(ShapeModifierHelp.ShiftFlatSide, "shift",
                    "heldhelp-layout-flatside"),
                ModifierInteraction(ShapeModifierHelp.CtrlShiftDiagonal, new[] { "ctrl", "shift" },
                    "heldhelp-layout-diagonal"),
                ModifierInteraction(ShapeModifierHelp.ShiftAllowFlare, "shift",
                    "heldhelp-layout-flare"),
                ModifierInteraction(ShapeModifierHelp.ShiftEmbedGuide, "shift",
                    "heldhelp-layout-guideembed"),
                ModifierInteraction(ShapeModifierHelp.CtrlBypassGrab, "ctrl",
                    "heldhelp-layout-bypassgrab"),
                StageInteraction(ShapeModifierHelp.RoundoverRoute,
                    "heldhelp-layout-roundoverroute")
            };

            WorldInteraction[] inherited = Controller?.SuppressStandardHeldHelp == true
                ? null : base.GetHeldInteractionHelp(inSlot);
            if (inherited != null)
            {
                foreach (WorldInteraction interaction in inherited)
                {
                    InteractionMatcherDelegate inheritedPredicate = interaction.ShouldApply;
                    var idleInteraction = new WorldInteraction
                    {
                        MouseButton = interaction.MouseButton,
                        HotKeyCode = interaction.HotKeyCode,
                        HotKeyCodes = interaction.HotKeyCodes,
                        ActionLangCode = interaction.ActionLangCode,
                        JsonItemStacks = interaction.JsonItemStacks,
                        Itemstacks = interaction.Itemstacks,
                        RequireFreeHand = interaction.RequireFreeHand,
                        GetMatchingStacks = interaction.GetMatchingStacks,
                        ShouldApply = (wi, block, entity) => (Controller?.IsIdle ?? true)
                            && (inheritedPredicate == null || inheritedPredicate(wi, block, entity))
                    };
                    interactions.Add(idleInteraction);
                }
            }
            return interactions.ToArray();
        }

        private WorldInteraction ModifierInteraction(ShapeModifierHelp flag, string hotKey, string langCode) =>
            new WorldInteraction
            {
                MouseButton = EnumMouseButton.Left,
                HotKeyCode = hotKey,
                ActionLangCode = "layout:" + langCode,
                ShouldApply = (_, _, _) => (Controller?.ModifierHelp & flag) != 0
            };

        private WorldInteraction ModifierInteraction(ShapeModifierHelp flag, string[] hotKeys, string langCode) =>
            new WorldInteraction
            {
                MouseButton = EnumMouseButton.Left,
                HotKeyCodes = hotKeys,
                ActionLangCode = "layout:" + langCode,
                ShouldApply = (_, _, _) => (Controller?.ModifierHelp & flag) != 0
            };

        private WorldInteraction StageInteraction(ShapeModifierHelp flag, string langCode) =>
            new WorldInteraction
            {
                MouseButton = EnumMouseButton.Left,
                ActionLangCode = "layout:" + langCode,
                ShouldApply = (_, _, _) => (Controller?.ModifierHelp & flag) != 0
            };

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
            // SHIFT+right-click on a block, while the tool is idle: set the kit down as ground storage.
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
        /// The ground-storage set-down gesture: SHIFT held. Read from <c>byEntity.Controls</c>
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
            if (c == null || !c.ShiftKey) return false;
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
