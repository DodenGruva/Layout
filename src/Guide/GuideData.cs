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
        /// Version 4 (Session 8) added <see cref="Constraint"/> and <see cref="ShapePlaneAxis"/>; version 5
        /// (Session 9) added <see cref="Divisions"/>. Older records load via defaults (None / Y / 0) —
        /// default-driven migration again. Version 6 (Session 11) added <see cref="Sides"/> (polygon side
        /// count) and the as-placed snapshot (<see cref="OriginalControlPoints"/> +
        /// <see cref="OriginalConstraint"/>) behind SHIFT spring-back; older records load with a null
        /// snapshot (spring-back reports "no original recorded" for them) — default-driven migration.
        /// Version 7 (Session 11, 0.1.15) added <see cref="IsClosed"/> for the Free-Shape (default false —
        /// harmless on every earlier shape). Version 8 (0.1.51) added the default-false
        /// <see cref="ControlPoint.IsLockMarker"/> role used by non-deforming Arch lock markers. Version 9
        /// added <see cref="FlatSideAligned"/> for polygon-based guides (default false preserves old guides).
        /// Version 10 stores lightweight display dimensions/count so merely hovering a large guide never
        /// has to regenerate its voxel shell. Version 11 added persistent Shell/Wireframe form. Version 12
        /// adds friendly creator and last-sculptor attribution for the HUD; older records simply display
        /// unknown attribution until a player next changes them. Version 13 (v0.4.15) re-gestured the
        /// Rectangle and Box, which changed how many control points they store — three and four instead of
        /// two and three. No migration STEP is needed: both shapes read the old encoding in place and
        /// reproduce it to the voxel (see RectangleShape's remarks). The bump exists so the change is
        /// visible, and because a record written here WOULD be misread by an older build, which would take
        /// the new edge anchor for the old diagonal corner.
        /// Version 14 makes the Arch family consume its stored <see cref="ShapePlaneAxis"/>. The
        /// <see cref="ArchUsesShapePlaneAxis"/> bit preserves older arches without reinterpreting their
        /// stored, possibly hand-edited control points.
        public const int CurrentDataVersion = 14;

        /// <summary>The voxel edge lengths a guide may use, in 1/16-block units (1 → 1/16 block, 16 → 1 block).</summary>
        public static readonly int[] ValidVoxelScales = { 1, 2, 4, 8, 16 };

        /// <summary>Unique identity, assigned by the server on creation. Stable for the guide's lifetime.</summary>
        public Guid Id { get; set; }

        /// <summary>Which shape primitive backs this guide (pinned enum).</summary>
        public GuideShapeType ShapeType { get; set; }

        /// <summary>Constraint modifier on the primitive (pinned enum). None = the free parent shape.</summary>
        public ShapeConstraint Constraint { get; set; }

        /// <summary>
        /// Session 9: divide the guide into this many equal parts VISUALLY — voxels at the part boundaries
        /// recolor (renderer-side, by arc length). Purely a visual reference: never affects geometry,
        /// counts, or caps. 0 or 1 = no divisions.
        /// </summary>
        public int Divisions { get; set; }

        /// <summary>
        /// The regular polygon side count. Meaningful for the 2D Polygon and both polygonal prism volumes;
        /// carried as 0 but ignored by every other shape.
        /// </summary>
        public int Sides { get; set; }

        /// <summary>Whether a polygon-based guide aligns an edge midpoint, rather than a vertex, to its
        /// two-click placement axis. Ignored by non-polygon shapes.</summary>
        public bool FlatSideAligned { get; set; }

        /// <summary>
        /// Session 11 (0.1.15): whether a Free-Shape loops back to its first corner (drafted by clicking
        /// the first anchor). Fixed at creation. Carried (false) but ignored by every other shape.
        /// </summary>
        public bool IsClosed { get; set; }

        /// <summary>
        /// Session 11 (SHIFT spring-back): a deep, immutable snapshot of the control points exactly as the
        /// guide was FIRST PLACED, so hand distortions can be undone back to the pristine geometry at any
        /// later time. Persisted with the save; deliberately NOT sent over the wire (the server executes
        /// spring-back; clients only request it). Null on guides that predate 0.1.14 — spring-back is
        /// simply unavailable for those.
        /// </summary>
        public List<ControlPoint> OriginalControlPoints { get; set; }

        /// <summary>The constraint the guide was first placed with, restored together with
        /// <see cref="OriginalControlPoints"/> on spring-back (a broken circle springs back to a circle).</summary>
        public ShapeConstraint OriginalConstraint { get; set; }

        /// <summary>
        /// The INTRINSIC geometry plane of planar shapes: the axis normal to
        /// the plane the shape is drawn in, captured from the first click's block face at creation. Not to
        /// be confused with <see cref="Plane"/>, which is the user-set Surface PROJECTION plane. Carried
        /// (defaulted to Y) but ignored by shapes that don't need it.
        /// </summary>
        public PlaneAxis ShapePlaneAxis { get; set; }

        /// <summary>
        /// Version 14 compatibility bit for the Arch family. New arches use <see cref="ShapePlaneAxis"/>
        /// for their apex, foot tangents and half-circle arc. False preserves the world-vertical geometry
        /// of existing arches and guides received from an older server. Ignored by every other shape.
        /// </summary>
        public bool ArchUsesShapePlaneAxis { get; set; }

        /// <summary>
        /// UID of the player who created this guide, or null when unknown (guides from saves that predate the
        /// field, or created without an acting player). PURELY BOOKKEEPING for the server-config per-player
        /// guide-count cap — it does NOT establish ownership and grants no permissions; the settled "world-shared,
        /// no ownership" model is unchanged. Persisted with the save; deliberately NOT sent over the wire
        /// (clients have no use for it).
        /// </summary>
        public string CreatorUid { get; set; }

        /// <summary>Friendly player-name snapshot captured when the guide was created. Unlike the UID this
        /// is display-only, crosses the wire, and grants no ownership or permissions.</summary>
        public string CreatorName { get; set; }

        /// <summary>UID of the player responsible for the most recent committed, persistent visible change.
        /// Server bookkeeping only; the friendly name is what clients receive and display.</summary>
        public string LastSculptorUid { get; set; }

        /// <summary>Friendly player-name snapshot for the guide's most recent visible mutation.</summary>
        public string LastSculptorName { get; set; }

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

        /// <summary>For 3D volumes, render/count the canonical structural wireframe instead of the shell.</summary>
        public bool IsWireframe { get; set; }

        /// <summary>Human-readable guide name derived from its cached whole-block dimensions.</summary>
        public string DisplayName { get; set; }

        /// <summary>Last authoritative voxel count, stored for hover/cap display without regeneration.</summary>
        public int CachedVoxelCount { get; set; }

        /// <summary>Cached lightweight dimensions in selected-scale voxels and whole blocks.</summary>
        public int CachedVoxelWidth { get; set; }
        public int CachedVoxelHeight { get; set; }
        public int CachedBlockWidth { get; set; }
        public int CachedBlockHeight { get; set; }

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
            ArchUsesShapePlaneAxis = false;
            Divisions = 0;
            Sides = 0;
            FlatSideAligned = false;
            IsClosed = false;
            OriginalControlPoints = null;      // null = no as-placed snapshot (pre-0.1.14 records)
            OriginalConstraint = ShapeConstraint.None;
            ControlPoints = new List<ControlPoint>();
            VoxelScale = 1;
            IsHidden = false;
            Projection = ProjectionMode.Volumetric;
            Plane = ProjectionPlane.Default;
            IsFilled = false;
            IsWireframe = false;
            DisplayName = null;
            CachedVoxelCount = 0;
            CreatorName = null;
            LastSculptorUid = null;
            LastSculptorName = null;
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
            PlaneAxis shapePlaneAxis = PlaneAxis.Y,
            int divisions = 0,
            int sides = 0,
            bool isClosed = false,
            bool flatSideAligned = false,
            bool isWireframe = false)
        {
            if (controlPoints == null) throw new ArgumentNullException(nameof(controlPoints));
            if (!IsValidVoxelScale(voxelScale))
                throw new ArgumentOutOfRangeException(
                    nameof(voxelScale), voxelScale, "Voxel scale must be one of 1, 2, 4, 8, 16.");

            // The as-placed snapshot for SHIFT spring-back: taken at the ONE moment a guide is born, from
            // the fully-derived spine (the caller applies any third-click apex BEFORE building the record).
            var original = new List<ControlPoint>(controlPoints.Count);
            foreach (var cp in controlPoints)
                original.Add(cp == null ? new ControlPoint() : cp.Clone());

            return new GuideData
            {
                Id = Guid.NewGuid(),
                ShapeType = shapeType,
                Constraint = constraint,
                ShapePlaneAxis = shapePlaneAxis,
                ArchUsesShapePlaneAxis = shapeType == GuideShapeType.Arch,
                Divisions = divisions,
                Sides = sides,
                FlatSideAligned = flatSideAligned,
                IsClosed = isClosed,
                OriginalControlPoints = original,
                OriginalConstraint = constraint,
                ControlPoints = controlPoints,                 // adopted by reference — shared with the shape
                VoxelScale = voxelScale,
                IsHidden = false,
                Projection = projection,
                Plane = plane ?? ProjectionPlane.Default,
                IsFilled = isFilled,
                IsWireframe = isWireframe,
                DataVersion = CurrentDataVersion
            };
        }

        /// <summary>
        /// Projects this record's render-relevant fields into a <see cref="GuideRenderSettings"/> bundle —
        /// the parameter object the shape layer's voxel-generation methods consume — so callers (renderer,
        /// cap checks) never hand the shape a <see cref="GuideData"/> directly.
        /// </summary>
        public GuideRenderSettings GetRenderSettings() =>
            new GuideRenderSettings(VoxelScale, Projection, Plane, IsFilled, wireframe: IsWireframe);

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

            List<ControlPoint> originalCopy = null;
            if (OriginalControlPoints != null)
            {
                originalCopy = new List<ControlPoint>(OriginalControlPoints.Count);
                foreach (var cp in OriginalControlPoints)
                    originalCopy.Add(cp == null ? new ControlPoint() : cp.Clone());
            }

            return new GuideData
            {
                Id = Id,
                ShapeType = ShapeType,
                Constraint = Constraint,
                ShapePlaneAxis = ShapePlaneAxis,
                ArchUsesShapePlaneAxis = ArchUsesShapePlaneAxis,
                Divisions = Divisions,
                Sides = Sides,
                FlatSideAligned = FlatSideAligned,
                IsClosed = IsClosed,
                OriginalControlPoints = originalCopy,
                OriginalConstraint = OriginalConstraint,
                ControlPoints = pointsCopy,
                VoxelScale = VoxelScale,
                IsHidden = IsHidden,
                Projection = Projection,
                Plane = Plane,
                IsFilled = IsFilled,
                IsWireframe = IsWireframe,
                DisplayName = DisplayName,
                CachedVoxelCount = CachedVoxelCount,
                CachedVoxelWidth = CachedVoxelWidth,
                CachedVoxelHeight = CachedVoxelHeight,
                CachedBlockWidth = CachedBlockWidth,
                CachedBlockHeight = CachedBlockHeight,
                CreatorUid = CreatorUid,
                CreatorName = CreatorName,
                LastSculptorUid = LastSculptorUid,
                LastSculptorName = LastSculptorName,
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
            $"{Projection}, filled={IsFilled}, wireframe={IsWireframe}, hidden={IsHidden}, v{DataVersion})";
    }
}
