using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The sphere (Session 11, 0.1.20) — the mod's first 3D VOLUME. Defined by exactly two stored anchors:
    /// the two draft clicks are opposite ends of the ball (a diameter — the circle gesture, lifted to 3D).
    /// Centre and radius derive per query, so grabbing either anchor absorbs as a resize/recentre exactly
    /// like the circle's diameter anchors. Hollow = the SHELL (the voxels the mathematical surface passes
    /// through — watertight and one cell thick by construction); Filled = the SOLID ball.
    /// </summary>
    /// <remarks>
    /// VOXELISATION. Unlike every planar shape (which march along curves), a volume evaluates lattice cells
    /// against these predicates:
    ///   shell — the surface crosses the cell (nearest-point distance ≤ R ≤ farthest-corner distance);
    ///   solid — the cell touches the ball (nearest-point distance ≤ R).
    /// The shell test is exact, so the shell is hole-free and minimally thick at every scale.
    ///
    /// Hollow spheres use <see cref="SphericalShellScan"/>: it walks X/Y columns, solves the two Z surface
    /// bands analytically, then applies the exact predicate above. Its work follows surface area instead of
    /// the bounding cube. Filled spheres still scan the solid bounding lattice and retain
    /// <see cref="MaxScanCells"/> because their output and scan cost both grow with R³.
    ///
    /// TARGETING. The visible body is a whole surface, but targeting tests polylines — so
    /// <see cref="SampleCurve"/> returns a WIREFRAME: the equator plus two meridians, routed so
    /// consecutive segments always lie on the wireframe (loops share their junction points; a quarter-arc
    /// of the equator bridges the two meridians). Clicking between wireframe lines misses the body — the
    /// anchors are the reliable handles (body clicks map to the nearest handle anyway, per the parametric
    /// policy). No inserts, no constraints in v1, no phantoms; Surface projection and Divisions do not
    /// apply (the GUI greys them).
    /// </remarks>
    public sealed class SphereShape : IGuideShape, IThresholdVoxelCounter, IProgressiveVoxelShape,
        IIntrinsicGuideExtent
    {
        private const double MinRadius = 0.05;

        /// <summary>Filled-volume lattice-scan ceiling (cells in the bounding box).</summary>
        private const long MaxScanCells = 4_000_000;

        private readonly List<ControlPoint> _controlPoints;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a fresh sphere from the two draft clicks (a diameter).</summary>
        public SphereShape(Vec3d a, Vec3d b)
        {
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true)
            };
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public SphereShape(List<ControlPoint> controlPoints)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
        }

        // --- geometry ------------------------------------------------------------------------------

        private bool TryGetBall(out Vec3d c, out double r)
        {
            c = null; r = 0;
            if (_controlPoints.Count < 2) return false;
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            c = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            r = 0.5 * ShapeGeometry.Dist(a, b);
            return r >= MinRadius;
        }

        public bool TryGetIntrinsicDimensions(out double width, out double height)
        {
            width = height = 0;
            if (!TryGetBall(out _, out double radius)) return false;
            width = height = radius * 2.0;
            return true;
        }

        // Bounding-box cell count at this scale, for the scan guard.
        private bool ScanTooBig(int scale, Vec3d c, double r)
        {
            long cellsPerAxis = (long)(2.0 * r * 16.0 / scale) + 3;
            return cellsPerAxis * cellsPerAxis * cellsPerAxis > MaxScanCells;
        }

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            filled = false;   // 0.2.17: 3D volumes are always hollow shells (see GuideShapeTypes.IsVolume)
            var result = new List<VoxelPosition>();
            if (!TryGetBall(out Vec3d c, out double r)) return result;

            if (!filled)
            {
                SphericalShellScan.Scan(c, r, scale, null, int.MaxValue, result);
                ClaimHandleMarkers(result, scale);
                return result;
            }

            if (ScanTooBig(scale, c, r)) return result;          // filled-volume scan guard

            double cell = scale / 16.0;
            double r2 = r * r;

            // Cell-index range on each axis (indices in 1/16 units, aligned to the scale grid).
            int min16X = AlignDown(c.X - r, scale), max16X = AlignDown(c.X + r, scale);
            int min16Y = AlignDown(c.Y - r, scale), max16Y = AlignDown(c.Y + r, scale);
            int min16Z = AlignDown(c.Z - r, scale), max16Z = AlignDown(c.Z + r, scale);

            for (int ix = min16X; ix <= max16X; ix += scale)
            {
                double lox = ix / 16.0;
                double nx = Nearest(c.X, lox, lox + cell);
                for (int iy = min16Y; iy <= max16Y; iy += scale)
                {
                    double loy = iy / 16.0;
                    double ny = Nearest(c.Y, loy, loy + cell);
                    double nxy2 = nx * nx + ny * ny;
                    if (nxy2 > r2) continue;                     // whole row is outside the ball
                    for (int iz = min16Z; iz <= max16Z; iz += scale)
                    {
                        double loz = iz / 16.0;
                        double nz = Nearest(c.Z, loz, loz + cell);
                        double dmin2 = nxy2 + nz * nz;
                        if (dmin2 > r2) continue;                // cell entirely outside

                        result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
                }
            }

            ClaimHandleMarkers(result, scale);
            return result;
        }

        public List<VoxelPosition> GetVoxelPositionsProgressively(
            int scale, bool filled, int targetVoxelsPerChunk,
            CancellationToken cancellationToken, Action<List<VoxelPosition>> emitChunk)
        {
            var collector = new ProgressiveVoxelCollector(
                targetVoxelsPerChunk, cancellationToken, emitChunk);
            if (!TryGetBall(out Vec3d c, out double r)) return collector.Result;
            SphericalShellScan.ScanProgressively(c, r, scale, null, collector);
            collector.Flush();
            ClaimHandleMarkers(collector.Result, scale);
            return collector.Result;
        }

        public int GetVoxelCount(int scale, bool filled = false)
            => GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter)
        {
            filled = false;   // 0.2.17: 3D volumes are always hollow shells (see GuideShapeTypes.IsVolume)
            if (!TryGetBall(out Vec3d c, out double r)) return 0;

            if (!filled)
                return SphericalShellScan.Scan(c, r, scale, null, stopAfter, null);

            // The short-circuit: report "far too many" without scanning, so cap checks reject instantly
            // and the ghost's scale-coarsening steps past this scale for filled volumes.
            if (ScanTooBig(scale, c, r)) return GuideShapeVoxelCounting.Exceeded(stopAfter);

            stopAfter = Math.Max(0, stopAfter);
            double cell = scale / 16.0;
            double r2 = r * r;
            int count = 0;

            int min16X = AlignDown(c.X - r, scale), max16X = AlignDown(c.X + r, scale);
            int min16Y = AlignDown(c.Y - r, scale), max16Y = AlignDown(c.Y + r, scale);
            int min16Z = AlignDown(c.Z - r, scale), max16Z = AlignDown(c.Z + r, scale);

            for (int ix = min16X; ix <= max16X; ix += scale)
            {
                double lox = ix / 16.0;
                double nx = Nearest(c.X, lox, lox + cell);
                for (int iy = min16Y; iy <= max16Y; iy += scale)
                {
                    double loy = iy / 16.0;
                    double ny = Nearest(c.Y, loy, loy + cell);
                    double nxy2 = nx * nx + ny * ny;
                    if (nxy2 > r2) continue;
                    for (int iz = min16Z; iz <= max16Z; iz += scale)
                    {
                        double loz = iz / 16.0;
                        double nz = Nearest(c.Z, loz, loz + cell);
                        if (nxy2 + nz * nz > r2) continue;
                        count++;
                        if (count > stopAfter) return GuideShapeVoxelCounting.Exceeded(stopAfter);
                    }
                }
            }
            return count;
        }

        private void ClaimHandleMarkers(List<VoxelPosition> cells, int scale)
        {
            for (int i = 0; i < 2 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                ShapeGeometry.ClaimMarker(cells, scale, cp.WorldPosition,
                    cp.IsLocked ? VoxelRenderType.Locked : VoxelRenderType.Anchor);
            }
        }

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        // Signed-distance helpers: offset from the centre coordinate to the nearest / farthest point of
        // the cell's [lo, hi] span on that axis.
        private static double Nearest(double centre, double lo, double hi) =>
            centre < lo ? lo - centre : centre > hi ? centre - hi : 0.0;

        // --- IGuideShape: curve queries (the targeting wireframe) -----------------------------------

        /// <summary>
        /// Meridians in the targeting/structural wireframe. Each meridian is a full great circle through
        /// both poles, so N of them cut the ball into 2N sectors: 4 gives the eight-piece read (v0.3.64,
        /// human request — two was too coarse to judge a large sphere's form).
        /// </summary>
        private const int MeridianCount = 4;

        // The wireframe polyline: equator (full loop) then each meridian in turn, reached by walking ALONG
        // the equator from the previous meridian. Every consecutive segment therefore lies ON the wireframe
        // and targeting never hits a phantom chord — the invariant this routing exists to preserve.
        private List<Vec3d> Wireframe(Vec3d c, double r, int samplesPerLoop)
        {
            int n = Math.Max(24, samplesPerLoop);
            var pts = new List<Vec3d>((MeridianCount + 2) * n);

            Vec3d Equator(double t) => new Vec3d(c.X + r * Math.Cos(t), c.Y, c.Z + r * Math.Sin(t));

            // Great circle through both poles at longitude `lon`. At t = 0 it is exactly Equator(lon), so a
            // meridian always starts and ends on the equator at its own longitude.
            Vec3d Meridian(double lon, double t) => new Vec3d(
                c.X + r * Math.Cos(t) * Math.Cos(lon),
                c.Y + r * Math.Sin(t),
                c.Z + r * Math.Cos(t) * Math.Sin(lon));

            for (int i = 0; i <= n; i++) pts.Add(Equator(2.0 * Math.PI * i / n));   // +X … back to +X

            double current = 0.0;
            for (int k = 0; k < MeridianCount; k++)
            {
                // Meridians only need half the circle of longitudes: one great circle covers lon and
                // lon+180 at once. 4 meridians therefore span 0/45/90/135 degrees.
                double lon = Math.PI * k / MeridianCount;
                BridgeAlongRing(pts, Equator, current, lon, n);
                for (int i = 0; i <= n; i++) pts.Add(Meridian(lon, 2.0 * Math.PI * i / n));
                current = lon;   // a full meridian loop returns to where it started
            }
            return pts;
        }

        /// <summary>
        /// Walks along a ring from one angle to another, sampled at the ring's own density so the voxel
        /// march follows the arc instead of cutting a chord across it. Emits nothing for a zero-length hop.
        /// </summary>
        internal static void BridgeAlongRing(
            List<Vec3d> pts, Func<double, Vec3d> ring, double from, double to, int loopSamples)
        {
            double delta = to - from;
            if (Math.Abs(delta) < 1e-9) return;
            int steps = Math.Max(4,
                (int)Math.Ceiling(Math.Abs(delta) / (2.0 * Math.PI) * loopSamples));
            for (int i = 1; i <= steps; i++) pts.Add(ring(from + delta * i / steps));
        }

        public List<Vec3d> SampleCurve(int samples)
        {
            if (!TryGetBall(out Vec3d c, out double r)) return new List<Vec3d>();
            return Wireframe(c, r, Math.Max(24, samples / 3));
        }

        public float GetNearestT(Vec3d worldPos)
        {
            List<Vec3d> wire = SampleCurve(96);
            if (wire.Count < 2) return 0f;
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < wire.Count; i++)
            {
                double d = ShapeGeometry.Dist(wire[i], worldPos);
                if (d < bestD) { bestD = d; best = i; }
            }
            return (float)best / (wire.Count - 1);
        }

        public Vec3d GetPointAt(float t)
        {
            List<Vec3d> wire = SampleCurve(96);
            if (wire.Count == 0)
                return _controlPoints.Count > 0
                    ? new Vec3d(_controlPoints[0].WorldPosition.X, _controlPoints[0].WorldPosition.Y, _controlPoints[0].WorldPosition.Z)
                    : new Vec3d();
            int i = (int)Math.Round((t < 0f ? 0f : t > 1f ? 1f : t) * (wire.Count - 1));
            Vec3d p = wire[i];
            return new Vec3d(p.X, p.Y, p.Z);
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

        public void InsertControlPoint(float t, Vec3d position) { /* a sphere has exactly two handles */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;
            // Centre + radius derive per query, so a raw anchor move IS the resize/recentre absorb.
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
        }

        public void RecalculatePhantomPoints() { /* no phantoms */ }

        public bool WouldBreakOnMove(int index) => false;

        public bool BreakConstraint() => false;
    }
}
