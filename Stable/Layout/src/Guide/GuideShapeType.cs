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
        Ellipse = 1

        // Reserved for future shapes — append only, never renumber:
        // Line     = 2,
        // Dome     = 3,
        // Cylinder = 4,
        // Roof     = 5,
        // Tunnel   = 6,
    }
}
