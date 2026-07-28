using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;

namespace Layout.Systems
{
    /// <summary>
    /// What the Layout tool's clicks mean. Client-side only; never crosses the wire (safe to reorder).
    /// FOUR modes: in <b>Create</b>, the two mouse buttons build guides contextually —
    /// left-click drafts anchors / grabs points / grabs the body (implicit insert) / releases; right-click
    /// cancels or toggles a point's lock. <b>Edit</b> is the same guide-manipulation minus placement (empty
    /// clicks just deselect) — while in it, the GUI's setting rows act on the SELECTED guide instead of the
    /// tool defaults, so per-guide editing no longer needs a separate expanding panel section.
    /// <b>Transform</b> (F6/F12, 0.3.86/0.3.90) selects a guide the same way and then acts on it as a WHOLE
    /// OBJECT without changing its shape — move it, rotate it, and in future copy and mirror it. Named for
    /// the category rather than one action, so the panel's buttons can keep the plain verbs.
    /// <b>Delete</b> is the deliberately separated destructive mode, kept last. Order matches the mode-row
    /// tiles via <c>(int)Mode</c>.
    /// </summary>
    public enum ToolMode
    {
        Create,
        Edit,
        Transform,
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
        /// <summary>0.2.24: the fourth click of a Tapered Cylinder (the rim/top-radius point), else null.</summary>
        public Vec3d Rim { get; }
        public bool FlatSideAligned { get; }
        public int VoxelCount { get; }
        public int CapLimit { get; }

        public DraftCompletion(DraftCompletionStatus status, Vec3d start, Vec3d end, int voxelCount, int capLimit,
            Vec3d apex = null, Vec3d rim = null, bool flatSideAligned = false)
        {
            Status = status;
            Start = start;
            End = end;
            Apex = apex;
            Rim = rim;
            FlatSideAligned = flatSideAligned;
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
        private bool _wireframe;
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
        private Vec3d _draftThird;                      // 0.2.24: the placed HEIGHT point of a 4-click draft
        private bool _draftFlatSideAligned;
        private PlaneAxis _draftPlaneAxis = PlaneAxis.Y; // intrinsic plane for the ellipse family, from click 1's face

        // Session 11 (0.1.15): the Free-Shape's growing corner chain. Seeded with the start point on
        // every draft; extra corners only ever accumulate while the Free-Shape is the picked shape.
        private readonly List<Vec3d> _draftChain = new List<Vec3d>();

        /// <summary>
        /// Constructs the tool state. The per-guide cap seeds the placement pre-check with the v2 default;
        /// once the join-time bulk sync arrives, the composition root (Module 7) replaces it with the server's
        /// actual figure via <see cref="SetPerGuideVoxelCap"/>, so the pre-check and the server always agree.
        /// </summary>
        public DraftManager(int perGuideVoxelCap = 500000)
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
        public void SetProjection(ProjectionMode projection)
        {
            if (_projection == projection) return;

            // Surface anchors sit directly on the clicked face; volumetric anchors sit at the centre
            // of the first voxel outside that face. Keep every point already placed in an active draft
            // on the same coordinate convention as the live cursor when projection changes mid-draft.
            // Without this translation, an Arch/Half-Circle's first anchor can share a cell with (or
            // appear to be replaced by) the newly generated volumetric body.
            if (_hasDraft)
            {
                double direction = _draftPlaneNegative ? -1.0 : 1.0;
                double offset = direction * _scale / 32.0;
                if (projection == ProjectionMode.Surface) offset = -offset;

                ShiftDraftPoint(_draftStart, offset);
                ShiftDraftPoint(_draftSecond, offset);
                ShiftDraftPoint(_draftThird, offset);
                foreach (Vec3d point in _draftChain) ShiftDraftPoint(point, offset);
            }

            _projection = projection;
        }

        private void ShiftDraftPoint(Vec3d point, double offset)
        {
            if (point == null) return;
            switch (_draftPlaneAxis)
            {
                case PlaneAxis.X: point.X += offset; break;
                case PlaneAxis.Y: point.Y += offset; break;
                case PlaneAxis.Z: point.Z += offset; break;
            }
        }

        /// <summary>The forced plane orientation in Surface mode, or null to auto-select from the clicked face.</summary>
        public PlaneAxis? PlaneOverride => _planeOverride;
        public void SetPlaneOverride(PlaneAxis axis) => _planeOverride = axis;
        public void ClearPlaneOverride() => _planeOverride = null;

        // --- Tool state: fill -------------------------------------------------------------------

        public bool Filled => _filled;
        public void SetFilled(bool filled) => _filled = filled;

        public bool Wireframe => _wireframe;
        public void SetWireframe(bool wireframe) => _wireframe = wireframe;

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
                    or GuideShapeType.TaperedCylinder or GuideShapeType.PolygonalPrism
                    or GuideShapeType.TaperedPolygonalPrism or GuideShapeType.Cone
                    or GuideShapeType.Box => shape,
                _ => GuideShapeType.Arch
            };
            _constraint = IsValidPair(_shape, constraint) ? constraint : ShapeConstraint.None;
            if (!NeedsApexClick(_shape, _constraint))
            {
                _draftSecond = null;
                _draftFlatSideAligned = false;
            }
            if (!NeedsRimClick(_shape)) _draftThird = null;
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
            || shape == GuideShapeType.TaperedCylinder
            || shape == GuideShapeType.PolygonalPrism
            || shape == GuideShapeType.TaperedPolygonalPrism
            || shape == GuideShapeType.Cone
            || shape == GuideShapeType.Box;

        /// <summary>
        /// True when this shape places with a FOURTH click after the height one (0.2.24): only the
        /// Tapered Cylinder, whose last click sets the lid's radius by its distance from the axis.
        /// </summary>
        public static bool NeedsRimClick(GuideShapeType shape) => shape == GuideShapeType.TaperedCylinder
            || shape == GuideShapeType.TaperedPolygonalPrism;

        /// <summary>True for the chained-click Free-Shape (Session 11, 0.1.15).</summary>
        public static bool IsChainShape(GuideShapeType shape) => shape == GuideShapeType.FreeShape;

        /// <summary>The intrinsic plane the active draft captured from its first click's block face.</summary>
        public PlaneAxis DraftPlaneAxis => _draftPlaneAxis;

        /// <summary>
        /// Whether the first click's face NORMAL points toward the axis's negative side (a ceiling, a
        /// north/west wall face). <see cref="DraftPlaneAxis"/> alone loses this sign — and the deterministic
        /// +axis default then grows a Dome INTO the surface it was placed on. The controller folds this into
        /// the Dome's inverted flag so it always rises AWAY from the clicked surface (0.2.11).
        /// </summary>
        public bool DraftPlaneNegative => _draftPlaneNegative;
        private bool _draftPlaneNegative;

        // --- Tool state: Edit-mode selection ----------------------------------------------------

        public Guid? SelectedGuideId => _selectedGuideId;
        public void SelectGuide(Guid guideId) { _selectedGuideId = guideId; ResetCopyRun(); }
        public void ClearSelection() { _selectedGuideId = null; ResetCopyRun(); }

        // --- Tool state: Move mode (F6) ---------------------------------------------------------

        /// <summary>The step multipliers offered by the Move pad. A step is always a whole number of the
        /// MOVED GUIDE's own voxels — never finer — so these multiply that guide's scale, not a fixed unit.</summary>
        public static readonly int[] MoveStepMultipliers = { 1, 2, 4, 8, 16 };

        private int _moveStep = 1;
        private bool _freeMove;

        /// <summary>How many of the guide's own voxels one arrow click travels.</summary>
        public int MoveStep => _moveStep;

        /// <summary>Sets the arrow-pad step multiplier. Ignores values outside the offered set.</summary>
        public bool SetMoveStep(int multiplier)
        {
            for (int i = 0; i < MoveStepMultipliers.Length; i++)
            {
                if (MoveStepMultipliers[i] != multiplier) continue;
                _moveStep = multiplier;
                return true;
            }
            return false;
        }

        /// <summary>Armed by the GUI toggle: the selected guide follows the crosshair once the panel closes.</summary>
        public bool FreeMove => _freeMove;
        public void SetFreeMove(bool enabled) => _freeMove = enabled;

        /// <summary>What the Transform pad does with the result: nothing (mirror in place), move it, or
        /// leave the original alone and produce a copy there.</summary>
        public enum TransformPlacement { None = 0, Move = 1, Copy = 2 }

        private TransformPlacement _placement = TransformPlacement.Move;
        private bool _transformMirror;
        private bool _transformSpanStep;
        private bool _spanEligible;      // eligibility as of the last action change, for the re-default

        public TransformPlacement Placement => _placement;
        public bool TransformMirror => _transformMirror;

        /// <summary>
        /// Whether stepping by the guide's OWN WIDTH can be reached at all — true wherever anything
        /// actually travels, plain Move included. Only a Mirror driving the pad by itself has no distance.
        /// </summary>
        public bool SpanStepAvailable => _placement != TransformPlacement.None;

        /// <summary>
        /// Whether span is what an action STARTS on. True where landing flush is the point: a copy, a
        /// mirrored copy, or a mirrored move (the "flip it over its own edge" gesture). False for a plain
        /// Move, whose purpose is the one-voxel nudge — starting that at a whole guide-width would break
        /// the case the mode was built for. Move can still reach span by re-clicking its lit step tile.
        /// </summary>
        public bool SpanStepDefault =>
            _placement == TransformPlacement.Copy
            || (_placement == TransformPlacement.Move && _transformMirror);

        /// <summary>Step by the guide's own extent along the pressed axis, rather than by <see cref="MoveStep"/>.</summary>
        public bool TransformSpanStep => _transformSpanStep && SpanStepAvailable;

        /// <summary>
        /// A step tile was clicked. <paramref name="selecting"/> is false when the player clicked the tile
        /// that was ALREADY lit, which means "give me the span back" — so the row is a plain exclusive
        /// picker on the way in and an escape hatch on the way out, with no separate span button.
        /// </summary>
        public void SelectMoveStep(int multiplier, bool selecting)
        {
            ResetCopyRun();          // a new distance starts a fresh line
            if (!selecting)
            {
                if (SpanStepAvailable) _transformSpanStep = true;
                return;
            }
            _transformSpanStep = false;
            SetMoveStep(multiplier);
        }

        /// <summary>
        /// Move and Copy are MUTUALLY EXCLUSIVE — "copy it and also move it" is just Copy, since the copy
        /// is what lands at the offset. Clicking the lit one turns it off, leaving Mirror to drive the pad
        /// on its own; that is refused when Mirror is off, because a pad that does nothing is not a state
        /// worth being able to reach.
        /// </summary>
        public void ToggleTransformPlacement(TransformPlacement which)
        {
            if (which != TransformPlacement.Move && which != TransformPlacement.Copy) return;
            if (_placement == which)
            {
                if (_transformMirror) _placement = TransformPlacement.None;
            }
            else _placement = which;
            SyncSpanDefault();
        }

        /// <summary>Toggles the mirror flag, refusing to leave the pad with nothing at all to do.</summary>
        public void ToggleTransformMirror()
        {
            if (_transformMirror && _placement == TransformPlacement.None) return;
            _transformMirror = !_transformMirror;
            SyncSpanDefault();
        }

        // Span follows the default only when CROSSING between actions that want it and actions that do not
        // — so switching Copy → Move drops back to the nudge, Move → Copy picks span up, and a deliberate
        // override survives any toggling that stays on one side of that line.
        private void SyncSpanDefault()
        {
            bool defaultsOn = SpanStepDefault;
            if (defaultsOn != _spanEligible) _transformSpanStep = defaultsOn;
            if (!SpanStepAvailable) _transformSpanStep = false;
            _spanEligible = defaultsOn;
            ResetCopyRun();          // any change of action starts a fresh run
        }

        // --- Copy runs -------------------------------------------------------------------------
        //
        // Hitting the same copy direction repeatedly should lay guides out in a LINE — 1 span out, then 2,
        // then 3 — rather than stacking every copy in the same place. The count is kept here rather than by
        // re-selecting each new copy, so the original stays selected and stays the thing being measured
        // from; that also keeps the whole gesture client-side, with no waiting on the new guide's id.
        private Guid? _copyRunGuide;
        private string _copyRunDirection;
        private int _copyRunCount;

        /// <summary>
        /// How many steps out the NEXT copy in this direction belongs: 1 the first time, then 2, 3, …
        /// Any change of guide or direction starts the run over.
        /// </summary>
        public int NextCopyRunFactor(Guid guideId, string directionCode)
        {
            if (_copyRunGuide == guideId && _copyRunDirection == directionCode) _copyRunCount++;
            else
            {
                _copyRunGuide = guideId;
                _copyRunDirection = directionCode;
                _copyRunCount = 1;
            }
            return _copyRunCount;
        }

        public void ResetCopyRun()
        {
            _copyRunGuide = null;
            _copyRunDirection = null;
            _copyRunCount = 0;
        }

        /// <summary>
        /// Assembles a render-settings bundle from the current (live) tool state plus a resolved projection plane.
        /// The held item resolves the plane (from the clicked face or the override) and calls this at send time; in
        /// Volumetric mode the plane is ignored, so any value (e.g. <see cref="ProjectionPlane.Default"/>) is fine.
        /// </summary>
        public GuideRenderSettings BuildRenderSettings(ProjectionPlane plane) =>
            new GuideRenderSettings(_scale, _projection, plane, _filled, _divisions,
                GuideShapeTypes.IsVolume(_shape) && _wireframe);

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

        public bool DraftFlatSideAligned => _hasDraft && _draftFlatSideAligned;

        /// <summary>
        /// The placed HEIGHT point of a four-click draft (the third click), as a deep copy — or null while
        /// the draft is still aiming its height (or the shape is not a four-click one).
        /// </summary>
        public Vec3d DraftThird => _hasDraft && _draftThird != null
            ? new Vec3d(_draftThird.X, _draftThird.Y, _draftThird.Z) : null;

        /// <summary>True when the active draft has its height down and is now aiming the RIM (click 4 of 4).</summary>
        public bool AwaitingRim => _hasDraft && _draftThird != null;

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
            int count = CountDraftVoxels(preview);

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
        public void StartDraft(Vec3d startPoint, PlaneAxis shapePlaneAxis = PlaneAxis.Y,
            bool planeNegative = false)
        {
            if (startPoint == null) throw new ArgumentNullException(nameof(startPoint));
            _draftStart = new Vec3d(startPoint.X, startPoint.Y, startPoint.Z);
            _draftSecond = null;
            _draftThird = null;
            _draftFlatSideAligned = false;
            _draftChain.Clear();
            _draftChain.Add(new Vec3d(startPoint.X, startPoint.Y, startPoint.Z));
            _draftPlaneAxis = shapePlaneAxis;
            _draftPlaneNegative = planeNegative;
            _hasDraft = true;
        }

        /// <summary>
        /// Stores the second click of a THREE-click draft (the base's far end); the draft then awaits its
        /// apex click. Only meaningful when <see cref="NeedsApexClick"/> is true for the current shape.
        /// </summary>
        public void PlaceSecondPoint(Vec3d secondPoint, bool flatSideAligned = false)
        {
            if (!_hasDraft || secondPoint == null) return;
            _draftSecond = new Vec3d(secondPoint.X, secondPoint.Y, secondPoint.Z);
            _draftFlatSideAligned = GuideShapeTypes.UsesSides(_shape) && flatSideAligned;
        }

        /// <summary>
        /// Stores the third click of a FOUR-click draft (the height point); the draft then awaits its rim
        /// click. Only meaningful when <see cref="NeedsRimClick"/> is true for the current shape (0.2.24).
        /// </summary>
        public void PlaceThirdPoint(Vec3d thirdPoint)
        {
            if (!_hasDraft || thirdPoint == null) return;
            _draftThird = new Vec3d(thirdPoint.X, thirdPoint.Y, thirdPoint.Z);
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
            if (_draftThird != null)
            {
                _draftThird = null;      // 0.2.24: a rim stage steps back to "aiming the height"
                return true;
            }
            if (_draftSecond != null)
            {
                _draftSecond = null;
                _draftFlatSideAligned = false;
                return true;
            }
            ClearDraft();
            return false;
        }

        /// <summary>
        /// Evaluates the final click against the active draft: builds the would-be shape (with
        /// <paramref name="apexPoint"/> applied for a three-click triangle) and checks it against the
        /// per-guide cap at the current scale. A caller may supply an already-computed exact count, or defer
        /// an expensive public immense-shell check to the authoritative server pipeline. Returns
        /// <see cref="DraftCompletionStatus.Ready"/> with the
        /// points when it fits, <see cref="DraftCompletionStatus.RejectedOverCap"/> (with count and cap)
        /// when it does not, or <see cref="DraftCompletionStatus.NoActiveDraft"/> when there is nothing to
        /// complete. This is a pure check — it does NOT clear the draft; the caller clears it after a
        /// successful send, assembling the render settings via <see cref="BuildRenderSettings"/> then.
        /// </summary>
        public DraftCompletion TryCompleteDraft(Vec3d endPoint, Vec3d apexPoint = null, bool inverted = false,
            Vec3d rimPoint = null, bool flatSideAligned = false,
            int knownVoxelCount = -1, bool deferCapCheckToAuthority = false)
        {
            if (!_hasDraft || endPoint == null)
                return new DraftCompletion(DraftCompletionStatus.NoActiveDraft, null, null, 0, 0);

            var start = new Vec3d(_draftStart.X, _draftStart.Y, _draftStart.Z);
            var end = new Vec3d(endPoint.X, endPoint.Y, endPoint.Z);
            Vec3d apex = apexPoint == null ? null : new Vec3d(apexPoint.X, apexPoint.Y, apexPoint.Z);
            Vec3d rim = rimPoint == null ? null : new Vec3d(rimPoint.X, rimPoint.Y, rimPoint.Z);

            int count = knownVoxelCount;
            if (count < 0 && !deferCapCheckToAuthority)
            {
                IGuideShape preview = ShapeFactory.Create(_shape, _constraint, _draftPlaneAxis, start, end,
                    inverted, _sides, flatSideAligned: flatSideAligned);
                ApplyPlacementPoints(preview, _shape, _constraint, apex, rim);
                count = CountDraftVoxels(preview);
            }
            if (count < 0) count = 0;

            // Cap of 0 or less = the server enforces no per-guide cap (unlimited); everything passes.
            DraftCompletionStatus status = !deferCapCheckToAuthority
                && _perGuideVoxelCap > 0 && count > _perGuideVoxelCap
                ? DraftCompletionStatus.RejectedOverCap
                : DraftCompletionStatus.Ready;

            return new DraftCompletion(status, start, end, count, _perGuideVoxelCap, apex, rim,
                flatSideAligned);
        }

        private int CountDraftVoxels(IGuideShape preview)
        {
            if (GuideShapeTypes.IsVolume(_shape) && _wireframe)
                return ShapeWireframe.GetVoxelCount(preview, _scale);
            return _perGuideVoxelCap > 0
                ? GuideShapeVoxelCounting.CountUpTo(preview, _scale, _filled, _perGuideVoxelCap)
                : preview.GetVoxelCount(_scale, _filled);
        }

        /// <summary>
        /// Applies the later placement clicks onto a freshly built shape: the third click (apex/height) at
        /// control point 2, and — for the four-click Tapered Cylinder — the fourth (rim/top radius) at
        /// control point 3. The one place that mapping lives; the ghost preview, the HUD measure, the cap
        /// pre-check, and the server's create path all route through it so they cannot drift apart.
        /// </summary>
        public static void ApplyPlacementPoints(IGuideShape shape, GuideShapeType type,
            ShapeConstraint constraint, Vec3d apex, Vec3d rim)
        {
            if (shape == null) return;
            if (apex != null && NeedsApexClick(type, constraint) && shape.ControlPoints.Count > 2)
                shape.MoveControlPoint(2, apex);
            if (rim != null && NeedsRimClick(type) && shape.ControlPoints.Count > 3)
                shape.MoveControlPoint(3, rim);
        }

        /// <summary>Discards the active draft (on a successful send, an explicit cancel, logout, or tool-away).</summary>
        public void ClearDraft()
        {
            _hasDraft = false;
            _draftStart = null;
            _draftSecond = null;
            _draftThird = null;
            _draftFlatSideAligned = false;
            _draftChain.Clear();
        }
    }
}
