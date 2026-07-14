using System;
using Vintagestory.API.MathTools;

namespace Layout.Guide
{
    /// <summary>
    /// A single point in a guide's Catmull-Rom spine.
    /// </summary>
    /// <remarks>
    /// A mutable data record. It is part of <see cref="GuideData"/> and therefore both
    /// persisted to the world save (as JSON) and — via the network DTOs it is mapped to
    /// (Layout.Network, Module 4) — synced to clients, so it is a plain POCO with a public
    /// get/set surface any standard serializer can round-trip. Serializer configuration for
    /// the JSON save path (e.g. a Vec3d converter, if one proves necessary) is owned by
    /// GuideManager, not here; the wire form is the protobuf DTOs in Layout.Network, which
    /// this type is converted to/from rather than being sent directly.
    ///
    /// OWNERSHIP — a ControlPoint exclusively owns its <see cref="WorldPosition"/> Vec3d.
    /// Vec3d is a MUTABLE REFERENCE TYPE in Vintage Story, so sharing a single instance
    /// between two ControlPoints would alias them: moving one would silently move the other,
    /// and an undo snapshot would be corrupted the instant the live point moved. Every
    /// constructor here DEEP-COPIES the incoming Vec3d, and <see cref="SetPosition"/> mutates
    /// the owned instance in place rather than swapping in a foreign reference. This is the
    /// core correctness guarantee of the class — the entire undo system depends on it.
    ///
    /// ROLE FLAGS are plain data. Mapping these roles to a render colour
    /// (<see cref="VoxelRenderType"/>), including any precedence when several apply at once,
    /// is ArchShape's responsibility, not this class's. Combinations are assumed valid as
    /// produced by the shape generator; this class does not police them.
    /// </remarks>
    public class ControlPoint
    {
        /// <summary>World-space position. Always non-null and exclusively owned (see remarks).</summary>
        public Vec3d WorldPosition { get; set; }

        /// <summary>Locked as a constraint. Rendered red. Orthogonal to the other roles.</summary>
        public bool IsLocked { get; set; }

        /// <summary>A tangent-only phantom point (P0 / P4). Exists for the math; never rendered.</summary>
        public bool IsPhantom { get; set; }

        /// <summary>A start/end anchor (P1 / P3). Rendered blue.</summary>
        public bool IsAnchor { get; set; }

        /// <summary>The apex / primary point (P2). Rendered green.</summary>
        public bool IsPrimary { get; set; }

        /// <summary>
        /// A lock-in-place marker that initially constrains the existing curve without becoming a spline
        /// knot. This prevents the act of locking from reparameterizing and shifting a Catmull-Rom arch.
        /// It is promoted to a regular knot only when the player later actively reshapes that guide.
        /// </summary>
        public bool IsLockMarker { get; set; }

        /// <summary>
        /// Parameterless constructor for deserialization. Initializes a non-null zero position
        /// so the WorldPosition getter never returns null before or during deserialization.
        /// </summary>
        public ControlPoint()
        {
            WorldPosition = new Vec3d();
        }

        /// <summary>
        /// Primary construction path used by shape generators. The supplied position is
        /// deep-copied; the new ControlPoint retains no reference to <paramref name="worldPosition"/>.
        /// </summary>
        public ControlPoint(
            Vec3d worldPosition,
            bool isLocked = false,
            bool isPhantom = false,
            bool isAnchor = false,
            bool isPrimary = false,
            bool isLockMarker = false)
        {
            WorldPosition = worldPosition != null
                ? new Vec3d(worldPosition.X, worldPosition.Y, worldPosition.Z)
                : new Vec3d();
            IsLocked = isLocked;
            IsPhantom = isPhantom;
            IsAnchor = isAnchor;
            IsPrimary = isPrimary;
            IsLockMarker = isLockMarker;
        }

        /// <summary>
        /// Copy constructor for undo snapshots. Produces a fully independent ControlPoint,
        /// deep-copying WorldPosition so the snapshot is immune to later edits of the original.
        /// </summary>
        public ControlPoint(ControlPoint other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            WorldPosition = other.WorldPosition != null
                ? new Vec3d(other.WorldPosition.X, other.WorldPosition.Y, other.WorldPosition.Z)
                : new Vec3d();
            IsLocked = other.IsLocked;
            IsPhantom = other.IsPhantom;
            IsAnchor = other.IsAnchor;
            IsPrimary = other.IsPrimary;
            IsLockMarker = other.IsLockMarker;
        }

        /// <summary>Returns a fully independent deep copy. Equivalent to the copy constructor.</summary>
        public ControlPoint Clone() => new ControlPoint(this);

        /// <summary>
        /// Moves the point by mutating the owned WorldPosition in place — no allocation and no
        /// risk of aliasing a foreign Vec3d. Preferred mutation path for game logic such as
        /// ArchShape.MoveControlPoint.
        /// </summary>
        public void SetPosition(double x, double y, double z)
        {
            WorldPosition.X = x;
            WorldPosition.Y = y;
            WorldPosition.Z = z;
        }

        public override string ToString()
        {
            string roles =
                (IsPhantom ? "Phantom " : "") +
                (IsAnchor ? "Anchor " : "") +
                (IsPrimary ? "Primary " : "") +
                (IsLocked ? "Locked " : "");
            if (roles.Length == 0) roles = "Body ";
            return $"ControlPoint({WorldPosition.X:0.###}, {WorldPosition.Y:0.###}, {WorldPosition.Z:0.###}) [{roles.TrimEnd()}]";
        }
    }
}
