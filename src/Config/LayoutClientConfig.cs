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

        /// <summary>The shape the tool starts on: 0 = Arch, 1 = Ellipse (pinned GuideShapeType values).</summary>
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
        [JsonProperty("favoriteShapes")]
        public List<string> FavoriteShapes { get; set; } =
            new List<string> { "arch", "halfcircle", "circle", "line" };

        // ------------------------------------------------------------------------------------------
        //  Guide opacities (Session-8, item 2): every voxel-type alpha is a client visual preference,
        //  hand-tunable here without a rebuild. 0 = invisible, 1 = solid. Applied once at client start
        //  (edit the file, restart the client). Defaults per the Session-8 decision: body and the point
        //  markers down to 0.5; anchors and the White grabbed highlight left where they were.
        // ------------------------------------------------------------------------------------------

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

        /// <summary>Folds out-of-range values (e.g. from a hand-edited file) back to safe defaults.</summary>
        public void Normalize()
        {
            if (!GuideData.IsValidVoxelScale(DefaultScale)) DefaultScale = 1;
            if (DefaultProjection != (int)ProjectionMode.Volumetric &&
                DefaultProjection != (int)ProjectionMode.Surface)
                DefaultProjection = (int)ProjectionMode.Volumetric;

            if (DefaultShape < (int)GuideShapeType.Arch || DefaultShape > (int)GuideShapeType.Sphere)
                DefaultShape = (int)GuideShapeType.Arch;
            if (!Systems.DraftManager.IsValidPair((GuideShapeType)DefaultShape, (ShapeConstraint)DefaultConstraint))
                DefaultConstraint = (int)ShapeConstraint.None;
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
