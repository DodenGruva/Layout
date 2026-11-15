using System;
using System.Collections.Generic;
using Layout.Guide;
using Vintagestory.API.MathTools;

namespace Layout.Shapes
{
    internal static class OutlineVoxelizer
    {
        private readonly record struct Cell(int X, int Y, int Z);

        public static void Segment(List<VoxelPosition> cells, HashSet<(int, int, int)> seen,
            Vec3d start, Vec3d end, int scale, OutlineStyle style, Vec3d center = null)
        {
            if (style == OutlineStyle.Legacy)
            {
                VoxelMarch.MarchSegmentInto(cells, seen, start, end, scale);
                return;
            }
            Path(cells, seen, new[] { start, end }, scale, style, false, center);
        }

        public static void Path(List<VoxelPosition> cells, HashSet<(int, int, int)> seen,
            IReadOnlyList<Vec3d> samples, int scale, OutlineStyle style, bool closed,
            Vec3d center = null, Func<bool> cancelled = null, bool samplesAreCellCenters = false)
        {
            if (samples.Count == 0 || scale <= 0) return;
            double units = VoxelGrid.UnitsPerBlock / scale;
            Cell? first = null, previous = null;
            int work = 0;

            if (samplesAreCellCenters)
            {
                foreach (Vec3d sample in samples) Visit(At(sample));
            }
            else if (!closed) Visit(At(samples[0]));
            for (int i = 1; !samplesAreCellCenters && i < samples.Count; i++)
            {
                Vec3d a = samples[i - 1], b = samples[i];
                double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y), dz = Math.Abs(b.Z - a.Z);
                // A thin line takes one cell at each center crossing along the locally dominant axis.
                double start, end;
                if (dx >= dy && dx >= dz) { start = a.X * units; end = b.X * units; }
                else if (dy >= dz) { start = a.Y * units; end = b.Y * units; }
                else { start = a.Z * units; end = b.Z * units; }
                if (start == end) continue;

                int direction = end > start ? 1 : -1;
                double crossing = (direction > 0 ? Math.Ceiling(start - 0.5) : Math.Floor(start - 0.5)) + 0.5;
                for (; direction * (crossing - end) < 0; crossing += direction)
                {
                    VoxelScanCancellation.Checkpoint(ref work, cancelled);
                    double t = (crossing - start) / (end - start);
                    Visit(At(new Vec3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                        a.Z + (b.Z - a.Z) * t)));
                }
            }

            if (closed && first.HasValue) Visit(first.Value);
            else if (closed)
                foreach (Vec3d sample in samples) Visit(At(sample));
            else Visit(At(samples[samples.Count - 1]));

            // Spline interpolation can land just below an integer even when every control
            // lies on the same voxel plane. Keep those samples in that plane's cell layer.
            Cell At(Vec3d p) => new Cell(Snap(p.X), Snap(p.Y), Snap(p.Z));
            int Snap(double coordinate) => (int)Math.Floor(coordinate * units + 1e-7);

            void Emit(Cell cell)
            {
                var key = (cell.X * scale, cell.Y * scale, cell.Z * scale);
                if (seen.Add(key)) cells.Add(new VoxelPosition(key.Item1, key.Item2, key.Item3));
            }

            void Visit(Cell target)
            {
                first ??= target;
                Cell cell = previous ?? target;
                while (cell != target)
                {
                    VoxelScanCancellation.Checkpoint(ref work, cancelled);
                    var next = new Cell(cell.X + Math.Sign(target.X - cell.X),
                        cell.Y + Math.Sign(target.Y - cell.Y), cell.Z + Math.Sign(target.Z - cell.Z));
                    if (style == OutlineStyle.FaceConnected) Bridge(cell, next);
                    cell = next;
                    Emit(cell);
                }
                Emit(target);
                previous = target;
            }

            double DistanceSquared(Cell cell)
            {
                double x = cell.X + 0.5 - center.X * units;
                double y = cell.Y + 0.5 - center.Y * units;
                double z = cell.Z + 0.5 - center.Z * units;
                return x * x + y * y + z * z;
            }

            void Bridge(Cell from, Cell to)
            {
                // For curved rims, favor the outside of the step. Equal choices are mirrors
                // at a grid-aligned symmetry axis, so include both.
                while (true)
                {
                    bool x = from.X != to.X, y = from.Y != to.Y, z = from.Z != to.Z;
                    if ((x ? 1 : 0) + (y ? 1 : 0) + (z ? 1 : 0) <= 1) return;
                    var alongX = new Cell(to.X, from.Y, from.Z);
                    var alongY = new Cell(from.X, to.Y, from.Z);
                    var alongZ = new Cell(from.X, from.Y, to.Z);
                    if (center == null)
                    {
                        // Order by absolute coordinates so reversing a straight segment gives the same bridge.
                        Cell low = Compare(from, to) <= 0 ? from : to;
                        Cell high = low == from ? to : from;
                        while (low != high)
                        {
                            low = low.X != high.X ? low with { X = high.X }
                                : low.Y != high.Y ? low with { Y = high.Y } : high;
                            Emit(low);
                        }
                        return;
                    }

                    double sx = x ? DistanceSquared(alongX) : double.NegativeInfinity;
                    double sy = y ? DistanceSquared(alongY) : double.NegativeInfinity;
                    double sz = z ? DistanceSquared(alongZ) : double.NegativeInfinity;
                    double best = Math.Max(sx, Math.Max(sy, sz));
                    if (x && best - sx <= 1e-9) Emit(alongX);
                    if (y && best - sy <= 1e-9) Emit(alongY);
                    if (z && best - sz <= 1e-9) Emit(alongZ);
                    from = sx == best ? alongX : sy == best ? alongY : alongZ;
                }
            }
        }

        private static int Compare(Cell a, Cell b) =>
            a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);

        public static List<VoxelPosition> Circle(Vec3d center, double radius, PlaneAxis plane,
            int scale, OutlineStyle style, Func<bool> cancelled)
        {
            double cell = scale / VoxelGrid.UnitsPerBlock;
            double u = (plane == PlaneAxis.X ? center.Y : center.X) / cell;
            double v = (plane == PlaneAxis.Z ? center.Y : center.Z) / cell;
            double r = radius / cell;
            double cut = r / Math.Sqrt(2);
            int uOrigin = (int)Math.Floor(u), vOrigin = (int)Math.Floor(v);
            u -= uOrigin;
            v -= vOrigin;
            int work = 0;
            var samples = new List<Vec3d>();

            // In each octant, visit the circle at cell centers along its dominant axis.
            // Quantize around the local origin so mirrored ties stay stable far from spawn.
            Octant(false, 1, 1, false);
            Octant(true, 1, 1, true);
            Octant(true, -1, 1, false);
            Octant(false, 1, -1, true);
            Octant(false, -1, -1, false);
            Octant(true, -1, -1, true);
            Octant(true, 1, -1, false);
            Octant(false, -1, 1, true);

            if (samples.Count == 0) samples.Add(center);
            var result = new List<VoxelPosition>();
            Path(result, new HashSet<(int, int, int)>(), samples, scale, style, closed: true,
                center, cancelled, samplesAreCellCenters: true);
            return result;

            void Octant(bool alongU, int nearSide, int farSide, bool reverse)
            {
                double nearCenter = alongU ? u : v;
                double limit = nearCenter + nearSide * cut;
                int min = (int)Math.Ceiling(Math.Min(nearCenter, limit) - 0.5);
                int max = (int)Math.Floor(Math.Max(nearCenter, limit) - 0.5);
                for (int i = 0; i <= max - min; i++)
                {
                    VoxelScanCancellation.Checkpoint(ref work, cancelled);
                    double near = (reverse == (nearSide > 0) ? max - i : min + i) + 0.5;
                    double other = near - nearCenter;
                    double far = (alongU ? v : u) + farSide * Math.Sqrt(r * r - other * other);
                    // An exact lattice tie belongs to the cell toward the center on each side.
                    far = Math.Floor(far - farSide * 1e-9) + 0.5;
                    double x = (uOrigin + (alongU ? near : far)) * cell;
                    double y = (vOrigin + (alongU ? far : near)) * cell;
                    samples.Add(plane switch
                    {
                        PlaneAxis.X => new Vec3d(center.X, x, y),
                        PlaneAxis.Z => new Vec3d(x, y, center.Z),
                        _ => new Vec3d(x, center.Y, y)
                    });
                }
            }
        }
    }
}
