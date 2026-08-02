using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// Behavioural contract for a guide's geometry. Everything outside the Shapes layer — the
    /// renderer, the systems, the networking — talks to guides exclusively through this interface
    /// and never needs to know which concrete shape (arch, future line/circle/dome…) it is.
    /// </summary>
    /// <remarks>
    /// PARAMETER t. Methods that take or return a curve parameter use one convention: t is a value
    /// in [0, 1] running monotonically along the VISIBLE curve, with t = 0 at the start anchor and
    /// t = 1 at the end anchor. Phantom (tangent-only) points lie outside the visible span and are
    /// never addressed by t.
    ///
    /// PHANTOM POINTS. Some shapes carry phantom control points (e.g. an arch's P0/P4) that shape
    /// the curve's end tangents but are never rendered and never grabbable. The mutating methods
    /// here (<see cref="MoveControlPoint"/>, <see cref="InsertControlPoint"/>) leave the shape in a
    /// fully valid state with phantoms already brought up to date, so a caller never has to follow
    /// them with <see cref="RecalculatePhantomPoints"/>. That method exists for the separate case of
    /// restoring validity after the control points were changed by some path OTHER than these
    /// methods — most importantly straight after deserialization.
    ///
    /// VEC3D OWNERSHIP. Any Vec3d passed in is copied by the implementation; the shape never retains
    /// a reference to a caller-owned Vec3d (mirrors ControlPoint's ownership contract).
    /// </remarks>
    public interface IGuideShape
    {
        /// <summary>
        /// The live control-point list — the SAME instance the owning <see cref="GuideData"/> adopts by
        /// reference (the shared-list binding; see GuideData remarks). Mutate only via this shape's
        /// methods. Promoted onto the interface in Session 8 so factory-built shapes hand their spine to
        /// <see cref="GuideData.Create"/> without a concrete-type cast.
        /// </summary>
        List<ControlPoint> ControlPoints { get; }

        /// <summary>
        /// Produces the full, fully-typed voxel set for rendering at the given scale: the visible
        /// curve from start anchor to end anchor, quantised onto the voxel grid and deduplicated,
        /// with each voxel already tagged with its <see cref="VoxelRenderType"/>. Phantom points
        /// contribute to the curve's shape but are themselves never emitted.
        /// </summary>
        /// <param name="scale">
        /// Voxel edge length in 1/16-block units; one of 1, 2, 4, 8, 16. Callers must pass a valid scale.
        /// </param>
        /// <returns>
        /// A deduplicated list (never null; empty if the shape is not yet fully formed). Ordering is
        /// unspecified — the result is a set for meshing, not a path.
        /// </returns>
        List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false);

        /// <summary>
        /// Returns how many voxels <see cref="GetVoxelPositions"/> would produce at the same scale,
        /// computed without the per-voxel typing and final list allocation so it is cheap enough to
        /// run on every throttled edit update for cap checking.
        /// </summary>
        /// <remarks>
        /// HARD INVARIANT: for every valid scale, GetVoxelCount(scale) == GetVoxelPositions(scale).Count.
        /// Server-side cap enforcement is authoritative and rejects edits that exceed the limit, so
        /// this count must be EXACT (not an estimate) and must agree with what actually renders, or the
        /// cap check and the visible guide will silently disagree at the boundary.
        /// </remarks>
        int GetVoxelCount(int scale, bool filled = false);

        /// <summary>The constraint modifier currently applied to this shape (Session 8).</summary>
        ShapeConstraint Constraint { get; }

        /// <summary>
        /// True if moving the control point at <paramref name="index"/> is something the current
        /// constraint CANNOT absorb — the caller should break the constraint before applying the move
        /// (a circle's minor handle; never true for the arch family, whose feet always absorb).
        /// </summary>
        bool WouldBreakOnMove(int index);

        /// <summary>
        /// Clears the constraint, demoting the shape to its free parent — materialising whatever control
        /// points are needed so the break is visually seamless (a half-circle becomes an arch with interior
        /// points sampled ON the arc; a circle becomes an ellipse losslessly). Returns true if anything
        /// changed. The caller (GuideManager) owns persistence, undo recording, and broadcasting.
        /// </summary>
        bool BreakConstraint();

        /// <summary>
        /// A dense polyline of points ON the visible curve, for targeting (Session-8 fix: chords between
        /// control points are nowhere near the rendered curve at an arch's feet, which depart vertically).
        /// Closed shapes include the closing segment (last point == first). Fresh Vec3d instances.
        /// </summary>
        List<Vec3d> SampleCurve(int samples);

        /// <summary>
        /// Returns the curve parameter t in [0, 1] of the point on the visible curve closest to
        /// <paramref name="worldPos"/>. Used to decide where a "grab the body" insert should land.
        /// </summary>
        /// <returns>A t clamped to [0, 1]; returns 0 if the shape has no curve yet.</returns>
        float GetNearestT(Vec3d worldPos);

        /// <summary>
        /// Returns the world position ON the visible curve at parameter <paramref name="t"/> (in [0, 1]).
        /// The returned Vec3d is a fresh instance owned by the caller. Session-8 addition: lets a
        /// lock-in-place insert land exactly on the curve — inserting at the targeting chord's position
        /// instead would visibly dent the shape, since chords cut across the curve between control points.
        /// </summary>
        Vec3d GetPointAt(float t);

        /// <summary>
        /// Returns the INDEX of the nearest GRABBABLE control point to <paramref name="worldPos"/>,
        /// excluding phantom points (which are never grabbable). Returns -1 if the shape has no grabbable
        /// points. This reports distance-nearest only; deciding whether the point is close enough to
        /// actually grab (versus falling through to a body insert via <see cref="GetNearestT"/>) is the
        /// caller's policy.
        /// </summary>
        /// <remarks>
        /// Returning an index rather than the <see cref="ControlPoint"/> object keeps every downstream
        /// consumer — the move pipeline, network packets, and undo commands — speaking the one currency that
        /// can leave this machine: a position in the list. Read the point itself, if needed, via the shape's
        /// control-point list at this index. Indices are stable within a single grab/move (insertions only
        /// ever land in the interior, and the edit lock prevents another player reshuffling the list under an
        /// active edit). The returned index addresses the same list <see cref="MoveControlPoint"/> indexes into.
        /// </remarks>
        int GetNearestControlPointIndex(Vec3d worldPos);

        /// <summary>
        /// Inserts a new movable body control point at parameter <paramref name="t"/> (in [0, 1]) along
        /// the visible curve, positioned at <paramref name="position"/>. The new point carries no
        /// special role (not anchor, primary, locked, or phantom). After this call the shape is fully
        /// valid and the inserted point is the nearest grabbable control point to
        /// <paramref name="position"/>, giving the caller a defined way to immediately grab it.
        /// </summary>
        void InsertControlPoint(float t, Vec3d position);

        /// <summary>
        /// Moves the control point at <paramref name="index"/> to <paramref name="newPosition"/> and
        /// brings phantom points back up to date, leaving the shape fully valid. This is a purely
        /// geometric operation: it does NOT enforce point-lock policy. Refusing to move a locked
        /// (constraint) point is handled upstream — grab targeting skips locked points and server
        /// validation rejects moving them — so the math layer stays free of policy.
        /// </summary>
        /// <param name="index">Index into the control-point list. Must reference a real (non-phantom) point.</param>
        void MoveControlPoint(int index, Vec3d newPosition);

        /// <summary>
        /// Re-derives the phantom (tangent-only) control points from the current real points so the
        /// curve keeps its intended end behaviour (for an arch, arriving and departing vertically at
        /// the feet). Idempotent and safe to call whenever the real points are set. The mutating methods
        /// above already maintain phantoms; the primary use of this method is restoring validity after
        /// the control points were populated by another path, e.g. deserialization.
        /// </summary>
        void RecalculatePhantomPoints();
    }

    /// <summary>
    /// Optional fast path for shapes that can count occupied cells directly without materialising their
    /// render list. The result is exact while it is at or below <paramref name="stopAfter"/>; once the
    /// real count exceeds that threshold the implementation may return the threshold-plus-one sentinel and
    /// stop immediately. This is sufficient for cap enforcement and prevents a rejected guide from doing
    /// millions of unnecessary allocations merely to prove that it is over a much smaller server limit.
    /// </summary>
    public interface IThresholdVoxelCounter
    {
        int GetVoxelCountUpTo(int scale, bool filled, int stopAfter);
    }

    /// <summary>
    /// Additive worker-only extension of <see cref="IThresholdVoxelCounter"/>. Ordinary synchronous callers
    /// keep the original contract; immense-operation workers supply a thread-safe cancellation probe so an
    /// abandoned scan can release the single validator lane without publishing a partial count.
    /// </summary>
    public interface ICancellableThresholdVoxelCounter : IThresholdVoxelCounter
    {
        int GetVoxelCountUpTo(
            int scale, bool filled, int stopAfter, Func<bool> cancellationRequested);
    }

    /// <summary>
    /// Optional exact-generation path for immense-operation workers. Implementations must either return the
    /// complete canonical voxel set or throw <see cref="OperationCanceledException"/>; a partially populated
    /// list must never escape as a successful result.
    /// </summary>
    public interface ICancellableVoxelGenerator
    {
        List<VoxelPosition> GetVoxelPositions(
            int scale, bool filled, Func<bool> cancellationRequested);
    }

    /// <summary>
    /// Optional nominal-dimension source for 3D shapes whose world-axis AABB diagonal is not their width.
    /// Circular and regular-polygon footprints use their true widest diameter; axial height is reported
    /// independently of placement orientation.
    /// </summary>
    public interface IIntrinsicGuideExtent
    {
        bool TryGetIntrinsicDimensions(out double width, out double height);
    }

    /// <summary>Shared dispatch and overflow-safe sentinel helpers for threshold-aware counting.</summary>
    public static class GuideShapeVoxelCounting
    {
        public static int CountUpTo(IGuideShape shape, int scale, bool filled, int stopAfter)
            => CountUpTo(shape, scale, filled, stopAfter, null);

        /// <summary>
        /// Cancellable threshold count for the immense-operation worker. Cancellation is exceptional on
        /// purpose: a partial count must never be mistaken for an exact result or a threshold sentinel.
        /// </summary>
        public static int CountUpTo(
            IGuideShape shape, int scale, bool filled, int stopAfter,
            Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            if (shape == null) return 0;
            stopAfter = Math.Max(0, stopAfter);
            if (shape is ICancellableThresholdVoxelCounter cancellableCounter)
                return cancellableCounter.GetVoxelCountUpTo(
                    scale, filled, stopAfter, cancellationRequested);
            if (shape is IThresholdVoxelCounter thresholdCounter)
            {
                int thresholdCount = thresholdCounter.GetVoxelCountUpTo(scale, filled, stopAfter);
                VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
                return thresholdCount;
            }

            int count = shape.GetVoxelCount(scale, filled);
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            return count > stopAfter ? Exceeded(stopAfter) : count;
        }

        public static int Exceeded(int stopAfter) =>
            stopAfter >= int.MaxValue ? int.MaxValue : Math.Max(0, stopAfter) + 1;
    }

    /// <summary>Shared dispatch for cancellable exact voxel materialisation.</summary>
    public static class GuideShapeVoxelGeneration
    {
        public static List<VoxelPosition> GetPositions(
            IGuideShape shape, int scale, bool filled, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            if (shape == null) return new List<VoxelPosition>();
            if (shape is ICancellableVoxelGenerator cancellableGenerator)
                return cancellableGenerator.GetVoxelPositions(
                    scale, filled, cancellationRequested);

            List<VoxelPosition> result = shape.GetVoxelPositions(scale, filled);
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            return result;
        }
    }

    /// <summary>
    /// Cheap cooperative checkpoint shared by exact-count scanners. The probe runs once per 2,048 visited
    /// scan cells, keeping normal counting overhead negligible while bounding cancellation latency.
    /// </summary>
    internal static class VoxelScanCancellation
    {
        private const int CheckMask = 2047;

        internal static void ThrowIfRequested(Func<bool> cancellationRequested)
        {
            if (cancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();
        }

        internal static void Checkpoint(ref int work, Func<bool> cancellationRequested)
        {
            work++;
            if ((work & CheckMask) == 0)
                ThrowIfRequested(cancellationRequested);
        }
    }
}
