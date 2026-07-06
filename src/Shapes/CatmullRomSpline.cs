using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// Non-uniform Catmull-Rom spline used as the geometric core of every guide shape. Pure math:
    /// it depends only on Vec3d and <see cref="VoxelPosition"/>, has no game/world state, and is
    /// immutable once constructed.
    /// </summary>
    /// <remarks>
    /// FORMULATION. This is the cardinal/Hermite form of non-uniform Catmull-Rom: per segment we do
    /// cubic Hermite interpolation, with the tangent at each interior point taken as the secant
    /// (P[i+1] − P[i−1]) scaled by the centripetal knot interval. Knot spacing uses the standard
    /// alpha parameterization (alpha = 0.5 → centripetal), which keeps samples evenly distributed and
    /// avoids the bunching/overshoot of the uniform form. The secant-direction tangent is deliberate:
    /// it is what lets a shape place a phantom point and obtain an EXACTLY vertical end tangent (the
    /// arch's vertical feet), since making (P[i+1] − P[i−1]) vertical makes the tangent vertical.
    ///
    /// INTERPOLATED RANGE. The first and last supplied points are tangent-only HANDLES (an arch's
    /// phantom P0 / P4). The spline interpolates the interior points P[1 .. N−2]; the visible curve
    /// runs from P[1] (start anchor) to P[N−2] (end anchor). The public parameter t in [0, 1] maps
    /// linearly onto the centripetal knot span of that interior range, so t = 0 is the start anchor
    /// and t = 1 is the end anchor — matching the IGuideShape t convention. A curve therefore needs at
    /// least 4 points (two handles + two interpolated); an arch supplies 5 (handles P0/P4, interior
    /// P1/P2/P3 → two segments).
    ///
    /// VEC3D OWNERSHIP. Supplied points are deep-copied on construction; the spline never aliases a
    /// caller-owned Vec3d (Vec3d is a mutable reference type).
    ///
    /// ROBUSTNESS. Coincident points would produce zero-length knot intervals and divide-by-zero, so
    /// every knot interval is floored to a small epsilon, guaranteeing a strictly increasing knot
    /// vector. The voxel marcher bounds its iteration count so degenerate input cannot hang it.
    /// </remarks>
    public sealed class CatmullRomSpline
    {
        // Floors a knot interval so the knot vector is strictly increasing even for coincident points.
        private const double KnotEpsilon = 1e-5;
        // Below this the interpolated knot span is treated as degenerate (curve collapses to a point).
        private const double SpanEpsilon = 1e-9;
        // Marcher samples roughly this fraction of a voxel per step (≈4 samples per voxel edge).
        private const double MarchStepFraction = 0.25;
        // Hard ceiling on marcher iterations — only reached by pathological (e.g. world-spanning) curves.
        private const int MarchSafetyMax = 2_000_000;
        // Dense samples per segment used for the cached arc-length estimate.
        private const int ArcLengthSamplesPerSegment = 64;

        private readonly Vec3d[] _p;      // Deep-copied control points (incl. handles at 0 and N−1).
        private readonly double[] _k;     // Centripetal knot vector, strictly increasing.
        private readonly int _n;          // Point count.
        private readonly int _loStart;    // First interpolated index (1).
        private readonly int _hiEnd;      // Last interpolated index (N−2).
        private readonly bool _hasCurve;  // True when there is at least one valid segment.
        private readonly double _arcLength;

        /// <summary>True when the spline has enough non-degenerate points to form a curve.</summary>
        public bool HasCurve => _hasCurve;

        /// <param name="controlPoints">
        /// Ordered points including the two outer tangent handles. May be null/short; the spline then
        /// reports <see cref="HasCurve"/> = false and all queries return safe defaults.
        /// </param>
        /// <param name="alpha">Knot parameterization: 0 uniform, 0.5 centripetal (default), 1 chordal.</param>
        public CatmullRomSpline(IReadOnlyList<Vec3d> controlPoints, double alpha = 0.5)
        {
            alpha = Clamp(alpha, 0.0, 1.0);

            _n = controlPoints?.Count ?? 0;
            _p = new Vec3d[_n];
            for (int i = 0; i < _n; i++)
            {
                Vec3d src = controlPoints[i];
                _p[i] = src != null ? new Vec3d(src.X, src.Y, src.Z) : new Vec3d();
            }

            _loStart = 1;
            _hiEnd = _n - 2;

            // Build the centripetal knot vector with epsilon-floored intervals.
            _k = new double[_n];
            if (_n > 0) _k[0] = 0.0;
            for (int i = 1; i < _n; i++)
            {
                double d = Distance(_p[i - 1], _p[i]);
                double interval = Math.Max(Math.Pow(d, alpha), KnotEpsilon);
                _k[i] = _k[i - 1] + interval;
            }

            _hasCurve = _n >= 4 && (_k[_hiEnd] - _k[_loStart]) > SpanEpsilon;
            _arcLength = ComputeArcLength();
        }

        /// <summary>
        /// Returns the world-space point on the visible curve at parameter <paramref name="t"/> (in
        /// [0, 1], clamped). For a degenerate spline returns the start anchor (or a zero vector if there
        /// are no points). The returned Vec3d is freshly allocated and owned by the caller.
        /// </summary>
        public Vec3d Evaluate(double t)
        {
            if (!_hasCurve)
                return _n > 0 ? new Vec3d(_p[_loStart < _n ? _loStart : 0].X, _p[_loStart < _n ? _loStart : 0].Y, _p[_loStart < _n ? _loStart : 0].Z) : new Vec3d();

            t = Clamp(t, 0.0, 1.0);
            double u = _k[_loStart] + t * (_k[_hiEnd] - _k[_loStart]);
            LocateSegment(u, out int i, out double s);
            return HermitePoint(i, s);
        }

        /// <summary>
        /// Returns the curve derivative at parameter <paramref name="t"/> with respect to t. Typically
        /// only its DIRECTION is used (e.g. an arch verifying its vertical feet); callers needing a unit
        /// tangent should normalize. Returns a zero vector for a degenerate spline.
        /// </summary>
        public Vec3d EvaluateTangent(double t)
        {
            if (!_hasCurve) return new Vec3d();

            t = Clamp(t, 0.0, 1.0);
            double span = _k[_hiEnd] - _k[_loStart];
            double u = _k[_loStart] + t * span;
            LocateSegment(u, out int i, out double s);

            Vec3d dCds = HermiteTangentLocal(i, s);      // derivative w.r.t. local s
            double dI = _k[i + 1] - _k[i];               // ds/du = 1/dI
            double factor = span / dI;                   // chain rule: dC/dt = dC/ds * (1/dI) * (du/dt)
            return new Vec3d(dCds.X * factor, dCds.Y * factor, dCds.Z * factor);
        }

        /// <summary>Approximate arc length of the visible curve (cached). Zero for a degenerate spline.</summary>
        public double GetArcLength() => _arcLength;

        /// <summary>
        /// Marches the visible curve and returns the deduplicated set of occupied voxel cells at the
        /// given scale, each tagged <see cref="VoxelRenderType.Normal"/>. Per the VoxelPosition contract,
        /// types are uniform here so the dedup is purely by coordinate; the owning shape assigns real
        /// types afterward on this already-unique set. Never null.
        /// </summary>
        /// <param name="scale">Voxel edge length in 1/16-block units; one of 1, 2, 4, 8, 16.</param>
        public List<VoxelPosition> SampleVoxelPositions(int scale)
        {
            HashSet<(int, int, int)> cells = MarchCells(scale);
            var result = new List<VoxelPosition>(cells.Count);
            foreach (var c in cells)
                result.Add(new VoxelPosition(c.Item1, c.Item2, c.Item3, VoxelRenderType.Normal));
            return result;
        }

        /// <summary>
        /// Returns the exact number of occupied voxel cells at the given scale, equal by construction to
        /// <see cref="SampleVoxelPositions"/>(scale).Count, but without allocating the VoxelPosition list.
        /// This is the cheap path the cap check runs on every throttled edit. (Beyond the four methods in
        /// the original spline spec; added to satisfy IGuideShape's exact-count invariant efficiently.)
        /// </summary>
        public int CountVoxels(int scale) => MarchCells(scale).Count;

        /// <summary>
        /// Returns the interpolated start index i of the segment containing parameter <paramref name="t"/>
        /// (in [0, 1], clamped), so the segment runs from point i to point i+1. A shape uses this to
        /// decide which existing points a body insertion falls between. Returns the first interpolated
        /// index for a degenerate spline.
        /// </summary>
        public int SegmentStartIndexForT(double t)
        {
            if (!_hasCurve) return _loStart;
            t = Clamp(t, 0.0, 1.0);
            double u = _k[_loStart] + t * (_k[_hiEnd] - _k[_loStart]);
            LocateSegment(u, out int i, out _);
            return i;
        }

        // --- internals -------------------------------------------------------------------------

        private HashSet<(int, int, int)> MarchCells(int scale)
        {
            var cells = new HashSet<(int, int, int)>();
            if (!_hasCurve || scale <= 0) return cells;

            double worldVoxel = scale / 16.0;                 // voxel edge in blocks
            double length = Math.Max(_arcLength, worldVoxel); // guard tiny/zero length

            // Sample count sized to arc length so step ≈ MarchStepFraction of a voxel everywhere on
            // average. A step ≤ ~1 voxel cannot jump over a cell's interior, so no interior cell is
            // skipped (diagonal corner-cutting is acceptable for a visual guide). Bounded both ways.
            int minSteps = Math.Max((_n - 3) * 16, 16);
            long sized = (long)Math.Ceiling(length / (worldVoxel * MarchStepFraction));
            int steps = (int)Clamp(sized, minSteps, MarchSafetyMax);

            for (int j = 0; j <= steps; j++)
            {
                double t = (double)j / steps;
                Vec3d pos = Evaluate(t);
                cells.Add(Quantize(pos, scale));
            }

            // Guarantee each interpolated control point's own cell is present, so the shape can colour
            // anchors/apex even if the march step happened to straddle the exact point.
            for (int idx = _loStart; idx <= _hiEnd; idx++)
                cells.Add(Quantize(_p[idx], scale));

            return cells;
        }

        // Lower-corner voxel cell containing worldPos, in 1/16-block units, snapped to a multiple of
        // scale. Math.Floor (not truncation) keeps negative coordinates correct.
        private static (int, int, int) Quantize(Vec3d worldPos, int scale)
        {
            int x = (int)Math.Floor(worldPos.X * 16.0 / scale) * scale;
            int y = (int)Math.Floor(worldPos.Y * 16.0 / scale) * scale;
            int z = (int)Math.Floor(worldPos.Z * 16.0 / scale) * scale;
            return (x, y, z);
        }

        // Finds the segment index i (in [_loStart, _hiEnd−1]) containing global parameter u, and the
        // local parameter s in [0, 1] within it. Assumes _hasCurve.
        private void LocateSegment(double u, out int i, out double s)
        {
            double lo = _k[_loStart];
            double hi = _k[_hiEnd];
            if (u <= lo) { i = _loStart; s = 0.0; return; }
            if (u >= hi) { i = _hiEnd - 1; s = 1.0; return; }

            for (int seg = _loStart; seg <= _hiEnd - 1; seg++)
            {
                if (u <= _k[seg + 1])
                {
                    i = seg;
                    s = (u - _k[seg]) / (_k[seg + 1] - _k[seg]);
                    return;
                }
            }
            i = _hiEnd - 1; s = 1.0; // numerical fallback
        }

        // Global (knot-space) Catmull-Rom tangent at interpolated index idx: secant scaled by knot span.
        private Vec3d GlobalTangentAt(int idx)
        {
            double denom = _k[idx + 1] - _k[idx - 1];
            Vec3d a = _p[idx + 1];
            Vec3d b = _p[idx - 1];
            return new Vec3d((a.X - b.X) / denom, (a.Y - b.Y) / denom, (a.Z - b.Z) / denom);
        }

        private Vec3d HermitePoint(int i, double s)
        {
            double dI = _k[i + 1] - _k[i];
            Vec3d p0 = _p[i];
            Vec3d p1 = _p[i + 1];
            Vec3d gT0 = GlobalTangentAt(i);
            Vec3d gT1 = GlobalTangentAt(i + 1);

            // Local tangents = global tangents scaled to the segment's local [0,1] parameter.
            double m0x = gT0.X * dI, m0y = gT0.Y * dI, m0z = gT0.Z * dI;
            double m1x = gT1.X * dI, m1y = gT1.Y * dI, m1z = gT1.Z * dI;

            double s2 = s * s, s3 = s2 * s;
            double h00 = 2 * s3 - 3 * s2 + 1;
            double h10 = s3 - 2 * s2 + s;
            double h01 = -2 * s3 + 3 * s2;
            double h11 = s3 - s2;

            return new Vec3d(
                h00 * p0.X + h10 * m0x + h01 * p1.X + h11 * m1x,
                h00 * p0.Y + h10 * m0y + h01 * p1.Y + h11 * m1y,
                h00 * p0.Z + h10 * m0z + h01 * p1.Z + h11 * m1z);
        }

        private Vec3d HermiteTangentLocal(int i, double s)
        {
            double dI = _k[i + 1] - _k[i];
            Vec3d p0 = _p[i];
            Vec3d p1 = _p[i + 1];
            Vec3d gT0 = GlobalTangentAt(i);
            Vec3d gT1 = GlobalTangentAt(i + 1);

            double m0x = gT0.X * dI, m0y = gT0.Y * dI, m0z = gT0.Z * dI;
            double m1x = gT1.X * dI, m1y = gT1.Y * dI, m1z = gT1.Z * dI;

            double s2 = s * s;
            double dh00 = 6 * s2 - 6 * s;
            double dh10 = 3 * s2 - 4 * s + 1;
            double dh01 = -6 * s2 + 6 * s;
            double dh11 = 3 * s2 - 2 * s;

            return new Vec3d(
                dh00 * p0.X + dh10 * m0x + dh01 * p1.X + dh11 * m1x,
                dh00 * p0.Y + dh10 * m0y + dh01 * p1.Y + dh11 * m1y,
                dh00 * p0.Z + dh10 * m0z + dh01 * p1.Z + dh11 * m1z);
        }

        private double ComputeArcLength()
        {
            if (!_hasCurve) return 0.0;

            int samples = Math.Max((_n - 3) * ArcLengthSamplesPerSegment, ArcLengthSamplesPerSegment);
            double total = 0.0;
            Vec3d prev = Evaluate(0.0);
            for (int j = 1; j <= samples; j++)
            {
                Vec3d cur = Evaluate((double)j / samples);
                total += Distance(prev, cur);
                prev = cur;
            }
            return total;
        }

        private static double Distance(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
