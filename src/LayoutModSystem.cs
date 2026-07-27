using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
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
        public LayoutAdminPolicyManager AdminPolicies { get; private set; }
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

            var serverPersistence = new ServerGuidePersistence(sapi);
            AdminPolicies = new LayoutAdminPolicyManager(serverPersistence, sapi.Logger);

            Guides = new GuideManager(
                serverPersistence,
                new BlockAccessorGuideProbe(() => sapi.World?.BlockAccessor),
                sapi.Logger,
                ServerConfig.PerGuideVoxelCap,
                ServerConfig.TotalVoxelCap,
                ServerConfig.PerPlayerTotalVoxelCap,
                ServerConfig.MaxGuidesPerPlayer,
                ServerConfig.MaxGuidesWorldWide,
                uid => AdminPolicies.EffectiveGuideLimit(uid, ServerConfig.MaxGuidesPerPlayer),
                uid => AdminPolicies.EffectivePlayerTotalVoxelCap(
                    uid, ServerConfig.PerPlayerTotalVoxelCap));

            // Server save/load event ownership stays in the server composition root. GuideManager itself is
            // now side-neutral and can also back an intentionally transient client-only authority.
            sapi.Event.SaveGameLoaded += AdminPolicies.Load;
            sapi.Event.SaveGameLoaded += Guides.Load;
            sapi.Event.GameWorldSave += AdminPolicies.Persist;
            sapi.Event.GameWorldSave += Guides.Persist;

            Locks = new GuideLockManager();
            Undo = new UndoManager(Guides, ServerConfig.UndoHistoryDepth, Locks);

            ServerNet = new ServerNetworkHandler(
                sapi, Guides, Locks, Undo, AdminPolicies,
                ServerConfig.RequiredPrivilege,
                ServerConfig.AdminCanOverrideLocks,
                ServerConfig.AllowClientOnlyMode,
                ServerConfig.EnableChalkDurability);

            sapi.Logger.Notification(
                "[Layout] Server started. Caps: {0} voxels/guide, {1} per-player total, {2} world total, {3} guides/player, {4} world-wide (0 = unlimited); undo depth {5}; privilege '{6}'; admin lock-override {7}; client-only mode {8}.",
                ServerConfig.PerGuideVoxelCap, ServerConfig.PerPlayerTotalVoxelCap,
                ServerConfig.TotalVoxelCap, ServerConfig.MaxGuidesPerPlayer,
                ServerConfig.MaxGuidesWorldWide, ServerConfig.UndoHistoryDepth,
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
            PushOpacitiesToMeshBuilder();
            GuideMeshBuilder.ConfigureBlockPlaneInset(ClientConfig.ZFightInset);

            // The one DraftManager (client tool state), seeded with the remembered defaults. The cap seed is
            // the baked default until the server's bulk sync replaces it below.
            Draft = new DraftManager();
            Draft.SetScale(ClientConfig.DefaultScale);
            Draft.SetProjection((ProjectionMode)ClientConfig.DefaultProjection);
            Draft.SetFilled(ClientConfig.DefaultFill);
            Draft.SetWireframe(ClientConfig.DefaultWireframe);
            Draft.SetShape((GuideShapeType)ClientConfig.DefaultShape,
                           (ShapeConstraint)ClientConfig.DefaultConstraint);
            Draft.SetDivisions(ClientConfig.DefaultDivisions);
            Draft.SetSides(ClientConfig.DefaultSides);

            // The one ClientNetworkHandler (mirror + send-API). On every bulk sync (join), push the server's
            // per-guide cap into the placement pre-check so client and server agree on the same figure.
            ClientNet = new ClientNetworkHandler(capi);
            ClientNet.ResetAuthorityMode(ClientConfig.ForceClientOnly);
            ClientNet.GuidesBulkSynced += OnGuidesBulkSynced;
            ClientNet.PublicGuidePolicyChanged += OnPublicGuidePolicyChanged;
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
            Renderer.SetRenderingEnabled(ClientConfig.GuideRenderingEnabled);
            // A hand-edited layout-client.json must take effect at login, not only via the command.
            Renderer.SetShaderBrightness(
                ClientConfig.ShaderGuideBrightness, ClientConfig.ShaderAmbientResponse);
            Renderer.SetVoxelFrameStrength(ClientConfig.VoxelFrameStrength);

            // Restore the saved built-voxel colouring. Set before any guide mesh exists, so the rebuild
            // inside it finds nothing to rebuild and this costs nothing at startup.
            if (ClientConfig.OccupancyRecolour) Renderer.SetOccupancyEnabled(true);

            ClientNet.GuideRenderingChanged += OnGuideRenderingChanged;

            // The two dialogs share the same DraftManager + ClientNetworkHandler (the Module-6 contract).
            // They are constructed ready but stay closed: the controller shows the HUD on equip and opens
            // the GUI on F.
            // config carries the pinned favorites; the save action lets the GUI persist a pin change
            // immediately (B-24-2 fix — not relying on Dispose firing on exit-to-title).
            ToolGui = new GuideToolGui(capi, Draft, ClientNet, ClientConfig, SaveClientConfig,
                ApplyOpacitiesAndRebuild, ApplyOccupancyRecolour,
                () => Renderer?.RefreshOccupancy());
            Hud = new GuideHud(capi, Draft, ClientNet);

            // The aim-controller: per-tick raycast + click routing while the tool is held. It (not the
            // ModSystem, not the item) owns all interaction state, including comatose grab sessions.
            Controller = new GuideToolController(capi, Draft, ClientNet, Renderer, ToolGui, Hud);
            Controller.OnRenderingChanged(Renderer.RenderingEnabled);
            if (ClientNet.PublicGuideAccessJailed) Controller.OnPublicGuidePolicyChanged(true);

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

            if (Renderer?.RenderingEnabled == false)
                _capi.ShowChatMessage(
                    "[Layout] Guide rendering is currently off. Use /layout on to turn it back on "
                    + "(or .layout on in client-only mode).");

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
                .BeginSubCommand("dispel")
                    .WithDescription("Dispel private guides locally.")
                    .WithArgs(parsers.Word("all-or-radius"))
                    .HandleWith(OnClientDispelCommand)
                .EndSubCommand()
                .BeginSubCommand("who")
                    .WithDescription("Show the creator and last sculptor of your selected or targeted guide.")
                    .HandleWith(OnClientWhoCommand)
                .EndSubCommand()
                .BeginSubCommand("renderstats")
                    .WithDescription("Show local Layout guide rendering statistics for the last frame.")
                    .HandleWith(OnClientRenderStatsCommand)
                .EndSubCommand()
                .BeginSubCommand("inset")
                    .WithDescription(
                        "Set the anti-z-fight inset in blocks (0-0.05, default 0.003). Raise it if guides "
                        + "resting on the ground shimmer. No argument reports the current value.")
                    .WithArgs(parsers.OptionalFloat("blocks", float.NaN))   // NaN = "not supplied"; see Supplied()
                    .HandleWith(OnClientInsetCommand)
                .EndSubCommand()
                .BeginSubCommand("voxelframe")
                    .WithDescription(
                        "Set how strongly each voxel's boundary is outlined (0 = off, 1 = strongest). "
                        + "No argument reports the current value.")
                    .WithArgs(parsers.OptionalFloat("strength", float.NaN))
                    .HandleWith(OnClientVoxelFrameCommand)
                .EndSubCommand()
                .BeginSubCommand("shaderbrightness")
                    .WithDescription(
                        "Set custom-shader guide brightness (0.2-1.5) and ambient response (0-1). "
                        + "No arguments reports the current values.")
                    .WithArgs(
                        parsers.OptionalFloat("brightness", float.NaN),
                        parsers.OptionalFloat("ambient-response", float.NaN))
                    .HandleWith(OnClientShaderBrightnessCommand)
                .EndSubCommand()
                .BeginSubCommand("shader")
                    .WithDescription(
                        "Toggle the lean custom guide shader against the game's standard shader.")
                    .WithArgs(parsers.Word("on-or-off"))
                    .HandleWith(OnClientShaderCommand)
                .EndSubCommand()
                .BeginSubCommand("weld")
                    .WithDescription(
                        "Diagnostic: toggle guide vertex welding to compare the meshes side by side.")
                    .WithArgs(parsers.Word("on-or-off"))
                    .HandleWith(OnClientWeldCommand)
                .EndSubCommand()
                // Two subcommands rather than one with an optional radius: parsers.OptionalInt yields 0
                // when the argument is absent, not null, so an `args[0] is int` test can never tell
                // "no radius given" from "radius 0" and the inspect path was unreachable (v0.3.76 bug).
                .BeginSubCommand("occupancy")
                    .WithDescription(
                        "Diagnostic: read sub-block material occupancy of the block you are looking at, "
                        + "and print the 1/16 slice at your aim point.")
                    .HandleWith(OnClientOccupancyCommand)
                .EndSubCommand()
                .BeginSubCommand("built")
                    .WithDescription(
                        "Colour guide voxels that already hold material (on / off / refresh). Rebuilds "
                        + "your guides; affects nobody else.")
                    .WithArgs(parsers.Word("on-off-or-refresh"))
                    .HandleWith(OnClientBuiltCommand)
                .EndSubCommand()
                .BeginSubCommand("occupancyscan")
                    .WithDescription(
                        "Diagnostic: read sub-block occupancy for a cube of the given radius (1-32) around "
                        + "you and report the solid/empty/partial split, timing, and resident size.")
                    .WithArgs(parsers.Int("radius"))
                    .HandleWith(OnClientOccupancyScanCommand)
                .EndSubCommand()
                .BeginSubCommand("blockevents")
                    .WithDescription(
                        "Diagnostic: log every client block-change event. Use it somewhere QUIET — the "
                        + "event also fires for nearby chests, furnaces and other ticking blocks.")
                    .WithArgs(parsers.Word("on-or-off"))
                    .HandleWith(OnClientBlockEventsCommand)
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Turn off all Layout guide rendering for yourself.")
                    .HandleWith(OnClientRenderingOffCommand)
                .EndSubCommand()
                .BeginSubCommand("on")
                    .WithDescription("Turn on all Layout guide rendering for yourself.")
                    .HandleWith(OnClientRenderingOnCommand)
                .EndSubCommand();
        }

        private TextCommandResult OnClientRenderingOffCommand(TextCommandCallingArgs args) =>
            SetLocalRenderingState(false);

        private TextCommandResult OnClientRenderingOnCommand(TextCommandCallingArgs args) =>
            SetLocalRenderingState(true);

        /// <summary>
        /// Live tuning for the anti-z-fight inset. Unlike the brightness and frame controls this one is
        /// baked into the mesh, so every guide is rebuilt when it changes.
        /// </summary>
        private TextCommandResult OnClientInsetCommand(TextCommandCallingArgs args)
        {
            if (Renderer == null) return TextCommandResult.Error("Layout renderer is not active.");

            bool changed = false;
            if (Supplied(args[0], out float inset))
            {
                ClientConfig.ZFightInset = inset;
                ClientConfig.Normalize();
                SaveClientConfig();
                GuideMeshBuilder.ConfigureBlockPlaneInset(ClientConfig.ZFightInset);
                Renderer.RebuildAllForDiagnostics();   // the inset is baked in at build time
                changed = true;
            }

            return TextCommandResult.Success(string.Format(
                "Layout z-fight inset {0:0.0000} blocks.{1}",
                ClientConfig.ZFightInset,
                changed
                    ? " Saved, guides rebuilt."
                    : " (Pass a value 0-0.05 to change; raise it if voxels shimmer against material,"
                      + " lower it if guides look inflated.)"));
        }

        /// <summary>
        /// Live tuning for the voxel boundary frame, saved immediately to layout-client.json.
        /// </summary>
        private TextCommandResult OnClientVoxelFrameCommand(TextCommandCallingArgs args)
        {
            if (Renderer == null) return TextCommandResult.Error("Layout renderer is not active.");

            bool changed = false;
            if (Supplied(args[0], out float strength))
            {
                ClientConfig.VoxelFrameStrength = strength;
                ClientConfig.Normalize();
                SaveClientConfig();
                Renderer.SetVoxelFrameStrength(ClientConfig.VoxelFrameStrength);
                changed = true;
            }

            float current = ClientConfig.VoxelFrameStrength;
            string state = current <= 0f ? "off" : string.Format("{0:0.00}", current);
            return TextCommandResult.Success(
                changed
                    ? $"Layout voxel frame {state}. Saved."
                    : $"Layout voxel frame {state}. (Pass a value 0-1 to change; requires the custom "
                      + "shader — /layout shader on.)");
        }

        /// <summary>
        /// Live tuning for the custom shader's brightness, saved immediately to layout-client.json. Meant to
        /// be dialled against <c>/layout shader off</c> until the two look alike, rather than guessed.
        /// </summary>
        private TextCommandResult OnClientShaderBrightnessCommand(TextCommandCallingArgs args)
        {
            if (Renderer == null) return TextCommandResult.Error("Layout renderer is not active.");

            bool changed = false;
            if (Supplied(args[0], out float brightness))
            {
                ClientConfig.ShaderGuideBrightness = brightness;
                changed = true;
            }
            if (Supplied(args[1], out float response))
            {
                ClientConfig.ShaderAmbientResponse = response;
                changed = true;
            }

            if (changed)
            {
                ClientConfig.Normalize();   // folds out-of-range values back to the safe band
                SaveClientConfig();
                Renderer.SetShaderBrightness(
                    ClientConfig.ShaderGuideBrightness, ClientConfig.ShaderAmbientResponse);
            }

            return TextCommandResult.Success(string.Format(
                "Layout guide brightness {0:0.00}, ambient response {1:0.00}.{2}",
                ClientConfig.ShaderGuideBrightness, ClientConfig.ShaderAmbientResponse,
                changed ? " Saved." : " (Pass values to change; 1.0 / 0.0 is the raw palette.)"));
        }

        /// <summary>
        /// A/B switch between the lean custom guide shader and the game's standard shader. Unlike the weld
        /// toggle this is expected to look DIFFERENT: the standard shader runs guide colour through ambient
        /// lighting and shadow-map brightness, so guides currently darken in shadow, while the custom
        /// shader emits the palette verbatim. Reproducing that needs samplers the modding API does not
        /// expose, so the difference is a decision, not a defect to fix.
        /// </summary>
        private TextCommandResult OnClientShaderCommand(TextCommandCallingArgs args)
        {
            if (Renderer == null) return TextCommandResult.Error("Layout renderer is not active.");

            string word = (args[0] as string)?.Trim().ToLowerInvariant();
            bool enable;
            switch (word)
            {
                case "on": case "true": case "1": enable = true; break;
                case "off": case "false": case "0": enable = false; break;
                default: return TextCommandResult.Error("Use /layout shader on or /layout shader off.");
            }

            if (enable && !Renderer.CustomShaderAvailable)
                return TextCommandResult.Error(
                    "The custom guide shader failed to compile — see client-main.log. "
                    + "Layout is using the standard shader.");

            Renderer.SetCustomShaderEnabled(enable);
            return TextCommandResult.Success(
                enable
                    ? "Layout custom guide shader ON — lean shader, guides are fully self-lit."
                    : "Layout custom guide shader OFF — standard shader, guides take ambient light "
                      + "and shadow.");
        }

        /// <summary>
        /// Diagnostic A/B switch for v0.3.57 vertex welding. Welding is proven to emit an identical triangle
        /// stream, so this should show no visible difference — it exists so that claim can be checked in
        /// play, on one guide, from one camera position, instead of by swapping builds and comparing from
        /// memory. Not persisted: welding is always on again next launch.
        /// </summary>
        private TextCommandResult OnClientWeldCommand(TextCommandCallingArgs args)
        {
            string word = (args[0] as string)?.Trim().ToLowerInvariant();
            bool enable;
            switch (word)
            {
                case "on": case "true": case "1": enable = true; break;
                case "off": case "false": case "0": enable = false; break;
                default: return TextCommandResult.Error("Use /layout weld on or /layout weld off.");
            }

            if (Systems.GuideMeshBuilder.WeldByDefault == enable)
                return TextCommandResult.Success(
                    $"Layout vertex welding is already {(enable ? "on" : "off")}.");

            Systems.GuideMeshBuilder.WeldByDefault = enable;
            Renderer?.RebuildAllForDiagnostics();
            return TextCommandResult.Success(
                enable
                    ? "Layout vertex welding ON — shared vertices (the v0.3.57 default)."
                    : "Layout vertex welding OFF — pre-v0.3.57 unshared vertices. "
                      + "Expect the same picture at roughly 2.4x the mesh data.");
        }

        private TextCommandResult OnClientRenderStatsCommand(TextCommandCallingArgs args)
        {
            if (Renderer == null)
                return TextCommandResult.Error("Enter a world before inspecting Layout render statistics.");

            return TextCommandResult.Success(Renderer.DescribeRenderStats());
        }

        private TextCommandResult SetLocalRenderingState(bool enabled)
        {
            if (Renderer == null)
                return TextCommandResult.Error("Enter a world before changing Layout rendering.");

            Renderer.SetRenderingEnabled(enabled);
            Controller?.OnRenderingChanged(enabled);
            ClientConfig.GuideRenderingEnabled = enabled;
            SaveClientConfig();
            return TextCommandResult.Success(enabled
                ? "Layout guide rendering is on for you."
                : "Layout guide rendering is off for you.");
        }

        private void OnGuideRenderingChanged(bool enabled)
        {
            Renderer?.SetRenderingEnabled(enabled);
            Controller?.OnRenderingChanged(enabled);
            if (ClientConfig == null) return;
            ClientConfig.GuideRenderingEnabled = enabled;
            SaveClientConfig();
        }

        private TextCommandResult OnClientWhoCommand(TextCommandCallingArgs args)
        {
            if (Controller == null)
                return TextCommandResult.Error("Enter a world before inspecting a Layout guide.");

            string description = Controller.DescribeCurrentGuide(out bool found);
            return found
                ? TextCommandResult.Success(description)
                : TextCommandResult.Error(description);
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

            return TextCommandResult.Error("Usage: .layout dispel all   OR   .layout dispel <chunk radius>");
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

        private void OnPublicGuidePolicyChanged(bool jailed)
        {
            Draft.SetPerGuideVoxelCap(ClientNet.PerGuideVoxelCap);
            Controller?.OnPublicGuidePolicyChanged(jailed);
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

        /// <summary>
        /// The block-occupancy recolour toggle (v0.3.79). Also reachable from the GUI settings page; both
        /// routes go through <see cref="ApplyOccupancyRecolour"/> so the config, the renderer and the saved
        /// file cannot drift apart.
        /// </summary>
        private TextCommandResult OnClientBuiltCommand(TextCommandCallingArgs args)
        {
            if (Renderer == null) return TextCommandResult.Error("Layout renderer is not active.");

            switch ((args[0] as string ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1":
                    return TextCommandResult.Success(
                        ApplyOccupancyRecolour(true)
                            ? "Built-voxel colouring ON. Guides rebuilt; voxels holding material are cyan."
                            : "Built-voxel colouring is already on.");

                case "off": case "false": case "0":
                    return TextCommandResult.Success(
                        ApplyOccupancyRecolour(false)
                            ? "Built-voxel colouring OFF. Guides rebuilt."
                            : "Built-voxel colouring is already off.");

                case "refresh":
                    if (!ClientConfig.OccupancyRecolour)
                        return TextCommandResult.Error("Turn it on first: /layout built on.");
                    int stale = Renderer.OccupancyStaleGuides;
                    Renderer.RefreshOccupancy();
                    return TextCommandResult.Success(stale > 0
                        ? $"Re-read the world and rebuilt, including {stale} guide(s) too large to update live."
                        : "Re-read the world and rebuilt. (Ordinary guides update by themselves as you "
                          + "build — refresh is only needed for very large ones.)");

                default:
                    return TextCommandResult.Error("Use /layout built on, off, or refresh.");
            }
        }

        /// <summary>
        /// Sets the recolour state, persists it, and rebuilds. Returns false when nothing changed.
        /// </summary>
        internal bool ApplyOccupancyRecolour(bool enabled)
        {
            if (Renderer == null || ClientConfig == null) return false;
            if (!Renderer.SetOccupancyEnabled(enabled)) return false;   // rebuilds only on a real change
            ClientConfig.OccupancyRecolour = enabled;
            SaveClientConfig();
            return true;
        }

        // ---- Diagnostic: sub-block occupancy readback (v0.3.76) ----
        //
        // STAGE 1 verification for PLAN_BLOCK_OCCUPANCY. The feature stands or falls on whether the client
        // can answer "is there material in this 1/16 cell" correctly — everything downstream is plumbing.
        // The honest way to check that is to chisel a recognisable shape, aim at it, and see whether the
        // printed slice is the shape you cut.
        private BlockOccupancy _occupancy;

        private TextCommandResult OnClientOccupancyCommand(TextCommandCallingArgs args)
        {
            IBlockAccessor accessor = _capi?.World?.BlockAccessor;
            if (accessor == null) return TextCommandResult.Error("No world.");

            _occupancy ??= new BlockOccupancy();

            BlockSelection sel = _capi.World.Player?.CurrentBlockSelection;
            if (sel?.Position == null)
                return TextCommandResult.Error(
                    "Look at a block first. (/layout occupancyscan <radius> scans around you instead.)");

            _occupancy.Invalidate(sel.Position);   // always read fresh; this is a diagnostic
            BlockFill fill = _occupancy.FillAt(accessor, sel.Position);
            int filled = _occupancy.FilledCellCount(accessor, sel.Position);
            bool micro = BlockOccupancy.HasMicroBlockEntity(accessor, sel.Position);
            Block block = accessor.GetBlock(sel.Position);

            _capi.ShowChatMessage(string.Format(
                "[Layout] {0} @ {1},{2},{3} — {4}, {5}/4096 cells, microblock={6}",
                block?.Code?.ToShortString() ?? "?",
                sel.Position.X, sel.Position.Y, sel.Position.Z,
                fill, filled, micro ? "YES" : "no"));

            if (fill == BlockFill.Partial) PrintOccupancySlice(accessor, sel);
            return TextCommandResult.Success("");
        }

        // One horizontal 16x16 slice at the height you are aiming at. '#' is material, '.' is air. Printed
        // north-to-south so it reads the way the block looks from above.
        private void PrintOccupancySlice(IBlockAccessor accessor, BlockSelection sel)
        {
            double hitY = sel.HitPosition?.Y ?? 0.5;
            int layer = Math.Min(15, Math.Max(0, (int)(hitY * BlockOccupancy.CellsPerBlock)));

            int baseX = sel.Position.X * BlockOccupancy.CellsPerBlock;
            int baseY = sel.Position.Y * BlockOccupancy.CellsPerBlock + layer;
            int baseZ = sel.Position.Z * BlockOccupancy.CellsPerBlock;

            _capi.ShowChatMessage($"[Layout] horizontal slice at cell y={layer} (aim height):");
            var row = new char[BlockOccupancy.CellsPerBlock];
            for (int cz = 0; cz < BlockOccupancy.CellsPerBlock; cz++)
            {
                for (int cx = 0; cx < BlockOccupancy.CellsPerBlock; cx++)
                    row[cx] = _occupancy.IsMaterialAt(accessor, baseX + cx, baseY, baseZ + cz) ? '#' : '.';
                _capi.ShowChatMessage(new string(row));
            }
        }

        /// <summary>
        /// Whether an optional float argument was actually supplied. The optional parsers return their
        /// DEFAULT when the argument is absent, not null, so "no value given" is indistinguishable from
        /// "the default was typed" unless the default is a value nobody can type. Every optional float in
        /// this file therefore defaults to NaN, which no input parses to.
        /// </summary>
        /// <remarks>
        /// This cost three shipped commands their documented no-argument behaviour (v0.3.65–v0.3.69):
        /// <c>/layout inset</c>, <c>voxelframe</c> and <c>shaderbrightness</c> each claimed "no argument
        /// reports the current value" but their <c>args[0] is float</c> test always passed, so a bare
        /// <c>/layout inset</c> silently SET the inset to 0 and saved it. Fixed in v0.3.78; the same trap
        /// took out <c>/layout occupancy</c> one version earlier, which is how it was noticed.
        /// </remarks>
        private static bool Supplied(object arg, out float value)
        {
            value = 0f;
            if (arg is not float f || float.IsNaN(f)) return false;
            value = f;
            return true;
        }

        private TextCommandResult OnClientOccupancyScanCommand(TextCommandCallingArgs args)
        {
            IBlockAccessor accessor = _capi?.World?.BlockAccessor;
            if (accessor == null) return TextCommandResult.Error("No world.");
            if (args[0] is not int requested || requested < 1 || requested > 32)
                return TextCommandResult.Error("Give a radius from 1 to 32.");

            _occupancy ??= new BlockOccupancy();
            return ScanOccupancy(accessor, requested);
        }

        private TextCommandResult ScanOccupancy(IBlockAccessor accessor, int radius)
        {
            var p = _capi.World.Player?.Entity?.Pos;
            if (p == null) return TextCommandResult.Error("No player position.");

            _occupancy.Clear();
            int cx = (int)Math.Floor(p.X), cy = (int)Math.Floor(p.Y), cz = (int)Math.Floor(p.Z);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            BlockPos pos = p.AsBlockPos;   // carries the player's dimension; Set() below preserves it
            for (int x = cx - radius; x <= cx + radius; x++)
                for (int y = cy - radius; y <= cy + radius; y++)
                    for (int z = cz - radius; z <= cz + radius; z++)
                    {
                        pos.Set(x, y, z);
                        _occupancy.FillAt(accessor, pos);
                    }
            timer.Stop();

            int side = radius * 2 + 1;
            return TextCommandResult.Success(string.Format(
                "Layout occupancy: {0} blocks ({1} cubed) in {2} ms — {3} solid, {4} empty, {5} partial. "
                + "~{6} KB resident. Bricks are built only for the {5} partial ones.",
                side * side * side, side, timer.ElapsedMilliseconds,
                _occupancy.SolidBlocks, _occupancy.EmptyBlocks, _occupancy.PartialBlocks,
                _occupancy.ApproximateBytes / 1024));
        }

        // ---- Diagnostic: block-change event tracing (v0.3.75) ----
        //
        // PLAN_BLOCK_OCCUPANCY §3.2 concludes, from tracing call sites in the shipped assemblies, that a
        // chisel stroke reaches IClientEventAPI.BlockChanged through the block-entity sync path
        // (BlockEntityChisel.UpdateVoxel -> MarkDirty -> GeneralPacketHandler.HandleBlockEntities ->
        // TriggerBlockChanged). That is static evidence: the call exists, but whether it is reached under a
        // guard cannot be seen from token scanning. This command turns it into an observation — switch it
        // on, chisel a block, and see whether events actually arrive.
        //
        // It measures the second half of that finding too. The event fires on ANY block-entity sync, so a
        // chest or a running furnace nearby will trigger it as well; the running count shows how noisy the
        // event really is, which decides how hard the real handler has to work to reject what it does not
        // care about.
        private bool _blockEventLogging;
        private int _blockEventCount;
        private int _blockEventMicroCount;

        private void OnDiagnosticBlockChanged(BlockPos pos, Block oldBlock)
        {
            _blockEventCount++;
            if (_capi?.World?.BlockAccessor == null || pos == null) return;

            Block now = _capi.World.BlockAccessor.GetBlock(pos);

            // A chiselled block carries a BlockEntityMicroBlock (BlockEntityChisel derives from it), so this
            // flag is what distinguishes "someone edited sub-block detail here" from a whole-block change.
            bool micro = _capi.World.BlockAccessor
                .GetBlockEntity<Vintagestory.GameContent.BlockEntityMicroBlock>(pos) != null;
            if (micro) _blockEventMicroCount++;

            _capi.ShowChatMessage(string.Format(
                "[Layout] #{0} @ {1},{2},{3}  was={4}  now={5}  microblock={6}",
                _blockEventCount, pos.X, pos.Y, pos.Z,
                oldBlock?.Code?.ToShortString() ?? "null",
                now?.Code?.ToShortString() ?? "null",
                micro ? "YES" : "no"));
        }

        private TextCommandResult OnClientBlockEventsCommand(TextCommandCallingArgs args)
        {
            bool enable;
            switch ((args[0] as string ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": enable = true; break;
                case "off": case "false": case "0": enable = false; break;
                default: return TextCommandResult.Error(
                    "Use /layout blockevents on or /layout blockevents off.");
            }

            if (enable == _blockEventLogging)
                return TextCommandResult.Success(
                    $"Layout block-event logging is already {(enable ? "on" : "off")}.");

            if (enable)
            {
                _blockEventCount = 0;
                _blockEventMicroCount = 0;
                _capi.Event.BlockChanged += OnDiagnosticBlockChanged;
                _blockEventLogging = true;
                return TextCommandResult.Success(
                    "Layout block-event logging ON. Place a block, break one, then CHISEL one and watch "
                    + "for microblock=YES lines. Somewhere quiet — chests and furnaces fire this too.");
            }

            _capi.Event.BlockChanged -= OnDiagnosticBlockChanged;
            _blockEventLogging = false;
            return TextCommandResult.Success(string.Format(
                "Layout block-event logging OFF. {0} event(s), {1} of them on a microblock.",
                _blockEventCount, _blockEventMicroCount));
        }

        // Pushes the configured guide alphas into the mesh builder's colour table.
        private void PushOpacitiesToMeshBuilder()
        {
            GuideMeshBuilder.ConfigureOpacities(
                ClientConfig.OpacityBody, ClientConfig.OpacityLocked, ClientConfig.OpacityApex,
                ClientConfig.OpacityAnchor, ClientConfig.OpacityGrabbed, ClientConfig.OpacityHiddenAnchor);
        }

        /// <summary>
        /// Re-applies the client opacity values and rebuilds every guide mesh. Opacity is baked into vertex
        /// COLOURS at build time, so changing the config alone changes nothing on screen — the meshes have
        /// to be rebuilt for it to show. Wired to the GUI's Settings tab; the caller is responsible for
        /// coalescing rapid changes (a slider drag) so this runs once, not once per step.
        /// </summary>
        internal void ApplyOpacitiesAndRebuild()
        {
            if (ClientConfig == null) return;
            PushOpacitiesToMeshBuilder();
            Renderer?.RebuildAllForDiagnostics();
        }

        // Write the player's current tool settings back as the new defaults — "come up the way I left it".
        private void SaveClientConfig()
        {
            if (_capi == null || ClientConfig == null || Draft == null) return;

            ClientConfig.DefaultScale = Draft.Scale;
            ClientConfig.DefaultProjection = (int)Draft.Projection;
            ClientConfig.DefaultFill = Draft.Filled;
            ClientConfig.DefaultWireframe = Draft.Wireframe;
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
                if (_blockEventLogging)
                {
                    _capi.Event.BlockChanged -= OnDiagnosticBlockChanged;
                    _blockEventLogging = false;
                }

                if (ClientNet != null)
                {
                    ClientNet.GuidesBulkSynced -= OnGuidesBulkSynced;
                    ClientNet.PublicGuidePolicyChanged -= OnPublicGuidePolicyChanged;
                    ClientNet.AuthorityModeChanged -= OnAuthorityModeChanged;
                    ClientNet.ForceClientOnlyPreferenceChanged -= OnForceClientOnlyPreferenceChanged;
                    ClientNet.GuideRenderingChanged -= OnGuideRenderingChanged;
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

            if (_sapi != null)
            {
                try { ServerNet?.Dispose(); }
                catch (Exception e) { _sapi.Logger.Warning("[Layout] Server network dispose: {0}", e.Message); }
                ServerNet = null;
            }

            // The remaining server managers are dropped with the system.
            base.Dispose();
        }
    }
}
