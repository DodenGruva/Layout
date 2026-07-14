using System;
using System.Collections.Generic;
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
    /// VOXELISATION. Unlike every planar shape (which march along curves), a volume scans the cell lattice
    /// inside the ball's bounding box and keeps the cells that satisfy the predicate:
    ///   shell — the surface crosses the cell (nearest-point distance ≤ R ≤ farthest-corner distance);
    ///   solid — the cell touches the ball (nearest-point distance ≤ R).
    /// The shell test is exact, so the shell is hole-free and minimally thick at every scale.
    ///
    /// SCAN GUARD. A shell's cell count grows with R², a solid's with R³, and the SCAN itself with the
    /// bounding box's R³ — a huge fine-scale drag could stall the game computing voxels the cap will
    /// reject anyway. Above <see cref="MaxScanCells"/> lattice cells the shape SHORT-CIRCUITS:
    /// <see cref="GetVoxelCount"/> reports a huge count (so cap checks reject instantly and the draft
    /// ghost's coarsening picks a coarser preview scale) and <see cref="GetVoxelPositions"/> returns
    /// empty. The two disagree ONLY in that regime, which cap rejection makes unreachable for real guides.
    ///
    /// TARGETING. The visible body is a whole surface, but targeting tests polylines — so
    /// <see cref="SampleCurve"/> returns a WIREFRAME: the equator plus two meridians, routed so
    /// consecutive segments always lie on the wireframe (loops share their junction points; a quarter-arc
    /// of the equator bridges the two meridians). Clicking between wireframe lines misses the body — the
    /// anchors are the reliable handles (body clicks map to the nearest handle anyway, per the parametric
    /// policy). No inserts, no constraints in v1, no phantoms; Surface projection and Divisions do not
    /// apply (the GUI greys them).
    /// </remarks>
    public sealed class SphereShape : IGuideShape, IThresholdVoxelCounter
    {
        private const double MinRadius = 0.05;

        /// <summary>Lattice-scan ceiling (cells in the bounding box) before the short-circuit kicks in.
        /// ~158³ — covers shells far beyond any sane per-guide voxel cap before it ever triggers.</summary>
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

        // Bounding-box cell count at this scale, for the scan guard.
        private bool ScanTooBig(int scale, Vec3d c, double r)
        {
            long cellsPerAxis = (long)(2.0 * r * 16.0 / scale) + 3;
            return cellsPerAxis * cellsPerAxis * cellsPerAxis > MaxScanCells;
        }

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            var result = new List<VoxelPosition>();
            if (!TryGetBall(out Vec3d c, out double r)) return result;
            if (ScanTooBig(scale, c, r)) return result;          // see SCAN GUARD in the remarks

            double cell = scale / 16.0;
            double r2 = r * r;

            // Cell-index range on each axis (indices in 1/16 units, aligned to the scale grid).
            int min16X = AlignDown(c.X - r, scale), max16X = AlignDown(c.X + r, scale);
            int min16Y = AlignDown(c.Y - r, scale), max16Y = AlignDown(c.Y + r, scale);
            int min16Z = AlignDown(c.Z - r, scale), max16Z = AlignDown(c.Z + r, scale);

            for (int ix = min16X; ix <= max16X; ix += scale)
            {
                double lox = ix / 16.0;
                double nx = Nearest(c.X, lox, lox + cell), fx = Farthest(c.X, lox, lox + cell);
                for (int iy = min16Y; iy <= max16Y; iy += scale)
                {
                    double loy = iy / 16.0;
                    double ny = Nearest(c.Y, loy, loy + cell), fy = Farthest(c.Y, loy, loy + cell);
                    double nxy2 = nx * nx + ny * ny;
                    if (nxy2 > r2) continue;                     // whole row is outside the ball
                    double fxy2 = fx * fx + fy * fy;
                    for (int iz = min16Z; iz <= max16Z; iz += scale)
                    {
                        double loz = iz / 16.0;
                        double nz = Nearest(c.Z, loz, loz + cell);
                        double dmin2 = nxy2 + nz * nz;
                        if (dmin2 > r2) continue;                // cell entirely outside

                        if (!filled)
                        {
                            // Shell: the surface must also REACH the cell — its farthest corner is outside.
                            double fz = Farthest(c.Z, loz, loz + cell);
                            if (fxy2 + fz * fz < r2) continue;   // cell entirely inside → interior, skip
                        }
                        result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
                }
            }

            for (int i = 0; i < 2 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition,
                    cp.IsLocked ? VoxelRenderType.Locked : VoxelRenderType.Anchor);
            }
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false)
            => GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter)
        {
            if (!TryGetBall(out Vec3d c, out double r)) return 0;
            // The short-circuit: report "far too many" without scanning, so cap checks reject instantly
            // and the ghost's scale-coarsening steps past this scale (see SCAN GUARD in the remarks).
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
                double nx = Nearest(c.X, lox, lox + cell), fx = Farthest(c.X, lox, lox + cell);
                for (int iy = min16Y; iy <= max16Y; iy += scale)
                {
                    double loy = iy / 16.0;
                    double ny = Nearest(c.Y, loy, loy + cell), fy = Farthest(c.Y, loy, loy + cell);
                    double nxy2 = nx * nx + ny * ny;
                    if (nxy2 > r2) continue;
                    double fxy2 = fx * fx + fy * fy;
                    for (int iz = min16Z; iz <= max16Z; iz += scale)
                    {
                        double loz = iz / 16.0;
                        double nz = Nearest(c.Z, loz, loz + cell);
                        if (nxy2 + nz * nz > r2) continue;
                        if (!filled)
                        {
                            double fz = Farthest(c.Z, loz, loz + cell);
                            if (fxy2 + fz * fz < r2) continue;
                        }
                        count++;
                        if (count > stopAfter) return GuideShapeVoxelCounting.Exceeded(stopAfter);
                    }
                }
            }
            return count;
        }

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        // Signed-distance helpers: offset from the centre coordinate to the nearest / farthest point of
        // the cell's [lo, hi] span on that axis.
        private static double Nearest(double centre, double lo, double hi) =>
            centre < lo ? lo - centre : centre > hi ? centre - hi : 0.0;

        private static double Farthest(double centre, double lo, double hi) =>
            Math.Max(Math.Abs(centre - lo), Math.Abs(centre - hi));

        // --- IGuideShape: curve queries (the targeting wireframe) -----------------------------------

        // The wireframe polyline: equator (full loop) → meridian A (full loop, sharing the equator's +X
        // start point) → a quarter of the equator over to +Z → meridian B (full loop from +Z). Every
        // consecutive segment lies ON the wireframe, so targeting never hits a phantom chord.
        private List<Vec3d> Wireframe(Vec3d c, double r, int samplesPerLoop)
        {
            int n = Math.Max(24, samplesPerLoop);
            var pts = new List<Vec3d>(3 * n + n / 4 + 4);

            void Loop(Func<double, Vec3d> at)
            {
                for (int i = 0; i <= n; i++) pts.Add(at(2.0 * Math.PI * i / n));
            }

            Vec3d Equator(double t) => new Vec3d(c.X + r * Math.Cos(t), c.Y, c.Z + r * Math.Sin(t));
            Vec3d MeridianXY(double t) => new Vec3d(c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t), c.Z);
            Vec3d MeridianZY(double t) => new Vec3d(c.X, c.Y + r * Math.Sin(t), c.Z + r * Math.Cos(t));

            Loop(Equator);                                        // +X … back to +X
            Loop(MeridianXY);                                     // starts/ends at +X too — no chord
            int q = Math.Max(6, n / 4);
            for (int i = 0; i <= q; i++)                          // bridge along the equator +X → +Z
                pts.Add(Equator(2.0 * Math.PI * i / (4.0 * q)));
            Loop(MeridianZY);                                     // starts/ends at +Z — no chord
            return pts;
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
