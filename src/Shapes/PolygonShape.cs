using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The regular-polygon primitive (Session 11): N equal sides inscribed in a circle, defined by the two
    /// draft clicks and a side count (<see cref="GuideData.Sides"/>). The FIRST click is a VERTEX; the
    /// SECOND is the point of the perimeter diametrically opposite it — a vertex when N is even, the far
    /// edge's MIDPOINT when N is odd — so the shape exactly spans the two-click gesture (like every other
    /// primitive) and both anchors always sit ON the outline. "Which way it points" is simply where the
    /// first click (the vertex) is.
    /// </summary>
    /// <remarks>
    /// GEOMETRY. û = A→B; the circumradius r = |AB| / (1 + cos(π/N)) for odd N (vertex + apothem) and
    /// |AB| / 2 for even (vertex + vertex); centre = A + û·r. Vertices sit at angles π + 2πk/N in the
    /// (û, m̂) frame around the centre, with m̂ the intrinsic-plane perpendicular from
    /// <see cref="ShapeGeometry.TryGetFrame"/> — the polygon is mirror-symmetric across the A–B axis, so
    /// m̂'s sign never changes the shape. Everything derives per query from the two stored anchors + N;
    /// nothing else is stored, so grabs of either anchor absorb as resize/rotate (the ellipse pattern) and
    /// a side-count change is a pure re-derivation.
    ///
    /// Body clicks map to the NEAREST ANCHOR (controller policy for all parametric shapes), so
    /// <see cref="InsertControlPoint"/> is a defensive no-op. Fill = a fan from the centre to a dense
    /// perimeter. No phantoms, no constraints (the side count plays that role).
    /// </remarks>
    public sealed class PolygonShape : IGuideShape
    {
        public const int MinSides = 3;
        public const int MaxSides = 24;
        public const int DefaultSides = 6;

        private const double MinSpan = 0.05;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;
        private readonly int _sides;
        private readonly bool _flatSideAligned;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>The side count this shape was built with (clamped to 3..24).</summary>
        public int Sides => _sides;

        /// <summary>Folds an arbitrary stored/typed side count into the valid range (0/default → 6).</summary>
        public static int ClampSides(int sides) =>
            sides < MinSides ? (sides <= 0 ? DefaultSides : MinSides) : (sides > MaxSides ? MaxSides : sides);

        /// <summary>Creates a fresh polygon from the two draft clicks (vertex → opposite perimeter point).</summary>
        public PolygonShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, int sides,
            bool flatSideAligned = false)
        {
            _preferredAxis = planeAxis;
            _sides = ClampSides(sides);
            _flatSideAligned = flatSideAligned;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true)
            };
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public PolygonShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis, int sides,
            bool flatSideAligned = false)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
            _sides = ClampSides(sides);
            _flatSideAligned = flatSideAligned;
        }

        // --- geometry ------------------------------------------------------------------------------

        // All N vertices in perimeter order, index 0 == anchor A. False when the anchors are degenerate.
        private bool TryVertices(out Vec3d[] verts)
        {
            verts = null;
            if (_controlPoints.Count < 2) return false;
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            if (!ShapeGeometry.TryGetFrame(a, b, _preferredAxis, out Vec3d u, out Vec3d m, out double span))
                return false;
            if (span < MinSpan) return false;

            // Far feature along +û: a vertex (even N) or an edge midpoint at the apothem (odd N).
            double apothemRatio = Math.Cos(Math.PI / _sides);
            double near = _flatSideAligned ? apothemRatio : 1.0;
            double far = _flatSideAligned
                ? (_sides % 2 == 0 ? apothemRatio : 1.0)
                : (_sides % 2 == 0 ? 1.0 : apothemRatio);
            double r = span / (near + far);
            var c = new Vec3d(a.X + u.X * r * near, a.Y + u.Y * r * near,
                a.Z + u.Z * r * near);

            verts = new Vec3d[_sides];
            for (int k = 0; k < _sides; k++)
            {
                double phi = Math.PI + (_flatSideAligned ? Math.PI / _sides : 0.0)
                    + 2.0 * Math.PI * k / _sides;
                double cu = Math.Cos(phi) * r, cm = Math.Sin(phi) * r;
                verts[k] = new Vec3d(
                    c.X + u.X * cu + m.X * cm,
                    c.Y + u.Y * cu + m.Y * cm,
                    c.Z + u.Z * cu + m.Z * cm);
            }
            return true;
        }

        // The closed perimeter (corner list + cumulative lengths), curve order vertex 0 → 1 → … → 0.
        private bool TryPerimeter(out Vec3d[] corners, out double[] cum)
        {
            corners = null; cum = null;
            if (!TryVertices(out Vec3d[] verts)) return false;
            corners = new Vec3d[verts.Length + 1];
            Array.Copy(verts, corners, verts.Length);
            corners[verts.Length] = verts[0];
            cum = new double[corners.Length];
            for (int i = 1; i < corners.Length; i++)
                cum[i] = cum[i - 1] + ShapeGeometry.Dist(corners[i - 1], corners[i]);
            return cum[cum.Length - 1] > 1e-9;
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
            if (!TryVertices(out Vec3d[] verts)) return result;

            var seen = new HashSet<(int, int, int)>();
            for (int k = 0; k < verts.Length; k++)
                VoxelMarch.MarchSegmentInto(result, seen, verts[k], verts[(k + 1) % verts.Length], scale);

            if (filled && TryPerimeter(out Vec3d[] corners, out double[] cum))
            {
                // Centroid fan to a dense perimeter (angular AND radial steps ≤ half a cell — no holes).
                var centre = new Vec3d();
                for (int k = 0; k < verts.Length; k++)
                {
                    centre.X += verts[k].X; centre.Y += verts[k].Y; centre.Z += verts[k].Z;
                }
                centre.X /= verts.Length; centre.Y /= verts.Length; centre.Z /= verts.Length;

                double cell = scale / 16.0;
                int steps = Math.Max(24, Math.Min(8192, (int)Math.Ceiling(cum[cum.Length - 1] / (cell * 0.5))));
                for (int i = 0; i < steps; i++)
                    VoxelMarch.MarchSegmentInto(result, seen, centre,
                        PerimeterPoint(corners, cum, (double)i / steps), scale);
            }

            for (int i = 0; i < 2 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition,
                    cp.IsLocked ? VoxelRenderType.Locked : VoxelRenderType.Anchor);
            }
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false) => GetVoxelPositions(scale, filled).Count;

        // --- IGuideShape: curve queries --------------------------------------------------------------

        public float GetNearestT(Vec3d worldPos)
        {
            if (!TryPerimeter(out Vec3d[] corners, out double[] cum)) return 0f;
            int n = Math.Max(96, _sides * 12);
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
            int n = Math.Max(Math.Max(32, samples), _sides * 4);
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

        public void InsertControlPoint(float t, Vec3d position) { /* a regular polygon has no free points */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;
            // Both anchors DEFINE the polygon; everything else derives per query, so a raw move IS the
            // resize/rotate absorb — nothing to re-derive or snap.
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
        }

        public void RecalculatePhantomPoints() { /* no phantoms in this family */ }

        public bool WouldBreakOnMove(int index) => false;      // anchor drags always absorb

        public bool BreakConstraint() => false;                // nothing to break — N is data, not a constraint
    }
}
