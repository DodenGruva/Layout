using System;

namespace Layout.Guide
{
    /// <summary>
    /// A small, self-contained bundle describing <em>how</em> to turn a shape into voxels: the voxel
    /// scale, the projection mode (and plane, for Surface), and whether the region is filled.
    /// </summary>
    /// <remarks>
    /// PURPOSE — this is the parameter object the shape layer's voxel-generation methods are widened to
    /// take so they never have to read <see cref="GuideData"/> directly (keeping the Shapes layer free of
    /// any dependency on guide records). Build one from a guide via <see cref="GuideData.GetRenderSettings"/>.
    ///
    /// CURRENT STATUS — the built math layer (Module 1) still exposes <c>GetVoxelPositions(int scale)</c> /
    /// <c>GetVoxelCount(int scale)</c>, the hollow-volumetric subset. This bundle is defined now (data
    /// model) ahead of that contained widening, which lands together with Surface mode and fill. Until
    /// then, only <see cref="Scale"/> is actually consumed; <see cref="Mode"/>, <see cref="Plane"/>, and
    /// <see cref="Filled"/> are carried for the forthcoming paths.
    ///
    /// VALUE TYPE — an immutable <c>readonly struct</c> with value semantics (like <see cref="VoxelPosition"/>
    /// and <see cref="ProjectionPlane"/>). It holds no reference state, so it can be passed and copied
    /// freely with no aliasing concern. It is transient: derived on demand from a guide, never persisted as
    /// part of <see cref="GuideData"/> (the guide stores the individual fields). It does travel on the wire
    /// in a create request; the exact wire encoding is a Network-layer (Module 4) decision.
    ///
    /// SERIALIZATION — single public constructor with parameter names matching the properties, so a
    /// standard serializer round-trips it through that constructor. Named factories are static, not
    /// constructors, so they do not interfere.
    /// </remarks>
    public readonly struct GuideRenderSettings : IEquatable<GuideRenderSettings>
    {
        /// <summary>Voxel edge length in 1/16-block units; one of 1, 2, 4, 8, 16.</summary>
        public int Scale { get; }

        /// <summary>Volumetric (cubes) or Surface (flat tiles on <see cref="Plane"/>).</summary>
        public ProjectionMode Mode { get; }

        /// <summary>The projection plane. Ignored when <see cref="Mode"/> is <see cref="ProjectionMode.Volumetric"/>.</summary>
        public ProjectionPlane Plane { get; }

        /// <summary>Whether the bounded region is filled (vs. a hollow curve).</summary>
        public bool Filled { get; }

        public GuideRenderSettings(int scale, ProjectionMode mode, ProjectionPlane plane, bool filled)
        {
            Scale = scale;
            Mode = mode;
            Plane = plane;
            Filled = filled;
        }

        /// <summary>Volumetric settings at the given scale. The plane is irrelevant and left at default.</summary>
        public static GuideRenderSettings Volumetric(int scale, bool filled = false) =>
            new GuideRenderSettings(scale, ProjectionMode.Volumetric, ProjectionPlane.Default, filled);

        /// <summary>Surface settings projecting onto <paramref name="plane"/> at the given scale.</summary>
        public static GuideRenderSettings Surface(int scale, ProjectionPlane plane, bool filled = false) =>
            new GuideRenderSettings(scale, ProjectionMode.Surface, plane, filled);

        public bool Equals(GuideRenderSettings other) =>
            Scale == other.Scale && Mode == other.Mode && Plane == other.Plane && Filled == other.Filled;

        public override bool Equals(object obj) => obj is GuideRenderSettings other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Scale, (int)Mode, Plane, Filled);

        public static bool operator ==(GuideRenderSettings a, GuideRenderSettings b) => a.Equals(b);

        public static bool operator !=(GuideRenderSettings a, GuideRenderSettings b) => !a.Equals(b);

        public override string ToString() =>
            $"GuideRenderSettings(scale {Scale}, {Mode}, {Plane}, filled={Filled})";
    }
}
