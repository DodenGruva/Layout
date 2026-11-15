using System;
using System.Collections.Generic;
using Layout.Guide;
using Vintagestory.API.MathTools;

namespace Layout.Shapes
{
    /// <summary>Cell coverage of an axis-aligned circle, computed one row at a time.</summary>
    internal readonly struct CircleRaster
    {
        private readonly double _u, _v, _radiusSquared;
        private readonly int _originU, _originV, _layer;
        public PlaneAxis Axis { get; }

        public CircleRaster(PlaneAxis axis, Vec3d center, Vec3d halfDiameter, double cell)
        {
            Axis = axis;
            double u = (axis == PlaneAxis.X ? center.Y : center.X) / cell;
            double v = (axis == PlaneAxis.Z ? center.Y : center.Z) / cell;
            // Round local distances before adding the world-cell offset. Far from the world
            // origin, adding that offset first can swallow the fraction of a near-tangent cell.
            _originU = (int)Math.Floor(u);
            _originV = (int)Math.Floor(v);
            _u = u - _originU;
            _v = v - _originV;
            _layer = (int)Math.Floor(Coordinate(center, axis) / cell);
            double x = halfDiameter.X / cell, y = halfDiameter.Y / cell, z = halfDiameter.Z / cell;
            _radiusSquared = x * x + y * y + z * z;
        }

        public static double Coordinate(Vec3d point, PlaneAxis axis) =>
            axis == PlaneAxis.X ? point.X : axis == PlaneAxis.Y ? point.Y : point.Z;

        private readonly record struct Row(int V, int Left, int Right, int InnerLeft, int InnerRight);

        private IEnumerable<Row> Rows()
        {
            double radius = Math.Sqrt(_radiusSquared);
            int end = (int)Math.Ceiling(_v + radius);
            for (int row = (int)Math.Floor(_v - radius); row < end; row++)
            {
                double near = Math.Max(0, Math.Max(row - _v, _v - (row + 1)));
                double outer = _radiusSquared - near * near;
                if (outer <= 0) continue;
                double width = Math.Sqrt(outer);
                int left = (int)Math.Floor(_u - width);
                int right = (int)Math.Ceiling(_u + width);

                // Remove cells entirely inside the curve from the hollow ring. A cell touched
                // only at an edge or corner contributes no extra voxel on either side.
                double far = Math.Max(Math.Abs(row - _v), Math.Abs(row + 1 - _v));
                double inner = _radiusSquared - far * far;
                int innerLeft = right, innerRight = right;
                if (inner > 0)
                {
                    double inside = Math.Sqrt(inner);
                    int first = Math.Max(left, (int)Math.Ceiling(_u - inside));
                    int last = Math.Min(right, (int)Math.Floor(_u + inside));
                    if (first < last) { innerLeft = first; innerRight = last; }
                }
                yield return new Row(row + _originV, left + _originU, right + _originU,
                    innerLeft + _originU, innerRight + _originU);
            }
        }

        public int CountUpTo(bool filled, int stopAfter, Func<bool> cancelled)
        {
            long count = 0;
            int work = 0;
            foreach (Row row in Rows())
            {
                VoxelScanCancellation.Checkpoint(ref work, cancelled);
                count += (long)row.Right - row.Left;
                if (!filled) count -= row.InnerRight - row.InnerLeft;
                if (count > stopAfter) return GuideShapeVoxelCounting.Exceeded(stopAfter);
            }
            return (int)count;
        }

        public void Append(List<VoxelPosition> result, int scale, bool interior, Func<bool> cancelled)
        {
            int work = 0;
            int layer = _layer * scale;
            PlaneAxis axis = Axis;
            foreach (Row row in Rows())
            {
                VoxelScanCancellation.Checkpoint(ref work, cancelled);
                if (interior) AppendRun(row.InnerLeft, row.InnerRight, row.V);
                else
                {
                    AppendRun(row.Left, row.InnerLeft, row.V);
                    AppendRun(row.InnerRight, row.Right, row.V);
                }
            }

            void AppendRun(int start, int end, int row)
            {
                for (int column = start; column < end; column++)
                {
                    VoxelScanCancellation.Checkpoint(ref work, cancelled);
                    int u = column * scale, v = row * scale;
                    result.Add(axis switch
                    {
                        PlaneAxis.X => new VoxelPosition(layer, u, v),
                        PlaneAxis.Z => new VoxelPosition(u, v, layer),
                        _ => new VoxelPosition(u, layer, v)
                    });
                }
            }
        }
    }
}
