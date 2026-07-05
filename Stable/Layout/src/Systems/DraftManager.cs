using System;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;

namespace Layout.Systems
{
    /// <summary>
    /// What the Layout tool's clicks mean. Client-side only; never crosses the wire. Session-8 control
    /// scheme: TWO modes. In Create, everything constructive lives on the two mouse buttons contextually —
    /// left-click drafts anchors / grabs points / grabs the body (implicit insert) / releases; right-click
    /// cancels the in-progress action, or toggles a point's lock when idle. Delete is the one deliberately
    /// separated, destructive mode (the old Dispel). The old Edit and Lock modes folded into Create.
    /// </summary>
    public enum ToolMode
    {
        Create,
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
    /// two foot points the network layer needs to request it. On <see cref="DraftCompletionStatus.RejectedOverCap"/>
    /// the voxel count and cap are filled in for a HUD warning. The render settings are assembled separately by the
    /// caller (via <see cref="DraftManager.BuildRenderSettings"/>) at send time, so they reflect any settings the
    /// player changed mid-draft.
    /// </summary>
    public readonly struct DraftCompletion
    {
        public DraftCompletionStatus Status { get; }
        public Vec3d Start { get; }
        public Vec3d End { get; }
        public int VoxelCount { get; }
        public int CapLimit { get; }

        public DraftCompletion(DraftCompletionStatus status, Vec3d start, Vec3d end, int voxelCount, int capLimit)
        {
            Status = status;
            Start = start;
            End = end;
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
        private GuideShapeType _shape = GuideShapeType.Arch;            // Session 8: shape catalog
        private ShapeConstraint _constraint = ShapeConstraint.None;     //   Arch / Arch+SemiCircle / Ellipse / Ellipse+Circle
        private ToolMode _mode = ToolMode.Create;
        private Guid? _selectedGuideId;                 // the guide highlighted/selected in Edit mode

        // --- draft state ---
        private bool _hasDraft;
        private Vec3d _draftStart;                      // deep-copied; owned
        private PlaneAxis _draftPlaneAxis = PlaneAxis.Y; // intrinsic plane for the ellipse family, from click 1's face

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

        // --- Tool state: shape (Session 8) --------------------------------------------------------

        /// <summary>The primitive the NEXT guide will use (the GUI's initial-shape picker writes this).</summary>
        public GuideShapeType Shape => _shape;

        /// <summary>The constraint the NEXT guide will carry (SemiCircle rides Arch; Circle rides Ellipse).</summary>
        public ShapeConstraint Constraint => _constraint;

        /// <summary>
        /// Sets the shape+constraint pair for the next placement. The pair is validated as one of the four
        /// catalog entries; anything else falls back to the free parent of the given primitive. Changing
        /// shape mid-draft applies live to the ghost and the completed guide, like every other setting.
        /// </summary>
        public void SetShape(GuideShapeType shape, ShapeConstraint constraint)
        {
            _shape = shape == GuideShapeType.Ellipse ? GuideShapeType.Ellipse : GuideShapeType.Arch;
            bool valid = (_shape == GuideShapeType.Arch && constraint == ShapeConstraint.SemiCircle)
                      || (_shape == GuideShapeType.Ellipse && constraint == ShapeConstraint.Circle);
            _constraint = valid ? constraint : ShapeConstraint.None;
        }

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
            new GuideRenderSettings(_scale, _projection, plane, _filled);

        // --- Draft lifecycle --------------------------------------------------------------------

        public bool HasActiveDraft => _hasDraft;

        /// <summary>The draft's start point as a deep copy, or null if no draft is active.</summary>
        public Vec3d DraftStart => _hasDraft ? new Vec3d(_draftStart.X, _draftStart.Y, _draftStart.Z) : null;

        /// <summary>
        /// Begins a placement draft from the first-click anchor. The start point is deep-copied. Settings are NOT
        /// captured here — they are read live when the draft completes — so mid-draft setting changes take effect.
        /// Starting a new draft replaces any existing one.
        /// </summary>
        public void StartDraft(Vec3d startPoint, PlaneAxis shapePlaneAxis = PlaneAxis.Y)
        {
            if (startPoint == null) throw new ArgumentNullException(nameof(startPoint));
            _draftStart = new Vec3d(startPoint.X, startPoint.Y, startPoint.Z);
            _draftPlaneAxis = shapePlaneAxis;
            _hasDraft = true;
        }

        /// <summary>
        /// Evaluates the second-click end point against the active draft: builds the would-be arch and checks it
        /// against the per-guide cap at the current scale. Returns <see cref="DraftCompletionStatus.Ready"/> with
        /// the two foot points when it fits, <see cref="DraftCompletionStatus.RejectedOverCap"/> (with count and
        /// cap) when it does not, or <see cref="DraftCompletionStatus.NoActiveDraft"/> when there is nothing to
        /// complete. This is a pure check — it does NOT clear the draft; the caller clears it after a successful
        /// send, assembling the render settings via <see cref="BuildRenderSettings"/> at that point.
        /// </summary>
        public DraftCompletion TryCompleteDraft(Vec3d endPoint)
        {
            if (!_hasDraft || endPoint == null)
                return new DraftCompletion(DraftCompletionStatus.NoActiveDraft, null, null, 0, 0);

            var start = new Vec3d(_draftStart.X, _draftStart.Y, _draftStart.Z);
            var end = new Vec3d(endPoint.X, endPoint.Y, endPoint.Z);

            IGuideShape preview = ShapeFactory.Create(_shape, _constraint, _draftPlaneAxis, start, end);
            int count = preview.GetVoxelCount(_scale, _filled);

            // Cap of 0 or less = the server enforces no per-guide cap (unlimited); everything passes.
            DraftCompletionStatus status = _perGuideVoxelCap > 0 && count > _perGuideVoxelCap
                ? DraftCompletionStatus.RejectedOverCap
                : DraftCompletionStatus.Ready;

            return new DraftCompletion(status, start, end, count, _perGuideVoxelCap);
        }

        /// <summary>Discards the active draft (on a successful send, an explicit cancel, logout, or tool-away).</summary>
        public void ClearDraft()
        {
            _hasDraft = false;
            _draftStart = null;
        }
    }
}
