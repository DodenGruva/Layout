using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// A regular polygon extruded along an axis, either straight (three clicks) or tapered (four clicks).
    /// The base gesture is identical to <see cref="PolygonShape"/>: the first click is a vertex and the
    /// second is the opposite vertex (even N) or opposite edge midpoint (odd N).
    /// </summary>
    public sealed class PolygonalPrismShape : IGuideShape, ICancellableThresholdVoxelCounter,
        ICancellableVoxelGenerator, IProgressiveVoxelShape, IIntrinsicGuideExtent
    {
        private const double MinRadius = 0.05;
        private const double MinHeight = 0.05;
        private const double MaxTopRatio = 4.0;

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;
        private readonly int _sides;
        private readonly bool _tapered;
        private readonly bool _flatSideAligned;
        private readonly double _apothemRatio;
        private readonly double[] _edgeNormalX;
        private readonly double[] _edgeNormalY;

        public List<ControlPoint> ControlPoints => _controlPoints;
        public ShapeConstraint Constraint => ShapeConstraint.None;

        public PolygonalPrismShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, int sides,
            bool tapered, bool inverted = false, bool flatSideAligned = false)
        {
            _preferredAxis = planeAxis;
            _sides = PolygonShape.ClampSides(sides);
            _tapered = tapered;
            _flatSideAligned = flatSideAligned;
            _apothemRatio = Math.Cos(Math.PI / _sides);
            BuildEdgeNormals(_sides, _flatSideAligned,
                out _edgeNormalX, out _edgeNormalY);
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true),
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true)
            };
            if (tapered)
                _controlPoints.Add(new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true));

            if (TryGetBaseFrame(out Vec3d c, out double r, out Vec3d u, out _, out Vec3d n))
            {
                double h = Math.Max(MinHeight, 2.0 * r) * (inverted ? -1.0 : 1.0);
                SeatHandles(c, r, u, n, h, tapered ? r * TaperedCylinderShape.DefaultTopRatio : r);
            }
        }

        public PolygonalPrismShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis, int sides,
            bool tapered, bool flatSideAligned = false)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
            _sides = PolygonShape.ClampSides(sides);
            _tapered = tapered;
            _flatSideAligned = flatSideAligned;
            _apothemRatio = Math.Cos(Math.PI / _sides);
            BuildEdgeNormals(_sides, _flatSideAligned,
                out _edgeNormalX, out _edgeNormalY);
        }

        private static bool TryBaseFrame(List<ControlPoint> points, PlaneAxis axis, int sides,
            bool flatSideAligned,
            out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n)
        {
            c = u = m = n = null; r = 0;
            if (points == null || points.Count < 2) return false;
            Vec3d a = points[0].WorldPosition, b = points[1].WorldPosition;
            if (!ShapeGeometry.TryGetFrame(a, b, axis, out u, out m, out double span)) return false;

            int count = PolygonShape.ClampSides(sides);
            double apothemRatio = Math.Cos(Math.PI / count);
            double near = flatSideAligned ? apothemRatio : 1.0;
            double far = flatSideAligned
                ? (count % 2 == 0 ? apothemRatio : 1.0)
                : (count % 2 == 0 ? 1.0 : apothemRatio);
            r = span / (near + far);
            c = new Vec3d(a.X + u.X * r * near, a.Y + u.Y * r * near,
                a.Z + u.Z * r * near);
            n = ShapeGeometry.BaseNormal(u, axis);
            return n != null && r >= MinRadius;
        }

        private bool TryGetBaseFrame(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n) =>
            TryBaseFrame(_controlPoints, _preferredAxis, _sides, _flatSideAligned,
                out c, out r, out u, out m, out n);

        private bool TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n,
            out double h, out double rTop)
        {
            h = 0; rTop = 0;
            if (!TryGetBaseFrame(out c, out r, out u, out m, out n) || _controlPoints.Count < 3)
                return false;

            Vec3d height = _controlPoints[2].WorldPosition;
            h = ShapeGeometry.Dot(new Vec3d(height.X - c.X, height.Y - c.Y, height.Z - c.Z), n);
            if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;

            rTop = _tapered && _controlPoints.Count >= 4
                ? RadialDistance(_controlPoints[3].WorldPosition, c, n)
                : r;
            rTop = Math.Max(0, Math.Min(r * MaxTopRatio, rTop));
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

        private static double RadialDistance(Vec3d p, Vec3d c, Vec3d n)
        {
            double px = p.X - c.X, py = p.Y - c.Y, pz = p.Z - c.Z;
            double axial = px * n.X + py * n.Y + pz * n.Z;
            double rx = px - n.X * axial, ry = py - n.Y * axial, rz = pz - n.Z * axial;
            return Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }

        private void SeatHandles(Vec3d c, double r, Vec3d u, Vec3d n, double h, double rTop)
        {
            double lx = c.X + n.X * h, ly = c.Y + n.Y * h, lz = c.Z + n.Z * h;
            if (_controlPoints.Count > 2) _controlPoints[2].SetPosition(lx, ly, lz);
            // The -u side is always a true polygon vertex, including odd side counts.
            if (_tapered && _controlPoints.Count > 3)
                _controlPoints[3].SetPosition(lx - u.X * rTop, ly - u.Y * rTop, lz - u.Z * rTop);
        }

        private Vec3d[] Vertices(Vec3d c, double r, Vec3d u, Vec3d m, Vec3d n, double axial, int sides)
        {
            var result = new Vec3d[sides];
            for (int k = 0; k < sides; k++)
            {
                double angle = Math.PI + (_flatSideAligned ? Math.PI / sides : 0.0)
                    + 2.0 * Math.PI * k / sides;
                double x = Math.Cos(angle) * r, y = Math.Sin(angle) * r;
                result[k] = new Vec3d(c.X + n.X * axial + u.X * x + m.X * y,
                    c.Y + n.Y * axial + u.Y * x + m.Y * y,
                    c.Z + n.Z * axial + u.Z * x + m.Z * y);
            }
            return result;
        }

        private static void BuildEdgeNormals(
            int sides, bool flatSideAligned, out double[] normalX, out double[] normalY)
        {
            normalX = new double[sides];
            normalY = new double[sides];
            for (int k = 0; k < sides; k++)
            {
                double angle = Math.PI
                    + (2.0 * k + (flatSideAligned ? 2.0 : 1.0)) * Math.PI / sides;
                normalX[k] = Math.Cos(angle);
                normalY[k] = Math.Sin(angle);
            }
        }

        // Exact inside-distance to a regular polygon's nearest supporting edge. The circular annulus checks
        // are conservative: they reject only points whose support can provably never reach the wall. This is
        // especially important for tapered prisms, whose AABB is sized from the widest end even when most
        // axial slices are much narrower.
        private bool IsOnPolygonBoundary(double x, double y, double radius, double halfDiagonal)
        {
            double radiusSquared = x * x + y * y;
            double apothem = radius * _apothemRatio;
            double innerRadius = apothem - halfDiagonal;
            if (innerRadius > 0 && radiusSquared < innerRadius * innerRadius)
                return false;

            double outerRadius = radius + halfDiagonal / _apothemRatio;
            if (radiusSquared > outerRadius * outerRadius)
                return false;

            double support = double.MinValue;
            for (int k = 0; k < _sides; k++)
                support = Math.Max(support,
                    x * _edgeNormalX[k] + y * _edgeNormalY[k]);
            return Math.Abs(support - apothem) <= halfDiagonal;
        }

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
            => GetVoxelPositions(scale, filled, null);

        public List<VoxelPosition> GetVoxelPositions(
            int scale, bool filled, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            var result = new List<VoxelPosition>();
            if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n,
                out double h, out double rTop)) return result;

            double cell = scale / 16.0, hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);
            GetAabb(c, n, r, rTop, h, cell, out double x0, out double y0, out double z0,
                out double x1, out double y1, out double z1);

            int work = 0;
            for (int ix = AlignDown(x0, scale); ix <= AlignDown(x1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(y0, scale); iy <= AlignDown(y1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(z0, scale); iz <= AlignDown(z1, scale); iz += scale)
                    {
                        VoxelScanCancellation.Checkpoint(ref work, cancellationRequested);
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double axial = px * n.X + py * n.Y + pz * n.Z;
                        if (axial < lo || axial > hi) continue;
                        double localR = r + (rTop - r) * (axial / h);
                        double rx = px - n.X * axial, ry = py - n.Y * axial, rz = pz - n.Z * axial;
                        double localX = rx * u.X + ry * u.Y + rz * u.Z;
                        double localY = rx * m.X + ry * m.Y + rz * m.Z;
                        if (IsOnPolygonBoundary(localX, localY, localR, hd))
                            result.Add(new VoxelPosition(ix, iy, iz, VoxelRenderType.Normal));
                    }
                }
            }

            ClaimMarkers(result, scale, cancellationRequested);
            return result;
        }

        public List<VoxelPosition> GetVoxelPositionsProgressively(
            int scale, bool filled, int targetVoxelsPerChunk,
            CancellationToken cancellationToken, Action<List<VoxelPosition>> emitChunk)
        {
            var collector = new ProgressiveVoxelCollector(
                targetVoxelsPerChunk, cancellationToken, emitChunk);
            if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n,
                out double h, out double rTop)) return collector.Result;

            double cell = scale / 16.0, hd = cell * 0.866;
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
                            double localX = rx * u.X + ry * u.Y + rz * u.Z;
                            double localY = rx * m.X + ry * m.Y + rz * m.Z;
                            if (IsOnPolygonBoundary(localX, localY, localR, hd))
                                collector.Add(new VoxelPosition(
                                    ix, iy, iz, VoxelRenderType.Normal));
                        }
                    }
                }
            }

            collector.Flush();
            ClaimMarkers(collector.Result, scale);
            return collector.Result;
        }

        public int GetVoxelCount(int scale, bool filled = false) =>
            GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter)
            => GetVoxelCountUpTo(scale, filled, stopAfter, null);

        public int GetVoxelCountUpTo(
            int scale, bool filled, int stopAfter, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n,
                out double h, out double rTop)) return 0;

            stopAfter = Math.Max(0, stopAfter);
            int count = 0;
            int work = 0;
            double cell = scale / 16.0, hd = cell * 0.866;
            double lo = Math.Min(0, h), hi = Math.Max(0, h);
            GetAabb(c, n, r, rTop, h, cell, out double x0, out double y0, out double z0,
                out double x1, out double y1, out double z1);

            for (int ix = AlignDown(x0, scale); ix <= AlignDown(x1, scale); ix += scale)
            {
                double px = ix / 16.0 + cell * 0.5 - c.X;
                for (int iy = AlignDown(y0, scale); iy <= AlignDown(y1, scale); iy += scale)
                {
                    double py = iy / 16.0 + cell * 0.5 - c.Y;
                    for (int iz = AlignDown(z0, scale); iz <= AlignDown(z1, scale); iz += scale)
                    {
                        VoxelScanCancellation.Checkpoint(ref work, cancellationRequested);
                        double pz = iz / 16.0 + cell * 0.5 - c.Z;
                        double axial = px * n.X + py * n.Y + pz * n.Z;
                        if (axial < lo || axial > hi) continue;
                        double localR = r + (rTop - r) * (axial / h);
                        double rx = px - n.X * axial, ry = py - n.Y * axial, rz = pz - n.Z * axial;
                        double localX = rx * u.X + ry * u.Y + rz * u.Z;
                        double localY = rx * m.X + ry * m.Y + rz * m.Z;
                        if (!IsOnPolygonBoundary(localX, localY, localR, hd)) continue;
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

        private static int AlignDown(double world, int scale) =>
            (int)Math.Floor(world * 16.0 / scale) * scale;

        private void ClaimMarkers(
            List<VoxelPosition> cells, int scale, Func<bool> cancellationRequested = null)
        {
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                ControlPoint point = _controlPoints[i];
                VoxelRenderType type = point.IsLocked ? VoxelRenderType.Locked
                    : point.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(
                    cells, scale, point.WorldPosition, type, cancellationRequested);
            }
        }

        private List<Vec3d> Wireframe()
        {
            var points = new List<Vec3d>();
            if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out Vec3d m, out Vec3d n,
                out double h, out double rTop)) return points;

            Vec3d[] bottom = Vertices(c, r, u, m, n, 0, _sides);
            Vec3d[] top = Vertices(c, rTop, u, m, n, h, _sides);

            // Close the base, then walk the top perimeter while visiting every matching corner pair.
            // Consecutive points always share a real prism edge. Some verticals are retraced to keep this
            // as one safe polyline, but voxel marching deduplicates them. The visible result is exactly one
            // longitudinal wire per polygon corner: a six-sided prism has six, an octagon has eight, etc.
            for (int i = 0; i <= _sides; i++) points.Add(bottom[i % _sides]);
            for (int i = 0; i < _sides; i++)
            {
                int next = (i + 1) % _sides;
                points.Add(top[i]);
                points.Add(top[next]);
                points.Add(bottom[next]);
            }
            return points;
        }

        public List<Vec3d> SampleCurve(int samples) => Wireframe();

        public float GetNearestT(Vec3d worldPos)
        {
            List<Vec3d> wire = Wireframe();
            if (wire.Count < 2) return 0;
            int best = 0; double distance = double.MaxValue;
            for (int i = 0; i < wire.Count; i++)
            {
                double d = ShapeGeometry.Dist(wire[i], worldPos);
                if (d < distance) { distance = d; best = i; }
            }
            return (float)best / (wire.Count - 1);
        }

        public Vec3d GetPointAt(float t)
        {
            List<Vec3d> wire = Wireframe();
            if (wire.Count == 0) return _controlPoints.Count > 0
                ? new Vec3d(_controlPoints[0].WorldPosition.X, _controlPoints[0].WorldPosition.Y,
                    _controlPoints[0].WorldPosition.Z) : new Vec3d();
            int index = (int)Math.Round(Math.Max(0, Math.Min(1, t)) * (wire.Count - 1));
            Vec3d p = wire[index];
            return new Vec3d(p.X, p.Y, p.Z);
        }

        public int GetNearestControlPointIndex(Vec3d worldPos)
        {
            int best = -1; double distance = double.MaxValue;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                double d = ShapeGeometry.Dist(_controlPoints[i].WorldPosition, worldPos);
                if (d < distance) { distance = d; best = i; }
            }
            return best;
        }

        public void InsertControlPoint(float t, Vec3d position) { }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;

            if (_tapered && index == 3)
            {
                if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out _, out Vec3d n,
                    out double h, out _)) return;
                SeatHandles(c, r, u, n, h, Math.Min(r * MaxTopRatio, RadialDistance(newPosition, c, n)));
                return;
            }

            if (index == 2)
            {
                if (!TryGetFull(out Vec3d c, out double r, out Vec3d u, out _, out Vec3d n,
                    out _, out double rTop)) return;
                double h = ShapeGeometry.Dot(new Vec3d(newPosition.X - c.X, newPosition.Y - c.Y,
                    newPosition.Z - c.Z), n);
                if (Math.Abs(h) < MinHeight) h = h < 0 ? -MinHeight : MinHeight;
                SeatHandles(c, r, u, n, h, rTop);
                return;
            }

            double oldHeight = 0, ratio = _tapered ? TaperedCylinderShape.DefaultTopRatio : 1.0;
            if (TryGetFull(out _, out double oldRadius, out _, out _, out _, out oldHeight, out double oldTop)
                && oldRadius > MinRadius) ratio = oldTop / oldRadius;
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            if (TryGetBaseFrame(out Vec3d c1, out double r1, out Vec3d u1, out _, out Vec3d n1))
                SeatHandles(c1, r1, u1, n1,
                    Math.Abs(oldHeight) < MinHeight ? 2.0 * r1 : oldHeight, r1 * ratio);
        }

        /// <summary>Preserves height and taper ratio when an edit changes odd/even side geometry.</summary>
        public static void ReseatForSideChange(List<ControlPoint> points, PlaneAxis axis,
            int oldSides, int newSides, bool tapered, bool flatSideAligned = false)
        {
            if (!TryBaseFrame(points, axis, oldSides, flatSideAligned, out Vec3d oldC, out double oldR,
                out _, out _, out Vec3d oldN) || points.Count < 3) return;
            Vec3d height = points[2].WorldPosition;
            double h = ShapeGeometry.Dot(new Vec3d(height.X - oldC.X, height.Y - oldC.Y,
                height.Z - oldC.Z), oldN);
            double ratio = 1.0;
            if (tapered && points.Count >= 4 && oldR > MinRadius)
                ratio = RadialDistance(points[3].WorldPosition, oldC, oldN) / oldR;

            if (!TryBaseFrame(points, axis, newSides, flatSideAligned, out Vec3d c, out double r,
                out Vec3d u, out _, out Vec3d n)) return;
            double lx = c.X + n.X * h, ly = c.Y + n.Y * h, lz = c.Z + n.Z * h;
            points[2].SetPosition(lx, ly, lz);
            if (tapered && points.Count >= 4)
                points[3].SetPosition(lx - u.X * r * ratio, ly - u.Y * r * ratio, lz - u.Z * r * ratio);
        }

        public void RecalculatePhantomPoints() { }
        public bool WouldBreakOnMove(int index) => false;
        public bool BreakConstraint() => false;
    }
}
