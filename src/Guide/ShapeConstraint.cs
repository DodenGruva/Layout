namespace Layout.Guide
{
    /// <summary>
    /// The optional constraint modifier applied to a shape primitive — the settled primitives+modifiers
    /// model (Session 8): a "half-circle" is an Arch under SemiCircle, a "circle" is an Ellipse under
    /// Circle. PINNED VALUES: persisted in saves and sent in create requests — append only, never
    /// renumber (same rule as <see cref="GuideShapeType"/>).
    ///
    /// ABSORB-OR-BREAK (the interaction contract): a grab the constraint can absorb, it absorbs (dragging
    /// a half-circle's foot re-derives the semicircle; dragging a circle's diameter anchors resizes it).
    /// A grab it cannot absorb BREAKS it — the constraint is cleared and the shape demotes to its free
    /// parent (half-circle → arch, with interior points materialised on-curve so the break is visually
    /// seamless; circle → ellipse, losslessly). Locking is then the ONLY thing that pins geometry.
    /// </summary>
    public enum ShapeConstraint
    {
        None = 0,

        /// <summary>Arch constrained to a perfect half-circle over its two feet (true circular sampler).</summary>
        SemiCircle = 1,

        /// <summary>Ellipse constrained to equal axes; the minor handle is derived from the diameter.</summary>
        Circle = 2,

        /// <summary>Triangle with a 90° angle at the FIRST clicked anchor; the apex slides on the
        /// perpendicular at that anchor.</summary>
        Right = 3,

        /// <summary>Triangle with the apex fully derived from the base (√3/2·|AB| on the bisector);
        /// dragging the apex breaks to a free triangle (the circle→ellipse pattern).</summary>
        Equilateral = 4,

        /// <summary>Triangle whose apex slides along the base's perpendicular bisector.</summary>
        Isosceles = 5,

        /// <summary>Rectangle with equal sides: the derived corners use the dominant diagonal component.</summary>
        Square = 6
    }
}
