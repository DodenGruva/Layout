using System.Collections.Generic;
using Newtonsoft.Json;
using Layout.Guide;

namespace Layout.Config
{
    /// <summary>
    /// Client-side, per-machine configuration, persisted as <c>layout-client.json</c> in the game's ModConfig
    /// folder. Holds the tool-appearance defaults — scale, projection, fill — which are client-retained and
    /// client-dictated (the server never sees or tracks them). Doubles as "remember my last-used settings":
    /// <see cref="LayoutModSystem"/> applies these to <c>DraftManager</c> on client start and writes the
    /// current values back on client shutdown, so the tool comes up the way the player left it.
    /// </summary>
    public class LayoutClientConfig
    {
        /// <summary>
        /// When true, prefers placing new guides in this client's per-world local file. On a server that
        /// runs Layout this preference applies only when its allowClientOnlyMode setting permits it.
        /// </summary>
        [JsonProperty("forceClientOnly")]
        public bool ForceClientOnly { get; set; } = false;

        [JsonProperty("_forceClientOnlyNote")]
        public string ForceClientOnlyNote { get; set; } =
            "Only applies on Layout-enabled servers when allowClientOnlyMode is enabled by the server owner.";

        /// <summary>
        /// Whether Layout guides are rendered for this client. Updated immediately by /layout on|off and
        /// retained across disconnects, world changes, and game restarts.
        /// </summary>
        [JsonProperty("guideRenderingEnabled")]
        public bool GuideRenderingEnabled { get; set; } = true;

        // ------------------------------------------------------------------------------------------
        //  Chalk refill channels (v0.2.22 — moved here from the SERVER config, human-directed). These are
        //  player CONVENIENCE toggles, not server policy: refilling always costs the same powder wherever
        //  it happens, so there is nothing for a server to protect. Ground-storage refill (Shift+right-click
        //  a set-down kit) is the intended ritual and is always available — only these two shortcuts are
        //  opt-in. Because the gate is now client-side, the SERVER never checks them (see LayoutModSystem);
        //  it still validates that a refill request targets a real kit with a real powder stack, which is
        //  inventory-corruption safety, not policy.
        // ------------------------------------------------------------------------------------------

        /// <summary>Allows refilling a hotbar kit by right-clicking with powder in hand. Default false.</summary>
        [JsonProperty("allowHotbarChalkRefill")]
        public bool AllowHotbarChalkRefill { get; set; } = false;

        /// <summary>
        /// Allows refilling by right-clicking a held powder stack onto a kit's inventory slot. Default false.
        /// </summary>
        [JsonProperty("allowInventoryChalkRefill")]
        public bool AllowInventoryChalkRefill { get; set; } = false;

        [JsonProperty("_chalkRefillNote")]
        public string ChalkRefillNote { get; set; } =
            "Convenience only. Setting a kit on the ground and Shift+right-clicking it with powder always "
            + "works regardless of these two settings.";

        /// <summary>Voxel scale the tool starts with (one of 1/2/4/8/16). Default 1 — chisel resolution.</summary>
        [JsonProperty("defaultScale")]
        public int DefaultScale { get; set; } = 1;

        /// <summary>
        /// Projection mode the tool starts with, stored as the pinned enum value so a hand-edited file stays
        /// stable across versions: 0 = Volumetric, 1 = Surface.
        /// </summary>
        [JsonProperty("defaultProjection")]
        public int DefaultProjection { get; set; } = (int)ProjectionMode.Volumetric;

        /// <summary>Fill state the tool starts with. Default false (hollow).</summary>
        [JsonProperty("defaultFill")]
        public bool DefaultFill { get; set; } = false;

        /// <summary>For 3D shapes, start with a structural wireframe instead of the shell.</summary>
        [JsonProperty("defaultWireframe")]
        public bool DefaultWireframe { get; set; } = false;

        /// <summary>
        /// The shape the tool starts on, as the pinned <see cref="GuideShapeType"/> value so a hand-edited
        /// file stays stable across versions. See that enum for the full list — it is the authority, and
        /// re-listing the values here is how this comment came to claim the only shapes were 0 = Arch and
        /// 1 = Ellipse long after thirteen more had been added.
        /// </summary>
        public int DefaultShape { get; set; } = (int)GuideShapeType.Arch;

        /// <summary>
        /// The constraint the tool starts with (pinned ShapeConstraint values). An invalid pairing for the
        /// shape normalises to None.
        /// </summary>
        public int DefaultConstraint { get; set; } = (int)ShapeConstraint.None;

        /// <summary>Session 9: default equal-part division marks for new guides (0/1 = none).</summary>
        public int DefaultDivisions { get; set; } = 0;

        /// <summary>Session 11: the polygon side count the tool starts with (3..24).</summary>
        public int DefaultSides { get; set; } = 6;

        /// <summary>
        /// Session 11: the pinned shapes filling the picker's slots (up to FOUR since 0.1.15), as the
        /// GUI's shape codes (each maps to a {type + constraint} pair, e.g. "halfcircle" = Arch +
        /// SemiCircle). HARD-KEPT (0.1.15, human-directed): starring never evicts — right-click a starred
        /// tile to unstar it and free the slot; fewer than four is fine (empty slots show as faint
        /// placeholders). Unknown codes are dropped at load; the list is never re-padded.
        /// </summary>
        /// <remarks>
        /// B-24-2 fix (v0.1.25): <see cref="ObjectCreationHandling.Replace"/> is REQUIRED. Without it,
        /// Newtonsoft REUSES this pre-initialised default list on deserialization and APPENDS the saved
        /// pins after the four defaults; <see cref="Normalize"/> then caps at 4 keeping the first four —
        /// the defaults — so saved pins were silently dropped every load (they persisted to disk fine; the
        /// LOAD threw them away). Replace makes the deserialized list overwrite the default; when the JSON
        /// key is absent (first run) the default initializer still stands.
        /// </remarks>
        [JsonProperty("favoriteShapes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> FavoriteShapes { get; set; } =
            new List<string> { "arch", "halfcircle", "circle", "line" };

        // ------------------------------------------------------------------------------------------
        //  Guide opacities (Session-8, item 2): every voxel-type alpha is a client visual preference,
        //  hand-tunable here without a rebuild. 0 = invisible, 1 = solid. Defaults per the Session-8
        //  decision: body and the point markers down to 0.5; anchors and the White grabbed highlight left
        //  where they were.
        //
        //  ONLY THE BODY ALPHA HAS A CONTROL. The settings page's Guide opacity slider drives OpacityBody
        //  live (it rebuilds every mesh behind a debounce, since alpha is baked into vertex colours). The
        //  other five are file-only and are read at client start, so those still want an edit-and-restart.
        //  This block said "applied once at client start" for all of them until the 2026-08-01 sweep; the
        //  slider arrived in v0.3.72 and the note never followed it.
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Which guide colour scheme to draw with, as the pinned <c>GuidePaletteScheme</c> value so a
        /// hand-edited file stays stable across versions: 0 = Default, 1 = Red-Green Safe, 3 = Custom.
        /// <b>2 is RETIRED</b> (it was High Contrast, dropped in v0.4.10) and is not reusable — the number
        /// stays burnt because a file written by 0.4.8/0.4.9 can still say 2. See <c>GuidePaletteScheme</c>.
        /// </summary>
        /// <remarks>
        /// Accessibility, not taste (F11). In the default scheme locked points are red and apex points are
        /// green — the exact pair deuteranopia and protanopia collapse — so a player with either cannot tell
        /// a locked point from an apex at all. Unknown values fall back to Default.
        /// </remarks>
        [JsonProperty("colorScheme")]
        public int ColorScheme { get; set; } = (int)Systems.GuidePaletteScheme.Default;

        /// <summary>
        /// The player's own per-role colours, used only when <see cref="ColorScheme"/> is Custom (3). Seven
        /// "#RRGGBB" strings in the pinned role order: body, locked, apex, anchor, private anchor, division,
        /// built. Null or short means "not chosen" and falls back to the Default palette role by role.
        /// </summary>
        /// <remarks>
        /// Hex rather than numbers so the file stays hand-editable and diffable — this is the one setting a
        /// player might reasonably want to copy between machines or share.
        /// </remarks>
        [JsonProperty("customColors", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public string[] CustomColors { get; set; } = null;

        /// <summary>Alpha of the Yellow guide body. Default 0.5.</summary>
        [JsonProperty("opacityBody")]
        public float OpacityBody { get; set; } = 0.5f;

        /// <summary>Alpha of Red locked-point markers. Default 0.5.</summary>
        [JsonProperty("opacityLocked")]
        public float OpacityLocked { get; set; } = 0.5f;

        /// <summary>Alpha of the Green apex marker. Default 0.5.</summary>
        [JsonProperty("opacityApex")]
        public float OpacityApex { get; set; } = 0.5f;

        /// <summary>Alpha of Blue/Indigo anchor markers. Default 0.8 (unchanged).</summary>
        [JsonProperty("opacityAnchor")]
        public float OpacityAnchor { get; set; } = 0.8f;

        /// <summary>Alpha of the White grabbed-point highlight. Default 0.95 (unchanged).</summary>
        [JsonProperty("opacityGrabbed")]
        public float OpacityGrabbed { get; set; } = 0.95f;

        /// <summary>Alpha of the anchor dots a HIDDEN guide is reduced to. Default 0.35 (unchanged).</summary>
        [JsonProperty("opacityHiddenAnchor")]
        public float OpacityHiddenAnchor { get; set; } = 0.35f;

        /// <summary>
        /// Overall brightness of guides under the custom shader (v0.3.61). 1.0 emits the palette exactly as
        /// authored; lower values darken it toward the look of the game's standard shader.
        /// </summary>
        /// <remarks>
        /// The standard shader ran guide colour through <c>applyLight()</c> and then multiplied by
        /// shadow-map brightness, so guides were always darker than their palette and varied with light.
        /// The custom shader cannot sample the shadow map (the modding API does not expose it), so this
        /// scale plus <see cref="ShaderAmbientResponse"/> approximates that darkening cheaply. Tune live
        /// with <c>/layout shaderbrightness</c>. Clamped to 0.2–1.5.
        /// </remarks>
        [JsonProperty("shaderGuideBrightness")]
        public float ShaderGuideBrightness { get; set; } = 0.78f;

        /// <summary>
        /// How strongly guides follow the world's ambient light under the custom shader, 0–1. 0 keeps them
        /// perfectly self-lit at all hours; 1 tints and dims them fully with ambient. Default 0.55 restores
        /// most of the day/night response the standard shader gave, without any per-fragment cost.
        /// </summary>
        [JsonProperty("shaderAmbientResponse")]
        public float ShaderAmbientResponse { get; set; } = 0.55f;

        /// <summary>
        /// Darkens each voxel's boundary so individual cells are legible on a large guide surface.
        /// 0 disables it; 1 would be a fully black frame. Default 0.25 is a light pencil line.
        /// </summary>
        /// <remarks>
        /// Drawn procedurally in the fragment shader — no extra vertices, indices, or draw calls — so it
        /// costs a few arithmetic operations per pixel and nothing per voxel. Custom shader only; with
        /// <c>/layout shader off</c> there is no frame. Tune live with <c>/layout voxelframe</c>.
        /// </remarks>
        [JsonProperty("voxelFrameStrength")]
        public float VoxelFrameStrength { get; set; } = 0.25f;

        /// <summary>
        /// How far a guide's exposed faces are pushed OUT of the voxel, in world blocks, so they cannot
        /// z-fight a world block surface lying in the same plane. Default 0.0002. Raise it if guide voxels
        /// shimmer against material; lower it if guides look inflated or float off their own cells.
        /// </summary>
        /// <remarks>
        /// Clamped to 0–0.05. The name is historical: until v0.3.69 this was an INSET pulling faces off the
        /// grid plane, and it kept the name through the v0.3.70 flip so the config key, the command, and the
        /// playtest history all still line up.
        ///
        /// Playtest history. As an inset: 0.004 seamy, 0.001 shimmered with distance, 0.003 chosen (0.2.14).
        /// As an outset: 0.0006 was initially confirmed in play (v0.3.71). A later systemic alignment test
        /// found that the renderer was also translating the entire mesh 0.003 blocks toward the camera.
        /// Removing that off-grid translation made 0.0001 stable in play; 0.0002 was chosen as the default
        /// for a small extra buffer while remaining only 0.32% of a scale-1 micro-block.
        /// Tune live with <c>/layout inset</c>.
        /// </remarks>
        [JsonProperty("zFightInset")]
        public float ZFightInset { get; set; } = 0.0002f;

        /// <summary>
        /// Draw guide body voxels that already hold world material in the "built" colour (cyan), so you can
        /// see which parts of the plan exist. Off by default. Purely local — see PLAN_BLOCK_OCCUPANCY §2.3;
        /// other players are unaffected by your setting.
        /// </summary>
        /// <remarks>
        /// The colour is baked into the guide mesh, so switching this rebuilds every guide — instant on
        /// ordinary guides, a few seconds of re-streaming on very large ones.
        ///
        /// THE COLOURS FOLLOW THE WORLD. Chisel or place a block inside a guide and its colours update a
        /// beat later (the per-batch rebuild in <c>GuideRenderer</c>). Two cases still need
        /// <c>/layout built refresh</c>: a guide too large to update live, which the command names when it
        /// runs, and terrain that had not streamed in when the guide was first meshed — that one now
        /// re-probes itself as the chunks arrive (v0.4.42).
        ///
        /// This remark described the colours as STATIC, with live updates "the next stage", until the
        /// 2026-08-01 sweep. That stage shipped in v0.3.81–v0.3.82.
        /// </remarks>
        [JsonProperty("occupancyRecolour")]
        public bool OccupancyRecolour { get; set; } = false;

        /// <summary>
        /// Registers the development diagnostic chat commands. **Off by default and deliberately not
        /// surfaced in the GUI** — these were built to investigate specific problems during development and
        /// are not part of the mod a player is meant to operate.
        /// </summary>
        /// <remarks>
        /// Hidden rather than deleted because several are still the only way to run verification work that
        /// remains open (SESSION_29 §7 wants <c>blockevents</c> run in a busy base to measure event noise,
        /// and <c>occupancyscan</c> to size the occupancy cache). Setting this true in
        /// <c>layout-client.json</c> brings back: <c>renderstats</c>, <c>weld</c>, <c>occupancy</c>,
        /// <c>occupancyscan</c>, <c>blockevents</c>. Everything a player legitimately tunes — <c>built</c>,
        /// <c>inset</c>, <c>voxelframe</c>, <c>shaderbrightness</c>, <c>shader</c>, <c>on</c>/<c>off</c> —
        /// is registered unconditionally and is unaffected by this.
        /// </remarks>
        [JsonProperty("diagnosticCommands")]
        public bool DiagnosticCommands { get; set; } = false;

        /// <summary>Folds out-of-range values (e.g. from a hand-edited file) back to safe defaults.</summary>
        public void Normalize()
        {
            if (!GuideData.IsValidVoxelScale(DefaultScale)) DefaultScale = 1;
            if (DefaultProjection != (int)ProjectionMode.Volumetric &&
                DefaultProjection != (int)ProjectionMode.Surface)
                DefaultProjection = (int)ProjectionMode.Volumetric;

            // ⚠️ NOT A HAND-WRITTEN UPPER BOUND. This read `> GuideShapeType.Sphere` from 0.1.20, when
            // Sphere was the newest shape — and stayed that way as the whole volume family was added after
            // it. Sphere is 7; the enum now runs to 14, so SEVEN shapes (Dome, Cylinder, Cone, Box,
            // Tapered Cylinder, Polygonal Prism, Tapered Polygonal Prism) failed this test and were reset
            // to Arch on every load. Closing the game with a Box selected reopened it on an Arch.
            // Found by the 2026-08-01 doc-comment sweep; fixed v0.4.43.
            if (!System.Enum.IsDefined(typeof(GuideShapeType), DefaultShape))
                DefaultShape = (int)GuideShapeType.Arch;
            if (!Systems.DraftManager.IsValidPair((GuideShapeType)DefaultShape, (ShapeConstraint)DefaultConstraint))
                DefaultConstraint = (int)ShapeConstraint.None;
            if (ShaderGuideBrightness < 0.2f) ShaderGuideBrightness = 0.2f;
            if (ShaderGuideBrightness > 1.5f) ShaderGuideBrightness = 1.5f;
            if (ShaderAmbientResponse < 0f) ShaderAmbientResponse = 0f;
            if (ShaderAmbientResponse > 1f) ShaderAmbientResponse = 1f;
            if (VoxelFrameStrength < 0f) VoxelFrameStrength = 0f;
            if (VoxelFrameStrength > 1f) VoxelFrameStrength = 1f;
            if (ZFightInset < 0f) ZFightInset = 0f;
            if (ZFightInset > 0.05f) ZFightInset = 0.05f;

            if (DefaultDivisions < 0) DefaultDivisions = 0;
            if (DefaultDivisions > Shapes.DivisionMarks.MaxDivisions) DefaultDivisions = Shapes.DivisionMarks.MaxDivisions;
            DefaultSides = Shapes.PolygonShape.ClampSides(DefaultSides);

            // Favorites (0.1.15, hard-kept): keep only codes the GUI knows, dedupe, cap at four slots.
            // Deliberately NO padding — an unstar stays unstarred across restarts.
            var seen = new HashSet<string>();
            var valid = new List<string>(4);
            if (FavoriteShapes != null)
                foreach (string code in FavoriteShapes)
                {
                    string c = code?.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(c) || !UI.GuideToolGui.IsKnownShapeCode(c) || !seen.Add(c)) continue;
                    valid.Add(c);
                    if (valid.Count == 4) break;
                }
            FavoriteShapes = valid;

            ColorScheme = (int)Systems.GuidePalette.Normalize(ColorScheme);

            OpacityBody = ClampAlpha(OpacityBody, 0.5f);

            OpacityLocked = ClampAlpha(OpacityLocked, 0.5f);
            OpacityApex = ClampAlpha(OpacityApex, 0.5f);
            OpacityAnchor = ClampAlpha(OpacityAnchor, 0.8f);
            OpacityGrabbed = ClampAlpha(OpacityGrabbed, 0.95f);
            OpacityHiddenAnchor = ClampAlpha(OpacityHiddenAnchor, 0.35f);
        }

        // NaN (a hand-edit gone wrong) fails every comparison, so it falls to the default too.
        private static float ClampAlpha(float v, float fallback)
        {
            if (float.IsNaN(v)) return fallback;
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
