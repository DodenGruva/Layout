using System;
using System.Collections.Generic;

namespace Layout.Guide
{
    /// <summary>
    /// The canonical, persistent record for a single guide. Server-authoritative (owned by
    /// GuideManager), serialized to the world save as JSON, and mapped to the network DTOs in
    /// Layout.Network (Module 4) to sync to clients.
    /// </summary>
    /// <remarks>
    /// NEVER STORES BAKED VOXELS. A guide is defined entirely by its control points plus a handful of
    /// settings; the voxel set is always derived on demand by the shape layer from those inputs. Caching
    /// voxels here would bloat the save, go stale on every edit, and duplicate the shape's authority over
    /// geometry. The voxel cap is therefore enforced by counting via the shape, never by reading a field.
    ///
    /// SHARED CONTROL-POINT LIST (the central binding). <see cref="ControlPoints"/> is the SAME
    /// <see cref="List{T}"/> instance the guide's shape (an <c>ArchShape</c>) operates on — adopted by
    /// reference, never copied. Edits made through the shape's methods mutate this list in place and are
    /// immediately visible here, and vice versa, with no copy/sync step. Two creation orders both end at
    /// one shared instance:
    ///   • Create-from-scratch: a shape builds the spine list, then <see cref="Create"/> adopts that exact
    ///     list (pass <c>arch.ControlPoints</c>).
    ///   • Load-from-save: this record is deserialized with its list, then a shape is constructed over that
    ///     same list and <c>RecalculatePhantomPoints</c> is called to restore the phantoms.
    /// The one deliberate exception is <see cref="DeepClone"/>, which produces an INDEPENDENT list for undo
    /// snapshots (see that method).
    ///
    /// POCO / SERIALIZATION. Plain public get/set surface so any standard serializer round-trips it on the
    /// JSON save path. Serializer configuration — most notably a <c>Vec3d</c> converter for the control
    /// points, if one proves necessary — is owned by GuideManager, not this class. For network transport the
    /// record is converted to/from the protobuf DTOs in Layout.Network (Module 4) rather than being sent
    /// directly. Enums that cross the wire/disk (<see cref="GuideShapeType"/>, <see cref="ProjectionMode"/>,
    /// <see cref="PlaneAxis"/>) use pinned values; <see cref="DataVersion"/> drives schema migration on load.
    ///
    /// LAYER PURITY. This type depends only on the other pure-data types in this namespace — it does not
    /// reference the Shapes layer. Building a guide from a raw start/end pair (which needs the spine math
    /// that lives in <c>ArchShape</c>) is done one layer up, in GuideManager; here, <see cref="Create"/>
    /// only adopts an already-built control-point list.
    /// </remarks>
    public class GuideData
    {
        /// <summary>
        /// Current schema version written by <see cref="Create"/>. Bump when the persisted shape of a
        /// guide changes so the load path can migrate older records. Version 2 introduced
        /// <see cref="Projection"/>, <see cref="Plane"/>, and <see cref="IsFilled"/>; a record with
        /// <see cref="DataVersion"/> below this (including the implicit 0 of pre-versioned JSON) is treated
        /// as Volumetric / default-plane / hollow when loaded. Version 3 (Module 7) added the nullable
        /// <see cref="CreatorUid"/>; a record without it simply loads with a null creator (it counts toward
        /// no player's guide-count cap) — default-driven migration, no explicit migration step needed.
        /// </summary>
        /// Version 4 (Session 8) added <see cref="Constraint"/> and <see cref="ShapePlaneAxis"/>; older
        /// records load with None / Y via defaults — default-driven migration again.
        public const int CurrentDataVersion = 4;

        /// <summary>The voxel edge lengths a guide may use, in 1/16-block units (1 → 1/16 block, 16 → 1 block).</summary>
        public static readonly int[] ValidVoxelScales = { 1, 2, 4, 8, 16 };

        /// <summary>Unique identity, assigned by the server on creation. Stable for the guide's lifetime.</summary>
        public Guid Id { get; set; }

        /// <summary>Which shape primitive backs this guide (pinned enum).</summary>
        public GuideShapeType ShapeType { get; set; }

        /// <summary>Constraint modifier on the primitive (pinned enum). None = the free parent shape.</summary>
        public ShapeConstraint Constraint { get; set; }

        /// <summary>
        /// The INTRINSIC geometry plane of planar closed shapes (the ellipse family): the axis normal to
        /// the plane the shape is drawn in, captured from the first click's block face at creation. Not to
        /// be confused with <see cref="Plane"/>, which is the user-set Surface PROJECTION plane. Carried
        /// (defaulted to Y) but ignored by shapes that don't need it (the arch family derives its own).
        /// </summary>
        public PlaneAxis ShapePlaneAxis { get; set; }

        /// <summary>
        /// UID of the player who created this guide, or null when unknown (guides from saves that predate the
        /// field, or created without an acting player). PURELY BOOKKEEPING for the server-config per-player
        /// guide-count cap — it does NOT establish ownership and grants no permissions; the settled "world-shared,
        /// no ownership" model is unchanged. Persisted with the save; deliberately NOT sent over the wire
        /// (clients have no use for it).
        /// </summary>
        public string CreatorUid { get; set; }

        /// <summary>
        /// The guide's spine. The SAME list instance the shape mutates (see type remarks). Never null.
        /// </summary>
        public List<ControlPoint> ControlPoints { get; set; }

        /// <summary>Voxel edge length in 1/16-block units; one of <see cref="ValidVoxelScales"/>.</summary>
        public int VoxelScale { get; set; }

        /// <summary>When true, the guide renders as anchors-only at reduced opacity rather than its full body.</summary>
        public bool IsHidden { get; set; }

        /// <summary>Volumetric (cubes) or Surface (flat decal). Per-guide and toggleable after creation.</summary>
        public ProjectionMode Projection { get; set; }

        /// <summary>The plane a Surface guide projects onto. Carried (defaulted) but ignored when Volumetric.</summary>
        public ProjectionPlane Plane { get; set; }

        /// <summary>Hollow curve (false) vs. filled region (true). Per-guide and toggleable after creation.</summary>
        public bool IsFilled { get; set; }

        /// <summary>Schema version of this record; see <see cref="CurrentDataVersion"/>.</summary>
        public int DataVersion { get; set; }

        /// <summary>
        /// Parameterless constructor for deserialization. Initialises non-null, sensible defaults so a
        /// freshly constructed (or partially populated) record never trips a null reference: an empty
        /// control-point list, Volumetric projection, a default horizontal plane, and not filled.
        /// <see cref="DataVersion"/> is left at 0 so a loaded record that predates versioning is recognised
        /// as needing migration; <see cref="Create"/> stamps the current version for new guides.
        /// </summary>
        public GuideData()
        {
            Id = Guid.Empty;
            ShapeType = GuideShapeType.Arch;
            Constraint = ShapeConstraint.None;
            ShapePlaneAxis = PlaneAxis.Y;
            ControlPoints = new List<ControlPoint>();
            VoxelScale = 1;
            IsHidden = false;
            Projection = ProjectionMode.Volumetric;
            Plane = ProjectionPlane.Default;
            IsFilled = false;
            DataVersion = 0;
        }

        /// <summary>
        /// Builds a new guide record around an already-constructed control-point list, ADOPTING that list
        /// BY REFERENCE (no copy). Pass the shape's own list — e.g. <c>GuideData.Create(GuideShapeType.Arch,
        /// arch.ControlPoints, scale, …)</c> — so the record and the shape share one instance, which is the
        /// whole binding contract (see type remarks). A fresh <see cref="Id"/> is assigned and
        /// <see cref="DataVersion"/> is stamped to <see cref="CurrentDataVersion"/>.
        /// </summary>
        /// <param name="shapeType">Which shape backs the guide.</param>
        /// <param name="controlPoints">The shape's live spine list. Adopted by reference; must not be null.</param>
        /// <param name="voxelScale">One of <see cref="ValidVoxelScales"/>.</param>
        /// <param name="projection">Projection mode; defaults to Volumetric.</param>
        /// <param name="plane">Projection plane; defaults to <see cref="ProjectionPlane.Default"/> when omitted.</param>
        /// <param name="isFilled">Filled vs hollow; defaults to hollow.</param>
        /// <exception cref="ArgumentNullException"><paramref name="controlPoints"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="voxelScale"/> is not a valid scale.</exception>
        public static GuideData Create(
            GuideShapeType shapeType,
            List<ControlPoint> controlPoints,
            int voxelScale,
            ProjectionMode projection = ProjectionMode.Volumetric,
            ProjectionPlane? plane = null,
            bool isFilled = false,
            ShapeConstraint constraint = ShapeConstraint.None,
            PlaneAxis shapePlaneAxis = PlaneAxis.Y)
        {
            if (controlPoints == null) throw new ArgumentNullException(nameof(controlPoints));
            if (!IsValidVoxelScale(voxelScale))
                throw new ArgumentOutOfRangeException(
                    nameof(voxelScale), voxelScale, "Voxel scale must be one of 1, 2, 4, 8, 16.");

            return new GuideData
            {
                Id = Guid.NewGuid(),
                ShapeType = shapeType,
                Constraint = constraint,
                ShapePlaneAxis = shapePlaneAxis,
                ControlPoints = controlPoints,                 // adopted by reference — shared with the shape
                VoxelScale = voxelScale,
                IsHidden = false,
                Projection = projection,
                Plane = plane ?? ProjectionPlane.Default,
                IsFilled = isFilled,
                DataVersion = CurrentDataVersion
            };
        }

        /// <summary>
        /// Projects this record's render-relevant fields into a <see cref="GuideRenderSettings"/> bundle —
        /// the parameter object the shape layer's voxel-generation methods consume — so callers (renderer,
        /// cap checks) never hand the shape a <see cref="GuideData"/> directly.
        /// </summary>
        public GuideRenderSettings GetRenderSettings() =>
            new GuideRenderSettings(VoxelScale, Projection, Plane, IsFilled);

        /// <summary>
        /// Produces a fully INDEPENDENT deep copy for undo snapshots: a new control-point list whose
        /// elements are themselves deep-copied (each <see cref="ControlPoint"/>'s <c>Vec3d</c> is cloned),
        /// so the snapshot can never be corrupted by later edits to the live guide. This is the deliberate
        /// exception to the shared-list binding — a snapshot must NOT alias the live list. <see cref="Id"/>
        /// and all settings are preserved exactly, so the clone can faithfully recreate the same guide
        /// (e.g. a delete/undo restoring it).
        /// </summary>
        public GuideData DeepClone()
        {
            var pointsCopy = new List<ControlPoint>(ControlPoints.Count);
            foreach (var cp in ControlPoints)
                pointsCopy.Add(cp == null ? new ControlPoint() : cp.Clone());

            return new GuideData
            {
                Id = Id,
                ShapeType = ShapeType,
                Constraint = Constraint,
                ShapePlaneAxis = ShapePlaneAxis,
                ControlPoints = pointsCopy,
                VoxelScale = VoxelScale,
                IsHidden = IsHidden,
                Projection = Projection,
                Plane = Plane,
                IsFilled = IsFilled,
                CreatorUid = CreatorUid,
                DataVersion = DataVersion
            };
        }

        /// <summary>True if <paramref name="scale"/> is one of the permitted voxel scales.</summary>
        public static bool IsValidVoxelScale(int scale)
        {
            for (int i = 0; i < ValidVoxelScales.Length; i++)
                if (ValidVoxelScales[i] == scale) return true;
            return false;
        }

        public override string ToString() =>
            $"GuideData({ShapeType}, {Id}, points={ControlPoints?.Count ?? 0}, scale={VoxelScale}, " +
            $"{Projection}, filled={IsFilled}, hidden={IsHidden}, v{DataVersion})";
    }
}
