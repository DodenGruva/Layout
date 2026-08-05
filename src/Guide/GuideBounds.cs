using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Layout.Guide
{
    /// <summary>
    /// The one range check every guide coordinate passes before anything scans it.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS (A14.7, Session 36). Every volume scan guard in this mod bounds a shape's SIZE and
    /// never its POSITION, so a one-block shape at an absurd coordinate sails through the guard and into a
    /// lattice loop of the form:
    ///
    ///     for (int i = AlignDown(lo, scale); i &lt;= AlignDown(hi, scale); i += scale)
    ///
    /// where <c>AlignDown</c> is <c>(int)Math.Floor(world * 16.0 / scale) * scale</c>. Past roughly
    /// ±134 million the ×16 puts that bound beside <see cref="int.MaxValue"/>, <c>i += scale</c> wraps
    /// negative, the condition stays true, and the loop NEVER TERMINATES. The <c>count &gt; stopAfter</c>
    /// escape does not save it: the wrapped cells fail the range test, so nothing is ever counted.
    ///
    /// THREE SOURCES FEED THOSE LOOPS, and only one of them is adversarial:
    ///   1. the wire — every create/edit/insert/push packet (a modified client);
    ///   2. <c>Layout/ClientOnlyGuides/*.json</c> — a HAND-EDITED private guide file hangs the player's own
    ///      client on world load, no attacker anywhere;
    ///   3. the world save blob — a corrupted save hangs the server at start.
    /// So this is a corruption-robustness fix first and a hostile-client fix second.
    ///
    /// NO FALSE REJECTIONS. Every real placement is inside the map by construction, and the limit still
    /// carries a generous margin past the map edge plus a hard ±33.5M backstop — the same figure
    /// <c>BlockOccupancy.Key</c> already calls "far beyond any world", and four times inside the coordinate
    /// that actually overflows.
    ///
    /// Static because the call sites sit in layers that share no object: the server network handler,
    /// <c>GuideManager</c>'s mutation/load/restore paths (which hold no world API), and the client's private
    /// guide authority. Both sides call <see cref="UseWorldSize"/> once at startup; until then, and forever
    /// if the engine reports nothing, the hard backstop alone applies — which is what closes the hang.
    /// </remarks>
    public static class GuideBounds
    {
        /// <summary>
        /// Absolute limit regardless of world size. 2^25 blocks — <c>BlockOccupancy</c>'s own "far beyond
        /// any world" figure, and a quarter of the ~134M coordinate at which the scan loops overflow.
        /// </summary>
        public const double HardExtent = 33_554_432.0;

        /// <summary>
        /// How far past the map edge a coordinate may still sit. Guides are placed by aiming at blocks, so
        /// nothing legitimate lands outside the map at all — this is pure slack so that an edge placement,
        /// a phantom point derived slightly beyond it, or an off-by-one in the engine's reported size can
        /// never be refused.
        /// </summary>
        private const double MapMargin = 4096.0;

        private static double _limitX = HardExtent;
        private static double _limitY = HardExtent;
        private static double _limitZ = HardExtent;

        /// <summary>
        /// Narrows the limit to the real world once the engine reports it. Safe to call more than once and
        /// from either side; a non-positive size is ignored, leaving the hard backstop in force.
        /// </summary>
        public static void UseWorldSize(int mapSizeX, int mapSizeY, int mapSizeZ)
        {
            _limitX = Limit(mapSizeX);
            _limitY = Limit(mapSizeY);
            _limitZ = Limit(mapSizeZ);
        }

        /// <summary>
        /// Reads the world's size from a block accessor, tolerating a null or a not-yet-ready one. Never
        /// throws: this runs from a load event, and the hard backstop is already in force without it, so
        /// failing to narrow the limit must never be able to stop the mod from loading.
        /// </summary>
        public static void UseWorldSize(IBlockAccessor accessor)
        {
            if (accessor == null) return;
            try { UseWorldSize(accessor.MapSizeX, accessor.MapSizeY, accessor.MapSizeZ); }
            catch (Exception) { /* leave the hard backstop in force */ }
        }

        private static double Limit(int mapSize) =>
            mapSize <= 0 ? HardExtent : Math.Min(HardExtent, mapSize + MapMargin);

        /// <summary>
        /// True if this coordinate is safe to scan: finite (no NaN, no infinity) and inside the world.
        /// A null is accepted — the call sites all treat a missing point as "not supplied", which their
        /// own logic already handles, and rejecting it here would change unrelated behaviour.
        /// </summary>
        public static bool IsUsable(Vec3d p)
        {
            if (p == null) return true;
            return Within(p.X, _limitX) && Within(p.Y, _limitY) && Within(p.Z, _limitZ);
        }

        private static bool Within(double v, double limit) =>
            !double.IsNaN(v) && !double.IsInfinity(v) && v >= -MapMargin && v <= limit;

        /// <summary>True only if every supplied point is usable. A null list is usable (nothing to check).</summary>
        public static bool AllUsable(IReadOnlyList<Vec3d> points)
        {
            if (points == null) return true;
            for (int i = 0; i < points.Count; i++)
                if (!IsUsable(points[i])) return false;
            return true;
        }

        /// <summary>True only if every control point in the list is usable.</summary>
        public static bool AllUsable(IReadOnlyList<ControlPoint> points)
        {
            if (points == null) return true;
            for (int i = 0; i < points.Count; i++)
                if (!IsUsable(points[i]?.WorldPosition)) return false;
            return true;
        }

        /// <summary>
        /// True only for a defined projection mode and plane axis whose stored plane coordinate lies inside
        /// the same world bounds as guide points. Volumetric guides do not render against that plane, but
        /// whole-guide movement still carries it, so leaving an extreme dormant value would retain an
        /// overflow seam and could make an otherwise valid guide impossible to move safely.
        /// </summary>
        public static bool IsUsableProjection(ProjectionMode mode, ProjectionPlane plane)
        {
            if (!Enum.IsDefined(typeof(ProjectionMode), mode)
                || !Enum.IsDefined(typeof(PlaneAxis), plane.FlattenedAxis))
                return false;
            double coordinate = plane.PlaneOffset / 16.0;
            return plane.FlattenedAxis switch
            {
                PlaneAxis.X => Within(coordinate, _limitX),
                PlaneAxis.Z => Within(coordinate, _limitZ),
                _ => Within(coordinate, _limitY)
            };
        }
    }
}
