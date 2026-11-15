using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Systems;

namespace Layout.Shapes
{
    /// <summary>
    /// The closed planar primitive (Session 8): an ellipse defined by THREE real control points —
    /// two major-axis anchors A and B (the two draft clicks: a diameter, matching the arch's two-click
    /// gesture) and a minor-axis handle M (Primary/green). No phantoms. A CIRCLE is this shape under
    /// <see cref="ShapeConstraint.Circle"/>: M is derived (minor radius == major radius) and dragging it
    /// is the absorb-or-break trigger that demotes circle → ellipse (losslessly).
    /// </summary>
    /// <remarks>
    /// GEOMETRY. centre = (A+B)/2; u = (B−A)/2 (major half-axis). The plane is derived every query, not
    /// stored: the plane containing u whose normal is as close as possible to the preferred axis
    /// (<see cref="GuideData.ShapePlaneAxis"/>, captured from the first click's block face) — i.e.
    /// n = normalise(axis − û(axis·û)), minorDir m̂ = normalise(n × û). This stays well-defined even when
    /// the anchors are dragged arbitrarily in 3D; if u becomes parallel to the preferred axis, a fallback
    /// axis steps in. The minor handle M is kept ON the minor axis by <see cref="MoveControlPoint"/>
    /// (a drag of M slides it along that axis — the natural resize feel), so rMinor = |M − centre|.
    ///
    /// CONTROL-POINT SEMANTICS. Body grabs on this family never insert (an arbitrary interpolation point
    /// has no meaning on a parametric ellipse); the controller maps body hits to the nearest handle
    /// instead. Consequently <see cref="InsertControlPoint"/> is a defensive no-op.
    ///
    /// FILL. Hollow = the perimeter ring; filled = the disc, produced by marching radial segments from the
    /// centre to a dense perimeter (segment and angular steps both ≤ half a cell, so no holes), voxelised
    /// with the exact spline cell convention via <see cref="VoxelMarch"/>.
    /// </remarks>
    public sealed class EllipseShape : IGuideShape, ICancellableThresholdVoxelCounter, ICancellableVoxelGenerator
    {
        private const double MinRadius = 0.05;          // degenerate-geometry guard, world blocks

        private readonly List<ControlPoint> _controlPoints;
        private readonly PlaneAxis _preferredAxis;
        private readonly bool _symmetricCircleRaster;
        private ShapeConstraint _constraint;

        internal OutlineStyle Outline { get; set; }

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => _constraint;

        /// <summary>Creates a fresh ellipse from the two draft clicks (a diameter).</summary>
        public EllipseShape(Vec3d a, Vec3d b, PlaneAxis planeAxis, ShapeConstraint constraint,
            bool symmetricCircleRaster = true)
        {
            _preferredAxis = planeAxis;
            _symmetricCircleRaster = symmetricCircleRaster;
            _constraint = constraint == ShapeConstraint.Circle ? ShapeConstraint.Circle : ShapeConstraint.None;
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true),
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isPrimary: true)   // placed properly just below
            };
            DeriveMinorHandle(initial: true);
        }

        /// <summary>Adopts an existing list (load/wire path). List is shared by reference, never copied.</summary>
        public EllipseShape(List<ControlPoint> controlPoints, PlaneAxis planeAxis, ShapeConstraint constraint,
            bool symmetricCircleRaster = false)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _preferredAxis = planeAxis;
            _symmetricCircleRaster = symmetricCircleRaster;
            _constraint = constraint == ShapeConstraint.Circle ? ShapeConstraint.Circle : ShapeConstraint.None;
        }

        // --- frame -----------------------------------------------------------------------------

        private bool TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out double rMaj, out double rMin)
        {
            c = u = m = null; rMaj = rMin = 0;
            if (_controlPoints.Count < 3) return false;
            Vec3d a = _controlPoints[0].WorldPosition, b = _controlPoints[1].WorldPosition;
            Vec3d hm = _controlPoints[2].WorldPosition;

            c = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            u = new Vec3d(b.X - c.X, b.Y - c.Y, b.Z - c.Z);
            rMaj = Len(u);
            if (rMaj < MinRadius) return false;
            Vec3d uh = Scale(u, 1.0 / rMaj);

            Vec3d n = ProjectOutAndNormalise(AxisVec(_preferredAxis), uh)
                   ?? ProjectOutAndNormalise(AxisVec(PlaneAxis.Y), uh)
                   ?? ProjectOutAndNormalise(AxisVec(PlaneAxis.X), uh);
            m = Cross(n, uh);                          // unit: n ⟂ û, both unit

            rMin = _constraint == ShapeConstraint.Circle
                ? rMaj
                : Math.Max(MinRadius, Math.Abs(Dot(Sub(hm, c), m)));
            return true;
        }

        private Vec3d PointAtTheta(Vec3d c, Vec3d u, Vec3d m, double rMin, double theta)
        {
            double ct = Math.Cos(theta), st = Math.Sin(theta);
            return new Vec3d(
                c.X + u.X * ct + m.X * rMin * st,
                c.Y + u.Y * ct + m.Y * rMin * st,
                c.Z + u.Z * ct + m.Z * rMin * st);
        }

        /// <summary>Puts M where the current frame says it belongs (circle: on the ring; ellipse: keep radius).</summary>
        private void DeriveMinorHandle(bool initial = false)
        {
            if (!TryGetFrame(out Vec3d c, out _, out Vec3d m, out double rMaj, out double rMin)) return;
            double r = _constraint == ShapeConstraint.Circle ? rMaj : (initial ? rMaj * 0.5 : rMin);
            _controlPoints[2].SetPosition(c.X + m.X * r, c.Y + m.Y * r, c.Z + m.Z * r);
        }

        // --- IGuideShape: voxels ----------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false) =>
            GetVoxelPositions(scale, filled, null);

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled, Func<bool> cancelled)
        {
            VoxelScanCancellation.ThrowIfRequested(cancelled);
            var result = new List<VoxelPosition>();
            if (scale <= 0 || !TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out double rMaj, out double rMin))
                return result;

            if (TryRaster(c, u, m, rMaj, scale, out CircleRaster raster))
            {
                if (!filled && Outline != OutlineStyle.Legacy)
                {
                    if (CircleOutsideOutlineBudget(rMaj, scale)) return result;
                    result = OutlineVoxelizer.Circle(c, rMaj, raster.Axis, scale, Outline, cancelled);
                    ClaimMarkers(result, scale, raster.Axis, cancelled);
                    return result;
                }
                int count = raster.CountUpTo(filled, GuideManager.HardVoxelCeiling, cancelled);
                if (count > GuideManager.HardVoxelCeiling) return result;
                result.Capacity = count;
                raster.Append(result, scale, interior: false, cancelled);
                ClaimMarkers(result, scale, raster.Axis, cancelled);
                if (filled) raster.Append(result, scale, interior: true, cancelled);
                VoxelScanCancellation.ThrowIfRequested(cancelled);
                return result;
            }

            var seen = new HashSet<(int, int, int)>();
            double cell = scale / VoxelGrid.UnitsPerBlock;
            double rBig = Math.Max(rMaj, rMin);
            int thetaSteps = Clamp((int)Math.Ceiling(2.0 * Math.PI * rBig / (cell * 0.5)), 64, 8192);

            var perimeter = new List<Vec3d>(thetaSteps + 1);
            for (int i = 0; i <= thetaSteps; i++)
                perimeter.Add(PointAtTheta(c, u, m, rMin, 2.0 * Math.PI * i / thetaSteps));
            if (filled || Outline == OutlineStyle.Legacy) VoxelMarch.MarchInto(result, seen, perimeter, scale);
            else OutlineVoxelizer.Path(result, seen, perimeter, scale, Outline, closed: true,
                center: c, cancelled: cancelled);

            if (filled)
            {
                // Radial fan centre→perimeter; both the angular and radial steps are ≤ half a cell.
                for (int i = 0; i < thetaSteps; i++)
                    VoxelMarch.MarchSegmentInto(result, seen, c, perimeter[i], scale, cancelled);
            }

            ClaimMarkers(result, scale, null, cancelled);
            VoxelScanCancellation.ThrowIfRequested(cancelled);
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false) =>
            GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter) =>
            GetVoxelCountUpTo(scale, filled, stopAfter, null);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter, Func<bool> cancelled)
        {
            VoxelScanCancellation.ThrowIfRequested(cancelled);
            if (scale <= 0 || !TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out double radius, out _))
                return 0;
            int count;
            if (TryRaster(c, u, m, radius, scale, out CircleRaster raster))
            {
                if (filled || Outline == OutlineStyle.Legacy)
                    return raster.CountUpTo(filled, Math.Min(stopAfter, GuideManager.HardVoxelCeiling), cancelled);
                int limit = Math.Min(stopAfter, GuideManager.HardVoxelCeiling);
                // The connected rim must cover every column of its span. This rejects
                // oversized requests before allocating the outline.
                double span = 2 * radius / (scale / VoxelGrid.UnitsPerBlock);
                if (CircleOutsideOutlineBudget(radius, scale) || span - 2 > limit)
                    return GuideShapeVoxelCounting.Exceeded(limit);
                count = OutlineVoxelizer.Circle(c, radius, raster.Axis, scale, Outline, cancelled).Count;
            }
            else count = GetVoxelPositions(scale, filled, cancelled).Count;
            return count > stopAfter ? GuideShapeVoxelCounting.Exceeded(stopAfter) : count;
        }

        private static bool CircleOutsideOutlineBudget(double radius, int scale) =>
            radius / (scale / VoxelGrid.UnitsPerBlock) > GuideManager.HardVoxelCeiling / 4.0;

        internal bool CanCountCircleByRows(int scale, int maxDiameterInCells) =>
            TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out double radius, out _)
            && 2 * radius <= maxDiameterInCells * (scale / VoxelGrid.UnitsPerBlock)
            && TryRaster(c, u, m, radius, scale, out _);

        private bool TryRaster(Vec3d c, Vec3d u, Vec3d m, double radius, int scale, out CircleRaster raster)
        {
            raster = default;
            if (!_symmetricCircleRaster || _constraint != ShapeConstraint.Circle) return false;

            Vec3d normal = Cross(u, m);
            double tolerance = radius * 1e-9;
            PlaneAxis axis;
            if (Math.Abs(normal.Y) <= tolerance && Math.Abs(normal.Z) <= tolerance) axis = PlaneAxis.X;
            else if (Math.Abs(normal.X) <= tolerance && Math.Abs(normal.Z) <= tolerance) axis = PlaneAxis.Y;
            else if (Math.Abs(normal.X) <= tolerance && Math.Abs(normal.Y) <= tolerance) axis = PlaneAxis.Z;
            else return false;

            raster = new CircleRaster(axis, c, u, scale / VoxelGrid.UnitsPerBlock);
            return true;
        }

        private void ClaimMarkers(List<VoxelPosition> cells, int scale, PlaneAxis? circleAxis, Func<bool> cancelled)
        {
            foreach (var entry in MarkerClaims(cells, scale, circleAxis, cancelled))
                cells[entry.Key] = cells[entry.Key].WithType(entry.Value.Type);
        }

        // Shares ownership with click targeting, including both cells of a paired handle.
        internal bool TryGetCircleMarkerOwner(List<VoxelPosition> cells, int scale,
            Func<VoxelPosition, bool> matches, out int owner)
        {
            owner = -1;
            if (!TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out double radius, out _)
                || !TryRaster(c, u, m, radius, scale, out CircleRaster raster)) return false;
            int bestRole = -1;
            int bestIndex = int.MaxValue;
            foreach (var entry in MarkerClaims(cells, scale, raster.Axis, null))
            {
                int role = Precedence(entry.Value.Type);
                if (entry.Value.Point >= 0 && (role > bestRole || (role == bestRole && entry.Key < bestIndex))
                    && matches(cells[entry.Key]))
                {
                    bestRole = role;
                    bestIndex = entry.Key;
                    owner = entry.Value.Point;
                }
            }
            return true;
        }

        private Dictionary<int, (int Point, VoxelRenderType Type)> MarkerClaims(
            List<VoxelPosition> cells, int scale, PlaneAxis? circleAxis, Func<bool> cancelled)
        {
            var claims = new Dictionary<int, (int Point, VoxelRenderType Type)>();
            if (cells.Count == 0) return claims;
            double half = scale * 0.5;
            int work = 0;
            var (anchorSeam, midpointSeam) = circleAxis.HasValue
                ? RimSeams(circleAxis.Value, scale) : default;
            for (int p = 0; p < _controlPoints.Count && p < 3; p++)
            {
                ControlPoint cp = _controlPoints[p];
                VoxelRenderType t = cp.IsLocked ? VoxelRenderType.Locked
                    : cp.IsPrimary ? VoxelRenderType.Primary
                    : cp.IsAnchor ? VoxelRenderType.Anchor
                    : VoxelRenderType.Normal;
                if (t == VoxelRenderType.Normal) continue;

                bool circleMidpoint = p == 2 && _constraint == ShapeConstraint.Circle;
                Vec3d w = cp.WorldPosition;
                ClaimMarker(w, p, t, cp.IsAnchor ? anchorSeam : circleMidpoint ? midpointSeam : null);
                if (circleMidpoint)
                {
                    Vec3d a = _controlPoints[0].WorldPosition;
                    Vec3d b = _controlPoints[1].WorldPosition;
                    // Reflect the minor handle through the centre for a visual marker on the opposite rim.
                    var opposite = new Vec3d(a.X + b.X - w.X, a.Y + b.Y - w.Y, a.Z + b.Z - w.Z);
                    ClaimMarker(opposite, -1, VoxelRenderType.Primary, midpointSeam);
                }
            }
            return claims;

            void ClaimMarker(Vec3d w, int point, VoxelRenderType type, (PlaneAxis Axis, long Coordinate)? seam)
            {
                int best = -1; double bestD2 = double.MaxValue;
                for (int i = 0; i < cells.Count; i++)
                {
                    VoxelScanCancellation.Checkpoint(ref work, cancelled);
                    double dx = (cells[i].X + half) / VoxelGrid.UnitsPerBlock - w.X;
                    double dy = (cells[i].Y + half) / VoxelGrid.UnitsPerBlock - w.Y;
                    double dz = (cells[i].Z + half) / VoxelGrid.UnitsPerBlock - w.Z;
                    double d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < bestD2) { bestD2 = d2; best = i; }
                }
                if (best < 0) return;
                Claim(best, point, type);
                if (!seam.HasValue) return;

                VoxelPosition nearest = cells[best];
                (PlaneAxis axis, long seamCoordinate) = seam.Value;
                int transverse = axis == PlaneAxis.X ? nearest.X : axis == PlaneAxis.Y ? nearest.Y : nearest.Z;
                if (transverse != seamCoordinate && transverse != seamCoordinate - scale) return;
                int partner = (int)(2 * seamCoordinate - transverse - scale);
                int x = axis == PlaneAxis.X ? partner : nearest.X;
                int y = axis == PlaneAxis.Y ? partner : nearest.Y;
                int z = axis == PlaneAxis.Z ? partner : nearest.Z;
                for (int i = 0; i < cells.Count; i++)
                {
                    VoxelScanCancellation.Checkpoint(ref work, cancelled);
                    if (cells[i].X != x || cells[i].Y != y || cells[i].Z != z) continue;
                    Claim(i, point, type);
                    break;
                }
            }

            void Claim(int index, int point, VoxelRenderType type)
            {
                if (!claims.TryGetValue(index, out var current)
                    || (point >= 0 && Precedence(type) > Precedence(current.Type)))
                    claims[index] = (point, type);
            }
        }

        private ((PlaneAxis Axis, long Coordinate)? Anchor, (PlaneAxis Axis, long Coordinate)? Midpoint)
            RimSeams(PlaneAxis circleAxis, int scale)
        {
            const double tolerance = 1e-8;
            Vec3d start = _controlPoints[0].WorldPosition;
            Vec3d end = _controlPoints[1].WorldPosition;
            double cell = scale / VoxelGrid.UnitsPerBlock;
            PlaneAxis u = circleAxis == PlaneAxis.X ? PlaneAxis.Y : PlaneAxis.X;
            PlaneAxis v = circleAxis == PlaneAxis.Z ? PlaneAxis.Y : PlaneAxis.Z;
            double du = Math.Abs(CircleRaster.Coordinate(end, u) - CircleRaster.Coordinate(start, u)) / cell;
            double dv = Math.Abs(CircleRaster.Coordinate(end, v) - CircleRaster.Coordinate(start, v)) / cell;
            double cu = (CircleRaster.Coordinate(start, u) + CircleRaster.Coordinate(end, u)) / (2 * cell);
            double cv = (CircleRaster.Coordinate(start, v) + CircleRaster.Coordinate(end, v)) / (2 * cell);
            double seamU = Math.Round(cu), seamV = Math.Round(cv);
            bool onU = Math.Abs(cu - seamU) <= tolerance;
            bool onV = Math.Abs(cv - seamV) <= tolerance;
            // For diagonal anchors, pair markers only when the circle is centered on both seams.
            if (Math.Min(du, dv) > tolerance && !(onU && onV)) return default;
            (PlaneAxis, long)? uSeam = onU ? (u, (long)seamU * scale) : null;
            (PlaneAxis, long)? vSeam = onV ? (v, (long)seamV * scale) : null;
            return du >= dv ? (vSeam, uSeam) : (uSeam, vSeam);
        }

        private static int Precedence(VoxelRenderType t) => t switch
        {
            VoxelRenderType.Locked => 3,
            VoxelRenderType.Primary => 2,
            VoxelRenderType.Anchor => 1,
            _ => 0
        };

        // --- IGuideShape: curve queries -----------------------------------------------------------

        public float GetNearestT(Vec3d worldPos)
        {
            if (!TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out _, out double rMin)) return 0f;
            const int coarse = 128;
            double bestT = 0, bestD2 = double.MaxValue;
            for (int i = 0; i <= coarse; i++)
            {
                double t = (double)i / coarse;
                double d2 = Dist2(PointAtTheta(c, u, m, rMin, t * 2.0 * Math.PI), worldPos);
                if (d2 < bestD2) { bestD2 = d2; bestT = t; }
            }
            return (float)bestT;
        }

        public Vec3d GetPointAt(float t)
        {
            if (!TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out _, out double rMin))
            {
                Vec3d f = _controlPoints.Count > 0 ? _controlPoints[0].WorldPosition : null;
                return f != null ? new Vec3d(f.X, f.Y, f.Z) : new Vec3d();
            }
            double tt = t < 0f ? 0.0 : t > 1f ? 1.0 : t;
            return PointAtTheta(c, u, m, rMin, tt * 2.0 * Math.PI);
        }

        public List<Vec3d> SampleCurve(int samples)
        {
            var list = new List<Vec3d>();
            if (!TryGetFrame(out Vec3d c, out Vec3d u, out Vec3d m, out _, out double rMin)) return list;
            int n = Math.Max(16, samples);
            for (int i = 0; i <= n; i++)                        // closed: last == first
                list.Add(PointAtTheta(c, u, m, rMin, 2.0 * Math.PI * i / n));
            return list;
        }

        public int GetNearestControlPointIndex(Vec3d worldPos)
        {
            int best = -1; double bestD2 = double.MaxValue;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                if (_controlPoints[i].IsPhantom) continue;
                double d2 = Dist2(_controlPoints[i].WorldPosition, worldPos);
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return best;
        }

        // --- IGuideShape: mutation ------------------------------------------------------------------

        /// <summary>Body inserts have no meaning on a parametric ellipse — defensive no-op (see remarks).</summary>
        public void InsertControlPoint(float t, Vec3d position) { }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;

            if (index == 2)
            {
                // The minor handle slides ALONG the minor axis (the natural resize feel); free-space drags
                // are projected onto it. Under Circle the caller breaks the constraint before this move.
                _controlPoints[2].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
                if (TryGetFrame(out Vec3d c, out _, out Vec3d m, out _, out _))
                {
                    double r = Math.Max(MinRadius, Math.Abs(Dot(Sub(newPosition, c), m)));
                    _controlPoints[2].SetPosition(c.X + m.X * r, c.Y + m.Y * r, c.Z + m.Z * r);
                }
                return;
            }

            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
            DeriveMinorHandle();     // recentre M (and, under Circle, re-derive its radius) as A/B absorb
        }

        public void RecalculatePhantomPoints() { /* no phantoms in this family */ }

        public bool WouldBreakOnMove(int index) =>
            _constraint == ShapeConstraint.Circle && index == 2;

        public bool BreakConstraint()
        {
            if (_constraint == ShapeConstraint.None) return false;
            _constraint = ShapeConstraint.None;      // circle → ellipse is lossless; M keeps its position
            return true;
        }

        // --- small vector helpers (component form, per codebase style) -------------------------------

        private static Vec3d AxisVec(PlaneAxis axis) => axis switch
        {
            PlaneAxis.X => new Vec3d(1, 0, 0),
            PlaneAxis.Z => new Vec3d(0, 0, 1),
            _ => new Vec3d(0, 1, 0)
        };

        private static Vec3d ProjectOutAndNormalise(Vec3d v, Vec3d unit)
        {
            double d = Dot(v, unit);
            var r = new Vec3d(v.X - unit.X * d, v.Y - unit.Y * d, v.Z - unit.Z * d);
            double len = Len(r);
            return len < 1e-6 ? null : Scale(r, 1.0 / len);
        }

        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(
            a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static Vec3d Sub(Vec3d a, Vec3d b) => new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static Vec3d Scale(Vec3d a, double f) => new Vec3d(a.X * f, a.Y * f, a.Z * f);
        private static double Len(Vec3d a) => Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        private static double Dist2(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
