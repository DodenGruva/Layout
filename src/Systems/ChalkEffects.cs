using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;

namespace Layout.Systems
{
    /// <summary>
    /// Placement and refill feedback: the chalk-line snap, short falling flecks, and a softer floating
    /// dust complement. Public effects broadcast from the server; private effects play only locally.
    /// </summary>
    /// <remarks>
    /// The original falling flecks remain along the whole 2D curve or around a volume's base ring. The
    /// lighter layer drifts away from 2D curves and mathematically sampled points across every 3D shell.
    /// Shell sampling is parametric and hard-capped; it never voxelises the guide, so its cost is independent
    /// of guide resolution and bounded even for enormous volumes.
    /// </remarks>
    public static class ChalkEffects
    {
        private static readonly int ChalkColor = ColorUtil.ToRgba(180, 235, 205, 90);
        private static readonly int FloatingDustColor = ColorUtil.ToRgba(105, 245, 225, 150);

        private const double LineEmitSpacing = 0.5;
        private const int MaxLineEmitPoints = 40;
        private const int MaxRingEmitPoints = 24;
        private const int MaxLineDustPoints = 32;
        private const int MaxSurfaceEmitPoints = 56;
        private const double GoldenAngle = Math.PI * (3.0 - 2.2360679774997896964); // pi * (3 - sqrt(5))

        private readonly struct SurfaceEmitPoint
        {
            public readonly Vec3d Position;
            public readonly Vec3d Normal;

            public SurfaceEmitPoint(Vec3d position, Vec3d normal)
            {
                Position = position;
                Normal = normal;
            }
        }

        /// <summary>A full-strength puff of yellow chalk dust centred on <paramref name="pos"/> (refills).</summary>
        public static void SpawnChalkPuff(IWorldAccessor world, Vec3d pos) => Puff(world, pos, 8f, 16f);

        /// <summary>
        /// Completed-placement feedback. The existing falling run/base-ring burst remains, then a capped
        /// floating complement is added along 2D curves or across the full shell of a 3D volume.
        /// </summary>
        public static void PlacementEffects(IWorldAccessor world, GuideData guide)
        {
            if (world == null || guide == null) return;

            try
            {
                bool volume = GuideShapeTypes.IsVolume(guide.ShapeType);
                List<Vec3d> points = volume ? BaseRingPoints(guide) : CurveEmitPoints(guide);
                if (points == null || points.Count == 0) return;

                double cx = 0, cy = 0, cz = 0;
                foreach (Vec3d p in points) { cx += p.X; cy += p.Y; cz += p.Z; }
                int n = points.Count;
                int spanBlocks = Math.Max(guide.CachedBlockWidth, guide.CachedBlockHeight);
                double size = Math.Max(1, spanBlocks);
                float heft = (float)Math.Max(0.0, Math.Min(1.0, Math.Log(size, 2.0) / 10.0));
                float pitch = 1.0f - 0.38f * heft;
                float range = 32f + 20f * heft;
                float soundVolume = 0.55f + 0.40f * heft;
                world.PlaySoundAt(new AssetLocation("sounds/bow-release"),
                    cx / n, cy / n, cz / n, null, pitch, range, soundVolume);

                // Original short-lived, falling chalk flecks.
                Puff(world, points[0], 8f, 16f);
                if (n > 1) Puff(world, points[n - 1], 8f, 16f);
                for (int i = 1; i < n - 1; i++) Puff(world, points[i], 2f, 5f);

                if (volume)
                {
                    // Sparse falling flecks and soft outward drift over the whole shell. The stronger base
                    // ring remains, preserving the grounded chalk-line snap at the placement plane.
                    List<SurfaceEmitPoint> shell = VolumeSurfacePoints(guide);
                    if (shell != null)
                    {
                        foreach (SurfaceEmitPoint site in shell)
                        {
                            Puff(world, site.Position, 1f, 2f);
                            DriftPuff(world, site.Position, site.Normal, 1f, 3f);
                        }
                    }
                }
                else
                {
                    foreach (Vec3d p in ThinEvenly(points, MaxLineDustPoints))
                        DriftPuff(world, p, null, 1f, 3f);
                }
            }
            catch (Exception e)
            {
                world.Logger?.VerboseDebug("[Layout] placement effects skipped: {0}", e.Message);
            }
        }

        // --- original line/base emission points -----------------------------------------------------

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

            if (guide.ShapeType == GuideShapeType.PolygonalPrism
                || guide.ShapeType == GuideShapeType.TaperedPolygonalPrism)
                return PolygonBaseRingPoints(a, b, guide.ShapePlaneAxis, guide.Sides,
                    guide.FlatSideAligned);

            var centre = new Vec3d((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
            double radius = a.DistanceTo(b) / 2;
            if (radius < 0.05) return new List<Vec3d> { centre };

            ShapeGeometry.InPlaneAxes(guide.ShapePlaneAxis, out Vec3d u, out Vec3d v);
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

        private static List<Vec3d> PolygonBaseRingPoints(Vec3d a, Vec3d b, PlaneAxis axis,
            int sideSetting, bool flatSideAligned)
        {
            if (!ShapeGeometry.TryGetFrame(a, b, axis, out Vec3d u, out Vec3d m, out double span))
                return new List<Vec3d> { a.Clone() };

            int sides = PolygonShape.ClampSides(sideSetting);
            double apothemRatio = Math.Cos(Math.PI / sides);
            double near = flatSideAligned ? apothemRatio : 1.0;
            double far = flatSideAligned
                ? (sides % 2 == 0 ? apothemRatio : 1.0)
                : (sides % 2 == 0 ? 1.0 : apothemRatio);
            double radius = span / (near + far);
            if (radius < 0.05) return new List<Vec3d> { a.Clone() };

            var centre = new Vec3d(a.X + u.X * radius * near,
                a.Y + u.Y * radius * near, a.Z + u.Z * radius * near);
            double perimeter = 2.0 * sides * radius * Math.Sin(Math.PI / sides);
            int count = (int)GameMath.Clamp(perimeter / LineEmitSpacing, 8, MaxRingEmitPoints);
            var points = new List<Vec3d>(count);
            for (int i = 0; i < count; i++)
            {
                double edgePosition = i * sides / (double)count;
                int edge = (int)Math.Floor(edgePosition);
                double t = edgePosition - edge;
                double phase = Math.PI + (flatSideAligned ? Math.PI / sides : 0.0);
                double a0 = phase + 2.0 * Math.PI * edge / sides;
                double a1 = phase + 2.0 * Math.PI * ((edge + 1) % sides) / sides;
                double x = (Math.Cos(a0) * (1.0 - t) + Math.Cos(a1) * t) * radius;
                double y = (Math.Sin(a0) * (1.0 - t) + Math.Sin(a1) * t) * radius;
                points.Add(new Vec3d(centre.X + u.X * x + m.X * y,
                    centre.Y + u.Y * x + m.Y * y, centre.Z + u.Z * x + m.Z * y));
            }
            return points;
        }

        private static List<Vec3d> ThinEvenly(List<Vec3d> points, int cap)
        {
            if (points == null || points.Count <= cap) return points ?? new List<Vec3d>();
            var result = new List<Vec3d>(cap);
            for (int i = 0; i < cap; i++)
            {
                int index = (int)Math.Round(i * (points.Count - 1.0) / (cap - 1.0));
                result.Add(points[index]);
            }
            return result;
        }

        // --- capped, non-voxel 3D surface sampling -------------------------------------------------

        private static List<SurfaceEmitPoint> VolumeSurfacePoints(GuideData guide) => guide.ShapeType switch
        {
            GuideShapeType.Sphere => SphereSurfacePoints(guide),
            GuideShapeType.Dome => DomeSurfacePoints(guide),
            GuideShapeType.Cylinder => RevolutionSurfacePoints(guide, 1.0),
            GuideShapeType.Cone => RevolutionSurfacePoints(guide, 0.0),
            GuideShapeType.TaperedCylinder => RevolutionSurfacePoints(guide, null),
            GuideShapeType.PolygonalPrism => PolygonalPrismSurfacePoints(guide, tapered: false),
            GuideShapeType.TaperedPolygonalPrism => PolygonalPrismSurfacePoints(guide, tapered: true),
            GuideShapeType.Box => BoxSurfacePoints(guide),
            _ => null
        };

        private static int SurfaceSiteCount(double area) =>
            (int)GameMath.Clamp(Math.Ceiling(Math.Max(0, area) / 4.0), 10, MaxSurfaceEmitPoints);

        private static double Phase(GuideData guide) =>
            ((uint)guide.Id.GetHashCode() / (double)uint.MaxValue) * 2.0 * Math.PI;

        private static bool TryBaseFrame(GuideData guide, out Vec3d centre, out double radius,
            out Vec3d u, out Vec3d m, out Vec3d axis)
        {
            centre = u = m = axis = null; radius = 0;
            if (guide.ControlPoints == null || guide.ControlPoints.Count < 2) return false;
            Vec3d a = guide.ControlPoints[0]?.WorldPosition, b = guide.ControlPoints[1]?.WorldPosition;
            if (a == null || b == null
                || !ShapeGeometry.TryGetFrame(a, b, guide.ShapePlaneAxis, out u, out m, out double len))
                return false;
            centre = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            radius = len * 0.5;
            axis = ShapeGeometry.BaseNormal(u, guide.ShapePlaneAxis);
            return axis != null && radius >= 0.05;
        }

        private static List<SurfaceEmitPoint> SphereSurfacePoints(GuideData guide)
        {
            if (guide.ControlPoints == null || guide.ControlPoints.Count < 2) return null;
            Vec3d a = guide.ControlPoints[0]?.WorldPosition, b = guide.ControlPoints[1]?.WorldPosition;
            if (a == null || b == null) return null;
            var c = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            double r = a.DistanceTo(b) * 0.5;
            if (r < 0.05) return null;

            int count = SurfaceSiteCount(4.0 * Math.PI * r * r);
            double phase = Phase(guide);
            var result = new List<SurfaceEmitPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double y = 1.0 - 2.0 * (i + 0.5) / count;
                double ring = Math.Sqrt(Math.Max(0, 1.0 - y * y));
                double ang = phase + i * GoldenAngle;
                var normal = new Vec3d(Math.Cos(ang) * ring, y, Math.Sin(ang) * ring);
                result.Add(new SurfaceEmitPoint(new Vec3d(
                    c.X + normal.X * r, c.Y + normal.Y * r, c.Z + normal.Z * r), normal));
            }
            return result;
        }

        private static List<SurfaceEmitPoint> DomeSurfacePoints(GuideData guide)
        {
            if (!TryBaseFrame(guide, out Vec3d c, out double r,
                out Vec3d u, out Vec3d m, out Vec3d axis)
                || guide.ControlPoints.Count < 3) return null;

            Vec3d apex = guide.ControlPoints[2]?.WorldPosition;
            if (apex == null) return null;
            var apexDelta = new Vec3d(apex.X - c.X, apex.Y - c.Y, apex.Z - c.Z);
            if (ShapeGeometry.Dot(apexDelta, axis) < 0)
                axis = new Vec3d(-axis.X, -axis.Y, -axis.Z);

            int count = SurfaceSiteCount(2.0 * Math.PI * r * r);
            double phase = Phase(guide);
            var result = new List<SurfaceEmitPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double axial = (i + 0.5) / count;
                double ring = Math.Sqrt(Math.Max(0, 1.0 - axial * axial));
                double ang = phase + i * GoldenAngle;
                double ca = Math.Cos(ang) * ring, sa = Math.Sin(ang) * ring;
                var normal = Normalise(new Vec3d(
                    u.X * ca + m.X * sa + axis.X * axial,
                    u.Y * ca + m.Y * sa + axis.Y * axial,
                    u.Z * ca + m.Z * sa + axis.Z * axial));
                result.Add(new SurfaceEmitPoint(new Vec3d(
                    c.X + normal.X * r, c.Y + normal.Y * r, c.Z + normal.Z * r), normal));
            }
            return result;
        }

        private static List<SurfaceEmitPoint> RevolutionSurfacePoints(GuideData guide, double? topRatio)
        {
            if (!TryBaseFrame(guide, out Vec3d c, out double r,
                out Vec3d u, out Vec3d m, out Vec3d axis)
                || guide.ControlPoints.Count < 3) return null;
            Vec3d heightPoint = guide.ControlPoints[2]?.WorldPosition;
            if (heightPoint == null) return null;
            double h = (heightPoint.X - c.X) * axis.X + (heightPoint.Y - c.Y) * axis.Y
                + (heightPoint.Z - c.Z) * axis.Z;
            if (Math.Abs(h) < 0.05) return null;

            double rTop;
            if (topRatio.HasValue) rTop = r * topRatio.Value;
            else if (guide.ControlPoints.Count >= 4 && guide.ControlPoints[3]?.WorldPosition != null)
                rTop = RadialDistance(guide.ControlPoints[3].WorldPosition, c, axis);
            else rTop = r * TaperedCylinderShape.DefaultTopRatio;
            rTop = Math.Max(0, Math.Min(r * 4.0, rTop));

            double slant = Math.Sqrt(h * h + (rTop - r) * (rTop - r));
            int count = SurfaceSiteCount(Math.PI * (r + rTop) * slant);
            double phase = Phase(guide), slope = (rTop - r) / h;
            var result = new List<SurfaceEmitPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double t = (i + 0.5) / count;
                double ang = phase + i * GoldenAngle;
                var radial = new Vec3d(
                    u.X * Math.Cos(ang) + m.X * Math.Sin(ang),
                    u.Y * Math.Cos(ang) + m.Y * Math.Sin(ang),
                    u.Z * Math.Cos(ang) + m.Z * Math.Sin(ang));
                double localR = r + (rTop - r) * t;
                var normal = Normalise(new Vec3d(
                    radial.X - axis.X * slope,
                    radial.Y - axis.Y * slope,
                    radial.Z - axis.Z * slope));
                result.Add(new SurfaceEmitPoint(new Vec3d(
                    c.X + axis.X * h * t + radial.X * localR,
                    c.Y + axis.Y * h * t + radial.Y * localR,
                    c.Z + axis.Z * h * t + radial.Z * localR), normal));
            }
            return result;
        }

        private static List<SurfaceEmitPoint> PolygonalPrismSurfacePoints(GuideData guide, bool tapered)
        {
            if (guide.ControlPoints == null || guide.ControlPoints.Count < 3) return null;
            Vec3d a = guide.ControlPoints[0]?.WorldPosition, b = guide.ControlPoints[1]?.WorldPosition;
            Vec3d heightPoint = guide.ControlPoints[2]?.WorldPosition;
            if (a == null || b == null || heightPoint == null
                || !ShapeGeometry.TryGetFrame(a, b, guide.ShapePlaneAxis,
                    out Vec3d u, out Vec3d m, out double span)) return null;

            int sides = PolygonShape.ClampSides(guide.Sides);
            double apothemRatio = Math.Cos(Math.PI / sides);
            double near = guide.FlatSideAligned ? apothemRatio : 1.0;
            double far = guide.FlatSideAligned
                ? (sides % 2 == 0 ? apothemRatio : 1.0)
                : (sides % 2 == 0 ? 1.0 : apothemRatio);
            double r = span / (near + far);
            var c = new Vec3d(a.X + u.X * r * near,
                a.Y + u.Y * r * near, a.Z + u.Z * r * near);
            Vec3d axis = ShapeGeometry.BaseNormal(u, guide.ShapePlaneAxis);
            if (axis == null || r < 0.05) return null;

            double h = (heightPoint.X - c.X) * axis.X + (heightPoint.Y - c.Y) * axis.Y
                + (heightPoint.Z - c.Z) * axis.Z;
            if (Math.Abs(h) < 0.05) return null;
            double rTop = tapered && guide.ControlPoints.Count >= 4
                ? RadialDistance(guide.ControlPoints[3].WorldPosition, c, axis) : r;
            rTop = Math.Max(0, Math.Min(r * 4.0, rTop));

            double perimeterAverage = sides * (r + rTop) * Math.Sin(Math.PI / sides);
            double apothemDelta = (rTop - r) * Math.Cos(Math.PI / sides);
            double slant = Math.Sqrt(h * h + apothemDelta * apothemDelta);
            int count = SurfaceSiteCount(perimeterAverage * slant);
            double phase = Phase(guide);
            var result = new List<SurfaceEmitPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double t = (i + 0.5) / count;
                double angle = phase + i * GoldenAngle;
                double dx = Math.Cos(angle), dy = Math.Sin(angle);
                double bestDot = double.MinValue, edgeAngle = 0;
                for (int edge = 0; edge < sides; edge++)
                {
                    double candidate = Math.PI
                        + (2.0 * edge + (guide.FlatSideAligned ? 2.0 : 1.0)) * Math.PI / sides;
                    double dot = dx * Math.Cos(candidate) + dy * Math.Sin(candidate);
                    if (dot > bestDot) { bestDot = dot; edgeAngle = candidate; }
                }

                double localR = r + (rTop - r) * t;
                double rayDistance = localR * Math.Cos(Math.PI / sides) / Math.Max(1e-6, bestDot);
                var radial = new Vec3d(u.X * dx + m.X * dy,
                    u.Y * dx + m.Y * dy, u.Z * dx + m.Z * dy);
                var edgeNormal = new Vec3d(u.X * Math.Cos(edgeAngle) + m.X * Math.Sin(edgeAngle),
                    u.Y * Math.Cos(edgeAngle) + m.Y * Math.Sin(edgeAngle),
                    u.Z * Math.Cos(edgeAngle) + m.Z * Math.Sin(edgeAngle));
                double slope = apothemDelta / h;
                Vec3d normal = Normalise(new Vec3d(edgeNormal.X - axis.X * slope,
                    edgeNormal.Y - axis.Y * slope, edgeNormal.Z - axis.Z * slope));
                result.Add(new SurfaceEmitPoint(new Vec3d(
                    c.X + axis.X * h * t + radial.X * rayDistance,
                    c.Y + axis.Y * h * t + radial.Y * rayDistance,
                    c.Z + axis.Z * h * t + radial.Z * rayDistance), normal));
            }
            return result;
        }

        private static List<SurfaceEmitPoint> BoxSurfacePoints(GuideData guide)
        {
            if (guide.ControlPoints == null || guide.ControlPoints.Count < 3) return null;
            Vec3d a = guide.ControlPoints[0]?.WorldPosition;
            Vec3d diagonal = guide.ControlPoints[1]?.WorldPosition;
            Vec3d heightPoint = guide.ControlPoints[2]?.WorldPosition;
            if (a == null || diagonal == null || heightPoint == null) return null;

            ShapeGeometry.InPlaneAxes(guide.ShapePlaneAxis, out Vec3d u1, out Vec3d u2);
            Vec3d axis = ShapeGeometry.AxisVec(guide.ShapePlaneAxis);
            var d = new Vec3d(diagonal.X - a.X, diagonal.Y - a.Y, diagonal.Z - a.Z);
            double du = ShapeGeometry.Dot(d, u1), dv = ShapeGeometry.Dot(d, u2);
            if (Math.Abs(du) < 0.05 || Math.Abs(dv) < 0.05) return null;
            var bc = new Vec3d(a.X + (u1.X * du + u2.X * dv) * 0.5,
                a.Y + (u1.Y * du + u2.Y * dv) * 0.5,
                a.Z + (u1.Z * du + u2.Z * dv) * 0.5);
            double h = (heightPoint.X - bc.X) * axis.X + (heightPoint.Y - bc.Y) * axis.Y
                + (heightPoint.Z - bc.Z) * axis.Z;
            if (Math.Abs(h) < 0.05) return null;

            double asFace = Math.Abs(dv * h), atFace = Math.Abs(du * h), awFace = Math.Abs(du * dv);
            double total = 2.0 * (asFace + atFace + awFace);
            int count = SurfaceSiteCount(total);
            double phase = Phase(guide) / (2.0 * Math.PI);
            var result = new List<SurfaceEmitPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double selector = (i + 0.5) * total / count;
                double f1 = Frac(phase + i * 0.6180339887498949);
                double f2 = Frac(phase * 0.37 + i * 0.4142135623730950);
                double s, t, w;
                Vec3d normal;
                if ((selector -= asFace) < 0) { s = 0;  t = dv * f1; w = h * f2; normal = Scaled(u1, -Math.Sign(du)); }
                else if ((selector -= asFace) < 0) { s = du; t = dv * f1; w = h * f2; normal = Scaled(u1, Math.Sign(du)); }
                else if ((selector -= atFace) < 0) { s = du * f1; t = 0;  w = h * f2; normal = Scaled(u2, -Math.Sign(dv)); }
                else if ((selector -= atFace) < 0) { s = du * f1; t = dv; w = h * f2; normal = Scaled(u2, Math.Sign(dv)); }
                else if ((selector -= awFace) < 0) { s = du * f1; t = dv * f2; w = 0; normal = Scaled(axis, -Math.Sign(h)); }
                else { s = du * f1; t = dv * f2; w = h; normal = Scaled(axis, Math.Sign(h)); }

                result.Add(new SurfaceEmitPoint(new Vec3d(
                    a.X + u1.X * s + u2.X * t + axis.X * w,
                    a.Y + u1.Y * s + u2.Y * t + axis.Y * w,
                    a.Z + u1.Z * s + u2.Z * t + axis.Z * w), normal));
            }
            return result;
        }

        private static double RadialDistance(Vec3d p, Vec3d c, Vec3d axis)
        {
            double px = p.X - c.X, py = p.Y - c.Y, pz = p.Z - c.Z;
            double axial = px * axis.X + py * axis.Y + pz * axis.Z;
            double rx = px - axis.X * axial, ry = py - axis.Y * axial, rz = pz - axis.Z * axial;
            return Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }

        private static Vec3d Normalise(Vec3d v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return len < 1e-9 ? new Vec3d(0, 1, 0) : new Vec3d(v.X / len, v.Y / len, v.Z / len);
        }

        private static Vec3d Scaled(Vec3d v, double s) => new Vec3d(v.X * s, v.Y * s, v.Z * s);
        private static double Frac(double v) => v - Math.Floor(v);

        // --- particle recipes -----------------------------------------------------------------------

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

        private static void DriftPuff(IWorldAccessor world, Vec3d pos, Vec3d normal,
            float minQuantity, float maxQuantity)
        {
            if (pos == null) return;

            Vec3f minVelocity, maxVelocity;
            if (normal == null)
            {
                // Flat guides read like chalk striking a surface: a broad sideways scatter rather than
                // every mote rising together. Slight vertical variation keeps it dusty instead of planar.
                minVelocity = new Vec3f(-0.34f, -0.05f, -0.34f);
                maxVelocity = new Vec3f(0.34f, 0.16f, 0.34f);
            }
            else
            {
                float vx = (float)(normal.X * 0.12);
                float vy = (float)(normal.Y * 0.12 + 0.12);
                float vz = (float)(normal.Z * 0.12);
                minVelocity = new Vec3f(vx - 0.12f, vy - 0.04f, vz - 0.12f);
                maxVelocity = new Vec3f(vx + 0.12f, vy + 0.20f, vz + 0.12f);
            }

            var dust = new SimpleParticleProperties(
                minQuantity, maxQuantity, FloatingDustColor,
                new Vec3d(pos.X - 0.12, pos.Y - 0.05, pos.Z - 0.12),
                new Vec3d(pos.X + 0.12, pos.Y + 0.12, pos.Z + 0.12),
                minVelocity,
                maxVelocity,
                lifeLength: 1.65f,
                gravityEffect: 0f,
                minSize: 0.14f,
                maxSize: 0.36f,
                model: EnumParticleModel.Quad);

            world.SpawnParticles(dust);
        }
    }
}
