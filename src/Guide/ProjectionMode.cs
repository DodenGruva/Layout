namespace Layout.Guide
{
    /// <summary>
    /// How a guide is projected into space: as solid 3D cubes, or as a flat decal on a plane.
    /// </summary>
    /// <remarks>
    /// These integer values are part of the on-disk and on-wire contract. They are written into
    /// persisted <see cref="GuideData"/> (as JSON) and travel in network packets (a create request
    /// carries the chosen mode, and toggling it later sends a dedicated packet). Once shipped, a
    /// value must NEVER be reordered, reused, or removed — doing so would silently rebind every
    /// saved guide to the wrong projection. New modes are only ever appended with a fresh value.
    ///
    /// Volumetric is value 0 deliberately: it is the original (and default) behaviour, so a JSON
    /// record that predates this field — and therefore lacks it (defaulting to 0) — still resolves
    /// to the correct mode. Schema-level migration is handled by <see cref="GuideData.DataVersion"/>.
    /// </remarks>
    public enum ProjectionMode
    {
        /// <summary>Solid 3D voxel cubes filling the guide's geometry. The original behaviour.</summary>
        Volumetric = 0,

        /// <summary>
        /// A flat decal: the geometry is flattened onto an axis-aligned plane (see
        /// <see cref="ProjectionPlane"/>) and drawn hugging that plane rather than as full cubes.
        /// </summary>
        /// <remarks>
        /// PAPER-THIN SLABS, not tiles. The renderer flattens the voxel set onto the plane's air-side layer
        /// and emits very shallow cubes; the mesh builder's separate tile path exists but is dormant, and
        /// its <c>TODO(Surface)</c> records the intent to move flattening into the shape's own sampler one
        /// day. This summary said "thin tiles" until the 2026-08-01 sweep, naming the path that is NOT the
        /// one running.
        /// </remarks>
        Surface = 1

        // Reserved for future projection modes — append only, never renumber.
    }
}
