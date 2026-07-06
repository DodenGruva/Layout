using System;

namespace Layout.Guide
{
    /// <summary>
    /// The axis that a Surface guide flattens (drops) when projecting onto a plane.
    /// </summary>
    /// <remarks>
    /// These integer values are part of the on-disk and on-wire contract: a <see cref="ProjectionPlane"/>
    /// carrying one of them is persisted inside <see cref="GuideData"/> and sent in packets. They must
    /// NEVER be reordered, reused, or removed once shipped; new axes (there will not be any — space has
    /// three) are not anticipated, but the append-only discipline still applies. X = 0 is the natural
    /// default of <c>default(PlaneAxis)</c>; callers that want a sensible default plane should use
    /// <see cref="ProjectionPlane.Default"/> rather than relying on the zero value's meaning.
    /// </remarks>
    public enum PlaneAxis
    {
        X = 0,
        Y = 1,
        Z = 2
    }

    /// <summary>
    /// Describes the flat, axis-aligned plane a Surface-mode guide projects onto: which axis is
    /// flattened, and where along that axis the plane sits.
    /// </summary>
    /// <remarks>
    /// AXIS MAPPING (the flattened axis is the dropped coordinate; the other two are kept):
    ///   <see cref="PlaneAxis.Y"/> → horizontal ground plane (keep X, Z) — a footprint.
    ///   <see cref="PlaneAxis.Z"/> → vertical wall facing north–south (keep X, Y) — an upright silhouette.
    ///   <see cref="PlaneAxis.X"/> → vertical wall facing east–west (keep Z, Y).
    /// Use the named factories (<see cref="Horizontal"/>, <see cref="VerticalNorthSouth"/>,
    /// <see cref="VerticalEastWest"/>) at call sites for readability instead of remembering this table.
    ///
    /// OFFSET — <see cref="PlaneOffset"/> is the coordinate on the flattened axis, expressed in the same
    /// 1/16-block units as <see cref="VoxelPosition"/> (world × 16). It is the only place the plane's
    /// world location is recorded; the kept axes are free.
    ///
    /// VALUE TYPE — this is a small immutable <c>readonly struct</c> with value semantics, mirroring
    /// <see cref="VoxelPosition"/>. It carries no <c>Vec3d</c> and no reference state, so there is no
    /// aliasing hazard: copying it is a genuine, independent copy. It is only consulted when the owning
    /// guide is in <see cref="ProjectionMode.Surface"/>; a Volumetric guide still carries a (defaulted)
    /// plane so the field always round-trips cleanly, but ignores it.
    ///
    /// SERIALIZATION — exactly one public constructor whose parameter names match the property names, so
    /// a standard serializer (Newtonsoft) round-trips it through that constructor without attributes. The
    /// named factories below are static methods, not constructors, so they never interfere with that.
    /// </remarks>
    public readonly struct ProjectionPlane : IEquatable<ProjectionPlane>
    {
        /// <summary>The dropped axis. See the axis-mapping table in the type remarks.</summary>
        public PlaneAxis FlattenedAxis { get; }

        /// <summary>Coordinate on the flattened axis, in 1/16-block units (world × 16).</summary>
        public int PlaneOffset { get; }

        public ProjectionPlane(PlaneAxis flattenedAxis, int planeOffset)
        {
            FlattenedAxis = flattenedAxis;
            PlaneOffset = planeOffset;
        }

        /// <summary>
        /// A sensible default plane (horizontal, at offset 0). Preferred over <c>default(ProjectionPlane)</c>,
        /// whose zero <see cref="PlaneAxis"/> would be the east–west wall rather than the more intuitive ground.
        /// </summary>
        public static ProjectionPlane Default => Horizontal(0);

        /// <summary>Horizontal ground plane (Y flattened) — a footprint. Keeps X and Z.</summary>
        public static ProjectionPlane Horizontal(int planeOffset) => new ProjectionPlane(PlaneAxis.Y, planeOffset);

        /// <summary>Vertical wall facing north–south (Z flattened) — an upright silhouette. Keeps X and Y.</summary>
        public static ProjectionPlane VerticalNorthSouth(int planeOffset) => new ProjectionPlane(PlaneAxis.Z, planeOffset);

        /// <summary>Vertical wall facing east–west (X flattened). Keeps Z and Y.</summary>
        public static ProjectionPlane VerticalEastWest(int planeOffset) => new ProjectionPlane(PlaneAxis.X, planeOffset);

        public bool Equals(ProjectionPlane other) =>
            FlattenedAxis == other.FlattenedAxis && PlaneOffset == other.PlaneOffset;

        public override bool Equals(object obj) => obj is ProjectionPlane other && Equals(other);

        public override int GetHashCode() => HashCode.Combine((int)FlattenedAxis, PlaneOffset);

        public static bool operator ==(ProjectionPlane a, ProjectionPlane b) => a.Equals(b);

        public static bool operator !=(ProjectionPlane a, ProjectionPlane b) => !a.Equals(b);

        public override string ToString() => $"ProjectionPlane(flatten {FlattenedAxis} @ {PlaneOffset})";
    }
}
