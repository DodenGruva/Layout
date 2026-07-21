using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>Canonical shape wire topology → selected-scale guide voxels.</summary>
    public static class ShapeWireframe
    {
        public static List<VoxelPosition> GetVoxelPositions(IGuideShape shape, int scale)
        {
            var voxels = new List<VoxelPosition>();
            if (shape == null || scale <= 0) return voxels;

            List<Vec3d> curve = shape.SampleCurve(128);
            var seen = new HashSet<(int, int, int)>();
            if (curve != null)
            {
                if (curve.Count == 1) VoxelMarch.MarchInto(voxels, seen, curve, scale);
                else for (int i = 1; i < curve.Count; i++)
                {
                    Vec3d a = curve[i - 1], b = curve[i];
                    if (a == null || b == null) continue;
                    VoxelMarch.MarchSegmentInto(voxels, seen, a, b, scale);
                }
            }

            List<ControlPoint> points = shape.ControlPoints;
            if (points != null)
                for (int i = 0; i < points.Count; i++)
                {
                    ControlPoint point = points[i];
                    if (point?.WorldPosition == null || point.IsPhantom) continue;
                    VoxelRenderType type = point.IsLocked ? VoxelRenderType.Locked
                        : point.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                    ShapeGeometry.ClaimMarker(voxels, scale, point.WorldPosition, type);
                }
            return voxels;
        }

        public static int GetVoxelCount(IGuideShape shape, int scale) =>
            GetVoxelPositions(shape, scale).Count;
    }
}
