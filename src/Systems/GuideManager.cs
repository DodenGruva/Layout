using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Layout.Guide;
using Layout.Shapes;

namespace Layout.Systems
{
    /// <summary>
    /// Outcome categories for a <see cref="GuideManager"/> mutation.
    /// </summary>
    public enum GuideOpStatus
    {
        /// <summary>Applied and persisted.</summary>
        Success,
        /// <summary>No guide with the given id exists.</summary>
        GuideNotFound,
        /// <summary>Rejected because the result would exceed a voxel cap; state was reverted.</summary>
        RejectedOverCap,
        /// <summary>Rejected because it targeted a locked control point.</summary>
        RejectedPointLocked,
        /// <summary>Rejected because an argument was invalid (bad index, bad scale, etc.).</summary>
        InvalidArgument,
        /// <summary>
        /// Rejected because it would exceed a guide-COUNT cap (per-player or world-wide, from server config;
        /// Module 7). <see cref="GuideOperationResult.VoxelCount"/> carries the current guide count and
        /// <see cref="GuideOperationResult.CapLimit"/> the count cap that was hit.
        /// </summary>
        RejectedOverGuideCount,
        /// <summary>
        /// Rejected because the target guide is edit-locked by ANOTHER player (Module 7's full-exclusivity
        /// rule). Produced by the undo path (<see cref="UndoManager"/>); direct operations are gated in the
        /// network handler before they ever reach this manager.
        /// </summary>
        RejectedGuideLocked
    }

    /// <summary>
    /// The result of a <see cref="GuideManager"/> mutation. GuideManager communicates outcomes by RETURN
    /// VALUE rather than firing events: the server network handler is the sole consumer, and an explicit
    /// result keeps the accept / reject / broadcast decision in one obvious place. The handler reads this to
    /// decide what to broadcast to everyone and what to tell the requesting player.
    /// </summary>
    /// <remarks>
    /// (This is a deliberate, flagged deviation from the v2 doc, which sketched GuideManager as "fires events
    /// for the handler to broadcast." Return values are simpler to follow and avoid event-lifetime concerns;
    /// the trade-off is that only a directly-calling consumer is notified, which is exactly the handler's role.)
    /// </remarks>
    public readonly struct GuideOperationResult
    {
        public GuideOpStatus Status { get; }

        /// <summary>The affected guide. Non-null on Success and on cap/lock rejections (reverted); null when not found.</summary>
        public GuideData Guide { get; }

        /// <summary>Voxel count after the (attempted) change — the figure to surface in a cap warning.</summary>
        public int VoxelCount { get; }

        /// <summary>The cap that was exceeded, for <see cref="GuideOpStatus.RejectedOverCap"/>; otherwise 0.</summary>
        public int CapLimit { get; }

        /// <summary>
        /// For an insert, the list index the new point landed at (so the broadcast / undo command can name it);
        /// -1 for every other operation.
        /// </summary>
        public int ControlPointIndex { get; }

        private GuideOperationResult(GuideOpStatus status, GuideData guide, int voxelCount, int capLimit, int controlPointIndex)
        {
            Status = status;
            Guide = guide;
            VoxelCount = voxelCount;
            CapLimit = capLimit;
            ControlPointIndex = controlPointIndex;
        }

        public bool IsSuccess => Status == GuideOpStatus.Success;

        public static GuideOperationResult Success(GuideData guide, int voxelCount, int controlPointIndex = -1) =>
            new GuideOperationResult(GuideOpStatus.Success, guide, voxelCount, 0, controlPointIndex);

        public static GuideOperationResult NotFound() =>
            new GuideOperationResult(GuideOpStatus.GuideNotFound, null, 0, 0, -1);

        public static GuideOperationResult OverCap(GuideData guide, int attemptedCount, int capLimit) =>
            new GuideOperationResult(GuideOpStatus.RejectedOverCap, guide, attemptedCount, capLimit, -1);

        public static GuideOperationResult PointLocked(GuideData guide) =>
            new GuideOperationResult(GuideOpStatus.RejectedPointLocked, guide, 0, 0, -1);

        public static GuideOperationResult Invalid(GuideData guide = null) =>
            new GuideOperationResult(GuideOpStatus.InvalidArgument, guide, 0, 0, -1);

        /// <summary>A guide-count cap (per-player or world-wide) was hit. Guide may be null (nothing was built).</summary>
        public static GuideOperationResult OverGuideCount(GuideData guide, int currentCount, int countCap) =>
            new GuideOperationResult(GuideOpStatus.RejectedOverGuideCount, guide, currentCount, countCap, -1);

        /// <summary>The target guide is edit-locked by another player (undo-path gating; see UndoManager).</summary>
        public static GuideOperationResult LockedByOtherPlayer(GuideData guide) =>
            new GuideOperationResult(GuideOpStatus.RejectedGuideLocked, guide, 0, 0, -1);

        public override string ToString() =>
            $"GuideOperationResult({Status}, guide={(Guide != null ? Guide.Id.ToString() : "none")}, " +
            $"voxels={VoxelCount}, cap={CapLimit}, cpIndex={ControlPointIndex})";
    }

    /// <summary>
    /// One element of a control-point update: which point (by list index) moves, and where to.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="GuideManager.UpdateControlPoints"/>. A single grab/move is just a one-element list;
    /// the same shape lets the network layer batch several moved points into one update if it ever needs to.
    /// The <see cref="Position"/> Vec3d is copied by the manager (via the shape), never aliased.
    /// </remarks>
    public readonly struct ControlPointEdit
    {
        public int Index { get; }
        public Vec3d Position { get; }

        public ControlPointEdit(int index, Vec3d position)
        {
            Index = index;
            Position = position;
        }
    }

    /// <summary>
    /// Server-side single authority for all guide state: the in-memory registry, the spine generation on
    /// create, validation (voxel caps, locks, existence) of every mutation, and JSON persistence to the world
    /// save. Nothing else is allowed to mutate guide state — callers go through the mutation methods here.
    /// </summary>
    /// <remarks>
    /// THREADING. All access is on the server's main thread (the game loop and the save/load events all run
    /// there), so there are no locks — matching the rest of the systems layer.
    ///
    /// THE TWO PARALLEL MAPS. The public registry is <see cref="AllGuides"/> (id → <see cref="GuideData"/>).
    /// Internally each guide is paired with its live <see cref="IGuideShape"/> (an <see cref="ArchShape"/>) in
    /// a second map, because the shape is what actually generates and counts voxels and applies geometric
    /// edits. The shape adopts the guide's <c>ControlPoints</c> list BY REFERENCE, so the two views never
    /// drift: a move applied through the shape is immediately visible in the persisted <see cref="GuideData"/>.
    ///
    /// VOXEL COUNTING (current scope). The built shape exposes <c>GetVoxelCount(int scale)</c> — the
    /// hollow-volumetric subset. So a guide's voxel count today is a function of its control points and its
    /// <see cref="GuideData.VoxelScale"/> only; <see cref="GuideData.Projection"/> and
    /// <see cref="GuideData.IsFilled"/> are stored and broadcast but do not yet change the geometry (that
    /// lands when the shape is widened to take <see cref="GuideRenderSettings"/>). Every call site that counts
    /// voxels is marked; widening is a contained change to those sites. A running total
    /// (<see cref="_totalVoxels"/>) plus a per-guide cache keep cap checks O(1).
    ///
    /// VALIDATION + REVERT. Every geometry-changing mutation is applied, then the result is counted; if it
    /// would breach the per-guide or total cap the change is rolled back to the pre-mutation snapshot and the
    /// caller gets <see cref="GuideOpStatus.RejectedOverCap"/>. Rescale and large drags are the realistic
    /// cap triggers (a finer scale multiplies voxel count).
    ///
    /// PERSISTENCE. All guides serialize as a single versioned JSON blob into the world save via
    /// <c>SaveGame.StoreData</c>; voxels are never stored (always re-derived). The save is rewritten after
    /// every committed mutation (cheap — it updates the in-memory save blob, flushed on the next world save)
    /// and on the world-save event; it is read back on save-game load, after which each guide's phantom points
    /// are recomputed before first sync. The <see cref="Vec3dJsonConverter"/> handles the one type a stock
    /// serializer can't (positions); it lives here because serializer config is GuideManager's responsibility.
    /// </remarks>
    public class GuideManager
    {
        /// <summary>Save-blob key under which all guides are stored.</summary>
        public const string StorageKey = "layout:guidedata";

        private readonly ICoreServerAPI _sapi;
        private readonly int _perGuideVoxelCap;
        private readonly int _totalVoxelCap;
        private readonly int _maxGuidesPerPlayer;
        private readonly int _maxGuidesWorldWide;

        private readonly Dictionary<Guid, GuideData> _guides = new Dictionary<Guid, GuideData>();
        private readonly Dictionary<Guid, IGuideShape> _shapes = new Dictionary<Guid, IGuideShape>();
        private readonly Dictionary<Guid, int> _voxelCounts = new Dictionary<Guid, int>();
        private int _totalVoxels;

        private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            Converters = { new Vec3dJsonConverter() },
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Include
        };

        /// <summary>
        /// Read-only view of all guides by id. Mutate guide state ONLY through this class's methods; treat
        /// these records as read-only (they are the live instances the shapes operate on). For the network
        /// bulk-sync on player join, enumerate the values.
        /// </summary>
        public IReadOnlyDictionary<Guid, GuideData> AllGuides => _guides;

        /// <summary>Total voxels across all loaded guides (kept in step with the per-guide cache).</summary>
        public int TotalVoxelCount => _totalVoxels;

        /// <summary>
        /// The active per-guide voxel cap this server enforces. Exposed read-only so the network layer can
        /// include it in the join-time bulk sync, letting the client's placement pre-check use the exact same
        /// number the server validates against (rather than a hard-coded default).
        /// </summary>
        public int PerGuideVoxelCap => _perGuideVoxelCap;

        /// <summary>
        /// The active total voxel cap across all guides this server enforces. Exposed read-only for the same
        /// join-time cap sync as <see cref="PerGuideVoxelCap"/>.
        /// </summary>
        public int TotalVoxelCap => _totalVoxelCap;

        /// <summary>Max guides one player may have created at once; 0 = unlimited. Server config (Module 7).</summary>
        public int MaxGuidesPerPlayer => _maxGuidesPerPlayer;

        /// <summary>Max guides that may exist world-wide at once; 0 = unlimited. Server config (Module 7).</summary>
        public int MaxGuidesWorldWide => _maxGuidesWorldWide;

        /// <summary>Number of guides currently loaded (the figure the world-wide count cap checks against).</summary>
        public int GuideCount => _guides.Count;

        /// <summary>
        /// Wires up persistence (load on save-game load, write on world-save) and stores the caps, which
        /// normally come from server config (<c>layout.json</c>). UNLIMITED SEMANTICS: a cap of 0 or any
        /// negative value means "unlimited" — that check is simply skipped. This applies to all four caps
        /// (both voxel caps and both guide-count caps); values are normalised to 0 here so "unlimited" has
        /// one representation. Existing guides always LOAD regardless of caps (caps gate new mutations, never
        /// eject persisted state a server admin lowered a cap underneath).
        /// </summary>
        public GuideManager(
            ICoreServerAPI sapi,
            int perGuideVoxelCap = 25000,
            int totalVoxelCap = 250000,
            int maxGuidesPerPlayer = 0,
            int maxGuidesWorldWide = 0)
        {
            _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
            _perGuideVoxelCap = perGuideVoxelCap > 0 ? perGuideVoxelCap : 0;
            _totalVoxelCap = totalVoxelCap > 0 ? totalVoxelCap : 0;
            _maxGuidesPerPlayer = maxGuidesPerPlayer > 0 ? maxGuidesPerPlayer : 0;
            _maxGuidesWorldWide = maxGuidesWorldWide > 0 ? maxGuidesWorldWide : 0;

            // VS-API touch points (verify signatures at compile-check):
            //   sapi.Event.SaveGameLoaded  — fired once when the save loads; we read guides here.
            //   sapi.Event.GameWorldSave   — fired before each world save; we flush the latest blob here.
            _sapi.Event.SaveGameLoaded += Load;
            _sapi.Event.GameWorldSave += Persist;
        }

        // --- Lookups ------------------------------------------------------------------------------

        /// <summary>Fetches a guide by id. Returns false (and a null out) if absent.</summary>
        public bool TryGetGuide(Guid id, out GuideData guide) => _guides.TryGetValue(id, out guide);

        /// <summary>True if a guide with this id is currently loaded.</summary>
        public bool HasGuide(Guid id) => _guides.ContainsKey(id);

        /// <summary>The shape backing a guide, or null if absent. Exposed for client-targeting-style queries.</summary>
        public IGuideShape GetShape(Guid id) => _shapes.TryGetValue(id, out var s) ? s : null;

        // --- Creation -----------------------------------------------------------------------------

        /// <summary>
        /// Builds a brand-new arch from two foot points and the requested render settings: generates the spine
        /// via <see cref="ArchShape"/>, wraps it in a fresh <see cref="GuideData"/> (new id, list shared with
        /// the shape), validates against the caps, and on success stores and persists it. The server is the one
        /// that builds the guide — clients only ever send the two points plus settings.
        /// <paramref name="creatorUid"/> is stamped onto the guide for the per-player guide-count cap only
        /// (bookkeeping, not ownership); pass null when there is no acting player.
        /// </summary>
        public GuideOperationResult CreateGuide(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType = GuideShapeType.Arch,
            ShapeConstraint constraint = ShapeConstraint.None,
            PlaneAxis shapePlaneAxis = PlaneAxis.Y,
            string creatorUid = null,
            Vec3d thirdPoint = null,
            bool inverted = false,
            int sides = 0,
            IReadOnlyList<Vec3d> chain = null,
            bool closed = false)
        {
            if (start == null || end == null) return GuideOperationResult.Invalid();
            if (!GuideData.IsValidVoxelScale(settings.Scale)) return GuideOperationResult.Invalid();

            // 3D volumes are always Volumetric and carry no division marks (0.1.20 sphere, 0.1.21 family)
            // — normalise defensively whatever a client sends (a lingering Surface/Divisions default).
            if (GuideShapeTypes.IsVolume(shapeType)
                && (settings.Mode == ProjectionMode.Surface || settings.Divisions != 0))
                settings = new GuideRenderSettings(settings.Scale, ProjectionMode.Volumetric,
                    settings.Plane, settings.Filled, 0);

            // Guide-COUNT caps first — cheap, and nothing has been built yet (Guide is null in the result).
            if (_maxGuidesWorldWide > 0 && _guides.Count >= _maxGuidesWorldWide)
                return GuideOperationResult.OverGuideCount(null, _guides.Count, _maxGuidesWorldWide);
            if (_maxGuidesPerPlayer > 0 && creatorUid != null && CountGuidesBy(creatorUid) >= _maxGuidesPerPlayer)
                return GuideOperationResult.OverGuideCount(null, CountGuidesBy(creatorUid), _maxGuidesPerPlayer);

            IGuideShape shape = ShapeFactory.Create(shapeType, constraint, shapePlaneAxis, start, end,
                inverted, sides, chain, closed);

            // Three-click shapes (Session 11 triangle; 0.1.21 cylinder/cone/box): the third click sets the
            // apex/height, stored at control point index 2. Applied before the record is built so the
            // as-placed snapshot captures the true placed form. Two-click shapes send no third point.
            if (thirdPoint != null && DraftManager.NeedsApexClick(shapeType, shape.Constraint)
                && shape.ControlPoints.Count > 2)
            {
                shape.MoveControlPoint(2, thirdPoint);
            }

            var data = GuideData.Create(
                shapeType,
                shape.ControlPoints,            // adopted by reference — guide and shape share one list
                settings.Scale,
                settings.Mode,
                settings.Plane,
                settings.Filled,
                shape.Constraint,               // the shape may have rejected an inapplicable constraint
                shapePlaneAxis,
                settings.Divisions,
                shapeType == GuideShapeType.Polygon ? Shapes.PolygonShape.ClampSides(sides) : 0,
                shape is Shapes.FreeShape fs && fs.IsClosed);
            data.CreatorUid = creatorUid;

            int count = shape.GetVoxelCount(data.VoxelScale, data.IsFilled);
            if (_perGuideVoxelCap > 0 && count > _perGuideVoxelCap)
                return GuideOperationResult.OverCap(data, count, _perGuideVoxelCap);
            if (_totalVoxelCap > 0 && _totalVoxels + count > _totalVoxelCap)
                return GuideOperationResult.OverCap(data, count, _totalVoxelCap);

            _guides[data.Id] = data;
            _shapes[data.Id] = shape;
            StoreCount(data.Id, count);
            Persist();
            return GuideOperationResult.Success(data, count);
        }

        /// <summary>
        /// Re-inserts a previously-deleted guide EXACTLY as it was, preserving its id — the undo path for a
        /// delete. The provided snapshot is deep-copied so the live guide never aliases the undo command's
        /// stored copy. No-ops to success if a guide with that id already exists.
        /// </summary>
        public GuideOperationResult RestoreGuide(GuideData snapshot)
        {
            if (snapshot == null) return GuideOperationResult.Invalid();
            if (_guides.TryGetValue(snapshot.Id, out var existing))
                return GuideOperationResult.Success(existing, _voxelCounts.TryGetValue(existing.Id, out var c) ? c : 0);

            var live = snapshot.DeepClone();                    // independent of the command's stored snapshot
            if (live.ControlPoints == null) live.ControlPoints = new List<ControlPoint>();
            IGuideShape shape = ShapeFactory.Adopt(live);
            shape.RecalculatePhantomPoints();

            // Guide-count caps apply to a restore too (an undo-of-delete / redo-of-create is a (re-)creation):
            // hitting one yields the same push-back-and-warn Blocked path as a voxel-cap rejection would.
            if (_maxGuidesWorldWide > 0 && _guides.Count >= _maxGuidesWorldWide)
                return GuideOperationResult.OverGuideCount(live, _guides.Count, _maxGuidesWorldWide);
            if (_maxGuidesPerPlayer > 0 && live.CreatorUid != null && CountGuidesBy(live.CreatorUid) >= _maxGuidesPerPlayer)
                return GuideOperationResult.OverGuideCount(live, CountGuidesBy(live.CreatorUid), _maxGuidesPerPlayer);

            int count = shape.GetVoxelCount(live.VoxelScale, live.IsFilled);
            if (_perGuideVoxelCap > 0 && count > _perGuideVoxelCap)
                return GuideOperationResult.OverCap(live, count, _perGuideVoxelCap);
            if (_totalVoxelCap > 0 && _totalVoxels + count > _totalVoxelCap)
                return GuideOperationResult.OverCap(live, count, _totalVoxelCap);

            _guides[live.Id] = live;
            _shapes[live.Id] = shape;
            StoreCount(live.Id, count);
            Persist();
            return GuideOperationResult.Success(live, count);
        }

        // --- Control-point edits ------------------------------------------------------------------

        /// <summary>
        /// Moves one or more control points to new positions. Rejects (without applying) any edit aimed at a
        /// phantom, an out-of-range index, or a LOCKED point; otherwise applies every move, re-counts, and
        /// rolls the whole batch back if it breaches a cap. Locked-point rejection is the server-side half of
        /// the lock policy (the client also skips locked points when targeting a grab).
        /// </summary>
        public GuideOperationResult UpdateControlPoints(Guid id, IReadOnlyList<ControlPointEdit> edits)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (edits == null || edits.Count == 0) return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];

            // Validate every target before mutating anything.
            for (int i = 0; i < edits.Count; i++)
            {
                int idx = edits[i].Index;
                if (idx < 0 || idx >= g.ControlPoints.Count) return GuideOperationResult.Invalid(g);
                ControlPoint cp = g.ControlPoints[idx];
                if (cp.IsPhantom) return GuideOperationResult.Invalid(g);
                if (cp.IsLocked) return GuideOperationResult.PointLocked(g);
                if (edits[i].Position == null) return GuideOperationResult.Invalid(g);
            }

            var snapshot = SnapshotPoints(g);
            for (int i = 0; i < edits.Count; i++)
                shape.MoveControlPoint(edits[i].Index, edits[i].Position);

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.OverCap(g, count, cap);
            }

            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Inserts a new plain body point at curve parameter <paramref name="t"/> (0..1) positioned at
        /// <paramref name="position"/> — "grab the body" creates a point here. Reports the list index the new
        /// point landed at (recovered via the shape's nearest-point lookup, since the inserted point is by
        /// contract the nearest to <paramref name="position"/>) so the broadcast and undo command can name it.
        /// Rolls back if the insert breaches a cap.
        /// </summary>
        /// <remarks>
        /// Inserts by curve parameter <paramref name="t"/> to match the shape's own insert primitive; whether
        /// the network packet carries <c>t</c> or the resulting index is a Module 4 (networking) decision, and
        /// the index seam makes either recoverable.
        /// </remarks>
        public GuideOperationResult InsertControlPoint(Guid id, float t, Vec3d position)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (position == null) return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];

            var snapshot = SnapshotPoints(g);
            shape.InsertControlPoint(t, position);
            int insertedIndex = shape.GetNearestControlPointIndex(position); // the just-inserted point

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.OverCap(g, count, cap);
            }

            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count, insertedIndex);
        }

        /// <summary>
        /// Removes a single plain body control point by index — the undo path for an insert. Refuses to remove
        /// anchors, the apex, or phantom points (only interior inserted points are removable). Removal can only
        /// reduce the voxel count, so it never breaches a cap.
        /// </summary>
        public GuideOperationResult RemoveControlPoint(Guid id, int index)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (index < 0 || index >= g.ControlPoints.Count) return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];

            ControlPoint cp = g.ControlPoints[index];
            if (cp.IsPhantom || cp.IsAnchor || cp.IsPrimary) return GuideOperationResult.Invalid(g);

            g.ControlPoints.RemoveAt(index);     // mutates the shared list in place
            shape.RecalculatePhantomPoints();    // restore validity after an external list change

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Sets a single control point's locked-constraint flag (the red "do not move me" state). Refuses
        /// phantom points and out-of-range indices. Locking changes no geometry, so the voxel count is
        /// unaffected. This is the authoritative operation behind the lock-point packet and the lock-point
        /// undo command; moving a locked point is separately refused by <see cref="UpdateControlPoints"/>.
        /// </summary>
        public GuideOperationResult SetPointLocked(Guid id, int index, bool locked)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (index < 0 || index >= g.ControlPoints.Count) return GuideOperationResult.Invalid(g);

            ControlPoint cp = g.ControlPoints[index];
            if (cp.IsPhantom) return GuideOperationResult.Invalid(g);

            cp.IsLocked = locked;
            Persist();
            return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c) ? c : 0);
        }

        // --- Deletion -----------------------------------------------------------------------------

        /// <summary>
        /// Deletes a guide outright. The caller (undo command) is responsible for snapshotting it first via
        /// <see cref="GuideData.DeepClone"/> if the delete is to be undoable.
        /// </summary>
        public GuideOperationResult DeleteGuide(Guid id)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();

            _guides.Remove(id);
            _shapes.Remove(id);
            RemoveCount(id);
            Persist();
            return GuideOperationResult.Success(g, 0);
        }

        // --- Per-guide property toggles -----------------------------------------------------------

        /// <summary>Sets a guide's hidden flag (hidden guides render as anchors-only). Voxel count is unaffected.</summary>
        public GuideOperationResult SetHidden(Guid id, bool hidden)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            g.IsHidden = hidden;
            Persist();
            return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c) ? c : 0);
        }

        /// <summary>
        /// Sets a guide's projection mode and plane. The cap is re-checked for forward-safety (today the count
        /// is unchanged because the shape ignores projection; once the shape is projection-aware this guards the
        /// Surface path automatically), reverting the fields if it would breach.
        /// </summary>
        public GuideOperationResult SetProjection(Guid id, ProjectionMode mode, ProjectionPlane plane)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            // A 3D volume cannot go Surface (0.1.20/0.1.21) — the GUI greys the row; server-side gate.
            if (GuideShapeTypes.IsVolume(g.ShapeType) && mode == ProjectionMode.Surface)
                return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];

            ProjectionMode oldMode = g.Projection;
            ProjectionPlane oldPlane = g.Plane;

            // BAKE ON LEAVING SURFACE (Session-8 playtest fix): while a guide is Surface, every edit the
            // player makes is made against the FLATTENED view, so the stored 3D spine drifts out of the
            // plane wherever it was never touched. Toggling to Volumetric therefore used to reveal a curve
            // that mostly "jumped" off the plane — only interacted points looked right. Now the flattened
            // positions are baked into the control points at the moment Surface is left: the volumetric
            // guide IS the shape you were looking at, where you were looking at it. Undoable (the server
            // handler snapshots the pre-bake points into the projection command).
            //
            // AIR-SIDE BAKE (Session 11 — the B-S10-2 fix). Baking to the EXACT plane coordinate quantised
            // to the plane's positive-side cell regardless of which side was air, so on a negative-facing
            // wall the volume grew INTO the block. The bake now probes world solidity on both sides of the
            // plane (the server-side mirror of the renderer's decal-side probe) and nudges every baked
            // point HALF A VOXEL into the airier side — exactly where a fresh Volumetric placement against
            // that face would put its anchors — so the volume grows out of the wall, into the open air.
            List<ControlPoint> bakeRollback = null;
            if (oldMode == ProjectionMode.Surface && mode == ProjectionMode.Volumetric)
            {
                bakeRollback = SnapshotPoints(g);
                double planeCoord = oldPlane.PlaneOffset / 16.0;
                double bakeCoord = planeCoord
                    + ProbeAirSide(g, oldPlane) * (g.VoxelScale / 16.0) * 0.5;
                foreach (ControlPoint cp in g.ControlPoints)
                {
                    if (cp == null || cp.IsPhantom) continue;
                    Vec3d w = cp.WorldPosition;
                    switch (oldPlane.FlattenedAxis)
                    {
                        case PlaneAxis.X: cp.SetPosition(bakeCoord, w.Y, w.Z); break;
                        case PlaneAxis.Z: cp.SetPosition(w.X, w.Y, bakeCoord); break;
                        default:          cp.SetPosition(w.X, bakeCoord, w.Z); break;
                    }
                }
                shape.RecalculatePhantomPoints();
            }

            g.Projection = mode;
            g.Plane = plane;

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.Projection = oldMode;
                g.Plane = oldPlane;
                if (bakeRollback != null) RestorePoints(id, bakeRollback);
                return GuideOperationResult.OverCap(g, count, cap);
            }

            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        // Which side of a Surface plane is open air, by world solidity at the guide's own points: probes
        // the block half a block out on each side of the plane at every non-phantom control point and
        // votes. +1 = the positive side is airier, −1 = the negative side. Ties (free-floating planes,
        // fully buried, or unloaded chunks) fall back to +1 — the pre-fix behaviour, harmless there.
        // This is the server-side mirror of the renderer's CountSolidProbes decal-side vote (B-S10-2).
        private int ProbeAirSide(GuideData g, ProjectionPlane plane)
        {
            var accessor = _sapi.World?.BlockAccessor;
            if (accessor == null) return 1;

            double planeCoord = plane.PlaneOffset / 16.0;
            int solidsPos = 0, solidsNeg = 0;
            foreach (ControlPoint cp in g.ControlPoints)
            {
                if (cp == null || cp.IsPhantom) continue;
                Vec3d w = cp.WorldPosition;
                for (int side = 0; side < 2; side++)
                {
                    double c = planeCoord + (side == 0 ? 0.5 : -0.5);
                    double wx = plane.FlattenedAxis == PlaneAxis.X ? c : w.X;
                    double wy = plane.FlattenedAxis == PlaneAxis.Y ? c : w.Y;
                    double wz = plane.FlattenedAxis == PlaneAxis.Z ? c : w.Z;
                    var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Floor(wy), (int)Math.Floor(wz));
                    if (accessor.GetChunkAtBlockPos(pos) == null) continue;   // unloaded: no vote
                    var block = accessor.GetBlock(pos);
                    if (block != null && block.Id != 0)
                    {
                        if (side == 0) solidsPos++; else solidsNeg++;
                    }
                }
            }
            return solidsPos <= solidsNeg ? 1 : -1;
        }

        /// <summary>
        /// Session 11: sets a polygon guide's side count (re-derives the outline; anchors are untouched).
        /// Clamped to the polygon's 3..24 range; re-counts and reverts on a cap breach, since more sides
        /// mean more perimeter voxels. Rejects non-polygon guides.
        /// </summary>
        public GuideOperationResult SetSides(Guid id, int sides)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (g.ShapeType != GuideShapeType.Polygon) return GuideOperationResult.Invalid(g);

            int oldSides = g.Sides;
            int clamped = Shapes.PolygonShape.ClampSides(sides);
            if (clamped == oldSides)
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c0) ? c0 : 0);

            g.Sides = clamped;
            IGuideShape shape = ShapeFactory.Adopt(g);          // side count is baked into the shape view
            _shapes[id] = shape;

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.Sides = oldSides;
                _shapes[id] = ShapeFactory.Adopt(g);
                return GuideOperationResult.OverCap(g, count, cap);
            }

            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Session 11 (SHIFT spring-back): restores the guide's control points AND constraint to the
        /// as-placed snapshot captured at creation. Returns Invalid when the guide predates the snapshot
        /// (nothing recorded to spring back to). The caller owns undo recording and broadcasting, as ever.
        /// </summary>
        public GuideOperationResult SpringBackToOriginal(Guid id)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (g.OriginalControlPoints == null || g.OriginalControlPoints.Count == 0)
                return GuideOperationResult.Invalid(g);
            return RestoreConstraint(id, g.OriginalConstraint, g.OriginalControlPoints);
        }

        /// <summary>
        /// Replaces a guide's control points with a snapshot (deep-copied in), re-adopting the shape and
        /// recounting — the undo seam for operations that rewrite points wholesale (the Surface-exit bake).
        /// </summary>
        public GuideOperationResult RestoreControlPoints(Guid id, List<ControlPoint> pointsSnapshot)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (pointsSnapshot == null) return GuideOperationResult.Invalid(g);

            g.ControlPoints.Clear();
            foreach (var cp in pointsSnapshot) g.ControlPoints.Add(cp.Clone());
            IGuideShape shape = ShapeFactory.Adopt(g);
            _shapes[id] = shape;
            shape.RecalculatePhantomPoints();

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>Sets a guide's filled flag (hollow vs filled). Cap re-checked for forward-safety, as in <see cref="SetProjection"/>.</summary>
        /// <summary>
        /// Clears the guide's constraint, demoting it to its free parent shape (Session-8 absorb-or-break;
        /// see <see cref="ShapeConstraint"/>). Point materialisation is the shape's job; recounting,
        /// persistence, and returning the refreshed record are this layer's. No-ops to success when the
        /// guide is already unconstrained. Undo snapshotting (the pre-break points + constraint) is the
        /// CALLER's job, taken BEFORE calling this.
        /// </summary>
        public GuideOperationResult BreakConstraint(Guid id)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            var shape = _shapes[id];

            if (!shape.BreakConstraint())
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c0) ? c0 : 0);

            g.Constraint = ShapeConstraint.None;
            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Restores a guide's constraint AND its exact pre-break control points — the undo of
        /// <see cref="BreakConstraint"/>. The snapshot list is deep-copied in.
        /// </summary>
        public GuideOperationResult RestoreConstraint(Guid id, ShapeConstraint constraint, List<ControlPoint> pointsSnapshot)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (pointsSnapshot == null) return GuideOperationResult.Invalid();

            g.ControlPoints.Clear();
            foreach (var cp in pointsSnapshot) g.ControlPoints.Add(cp.Clone());
            g.Constraint = constraint;
            IGuideShape shape = ShapeFactory.Adopt(g);      // re-adopt so the shape sees the constraint
            _shapes[id] = shape;
            shape.RecalculatePhantomPoints();

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Sets the equal-part division count (Session 9). A pure visual recolor — geometry, counts, and
        /// caps are untouched, so there is nothing to check beyond existence: clamp, set, persist.
        /// </summary>
        public GuideOperationResult SetDivisions(Guid id, int divisions)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            // Divisions don't apply to 3D volumes (0.1.20/0.1.21 — no single curve); the GUI greys the
            // field, this is the server-side gate against a stale packet painting the wireframe.
            if (GuideShapeTypes.IsVolume(g.ShapeType)) return GuideOperationResult.Invalid(g);
            int clamped = divisions < 0 ? 0 : divisions > Shapes.DivisionMarks.MaxDivisions
                ? Shapes.DivisionMarks.MaxDivisions : divisions;
            g.Divisions = clamped;
            Persist();
            return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c0) ? c0 : 0);
        }

        public GuideOperationResult SetFilled(Guid id, bool filled)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            var shape = _shapes[id];

            bool oldFilled = g.IsFilled;
            g.IsFilled = filled;

            int count = shape.GetVoxelCount(g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.IsFilled = oldFilled;
                return GuideOperationResult.OverCap(g, count, cap);
            }

            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Changes a guide's voxel scale (one of 1/2/4/8/16). This is the most likely cap trigger — a finer
        /// scale multiplies the voxel count — so the new count is validated and the scale reverted if it breaches.
        /// </summary>
        public GuideOperationResult Rescale(Guid id, int newScale)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (!GuideData.IsValidVoxelScale(newScale)) return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];

            int oldScale = g.VoxelScale;
            g.VoxelScale = newScale;

            int count = shape.GetVoxelCount(newScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.VoxelScale = oldScale;
                return GuideOperationResult.OverCap(g, count, cap);
            }

            StoreCount(id, count);
            Persist();
            return GuideOperationResult.Success(g, count);
        }

        // --- Cap + count bookkeeping --------------------------------------------------------------

        // True if making guide `id`'s count `newCount` would breach the per-guide or projected total cap.
        // A cap of 0 means unlimited — that check is skipped (normalised in the ctor).
        private bool WouldExceedCaps(Guid id, int newCount, out int cap)
        {
            if (_perGuideVoxelCap > 0 && newCount > _perGuideVoxelCap) { cap = _perGuideVoxelCap; return true; }
            int currentForId = _voxelCounts.TryGetValue(id, out var c) ? c : 0;
            int projectedTotal = _totalVoxels - currentForId + newCount;
            if (_totalVoxelCap > 0 && projectedTotal > _totalVoxelCap) { cap = _totalVoxelCap; return true; }
            cap = 0;
            return false;
        }

        /// <summary>
        /// How many loaded guides were created by <paramref name="playerUid"/> — the figure the per-player
        /// guide-count cap checks against. Guides with a null creator (pre-Module-7 saves) count toward no one.
        /// O(n) over the guide registry, which is fine at cap-relevant scales and avoids a second index to
        /// keep consistent through create/delete/restore.
        /// </summary>
        public int CountGuidesBy(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid)) return 0;
            int n = 0;
            foreach (var g in _guides.Values)
                if (g.CreatorUid == playerUid) n++;
            return n;
        }

        // Records a guide's voxel count, keeping the running total in step.
        private void StoreCount(Guid id, int count)
        {
            int prev = _voxelCounts.TryGetValue(id, out var c) ? c : 0;
            _totalVoxels += count - prev;
            _voxelCounts[id] = count;
        }

        // Drops a guide's count from the cache and the running total.
        private void RemoveCount(Guid id)
        {
            if (_voxelCounts.TryGetValue(id, out var c))
            {
                _totalVoxels -= c;
                _voxelCounts.Remove(id);
            }
        }

        // --- Snapshot / revert (shared-list aware) ------------------------------------------------

        // A deep, independent copy of a guide's control points, for rolling back a rejected mutation.
        private static List<ControlPoint> SnapshotPoints(GuideData g)
        {
            var copy = new List<ControlPoint>(g.ControlPoints.Count);
            foreach (var cp in g.ControlPoints)
                copy.Add(cp == null ? new ControlPoint() : cp.Clone());
            return copy;
        }

        // Restores a guide's points from a snapshot. Crucially this refills the SAME list instance the shape
        // holds (clear + re-add), never swaps the reference, then re-derives phantoms — so the shape and guide
        // stay bound to one list.
        private void RestorePoints(Guid id, List<ControlPoint> snapshot)
        {
            var g = _guides[id];
            var shape = _shapes[id];
            g.ControlPoints.Clear();
            foreach (var cp in snapshot)
                g.ControlPoints.Add(cp.Clone());
            shape.RecalculatePhantomPoints();
            StoreCount(id, shape.GetVoxelCount(g.VoxelScale, g.IsFilled));
        }

        // --- Persistence --------------------------------------------------------------------------

        // Serializes all guides into the save blob. Cheap (in-memory); the actual disk write happens on world
        // save. Never throws out of here — a serialization fault must not crash the server.
        private void Persist()
        {
            try
            {
                var root = new PersistedRoot
                {
                    Version = GuideData.CurrentDataVersion,
                    Guides = _guides.Values.ToList()
                };
                string json = JsonConvert.SerializeObject(root, _jsonSettings);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _sapi.WorldManager.SaveGame.StoreData(StorageKey, bytes);
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[Layout] Failed to persist guides: {0}", e);
            }
        }

        // Reads guides back from the save blob, rebuilds each guide's shape over its (shared) control-point
        // list, re-derives phantoms, and rebuilds the count caches. Older records migrate by deserialization
        // defaults (missing Projection/Plane/IsFilled fall to Volumetric / default plane / hollow).
        private void Load()
        {
            try
            {
                byte[] bytes = _sapi.WorldManager.SaveGame.GetData(StorageKey);
                if (bytes == null || bytes.Length == 0)
                {
                    _sapi.Logger.Notification("[Layout] No saved guides found.");
                    return;
                }

                string json = Encoding.UTF8.GetString(bytes);
                var root = JsonConvert.DeserializeObject<PersistedRoot>(json, _jsonSettings);

                _guides.Clear();
                _shapes.Clear();
                _voxelCounts.Clear();
                _totalVoxels = 0;

                if (root?.Guides == null)
                {
                    _sapi.Logger.Warning("[Layout] Guide save blob was empty or unreadable; starting with no guides.");
                    return;
                }

                foreach (var g in root.Guides)
                {
                    if (g == null) continue;
                    if (g.ControlPoints == null) g.ControlPoints = new List<ControlPoint>();
                    g.DataVersion = GuideData.CurrentDataVersion; // normalize after default-driven migration

                    IGuideShape shape = ShapeFactory.Adopt(g);
                    shape.RecalculatePhantomPoints();             // restore phantoms before first sync

                    _guides[g.Id] = g;
                    _shapes[g.Id] = shape;
                    StoreCount(g.Id, shape.GetVoxelCount(g.VoxelScale, g.IsFilled));
                }

                _sapi.Logger.Notification("[Layout] Loaded {0} guide(s).", _guides.Count);
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[Layout] Failed to load guides; starting with none: {0}", e);
                _guides.Clear();
                _shapes.Clear();
                _voxelCounts.Clear();
                _totalVoxels = 0;
            }
        }

        // Versioned on-disk envelope. Voxels are never part of this — always re-derived from control points.
        private class PersistedRoot
        {
            public int Version { get; set; }
            public List<GuideData> Guides { get; set; }
        }
    }

    /// <summary>
    /// Newtonsoft converter for <see cref="Vec3d"/>, the one type in the guide model a stock serializer can't
    /// round-trip on its own. Serializes as a compact {x,y,z} object. Owned by GuideManager because the v2 plan
    /// assigns serializer configuration to it.
    /// </summary>
    internal sealed class Vec3dJsonConverter : JsonConverter<Vec3d>
    {
        public override void WriteJson(JsonWriter writer, Vec3d value, JsonSerializer serializer)
        {
            if (value == null) { writer.WriteNull(); return; }
            writer.WriteStartObject();
            writer.WritePropertyName("x"); writer.WriteValue(value.X);
            writer.WritePropertyName("y"); writer.WriteValue(value.Y);
            writer.WritePropertyName("z"); writer.WriteValue(value.Z);
            writer.WriteEndObject();
        }

        public override Vec3d ReadJson(JsonReader reader, Type objectType, Vec3d existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            JObject o = JObject.Load(reader);
            double x = o.Value<double?>("x") ?? 0.0;
            double y = o.Value<double?>("y") ?? 0.0;
            double z = o.Value<double?>("z") ?? 0.0;
            return new Vec3d(x, y, z);
        }
    }
}
