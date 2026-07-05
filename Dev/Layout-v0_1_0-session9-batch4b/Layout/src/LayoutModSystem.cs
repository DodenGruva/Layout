using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Layout.Client;
using Layout.Config;
using Layout.Guide;
using Layout.Items;
using Layout.Network;
using Layout.Systems;
using Layout.UI;

namespace Layout
{
    /// <summary>
    /// The mod's entry point and composition root — the "front desk". One instance runs on each side; it
    /// loads the side's config, constructs the SINGLE SHARED INSTANCES of every manager/handler/dialog, and
    /// wires them together. Nothing else in the mod constructs these: the item, the controller (Module 7b),
    /// the dialogs, and the renderer all reach the same objects through this system
    /// (<c>api.ModLoader.GetModSystem&lt;LayoutModSystem&gt;()</c>).
    /// </summary>
    /// <remarks>
    /// SERVER SIDE. Loads <c>layout.json</c> (creating it with defaults on first run so admins get a file to
    /// edit), then builds <see cref="GuideManager"/> (voxel + guide-count caps), <see cref="GuideLockManager"/>,
    /// <see cref="UndoManager"/> (config depth + lock gating), and <see cref="ServerNetworkHandler"/>
    /// (privilege + admin-override policy). Config is read once at construction; edits apply on restart.
    ///
    /// CLIENT SIDE. Loads <c>layout-client.json</c> and seeds <see cref="DraftManager"/> with the remembered
    /// scale / projection / fill; builds <see cref="ClientNetworkHandler"/>, <see cref="GuideRenderer"/>, and
    /// the two dialogs (<see cref="GuideToolGui"/>, <see cref="GuideHud"/>) around the ONE DraftManager and
    /// ONE ClientNetworkHandler. When the join-time bulk sync lands, the server's per-guide voxel cap is
    /// pushed into DraftManager so the placement pre-check uses the exact figure the server enforces. On
    /// shutdown the player's current scale / projection / fill are written back to the client config —
    /// "remember my last-used settings" — and the renderer/dialogs are disposed.
    ///
    /// MODULE 7a SCOPE. This file makes both sides construct and compile; the tool item, the interaction
    /// controller, the F hotkey, and item-class registration land in Module 7b, at which point the mod
    /// becomes visibly runnable. The dialogs are constructed here but opened/closed by the 7b controller
    /// (HUD on equip, GUI on F).
    /// </remarks>
    public class LayoutModSystem : ModSystem
    {
        /// <summary>Server config filename inside the game's ModConfig folder.</summary>
        public const string ServerConfigFile = "layout.json";

        /// <summary>Client config filename inside the game's ModConfig folder.</summary>
        public const string ClientConfigFile = "layout-client.json";

        // --- Server-side shared instances (null on the client) ------------------------------------
        private ICoreServerAPI _sapi;
        public LayoutServerConfig ServerConfig { get; private set; }
        public GuideManager Guides { get; private set; }
        public GuideLockManager Locks { get; private set; }
        public UndoManager Undo { get; private set; }
        public ServerNetworkHandler ServerNet { get; private set; }

        // --- Client-side shared instances (null on the server) ------------------------------------
        private ICoreClientAPI _capi;
        public LayoutClientConfig ClientConfig { get; private set; }
        public DraftManager Draft { get; private set; }
        public ClientNetworkHandler ClientNet { get; private set; }
        public GuideRenderer Renderer { get; private set; }
        public GuideToolGui ToolGui { get; private set; }
        public GuideHud Hud { get; private set; }
        public GuideToolController Controller { get; private set; }

        // ==========================================================================================
        //  Common (both sides)
        // ==========================================================================================

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // The custom item class behind assets/layout/itemtypes/guidetool.json ("class": "LayoutGuideTool").
            // Must exist on BOTH sides — the server instantiates the item too, it just stays inert there.
            api.RegisterItemClass("LayoutGuideTool", typeof(ItemGuideTool));
        }

        // ==========================================================================================
        //  Server
        // ==========================================================================================

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            _sapi = sapi;

            ServerConfig = LoadServerConfig(sapi);

            Guides = new GuideManager(
                sapi,
                ServerConfig.PerGuideVoxelCap,
                ServerConfig.TotalVoxelCap,
                ServerConfig.MaxGuidesPerPlayer,
                ServerConfig.MaxGuidesWorldWide);

            Locks = new GuideLockManager();
            Undo = new UndoManager(Guides, ServerConfig.UndoHistoryDepth, Locks);

            ServerNet = new ServerNetworkHandler(
                sapi, Guides, Locks, Undo,
                ServerConfig.RequiredPrivilege,
                ServerConfig.AdminCanOverrideLocks);

            sapi.Logger.Notification(
                "[Layout] Server started. Caps: {0} voxels/guide, {1} total, {2} guides/player, {3} world-wide (0 = unlimited); undo depth {4}; privilege '{5}'; admin lock-override {6}.",
                ServerConfig.PerGuideVoxelCap, ServerConfig.TotalVoxelCap,
                ServerConfig.MaxGuidesPerPlayer, ServerConfig.MaxGuidesWorldWide,
                ServerConfig.UndoHistoryDepth,
                ServerConfig.RequiredPrivilege == "" ? "(none)" : ServerConfig.RequiredPrivilege,
                ServerConfig.AdminCanOverrideLocks ? "on" : "off");
        }

        // Load-or-create: a malformed file logs a warning and falls back to defaults rather than killing the
        // server; the (normalized) config is always stored back so the on-disk file gains any new keys.
        private static LayoutServerConfig LoadServerConfig(ICoreServerAPI sapi)
        {
            LayoutServerConfig cfg = null;
            try
            {
                cfg = sapi.LoadModConfig<LayoutServerConfig>(ServerConfigFile);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[Layout] Could not read {0} ({1}); using defaults.", ServerConfigFile, e.Message);
            }

            if (cfg == null) cfg = new LayoutServerConfig();
            cfg.Normalize();

            try
            {
                sapi.StoreModConfig(cfg, ServerConfigFile);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[Layout] Could not write {0} ({1}); continuing with in-memory config.", ServerConfigFile, e.Message);
            }

            return cfg;
        }

        // ==========================================================================================
        //  Client
        // ==========================================================================================

        public override void StartClientSide(ICoreClientAPI capi)
        {
            _capi = capi;

            ClientConfig = LoadClientConfig(capi);

            // Apply the configured guide opacities before anything builds a mesh (Session-8, item 2).
            GuideMeshBuilder.ConfigureOpacities(
                ClientConfig.OpacityBody, ClientConfig.OpacityLocked, ClientConfig.OpacityApex,
                ClientConfig.OpacityAnchor, ClientConfig.OpacityGrabbed, ClientConfig.OpacityHiddenAnchor);

            // The one DraftManager (client tool state), seeded with the remembered defaults. The cap seed is
            // the baked default until the server's bulk sync replaces it below.
            Draft = new DraftManager();
            Draft.SetScale(ClientConfig.DefaultScale);
            Draft.SetProjection((ProjectionMode)ClientConfig.DefaultProjection);
            Draft.SetFilled(ClientConfig.DefaultFill);
            Draft.SetShape((GuideShapeType)ClientConfig.DefaultShape,
                           (ShapeConstraint)ClientConfig.DefaultConstraint);
            Draft.SetDivisions(ClientConfig.DefaultDivisions);

            // The one ClientNetworkHandler (mirror + send-API). On every bulk sync (join), push the server's
            // per-guide cap into the placement pre-check so client and server agree on the same figure.
            ClientNet = new ClientNetworkHandler(capi);
            ClientNet.GuidesBulkSynced += OnGuidesBulkSynced;

            // Renderer over the mirror (constructed here, disposed in Dispose — the seam Module 5 left open).
            Renderer = new GuideRenderer(capi, ClientNet);

            // The two dialogs share the same DraftManager + ClientNetworkHandler (the Module-6 contract).
            // They are constructed ready but stay closed: the controller shows the HUD on equip and opens
            // the GUI on F.
            ToolGui = new GuideToolGui(capi, Draft, ClientNet);
            Hud = new GuideHud(capi, Draft, ClientNet);

            // The aim-controller: per-tick raycast + click routing while the tool is held. It (not the
            // ModSystem, not the item) owns all interaction state, including comatose grab sessions.
            Controller = new GuideToolController(capi, Draft, ClientNet, Renderer, ToolGui, Hud);

            // Hotkeys — all rebindable in the vanilla controls screen, all gated to the held tool by the
            // controller (they fall through to other handlers otherwise, so Ctrl+Z stays safe elsewhere).
            // F mirrors the vanilla tool-mode convention; ours opens a modal (dropdowns need a cursor)
            // rather than the native radial — the settled Module-6 decision.
            capi.Input.RegisterHotKey("layouttoolgui", "Layout: tool mode & settings", GlKeys.F, HotkeyType.CharacterControls);
            capi.Input.SetHotKeyHandler("layouttoolgui", _ => Controller.OnToolGuiHotkey());

            capi.Input.RegisterHotKey("layoutundo", "Layout: undo", GlKeys.Z, HotkeyType.CharacterControls, ctrlPressed: true);
            capi.Input.SetHotKeyHandler("layoutundo", _ => Controller.OnUndoHotkey());

            capi.Input.RegisterHotKey("layoutredo", "Layout: redo", GlKeys.Y, HotkeyType.CharacterControls, ctrlPressed: true);
            capi.Input.SetHotKeyHandler("layoutredo", _ => Controller.OnRedoHotkey());

            capi.Logger.Notification("[Layout] Client started. Tool defaults: scale {0}, {1}, {2}.",
                Draft.Scale, Draft.Projection, Draft.Filled ? "filled" : "hollow");
        }

        private void OnGuidesBulkSynced()
        {
            Draft.SetPerGuideVoxelCap(ClientNet.PerGuideVoxelCap);
        }

        private static LayoutClientConfig LoadClientConfig(ICoreClientAPI capi)
        {
            LayoutClientConfig cfg = null;
            try
            {
                cfg = capi.LoadModConfig<LayoutClientConfig>(ClientConfigFile);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[Layout] Could not read {0} ({1}); using defaults.", ClientConfigFile, e.Message);
            }

            if (cfg == null) cfg = new LayoutClientConfig();
            cfg.Normalize();
            return cfg;
        }

        // Write the player's current tool settings back as the new defaults — "come up the way I left it".
        private void SaveClientConfig()
        {
            if (_capi == null || ClientConfig == null || Draft == null) return;

            ClientConfig.DefaultScale = Draft.Scale;
            ClientConfig.DefaultProjection = (int)Draft.Projection;
            ClientConfig.DefaultFill = Draft.Filled;
            ClientConfig.DefaultShape = (int)Draft.Shape;
            ClientConfig.DefaultConstraint = (int)Draft.Constraint;
            ClientConfig.DefaultDivisions = Draft.Divisions;

            try
            {
                _capi.StoreModConfig(ClientConfig, ClientConfigFile);
            }
            catch (Exception e)
            {
                _capi.Logger.Warning("[Layout] Could not save {0}: {1}", ClientConfigFile, e.Message);
            }
        }

        // ==========================================================================================
        //  Teardown
        // ==========================================================================================

        public override void Dispose()
        {
            // Client side: persist last-used settings, then release GPU/event resources. Everything is
            // null-guarded and exception-guarded — Dispose runs during shutdown and must never throw.
            if (_capi != null)
            {
                SaveClientConfig();

                if (ClientNet != null) ClientNet.GuidesBulkSynced -= OnGuidesBulkSynced;

                try { Controller?.Dispose(); } catch (Exception e) { _capi.Logger.Warning("[Layout] Controller dispose: {0}", e.Message); }
                Controller = null;

                try { Renderer?.Dispose(); } catch (Exception e) { _capi.Logger.Warning("[Layout] Renderer dispose: {0}", e.Message); }
                try { ToolGui?.Dispose(); } catch (Exception e) { _capi.Logger.Warning("[Layout] ToolGui dispose: {0}", e.Message); }
                try { Hud?.Dispose(); } catch (Exception e) { _capi.Logger.Warning("[Layout] Hud dispose: {0}", e.Message); }

                Renderer = null;
                ToolGui = null;
                Hud = null;
            }

            // Server side holds no unmanaged resources; the managers are dropped with the system.
            base.Dispose();
        }
    }
}
