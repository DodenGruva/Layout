using System;
using System.Collections.Generic;
using System.Threading;
using Layout.Guide;
using Vintagestory.API.MathTools;

namespace Layout.Shapes
{
    /// <summary>
    /// A rounded profile swept along an open route. New guides store the exact two clicked profile legs
    /// after the route controls; older guides with one radius handle retain their original quarter-round
    /// interpretation. Interior route corners are replaced with tangent arcs before the profile is swept.
    /// </summary>
    public sealed class RoundoverShape : IGuideShape, ICancellableThresholdVoxelCounter,
        ICancellableVoxelGenerator, IProgressiveVoxelShape
    {
        /// <summary>The wire carries route points plus two profile handles in the shared chain field.</summary>
        public const int MaxRoutePoints = FreeShape.MaxCorners - 2;

        private const double MinLength = 0.05;
        private const double QuarterTurn = Math.PI * 0.5;
        private readonly List<ControlPoint> _controlPoints;

        public List<ControlPoint> ControlPoints => _controlPoints;
        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a legacy shape from route points followed by one tangent/radius handle.</summary>
        public RoundoverShape(IReadOnlyList<Vec3d> points)
            : this(points, hasTwoProfileHandles: false)
        {
        }

        /// <summary>Creates a profile-first shape from route points followed by both exact profile legs.</summary>
        public RoundoverShape(IReadOnlyList<Vec3d> points, bool hasTwoProfileHandles)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            _controlPoints = new List<ControlPoint>(points.Count);
            int profileIndex = points.Count - (hasTwoProfileHandles ? 2 : 1);
            for (int i = 0; i < points.Count; i++)
            {
                Vec3d point = points[i] ?? new Vec3d();
                _controlPoints.Add(new ControlPoint(point,
                    isAnchor: i < profileIndex,
                    isPrimary: i >= profileIndex));
            }
        }

        /// <summary>Adopts an existing load/wire list by reference.</summary>
        public RoundoverShape(List<ControlPoint> controlPoints)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
        }

        private int ProfileHandleCount => _controlPoints.Count >= 4
            && _controlPoints[_controlPoints.Count - 1].IsPrimary
            && _controlPoints[_controlPoints.Count - 2].IsPrimary ? 2 : 1;
        private int RouteCount => Math.Max(0, _controlPoints.Count - ProfileHandleCount);

        private bool TryInitialFrame(
            out double profileExtent, out Vec3d tangent, out Vec3d u, out Vec3d v)
        {
            profileExtent = 0;
            tangent = u = v = null;
            if (RouteCount < 2) return false;

            Vec3d start = _controlPoints[0].WorldPosition;
            Vec3d second = _controlPoints[1].WorldPosition;
            tangent = Unit(Sub(second, start));
            if (tangent == null) return false;

            if (ProfileHandleCount == 2)
            {
                u = Sub(_controlPoints[RouteCount].WorldPosition, start);
                v = Sub(_controlPoints[RouteCount + 1].WorldPosition, start);
                double uLength = ShapeGeometry.Len(u);
                double vLength = ShapeGeometry.Len(v);
                profileExtent = Math.Max(uLength, vLength);
                return uLength >= MinLength && vLength >= MinLength;
            }

            Vec3d handle = _controlPoints[_controlPoints.Count - 1].WorldPosition;
            Vec3d radial = Sub(handle, start);
            double along = ShapeGeometry.Dot(radial, tangent);
            radial = new Vec3d(radial.X - tangent.X * along,
                radial.Y - tangent.Y * along,
                radial.Z - tangent.Z * along);
            profileExtent = ShapeGeometry.Len(radial);
            if (profileExtent < MinLength) return false;

            v = radial;
            Vec3d unitV = Scale(radial, 1.0 / profileExtent);
            Vec3d unitU = Unit(ShapeGeometry.Cross(tangent, unitV));
            if (unitU == null) return false;
            u = Scale(unitU, profileExtent);
            return true;
        }

        private bool TryRouteTangents(out Vec3d[] tangents)
        {
            int routeCount = RouteCount;
            tangents = routeCount >= 2 ? new Vec3d[routeCount - 1] : null;
            if (tangents == null) return false;
            for (int i = 0; i < tangents.Length; i++)
            {
                tangents[i] = Unit(Sub(
                    _controlPoints[i + 1].WorldPosition,
                    _controlPoints[i].WorldPosition));
                if (tangents[i] == null) return false;
                if (i > 0 && ShapeGeometry.Dot(tangents[i - 1], tangents[i]) < -0.999999)
                    return false; // a full reversal has no unique rolling-ball turn
            }
            return true;
        }

        private sealed class CornerFillet
        {
            internal Vec3d Start;
            internal Vec3d End;
            internal Vec3d Center;
            internal Vec3d Axis;
            internal double Angle;
            internal double Radius;
            internal double Trim;
        }

        /// <summary>
        /// Replaces each sharp route vertex with a tangent arc. The arc's route radius is twice the
        /// roundover radius: its innermost profile rail then still has radius R instead of collapsing to
        /// the vertex. Returning false means the requested radius cannot fit between neighboring corners.
        /// </summary>
        private bool TryBuildFillets(double roundoverRadius, Vec3d[] tangents,
            out CornerFillet[] fillets)
        {
            int routeCount = RouteCount;
            fillets = new CornerFillet[routeCount];
            double routeRadius = roundoverRadius * 2.0;

            for (int vertex = 1; vertex < routeCount - 1; vertex++)
            {
                double dot = Clamp(ShapeGeometry.Dot(tangents[vertex - 1], tangents[vertex]), -1, 1);
                double angle = Math.Acos(dot);
                if (angle < 1e-8) continue;

                Vec3d axis = Unit(ShapeGeometry.Cross(tangents[vertex - 1], tangents[vertex]));
                double trim = routeRadius * Math.Tan(angle * 0.5);
                if (axis == null || double.IsNaN(trim) || double.IsInfinity(trim)) return false;

                Vec3d corner = _controlPoints[vertex].WorldPosition;
                Vec3d start = Add(corner, Scale(tangents[vertex - 1], -trim));
                Vec3d end = Add(corner, Scale(tangents[vertex], trim));
                Vec3d center = Add(start,
                    Scale(ShapeGeometry.Cross(axis, tangents[vertex - 1]), routeRadius));
                fillets[vertex] = new CornerFillet
                {
                    Start = start,
                    End = end,
                    Center = center,
                    Axis = axis,
                    Angle = angle,
                    Radius = routeRadius,
                    Trim = trim
                };
            }

            // Both corner trims sharing a route segment must fit without crossing. Rejecting an
            // oversized radius is preferable to silently recreating a cusp or miter on a short segment.
            for (int segment = 0; segment < tangents.Length; segment++)
            {
                double startTrim = fillets[segment]?.Trim ?? 0;
                double endTrim = fillets[segment + 1]?.Trim ?? 0;
                double length = ShapeGeometry.Dist(
                    _controlPoints[segment].WorldPosition,
                    _controlPoints[segment + 1].WorldPosition);
                if (startTrim + endTrim > length - 1e-7) return false;
            }
            return true;
        }

        private sealed class VoxelAccumulator
        {
            private readonly Dictionary<(int, int, int), int> _indices;
            private readonly HashSet<(int, int, int)> _seen;
            private readonly ProgressiveVoxelCollector _progressive;
            private readonly Func<bool> _cancellationRequested;
            private readonly int _stopAfter;
            private int _work;

            internal List<VoxelPosition> Result { get; }
            internal int Count { get; private set; }
            internal bool Exceeded { get; private set; }

            internal VoxelAccumulator(int stopAfter, Func<bool> cancellationRequested,
                ProgressiveVoxelCollector progressive = null, bool storePositions = true)
            {
                _stopAfter = Math.Max(0, stopAfter);
                _cancellationRequested = cancellationRequested;
                _progressive = progressive;
                if (storePositions)
                {
                    _indices = new Dictionary<(int, int, int), int>();
                    Result = progressive?.Result ?? new List<VoxelPosition>();
                }
                else
                {
                    _seen = new HashSet<(int, int, int)>();
                    Result = null;
                }
            }

            internal bool Claim(Vec3d point, int scale)
            {
                VoxelScanCancellation.Checkpoint(ref _work, _cancellationRequested);
                var key = Quantize(point, scale);
                if (_indices != null)
                {
                    if (_indices.ContainsKey(key)) return true;
                    _indices.Add(key, Count);
                }
                else if (!_seen.Add(key)) return true;

                Count++;
                if (Result != null)
                {
                    var voxel = new VoxelPosition(
                        key.Item1, key.Item2, key.Item3, VoxelRenderType.Normal);
                    if (_progressive != null) _progressive.Add(voxel);
                    else Result.Add(voxel);
                }
                if (Count <= _stopAfter) return true;
                Exceeded = true;
                return false;
            }

            internal void Mark(Vec3d point, int scale, VoxelRenderType type)
            {
                if (type == VoxelRenderType.Normal || _indices == null) return;
                var key = Quantize(point, scale);
                if (_indices.TryGetValue(key, out int index)
                    && ShapeGeometry.Precedence(type) > ShapeGeometry.Precedence(Result[index].Type))
                    Result[index] = Result[index].WithType(type);
            }

            private static (int, int, int) Quantize(Vec3d point, int scale) => (
                (int)Math.Floor(point.X * 16.0 / scale) * scale,
                (int)Math.Floor(point.Y * 16.0 / scale) * scale,
                (int)Math.Floor(point.Z * 16.0 / scale) * scale);
        }

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false) =>
            GetVoxelPositions(scale, filled, null);

        public List<VoxelPosition> GetVoxelPositions(
            int scale, bool filled, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            var sink = new VoxelAccumulator(int.MaxValue, cancellationRequested);
            GenerateSurface(scale, sink, markControlPoints: true);
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            return sink.Result;
        }

        public List<VoxelPosition> GetVoxelPositionsProgressively(
            int scale, bool filled, int targetVoxelsPerChunk,
            CancellationToken cancellationToken, Action<List<VoxelPosition>> emitChunk)
        {
            var collector = new ProgressiveVoxelCollector(
                targetVoxelsPerChunk, cancellationToken, emitChunk);
            var sink = new VoxelAccumulator(int.MaxValue,
                () => cancellationToken.IsCancellationRequested, collector);
            GenerateSurface(scale, sink, markControlPoints: false);
            collector.Flush();
            MarkControlPoints(sink, scale);
            return sink.Result;
        }

        public int GetVoxelCount(int scale, bool filled = false) =>
            GetVoxelCountUpTo(scale, filled, int.MaxValue);

        public int GetVoxelCountUpTo(int scale, bool filled, int stopAfter) =>
            GetVoxelCountUpTo(scale, filled, stopAfter, null);

        public int GetVoxelCountUpTo(
            int scale, bool filled, int stopAfter, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            stopAfter = Math.Max(0, stopAfter);
            var sink = new VoxelAccumulator(
                stopAfter, cancellationRequested, storePositions: false);
            GenerateSurface(scale, sink, markControlPoints: false);
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            return sink.Exceeded ? GuideShapeVoxelCounting.Exceeded(stopAfter) : sink.Count;
        }

        private void GenerateSurface(int scale, VoxelAccumulator sink, bool markControlPoints)
        {
            if (!GuideData.IsValidVoxelScale(scale)
                || !TryInitialFrame(out double profileExtent, out _,
                    out Vec3d initialU, out Vec3d initialV)
                || !TryRouteTangents(out Vec3d[] tangents)
                || !TryBuildFillets(profileExtent, tangents, out CornerFillet[] fillets))
                return;

            double halfCell = scale / 32.0;
            int profileSteps = StepCount(QuarterTurn * profileExtent, halfCell, minimum: 4);
            for (int profileIndex = 0; profileIndex <= profileSteps && !sink.Exceeded; profileIndex++)
            {
                double theta = QuarterTurn * profileIndex / profileSteps;
                // Keep the two tangent rails exact. Tiny trig residues at pi/2 otherwise quantize an
                // axis-aligned endpoint into the cell just below it, separating its visible marker.
                double cos = profileIndex == profileSteps ? 0.0 : Math.Cos(theta);
                double sin = profileIndex == 0 ? 0.0 : Math.Sin(theta);
                Vec3d u = Copy(initialU), v = Copy(initialV);

                Vec3d cursor = _controlPoints[0].WorldPosition;
                if (!sink.Claim(ProfilePoint(cursor, u, v, cos, sin), scale)) break;

                for (int segment = 0; segment < tangents.Length && !sink.Exceeded; segment++)
                {
                    CornerFillet fillet = fillets[segment + 1];
                    Vec3d straightEnd = fillet?.Start
                        ?? _controlPoints[segment + 1].WorldPosition;
                    int longitudinalSteps = StepCount(
                        ShapeGeometry.Dist(cursor, straightEnd), halfCell, minimum: 1);
                    for (int step = 1; step <= longitudinalSteps; step++)
                    {
                        Vec3d position = ShapeGeometry.Lerp(
                            cursor, straightEnd, (double)step / longitudinalSteps);
                        if (!sink.Claim(ProfilePoint(position, u, v, cos, sin), scale)) break;
                    }
                    cursor = straightEnd;
                    if (sink.Exceeded || fillet == null) continue;

                    Vec3d beforeU = u, beforeV = v;
                    Vec3d startOffset = Sub(fillet.Start, fillet.Center);
                    // The outermost swept rail travels as far as routeRadius + profileRadius.
                    int joinSteps = StepCount(
                        (fillet.Radius + profileExtent) * fillet.Angle, halfCell, minimum: 2);
                    for (int step = 1; step <= joinSteps; step++)
                    {
                        double turn = fillet.Angle * step / joinSteps;
                        Vec3d routePoint = Add(fillet.Center,
                            Rotate(startOffset, fillet.Axis, turn));
                        Vec3d turnedU = Rotate(beforeU, fillet.Axis, turn);
                        Vec3d turnedV = Rotate(beforeV, fillet.Axis, turn);
                        if (!sink.Claim(ProfilePoint(
                            routePoint, turnedU, turnedV, cos, sin), scale)) break;
                    }
                    u = Rotate(beforeU, fillet.Axis, fillet.Angle);
                    v = Rotate(beforeV, fillet.Axis, fillet.Angle);
                    cursor = fillet.End;
                }
            }

            if (markControlPoints && !sink.Exceeded) MarkControlPoints(sink, scale);
        }

        private void MarkControlPoints(VoxelAccumulator sink, int scale)
        {
            // A route control represents the original sharp edge. Its visible shell marker lies at the
            // middle of the concave profile and, for an interior control, halfway around its tangent arc.
            // Color that exact sampled cell so hit ownership agrees with what the player sees.
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                ControlPoint point = _controlPoints[i];
                VoxelRenderType type = point.IsLocked ? VoxelRenderType.Locked
                    : point.IsPrimary ? VoxelRenderType.Primary
                    : point.IsAnchor ? VoxelRenderType.Anchor : VoxelRenderType.Normal;
                if (TryGetMarkerPosition(i, scale, out Vec3d marker)) sink.Mark(marker, scale, type);
            }
        }

        /// <summary>
        /// Exact sampled shell position colored for one control. The targeting layer uses this additive
        /// seam to map Roundover markers from bounded route geometry rather than rescanning a large shell
        /// once per point on every crosshair hit.
        /// </summary>
        internal bool TryGetMarkerPosition(int index, int scale, out Vec3d marker)
        {
            marker = null;
            if (index < 0 || index >= _controlPoints.Count
                || !GuideData.IsValidVoxelScale(scale)
                || !TryInitialFrame(out double profileExtent, out _, out Vec3d u, out Vec3d v)
                || !TryRouteTangents(out Vec3d[] tangents)
                || !TryBuildFillets(profileExtent, tangents, out CornerFillet[] fillets))
                return false;

            if (index >= RouteCount)
            {
                marker = Copy(_controlPoints[index].WorldPosition);
                return true;
            }

            for (int vertex = 1; vertex < index && vertex < RouteCount - 1; vertex++)
            {
                CornerFillet preceding = fillets[vertex];
                if (preceding == null) continue;
                u = Rotate(u, preceding.Axis, preceding.Angle);
                v = Rotate(v, preceding.Axis, preceding.Angle);
            }

            int profileSteps = StepCount(QuarterTurn * profileExtent, scale / 32.0, minimum: 4);
            int nearestProfileIndex = (int)Math.Round(profileSteps * 0.5);
            double theta = QuarterTurn * nearestProfileIndex / profileSteps;
            double cos = nearestProfileIndex == profileSteps ? 0.0 : Math.Cos(theta);
            double sin = nearestProfileIndex == 0 ? 0.0 : Math.Sin(theta);
            Vec3d routePoint = _controlPoints[index].WorldPosition;

            CornerFillet ownFillet = index < fillets.Length ? fillets[index] : null;
            if (ownFillet != null)
            {
                int joinSteps = StepCount(
                    (ownFillet.Radius + profileExtent) * ownFillet.Angle,
                    scale / 32.0, minimum: 2);
                int middleStep = Math.Max(1, (int)Math.Round(joinSteps * 0.5));
                double turn = ownFillet.Angle * middleStep / joinSteps;
                routePoint = Add(ownFillet.Center,
                    Rotate(Sub(ownFillet.Start, ownFillet.Center), ownFillet.Axis, turn));
                u = Rotate(u, ownFillet.Axis, turn);
                v = Rotate(v, ownFillet.Axis, turn);
            }

            marker = ProfilePoint(routePoint, u, v, cos, sin);
            return true;
        }

        private static Vec3d ProfilePoint(
            Vec3d routePoint, Vec3d u, Vec3d v, double cos, double sin) =>
            new Vec3d(
                routePoint.X + (1.0 - cos) * u.X + (1.0 - sin) * v.X,
                routePoint.Y + (1.0 - cos) * u.Y + (1.0 - sin) * v.Y,
                routePoint.Z + (1.0 - cos) * u.Z + (1.0 - sin) * v.Z);

        // --- targeting / editing -----------------------------------------------------------------

        public List<Vec3d> SampleCurve(int samples) =>
            SampleRail(samples, Math.Sqrt(0.5), Math.Sqrt(0.5));

        /// <summary>
        /// The useful construction cage for a Roundover: both exact clicked profile endpoints swept along
        /// the path, plus the original sharp-corner route between them. Keeping these as separate polylines
        /// prevents the wireframe marcher from drawing false cross-connections between the three rails.
        /// </summary>
        public List<List<Vec3d>> SampleWireframeRails(int samples)
        {
            return new List<List<Vec3d>>
            {
                SampleRail(samples, cos: 0.0, sin: 1.0),
                SampleRail(samples, cos: 1.0, sin: 1.0),
                SampleRail(samples, cos: 1.0, sin: 0.0)
            };
        }

        private List<Vec3d> SampleRail(int samples, double cos, double sin)
        {
            var result = new List<Vec3d>();
            if (!TryInitialFrame(out double profileExtent, out _, out Vec3d u, out Vec3d v)
                || !TryRouteTangents(out Vec3d[] tangents)
                || !TryBuildFillets(profileExtent, tangents, out CornerFillet[] fillets))
                return result;

            double total = 0;
            Vec3d lengthCursor = _controlPoints[0].WorldPosition;
            for (int segment = 0; segment < tangents.Length; segment++)
            {
                CornerFillet fillet = fillets[segment + 1];
                Vec3d straightEnd = fillet?.Start
                    ?? _controlPoints[segment + 1].WorldPosition;
                total += ShapeGeometry.Dist(lengthCursor, straightEnd);
                if (fillet != null)
                {
                    total += fillet.Radius * fillet.Angle;
                    lengthCursor = fillet.End;
                }
                else lengthCursor = straightEnd;
            }
            double targetStep = total / Math.Max(16, samples);
            Vec3d cursor = _controlPoints[0].WorldPosition;
            result.Add(ProfilePoint(cursor, u, v, cos, sin));

            for (int segment = 0; segment < tangents.Length; segment++)
            {
                CornerFillet fillet = fillets[segment + 1];
                Vec3d straightEnd = fillet?.Start
                    ?? _controlPoints[segment + 1].WorldPosition;
                int steps = StepCount(ShapeGeometry.Dist(cursor, straightEnd),
                    Math.Max(MinLength, targetStep), 1);
                for (int step = 1; step <= steps; step++)
                    result.Add(ProfilePoint(ShapeGeometry.Lerp(
                            cursor, straightEnd, (double)step / steps),
                        u, v, cos, sin));
                cursor = straightEnd;

                if (fillet == null) continue;
                Vec3d beforeU = u, beforeV = v;
                Vec3d startOffset = Sub(fillet.Start, fillet.Center);
                int joinSteps = StepCount((fillet.Radius + profileExtent) * fillet.Angle,
                    Math.Max(MinLength, targetStep), minimum: 2);
                for (int step = 1; step <= joinSteps; step++)
                {
                    double turn = fillet.Angle * step / joinSteps;
                    Vec3d routePoint = Add(fillet.Center,
                        Rotate(startOffset, fillet.Axis, turn));
                    result.Add(ProfilePoint(routePoint,
                        Rotate(beforeU, fillet.Axis, turn),
                        Rotate(beforeV, fillet.Axis, turn), cos, sin));
                }
                u = Rotate(beforeU, fillet.Axis, fillet.Angle);
                v = Rotate(beforeV, fillet.Axis, fillet.Angle);
                cursor = fillet.End;
            }
            return result;
        }

        public float GetNearestT(Vec3d worldPos)
        {
            List<Vec3d> curve = SampleCurve(256);
            if (curve.Count < 2) return 0;
            double[] cumulative = Cumulative(curve, out double length);
            double best = double.MaxValue, bestAlong = 0;
            for (int i = 1; i < curve.Count; i++)
            {
                Vec3d a = curve[i - 1], b = curve[i];
                Vec3d ab = Sub(b, a), ap = Sub(worldPos, a);
                double denom = ShapeGeometry.Dot(ab, ab);
                double f = denom < 1e-12 ? 0 : Clamp(ShapeGeometry.Dot(ap, ab) / denom, 0, 1);
                Vec3d nearest = Add(a, Scale(ab, f));
                double d = ShapeGeometry.Dist(nearest, worldPos);
                if (d < best) { best = d; bestAlong = cumulative[i - 1] + f * (cumulative[i] - cumulative[i - 1]); }
            }
            return length < 1e-9 ? 0 : (float)(bestAlong / length);
        }

        public Vec3d GetPointAt(float t)
        {
            List<Vec3d> curve = SampleCurve(256);
            if (curve.Count == 0) return new Vec3d();
            double[] cumulative = Cumulative(curve, out double length);
            double target = Clamp(t, 0, 1) * length;
            for (int i = 1; i < curve.Count; i++)
            {
                if (target > cumulative[i] && i < curve.Count - 1) continue;
                double span = cumulative[i] - cumulative[i - 1];
                return ShapeGeometry.Lerp(curve[i - 1], curve[i],
                    span < 1e-9 ? 0 : (target - cumulative[i - 1]) / span);
            }
            return Copy(curve[curve.Count - 1]);
        }

        public int GetNearestControlPointIndex(Vec3d worldPos)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                ControlPoint point = _controlPoints[i];
                if (point == null || point.IsPhantom) continue;
                double distance = ShapeGeometry.Dist(point.WorldPosition, worldPos);
                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }
            return best;
        }

        public void InsertControlPoint(float t, Vec3d position) { /* route insertion is not a v1 edit gesture */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count || newPosition == null) return;
            // Deliberately move only the named point. Incremental edit packets, undo origins and remote
            // clients all name explicit indices; silently carrying the radius handle with route point 0
            // would make the server mutate geometry that those paths never received.
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
        }

        public void RecalculatePhantomPoints() { }
        public bool WouldBreakOnMove(int index) => false;
        public bool BreakConstraint() => false;

        private static double[] Cumulative(List<Vec3d> points, out double total)
        {
            var result = new double[points.Count];
            for (int i = 1; i < points.Count; i++)
                result[i] = result[i - 1] + ShapeGeometry.Dist(points[i - 1], points[i]);
            total = result.Length == 0 ? 0 : result[result.Length - 1];
            return result;
        }

        private static int StepCount(double length, double maximumStep, int minimum)
        {
            if (maximumStep <= 0 || double.IsNaN(length) || double.IsInfinity(length)) return minimum;
            double raw = Math.Ceiling(Math.Max(0, length) / maximumStep);
            return raw >= int.MaxValue ? int.MaxValue : Math.Max(minimum, (int)raw);
        }

        private static Vec3d Rotate(Vec3d vector, Vec3d unitAxis, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            double dot = ShapeGeometry.Dot(unitAxis, vector) * (1.0 - c);
            Vec3d cross = ShapeGeometry.Cross(unitAxis, vector);
            return new Vec3d(vector.X * c + cross.X * s + unitAxis.X * dot,
                vector.Y * c + cross.Y * s + unitAxis.Y * dot,
                vector.Z * c + cross.Z * s + unitAxis.Z * dot);
        }

        private static Vec3d Unit(Vec3d vector)
        {
            double length = ShapeGeometry.Len(vector);
            return length < 1e-9 ? null : Scale(vector, 1.0 / length);
        }

        private static Vec3d Add(Vec3d a, Vec3d b) =>
            new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static Vec3d Sub(Vec3d a, Vec3d b) =>
            new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static Vec3d Scale(Vec3d a, double factor) =>
            new Vec3d(a.X * factor, a.Y * factor, a.Z * factor);
        private static Vec3d Copy(Vec3d a) => new Vec3d(a.X, a.Y, a.Z);
        private static double Clamp(double value, double min, double max) =>
            value < min ? min : value > max ? max : value;
    }
}
