using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The cylinder (0.1.21): THREE clicks — the base diameter (two anchors, the circle gesture), then a
    /// height click, projected onto the base's axis and stored as the third point (Primary — the "lid
    /// handle", grabbable to re-height after placement). The axis is the clicked plane's normal, so a
    /// ground cylinder stands upright and a wall cylinder pokes out of the wall. Hollow = the open TUBE
    /// (the lateral surface — its ends read as rings); Filled = the solid, flat-capped cylinder.
    /// </summary>
    /// <remarks>
    /// VOXELISATION is centre-banded rather than exact (the axis can be arbitrary after 3D drags): a cell
    /// joins the shell when its centre lies within half a cell-diagonal of the lateral surface, inside the
    /// axial span. Watertight by construction; walls may run two cells thick at diagonal orientations —
    /// the accepted v1 trade. Solid is the matching fattened disc column, a strict superset of the shell.
    /// Same scan guard scheme as the sphere.
    /// </remarks>
    public sealed class CylinderShape : IGuideShape, IThresholdVoxelCounter
    {
        private const double MinRadius = 0.05;
        private const double MinHeight = 0.05;
        private const long MaxScanCells = 4_000_000;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a fresh cylinder from the base clicks; the born height is one diameter
        /// (a square profile) until the third click sets it.</summary>
        public CylinderShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, bool inverted = false)
        {
            _preferredAxis = planeAxis;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true),
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true)
            };
            if (TryGetBaseFrame(out Vec3d c, out double r, out _, out _, out Vec3d n0))
            {
                double h = Math.Max(MinHeight, 2.0 * r) * (inverted ? -1.0 : 1.0);
                _controlPoints[2].SetPosition(c.X + n0.X * h, c.Y + n0.Y * h, c.Z + n0.Z * h);
            }
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public CylinderShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
        }

        // --- frame ------------------------------------------------------------------------------

        // The UNSIGNED base frame (n̂ = positive-biased base normal). The signed height rides h below.
        private bool TryGetBaseFrame(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n)
        {
            c = u = m = n = null; r = 0;
            if (_controlPoints.Count < 2) return false;
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            if (!ShapeGeometry.TryGetFrame(a, b, _preferredAxis, out u, out m, out double baseLen))
                return false;
            c = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            r = baseLen * 0.5;
            // Deterministic axis (0.1.23): +up regardless of base-anchor order, so dragging the second
            // base point to the other side no longer inverts the cylinder into the floor.
            n = ShapeGeometry.BaseNormal(u, _preferredAxis);
            return n != null && r >= MinRadius;
        }

        private bool TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n, out double h)
        {
            h = 0;
            if (!TryGetBaseFrame(out c, out r, out u, out m, out n) || _controlPoints.Count < 3) return false;
            Vec3d p = _controlPoints[2].WorldPosition;
            h = (p.X - c.X) * n.X + (p.Y - c.Y) * n.Y + (p.Z - c.Z) * n.Z;
            if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
            return true;
        }

        // Re-seats the lid handle exactly on the axis at signed height h (after any move).
        private void SeatHandle(Vec3d c, Vec3d n, double h) =>
            _controlPoints[2].SetPosition(c.X + n.X * h, c.Y + n.Y * h, c.Z + n.Z * h);

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            filled = false;   // 0.2.17: 3D volumes are always hollow shells (see GuideShapeTypes.IsVolume)
            var result = new List<VoxelPosition>();
            if (!TryGetFull(out Vec3d c, out double r, out _, out _, out Vec3d n, out double h)) return result;
            if (ScanTooBig(scale, r, h)) return result;

            double cell = scale / 16.0;
            double hd = cell * 0.866;                            // half the cell diagonal — the band width
            double lo = Math.Min(0, h), hi = Math.Max(0, h);

            GetAabb(c, n, r, h, cell, out double ax0, out double ay0, out double az0,
                out double ax1, out double ay1, out double az1);

            for (int ix = AlignDown(ax0, scale); ix <= AlignDown(ax1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(ay0, scale); iy <= AlignDown(ay1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(az0, scale); iz <= AlignDown(az1, scale); iz += scale)
                    {
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double a = px * n.X + py * n.Y + pz * n.Z;          // axial coordinate
                        if (a < lo || a > hi) continue;
                        double rx = px - n.X * a, ry = py - n.Y * a, rz = pz - n.Z * a;
                        double rho = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                        bool keep = filled ? rho <= r + hd : Math.Abs(rho - r) <= hd;
                        if (keep) result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
                }
            }

            ClaimHandleMarkers(result, scale);
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false)
            => GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter)
        {
            filled = false;   // 0.2.17: 3D volumes are always hollow shells (see GuideShapeTypes.IsVolume)
            if (!TryGetFull(out Vec3d c, out double r, out _, out _, out Vec3d n, out double h)) return 0;
            if (ScanTooBig(scale, r, h)) return GuideShapeVoxelCounting.Exceeded(stopAfter);

            stopAfter = Math.Max(0, stopAfter);
            double cell = scale / 16.0;
            double hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);
            int count = 0;

            GetAabb(c, n, r, h, cell, out double ax0, out double ay0, out double az0,
                out double ax1, out double ay1, out double az1);

            for (int ix = AlignDown(ax0, scale); ix <= AlignDown(ax1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(ay0, scale); iy <= AlignDown(ay1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(az0, scale); iz <= AlignDown(az1, scale); iz += scale)
                    {
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double a = px * n.X + py * n.Y + pz * n.Z;
                        if (a < lo || a > hi) continue;
                        double rx = px - n.X * a, ry = py - n.Y * a, rz = pz - n.Z * a;
                        double rho = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                        bool keep = filled ? rho <= r + hd : Math.Abs(rho - r) <= hd;
                        if (!keep) continue;
                        count++;
                        if (count > stopAfter) return GuideShapeVoxelCounting.Exceeded(stopAfter);
                    }
                }
            }
            return count;
        }

        private static bool ScanTooBig(int scale, double r, double h)
        {
            double span = 2.0 * r + Math.Abs(h);                 // conservative per-axis extent
            long cellsPerAxis = (long)(span * 16.0 / scale) + 3;
            return cellsPerAxis * cellsPerAxis * cellsPerAxis > MaxScanCells;
        }

        private static void GetAabb(Vec3d c, Vec3d n, double r, double h, double cell,
            out double x0, out double y0, out double z0, out double x1, out double y1, out double z1)
        {
            double ex = c.X + n.X * h, ey = c.Y + n.Y * h, ez = c.Z + n.Z * h;
            double pad = r + cell;
            x0 = Math.Min(c.X, ex) - pad; x1 = Math.Max(c.X, ex) + pad;
            y0 = Math.Min(c.Y, ey) - pad; y1 = Math.Max(c.Y, ey) + pad;
            z0 = Math.Min(c.Z, ez) - pad; z1 = Math.Max(c.Z, ez) + pad;
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

        // --- IGuideShape: curve queries (targeting wireframe) ----------------------------------------

        // Base loop from A → lateral A→A′ → top loop from A′ → top half back to B′ → lateral B′→B.
        private List<Vec3d> Wireframe(int samplesPerLoop)
        {
            var pts = new List<Vec3d>();
            if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n, out double h))
                return pts;
            int nn = Math.Max(24, samplesPerLoop);

            Vec3d Ring(double axial, double ang) => new Vec3d(
                c.X + n.X * axial + (u.X * Math.Cos(ang) + m.X * Math.Sin(ang)) * r,
                c.Y + n.Y * axial + (u.Y * Math.Cos(ang) + m.Y * Math.Sin(ang)) * r,
                c.Z + n.Z * axial + (u.Z * Math.Cos(ang) + m.Z * Math.Sin(ang)) * r);

            for (int i = 0; i <= nn; i++) pts.Add(Ring(0, Math.PI + 2.0 * Math.PI * i / nn));   // base, from A
            pts.Add(Ring(h, Math.PI));                                                          // lateral A→A′
            for (int i = 0; i <= nn; i++) pts.Add(Ring(h, Math.PI + 2.0 * Math.PI * i / nn));   // top, from A′
            int half = Math.Max(8, nn / 2);
            for (int i = 0; i <= half; i++) pts.Add(Ring(h, Math.PI - Math.PI * i / half));     // A′→B′
            pts.Add(Ring(0, 0));                                                                // lateral B′→B
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

        public void InsertControlPoint(float t, Vec3d position) { /* nothing to insert */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;

            if (index == 2)
            {
                // The lid handle slides along the AXIS: the drag projects onto it (signed — dragging
                // through the base flips which way the cylinder grows).
                if (!TryGetBaseFrame(out Vec3d c0, out _, out _, out _, out Vec3d n0)) return;
                double h = (newPosition.X - c0.X) * n0.X + (newPosition.Y - c0.Y) * n0.Y
                    + (newPosition.Z - c0.Z) * n0.Z;
                if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
                SeatHandle(c0, n0, h);
                return;
            }

            // A base move absorbs as recentre/resize; the lid keeps its signed height on the new axis.
            double hOld = 0;
            bool hadFull = TryGetFull(out _, out _, out _, out _, out _, out hOld);
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            if (hadFull && TryGetBaseFrame(out Vec3d c1, out _, out _, out _, out Vec3d n1))
                SeatHandle(c1, n1, hOld);
        }

        public void RecalculatePhantomPoints() { /* no phantoms */ }

        public bool WouldBreakOnMove(int index) => false;

        public bool BreakConstraint() => false;
    }
}
