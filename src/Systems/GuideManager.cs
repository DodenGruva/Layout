using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
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
        RejectedGuideLocked,
        /// <summary>
        /// Rejected because the resulting public guide would occupy a block where the acting player does
        /// not have build permission. The attempted mutation has already been rolled back.
        /// </summary>
        RejectedClaimAccess,
        /// <summary>
        /// Rejected because the guide voxelises to NOTHING — no voxels at all, so there would be nothing to
        /// see and nothing to build against.
        /// </summary>
        /// <remarks>
        /// ⚠️ APPENDED LAST ON PURPOSE (<c>GOTCHAS</c> G1). This enum's integer value crosses the wire in
        /// <c>GuidePlacementRejectedPacket</c>; inserting a member anywhere else would renumber every
        /// status after it for older clients. (Today's client ignores the field, but that is not a licence
        /// to renumber — it is only the reason this is safe to add at all.)
        ///
        /// Added v0.4.37 after a playtest found the hole: a shape whose frame check fails returns a count
        /// of zero and an empty voxel set, and zero passes EVERY cap — so the guide was created, persisted,
        /// listed at 0 voxels, and drawn as nothing, with no message. It is also the vector A14.8 names,
        /// where a degenerate pushed guide costs zero voxels and evades the whole budget.
        /// </remarks>
        RejectedEmpty
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

        /// <summary>The first protected block found for a claim-access rejection; otherwise null.</summary>
        public BlockPos DeniedPosition { get; }

        private GuideOperationResult(GuideOpStatus status, GuideData guide, int voxelCount, int capLimit,
            int controlPointIndex, BlockPos deniedPosition = null)
        {
            Status = status;
            Guide = guide;
            VoxelCount = voxelCount;
            CapLimit = capLimit;
            ControlPointIndex = controlPointIndex;
            DeniedPosition = deniedPosition;
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

        public static GuideOperationResult ClaimDenied(GuideData guide, BlockPos position) =>
            new GuideOperationResult(GuideOpStatus.RejectedClaimAccess, guide, 0, 0, -1,
                position);

        /// <summary>The guide contains no voxels at all — nothing to draw, nothing to build against.</summary>
        public static GuideOperationResult Empty(GuideData guide) =>
            new GuideOperationResult(GuideOpStatus.RejectedEmpty, guide, 0, 0, -1);

        public override string ToString() =>
            $"GuideOperationResult({Status}, guide={(Guide != null ? Guide.Id.ToString() : "none")}, " +
            $"voxels={VoxelCount}, cap={CapLimit}, cpIndex={ControlPointIndex}, denied={DeniedPosition})";
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
    /// A not-yet-authoritative create candidate. Its shape and guide record are isolated from the live
    /// manager, so expensive counting can happen away from the server tick before an atomic commit.
    /// </summary>
    internal sealed class PreparedGuideCreation
    {
        public GuideData Guide { get; }
        public IGuideShape Shape { get; }

        public PreparedGuideCreation(GuideData guide, IGuideShape shape)
        {
            Guide = guide ?? throw new ArgumentNullException(nameof(guide));
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        }

        public int CountUpTo(int stopAfter) => CountUpTo(stopAfter, null);

        public int CountUpTo(int stopAfter, Func<bool> cancellationRequested)
        {
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            if (!Guide.IsWireframe)
                return GuideShapeVoxelCounting.CountUpTo(
                    Shape, Guide.VoxelScale, Guide.IsFilled, stopAfter,
                    cancellationRequested);

            int count = ShapeWireframe.GetVoxelCount(Shape, Guide.VoxelScale);
            VoxelScanCancellation.ThrowIfRequested(cancellationRequested);
            return count;
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
    /// would breach the per-guide, creator-total, or world-total cap, the change is rolled back to the
    /// pre-mutation snapshot and the
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

        /// <summary>
        /// Hard voxel ceiling enforced INDEPENDENT of the configurable caps (v0.1.26). A shape whose voxel
        /// scan exceeds its guard returns a huge sentinel count (~536M) from <c>GetVoxelCount</c> and an
        /// EMPTY voxel set — i.e. it can't be rendered. Real guides never exceed a few million (the scan
        /// guard caps the lattice scan itself), so anything above this ceiling is that "too big to
        /// voxelise" sentinel and is ALWAYS rejected — otherwise a server with caps disabled (0 =
        /// unlimited) could create INVISIBLE giant guides that also dump the sentinel onto the running
        /// total and silently max the world budget.
        /// </summary>
        public const int HardVoxelCeiling = 10_000_000;

        /// <summary>
        /// Exact volume size above which creation/refinement crosses to the shared background pipeline.
        /// The server and client must use one value: the server owns public placement feedback below this
        /// line, while the client owns it after a streamed placement finishes above it.
        /// </summary>
        public const int BackgroundVolumeVoxelThreshold = 8000;

        private readonly IGuidePersistence _persistence;
        private readonly IGuideBlockProbe _blockProbe;
        private readonly ILogger _logger;
        // NOT readonly since v0.4.16: the settings page's Admin section changes these in play, through
        // ApplyCaps below. Still write-once-per-change on the server main thread — see that method.
        private int _perGuideVoxelCap;
        private int _totalVoxelCap;
        private int _perPlayerTotalVoxelCap;
        private int _maxGuidesPerPlayer;
        private int _maxGuidesWorldWide;
        private readonly System.Func<string, int> _playerGuideLimitResolver;
        private readonly System.Func<string, int> _playerTotalVoxelCapResolver;
        // Server-main-thread operation scope. The network authority sets this around one player's mutation
        // so a personal per-guide cap can replace the server default without contaminating client authority.
        private int? _operationPerGuideVoxelCap;
        private GuideMutationAccessValidator _operationAccessValidator;
        // Something has changed since the last successful write. Set by MarkDirty on every mutation,
        // cleared by Persist. Server main thread only, like every field above. See MarkDirty's remarks.
        private bool _persistDirty;
        // The background serialisation currently running on a worker, or null. Main thread only — a worker
        // NEVER touches this field, it only reads its own job. Two things depend on it, both load-bearing:
        // it stops a second worker starting, and it is the identity a completing job must match before it
        // is allowed to store anything (GOTCHAS G29). See BeginBackgroundPersist.
        private GuidePersistJob _persistInFlight;

        private readonly Dictionary<Guid, GuideData> _guides = new Dictionary<Guid, GuideData>();
        private readonly Dictionary<Guid, IGuideShape> _shapes = new Dictionary<Guid, IGuideShape>();
        private readonly Dictionary<Guid, int> _voxelCounts = new Dictionary<Guid, int>();
        // LONG, not int (v0.1.27): each guide's own count fits an int (the hard ceiling caps a single guide
        // at ~10M), but the SUM across many guides can pass int's ~2.1B limit on a caps-disabled server —
        // and a wrapped-negative total would read as "under budget" and disable the cap checks. A long
        // holds ~9 quintillion, so the running total can never overflow in practice.
        private long _totalVoxels;

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
        public long TotalVoxelCount => _totalVoxels;

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

        /// <summary>
        /// Max voxels attributed to one guide creator across all of their currently existing guides.
        /// 0 = unlimited.
        /// </summary>
        public int PerPlayerTotalVoxelCap => _perPlayerTotalVoxelCap;

        /// <summary>Max guides one player may have created at once; 0 = unlimited. Server config (Module 7).</summary>
        public int MaxGuidesPerPlayer => _maxGuidesPerPlayer;

        /// <summary>Max guides that may exist world-wide at once; 0 = unlimited. Server config (Module 7).</summary>
        public int MaxGuidesWorldWide => _maxGuidesWorldWide;

        /// <summary>Number of guides currently loaded (the figure the world-wide count cap checks against).</summary>
        public int GuideCount => _guides.Count;

        /// <summary>
        /// Replaces the five cap values in play (v0.4.16, the settings page's Admin section). Non-positive
        /// means unlimited, matching the config's own semantics.
        /// </summary>
        /// <remarks>
        /// LOWERING A CAP NEVER DELETES ANYTHING. The caps are entry conditions checked when a guide is
        /// created or grown, so guides already over a newly-lowered cap keep existing and keep rendering;
        /// they simply cannot grow, exactly like the existing over-cap guides the cumulative budget already
        /// tolerates. Dropping a cap and expecting the world to tidy itself would be a destructive surprise
        /// from a settings panel, and <c>/layout dispel</c> is the deliberate tool for that.
        ///
        /// Server main thread only, which is where every network handler and command already runs. The
        /// immense create/sculpt workers read their limit BEFORE going off-thread, so a change landing
        /// mid-operation cannot alter the rules an in-flight placement is being judged against.
        /// </remarks>
        public void ApplyCaps(int perGuideVoxelCap, int totalVoxelCap, int perPlayerTotalVoxelCap,
            int maxGuidesPerPlayer, int maxGuidesWorldWide)
        {
            _perGuideVoxelCap = perGuideVoxelCap > 0 ? perGuideVoxelCap : 0;
            _totalVoxelCap = totalVoxelCap > 0 ? totalVoxelCap : 0;
            _perPlayerTotalVoxelCap = perPlayerTotalVoxelCap > 0 ? perPlayerTotalVoxelCap : 0;
            _maxGuidesPerPlayer = maxGuidesPerPlayer > 0 ? maxGuidesPerPlayer : 0;
            _maxGuidesWorldWide = maxGuidesWorldWide > 0 ? maxGuidesWorldWide : 0;
        }

        /// <summary>
        /// Wires up persistence (load on save-game load, write on world-save) and stores the caps, which
        /// normally come from server config (<c>layout.json</c>). UNLIMITED SEMANTICS: a cap of 0 or any
        /// negative value means "unlimited" — that check is simply skipped. This applies to all five caps
        /// (three voxel caps and both guide-count caps); values are normalised to 0 here so "unlimited" has
        /// one representation. Existing guides always LOAD regardless of caps (caps gate new mutations, never
        /// eject persisted state a server admin lowered a cap underneath).
        /// </summary>
        public GuideManager(
            IGuidePersistence persistence,
            IGuideBlockProbe blockProbe,
            ILogger logger,
            int perGuideVoxelCap = 500000,
            int totalVoxelCap = 0,
            int perPlayerTotalVoxelCap = 1000000,
            int maxGuidesPerPlayer = 0,
            int maxGuidesWorldWide = 0,
            System.Func<string, int> playerGuideLimitResolver = null,
            System.Func<string, int> playerTotalVoxelCapResolver = null)
        {
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _blockProbe = blockProbe ?? throw new ArgumentNullException(nameof(blockProbe));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _perGuideVoxelCap = perGuideVoxelCap > 0 ? perGuideVoxelCap : 0;
            _totalVoxelCap = totalVoxelCap > 0 ? totalVoxelCap : 0;
            _perPlayerTotalVoxelCap =
                perPlayerTotalVoxelCap > 0 ? perPlayerTotalVoxelCap : 0;
            _maxGuidesPerPlayer = maxGuidesPerPlayer > 0 ? maxGuidesPerPlayer : 0;
            _maxGuidesWorldWide = maxGuidesWorldWide > 0 ? maxGuidesWorldWide : 0;
            _playerGuideLimitResolver = playerGuideLimitResolver;
            _playerTotalVoxelCapResolver = playerTotalVoxelCapResolver;
        }

        /// <summary>
        /// Temporarily replaces the configurable per-guide cap for one synchronous authority operation.
        /// The absolute hard ceiling and world-total cap are never replaced. Main-thread use only.
        /// </summary>
        public IDisposable UsePerGuideVoxelCap(int cap)
        {
            int? previous = _operationPerGuideVoxelCap;
            _operationPerGuideVoxelCap = cap > 0 ? cap : 0;
            return new ActionOnDispose(() => _operationPerGuideVoxelCap = previous);
        }

        /// <summary>
        /// Installs the acting player's build-access validator for one synchronous server operation.
        /// Passing null deliberately suppresses claim checks for cleanup/administrative rollback paths.
        /// </summary>
        public IDisposable UseMutationAccessValidator(GuideMutationAccessValidator validator)
        {
            GuideMutationAccessValidator previous = _operationAccessValidator;
            _operationAccessValidator = validator;
            return new ActionOnDispose(() => _operationAccessValidator = previous);
        }

        /// <summary>
        /// Records that the registry has changed and owes a save. **This is what every mutation calls.**
        /// </summary>
        /// <remarks>
        /// ⚠️ MUTATIONS MUST NEVER CALL <see cref="Persist"/> DIRECTLY. Persist re-serialises the WHOLE
        /// registry — every guide in the world, not the one that changed — so its cost tracks world size
        /// and not edit size. Calling it per mutation made a reshape drag, which sends about ten updates a
        /// second, re-serialise everything ten times a second: measured at ~4 ms per megabyte of guide
        /// data, that is ~20 ms of server main thread per drag update at a thousand guides, when a whole
        /// server tick is 20 ms. It was invisible on a small test world and crippling on a large one.
        ///
        /// Setting a bool instead costs nothing, and the actual write happens at the points that need it:
        /// the world save (where the data is about to hit disk anyway), a periodic flush, and shutdown.
        ///
        /// This also subsumes the old <c>BatchPersist</c> scope, which existed solely to stop the
        /// client-only push writing the registry once per guide (A14.8). A bulk operation now marks the
        /// same bool repeatedly and writes once, without needing a scope to say so.
        /// </remarks>
        private void MarkDirty() => _persistDirty = true;

        private int EffectivePerGuideVoxelCap => _operationPerGuideVoxelCap ?? _perGuideVoxelCap;

        /// <summary>
        /// Returns the cumulative cap currently effective for one original creator. The server may supply
        /// a persistent per-player override resolver; local/client authority falls back to its configured
        /// default. 0 means unlimited.
        /// </summary>
        public int EffectivePerPlayerTotalVoxelCap(string playerUid)
        {
            int resolved = _playerTotalVoxelCapResolver != null
                ? _playerTotalVoxelCapResolver(playerUid)
                : _perPlayerTotalVoxelCap;
            return resolved > 0 ? resolved : 0;
        }

        private GuideData AccessSnapshot(GuideData guide) =>
            _operationAccessValidator == null || guide == null ? null : guide.DeepClone();

        private bool AccessDenied(GuideData before, GuideData after, IGuideShape afterShape,
            out BlockPos deniedPosition)
        {
            deniedPosition = null;
            if (_operationAccessValidator == null) return false;
            GuideMutationAccessResult result = _operationAccessValidator(before, after, afterShape);
            deniedPosition = result.DeniedPosition;
            return !result.Allowed;
        }

        private int EffectivePlayerGuideLimit(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid)) return 0;
            int resolved = _playerGuideLimitResolver?.Invoke(playerUid) ?? _maxGuidesPerPlayer;
            return Math.Max(0, resolved);
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
        /// (bookkeeping, not ownership); <paramref name="creatorName"/> is its display-only HUD snapshot.
        /// Pass null when there is no acting player.
        /// </summary>
        public GuideOperationResult CreateGuide(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType = GuideShapeType.Arch,
            ShapeConstraint constraint = ShapeConstraint.None,
            PlaneAxis shapePlaneAxis = PlaneAxis.Y,
            string creatorUid = null,
            string creatorName = null,
            Vec3d thirdPoint = null,
            bool inverted = false,
            int sides = 0,
            IReadOnlyList<Vec3d> chain = null,
            bool closed = false,
            Vec3d fourthPoint = null,
            bool flatSideAligned = false)
        {
            if (start == null || end == null) return GuideOperationResult.Invalid();
            if (!GuideData.IsValidVoxelScale(settings.Scale)
                || !GuideBounds.IsUsableProjection(settings.Mode, settings.Plane))
                return GuideOperationResult.Invalid();

            // 3D volumes are always Volumetric, carry no division marks (0.1.20/0.1.21), and are always
            // HOLLOW shells (0.2.17 — exposed-face meshing made filled interiors emit no geometry, so fill
            // bought nothing visible at an R³ voxel cost) — normalise defensively whatever a client sends.
            if (GuideShapeTypes.IsVolume(shapeType)
                && (settings.Mode == ProjectionMode.Surface || settings.Divisions != 0 || settings.Filled))
                settings = new GuideRenderSettings(settings.Scale, ProjectionMode.Volumetric,
                    settings.Plane, false, 0, settings.Wireframe);
            else if (!GuideShapeTypes.IsVolume(shapeType) && settings.Wireframe)
                settings = new GuideRenderSettings(settings.Scale, settings.Mode,
                    settings.Plane, settings.Filled, settings.Divisions, false);

            // Guide-COUNT caps first — cheap, and nothing has been built yet (Guide is null in the result).
            if (_maxGuidesWorldWide > 0 && _guides.Count >= _maxGuidesWorldWide)
                return GuideOperationResult.OverGuideCount(null, _guides.Count, _maxGuidesWorldWide);
            int playerGuideLimit = EffectivePlayerGuideLimit(creatorUid);
            if (playerGuideLimit > 0 && CountGuidesBy(creatorUid) >= playerGuideLimit)
                return GuideOperationResult.OverGuideCount(null, CountGuidesBy(creatorUid), playerGuideLimit);

            IGuideShape shape = ShapeFactory.Create(shapeType, constraint, shapePlaneAxis, start, end,
                inverted, sides, chain, closed, flatSideAligned);

            // Three-click shapes (Session 11 triangle; 0.1.21 cylinder/cone/box): the third click sets the
            // apex/height, stored at control point index 2 — and the four-click Tapered Cylinder's fourth
            // click sets the top radius at index 3 (0.2.24). Applied before the record is built so the
            // as-placed snapshot captures the true placed form. Shorter gestures send no later points.
            DraftManager.ApplyPlacementPoints(shape, shapeType, shape.Constraint, thirdPoint, fourthPoint);

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
                GuideShapeTypes.UsesSides(shapeType) ? Shapes.PolygonShape.ClampSides(sides) : 0,
                shape is Shapes.FreeShape fs && fs.IsClosed,
                GuideShapeTypes.UsesSides(shapeType) && flatSideAligned,
                GuideShapeTypes.IsVolume(shapeType) && settings.Wireframe);
            data.CreatorUid = creatorUid;
            data.CreatorName = CleanPlayerName(creatorName);
            data.LastSculptorUid = creatorUid;
            data.LastSculptorName = data.CreatorName;

            int count = CountForCaps(
                data.Id, shape, data.VoxelScale, data.IsFilled, data.IsWireframe, creatorUid);
            // A GUIDE MADE OF NOTHING IS NOT A GUIDE. Every shape's counter returns 0 when its frame check
            // fails (BoxShape's MinSide, and the same idiom in its siblings), and its voxel set comes back
            // empty to match — so zero sailed through every cap, including a per-guide cap of 5,000, and
            // the player got an invisible guide listed at 0 voxels with no message at all. Refuse it here,
            // where the count is first known, rather than storing it and puzzling everyone later.
            if (count <= 0) return GuideOperationResult.Empty(data);
            if (count > HardVoxelCeiling)                        // scan-guard sentinel — too big to render
                return GuideOperationResult.OverCap(data, count, HardVoxelCeiling);
            int perGuideCap = EffectivePerGuideVoxelCap;
            if (perGuideCap > 0 && count > perGuideCap)
                return GuideOperationResult.OverCap(data, count, perGuideCap);
            if (WouldExceedPlayerTotal(
                data.CreatorUid, data.Id, count, out int playerTotalCap))
                return GuideOperationResult.OverCap(data, count, playerTotalCap);
            if (_totalVoxelCap > 0 && _totalVoxels + count > _totalVoxelCap)
                return GuideOperationResult.OverCap(data, count, _totalVoxelCap);

            if (AccessDenied(null, data, shape, out BlockPos deniedPosition))
                return GuideOperationResult.ClaimDenied(data, deniedPosition);

            _guides[data.Id] = data;
            _shapes[data.Id] = shape;
            StoreCount(data.Id, count);
            MarkDirty();
            return GuideOperationResult.Success(data, count);
        }

        /// <summary>
        /// Builds a create candidate without counting voxels, testing claims, persisting, or touching live
        /// manager state. Cheap guide-count limits are checked here and again at commit because a queued
        /// immense placement can wait behind another request.
        /// </summary>
        internal bool TryPrepareGuideCreation(
            Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType, ShapeConstraint constraint, PlaneAxis shapePlaneAxis,
            string creatorUid, string creatorName, Vec3d thirdPoint, bool inverted, int sides,
            IReadOnlyList<Vec3d> chain, bool closed, Vec3d fourthPoint, bool flatSideAligned,
            out PreparedGuideCreation prepared, out GuideOperationResult failure)
        {
            prepared = null;
            failure = default;
            if (start == null || end == null || !GuideData.IsValidVoxelScale(settings.Scale)
                || !GuideBounds.IsUsableProjection(settings.Mode, settings.Plane))
            {
                failure = GuideOperationResult.Invalid();
                return false;
            }

            bool volume = GuideShapeTypes.IsVolume(shapeType);
            if (volume && (settings.Mode == ProjectionMode.Surface
                || settings.Divisions != 0 || settings.Filled))
                settings = new GuideRenderSettings(settings.Scale, ProjectionMode.Volumetric,
                    settings.Plane, false, 0, settings.Wireframe);
            else if (!volume && settings.Wireframe)
                settings = new GuideRenderSettings(settings.Scale, settings.Mode,
                    settings.Plane, settings.Filled, settings.Divisions, false);

            GuideOperationResult countLimit = CheckCreateGuideCountLimits(creatorUid);
            if (countLimit.Status == GuideOpStatus.RejectedOverGuideCount)
            {
                failure = countLimit;
                return false;
            }

            IGuideShape shape;
            try
            {
                shape = ShapeFactory.Create(shapeType, constraint, shapePlaneAxis, start, end,
                    inverted, sides, chain, closed, flatSideAligned);
                DraftManager.ApplyPlacementPoints(
                    shape, shapeType, shape.Constraint, thirdPoint, fourthPoint);
            }
            catch (Exception)
            {
                failure = GuideOperationResult.Invalid();
                return false;
            }

            var data = GuideData.Create(
                shapeType,
                shape.ControlPoints,
                settings.Scale,
                settings.Mode,
                settings.Plane,
                settings.Filled,
                shape.Constraint,
                shapePlaneAxis,
                settings.Divisions,
                GuideShapeTypes.UsesSides(shapeType) ? Shapes.PolygonShape.ClampSides(sides) : 0,
                shape is Shapes.FreeShape fs && fs.IsClosed,
                GuideShapeTypes.UsesSides(shapeType) && flatSideAligned,
                volume && settings.Wireframe);
            data.CreatorUid = creatorUid;
            data.CreatorName = CleanPlayerName(creatorName);
            data.LastSculptorUid = creatorUid;
            data.LastSculptorName = data.CreatorName;
            prepared = new PreparedGuideCreation(data, shape);
            return true;
        }

        /// <summary>
        /// Atomically publishes a prepared candidate after its exact count is known. All volatile limits are
        /// rechecked. A caller may skip the ordinary all-at-once access validator only after completing the
        /// candidate's full claim footprint through an equivalent main-thread validation pipeline.
        /// </summary>
        internal GuideOperationResult CommitPreparedGuideCreation(
            PreparedGuideCreation prepared, int exactCount, bool validateAccess)
        {
            if (prepared?.Guide == null || prepared.Shape == null || exactCount < 0)
                return GuideOperationResult.Invalid(prepared?.Guide);

            GuideData data = prepared.Guide;
            if (_guides.ContainsKey(data.Id)) return GuideOperationResult.Invalid(data);
            if (!GuideBounds.IsUsableProjection(data.Projection, data.Plane))
                return GuideOperationResult.Invalid(data);

            GuideOperationResult countLimit = CheckCreateGuideCountLimits(data.CreatorUid);
            if (countLimit.Status == GuideOpStatus.RejectedOverGuideCount) return countLimit;

            // Nothing to draw — same rule as CreateGuide, applied to the immense path's committed count.
            if (exactCount <= 0) return GuideOperationResult.Empty(data);
            if (exactCount > HardVoxelCeiling)
                return GuideOperationResult.OverCap(data, exactCount, HardVoxelCeiling);
            int perGuideCap = EffectivePerGuideVoxelCap;
            if (perGuideCap > 0 && exactCount > perGuideCap)
                return GuideOperationResult.OverCap(data, exactCount, perGuideCap);
            if (WouldExceedPlayerTotal(
                data.CreatorUid, data.Id, exactCount, out int playerTotalCap))
                return GuideOperationResult.OverCap(
                    data, exactCount, playerTotalCap);
            if (_totalVoxelCap > 0 && _totalVoxels + exactCount > _totalVoxelCap)
                return GuideOperationResult.OverCap(data, exactCount, _totalVoxelCap);

            if (validateAccess
                && AccessDenied(null, data, prepared.Shape, out BlockPos deniedPosition))
                return GuideOperationResult.ClaimDenied(data, deniedPosition);

            _guides[data.Id] = data;
            _shapes[data.Id] = prepared.Shape;
            StoreCount(data.Id, exactCount);
            MarkDirty();
            return GuideOperationResult.Success(data, exactCount);
        }

        private GuideOperationResult CheckCreateGuideCountLimits(string creatorUid)
        {
            if (_maxGuidesWorldWide > 0 && _guides.Count >= _maxGuidesWorldWide)
                return GuideOperationResult.OverGuideCount(
                    null, _guides.Count, _maxGuidesWorldWide);

            int playerGuideLimit = EffectivePlayerGuideLimit(creatorUid);
            if (playerGuideLimit > 0)
            {
                int playerGuideCount = CountGuidesBy(creatorUid);
                if (playerGuideCount >= playerGuideLimit)
                    return GuideOperationResult.OverGuideCount(
                        null, playerGuideCount, playerGuideLimit);
            }
            return GuideOperationResult.Success(null, 0);
        }

        /// <summary>Records the player behind the latest committed visible change. Selection, hover,
        /// rejected/no-op operations, and cancelled grabs never call this seam.</summary>
        public bool StampLastSculptor(Guid id, string playerUid, string playerName)
        {
            if (!_guides.TryGetValue(id, out GuideData guide)) return false;

            string cleanName = CleanPlayerName(playerName);
            bool changed = guide.LastSculptorUid != playerUid || guide.LastSculptorName != cleanName;
            guide.LastSculptorUid = playerUid;
            guide.LastSculptorName = cleanName;
            guide.DataVersion = GuideData.CurrentDataVersion;
            if (changed) MarkDirty();
            return true;
        }

        private static string CleanPlayerName(string playerName) =>
            string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName.Trim();

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
            RemoveInactiveLockMarkers(live.ControlPoints);
            // Restore normally receives an internal undo snapshot, but client-only push deliberately reuses
            // it as an import seam. Reject an invalid client-supplied scale before shape sampling can perform
            // arithmetic with it. Normal guides and every internal undo snapshot already use these values.
            if (!GuideData.IsValidVoxelScale(live.VoxelScale))
                return GuideOperationResult.Invalid(live);
            // Range check BEFORE the shape is adopted and counted (GOTCHAS G31). This is the seam the
            // client-only "push" import comes through, and CountForCaps below is exactly the scan that
            // never terminates on an out-of-world coordinate.
            if (!GuideBounds.AllUsable(live.ControlPoints)
                || !GuideBounds.AllUsable(live.OriginalControlPoints)
                || !GuideBounds.IsUsableProjection(live.Projection, live.Plane))
                return GuideOperationResult.Invalid(live);
            IGuideShape shape = ShapeFactory.Adopt(live);
            shape.RecalculatePhantomPoints();

            // Guide-count caps apply to a restore too (an undo-of-delete / redo-of-create is a (re-)creation):
            // hitting one yields the same push-back-and-warn Blocked path as a voxel-cap rejection would.
            if (_maxGuidesWorldWide > 0 && _guides.Count >= _maxGuidesWorldWide)
                return GuideOperationResult.OverGuideCount(live, _guides.Count, _maxGuidesWorldWide);
            int playerGuideLimit = EffectivePlayerGuideLimit(live.CreatorUid);
            if (playerGuideLimit > 0 && CountGuidesBy(live.CreatorUid) >= playerGuideLimit)
                return GuideOperationResult.OverGuideCount(live, CountGuidesBy(live.CreatorUid), playerGuideLimit);

            int count = CountForCaps(
                live.Id, shape, live.VoxelScale, live.IsFilled, live.IsWireframe,
                live.CreatorUid);
            // Zero voxels — nothing to render. This is also the import seam for the client-only "push", and
            // a degenerate pushed guide costs zero voxels, so without this it evades every voxel budget
            // outright (A14.8's stated vector) while still consuming a registry slot and a save entry.
            if (count <= 0) return GuideOperationResult.Empty(live);
            // Restore is also the import seam used by client-only "push". Keep the absolute rendering
            // safeguard identical to normal creation even when an administrator disables configurable
            // per-guide, creator-total, and world caps; otherwise a client-supplied snapshot could bypass
            // the ceiling.
            if (count > HardVoxelCeiling)
                return GuideOperationResult.OverCap(live, count, HardVoxelCeiling);
            int perGuideCap = EffectivePerGuideVoxelCap;
            if (perGuideCap > 0 && count > perGuideCap)
                return GuideOperationResult.OverCap(live, count, perGuideCap);
            if (WouldExceedPlayerTotal(
                live.CreatorUid, live.Id, count, out int playerTotalCap))
                return GuideOperationResult.OverCap(live, count, playerTotalCap);
            if (_totalVoxelCap > 0 && _totalVoxels + count > _totalVoxelCap)
                return GuideOperationResult.OverCap(live, count, _totalVoxelCap);

            if (AccessDenied(null, live, shape, out BlockPos deniedPosition))
                return GuideOperationResult.ClaimDenied(live, deniedPosition);

            _guides[live.Id] = live;
            _shapes[live.Id] = shape;
            StoreCount(live.Id, count);
            MarkDirty();
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
            GuideData accessBefore = AccessSnapshot(g);

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
            // A body lock begins as a non-deforming marker. The first real reshape promotes it to an
            // ordinary spline knot as part of that active gesture, so it can constrain the edited curve.
            PromoteLockedMarkers(g);
            for (int i = 0; i < edits.Count; i++)
                shape.MoveControlPoint(edits[i].Index, edits[i].Position);

            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled);
            if (count <= 0)
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.Empty(g);
            }
            if (WouldExceedCaps(id, count, out int cap))
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
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
        public GuideOperationResult InsertControlPoint(Guid id, float t, Vec3d position, bool isLockMarker = false)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (position == null) return GuideOperationResult.Invalid(g);
            if (!GuideShapeTypes.SupportsBodyInsert(g.ShapeType))
                return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];
            GuideData accessBefore = AccessSnapshot(g);

            var snapshot = SnapshotPoints(g);
            int insertedIndex;
            if (isLockMarker && shape is ArchShape arch)
                insertedIndex = arch.InsertLockMarker(t, position);
            else
            {
                shape.InsertControlPoint(t, position);
                insertedIndex = shape.GetNearestControlPointIndex(position); // the just-inserted point
            }

            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count, insertedIndex);
        }

        private static void PromoteLockedMarkers(GuideData guide)
        {
            for (int i = 0; i < guide.ControlPoints.Count; i++)
            {
                ControlPoint point = guide.ControlPoints[i];
                if (point.IsLockMarker && point.IsLocked) point.IsLockMarker = false;
            }
        }

        private static void RemoveInactiveLockMarkers(List<ControlPoint> points)
        {
            if (points == null) return;
            points.RemoveAll(point => point != null && point.IsLockMarker && !point.IsLocked);
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
            GuideData accessBefore = AccessSnapshot(g);
            List<ControlPoint> snapshot = SnapshotPoints(g);

            ControlPoint cp = g.ControlPoints[index];
            if (cp.IsPhantom || cp.IsAnchor || cp.IsPrimary) return GuideOperationResult.Invalid(g);

            g.ControlPoints.RemoveAt(index);     // mutates the shared list in place
            shape.RecalculatePhantomPoints();    // restore validity after an external list change

            int count = ExactVoxelCount(id, shape, g.VoxelScale, g.IsFilled);
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                RestorePoints(id, snapshot);
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }
            StoreCount(id, count);
            MarkDirty();
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
            MarkDirty();
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
            MarkDirty();
            return GuideOperationResult.Success(g, 0);
        }

        // --- Per-guide property toggles -----------------------------------------------------------

        /// <summary>Sets a guide's hidden flag (hidden guides render as anchors-only). Voxel count is unaffected.</summary>
        public GuideOperationResult SetHidden(Guid id, bool hidden)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            // NO-OP GUARD. Persist() re-serialises the WHOLE registry, so setting hidden to the value it
            // already has was a full save per packet — and nothing rate-limits packets. Success either way;
            // the caller decides whether anything is worth broadcasting.
            if (g.IsHidden == hidden)
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var same) ? same : 0);
            g.IsHidden = hidden;
            MarkDirty();
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
            if (!GuideBounds.IsUsableProjection(mode, plane))
                return GuideOperationResult.Invalid(g);
            // A 3D volume cannot go Surface (0.1.20/0.1.21) — the GUI greys the row; server-side gate.
            if (GuideShapeTypes.IsVolume(g.ShapeType) && mode == ProjectionMode.Surface)
                return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];
            GuideData accessBefore = AccessSnapshot(g);

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

            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.Projection = oldMode;
                g.Plane = oldPlane;
                if (bakeRollback != null) RestorePoints(id, bakeRollback);
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.Projection = oldMode;
                g.Plane = oldPlane;
                if (bakeRollback != null) RestorePoints(id, bakeRollback);
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        // Which side of a Surface plane is open air, by world solidity at the guide's own points: probes
        // the block half a block out on each side of the plane at every non-phantom control point and
        // votes. +1 = the positive side is airier, −1 = the negative side. Ties (free-floating planes,
        // fully buried, or unloaded chunks) fall back to +1 — the pre-fix behaviour, harmless there.
        // This is the server-side mirror of the renderer's CountSolidProbes decal-side vote (B-S10-2).
        private int ProbeAirSide(GuideData g, ProjectionPlane plane)
        {
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
                    if (!_blockProbe.TryIsSolid(pos, out bool solid)) continue;   // unloaded: no vote
                    if (solid)
                    {
                        if (side == 0) solidsPos++; else solidsNeg++;
                    }
                }
            }
            return solidsPos <= solidsNeg ? 1 : -1;
        }

        /// <summary>
        /// Sets a polygon-based guide's side count (re-derives the outline; anchors are untouched).
        /// Polygonal volumes also re-seat their height/rim handles so odd/even centre changes preserve
        /// height and taper ratio. Clamped to 3..24; re-counts and reverts on a cap breach.
        /// </summary>
        public GuideOperationResult SetSides(Guid id, int sides)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (!GuideShapeTypes.UsesSides(g.ShapeType)) return GuideOperationResult.Invalid(g);
            GuideData accessBefore = AccessSnapshot(g);

            int oldSides = g.Sides;
            int clamped = Shapes.PolygonShape.ClampSides(sides);
            if (clamped == oldSides)
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c0) ? c0 : 0);

            List<ControlPoint> oldPoints = SnapshotPoints(g);
            bool taperedPrism = g.ShapeType == GuideShapeType.TaperedPolygonalPrism;
            if (g.ShapeType == GuideShapeType.PolygonalPrism || taperedPrism)
                Shapes.PolygonalPrismShape.ReseatForSideChange(
                    g.ControlPoints, g.ShapePlaneAxis, oldSides, clamped, taperedPrism,
                    g.FlatSideAligned);
            g.Sides = clamped;
            IGuideShape shape = ShapeFactory.Adopt(g);          // side count is baked into the shape view
            _shapes[id] = shape;

            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.Sides = oldSides;
                g.ControlPoints.Clear();
                foreach (ControlPoint point in oldPoints) g.ControlPoints.Add(point.Clone());
                _shapes[id] = ShapeFactory.Adopt(g);
                StoreCount(id, ExactVoxelCount(id, _shapes[id], g.VoxelScale, g.IsFilled));
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.Sides = oldSides;
                g.ControlPoints.Clear();
                foreach (ControlPoint point in oldPoints) g.ControlPoints.Add(point.Clone());
                _shapes[id] = ShapeFactory.Adopt(g);
                StoreCount(id, ExactVoxelCount(id, _shapes[id], g.VoxelScale, g.IsFilled));
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
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
            GuideData accessBefore = AccessSnapshot(g);

            List<ControlPoint> before = SnapshotPoints(g);
            g.ControlPoints.Clear();
            foreach (var cp in pointsSnapshot) g.ControlPoints.Add(cp.Clone());
            IGuideShape shape = ShapeFactory.Adopt(g);
            _shapes[id] = shape;
            shape.RecalculatePhantomPoints();

            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled, g.IsWireframe);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.ControlPoints.Clear();
                foreach (ControlPoint cp in before) g.ControlPoints.Add(cp.Clone());
                _shapes[id] = ShapeFactory.Adopt(g);
                _shapes[id].RecalculatePhantomPoints();
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.ControlPoints.Clear();
                foreach (ControlPoint cp in before) g.ControlPoints.Add(cp.Clone());
                _shapes[id] = ShapeFactory.Adopt(g);
                _shapes[id].RecalculatePhantomPoints();
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }
            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// F6 Move: slides the WHOLE guide by <paramref name="delta"/> world units, shape untouched. Every
        /// control point moves — locked ones included, because a lock constrains the guide's own geometry
        /// against reshaping and carries no relationship to anything outside the guide (the Session-20
        /// "adjacent lock" trouble was click targeting, not stored data). The as-placed spring-back snapshot
        /// moves with it, or a later spring-back would teleport the guide back to where it used to stand.
        /// </summary>
        /// <remarks>
        /// THE DELTA MUST BE A WHOLE NUMBER OF THE GUIDE'S OWN VOXELS, and this is enforced here rather than
        /// merely assumed at the call site. That restriction is what makes the operation cheap AND exact:
        /// cells quantise to <c>Floor(world*16/scale)*scale</c>, so shifting by an exact multiple of the
        /// scale remaps every cell one-for-one and the voxel count provably cannot change — the cached count
        /// is reused instead of rescanning a behemoth. A sub-voxel delta would instead move cells by a whole
        /// cell wherever it happened to cross a quantise boundary and by nothing elsewhere, deforming the
        /// rendered shell; it is refused, not rounded.
        ///
        /// A Surface guide's plane offset travels too. The points are flattened onto that plane when drawn,
        /// so moving them along the flattened axis without the plane would look like nothing happened.
        /// </remarks>
        public GuideOperationResult TranslateGuide(Guid id, Vec3d delta)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (delta == null) return GuideOperationResult.Invalid(g);

            int cached = _voxelCounts.TryGetValue(id, out int existing) ? existing : 0;
            if (!TryQuantiseTranslation(delta, g.VoxelScale, out int sx, out int sy, out int sz))
                return GuideOperationResult.Invalid(g);
            if (sx == 0 && sy == 0 && sz == 0) return GuideOperationResult.Success(g, cached);

            IGuideShape shape = _shapes[id];
            GuideData accessBefore = AccessSnapshot(g);
            ProjectionPlane planeBefore = g.Plane;

            if (!TryOffsetPlane(g.Plane, sx, sy, sz, out ProjectionPlane movedPlane))
                return GuideOperationResult.Invalid(g);

            OffsetPoints(g.ControlPoints, delta);
            OffsetPoints(g.OriginalControlPoints, delta);
            g.Plane = movedPlane;
            if (!GuideBounds.AllUsable(g.ControlPoints)
                || !GuideBounds.AllUsable(g.OriginalControlPoints)
                || !GuideBounds.IsUsableProjection(g.Projection, g.Plane))
            {
                var back = new Vec3d(-delta.X, -delta.Y, -delta.Z);
                OffsetPoints(g.ControlPoints, back);
                OffsetPoints(g.OriginalControlPoints, back);
                g.Plane = planeBefore;
                return GuideOperationResult.Invalid(g);
            }
            shape.RecalculatePhantomPoints();

            // No cap check: the count is unchanged by construction (see remarks), so a move can never push a
            // guide over a budget it was already inside. The CLAIM check is the real gate — the destination
            // is new ground and a move is a placement.
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                // Undo by translating BACK rather than restoring a snapshot: the snapshot path recounts, and
                // rescanning a behemoth to reject a move it never made would be the expensive half of nothing.
                var back = new Vec3d(-delta.X, -delta.Y, -delta.Z);
                OffsetPoints(g.ControlPoints, back);
                OffsetPoints(g.OriginalControlPoints, back);
                g.Plane = planeBefore;
                shape.RecalculatePhantomPoints();
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, cached);
            MarkDirty();
            return GuideOperationResult.Success(g, cached);
        }

        // Expresses a translation in whole 1/16 units and verifies each axis is a whole number of the guide's
        // own voxels. Returns false for anything finer, so a malformed or stale packet cannot deform a shell.
        private static bool TryQuantiseTranslation(Vec3d delta, int scale, out int sx, out int sy, out int sz)
        {
            sx = sy = sz = 0;
            if (scale <= 0) return false;
            return TryAxis(delta.X, scale, out sx)
                && TryAxis(delta.Y, scale, out sy)
                && TryAxis(delta.Z, scale, out sz);

            static bool TryAxis(double world, int scale, out int sixteenths)
            {
                double exact = world * 16.0;
                double rounded = Math.Round(exact);
                if (double.IsNaN(rounded) || double.IsInfinity(rounded)
                    || rounded < int.MinValue || rounded > int.MaxValue)
                {
                    sixteenths = 0;
                    return false;
                }
                sixteenths = (int)rounded;
                return Math.Abs(exact - rounded) <= 1e-6 && sixteenths % scale == 0;
            }
        }

        private static void OffsetPoints(List<ControlPoint> points, Vec3d delta)
        {
            if (points == null) return;
            foreach (ControlPoint cp in points)
            {
                if (cp?.WorldPosition == null) continue;
                Vec3d w = cp.WorldPosition;
                cp.SetPosition(w.X + delta.X, w.Y + delta.Y, w.Z + delta.Z);
            }
        }

        private static bool TryOffsetPlane(
            ProjectionPlane plane, int sx, int sy, int sz, out ProjectionPlane moved)
        {
            int along = plane.FlattenedAxis switch
            {
                PlaneAxis.X => sx,
                PlaneAxis.Z => sz,
                _ => sy
            };
            long offset = (long)plane.PlaneOffset + along;
            if (offset < int.MinValue || offset > int.MaxValue)
            {
                moved = plane;
                return false;
            }
            moved = along == 0
                ? plane
                : new ProjectionPlane(plane.FlattenedAxis, (int)offset);
            return true;
        }

        /// <summary>
        /// F12 Rotate: turns the WHOLE guide a quarter turn at a time about a world axis, shape untouched.
        /// Returns the pivot actually used through <paramref name="pivot"/> so the undo command can rotate
        /// back about the SAME point — recomputing it would drift, because a rotated shape's bounding centre
        /// is not generally where the original's was.
        /// </summary>
        /// <remarks>
        /// QUARTER TURNS ONLY, and about a world axis. At 90 degrees the voxel lattice maps onto itself, so
        /// the rendered cells of the turned guide are a clean remap of the originals — the same property
        /// that makes <see cref="TranslateGuide"/> exact. An arbitrary angle would leave control points off
        /// the lattice and the shell would resample rather than turn. The pivot is snapped to the guide's
        /// own voxel scale for the same reason.
        ///
        /// ORIENTATION STATE. Rotating the points alone is not enough — several fields encode facing and
        /// must be carried round with them, or the turned guide keeps describing its old orientation:
        ///   • <see cref="GuideData.ShapePlaneAxis"/>, the intrinsic plane of planar shapes and the axis a
        ///     volume's rise direction is derived from. A quarter turn maps the axis set onto itself.
        ///   • <see cref="GuideData.Plane"/> for Surface guides — both the flattened axis and the offset,
        ///     which is a world coordinate and has to be re-derived at the new orientation.
        /// The volumes need nothing further: a dome's rise is <c>BaseNormal(û, ShapePlaneAxis)</c> with its
        /// SIGN taken from which side the apex control point sits on, so rotating the apex carries the
        /// facing automatically and the canonical sign flip is absorbed.
        ///
        /// UNLIKE A TRANSLATION, THE VOXEL COUNT IS NOT GUARANTEED. The lattice maps onto itself, but the
        /// shape is regenerated from control points rather than from moved cells, and the in-plane frame
        /// (<c>ShapeGeometry.TryGetFrame</c>) sign-normalises m̂ toward world up. A polygon-family guide can
        /// therefore come back with its vertices in a different phase and a slightly different count. So
        /// this recounts and re-checks caps in full, exactly as <see cref="Rescale"/> does.
        /// </remarks>
        public GuideOperationResult RotateGuide(
            Guid id, PlaneAxis axis, int quarterTurns, ref Vec3d pivot)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();

            int turns = ((quarterTurns % 4) + 4) % 4;
            if (turns == 0)
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out int same) ? same : 0);
            if (!Enum.IsDefined(typeof(PlaneAxis), axis))
                return GuideOperationResult.Invalid(g);

            IGuideShape shape = _shapes[id];
            GuideData accessBefore = AccessSnapshot(g);
            List<ControlPoint> before = SnapshotPoints(g);
            List<ControlPoint> originalBefore = SnapshotOriginalPoints(g);
            PlaneAxis shapeAxisBefore = g.ShapePlaneAxis;
            ProjectionPlane planeBefore = g.Plane;

            pivot ??= RotationPivot(g);
            RotatePoints(g.ControlPoints, axis, turns, pivot);
            RotatePoints(g.OriginalControlPoints, axis, turns, pivot);
            g.ShapePlaneAxis = RotateAxis(g.ShapePlaneAxis, axis, turns);

            if (g.Projection == ProjectionMode.Surface)
                g.Plane = RotateProjectionPlane(g, planeBefore, axis, turns);
            if (!GuideBounds.AllUsable(g.ControlPoints)
                || !GuideBounds.AllUsable(g.OriginalControlPoints)
                || !GuideBounds.IsUsableProjection(g.Projection, g.Plane))
            {
                RestoreRotation(id, g, before, originalBefore, shapeAxisBefore, planeBefore);
                return GuideOperationResult.Invalid(g);
            }

            // Re-adopt: the shape captured its preferred axis at construction, so a changed ShapePlaneAxis
            // only takes effect through a fresh adoption.
            shape = ShapeFactory.Adopt(g);
            _shapes[id] = shape;
            shape.RecalculatePhantomPoints();

            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled, g.IsWireframe);
            if (WouldExceedCaps(id, count, out int cap))
            {
                RestoreRotation(id, g, before, originalBefore, shapeAxisBefore, planeBefore);
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, _shapes[id], out BlockPos deniedPosition))
            {
                RestoreRotation(id, g, before, originalBefore, shapeAxisBefore, planeBefore);
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        private void RestoreRotation(
            Guid id, GuideData g, List<ControlPoint> points, List<ControlPoint> originalPoints,
            PlaneAxis shapeAxis, ProjectionPlane plane)
        {
            g.ShapePlaneAxis = shapeAxis;
            g.Plane = plane;
            g.ControlPoints.Clear();
            foreach (ControlPoint cp in points) g.ControlPoints.Add(cp.Clone());
            g.OriginalControlPoints = originalPoints;
            _shapes[id] = ShapeFactory.Adopt(g);
            _shapes[id].RecalculatePhantomPoints();
        }

        private static List<ControlPoint> SnapshotOriginalPoints(GuideData g)
        {
            if (g.OriginalControlPoints == null) return null;
            var copy = new List<ControlPoint>(g.OriginalControlPoints.Count);
            foreach (ControlPoint cp in g.OriginalControlPoints)
                copy.Add(cp == null ? new ControlPoint() : cp.Clone());
            return copy;
        }

        // The centre of the guide's real control points, snapped to its own voxel lattice so a quarter turn
        // maps cells onto cells rather than between them.
        private static Vec3d RotationPivot(GuideData g)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int seen = 0;
            foreach (ControlPoint cp in g.ControlPoints)
            {
                Vec3d w = cp?.WorldPosition;
                if (w == null || cp.IsPhantom) continue;
                if (w.X < minX) minX = w.X; if (w.X > maxX) maxX = w.X;
                if (w.Y < minY) minY = w.Y; if (w.Y > maxY) maxY = w.Y;
                if (w.Z < minZ) minZ = w.Z; if (w.Z > maxZ) maxZ = w.Z;
                seen++;
            }
            if (seen == 0) return new Vec3d();

            double cell = Math.Max(1, g.VoxelScale) / 16.0;
            return new Vec3d(
                Math.Round((minX + maxX) * 0.5 / cell) * cell,
                Math.Round((minY + maxY) * 0.5 / cell) * cell,
                Math.Round((minZ + maxZ) * 0.5 / cell) * cell);
        }

        // Right-handed quarter turns about a world axis, applied about `pivot`. Vintage Story is Y-up with
        // +X east and +Z south.
        private static void RotatePoints(
            List<ControlPoint> points, PlaneAxis axis, int turns, Vec3d pivot)
        {
            if (points == null) return;
            foreach (ControlPoint cp in points)
            {
                Vec3d w = cp?.WorldPosition;
                if (w == null) continue;
                double dx = w.X - pivot.X, dy = w.Y - pivot.Y, dz = w.Z - pivot.Z;
                for (int t = 0; t < turns; t++)
                {
                    double nx = dx, ny = dy, nz = dz;
                    switch (axis)
                    {
                        case PlaneAxis.X: ny = -dz; nz = dy; break;
                        case PlaneAxis.Z: nx = -dy; ny = dx; break;
                        default:          nx = dz;  nz = -dx; break;   // about Y
                    }
                    dx = nx; dy = ny; dz = nz;
                }
                cp.SetPosition(pivot.X + dx, pivot.Y + dy, pivot.Z + dz);
            }
        }

        // Where a world axis ends up after the same quarter turns. Sign is irrelevant: PlaneAxis names an
        // axis, not a direction, and every consumer re-derives its own sign.
        private static PlaneAxis RotateAxis(PlaneAxis subject, PlaneAxis axis, int turns)
        {
            if (turns % 2 == 0 || subject == axis) return subject;   // 180 degrees maps every axis to itself
            switch (axis)
            {
                case PlaneAxis.X: return subject == PlaneAxis.Y ? PlaneAxis.Z : PlaneAxis.Y;
                case PlaneAxis.Z: return subject == PlaneAxis.X ? PlaneAxis.Y : PlaneAxis.X;
                default:          return subject == PlaneAxis.X ? PlaneAxis.Z : PlaneAxis.X;
            }
        }

        // A Surface guide's plane after the turn: the flattened axis maps like any other, and the offset is
        // re-derived from where the points now actually sit rather than transformed, which keeps it correct
        // regardless of how the sign fell out.
        private static ProjectionPlane RotateProjectionPlane(
            GuideData g, ProjectionPlane before, PlaneAxis axis, int turns)
        {
            PlaneAxis flattened = RotateAxis(before.FlattenedAxis, axis, turns);
            foreach (ControlPoint cp in g.ControlPoints)
            {
                Vec3d w = cp?.WorldPosition;
                if (w == null || cp.IsPhantom) continue;
                double coordinate = flattened switch
                {
                    PlaneAxis.X => w.X,
                    PlaneAxis.Z => w.Z,
                    _ => w.Y
                };
                return new ProjectionPlane(flattened, (int)Math.Floor(coordinate * 16.0));
            }
            return new ProjectionPlane(flattened, before.PlaneOffset);
        }

        /// <summary>
        /// F7/F8 Transform: applies an optional MIRROR and an optional TRANSLATION to a guide in one
        /// atomic step, so a compound pad action ("move left and flip") validates once and undoes once.
        /// <paramref name="mirrorAxis"/> is -1 for no mirror, else a <see cref="PlaneAxis"/> value.
        /// The pivot used for the mirror plane is returned so the undo command can reflect about the same
        /// plane rather than about the transformed guide's new centre.
        /// </summary>
        /// <remarks>
        /// MIRROR IS SIMPLER THAN ROTATE, and for a reason worth recording: an axis-aligned reflection maps
        /// every world axis onto ITSELF, so <see cref="GuideData.ShapePlaneAxis"/> and a Surface plane's
        /// flattened axis are both unchanged — only the plane's OFFSET can move, and only when the guide is
        /// flattened along the very axis being mirrored. Constraints survive too: a right triangle reflects
        /// to a right triangle, an equilateral to an equilateral. Rotate needed an axis remap; this does not.
        ///
        /// Order is MIRROR FIRST, then translate. The inverse is therefore translate back, then mirror about
        /// the same stored plane — a reflection is its own inverse only while the plane stays put.
        /// </remarks>
        public GuideOperationResult TransformGuide(
            Guid id, Vec3d delta, int mirrorAxis, ref Vec3d pivot)
            => TransformGuide(
                id, delta, mirrorAxis, PlaneAxis.Y, 0, inverseOrder: false, ref pivot);

        /// <summary>
        /// Applies one complete in-place Transform-pad action transactionally. Forward order preserves the
        /// shipped behaviour: rotate, mirror, translate. <paramref name="inverseOrder"/> reverses that order
        /// for undo: translate back, mirror back, rotate back. Either order validates and commits once.
        /// </summary>
        public GuideOperationResult TransformGuide(
            Guid id, Vec3d delta, int mirrorAxis, PlaneAxis rotateAxis, int quarterTurns,
            bool inverseOrder, ref Vec3d pivot)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            bool mirroring = mirrorAxis >= 0;
            int turns = ((quarterTurns % 4) + 4) % 4;
            bool rotating = turns != 0;
            bool translating = delta != null && (delta.X != 0 || delta.Y != 0 || delta.Z != 0);
            if (mirrorAxis < -1
                || (mirroring && !Enum.IsDefined(typeof(PlaneAxis), (PlaneAxis)mirrorAxis))
                || (rotating && !Enum.IsDefined(typeof(PlaneAxis), rotateAxis)))
                return GuideOperationResult.Invalid(g);
            if (!mirroring && !rotating && !translating)
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out int same) ? same : 0);
            if (translating && !TryQuantiseTranslation(delta, g.VoxelScale, out _, out _, out _))
                return GuideOperationResult.Invalid(g);

            GuideData accessBefore = AccessSnapshot(g);
            List<ControlPoint> before = SnapshotPoints(g);
            List<ControlPoint> originalBefore = SnapshotOriginalPoints(g);
            PlaneAxis shapeAxisBefore = g.ShapePlaneAxis;
            ProjectionPlane planeBefore = g.Plane;

            pivot ??= RotationPivot(g);
            if (!GuideBounds.IsUsable(pivot)) return GuideOperationResult.Invalid(g);
            Vec3d appliedPivot = pivot;

            void ApplyRotation()
            {
                if (!rotating) return;
                RotatePoints(g.ControlPoints, rotateAxis, turns, appliedPivot);
                RotatePoints(g.OriginalControlPoints, rotateAxis, turns, appliedPivot);
                g.ShapePlaneAxis = RotateAxis(g.ShapePlaneAxis, rotateAxis, turns);
            }

            void ApplyMirror()
            {
                if (!mirroring) return;
                MirrorPoints(g.ControlPoints, (PlaneAxis)mirrorAxis, appliedPivot);
                MirrorPoints(g.OriginalControlPoints, (PlaneAxis)mirrorAxis, appliedPivot);
            }

            void ApplyTranslation()
            {
                if (!translating) return;
                OffsetPoints(g.ControlPoints, delta);
                OffsetPoints(g.OriginalControlPoints, delta);
            }

            if (inverseOrder)
            {
                ApplyTranslation();
                ApplyMirror();
                ApplyRotation();
            }
            else
            {
                ApplyRotation();
                ApplyMirror();
                ApplyTranslation();
            }

            if (g.Projection == ProjectionMode.Surface)
            {
                PlaneAxis flattened = rotating
                    ? RotateAxis(planeBefore.FlattenedAxis, rotateAxis, turns)
                    : planeBefore.FlattenedAxis;
                g.Plane = RederiveSurfacePlane(g, flattened, planeBefore);
            }
            if (!GuideBounds.AllUsable(g.ControlPoints)
                || !GuideBounds.AllUsable(g.OriginalControlPoints)
                || !GuideBounds.IsUsableProjection(g.Projection, g.Plane))
            {
                RestoreRotation(id, g, before, originalBefore, shapeAxisBefore, planeBefore);
                return GuideOperationResult.Invalid(g);
            }

            IGuideShape shape = ShapeFactory.Adopt(g);
            _shapes[id] = shape;
            shape.RecalculatePhantomPoints();

            // A mirror can re-phase a polygon exactly as a rotation can (the in-plane frame sign-normalises
            // toward world up), so this recounts rather than reusing the cached figure the way a pure
            // translation may.
            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled, g.IsWireframe);
            if (WouldExceedCaps(id, count, out int cap))
            {
                RestoreRotation(id, g, before, originalBefore, shapeAxisBefore, planeBefore);
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, _shapes[id], out BlockPos deniedPosition))
            {
                RestoreRotation(id, g, before, originalBefore, shapeAxisBefore, planeBefore);
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// F8 Copy: inserts a NEW guide that is <paramref name="id"/> optionally mirrored, optionally
        /// rotated, and offset by <paramref name="delta"/>. Validation is <see cref="RestoreGuide"/>'s —
        /// guide-count caps, the hard ceiling, per-guide and creator-total voxel caps, and land claims — so
        /// a copy is refused on exactly the terms a placement is, which the other transform actions are not.
        /// </summary>
        public GuideOperationResult CopyGuide(
            Guid id, Vec3d delta, int mirrorAxis, PlaneAxis rotateAxis, int quarterTurns,
            string creatorUid, string creatorName)
        {
            if (!_guides.TryGetValue(id, out var source)) return GuideOperationResult.NotFound();

            int turns = ((quarterTurns % 4) + 4) % 4;
            if (mirrorAxis < -1
                || (mirrorAxis >= 0 && !Enum.IsDefined(typeof(PlaneAxis), (PlaneAxis)mirrorAxis))
                || (turns != 0 && !Enum.IsDefined(typeof(PlaneAxis), rotateAxis))
                || (delta != null
                    && !TryQuantiseTranslation(delta, source.VoxelScale, out _, out _, out _)))
                return GuideOperationResult.Invalid(source);

            GuideData clone = source.DeepClone();
            clone.Id = Guid.NewGuid();
            clone.CreatorUid = creatorUid;
            clone.CreatorName = CleanPlayerName(creatorName);
            clone.LastSculptorUid = creatorUid;
            clone.LastSculptorName = clone.CreatorName;

            Vec3d pivot = RotationPivot(clone);
            if (mirrorAxis >= 0)
            {
                MirrorPoints(clone.ControlPoints, (PlaneAxis)mirrorAxis, pivot);
                MirrorPoints(clone.OriginalControlPoints, (PlaneAxis)mirrorAxis, pivot);
            }
            if (turns != 0)
            {
                RotatePoints(clone.ControlPoints, rotateAxis, turns, pivot);
                RotatePoints(clone.OriginalControlPoints, rotateAxis, turns, pivot);
                clone.ShapePlaneAxis = RotateAxis(clone.ShapePlaneAxis, rotateAxis, turns);
            }
            if (delta != null)
            {
                OffsetPoints(clone.ControlPoints, delta);
                OffsetPoints(clone.OriginalControlPoints, delta);
            }
            if (clone.Projection == ProjectionMode.Surface)
                clone.Plane = RederiveSurfacePlane(
                    clone, RotateAxis(clone.Plane.FlattenedAxis, rotateAxis, turns), clone.Plane);

            return RestoreGuide(clone);
        }

        // Reflects each point's `axis` coordinate about the pivot's. An axis-aligned reflection needs no
        // axis remap, unlike a rotation — see TransformGuide's remarks.
        private static void MirrorPoints(List<ControlPoint> points, PlaneAxis axis, Vec3d pivot)
        {
            if (points == null) return;
            foreach (ControlPoint cp in points)
            {
                Vec3d w = cp?.WorldPosition;
                if (w == null) continue;
                switch (axis)
                {
                    case PlaneAxis.X: cp.SetPosition(2 * pivot.X - w.X, w.Y, w.Z); break;
                    case PlaneAxis.Z: cp.SetPosition(w.X, w.Y, 2 * pivot.Z - w.Z); break;
                    default:          cp.SetPosition(w.X, 2 * pivot.Y - w.Y, w.Z); break;
                }
            }
        }

        // Re-derives a Surface guide's plane offset from where its points now actually sit, rather than
        // transforming the old offset — correct however the sign fell out of the transform.
        private static ProjectionPlane RederiveSurfacePlane(
            GuideData g, PlaneAxis flattened, ProjectionPlane fallback)
        {
            foreach (ControlPoint cp in g.ControlPoints)
            {
                Vec3d w = cp?.WorldPosition;
                if (w == null || cp.IsPhantom) continue;
                double coordinate = flattened switch
                {
                    PlaneAxis.X => w.X,
                    PlaneAxis.Z => w.Z,
                    _ => w.Y
                };
                return new ProjectionPlane(flattened, (int)Math.Floor(coordinate * 16.0));
            }
            return new ProjectionPlane(flattened, fallback.PlaneOffset);
        }

        /// <summary>The guide's extent along one world axis in 1/16 units — the "span" step distance.</summary>
        public int SpanAlong(Guid id, PlaneAxis axis) =>
            _guides.TryGetValue(id, out GuideData g) ? SpanAlong(g, axis) : 0;

        internal static int SpanAlong(GuideData g, PlaneAxis axis)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (ControlPoint cp in g.ControlPoints)
            {
                Vec3d w = cp?.WorldPosition;
                if (w == null || cp.IsPhantom) continue;
                double v = axis switch { PlaneAxis.X => w.X, PlaneAxis.Z => w.Z, _ => w.Y };
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (min > max) return 0;
            int scale = Math.Max(1, g.VoxelScale);
            // Rounded UP to a whole number of the guide's own voxels, so a copy lands flush rather than
            // overlapping by the fraction of a cell the control points happen to span.
            int span = (int)Math.Ceiling((max - min) * 16.0 / scale) * scale;
            return Math.Max(scale, span);
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
            GuideData accessBefore = AccessSnapshot(g);
            List<ControlPoint> before = SnapshotPoints(g);
            ShapeConstraint constraintBefore = g.Constraint;

            if (!shape.BreakConstraint())
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c0) ? c0 : 0);

            g.Constraint = ShapeConstraint.None;
            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled, g.IsWireframe);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.ControlPoints.Clear();
                foreach (ControlPoint cp in before) g.ControlPoints.Add(cp.Clone());
                g.Constraint = constraintBefore;
                _shapes[id] = ShapeFactory.Adopt(g);
                _shapes[id].RecalculatePhantomPoints();
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.ControlPoints.Clear();
                foreach (ControlPoint cp in before) g.ControlPoints.Add(cp.Clone());
                g.Constraint = constraintBefore;
                _shapes[id] = ShapeFactory.Adopt(g);
                _shapes[id].RecalculatePhantomPoints();
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }
            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Restores a guide's constraint AND its exact pre-break control points — the undo of
        /// <see cref="BreakConstraint"/>. The snapshot list is deep-copied in.
        /// </summary>
        public GuideOperationResult RestoreConstraint(
            Guid id, ShapeConstraint constraint, List<ControlPoint> pointsSnapshot,
            int knownVoxelCount = -1)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (pointsSnapshot == null) return GuideOperationResult.Invalid();
            GuideData accessBefore = AccessSnapshot(g);

            List<ControlPoint> before = SnapshotPoints(g);
            ShapeConstraint constraintBefore = g.Constraint;
            g.ControlPoints.Clear();
            foreach (var cp in pointsSnapshot) g.ControlPoints.Add(cp.Clone());
            g.Constraint = constraint;
            IGuideShape shape = ShapeFactory.Adopt(g);      // re-adopt so the shape sees the constraint
            _shapes[id] = shape;
            shape.RecalculatePhantomPoints();

            // Cancellation restores the exact snapshot whose authoritative count was captured at grab
            // start. Reuse it instead of rescanning a behemoth; undo and other callers still recount.
            int count = knownVoxelCount >= 0
                ? knownVoxelCount
                : CountForCaps(id, shape, g.VoxelScale, g.IsFilled, g.IsWireframe);
            // A known count means gesture cancellation: restoring the exact authoritative origin must never
            // be blocked by a cap changed during the drag. Ordinary undo/spring-back restores are rechecked.
            if (knownVoxelCount < 0 && WouldExceedCaps(id, count, out int cap))
            {
                g.ControlPoints.Clear();
                foreach (ControlPoint cp in before) g.ControlPoints.Add(cp.Clone());
                g.Constraint = constraintBefore;
                _shapes[id] = ShapeFactory.Adopt(g);
                _shapes[id].RecalculatePhantomPoints();
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.ControlPoints.Clear();
                foreach (ControlPoint cp in before) g.ControlPoints.Add(cp.Clone());
                g.Constraint = constraintBefore;
                _shapes[id] = ShapeFactory.Adopt(g);
                _shapes[id].RecalculatePhantomPoints();
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }
            StoreCount(id, count);
            MarkDirty();
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
            // NO-OP GUARD — see SetHidden. Note the comparison is against the CLAMPED value, so spamming
            // any out-of-range number repeats as a no-op too, not just the exact current one.
            if (g.Divisions == clamped)
                return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var same) ? same : 0);
            g.Divisions = clamped;
            MarkDirty();
            return GuideOperationResult.Success(g, _voxelCounts.TryGetValue(id, out var c0) ? c0 : 0);
        }

        public GuideOperationResult SetFilled(Guid id, bool filled)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            // Fill doesn't apply to 3D volumes (0.2.17 — always hollow shells); the GUI greys the tiles,
            // this is the server-side gate against a stale packet.
            if (GuideShapeTypes.IsVolume(g.ShapeType)) return GuideOperationResult.Invalid(g);
            var shape = _shapes[id];
            GuideData accessBefore = AccessSnapshot(g);

            bool oldFilled = g.IsFilled;
            g.IsFilled = filled;

            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.IsFilled = oldFilled;
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.IsFilled = oldFilled;
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        public GuideOperationResult SetWireframe(Guid id, bool wireframe)
        {
            if (!_guides.TryGetValue(id, out var g)) return GuideOperationResult.NotFound();
            if (!GuideShapeTypes.IsVolume(g.ShapeType)) return GuideOperationResult.Invalid(g);
            if (g.IsWireframe == wireframe)
                return GuideOperationResult.Success(g,
                    _voxelCounts.TryGetValue(id, out int existing) ? existing : 0);

            IGuideShape shape = _shapes[id];
            GuideData accessBefore = AccessSnapshot(g);
            bool oldWireframe = g.IsWireframe;
            g.IsWireframe = wireframe;
            int count = CountForCaps(id, shape, g.VoxelScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.IsWireframe = oldWireframe;
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.IsWireframe = oldWireframe;
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        /// <summary>
        /// Commits an isolated immense-sculpt candidate whose expensive count and claim footprint were
        /// calculated off-thread. The expected live object must still be current; all collection mutation,
        /// final cap validation, cached metadata, and persistence remain on the server tick thread.
        /// </summary>
        public GuideOperationResult CommitPreparedGuideMutation(
            Guid id, GuideData expectedLive, GuideData candidate,
            IGuideShape candidateShape, int exactVoxelCount)
        {
            if (!_guides.TryGetValue(id, out GuideData live))
                return GuideOperationResult.NotFound();
            if (!ReferenceEquals(live, expectedLive) || candidate == null
                || candidateShape == null || candidate.Id != id || exactVoxelCount < 0)
                return GuideOperationResult.Invalid(live);
            if (exactVoxelCount == 0) return GuideOperationResult.Empty(live);
            if (WouldExceedCaps(id, exactVoxelCount, out int cap))
                return GuideOperationResult.OverCap(live, exactVoxelCount, cap);

            _guides[id] = candidate;
            _shapes[id] = candidateShape;
            StoreCount(id, exactVoxelCount);
            MarkDirty();
            return GuideOperationResult.Success(candidate, exactVoxelCount);
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
            GuideData accessBefore = AccessSnapshot(g);

            int oldScale = g.VoxelScale;
            g.VoxelScale = newScale;

            int count = CountForCaps(id, shape, newScale, g.IsFilled);
            if (WouldExceedCaps(id, count, out int cap))
            {
                g.VoxelScale = oldScale;
                return GuideOperationResult.OverCap(g, count, cap);
            }
            if (AccessDenied(accessBefore, g, shape, out BlockPos deniedPosition))
            {
                g.VoxelScale = oldScale;
                return GuideOperationResult.ClaimDenied(g, deniedPosition);
            }

            StoreCount(id, count);
            MarkDirty();
            return GuideOperationResult.Success(g, count);
        }

        // --- Cap + count bookkeeping --------------------------------------------------------------

        // Counts only as far as cap enforcement needs. Volume shapes can stop their cell scan as soon as
        // this threshold is crossed; other shapes fall back to their exact counter. When the result is
        // accepted it is still exact, so the running total/cache retain their existing invariant.
        private int CountForCaps(Guid id, IGuideShape shape, int scale, bool filled,
            bool wireframe = false, string creatorUid = null)
        {
            _guides.TryGetValue(id, out GuideData guide);
            if (wireframe || guide?.IsWireframe == true)
                return ShapeWireframe.GetVoxelCount(shape, scale);
            int limit = HardVoxelCeiling;
            int perGuideCap = EffectivePerGuideVoxelCap;
            bool existingExceedsActorCap = perGuideCap > 0
                && _voxelCounts.TryGetValue(id, out int existingCount)
                && existingCount > perGuideCap;
            // A lower-cap actor is allowed to preserve or shrink a trusted player's oversized guide. In
            // that case an early stop at actorCap+1 would not be the real count and could corrupt totals.
            if (perGuideCap > 0 && !existingExceedsActorCap) limit = Math.Min(limit, perGuideCap);

            string attributedCreator = creatorUid ?? guide?.CreatorUid;
            int playerTotalCap = EffectivePerPlayerTotalVoxelCap(attributedCreator);
            if (playerTotalCap > 0 && !string.IsNullOrEmpty(attributedCreator))
            {
                long playerTotal = VoxelCountBy(attributedCreator);
                int currentForId = guide?.CreatorUid == attributedCreator
                    && _voxelCounts.TryGetValue(id, out int currentPlayerGuide)
                    ? currentPlayerGuide : 0;
                // If persisted state already exceeds a newly lowered cap, count exactly so a shrink can be
                // accepted. Otherwise stop as soon as this guide would pass the creator's remaining budget.
                if (playerTotal <= playerTotalCap)
                {
                    long available =
                        (long)playerTotalCap - (playerTotal - currentForId);
                    int playerLimit = available <= 0 ? 0
                        : available >= int.MaxValue ? int.MaxValue
                        : (int)available;
                    limit = Math.Min(limit, playerLimit);
                }
            }

            // Same shrink escape as the creator-total branch above, and for the same reason: if the world
            // is ALREADY over a newly-lowered totalVoxelCap, clamping here would stop the scan at zero and
            // hand back the Exceeded(0) sentinel — 1 — instead of a real count, so the shrink that would
            // bring the world back under budget could never even be measured. Count exactly in that case.
            if (_totalVoxelCap > 0 && _totalVoxels <= _totalVoxelCap)
            {
                int currentForId = _voxelCounts.TryGetValue(id, out int current) ? current : 0;
                long totalWithoutGuide = _totalVoxels - currentForId;
                long available = (long)_totalVoxelCap - totalWithoutGuide;
                int totalLimit = available <= 0 ? 0
                    : available >= int.MaxValue ? int.MaxValue
                    : (int)available;
                limit = Math.Min(limit, totalLimit);
            }

            return GuideShapeVoxelCounting.CountUpTo(shape, scale, filled, limit);
        }

        private int ExactVoxelCount(Guid id, IGuideShape shape, int scale, bool filled) =>
            _guides.TryGetValue(id, out GuideData guide) && guide.IsWireframe
                ? ShapeWireframe.GetVoxelCount(shape, scale)
                : shape.GetVoxelCount(scale, filled);

        // True if making guide `id`'s count `newCount` would breach its per-guide, creator-total, or
        // projected world-total cap.
        // A cap of 0 means unlimited — that check is skipped (normalised in the ctor).
        private bool WouldExceedCaps(Guid id, int newCount, out int cap)
        {
            // Hard ceiling first, ALWAYS (even with caps disabled): a scan-guard sentinel count means the
            // guide is too big to voxelise/render — never let an edit (rescale, fill, drag) grow into one.
            int currentForId = _voxelCounts.TryGetValue(id, out var c) ? c : 0;
            if (newCount > HardVoxelCeiling) { cap = HardVoxelCeiling; return true; }
            int perGuideCap = EffectivePerGuideVoxelCap;
            // An actor without the elevated cap may still preserve or shrink a legitimately oversized guide.
            // Only growth above that actor's cap is rejected.
            if (perGuideCap > 0 && newCount > perGuideCap && newCount > currentForId)
            {
                cap = perGuideCap;
                return true;
            }
            if (_guides.TryGetValue(id, out GuideData guide)
                && WouldExceedPlayerTotal(
                    guide.CreatorUid, id, newCount, out int playerTotalCap))
            {
                cap = playerTotalCap;
                return true;
            }
            // GROWTH ONLY, like both siblings above (G28). Without the second clause, setting
            // totalVoxelCap below current world usage refused EVERY edit on EVERY guide — including the
            // shrinks and dispels that would bring the world back under the new budget — which directly
            // contradicts ApplyCaps' promise that lowering a cap only stops guides from growing.
            long projectedTotal = _totalVoxels - currentForId + newCount;
            if (_totalVoxelCap > 0 && projectedTotal > _totalVoxelCap && projectedTotal > _totalVoxels)
            {
                cap = _totalVoxelCap;
                return true;
            }
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

        /// <summary>
        /// Guide count and voxel total for EVERY creator at once, in a single pass over the registry.
        /// </summary>
        /// <remarks>
        /// For a per-player figure, <see cref="CountGuidesBy"/> and <see cref="VoxelCountBy"/> are the right
        /// tools — one scan is nothing next to the cap check that asked. **This exists for the case that
        /// wants all of them**, which was building the admin roster: one row per player, each row calling
        /// both of those, so the work was (players × guides) × 2 and grew with the product. With a couple of
        /// hundred known players and a few thousand guides that is over a million passes to open one dialog.
        ///
        /// Creators are keyed by UID; guides with no creator (pre-attribution saves) belong to nobody and
        /// are counted for nobody, matching the two single-player methods exactly.
        /// </remarks>
        public Dictionary<string, (int Guides, long Voxels)> TallyByCreator()
        {
            var tally = new Dictionary<string, (int Guides, long Voxels)>(StringComparer.Ordinal);
            foreach (KeyValuePair<Guid, GuideData> entry in _guides)
            {
                string uid = entry.Value?.CreatorUid;
                if (string.IsNullOrEmpty(uid)) continue;

                // _voxelCounts, NOT GuideData.CachedVoxelCount — VoxelCountBy reads the live count map and
                // this must agree with it exactly, or the roster and the cap checks would report different
                // totals for the same player. A guide missing from the map contributes nothing, as there.
                long voxels = _voxelCounts.TryGetValue(entry.Key, out int count) ? Math.Max(0, count) : 0;

                tally.TryGetValue(uid, out (int Guides, long Voxels) current);
                tally[uid] = (current.Guides + 1, current.Voxels + voxels);
            }
            return tally;
        }

        /// <summary>
        /// Sum of cached voxels across all currently existing guides attributed to one original creator.
        /// Guides from pre-attribution saves (no creator uid) count toward no player.
        /// </summary>
        public long VoxelCountBy(string playerUid)
        {
            if (string.IsNullOrEmpty(playerUid)) return 0;
            long total = 0;
            foreach (KeyValuePair<Guid, GuideData> entry in _guides)
            {
                if (entry.Value?.CreatorUid != playerUid) continue;
                if (_voxelCounts.TryGetValue(entry.Key, out int count))
                    total += Math.Max(0, count);
            }
            return total;
        }

        private bool WouldExceedPlayerTotal(
            string playerUid, Guid id, int newCount, out int effectiveCap)
        {
            effectiveCap = EffectivePerPlayerTotalVoxelCap(playerUid);
            if (effectiveCap <= 0 || string.IsNullOrEmpty(playerUid)) return false;
            long currentTotal = VoxelCountBy(playerUid);
            int currentForId = _guides.TryGetValue(id, out GuideData guide)
                && guide?.CreatorUid == playerUid
                && _voxelCounts.TryGetValue(id, out int count)
                ? count : 0;
            long projected = currentTotal - currentForId + Math.Max(0, newCount);
            // Loading never ejects old state, and lowering a cap must not prevent a player from shrinking it.
            return projected > effectiveCap && projected > currentTotal;
        }

        // Records a guide's voxel count, keeping the running total in step.
        private void StoreCount(Guid id, int count)
        {
            int prev = _voxelCounts.TryGetValue(id, out var c) ? c : 0;
            _totalVoxels += count - prev;
            _voxelCounts[id] = count;

            if (_guides.TryGetValue(id, out GuideData guide)
                && _shapes.TryGetValue(id, out IGuideShape shape))
            {
                GuideExtent extent = GuideMeshBuilder.MeasureShapeExtent(shape, guide.VoxelScale);
                guide.CachedVoxelCount = Math.Max(0, count);
                guide.CachedVoxelWidth = extent.VoxelWidth;
                guide.CachedVoxelHeight = extent.VoxelHeight;
                guide.CachedBlockWidth = extent.BlockWidth;
                guide.CachedBlockHeight = extent.BlockHeight;
                guide.DisplayName = extent.BlockWidth + " \u00D7 " + extent.BlockHeight + " blocks";
                guide.DataVersion = GuideData.CurrentDataVersion;
            }
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
            StoreCount(id, ExactVoxelCount(id, shape, g.VoxelScale, g.IsFilled));
        }

        // --- Persistence --------------------------------------------------------------------------

        /// <summary>
        /// Writes the registry out, if anything has changed since the last write. **Call this from
        /// lifecycle points, never from a mutation** — mutations call <see cref="MarkDirty"/>.
        /// </summary>
        /// <remarks>
        /// WIRED TO: the world save (server), a periodic flush on both sides, and shutdown. Together those
        /// bound how much can be lost to the flush interval, and the world-save hook guarantees the blob is
        /// current at the moment the game writes it to disk.
        ///
        /// ⚠️ THE DIRTY FLAG IS RESTORED IF THE WRITE FAILS. Clearing it first and leaving it clear would
        /// turn one transient serialisation fault into permanent data loss: the next flush would see
        /// nothing owed and skip, and every later flush would too. Never throws out of here either — a
        /// serialization fault must not crash the server.
        ///
        /// ⚠️ THIS IS ALSO THE OVERRIDE FOR A BACKGROUND PASS, and that is why "nothing owed" is not just
        /// the dirty flag. A worker started by <see cref="BeginBackgroundPersist"/> has ALREADY cleared the
        /// flag, against a snapshot whose bytes are not stored yet — so a clean flag with a job in flight
        /// still owes a write, and reading only the flag here would skip it and lose everything since that
        /// snapshot. This writes the LIVE registry (always the newer answer) and drops the job, whose
        /// completion then finds itself superseded and discards its older bytes.
        /// </remarks>
        /// <summary>
        /// Whether the registry holds anything not yet handed to storage. **Main thread only.**
        /// </summary>
        /// <remarks>
        /// ⚠️ A CLEAN DIRTY FLAG IS NOT THE SAME AS NOTHING OWED, which is the whole reason this exists as
        /// one named answer rather than a condition written out at each site. A background pass clears the
        /// flag when it takes its snapshot, long before those bytes are stored — so between those two
        /// moments the flag says "clean" while a write is still owed. Both <see cref="Persist"/> and the
        /// background scheduler's decision to fall back to a synchronous write read this, and they must
        /// read the same thing: a site that checked only the flag would skip a write that was still due.
        /// </remarks>
        public bool HasUnsavedChanges => _persistDirty || _persistInFlight != null;

        public void Persist()
        {
            if (!HasUnsavedChanges) return;
            _persistDirty = false;
            // Dropping the reference IS the cancellation: the worker runs to completion on its own
            // snapshot and CompleteBackgroundPersist throws the result away (GOTCHAS G29). There is
            // deliberately nothing to join — a shutdown must not wait on a serialisation it is about to
            // redo, and a background task thread never holds the process open.
            _persistInFlight = null;
            try
            {
                byte[] bytes = SerializeRegistry(_guides.Values.ToList());
                _persistence.Store(StorageKey, bytes);
            }
            catch (Exception e)
            {
                _persistDirty = true;
                _logger.Error("[Layout] Failed to persist guides: {0}", e);
            }
        }

        /// <summary>
        /// Takes a private snapshot of the registry so the expensive half of a write — turning it into
        /// text — can happen on a worker thread. Returns null when there is nothing to do. **Main thread
        /// only.** The caller must run <see cref="GuidePersistJob.Run"/> off-thread and then hand the job
        /// back to <see cref="CompleteBackgroundPersist"/> on the main thread.
        /// </summary>
        /// <remarks>
        /// ⚠️ THE SNAPSHOT IS NOT AN OPTIMISATION, IT IS THE WHOLE REASON THIS IS SAFE, and nothing at the
        /// call site says so. <see cref="GuideData.ControlPoints"/> is the SAME list instance the guide's
        /// shape mutates (see that type's remarks): a worker walking the live records while a player drags
        /// would read a half-applied reshape, or throw outright when <c>RestorePoints</c> clears and refills
        /// the list under it. Deep-copying every record first is what makes the worker's view immutable.
        ///
        /// It is affordable because copying is ~80× cheaper than serialising — measured 2026-08-01 at
        /// 0.52 ms versus 42 ms for three thousand guides. The main thread pays the copy; the 42 ms leaves
        /// the tick entirely.
        ///
        /// ONE AT A TIME. A job already in flight means this returns null and the registry stays dirty, so
        /// the next attempt picks the change up — the flag is never latched (GOTCHAS G36): every path out
        /// of here either leaves work owed or has a job that will clear itself.
        /// </remarks>
        public GuidePersistJob BeginBackgroundPersist()
        {
            if (_persistInFlight != null || !_persistDirty) return null;

            List<GuideData> snapshot;
            try
            {
                snapshot = new List<GuideData>(_guides.Count);
                foreach (GuideData g in _guides.Values)
                    snapshot.Add(g.DeepClone());
            }
            catch (Exception e)
            {
                // Left dirty on purpose: the next pass, or the next world save, tries again.
                _logger.Error("[Layout] Failed to snapshot guides for background save: {0}", e);
                return null;
            }

            _persistDirty = false;
            _persistInFlight = new GuidePersistJob(this, snapshot);
            return _persistInFlight;
        }

        /// <summary>
        /// Stores the bytes a finished <see cref="GuidePersistJob"/> produced. **Main thread only**, and
        /// safe to call for a job that has been superseded — it checks first. Returns whether THIS job's
        /// bytes reached storage.
        /// </summary>
        /// <remarks>
        /// ⚠️ THE IDENTITY CHECK IS NOT DEFENSIVE, IT PREVENTS A ROLLBACK. Between a snapshot and its
        /// completion, a synchronous <see cref="Persist"/> (shutdown, or a drop path) can have written the
        /// LIVE registry. Storing this job's older bytes on top of that would silently undo everything
        /// edited since the snapshot. Same shape as GOTCHAS G29: async work must prove it is still the
        /// current work before acting on a shared slot.
        ///
        /// ⚠️ THE RETURN VALUE IS NOT <see cref="HasUnsavedChanges"/> INVERTED, and must not be simplified
        /// into it. This answers "did this pass do its job", which stays true when an edit lands after the
        /// snapshot — that edit belongs to the next cycle, and is the whole point of preparing bytes early.
        /// Asking the registry instead would report every such edit as a failed pass and drive the caller
        /// into a synchronous write, which on a world with several people building is most saves: the exact
        /// on-tick cost this machinery exists to remove.
        /// </remarks>
        public bool CompleteBackgroundPersist(GuidePersistJob job)
        {
            if (job == null || !ReferenceEquals(_persistInFlight, job)) return false;
            _persistInFlight = null;

            if (job.Bytes == null)
            {
                // Serialisation faulted on the worker. Re-arm exactly as the synchronous path does, so the
                // next pass retries rather than the failure becoming permanent.
                _persistDirty = true;
                _logger.Error("[Layout] Background guide serialisation failed: {0}", job.Error);
                return false;
            }

            try
            {
                _persistence.Store(StorageKey, job.Bytes);
                return true;
            }
            catch (Exception e)
            {
                _persistDirty = true;
                _logger.Error("[Layout] Failed to store background-serialised guides: {0}", e);
                return false;
            }
        }

        // The one place the on-disk shape is built, shared by the synchronous and background paths so the
        // two can never drift into writing different formats.
        //
        // ⚠️ CALLED FROM TWO THREADS, and possibly at the same instant (a shutdown flush while a worker is
        // still running). That is safe only because everything it touches is immutable after construction:
        // _jsonSettings is never mutated, and Vec3dJsonConverter holds no state. Adding a stateful
        // converter, or reconfiguring the settings in play, would make this a race with no symptom until a
        // save came out malformed.
        private byte[] SerializeRegistry(List<GuideData> guides)
        {
            var root = new PersistedRoot
            {
                Version = GuideData.CurrentDataVersion,
                Guides = guides
            };
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(root, _jsonSettings));
        }

        /// <summary>
        /// One background serialisation: a private snapshot of the registry, and the bytes it turns into.
        /// Created by <see cref="BeginBackgroundPersist"/>, run on a worker, completed on the main thread.
        /// </summary>
        /// <remarks>
        /// The snapshot is touched by exactly one thread at a time and handed over by the task boundary,
        /// which is its own memory barrier — so no locking is needed here, and none should be added. What
        /// makes that true is that the snapshot is PRIVATE (see <see cref="BeginBackgroundPersist"/>);
        /// nothing else in the mod holds a reference to these records.
        /// </remarks>
        public sealed class GuidePersistJob
        {
            private readonly GuideManager _owner;
            private readonly List<GuideData> _snapshot;

            /// <summary>The serialised payload, or null if <see cref="Run"/> faulted.</summary>
            public byte[] Bytes { get; private set; }

            /// <summary>Why <see cref="Run"/> produced no bytes, or null on success.</summary>
            public Exception Error { get; private set; }

            internal GuidePersistJob(GuideManager owner, List<GuideData> snapshot)
            {
                _owner = owner;
                _snapshot = snapshot;
            }

            /// <summary>
            /// The expensive part — JSON plus UTF8, ~42 ms at three thousand guides. **Call this on a
            /// worker thread; it must never run on the tick.** Never throws: a fault is recorded and dealt
            /// with by <see cref="CompleteBackgroundPersist"/> back on the main thread.
            /// </summary>
            public void Run()
            {
                try
                {
                    Bytes = _owner.SerializeRegistry(_snapshot);
                }
                catch (Exception e)
                {
                    Bytes = null;
                    Error = e;
                }
            }
        }

        // Reads guides back from the save blob, rebuilds each guide's shape over its (shared) control-point
        // list, re-derives phantoms, and rebuilds the count caches. Older records migrate by deserialization
        // defaults (missing Projection/Plane/IsFilled fall to Volumetric / default plane / hollow).
        public void Load()
        {
            Exception primaryError = null;
            try
            {
                byte[] bytes = _persistence.Load(StorageKey);
                if (bytes == null || bytes.Length == 0)
                {
                    _logger.Notification("[Layout] No saved guides found.");
                    return;
                }

                LoadPayload(bytes);
                _logger.Notification("[Layout] Loaded {0} guide(s).", _guides.Count);
                return;
            }
            catch (Exception e)
            {
                primaryError = e;
                ClearLoadedState();
            }

            if (_persistence is IRecoverableGuidePersistence recoverable)
            {
                try
                {
                    byte[] backup = recoverable.LoadBackup(StorageKey);
                    if (backup != null && backup.Length > 0)
                    {
                        LoadPayload(backup);
                        recoverable.RejectPrimary(StorageKey);
                        _logger.Warning(
                            "[Layout] Primary guide file was unreadable; recovered {0} guide(s) from its backup.",
                            _guides.Count);
                        return;
                    }
                }
                catch (Exception backupError)
                {
                    ClearLoadedState();
                    _logger.Error(
                        "[Layout] Failed to load both primary and backup guide files; starting with none. Primary: {0} Backup: {1}",
                        primaryError, backupError);
                    return;
                }
            }

            _logger.Error("[Layout] Failed to load guides; starting with none: {0}", primaryError);
        }

        private void LoadPayload(byte[] bytes)
        {
            string json = Encoding.UTF8.GetString(bytes);
            var root = JsonConvert.DeserializeObject<PersistedRoot>(json, _jsonSettings);
            if (root?.Guides == null)
                throw new JsonSerializationException("The guide payload has no guide collection.");

            ClearLoadedState();
            foreach (var g in root.Guides)
            {
                if (g == null) continue;
                if (g.ControlPoints == null) g.ControlPoints = new List<ControlPoint>();
                RemoveInactiveLockMarkers(g.ControlPoints);

                // A guide whose coordinates or Surface plane are outside the world is DROPPED, not loaded
                // (GOTCHAS G31). Undefined projection/axis values are rejected at this same untrusted seam.
                // ExactVoxelCount below is the scan that hangs on one, and this method is reached both
                // from the world save (a corrupted blob would hang the server at start) and from
                // Layout/ClientOnlyGuides/*.json (a hand-edited private file would hang the player's own
                // client on world load). Neither needs an attacker. Logged per guide because silently
                // losing someone's work would be the worse surprise.
                if (!GuideBounds.AllUsable(g.ControlPoints)
                    || !GuideBounds.AllUsable(g.OriginalControlPoints)
                    || !GuideBounds.IsUsableProjection(g.Projection, g.Plane))
                {
                    _logger?.Warning(
                        "[Layout] Skipped guide {0}: coordinates or projection are invalid or outside the "
                        + "world. The record is corrupt or hand-edited and is unsafe to load.", g.Id);
                    continue;
                }

                g.DataVersion = GuideData.CurrentDataVersion; // normalize after default-driven migration

                IGuideShape shape = ShapeFactory.Adopt(g);
                shape.RecalculatePhantomPoints();             // restore phantoms before first sync

                _guides[g.Id] = g;
                _shapes[g.Id] = shape;
                StoreCount(g.Id, ExactVoxelCount(g.Id, shape, g.VoxelScale, g.IsFilled));
            }

            // ⚠️ A LOAD OWES A WRITE, even though nobody edited anything. What is now in memory is not
            // necessarily what is on disk: records migrate by deserialization default, DataVersion is
            // re-stamped above, inactive lock markers are dropped, out-of-world guides are skipped
            // entirely, and a backup recovery has just replaced a quarantined primary. Persist used to
            // write unconditionally on every world save, so all of that reached disk by accident of the
            // old design; now that a clean flag skips the write, the load has to declare it.
            MarkDirty();
        }

        private void ClearLoadedState()
        {
            _guides.Clear();
            _shapes.Clear();
            _voxelCounts.Clear();
            _totalVoxels = 0;
        }

        // Versioned on-disk envelope. Voxels are never part of this — always re-derived from control points.
        private class PersistedRoot
        {
            public int Version { get; set; }
            public List<GuideData> Guides { get; set; }
        }

        private sealed class ActionOnDispose : IDisposable
        {
            private Action _action;
            public ActionOnDispose(Action action) { _action = action; }
            public void Dispose()
            {
                Action action = _action;
                _action = null;
                action?.Invoke();
            }
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
