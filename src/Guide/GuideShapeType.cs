namespace Layout.Guide
{
    /// <summary>
    /// Identifies which shape generator backs a guide.
    /// </summary>
    /// <remarks>
    /// These integer values are part of the on-disk and on-wire contract. They are
    /// written into persisted <see cref="GuideData"/> (as JSON) and transmitted in
    /// network packets. Once shipped, a value must NEVER be reordered, reused, or
    /// removed — doing so would silently rebind every saved guide to the wrong shape.
    /// New shapes are only ever appended with a fresh explicit value.
    ///
    /// Arch is value 0 deliberately: it is the only shape that currently exists, so a
    /// JSON record that is missing the field (defaulting to 0) still resolves to a
    /// valid shape. Schema-level migrations are handled by GuideData.DataVersion, not here.
    /// </remarks>
    public enum GuideShapeType
    {
        Arch = 0,

        /// <summary>Closed ellipse in an axis-aligned plane (Session 8). Circle = this + Circle constraint.</summary>
        Ellipse = 1,

        /// <summary>Straight segment between two anchors (Session 9). No interior points, no fill.</summary>
        Line = 2,

        /// <summary>
        /// Triangle (Session 9): two clicked base anchors + a real apex vertex born at the equilateral
        /// position. Free = scalene; Right/Equilateral/Isosceles are constraints on the apex.
        /// </summary>
        Triangle = 3,

        /// <summary>
        /// Rectangle (Session 9): the two clicks are its DIAGONAL corners (stored); the other two corners
        /// derive in the intrinsic plane. Square = this + Square constraint.
        /// </summary>
        Rectangle = 4,

        /// <summary>
        /// Regular polygon (Session 11): the two clicks span the shape along an axis of symmetry — the
        /// first click is a VERTEX, the second the point of the perimeter diametrically opposite it (a
        /// vertex for even side counts, the far edge's midpoint for odd). Side count lives in
        /// <see cref="GuideData.Sides"/>.
        /// </summary>
        Polygon = 5,

        /// <summary>
        /// Free-Shape (Session 11, 0.1.15): an irregular polyline — every draft click chains another
        /// straight segment; clicking the LAST placed corner finishes it open, clicking the FIRST closes
        /// it into a loop (<see cref="GuideData.IsClosed"/>). All clicked corners are anchors.
        /// </summary>
        FreeShape = 6,

        /// <summary>
        /// Sphere (Session 11, 0.1.20 — the FIRST 3D volume): the two clicks are opposite ends of the
        /// ball (a diameter, the circle gesture in 3D). Hollow = the one-voxel shell; Filled = the solid
        /// ball. Always Volumetric; Surface and Divisions don't apply (true of every volume below too).
        /// </summary>
        Sphere = 7,

        /// <summary>
        /// Dome (0.1.21): a half-sphere over the two-click base diameter, rising out of the clicked
        /// plane (SHIFT at placement inverts it into a bowl). Stores the two base anchors plus the apex
        /// (derived, side-remembering). Hollow = the curved shell, open across the base.
        /// </summary>
        Dome = 8,

        /// <summary>
        /// Cylinder (0.1.21): three clicks — base diameter, then the height click (projected onto the
        /// base's axis). Stores the two base anchors plus the height point. Hollow = the open tube.
        /// </summary>
        Cylinder = 9,

        /// <summary>
        /// Cone (0.1.21): three clicks — base diameter, then the tip click (projected onto the axis).
        /// Stores the two base anchors plus the tip. Hollow = the sloped shell, open across the base.
        /// </summary>
        Cone = 10,

        /// <summary>
        /// Box (0.1.21): three clicks — two DIAGONAL corners of the base rectangle (the rectangle
        /// gesture) then the height click; every side may differ. Stores the two base corners plus the
        /// lid point over the base centre. Hollow = all six faces as a one-voxel shell.
        /// </summary>
        Box = 11

        // Reserved for future shapes — append only, never renumber: Roof, Tunnel ...
    }

    /// <summary>Shared classifications over the shape catalog (0.1.21).</summary>
    public static class GuideShapeTypes
    {
        /// <summary>
        /// True for the 3D volumes (Sphere/Dome/Cylinder/Cone/Box): always Volumetric (Surface projection
        /// is meaningless and gated out everywhere), and Divisions don't apply. Explicit switch — never
        /// infer from enum ordering, since future 2D shapes may be appended after the volumes.
        /// </summary>
        public static bool IsVolume(GuideShapeType t) => t switch
        {
            GuideShapeType.Sphere or GuideShapeType.Dome or GuideShapeType.Cylinder
                or GuideShapeType.Cone or GuideShapeType.Box => true,
            _ => false
        };
    }
}
