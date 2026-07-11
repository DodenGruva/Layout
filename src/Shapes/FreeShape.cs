using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The Free-Shape (Session 11, 0.1.15): an irregular polyline. Every draft click chained another
    /// straight segment; the player finished it either OPEN (clicked the last placed corner again) or
    /// CLOSED into a loop (clicked the first corner — <see cref="GuideData.IsClosed"/>). Unlike every
    /// other multi-point shape this one stores ONLY hand-placed geometry: each clicked corner is a real,
    /// grabbable ANCHOR; nothing is derived, so moving a corner moves exactly that corner.
    /// </summary>
    /// <remarks>
    /// EDITING. This shape joins the ARCH in taking body inserts (the controller's insert-and-grab
    /// gesture): clicking a segment inserts a plain (non-anchor) vertex there — segments stay perfectly
    /// straight, there is no spline and no soft-point flow. Constraints don't apply; closed-ness is fixed
    /// at creation. FILL IS DEFERRED (v1, human-directed): an irregular outline can be concave, which the
    /// existing fan/scanline fills would get wrong, so <c>filled</c> is ignored for now.
    ///
    /// Curve parameter t runs along the corner order, including the closing segment when closed;
    /// <see cref="SampleCurve"/> emits last == first for closed shapes (the settled convention DivisionMarks
    /// keys on). No phantoms.
    /// </remarks>
    public sealed class FreeShape : IGuideShape
    {
        /// <summary>Sanity ceiling on hand-placed corners (undo snapshots and targeting stay cheap).</summary>
        public const int MaxCorners = 64;

        private readonly List<ControlPoint> _controlPoints;
        private readonly bool _closed;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Whether the outline loops back to its first corner.</summary>
        public bool IsClosed => _closed;

        /// <summary>Creates a fresh Free-Shape from the full draft chain of clicked corners.</summary>
        public FreeShape(IReadOnlyList<Vec3d> corners, bool closed)
        {
            if (corners == null) throw new ArgumentNullException(nameof(corners));
            _closed = closed;
            _controlPoints = new List<ControlPoint>(corners.Count);
            foreach (Vec3d p in corners)
                _controlPoints.Add(new ControlPoint(new Vec3d(p.X, p.Y, p.Z), isAnchor: true));
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public FreeShape(List<ControlPoint> controlPoints, bool closed)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _closed = closed;
        }

        // --- geometry ------------------------------------------------------------------------------

        // The corner list in curve order (+ the wrap-around copy of corner 0 when closed), with
        // cumulative lengths. False when fewer than two corners exist or the outline has no length.
        private bool TryPerimeter(out Vec3d[] corners, out double[] cum)
        {
            corners = null; cum = null;
            int n = _controlPoints.Count;
            if (n < 2) return false;

            int total = _closed ? n + 1 : n;
            corners = new Vec3d[total];
            for (int i = 0; i < n; i++) corners[i] = _controlPoints[i].WorldPosition;
            if (_closed) corners[n] = _controlPoints[0].WorldPosition;

            cum = new double[total];
            for (int i = 1; i < total; i++)
                cum[i] = cum[i - 1] + ShapeGeometry.Dist(corners[i - 1], corners[i]);
            return cum[total - 1] > 1e-9;
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
            // `filled` deliberately ignored (fill deferred for irregular outlines — see remarks).
            var result = new List<VoxelPosition>();
            int n = _controlPoints.Count;
            if (n < 2) return result;

            var seen = new HashSet<(int, int, int)>();
            int segments = _closed ? n : n - 1;
            for (int i = 0; i < segments; i++)
                VoxelMarch.MarchSegmentInto(result, seen,
                    _controlPoints[i].WorldPosition, _controlPoints[(i + 1) % n].WorldPosition, scale);

            foreach (ControlPoint cp in _controlPoints)
            {
                VoxelRenderType t = cp.IsLocked ? VoxelRenderType.Locked
                    : cp.IsPrimary ? VoxelRenderType.Primary
                    : cp.IsAnchor ? VoxelRenderType.Anchor : VoxelRenderType.Normal;
                if (t != VoxelRenderType.Normal)
                    ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition, t);
            }
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false) => GetVoxelPositions(scale, filled).Count;

        // --- IGuideShape: curve queries --------------------------------------------------------------

        public float GetNearestT(Vec3d worldPos)
        {
            if (!TryPerimeter(out Vec3d[] corners, out double[] cum)) return 0f;
            int samples = Math.Max(96, corners.Length * 16);
            double bestT = 0, bestD = double.MaxValue;
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples;
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
            int n = Math.Max(Math.Max(32, samples), corners.Length * 4);
            for (int i = 0; i <= n; i++)                 // closed: last == first (perimeter wraps)
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

        /// <summary>
        /// Inserts a plain (non-anchor) vertex on the segment containing curve parameter
        /// <paramref name="t"/> — the arch-style body insert, kept straight-line: the new vertex lands at
        /// <paramref name="position"/> and simply becomes another corner of the polyline.
        /// </summary>
        public void InsertControlPoint(float t, Vec3d position)
        {
            int n = _controlPoints.Count;
            if (n < 2 || position == null) return;
            if (n >= MaxCorners) return;                 // defensive ceiling

            if (!TryPerimeter(out Vec3d[] corners, out double[] cum)) return;

            // Which segment does t land in? Segment i runs corners[i] → corners[i+1]; inserting after
            // list index i keeps the corner order (the closing segment maps to "after the last vertex").
            double target = (t < 0 ? 0 : t > 1 ? 1 : t) * cum[cum.Length - 1];
            int seg = 0;
            for (int i = 1; i < corners.Length; i++)
            {
                if (target <= cum[i] || i == corners.Length - 1) { seg = i - 1; break; }
            }

            _controlPoints.Insert(Math.Min(seg + 1, n), new ControlPoint(position));
        }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;
            // Nothing derives from anything: a corner move is exactly that corner moving.
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
        }

        public void RecalculatePhantomPoints() { /* no phantoms in this family */ }

        public bool WouldBreakOnMove(int index) => false;

        public bool BreakConstraint() => false;          // nothing to break
    }
}
