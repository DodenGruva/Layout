using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The rectangle primitive (Session 9): the two draft clicks are its DIAGONAL corners A and C — the
    /// only stored control points (both anchors). The other two corners derive in the intrinsic
    /// axis-aligned plane (normal = <see cref="GuideData.ShapePlaneAxis"/>, from the first click's face)
    /// and render as Primary/green markers. A SQUARE is this shape under
    /// <see cref="ShapeConstraint.Square"/>: the derived corners use the DOMINANT diagonal component for
    /// both sides, so dragging a corner absorbs by resizing the square.
    /// </summary>
    /// <remarks>
    /// GEOMETRY. The rectangle lies in A's plane layer: C's in-plane components define the sides; C is
    /// snapped onto A's plane by <see cref="MoveControlPoint"/> (moving A re-snaps C onto A's NEW layer,
    /// keeping its in-plane offsets — the whole rectangle rides with A). Corners in curve order:
    /// A → B(=A+du·u1) → C → D(=A+dv·u2) → A.
    ///
    /// [Flagged decisions:] the derived corners are markers, not grabbable points (body clicks map to the
    /// nearest STORED anchor); Square has no break gesture in v1 (corner drags absorb by design, so
    /// square → rectangle demotion never triggers — the two live as separate catalog tiles like
    /// circle/ellipse); Square's side = max(|du|,|dv|) with signs preserved (the dominant drag dimension).
    ///
    /// Fill = in-plane scanlines (chord marching at ≤ half-cell steps — exact box). No phantoms.
    /// </remarks>
    public sealed class RectangleShape : IGuideShape
    {
        private const double MinSide = 0.05;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _planeAxis;
        private ShapeConstraint _constraint;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => _constraint;

        /// <summary>Creates a fresh rectangle from the two draft clicks (the diagonal).</summary>
        public RectangleShape(Vec3d a, Vec3d c, PlaneAxis planeAxis, ShapeConstraint constraint)
        {
            _planeAxis = planeAxis;
            _constraint = constraint == ShapeConstraint.Square ? ShapeConstraint.Square : ShapeConstraint.None;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(c.X, c.Y, c.Z), isAnchor: true)
            };
            SnapFarCorner();
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public RectangleShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis, ShapeConstraint constraint)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _planeAxis = planeAxis;
            _constraint = constraint == ShapeConstraint.Square ? ShapeConstraint.Square : ShapeConstraint.None;
        }

        // --- geometry ------------------------------------------------------------------------------

        // The four corners in curve order A → B → C' → D (C' = the derived far corner, where the stored C
        // is kept snapped). du/dv are C's signed in-plane offsets from A (Square: dominant for both).
        private bool TryCorners(out Vec3d a, out Vec3d b, out Vec3d c, out Vec3d d)
        {
            a = b = c = d = null;
            if (_controlPoints.Count < 2) return false;
            a = _controlPoints[0].WorldPosition;
            Vec3d cStored = _controlPoints[1].WorldPosition;

            ShapeGeometry.InPlaneAxes(_planeAxis, out Vec3d u1, out Vec3d u2);
            var diag = new Vec3d(cStored.X - a.X, cStored.Y - a.Y, cStored.Z - a.Z);
            double du = ShapeGeometry.Dot(diag, u1);
            double dv = ShapeGeometry.Dot(diag, u2);
            if (Math.Abs(du) < MinSide || Math.Abs(dv) < MinSide) return false;

            if (_constraint == ShapeConstraint.Square)
            {
                double side = Math.Max(Math.Abs(du), Math.Abs(dv));
                du = Math.Sign(du) * side;
                dv = Math.Sign(dv) * side;
            }

            b = new Vec3d(a.X + u1.X * du, a.Y + u1.Y * du, a.Z + u1.Z * du);
            d = new Vec3d(a.X + u2.X * dv, a.Y + u2.Y * dv, a.Z + u2.Z * dv);
            c = new Vec3d(b.X + u2.X * dv, b.Y + u2.Y * dv, b.Z + u2.Z * dv);
            return true;
        }

        /// <summary>Keeps the stored far anchor exactly ON the derived far corner (plane snap + Square).</summary>
        private void SnapFarCorner()
        {
            if (TryCorners(out _, out _, out Vec3d c, out _))
                _controlPoints[1].SetPosition(c.X, c.Y, c.Z);
        }

        private bool TryPerimeter(out Vec3d[] corners, out double[] cum)
        {
            corners = null; cum = null;
            if (!TryCorners(out Vec3d a, out Vec3d b, out Vec3d c, out Vec3d d)) return false;
            corners = new[] { a, b, c, d, a };
            cum = new double[5];
            for (int i = 1; i < 5; i++)
                cum[i] = cum[i - 1] + ShapeGeometry.Dist(corners[i - 1], corners[i]);
            return cum[4] > 1e-9;
        }

        private static Vec3d PerimeterPoint(Vec3d[] corners, double[] cum, double t)
        {
            double target = (t < 0 ? 0 : t > 1 ? 1 : t) * cum[cum.Length - 1];
            for (int i = 1; i < corners.Length; i++)
            {
                if (target <= cum[i] || i == corners.Length - 1)
                {
                    double seg = cum[i] - cum[i - 1];
                    double f = seg < 1e-9 ? 0 : (target - cum[i - 1]) / seg;
                    return ShapeGeometry.Lerp(corners[i - 1], corners[i], f < 0 ? 0 : f > 1 ? 1 : f);
                }
            }
            return new Vec3d(corners[0].X, corners[0].Y, corners[0].Z);
        }

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            var result = new List<VoxelPosition>();
            if (!TryCorners(out Vec3d a, out Vec3d b, out Vec3d c, out Vec3d d)) return result;

            var seen = new HashSet<(int, int, int)>();
            VoxelMarch.MarchSegmentInto(result, seen, a, b, scale);
            VoxelMarch.MarchSegmentInto(result, seen, b, c, scale);
            VoxelMarch.MarchSegmentInto(result, seen, c, d, scale);
            VoxelMarch.MarchSegmentInto(result, seen, d, a, scale);

            if (filled)
            {
                // In-plane scanlines: chords parallel to A→B marched across the D-direction at ≤ half-cell.
                double cell = scale / 16.0;
                double span = ShapeGeometry.Dist(a, d);
                int lines = Math.Max(2, Math.Min(8192, (int)Math.Ceiling(span / (cell * 0.5))));
                for (int i = 0; i <= lines; i++)
                {
                    double f = (double)i / lines;
                    VoxelMarch.MarchSegmentInto(result, seen,
                        ShapeGeometry.Lerp(a, d, f), ShapeGeometry.Lerp(b, c, f), scale);
                }
            }

            // Stored anchors A and C', then the derived corners as Primary markers (visual references).
            for (int i = 0; i < 2 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition,
                    cp.IsLocked ? VoxelRenderType.Locked : VoxelRenderType.Anchor);
            }
            ShapeGeometry.ClaimMarker(result, scale, b, VoxelRenderType.Primary);
            ShapeGeometry.ClaimMarker(result, scale, d, VoxelRenderType.Primary);
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false) => GetVoxelPositions(scale, filled).Count;

        // --- IGuideShape: curve queries --------------------------------------------------------------

        public float GetNearestT(Vec3d worldPos)
        {
            if (!TryPerimeter(out Vec3d[] corners, out double[] cum)) return 0f;
            const int n = 128;
            double bestT = 0, bestD = double.MaxValue;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                double dd = ShapeGeometry.Dist(PerimeterPoint(corners, cum, t), worldPos);
                if (dd < bestD) { bestD = dd; bestT = t; }
            }
            return (float)bestT;
        }

        public Vec3d GetPointAt(float t)
        {
            if (!TryPerimeter(out Vec3d[] corners, out double[] cum))
                return _controlPoints.Count > 0
                    ? new Vec3d(_controlPoints[0].WorldPosition.X, _controlPoints[0].WorldPosition.Y, _controlPoints[0].WorldPosition.Z)
                    : new Vec3d();
            return PerimeterPoint(corners, cum, t);
        }

        public List<Vec3d> SampleCurve(int samples)
        {
            var list = new List<Vec3d>();
            if (!TryPerimeter(out Vec3d[] corners, out double[] cum)) return list;
            int n = Math.Max(32, samples);
            for (int i = 0; i <= n; i++)                       // closed: last == first
                list.Add(PerimeterPoint(corners, cum, (double)i / n));
            return list;
        }

        public int GetNearestControlPointIndex(Vec3d worldPos)
        {
            int best = -1; double bestD = double.MaxValue;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                double dd = ShapeGeometry.Dist(_controlPoints[i].WorldPosition, worldPos);
                if (dd < bestD) { bestD = dd; best = i; }
            }
            return best;
        }

        // --- IGuideShape: mutation -------------------------------------------------------------------

        public void InsertControlPoint(float t, Vec3d position) { /* rectangles have exactly four corners */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            SnapFarCorner();     // keeps C on A's plane layer (and on the square, under the constraint)
        }

        public void RecalculatePhantomPoints() { /* no phantoms in this family */ }

        public bool WouldBreakOnMove(int index) => false;      // corner drags always absorb (see remarks)

        public bool BreakConstraint()
        {
            if (_constraint == ShapeConstraint.None) return false;
            _constraint = ShapeConstraint.None;                // square → rectangle, lossless
            return true;
        }
    }
}
