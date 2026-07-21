using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// Surface-proportional fallback for volumes whose legacy bounding-box scan would be excessive.
    /// It walks only rings or faces, so work grows with visible shell area instead of empty interior volume.
    /// </summary>
    internal static class LargeVolumeShellFallback
    {
        public static List<VoxelPosition> RadialShell(
            Vec3d centre, double baseRadius, Vec3d u, Vec3d m, Vec3d axis, double height,
            bool taperedToPoint, int scale, int stopAfter, out bool exceeded)
        {
            var result = new List<VoxelPosition>();
            var seen = new HashSet<(int, int, int)>();
            exceeded = false;
            if (centre == null || u == null || m == null || axis == null || scale <= 0) return result;

            double cell = scale / 16.0;
            int axialSteps = Math.Max(1, (int)Math.Ceiling(Math.Abs(height) / (cell * 0.5)));
            for (int axial = 0; axial <= axialSteps; axial++)
            {
                double f = (double)axial / axialSteps;
                double radius = taperedToPoint ? baseRadius * (1.0 - f) : baseRadius;
                Vec3d ringCentre = Add(centre, axis, height * f);
                int around = Math.Max(8,
                    (int)Math.Ceiling(2.0 * Math.PI * Math.Max(radius, cell) / (cell * 0.75)));

                for (int segment = 0; segment < around; segment++)
                {
                    double a0 = 2.0 * Math.PI * segment / around;
                    double a1 = 2.0 * Math.PI * (segment + 1) / around;
                    Vec3d p0 = RingPoint(ringCentre, u, m, radius, a0);
                    Vec3d p1 = RingPoint(ringCentre, u, m, radius, a1);
                    if (!MarchSegment(result, seen, p0, p1, scale, stopAfter))
                    {
                        exceeded = true;
                        return result;
                    }
                }
            }
            return result;
        }

        public static List<VoxelPosition> BoxShell(
            Vec3d origin, Vec3d u, Vec3d v, Vec3d axis,
            double uLength, double vLength, double height,
            int scale, int stopAfter, out bool exceeded)
        {
            var result = new List<VoxelPosition>();
            var seen = new HashSet<(int, int, int)>();
            exceeded = false;
            if (origin == null || u == null || v == null || axis == null || scale <= 0) return result;

            Vec3d du = Scale(u, uLength), dv = Scale(v, vLength), dh = Scale(axis, height);
            if (!Face(result, seen, origin, du, dv, scale, stopAfter)
                || !Face(result, seen, Add(origin, dh), du, dv, scale, stopAfter)
                || !Face(result, seen, origin, du, dh, scale, stopAfter)
                || !Face(result, seen, Add(origin, dv), du, dh, scale, stopAfter)
                || !Face(result, seen, origin, dv, dh, scale, stopAfter)
                || !Face(result, seen, Add(origin, du), dv, dh, scale, stopAfter))
            {
                exceeded = true;
            }
            return result;
        }

        private static bool Face(List<VoxelPosition> result, HashSet<(int, int, int)> seen,
            Vec3d origin, Vec3d across, Vec3d down, int scale, int stopAfter)
        {
            double cell = scale / 16.0;
            int rows = Math.Max(1, (int)Math.Ceiling(Length(down) / (cell * 0.5)));
            for (int row = 0; row <= rows; row++)
            {
                Vec3d start = Add(origin, down, (double)row / rows);
                if (!MarchSegment(result, seen, start, Add(start, across), scale, stopAfter)) return false;
            }
            return true;
        }

        private static bool MarchSegment(List<VoxelPosition> result, HashSet<(int, int, int)> seen,
            Vec3d a, Vec3d b, int scale, int stopAfter)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double cell = scale / 16.0;
            int steps = Math.Max(1, (int)Math.Ceiling(length / (cell * 0.5)));
            for (int step = 0; step <= steps; step++)
            {
                double f = (double)step / steps;
                int x = (int)Math.Floor((a.X + dx * f) * 16.0 / scale) * scale;
                int y = (int)Math.Floor((a.Y + dy * f) * 16.0 / scale) * scale;
                int z = (int)Math.Floor((a.Z + dz * f) * 16.0 / scale) * scale;
                if (!seen.Add((x, y, z))) continue;
                result.Add(new VoxelPosition(x, y, z, VoxelRenderType.Normal));
                if (result.Count > stopAfter) return false;
            }
            return true;
        }

        private static Vec3d RingPoint(Vec3d centre, Vec3d u, Vec3d m, double radius, double angle) =>
            new Vec3d(
                centre.X + radius * (u.X * Math.Cos(angle) + m.X * Math.Sin(angle)),
                centre.Y + radius * (u.Y * Math.Cos(angle) + m.Y * Math.Sin(angle)),
                centre.Z + radius * (u.Z * Math.Cos(angle) + m.Z * Math.Sin(angle)));

        private static Vec3d Scale(Vec3d value, double amount) =>
            new Vec3d(value.X * amount, value.Y * amount, value.Z * amount);

        private static Vec3d Add(Vec3d a, Vec3d b) =>
            new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        private static Vec3d Add(Vec3d a, Vec3d direction, double amount) =>
            new Vec3d(a.X + direction.X * amount, a.Y + direction.Y * amount, a.Z + direction.Z * amount);

        private static double Length(Vec3d value) =>
            Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
    }
}
