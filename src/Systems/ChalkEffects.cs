using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;

namespace Layout.Systems
{
    /// <summary>
    /// The chalk feedback effects (F5 polish): yellow chalk-dust puffs and the chalk-line "snap" heard when
    /// a guide is placed. Pure fire-and-forget helpers — callable from either side (a server world
    /// broadcasts the particles/sound to every client in range; a client world plays them locally, which is
    /// exactly right for PRIVATE placements only that player can see).
    /// </summary>
    /// <remarks>
    /// EMISSION SHAPE. 2D guides puff along their WHOLE sampled curve (a dust point roughly every half
    /// block, capped — see <see cref="MaxLineEmitPoints"/> — so giant guides cannot flood the particle
    /// system). 3D volumes deliberately do NOT puff over their entire shell (a 100-block sphere would emit
    /// an absurd cloud); they puff around the BASE RING on the plane they were set on — the ring through
    /// their two base anchors. For a Box that ring is an approximation of the base outline; accepted, it is
    /// a dust cloud, not geometry. Effects are non-critical: any sampling surprise is swallowed and at
    /// worst the placement stays silent.
    /// </remarks>
    public static class ChalkEffects
    {
        // Chalk-dust yellow, matching the guide body's colour language.
        private static readonly int ChalkColor = ColorUtil.ToRgba(180, 235, 205, 90);

        // Whole-curve emission: one dust point about every half block, hard-capped so large guides stay sane.
        private const double LineEmitSpacing = 0.5;
        private const int MaxLineEmitPoints = 40;
        private const int MaxRingEmitPoints = 24;

        /// <summary>A full-strength puff of yellow chalk dust centred on <paramref name="pos"/> (refills).</summary>
        public static void SpawnChalkPuff(IWorldAccessor world, Vec3d pos) => Puff(world, pos, 8f, 16f);

        /// <summary>
        /// The completed-placement feedback: the taut-string snap of a real chalk line (the vanilla
        /// bow-release twang, slightly quiet) plus dust along the guide itself — the whole curve for 2D
        /// shapes, the base ring for 3D volumes. Deliberately independent of the chalk-durability system —
        /// it is placement feedback, so it also plays for creative players and durability-disabled servers.
        /// </summary>
        public static void PlacementEffects(IWorldAccessor world, GuideData guide)
        {
            if (world == null || guide == null) return;

            try
            {
                List<Vec3d> points = GuideShapeTypes.IsVolume(guide.ShapeType)
                    ? BaseRingPoints(guide)
                    : CurveEmitPoints(guide);
                if (points == null || points.Count == 0) return;

                // The snap plays once, at the emission centroid (≈ the guide's middle).
                double cx = 0, cy = 0, cz = 0;
                foreach (Vec3d p in points) { cx += p.X; cy += p.Y; cz += p.Z; }
                int n = points.Count;
                world.PlaySoundAt(new AssetLocation("sounds/bow-release"),
                    cx / n, cy / n, cz / n, null, true, 32f, 0.55f);

                // Anchors get the full puff; the run of the guide gets lighter dust per point.
                Puff(world, points[0], 8f, 16f);
                if (n > 1) Puff(world, points[n - 1], 8f, 16f);
                for (int i = 1; i < n - 1; i++) Puff(world, points[i], 2f, 5f);
            }
            catch (Exception e)
            {
                world.Logger?.VerboseDebug("[Layout] placement effects skipped: {0}", e.Message);
            }
        }

        // --- emission-point generation ---------------------------------------------------------------

        /// <summary>2D shapes: the sampled curve thinned to ~one point per half block, capped.</summary>
        private static List<Vec3d> CurveEmitPoints(GuideData guide)
        {
            IGuideShape shape = ShapeFactory.Adopt(guide);
            List<Vec3d> curve = shape?.SampleCurve(64);
            if (curve == null || curve.Count == 0) return null;
            if (curve.Count == 1) return curve;

            double total = 0;
            for (int i = 1; i < curve.Count; i++) total += curve[i].DistanceTo(curve[i - 1]);
            double spacing = Math.Max(LineEmitSpacing, total / MaxLineEmitPoints);

            var points = new List<Vec3d> { curve[0] };
            double sinceLast = 0;
            for (int i = 1; i < curve.Count; i++)
            {
                sinceLast += curve[i].DistanceTo(curve[i - 1]);
                if (sinceLast >= spacing || i == curve.Count - 1)
                {
                    points.Add(curve[i]);
                    sinceLast = 0;
                }
            }
            return points;
        }

        /// <summary>
        /// 3D volumes: a ring on the base plane (normal = the guide's intrinsic axis) through the two base
        /// anchors — the "surface plane they are set on". Point count scales with circumference, capped.
        /// </summary>
        private static List<Vec3d> BaseRingPoints(GuideData guide)
        {
            Vec3d a = null, b = null;
            foreach (ControlPoint cp in guide.ControlPoints)
            {
                if (cp == null || cp.IsPhantom || !cp.IsAnchor) continue;
                if (a == null) a = cp.WorldPosition;
                else { b = cp.WorldPosition; break; }
            }
            if (a == null) return null;
            if (b == null) return new List<Vec3d> { a.Clone() };

            var centre = new Vec3d((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
            double radius = a.DistanceTo(b) / 2;
            if (radius < 0.05) return new List<Vec3d> { centre };

            // In-plane basis perpendicular to the base axis.
            Vec3d u, v;
            switch (guide.ShapePlaneAxis)
            {
                case PlaneAxis.X: u = new Vec3d(0, 1, 0); v = new Vec3d(0, 0, 1); break;
                case PlaneAxis.Z: u = new Vec3d(1, 0, 0); v = new Vec3d(0, 1, 0); break;
                default:          u = new Vec3d(1, 0, 0); v = new Vec3d(0, 0, 1); break;
            }

            int count = (int)GameMath.Clamp(2 * Math.PI * radius / LineEmitSpacing, 8, MaxRingEmitPoints);
            var points = new List<Vec3d>(count);
            for (int i = 0; i < count; i++)
            {
                double ang = 2 * Math.PI * i / count;
                double cu = Math.Cos(ang) * radius, sv = Math.Sin(ang) * radius;
                points.Add(new Vec3d(
                    centre.X + u.X * cu + v.X * sv,
                    centre.Y + u.Y * cu + v.Y * sv,
                    centre.Z + u.Z * cu + v.Z * sv));
            }
            return points;
        }

        // --- the puff itself -------------------------------------------------------------------------

        private static void Puff(IWorldAccessor world, Vec3d pos, float minQuantity, float maxQuantity)
        {
            if (pos == null) return;

            var puff = new SimpleParticleProperties(
                minQuantity, maxQuantity, ChalkColor,
                new Vec3d(pos.X - 0.15, pos.Y, pos.Z - 0.15),
                new Vec3d(pos.X + 0.15, pos.Y + 0.15, pos.Z + 0.15),
                new Vec3f(-0.4f, 0.1f, -0.4f),
                new Vec3f(0.4f, 0.6f, 0.4f),
                lifeLength: 0.7f,
                gravityEffect: 0.35f,
                minSize: 0.08f,
                maxSize: 0.22f,
                model: EnumParticleModel.Quad);

            world.SpawnParticles(puff);
        }
    }
}
