using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// Shared point-sequence → voxel-cell marching (Session 8). The same convention CatmullRomSpline uses
    /// internally: world position → guide grid → snap DOWN to the scale-aligned lower corner; a shared
    /// seen-set gives first-claim dedupe while preserving discovery order. Extracted so the arc, ellipse,
    /// and fill samplers voxelise identically to the spline (Module-1 invariant: same cell math everywhere,
    /// or the cap check and the visible guide disagree at the boundary).
    /// </summary>
    internal static class VoxelMarch
    {
        /// <summary>Appends the cells visited by walking <paramref name="samples"/> in order.</summary>
        public static void MarchInto(
            List<VoxelPosition> into, HashSet<(int, int, int)> seen,
            IReadOnlyList<Vec3d> samples, int scale)
        {
            if (samples == null || scale <= 0) return;
            for (int i = 0; i < samples.Count; i++)
                Claim(into, seen, samples[i], scale);
        }

        /// <summary>
        /// Appends the cells along the straight segment a→b, stepping at ≤ half a cell so no cell on the
        /// line can be skipped. Used by the ruled fill (curve → chord) and the ellipse's radial fill.
        /// </summary>
        public static void MarchSegmentInto(
            List<VoxelPosition> into, HashSet<(int, int, int)> seen,
            Vec3d a, Vec3d b, int scale, Func<bool> cancellationRequested = null)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double cell = scale / VoxelGrid.UnitsPerBlock;
            int steps = Math.Max(1, (int)Math.Ceiling(len / (cell * 0.5)));
            for (int i = 0; i <= steps; i++)
            {
                if ((i & 2047) == 0) VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
                double f = (double)i / steps;
                Claim(into, seen,
                    new Vec3d(a.X + dx * f, a.Y + dy * f, a.Z + dz * f), scale);
            }
        }

        private static void Claim(List<VoxelPosition> into, HashSet<(int, int, int)> seen, Vec3d p, int scale)
        {
            // EXACTLY CatmullRomSpline.Quantize: lower-corner guide grid cell snapped to a multiple of
            // scale, Math.Floor (not truncation) so negative coordinates stay correct.
            int x = (int)Math.Floor(p.X * VoxelGrid.UnitsPerBlock / scale) * scale;
            int y = (int)Math.Floor(p.Y * VoxelGrid.UnitsPerBlock / scale) * scale;
            int z = (int)Math.Floor(p.Z * VoxelGrid.UnitsPerBlock / scale) * scale;
            if (seen.Add((x, y, z)))
                into.Add(new VoxelPosition(x, y, z, VoxelRenderType.Normal));
        }
    }
}
