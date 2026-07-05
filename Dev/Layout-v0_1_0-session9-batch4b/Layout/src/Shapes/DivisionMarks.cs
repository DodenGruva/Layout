using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The Divisions feature (Session 9): recolors the voxels at the boundaries that split a guide into N
    /// equal parts BY ARC LENGTH along its curve/perimeter — purely a visual reference for the player.
    /// Applied by the RENDERER after the shape produces its cells, never by the shapes themselves, so it
    /// touches no geometry, no counts, no caps, and no shape contract: it is a recolor of existing cells
    /// (Division marks never override the functional Anchor/Primary/Locked markers, per the settled
    /// precedence in <see cref="ShapeGeometry"/>).
    /// </summary>
    public static class DivisionMarks
    {
        /// <summary>Sanity ceiling — the GUI clamps to this too (typing "9999" shouldn't paint the world).</summary>
        public const int MaxDivisions = 256;

        /// <summary>
        /// Paints the part boundaries onto <paramref name="cells"/>. <paramref name="curve"/> is the
        /// shape's dense <c>SampleCurve</c> polyline; closed shapes are auto-detected by the settled
        /// convention (last sample == first). Open shapes get the N−1 interior boundaries (the endpoints
        /// are already anchors); closed loops get all N (the one at the seam loses to the anchor marker
        /// by precedence, which is correct — the anchor IS that boundary).
        /// </summary>
        public static void Apply(List<VoxelPosition> cells, List<Vec3d> curve, int divisions, int scale)
        {
            if (divisions < 2 || cells == null || cells.Count == 0 || curve == null || curve.Count < 2) return;
            int n = Math.Min(divisions, MaxDivisions);

            var cum = new double[curve.Count];
            for (int i = 1; i < curve.Count; i++)
                cum[i] = cum[i - 1] + ShapeGeometry.Dist(curve[i - 1], curve[i]);
            double total = cum[curve.Count - 1];
            if (total < 1e-9) return;

            bool closed = ShapeGeometry.Dist(curve[0], curve[curve.Count - 1]) < 1e-6;
            int first = closed ? 0 : 1;
            int last = n - 1;

            for (int k = first; k <= last; k++)
            {
                double target = (double)k / n * total;
                Vec3d p = PointAtLength(curve, cum, target);
                ShapeGeometry.ClaimMarker(cells, scale, p, VoxelRenderType.Division);
            }
        }

        private static Vec3d PointAtLength(List<Vec3d> curve, double[] cum, double target)
        {
            for (int i = 1; i < curve.Count; i++)
            {
                if (target <= cum[i] || i == curve.Count - 1)
                {
                    double seg = cum[i] - cum[i - 1];
                    double f = seg < 1e-9 ? 0 : (target - cum[i - 1]) / seg;
                    return ShapeGeometry.Lerp(curve[i - 1], curve[i], f < 0 ? 0 : f > 1 ? 1 : f);
                }
            }
            return new Vec3d(curve[0].X, curve[0].Y, curve[0].Z);
        }
    }
}
