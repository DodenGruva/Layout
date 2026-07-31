using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The rectangle primitive (Session 9; re-gestured in v0.4.15). THREE clicks: corner A, the far end of
    /// one EDGE, then a third click that pulls the width out sideways. A SQUARE is this shape under
    /// <see cref="ShapeConstraint.Square"/> and stays a TWO-click gesture — one edge already determines it,
    /// so a width click would have nothing left to say; SHIFT at placement puts it on the other side of
    /// that edge instead.
    /// </summary>
    /// <remarks>
    /// WHY THE GESTURE CHANGED (v0.4.15, human-requested). The old gesture was two DIAGONAL corners, and a
    /// diagonal carries no rotation — so the sides had to be read off the plane's world axes
    /// (<see cref="ShapeGeometry.InPlaneAxes"/>) and a rectangle or square could only ever come out lined
    /// up north/south/east/west. Rectangle and Box were the only two shapes in the catalog doing that;
    /// every other shape derives its frame from the clicks themselves. Clicking one EDGE supplies the
    /// missing rotation, and the width then needs a click of its own. Precedent: the triangle went 2 → 3
    /// clicks for exactly this reason, and equilateral stayed at 2 exactly as Square does here.
    ///
    /// GEOMETRY. The frame is <see cref="ShapeGeometry.TryGetFrame"/> on A→B: û along the edge, m̂ the
    /// in-plane perpendicular. du = |A→B| is the edge length; dv = (W − A)·m̂ is the SIGNED width, so the
    /// third click also chooses which side of the edge the rectangle lies on. Corners in curve order:
    /// A → B(= A + û·du) → C(= B + m̂·dv) → D(= A + m̂·dv) → A. Square forces dv = ±du.
    ///
    /// STORED POINTS are three of the four corners: A and B as anchors, C as the width handle (Primary).
    /// D is derived and renders as a Primary marker. Dragging C therefore changes only the width — the
    /// edge length belongs to B — which is the same absorb-don't-break behaviour the family always had.
    ///
    /// LEGACY ENCODING. Guides placed before v0.4.15 stored only A and the diagonal corner. Those are read
    /// in place, in the old world-axis frame, and reproduce exactly: a rectangle already standing in
    /// someone's world must not silently rotate under them. They keep their old two-handle behaviour —
    /// free rotation is a property of the new gesture, not a retro-fit. The stored list is never rewritten
    /// to the new form, because shapes are adopted on renderer WORKER THREADS and mutating the shared
    /// control-point list from here would be a data race.
    ///
    /// [Flagged decisions:] Square has no break gesture in v1 (corner drags absorb by design, so square →
    /// rectangle demotion never triggers — the two live as separate catalog tiles like circle/ellipse).
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

        /// <summary>
        /// Creates a fresh rectangle from the first two clicks: corner A and the far end of one edge. The
        /// born width is a square until the third click sets it — a Square keeps that width for good and
        /// uses <paramref name="inverted"/> (SHIFT at placement) to pick its side of the edge.
        /// </summary>
        public RectangleShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, ShapeConstraint constraint,
            bool inverted = false)
        {
            _planeAxis = planeAxis;
            _constraint = constraint == ShapeConstraint.Square ? ShapeConstraint.Square : ShapeConstraint.None;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isPrimary: true)
            };
            SeatBornWidth(inverted);
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public RectangleShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis, ShapeConstraint constraint)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _planeAxis = planeAxis;
            _constraint = constraint == ShapeConstraint.Square ? ShapeConstraint.Square : ShapeConstraint.None;
        }

        // --- geometry ------------------------------------------------------------------------------

        /// <summary>
        /// The base frame, resolved from whichever encoding this guide carries (see the class remarks):
        /// corner A, the in-plane axes û/m̂, and the signed extents du along û and dv along m̂.
        /// False when either side is too short to be a rectangle.
        /// </summary>
        private bool TryFrame(out Vec3d a, out Vec3d u, out Vec3d m, out double du, out double dv)
        {
            a = u = m = null; du = dv = 0;
            if (_controlPoints.Count < 2) return false;
            a = _controlPoints[0].WorldPosition;

            if (_controlPoints.Count >= 3)
            {
                Vec3d b = _controlPoints[1].WorldPosition, w = _controlPoints[2].WorldPosition;
                if (!ShapeGeometry.TryGetFrame(a, b, _planeAxis, out u, out m, out du)) { a = null; return false; }
                dv = ShapeGeometry.Dot(new Vec3d(w.X - a.X, w.Y - a.Y, w.Z - a.Z), m);
                // One clicked edge IS a square's side, so the width handle only picks the side it lies on.
                if (_constraint == ShapeConstraint.Square) dv = (dv < 0 ? -du : du);
            }
            else
            {
                // LEGACY (pre-v0.4.15): corner A plus the DIAGONAL corner, sides along the plane's world
                // axes. Square's old rule was the DOMINANT diagonal component, kept verbatim so an
                // existing square reproduces to the voxel.
                ShapeGeometry.InPlaneAxes(_planeAxis, out u, out m);
                Vec3d c = _controlPoints[1].WorldPosition;
                var diag = new Vec3d(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                du = ShapeGeometry.Dot(diag, u);
                dv = ShapeGeometry.Dot(diag, m);
                if (_constraint == ShapeConstraint.Square)
                {
                    double side = Math.Max(Math.Abs(du), Math.Abs(dv));
                    du = Math.Sign(du) * side;
                    dv = Math.Sign(dv) * side;
                }
            }

            if (Math.Abs(du) >= MinSide && Math.Abs(dv) >= MinSide) return true;
            a = u = m = null; du = dv = 0;
            return false;
        }

        // The four corners in curve order A → B → C → D.
        private bool TryCorners(out Vec3d a, out Vec3d b, out Vec3d c, out Vec3d d)
        {
            b = c = d = null;
            if (!TryFrame(out a, out Vec3d u, out Vec3d m, out double du, out double dv)) return false;
            b = new Vec3d(a.X + u.X * du, a.Y + u.Y * du, a.Z + u.Z * du);
            d = new Vec3d(a.X + m.X * dv, a.Y + m.Y * dv, a.Z + m.Z * dv);
            c = new Vec3d(b.X + m.X * dv, b.Y + m.Y * dv, b.Z + m.Z * dv);
            return true;
        }

        // Seats the freshly born width handle one edge-length to either side, so the shape is a square
        // until the third click widens it (and, for a Square, for good).
        private void SeatBornWidth(bool inverted)
        {
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            if (!ShapeGeometry.TryGetFrame(a, b, _planeAxis, out Vec3d u, out Vec3d m, out double du)) return;
            double dv = inverted ? -du : du;
            _controlPoints[2].SetPosition(a.X + u.X * du + m.X * dv,
                                          a.Y + u.Y * du + m.Y * dv,
                                          a.Z + u.Z * du + m.Z * dv);
        }

        /// <summary>
        /// Keeps the stored width handle exactly ON the corner it represents. This is what discards a
        /// drag's along-edge drift (the edge length belongs to B) and what applies the Square constraint.
        /// </summary>
        private void SnapWidthHandle()
        {
            if (!TryCorners(out _, out _, out Vec3d c, out _)) return;
            // Legacy guides store the diagonal corner in slot 1; the new form stores it in slot 2.
            _controlPoints[_controlPoints.Count >= 3 ? 2 : 1].SetPosition(c.X, c.Y, c.Z);
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

            // The DERIVED corners as Primary references, then the stored handles by their own role. A
            // corner that is stored must not be claimed as Primary here: Primary outranks Anchor in
            // ClaimMarker's precedence, so doing so would quietly repaint B green.
            if (_controlPoints.Count < 3) ShapeGeometry.ClaimMarker(result, scale, b, VoxelRenderType.Primary);
            ShapeGeometry.ClaimMarker(result, scale, d, VoxelRenderType.Primary);
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition,
                    cp.IsLocked ? VoxelRenderType.Locked
                    : cp.IsAnchor ? VoxelRenderType.Anchor
                    : VoxelRenderType.Primary);
            }
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
            SnapWidthHandle();   // re-seats the width corner (and applies the square constraint)
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
