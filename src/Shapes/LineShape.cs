using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The straight segment (Session 9): two anchors, nothing else. No interior points ever — the
    /// controller maps body clicks to the nearest anchor (the polygon-family policy), and
    /// <see cref="InsertControlPoint"/> is a defensive no-op — so a Line stays a line. Fill is meaningless
    /// (no enclosed region) and is ignored. No constraints exist on this primitive.
    /// [Flagged idea, parked: a body-grab could someday BREAK a line into a free arch (bendable line)
    ///  under absorb-or-break; deliberately out of scope this session.]
    /// </summary>
    public sealed class LineShape : IGuideShape
    {
        private readonly List<ControlPoint> _controlPoints;

        public List<ControlPoint> ControlPoints => _controlPoints;

        public ShapeConstraint Constraint => ShapeConstraint.None;

        /// <summary>Creates a fresh line from the two draft clicks.</summary>
        public LineShape(Vec3d a, Vec3d b)
        {
            _controlPoints = new List<ControlPoint>
            {
                new ControlPoint(new Vec3d(a.X, a.Y, a.Z), isAnchor: true),
                new ControlPoint(new Vec3d(b.X, b.Y, b.Z), isAnchor: true)
            };
        }

        /// <summary>Adopts an existing list (load/wire path). Shared by reference, never copied.</summary>
        public LineShape(List<ControlPoint> controlPoints)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
        }

        private bool TryGetEnds(out Vec3d a, out Vec3d b)
        {
            a = b = null;
            if (_controlPoints.Count < 2) return false;
            a = _controlPoints[0].WorldPosition;
            b = _controlPoints[1].WorldPosition;
            return true;
        }

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            var result = new List<VoxelPosition>();
            if (!TryGetEnds(out Vec3d a, out Vec3d b)) return result;

            var seen = new HashSet<(int, int, int)>();
            VoxelMarch.MarchSegmentInto(result, seen, a, b, scale);

            for (int i = 0; i < 2 && i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                ShapeGeometry.ClaimMarker(result, scale, cp.WorldPosition,
                    cp.IsLocked ? VoxelRenderType.Locked : VoxelRenderType.Anchor);
            }
            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false) => GetVoxelPositions(scale, filled).Count;

        public float GetNearestT(Vec3d worldPos)
        {
            if (!TryGetEnds(out Vec3d a, out Vec3d b)) return 0f;
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double denom = ux * ux + uy * uy + uz * uz;
            if (denom < 1e-12) return 0f;
            double f = ((worldPos.X - a.X) * ux + (worldPos.Y - a.Y) * uy + (worldPos.Z - a.Z) * uz) / denom;
            return (float)(f < 0 ? 0 : f > 1 ? 1 : f);
        }

        public Vec3d GetPointAt(float t)
        {
            if (!TryGetEnds(out Vec3d a, out Vec3d b))
                return _controlPoints.Count > 0
                    ? new Vec3d(_controlPoints[0].WorldPosition.X, _controlPoints[0].WorldPosition.Y, _controlPoints[0].WorldPosition.Z)
                    : new Vec3d();
            double tt = t < 0f ? 0.0 : t > 1f ? 1.0 : t;
            return ShapeGeometry.Lerp(a, b, tt);
        }

        public List<Vec3d> SampleCurve(int samples)
        {
            var list = new List<Vec3d>();
            if (!TryGetEnds(out Vec3d a, out Vec3d b)) return list;
            int n = Math.Max(8, samples);
            for (int i = 0; i <= n; i++) list.Add(ShapeGeometry.Lerp(a, b, (double)i / n));
            return list;
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

        public void InsertControlPoint(float t, Vec3d position) { /* a line has no interior points */ }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count) return;
            _controlPoints[index].SetPosition(newPosition.X, newPosition.Y, newPosition.Z);
        }

        public void RecalculatePhantomPoints() { /* no phantoms */ }

        public bool WouldBreakOnMove(int index) => false;

        public bool BreakConstraint() => false;
    }
}
