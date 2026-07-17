using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The cone (0.1.21): THREE clicks — the base diameter (two anchors), then the TIP click, projected
    /// onto the base's axis and stored as the third point (Primary — grabbable to re-height). Radius
    /// shrinks linearly from the base to the tip. Hollow = the sloped shell, OPEN across the base (the
    /// shell's bottom ring outlines it); Filled = the solid cone with a flat base.
    /// </summary>
    /// <remarks>
    /// Same centre-banded voxelisation, scan guard, and lid-handle mechanics as the cylinder — see
    /// <see cref="CylinderShape"/> for the reasoning. The cone's band compares the cell centre's radial
    /// distance against the LOCAL radius at its axial station.
    /// </remarks>
    public sealed class ConeShape : IGuideShape, IThresholdVoxelCounter
    {
        private const double MinRadius = 0.05;
        private const double MinHeight = 0.05;
        private const long MaxScanCells = 4_000_000;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a fresh cone from the base clicks; the born height is one diameter
        /// until the third click sets the tip.</summary>
        public ConeShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, bool inverted = false)
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
        public ConeShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
        }

        // --- frame (the cylinder recipe) ----------------------------------------------------------

        private bool TryGetBaseFrame(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n)
        {
            c = u = m = n = null; r = 0;
            if (_controlPoints.Count < 2) return false;
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            if (!ShapeGeometry.TryGetFrame(a, b, _preferredAxis, out u, out m, out double baseLen))
                return false;
            c = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            r = baseLen * 0.5;
            // Deterministic axis (0.1.23): +up regardless of base-anchor order (see CylinderShape).
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

        private void SeatHandle(Vec3d c, Vec3d n, double h) =>
            _controlPoints[2].SetPosition(c.X + n.X * h, c.Y + n.Y * h, c.Z + n.Z * h);

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            var result = new List<VoxelPosition>();
            if (!TryGetFull(out Vec3d c, out double r, out _, out _, out Vec3d n, out double h)) return result;
            if (ScanTooBig(scale, r, h)) return result;

            double cell = scale / 16.0;
            double hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);

            double ex = c.X + n.X * h, ey = c.Y + n.Y * h, ez = c.Z + n.Z * h;
            double pad = r + cell;
            double x0 = Math.Min(c.X, ex) - pad, x1 = Math.Max(c.X, ex) + pad;
            double y0 = Math.Min(c.Y, ey) - pad, y1 = Math.Max(c.Y, ey) + pad;
            double z0 = Math.Min(c.Z, ez) - pad, z1 = Math.Max(c.Z, ez) + pad;

            for (int ix = AlignDown(x0, scale); ix <= AlignDown(x1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(y0, scale); iy <= AlignDown(y1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(z0, scale); iz <= AlignDown(z1, scale); iz += scale)
                    {
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double a = px * n.X + py * n.Y + pz * n.Z;
                        if (a < lo || a > hi) continue;
                        double localR = r * (1.0 - a / h);        // shrinks linearly toward the tip
                        double rx = px - n.X * a, ry = py - n.Y * a, rz = pz - n.Z * a;
                        double rho = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                        bool keep = filled ? rho <= localR + hd : Math.Abs(rho - localR) <= hd;
                        if (keep) result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
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

        public int GetVoxelCount(int scale, bool filled = false)
            => GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter)
        {
            if (!TryGetFull(out Vec3d c, out double r, out _, out _, out Vec3d n, out double h)) return 0;
            if (ScanTooBig(scale, r, h)) return GuideShapeVoxelCounting.Exceeded(stopAfter);

            stopAfter = Math.Max(0, stopAfter);
            double cell = scale / 16.0;
            double hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);

            double ex = c.X + n.X * h, ey = c.Y + n.Y * h, ez = c.Z + n.Z * h;
            double pad = r + cell;
            double x0 = Math.Min(c.X, ex) - pad, x1 = Math.Max(c.X, ex) + pad;
            double y0 = Math.Min(c.Y, ey) - pad, y1 = Math.Max(c.Y, ey) + pad;
            double z0 = Math.Min(c.Z, ez) - pad, z1 = Math.Max(c.Z, ez) + pad;
            int count = 0;

            for (int ix = AlignDown(x0, scale); ix <= AlignDown(x1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(y0, scale); iy <= AlignDown(y1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(z0, scale); iz <= AlignDown(z1, scale); iz += scale)
                    {
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double a = px * n.X + py * n.Y + pz * n.Z;
                        if (a < lo || a > hi) continue;
                        double localR = r * (1.0 - a / h);
                        double rx = px - n.X * a, ry = py - n.Y * a, rz = pz - n.Z * a;
                        double rho = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                        bool keep = filled ? rho <= localR + hd : Math.Abs(rho - localR) <= hd;
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
            double span = 2.0 * r + Math.Abs(h);
            long cellsPerAxis = (long)(span * 16.0 / scale) + 3;
            return cellsPerAxis * cellsPerAxis * cellsPerAxis > MaxScanCells;
        }

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        // --- IGuideShape: curve queries (targeting wireframe) ----------------------------------------

        // Base loop from A → slant A→tip → slant tip→B → base quarter B→P(+m̂) → slant P→tip.
        private List<Vec3d> Wireframe(int samplesPerLoop)
        {
            var pts = new List<Vec3d>();
            if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n, out double h))
                return pts;
            int nn = Math.Max(24, samplesPerLoop);

            Vec3d Base(double ang) => new Vec3d(
                c.X + (u.X * Math.Cos(ang) + m.X * Math.Sin(ang)) * r,
                c.Y + (u.Y * Math.Cos(ang) + m.Y * Math.Sin(ang)) * r,
                c.Z + (u.Z * Math.Cos(ang) + m.Z * Math.Sin(ang)) * r);
            var tip = new Vec3d(c.X + n.X * h, c.Y + n.Y * h, c.Z + n.Z * h);

            for (int i = 0; i <= nn; i++) pts.Add(Base(Math.PI + 2.0 * Math.PI * i / nn));   // base, from A
            pts.Add(tip);                                                                    // slant A→tip
            pts.Add(Base(0));                                                                // slant tip→B
            int q = Math.Max(6, nn / 4);
            for (int i = 0; i <= q; i++) pts.Add(Base(0.5 * Math.PI * i / q));               // B→P
            pts.Add(tip);                                                                    // slant P→tip
            return pts;
        }

        public List<Vec3d> SampleCurve(int samples) => Wireframe(Math.Max(24, samples / 2));

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
                // The tip slides along the axis (signed — dragging through the base flips the cone).
                if (!TryGetBaseFrame(out Vec3d c0, out _, out _, out _, out Vec3d n0)) return;
                double h = (newPosition.X - c0.X) * n0.X + (newPosition.Y - c0.Y) * n0.Y
                    + (newPosition.Z - c0.Z) * n0.Z;
                if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
                SeatHandle(c0, n0, h);
                return;
            }

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
