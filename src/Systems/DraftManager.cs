using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;

namespace Layout.Systems
{
    /// <summary>
    /// What the Layout tool's clicks mean. Client-side only; never crosses the wire (safe to reorder).
    /// THREE modes (Session 10): in <b>Create</b>, the two mouse buttons build guides contextually —
    /// left-click drafts anchors / grabs points / grabs the body (implicit insert) / releases; right-click
    /// cancels or toggles a point's lock. <b>Edit</b> is the same guide-manipulation minus placement (empty
    /// clicks just deselect) — while in it, the GUI's setting rows act on the SELECTED guide instead of the
    /// tool defaults, so per-guide editing no longer needs a separate expanding panel section. <b>Delete</b>
    /// is the deliberately separated destructive mode. Order is Create · Edit · Delete (matches the mode-row
    /// tiles via <c>(int)Mode</c>).
    /// </summary>
    public enum ToolMode
    {
        Create,
        Edit,
        Delete
    }

    /// <summary>Outcome of trying to finish a placement draft on the second click.</summary>
    public enum DraftCompletionStatus
    {
        /// <summary>Within the per-guide cap; the create request may be sent.</summary>
        Ready,
        /// <summary>Would exceed the per-guide voxel cap; nothing should be sent.</summary>
        RejectedOverCap,
        /// <summary>There was no active draft to complete.</summary>
        NoActiveDraft
    }

    /// <summary>
    /// Result of <see cref="DraftManager.TryCompleteDraft"/>: whether the pending guide may be created, plus the
    /// two foot points (and, for a three-click triangle, the apex) the network layer needs to request it. On
    /// <see cref="DraftCompletionStatus.RejectedOverCap"/> the voxel count and cap are filled in for a HUD
    /// warning. The render settings are assembled separately by the caller (via
    /// <see cref="DraftManager.BuildRenderSettings"/>) at send time, so they reflect any settings the player
    /// changed mid-draft.
    /// </summary>
    public readonly struct DraftCompletion
    {
        public DraftCompletionStatus Status { get; }
        public Vec3d Start { get; }
        public Vec3d End { get; }
        public Vec3d Apex { get; }
        public int VoxelCount { get; }
        public int CapLimit { get; }

        public DraftCompletion(DraftCompletionStatus status, Vec3d start, Vec3d end, int voxelCount, int capLimit,
            Vec3d apex = null)
        {
            Status = status;
            Start = start;
            End = end;
            Apex = apex;
            VoxelCount = voxelCount;
            CapLimit = capLimit;
        }

        public bool IsReady => Status == DraftCompletionStatus.Ready;

        public override string ToString() => $"DraftCompletion({Status}, voxels={VoxelCount}, cap={CapLimit})";
    }

    /// <summary>
    /// Client-side state for the local player's Layout tool: the live tool settings (mode, scale, projection,
    /// plane override, fill, and the Edit-mode selected guide) plus the in-progress two-click placement draft.
    /// It also runs the client-side voxel pre-check that catches an obviously-too-big guide before anything is
    /// sent to the server.
    /// </summary>
    /// <remarks>
    /// CLIENT-ONLY, STATE-ONLY. This holds the local player's tool state; it sends no packets and reads no server
    /// state. The held item drives it (settings from the GUI, clicks drive the draft) and the network layer reads
    /// its data to send packets — mirroring how the server-side managers expose state/results and let the
    /// orchestration layer do the messaging. The server stays the sole authority: the pre-check here is a courtesy
    /// gate, never a substitute for server validation.
    ///
    /// SETTINGS ARE LIVE. The draft stores only the first-click start point; the render settings are read live when
    /// the guide is finalised. So if the player changes scale, projection, plane, or fill between the two clicks,
    /// the change applies to the guide in progress (and to the live preview) — which is what a player who just
    /// changed a setting mid-placement expects. The projection plane is resolved by the held item at send time
    /// (from the clicked face or the tool's override) and passed to <see cref="BuildRenderSettings"/>.
    ///
    /// VEC3D OWNERSHIP. Vec3d is a mutable reference type, so the draft DEEP-COPIES the start point it is given and
    /// the end point it is handed, and never aliases a caller's instance — the same rule the rest of the codebase
    /// follows.
    ///
    /// CAP PRE-CHECK. The check builds a throwaway shape (via <see cref="ShapeFactory"/>, honouring the
    /// selected shape, constraint, fill, and plane) from the two points — the same math the server runs — and counts voxels at the current scale against the per-guide cap only. The
    /// total-across-all-guides cap stays the server's job. The held item runs this for placement; other operations
    /// (rescale, etc.) rely on server validation and the cap-warning packet.
    /// </remarks>
    public class DraftManager
    {
        private int _perGuideVoxelCap;                  // mutable: re-seeded from the server's join-time cap sync

        // --- live tool state ---
        private int _scale = 1;                         // 1/16-block voxels by default — matches VS chisel resolution
        private ProjectionMode _projection = ProjectionMode.Volumetric;
        private PlaneAxis? _planeOverride;              // null => auto-select the plane from the clicked face
        private bool _filled;
        private int _divisions;
        private int _sides = Shapes.PolygonShape.DefaultSides;          // Session 11: polygon side count
        private GuideShapeType _shape = GuideShapeType.Arch;            // Session 8: shape catalog
        private ShapeConstraint _constraint = ShapeConstraint.None;     //   Arch / Arch+SemiCircle / Ellipse / Ellipse+Circle
        private ToolMode _mode = ToolMode.Create;
        private Guid? _selectedGuideId;                 // the guide highlighted/selected in Edit mode

        // --- draft state ---
        private bool _hasDraft;
        private Vec3d _draftStart;                      // deep-copied; owned
        private Vec3d _draftSecond;                     // Session 11: the placed BASE end of a 3-click draft
        private PlaneAxis _draftPlaneAxis = PlaneAxis.Y; // intrinsic plane for the ellipse family, from click 1's face

        // Session 11 (0.1.15): the Free-Shape's growing corner chain. Seeded with the start point on
        // every draft; extra corners only ever accumulate while the Free-Shape is the picked shape.
        private readonly List<Vec3d> _draftChain = new List<Vec3d>();

        /// <summary>
        /// Constructs the tool state. The per-guide cap seeds the placement pre-check with the v2 default;
        /// once the join-time bulk sync arrives, the composition root (Module 7) replaces it with the server's
        /// actual figure via <see cref="SetPerGuideVoxelCap"/>, so the pre-check and the server always agree.
        /// </summary>
        public DraftManager(int perGuideVoxelCap = 25000)
        {
            _perGuideVoxelCap = perGuideVoxelCap;
        }

        /// <summary>
        /// Re-seeds the placement pre-check with the server's synced per-guide voxel cap
        /// (<c>ClientNetworkHandler.PerGuideVoxelCap</c>, carried by the join-time bulk sync). A value of 0
        /// or less means the server enforces no per-guide cap, and the pre-check passes everything.
        /// </summary>
        public void SetPerGuideVoxelCap(int cap) => _perGuideVoxelCap = cap;

        // --- Tool state: mode -------------------------------------------------------------------

        public ToolMode Mode => _mode;
        public void SetMode(ToolMode mode) => _mode = mode;

        // --- Tool state: scale ------------------------------------------------------------------

        public int Scale => _scale;

        /// <summary>Sets the placement/body scale. Returns false and leaves it unchanged if not a valid scale.</summary>
        public bool SetScale(int scale)
        {
            if (!GuideData.IsValidVoxelScale(scale)) return false;
            _scale = scale;
            return true;
        }

        // --- Tool state: projection + plane override --------------------------------------------

        public ProjectionMode Projection => _projection;
        public void SetProjection(ProjectionMode projection) => _projection = projection;

        /// <summary>The forced plane orientation in Surface mode, or null to auto-select from the clicked face.</summary>
        public PlaneAxis? PlaneOverride => _planeOverride;
        public void SetPlaneOverride(PlaneAxis axis) => _planeOverride = axis;
        public void ClearPlaneOverride() => _planeOverride = null;

        // --- Tool state: fill -------------------------------------------------------------------

        public bool Filled => _filled;
        public void SetFilled(bool filled) => _filled = filled;

        /// <summary>Session 9: equal-part division marks for the NEXT guide (0/1 = none; clamped).</summary>
        public int Divisions => _divisions;
        public void SetDivisions(int divisions) =>
            _divisions = divisions < 0 ? 0 : divisions > Shapes.DivisionMarks.MaxDivisions
                ? Shapes.DivisionMarks.MaxDivisions : divisions;

        /// <summary>Session 11: the side count the NEXT polygon guide will use (clamped to 3..24).</summary>
        public int Sides => _sides;
        public void SetSides(int sides) => _sides = Shapes.PolygonShape.ClampSides(sides);

        // --- Tool state: shape (Session 8) --------------------------------------------------------

        /// <summary>The primitive the NEXT guide will use (the GUI's initial-shape picker writes this).</summary>
        public GuideShapeType Shape => _shape;

        /// <summary>The constraint the NEXT guide will carry (SemiCircle rides Arch; Circle rides Ellipse).</summary>
        public ShapeConstraint Constraint => _constraint;

        /// <summary>
        /// Sets the shape+constraint pair for the next placement. The pair is validated as one of the
        /// catalog entries; anything else falls back to the free parent of the given primitive. Changing
        /// shape mid-draft applies live to the ghost and the completed guide, like every other setting —
        /// except a stored 3-click BASE point, which is dropped when the new shape doesn't use one (the
        /// draft steps back to "one anchor placed").
        /// </summary>
        public void SetShape(GuideShapeType shape, ShapeConstraint constraint)
        {
            _shape = shape switch
            {
                GuideShapeType.Ellipse or GuideShapeType.Line or GuideShapeType.Triangle
                    or GuideShapeType.Rectangle or GuideShapeType.Polygon
                    or GuideShapeType.FreeShape
                    or GuideShapeType.Sphere or GuideShapeType.Dome or GuideShapeType.Cylinder
                    or GuideShapeType.Cone or GuideShapeType.Box => shape,
                _ => GuideShapeType.Arch
            };
            _constraint = IsValidPair(_shape, constraint) ? constraint : ShapeConstraint.None;
            if (!NeedsApexClick(_shape, _constraint)) _draftSecond = null;
            // Switching away from the Free-Shape mid-draft drops any chained corners beyond the first
            // (the draft steps back to "one anchor placed", same as the 3-click base rule above).
            if (_shape != GuideShapeType.FreeShape && _draftChain.Count > 1)
                _draftChain.RemoveRange(1, _draftChain.Count - 1);
        }

        /// <summary>The catalog's valid primitive+constraint pairs (Session 9 expansion).</summary>
        public static bool IsValidPair(GuideShapeType shape, ShapeConstraint constraint) =>
            constraint == ShapeConstraint.None
            || (shape == GuideShapeType.Arch && constraint == ShapeConstraint.SemiCircle)
            || (shape == GuideShapeType.Ellipse && constraint == ShapeConstraint.Circle)
            || (shape == GuideShapeType.Triangle && (constraint == ShapeConstraint.Right
                || constraint == ShapeConstraint.Equilateral || constraint == ShapeConstraint.Isosceles))
            || (shape == GuideShapeType.Rectangle && constraint == ShapeConstraint.Square);

        /// <summary>
        /// True when this shape+constraint places with THREE clicks (base · base · height/apex): the free,
        /// Right, and Isosceles triangles (apex click), and the Cylinder / Cone / Box volumes (0.1.21 —
        /// two base clicks then a height click). Equilateral stays two-click (its apex is fully derived);
        /// the Sphere and Dome are two-click volumes.
        /// </summary>
        public static bool NeedsApexClick(GuideShapeType shape, ShapeConstraint constraint) =>
            (shape == GuideShapeType.Triangle && constraint != ShapeConstraint.Equilateral)
            || shape == GuideShapeType.Cylinder
            || shape == GuideShapeType.Cone
            || shape == GuideShapeType.Box;

        /// <summary>True for the chained-click Free-Shape (Session 11, 0.1.15).</summary>
        public static bool IsChainShape(GuideShapeType shape) => shape == GuideShapeType.FreeShape;

        /// <summary>The intrinsic plane the active draft captured from its first click's block face.</summary>
        public PlaneAxis DraftPlaneAxis => _draftPlaneAxis;

        // --- Tool state: Edit-mode selection ----------------------------------------------------

        public Guid? SelectedGuideId => _selectedGuideId;
        public void SelectGuide(Guid guideId) => _selectedGuideId = guideId;
        public void ClearSelection() => _selectedGuideId = null;

        /// <summary>
        /// Assembles a render-settings bundle from the current (live) tool state plus a resolved projection plane.
        /// The held item resolves the plane (from the clicked face or the override) and calls this at send time; in
        /// Volumetric mode the plane is ignored, so any value (e.g. <see cref="ProjectionPlane.Default"/>) is fine.
        /// </summary>
        public GuideRenderSettings BuildRenderSettings(ProjectionPlane plane) =>
            new GuideRenderSettings(_scale, _projection, plane, _filled, _divisions);

        // --- Draft lifecycle --------------------------------------------------------------------

        public bool HasActiveDraft => _hasDraft;

        /// <summary>The draft's start point as a deep copy, or null if no draft is active.</summary>
        public Vec3d DraftStart => _hasDraft ? new Vec3d(_draftStart.X, _draftStart.Y, _draftStart.Z) : null;

        /// <summary>
        /// The placed BASE end of a three-click draft (the second click), as a deep copy — or null while
        /// the draft is still aiming its second point (or the shape is a plain two-click one).
        /// </summary>
        public Vec3d DraftSecond => _hasDraft && _draftSecond != null
            ? new Vec3d(_draftSecond.X, _draftSecond.Y, _draftSecond.Z) : null;

        /// <summary>True when the active draft has its base down and is now aiming the apex (click 3 of 3).</summary>
        public bool AwaitingApex => _hasDraft && _draftSecond != null;

        // --- Free-Shape corner chain (Session 11, 0.1.15) ----------------------------------------

        /// <summary>How many Free-Shape corners the active draft has placed (start point included).</summary>
        public int ChainCount => _hasDraft ? _draftChain.Count : 0;

        /// <summary>A deep copy of the placed corner chain (empty when no draft).</summary>
        public List<Vec3d> DraftChain
        {
            get
            {
                var copy = new List<Vec3d>(_hasDraft ? _draftChain.Count : 0);
                if (_hasDraft)
                    foreach (Vec3d p in _draftChain) copy.Add(new Vec3d(p.X, p.Y, p.Z));
                return copy;
            }
        }

        /// <summary>The chain's first corner (deep copy), or null. The close-the-loop click target.</summary>
        public Vec3d ChainFirst => _hasDraft && _draftChain.Count > 0
            ? new Vec3d(_draftChain[0].X, _draftChain[0].Y, _draftChain[0].Z) : null;

        /// <summary>The chain's last placed corner (deep copy), or null. The finish-open click target,
        /// and the reference point for the CTRL cardinal snap while chaining.</summary>
        public Vec3d ChainLast => _hasDraft && _draftChain.Count > 0
            ? new Vec3d(_draftChain[_draftChain.Count - 1].X,
                        _draftChain[_draftChain.Count - 1].Y,
                        _draftChain[_draftChain.Count - 1].Z) : null;

        /// <summary>
        /// Appends a Free-Shape corner (deep-copied). Returns false when the corner cap is reached —
        /// the caller surfaces the "too many corners" message and the click is ignored.
        /// </summary>
        public bool AppendChainPoint(Vec3d point)
        {
            if (!_hasDraft || point == null) return false;
            if (_draftChain.Count >= Shapes.FreeShape.MaxCorners) return false;
            _draftChain.Add(new Vec3d(point.X, point.Y, point.Z));
            return true;
        }

        /// <summary>
        /// Evaluates finishing the Free-Shape chain (open, or closed back to corner 0) against the
        /// per-guide cap. Same contract as <see cref="TryCompleteDraft"/>: pure check, caller clears.
        /// Start/End in the result are the chain's first and last corners (the wire's legacy fields).
        /// </summary>
        public DraftCompletion TryCompleteChain(bool closed)
        {
            if (!_hasDraft || _draftChain.Count < 2 || (closed && _draftChain.Count < 3))
                return new DraftCompletion(DraftCompletionStatus.NoActiveDraft, null, null, 0, 0);

            IGuideShape preview = new Shapes.FreeShape(_draftChain, closed);
            int count = preview.GetVoxelCount(_scale, _filled);

            DraftCompletionStatus status = _perGuideVoxelCap > 0 && count > _perGuideVoxelCap
                ? DraftCompletionStatus.RejectedOverCap
                : DraftCompletionStatus.Ready;

            return new DraftCompletion(status, ChainFirst, ChainLast, count, _perGuideVoxelCap);
        }

        /// <summary>
        /// Begins a placement draft from the first-click anchor. The start point is deep-copied. Settings are NOT
        /// captured here — they are read live when the draft completes — so mid-draft setting changes take effect.
        /// Starting a new draft replaces any existing one.
        /// </summary>
        public void StartDraft(Vec3d startPoint, PlaneAxis shapePlaneAxis = PlaneAxis.Y)
        {
            if (startPoint == null) throw new ArgumentNullException(nameof(startPoint));
            _draftStart = new Vec3d(startPoint.X, startPoint.Y, startPoint.Z);
            _draftSecond = null;
            _draftChain.Clear();
            _draftChain.Add(new Vec3d(startPoint.X, startPoint.Y, startPoint.Z));
            _draftPlaneAxis = shapePlaneAxis;
            _hasDraft = true;
        }

        /// <summary>
        /// Stores the second click of a THREE-click draft (the base's far end); the draft then awaits its
        /// apex click. Only meaningful when <see cref="NeedsApexClick"/> is true for the current shape.
        /// </summary>
        public void PlaceSecondPoint(Vec3d secondPoint)
        {
            if (!_hasDraft || secondPoint == null) return;
            _draftSecond = new Vec3d(secondPoint.X, secondPoint.Y, secondPoint.Z);
        }

        /// <summary>
        /// Steps a draft back one click (the right-click cancel, generalised for multi-click drafts):
        /// a Free-Shape chain retracts its last placed corner, an awaited apex reverts to "base end not
        /// placed"; with only the first anchor left, the whole draft is discarded.
        /// Returns true when the DRAFT SURVIVES (only a point was retracted), false when it was cleared.
        /// </summary>
        public bool StepBackDraft()
        {
            if (!_hasDraft) return false;
            if (IsChainShape(_shape) && _draftChain.Count > 1)
            {
                _draftChain.RemoveAt(_draftChain.Count - 1);
                return true;
            }
            if (_draftSecond != null)
            {
                _draftSecond = null;
                return true;
            }
            ClearDraft();
            return false;
        }

        /// <summary>
        /// Evaluates the final click against the active draft: builds the would-be shape (with
        /// <paramref name="apexPoint"/> applied for a three-click triangle) and checks it against the
        /// per-guide cap at the current scale. Returns <see cref="DraftCompletionStatus.Ready"/> with the
        /// points when it fits, <see cref="DraftCompletionStatus.RejectedOverCap"/> (with count and cap)
        /// when it does not, or <see cref="DraftCompletionStatus.NoActiveDraft"/> when there is nothing to
        /// complete. This is a pure check — it does NOT clear the draft; the caller clears it after a
        /// successful send, assembling the render settings via <see cref="BuildRenderSettings"/> then.
        /// </summary>
        public DraftCompletion TryCompleteDraft(Vec3d endPoint, Vec3d apexPoint = null, bool inverted = false)
        {
            if (!_hasDraft || endPoint == null)
                return new DraftCompletion(DraftCompletionStatus.NoActiveDraft, null, null, 0, 0);

            var start = new Vec3d(_draftStart.X, _draftStart.Y, _draftStart.Z);
            var end = new Vec3d(endPoint.X, endPoint.Y, endPoint.Z);
            Vec3d apex = apexPoint == null ? null : new Vec3d(apexPoint.X, apexPoint.Y, apexPoint.Z);

            IGuideShape preview = ShapeFactory.Create(_shape, _constraint, _draftPlaneAxis, start, end,
                inverted, _sides);
            if (apex != null && NeedsApexClick(_shape, _constraint) && preview.ControlPoints.Count > 2)
                preview.MoveControlPoint(2, apex);
            int count = preview.GetVoxelCount(_scale, _filled);

            // Cap of 0 or less = the server enforces no per-guide cap (unlimited); everything passes.
            DraftCompletionStatus status = _perGuideVoxelCap > 0 && count > _perGuideVoxelCap
                ? DraftCompletionStatus.RejectedOverCap
                : DraftCompletionStatus.Ready;

            return new DraftCompletion(status, start, end, count, _perGuideVoxelCap, apex);
        }

        /// <summary>Discards the active draft (on a successful send, an explicit cancel, logout, or tool-away).</summary>
        public void ClearDraft()
        {
            _hasDraft = false;
            _draftStart = null;
            _draftSecond = null;
            _draftChain.Clear();
        }
    }
}
