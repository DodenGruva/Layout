using System;

namespace Layout.Guide
{
    /// <summary>
    /// How a single guide voxel should be coloured when rendered.
    /// </summary>
    /// <remarks>
    /// This is a render-only classification. It is never serialized and never sent over
    /// the network: clients recompute it locally on every guide mesh rebuild by inspecting
    /// the authoritative control-point roles (anchor / primary / locked) that DO travel in
    /// <see cref="ControlPoint"/>. Because it never crosses a persistence or wire boundary,
    /// its underlying integer values carry no compatibility contract.
    /// </remarks>
    public enum VoxelRenderType
    {
        Normal,   // Yellow — ordinary curve body
        Locked,   // Red    — a locked (constraint) control point
        Primary,  // Green  — the apex / primary control point
        Anchor,   // Blue   — start / end anchor points
        Grabbed,  // White  — the point currently held during an active drag
        Division  // Magenta — Session 9: an equal-part boundary mark (purely visual; renderer-applied)
    }

    /// <summary>
    /// A single occupied cell in the voxel guide grid, together with how it should be coloured.
    /// </summary>
    /// <remarks>
    /// COORDINATE SPACE — X/Y/Z are integer coordinates in 1/16-block units, i.e.
    /// (world coordinate × 16) floored. This space is absolute and independent of the
    /// guide's VoxelScale: a given position denotes the same world location at every scale.
    ///
    /// The coordinate is the LOWER CORNER of the voxel cube (its minimum-X/Y/Z corner),
    /// matching the convention for building a cube mesh from a corner origin.
    ///
    /// The cube's EDGE LENGTH is NOT stored here — it is the guide's VoxelScale expressed in
    /// those same 1/16-block units (scale 1 → a 1/16-block cube, scale 16 → a full 1-block
    /// cube). Scale is always supplied separately by the caller (e.g. the mesh builder),
    /// never embedded in the position. Consequently every emitted position at scale N is a
    /// multiple of N on each axis.
    ///
    /// This is a readonly value type generated in large quantities (up to the per-guide
    /// voxel cap) and deduplicated through a HashSet, so it implements
    /// <see cref="IEquatable{T}"/> to avoid boxing in that hot path. Equality and hashing
    /// cover ALL fields, including <see cref="Type"/>. Coordinate-only deduplication (as the
    /// spline sampler performs) therefore depends on the caller emitting one uniform Type
    /// before deduplicating; the sampler does exactly that — every raw position is stamped
    /// Normal, then the shape assigns real types afterward on the already-unique set, so no
    /// two surviving voxels ever share a coordinate.
    /// </remarks>
    public readonly struct VoxelPosition : IEquatable<VoxelPosition>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;
        public readonly VoxelRenderType Type;

        public VoxelPosition(int x, int y, int z, VoxelRenderType type = VoxelRenderType.Normal)
        {
            X = x;
            Y = y;
            Z = z;
            Type = type;
        }

        /// <summary>
        /// Returns a copy of this position with a different render type. Used by shape
        /// generators to tag raw (Normal) sampler output without mutating the source set.
        /// </summary>
        public VoxelPosition WithType(VoxelRenderType type) => new VoxelPosition(X, Y, Z, type);

        public bool Equals(VoxelPosition other) =>
            X == other.X && Y == other.Y && Z == other.Z && Type == other.Type;

        public override bool Equals(object obj) => obj is VoxelPosition other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z, (int)Type);

        public static bool operator ==(VoxelPosition a, VoxelPosition b) => a.Equals(b);

        public static bool operator !=(VoxelPosition a, VoxelPosition b) => !a.Equals(b);

        public override string ToString() => $"Voxel({X}, {Y}, {Z}, {Type})";
    }
}
