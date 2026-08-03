using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace Layout.UI
{
    /// <summary>
    /// Vector (Cairo) glyphs for the tool GUI's icon tiles (UI refinement pass). Each shape/toggle/plane
    /// option is drawn as simple line-art that communicates its meaning, replacing the old text tiles.
    /// </summary>
    /// <remarks>
    /// HOW IT PLUGS IN. Vintage Story lets a mod register named custom icons in
    /// <c>capi.Gui.Icons.CustomIcons</c> (a <c>Dictionary&lt;string, IconRendererDelegate&gt;</c>). Any stock
    /// <c>GuiElementToggleButton</c> constructed with an <c>icon</c> string then renders that glyph via
    /// <c>IconUtil.DrawIcon</c>, which checks CustomIcons first. So the GUI keeps using the exact same
    /// exclusive-toggle machinery it already had (keys, relight, inert-guard) — only the tile's face changes
    /// from text to an icon. Registration is idempotent and done once at client start.
    ///
    /// DRAWING MODEL. The delegate hands us the icon's pixel box (x, y, w, h) and a colour (rgba, 0..1). We
    /// map a fixed design box (76 units for shapes, 60 for the smaller toggles) uniformly into that pixel box
    /// — centred, aspect-preserved — so circles stay circular even on non-square tiles. Everything is stroked
    /// (or filled) in the supplied colour, so the icon inherits the button's normal/hover/pressed tint for
    /// free.
    /// </remarks>
    public static class LayoutToolIcons
    {
        // ---- Icon names (the keys registered in CustomIcons; referenced from GuideToolGui) ----------
        public const string Arch = "layout-arch";
        public const string HalfCircle = "layout-halfcircle";
        public const string Circle = "layout-circle";
        public const string Ellipse = "layout-ellipse";
        public const string Line = "layout-line";
        public const string Triangle = "layout-triangle";
        public const string RightTri = "layout-righttri";
        public const string Equilateral = "layout-equilateral";
        public const string Isosceles = "layout-isosceles";
        public const string Rectangle = "layout-rectangle";
        public const string Square = "layout-square";
        public const string Polygon = "layout-polygon";
        public const string FreeShapeIcon = "layout-freeshape";
        public const string Roundover = "layout-roundover";
        public const string Sphere = "layout-sphere";
        public const string Dome = "layout-dome";
        public const string Cylinder = "layout-cylinder";
        public const string TaperedCylinder = "layout-taperedcylinder";
        public const string PolygonalPrism = "layout-polygonalprism";
        public const string TaperedPolygonalPrism = "layout-taperedpolygonalprism";
        public const string Cone = "layout-cone";
        public const string Box = "layout-box";

        /// <summary>The shape picker's expand/collapse tile (Session 11): ▾ closed, ▴ open.</summary>
        public const string ExpandDown = "layout-expand-down";
        public const string ExpandUp = "layout-expand-up";

        /// <summary>An empty favorite slot (0.1.15): a faint placeholder square.</summary>
        public const string EmptySlot = "layout-empty-slot";

        /// <summary>Settings gear — the title-bar button that swaps the panel to its settings page.</summary>
        public const string Gear = "layout-gear";

        /// <summary>
        /// Suffix variants registered for every shape glyph (0.1.15): "&lt;name&gt;-star" draws the glyph
        /// with a small ★ badge (a pinned favorite in the catalog); "&lt;name&gt;-current" draws it in the
        /// guide-body YELLOW regardless of button tint (the always-lit current-shape chip). 0.1.16 adds
        /// "&lt;name&gt;-ghost" for EVERY layout glyph — the same drawing at a fraction of its alpha, the
        /// visual for Delete-mode disabled tiles (the stock button's Enabled=false dims its chrome but not
        /// a custom icon face, which is why the tiles didn't read as greyed).
        /// </summary>
        public const string StarSuffix = "-star";
        public const string CurrentSuffix = "-current";
        public const string GhostSuffix = "-ghost";
        public const string PausedSuffix = "-paused";

        public const string ModeCreate = "layout-mode-create";
        public const string ModeEdit = "layout-mode-edit";
        public const string ModeTransform = "layout-mode-transform";
        public const string ModeDelete = "layout-mode-delete";

        // F6 Move pad. Away/Toward/Left/Right are the four HORIZONTAL directions, read relative to the
        // player's facing; Up/Down are vertical and wear a ground bar so they cannot be mistaken for the
        // plain arrows beside them.
        public const string MoveAway = "layout-move-away";
        public const string MoveToward = "layout-move-toward";
        public const string MoveLeft = "layout-move-left";
        public const string MoveRight = "layout-move-right";
        public const string MoveUp = "layout-move-up";
        public const string MoveDown = "layout-move-down";
        public const string MoveGround = "layout-move-ground";
        public const string MoveFree = "layout-move-free";

        // F12 Rotate, in the four corners of the Move pad. The SPIN pair (top corners) turns the guide about
        // the vertical axis and is drawn as a flattened ellipse — a turntable seen at an angle. The TIP pair
        // (bottom corners) turns it about the horizontal axis pointing away from the player and is drawn as
        // an upright circle. Same silhouette family, different plane, so the two pairs read as siblings.
        public const string RotateSpinLeft = "layout-rotate-spin-left";
        public const string RotateSpinRight = "layout-rotate-spin-right";
        public const string RotateTipLeft = "layout-rotate-tip-left";
        public const string RotateTipRight = "layout-rotate-tip-right";

        // F7/F8 Transform action row — what the direction pad DOES. These LATCH, unlike the pad tiles.
        public const string ActionMove = "layout-action-move";
        public const string ActionCopy = "layout-action-copy";
        public const string ActionMirror = "layout-action-mirror";
        public const string ProjVolumetric = "layout-proj-vol";
        public const string ProjSurface = "layout-proj-surf";
        public const string FillHollow = "layout-fill-hollow";
        public const string FillFilled = "layout-fill-filled";
        public const string FormShell = "layout-form-shell";
        public const string FormWireframe = "layout-form-wireframe";
        public const string VisShown = "layout-vis-shown";
        public const string VisHidden = "layout-vis-hidden";
        /// <summary>Reveal hidden guides within reach: an eye over a short measured run of blocks.</summary>
        public const string RevealNear = "layout-reveal-near";
        /// <summary>Reveal all of your own hidden guides: an eye casting rays outward.</summary>
        public const string RevealAll = "layout-reveal-all";

        public const string PlaneAuto = "layout-plane-auto";
        public const string PlaneFloor = "layout-plane-floor";
        public const string PlaneNS = "layout-plane-ns";
        public const string PlaneEW = "layout-plane-ew";

        // Scale = how many 1/16-block units the voxel edge spans, shown as that many tiny squares filling a
        // 4x4 grid (1/16 -> one dot ... 16/16 -> the full block).
        public const string Scale1 = "layout-scale-1";
        public const string Scale2 = "layout-scale-2";
        public const string Scale4 = "layout-scale-4";
        public const string Scale8 = "layout-scale-8";
        public const string Scale16 = "layout-scale-16";

        /// <summary>
        /// Registers every Layout glyph into the client's live icon dictionary. Idempotent (overwrites the
        /// same keys), and called on EVERY client start.
        /// </summary>
        /// <remarks>
        /// B-24-1 fix (v0.1.24): this used to short-circuit on a process-STATIC <c>_registered</c> flag.
        /// That flag outlived the client — so after exit-to-title → re-enter (a fresh client API with a
        /// brand-new, EMPTY <c>capi.Gui.Icons.CustomIcons</c> dictionary), the guard was still true and the
        /// glyphs were never registered into the new dictionary, leaving the GUI tiles blank. Gating on the
        /// LIVE dictionary instead (skip only if our sentinel is already present) makes it correct across
        /// re-inits; registration is just cheap dictionary writes.
        /// </remarks>
        public static void EnsureRegistered(ICoreClientAPI capi)
        {
            var reg = capi?.Gui?.Icons?.CustomIcons;
            if (reg == null || reg.ContainsKey(Arch)) return;   // already registered in THIS dictionary

            reg[Arch] = DrawArch;
            reg[HalfCircle] = DrawHalfCircle;
            reg[Circle] = DrawCircle;
            reg[Ellipse] = DrawEllipse;
            reg[Line] = DrawLine;
            reg[Triangle] = DrawTriangle;
            reg[RightTri] = DrawRightTri;
            reg[Equilateral] = DrawEquilateral;
            reg[Isosceles] = DrawIsosceles;
            reg[Rectangle] = DrawRectangle;
            reg[Square] = DrawSquare;
            reg[Polygon] = DrawPolygon;
            reg[FreeShapeIcon] = DrawFreeShape;
            reg[Roundover] = DrawRoundover;
            reg[Sphere] = DrawSphere;
            reg[Dome] = DrawDome;
            reg[Cylinder] = DrawCylinder;
            reg[TaperedCylinder] = DrawTaperedCylinder;
            reg[PolygonalPrism] = DrawPolygonalPrism;
            reg[TaperedPolygonalPrism] = DrawTaperedPolygonalPrism;
            reg[Cone] = DrawCone;
            reg[Box] = DrawBox;
            reg[ExpandDown] = (ctx, x, y, w, h, rgba) => DrawExpandChevron(ctx, x, y, w, h, rgba, down: true);
            reg[ExpandUp] = (ctx, x, y, w, h, rgba) => DrawExpandChevron(ctx, x, y, w, h, rgba, down: false);
            reg[EmptySlot] = DrawEmptySlot;
            reg[Gear] = DrawGear;

            // 0.1.15: per-shape "-star" (pinned badge) and "-current" (always-yellow chip) variants.
            foreach (string shapeName in new[]
            {
                Arch, HalfCircle, Circle, Ellipse, Line, Triangle, RightTri, Equilateral,
                Isosceles, Rectangle, Square, Polygon, FreeShapeIcon, Roundover, Sphere, Dome, Cylinder,
                TaperedCylinder, PolygonalPrism, TaperedPolygonalPrism, Cone, Box
            })
            {
                var baseDrawer = reg[shapeName];
                reg[shapeName + StarSuffix] = (ctx, x, y, w, h, rgba) =>
                {
                    baseDrawer(ctx, x, y, w, h, rgba);
                    DrawStarBadge(ctx, x, y, w, h, rgba);
                };
                reg[shapeName + CurrentSuffix] = (ctx, x, y, w, h, rgba) =>
                {
                    // Guide-body yellow, dimming with whatever alpha the button passes (disabled states).
                    double a = rgba != null && rgba.Length >= 4 ? rgba[3] : 1.0;
                    baseDrawer(ctx, x, y, w, h, new[] { 1.0, 0.88, 0.15, a });
                };
                reg[shapeName + CurrentSuffix + PausedSuffix] = (ctx, x, y, w, h, rgba) =>
                {
                    // The comatose-draft HUD must desaturate the actual glyph, not merely its button chrome.
                    double a = rgba != null && rgba.Length >= 4 ? rgba[3] * 0.24 : 0.24;
                    baseDrawer(ctx, x, y, w, h, new[] { 0.62, 0.62, 0.62, a });
                };
            }

            reg[ModeCreate] = DrawModeCreate;
            reg[ModeEdit] = DrawModeEdit;
            reg[ModeTransform] = DrawModeTransform;
            reg[ModeDelete] = DrawModeDelete;

            reg[ActionMove] = DrawActionMove;
            reg[ActionCopy] = DrawActionCopy;
            reg[ActionMirror] = DrawActionMirror;

            reg[RotateSpinLeft] = (ctx, x, y, w, h, rgba) => DrawRotate(ctx, x, y, w, h, rgba, false, false);
            reg[RotateSpinRight] = (ctx, x, y, w, h, rgba) => DrawRotate(ctx, x, y, w, h, rgba, false, true);
            reg[RotateTipLeft] = (ctx, x, y, w, h, rgba) => DrawRotate(ctx, x, y, w, h, rgba, true, false);
            reg[RotateTipRight] = (ctx, x, y, w, h, rgba) => DrawRotate(ctx, x, y, w, h, rgba, true, true);

            reg[MoveAway] = (ctx, x, y, w, h, rgba) => DrawMoveArrow(ctx, x, y, w, h, rgba, 0, false);
            reg[MoveRight] = (ctx, x, y, w, h, rgba) => DrawMoveArrow(ctx, x, y, w, h, rgba, Math.PI / 2, false);
            reg[MoveToward] = (ctx, x, y, w, h, rgba) => DrawMoveArrow(ctx, x, y, w, h, rgba, Math.PI, false);
            reg[MoveLeft] = (ctx, x, y, w, h, rgba) => DrawMoveArrow(ctx, x, y, w, h, rgba, -Math.PI / 2, false);
            reg[MoveUp] = (ctx, x, y, w, h, rgba) => DrawMoveArrow(ctx, x, y, w, h, rgba, 0, true);
            reg[MoveDown] = (ctx, x, y, w, h, rgba) => DrawMoveArrow(ctx, x, y, w, h, rgba, Math.PI, true);
            reg[MoveGround] = DrawMoveGround;
            reg[MoveFree] = DrawMoveFree;
            reg[ProjVolumetric] = DrawProjVolumetric;
            reg[ProjSurface] = DrawProjSurface;
            reg[FillHollow] = DrawFillHollow;
            reg[FillFilled] = DrawFillFilled;
            reg[FormShell] = DrawFormShell;
            reg[FormWireframe] = DrawFormWireframe;
            reg[VisShown] = DrawVisShown;
            reg[VisHidden] = DrawVisHidden;
            reg[RevealNear] = DrawRevealNear;
            reg[RevealAll] = DrawRevealAll;

            reg[PlaneAuto] = DrawPlaneAuto;
            reg[PlaneFloor] = DrawPlaneFloor;
            reg[PlaneNS] = DrawPlaneNS;
            reg[PlaneEW] = DrawPlaneEW;

            // Scale S -> an SxS grid of squares: the NxN icon IS the voxel count, exactly like the game's
            // native icons (1x1 the smallest ... 16x16 a full block). The full block draws as ONE solid
            // square filling the button rather than an unreadable 16x16 mesh (human-requested).
            reg[Scale1] = (ctx, x, y, w, h, rgba) => DrawScaleGrid(ctx, x, y, w, h, rgba, 1);
            reg[Scale2] = (ctx, x, y, w, h, rgba) => DrawScaleGrid(ctx, x, y, w, h, rgba, 2);
            reg[Scale4] = (ctx, x, y, w, h, rgba) => DrawScaleGrid(ctx, x, y, w, h, rgba, 4);
            reg[Scale8] = (ctx, x, y, w, h, rgba) => DrawScaleGrid(ctx, x, y, w, h, rgba, 8);
            reg[Scale16] = DrawScaleFullBlock;

            // 0.1.16: a "-ghost" variant of EVERY layout glyph registered above (shapes, star/current
            // variants, toggles, planes, scale grids, chevrons) — the same drawing at ~a quarter alpha,
            // used by every disabled tile so Delete mode reads properly greyed (the stock button's
            // Enabled=false dims its chrome but not a custom icon face). MUST run last so it wraps the
            // full set; filtered to our prefix (CustomIcons is a shared, cross-mod dictionary).
            foreach (string name in new List<string>(reg.Keys))
            {
                if (!name.StartsWith("layout-") || name.EndsWith(GhostSuffix)
                    || name.EndsWith(PausedSuffix)) continue;
                var baseDrawer = reg[name];
                reg[name + GhostSuffix] = (ctx, x, y, w, h, rgba) =>
                {
                    double[] dim = rgba != null && rgba.Length >= 4
                        ? new[] { rgba[0], rgba[1], rgba[2], rgba[3] * 0.25 }
                        : new[] { 1.0, 1.0, 1.0, 0.25 };
                    baseDrawer(ctx, x, y, w, h, dim);
                };
            }
        }

        // ============================ drawing helpers ============================

        // Uniformly maps a BxB design box into the pixel box (x,y,w,h), centred and aspect-preserved.
        private sealed class Canvas
        {
            private readonly double _s, _ox, _oy;
            // zoom > 1 enlarges the glyph within its tile (fuller, closer to the game's native icon weight).
            // Safe because our glyphs are drawn inset from the design-box edges.
            public Canvas(int x, int y, float w, float h, double box, double zoom = 1.12)
            {
                _s = Math.Min(w, h) / box * zoom;
                _ox = x + (w - box * _s) / 2.0;
                _oy = y + (h - box * _s) / 2.0;
            }
            public double X(double u) => _ox + u * _s;   // design-x -> pixel-x
            public double Y(double v) => _oy + v * _s;   // design-y -> pixel-y (down)
            public double L(double d) => d * _s;         // design length -> pixels
        }

        private static void Pen(Context ctx, double[] rgba, double width)
        {
            ctx.LineWidth = Math.Max(1.4, width);
            ctx.LineCap = LineCap.Round;
            ctx.LineJoin = LineJoin.Round;
            SetColor(ctx, rgba, 1.0);
        }

        private static void SetColor(Context ctx, double[] rgba, double alphaMul)
        {
            if (rgba != null && rgba.Length >= 4) ctx.SetSourceRGBA(rgba[0], rgba[1], rgba[2], rgba[3] * alphaMul);
            else ctx.SetSourceRGBA(1, 1, 1, alphaMul);
        }

        // ============================ shape glyphs (box = 76) ============================

        private static void DrawArch(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            // The real Catmull-Rom arch reads as the upper half of an ellipse (a smooth dome), NOT a
            // legged archway — so the icon is an open elliptical arc, wider than tall, open at the bottom.
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            double cx = c.X(38), cy = c.Y(54), rx = c.L(29), ry = c.L(27);
            ctx.Save();
            ctx.Translate(cx, cy);
            ctx.Scale(rx, ry);
            ctx.Arc(0, 0, 1, Math.PI, 2 * Math.PI);   // upper half of the ellipse
            ctx.Restore();
            ctx.Stroke();                               // open — no baseline, no legs
        }

        private static void DrawHalfCircle(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            double cx = c.X(38), cy = c.Y(46), rx = c.L(22), ry = c.L(22);
            ctx.Save();
            ctx.Translate(cx, cy);
            ctx.Scale(rx, ry);
            ctx.Arc(0, 0, 1, Math.PI, 2 * Math.PI);   // upper dome (device y-down)
            ctx.Restore();
            ctx.LineTo(cx - rx, cy);                    // close the diameter
            ctx.ClosePath();
            ctx.Stroke();
        }

        private static void DrawCircle(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            ctx.Arc(c.X(38), c.Y(37), c.L(22), 0, 2 * Math.PI);
            ctx.Stroke();
        }

        private static void DrawEllipse(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            ctx.Save();
            ctx.Translate(c.X(38), c.Y(37));
            ctx.Scale(c.L(24), c.L(15));
            ctx.Arc(0, 0, 1, 0, 2 * Math.PI);
            ctx.Restore();
            ctx.Stroke();
        }

        private static void DrawLine(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            ctx.MoveTo(c.X(17), c.Y(57));
            ctx.LineTo(c.X(59), c.Y(17));
            ctx.Stroke();
            // anchor dots
            SetColor(ctx, rgba, 1.0);
            ctx.Arc(c.X(17), c.Y(57), c.L(3.4), 0, 2 * Math.PI); ctx.Fill();
            ctx.Arc(c.X(59), c.Y(17), c.L(3.4), 0, 2 * Math.PI); ctx.Fill();
        }

        private static void Poly(Context ctx, Canvas c, double[] rgba, bool close, params double[] uv)
        {
            ctx.MoveTo(c.X(uv[0]), c.Y(uv[1]));
            for (int i = 2; i + 1 < uv.Length; i += 2) ctx.LineTo(c.X(uv[i]), c.Y(uv[i + 1]));
            if (close) ctx.ClosePath();
            ctx.Stroke();
        }

        private static void DrawTriangle(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            Poly(ctx, c, rgba, true, 16, 58, 60, 51, 31, 16);   // scalene
        }

        private static void DrawRightTri(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            Poly(ctx, c, rgba, true, 20, 56, 60, 56, 20, 18);
            Poly(ctx, c, rgba, false, 20, 49, 27, 49, 27, 56);   // right-angle mark
        }

        private static void DrawEquilateral(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            Poly(ctx, c, rgba, true, 38, 16, 18, 56, 58, 56);
            // equal-side ticks (one per side)
            Pen(ctx, rgba, c.L(2.0));
            Poly(ctx, c, rgba, false, 26.5, 38.5, 30.5, 36.5);
            Poly(ctx, c, rgba, false, 45.5, 36.5, 49.5, 38.5);
            Poly(ctx, c, rgba, false, 36, 58, 40, 58);
        }

        private static void DrawIsosceles(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            Poly(ctx, c, rgba, true, 38, 15, 25, 57, 51, 57);
            Pen(ctx, rgba, c.L(2.0));
            Poly(ctx, c, rgba, false, 29.5, 37, 33.5, 35.5);      // ticks on the two equal legs
            Poly(ctx, c, rgba, false, 42.5, 35.5, 46.5, 37);
        }

        private static void DrawRectangle(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            ctx.Rectangle(c.X(14), c.Y(25), c.L(48), c.L(26));
            ctx.Stroke();
        }

        private static void DrawSquare(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            ctx.Rectangle(c.X(19), c.Y(18), c.L(38), c.L(38));
            ctx.Stroke();
        }

        private static void DrawPolygon(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            // A regular pentagon, point-up — reads as "N-gon" at tile size (a hexagon reads as a cell).
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            const int n = 5;
            double cx = 38, cy = 38.5, r = 23;
            for (int k = 0; k <= n; k++)
            {
                double ang = -Math.PI / 2 + 2 * Math.PI * k / n;      // vertex 0 at the top
                double px = cx + r * Math.Cos(ang), py = cy + r * Math.Sin(ang);
                if (k == 0) ctx.MoveTo(c.X(px), c.Y(py)); else ctx.LineTo(c.X(px), c.Y(py));
            }
            ctx.ClosePath();
            ctx.Stroke();
        }

        // The shape picker's expand tile: a bold chevron (▾ collapsed / ▴ expanded).
        /// <summary>
        /// Settings gear (redrawn v0.4.9, refined v0.4.10, F10): a spoked wheel-gear, drawn from the human's
        /// reference image — a rim of eight trapezoidal teeth with rounded valleys, six spokes, a bored hub,
        /// and six large open segments between. The SPOKED WHEEL is the point of the design (human-directed);
        /// the teeth are the frame around it, so they are deliberately few and the strokes deliberately light.
        /// </summary>
        /// <remarks>
        /// FILLED, NOT STROKED, and that is the whole trick. The original drew eight radial spokes around a
        /// ring because detail on a STROKED outline becomes a grey smudge at icon sizes — each tooth would be
        /// a two-pixel box drawn with a two-pixel pen. As solid silhouette the same teeth are bumps on the
        /// edge of a disc, and an edge bump survives at two pixels where an outlined box does not.
        ///
        /// Every hole is cut with the EVEN-ODD fill rule rather than painted in a background colour, so the
        /// icon stays correct on any button state or backdrop.
        ///
        /// THREE SEPARATE FILL PASSES, and that is not an accident. Ring, spokes and hub overlap each other,
        /// and under a single even-odd path every overlap would CANCEL — the spokes would punch holes through
        /// the hub instead of joining it. Filling each part on its own path means an overlap simply paints
        /// the same pixels twice, which is what "these pieces are one solid object" needs to look like.
        ///
        /// SIZE. Rendered and inspected at 18/22/28/40 px before it shipped. The spokes and the bore do not
        /// survive 18 px — the spoke gaps close and the centre fills in — which is why the title bar draws
        /// this at 22 (see GEAR_SIZE in GuideToolGui). Radii are bounded by the Canvas zoom: design box 60 at
        /// 1.12 zoom means anything past about 26.8 from the centre falls outside the tile, so the tips sit
        /// at 25.5. Do not raise them without lowering the zoom.
        /// </remarks>
        private static void DrawGear(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            SetColor(ctx, rgba, 1.0);

            const double cx = 30, cy = 30;
            const int teeth = 8, spokes = 6;
            // TALLER TEETH in v0.4.28 (human-directed). The height came out of the ROOT, not the tip: the
            // tips already sit as far out as the Canvas zoom allows (see the size note above), so raising
            // them would have pushed the silhouette outside the tile. Dropping the root from 21.5 to 19.5
            // takes the tooth from 4.0 to 6.0 — half again as tall — and rRimIn follows it down so the rim
            // keeps its 3.5-ish thickness instead of thinning to a wire that breaks up at 22 px.
            const double rTip = 25.5;       // tooth tips — the outer silhouette
            const double rRoot = 19.5;      // tooth roots = outer edge of the rim
            const double rRimIn = 16.0;     // inner edge of the rim
            const double rHub = 7.6;        // hub boss
            const double rBore = 4.6;       // the hole through the middle
            const double spokeHalf = 1.9;

            // ---- 1. the toothed rim: the outer outline, with the rim's bore taken out of it ----
            double pitch = Math.PI * 2 / teeth;
            // TAPER. 0.22 originally; 0.19 was tried in v0.4.28 and the human could not see the difference,
            // which was fair — at r = 25.5 it moved the tip chord by under a pixel at 42 px. 0.13 against a
            // root of 0.32 makes the tip well under half the root's width, so the flanks visibly rake in
            // over the tooth's whole height instead of rising straight. Do not go much below this: the tip
            // chord is then about 4 design units, which is roughly one pixel at 18 px, and the teeth start
            // to come to nothing at the smallest size the glyph is drawn.
            double tipHalf = pitch * 0.13;           // narrower at the tip than at the root, so the flanks
            double rootHalf = pitch * 0.32;          // taper like a cast tooth rather than being square

            // HALF A TOOTH OF PHASE (human-directed, v0.4.10): puts a VALLEY at twelve and six o'clock
            // instead of a tooth. With eight teeth that lands valleys on all four cardinals, so the glyph
            // reads square to the title bar rather than tilted. The spokes are deliberately NOT rotated to
            // match — six spokes cannot align with eight teeth at any phase, and rotating them 30 degrees
            // aims a spoke straight into the top valley, which looks like a mistake.
            double phase = pitch * 0.5;

            ctx.NewPath();
            ctx.FillRule = FillRule.EvenOdd;
            for (int i = 0; i < teeth; i++)
            {
                double a = i * pitch + phase;
                GearVertex(ctx, c, cx, cy, rRoot, a - rootHalf, moveTo: i == 0);
                GearVertex(ctx, c, cx, cy, rTip, a - tipHalf, moveTo: false);
                GearVertex(ctx, c, cx, cy, rTip, a + tipHalf, moveTo: false);
                GearVertex(ctx, c, cx, cy, rRoot, a + rootHalf, moveTo: false);
                // The valley is a genuine arc of the root circle, so the gaps between teeth are round like a
                // cast gear rather than flat-bottomed. It ends exactly where the next tooth's root begins.
                ctx.Arc(c.X(cx), c.Y(cy), c.L(rRoot), a + rootHalf, a + pitch - rootHalf);
            }
            ctx.ClosePath();
            GearCircle(ctx, c, cx, cy, rRimIn);
            ctx.Fill();

            // ---- 2. the spokes ----
            // Winding, not even-odd: all six are wound the same way, so where they meet at the centre they
            // merge instead of cancelling. They start inside the hub and end inside the rim so both joints
            // are overlaps rather than seams that could open up a pixel gap.
            ctx.NewPath();
            ctx.FillRule = FillRule.Winding;
            for (int i = 0; i < spokes; i++)
            {
                double a = i * Math.PI * 2 / spokes;
                double ca = Math.Cos(a), sa = Math.Sin(a);
                SpokeCorner(ctx, c, cx, cy, ca, sa, rBore + 0.5, -spokeHalf, moveTo: true);
                SpokeCorner(ctx, c, cx, cy, ca, sa, rRimIn + 0.6, -spokeHalf, moveTo: false);
                SpokeCorner(ctx, c, cx, cy, ca, sa, rRimIn + 0.6, spokeHalf, moveTo: false);
                SpokeCorner(ctx, c, cx, cy, ca, sa, rBore + 0.5, spokeHalf, moveTo: false);
                ctx.ClosePath();
            }
            ctx.Fill();

            // ---- 3. the hub, with the bore through it ----
            ctx.NewPath();
            ctx.FillRule = FillRule.EvenOdd;
            GearCircle(ctx, c, cx, cy, rHub);
            GearCircle(ctx, c, cx, cy, rBore);
            ctx.Fill();
        }

        private static void GearVertex(
            Context ctx, Canvas c, double cx, double cy, double r, double a, bool moveTo)
        {
            double px = c.X(cx + Math.Cos(a) * r);
            double py = c.Y(cy + Math.Sin(a) * r);
            if (moveTo) ctx.MoveTo(px, py); else ctx.LineTo(px, py);
        }

        // A full circle as its own subpath. The MoveTo (rather than NewSubPath) puts the current point on the
        // arc's own start, so the lead-in line Cairo inserts has zero length and cannot streak across the face.
        private static void GearCircle(Context ctx, Canvas c, double cx, double cy, double r)
        {
            ctx.MoveTo(c.X(cx + r), c.Y(cy));
            ctx.Arc(c.X(cx), c.Y(cy), c.L(r), 0, Math.PI * 2);
        }

        // A spoke corner, given the spoke's axis: u runs along it from the centre, v across it.
        private static void SpokeCorner(
            Context ctx, Canvas c, double cx, double cy, double ca, double sa,
            double u, double v, bool moveTo)
        {
            double px = c.X(cx + u * ca - v * sa);
            double py = c.Y(cy + u * sa + v * ca);
            if (moveTo) ctx.MoveTo(px, py); else ctx.LineTo(px, py);
        }

        private static void DrawExpandChevron(Context ctx, int x, int y, float w, float h, double[] rgba, bool down)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(3.4));
            double yTip = down ? 38 : 22, yBase = down ? 22 : 38;
            ctx.MoveTo(c.X(17), c.Y(yBase));
            ctx.LineTo(c.X(30), c.Y(yTip));
            ctx.LineTo(c.X(43), c.Y(yBase));
            ctx.Stroke();
        }

        // The Free-Shape (0.1.15): an irregular closed pentagon-ish outline — clearly hand-drawn, not
        // any of the regular primitives; corner dots hint that every corner is a clicked anchor.
        private static void DrawFreeShape(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            double[] uv = { 20, 24, 52, 15, 61, 40, 40, 60, 15, 50 };
            ctx.MoveTo(c.X(uv[0]), c.Y(uv[1]));
            for (int i = 2; i + 1 < uv.Length; i += 2) ctx.LineTo(c.X(uv[i]), c.Y(uv[i + 1]));
            ctx.ClosePath();
            ctx.Stroke();
            SetColor(ctx, rgba, 1.0);
            for (int i = 0; i + 1 < uv.Length; i += 2)
            {
                ctx.Arc(c.X(uv[i]), c.Y(uv[i + 1]), c.L(3.0), 0, 2 * Math.PI);
                ctx.Fill();
            }
        }

        // A quarter-round ribbon following a bent route. Three nested rails make the rounded cross-section
        // legible at tile size; the route itself turns without a mitred point.
        private static void DrawRoundover(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.4));
            void Rail(double inset)
            {
                ctx.MoveTo(c.X(16 + inset), c.Y(56 - inset));
                ctx.LineTo(c.X(16 + inset), c.Y(34));
                ctx.CurveTo(c.X(16 + inset), c.Y(23 + inset),
                    c.X(25 + inset), c.Y(16 + inset), c.X(36), c.Y(16 + inset));
                ctx.LineTo(c.X(60 - inset), c.Y(16 + inset));
                ctx.Stroke();
            }
            Rail(0);
            Rail(6);
            Rail(12);
        }

        // The sphere (0.1.20, the first 3D volume): a circle with an equatorial ellipse — the classic
        // "this is a ball, not a disc" cue.
        private static void DrawSphere(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            ctx.Arc(c.X(38), c.Y(37), c.L(22), 0, 2 * Math.PI);
            ctx.Stroke();
            // The equator: a squashed ellipse across the middle, slightly thinner pen.
            Pen(ctx, rgba, c.L(1.8));
            ctx.Save();
            ctx.Translate(c.X(38), c.Y(37));
            ctx.Scale(c.L(22), c.L(7.5));
            ctx.Arc(0, 0, 1, 0, 2 * Math.PI);
            ctx.Restore();
            ctx.Stroke();
        }

        // The dome (0.1.21): a half-circle with an elliptical base — a bump on a plane.
        private static void DrawDome(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.6));
            double cx = c.X(38), cy = c.Y(50), rx = c.L(24), ry = c.L(24);
            ctx.Save();
            ctx.Translate(cx, cy);
            ctx.Scale(rx, ry);
            ctx.Arc(0, 0, 1, Math.PI, 2 * Math.PI);     // upper dome
            ctx.Restore();
            ctx.Stroke();
            // Elliptical base across the bottom (the open ring).
            Pen(ctx, rgba, c.L(1.8));
            ctx.Save();
            ctx.Translate(cx, cy);
            ctx.Scale(rx, c.L(7));
            ctx.Arc(0, 0, 1, 0, 2 * Math.PI);
            ctx.Restore();
            ctx.Stroke();
        }

        // The cylinder (0.1.21): two ellipses (top + bottom) joined by vertical sides.
        private static void DrawCylinder(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.4));
            double cx = c.X(38), rx = c.L(20), ry = c.L(7);
            double topY = c.Y(20), botY = c.Y(56);
            // sides
            ctx.MoveTo(cx - rx, topY); ctx.LineTo(cx - rx, botY); ctx.Stroke();
            ctx.MoveTo(cx + rx, topY); ctx.LineTo(cx + rx, botY); ctx.Stroke();
            // bottom ellipse (full) then top ellipse (full)
            void Ell(double yc) { ctx.Save(); ctx.Translate(cx, yc); ctx.Scale(rx, ry); ctx.Arc(0, 0, 1, 0, 2 * Math.PI); ctx.Restore(); ctx.Stroke(); }
            Ell(botY);
            Ell(topY);
        }

        // The tapered cylinder (0.2.24): the cylinder with a NARROWER top ellipse — the windmill silhouette.
        private static void DrawTaperedCylinder(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.4));
            double cx = c.X(38), rxBot = c.L(21), rxTop = c.L(12);
            double ryBot = c.L(7), ryTop = c.L(4);
            double topY = c.Y(20), botY = c.Y(56);
            // slanted sides
            ctx.MoveTo(cx - rxBot, botY); ctx.LineTo(cx - rxTop, topY); ctx.Stroke();
            ctx.MoveTo(cx + rxBot, botY); ctx.LineTo(cx + rxTop, topY); ctx.Stroke();
            void Ell(double yc, double rx, double ry)
            { ctx.Save(); ctx.Translate(cx, yc); ctx.Scale(rx, ry); ctx.Arc(0, 0, 1, 0, 2 * Math.PI); ctx.Restore(); ctx.Stroke(); }
            Ell(botY, rxBot, ryBot);
            Ell(topY, rxTop, ryTop);
        }

        private static void DrawPolygonalPrism(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.4));
            Poly(ctx, c, rgba, true, 18, 48, 28, 42, 48, 42, 58, 48, 48, 54, 28, 54);
            Poly(ctx, c, rgba, true, 18, 22, 28, 16, 48, 16, 58, 22, 48, 28, 28, 28);
            ctx.MoveTo(c.X(18), c.Y(22)); ctx.LineTo(c.X(18), c.Y(48));
            ctx.MoveTo(c.X(58), c.Y(22)); ctx.LineTo(c.X(58), c.Y(48));
            ctx.MoveTo(c.X(28), c.Y(28)); ctx.LineTo(c.X(28), c.Y(54));
            ctx.MoveTo(c.X(48), c.Y(28)); ctx.LineTo(c.X(48), c.Y(54));
            ctx.Stroke();
        }

        private static void DrawTaperedPolygonalPrism(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.4));
            Poly(ctx, c, rgba, true, 14, 50, 26, 43, 50, 43, 62, 50, 50, 57, 26, 57);
            Poly(ctx, c, rgba, true, 25, 22, 31, 18, 45, 18, 51, 22, 45, 26, 31, 26);
            ctx.MoveTo(c.X(14), c.Y(50)); ctx.LineTo(c.X(25), c.Y(22));
            ctx.MoveTo(c.X(62), c.Y(50)); ctx.LineTo(c.X(51), c.Y(22));
            ctx.MoveTo(c.X(26), c.Y(57)); ctx.LineTo(c.X(31), c.Y(26));
            ctx.MoveTo(c.X(50), c.Y(57)); ctx.LineTo(c.X(45), c.Y(26));
            ctx.Stroke();
        }

        // The cone (0.1.21): an elliptical base rising to a tip.
        private static void DrawCone(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.4));
            double cx = c.X(38), rx = c.L(20), ry = c.L(7);
            double botY = c.Y(56), tipY = c.Y(16);
            ctx.MoveTo(cx - rx, botY); ctx.LineTo(cx, tipY); ctx.LineTo(cx + rx, botY); ctx.Stroke();
            ctx.Save(); ctx.Translate(cx, botY); ctx.Scale(rx, ry); ctx.Arc(0, 0, 1, 0, 2 * Math.PI); ctx.Restore();
            ctx.Stroke();
        }

        // The box (0.1.21): an isometric cuboid (front face + top + right edges) — a solid, not a flat square.
        private static void DrawBox(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76);
            Pen(ctx, rgba, c.L(2.4));
            // front face
            Poly(ctx, c, rgba, true, 16, 30, 46, 30, 46, 60, 16, 60);
            // top
            Poly(ctx, c, rgba, false, 16, 30, 30, 18, 60, 18, 46, 30);
            // right side
            Poly(ctx, c, rgba, false, 46, 30, 60, 18, 60, 48, 46, 60);
        }

        // An empty favorite slot (0.1.15): a faint small hollow square — visibly "nothing pinned here".
        private static void DrawEmptySlot(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            ctx.LineWidth = Math.Max(1.2, c.L(1.6));
            SetColor(ctx, rgba, 0.28);
            ctx.Rectangle(c.X(20), c.Y(20), c.L(20), c.L(20));
            ctx.Stroke();
        }

        // The ★ badge on pinned catalog tiles (0.1.15; repositioned in 0.1.16 — human-requested): a small
        // filled five-point star tucked into the UPPER-RIGHT corner, close to the tile edge so it clears
        // every shape glyph (zoom 1.0 so it can hug the corner without clipping).
        private static void DrawStarBadge(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 76, zoom: 1.0);
            double cx = 69.5, cy = 7, rOut = 6.5, rIn = 2.7;
            SetColor(ctx, rgba, 1.0);
            for (int i = 0; i < 10; i++)
            {
                double r = i % 2 == 0 ? rOut : rIn;
                double ang = -Math.PI / 2 + Math.PI * i / 5.0;
                double px = cx + r * Math.Cos(ang), py = cy + r * Math.Sin(ang);
                if (i == 0) ctx.MoveTo(c.X(px), c.Y(py)); else ctx.LineTo(c.X(px), c.Y(py));
            }
            ctx.ClosePath();
            ctx.Fill();
        }

        // ============================ toggle glyphs (box = 60) ============================

        private static void DrawModeCreate(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(3.0));
            ctx.MoveTo(c.X(30), c.Y(16)); ctx.LineTo(c.X(30), c.Y(44)); ctx.Stroke();
            ctx.MoveTo(c.X(16), c.Y(30)); ctx.LineTo(c.X(44), c.Y(30)); ctx.Stroke();
        }

        private static void DrawModeEdit(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            // A pencil pointing down-left (the classic "edit" glyph).
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            // body (rotated rectangle: eraser end upper-right → toward the tip lower-left)
            ctx.MoveTo(c.X(37), c.Y(15)); ctx.LineTo(c.X(45), c.Y(23));
            ctx.LineTo(c.X(23), c.Y(45)); ctx.LineTo(c.X(15), c.Y(37)); ctx.ClosePath(); ctx.Stroke();
            // sharpened tip point
            ctx.MoveTo(c.X(15), c.Y(37)); ctx.LineTo(c.X(11), c.Y(49)); ctx.LineTo(c.X(23), c.Y(45)); ctx.Stroke();
            // graphite boundary INSIDE the tip triangle — both endpoints sit on the tip's edges (the old
            // band ran to (26,48), outside the pencil: the "dangling piece", snipped 0.2.10)
            ctx.MoveTo(c.X(14), c.Y(40)); ctx.LineTo(c.X(19), c.Y(46)); ctx.Stroke();
            // eraser: a band across the body near the upper-right end; the segment beyond it is the eraser
            ctx.MoveTo(c.X(31), c.Y(21)); ctx.LineTo(c.X(39), c.Y(29)); ctx.Stroke();
        }

        private static void DrawModeTransform(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            // Four corner brackets around a centre pip. The brackets say "the WHOLE guide, taken hold of" —
            // the category, not any one operation — which is what lets one glyph cover move, rotate, and
            // later copy and mirror. Anything action-shaped in the middle (a four-way arrow, a curved arrow,
            // an offset ghost) would sell one of those four and mis-sell the other three.
            //
            // The centre pip is deliberately NOT tiny, and the frame is deliberately NOT left empty: this
            // GUI already uses a faint empty square to mean "unfilled favourite slot", so a hollow frame
            // would read as a placeholder rather than as a mode.
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(3.0));

            const double lo = 13, hi = 47, arm = 11;
            ctx.MoveTo(c.X(lo), c.Y(lo + arm)); ctx.LineTo(c.X(lo), c.Y(lo)); ctx.LineTo(c.X(lo + arm), c.Y(lo));
            ctx.Stroke();
            ctx.MoveTo(c.X(hi - arm), c.Y(lo)); ctx.LineTo(c.X(hi), c.Y(lo)); ctx.LineTo(c.X(hi), c.Y(lo + arm));
            ctx.Stroke();
            ctx.MoveTo(c.X(hi), c.Y(hi - arm)); ctx.LineTo(c.X(hi), c.Y(hi)); ctx.LineTo(c.X(hi - arm), c.Y(hi));
            ctx.Stroke();
            ctx.MoveTo(c.X(lo + arm), c.Y(hi)); ctx.LineTo(c.X(lo), c.Y(hi)); ctx.LineTo(c.X(lo), c.Y(hi - arm));
            ctx.Stroke();

            SetColor(ctx, rgba, 1.0);
            ctx.Arc(c.X(30), c.Y(30), c.L(4), 0, 2 * Math.PI);
            ctx.Fill();
        }

        // The action row's four latching toggles. Each depicts WHAT HAPPENS TO A SHAPE, so they read as a
        // family and stay distinct from the pad's directional arrows below them.

        private static void DrawActionMove(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            // One square, shifted, with a motion arrow behind it.
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.8));
            ctx.Rectangle(c.X(26), c.Y(20), c.L(22), c.L(22));
            ctx.Stroke();
            ctx.MoveTo(c.X(11), c.Y(31)); ctx.LineTo(c.X(23), c.Y(31)); ctx.Stroke();
            ctx.MoveTo(c.X(17), c.Y(25)); ctx.LineTo(c.X(23), c.Y(31)); ctx.LineTo(c.X(17), c.Y(37));
            ctx.Stroke();
        }

        private static void DrawActionCopy(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            // Two overlapping squares — the universal duplicate glyph.
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.6));
            ctx.Rectangle(c.X(14), c.Y(14), c.L(24), c.L(24));
            ctx.Stroke();
            ctx.Rectangle(c.X(24), c.Y(24), c.L(24), c.L(24));
            ctx.Stroke();
        }

        /// <summary>
        /// Mirror: a dashed axis with a closed asymmetric form on each side, each the other's reflection.
        /// </summary>
        /// <remarks>
        /// REDRAWN v0.4.28 (human-directed: "a mirror on BOTH sides of the line"). The old glyph drew two
        /// OPEN polylines — a vertical edge with a single line running out to a point — so neither side was
        /// a closed shape. The pair read as two arrowheads aimed outward from the axis, which is a "spread
        /// apart" glyph, not a mirror one.
        ///
        /// THE FORM HAS TO BE ASYMMETRIC or the icon says nothing. A shape symmetric about the vertical
        /// axis looks identical to its own reflection, so the glyph would depict mirroring by showing the
        /// one case where mirroring changes nothing. A right triangle is the smallest form that reads as
        /// handed at 42 px: its vertical edge hugs the axis on one side and faces away on the other, and
        /// that swap is the whole message.
        ///
        /// Both sides are stroked identically. Filling one and outlining the other was tried on paper and
        /// rejected — that reads as "original and copy", which is what the Copy tile already says.
        /// </remarks>
        private static void DrawActionMirror(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));

            // The axis runs the full height of the glyph, so it reads as a mirror plane the forms sit
            // against rather than a divider drawn only as far as they happen to reach.
            for (double v = 10; v < 50; v += 7)
            {
                ctx.MoveTo(c.X(30), c.Y(v)); ctx.LineTo(c.X(30), c.Y(v + 3.6)); ctx.Stroke();
            }

            // Right triangles, upright edge against the axis, hypotenuse falling away outward.
            Poly(ctx, c, rgba, true, 25, 16, 25, 44, 12, 44);
            Poly(ctx, c, rgba, true, 35, 16, 35, 44, 48, 44);
        }

        /// <summary>
        /// One rotate glyph: a ring with an arrowhead on it. <paramref name="upright"/> draws a true circle
        /// (turning about a horizontal axis — tipping the guide over); otherwise a flattened ellipse, a
        /// turntable seen at an angle (turning about the vertical axis). <paramref name="clockwise"/> mirrors
        /// the whole thing, so a left button and a right button are exact reflections of one another.
        /// </summary>
        private static void DrawRotate(
            Context ctx, int x, int y, float w, float h, double[] rgba, bool upright, bool clockwise)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(3.0));

            double cx = c.X(30), cy = c.Y(30);
            double rx = c.L(16);
            double ry = upright ? c.L(16) : c.L(8.5);
            // Cairo's y runs DOWN, so increasing t sweeps CLOCKWISE on screen. Mirroring x reverses it.
            double m = clockwise ? 1.0 : -1.0;

            // Gap centred on the top of the ring, so the head sits clear of it at the upper left/right.
            const double start = -0.32 * Math.PI;
            const double end = 1.32 * Math.PI;

            // THE ARC STOPS SHORT OF THE HEAD (v0.4.28). Running it all the way to the tip put a full
            // stroke width of ring underneath the whole arrowhead, and the two fused into one blunt slab
            // that read as a bar across the top of the glyph. Ending the stroke where the head's base
            // begins leaves a shaft that grows into a head, which is what an arrow looks like.
            const double headArc = 24.0 * Math.PI / 180.0;

            (double X, double Y) At(double t) =>
                (cx + m * rx * Math.Cos(t), cy + ry * Math.Sin(t));

            // Walked as a polyline rather than stroked under a non-uniform Scale, so the pen stays an even
            // width all the way round instead of pinching where a squashed ellipse is steepest.
            const int steps = 56;
            for (int i = 0; i <= steps; i++)
            {
                (double ax, double ay) = At(start + (end - headArc - start) * i / steps);
                if (i == 0) ctx.MoveTo(ax, ay); else ctx.LineTo(ax, ay);
            }
            ctx.Stroke();

            // A FILLED TRIANGLE at the end of the sweep, on the true tangent there.
            //
            // IT WAS A STROKED CHEVRON UNTIL v0.4.28 AND THAT WAS THE BUG. A chevron's trailing leg runs
            // back along the direction of travel, which on a ring of this radius is very nearly the ring
            // itself — so that leg lay on top of the arc and vanished into it, leaving only the outward leg
            // showing. The glyph read as a hook or a flag with a bar across the top, not as an arrow. The
            // legs were also far too long for the ring (11.9 against a radius of 16), which is what made
            // the surviving one look like a bar rather than a barb.
            //
            // A solid head cannot suffer that: it is a shape rather than two lines, and with the arc now
            // stopping at its base it is the only thing at the end of the stroke. Half-width 4.5 against
            // the 3.0 pen is what settled it — 5.2 was a blunt wedge wider than the ring it sat on, and
            // 3.8 was too faint to see the direction of at 42 px. All four were rendered and compared.
            (double hx, double hy) = At(end);
            double dx = -m * rx * Math.Sin(end), dy = ry * Math.Cos(end);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return;
            dx /= len; dy /= len;

            double nx = -dy, ny = dx;                       // unit normal to the direction of travel
            double back = c.L(8.5), half = c.L(4.5), over = c.L(1.0);
            ctx.NewPath();
            ctx.FillRule = FillRule.Winding;
            ctx.MoveTo(hx + dx * over, hy + dy * over);     // tip carried just past where the arc stops
            ctx.LineTo(hx - dx * back + nx * half, hy - dy * back + ny * half);
            ctx.LineTo(hx - dx * back - nx * half, hy - dy * back - ny * half);
            ctx.ClosePath();
            ctx.Fill();
        }

        // One Move-pad arrow, drawn pointing up and rotated into place. `groundBar` adds a floor line under
        // a shortened arrow — the vertical Up/Down pair, so they read differently from Away/Toward.
        private static void DrawMoveArrow(
            Context ctx, int x, int y, float w, float h, double[] rgba, double rotation, bool groundBar)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(3.2));

            double half = c.L(groundBar ? 12 : 16);
            double spread = c.L(9), drop = c.L(11);
            ctx.Save();
            ctx.Translate(c.X(30), c.Y(groundBar ? 25 : 30));
            ctx.Rotate(rotation);
            ctx.MoveTo(0, half); ctx.LineTo(0, -half); ctx.Stroke();
            ctx.MoveTo(-spread, -half + drop); ctx.LineTo(0, -half); ctx.LineTo(spread, -half + drop);
            ctx.Stroke();
            ctx.Restore();

            if (!groundBar) return;
            ctx.MoveTo(c.X(14), c.Y(48)); ctx.LineTo(c.X(46), c.Y(48)); ctx.Stroke();
        }

        /// <summary>
        /// Send to ground (v0.4.28): a DOUBLE chevron falling onto the same floor bar the Up/Down pair
        /// carries, so it reads as one more member of the vertical family rather than a new idea.
        /// </summary>
        /// <remarks>
        /// NO SHAFT, unlike the single-step arrows, and that is the whole distinction. Down is a shaft with
        /// one head — a measured step. This is two heads and no shaft: not a distance, but a fall that ends
        /// at the bar. Two chevrons is also the settled convention for "go all the way" (the media
        /// skip-to-end button), so it reads without a legend.
        ///
        /// The chevrons are DEEPER than the move arrowheads (11 down over 11 across, against the arrow's 9
        /// over 11). A shallow chevron next to a shallow arrowhead was the pair that blurred together at
        /// 42 px; making the fall steeper than the step separates them at a glance.
        /// </remarks>
        private static void DrawMoveGround(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(3.2));

            // Two chevrons, the lower one nearly touching the bar so the fall reads as landing on it.
            ctx.MoveTo(c.X(19), c.Y(15)); ctx.LineTo(c.X(30), c.Y(26)); ctx.LineTo(c.X(41), c.Y(15));
            ctx.Stroke();
            ctx.MoveTo(c.X(19), c.Y(28)); ctx.LineTo(c.X(30), c.Y(39)); ctx.LineTo(c.X(41), c.Y(28));
            ctx.Stroke();

            // The same floor bar, at the same 48, as the Up/Down pair.
            ctx.MoveTo(c.X(14), c.Y(48)); ctx.LineTo(c.X(46), c.Y(48)); ctx.Stroke();
        }

        private static void DrawMoveFree(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            // A crosshair: free-move follows where you are aiming, not a fixed axis.
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            ctx.Arc(c.X(30), c.Y(30), c.L(13), 0, 2 * Math.PI);
            ctx.Stroke();
            ctx.MoveTo(c.X(30), c.Y(10)); ctx.LineTo(c.X(30), c.Y(21)); ctx.Stroke();
            ctx.MoveTo(c.X(30), c.Y(39)); ctx.LineTo(c.X(30), c.Y(50)); ctx.Stroke();
            ctx.MoveTo(c.X(10), c.Y(30)); ctx.LineTo(c.X(21), c.Y(30)); ctx.Stroke();
            ctx.MoveTo(c.X(39), c.Y(30)); ctx.LineTo(c.X(50), c.Y(30)); ctx.Stroke();
            SetColor(ctx, rgba, 1.0);
            ctx.Arc(c.X(30), c.Y(30), c.L(3), 0, 2 * Math.PI);
            ctx.Fill();
        }

        private static void DrawModeDelete(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            ctx.MoveTo(c.X(15), c.Y(22)); ctx.LineTo(c.X(45), c.Y(22)); ctx.Stroke();     // lid
            Poly(ctx, c, rgba, false, 25, 22, 25, 17, 35, 17, 35, 22);                    // handle
            Poly(ctx, c, rgba, false, 18, 22, 21, 47, 39, 47, 42, 22);                    // body
            ctx.MoveTo(c.X(26), c.Y(28)); ctx.LineTo(c.X(26), c.Y(42)); ctx.Stroke();     // ribs
            ctx.MoveTo(c.X(34), c.Y(28)); ctx.LineTo(c.X(34), c.Y(42)); ctx.Stroke();
        }

        private static void DrawProjVolumetric(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            Poly(ctx, c, rgba, true, 19, 26, 37, 26, 37, 46, 19, 46);   // front face
            Poly(ctx, c, rgba, false, 19, 26, 28, 18, 46, 18, 37, 26);  // top edges
            Poly(ctx, c, rgba, false, 37, 26, 46, 18, 46, 38, 37, 46);  // right edges
        }

        private static void DrawProjSurface(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            Poly(ctx, c, rgba, true, 13, 41, 29, 24, 50, 24, 34, 41);   // flat plane (parallelogram)
            ctx.MoveTo(c.X(22), c.Y(33)); ctx.LineTo(c.X(40), c.Y(33)); ctx.Stroke();  // surface hint
        }

        private static void DrawFillHollow(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.6));
            ctx.Rectangle(c.X(16), c.Y(16), c.L(28), c.L(28));
            ctx.Stroke();
        }

        private static void DrawFillFilled(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            SetColor(ctx, rgba, 1.0);
            ctx.Rectangle(c.X(16), c.Y(16), c.L(28), c.L(28));
            ctx.Fill();
        }

        private static void DrawFormShell(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);

            // Three continuous faces make this read as an enclosing skin, not a solid interior.
            SetColor(ctx, rgba, 0.22);
            ctx.MoveTo(c.X(13), c.Y(24)); ctx.LineTo(c.X(30), c.Y(14));
            ctx.LineTo(c.X(47), c.Y(24)); ctx.LineTo(c.X(30), c.Y(34)); ctx.ClosePath(); ctx.Fill();
            SetColor(ctx, rgba, 0.34);
            ctx.MoveTo(c.X(13), c.Y(24)); ctx.LineTo(c.X(30), c.Y(34));
            ctx.LineTo(c.X(30), c.Y(51)); ctx.LineTo(c.X(13), c.Y(41)); ctx.ClosePath(); ctx.Fill();
            SetColor(ctx, rgba, 0.48);
            ctx.MoveTo(c.X(30), c.Y(34)); ctx.LineTo(c.X(47), c.Y(24));
            ctx.LineTo(c.X(47), c.Y(41)); ctx.LineTo(c.X(30), c.Y(51)); ctx.ClosePath(); ctx.Fill();

            Pen(ctx, rgba, c.L(2.3));
            Poly(ctx, c, rgba, true, 13, 24, 30, 14, 47, 24, 47, 41, 30, 51, 13, 41);
        }

        private static void DrawFormWireframe(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.2));

            // Two offset frames plus their four corner wires: unmistakably skeletal and open.
            Poly(ctx, c, rgba, true, 14, 22, 34, 22, 34, 42, 14, 42);
            Poly(ctx, c, rgba, true, 25, 13, 46, 13, 46, 34, 25, 34);
            ctx.MoveTo(c.X(14), c.Y(22)); ctx.LineTo(c.X(25), c.Y(13));
            ctx.MoveTo(c.X(34), c.Y(22)); ctx.LineTo(c.X(46), c.Y(13));
            ctx.MoveTo(c.X(34), c.Y(42)); ctx.LineTo(c.X(46), c.Y(34));
            ctx.MoveTo(c.X(14), c.Y(42)); ctx.LineTo(c.X(25), c.Y(34));
            ctx.Stroke();
        }

        private static void DrawVisShown(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            EyeAlmond(ctx, c);
            ctx.Stroke();
            SetColor(ctx, rgba, 1.0);
            ctx.Arc(c.X(30), c.Y(30), c.L(5), 0, 2 * Math.PI);
            ctx.Fill();
        }

        private static void DrawVisHidden(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            EyeAlmond(ctx, c);
            ctx.Stroke();
            ctx.MoveTo(c.X(15), c.Y(16)); ctx.LineTo(c.X(45), c.Y(44)); ctx.Stroke();   // slash
        }

        // ---- reveal actions (v0.4.18) ----
        // Both are an EYE plus a qualifier, because both do the same thing (unhide) and differ only in
        // reach. The eye is lifted and shrunk to leave room underneath; the qualifier carries the meaning.

        /// <summary>Eye over a measured run of six blocks — "unhide what is standing right here".</summary>
        private static void DrawRevealNear(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.4));
            SmallEye(ctx, c, 22);
            ctx.Stroke();
            SetColor(ctx, rgba, 1.0);
            ctx.Arc(c.X(30), c.Y(22), c.L(4), 0, 2 * Math.PI);
            ctx.Fill();

            // SIX blocks, drawn as filled squares with real gaps rather than a ruler with fine ticks: at a
            // 42 px tile a tick every four pixels is a grey smear, whereas six separated blocks stay
            // countable. They are the unit the range is measured in, so they read as the range too.
            //
            // Sized off a render, not by eye (dev/RenderIcon.ps1 -Glyph revealnear): the first attempt used
            // 6.2-wide blocks 6.2 tall and they collapsed into a dashed underline at true size. Height is
            // what rescued them — a mark needs vertical mass to read as a block rather than a dash — so
            // these are TALLER than they are wide, which looks wrong in the source and right on screen.
            const double count = 6, blockW = 6.8, gap = 1.4, blockH = 9.0;
            double runW = count * blockW + (count - 1) * gap;
            double bx = (60 - runW) / 2.0;
            SetColor(ctx, rgba, 0.95);
            for (int i = 0; i < count; i++)
            {
                ctx.Rectangle(c.X(bx + i * (blockW + gap)), c.Y(40), c.L(blockW), c.L(blockH));
                ctx.Fill();
            }
        }

        /// <summary>Eye casting rays outward — "unhide everything of mine, wherever it stands".</summary>
        private static void DrawRevealAll(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);

            // Rays first, so the eye's own stroke draws over any that reach too far inward.
            Pen(ctx, rgba, c.L(2.2));
            const double inner = 19, outer = 27;
            for (int i = 0; i < 8; i++)
            {
                // Skipped at the horizontal, where a ray would run straight into the eye's own corners
                // and read as the eye being wider rather than as light leaving it.
                double deg = 22.5 + i * 45.0;
                if (Math.Abs(Math.Sin(deg * Math.PI / 180.0)) < 0.2) continue;
                double a = deg * Math.PI / 180.0;
                double dx = Math.Cos(a), dy = Math.Sin(a);
                ctx.MoveTo(c.X(30 + dx * inner), c.Y(30 + dy * inner));
                ctx.LineTo(c.X(30 + dx * outer), c.Y(30 + dy * outer));
            }
            ctx.Stroke();

            Pen(ctx, rgba, c.L(2.4));
            SmallEye(ctx, c, 30);
            ctx.Stroke();
            SetColor(ctx, rgba, 1.0);
            ctx.Arc(c.X(30), c.Y(30), c.L(4), 0, 2 * Math.PI);
            ctx.Fill();
        }

        // The eye almond at ~three quarters scale, centred on an arbitrary row. Kept separate from
        // EyeAlmond so the two full-size visibility tiles are untouched by anything done here.
        private static void SmallEye(Context ctx, Canvas c, double cy)
        {
            ctx.MoveTo(c.X(17), c.Y(cy));
            ctx.CurveTo(c.X(23), c.Y(cy - 9), c.X(37), c.Y(cy - 9), c.X(43), c.Y(cy));
            ctx.CurveTo(c.X(37), c.Y(cy + 9), c.X(23), c.Y(cy + 9), c.X(17), c.Y(cy));
            ctx.ClosePath();
        }

        private static void EyeAlmond(Context ctx, Canvas c)
        {
            ctx.MoveTo(c.X(13), c.Y(30));
            ctx.CurveTo(c.X(21), c.Y(18), c.X(39), c.Y(18), c.X(47), c.Y(30));
            ctx.CurveTo(c.X(39), c.Y(42), c.X(21), c.Y(42), c.X(13), c.Y(30));
            ctx.ClosePath();
        }

        // ============================ plane glyphs (box = 60) ============================
        // Iso cube; the active plane's face is filled, all edges are faint.

        private static void DrawPlaneFloor(Context ctx, int x, int y, float w, float h, double[] rgba) => IsoPlane(ctx, x, y, w, h, rgba, 0);
        private static void DrawPlaneEW(Context ctx, int x, int y, float w, float h, double[] rgba) => IsoPlane(ctx, x, y, w, h, rgba, 1);
        private static void DrawPlaneNS(Context ctx, int x, int y, float w, float h, double[] rgba) => IsoPlane(ctx, x, y, w, h, rgba, 2);

        private static void IsoPlane(Context ctx, int x, int y, float w, float h, double[] rgba, int face)
        {
            var c = new Canvas(x, y, w, h, 60);
            // vertices
            double ax = 30, ay = 12, rx = 50, ry = 23, bx = 30, by = 34, lx = 10, ly = 23; // top diamond
            double l2 = 39, b2 = 50, r2 = 39;                                                // bottom (verticals +16)

            // active face fill (semi-transparent)
            SetColor(ctx, rgba, 0.85);
            switch (face)
            {
                case 0: PathPoly(ctx, c, ax, ay, rx, ry, bx, by, lx, ly); break;        // top
                case 1: PathPoly(ctx, c, lx, ly, bx, by, 30, b2, 10, l2); break;        // left face
                default: PathPoly(ctx, c, bx, by, rx, ry, 50, r2, 30, b2); break;       // right face
            }
            ctx.Fill();

            // all edges faint on top
            Pen(ctx, rgba, c.L(1.8));
            SetColor(ctx, rgba, 0.5);
            PathPoly(ctx, c, ax, ay, rx, ry, bx, by, lx, ly); ctx.Stroke();             // top diamond
            ctx.MoveTo(c.X(lx), c.Y(ly)); ctx.LineTo(c.X(10), c.Y(l2)); ctx.Stroke();
            ctx.MoveTo(c.X(bx), c.Y(by)); ctx.LineTo(c.X(30), c.Y(b2)); ctx.Stroke();
            ctx.MoveTo(c.X(rx), c.Y(ry)); ctx.LineTo(c.X(50), c.Y(r2)); ctx.Stroke();
            ctx.MoveTo(c.X(10), c.Y(l2)); ctx.LineTo(c.X(30), c.Y(b2)); ctx.Stroke();
            ctx.MoveTo(c.X(30), c.Y(b2)); ctx.LineTo(c.X(50), c.Y(r2)); ctx.Stroke();
        }

        private static void PathPoly(Context ctx, Canvas c, params double[] uv)
        {
            ctx.MoveTo(c.X(uv[0]), c.Y(uv[1]));
            for (int i = 2; i + 1 < uv.Length; i += 2) ctx.LineTo(c.X(uv[i]), c.Y(uv[i + 1]));
            ctx.ClosePath();
        }

        private static void DrawPlaneAuto(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(2.8));
            ctx.MoveTo(c.X(20), c.Y(46)); ctx.LineTo(c.X(30), c.Y(15)); ctx.LineTo(c.X(40), c.Y(46)); ctx.Stroke();
            ctx.MoveTo(c.X(24.5), c.Y(33)); ctx.LineTo(c.X(35.5), c.Y(33)); ctx.Stroke();   // A crossbar
        }

        // ============================ scale glyphs (box = 60) ============================
        // The game's native scale look, copied: an NxN grid of filled squares where N IS the voxel count
        // (1x1 smallest ... 16x16 = a full block). Per the human's spec: the 1x1 is a small near-dot, the
        // 2x2 is mid-sized, and from 4x4 up the grid runs EDGE-TO-EDGE with enlarged squares; the full
        // block (scale 16) is drawn by DrawScaleFullBlock as one solid square filling the button.
        private static void DrawScaleGrid(Context ctx, int x, int y, float w, float h, double[] rgba, int n)
        {
            var c = new Canvas(x, y, w, h, 60, zoom: 1.0);
            SetColor(ctx, rgba, 1.0);

            if (n <= 1)
            {
                const double d = 13;                          // a single small centred square (~a dot)
                ctx.Rectangle(c.X(30 - d / 2), c.Y(30 - d / 2), c.L(d), c.L(d));
                ctx.Fill();
                return;
            }

            // 2x2 sits mid-sized like the vanilla icon; 4x4 and denser go to the button's edge.
            double inset = n == 2 ? 13 : 2;
            double area = 60 - 2 * inset;
            double cell = area / n;
            double gap = Math.Min(cell * 0.22, 2.6);          // gaps shrink with density; squares stay chunky
            double sq = Math.Max(1.0, cell - gap);
            for (int r = 0; r < n; r++)
                for (int col = 0; col < n; col++)
                    ctx.Rectangle(c.X(inset + col * cell + gap * 0.5), c.Y(inset + r * cell + gap * 0.5), c.L(sq), c.L(sq));
            ctx.Fill();                                        // one fill for the whole grid
        }

        // Scale 16 (a whole block): ONE solid square filling the button — reads instantly, unlike a 16x16 mesh.
        private static void DrawScaleFullBlock(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60, zoom: 1.0);
            SetColor(ctx, rgba, 1.0);
            ctx.Rectangle(c.X(2), c.Y(2), c.L(56), c.L(56));
            ctx.Fill();
        }
    }
}
