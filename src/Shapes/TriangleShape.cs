using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The triangle primitive (Session 9): the two draft clicks are the BASE anchors A and B; a real,
    /// draggable third vertex V (Primary/green) is born at the equilateral position above the base
    /// midpoint, in the intrinsic plane. Free (no constraint) = scalene: V moves anywhere in 3D.
    /// </summary>
    /// <remarks>
    /// CONSTRAINTS (all are apex-derivation rules, the ellipse-M pattern):
    /// • <see cref="ShapeConstraint.Right"/> — 90° at A (the FIRST click): V slides along the in-plane
    ///   perpendicular AT A; moving A/B re-derives V keeping its leg length. Absorbs everything.
    /// • <see cref="ShapeConstraint.Isosceles"/> — V slides along the base's perpendicular BISECTOR;
    ///   moving A/B re-derives keeping the bisector height. Absorbs everything.
    /// • <see cref="ShapeConstraint.Equilateral"/> — V is fully derived (√3/2·|AB| on the bisector);
    ///   moving A/B absorbs (resize/rotate); dragging V is the break trigger → free triangle
    ///   (the circle→ellipse pattern; lossless, V keeps its position).
    /// [Flagged: Right/Isosceles have no break gesture in v1 — their apex drags absorb by sliding, so the
    ///  constraint simply persists; a future re-constrain/free op could change that.]
    ///
    /// Body clicks map to the NEAREST VERTEX (controller policy — a parametric triangle has nothing to
    /// insert), so <see cref="InsertControlPoint"/> is a defensive no-op. Fill = a fan from the centroid
    /// to a dense perimeter. No phantoms.
    /// </remarks>
    public sealed class TriangleShape : IGuideShape
    {
        private const double MinLeg = 0.05;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;
        private ShapeConstraint _constraint;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => _constraint;

        /// <summary>
        /// Creates a fresh triangle from the two draft clicks (the base). <paramref name="inverted"/>
        /// (SHIFT at placement, Session 11) mirrors the born apex to the other side of the base; with
        /// default-up frames that means opening downward instead of up.
        /// </summary>
        public TriangleShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, ShapeConstraint constraint,
            bool inverted = false)
        {
            _preferredAxis = planeAxis;
            _constraint = ValidConstraint(constraint);
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true),
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true)   // placed properly just below
            };
            DeriveApex(initial: true, initialSide: inverted ? -1.0 : 1.0);
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public TriangleShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis, ShapeConstraint constraint)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
            _constraint = ValidConstraint(constraint);
        }

        private static ShapeConstraint ValidConstraint(ShapeConstraint c) =>
            c == ShapeConstraint.Right || c == ShapeConstraint.Equilateral || c == ShapeConstraint.Isosceles
                ? c : ShapeConstraint.None;

        // --- geometry ------------------------------------------------------------------------------

        private bool TryCorners(out Vec3d a, out Vec3d b, out Vec3d v)
        {
            a = b = v = null;
            if (_controlPoints.Count < 3) return false;
            a = _controlPoints[0].WorldPosition;
            b = _controlPoints[1].WorldPosition;
            v = _controlPoints[2].WorldPosition;
            return true;
        }

        /// <summary>
        /// Puts V where the current constraint says it belongs after an A/B move (or at creation).
        /// Free triangles leave V untouched except at creation (born equilateral).
        /// </summary>
        /// <remarks>
        /// SIDE-PRESERVING (Session 11). Re-derivation used to take the ABSOLUTE height/leg along m̂,
        /// which silently flipped an inverted (below-the-base) apex back up on the next base move. The
        /// projections are now SIGNED: whichever side of the base the apex currently lives on, it stays
        /// on — the sign is read from the stored geometry, so it persists and syncs for free. At creation
        /// <paramref name="initialSide"/> (+1 up / −1 the SHIFT-inverted side) decides instead.
        /// </remarks>
        private void DeriveApex(bool initial = false, double initialSide = 1.0)
        {
            if (_controlPoints.Count < 3) return;
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            if (!ShapeGeometry.TryGetFrame(a, b, _preferredAxis, out Vec3d u, out Vec3d m, out double baseLen))
                return;
            Vec3d mid = ShapeGeometry.Lerp(a, b, 0.5);
            Vec3d v = _controlPoints[2].WorldPosition;

            // Signed offset with a minimum magnitude: keeps the apex's current side (0 counts as up).
            static double Signed(double dot, double min)
            {
                double side = dot < 0 ? -1.0 : 1.0;
                return side * Math.Max(min, Math.Abs(dot));
            }

            switch (_constraint)
            {
                case ShapeConstraint.Equilateral:
                {
                    double side = initial ? initialSide
                        : (ShapeGeometry.Dot(new Vec3d(v.X - mid.X, v.Y - mid.Y, v.Z - mid.Z), m) < 0 ? -1.0 : 1.0);
                    double h = side * Math.Sqrt(3) / 2.0 * baseLen;
                    _controlPoints[2].SetPosition(mid.X + m.X * h, mid.Y + m.Y * h, mid.Z + m.Z * h);
                    return;
                }
                case ShapeConstraint.Isosceles:
                {
                    double h = initial ? initialSide * Math.Sqrt(3) / 2.0 * baseLen
                        : Signed(ShapeGeometry.Dot(
                            new Vec3d(v.X - mid.X, v.Y - mid.Y, v.Z - mid.Z), m), MinLeg);
                    _controlPoints[2].SetPosition(mid.X + m.X * h, mid.Y + m.Y * h, mid.Z + m.Z * h);
                    return;
                }
                case ShapeConstraint.Right:
                {
                    double leg = initial ? initialSide * baseLen
                        : Signed(ShapeGeometry.Dot(
                            new Vec3d(v.X - a.X, v.Y - a.Y, v.Z - a.Z), m), MinLeg);
                    _controlPoints[2].SetPosition(a.X + m.X * leg, a.Y + m.Y * leg, a.Z + m.Z * leg);
                    return;
                }
                default:
                {
                    if (initial)
                    {
                        double h = initialSide * Math.Sqrt(3) / 2.0 * baseLen;
                        _controlPoints[2].SetPosition(mid.X + m.X * h, mid.Y + m.Y * h, mid.Z + m.Z * h);
                    }
                    return;
                }
            }
        }

        // The perimeter in curve order A → B → V → A, as (corner, cumulative-length) data.
        private bool TryPerimeter(out Vec3d[] corners, out double[] cum)
        {
            corners = null; cum = null;
            if (!TryCorners(out Vec3d a, out Vec3d b, out Vec3d v)) return false;
            corners = new[] { a, b, v, a };
            cum = new double[4];
            for (int i = 1; i < 4; i++)
                cum[i] = cum[i - 1] + ShapeGeometry.Dist(corners[i - 1], corners[i]);
            return cum[3] > 1e-9;
        }

        private static Vec3d PerimeterPoint(Vec3d[] corners, double[] cum, double t)
        {
            double target = (t < 0 ? 0 : t > 1 ? 1 : t) * cum[3];
            for (int i = 1; i < 4; i++)
            {
                if (target <= cum[i] || i == 3)
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
            if (!TryCorners(out Vec3d a, out Vec3d b, out Vec3d v)) return result;

            var seen = new HashSet<(int, int, int)>();
            VoxelMarch.MarchSegmentInto(result, seen, a, b, scale);
            VoxelMarch.MarchSegmentInto(result, seen, b, v, scale);
            VoxelMarch.MarchSegmentInto(result, seen, v, a, scale);

            if (filled)
            {
                // Centroid fan to a dense perimeter (angular AND radial steps ≤ half a cell — no holes).
                var centroid = new Vec3d((a.X + b.X + v.X) / 3.0, (a.Y + b.Y + v.Y) / 3.0, (a.Z + b.Z + v.Z) / 3.0);
                if (TryPerimeter(out Vec3d[] corners, out double[] cum))
                {
                    double cell = scale / 16.0;
                    int steps = Math.Max(24, Math.Min(8192, (int)Math.Ceiling(cum[3] / (cell * 0.5))));
                    for (int i = 0; i < steps; i++)
                        VoxelMarch.MarchSegmentInto(result, seen, centroid,
                            PerimeterPoint(corners, cum, (double)i / steps), scale);
                }
            }

            for (int i = 0; i < 3 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                VoxelRenderType t = cp.IsLocked ? VoxelRenderType.Locked
                    : cp.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition, t);
            }
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false) => GetVoxelPositions(scale, filled).Count;

        // --- IGuideShape: curve queries --------------------------------------------------------------

        public float GetNearestT(Vec3d worldPos)
        {
            if (!TryPerimeter(out Vec3d[] corners, out double[] cum)) return 0f;
            const int n = 96;
            double bestT = 0, bestD = double.MaxValue;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                double d = ShapeGeometry.Dist(PerimeterPoint(corners, cum, t), worldPos);
                if (d < bestD) { bestD = d; bestT = t; }
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
            int n = Math.Max(24, samples);
            for (int i = 0; i <= n; i++)                       // closed: last == first
                list.Add(PerimeterPoint(corners, cum, (double)i / n));
            return list;
        }

        public int GetNearestControlPointIndex(Vec3d worldPos)
        {
            int best = -1; double bestD = double.MaxValue;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                double d = ShapeGeometry.Dist(_controlPoints[i].WorldPosition, worldPos);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // --- IGuideShape: mutation -------------------------------------------------------------------

        public void InsertControlPoint(float t, Vec3d position) { /* triangles have exactly three vertices */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;

            if (index == 2)
            {
                // The apex: free triangles take it raw (full 3D); Right/Isosceles slide it along their
                // line (DeriveApex projects). Equilateral never reaches here — the caller breaks first.
                _controlPoints[2].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
                if (_constraint == ShapeConstraint.Right || _constraint == ShapeConstraint.Isosceles)
                    DeriveApex();
                return;
            }

            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            DeriveApex();                                      // base moves absorb: re-derive per constraint
        }

        public void RecalculatePhantomPoints() { /* no phantoms in this family */ }

        public bool WouldBreakOnMove(int index) =>
            _constraint == ShapeConstraint.Equilateral && index == 2;

        public bool BreakConstraint()
        {
            if (_constraint == ShapeConstraint.None) return false;
            _constraint = ShapeConstraint.None;                // lossless: V keeps its current position
            return true;
        }
    }
}
