using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The dome (0.1.21): a half-sphere rising from the two-click base diameter, out of the clicked
    /// plane. Stores the two base anchors PLUS the apex (Primary) — the apex is fully derived (centre +
    /// radius along the base normal) but storing it makes the dome's SIDE persistent: SHIFT at placement
    /// puts it below the base (a bowl), and every re-derivation keeps whichever side it is on (the
    /// equilateral-triangle pattern). Hollow = the curved shell, OPEN across the base (the shell's bottom
    /// ring outlines it); Filled = the solid half-ball.
    /// </summary>
    /// <remarks>
    /// Base normal = the intrinsic plane axis (first click's face) with the base direction projected out,
    /// so a ground dome rises up and a wall dome bulges out of the wall. Voxelisation reuses the sphere's
    /// EXACT surface-crossing lattice test, clipped to the apex's half-space (cells with any part on the
    /// apex side survive) — watertight, one cell thick. Same scan guard as the sphere. Dragging the apex
    /// is absorbed (it snaps back to the derived position — no break target in v1).
    /// </remarks>
    public sealed class DomeShape : IGuideShape
    {
        private const double MinRadius = 0.05;
        private const long MaxScanCells = 4_000_000;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a fresh dome from the two draft clicks (the base diameter).</summary>
        public DomeShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, bool inverted = false)
        {
            _preferredAxis = planeAxis;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true),
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true)   // placed by DeriveApex
            };
            DeriveApex(initialSide: inverted ? -1.0 : 1.0);
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public DomeShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
        }

        // --- frame ------------------------------------------------------------------------------

        // Centre, radius, and the (side-signed) rise direction n̂. û/m̂ span the base plane.
        private bool TryGetFrame(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n)
        {
            c = u = m = n = null; r = 0;
            if (_controlPoints.Count < 3) return false;
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            if (!ShapeGeometry.TryGetFrame(a, b, _preferredAxis, out u, out m, out double baseLen))
                return false;
            c = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            r = baseLen * 0.5;
            if (r < MinRadius) return false;

            n = ShapeGeometry.Cross(u, m);                       // the base normal (unit: u ⟂ m, both unit)
            Vec3d v = _controlPoints[2].WorldPosition;
            double side = ShapeGeometry.Dot(new Vec3d(v.X - c.X, v.Y - c.Y, v.Z - c.Z), n) < 0 ? -1.0 : 1.0;
            n = new Vec3d(n.X * side, n.Y * side, n.Z * side);   // n̂ points at the apex's side
            return true;
        }

        // Puts the apex where it belongs: centre + R along the (side-preserving) base normal.
        private void DeriveApex(double? initialSide = null)
        {
            if (_controlPoints.Count < 3) return;
            if (initialSide.HasValue)
            {
                // At creation the apex is still a placeholder — aim it before the frame reads its side.
                Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
                if (!ShapeGeometry.TryGetFrame(a, b, _preferredAxis, out Vec3d u0, out Vec3d m0, out double len))
                    return;
                Vec3d n0 = ShapeGeometry.Cross(u0, m0);
                double r0 = len * 0.5, s = initialSide.Value;
                var c0 = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
                _controlPoints[2].SetPosition(c0.X + n0.X * r0 * s, c0.Y + n0.Y * r0 * s, c0.Z + n0.Z * r0 * s);
                return;
            }
            if (!TryGetFrame(out Vec3d c, out double r, out _, out _, out Vec3d n)) return;
            _controlPoints[2].SetPosition(c.X + n.X * r, c.Y + n.Y * r, c.Z + n.Z * r);
        }

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            var result = new List<VoxelPosition>();
            if (!TryGetFrame(out Vec3d c, out double r, out _, out _, out Vec3d n)) return result;

            long cellsPerAxis = (long)(2.0 * r * 16.0 / scale) + 3;
            if (cellsPerAxis * cellsPerAxis * cellsPerAxis > MaxScanCells) return result;   // scan guard

            double cell = scale / 16.0;
            double r2 = r * r;
            // Half of a cell's extent projected onto n̂ — the exact reach of its corners across the plane.
            double spread = (Math.Abs(n.X) + Math.Abs(n.Y) + Math.Abs(n.Z)) * cell * 0.5;

            int min16X = AlignDown(c.X - r, scale), max16X = AlignDown(c.X + r, scale);
            int min16Y = AlignDown(c.Y - r, scale), max16Y = AlignDown(c.Y + r, scale);
            int min16Z = AlignDown(c.Z - r, scale), max16Z = AlignDown(c.Z + r, scale);

            for (int ix = min16X; ix <= max16X; ix += scale)
            {
                double lox = ix / 16.0;
                double nx = Nearest(c.X, lox, lox + cell), fx = Farthest(c.X, lox, lox + cell);
                double ccx = lox + cell * 0.5 - c.X;
                for (int iy = min16Y; iy <= max16Y; iy += scale)
                {
                    double loy = iy / 16.0;
                    double ny = Nearest(c.Y, loy, loy + cell), fy = Farthest(c.Y, loy, loy + cell);
                    double nxy2 = nx * nx + ny * ny;
                    if (nxy2 > r2) continue;
                    double fxy2 = fx * fx + fy * fy;
                    double ccy = loy + cell * 0.5 - c.Y;
                    for (int iz = min16Z; iz <= max16Z; iz += scale)
                    {
                        double loz = iz / 16.0;
                        double nz = Nearest(c.Z, loz, loz + cell);
                        double dmin2 = nxy2 + nz * nz;
                        if (dmin2 > r2) continue;

                        double ccz = loz + cell * 0.5 - c.Z;
                        // Half-space clip: some part of the cell must lie on the apex side of the base.
                        double sc = ccx * n.X + ccy * n.Y + ccz * n.Z;
                        if (sc + spread < 0) continue;

                        if (!filled)
                        {
                            double fz = Farthest(c.Z, loz, loz + cell);
                            if (fxy2 + fz * fz < r2) continue;   // entirely inside the ball → interior
                        }
                        result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
                }
            }

            ClaimHandleMarkers(result, scale);
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false)
        {
            if (!TryGetFrame(out _, out double r, out _, out _, out _)) return 0;
            long cellsPerAxis = (long)(2.0 * r * 16.0 / scale) + 3;
            if (cellsPerAxis * cellsPerAxis * cellsPerAxis > MaxScanCells) return int.MaxValue / 4;
            return GetVoxelPositions(scale, filled).Count;
        }

        private void ClaimHandleMarkers(List<VoxelPosition> cells, int scale)
        {
            for (int i = 0; i < 3 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                VoxelRenderType t = cp.IsLocked ? VoxelRenderType.Locked
                    : cp.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(cells, scale, cp.WorldPosition, t);
            }
        }

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        private static double Nearest(double centre, double lo, double hi) =>
            centre < lo ? lo - centre : centre > hi ? centre - hi : 0.0;

        private static double Farthest(double centre, double lo, double hi) =>
            Math.Max(Math.Abs(centre - lo), Math.Abs(centre - hi));

        // --- IGuideShape: curve queries (targeting wireframe) ----------------------------------------

        // Base circle (full loop from A) → arc A→apex→B → base quarter B→P(+m̂) → arc P→apex→P′(−m̂).
        // Every junction lies on the wireframe, so no phantom chords.
        private List<Vec3d> Wireframe(int samplesPerLoop)
        {
            var pts = new List<Vec3d>();
            if (!TryGetFrame(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n)) return pts;
            int nn = Math.Max(24, samplesPerLoop);

            Vec3d At(Vec3d e1, Vec3d e2, double ang) => new Vec3d(
                c.X + e1.X * r * Math.Cos(ang) + e2.X * r * Math.Sin(ang),
                c.Y + e1.Y * r * Math.Cos(ang) + e2.Y * r * Math.Sin(ang),
                c.Z + e1.Z * r * Math.Cos(ang) + e2.Z * r * Math.Sin(ang));

            for (int i = 0; i <= nn; i++) pts.Add(At(u, m, Math.PI + 2.0 * Math.PI * i / nn));   // base, from A
            for (int i = 0; i <= nn / 2; i++) pts.Add(At(u, n, Math.PI - Math.PI * i / (nn / 2.0)));  // A→apex→B
            int q = Math.Max(6, nn / 4);
            for (int i = 0; i <= q; i++) pts.Add(At(u, m, 0.5 * Math.PI * i / q));               // B→P
            for (int i = 0; i <= nn / 2; i++) pts.Add(At(m, n, Math.PI * i / (nn / 2.0)));       // P→apex→P′
            return pts;
        }

        public List<Vec3d> SampleCurve(int samples) => Wireframe(Math.Max(24, samples / 3));

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

        public void InsertControlPoint(float t, Vec3d position) { /* nothing to insert on a dome */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;
            if (index == 2)
            {
                // The apex is fully derived; a drag is absorbed — it snaps back to centre + R, keeping
                // only the SIDE the drag ended on (dragging it through the base flips dome ↔ bowl).
                _controlPoints[2].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
                DeriveApex();
                return;
            }
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            DeriveApex();                                        // base moves absorb: recentre + resize
        }

        public void RecalculatePhantomPoints() { /* no phantoms */ }

        public bool WouldBreakOnMove(int index) => false;

        public bool BreakConstraint() => false;
    }
}
