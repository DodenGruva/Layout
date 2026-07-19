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

        /// <summary>
        /// F5 hotbar refill-channel preference (v0.2.22: a CLIENT setting, was server config in 0.2.21).
        /// Ground-storage refill is always allowed and is not gated by this.
        /// </summary>
        /// <remarks>
        /// The PLAYER owns this setting, but both sides must agree on it: <c>ItemChalkingPowder</c>'s
        /// held-interact callbacks run on the client AND the server, and it is the server that mutates the
        /// stacks. So the client reads <c>layout-client.json</c> directly, while the server reads the
        /// preference that client reported on join (<c>ChalkRefillPrefsPacket</c>). Answering permissively
        /// on the server instead would refill for a player who had switched the shortcut off — hence the
        /// per-player argument rather than a plain flag.
        /// </remarks>
        public bool HotbarChalkRefillAllowedFor(string playerUid) =>
            _sapi != null ? (ServerNet?.HotbarRefillOptIn(playerUid) ?? false)
                          : (ClientConfig?.AllowHotbarChalkRefill ?? false);

        /// <summary>
        /// Companion for the inventory cursor-drop refill. Client-only by nature — that channel is
        /// initiated by an explicit client packet, so the server never needs to consult it.
        /// </summary>
        public bool InventoryChalkRefillAllowed =>
            _sapi == null && (ClientConfig?.AllowInventoryChalkRefill ?? false);

        private long _modeDetectionTickId;
        private float _modeDetectionElapsedSeconds;
        private const float ModeDetectionGraceSeconds = 3f;

        // ==========================================================================================
        //  Common (both sides)
        // ==========================================================================================

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // The custom item classes behind assets/layout/itemtypes/*.json. Must exist on BOTH sides —
            // the server instantiates the items too (and the powder's refill consumption runs server-side).
            api.RegisterItemClass("LayoutGuideTool", typeof(ItemGuideTool));
            api.RegisterItemClass("LayoutChalkingPowder", typeof(ItemChalkingPowder));
        }

        // ==========================================================================================
        //  Server
        // ==========================================================================================

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            _sapi = sapi;

            ServerConfig = LoadServerConfig(sapi);

            Guides = new GuideManager(
                new ServerGuidePersistence(sapi),
                new BlockAccessorGuideProbe(() => sapi.World?.BlockAccessor),
                sapi.Logger,
                ServerConfig.PerGuideVoxelCap,
                ServerConfig.TotalVoxelCap,
                ServerConfig.MaxGuidesPerPlayer,
                ServerConfig.MaxGuidesWorldWide);

            // Server save/load event ownership stays in the server composition root. GuideManager itself is
            // now side-neutral and can also back an intentionally transient client-only authority.
            sapi.Event.SaveGameLoaded += Guides.Load;
            sapi.Event.GameWorldSave += Guides.Persist;

            Locks = new GuideLockManager();
            Undo = new UndoManager(Guides, ServerConfig.UndoHistoryDepth, Locks);

            ServerNet = new ServerNetworkHandler(
                sapi, Guides, Locks, Undo,
                ServerConfig.RequiredPrivilege,
                ServerConfig.AdminCanOverrideLocks,
                ServerConfig.AllowClientOnlyMode,
                ServerConfig.EnableChalkDurability);

            sapi.Logger.Notification(
                "[Layout] Server started. Caps: {0} voxels/guide, {1} total, {2} guides/player, {3} world-wide (0 = unlimited); undo depth {4}; privilege '{5}'; admin lock-override {6}; client-only mode {7}.",
                ServerConfig.PerGuideVoxelCap, ServerConfig.TotalVoxelCap,
                ServerConfig.MaxGuidesPerPlayer, ServerConfig.MaxGuidesWorldWide,
                ServerConfig.UndoHistoryDepth,
                ServerConfig.RequiredPrivilege == "" ? "(none)" : ServerConfig.RequiredPrivilege,
                ServerConfig.AdminCanOverrideLocks ? "on" : "off",
                ServerConfig.AllowClientOnlyMode ? "allowed" : "disallowed");
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

            // Register the tool GUI's custom shape/toggle/plane glyphs before any dialog composes (UI pass).
            LayoutToolIcons.EnsureRegistered(capi);

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
            Draft.SetSides(ClientConfig.DefaultSides);

            // The one ClientNetworkHandler (mirror + send-API). On every bulk sync (join), push the server's
            // per-guide cap into the placement pre-check so client and server agree on the same figure.
            ClientNet = new ClientNetworkHandler(capi);
            ClientNet.ResetAuthorityMode(ClientConfig.ForceClientOnly);
            ClientNet.GuidesBulkSynced += OnGuidesBulkSynced;
            ClientNet.AuthorityModeChanged += OnAuthorityModeChanged;
            ClientNet.ForceClientOnlyPreferenceChanged += OnForceClientOnlyPreferenceChanged;

            // The channel is registered during client startup, but its final state is not knowable until
            // the server sends its channel list. Resolve once the world finalizes; if that event catches the
            // handshake in its last instant, keep checking briefly rather than guessing the wrong mode.
            capi.Event.LevelFinalize += OnLevelFinalize;
            capi.Event.LeftWorld += OnLeftWorld;

            RegisterClientCommands(capi);

            // Renderer over the mirror (constructed here, disposed in Dispose — the seam Module 5 left open).
            Renderer = new GuideRenderer(capi, ClientNet);

            // The two dialogs share the same DraftManager + ClientNetworkHandler (the Module-6 contract).
            // They are constructed ready but stay closed: the controller shows the HUD on equip and opens
            // the GUI on F.
            // config carries the pinned favorites; the save action lets the GUI persist a pin change
            // immediately (B-24-2 fix — not relying on Dispose firing on exit-to-title).
            ToolGui = new GuideToolGui(capi, Draft, ClientNet, ClientConfig, SaveClientConfig);
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

        private void OnLevelFinalize()
        {
            StopModeDetection();
            _modeDetectionElapsedSeconds = 0f;

            if (ClientNet.AuthorityMode != ClientAuthorityMode.Detecting) return;

            if (!ClientNet.TryResolveAuthorityMode())
                _modeDetectionTickId = _capi.Event.RegisterGameTickListener(OnModeDetectionTick, 100);
        }

        private void OnModeDetectionTick(float dt)
        {
            _modeDetectionElapsedSeconds += Math.Max(0f, dt);
            bool graceExpired = _modeDetectionElapsedSeconds >= ModeDetectionGraceSeconds;
            if (ClientNet.TryResolveAuthorityMode(graceExpired)) StopModeDetection();
        }

        private void OnLeftWorld()
        {
            StopModeDetection();
            ClientNet?.EndWorldSession();
        }

        private void OnAuthorityModeChanged(ClientAuthorityMode mode)
        {
            if (mode == ClientAuthorityMode.Detecting) return;

            _capi.Logger.Notification("[Layout] Authority mode for this world: {0}.",
                mode == ClientAuthorityMode.Networked ? "networked" : "client-only");

            if (mode == ClientAuthorityMode.Local && !ClientNet.ServerLayoutAvailable)
                _capi.ShowChatMessage(
                    "[Layout] Client-only mode is available. Hold Flax Twine in your main hand and a Hammer in your off-hand; press F for Layout settings.");

            Draft.SetPerGuideVoxelCap(ClientNet.PerGuideVoxelCap);
        }

        private void RegisterClientCommands(ICoreClientAPI capi)
        {
            var parsers = capi.ChatCommands.Parsers;
            capi.ChatCommands
                .Create("layout")
                .WithDescription("Client-side Layout commands.")
                .BeginSubCommand("client")
                    .BeginSubCommand("dispel")
                        .WithDescription("Dispel private guides locally.")
                        .WithArgs(parsers.Word("all-or-radius"))
                        .HandleWith(OnClientDispelCommand)
                    .EndSubCommand()
                .EndSubCommand();
        }

        private TextCommandResult OnClientDispelCommand(TextCommandCallingArgs args)
        {
            string argument = (args[0] as string)?.Trim();
            if (argument != null && argument.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                int count = ClientNet.DispelLocalGuides(null, 0);
                return TextCommandResult.Success($"Dispelled all {count} client-only guide(s) in this world.");
            }

            if (int.TryParse(argument, out int radius) && radius >= 0)
            {
                var position = _capi.World?.Player?.Entity?.Pos;
                if (position == null)
                    return TextCommandResult.Error("Your position is unavailable; try again after entering the world.");

                int count = ClientNet.DispelLocalGuides(
                    new Vintagestory.API.MathTools.Vec3d(position.X, position.Y, position.Z), radius);
                return TextCommandResult.Success(
                    $"Dispelled {count} client-only guide(s) within {radius} chunk(s).");
            }

            return TextCommandResult.Error("Usage: .layout client dispel all   OR   .layout client dispel <chunk radius>");
        }

        private void OnForceClientOnlyPreferenceChanged(bool forceClientOnly)
        {
            ClientConfig.ForceClientOnly = forceClientOnly;
            SaveClientConfig();
        }

        private void StopModeDetection()
        {
            if (_modeDetectionTickId == 0 || _capi == null) return;
            _capi.Event.UnregisterGameTickListener(_modeDetectionTickId);
            _modeDetectionTickId = 0;
        }

        private void OnGuidesBulkSynced()
        {
            Draft.SetPerGuideVoxelCap(ClientNet.PerGuideVoxelCap);

            // v0.2.22: tell the server this player's refill-channel preferences. The server needs them
            // because ItemChalkingPowder's held-interact runs on both sides and the SERVER mutates the
            // stacks — without this the hotbar shortcut would fire for players who had switched it off.
            ClientNet.SendChalkRefillPrefs(
                ClientConfig?.AllowHotbarChalkRefill ?? false,
                ClientConfig?.AllowInventoryChalkRefill ?? false);
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

            // Rewrite normalized config so newly introduced options appear for existing installations.
            try
            {
                capi.StoreModConfig(cfg, ClientConfigFile);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[Layout] Could not update {0}: {1}", ClientConfigFile, e.Message);
            }
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
            ClientConfig.DefaultSides = Draft.Sides;
            // The pinned favorites live in ClientConfig directly (the GUI mutates that list in place), so
            // they ride along with this same store call.

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

                StopModeDetection();
                _capi.Event.LevelFinalize -= OnLevelFinalize;
                _capi.Event.LeftWorld -= OnLeftWorld;

                if (ClientNet != null)
                {
                    ClientNet.GuidesBulkSynced -= OnGuidesBulkSynced;
                    ClientNet.AuthorityModeChanged -= OnAuthorityModeChanged;
                    ClientNet.ForceClientOnlyPreferenceChanged -= OnForceClientOnlyPreferenceChanged;
                }

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
