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
        public const string ModeDelete = "layout-mode-delete";
        public const string ProjVolumetric = "layout-proj-vol";
        public const string ProjSurface = "layout-proj-surf";
        public const string FillHollow = "layout-fill-hollow";
        public const string FillFilled = "layout-fill-filled";
        public const string FormShell = "layout-form-shell";
        public const string FormWireframe = "layout-form-wireframe";
        public const string VisShown = "layout-vis-shown";
        public const string VisHidden = "layout-vis-hidden";

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
                Isosceles, Rectangle, Square, Polygon, FreeShapeIcon, Sphere, Dome, Cylinder,
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
            reg[ModeDelete] = DrawModeDelete;
            reg[ProjVolumetric] = DrawProjVolumetric;
            reg[ProjSurface] = DrawProjSurface;
            reg[FillHollow] = DrawFillHollow;
            reg[FillFilled] = DrawFillFilled;
            reg[FormShell] = DrawFormShell;
            reg[FormWireframe] = DrawFormWireframe;
            reg[VisShown] = DrawVisShown;
            reg[VisHidden] = DrawVisHidden;

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
        // Settings gear. Eight radial spokes around a ring rather than a true toothed outline — at title-bar
        // size (18 px) real teeth turn into a grey smudge, while spokes stay legible.
        private static void DrawGear(Context ctx, int x, int y, float w, float h, double[] rgba)
        {
            var c = new Canvas(x, y, w, h, 60);
            Pen(ctx, rgba, c.L(3.4));

            const double cx = 30, cy = 30;
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4.0;
                double ca = Math.Cos(a), sa = Math.Sin(a);
                ctx.MoveTo(c.X(cx + ca * 14), c.Y(cy + sa * 14));
                ctx.LineTo(c.X(cx + ca * 23), c.Y(cy + sa * 23));
            }
            ctx.Stroke();

            ctx.Arc(c.X(cx), c.Y(cy), c.L(14), 0, Math.PI * 2);
            ctx.Stroke();

            ctx.Arc(c.X(cx), c.Y(cy), c.L(5.5), 0, Math.PI * 2);
            ctx.Stroke();
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
