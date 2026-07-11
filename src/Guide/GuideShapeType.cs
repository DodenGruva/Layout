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
        FreeShape = 6

        // Reserved for future shapes — append only, never renumber:
        // Dome, Cylinder, Roof, Tunnel ...
    }
}
