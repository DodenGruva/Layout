using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The tapered cylinder / frustum (0.2.24): FOUR clicks — the base diameter (two anchors, the circle
    /// gesture), a height click projected onto the base's axis (Primary — the lid handle), then a TOP
    /// RADIUS click whose distance from the axis sets how wide the lid is (Primary — the rim handle).
    /// The windmill/tower shape: a cylinder that narrows (or flares) toward the top. Top radius 0 degenerates
    /// to a cone, top radius = base radius to a plain cylinder. Always a hollow shell (every 3D volume is).
    /// </summary>
    /// <remarks>
    /// Geometrically this is the cylinder and the cone generalised: identical centre-banded voxelisation,
    /// scan guard, and lid-handle mechanics (see <see cref="CylinderShape"/> for the reasoning), with the
    /// band compared against the LOCAL radius, which interpolates linearly from the base radius at a=0 to
    /// the top radius at a=h — exactly the cone's rule with a non-zero endpoint.
    ///
    /// Unlike the older cylinder-family implementation, this shape deliberately has no fixed scan-box
    /// cutoff. A frustum can have a large mostly-empty box while its hollow shell remains within the
    /// server's configured voxel budget; the shared threshold counter and GuideManager's absolute rendered-
    /// voxel ceiling are the authoritative limits. A fixed scan cutoff made raised and unlimited server
    /// caps ineffective, especially while widening the base or top rim.
    ///
    /// The rim handle stores only a DISTANCE: it is always re-seated on the +û side of the top ring, so
    /// dragging it anywhere resolves to "how far from the axis", never "which way round". Moving a BASE
    /// anchor preserves the taper RATIO (top/base), not the absolute top radius — resizing the base of a
    /// windmill tower should scale the whole silhouette, not just its foot. (Decide-and-flag.)
    /// </remarks>
    public sealed class TaperedCylinderShape : IGuideShape, ICancellableThresholdVoxelCounter,
        ICancellableVoxelGenerator, IProgressiveVoxelShape, IIntrinsicGuideExtent
    {
        private const double MinRadius = 0.05;
        private const double MinHeight = 0.05;

        /// <summary>The born taper before the fourth click lands: a lid 60% of the base's width.</summary>
        public const double DefaultTopRatio = 0.6;

        /// <summary>The rim handle's reach, as a multiple of the base radius — a flare stop, not a wall.</summary>
        private const double MaxTopRatio = 4.0;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a fresh tapered cylinder from the base clicks; the born height is one diameter
        /// and the born lid <see cref="DefaultTopRatio"/> of the base, until clicks 3 and 4 set them.</summary>
        public TaperedCylinderShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, bool inverted = false)
        {
            _preferredAxis = planeAxis;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true),
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true),   // lid handle (height)
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true)    // rim handle (top radius)
            };
            if (TryGetBaseFrame(out Vec3d c, out double r, out Vec3d u, out _, out Vec3d n0))
            {
                double h = Math.Max(MinHeight, 2.0 * r) * (inverted ? -1.0 : 1.0);
                SeatHandles(c, u, n0, h, r * DefaultTopRatio);
            }
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public TaperedCylinderShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
        }

        // --- frame (the cylinder recipe, plus the top radius) ---------------------------------------

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

        private bool TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n,
            out double h, out double rTop)
        {
            h = 0; rTop = 0;
            if (!TryGetBaseFrame(out c, out r, out u, out m, out n) || _controlPoints.Count < 3) return false;
            Vec3d p = _controlPoints[2].WorldPosition;
            h = (p.X - c.X) * n.X + (p.Y - c.Y) * n.Y + (p.Z - c.Z) * n.Z;
            if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
            rTop = _controlPoints.Count >= 4
                ? RadialDistance(_controlPoints[3].WorldPosition, c, n)
                : r * DefaultTopRatio;
            if (rTop > r * MaxTopRatio) rTop = r * MaxTopRatio;
            if (rTop < 0) rTop = 0;
            return true;
        }

        public bool TryGetIntrinsicDimensions(out double width, out double height)
        {
            width = height = 0;
            if (!TryGetFull(out _, out double baseRadius, out _, out _, out _,
                out double axialHeight, out double topRadius))
                return false;
            width = Math.Max(baseRadius, topRadius) * 2.0;
            height = Math.Abs(axialHeight);
            return true;
        }

        // How far a world point sits from the axis (its height along the axis is irrelevant — the rim
        // handle carries a distance, and the shape re-seats it at the lid).
        private static double RadialDistance(Vec3d p, Vec3d c, Vec3d n)
        {
            double px = p.X - c.X, py = p.Y - c.Y, pz = p.Z - c.Z;
            double a = px * n.X + py * n.Y + pz * n.Z;
            double rx = px - n.X * a, ry = py - n.Y * a, rz = pz - n.Z * a;
            return Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }

        // Re-seats the lid handle on the axis at signed height h, and the rim handle on the +û side of
        // that lid at radius rTop (after any move).
        private void SeatHandles(Vec3d c, Vec3d u, Vec3d n, double h, double rTop)
        {
            double lx = c.X + n.X * h, ly = c.Y + n.Y * h, lz = c.Z + n.Z * h;
            if (_controlPoints.Count > 2) _controlPoints[2].SetPosition(lx, ly, lz);
            if (_controlPoints.Count > 3)
                _controlPoints[3].SetPosition(lx + u.X * rTop, ly + u.Y * rTop, lz + u.Z * rTop);
        }

        // --- IGuideShape: voxels ---------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
            => GetVoxelPositions(scale, filled, null);

        public List<VoxelPosition> GetVoxelPositions(
            int scale, bool filled, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            filled = false;   // 0.2.17: 3D volumes are always hollow shells (see GuideShapeTypes.IsVolume)
            var result = new List<VoxelPosition>();
            if (!TryGetFull(out Vec3d c, out double r, out _, out _, out Vec3d n, out double h, out double rTop))
                return result;

            double cell = scale / 16.0;
            double hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);

            GetAabb(c, n, r, rTop, h, cell, out double ax0, out double ay0, out double az0,
                out double ax1, out double ay1, out double az1);

            int work = 0;
            for (int ix = AlignDown(ax0, scale); ix <= AlignDown(ax1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(ay0, scale); iy <= AlignDown(ay1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(az0, scale); iz <= AlignDown(az1, scale); iz += scale)
                    {
                        VoxelScanCancellation.Checkpoint(ref work, cancellationRequested);
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double a = px * n.X + py * n.Y + pz * n.Z;
                        if (a < lo || a > hi) continue;
                        double localR = r + (rTop - r) * (a / h);   // base radius → top radius, linearly
                        double rx = px - n.X * a, ry = py - n.Y * a, rz = pz - n.Z * a;
                        double rho = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                        bool keep = filled ? rho <= localR + hd : Math.Abs(rho - localR) <= hd;
                        if (keep) result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
                }
            }

            ClaimHandleMarkers(result, scale, cancellationRequested);
            return result;
        }

        public List<VoxelPosition> GetVoxelPositionsProgressively(
            int scale, bool filled, int targetVoxelsPerChunk,
            CancellationToken cancellationToken, Action<List<VoxelPosition>> emitChunk)
        {
            var collector = new ProgressiveVoxelCollector(
                targetVoxelsPerChunk, cancellationToken, emitChunk);
            if (!TryGetFull(out Vec3d c, out double r, out _, out _, out Vec3d n,
                out double h, out double rTop)) return collector.Result;

            double cell = scale / 16.0;
            double hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);
            GetAabb(c, n, r, rTop, h, cell,
                out double x0, out double y0, out double z0,
                out double x1, out double y1, out double z1);
            int minX = AlignDown(x0, scale), maxX = AlignDown(x1, scale);
            int minY = AlignDown(y0, scale), maxY = AlignDown(y1, scale);
            int minZ = AlignDown(z0, scale), maxZ = AlignDown(z1, scale);
            ProgressiveVoxelTile[] tiles = ProgressiveVoxelOrder.ShuffledSpatialTiles(
                (maxX - minX) / scale + 1,
                (maxY - minY) / scale + 1,
                (maxZ - minZ) / scale + 1);
            for (int visit = 0; visit < tiles.Length; visit++)
            {
                collector.ThrowIfCancellationRequested();
                ProgressiveVoxelTile tile = tiles[visit];
                for (int xi = tile.X0; xi < tile.X1; xi++)
                {
                    int ix = minX + xi * scale;
                    double px = ix / 16.0 + cell * 0.5 - c.X;
                    for (int yi = tile.Y0; yi < tile.Y1; yi++)
                    {
                        int iy = minY + yi * scale;
                        double py = iy / 16.0 + cell * 0.5 - c.Y;
                        for (int zi = tile.Z0; zi < tile.Z1; zi++)
                        {
                            int iz = minZ + zi * scale;
                            double pz = iz / 16.0 + cell * 0.5 - c.Z;
                            double axial = px * n.X + py * n.Y + pz * n.Z;
                            if (axial < lo || axial > hi) continue;
                            double localR = r + (rTop - r) * (axial / h);
                            double rx = px - n.X * axial;
                            double ry = py - n.Y * axial;
                            double rz = pz - n.Z * axial;
                            double rho = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                            if (Math.Abs(rho - localR) <= hd)
                                collector.Add(new VoxelPosition(
                                    ix, iy, iz, VoxelRenderType.Normal));
                        }
                    }
                }
            }

            collector.Flush();
            ClaimHandleMarkers(collector.Result, scale);
            return collector.Result;
        }

        public int GetVoxelCount(int scale, bool filled = false)
            => GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter)
            => GetVoxelCountUpTo(scale, filled, stopAfter, null);

        public int GetVoxelCountUpTo(
            int scale, bool filled, int stopAfter, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            filled = false;   // 0.2.17: 3D volumes are always hollow shells (see GuideShapeTypes.IsVolume)
            if (!TryGetFull(out Vec3d c, out double r, out _, out _, out Vec3d n, out double h, out double rTop))
                return 0;

            stopAfter = Math.Max(0, stopAfter);
            double cell = scale / 16.0;
            double hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);
            int count = 0;
            int work = 0;

            GetAabb(c, n, r, rTop, h, cell, out double ax0, out double ay0, out double az0,
                out double ax1, out double ay1, out double az1);

            for (int ix = AlignDown(ax0, scale); ix <= AlignDown(ax1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(ay0, scale); iy <= AlignDown(ay1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(az0, scale); iz <= AlignDown(az1, scale); iz += scale)
                    {
                        VoxelScanCancellation.Checkpoint(ref work, cancellationRequested);
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double a = px * n.X + py * n.Y + pz * n.Z;
                        if (a < lo || a > hi) continue;
                        double localR = r + (rTop - r) * (a / h);
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

        private static void GetAabb(Vec3d c, Vec3d n, double r, double rTop, double h, double cell,
            out double x0, out double y0, out double z0, out double x1, out double y1, out double z1)
        {
            double ex = c.X + n.X * h, ey = c.Y + n.Y * h, ez = c.Z + n.Z * h;
            double pad = Math.Max(r, rTop) + cell;
            x0 = Math.Min(c.X, ex) - pad; x1 = Math.Max(c.X, ex) + pad;
            y0 = Math.Min(c.Y, ey) - pad; y1 = Math.Max(c.Y, ey) + pad;
            z0 = Math.Min(c.Z, ez) - pad; z1 = Math.Max(c.Z, ez) + pad;
        }

        private void ClaimHandleMarkers(
            List<VoxelPosition> cells, int scale, Func<bool> cancellationRequested = null)
        {
            for (int i = 0; i < 4 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                VoxelRenderType t = cp.IsLocked ? VoxelRenderType.Locked
                    : cp.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(
                    cells, scale, cp.WorldPosition, t, cancellationRequested);
            }
        }

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        // --- IGuideShape: curve queries (targeting wireframe) ----------------------------------------

        // Base loop from A → slant A→A′ → top loop from A′ → top half back to B′ → slant B′→B.
        private List<Vec3d> Wireframe(int samplesPerLoop)
        {
            var pts = new List<Vec3d>();
            if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n,
                out double h, out double rTop)) return pts;
            int nn = Math.Max(24, samplesPerLoop);

            Vec3d Ring(double axial, double rad, double ang) => new Vec3d(
                c.X + n.X * axial + (u.X * Math.Cos(ang) + m.X * Math.Sin(ang)) * rad,
                c.Y + n.Y * axial + (u.Y * Math.Cos(ang) + m.Y * Math.Sin(ang)) * rad,
                c.Z + n.Z * axial + (u.Z * Math.Cos(ang) + m.Z * Math.Sin(ang)) * rad);

            for (int i = 0; i <= nn; i++) pts.Add(Ring(0, r, Math.PI + 2.0 * Math.PI * i / nn));

            // Eight evenly-spaced longitudinal wires preserve the frustum read at very low cost.
            const int ribs = 8;
            int arcSamples = Math.Max(3, nn / ribs);
            for (int rib = 0; rib < ribs; rib++)
            {
                double a = Math.PI + 2.0 * Math.PI * rib / ribs;
                double b = Math.PI + 2.0 * Math.PI * (rib + 1) / ribs;
                pts.Add(Ring(h, rTop, a));
                for (int j = 1; j <= arcSamples; j++)
                    pts.Add(Ring(h, rTop, a + (b - a) * j / arcSamples));
                pts.Add(Ring(0, r, b));
            }
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

            if (index == 3)
            {
                // The rim handle carries the TOP RADIUS only: the drag's distance from the axis, re-seated
                // on the lid. Dragging it to the axis makes a cone; out to the base's width, a cylinder.
                if (!TryGetFull(out Vec3d c3, out double r3, out Vec3d u3, out _, out Vec3d n3,
                    out double h3, out _)) return;
                double rTop = RadialDistance(newPosition, c3, n3);
                if (rTop > r3 * MaxTopRatio) rTop = r3 * MaxTopRatio;
                SeatHandles(c3, u3, n3, h3, rTop);
                return;
            }

            if (index == 2)
            {
                // The lid handle slides along the AXIS (signed — dragging through the base flips the
                // taper end-over-end); the rim rides along with it, keeping its radius.
                if (!TryGetFull(out Vec3d c2, out _, out Vec3d u2, out _, out Vec3d n2, out _, out double rTop2))
                    return;
                double h = (newPosition.X - c2.X) * n2.X + (newPosition.Y - c2.Y) * n2.Y
                    + (newPosition.Z - c2.Z) * n2.Z;
                if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
                SeatHandles(c2, u2, n2, h, rTop2);
                return;
            }

            // A base move absorbs as recentre/resize: the lid keeps its signed height on the new axis and
            // the lid keeps its taper RATIO, so the whole silhouette scales with the base.
            double ratio = DefaultTopRatio, hOld = 0;
            if (TryGetFull(out _, out double rOld, out _, out _, out _, out hOld, out double rTopOld)
                && rOld > MinRadius)
                ratio = rTopOld / rOld;
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            if (TryGetBaseFrame(out Vec3d c1, out double r1, out Vec3d u1, out _, out Vec3d n1))
                SeatHandles(c1, u1, n1, Math.Abs(hOld) < MinHeight ? 2.0 * r1 : hOld, r1 * ratio);
        }

        public void RecalculatePhantomPoints() { /* no phantoms */ }

        public bool WouldBreakOnMove(int index) => false;

        public bool BreakConstraint() => false;
    }
}
