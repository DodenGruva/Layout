using System;
using Layout.Guide;

namespace Layout.Systems
{
    /// <summary>
    /// The named guide colour schemes a player can pick on the settings page (F11, v0.4.8).
    /// </summary>
    /// <remarks>
    /// PINNED VALUES. These are persisted in <c>layout-client.json</c> as integers, so an existing number
    /// must never change meaning. Append new schemes; never reorder.
    ///
    /// The motivation is accessibility, not taste. In the default scheme locked points are RED and apex
    /// points are GREEN — different meanings, both control-point markers, routinely seen side by side, and
    /// red/green is exactly the pair deuteranopia and protanopia collapse (around 8% of men cannot tell them
    /// apart at all).
    /// </remarks>
    public enum GuidePaletteScheme
    {
        /// <summary>The colour language the mod shipped with.</summary>
        Default = 0,

        /// <summary>
        /// Built on the Okabe–Ito palette, whose eight colours are chosen to stay mutually distinguishable
        /// under deuteranopia and protanopia. Covers both, so there is no reason to offer them separately —
        /// the two palettes would differ only in ways neither group can see.
        /// </summary>
        RedGreenSafe = 1,

        // 2 was HighContrast, removed in v0.4.10 (human-directed). The number stays retired rather than
        // reused: a layout-client.json written by 0.4.8/0.4.9 can still say 2, and Normalize folds any
        // unknown value back to Default.

        /// <summary>
        /// The player's own colours, chosen per role from the settings page's swatch grid (v0.4.12).
        /// </summary>
        /// <remarks>
        /// The presets answer "I cannot TELL these roles apart". Custom answers the other, equally real
        /// reason to change guide colours: yellow disappears against sandstone, and no fixed list of presets
        /// will ever cover every material a player builds in. Switching to Custom seeds it from whichever
        /// preset was showing, so it is a starting point to adjust rather than a blank slate.
        /// </remarks>
        Custom = 3
    }

    /// <summary>
    /// One complete, IMMUTABLE set of guide voxel colours. <see cref="GuideMeshBuilder"/> holds a single
    /// reference to the active instance and swaps it wholesale when the player changes scheme or opacity.
    /// </summary>
    /// <remarks>
    /// WHY IMMUTABLE, AND WHY SWAPPED BY REFERENCE. The colours used to be static arrays mutated in place.
    /// Meshes build on background worker threads, so a palette change part-way through a large guide's
    /// materialization could be seen by some batches and not others — one guide wearing two palettes until
    /// the rebuild settled. Handing every build a reference it reads ONCE means a batch is always built
    /// entirely from one palette: it either sees the old instance or the new one, never a mixture. The
    /// reference swap is a single aligned write, so no lock is needed on either side.
    ///
    /// Arrays are RGBA in 0..1, in the layout the mesh builder wants. They are exposed directly rather than
    /// copied per read because the build loop touches them once per voxel and an eight-million-voxel guide
    /// cannot afford the allocation. Nothing writes to them after construction.
    /// </remarks>
    public sealed class GuidePalette
    {
        /// <summary>Normal guide body.</summary>
        public readonly float[] Body;
        /// <summary>A locked control point.</summary>
        public readonly float[] Locked;
        /// <summary>The primary / apex marker.</summary>
        public readonly float[] Apex;
        /// <summary>Public anchor, aligned foot.</summary>
        public readonly float[] Anchor;
        /// <summary>Public anchor, far foot — the same role in an off-shade.</summary>
        public readonly float[] AnchorFar;
        /// <summary>Private anchor, aligned foot.</summary>
        public readonly float[] PrivateAnchor;
        /// <summary>Private anchor, far foot.</summary>
        public readonly float[] PrivateAnchorFar;
        /// <summary>The control point currently held in a drag. White in every scheme — see remarks.</summary>
        public readonly float[] Grabbed;
        /// <summary>Equal-part division marks.</summary>
        public readonly float[] Division;
        /// <summary>A body voxel whose cell already holds world material (the chiseling highlight).</summary>
        public readonly float[] Built;
        /// <summary>Alpha a hidden guide's remaining anchor dots are drawn at.</summary>
        public readonly float HiddenAnchorAlpha;

        private GuidePalette(
            float[] body, float[] locked, float[] apex, float[] anchor, float[] anchorFar,
            float[] privateAnchor, float[] privateAnchorFar, float[] grabbed, float[] division,
            float[] built, float hiddenAnchorAlpha)
        {
            Body = body;
            Locked = locked;
            Apex = apex;
            Anchor = anchor;
            AnchorFar = anchorFar;
            PrivateAnchor = privateAnchor;
            PrivateAnchorFar = privateAnchorFar;
            Grabbed = grabbed;
            Division = division;
            Built = built;
            HiddenAnchorAlpha = hiddenAnchorAlpha;
        }

        /// <summary>Display names for the settings page, in the order the picker offers them.</summary>
        public static readonly string[] SchemeNames = { "Default", "Red-Green Safe", "Custom" };

        /// <summary>The scheme values matching <see cref="SchemeNames"/>, since 2 is a retired number.</summary>
        public static readonly int[] SchemeValues = { 0, 1, 3 };

        // ------------------------------------------------------------------------------------------
        //  Custom scheme: the roles a player may recolour
        // ------------------------------------------------------------------------------------------
        // PINNED ORDER — these are array indices in layout-client.json. Append only.
        public const int RoleBody = 0;
        public const int RoleLocked = 1;
        public const int RoleApex = 2;
        public const int RoleAnchor = 3;
        public const int RolePrivateAnchor = 4;
        public const int RoleDivision = 5;
        public const int RoleBuilt = 6;
        public const int RoleCount = 7;

        /// <summary>Full role names, used as the hover text on the settings page's role table.</summary>
        public static readonly string[] RoleNames =
        {
            "Guide body", "Locked point", "Apex point", "Anchor",
            "Private anchor", "Division mark", "Built (chiseling highlight)"
        };

        /// <summary>
        /// One-word labels printed under each swatch in the role table. Short enough to sit under a 22 px
        /// chip in a four-column grid without wrapping or colliding with its neighbour; the full name in
        /// <see cref="RoleNames"/> is one hover away.
        /// </summary>
        public static readonly string[] RoleShortNames =
        {
            "Body", "Locked", "Apex", "Anchor", "Private", "Division", "Built"
        };

        /// <summary>
        /// The swatch grid offered on the settings page. Sixteen colours rather than a free picker
        /// (human-directed): every one of these is legible at guide alpha over arbitrary stone, which
        /// "any colour at all" cannot promise — a mid-grey or a deep navy simply vanishes at 50% over rock.
        /// </summary>
        public static readonly float[][] Swatches =
        {
            new[] { 1.00f, 1.00f, 1.00f }, new[] { 0.72f, 0.75f, 0.78f },
            new[] { 1.00f, 0.85f, 0.10f }, new[] { 0.94f, 0.89f, 0.26f },
            new[] { 1.00f, 0.62f, 0.00f }, new[] { 1.00f, 0.45f, 0.05f },
            new[] { 0.84f, 0.37f, 0.00f }, new[] { 0.90f, 0.15f, 0.15f },
            new[] { 1.00f, 0.45f, 0.60f }, new[] { 0.80f, 0.47f, 0.65f },
            new[] { 0.90f, 0.20f, 0.90f }, new[] { 0.58f, 0.40f, 1.00f },
            new[] { 0.20f, 0.50f, 1.00f }, new[] { 0.34f, 0.71f, 0.91f },
            new[] { 0.10f, 0.95f, 0.95f }, new[] { 0.20f, 0.90f, 0.30f }
        };

        /// <summary>
        /// Builds the palette for <paramref name="scheme"/> at the player's configured opacities.
        /// </summary>
        /// <remarks>
        /// Hue and alpha are deliberately independent: the scheme picks WHICH colour a role is, the opacity
        /// sliders pick how solid it is, and changing one must never silently move the other.
        ///
        /// GRABBED IS WHITE IN EVERY SCHEME. It is not a role competing with the others for a hue — it is a
        /// momentary override on exactly one voxel saying "this is the point in your hand". White is the one
        /// value that reads that way against every palette, and no colour deficiency affects it.
        /// </remarks>
        public static GuidePalette Build(
            GuidePaletteScheme scheme,
            float body, float locked, float apex, float anchor, float grabbed, float hiddenAnchor,
            float[][] customRoles = null)
        {
            switch (scheme)
            {
                case GuidePaletteScheme.Custom:
                    // Anything the player has not set falls through to Default, so a short or damaged
                    // customColors array degrades one role at a time instead of losing the whole palette.
                    float[][] cr = customRoles;
                    float[] Role(int i, float[] fallback) =>
                        cr != null && i < cr.Length && cr[i] != null && cr[i].Length >= 3 ? cr[i] : fallback;

                    float[] cAnchor = Role(RoleAnchor, new[] { 0.20f, 0.50f, 1.00f });
                    float[] cPrivate = Role(RolePrivateAnchor, new[] { 1.00f, 0.45f, 0.05f });
                    return new GuidePalette(
                        body:   Rgb(Role(RoleBody, new[] { 1.00f, 0.85f, 0.10f }), body),
                        locked: Rgb(Role(RoleLocked, new[] { 0.90f, 0.15f, 0.15f }), locked),
                        apex:   Rgb(Role(RoleApex, new[] { 0.20f, 0.90f, 0.30f }), apex),
                        anchor: Rgb(cAnchor, anchor),
                        // The far foot is DERIVED, not picked. It is the same role in an off-shade, so it
                        // has to track whatever the player chose for the anchor; offering it as its own
                        // swatch would let the pair drift until the distinction stopped reading.
                        anchorFar:        Rgb(Lighten(cAnchor), anchor),
                        privateAnchor:    Rgb(cPrivate, anchor),
                        privateAnchorFar: Rgb(Lighten(cPrivate), anchor),
                        grabbed:          Rgba(1.00f, 1.00f, 1.00f, grabbed),
                        division:         Rgb(Role(RoleDivision, new[] { 0.90f, 0.20f, 0.90f }), anchor),
                        built:            Rgb(Role(RoleBuilt, new[] { 0.10f, 0.95f, 0.95f }), body),
                        hiddenAnchorAlpha: hiddenAnchor);

                case GuidePaletteScheme.RedGreenSafe:
                    // Okabe–Ito assignments. Locked takes vermillion and apex takes reddish purple: both
                    // still read as "warm marker" to normal vision, but they separate cleanly for deutan and
                    // protan viewers, which red/green never can. Anchors move to the palette's true blue and
                    // sky blue, private anchors to its orange pair, and division marks to bluish green — a
                    // hue nothing else in the scheme uses.
                    return new GuidePalette(
                        body:             Rgba(0.94f, 0.89f, 0.26f, body),      // #F0E442 yellow
                        locked:           Rgba(0.84f, 0.37f, 0.00f, locked),    // #D55E00 vermillion
                        apex:             Rgba(0.80f, 0.47f, 0.65f, apex),      // #CC79A7 reddish purple
                        anchor:           Rgba(0.00f, 0.45f, 0.70f, anchor),    // #0072B2 blue
                        anchorFar:        Rgba(0.34f, 0.71f, 0.91f, anchor),    // #56B4E9 sky blue
                        privateAnchor:    Rgba(0.90f, 0.62f, 0.00f, anchor),    // #E69F00 orange
                        privateAnchorFar: Rgba(0.65f, 0.42f, 0.00f, anchor),    // darker orange
                        grabbed:          Rgba(1.00f, 1.00f, 1.00f, grabbed),
                        division:         Rgba(0.00f, 0.62f, 0.45f, anchor),    // #009E73 bluish green
                        // Cyan would sit close to the sky-blue far anchor here, so the built highlight moves
                        // to white-cyan: it only ever appears ON the yellow body, where it still separates
                        // hard, and it can no longer be mistaken for an anchor foot.
                        built:            Rgba(0.65f, 1.00f, 1.00f, body),
                        hiddenAnchorAlpha: hiddenAnchor);

                default:
                    // The shipped colour language, unchanged. These RGB values are the ones every screenshot,
                    // playtest note and session record in this project refers to.
                    return new GuidePalette(
                        body:             Rgba(1.00f, 0.85f, 0.10f, body),
                        locked:           Rgba(0.90f, 0.15f, 0.15f, locked),
                        apex:             Rgba(0.20f, 0.90f, 0.30f, apex),
                        anchor:           Rgba(0.20f, 0.50f, 1.00f, anchor),
                        anchorFar:        Rgba(0.45f, 0.45f, 1.00f, anchor),
                        privateAnchor:    Rgba(1.00f, 0.45f, 0.05f, anchor),
                        privateAnchorFar: Rgba(0.90f, 0.28f, 0.05f, anchor),
                        grabbed:          Rgba(1.00f, 1.00f, 1.00f, grabbed),
                        division:         Rgba(0.90f, 0.20f, 0.90f, anchor),
                        built:            Rgba(0.10f, 0.95f, 0.95f, body),
                        hiddenAnchorAlpha: hiddenAnchor);
            }
        }

        /// <summary>Folds a hand-edited, retired or future scheme number back to the default.</summary>
        public static GuidePaletteScheme Normalize(int scheme) =>
            Array.IndexOf(SchemeValues, scheme) >= 0
                ? (GuidePaletteScheme)scheme
                : GuidePaletteScheme.Default;

        /// <summary>
        /// The per-role colours a named preset would draw with, used to SEED Custom when the player first
        /// switches to it — so they start from the palette they were just looking at, not from nothing.
        /// </summary>
        public static float[][] RoleColorsOf(GuidePaletteScheme scheme)
        {
            // Alphas are irrelevant here; build at 1 and read the RGB back out.
            GuidePalette p = Build(scheme == GuidePaletteScheme.Custom ? GuidePaletteScheme.Default : scheme,
                1f, 1f, 1f, 1f, 1f, 1f);
            var roles = new float[RoleCount][];
            roles[RoleBody] = Rgb3(p.Body);
            roles[RoleLocked] = Rgb3(p.Locked);
            roles[RoleApex] = Rgb3(p.Apex);
            roles[RoleAnchor] = Rgb3(p.Anchor);
            roles[RolePrivateAnchor] = Rgb3(p.PrivateAnchor);
            roles[RoleDivision] = Rgb3(p.Division);
            roles[RoleBuilt] = Rgb3(p.Built);
            return roles;
        }

        /// <summary>
        /// Reads the config's "#RRGGBB" strings into role RGB triples. An entry that is missing, blank or
        /// malformed comes back null, which <see cref="Build"/> then fills from the Default palette — one bad
        /// hand-edit costs one role, never the whole scheme.
        /// </summary>
        public static float[][] ParseRoleColors(string[] hex)
        {
            if (hex == null) return null;
            var roles = new float[RoleCount][];
            for (int i = 0; i < RoleCount && i < hex.Length; i++)
            {
                string s = hex[i]?.Trim().TrimStart('#');
                if (string.IsNullOrEmpty(s) || s.Length != 6) continue;
                if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out int v)) continue;
                roles[i] = new[] { ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f };
            }
            return roles;
        }

        /// <summary>Writes role RGB triples back out as "#RRGGBB" for the config file.</summary>
        public static string[] FormatRoleColors(float[][] roles)
        {
            var hex = new string[RoleCount];
            for (int i = 0; i < RoleCount; i++)
            {
                float[] c = roles != null && i < roles.Length ? roles[i] : null;
                hex[i] = c == null || c.Length < 3 ? null : ToHex(c);
            }
            return hex;
        }

        /// <summary>One RGB triple as "#RRGGBB".</summary>
        public static string ToHex(float[] rgb) => string.Format(
            System.Globalization.CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
            Channel(rgb[0]), Channel(rgb[1]), Channel(rgb[2]));

        private static int Channel(float v) =>
            Math.Min(255, Math.Max(0, (int)Math.Round(v * 255f)));

        private static float[] Rgb3(float[] rgba) => new[] { rgba[0], rgba[1], rgba[2] };

        private static float[] Rgba(float r, float g, float b, float a) => new[] { r, g, b, a };

        private static float[] Rgb(float[] rgb, float a) => new[] { rgb[0], rgb[1], rgb[2], a };

        // The far-foot off-shade: a quarter of the way to white. Enough to read as "the same colour, other
        // end" at a glance without becoming a different hue.
        private static float[] Lighten(float[] rgb) => new[]
        {
            rgb[0] + (1f - rgb[0]) * 0.28f,
            rgb[1] + (1f - rgb[1]) * 0.28f,
            rgb[2] + (1f - rgb[2]) * 0.28f
        };

        /// <summary>The colour for a voxel role, before the anchor far-foot and privacy off-shades.</summary>
        public float[] ForType(VoxelRenderType type)
        {
            switch (type)
            {
                case VoxelRenderType.Locked:   return Locked;
                case VoxelRenderType.Primary:  return Apex;
                case VoxelRenderType.Anchor:   return Anchor;
                case VoxelRenderType.Grabbed:  return Grabbed;
                case VoxelRenderType.Division: return Division;
                default:                       return Body;   // Normal
            }
        }
    }
}
