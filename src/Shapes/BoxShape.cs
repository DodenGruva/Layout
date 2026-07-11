using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The box (0.1.21): THREE clicks — two DIAGONAL corners of the base rectangle (the rectangle
    /// gesture) then a height click. A true box, not a cube: the three edge lengths are independent.
    /// Stores corner A (anchor), the diagonal corner C (anchor, snapped onto A's plane layer like
    /// <see cref="RectangleShape"/>), and a lid handle (Primary) over the base centre at the clicked
    /// height. Axis-aligned to the clicked plane, so the base lies flat and the height runs along the
    /// plane normal. Hollow = all six faces as a one-voxel shell; Filled = the solid box.
    /// </summary>
    /// <remarks>
    /// Voxelisation is the sphere's shell recipe in box-local coordinates: a cell centre is SOLID when it
    /// lies inside the box, and SHELL when it is solid but not inside the box eroded by one cell on every
    /// side — a watertight, ~1-cell shell (a box thinner than two cells on some axis is all shell there,
    /// correctly). Same scan guard as the other volumes. The base corners absorb as resize; the lid
    /// handle slides along the height axis.
    /// </remarks>
    public sealed class BoxShape : IGuideShape
    {
        private const double MinSide = 0.05;
        private const double MinHeight = 0.05;
        private const long MaxScanCells = 4_000_000;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _planeAxis;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a fresh box from the two diagonal base corners; the born height is the base's
        /// shorter side until the third click sets it.</summary>
        public BoxShape(Vec3d a, Vec3d c, PlaneAxis planeAxis, bool inverted = false)
        {
            _planeAxis = planeAxis;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(c.X, c.Y, c.Z), isAnchor: true),
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true)
            };
            SnapBaseCorner();
            if (TryGetBase(out Vec3d bc, out _, out _, out double du, out double dv, out Vec3d n0))
            {
                double h = Math.Max(MinHeight, Math.Min(Math.Abs(du), Math.Abs(dv))) * (inverted ? -1.0 : 1.0);
                SeatHandle(bc, n0, h);
            }
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public BoxShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _planeAxis = planeAxis;
        }

        // --- frame ------------------------------------------------------------------------------

        // Base centre, the two in-plane axes u1/u2, the signed side extents du/dv from A, and the plane
        // normal n. C is kept snapped onto A's plane, so du/dv are read off the stored diagonal.
        private bool TryGetBase(out Vec3d baseCentre, out Vec3d u1, out Vec3d u2,
            out double du, out double dv, out Vec3d n)
        {
            baseCentre = u1 = u2 = n = null; du = dv = 0;
            if (_controlPoints.Count < 2) return false;
            Vec3d a = _controlPoints[0].WorldPosition, c = _controlPoints[1].WorldPosition;
            ShapeGeometry.InPlaneAxes(_planeAxis, out u1, out u2);
            n = ShapeGeometry.AxisVec(_planeAxis);
            var diag = new Vec3d(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            du = ShapeGeometry.Dot(diag, u1);
            dv = ShapeGeometry.Dot(diag, u2);
            if (Math.Abs(du) < MinSide || Math.Abs(dv) < MinSide) return false;
            baseCentre = new Vec3d(a.X + (u1.X * du + u2.X * dv) * 0.5,
                                   a.Y + (u1.Y * du + u2.Y * dv) * 0.5,
                                   a.Z + (u1.Z * du + u2.Z * dv) * 0.5);
            return true;
        }

        private bool TryGetFull(out Vec3d a, out Vec3d u1, out Vec3d u2, out Vec3d n,
            out double du, out double dv, out double h)
        {
            a = null; h = 0;
            if (!TryGetBase(out Vec3d bc, out u1, out u2, out du, out dv, out n) || _controlPoints.Count < 3)
            { u1 = u2 = n = null; return false; }
            a = _controlPoints[0].WorldPosition;
            Vec3d p = _controlPoints[2].WorldPosition;
            h = (p.X - bc.X) * n.X + (p.Y - bc.Y) * n.Y + (p.Z - bc.Z) * n.Z;
            if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
            return true;
        }

        // Keeps the stored diagonal corner ON A's plane layer (drops any drift along the normal).
        private void SnapBaseCorner()
        {
            if (_controlPoints.Count < 2) return;
            Vec3d a = _controlPoints[0].WorldPosition, c = _controlPoints[1].WorldPosition;
            ShapeGeometry.InPlaneAxes(_planeAxis, out Vec3d u1, out Vec3d u2);
            var diag = new Vec3d(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            double du = ShapeGeometry.Dot(diag, u1), dv = ShapeGeometry.Dot(diag, u2);
            _controlPoints[1].SetPosition(a.X + u1.X * du + u2.X * dv,
                                          a.Y + u1.Y * du + u2.Y * dv,
                                          a.Z + u1.Z * du + u2.Z * dv);
        }

        private void SeatHandle(Vec3d baseCentre, Vec3d n, double h) =>
            _controlPoints[2].SetPosition(baseCentre.X + n.X * h, baseCentre.Y + n.Y * h, baseCentre.Z + n.Z * h);

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            var result = new List<VoxelPosition>();
            if (!TryGetFull(out Vec3d a, out Vec3d u1, out Vec3d u2, out Vec3d n,
                out double du, out double dv, out double h)) return result;
            if (ScanTooBig(scale, du, dv, h)) return result;

            double cell = scale / 16.0;
            // Local coordinate ranges (signed extents → ordered [lo,hi]).
            double sLo = Math.Min(0, du), sHi = Math.Max(0, du);
            double tLo = Math.Min(0, dv), tHi = Math.Max(0, dv);
            double wLo = Math.Min(0, h),  wHi = Math.Max(0, h);

            GetAabb(a, u1, u2, n, du, dv, h, cell,
                out double x0, out double y0, out double z0, out double x1, out double y1, out double z1);

            for (int ix = AlignDown(x0, scale); ix <= AlignDown(x1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - a.X;
                for (int iy = AlignDown(y0, scale); iy <= AlignDown(y1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - a.Y;
                    for (int iz = AlignDown(z0, scale); iz <= AlignDown(z1, scale); iz += scale)
                    {
                        double pz = iz / 16.0 + cell * 0.5 - a.Z;
                        double s = px * u1.X + py * u1.Y + pz * u1.Z;
                        double t = px * u2.X + py * u2.Y + pz * u2.Z;
                        double w = px * n.X  + py * n.Y  + pz * n.Z;

                        bool solid = s >= sLo && s <= sHi && t >= tLo && t <= tHi && w >= wLo && w <= wHi;
                        if (!solid) continue;

                        if (!filled)
                        {
                            bool interior = s >= sLo + cell && s <= sHi - cell
                                         && t >= tLo + cell && t <= tHi - cell
                                         && w >= wLo + cell && w <= wHi - cell;
                            if (interior) continue;
                        }
                        result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
                }
            }

            for (int i = 0; i < 3 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                VoxelRenderType type = cp.IsLocked ? VoxelRenderType.Locked
                    : cp.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition, type);
            }
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false)
        {
            if (!TryGetFull(out _, out _, out _, out _, out double du, out double dv, out double h)) return 0;
            if (ScanTooBig(scale, du, dv, h)) return int.MaxValue / 4;
            return GetVoxelPositions(scale, filled).Count;
        }

        private static bool ScanTooBig(int scale, double du, double dv, double h)
        {
            double span = Math.Abs(du) + Math.Abs(dv) + Math.Abs(h);
            long cellsPerAxis = (long)(span * 16.0 / scale) + 3;
            return cellsPerAxis * cellsPerAxis * cellsPerAxis > MaxScanCells;
        }

        private static void GetAabb(Vec3d a, Vec3d u1, Vec3d u2, Vec3d n, double du, double dv, double h,
            double cell, out double x0, out double y0, out double z0, out double x1, out double y1, out double z1)
        {
            x0 = y0 = z0 = double.MaxValue; x1 = y1 = z1 = double.MinValue;
            for (int c = 0; c < 8; c++)
            {
                double s = (c & 1) == 0 ? 0 : du;
                double t = (c & 2) == 0 ? 0 : dv;
                double w = (c & 4) == 0 ? 0 : h;
                double vx = a.X + u1.X * s + u2.X * t + n.X * w;
                double vy = a.Y + u1.Y * s + u2.Y * t + n.Y * w;
                double vz = a.Z + u1.Z * s + u2.Z * t + n.Z * w;
                x0 = Math.Min(x0, vx); x1 = Math.Max(x1, vx);
                y0 = Math.Min(y0, vy); y1 = Math.Max(y1, vy);
                z0 = Math.Min(z0, vz); z1 = Math.Max(z1, vz);
            }
            x0 -= cell; y0 -= cell; z0 -= cell; x1 += cell; y1 += cell; z1 += cell;
        }

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        // --- IGuideShape: curve queries (targeting wireframe = the 12 edges) -------------------------

        private List<Vec3d> Wireframe()
        {
            var pts = new List<Vec3d>();
            if (!TryGetFull(out Vec3d a, out Vec3d u1, out Vec3d u2, out Vec3d n,
                out double du, out double dv, out double h)) return pts;

            Vec3d Corner(double s, double t, double w) => new Vec3d(
                a.X + u1.X * s + u2.X * t + n.X * w,
                a.Y + u1.Y * s + u2.Y * t + n.Y * w,
                a.Z + u1.Z * s + u2.Z * t + n.Z * w);

            Vec3d A = Corner(0, 0, 0), B = Corner(du, 0, 0), C = Corner(du, dv, 0), D = Corner(0, dv, 0);
            Vec3d A2 = Corner(0, 0, h), B2 = Corner(du, 0, h), C2 = Corner(du, dv, h), D2 = Corner(0, dv, h);

            // A trail covering all 12 edges; every consecutive pair is a real edge (some retraced).
            foreach (Vec3d p in new[] { A, B, C, D, A, A2, B2, B, C, C2, D2, D, A, A2, D2, C2, B2 })
                pts.Add(p);
            return pts;
        }

        public List<Vec3d> SampleCurve(int samples) => Wireframe();

        public float GetNearestT(Vec3d worldPos)
        {
            List<Vec3d> wire = Wireframe();
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
            List<Vec3d> wire = Wireframe();
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

        public void InsertControlPoint(float t, Vec3d position) { /* nothing to insert */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;

            if (index == 2)
            {
                // The lid handle slides along the height axis (signed — through the base flips the box).
                if (!TryGetBase(out Vec3d bc, out _, out _, out _, out _, out Vec3d n)) return;
                double h = (newPosition.X - bc.X) * n.X + (newPosition.Y - bc.Y) * n.Y + (newPosition.Z - bc.Z) * n.Z;
                if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
                SeatHandle(bc, n, h);
                return;
            }

            // A base-corner move absorbs (resize); keep C snapped and the lid at its signed height.
            double hOld = 0;
            bool hadFull = TryGetFull(out _, out _, out _, out _, out _, out _, out hOld);
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            SnapBaseCorner();
            if (hadFull && TryGetBase(out Vec3d bc2, out _, out _, out _, out _, out Vec3d n2))
                SeatHandle(bc2, n2, hOld);
        }

        public void RecalculatePhantomPoints() { /* no phantoms */ }

        public bool WouldBreakOnMove(int index) => false;

        public bool BreakConstraint() => false;
    }
}
