using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// Exact hollow sphere/hemisphere voxelisation without scanning the sphere's full bounding cube.
    /// Walks X/Y columns, solves the outer and inner Z boundaries analytically, then applies the same
    /// nearest-point/farthest-corner predicate as the original cubic scan to a narrow candidate band.
    /// </summary>
    internal static class SphericalShellScan
    {
        /// <summary>
        /// Counts or collects shell cells. <paramref name="halfSpaceNormal"/> clips to the half-space on
        /// that normal's side (the Dome path); null produces a full sphere. When <paramref name="output"/>
        /// is null, counting stops as soon as <paramref name="stopAfter"/> is exceeded.
        /// </summary>
        internal static int Scan(
            Vec3d centre,
            double radius,
            int scale,
            Vec3d halfSpaceNormal,
            int stopAfter,
            List<VoxelPosition> output)
            => Scan(centre, radius, scale, halfSpaceNormal, stopAfter, output, null);

        internal static int Scan(
            Vec3d centre,
            double radius,
            int scale,
            Vec3d halfSpaceNormal,
            int stopAfter,
            List<VoxelPosition> output,
            Func<bool> cancellationRequested)
            => ScanCore(
                centre, radius, scale, halfSpaceNormal, stopAfter,
                output, null, cancellationRequested);

        internal static void ScanProgressively(
            Vec3d centre,
            double radius,
            int scale,
            Vec3d halfSpaceNormal,
            ProgressiveVoxelCollector collector)
        {
            if (collector == null) throw new ArgumentNullException(nameof(collector));
            ScanCore(
                centre, radius, scale, halfSpaceNormal, int.MaxValue,
                null, collector, null);
        }

        private static int ScanCore(
            Vec3d centre,
            double radius,
            int scale,
            Vec3d halfSpaceNormal,
            int stopAfter,
            List<VoxelPosition> output,
            ProgressiveVoxelCollector collector,
            Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            stopAfter = Math.Max(0, stopAfter);
            double cell = scale / 16.0;
            double r2 = radius * radius;
            bool clipped = halfSpaceNormal != null;
            double spread = clipped
                ? (Math.Abs(halfSpaceNormal.X) + Math.Abs(halfSpaceNormal.Y) +
                   Math.Abs(halfSpaceNormal.Z)) * cell * 0.5
                : 0.0;
            int count = 0;
            int work = 0;

            int min16X = AlignDown(centre.X - radius, scale);
            int max16X = AlignDown(centre.X + radius, scale);
            int min16Y = AlignDown(centre.Y - radius, scale);
            int max16Y = AlignDown(centre.Y + radius, scale);
            int globalMin16Z = AlignDown(centre.Z - radius, scale);
            int globalMax16Z = AlignDown(centre.Z + radius, scale);

            int xCount = (max16X - min16X) / scale + 1;
            int yCount = (max16Y - min16Y) / scale + 1;
            int columnCount = checked(xCount * yCount);
            int[] columnOrder = collector == null
                ? null : ProgressiveVoxelOrder.ShuffledIndices(
                    columnCount, xCount * 73856093 ^ yCount * 19349663);
            int visitCount = columnOrder == null ? columnCount : columnOrder.Length;
            for (int visit = 0; visit < visitCount; visit++)
            {
                if (collector != null) collector.ThrowIfCancellationRequested();
                else VoxelScanCancellation.Checkpoint(ref work, cancellationRequested);
                int column = columnOrder == null ? visit : columnOrder[visit];
                int xi = column / yCount;
                int yi = column % yCount;
                int ix = min16X + xi * scale;
                int iy = min16Y + yi * scale;
                double lox = ix / 16.0;
                double nx = Nearest(centre.X, lox, lox + cell);
                double fx = Farthest(centre.X, lox, lox + cell);
                double ccx = lox + cell * 0.5 - centre.X;
                double loy = iy / 16.0;
                double ny = Nearest(centre.Y, loy, loy + cell);
                double fy = Farthest(centre.Y, loy, loy + cell);
                double nxy2 = nx * nx + ny * ny;
                if (nxy2 > r2) continue;

                double fxy2 = fx * fx + fy * fy;
                double outerZ = Math.Sqrt(Math.Max(0.0, r2 - nxy2));
                int outerMin16Z = Math.Max(
                    globalMin16Z,
                    AlignDown(centre.Z - outerZ, scale) - scale);
                int outerMax16Z = Math.Min(
                    globalMax16Z,
                    AlignDown(centre.Z + outerZ, scale));
                if (outerMin16Z > outerMax16Z) continue;

                double ccy = loy + cell * 0.5 - centre.Y;

                // If the X/Y cell itself crosses the spherical surface, every Z cell intersecting
                // the ball can belong to the shell. Otherwise skip the provably interior middle of
                // the column and visit only narrow bands around the two surface crossings.
                if (fxy2 >= r2)
                {
                    if (VisitRange(
                            ix, iy, outerMin16Z, outerMax16Z, scale,
                            centre, halfSpaceNormal, clipped, spread,
                            nxy2, fxy2, ccx, ccy, r2,
                            stopAfter, output, collector, ref count,
                            ref work, cancellationRequested))
                        return Exceeded(stopAfter);
                    continue;
                }

                double innerZ = Math.Sqrt(r2 - fxy2);
                int lowerEnd16Z = Math.Min(
                    outerMax16Z,
                    AlignDown(centre.Z - innerZ, scale) + scale);
                if (VisitRange(
                        ix, iy, outerMin16Z, lowerEnd16Z, scale,
                        centre, halfSpaceNormal, clipped, spread,
                        nxy2, fxy2, ccx, ccy, r2,
                        stopAfter, output, collector, ref count,
                        ref work, cancellationRequested))
                    return Exceeded(stopAfter);

                int upperStart16Z = Math.Max(
                    outerMin16Z,
                    AlignDown(centre.Z + innerZ, scale) - scale);
                // Avoid duplicate visits where the conservative lower/upper bands overlap.
                upperStart16Z = Math.Max(upperStart16Z, lowerEnd16Z + scale);
                if (VisitRange(
                        ix, iy, upperStart16Z, outerMax16Z, scale,
                        centre, halfSpaceNormal, clipped, spread,
                        nxy2, fxy2, ccx, ccy, r2,
                        stopAfter, output, collector, ref count,
                        ref work, cancellationRequested))
                    return Exceeded(stopAfter);
            }

            return count;
        }

        // Returns true once the threshold has been crossed in count-only mode.
        private static bool VisitRange(
            int ix,
            int iy,
            int first16Z,
            int last16Z,
            int scale,
            Vec3d centre,
            Vec3d halfSpaceNormal,
            bool clipped,
            double spread,
            double nxy2,
            double fxy2,
            double ccx,
            double ccy,
            double r2,
            int stopAfter,
            List<VoxelPosition> output,
            ProgressiveVoxelCollector collector,
            ref int count,
            ref int work,
            Func<bool> cancellationRequested)
        {
            if (first16Z > last16Z) return false;
            double cell = scale / 16.0;

            for (int iz = first16Z; iz <= last16Z; iz += scale)
            {
                if (collector == null)
                    VoxelScanCancellation.Checkpoint(ref work, cancellationRequested);
                double loz = iz / 16.0;
                double nz = Nearest(centre.Z, loz, loz + cell);
                if (nxy2 + nz * nz > r2) continue;

                double fz = Farthest(centre.Z, loz, loz + cell);
                if (fxy2 + fz * fz < r2) continue;

                if (clipped)
                {
                    double ccz = loz + cell * 0.5 - centre.Z;
                    double sc = ccx * halfSpaceNormal.X +
                                ccy * halfSpaceNormal.Y +
                                ccz * halfSpaceNormal.Z;
                    if (sc + spread < 0) continue;
                }

                count++;
                if (collector != null)
                    collector.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                else if (output != null)
                    output.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                else if (count > stopAfter)
                    return true;
            }

            return false;
        }

        private static int Exceeded(int stopAfter) =>
            stopAfter == int.MaxValue ? int.MaxValue : stopAfter + 1;

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        private static double Nearest(double centre, double lo, double hi) =>
            centre < lo ? lo - centre : centre > hi ? centre - hi : 0.0;

        private static double Farthest(double centre, double lo, double hi) =>
            Math.Max(Math.Abs(centre - lo), Math.Abs(centre - hi));
    }
}
