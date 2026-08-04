using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;
using Layout.Network;
using Layout.Client;

namespace Layout.Systems
{
    /// <summary>
    /// Client-side renderer for all guides. It is a pure <see cref="IRenderer"/> — it draws translucent voxel
    /// meshes and nothing else: no selection box, no collision, no entity. A placed guide is therefore fully
    /// visible as a reference but can never be targeted or obscure the real block behind it, exactly as the
    /// concurrency rules require. All hover/grab/targeting logic lives in the held-tool path (Module 7), not here.
    /// </summary>
    /// <remarks>
    /// PIPELINE. The renderer owns one compiled mesh per guide. It reacts to <see cref="ClientNetworkHandler"/>
    /// events: when a guide appears or changes it builds an <see cref="ArchShape"/> over that guide's mirror
    /// control-point list, re-derives the phantoms (the mirror's phantoms are stale after an incremental edit),
    /// samples the voxel set, and hands it to <see cref="GuideMeshBuilder"/> to produce a <see cref="MeshData"/>
    /// it uploads. Because the shape adopts the mirror's list BY REFERENCE, no copy is needed — the rebuild sees
    /// the latest positions directly.
    ///
    /// VOLUMETRIC-ONLY FOR NOW. The built shape still exposes only the hollow-volumetric voxel subset
    /// (<c>GetVoxelPositions(int scale)</c>), so every guide is meshed as cubes regardless of its
    /// <see cref="GuideData.Projection"/> flag (per PROJECT_STATUS.md). When the shape's Surface/fill widening
    /// lands, flip the single line marked below to pass the guide's real projection through to the builder.
    ///
    /// GRABBED HIGHLIGHT. The held tool tells the renderer which control point is being dragged via
    /// <see cref="SetGrabbedPoint"/> / <see cref="ClearGrabbedPoint"/>; that point's voxels then render White
    /// until released. Nothing is grabbed until the tool sets it, so this is inert in isolation.
    ///
    /// EVENTS NOT CONSUMED. <c>LockStateChanged</c> and <c>VoxelCapWarningReceived</c> are deliberately left to
    /// the HUD/tool (Module 6/7): neither changes a guide's geometry, and lock ownership is already exposed on
    /// <see cref="ClientNetworkHandler.LockHolders"/>. The renderer subscribes only to the events that change
    /// what is drawn.
    ///
    /// THREADING. VS delivers client packet handlers (and therefore these events) on the main thread, where the
    /// GL context is valid, so meshes are built and uploaded inline in the handlers. Per-frame work is limited to
    /// setting a model matrix and issuing draws over the already-uploaded meshes.
    /// </remarks>
    public sealed class GuideRenderer : IRenderer
    {
        // The stage guides draw in. Module-7 in-game finding: the OIT stage expects VS's dedicated
        // order-independent-transparency shaders (accumulation buffers), NOT the standard shader — using it
        // there broke both colour and alpha. Guides now draw in the Opaque stage, after terrain (RenderOrder
        // 0.5), with blending toggled manually: classic ordered alpha blending, depth-tested against the
        // world. Guide-vs-guide overlap is order-dependent, which is fine for translucent overlays.
        private const EnumRenderStage RenderStage = EnumRenderStage.Opaque;

        // Moving drafts keep a normal selected-scale shell while this cheap estimate fits. Expensive poses
        // fall back to a wireframe with its own smaller moving-mesh target.
        private const int PreviewFullResVoxelCap = 8000;
        private const int MovingWireframeVoxelTarget = 1500;
        private const double PrecisionTransitionRadius = 2.0;
        private const int MaterializationTargetVoxelsPerBatch = 750;
        private const int MaterializationMinimumBatches = 8;
        private const int MaterializationMaximumBatches = 128;
        private const int ProgressiveMaximumPreviewBatches = 128;
        private const int SmallMaterializationUploadIntervalMs = 18;
        private const int LargeMaterializationUploadIntervalMs = 45;
        private const int LargeMaterializationIntervalVoxelCount = 120000;
        private const int MaterializationCleanSwapDelayMs = 200;
        private const int MaterializationReadyBatchCapacity = 3;
        private const int RetiredMaterializationDeletesPerFrame = 4;
        private const int PendingPlacementVisualTimeoutMs = 300000;
        private const int GrabSettleDelayMs = 180;
        private const double FrustumCullPadding = 0.25;

        // Remote draft-anchor marker: a small blue cube (a fraction of a block) centred on the anchor.
        private const int DraftMarkerScale = 4;                          // 1/16 units → 0.25-block cube
        private const double DraftMarkerHalf = DraftMarkerScale / 32.0;  // half its edge, for centring
        private const double DraftMarkerCullRadius = DraftMarkerHalf * 1.7320508075688772;

        private readonly ICoreClientAPI _capi;
        private readonly ClientNetworkHandler _network;

        // A 2x2 pure-white texture for the standard shader's sampler. Module-7 in-game finding: Tex2D = 0
        // samples garbage (view-dependent colours that tracked the crosshair); the shader ALWAYS multiplies
        // texture x vertex colour, so it must be given a real, white texture for the vertex palette to
        // survive intact. Built from raw pixels (white is 0xFFFFFFFF in any channel order, so this is
        // channel-order-proof and needs no asset-path convention). If LoadOrUpdateTextureFromBgra doesn't
        // exist in your API build, the one-line fallback is:
        //   _whiteTexId = _capi.Render.GetOrLoadTexture(new AssetLocation("layout", "white.png"));
        // (the mod ships assets/layout/textures/white.png for exactly that fallback).
        private LoadedTexture _whiteTex;
        private bool _whiteTexBorrowed;   // true → engine-cached (GetOrLoadTexture); we must NOT dispose it
        private bool _whiteTexLogged;

        // Local draft preview (Module-7 in-game finding: after the first click the acting player saw NOTHING
        // until the second click — remote players got an anchor dot, the drafter got no ghost). While a draft
        // is live, the controller feeds (start, current aim, live settings) every tick; the mesh is rebuilt
        // only when one of those actually changes (aim is voxel-snapped, so this is cell-by-cell, not
        // per-tick). The ghost is built by the exact pipeline placed guides use, so what you see is what the
    // authority will build — including the ownership palette and far-foot coplanarity shade while aiming.
        private MeshRef _draftPreviewMesh;
        private Vec3d _draftPreviewOrigin;
        private Vec3d _draftCullCenter;
        private double _draftCullRadius;
        private Vec3d _previewStart, _previewEnd, _previewApex, _previewRim;
        private GuideRenderSettings _previewSettings;
        private GuideShapeType _previewShapeType = GuideShapeType.Arch;      // Session 8: shape is part of
        private ShapeConstraint _previewConstraint = ShapeConstraint.None;   //   the ghost's rebuild key
        private PlaneAxis _previewPlaneAxis = PlaneAxis.Y;
        private int _previewSides;                                           // Session 11: polygon ghosts
        private bool _previewInverted;                                       // Session 11: SHIFT-invert ghosts
        private bool _previewFlatSideAligned;
        private bool _hasPreviewKey;

        // v0.3 progressive drafting. Cheap moving poses retain their shell; expensive poses use an adaptive
        // wireframe, and settled shell refinements are accepted only while their generation is current.
        private readonly object _draftWorkGate = new object();
        private bool _draftWorkBusy;
        private int _activeDraftGeneration;
        private double _smoothedFrameMilliseconds = 16.67;
        private readonly List<MeshRef> _draftPrecisionMeshes = new List<MeshRef>();
        private readonly List<MeshRef> _draftMaterializationMeshes = new List<MeshRef>();
        private readonly List<MeshRef> _draftCleanMaterializationMeshes = new List<MeshRef>();
        private long _lastDraftMaterializationUploadMs;
        private DraftBuildResult _activeDraftBuild;
        private bool _draftMaterializationStarted;
        private bool _draftMaterializationFinal;
        private long _draftCleanSwapReadyMs = -1;
        private bool _draftPreviewWasWireframe;
        private ulong _draftVisualFingerprint;
        private readonly Queue<MeshRef> _retiredMaterializationMeshes = new Queue<MeshRef>();

        // Once an immense exact draft has been calculated, final placement must not throw that work away and
        // synchronously build the same shell again when authority echoes it. The visual remains provisional
        // until a byte-for-byte-equivalent guide arrives, then its uploaded batches become that guide's normal
        // mesh assets. A bounded timeout handles rejected public placements until the protocol grows an
        // explicit create-result acknowledgement in the server-validation stage.
        private sealed class PendingPlacementVisual
        {
            public DraftPreviewSpec Spec;
            public Vec3d Origin;
            public Vec3d CullCenter;
            public double CullRadius;
            public readonly List<MeshRef> Meshes = new List<MeshRef>();
            public readonly List<MeshRef> CleanMeshes = new List<MeshRef>();
            public readonly List<MeshRef> ProvisionalMeshes = new List<MeshRef>();
            public DraftBuildResult Build;
            public long LastUploadMs;
            public long StartedMs;
            public bool PrivateAnchors;
            public Guid AdoptedGuideId;
            public bool RefinementFinished;
            public bool RefinementFailed;
            public bool MeshesAreFinal;
            public bool PlacementEffectsRaised;
            public long CleanSwapReadyMs = -1;
        }

        private PendingPlacementVisual _pendingPlacementVisual;

        // A placed volume changing from Wireframe to Shell keeps its inexpensive wireframe as a scaffold
        // while the exact shell is generated off-thread, then follows the same organic/clean handoff as
        // draft placement and sculpting. Builds are keyed per guide so unrelated setting packets cannot
        // strand another guide on its old scaffold.
        private sealed class SettledMaterializationBuild
        {
            public Guid GuideId;
            public ulong Fingerprint;
            public GuideData Guide;
            public IGuideShape Shape;
            public BlockingCollection<MeshData> ReadyMeshes;
            public BlockingCollection<MeshData> CleanReadyMeshes;
            public CancellationTokenSource Cancellation;
            public Vec3d Origin;
            public Exception Error;
            public int VoxelCount;
            public long LastUploadMs;
            public long CleanSwapReadyMs = -1;
            public volatile bool MetadataReady;
            public volatile bool Completed;
            public bool CompletionHandled;
        }

        private readonly Dictionary<Guid, SettledMaterializationBuild> _settledMaterializations =
            new Dictionary<Guid, SettledMaterializationBuild>();

        // Large-guide sculpting follows the same motion/settle rhythm as initial placement: a cheap
        // wireframe while the handle moves, then an exact selected-scale shell built off-thread and
        // revealed in bounded batches after the pose rests.
        private readonly object _grabWorkGate = new object();
        private bool _grabWorkBusy;
        private int _grabGeneration;
        private ulong _grabPoseFingerprint;
        private ulong _grabRefinedFingerprint;
        private long _grabLastMotionMs;
        private Guid _grabMaterializationGuide = Guid.Empty;
        private Vec3d _grabMaterializationOrigin;
        private GrabBuildResult _activeGrabBuild;
        private bool _grabMaterializationStarted;
        private long _lastGrabMaterializationUploadMs;
        private long _grabCleanSwapReadyMs = -1;
        private bool _releasedGrabPending;
        private bool _releasedGrabAuthorityConfirmed;
        private bool _releasedGrabVisualComplete;
        private ulong _releasedGrabFingerprint;

        public event EventHandler<DraftPreviewCompletedEventArgs> DraftPreviewCompleted;
        public event Action<GuideData> PlacementMaterializationCompleted;

        /// <summary>Smoothed render-frame time used by the draft scheduler as a secondary pressure signal.</summary>
        public double SmoothedFrameMilliseconds => _smoothedFrameMilliseconds;

        public bool DraftRefinementBusy
        {
            get { lock (_draftWorkGate) return _draftWorkBusy; }
        }

        /// <summary>
        /// True while a retained immense placement is still waiting for authority or finishing its final
        /// materialization. A second placement must not replace this renderer-owned handoff visual.
        /// </summary>
        public bool PlacementMaterializationBusy =>
            _pendingPlacementVisual != null || _settledMaterializations.Count > 0;

        /// <summary>
        /// True while a released immense sculpt is being validated and materialized. Its cheap working
        /// wireframe remains visible; a second edit must not replace the one bounded refinement lane.
        /// </summary>
        public bool SculptMaterializationBusy => _releasedGrabPending;

        private readonly Dictionary<Guid, GuideMesh> _guideMeshes = new Dictionary<Guid, GuideMesh>();
        private readonly Dictionary<string, Vec3d> _remoteAnchors = new Dictionary<string, Vec3d>();
        private readonly Dictionary<MeshRef, MeshCost> _meshCosts = new Dictionary<MeshRef, MeshCost>();

        // Surface guides whose air-side probe hit UNLOADED chunks (the world-load / approach-from-afar race):
        // their decal side is provisional and may render behind the block face. A low-frequency tick re-probes
        // and rebuilds each once its neighbourhood loads — fixing the "guide sinks behind the face after
        // reload" bug with no wire/persistence change. A guide sits here only while its chunk is unloaded.
        private readonly HashSet<Guid> _deferredSurface = new HashSet<Guid>();

        // Guides whose OCCUPANCY colours were probed against unloaded chunks — the same world-load race as
        // _deferredSurface above, and drained by the same tick. Populated only while the recolour is on.
        private readonly HashSet<Guid> _deferredOccupancy = new HashSet<Guid>();

        // RETIRED v0.3.70 — `_deferredSolidity`. Volumetric guides used to queue here when their z-fight
        // probe ran against unloaded chunks, so provisional insets could be rebuilt once terrain arrived.
        // The face offset no longer consults the world at all (see GuideMeshBuilder), so an unloaded chunk
        // cannot make it wrong and there is nothing to defer. Surface guides still probe for their decal
        // side and keep `_deferredSurface`.
        private long _reprobeListenerId;
        private const int ReprobeIntervalMs = 500;

        private MeshRef _markerMesh;         // shared, lazily uploaded on the first frame
        private readonly float[] _modelMat = Mat4f.Create();

        // Which control point (if any) is being actively dragged locally, so its voxels render White.
        private Guid _grabbedGuide = Guid.Empty;
        private int _grabbedIndex = -1;
        private int _grabAdaptiveScale;
        private int _grabHealthyUpdates;
        private double _grabBaselineFrameMilliseconds = 16.67;
        private GuideExtent _grabExtent = GuideExtent.Empty;
        private Guid _cancelRestoreGuide = Guid.Empty;
        private ulong _cancelRestoreFingerprint;

        public GuideExtent CurrentGrabExtent => _grabExtent;

        private bool _disposed;

        private enum VisibilityResult
        {
            Visible,
            BeyondViewDistance,
            OutsideFrustum
        }

        /// <summary>
        /// What one uploaded mesh costs the GPU to read every frame it is drawn. Sessions 25–26 compared
        /// TRIANGLE counts and drew the wrong conclusion twice: an 8M-voxel guide costs the same whether it
        /// fills the screen or is a speck on the horizon, which rules out fill rate and points at vertex/index
        /// fetch bandwidth instead. <see cref="Bytes"/> is therefore the metric that actually tracks the
        /// bottleneck, and <see cref="Vertices"/> is the one that will move when vertex welding lands —
        /// triangles stay identical there by construction, so a triangle-only readout would show nothing.
        /// </summary>
        private readonly struct MeshCost
        {
            public readonly int Triangles;
            public readonly int Vertices;
            public readonly int Indices;
            public readonly long Bytes;

            /// <summary>
            /// Voxel scale this mesh was built at, or 0 for geometry with no voxel lattice (remote draft
            /// markers). Recorded at upload so the voxel-boundary frame works on EVERY mesh set — draft
            /// ghost, cursor precision bands, materialization batches, pending placement — without each
            /// path having to remember to carry the scale to the render loop. The precision bands in
            /// particular are deliberately built at differing scales, so a single per-guide value would
            /// draw the wrong grid on them.
            /// </summary>
            public readonly int VoxelScale;

            public MeshCost(int triangles, int vertices, int indices, long bytes, int voxelScale)
            {
                Triangles = triangles;
                Vertices = vertices;
                Indices = indices;
                Bytes = bytes;
                VoxelScale = voxelScale;
            }

            /// <summary>
            /// Measures an about-to-be-uploaded mesh. Per-vertex size follows the arrays the
            /// <c>MeshData</c> actually carries (guides are xyz + uv + rgba = 24 bytes today; the uv pair is
            /// always (0,0) and is pure waste — see the Stage 2 note in <c>PLAN_RENDER_PERFORMANCE.md</c>).
            /// Indices are 4-byte ints. This is the client-side data volume, not an exact VRAM figure:
            /// driver padding and alignment are not modelled, and it is meant for A/B comparison between
            /// builds rather than as an absolute.
            /// </summary>
            public static MeshCost Measure(MeshData data, int voxelScale)
            {
                if (data == null) return default;

                int vertices = Math.Max(0, data.VerticesCount);
                int indices = Math.Max(0, data.IndicesCount);

                int perVertex = 12;                             // xyz float3, always present
                if (data.Normals != null) perVertex += 4;       // packed normal int
                if (data.Uv != null) perVertex += 8;            // uv float2
                if (data.Rgba != null) perVertex += 4;          // rgba bytes
                if (data.Flags != null) perVertex += 4;         // render flags int

                return new MeshCost(
                    indices / 3, vertices, indices,
                    (long)vertices * perVertex + (long)indices * 4L,
                    voxelScale);
            }
        }

        private sealed class RenderFrameStats
        {
            public int PlacedGuides;
            public int VisiblePlacedGuides;
            public int DistanceCulledGuides;
            public int FrustumCulledGuides;
            public int MeshDrawCalls;
            public long SubmittedTriangles;
            public long SubmittedVertices;
            public long SubmittedIndices;
            public long SubmittedBytes;
            public int VisibleDraftBatches;
            public int VisiblePendingBatches;
            public int VisibleRemoteMarkers;
            public int TotalRemoteMarkers;
        }

        private RenderFrameStats _lastRenderStats = new RenderFrameStats();

        /// <summary>A compiled guide mesh plus the world-space origin its vertices are relative to.</summary>
        private sealed class GuideMesh
        {
            public MeshRef Ref;
            public Vec3d Origin;
            public Vec3d CullCenter;
            public double CullRadius;
            public readonly List<MeshRef> Auxiliary = new List<MeshRef>();
            public MeshRef GrabRef;
            public Vec3d GrabOrigin;
            public readonly List<MeshRef> GrabAuxiliary = new List<MeshRef>();
            public MeshRef GrabCleanRef;
            public readonly List<MeshRef> GrabCleanAuxiliary = new List<MeshRef>();
            public MeshRef TransitionRef;
            public Vec3d TransitionOrigin;
            public readonly List<MeshRef> TransitionAuxiliary = new List<MeshRef>();
            public MeshRef TransitionCleanRef;
            public readonly List<MeshRef> TransitionCleanAuxiliary = new List<MeshRef>();
            public bool RenderedWireframe;

            /// <summary>
            /// The guide state the currently-uploaded WIREFRAME SCAFFOLD was built from, or 0 when this
            /// mesh is not a scaffold. Meaningful only while <see cref="RenderedWireframe"/> is true.
            /// </summary>
            /// <remarks>
            /// Exists to answer "is the wireframe on screen still a picture of THIS guide?". Without it
            /// <c>TryStartSettledShellMaterialization</c> could only ask whether a wireframe was showing at
            /// all, and would happily take over from a stale one — see its preconditions.
            /// </remarks>
            public ulong ScaffoldFingerprint;
        }

        public GuideRenderer(ICoreClientAPI capi, ClientNetworkHandler network)
        {
            _capi = capi ?? throw new ArgumentNullException(nameof(capi));
            _network = network ?? throw new ArgumentNullException(nameof(network));

            _network.GuideAddedOrUpdated += OnGuideAddedOrUpdated;
            _network.GuideRemoved += OnGuideRemoved;
            _network.GuidesBulkSynced += OnGuidesBulkSynced;
            _network.RemoteDraftAnchorChanged += OnRemoteDraftAnchorChanged;
            _network.RemoteDraftAnchorRemoved += OnRemoteDraftAnchorRemoved;
            _network.PlacementRejected += OnPlacementRejected;

            _capi.Event.RegisterRenderer(this, RenderStage, "layout-guides");
            _reprobeListenerId = _capi.Event.RegisterGameTickListener(OnReprobeTick, ReprobeIntervalMs);

            // Must re-register on every shader reload, or an in-game reload leaves a dead program behind.
            _capi.Event.ReloadShader += LoadCustomShader;
            LoadCustomShader();

            // Pick up anything already synced before the renderer existed (e.g. tool equipped after join).
            RebuildAll();
        }

        // -- IRenderer -----------------------------------------------------------------------------

        /// <summary>Draw order within the stage. Mid-range is fine for translucent overlay geometry.</summary>
        public double RenderOrder => 0.5;

        /// <summary>The live terrain view distance; individual guide bounds are culled against it below.</summary>
        public int RenderRange => ConfiguredViewDistance();

        /// <summary>Personal render switch controlled by /layout off and /layout on.</summary>
        public bool RenderingEnabled { get; private set; } = true;

        public void SetRenderingEnabled(bool enabled) => RenderingEnabled = enabled;

        /// <summary>
        /// Rebuilds every guide from scratch so a diagnostic mesh-build change (currently only
        /// <c>.layout weld</c>) takes effect on already-rendered guides. Cancels in-flight materializations
        /// first so nothing finishes into a mesh built under the previous setting.
        /// </summary>
        public void RebuildAllForDiagnostics()
        {
            if (_disposed) return;
            CancelAllSettledMaterializations();
            RebuildAll();
        }

        public string DescribeRenderStats()
        {
            if (!RenderingEnabled)
                return "Layout render stats: rendering is off.";

            RenderFrameStats stats = _lastRenderStats;
            return string.Format(
                "Layout render stats (last frame): {0}/{1} placed guide(s) visible; "
                + "{2} distance-culled; {3} off-screen; {4} mesh batch(es) and about {5:N0} triangle(s) "
                + "submitted. Mesh data read: {6} ({7:N0} vertices, {8:N0} indices). "
                + "Extras drawn: {9} draft batch(es), {10} pending batch(es), "
                + "{11}/{12} remote marker(s). Smoothed frame time: {13:0.0} ms.{14}",
                stats.VisiblePlacedGuides, stats.PlacedGuides,
                stats.DistanceCulledGuides, stats.FrustumCulledGuides,
                stats.MeshDrawCalls, stats.SubmittedTriangles,
                FormatBytes(stats.SubmittedBytes), stats.SubmittedVertices, stats.SubmittedIndices,
                stats.VisibleDraftBatches, stats.VisiblePendingBatches,
                stats.VisibleRemoteMarkers, stats.TotalRemoteMarkers,
                SmoothedFrameMilliseconds,
                DescribeFrameCap());
        }

        /// <summary>
        /// Warns when the frame-time reading is pinned to the game's own FPS limiter. Session 25 compared a
        /// capped baseline against an uncapped loaded frame and understated the guide's true cost for two
        /// sessions before anyone noticed; any A/B measurement taken at the cap is measuring the cap.
        /// </summary>
        /// <remarks>
        /// EVIDENCE, NOT SETTINGS (v0.3.56 fix). The first attempt trusted the <c>maxFps</c> setting and
        /// warned whenever frame time sat at or below its frame budget. Both halves were wrong. The setting
        /// keeps its slider value — observed as 241 — when the limiter is switched OFF, so it does not mean
        /// "capped"; and a capped frame can never run FASTER than the limiter, so "at or below" fired on
        /// every fast frame. A 1.0 ms uncapped baseline was flagged as clipped.
        ///
        /// A limiter can only ever HOLD frame time at its budget, so the sole reliable evidence of clipping
        /// is frame time sitting in a narrow band AROUND that budget. Well below means the setting is not
        /// limiting anything (uncapped, or a sentinel value); well above means the GPU is the limit and the
        /// cap is irrelevant. Nothing is reported unless the reading is actually suspect — a stray
        /// "FPS cap: 241" on an uncapped run is worse than silence, because it invites exactly the
        /// misreading this whole warning exists to prevent.
        /// </remarks>
        private string DescribeFrameCap()
        {
            int cap = ConfiguredFrameCap();
            if (cap <= 0) return string.Empty;

            double capFrameMs = 1000.0 / cap;
            double frameMs = SmoothedFrameMilliseconds;
            if (frameMs <= 0) return string.Empty;

            bool pinnedToLimiter = frameMs >= capFrameMs * 0.90 && frameMs <= capFrameMs * 1.10;
            return pinnedToLimiter
                ? string.Format(
                    " WARNING: this reading sits at your {0} FPS limit ({1:0.0} ms), so it is a floor rather "
                    + "than a cost. Uncap the frame rate before comparing builds.",
                    cap, capFrameMs)
                : string.Empty;
        }

        /// <summary>
        /// The game's configured frame-rate limit setting, or 0 when unreadable. The key is not part of the
        /// modding API contract, so several spellings are tried and every failure degrades to "unknown".
        /// A returned value is NOT proof of an active cap — the setting retains its slider value while the
        /// limiter is off — so <see cref="DescribeFrameCap"/> confirms it against measured frame time.
        /// </summary>
        private int ConfiguredFrameCap()
        {
            string[] keys = { "maxFps", "maxFPS", "maxfps" };
            for (int i = 0; i < keys.Length; i++)
            {
                try
                {
                    int value = _capi.Settings.Int[keys[i]];
                    if (value > 0 && value < 10000) return value;
                }
                catch
                {
                    // Unknown key for this game build — try the next spelling.
                }
            }
            return 0;
        }

        // -- Custom guide shader (Stage 2a) --------------------------------------------------------

        private IShaderProgram _guideShader;

        /// <summary>
        /// Use the lean custom shader instead of <c>PreparedStandardShader</c>. Falls back automatically
        /// when the program failed to compile, so a shader problem degrades to the old look rather than to
        /// invisible or corrupt guides.
        /// </summary>
        public bool UseCustomShader { get; private set; } = true;

        public bool CustomShaderAvailable => _guideShader != null;

        public void SetCustomShaderEnabled(bool enabled) => UseCustomShader = enabled;

        /// <summary>
        /// Compiles <c>assets/layout/shaders/guide.vsh</c>/<c>.fsh</c>. Registered against the engine's
        /// shader-reload event so it survives an in-game shader reload, the same lifecycle trap that made
        /// GUI icons vanish after exit-to-title in v0.1.24.
        /// </summary>
        public bool LoadCustomShader()
        {
            try
            {
                IShaderProgram program = _capi.Shader.NewShaderProgram();
                program.AssetDomain = "layout";
                _capi.Shader.RegisterFileShaderProgram("guide", program);

                if (!program.Compile() || program.LoadError)
                {
                    _capi.Logger.Warning(
                        "[Layout] Guide shader failed to compile; using the standard shader instead.");
                    _guideShader = null;
                    return false;
                }

                _guideShader = program;
                return true;
            }
            catch (Exception ex)
            {
                _capi.Logger.Warning("[Layout] Guide shader could not be loaded ({0}); "
                    + "using the standard shader instead.", ex.Message);
                _guideShader = null;
                return false;
            }
        }

        // ---- Block-occupancy recolour (v0.3.79, PLAN_BLOCK_OCCUPANCY stages 2 AND 3) ----
        //
        // Off by default and, when off, completely inert: OccupancyProbe stays null, so a guide built with
        // the toggle off is byte-identical to one built before the feature existed, and no world reads
        // happen at all. Turning it on rebuilds every guide, because the colour is baked into vertex data.
        //
        // THE COLOURS FOLLOW THE WORLD (stage 3, v0.3.81-v0.3.82; the per-batch rebuild that made it cheap
        // enough not to stutter followed in v0.3.83-v0.3.85). A block change inside a guide marks it
        // dirty; once the player stops changing blocks, only the BATCHES holding those blocks are re-meshed
        // — see OnWorldBlockChanged, OnOccupancyTick and TryUpdateOccupancyBatches. The plan's other route,
        // a shader lookup, was rejected: updates happen about twice a second and fragments tens of millions
        // of times a second, so it would have made updates free by taxing every frame forever.
        //
        // Two things still are not live, both reported rather than hidden: a guide over
        // OccupancyBatchVoxelCeiling can never be serviced (OccupancyStaleGuides), and a guide first meshed
        // against unloaded terrain re-probes itself as the chunks arrive (_deferredOccupancy, v0.4.42).
        //
        // This header read "STATIC BY DESIGN AT THIS STAGE ... live updating is stage 3" until the
        // 2026-08-01 sweep, three sessions after stage 3 shipped in the same file.
        private BlockOccupancy _occupancy;
        private bool _occupancyEnabled;

        /// <summary>Whether body voxels holding world material are drawn in the "built" colour.</summary>
        public bool OccupancyEnabled => _occupancyEnabled;

        /// <summary>Blocks whose occupancy is currently cached — diagnostic readout only.</summary>
        public int OccupancyCachedBlocks => _occupancy?.CachedBlocks ?? 0;

        /// <summary>
        /// Turns the recolour on or off and rebuilds every guide to match. Returns false if the state was
        /// already what was asked for, so the caller can say "already on" rather than pay for a rebuild.
        /// </summary>
        public bool SetOccupancyEnabled(bool enabled)
        {
            if (_occupancyEnabled == enabled) return false;
            _occupancyEnabled = enabled;

            // The cache is dropped on every transition, not kept: while the toggle was off nothing was
            // watching the world, so anything still in it is of unknown age.
            _occupancy?.Clear();
            if (enabled) _occupancy ??= new BlockOccupancy();

            // Nothing watches the world while the feature is off — no event handler, no tick.
            SetOccupancySubscribed(enabled);

            RebuildAllForDiagnostics();
            return true;
        }

        /// <summary>
        /// Discards ALL cached occupancy and rebuilds every guide, so the colours re-read the world as it
        /// is now. What <c>/layout built refresh</c> runs.
        /// </summary>
        /// <remarks>
        /// The blunt instrument, not the normal path — ordinary guides follow block changes by themselves.
        /// This is for the two cases that cannot: a guide too large to service live, and anything still
        /// showing colours from before its terrain loaded.
        /// </remarks>
        public void RefreshOccupancy()
        {
            if (!_occupancyEnabled) return;
            _occupancy?.Clear();
            _occupancyDirty.Clear();          // a full rebuild subsumes every pending one
            _occupancyPermanentlyStale.Clear();
            _occupancyChangedBlocks.Clear();
            _occBatches = null;               // rebuilt meshes invalidate the recorded batch ranges
            RebuildAllForDiagnostics();
        }

        /// <summary>
        /// Guides known to be showing stale colours — changed while too large to rebuild live. Lets the UI
        /// say "these need a refresh" instead of leaving the player to wonder.
        /// </summary>
        public int OccupancyStaleGuides => _occupancyDirty.Count + _occupancyPermanentlyStale.Count;

        /// <summary>
        /// Guides whose colours are waiting on their terrain to load, and will re-probe themselves once it
        /// does (v0.4.42). Distinct from <see cref="OccupancyStaleGuides"/>: those are guides too large to
        /// update live, these are guides the world had not streamed in yet.
        /// </summary>
        public int OccupancyAwaitingChunks => _deferredOccupancy.Count;

        // ---- Stage 3: live occupancy updates ----
        //
        // Guides whose colours are out of date because a block inside them changed. Rebuilt once the
        // player stops changing blocks, not per change: a chisel burst is many events a second and each
        // one would otherwise re-mesh the guide.
        private readonly HashSet<Guid> _occupancyDirty = new HashSet<Guid>();

        // Guides that are stale and can NEVER be serviced live, because they are over
        // OccupancyBatchVoxelCeiling and BeginOccupancyBatchBuild refuses to build them a context.
        //
        // ⚠️ THEY ARE HELD SEPARATELY FOR A REASON (A14.5, fixed v0.4.38). They used to sit in
        // _occupancyDirty, which they could never leave — and _occupancyChangedBlocks is cleared only when
        // that set EMPTIES. So one >3M-voxel guide near the player meant the changed-block list grew by
        // every qualifying block change for the rest of the session, and every 50 ms tick re-scanned the
        // whole accumulated list on behalf of the other dirty guides. The permanent staleness is intended
        // and still reported; the unbounded list was not.
        private readonly HashSet<Guid> _occupancyPermanentlyStale = new HashSet<Guid>();

        // The blocks behind the pending dirty guides — a per-batch rebuild needs to know WHERE, not just
        // which guide. Copied on arrival: BlockPos is mutable and the caller reuses it.
        private readonly List<BlockPos> _occupancyChangedBlocks = new List<BlockPos>();
        private long _occupancyLastChangeMs;
        private long _occupancyListenerId;
        private bool _occupancySubscribed;

        /// <summary>
        /// Quiet period before a dirty guide is rebuilt.
        ///
        /// WAS 250 ms (v0.3.81), sized to swallow a "chisel burst". The human corrected that at v0.3.82:
        /// chiselling is rate-limited to roughly twice a second, so strokes arrive ~500 ms apart and a
        /// 250 ms wait coalesced nothing — it was pure added latency between the cut and the colour.
        ///
        /// A short settle still earns its place, because ONE stroke does not mean one event: a chiselled
        /// block's neighbours re-mark themselves (`BlockMicroBlock.OnNeighbourBlockChange` calls
        /// `MarkDirty`), so a single cut can arrive as several `BlockChanged` callbacks a few milliseconds
        /// apart. This is sized to absorb that and nothing more.
        /// </summary>
        private const int OccupancySettleMs = 80;

        private const int OccupancyTickMs = 50;

        /// <summary>
        /// How far from the player a block change is still worth looking at, in blocks. The human's
        /// observation (2026-07-26): the block you are placing or chiselling is always within a couple of
        /// blocks of you, so anything further away is somebody else's work or an unrelated block entity
        /// ticking. One distance compare is the cheapest possible rejection and it runs before anything
        /// else touches the guide list. Generous rather than tight — being wrong here means missing an
        /// update, which is worse than doing a little extra work.
        /// </summary>
        private const double OccupancyPlayerRadius = 8.0;

        // RETIRED v0.3.84 — `OccupancyLiveVoxelCeiling`. Guides under it took a whole-guide rebuild on the
        // render thread, which was the stutter reported at v0.3.83. Every guide now uses the batch path.

        private void OnWorldBlockChanged(BlockPos pos, Block oldBlock)
        {
            if (_disposed || !_occupancyEnabled || pos == null || _occupancy == null) return;

            var p = _capi.World?.Player?.Entity?.Pos;
            if (p == null) return;

            double dx = pos.X + 0.5 - p.X;
            double dy = pos.Y + 0.5 - p.Y;
            double dz = pos.Z + 0.5 - p.Z;
            if (dx * dx + dy * dy + dz * dz > OccupancyPlayerRadius * OccupancyPlayerRadius) return;

            // The cached answer for this block is now wrong whatever else happens.
            _occupancy.Invalidate(pos);
            _occupancyChangedBlocks.Add(pos.Copy());

            bool any = false;
            foreach (KeyValuePair<Guid, GuideMesh> entry in _guideMeshes)
            {
                Vec3d centre = entry.Value?.CullCenter;
                if (centre == null) continue;

                // The cull sphere is a conservative bound on the guide, so a miss here is a certain miss.
                // One block of slack covers a change sitting just outside it that still fills a voxel on
                // the guide's own surface.
                double r = entry.Value.CullRadius + 1.0;
                double gx = pos.X + 0.5 - centre.X;
                double gy = pos.Y + 0.5 - centre.Y;
                double gz = pos.Z + 0.5 - centre.Z;
                if (gx * gx + gy * gy + gz * gz > r * r) continue;

                _occupancyDirty.Add(entry.Key);
                any = true;
            }

            if (any) _occupancyLastChangeMs = _capi.World.ElapsedMilliseconds;
        }

        private void OnOccupancyTick(float dt)
        {
            if (_disposed) return;
            DrainOccupancyStaging();
            if (_occupancyDirty.Count == 0) return;
            if (_capi.World.ElapsedMilliseconds - _occupancyLastChangeMs < OccupancySettleMs) return;

            List<Guid> done = null;
            foreach (Guid id in _occupancyDirty)
            {
                _network.Guides.TryGetValue(id, out GuideData g);
                if (g == null)
                {
                    (done ??= new List<Guid>()).Add(id);
                    _occupancyPermanentlyStale.Remove(id);   // gone entirely — not stale, just absent
                    continue;
                }

                // EVERY guide goes through the batch path (v0.3.84), not just large ones. The old
                // small-guide shortcut re-meshed the whole guide on the render thread, and "sub-frame" was
                // measured against a 60 FPS budget — at 235 FPS it is four frames, which is exactly the
                // stutter the human saw. A tiny guide simply gets one batch, and it is meshed on a worker
                // like every other.
                if (TryUpdateOccupancyBatches(id, g, _occupancyChangedBlocks))
                {
                    (done ??= new List<Guid>()).Add(id);
                    // It was serviced after all — a guide that has since shrunk under the ceiling, or one
                    // whose context already existed. It is no longer permanently stale.
                    _occupancyPermanentlyStale.Remove(id);
                    continue;
                }

                // OVER THE BATCH CEILING = no context will ever be built for it (see
                // BeginOccupancyBatchBuild), so leaving it in the dirty set would pin _occupancyChangedBlocks
                // open forever. Retire it to the permanently-stale set: still counted and still reported by
                // OccupancyStaleGuides, but no longer holding the changed-block list hostage. A later block
                // change near it puts it back in the dirty set, so a shrink still gets picked up.
                if (g.CachedVoxelCount > OccupancyBatchVoxelCeiling)
                {
                    (done ??= new List<Guid>()).Add(id);
                    _occupancyPermanentlyStale.Add(id);
                    continue;
                }

                // No usable context yet. Build one in the background; this guide stays dirty and gets
                // picked up on a later tick, by which time the swap path is available.
                BeginOccupancyBatchBuild(g);
            }

            if (done == null) return;
            for (int i = 0; i < done.Count; i++) _occupancyDirty.Remove(done[i]);
            if (_occupancyDirty.Count == 0) _occupancyChangedBlocks.Clear();
        }

        // =============================================================================================
        //  Stage 3b — per-batch occupancy rebuild
        // =============================================================================================
        //
        // WHY THIS EXISTS. Occupancy colour is baked into vertex data, so a change means re-meshing. A
        // whole large guide is far too much to re-mesh twice a second, which is why v0.3.81 simply refused
        // to update guides over the streaming threshold. This splits such a guide into batches once, then
        // re-meshes only the batch containing the block that changed — roughly 1/128 of the work.
        //
        // WHY IT IS SAFE, given CLAUDE.md's warning that regrouping primitives changes the picture. The
        // guide's meshes are drawn in list order (primary, then Auxiliary in sequence), and every batch is
        // a CONTIGUOUS RUN of one X-sorted voxel list. The concatenation of the batches is therefore the
        // same primitive sequence no matter where the boundaries fall — moving a boundary reorders nothing.
        // That is what lets this choose its own batching instead of having to reproduce the streaming
        // pipeline's, and it is why this is not the Session 25-26 spatial-partitioning experiment.
        //
        // WHY IT IS CHEAPER THAN THE SHADER ALTERNATIVE (the human's question, 2026-07-26): updates happen
        // about twice a second, fragments tens of millions of times a second. A shader lookup would make
        // updates near-free by taxing every fragment forever. This adds nothing to the rendering path.
        //
        // The context is built ONCE per guide, on a background thread, and then reused. Only one is held
        // at a time — you work on one guide at a time, and the voxel list plus its lookup set is the bulk
        // of the memory.
        private sealed class OccupancyBatches
        {
            public Guid GuideId;
            public ulong Fingerprint;                 // invalidates the context when the guide changes
            public List<VoxelPosition> Sorted;        // X, then Y, then Z — batches are runs of this
            public GuideMeshOptions Template;         // carries the cross-batch Occupancy set and MinimumVoxelY
            public Vec3d Origin;
            public readonly List<int> Starts = new List<int>();
            public readonly List<int> Counts = new List<int>();
        }

        /// <summary>
        /// A context being built. Batches are handed over ONE AT A TIME and uploaded as they arrive
        /// (v0.3.85), rather than accumulating every batch's mesh data and uploading at the end.
        /// </summary>
        /// <remarks>
        /// The first version held all of it: a 3M-voxel guide's mesh data is hundreds of megabytes (the
        /// Session-28 figures put 8M voxels at 771 MB after welding), so the build spiked memory by more
        /// than the finished context costs. Uploading as we go releases each batch's CPU copy immediately
        /// and leaves only GPU buffers, which is what the voxel ceiling was really guarding against.
        ///
        /// The guide's meshes are still swapped ALL AT ONCE at the end. Swapping progressively would show
        /// the guide re-growing batch by batch, which is precisely the visible re-streaming this whole
        /// stage exists to avoid.
        /// </remarks>
        private sealed class OccupancyBatchStaging
        {
            public OccupancyBatches Ctx;                  // published by the worker once sorting is done
            public readonly ConcurrentQueue<MeshData> Ready = new ConcurrentQueue<MeshData>();
            public volatile bool Producing = true;
            public volatile bool Failed;
            public MeshRef[] Uploaded;
            public int UploadedCount;

            // Taken from Ctx rather than kept as its own field: Ctx is fully populated before the worker
            // publishes it, so reading the count through it needs no separate cross-thread guarantee.
            public int Expected => Ctx?.Starts.Count ?? -1;
        }

        private OccupancyBatches _occBatches;
        private OccupancyBatchStaging _occStaging;
        private bool _occBatchBuilding;
        private bool _occBatchMeshing;

        /// <summary>Batches uploaded per tick while streaming a context in. Keeps the render thread flat.</summary>
        private const int OccupancyUploadsPerTick = 6;

        /// <summary>
        /// Guides larger than this do not get a batch context — the voxel list and its lookup set would
        /// cost more memory than the feature is worth. They stay on the manual refresh.
        /// </summary>
        private const int OccupancyBatchVoxelCeiling = 3_000_000;

        // ^ A JUDGEMENT CALL, not a measurement — the same species as SettledStreamingVoxelThreshold, whose
        // guessed value caused the v0.3.83 stutter, so treat it with suspicion. What it costs at the cap:
        // the retained voxel list is 16 bytes each (~48 MB) and its cross-batch lookup set roughly twice
        // that (~90 MB), so ~140 MB resident for one context, briefly doubled while the pre-sort list is
        // still alive. Since v0.3.85 the mesh data no longer piles up on top of that, which is what made
        // the original number reckless. Only ever one context is held.

        /// <summary>
        /// Target voxels per batch, 128 batches max (matching the materialization pipeline, so draw-call
        /// count is unchanged). Smaller batches mean less to re-mesh per update; the cap means a very large
        /// guide still ends up with big ones, which is fine now that meshing is off the render thread.
        /// </summary>
        private const int OccupancyBatchTargetVoxels = 2500;

        private bool HasOccupancyBatches(Guid id, GuideData guide) =>
            _occBatches != null && _occBatches.GuideId == id
            && _occBatches.Fingerprint == RenderFingerprint(guide);

        /// <summary>
        /// Re-meshes only the batches covering <paramref name="blocks"/>. Returns false when there is no
        /// usable context, so the caller can fall back to leaving the guide dirty.
        /// </summary>
        private bool TryUpdateOccupancyBatches(Guid id, GuideData guide, List<BlockPos> blocks)
        {
            if (!HasOccupancyBatches(id, guide) || blocks == null || blocks.Count == 0) return false;
            if (!_guideMeshes.TryGetValue(id, out GuideMesh mesh)) return false;

            OccupancyBatches ctx = _occBatches;
            if (ctx.Starts.Count != mesh.Auxiliary.Count + 1) return false;   // meshes moved under us

            var touched = new HashSet<int>();
            for (int i = 0; i < blocks.Count; i++)
            {
                BlockPos p = blocks[i];
                if (p == null) continue;
                // Cell-space span of this world block. The sorted list is X-major, so the affected voxels
                // form one contiguous index range.
                CollectBatchesForCellRange(ctx, p.X * 16, p.X * 16 + 15, touched);
            }
            if (touched.Count == 0) return true;   // nothing of this guide sits in those blocks

            // MESH OFF THE RENDER THREAD (v0.3.84). Doing this inline cost ~18 ms per update — one 45 FPS
            // frame against a 235 FPS baseline, reported by the human at v0.3.83. Meshing is pure CPU work
            // and GuideMeshBuilder is already called from background threads by materialization; only the
            // GPU upload has to happen here. So the render thread now pays an upload and nothing else.
            if (_occBatchMeshing) return true;   // one in flight; later changes ride the next tick

            var slots = new List<int>(touched);
            ctx.Template.OccupancyProbe = OccupancyProbe();
            _occBatchMeshing = true;
            ulong fingerprint = ctx.Fingerprint;

            Task.Factory.StartNew(() =>
            {
                var built = new List<MeshData>(slots.Count);
                for (int i = 0; i < slots.Count; i++)
                {
                    int slot = slots[i];
                    // Sorted is immutable once the context is built, so reading it here needs no lock.
                    built.Add(GuideMeshBuilder.Build(
                        ctx.Sorted.GetRange(ctx.Starts[slot], ctx.Counts[slot]), ctx.Template));
                }
                return built;
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default)
            .ContinueWith(task =>
            {
                List<MeshData> built = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                try
                {
                    _capi.Event.EnqueueMainThreadTask(
                        () => ApplyOccupancyBatchMeshes(id, fingerprint, slots, built),
                        "layout-occupancy-batch-swap");
                }
                catch { _occBatchMeshing = false; }
            });
            return true;
        }

        // Main thread, and deliberately nothing but uploads: the meshing already happened on a worker.
        private void ApplyOccupancyBatchMeshes(
            Guid id, ulong fingerprint, List<int> slots, List<MeshData> built)
        {
            _occBatchMeshing = false;
            if (_disposed || built == null) return;

            OccupancyBatches ctx = _occBatches;
            if (ctx == null || ctx.GuideId != id || ctx.Fingerprint != fingerprint) return;
            if (!_guideMeshes.TryGetValue(id, out GuideMesh mesh)) return;
            if (ctx.Starts.Count != mesh.Auxiliary.Count + 1) return;

            for (int i = 0; i < slots.Count && i < built.Count; i++)
            {
                MeshRef uploaded = UploadTrackedMesh(built[i]);
                if (uploaded == null) continue;
                int slot = slots[i];
                if (slot == 0) { DeleteTrackedMesh(mesh.Ref); mesh.Ref = uploaded; }
                else { DeleteTrackedMesh(mesh.Auxiliary[slot - 1]); mesh.Auxiliary[slot - 1] = uploaded; }
            }
        }

        // Batches are runs of an X-sorted list, so every batch whose X span overlaps [cellX0, cellX1]
        // is touched. A linear pass over at most 128 batches is cheaper than binary searching for it.
        private static void CollectBatchesForCellRange(
            OccupancyBatches ctx, int cellX0, int cellX1, HashSet<int> into)
        {
            for (int slot = 0; slot < ctx.Starts.Count; slot++)
            {
                int start = ctx.Starts[slot], count = ctx.Counts[slot];
                if (count <= 0) continue;
                int firstX = ctx.Sorted[start].X;
                int lastX = ctx.Sorted[start + count - 1].X;
                if (lastX < cellX0 || firstX > cellX1) continue;
                into.Add(slot);
            }
        }

        /// <summary>
        /// Starts building the batch context for a guide, on a background thread. One at a time.
        /// </summary>
        private void BeginOccupancyBatchBuild(GuideData guide)
        {
            if (_occBatchBuilding || guide == null) return;
            if (guide.CachedVoxelCount > OccupancyBatchVoxelCeiling) return;

            _occBatchBuilding = true;
            Guid id = guide.Id;
            ulong fingerprint = RenderFingerprint(guide);
            bool privateAnchors = _network.ServerLayoutAvailable && _network.IsLocalGuide(id);
            System.Func<int, int, int, bool> probe = OccupancyProbe();

            var staging = new OccupancyBatchStaging();
            _occStaging = staging;

            Task.Factory.StartNew(
                    () => BuildOccupancyBatches(guide, id, fingerprint, privateAnchors, probe, staging),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ContinueWith(task =>
                {
                    if (task.Status != TaskStatus.RanToCompletion) staging.Failed = true;
                    staging.Producing = false;   // the drain finishes or discards from here
                });
        }

        /// <summary>
        /// Uploads whatever the worker has finished, a few per tick, and commits once every batch is in.
        /// Called from the occupancy tick; does nothing when no build is in flight.
        /// </summary>
        private void DrainOccupancyStaging()
        {
            OccupancyBatchStaging s = _occStaging;
            if (s == null) return;

            if (s.Failed) { DiscardOccupancyStaging(s); return; }
            if (s.Ctx == null) return;                     // still sorting; nothing to upload yet

            if (s.Uploaded == null && s.Expected >= 0) s.Uploaded = new MeshRef[s.Expected];
            if (s.Uploaded == null) return;

            for (int n = 0; n < OccupancyUploadsPerTick && s.Ready.TryDequeue(out MeshData data); n++)
            {
                if (s.UploadedCount < s.Uploaded.Length)
                    s.Uploaded[s.UploadedCount] = UploadTrackedMesh(data);
                s.UploadedCount++;
            }

            if (s.Producing || s.Ready.Count > 0 || s.UploadedCount < s.Expected) return;
            CommitOccupancyStaging(s);
        }

        private void CommitOccupancyStaging(OccupancyBatchStaging s)
        {
            _occStaging = null;
            _occBatchBuilding = false;

            // The guide may have been reshaped or deleted while we built. Nothing to salvage if so.
            if (_disposed
                || !_network.Guides.TryGetValue(s.Ctx.GuideId, out GuideData live) || live == null
                || RenderFingerprint(live) != s.Ctx.Fingerprint
                || !_guideMeshes.TryGetValue(s.Ctx.GuideId, out GuideMesh mesh))
            {
                DiscardUploaded(s);
                return;
            }

            // One atomic swap — the guide never renders half-old, half-new.
            DeleteTrackedMesh(mesh.Ref);
            DeleteAuxiliaryMeshes(mesh);
            mesh.Ref = null;
            for (int i = 0; i < s.Uploaded.Length; i++)
            {
                MeshRef m = s.Uploaded[i];
                if (m == null) continue;
                if (mesh.Ref == null) mesh.Ref = m;
                else mesh.Auxiliary.Add(m);
            }
            mesh.Origin = s.Ctx.Origin;
            _occBatches = s.Ctx;
        }

        private void DiscardOccupancyStaging(OccupancyBatchStaging s)
        {
            if (!ReferenceEquals(_occStaging, s)) return;
            _occStaging = null;
            _occBatchBuilding = false;
            DiscardUploaded(s);
        }

        private void DiscardUploaded(OccupancyBatchStaging s)
        {
            if (s.Uploaded == null) return;
            for (int i = 0; i < s.Uploaded.Length; i++) DeleteTrackedMesh(s.Uploaded[i]);
            s.Uploaded = null;
        }

        private static void BuildOccupancyBatches(
            GuideData guide, Guid id, ulong fingerprint, bool privateAnchors,
            System.Func<int, int, int, bool> probe, OccupancyBatchStaging staging)
        {
            IGuideShape shape = ShapeFactory.Adopt(guide);
            shape.RecalculatePhantomPoints();
            int scale = guide.VoxelScale;
            List<VoxelPosition> voxels = shape.GetVoxelPositions(scale, guide.IsFilled);
            if (voxels == null || voxels.Count == 0) { staging.Failed = true; return; }
            if (guide.Divisions > 1)
                DivisionMarks.Apply(voxels, shape.SampleCurve(128), guide.Divisions, scale);

            // Same ordering the materialization pipeline uses, for the same reason: it makes each batch a
            // slab of the guide, so a block change lands in one or two of them.
            var sorted = new List<VoxelPosition>(voxels);
            sorted.Sort((a, b) =>
            {
                int byX = a.X.CompareTo(b.X);
                if (byX != 0) return byX;
                int byY = a.Y.CompareTo(b.Y);
                return byY != 0 ? byY : a.Z.CompareTo(b.Z);
            });

            var occupancy = new HashSet<(int, int, int)>(sorted.Count);
            int minimumY = int.MaxValue;
            for (int i = 0; i < sorted.Count; i++)
            {
                VoxelPosition v = sorted[i];
                occupancy.Add((v.X, v.Y, v.Z));
                if (v.Y < minimumY) minimumY = v.Y;
            }

            Vec3d origin = ComputeOrigin(shape.ControlPoints);
            var template = new GuideMeshOptions
            {
                Scale = scale,
                Mode = ProjectionMode.Volumetric,
                Plane = guide.Plane,
                Origin = origin,
                Hidden = guide.IsHidden,
                GrabbedPoint = null,
                PrivateAnchors = privateAnchors,
                Occupancy = occupancy,
                MinimumVoxelY = minimumY,
                OccupancyProbe = probe
            };
            AssignAnchors(shape.ControlPoints, template);

            var ctx = new OccupancyBatches
            {
                GuideId = id,
                Fingerprint = fingerprint,
                Sorted = sorted,
                Template = template,
                Origin = origin
            };

            int batchCount = Math.Max(1, Math.Min(MaterializationMaximumBatches,
                (sorted.Count + OccupancyBatchTargetVoxels - 1) / OccupancyBatchTargetVoxels));
            int batchSize = Math.Max(1, (sorted.Count + batchCount - 1) / batchCount);
            for (int start = 0; start < sorted.Count; start += batchSize)
            {
                ctx.Starts.Add(start);
                ctx.Counts.Add(Math.Min(batchSize, sorted.Count - start));
            }

            // Published before the first batch is queued, so the drain knows how many to expect and can
            // start uploading while the rest are still being meshed.
            staging.Ctx = ctx;

            for (int i = 0; i < ctx.Starts.Count; i++)
            {
                staging.Ready.Enqueue(GuideMeshBuilder.Build(
                    sorted.GetRange(ctx.Starts[i], ctx.Counts[i]), template));

                // Back-pressure: without it the worker races ahead and every batch's mesh data piles up in
                // the queue, which is the memory spike this rewrite exists to remove. The drain uploads a
                // few per tick, so a shallow queue is all that is needed to keep it fed.
                while (staging.Ready.Count >= OccupancyUploadsPerTick * 4)
                {
                    if (staging.Failed) return;   // abandoned (toggle turned off, guide gone) — stop meshing
                    System.Threading.Thread.Sleep(2);
                }
            }
        }

        private void SetOccupancySubscribed(bool subscribe)
        {
            if (subscribe == _occupancySubscribed) return;
            if (subscribe)
            {
                _capi.Event.BlockChanged += OnWorldBlockChanged;
                _occupancyListenerId = _capi.Event.RegisterGameTickListener(OnOccupancyTick, OccupancyTickMs);
            }
            else
            {
                _capi.Event.BlockChanged -= OnWorldBlockChanged;
                if (_occupancyListenerId != 0) _capi.Event.UnregisterGameTickListener(_occupancyListenerId);
                _occupancyListenerId = 0;
                _occupancyDirty.Clear();
                _occupancyPermanentlyStale.Clear();
                _occupancyChangedBlocks.Clear();
                _deferredOccupancy.Clear();   // nothing to re-probe for: the colours are going away
                _occBatches = null;   // the guide's meshes are about to be rebuilt without occupancy

                // Stop any build in flight. Without this the worker parks forever in its back-pressure
                // wait, because nothing is draining the queue any more.
                OccupancyBatchStaging inFlight = _occStaging;
                if (inFlight != null) { inFlight.Failed = true; DiscardOccupancyStaging(inFlight); }
            }
            _occupancySubscribed = subscribe;
        }

        /// <summary>
        /// The probe handed to the mesh builder, or null when the feature is off. Captures the accessor
        /// once rather than reaching through <c>_capi.World</c> per voxel, since this is called from
        /// background materialization threads.
        /// </summary>
        private System.Func<int, int, int, bool> OccupancyProbe()
        {
            if (!_occupancyEnabled || _occupancy == null) return null;
            IBlockAccessor accessor = _capi?.World?.BlockAccessor;
            if (accessor == null) return null;
            return (x16, y16, z16) => _occupancy.IsMaterialAt(accessor, x16, y16, z16);
        }

        private float _shaderBrightness = 0.78f;
        private float _shaderAmbientResponse = 0.55f;
        private float _voxelFrameStrength = 0.25f;

        /// <summary>How strongly each voxel's boundary is darkened, 0 = off. Custom shader only.</summary>
        public float VoxelFrameStrength => _voxelFrameStrength;

        public void SetVoxelFrameStrength(float strength) => _voxelFrameStrength = strength;

        /// <summary>
        /// Sets the custom shader's brightness controls. Both are clamped by the config's Normalize().
        /// </summary>
        public void SetShaderBrightness(float brightness, float ambientResponse)
        {
            _shaderBrightness = brightness;
            _shaderAmbientResponse = ambientResponse;
        }

        public float ShaderBrightness => _shaderBrightness;
        public float ShaderAmbientResponse => _shaderAmbientResponse;

        /// <summary>
        /// The per-frame RGB multiplier that stands in for the standard shader's lighting and shadow terms.
        /// </summary>
        /// <remarks>
        /// The standard shader darkened guides two ways: <c>applyLight()</c> mixed the forced-white block
        /// light with the world's ambient colour, and the fragment stage then multiplied by shadow-map
        /// brightness. Neither is reproducible in a mod shader — the shadow samplers are not exposed — so
        /// this approximates the visible result at effectively zero cost: blend white toward the live
        /// ambient colour by <see cref="_shaderAmbientResponse"/> (restoring day/night response and tint),
        /// then scale by <see cref="_shaderBrightness"/> (restoring the overall darkening).
        ///
        /// It cannot reproduce per-pixel shadowing or torch response, and is not meant to. It is an eye
        /// match for the average case, tuned in play rather than derived.
        /// </remarks>
        private Vec3f ResolveGuideBrightness()
        {
            Vec3f ambient = _capi.Render.AmbientColor;
            float response = _shaderAmbientResponse;

            float r = 1f, g = 1f, b = 1f;
            if (ambient != null && response > 0f)
            {
                r = 1f + (ambient.R - 1f) * response;
                g = 1f + (ambient.G - 1f) * response;
                b = 1f + (ambient.B - 1f) * response;
            }

            return new Vec3f(
                Math.Max(0f, r * _shaderBrightness),
                Math.Max(0f, g * _shaderBrightness),
                Math.Max(0f, b * _shaderBrightness));
        }

        /// <summary>
        /// Feeds the custom program the uniforms its fog term needs. Values come from the ambient manager,
        /// which is the same blended state the engine hands its own shaders, so guides fade on the same
        /// curve as the world rather than on an approximation of it.
        /// </summary>
        private void PrepareCustomShader(IShaderProgram prog, IRenderAPI rpi)
        {
            prog.Use();
            prog.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
            prog.UniformMatrix("viewMatrix", rpi.CameraMatrixOriginf);

            prog.Uniform("layoutBrightnessIn", ResolveGuideBrightness());

            Vec4f fogColor = _capi.Ambient.BlendedFogColor;
            prog.Uniform("layoutFogColorIn", fogColor);
            prog.Uniform("layoutFogMinIn", _capi.Ambient.BlendedFogMin);
            prog.Uniform("layoutFogDensityIn", _capi.Ambient.BlendedFogDensity);
            prog.Uniform("layoutFlatFogDensityIn", _capi.Ambient.BlendedFlatFogDensity);
            // ...ForShader is the variant the engine feeds its own shaders (camera-relative), which is what
            // getFogLevel's flatFogStart term expects. BlendedFlatFogYOffset is world-absolute and wrong here.
            prog.Uniform("layoutFlatFogStartIn", _capi.Ambient.BlendedFlatFogYPosForShader);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024L) return bytes + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GB";
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (_disposed || stage != RenderStage) return;
            if (!RenderingEnabled)
            {
                _lastRenderStats = new RenderFrameStats();
                return;
            }
            double frameMs = Math.Max(1.0, Math.Min(250.0, deltaTime * 1000.0));
            _smoothedFrameMilliseconds += (frameMs - _smoothedFrameMilliseconds) * 0.08;
            AdvanceRetiredMaterializationDeletes();
            AdvanceMoveHolds();
            AdvanceDraftMaterialization();
            AdvancePendingPlacementMaterialization();
            AdvanceSettledMaterializations();
            AdvanceGrabMaterialization();
            RequestSettledGrabRefinement();
            if (_guideMeshes.Count == 0 && _remoteAnchors.Count == 0 && _draftPreviewMesh == null
                && _draftPrecisionMeshes.Count == 0 && _draftMaterializationMeshes.Count == 0
                && !HasUnadoptedPendingPlacementVisual())
            {
                _lastRenderStats = new RenderFrameStats();
                return;
            }

            IClientPlayer player = _capi.World?.Player;
            if (player?.Entity == null) return;
            Vec3d camPos = player.Entity.CameraPos;
            double viewDistance = ConfiguredViewDistance();
            var stats = new RenderFrameStats
            {
                TotalRemoteMarkers = _remoteAnchors.Count
            };

            EnsureMarkerMesh();
            EnsureWhiteTexture();

            // ─────────────────────────────────────────────────────────────────────────────────────────
            //  VS RENDER CALLS — verified in-game during Module 7. The recipe that works:
            //    • Opaque stage + manual GlToggleBlend (the OIT stage needs VS's own OIT shaders — see
            //      RenderStage above);
            //    • StandardShader with a REAL white texture on Tex2D (id 0 samples garbage), NormalShaded 0,
            //      RgbaTint white, RgbaLightIn white (unlit, so the vertex palette shows verbatim);
            //    • ViewMatrix = CameraMatrixOriginf; ProjectionMatrix = CurrentProjectionMatrix;
            //      ModelMatrix = identity translated by (meshOrigin − cameraPos).
            // ─────────────────────────────────────────────────────────────────────────────────────────
            IRenderAPI rpi = _capi.Render;
            // Vintage Story refreshes this culler immediately before the Opaque stage. Its planes use world
            // coordinates, matching CullCenter, and already follow the live camera/FOV and camera mode.
            FrustumCulling frustumCuller = rpi.DefaultFrustumCuller;
            rpi.GlToggleBlend(true);
            rpi.GlDisableCullFace(); // translucent guides are drawn double-sided

            // SHADER SELECTION (Stage 2a). The custom program does only what a guide needs; the standard
            // program is the fallback and the reference look. Falling back on a failed compile is
            // deliberate — a shader problem should cost performance, never make guides invisible.
            bool useCustom = UseCustomShader && _guideShader != null;
            IStandardShaderProgram prog = null;
            IShaderProgram activeProg;

            if (useCustom)
            {
                PrepareCustomShader(_guideShader, rpi);
                activeProg = _guideShader;
            }
            else
            {
                // PreparedStandardShader (NOT raw StandardShader.Use()): it configures ALL the standard
                // shader's uniforms — shadow map, ambient, fog — for the given world position and calls
                // Use(). Module-7 in-game finding: with raw Use(), the unset shadow/ambient uniforms
                // multiplied every fragment to black in the Opaque stage. We then override the lighting
                // inputs to full-bright so the guide palette shows verbatim, day or night.
                prog = rpi.PreparedStandardShader((int)camPos.X, (int)camPos.Y, (int)camPos.Z);
                prog.RgbaTint = new Vec4f(1f, 1f, 1f, 1f);
                prog.RgbaLightIn = new Vec4f(1f, 1f, 1f, 1f); // full-bright-ish; see the guide.fsh note
                prog.NormalShaded = 0;
                prog.ExtraGodray = 0f;
                prog.AddRenderFlags = 0;
                prog.Tex2D = _whiteTex.TextureId;             // real white texture — see _whiteTex remarks
                prog.ViewMatrix = rpi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
                activeProg = prog;
            }

            if (useCustom) _guideShader.Uniform("layoutFrameStrengthIn", _voxelFrameStrength);

            foreach (KeyValuePair<Guid, GuideMesh> entry in _guideMeshes)
            {
                GuideMesh gm = entry.Value;
                MeshRef primary = gm.GrabRef ?? gm.TransitionRef ?? gm.Ref;
                Vec3d origin = gm.GrabRef != null ? gm.GrabOrigin
                    : gm.TransitionRef != null ? gm.TransitionOrigin : gm.Origin;
                if (primary == null || origin == null) continue;
                stats.PlacedGuides++;

                // F6 free-move: this guide is being dragged, so shift where it draws. Both the cull test
                // and the voxel-frame grid follow the shift, or a guide moved to the screen edge would be
                // culled against where it used to be and its cell grid would slide across its own faces.
                bool shifted = _moveOffsetGuide != Guid.Empty && _moveOffsetGuide == entry.Key;
                Vec3d drawOrigin = shifted
                    ? new Vec3d(origin.X + _moveOffsetX, origin.Y + _moveOffsetY, origin.Z + _moveOffsetZ)
                    : origin;
                Vec3d cullCenter = shifted && gm.CullCenter != null
                    ? new Vec3d(gm.CullCenter.X + _moveOffsetX, gm.CullCenter.Y + _moveOffsetY,
                                gm.CullCenter.Z + _moveOffsetZ)
                    : gm.CullCenter;

                VisibilityResult visibility = ClassifyVisibility(
                    camPos, cullCenter, gm.CullRadius, viewDistance, frustumCuller);
                if (visibility == VisibilityResult.BeyondViewDistance)
                {
                    stats.DistanceCulledGuides++;
                    continue;
                }
                if (visibility == VisibilityResult.OutsideFrustum)
                {
                    stats.FrustumCulledGuides++;
                    continue;
                }
                stats.VisiblePlacedGuides++;
                SetModelMatrix(
                    drawOrigin.X - camPos.X, drawOrigin.Y - camPos.Y, drawOrigin.Z - camPos.Z);
                ApplyModelMatrix(useCustom, prog);
                ApplyVoxelFrame(useCustom, GuideVoxelScale(entry.Key), drawOrigin);
                RenderMeshTracked(rpi, primary, stats);
                List<MeshRef> auxiliary = gm.GrabRef != null ? gm.GrabAuxiliary
                    : gm.TransitionRef != null ? gm.TransitionAuxiliary : gm.Auxiliary;
                for (int i = 0; i < auxiliary.Count; i++)
                    RenderMeshTracked(rpi, auxiliary[i], stats);
            }

            // The draft ghost, its cursor precision bands, and its materialization batches all share the
            // draft origin. Group scale 0 means each mesh uses the scale it recorded at upload — which is
            // required here, because the precision bands are deliberately built at DIFFERENT scales from
            // the ghost around them, and a single group scale would draw the wrong grid on them.
            ApplyVoxelFrame(useCustom, 0, _draftPreviewOrigin);

            bool draftVisible = VisibleToPlayer(
                camPos, _draftCullCenter, _draftCullRadius, viewDistance, frustumCuller);
            if (_draftPreviewMesh != null && draftVisible)
            {
                SetModelMatrix(
                    _draftPreviewOrigin.X - camPos.X,
                    _draftPreviewOrigin.Y - camPos.Y,
                _draftPreviewOrigin.Z - camPos.Z);
                ApplyModelMatrix(useCustom, prog);
                RenderMeshTracked(rpi, _draftPreviewMesh, stats);
                stats.VisibleDraftBatches++;
            }

            for (int i = 0; draftVisible && i < _draftPrecisionMeshes.Count; i++)
            {
                SetModelMatrix(
                    _draftPreviewOrigin.X - camPos.X,
                    _draftPreviewOrigin.Y - camPos.Y,
                _draftPreviewOrigin.Z - camPos.Z);
                ApplyModelMatrix(useCustom, prog);
                RenderMeshTracked(rpi, _draftPrecisionMeshes[i], stats);
                stats.VisibleDraftBatches++;
            }

            for (int i = 0; draftVisible && i < _draftMaterializationMeshes.Count; i++)
            {
                SetModelMatrix(
                    _draftPreviewOrigin.X - camPos.X,
                    _draftPreviewOrigin.Y - camPos.Y,
                _draftPreviewOrigin.Z - camPos.Z);
                ApplyModelMatrix(useCustom, prog);
                RenderMeshTracked(rpi, _draftMaterializationMeshes[i], stats);
                stats.VisibleDraftBatches++;
            }

            PendingPlacementVisual pendingPlacement = _pendingPlacementVisual;
            if (pendingPlacement != null && pendingPlacement.Origin != null
                && VisibleToPlayer(
                    camPos, pendingPlacement.CullCenter ?? pendingPlacement.Origin,
                    pendingPlacement.CullRadius, viewDistance, frustumCuller))
            {
                SetModelMatrix(
                    pendingPlacement.Origin.X - camPos.X,
                    pendingPlacement.Origin.Y - camPos.Y,
                    pendingPlacement.Origin.Z - camPos.Z);
                ApplyModelMatrix(useCustom, prog);
                // The handoff visual between the final click and the guide being adopted. Without this the
                // frame would vanish for the duration of a local placement's materialization, then pop in.
                ApplyVoxelFrame(useCustom, 0, pendingPlacement.Origin);
                for (int i = 0; i < pendingPlacement.Meshes.Count; i++)
                {
                    if (pendingPlacement.Meshes[i] == null) continue;
                    RenderMeshTracked(rpi, pendingPlacement.Meshes[i], stats);
                    stats.VisiblePendingBatches++;
                }
                for (int i = 0; i < pendingPlacement.ProvisionalMeshes.Count; i++)
                {
                    if (pendingPlacement.ProvisionalMeshes[i] == null) continue;
                    RenderMeshTracked(rpi, pendingPlacement.ProvisionalMeshes[i], stats);
                    stats.VisiblePendingBatches++;
                }
            }

            if (_markerMesh != null)
            {
                // Remote draft anchors are 0.25-block cubes, not voxel geometry - a cell grid on them
                // would be meaningless. Null origin switches the frame off for the rest of the pass.
                ApplyVoxelFrame(useCustom, 0, null);
                foreach (Vec3d anchor in _remoteAnchors.Values)
                {
                    if (!VisibleToPlayer(
                        camPos, anchor, DraftMarkerCullRadius, viewDistance, frustumCuller)) continue;
                    SetModelMatrix(
                        anchor.X - camPos.X - DraftMarkerHalf,
                        anchor.Y - camPos.Y - DraftMarkerHalf,
                    anchor.Z - camPos.Z - DraftMarkerHalf);
                    ApplyModelMatrix(useCustom, prog);
                    RenderMeshTracked(rpi, _markerMesh, stats);
                    stats.VisibleRemoteMarkers++;
                }
            }

            activeProg.Stop();
            rpi.GlEnableCullFace();
            rpi.GlToggleBlend(false);
            _lastRenderStats = stats;
        }

        /// <summary>
        /// Pushes <see cref="_modelMat"/> to whichever program is active. The standard program exposes a
        /// typed property; the custom one takes a named uniform.
        /// </summary>
        private void ApplyModelMatrix(bool useCustom, IStandardShaderProgram standardProg)
        {
            if (useCustom) _guideShader.UniformMatrix("modelMatrix", _modelMat);
            else standardProg.ModelMatrix = _modelMat;
        }

        /// <summary>
        /// Sets the voxel-frame grid for the mesh about to be drawn. <paramref name="voxelScale"/> of 0
        /// disables the frame, which is what non-voxel geometry (remote draft markers) passes.
        /// </summary>
        /// <remarks>
        /// The grid must line up with the WORLD voxel lattice, but mesh vertices are relative to an
        /// arbitrary per-guide origin that is not itself on a voxel boundary. Passing the origin whole would
        /// destroy float precision at world scale, so only its remainder within one cell is sent: the
        /// discarded whole cells are an integer offset, which <c>fract()</c> in the shader ignores.
        ///
        /// Looked up per draw rather than cached on GuideMesh deliberately — the scale lives on GuideData,
        /// and every mesh path (rebuild, materialization, transition, grab) would otherwise need to
        /// remember to copy it. One dictionary probe for a handful of visible guides is not worth that risk.
        /// </remarks>
        private bool _frameUseCustom;
        private Vec3d _frameOrigin;
        private int _frameGroupScale;
        private int _frameAppliedScale = -1;

        /// <summary>
        /// Begins a group of meshes sharing one origin. <paramref name="groupScale"/> is the fallback for
        /// meshes that did not record their own scale at upload; pass 0 to let each mesh decide.
        /// </summary>
        private void ApplyVoxelFrame(bool useCustom, int groupScale, Vec3d origin)
        {
            _frameUseCustom = useCustom;
            _frameOrigin = origin;
            _frameGroupScale = groupScale;
            _frameAppliedScale = -1;   // force the next mesh to push uniforms
        }

        /// <summary>
        /// Pushes the frame uniforms for one mesh, preferring the scale recorded when it was uploaded and
        /// falling back to the group's. Redundant updates are skipped: a large guide draws ~128 batches at
        /// one scale, and re-sending identical uniforms 128 times a frame is pure waste.
        /// </summary>
        private void ApplyMeshVoxelFrame(MeshRef mesh, MeshCost cost)
        {
            if (!_frameUseCustom) return;

            int scale = cost.VoxelScale > 0 ? cost.VoxelScale : _frameGroupScale;
            if (_frameOrigin == null || _voxelFrameStrength <= 0f) scale = 0;
            if (scale == _frameAppliedScale) return;
            _frameAppliedScale = scale;

            if (scale <= 0)
            {
                _guideShader.Uniform("layoutVoxelSizeIn", 0f);
                return;
            }

            double cell = scale / 16.0;
            _guideShader.Uniform("layoutVoxelSizeIn", (float)cell);
            _guideShader.Uniform("layoutGridOffsetIn", new Vec3f(
                (float)(_frameOrigin.X - Math.Floor(_frameOrigin.X / cell) * cell),
                (float)(_frameOrigin.Y - Math.Floor(_frameOrigin.Y / cell) * cell),
                (float)(_frameOrigin.Z - Math.Floor(_frameOrigin.Z / cell) * cell)));
        }

        private int GuideVoxelScale(Guid id) =>
            _network.Guides.TryGetValue(id, out GuideData guide) && guide != null ? guide.VoxelScale : 0;

        private void SetModelMatrix(double dx, double dy, double dz)
        {
            // Keep guide geometry registered to Vintage Story's exact 1/16 lattice. A former 0.003-block
            // whole-mesh pull toward the camera caused the guide cells to visibly miss micro-block edges.
            // Z-fight clearance belongs on exposed faces in GuideMeshBuilder, never in this transform.
            Mat4f.Identity(_modelMat);
            Mat4f.Translate(_modelMat, _modelMat, (float)dx, (float)dy, (float)dz);
        }

        private int ConfiguredViewDistance()
        {
            try
            {
                return Math.Max(1, _capi.Settings.Int["viewDistance"]);
            }
            catch
            {
                return 128;
            }
        }

        private static bool WithinViewDistance(
            Vec3d camera, Vec3d centre, double radius, double viewDistance)
        {
            if (camera == null || centre == null) return true;
            double limit = Math.Max(1.0, viewDistance) + Math.Max(0.0, radius);
            double dx = centre.X - camera.X;
            double dy = centre.Y - camera.Y;
            double dz = centre.Z - camera.Z;
            return dx * dx + dy * dy + dz * dz <= limit * limit;
        }

        private static bool VisibleToPlayer(
            Vec3d camera, Vec3d centre, double radius, double viewDistance,
            FrustumCulling frustumCuller)
        {
            return ClassifyVisibility(
                camera, centre, radius, viewDistance, frustumCuller) == VisibilityResult.Visible;
        }

        private static VisibilityResult ClassifyVisibility(
            Vec3d camera, Vec3d centre, double radius, double viewDistance,
            FrustumCulling frustumCuller)
        {
            // Missing bounds deliberately fail open. Transitional meshes must not disappear merely because
            // their culling metadata has not reached the main/render thread yet.
            if (centre == null) return VisibilityResult.Visible;
            if (!WithinViewDistance(camera, centre, radius, viewDistance))
                return VisibilityResult.BeyondViewDistance;
            if (frustumCuller == null) return VisibilityResult.Visible;

            return frustumCuller.SphereInFrustum(
                centre.X, centre.Y, centre.Z,
                Math.Max(0.0, radius) + FrustumCullPadding)
                ? VisibilityResult.Visible
                : VisibilityResult.OutsideFrustum;
        }

        private void RenderMeshTracked(IRenderAPI render, MeshRef mesh, RenderFrameStats stats)
        {
            if (mesh == null) return;
            _meshCosts.TryGetValue(mesh, out MeshCost cost);
            ApplyMeshVoxelFrame(mesh, cost);
            render.RenderMesh(mesh);
            stats.MeshDrawCalls++;
            {
                stats.SubmittedTriangles += cost.Triangles;
                stats.SubmittedVertices += cost.Vertices;
                stats.SubmittedIndices += cost.Indices;
                stats.SubmittedBytes += cost.Bytes;
            }
        }

        // -- Network events (main thread) ----------------------------------------------------------

        private void OnGuideAddedOrUpdated(GuideData guide)
        {
            if (guide == null) return;

            if (TryAdoptPendingPlacementVisual(guide)) return;
            if (_pendingPlacementVisual != null
                && _pendingPlacementVisual.AdoptedGuideId == guide.Id)
            {
                // A later mutation outran the tail of the original placement upload. The ordinary rebuild
                // below becomes authoritative; prevent any stale remaining placement batches joining it.
                DiscardPendingPlacementVisual();
            }

            // Releasing an immense sculpt keeps the cheap working visual and its off-thread refinement alive.
            // The authority echo confirms (or corrects) that pose without ever entering the ordinary
            // synchronous full-shell rebuild below.
            if (_releasedGrabPending && guide.Id == _grabbedGuide)
            {
                // A guide can cross below the immense threshold during the released sculpt. The release path
                // was selected from the pre-authority count, but the immense refinement scheduler correctly
                // refuses the now-small result. Finish it through the safe immediate path instead of leaving
                // _releasedGrabPending latched forever.
                if (guide.CachedVoxelCount <= PreviewFullResVoxelCap)
                {
                    CompleteReleasedGrabAsOrdinary(guide);
                    return;
                }

                ulong authorityFingerprint = RenderFingerprint(guide);
                if (authorityFingerprint != _releasedGrabFingerprint)
                {
                    RebuildGuide(guide); // internally retained grab => bounded wireframe only
                    _releasedGrabFingerprint = _grabPoseFingerprint;
                    _releasedGrabVisualComplete = false;
                }
                _releasedGrabAuthorityConfirmed = true;
                TryFinalizeReleasedGrab();
                return;
            }

            // A cancelled grab already restored the original view by revealing the retained settled mesh.
            // The authority's full-state echo confirms that same geometry; rebuilding it would synchronously
            // voxelise the behemoth again for no visual change.
            if (guide.Id == _cancelRestoreGuide)
            {
                ulong fingerprint = RenderFingerprint(guide);
                _cancelRestoreGuide = Guid.Empty;
                if (fingerprint == _cancelRestoreFingerprint) return;
            }

            // BEING TRANSFORMED: go straight to the rebuild, which is where the move hold's scaffold and
            // its true-scale floor band are raised. TryStartSettledShellMaterialization would now decline
            // this anyway — the guide has changed, so its scaffold fingerprint no longer matches — but
            // saying so here keeps the intent explicit rather than resting on that.
            if (MoveHoldActive(guide.Id)) { RebuildGuide(guide); return; }

            if (TryStartSettledShellMaterialization(guide)) return;
            RebuildGuide(guide);
        }

        private void OnGuideRemoved(Guid id)
        {
            // A dispelled guide is not "stale", it is gone. Without this its id stayed in the
            // permanently-stale set for the rest of the session and OccupancyStaleGuides kept counting it,
            // because the tick's own cleanup only ever looks at ids still in the DIRTY set.
            _occupancyPermanentlyStale.Remove(id);
            _occupancyDirty.Remove(id);

            CancelSettledMaterialization(id);
            if (_pendingPlacementVisual != null
                && _pendingPlacementVisual.AdoptedGuideId == id)
                DiscardPendingPlacementVisual();
            if (_activeGrabBuild?.GuideId == id)
            {
                _activeGrabBuild.Cancellation?.Cancel();
                _activeGrabBuild = null;
                _grabMaterializationStarted = false;
            }
            if (_releasedGrabPending && id == _grabbedGuide)
            {
                _grabbedGuide = Guid.Empty;
                _grabbedIndex = -1;
                _grabExtent = GuideExtent.Empty;
                ResetGrabRefinementState();
            }
            RemoveGuideMesh(id);
        }

        private void OnGuidesBulkSynced()
        {
            CancelAllSettledMaterializations();
            DiscardPendingPlacementVisual();
            RebuildAll();
        }

        private void OnPlacementRejected() => DiscardPendingPlacementVisual();

        private void OnRemoteDraftAnchorChanged(string playerUid, Vec3d start)
        {
            if (!string.IsNullOrEmpty(playerUid) && start != null)
                _remoteAnchors[playerUid] = new Vec3d(start.X, start.Y, start.Z); // copy — do not alias the mirror
        }

        private void OnRemoteDraftAnchorRemoved(string playerUid)
        {
            if (!string.IsNullOrEmpty(playerUid)) _remoteAnchors.Remove(playerUid);
        }

        // -- Grabbed-point API (driven by the held tool, Module 7) ---------------------------------

        /// <summary>
        /// Marks a control point as actively dragged so its voxels render White. Rebuilds the affected guide
        /// (and any previously-grabbed one) so the highlight moves cleanly between points.
        /// </summary>
        // ---- F6 free-move preview (v0.3.86) ------------------------------------------------------
        //
        // A whole-guide translation is a pure change of WHERE the mesh is drawn, so the preview is a change
        // to the per-mesh model-matrix translation and nothing else. No re-meshing, no re-upload, no
        // regrouping of primitives — an eight-million-voxel guide previews its move exactly as cheaply as a
        // small one, and the drawn primitive order is untouched (the standing renderer constraint).
        private Guid _moveOffsetGuide = Guid.Empty;
        private double _moveOffsetX, _moveOffsetY, _moveOffsetZ;

        /// <summary>Draws one guide shifted by a world offset until cleared. Purely visual — no mesh changes.</summary>
        public void SetMoveOffset(Guid guideId, double dx, double dy, double dz)
        {
            _moveOffsetGuide = guideId;
            _moveOffsetX = dx;
            _moveOffsetY = dy;
            _moveOffsetZ = dz;
        }

        /// <summary>Drops any free-move preview offset, so guides draw where their data says they are.</summary>
        public void ClearMoveOffset()
        {
            _moveOffsetGuide = Guid.Empty;
            _moveOffsetX = _moveOffsetY = _moveOffsetZ = 0;
        }

        // ---- Move materialization hold (v0.3.86) -------------------------------------------------
        //
        // A moved immense guide drops to its cheap wireframe scaffold and then immediately starts streaming
        // its exact shell back in. That is right for a settling guide and wrong for a moving one: the player
        // is usually mid-adjustment, and each further nudge would throw the part-built shell away and start
        // over. Nudging therefore parks the shell for a beat; the hold restarts on every nudge, so the
        // rebuild only happens once the player has actually stopped.
        private const long MoveMaterializationHoldMs = 2500;

        // Total height of the graduated precision band along the bottom of a held wireframe, in blocks.
        // The true scale occupies its lowest part and the transition up to the scaffold's own coarse scale
        // fills the rest — see BuildFloorPrecisionLayers.
        private const double MoveWireframeFloorBandBlocks = 1.0;

        private readonly Dictionary<Guid, long> _moveHolds = new Dictionary<Guid, long>();

        /// <summary>
        /// Parks a moved guide on its wireframe for <see cref="MoveMaterializationHoldMs"/>, restarting the
        /// clock if it is already held. Any shell already streaming for the old position is abandoned — it
        /// describes where the guide used to be.
        /// </summary>
        public void HoldMoveMaterialization(Guid guideId)
        {
            // Only guides that actually stream are worth holding. A small guide rebuilds synchronously in
            // well under a frame and never shows a wireframe at all, so holding one would buy nothing and
            // cost an extra rebuild when the hold expired.
            if (!_network.Guides.TryGetValue(guideId, out GuideData guide)
                || !ShouldStreamSettledShell(guide)) return;

            _moveHolds[guideId] = (_capi.World?.ElapsedMilliseconds ?? 0) + MoveMaterializationHoldMs;
            CancelSettledMaterialization(guideId);
        }

        private bool MoveHoldActive(Guid guideId) =>
            _moveHolds.TryGetValue(guideId, out long until)
            && (_capi.World?.ElapsedMilliseconds ?? 0) < until;

        // Releases guides whose hold has run out: the shell they were holding off now streams in normally.
        private void AdvanceMoveHolds()
        {
            if (_moveHolds.Count == 0) return;
            long now = _capi.World?.ElapsedMilliseconds ?? 0;

            List<Guid> expired = null;
            foreach (KeyValuePair<Guid, long> hold in _moveHolds)
                if (now >= hold.Value) (expired ??= new List<Guid>()).Add(hold.Key);
            if (expired == null) return;

            for (int i = 0; i < expired.Count; i++)
            {
                Guid id = expired[i];
                _moveHolds.Remove(id);
                if (!_network.Guides.TryGetValue(id, out GuideData guide) || guide == null) continue;
                // Rebuild rather than only starting the stream: this also drops the precise floor band the
                // held scaffold carried, and covers a guide that stopped qualifying for streaming.
                RebuildGuide(guide);
            }
        }

        public void SetGrabbedPoint(Guid guideId, int controlPointIndex)
        {
            Guid previous = _grabbedGuide;
            if (previous != guideId)
            {
                ResetGrabRefinementState();
                _grabAdaptiveScale = 0;
                _grabHealthyUpdates = 0;
                _grabBaselineFrameMilliseconds = Math.Max(1.0, _smoothedFrameMilliseconds);
            }
            _grabbedGuide = guideId;
            _grabbedIndex = controlPointIndex;

            if (previous != Guid.Empty && previous != guideId) RebuildGuideById(previous);
            RebuildGuideById(guideId);
        }

        /// <summary>Clears any grabbed-point highlight and rebuilds the guide that had it.</summary>
        public void ClearGrabbedPoint()
        {
            if (_grabbedGuide == Guid.Empty) return;
            Guid was = _grabbedGuide;

            if (_network.Guides.TryGetValue(was, out GuideData immense)
                && immense != null && immense.CachedVoxelCount > PreviewFullResVoxelCap)
            {
                // Do not clear the renderer's internal guide identity yet. That identity lets the existing
                // below-normal refinement lane finish the released pose while the controller and server lock
                // are already free. Most importantly, never call RebuildGuide here: that old path voxelised
                // and uploaded the complete immense shell synchronously on the release click.
                _grabbedIndex = -1;
                _grabAdaptiveScale = 0;
                _grabHealthyUpdates = 0;
                _grabExtent = GuideExtent.Empty;
                _activeGrabBuild?.Cancellation?.Cancel();
                _activeGrabBuild = null;
                _grabGeneration++;
                _grabRefinedFingerprint = 0;
                _grabLastMotionMs = (_capi.World?.ElapsedMilliseconds ?? 0) - GrabSettleDelayMs;
                _grabMaterializationStarted = false;
                _grabMaterializationGuide = Guid.Empty;
                _lastGrabMaterializationUploadMs = 0;
                _releasedGrabPending = true;
                _releasedGrabAuthorityConfirmed = !_network.ServerLayoutAvailable;
                _releasedGrabVisualComplete = false;
                _releasedGrabFingerprint = _grabPoseFingerprint;
                return;
            }

            _grabbedGuide = Guid.Empty;
            _grabbedIndex = -1;
            _grabAdaptiveScale = 0;
            _grabHealthyUpdates = 0;
            _grabExtent = GuideExtent.Empty;
            ResetGrabRefinementState();
            RebuildGuideById(was);
            DeleteGrabMeshes(was);
        }

        /// <summary>
        /// Ends a cancelled grab. A large guide's wireframe is a separate working asset, so cancellation
        /// can discard it and reveal the untouched settled mesh. Small guides keep the inexpensive rebuild
        /// path because their live full-shell preview still replaces the settled mesh in place.
        /// </summary>
        public void CancelGrabbedPoint()
        {
            if (_grabbedGuide == Guid.Empty) return;
            Guid was = _grabbedGuide;
            _grabbedGuide = Guid.Empty;
            _grabbedIndex = -1;
            _grabAdaptiveScale = 0;
            _grabHealthyUpdates = 0;
            _grabExtent = GuideExtent.Empty;
            ResetGrabRefinementState();

            bool canRestoreRetained = _guideMeshes.TryGetValue(was, out GuideMesh mesh)
                && mesh.Ref != null && mesh.GrabRef != null;
            if (canRestoreRetained)
            {
                if (_network.Guides.TryGetValue(was, out GuideData restored) && restored != null)
                {
                    _cancelRestoreGuide = was;
                    _cancelRestoreFingerprint = RenderFingerprint(restored);
                }
                DeleteGrabMeshes(mesh);
                return;
            }

            RebuildGuideById(was);
        }

        // -- Local draft preview (driven by the held tool, Module 7) --------------------------------

        private sealed class DraftBuildResult
        {
            public DraftPreviewSpec Spec;
            public int RenderScale;
            public int PreviewChunkTarget;
            public int VoxelCount;
            public GuideExtent Extent;
            public long WorkMilliseconds;
            public IGuideShape Shape;
            public List<VoxelPosition> Voxels;
            public BlockingCollection<MeshData> ScaffoldReadyMeshes;
            public BlockingCollection<MeshData> ReadyMeshes;
            public BlockingCollection<MeshData> CleanReadyMeshes;
            public CancellationTokenSource Cancellation;
            public Vec3d Origin;
            public Exception Error;
            public bool IsMaterialization;
            public volatile bool OriginReady;
            public volatile bool MetadataReady;
            public volatile bool Completed;
            public bool CompletionHandled;
        }

        private sealed class GrabBuildResult
        {
            public int Generation;
            public ulong Fingerprint;
            public Guid GuideId;
            public int GrabbedIndex;
            public GuideData Guide;
            public IGuideShape Shape;
            public List<VoxelPosition> Voxels;
            public int VoxelCount;
            public BlockingCollection<MeshData> ReadyMeshes;
            public BlockingCollection<MeshData> CleanReadyMeshes;
            public CancellationTokenSource Cancellation;
            public GuideExtent Extent;
            public Vec3d Origin;
            public Exception Error;
            public bool IsMaterialization;
            public volatile bool MetadataReady;
            public volatile bool Completed;
            public bool CompletionHandled;
        }

        /// <summary>
        /// Invalidates older refinement results without removing the currently visible ghost. The moving
        /// wireframe replaces it immediately and cancellation stops obsolete meshing/queued production so
        /// an earlier immense pose cannot keep consuming client CPU behind the new one.
        /// </summary>
        public void BeginDraftGeneration(int generation)
        {
            if (_activeDraftGeneration != generation)
                _draftVisualFingerprint = 0;
            DraftBuildResult obsolete = _activeDraftBuild;
            if (obsolete != null && obsolete.Spec?.Generation != generation)
            {
                obsolete.Cancellation?.Cancel();
                _activeDraftBuild = null;
            }
            _activeDraftGeneration = generation;
            _hasPreviewKey = false;
            _draftMaterializationStarted = false;
            ClearDraftMaterialization();
        }

        /// <summary>
        /// Shows one moving pose. Cheap guides retain the exact selected-scale shell and HUD measurement.
        /// Expensive guides use a bounded wireframe at the least-coarse scale that fits the moving budgets.
        /// The estimator only samples the already-cheap parametric curve; it never scans a volume.
        /// </summary>
        public MovingDraftPreviewResult ShowMovingDraft(DraftPreviewSpec spec, int adaptiveMinimumScale = 0)
        {
            if (_disposed || spec == null) return null;
            _activeDraftGeneration = spec.Generation;
            var timer = Stopwatch.StartNew();

            try
            {
                IGuideShape shape = spec.CreateShape();
                List<Vec3d> curve = shape.SampleCurve(128);
                int selectedScale = spec.Settings.Scale;
                double selectedEstimate = EstimateMovingShellWork(shape, curve, spec, selectedScale);
                bool useFullShell = adaptiveMinimumScale <= selectedScale
                    && selectedEstimate <= PreviewFullResVoxelCap;
                _draftPreviewWasWireframe = !useFullShell;

                if (useFullShell)
                {
                    List<VoxelPosition> shell = spec.Settings.Wireframe
                        ? ShapeWireframe.GetVoxelPositions(shape, selectedScale)
                        : shape.GetVoxelPositions(selectedScale, spec.Settings.Filled);
                    if (spec.Settings.Divisions > 1)
                        DivisionMarks.Apply(shell, curve, spec.Settings.Divisions, selectedScale);
                    GuideExtent extent =
                        GuideMeshBuilder.MeasureShapeExtent(shape, selectedScale);
                    UploadPreviewVoxels(shape, shell, spec.Settings, selectedScale);
                    _draftVisualFingerprint = spec.Fingerprint();
                    timer.Stop();
                    return new MovingDraftPreviewResult(
                        selectedScale, true, shell.Count, extent, timer.ElapsedMilliseconds);
                }

                int movingScale = ChooseMovingWireframeScale(
                    curve, Math.Max(selectedScale, adaptiveMinimumScale));
                List<VoxelPosition> voxels = BuildWireframe(shape, curve, movingScale);

                for (int i = 0; i < shape.ControlPoints.Count; i++)
                {
                    ControlPoint cp = shape.ControlPoints[i];
                    if (cp?.WorldPosition == null || cp.IsPhantom) continue;
                    VoxelRenderType type = cp.IsLocked ? VoxelRenderType.Locked
                        : cp.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                    ShapeGeometry.ClaimMarker(voxels, movingScale, cp.WorldPosition, type);
                }

                if (movingScale > selectedScale && spec.ActiveAim != null)
                {
                    Vec3d aim = spec.ActiveAim;
                    double half = movingScale / 32.0;
                    double r2 = PrecisionTransitionRadius * PrecisionTransitionRadius;
                    voxels.RemoveAll(v =>
                    {
                        double dx = (v.X / 16.0 + half) - aim.X;
                        double dy = (v.Y / 16.0 + half) - aim.Y;
                        double dz = (v.Z / 16.0 + half) - aim.Z;
                        return dx * dx + dy * dy + dz * dz <= r2;
                    });
                }

                UploadPreviewVoxels(shape, voxels, spec.Settings, movingScale);
                UploadPrecisionLayers(shape, curve, spec, movingScale);
                _draftVisualFingerprint = spec.Fingerprint();
                timer.Stop();
                return new MovingDraftPreviewResult(
                    movingScale, false, voxels.Count, GuideExtent.Empty, timer.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                _capi.Logger.Warning("[Layout] Moving draft preview failed: {0}", e.Message);
                return null;
            }
        }

        private static int ChooseMovingWireframeScale(IReadOnlyList<Vec3d> curve, int minimumScale)
        {
            double length = 0.0;
            if (curve != null)
            {
                for (int i = 1; i < curve.Count; i++)
                {
                    Vec3d a = curve[i - 1], b = curve[i];
                    if (a == null || b == null) continue;
                    double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
                    length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
            }

            int[] scales = GuideData.ValidVoxelScales;
            int chosen = scales[scales.Length - 1];
            for (int i = 0; i < scales.Length; i++)
            {
                int scale = scales[i];
                if (scale < minimumScale) continue;
                chosen = scale;
                double estimatedWireCells = length * 16.0 / scale + (curve?.Count ?? 0);
                if (estimatedWireCells <= MovingWireframeVoxelTarget) return scale;
            }
            return chosen;
        }

        private static List<VoxelPosition> BuildWireframe(IReadOnlyList<Vec3d> curve, int scale)
        {
            var voxels = new List<VoxelPosition>();
            var seen = new HashSet<(int, int, int)>();
            if (curve == null) return voxels;
            if (curve.Count == 1)
            {
                VoxelMarch.MarchInto(voxels, seen, curve, scale);
                return voxels;
            }
            for (int i = 1; i < curve.Count; i++)
                VoxelMarch.MarchSegmentInto(voxels, seen, curve[i - 1], curve[i], scale);
            return voxels;
        }

        private static List<VoxelPosition> BuildWireframe(
            IGuideShape shape, IReadOnlyList<Vec3d> fallbackCurve, int scale) =>
            shape is RoundoverShape
                ? ShapeWireframe.GetVoxelPositions(shape, scale)
                : BuildWireframe(fallbackCurve, scale);

        private void UploadPrecisionLayers(
            IGuideShape shape, IReadOnlyList<Vec3d> curve, DraftPreviewSpec spec, int movingScale)
        {
            int selectedScale = spec.Settings.Scale;
            Vec3d aim = spec.ActiveAim;
            if (aim == null || movingScale <= selectedScale || curve == null || curve.Count == 0) return;

            var scales = new List<int>();
            for (int i = 0; i < GuideData.ValidVoxelScales.Length; i++)
            {
                int scale = GuideData.ValidVoxelScales[i];
                if (scale >= selectedScale && scale < movingScale) scales.Add(scale);
            }
            if (scales.Count == 0) return;

            double innerRadius = Math.Max(selectedScale * 4.0 / 16.0, selectedScale / 16.0);
            double outerRadius = Math.Max(PrecisionTransitionRadius, innerRadius);
            if (outerRadius <= innerRadius + 1e-6 && scales.Count > 1)
                scales.RemoveRange(1, scales.Count - 1);

            for (int i = 0; i < scales.Count; i++)
            {
                int scale = scales[i];
                double extra = Math.Max(0.0, outerRadius - innerRadius);
                double lower = i == 0 ? 0.0 : innerRadius + extra * i / scales.Count;
                double upper = innerRadius + extra * (i + 1) / scales.Count;
                List<VoxelPosition> band = BuildLocalWireBand(curve, scale, aim, lower, upper);
                if (band.Count == 0) continue;

                if (i == 0)
                    ShapeGeometry.ClaimMarker(band, scale, aim, ActiveAimMarkerType(shape, aim));
                UploadAuxiliaryPreviewVoxels(shape, band, spec.Settings, scale);
            }
        }

        private static List<VoxelPosition> BuildLocalWireBand(
            IReadOnlyList<Vec3d> curve, int scale, Vec3d aim, double lowerRadius, double upperRadius)
        {
            var marched = new List<VoxelPosition>();
            var seen = new HashSet<(int, int, int)>();
            double guard = upperRadius + scale * 0.125;
            double guard2 = guard * guard;

            if (curve.Count == 1)
            {
                if (DistanceSquared(curve[0], aim) <= guard2)
                    VoxelMarch.MarchInto(marched, seen, curve, scale);
            }
            else
            {
                for (int i = 1; i < curve.Count; i++)
                {
                    Vec3d a = curve[i - 1], b = curve[i];
                    if (a == null || b == null || PointSegmentDistanceSquared(aim, a, b) > guard2) continue;
                    VoxelMarch.MarchSegmentInto(marched, seen, a, b, scale);
                }
            }

            double half = scale / 32.0;
            double overlap = scale / 32.0;
            double low2 = Math.Max(0.0, lowerRadius - overlap);
            low2 *= low2;
            double high2 = upperRadius + overlap;
            high2 *= high2;
            marched.RemoveAll(v =>
            {
                double dx = v.X / 16.0 + half - aim.X;
                double dy = v.Y / 16.0 + half - aim.Y;
                double dz = v.Z / 16.0 + half - aim.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                return d2 < low2 || d2 > high2;
            });
            return marched;
        }

        private static double PointSegmentDistanceSquared(Vec3d p, Vec3d a, Vec3d b)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            double length2 = abx * abx + aby * aby + abz * abz;
            if (length2 <= 1e-12) return DistanceSquared(p, a);
            double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby + (p.Z - a.Z) * abz) / length2;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double x = a.X + abx * t, y = a.Y + aby * t, z = a.Z + abz * t;
            double dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static double DistanceSquared(Vec3d a, Vec3d b)
        {
            if (a == null || b == null) return double.MaxValue;
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static VoxelRenderType ActiveAimMarkerType(IGuideShape shape, Vec3d aim)
        {
            ControlPoint nearest = null;
            double best = double.MaxValue;
            for (int i = 0; i < shape.ControlPoints.Count; i++)
            {
                ControlPoint cp = shape.ControlPoints[i];
                if (cp?.WorldPosition == null || cp.IsPhantom) continue;
                double d2 = DistanceSquared(cp.WorldPosition, aim);
                if (d2 < best) { best = d2; nearest = cp; }
            }
            if (nearest?.IsLocked == true) return VoxelRenderType.Locked;
            if (nearest?.IsPrimary == true) return VoxelRenderType.Primary;
            return VoxelRenderType.Anchor;
        }

        private static double EstimateMovingShellWork(
            IGuideShape shape, IReadOnlyList<Vec3d> curve, DraftPreviewSpec spec, int scale)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            double length = 0.0;

            void Include(Vec3d p)
            {
                if (p == null) return;
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
            }

            if (curve != null)
            {
                for (int i = 0; i < curve.Count; i++)
                {
                    Vec3d p = curve[i];
                    Include(p);
                    if (i == 0 || p == null || curve[i - 1] == null) continue;
                    Vec3d previous = curve[i - 1];
                    double dx = p.X - previous.X, dy = p.Y - previous.Y, dz = p.Z - previous.Z;
                    length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
            }
            if (shape?.ControlPoints != null)
                for (int i = 0; i < shape.ControlPoints.Count; i++)
                    Include(shape.ControlPoints[i]?.WorldPosition);

            if (minX == double.MaxValue) return 0.0;
            double cell = Math.Max(1, scale) / 16.0;
            double curveCells = length / cell + (curve?.Count ?? 0);
            if (spec.Settings.Wireframe
                || (!GuideShapeTypes.IsVolume(spec.ShapeType) && !spec.Settings.Filled))
                return curveCells;

            double dxSpan = Math.Max(cell, maxX - minX);
            double dySpan = Math.Max(cell, maxY - minY);
            double dzSpan = Math.Max(cell, maxZ - minZ);
            double projectedAreas = dxSpan * dySpan + dxSpan * dzSpan + dySpan * dzSpan;
            double areaCells = projectedAreas / (cell * cell);

            // A filled planar shape occupies one projected sheet; a volume shell may cover every face of
            // its bounding box. Both deliberately err high so an unexpectedly intricate pose falls back.
            return curveCells + (GuideShapeTypes.IsVolume(spec.ShapeType) ? 2.0 : 1.0) * areaCells;
        }

        /// <summary>
        /// Starts one settled shell refinement. Only one worker is allowed at a time; the controller asks
        /// again on a later tick. Pure volumetric mesh construction stays off-thread. Surface air-side
        /// probing remains on the main thread because it touches the live world accessor.
        /// </summary>
        public bool RequestDraftRefinement(DraftPreviewSpec spec, int renderScale)
        {
            bool privateAnchors = _network.ServerLayoutAvailable
                && _network.AuthorityMode == ClientAuthorityMode.Local;
            DraftBuildResult started = StartDraftBuild(
                spec, renderScale, privateAnchors, assignToActiveDraft: true);
            return started != null;
        }

        private DraftBuildResult StartDraftBuild(
            DraftPreviewSpec spec, int renderScale, bool privateAnchors,
            bool assignToActiveDraft)
        {
            if (_disposed || spec == null || renderScale <= 0) return null;
            lock (_draftWorkGate)
            {
                if (_draftWorkBusy) return null;
                _draftWorkBusy = true;
            }

            var result = new DraftBuildResult
            {
                Spec = spec,
                RenderScale = renderScale,
                PreviewChunkTarget = Math.Max(MaterializationTargetVoxelsPerBatch,
                    ((_network.PerGuideVoxelCap > 0
                        ? Math.Min(_network.PerGuideVoxelCap, GuideManager.HardVoxelCeiling)
                        : 384000) + ProgressiveMaximumPreviewBatches - 1)
                    / ProgressiveMaximumPreviewBatches),
                IsMaterialization = spec.Settings.Mode == ProjectionMode.Volumetric,
                Cancellation = new CancellationTokenSource()
            };
            if (result.IsMaterialization)
            {
                result.ScaffoldReadyMeshes = new BlockingCollection<MeshData>(
                    new ConcurrentQueue<MeshData>(), 1);
                result.ReadyMeshes = new BlockingCollection<MeshData>(
                    new ConcurrentQueue<MeshData>(), MaterializationReadyBatchCapacity);
                result.CleanReadyMeshes = new BlockingCollection<MeshData>(
                    new ConcurrentQueue<MeshData>(), MaterializationReadyBatchCapacity);
            }
            if (assignToActiveDraft)
            {
                _activeDraftBuild = result;
                _draftMaterializationStarted = false;
            }

            Task.Factory.StartNew(
                    () => BuildDraftRefinement(result, privateAnchors),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted && result.Error == null)
                        result.Error = task.Exception?.GetBaseException();

                    try
                    {
                        _capi.Event.EnqueueMainThreadTask(
                            () => FinishDraftRefinement(result), "layout-draft-refinement");
                    }
                    catch
                    {
                        lock (_draftWorkGate) _draftWorkBusy = false;
                    }
                });
            return result;
        }

        private static void BuildDraftRefinement(
            DraftBuildResult result, bool privateAnchors)
        {
            var timer = Stopwatch.StartNew();
            Thread thread = Thread.CurrentThread;
            ThreadPriority originalPriority = ThreadPriority.Normal;
            bool priorityChanged = false;
            try
            {
                try
                {
                    originalPriority = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                }
                catch (Exception) { }

                CancellationToken token = result.Cancellation.Token;
                token.ThrowIfCancellationRequested();
                IGuideShape shape = result.Spec.CreateShape();
                result.Shape = shape;
                result.Origin = ComputeOrigin(shape.ControlPoints);
                result.OriginReady = true;

                List<VoxelPosition> selectedScaleScaffold = null;
                if (result.IsMaterialization)
                {
                    selectedScaleScaffold = ShapeWireframe.GetVoxelPositions(
                        shape, result.RenderScale);
                    EnqueueSelectedScaleScaffold(
                        result, shape, selectedScaleScaffold, privateAnchors, token);
                }
                if (result.ScaffoldReadyMeshes != null
                    && !result.ScaffoldReadyMeshes.IsAddingCompleted)
                    result.ScaffoldReadyMeshes.CompleteAdding();

                List<VoxelPosition> voxels;
                bool progressivelyGenerated = result.IsMaterialization
                    && !result.Spec.Settings.Wireframe
                    && shape is IProgressiveVoxelShape;
                if (progressivelyGenerated)
                {
                    var progressive = (IProgressiveVoxelShape)shape;
                    voxels = progressive.GetVoxelPositionsProgressively(
                        result.RenderScale, result.Spec.Settings.Filled,
                        result.PreviewChunkTarget, token, null);
                }
                else
                {
                    voxels = result.Spec.Settings.Wireframe
                        ? selectedScaleScaffold
                            ?? ShapeWireframe.GetVoxelPositions(shape, result.RenderScale)
                        : shape.GetVoxelPositions(result.RenderScale, result.Spec.Settings.Filled);
                }
                token.ThrowIfCancellationRequested();
                if (result.Spec.Settings.Divisions > 1)
                    DivisionMarks.Apply(voxels, shape.SampleCurve(128),
                        result.Spec.Settings.Divisions, result.RenderScale);

                result.VoxelCount = voxels.Count;
                result.Extent =
                    GuideMeshBuilder.MeasureShapeExtent(shape, result.RenderScale);
                result.MetadataReady = true;

                if (result.IsMaterialization)
                {
                    if (!result.Spec.Settings.Wireframe)
                        ProduceMaterializationMeshes(
                            shape, voxels, result.Spec, result.RenderScale, result.Origin,
                            privateAnchors, result.ReadyMeshes, result.CleanReadyMeshes, token);
                    else
                        ProduceCleanMaterializationMeshes(
                            shape, voxels, result.Spec, result.RenderScale, result.Origin,
                            privateAnchors, result.CleanReadyMeshes, token);
                    if (!result.ReadyMeshes.IsAddingCompleted)
                        result.ReadyMeshes.CompleteAdding();
                    if (!result.CleanReadyMeshes.IsAddingCompleted)
                        result.CleanReadyMeshes.CompleteAdding();
                }
                else
                {
                    result.Voxels = voxels;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                result.Error = e;
            }
            finally
            {
                timer.Stop();
                result.WorkMilliseconds = timer.ElapsedMilliseconds;
                if (result.ScaffoldReadyMeshes != null
                    && !result.ScaffoldReadyMeshes.IsAddingCompleted)
                    result.ScaffoldReadyMeshes.CompleteAdding();
                if (result.ReadyMeshes != null && !result.ReadyMeshes.IsAddingCompleted)
                    result.ReadyMeshes.CompleteAdding();
                if (result.CleanReadyMeshes != null && !result.CleanReadyMeshes.IsAddingCompleted)
                    result.CleanReadyMeshes.CompleteAdding();
                result.Completed = true;
                if (priorityChanged)
                {
                    try { thread.Priority = originalPriority; }
                    catch (Exception) { }
                }
            }
        }

        private static void EnqueueSelectedScaleScaffold(
            DraftBuildResult result, IGuideShape shape, List<VoxelPosition> wireframe,
            bool privateAnchors, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (wireframe == null || wireframe.Count == 0) return;

            var options = new GuideMeshOptions
            {
                Scale = result.RenderScale,
                Mode = ProjectionMode.Volumetric,
                Plane = result.Spec.Settings.Plane,
                Origin = result.Origin,
                Hidden = false,
                GrabbedPoint = null,
                PrivateAnchors = privateAnchors
            };
            AssignAnchors(shape.ControlPoints, options);
            result.ScaffoldReadyMeshes.Add(
                GuideMeshBuilder.Build(wireframe, options), token);
        }

        private static void ProduceMaterializationMeshes(
            IGuideShape shape, List<VoxelPosition> voxels, DraftPreviewSpec spec,
            int renderScale, Vec3d origin, bool privateAnchors,
            BlockingCollection<MeshData> readyMeshes,
            BlockingCollection<MeshData> cleanMeshes, CancellationToken token)
        {
            var occupancy = new HashSet<(int, int, int)>(voxels.Count);
            int minimumY = int.MaxValue;
            for (int i = 0; i < voxels.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                VoxelPosition voxel = voxels[i];
                occupancy.Add((voxel.X, voxel.Y, voxel.Z));
                if (voxel.Y < minimumY) minimumY = voxel.Y;
            }

            var ordered = new List<VoxelPosition>(voxels);
            ordered.Sort((a, b) =>
            {
                int byX = a.X.CompareTo(b.X);
                if (byX != 0) return byX;
                int byY = a.Y.CompareTo(b.Y);
                return byY != 0 ? byY : a.Z.CompareTo(b.Z);
            });
            int cleanCursor = 0;

            OrganicVoxelGrowth.GrowBatches(
                voxels, renderScale, MaterializationTargetVoxelsPerBatch,
                MaterializationMinimumBatches, MaterializationMaximumBatches, token,
                batch =>
            {
                token.ThrowIfCancellationRequested();
                if (batch == null || batch.Count == 0) return;

                // Build and enqueue the corresponding final partition first. The renderer uploads this mesh
                // invisibly alongside the organic preview, so the complete uniform shell is ready at the exact
                // cadence point where the final growth batch arrives.
                int cleanCount = Math.Min(batch.Count, ordered.Count - cleanCursor);
                var cleanBatch = ordered.GetRange(cleanCursor, cleanCount);
                cleanCursor += cleanCount;
                var cleanOptions = new GuideMeshOptions
                {
                    Scale = renderScale,
                    Mode = ProjectionMode.Volumetric,
                    Plane = spec.Settings.Plane,
                    Origin = origin,
                    Hidden = false,
                    GrabbedPoint = null,
                    PrivateAnchors = privateAnchors,
                    Occupancy = occupancy,
                    MinimumVoxelY = minimumY
                };
                AssignAnchors(shape.ControlPoints, cleanOptions);
                cleanMeshes.Add(GuideMeshBuilder.Build(cleanBatch, cleanOptions), token);

                var previewOptions = new GuideMeshOptions
                {
                    Scale = renderScale,
                    Mode = ProjectionMode.Volumetric,
                    Plane = spec.Settings.Plane,
                    Origin = origin,
                    Hidden = false,
                    GrabbedPoint = null,
                    PrivateAnchors = privateAnchors,
                    Occupancy = occupancy
                };
                AssignAnchors(shape.ControlPoints, previewOptions);
                readyMeshes.Add(GuideMeshBuilder.Build(batch, previewOptions), token);
            });
        }

        private static void ProduceCleanMaterializationMeshes(
            IGuideShape shape, List<VoxelPosition> voxels, DraftPreviewSpec spec,
            int renderScale, Vec3d origin, bool privateAnchors,
            BlockingCollection<MeshData> cleanMeshes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (voxels == null || voxels.Count == 0) return;

            var ordered = new List<VoxelPosition>(voxels);
            ordered.Sort((a, b) =>
            {
                int byX = a.X.CompareTo(b.X);
                if (byX != 0) return byX;
                int byY = a.Y.CompareTo(b.Y);
                return byY != 0 ? byY : a.Z.CompareTo(b.Z);
            });

            var occupancy = new HashSet<(int, int, int)>(ordered.Count);
            int minimumY = int.MaxValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                VoxelPosition voxel = ordered[i];
                occupancy.Add((voxel.X, voxel.Y, voxel.Z));
                if (voxel.Y < minimumY) minimumY = voxel.Y;
            }

            int batchCount = Math.Max(MaterializationMinimumBatches,
                (ordered.Count + MaterializationTargetVoxelsPerBatch - 1)
                    / MaterializationTargetVoxelsPerBatch);
            batchCount = Math.Min(MaterializationMaximumBatches, Math.Max(1, batchCount));
            int batchSize = Math.Max(1, (ordered.Count + batchCount - 1) / batchCount);
            for (int start = 0; start < ordered.Count; start += batchSize)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(batchSize, ordered.Count - start);
                var batch = ordered.GetRange(start, count);
                var options = new GuideMeshOptions
                {
                    Scale = renderScale,
                    Mode = ProjectionMode.Volumetric,
                    Plane = spec.Settings.Plane,
                    Origin = origin,
                    Hidden = false,
                    GrabbedPoint = null,
                    PrivateAnchors = privateAnchors,
                    Occupancy = occupancy,
                    MinimumVoxelY = minimumY
                };
                AssignAnchors(shape.ControlPoints, options);
                cleanMeshes.Add(GuideMeshBuilder.Build(batch, options), token);
            }
        }

        private void FinishDraftRefinement(DraftBuildResult result)
        {
            lock (_draftWorkGate) _draftWorkBusy = false;
            if (_disposed || result == null) return;
            PendingPlacementVisual pending = _pendingPlacementVisual;
            bool belongsToPending = pending != null && ReferenceEquals(pending.Build, result);
            bool belongsToActive = ReferenceEquals(_activeDraftBuild, result);

            if (result.Error != null)
            {
                _capi.Logger.Warning("[Layout] Draft refinement failed: {0}", result.Error.Message);
                if (belongsToPending)
                {
                    pending.RefinementFailed = true;
                    pending.RefinementFinished = true;
                }
                if (belongsToActive) _activeDraftBuild = null;
                result.CompletionHandled = true;
                TryStartPendingPlacementRefinement();
                return;
            }
            if (result.Cancellation?.IsCancellationRequested == true)
            {
                if (belongsToActive) _activeDraftBuild = null;
                result.CompletionHandled = true;
                TryStartPendingPlacementRefinement();
                return;
            }

            if (result.IsMaterialization)
            {
                if (belongsToActive && result.Spec.Generation == _activeDraftGeneration)
                    DraftPreviewCompleted?.Invoke(this, new DraftPreviewCompletedEventArgs(
                        result.Spec.Generation, result.RenderScale, result.VoxelCount,
                        result.Extent, result.WorkMilliseconds));
                if (belongsToPending) pending.RefinementFinished = true;
                result.CompletionHandled = true;
                TryStartPendingPlacementRefinement();
                return;
            }

            if (!belongsToActive || result.Spec.Generation != _activeDraftGeneration)
            {
                TryStartPendingPlacementRefinement();
                return;
            }

            // Surface flattening and its five-point air-side vote intentionally remain on the main
            // thread. Large 3D volumetric shells take the materialization branch above.
            List<VoxelPosition> voxels = result.Voxels ?? new List<VoxelPosition>();
            bool isSurface = result.Spec.Settings.Mode == ProjectionMode.Surface;
            int slabSide = 0;
            if (isSurface)
                voxels = FlattenToPlaneLayer(voxels, result.Spec.Settings.Plane,
                    result.RenderScale, out slabSide, out _);
            var options = new GuideMeshOptions
            {
                Scale = result.RenderScale,
                Mode = ProjectionMode.Volumetric,
                Plane = result.Spec.Settings.Plane,
                Origin = result.Origin,
                Hidden = false,
                PlaneInset = isSurface ? SurfacePlaneInset : 0f,
                SurfaceSlabThickness = isSurface ? SurfaceSlabThicknessWorld : 0f,
                SurfaceSlabSide = slabSide,
                GrabbedPoint = null,
                PrivateAnchors = _network.ServerLayoutAvailable
                    && _network.AuthorityMode == ClientAuthorityMode.Local
            };
            AssignAnchors(result.Shape.ControlPoints, options);
            MeshData data = GuideMeshBuilder.Build(voxels, options);

            ReplaceDraftMesh(data, result.Origin, result.RenderScale);
            DraftPreviewCompleted?.Invoke(this, new DraftPreviewCompletedEventArgs(
                result.Spec.Generation, result.RenderScale, result.VoxelCount,
                result.Extent, result.WorkMilliseconds));
            _activeDraftBuild = null;
            TryStartPendingPlacementRefinement();
        }

        private void UploadPreviewVoxels(
            IGuideShape shape, List<VoxelPosition> voxels, GuideRenderSettings settings, int renderScale)
        {
            if (voxels == null || voxels.Count == 0) return;
            bool isSurface = settings.Mode == ProjectionMode.Surface;
            int slabSide = 0;
            if (isSurface)
                voxels = FlattenToPlaneLayer(voxels, settings.Plane, renderScale, out slabSide, out _);

            Vec3d origin = ComputeOrigin(shape.ControlPoints);
            var options = new GuideMeshOptions
            {
                Scale = renderScale,
                Mode = ProjectionMode.Volumetric,
                Plane = settings.Plane,
                Origin = origin,
                Hidden = false,
                PlaneInset = isSurface ? SurfacePlaneInset : 0f,
                SurfaceSlabThickness = isSurface ? SurfaceSlabThicknessWorld : 0f,
                SurfaceSlabSide = slabSide,
                GrabbedPoint = null,
                PrivateAnchors = _network.ServerLayoutAvailable
                    && _network.AuthorityMode == ClientAuthorityMode.Local
            };
            AssignAnchors(shape.ControlPoints, options);
            SetDraftCullBounds(shape, renderScale);
            ReplaceDraftMesh(GuideMeshBuilder.Build(voxels, options), origin, options.Scale);
        }

        private void UploadAuxiliaryPreviewVoxels(
            IGuideShape shape, List<VoxelPosition> voxels, GuideRenderSettings settings, int renderScale)
        {
            if (voxels == null || voxels.Count == 0) return;
            bool isSurface = settings.Mode == ProjectionMode.Surface;
            int slabSide = 0;
            if (isSurface)
                voxels = FlattenToPlaneLayer(voxels, settings.Plane, renderScale, out slabSide, out _);

            Vec3d origin = ComputeOrigin(shape.ControlPoints);
            var options = new GuideMeshOptions
            {
                Scale = renderScale,
                Mode = ProjectionMode.Volumetric,
                Plane = settings.Plane,
                Origin = origin,
                Hidden = false,
                PlaneInset = isSurface ? SurfacePlaneInset : 0f,
                SurfaceSlabThickness = isSurface ? SurfaceSlabThicknessWorld : 0f,
                SurfaceSlabSide = slabSide,
                GrabbedPoint = null,
                PrivateAnchors = _network.ServerLayoutAvailable
                    && _network.AuthorityMode == ClientAuthorityMode.Local
            };
            AssignAnchors(shape.ControlPoints, options);
            _draftPrecisionMeshes.Add(
                UploadTrackedMesh(GuideMeshBuilder.Build(voxels, options), options));
            _draftPreviewOrigin = origin;
        }

        private void ReplaceDraftMesh(MeshData data, Vec3d origin, int voxelScale)
        {
            ClearDraftPrecisionMeshes();
            ClearDraftMaterialization();
            MeshRef replacement = UploadTrackedMesh(data, voxelScale);
            MeshRef previous = _draftPreviewMesh;
            _draftPreviewMesh = replacement;
            _draftPreviewOrigin = origin;
            DeleteTrackedMesh(previous);
        }

        private void StartDraftMaterialization(DraftBuildResult build)
        {
            ClearDraftMaterialization();

            _draftPreviewOrigin = build.Origin;
            _lastDraftMaterializationUploadMs = 0;
            _draftCleanSwapReadyMs = -1;
            _draftMaterializationStarted = true;
            _draftMaterializationFinal = false;
        }

        private void AdvanceDraftMaterialization(bool force = false)
        {
            DraftBuildResult build = _activeDraftBuild;
            if (build == null || !build.IsMaterialization
                || build.Spec?.Generation != _activeDraftGeneration
                || build.Cancellation?.IsCancellationRequested == true)
                return;

            BlockingCollection<MeshData> ready = build.ReadyMeshes;
            BlockingCollection<MeshData> cleanReady = build.CleanReadyMeshes;
            BlockingCollection<MeshData> scaffoldReady = build.ScaffoldReadyMeshes;
            if (ready == null || cleanReady == null || scaffoldReady == null) return;
            if (!_draftMaterializationStarted)
            {
                if (!build.OriginReady || scaffoldReady.Count == 0)
                {
                    if (build.Completed && build.CompletionHandled
                        && scaffoldReady.IsCompleted
                        && ready.IsCompleted && cleanReady.IsCompleted)
                        _activeDraftBuild = null;
                    return;
                }
                StartDraftMaterialization(build);
            }

            long now = _capi.World?.ElapsedMilliseconds ?? 0;
            if (build.Completed && build.CompletionHandled
                && scaffoldReady.IsCompleted && scaffoldReady.Count == 0 && ready.IsCompleted
                && ready.Count == 0 && cleanReady.IsCompleted && cleanReady.Count == 0)
            {
                if (!CleanSwapDelayElapsed(ref _draftCleanSwapReadyMs, now)) return;
                ActivateDraftFinalMaterialization();
                _activeDraftBuild = null;
                _draftMaterializationStarted = false;
                return;
            }

            int interval = MaterializationUploadInterval(build.VoxelCount);
            if (!force && _lastDraftMaterializationUploadMs != 0
                && now - _lastDraftMaterializationUploadMs < interval) return;

            if (scaffoldReady.TryTake(out MeshData scaffoldData))
            {
                MeshRef selectedScaleScaffold = UploadTrackedMesh(scaffoldData, build.Spec?.Settings.Scale ?? 0);
                MeshRef previous = _draftPreviewMesh;
                _draftPreviewMesh = selectedScaleScaffold;
                _draftPreviewOrigin = build.Origin;
                if (previous != null)
                    _retiredMaterializationMeshes.Enqueue(previous);
                RetireMeshList(_draftPrecisionMeshes);
                _lastDraftMaterializationUploadMs = now;
                return;
            }

            // The selected-scale wireframe is a visual stage of its own. Never let shell pieces overtake it.
            if (!scaffoldReady.IsCompleted) return;

            bool uploaded = false;
            if (cleanReady.TryTake(out MeshData cleanData))
            {
                _draftCleanMaterializationMeshes.Add(UploadTrackedMesh(cleanData, build.Spec?.Settings.Scale ?? 0));
                uploaded = true;
            }
            if (ready.TryTake(out MeshData previewData))
            {
                RemoveDraftScaffoldForOrganicGrowth(build);
                _draftMaterializationMeshes.Add(UploadTrackedMesh(previewData, build.Spec?.Settings.Scale ?? 0));
                uploaded = true;
            }
            if (uploaded)
            {
                _lastDraftMaterializationUploadMs = now;
                return;
            }

            if (build.Completed && build.CompletionHandled
                && scaffoldReady.IsCompleted
                && ready.IsCompleted && cleanReady.IsCompleted)
            {
                if (!CleanSwapDelayElapsed(ref _draftCleanSwapReadyMs, now)) return;
                ActivateDraftFinalMaterialization();
                _activeDraftBuild = null;
                _draftMaterializationStarted = false;
            }
        }

        private static bool CleanSwapDelayElapsed(ref long readyMs, long now)
        {
            if (readyMs < 0) readyMs = now;
            return now - readyMs >= MaterializationCleanSwapDelayMs;
        }

        private int MaterializationUploadInterval(int voxelCount)
        {
            double span = Math.Max(1,
                LargeMaterializationIntervalVoxelCount - PreviewFullResVoxelCap);
            double position = Math.Max(0.0, Math.Min(1.0,
                (voxelCount - PreviewFullResVoxelCap) / span));
            int interval = (int)Math.Round(
                SmallMaterializationUploadIntervalMs
                + (LargeMaterializationUploadIntervalMs
                    - SmallMaterializationUploadIntervalMs) * Math.Sqrt(position));
            if (_smoothedFrameMilliseconds > 22.0) interval *= 2;
            if (_smoothedFrameMilliseconds > 32.0) interval *= 2;
            return interval;
        }

        private void RemoveDraftScaffoldForOrganicGrowth(DraftBuildResult build)
        {
            if (build?.Spec?.Settings.Wireframe == true) return;
            if (_draftPreviewMesh != null)
            {
                _retiredMaterializationMeshes.Enqueue(_draftPreviewMesh);
                _draftPreviewMesh = null;
            }
            RetireMeshList(_draftPrecisionMeshes);
        }

        private void ActivateDraftFinalMaterialization()
        {
            if (_draftCleanMaterializationMeshes.Count == 0) return;
            RetireMeshList(_draftMaterializationMeshes);
            if (_draftPreviewMesh != null)
            {
                _retiredMaterializationMeshes.Enqueue(_draftPreviewMesh);
                _draftPreviewMesh = null;
            }
            RetireMeshList(_draftPrecisionMeshes);
            _draftMaterializationMeshes.AddRange(_draftCleanMaterializationMeshes);
            _draftCleanMaterializationMeshes.Clear();
            _draftMaterializationFinal = true;
        }

        private void ClearDraftPrecisionMeshes()
        {
            for (int i = 0; i < _draftPrecisionMeshes.Count; i++)
                DeleteTrackedMesh(_draftPrecisionMeshes[i]);
            _draftPrecisionMeshes.Clear();
        }

        private void ClearDraftMaterialization()
        {
            _lastDraftMaterializationUploadMs = 0;
            _draftCleanSwapReadyMs = -1;
            DeleteMeshList(_draftMaterializationMeshes);
            DeleteMeshList(_draftCleanMaterializationMeshes);
            _draftMaterializationFinal = false;
        }

        /// <summary>
        /// Detaches an immense draft from live aiming and keeps its current scaffold visible while exact
        /// batches continue to arrive. Placement no longer falls back to a synchronous authoritative rebuild
        /// merely because the player clicked before background refinement had finished.
        /// </summary>
        public bool RetainExactDraftForPlacement(DraftPreviewSpec spec)
        {
            if (_disposed || spec == null || spec.Generation != _activeDraftGeneration
                || spec.Settings.Mode != ProjectionMode.Volumetric)
                return false;

            ulong placementFingerprint = spec.Fingerprint();
            DraftBuildResult activeBuild = _activeDraftBuild;
            bool matchingBuild = activeBuild != null && activeBuild.IsMaterialization
                && activeBuild.Spec?.Generation == spec.Generation
                && activeBuild.Spec.Fingerprint() == placementFingerprint;
            bool visualMatches = _draftVisualFingerprint != 0
                && _draftVisualFingerprint == placementFingerprint;

            // The completing click can arrive before the next moving-preview tick. In that case the visible
            // scaffold describes the prior snapped aim, so retaining it would be wrong. Build only the cheap,
            // bounded wireframe for the exact clicked pose now; its selected-scale scaffold and shell still
            // follow through the normal background queues.
            if (!visualMatches)
            {
                if (activeBuild != null && !matchingBuild)
                {
                    activeBuild.Cancellation?.Cancel();
                    _activeDraftBuild = null;
                    activeBuild = null;
                }
                if (!TryPrepareImmediatePlacementScaffold(spec, placementFingerprint))
                    return false;
            }

            bool immense = (visualMatches && _draftPreviewWasWireframe) || !visualMatches
                || matchingBuild || _draftMaterializationMeshes.Count > 0
                || _draftCleanMaterializationMeshes.Count > 0;
            if (!immense) return false;

            if (_pendingPlacementVisual != null) return false;
            ComputeCullBounds(
                spec.CreateShape(), spec.Settings.Scale,
                out Vec3d placementCullCenter, out double placementCullRadius);
            var pending = new PendingPlacementVisual
            {
                Spec = spec,
                Origin = _draftPreviewOrigin == null ? null : new Vec3d(
                    _draftPreviewOrigin.X, _draftPreviewOrigin.Y, _draftPreviewOrigin.Z),
                CullCenter = placementCullCenter,
                CullRadius = placementCullRadius,
                LastUploadMs = _lastDraftMaterializationUploadMs,
                StartedMs = _capi.World?.ElapsedMilliseconds ?? 0,
                PrivateAnchors = _network.ServerLayoutAvailable
                    && _network.AuthorityMode == ClientAuthorityMode.Local,
                Build = matchingBuild ? activeBuild : null,
                RefinementFinished = !matchingBuild && _draftMaterializationFinal,
                MeshesAreFinal = _draftMaterializationFinal,
                CleanSwapReadyMs = _draftCleanSwapReadyMs
            };
            pending.Meshes.AddRange(_draftMaterializationMeshes);
            pending.CleanMeshes.AddRange(_draftCleanMaterializationMeshes);
            if (_draftPreviewMesh != null)
            {
                pending.ProvisionalMeshes.Add(_draftPreviewMesh);
                _draftPreviewMesh = null;
            }
            pending.ProvisionalMeshes.AddRange(_draftPrecisionMeshes);
            _draftPrecisionMeshes.Clear();
            if (pending.MeshesAreFinal) DeletePendingProvisionalMeshes(pending);

            _draftMaterializationMeshes.Clear();
            _draftCleanMaterializationMeshes.Clear();
            _lastDraftMaterializationUploadMs = 0;
            _draftCleanSwapReadyMs = -1;
            _draftMaterializationStarted = false;
            _draftMaterializationFinal = false;
            if (matchingBuild) _activeDraftBuild = null;
            _hasPreviewKey = false;
            _draftVisualFingerprint = 0;
            _pendingPlacementVisual = pending;
            TryStartPendingPlacementRefinement();
            return true;
        }

        private bool TryPrepareImmediatePlacementScaffold(
            DraftPreviewSpec spec, ulong placementFingerprint)
        {
            try
            {
                IGuideShape shape = spec.CreateShape();
                List<Vec3d> curve = shape.SampleCurve(128);
                int selectedScale = spec.Settings.Scale;
                double estimate = EstimateMovingShellWork(
                    shape, curve, spec, selectedScale);
                if (estimate <= PreviewFullResVoxelCap) return false;

                int movingScale = ChooseMovingWireframeScale(curve, selectedScale);
                List<VoxelPosition> voxels = BuildWireframe(shape, curve, movingScale);
                for (int i = 0; i < shape.ControlPoints.Count; i++)
                {
                    ControlPoint point = shape.ControlPoints[i];
                    if (point?.WorldPosition == null || point.IsPhantom) continue;
                    VoxelRenderType type = point.IsLocked ? VoxelRenderType.Locked
                        : point.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                    ShapeGeometry.ClaimMarker(
                        voxels, movingScale, point.WorldPosition, type);
                }

                UploadPreviewVoxels(shape, voxels, spec.Settings, movingScale);
                _draftPreviewWasWireframe = true;
                _draftVisualFingerprint = placementFingerprint;
                return _draftPreviewMesh != null;
            }
            catch (Exception e)
            {
                _capi.Logger.Warning(
                    "[Layout] Immediate placement scaffold failed: {0}", e.Message);
                return false;
            }
        }

        private bool HasUnadoptedPendingPlacementVisual() =>
            _pendingPlacementVisual != null
            && (_pendingPlacementVisual.Meshes.Count > 0
                || _pendingPlacementVisual.CleanMeshes.Count > 0
                || _pendingPlacementVisual.ProvisionalMeshes.Count > 0);

        private void TryStartPendingPlacementRefinement()
        {
            PendingPlacementVisual pending = _pendingPlacementVisual;
            if (pending == null || pending.Build != null || pending.RefinementFinished
                || pending.RefinementFailed) return;

            DraftBuildResult started = StartDraftBuild(
                pending.Spec, pending.Spec.Settings.Scale, pending.PrivateAnchors,
                assignToActiveDraft: false);
            if (started != null) pending.Build = started;
        }

        private void AdvancePendingPlacementMaterialization()
        {
            PendingPlacementVisual pending = _pendingPlacementVisual;
            if (pending == null) return;

            long now = _capi.World?.ElapsedMilliseconds ?? 0;
            if (pending.AdoptedGuideId == Guid.Empty && pending.StartedMs != 0
                && now - pending.StartedMs >= PendingPlacementVisualTimeoutMs)
            {
                DiscardPendingPlacementVisual();
                return;
            }

            TryStartPendingPlacementRefinement();
            DraftBuildResult build = pending.Build;
            if (build == null)
            {
                TryFinishPendingPlacementVisual(pending);
                return;
            }

            if (build.Error != null)
            {
                pending.RefinementFailed = true;
                pending.RefinementFinished = true;
                pending.Build = null;
                if (pending.AdoptedGuideId != Guid.Empty)
                {
                    Guid failedGuide = pending.AdoptedGuideId;
                    DiscardPendingPlacementVisual();
                    RebuildGuideById(failedGuide);
                }
                return;
            }

            BlockingCollection<MeshData> ready = build.ReadyMeshes;
            BlockingCollection<MeshData> cleanReady = build.CleanReadyMeshes;
            BlockingCollection<MeshData> scaffoldReady = build.ScaffoldReadyMeshes;
            if (ready == null || cleanReady == null || scaffoldReady == null) return;

            if (build.Completed && scaffoldReady.IsCompleted && scaffoldReady.Count == 0
                && ready.IsCompleted
                && ready.Count == 0 && cleanReady.IsCompleted && cleanReady.Count == 0)
            {
                if (!CleanSwapDelayElapsed(ref pending.CleanSwapReadyMs, now)) return;
                if (!ActivatePendingFinalMaterialization(pending))
                    pending.RefinementFailed = true;
                pending.RefinementFinished = true;
                pending.Build = null;
                TryFinishPendingPlacementVisual(pending);
                return;
            }

            int interval = MaterializationUploadInterval(build.VoxelCount);
            if (pending.LastUploadMs != 0 && now - pending.LastUploadMs < interval) return;

            if (scaffoldReady.TryTake(out MeshData scaffoldData))
            {
                MeshRef selectedScaleScaffold = UploadTrackedMesh(scaffoldData, build.Spec?.Settings.Scale ?? 0);
                DeletePendingProvisionalMeshes(pending);
                pending.ProvisionalMeshes.Add(selectedScaleScaffold);
                pending.Origin = build.Origin;
                pending.LastUploadMs = now;
                return;
            }

            // Preserve the promised order even if shell generation has already filled its producer queue.
            if (!scaffoldReady.IsCompleted) return;

            bool uploadedPending = false;
            if (cleanReady.TryTake(out MeshData cleanData))
            {
                pending.CleanMeshes.Add(UploadTrackedMesh(cleanData, build.Spec?.Settings.Scale ?? 0));
                uploadedPending = true;
            }
            if (ready.TryTake(out MeshData previewData))
            {
                if (build.Spec?.Settings.Wireframe != true)
                    DeletePendingProvisionalMeshes(pending);
                MeshRef uploadedPreview = UploadTrackedMesh(previewData, build.Spec?.Settings.Scale ?? 0);
                pending.Meshes.Add(uploadedPreview);
                uploadedPending = true;
            }
            if (uploadedPending)
            {
                pending.LastUploadMs = now;
                return;
            }

            if (build.Completed && scaffoldReady.IsCompleted
                && ready.IsCompleted && cleanReady.IsCompleted)
            {
                if (!CleanSwapDelayElapsed(ref pending.CleanSwapReadyMs, now)) return;
                if (!ActivatePendingFinalMaterialization(pending))
                    pending.RefinementFailed = true;
                pending.RefinementFinished = true;
                pending.Build = null;
                TryFinishPendingPlacementVisual(pending);
            }
        }

        private bool ActivatePendingFinalMaterialization(PendingPlacementVisual pending)
        {
            if (pending == null || pending.CleanMeshes.Count == 0) return false;
            RetireMeshList(pending.Meshes);
            DeletePendingProvisionalMeshes(pending);
            pending.Meshes.AddRange(pending.CleanMeshes);
            pending.CleanMeshes.Clear();
            pending.MeshesAreFinal = true;
            if (pending.AdoptedGuideId != Guid.Empty)
                return TransferPendingFinalMeshesToGuide(pending);
            return true;
        }

        private bool TransferPendingFinalMeshesToGuide(PendingPlacementVisual pending)
        {
            if (pending == null || !pending.MeshesAreFinal || pending.Meshes.Count == 0
                || pending.AdoptedGuideId == Guid.Empty
                || !_guideMeshes.TryGetValue(pending.AdoptedGuideId, out GuideMesh guideMesh))
                return false;

            DeleteTrackedMesh(guideMesh.Ref);
            DeleteAuxiliaryMeshes(guideMesh);
            guideMesh.Ref = pending.Meshes[0];
            guideMesh.Origin = pending.Origin;
            for (int i = 1; i < pending.Meshes.Count; i++)
                guideMesh.Auxiliary.Add(pending.Meshes[i]);
            pending.Meshes.Clear();
            return true;
        }

        private bool TryAdoptPendingPlacementVisual(GuideData guide)
        {
            PendingPlacementVisual pending = _pendingPlacementVisual;
            if (pending == null || pending.AdoptedGuideId != Guid.Empty
                || !pending.Spec.MatchesPlacedGuide(guide)) return false;
            if (pending.RefinementFailed)
            {
                DiscardPendingPlacementVisual();
                return false;
            }

            bool placedPrivateAnchors = _network.ServerLayoutAvailable
                && _network.IsLocalGuide(guide.Id);
            if (pending.PrivateAnchors != placedPrivateAnchors) return false;

            RemoveGuideMesh(guide.Id);
            var mesh = new GuideMesh
            {
                Origin = pending.Origin,
                CullCenter = pending.CullCenter,
                CullRadius = pending.CullRadius,
                RenderedWireframe = false
            };
            _guideMeshes[guide.Id] = mesh;
            pending.AdoptedGuideId = guide.Id;
            if (pending.MeshesAreFinal && !TransferPendingFinalMeshesToGuide(pending))
            {
                DiscardPendingPlacementVisual();
                return false;
            }

            if (pending.Build == null && pending.RefinementFinished && pending.MeshesAreFinal)
                TryFinishPendingPlacementVisual(pending);
            return true;
        }

        private void TryFinishPendingPlacementVisual(PendingPlacementVisual pending)
        {
            if (pending == null || !ReferenceEquals(_pendingPlacementVisual, pending)
                || pending.AdoptedGuideId == Guid.Empty || pending.Build != null
                || !pending.RefinementFinished || !pending.MeshesAreFinal)
                return;

            if (!pending.PlacementEffectsRaised
                && _network.Guides.TryGetValue(pending.AdoptedGuideId, out GuideData guide)
                && guide != null)
            {
                pending.PlacementEffectsRaised = true;
                PlacementMaterializationCompleted?.Invoke(guide);
            }
            _pendingPlacementVisual = null;
        }

        private void DeletePendingProvisionalMeshes(PendingPlacementVisual pending)
        {
            if (pending == null) return;
            DeleteMeshList(pending.ProvisionalMeshes);
        }

        private void DiscardPendingPlacementVisual()
        {
            PendingPlacementVisual pending = _pendingPlacementVisual;
            if (pending == null) return;
            pending.Build?.Cancellation?.Cancel();
            // A partially materialized immense guide can own scores of uploaded batches. Deleting all of
            // them in the mutation packet handler creates the same one-frame cliff as building them there.
            RetireMeshList(pending.Meshes);
            RetireMeshList(pending.CleanMeshes);
            RetireMeshList(pending.ProvisionalMeshes);
            _pendingPlacementVisual = null;
        }

        /// <summary>
        /// Shows (or updates) the acting player's live draft ghost: the arch that WOULD be created from
        /// <paramref name="start"/> to <paramref name="end"/> with the given settings. Cheap to call every
        /// tick — rebuilds only when start/end/settings actually change. Cleared automatically when a real
        /// build lands via <see cref="ClearDraftPreview"/> from the controller.
        /// </summary>
        public void SetDraftPreview(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType = GuideShapeType.Arch,
            ShapeConstraint constraint = ShapeConstraint.None,
            PlaneAxis shapePlaneAxis = PlaneAxis.Y,
            int sides = 0, bool inverted = false, Vec3d apex = null, Vec3d rim = null,
            bool flatSideAligned = false)
        {
            if (_disposed || start == null || end == null) return;

            if (_hasPreviewKey
                && _previewSettings.Equals(settings)
                && _previewShapeType == shapeType && _previewConstraint == constraint
                && _previewPlaneAxis == shapePlaneAxis
                && _previewSides == sides && _previewInverted == inverted
                && _previewFlatSideAligned == flatSideAligned
                && SamePos(_previewStart, start)
                && SamePos(_previewEnd, end)
                && (apex == null ? _previewApex == null
                    : _previewApex != null && SamePos(_previewApex, apex))
                && (rim == null ? _previewRim == null
                    : _previewRim != null && SamePos(_previewRim, rim))) return;

            _previewShapeType = shapeType; _previewConstraint = constraint; _previewPlaneAxis = shapePlaneAxis;
            _previewSides = sides; _previewInverted = inverted;
            _previewFlatSideAligned = flatSideAligned;
            IGuideShape shape = ShapeFactory.Create(shapeType, constraint, shapePlaneAxis, start, end,
                inverted, sides, flatSideAligned: flatSideAligned);
            // A three-click shape mid-draft (triangle apex, or cylinder/cone/box height — 0.1.21): the
            // ghost's index-2 handle tracks the crosshair. A four-click Tapered Cylinder on its LAST stage
            // (0.2.24) also tracks the rim at index 3, so the taper opens and closes live.
            DraftManager.ApplyPlacementPoints(shape, shapeType, constraint, apex, rim);
            if (!UploadPreviewMesh(shape, settings)) return;

            _previewStart = new Vec3d(start.X, start.Y, start.Z);
            _previewEnd = new Vec3d(end.X, end.Y, end.Z);
            _previewApex = apex == null ? null : new Vec3d(apex.X, apex.Y, apex.Z);
            _previewRim = rim == null ? null : new Vec3d(rim.X, rim.Y, rim.Z);
            _previewSettings = settings;
            _hasPreviewKey = true;
        }

        // Free-Shape chain ghost (0.1.15): the placed corners + (unless closing) a live segment to the
        // crosshair, previewed through the exact placed-guide pipeline. Rebuild-keyed on a cheap corner
        // fingerprint so per-tick calls are free while nothing moves.
        private double _previewChainFp;

        /// <summary>
        /// Shows (or updates) the Free-Shape draft ghost: <paramref name="chain"/> = the placed corners;
        /// <paramref name="aim"/> = the live crosshair corner (null when <paramref name="closing"/> —
        /// the aim has snapped onto the first corner and the ghost previews the CLOSED loop instead).
        /// </summary>
        public void SetDraftChainPreview(List<Vec3d> chain, Vec3d aim, bool closing,
            GuideRenderSettings settings)
        {
            if (_disposed || chain == null || chain.Count == 0) return;

            var corners = new List<Vec3d>(chain.Count + 1);
            corners.AddRange(chain);
            if (!closing && aim != null) corners.Add(aim);
            if (corners.Count < 2) { ClearDraftPreview(); return; }

            double fp = corners.Count * 1000.0 + (closing ? 0.5 : 0.0);
            foreach (Vec3d p in corners) fp += p.X + p.Y * 3.0 + p.Z * 7.0;
            if (_hasPreviewKey && _previewShapeType == GuideShapeType.FreeShape
                && _previewSettings.Equals(settings) && _previewChainFp == fp) return;

            _previewShapeType = GuideShapeType.FreeShape;
            _previewConstraint = ShapeConstraint.None;
            _previewChainFp = fp;
            _previewStart = _previewEnd = _previewApex = _previewRim = null;   // the chain fp is this ghost's whole key

            IGuideShape shape = new FreeShape(corners, closing);
            if (!UploadPreviewMesh(shape, settings)) return;

            _previewSettings = settings;
            _hasPreviewKey = true;
        }

        // The shared draft-ghost tail: scale choice, voxel sampling, division marks, Surface flattening,
        // mesh build + upload. Returns false when the shape sampled to nothing (preview cleared).
        private bool UploadPreviewMesh(IGuideShape shape, GuideRenderSettings settings)
        {
            int renderScale = settings.Wireframe
                ? settings.Scale
                : ChooseRenderScale(shape, settings.Scale, settings.Filled);
            List<VoxelPosition> voxels = settings.Wireframe
                ? ShapeWireframe.GetVoxelPositions(shape, renderScale)
                : shape.GetVoxelPositions(renderScale, settings.Filled);
            if (settings.Divisions > 1)
                DivisionMarks.Apply(voxels, shape.SampleCurve(128), settings.Divisions, renderScale);
            if (voxels.Count == 0) { ClearDraftPreview(); return false; }

            // The ghost previews Surface projection faithfully too: flattened + paper-thin slab + inset,
            // same as the real thing.
            bool isSurface = settings.Mode == ProjectionMode.Surface;
            int slabSide = 0;
            if (isSurface) voxels = FlattenToPlaneLayer(voxels, settings.Plane, renderScale, out slabSide, out _);

            Vec3d origin = ComputeOrigin(shape.ControlPoints);
            var options = new GuideMeshOptions
            {
                Scale = renderScale,
                Mode = ProjectionMode.Volumetric,   // cubes even for Surface — see RebuildGuide
                Plane = settings.Plane,
                Origin = origin,
                Hidden = false,
                PlaneInset = isSurface ? SurfacePlaneInset : 0f,
                SurfaceSlabThickness = isSurface ? SurfaceSlabThicknessWorld : 0f,
                SurfaceSlabSide = slabSide,
                GrabbedPoint = null,
                PrivateAnchors = _network.ServerLayoutAvailable
                    && _network.AuthorityMode == ClientAuthorityMode.Local
            };
            AssignAnchors(shape.ControlPoints, options);

            MeshData data = GuideMeshBuilder.Build(voxels, options);
            DeleteTrackedMesh(_draftPreviewMesh);
            _draftPreviewMesh = UploadTrackedMesh(data, options);
            _draftPreviewOrigin = origin;
            SetDraftCullBounds(shape, renderScale);
            return true;
        }

        /// <summary>Removes the draft ghost (draft completed, cancelled, aim lost, or tool put away).</summary>
        public void ClearDraftPreview()
        {
            _hasPreviewKey = false;
            _draftVisualFingerprint = 0;
            _activeDraftBuild?.Cancellation?.Cancel();
            _activeDraftBuild = null;
            _draftMaterializationStarted = false;
            ClearDraftPrecisionMeshes();
            ClearDraftMaterialization();
            if (_draftPreviewMesh != null)
            {
                DeleteTrackedMesh(_draftPreviewMesh);
                _draftPreviewMesh = null;
            }
            _draftPreviewOrigin = null;
            _draftCullCenter = null;
            _draftCullRadius = 0;
        }

        private static bool SamePos(Vec3d a, Vec3d b)
        {
            const double eps = 1e-6;
            return a != null && b != null
                && System.Math.Abs(a.X - b.X) <= eps
                && System.Math.Abs(a.Y - b.Y) <= eps
                && System.Math.Abs(a.Z - b.Z) <= eps;
        }

        // World-unit slab shrink (plane axis only) for Surface-projected guides — see GuideMeshOptions.PlaneInset.
        private const float SurfacePlaneInset = 0.004f;

        // World-unit thickness of a Surface guide's paper-thin slab (Session-8 finding: the flattened voxel
        // sits in the AIR cell — right side, but a full-depth cube there floats off the wall; only a
        // paper-thin slab hugging the wall face reads as a decal). Tune by eye in-game.
        // Session 11 (human-requested): 0.01 → 0.0025 (a quarter of the old thickness — the slabs read
        // less like tiles stuck ON the wall). The anti-z-fight SurfacePlaneInset still holds the slab off
        // the wall: t = min(thickness, edge − 2·inset) keeps the pair valid at every scale.
        private const float SurfaceSlabThicknessWorld = 0.0025f;

        // Render-time realisation of Surface projection (Module 7; the shape-level Surface sampler remains
        // the eventual home, per the TODO in RebuildGuide). In-game findings that shaped it:
        //   1. Flush surface anchors sit EXACTLY on a voxel-cell boundary, so floor() jitter let sampled
        //      voxels flip between the two layers adjacent to the plane — stray cubes popped 3-D out of an
        //      otherwise flat guide. Projection semantics say the guide lies IN one layer anyway.
        //   2. WHICH layer cannot be decided by majority vote: floor() at an exact boundary is biased toward
        //      the positive side regardless of which side is air, so a majority put guides INSIDE negative-
        //      facing walls. The face sign was never stored, so the world itself is consulted instead: probe
        //      block solidity in both candidate layers and take the airier side (fallback: majority, for
        //      free-floating planes where both sides are air and either choice is visually fine).
        //   3. (Session 8) The chosen air cell is a FULL cell, so the builder needs to know which of its two
        //      faces the wall is on to hug it with the paper-thin slab: slabSide −1 = wall at the cell's min
        //      face, +1 = at its max face, 0 = no wall identified (mid-cell / free-floating → centre it).
        // Collapsed cells are deduped keeping the highest-precedence type, so single-voxel markers survive.
        private List<VoxelPosition> FlattenToPlaneLayer(List<VoxelPosition> voxels, ProjectionPlane plane, int scale,
                                                        out int slabSide, out bool probeReliable)
        {
            slabSide = 0;
            probeReliable = true;   // set false below if the air-side probe hit an unloaded chunk
            if (voxels.Count == 0 || scale <= 0) return voxels;

            PlaneAxis axis = plane.FlattenedAxis;

            static int AxisCoord(in VoxelPosition v, PlaneAxis a) =>
                a == PlaneAxis.X ? v.X : (a == PlaneAxis.Y ? v.Y : v.Z);
            static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

            // Candidate layers: the two cells that share the plane boundary. If the plane offset falls
            // MID-cell (a plane override off the grid), only one cell contains it — no side to choose.
            int layerPos = FloorDiv(plane.PlaneOffset, scale);
            int layerNeg = FloorDiv(plane.PlaneOffset - 1, scale);

            int chosen;
            if (layerPos == layerNeg)
            {
                chosen = layerPos;
            }
            else
            {
                int solidsPos = CountSolidProbes(voxels, axis, layerPos, scale, ref probeReliable);
                int solidsNeg = CountSolidProbes(voxels, axis, layerNeg, scale, ref probeReliable);
                if (solidsPos != solidsNeg)
                {
                    chosen = solidsPos < solidsNeg ? layerPos : layerNeg;   // the airier side
                    // The wall is the OTHER candidate cell, i.e. across the shared boundary: taking the
                    // positive cell puts the wall at its min face (−1); the negative cell, at its max (+1).
                    slabSide = chosen == layerPos ? -1 : 1;
                }
                else
                {
                    // Free-floating (or fully buried) plane: fall back to the sampled majority.
                    var counts = new Dictionary<int, int>();
                    foreach (VoxelPosition v in voxels)
                    {
                        int layer = FloorDiv(AxisCoord(v, axis), scale);
                        counts.TryGetValue(layer, out int n);
                        counts[layer] = n + 1;
                    }
                    chosen = layerPos; int bestCount = -1;
                    foreach (var kv in counts)
                        if (kv.Value > bestCount) { bestCount = kv.Value; chosen = kv.Key; }
                }
            }

            int target = chosen * scale;

            static int Rank(VoxelRenderType t) => t switch
            {
                VoxelRenderType.Locked => 3,
                VoxelRenderType.Primary => 2,
                VoxelRenderType.Anchor => 1,
                _ => 0
            };

            var flat = new Dictionary<(int, int, int), VoxelPosition>(voxels.Count);
            foreach (VoxelPosition v in voxels)
            {
                int x = axis == PlaneAxis.X ? target : v.X;
                int y = axis == PlaneAxis.Y ? target : v.Y;
                int z = axis == PlaneAxis.Z ? target : v.Z;
                var key = (x, y, z);
                var moved = new VoxelPosition(x, y, z, v.Type);
                if (!flat.TryGetValue(key, out VoxelPosition existing) || Rank(moved.Type) > Rank(existing.Type))
                    flat[key] = moved;
            }

            var result = new List<VoxelPosition>(flat.Count);
            foreach (VoxelPosition v in flat.Values) result.Add(v);
            return result;
        }

        // Samples up to five voxels spread along the guide and counts solid world blocks at their
        // positions projected into the given layer. Air has block id 0; anything else counts as solid —
        // plants slightly over-count, but the vote is averaged over several probes.
        //
        // RETIRED v0.3.70 — `NeighborSolidProbe` and its unloaded-chunk-aware wrapper `TrackedSolidProbe`,
        // the cell-space (1/16) world probes behind the mesh builder's z-fight clearances since 0.2.16.
        // The volumetric face offset is now derived from the guide's own voxel set and asks the world
        // nothing, so both are gone along with the millions of per-face block lookups a large guide used
        // to make at build time. `CountSolidProbes` below stays: Surface guides still need the world to
        // decide which side of a wall the decal sits on.
        private int CountSolidProbes(List<VoxelPosition> voxels, PlaneAxis axis, int layer, int scale, ref bool reliable)
        {
            var accessor = _capi.World.BlockAccessor;
            double planeCentre = (layer * scale + scale * 0.5) / 16.0;
            double half = scale * 0.5;

            int probes = Math.Min(5, voxels.Count);
            int solids = 0;
            for (int k = 0; k < probes; k++)
            {
                VoxelPosition v = voxels[(int)((long)k * (voxels.Count - 1) / Math.Max(1, probes - 1))];
                double wx = axis == PlaneAxis.X ? planeCentre : (v.X + half) / 16.0;
                double wy = axis == PlaneAxis.Y ? planeCentre : (v.Y + half) / 16.0;
                double wz = axis == PlaneAxis.Z ? planeCentre : (v.Z + half) / 16.0;

                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Floor(wy), (int)Math.Floor(wz));
                if (accessor.GetChunkAtBlockPos(pos) == null) { reliable = false; continue; } // world not loaded here yet
                var block = accessor.GetBlock(pos);
                if (block != null && block.Id != 0) solids++;
            }
            return solids;
        }

        // -- Mesh build pipeline -------------------------------------------------------------------

        private void RebuildAll()
        {
            foreach (KeyValuePair<Guid, GuideData> kv in _network.Guides)
            {
                if (kv.Value != null)
                    RebuildGuide(kv.Value);
            }

            // Drop meshes for guides no longer present in the mirror.
            if (_guideMeshes.Count > 0)
            {
                var stale = new List<Guid>();
                foreach (Guid id in _guideMeshes.Keys)
                    if (!_network.Guides.ContainsKey(id)) stale.Add(id);
                foreach (Guid id in stale) RemoveGuideMesh(id);
            }
        }

        // Fixes the world-load race: a Surface guide built while its blocks were still unloaded picked its
        // decal side from an all-air probe and can sit behind the face. Each tick, cheaply check whether a
        // deferred guide's neighbourhood has loaded (one chunk lookup) and, if so, rebuild it ONCE with a
        // now-reliable probe. Guides whose chunks stay unloaded (far away) cost only the cheap check — no
        // rebuild churn — and self-correct the moment you walk into range.
        private void OnReprobeTick(float dt)
        {
            if (_disposed) return;
            if (_deferredSurface.Count == 0 && _deferredOccupancy.Count == 0) return;
            IBlockAccessor accessor = _capi.World?.BlockAccessor;
            if (accessor == null) return;

            List<Guid> ready = null;

            foreach (Guid id in _deferredSurface)
            {
                bool rebuild = true;   // drop-and-rebuild if the guide is gone, has no anchor, or has loaded
                if (_network.Guides.TryGetValue(id, out GuideData g) && g != null)
                {
                    Vec3d a = FirstRealAnchor(g.ControlPoints);
                    if (a != null)
                    {
                        var bp = new BlockPos((int)Math.Floor(a.X), (int)Math.Floor(a.Y), (int)Math.Floor(a.Z));
                        rebuild = accessor.GetChunkAtBlockPos(bp) != null;   // only once the chunk is present
                    }
                }
                if (rebuild) (ready ??= new List<Guid>()).Add(id);
            }

            // The occupancy half (v0.4.42) — same race, same test, same tick. A guide sitting in BOTH sets
            // is queued once; the rebuild below serves both, since it re-probes everything.
            foreach (Guid id in _deferredOccupancy)
            {
                if (_deferredSurface.Contains(id)) continue;
                bool rebuild = !_network.Guides.TryGetValue(id, out GuideData g) || g == null
                    || OccupancyProbeReliable(g);
                if (rebuild) (ready ??= new List<Guid>()).Add(id);
            }

            if (ready == null) return;
            foreach (Guid id in ready)
            {
                _deferredSurface.Remove(id);
                _deferredOccupancy.Remove(id);
                RebuildGuideById(id);   // re-probes; re-defers itself only if still unreliable
            }
        }

        /// <summary>
        /// Whether the occupancy probe can currently see the world where <paramref name="guide"/> stands.
        /// </summary>
        /// <remarks>
        /// ONE CHUNK LOOKUP AT THE FIRST REAL ANCHOR — the same shape of test the Surface decal side uses,
        /// and deliberately the same one <see cref="OnReprobeTick"/> re-runs, so a guide deferred by this
        /// is released by the very condition that deferred it. A guide with no anchor to probe reports
        /// reliable rather than deferring forever: there is nothing here a later rebuild would read
        /// differently.
        /// </remarks>
        private bool OccupancyProbeReliable(GuideData guide)
        {
            IBlockAccessor accessor = _capi.World?.BlockAccessor;
            if (accessor == null) return false;

            Vec3d a = FirstRealAnchor(guide?.ControlPoints);
            if (a == null) return true;

            return accessor.GetChunkAtBlockPos(new BlockPos(
                (int)Math.Floor(a.X), (int)Math.Floor(a.Y), (int)Math.Floor(a.Z))) != null;
        }

        private static Vec3d FirstRealAnchor(List<ControlPoint> points)
        {
            if (points == null) return null;
            for (int i = 0; i < points.Count; i++)
            {
                ControlPoint cp = points[i];
                if (cp != null && !cp.IsPhantom && cp.IsAnchor && cp.WorldPosition != null) return cp.WorldPosition;
            }
            return null;
        }

        private void RebuildGuideById(Guid id) => RebuildGuideById(id, true);

        private void RebuildGuideById(Guid id, bool allowStreaming)
        {
            if (_network.Guides.TryGetValue(id, out GuideData guide) && guide != null)
                RebuildGuide(guide, allowStreaming);
        }

        private bool TryStartSettledShellMaterialization(GuideData guide)
        {
            if (guide == null) return false;
            ulong fingerprint = RenderFingerprint(guide);
            if (_settledMaterializations.TryGetValue(
                guide.Id, out SettledMaterializationBuild existing))
            {
                if (!guide.IsWireframe && existing.Fingerprint == fingerprint) return true;
                CancelSettledMaterialization(guide.Id);
            }

            if (guide.IsWireframe || guide.Projection != ProjectionMode.Volumetric
                || !GuideShapeTypes.IsVolume(guide.ShapeType)
                || !_guideMeshes.TryGetValue(guide.Id, out GuideMesh mesh)
                || !mesh.RenderedWireframe)
                return false;

            // THE SCAFFOLD MUST STILL BE A PICTURE OF THIS GUIDE. Without this test the check above only
            // asks whether a wireframe is showing at all — and one is, whenever the guide is already
            // mid-stream from an earlier change. Taking over from that stale scaffold starts the shell at
            // the NEW pose while leaving the wireframe drawn at the OLD one, and skips the caller's
            // rebuild, which is the only thing that would have re-uploaded it. Refusing here instead sends
            // the caller down RebuildGuide, which raises a fresh scaffold and then calls back in — at which
            // point the fingerprints agree and the stream starts properly.
            if (mesh.ScaffoldFingerprint != fingerprint) return false;

            GuideData snapshot = guide.DeepClone();
            var build = new SettledMaterializationBuild
            {
                GuideId = snapshot.Id,
                Fingerprint = fingerprint,
                Guide = snapshot,
                ReadyMeshes = new BlockingCollection<MeshData>(
                    new ConcurrentQueue<MeshData>(), MaterializationReadyBatchCapacity),
                CleanReadyMeshes = new BlockingCollection<MeshData>(
                    new ConcurrentQueue<MeshData>(), MaterializationReadyBatchCapacity),
                Cancellation = new CancellationTokenSource()
            };
            _settledMaterializations[guide.Id] = build;
            bool privateAnchors = _network.ServerLayoutAvailable
                && _network.IsLocalGuide(guide.Id);
            // Captured on the main thread, used on the worker: the closure holds the accessor and the
            // occupancy cache, both of which tolerate that (BlockOccupancy is lock-free — a
            // ConcurrentDictionary since v0.3.84, GOTCHAS G30).
            System.Func<int, int, int, bool> occupancyProbe = OccupancyProbe();

            Task.Factory.StartNew(
                    () => BuildSettledShellMaterialization(build, privateAnchors, occupancyProbe),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted && build.Error == null)
                        build.Error = task.Exception?.GetBaseException();
                    try
                    {
                        _capi.Event.EnqueueMainThreadTask(
                            () => FinishSettledShellMaterialization(build),
                            "layout-settled-shell-materialization");
                    }
                    catch
                    {
                        build.Cancellation.Cancel();
                    }
                });
            return true;
        }

        private static void BuildSettledShellMaterialization(
            SettledMaterializationBuild build, bool privateAnchors,
            System.Func<int, int, int, bool> occupancyProbe = null)
        {
            Thread thread = Thread.CurrentThread;
            ThreadPriority originalPriority = ThreadPriority.Normal;
            bool priorityChanged = false;
            try
            {
                try
                {
                    originalPriority = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                }
                catch (Exception) { }

                CancellationToken token = build.Cancellation.Token;
                token.ThrowIfCancellationRequested();
                GuideData guide = build.Guide;
                IGuideShape shape = ShapeFactory.Adopt(guide);
                shape.RecalculatePhantomPoints();
                int scale = guide.VoxelScale;
                List<VoxelPosition> voxels = shape is IProgressiveVoxelShape progressive
                    ? progressive.GetVoxelPositionsProgressively(
                        scale, guide.IsFilled, MaterializationTargetVoxelsPerBatch, token, null)
                    : shape.GetVoxelPositions(scale, guide.IsFilled);
                token.ThrowIfCancellationRequested();
                if (guide.Divisions > 1)
                    DivisionMarks.Apply(
                        voxels, shape.SampleCurve(128), guide.Divisions, scale);

                build.Shape = shape;
                build.Origin = ComputeOrigin(shape.ControlPoints);
                build.VoxelCount = voxels.Count;
                build.MetadataReady = true;
                ProduceGrabMaterializationMeshes(
                    guide, shape, voxels, -1, build.Origin, privateAnchors,
                    build.ReadyMeshes, build.CleanReadyMeshes, token, occupancyProbe);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                build.Error = e;
            }
            finally
            {
                if (!build.ReadyMeshes.IsAddingCompleted)
                    build.ReadyMeshes.CompleteAdding();
                if (!build.CleanReadyMeshes.IsAddingCompleted)
                    build.CleanReadyMeshes.CompleteAdding();
                build.Completed = true;
                if (priorityChanged)
                {
                    try { thread.Priority = originalPriority; }
                    catch (Exception) { }
                }
            }
        }

        private void FinishSettledShellMaterialization(SettledMaterializationBuild build)
        {
            if (_disposed || build == null) return;
            build.CompletionHandled = true;
            if (!_settledMaterializations.TryGetValue(build.GuideId, out var active)
                || !ReferenceEquals(active, build))
                return;

            if (build.Error == null
                && build.Cancellation?.IsCancellationRequested != true)
                return;

            if (build.Error != null)
                _capi.Logger.Warning(
                    "[Layout] Settled shell materialization failed: {0}", build.Error.Message);
            CancelSettledMaterialization(build.GuideId);
            if (_network.Guides.TryGetValue(build.GuideId, out GuideData live) && live != null)
            {
                if (_guideMeshes.TryGetValue(build.GuideId, out GuideMesh mesh))
                    mesh.RenderedWireframe = false;
                // Synchronous on purpose: streaming just failed for this guide, so re-entering it would
                // scaffold, fail, and recurse. A one-off hitch beats an infinite rebuild loop.
                RebuildGuide(live, false);
            }
        }

        private void AdvanceSettledMaterializations()
        {
            if (_settledMaterializations.Count == 0) return;
            var ids = new List<Guid>(_settledMaterializations.Keys);
            long now = _capi.World?.ElapsedMilliseconds ?? 0;
            for (int i = 0; i < ids.Count; i++)
            {
                Guid id = ids[i];
                if (!_settledMaterializations.TryGetValue(
                    id, out SettledMaterializationBuild build))
                    continue;
                if (build.Cancellation?.IsCancellationRequested == true)
                {
                    CancelSettledMaterialization(id);
                    continue;
                }
                if (!_network.Guides.TryGetValue(id, out GuideData live) || live == null
                    || RenderFingerprint(live) != build.Fingerprint)
                {
                    CancelSettledMaterialization(id);
                    continue;
                }
                if (!_guideMeshes.TryGetValue(id, out GuideMesh mesh)) continue;

                BlockingCollection<MeshData> ready = build.ReadyMeshes;
                BlockingCollection<MeshData> cleanReady = build.CleanReadyMeshes;
                bool drained = build.Completed && build.CompletionHandled
                    && ready.IsCompleted && ready.Count == 0
                    && cleanReady.IsCompleted && cleanReady.Count == 0;
                if (drained)
                {
                    if (!CleanSwapDelayElapsed(ref build.CleanSwapReadyMs, now)) continue;
                    CompleteSettledShellMaterialization(build, mesh);
                    continue;
                }

                int interval = MaterializationUploadInterval(build.VoxelCount);
                if (build.LastUploadMs != 0 && now - build.LastUploadMs < interval) continue;

                bool uploaded = false;
                if (cleanReady.TryTake(out MeshData cleanData))
                {
                    MeshRef clean = UploadTrackedMesh(cleanData, build.Guide?.VoxelScale ?? 0);
                    if (mesh.TransitionCleanRef == null)
                        mesh.TransitionCleanRef = clean;
                    else
                        mesh.TransitionCleanAuxiliary.Add(clean);
                    uploaded = true;
                }
                if (ready.TryTake(out MeshData previewData))
                {
                    MeshRef preview = UploadTrackedMesh(previewData, build.Guide?.VoxelScale ?? 0);
                    if (mesh.TransitionRef == null)
                    {
                        mesh.TransitionRef = preview;
                        mesh.TransitionOrigin = build.Origin;
                    }
                    else
                    {
                        mesh.TransitionAuxiliary.Add(preview);
                    }
                    uploaded = true;
                }
                if (uploaded) build.LastUploadMs = now;
            }
        }

        private void CompleteSettledShellMaterialization(
            SettledMaterializationBuild build, GuideMesh mesh)
        {
            if (build == null || mesh == null) return;
            if (mesh.TransitionCleanRef == null)
            {
                _settledMaterializations.Remove(build.GuideId);
                DeleteTransitionMeshes(mesh);
                mesh.RenderedWireframe = false;
                // Synchronous: the stream produced no clean mesh, so re-entering streaming would loop.
                RebuildGuideById(build.GuideId, false);
                return;
            }
            if (mesh.Ref != null) _retiredMaterializationMeshes.Enqueue(mesh.Ref);
            RetireMeshList(mesh.Auxiliary);
            if (mesh.TransitionRef != null)
                _retiredMaterializationMeshes.Enqueue(mesh.TransitionRef);
            RetireMeshList(mesh.TransitionAuxiliary);

            mesh.Ref = mesh.TransitionCleanRef;
            mesh.Origin = build.Origin;
            mesh.Auxiliary.AddRange(mesh.TransitionCleanAuxiliary);
            mesh.TransitionRef = null;
            mesh.TransitionOrigin = null;
            mesh.TransitionCleanRef = null;
            mesh.TransitionCleanAuxiliary.Clear();
            mesh.RenderedWireframe = false;
            _settledMaterializations.Remove(build.GuideId);
        }

        private void CancelSettledMaterialization(Guid id)
        {
            if (_settledMaterializations.TryGetValue(id, out SettledMaterializationBuild build))
            {
                build.Cancellation?.Cancel();
                _settledMaterializations.Remove(id);
            }
            if (_guideMeshes.TryGetValue(id, out GuideMesh mesh))
                DeleteTransitionMeshes(mesh);
        }

        private void CancelAllSettledMaterializations()
        {
            if (_settledMaterializations.Count == 0) return;
            var ids = new List<Guid>(_settledMaterializations.Keys);
            for (int i = 0; i < ids.Count; i++)
                CancelSettledMaterialization(ids[i]);
        }

        /// <summary>
        /// Above this cached voxel count a settled Volumetric shell is streamed in behind a wireframe
        /// scaffold instead of being generated, meshed, and uploaded in one main-thread call.
        /// </summary>
        /// <remarks>
        /// This closes the gap that made a big guide hang the client on world load and drop onto OTHER
        /// players in one lump when placed: <see cref="RebuildGuide"/> was fully synchronous with no size
        /// check, so <c>RebuildAll</c> (bulk sync) and a remote <c>OnGuideAddedOrUpdated</c> both paid the
        /// full cost at once. Only the local placer's own path was ever streamed.
        ///
        /// [FLAGGED FOR REVIEW] 100,000 is a judgement call, not a measurement. Below it a synchronous
        /// rebuild is a sub-frame blip; at 500,000 it is a visible hitch; at 8M it is the multi-second hang.
        /// Raising it means fewer guides visibly grow in on world load; lowering it means smoother loading
        /// but more guides animating at once. One constant, trivially retuned from play.
        /// </remarks>
        private const int SettledStreamingVoxelThreshold = 100000;

        /// <summary>
        /// Whether this guide should stream rather than build synchronously. Deliberately keyed on the
        /// PERSISTED <c>CachedVoxelCount</c>: generating the voxel set to find out how big it is would
        /// already have paid the cost this check exists to avoid. Preconditions mirror
        /// <see cref="TryStartSettledShellMaterialization"/> so a scaffold is never raised for a guide that
        /// would then decline to stream.
        /// </summary>
        private bool ShouldStreamSettledShell(GuideData guide) =>
            guide != null
            && !guide.IsWireframe
            && guide.Projection == ProjectionMode.Volumetric
            && GuideShapeTypes.IsVolume(guide.ShapeType)
            && guide.CachedVoxelCount > SettledStreamingVoxelThreshold;

        private void RebuildGuide(GuideData guide) => RebuildGuide(guide, true);

        /// <param name="allowStreaming">
        /// False forces the synchronous path. Used by the materialization completion/failure handlers, which
        /// call back into a rebuild — without this they would re-enter streaming and loop forever.
        /// </param>
        private void RebuildGuide(GuideData guide, bool allowStreaming)
        {
            if (_disposed) return;

            List<ControlPoint> points = guide.ControlPoints;
            // Minimum real content is shape-dependent now (an ellipse carries 3 points, an arch spine 4+
            // with phantoms); anything below 2 can't form any shape, and a degenerate list simply samples
            // to zero voxels below.
            if (points == null || points.Count < 2)
            {
                RemoveGuideMesh(guide.Id); // not enough points to form a curve yet
                return;
            }

            // Build a shape over the mirror's own list (adopted by reference) and refresh phantoms, which are
            // stale after an incremental point edit.
            IGuideShape shape = ShapeFactory.Adopt(guide);
            shape.RecalculatePhantomPoints();
            ComputeCullBounds(
                shape, guide.VoxelScale,
                out Vec3d cullCenter, out double cullRadius);

            bool locallyGrabbed = guide.Id == _grabbedGuide;
            if (locallyGrabbed)
            {
                ulong poseFingerprint = RenderFingerprint(guide);
                if (poseFingerprint != _grabPoseFingerprint)
                {
                    _grabGeneration++;
                    _grabPoseFingerprint = poseFingerprint;
                    _grabRefinedFingerprint = 0;
                    _grabLastMotionMs = _capi.World?.ElapsedMilliseconds ?? 0;
                    _activeGrabBuild?.Cancellation?.Cancel();
                    _activeGrabBuild = null;
                    _grabMaterializationStarted = false;
                    _grabMaterializationGuide = Guid.Empty;
                    _lastGrabMaterializationUploadMs = 0;
                }
                _grabExtent = GuideMeshBuilder.MeasureShapeExtent(shape, guide.VoxelScale);
                if (guide.CachedVoxelCount > PreviewFullResVoxelCap)
                {
                    RebuildGrabWireframe(guide, shape, points);
                    SetGuideCullBounds(guide.Id, cullCenter, cullRadius);
                    return;
                }
            }

            // IMMENSE SETTLED SHELL: show a cheap wireframe scaffold now and stream the exact shell in
            // behind it, rather than freezing the client while millions of voxels are generated, meshed,
            // and uploaded in this one call. Covers world load / bulk sync and guides arriving from other
            // players — previously only the local placer saw a guide materialize.
            if (!locallyGrabbed && allowStreaming && ShouldStreamSettledShell(guide))
            {
                // BEING MOVED: hold the scaffold and start nothing. The player is still adjusting, and each
                // further nudge would only discard a part-built shell. The floor band is drawn at the
                // guide's true scale here so the bottom edge can be lined up voxel-exactly while it moves.
                if (MoveHoldActive(guide.Id))
                {
                    RebuildSettledScaffold(
                        guide, shape, points, cullCenter, cullRadius, preciseFloorBand: true);
                    return;
                }

                // Already streaming this exact pose: leave it alone. Re-scaffolding every rebuild would
                // restart the animation and throw away completed batches.
                if (_settledMaterializations.TryGetValue(
                        guide.Id, out SettledMaterializationBuild running)
                    && running.Fingerprint == RenderFingerprint(guide))
                {
                    SetGuideCullBounds(guide.Id, cullCenter, cullRadius);
                    return;
                }

                RebuildSettledScaffold(guide, shape, points, cullCenter, cullRadius);
                if (TryStartSettledShellMaterialization(guide)) return;
                // Declined for a reason ShouldStreamSettledShell could not see. Fall through to the
                // synchronous build so a guide is never left showing only its scaffold.
            }

            // Session-8 playtest fix: PLACED guides always render at their TRUE scale. The coarsening
            // fallback (ChooseRenderScale) is a drafting-only courtesy — while the second foot is still
            // being aimed, a huge ghost may temporarily render coarser to stay cheap — and it was leaking
            // into settled guides here, permanently degrading anything over the preview cap. Once the
            // second anchor is placed, what you see is exactly the resolution you chose.
            int renderScale = guide.VoxelScale;
            List<VoxelPosition> voxels = guide.IsWireframe
                ? ShapeWireframe.GetVoxelPositions(shape, renderScale)
                : shape.GetVoxelPositions(renderScale, guide.IsFilled);

            // Session 9: equal-part division marks — a pure renderer-side recolor by arc length; never
            // touches geometry, counts, or caps. Functional markers win by precedence.
            if (guide.Divisions > 1)
                DivisionMarks.Apply(voxels, shape.SampleCurve(128), guide.Divisions, renderScale);
            if (voxels.Count == 0)
            {
                RemoveGuideMesh(guide.Id);
                return;
            }

            // Surface projection, realised at render time: flatten onto the plane's air-side layer and emit
            // paper-thin slabs hugging the wall face (anti z-fight via the inset). See FlattenToPlaneLayer.
            bool isSurface = guide.Projection == ProjectionMode.Surface;
            int slabSide = 0;
            bool probeReliable = true;
            if (isSurface) voxels = FlattenToPlaneLayer(voxels, guide.Plane, renderScale, out slabSide, out probeReliable);

            // World-load race: if the air-side probe couldn't see the world yet (chunk unloaded), the decal
            // side is provisional — defer so the re-probe tick rebuilds it correctly once the area loads.
            if (isSurface && !probeReliable) _deferredSurface.Add(guide.Id);
            else _deferredSurface.Remove(guide.Id);

            // THE SAME RACE, for the occupancy colours (v0.4.42, human-reported). A guide meshed while its
            // terrain was still streaming in probes nothing but unloaded chunks, so every body voxel reads
            // as unbuilt and the chiselling highlight stays dark for the rest of the session. Deferring it
            // here hands it to the same re-probe tick the Surface decal side already uses — one cheap chunk
            // lookup every 500 ms, one rebuild when the area arrives, and no rebuild at all for a guide
            // whose chunks never load.
            //
            // Tested at the ANCHOR — cheap, and attributable to THIS guide, which is what a signal from
            // BlockOccupancy itself could never be (the probe runs on materialization workers too). The
            // other half of the fix is upstream and does the real work: a read against an unloaded chunk
            // is no longer CACHED, so when this rebuild comes it reads the world rather than the answer
            // the first blind pass wrote down. See BlockOccupancy.GetOrBuild.
            if (_occupancyEnabled && !OccupancyProbeReliable(guide)) _deferredOccupancy.Add(guide.Id);
            else _deferredOccupancy.Remove(guide.Id);

            Vec3d origin = ComputeOrigin(points);

            var options = new GuideMeshOptions
            {
                Scale = renderScale,
                // TODO(Surface): move flattening into the shape's own Surface sampler eventually; the
                // builder's dormant tile path stays unused — Surface renders as paper-thin slabs for now.
                Mode = ProjectionMode.Volumetric,
                Plane = guide.Plane,
                Origin = origin,
                Hidden = guide.IsHidden,
                PlaneInset = isSurface ? SurfacePlaneInset : 0f,
                SurfaceSlabThickness = isSurface ? SurfaceSlabThicknessWorld : 0f,
                SurfaceSlabSide = slabSide,
                GrabbedPoint = ResolveGrabbedPoint(guide, points),
                PrivateAnchors = _network.ServerLayoutAvailable
                    && _network.IsLocalGuide(guide.Id),
                OccupancyProbe = OccupancyProbe(),
            };
            AssignAnchors(points, options);

            // No solidity probe since v0.3.70: the face offset is derived from the guide's own voxel set
            // and never consults the world, so there is nothing that unloaded terrain can get wrong and
            // nothing to re-probe once it arrives.
            MeshData data = GuideMeshBuilder.Build(voxels, options);

            if (locallyGrabbed) UploadOrReplaceGrab(guide.Id, data, origin);
            else UploadOrReplace(guide.Id, data, origin);
            if (_guideMeshes.TryGetValue(guide.Id, out GuideMesh rendered))
                rendered.RenderedWireframe = guide.IsWireframe;
            SetGuideCullBounds(guide.Id, cullCenter, cullRadius);
        }

        /// <summary>
        /// Uploads the cheap structural wireframe an immense settled guide shows while its exact shell is
        /// generated off-thread. Marks the guide <c>RenderedWireframe</c>, which is the precondition
        /// <see cref="TryStartSettledShellMaterialization"/> tests before taking over.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT the grab wireframe path: no adaptive motion scale, no cursor precision hole, no
        /// grab mesh slot. This is a static stand-in for a guide nobody is touching.
        /// </remarks>
        /// <param name="preciseFloorBand">
        /// Replaces the bottom of the coarse wireframe with graduated true-scale layers, exactly as a
        /// moving draft does around the dragged point. The scaffold body is deliberately coarse — that is
        /// what makes it cheap — but a coarse bottom edge is useless for lining a guide up while moving it,
        /// because the edge you are aiming at is not where the real shell's edge will land.
        /// </summary>
        /// <remarks>
        /// The coarse wire is CUT AWAY under the band rather than merely overdrawn. That cut is the whole
        /// trick, and its absence was the v0.3.88 defect: fine voxels were added along the bottom while the
        /// block-sized coarse cubes stayed exactly where they were, and since guides are order-dependent
        /// translucent geometry the primary mesh drew first and won the depth test. The result read as
        /// "1/16 voxels that are somehow a full block across" — the fine geometry was there and invisible
        /// inside the coarse geometry. <c>ShowMovingDraft</c> removes its coarse cells inside the precision
        /// radius for the same reason.
        /// </remarks>
        private void RebuildSettledScaffold(
            GuideData guide, IGuideShape shape, List<ControlPoint> points,
            Vec3d cullCenter, double cullRadius, bool preciseFloorBand = false)
        {
            List<Vec3d> curve = shape.SampleCurve(128);
            int scaffoldScale = ChooseMovingWireframeScale(curve, guide.VoxelScale);
            List<VoxelPosition> coarse = BuildWireframe(shape, curve, scaffoldScale);

            for (int i = 0; i < points.Count; i++)
            {
                ControlPoint point = points[i];
                if (point?.WorldPosition == null || point.IsPhantom) continue;
                VoxelRenderType type = point.IsLocked ? VoxelRenderType.Locked
                    : point.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(coarse, scaffoldScale, point.WorldPosition, type);
            }

            // Markers are claimed BEFORE the cut so a base anchor's coarse cell is removed with everything
            // else down there; the layers re-claim it at their own finer scale.
            List<(int Scale, List<VoxelPosition> Voxels)> floorLayers = null;
            if (preciseFloorBand && scaffoldScale > guide.VoxelScale)
            {
                floorLayers = BuildFloorPrecisionLayers(
                    curve, points, guide.VoxelScale, scaffoldScale, out double bandTop);
                if (floorLayers.Count > 0)
                {
                    double halfCoarse = scaffoldScale / 32.0;
                    coarse.RemoveAll(v => v.Y / 16.0 + halfCoarse <= bandTop);
                }
            }

            Vec3d origin = ComputeOrigin(points);
            var options = new GuideMeshOptions
            {
                Scale = scaffoldScale,
                // Streaming is gated to Volumetric volumes, so the Surface slab path cannot apply here.
                Mode = ProjectionMode.Volumetric,
                Plane = guide.Plane,
                Origin = origin,
                Hidden = guide.IsHidden,
                PrivateAnchors = _network.ServerLayoutAvailable
                    && _network.IsLocalGuide(guide.Id)
            };
            AssignAnchors(points, options);

            UploadOrReplace(
                guide.Id, GuideMeshBuilder.Build(coarse, options), origin, scaffoldScale);
            if (_guideMeshes.TryGetValue(guide.Id, out GuideMesh mesh))
            {
                mesh.RenderedWireframe = true;
                // Record WHICH guide state this scaffold depicts, so a later change can tell that the
                // wireframe on screen has gone stale rather than assuming any wireframe is a current one.
                mesh.ScaffoldFingerprint = RenderFingerprint(guide);

                // Added AFTER UploadOrReplace, which clears the auxiliary list as part of replacing the
                // primary mesh — building the layers first would only have them deleted again.
                if (floorLayers != null)
                {
                    for (int i = 0; i < floorLayers.Count; i++)
                    {
                        var layerOptions = new GuideMeshOptions
                        {
                            Scale = floorLayers[i].Scale,
                            Mode = ProjectionMode.Volumetric,
                            Plane = guide.Plane,
                            Origin = origin,
                            Hidden = guide.IsHidden,
                            PrivateAnchors = options.PrivateAnchors
                        };
                        AssignAnchors(points, layerOptions);
                        MeshRef layerMesh = UploadTrackedMesh(
                            GuideMeshBuilder.Build(floorLayers[i].Voxels, layerOptions),
                            floorLayers[i].Scale);
                        if (layerMesh != null) mesh.Auxiliary.Add(layerMesh);
                    }
                }
            }
            SetGuideCullBounds(guide.Id, cullCenter, cullRadius);
        }

        /// <summary>
        /// Builds the graduated floor layers: finest at the very bottom, stepping one valid scale coarser
        /// per layer until it meets the scaffold's own scale. Mirrors <see cref="UploadPrecisionLayers"/>,
        /// which does the same around a dragged point — the difference is only that the distance measured
        /// here is HEIGHT above the shape's lowest point rather than radius from an aim position, so the
        /// precision sits where the player is lining the guide up.
        /// </summary>
        /// <param name="bandTop">
        /// World Y below which the coarse wire must be cut away. Negative infinity when no layers were
        /// produced, so a caller cutting on it removes nothing.
        /// </param>
        private static List<(int Scale, List<VoxelPosition> Voxels)> BuildFloorPrecisionLayers(
            List<Vec3d> curve, List<ControlPoint> points, int fineScale, int coarseScale, out double bandTop)
        {
            bandTop = double.NegativeInfinity;
            var layers = new List<(int, List<VoxelPosition>)>();
            if (curve == null || curve.Count == 0 || fineScale <= 0 || coarseScale <= fineScale)
                return layers;

            double minY = double.MaxValue;
            for (int i = 0; i < curve.Count; i++)
                if (curve[i] != null && curve[i].Y < minY) minY = curve[i].Y;
            if (minY == double.MaxValue) return layers;

            var scales = new List<int>();
            for (int i = 0; i < GuideData.ValidVoxelScales.Length; i++)
            {
                int scale = GuideData.ValidVoxelScales[i];
                if (scale >= fineScale && scale < coarseScale) scales.Add(scale);
            }
            if (scales.Count == 0) return layers;

            // A solid core of four selected voxels at the true scale, then the transition above it — the
            // same shape of rule the dragged-point bands use for their inner radius.
            double inner = Math.Max(fineScale * 4.0 / 16.0, fineScale / 16.0);
            double outer = Math.Max(MoveWireframeFloorBandBlocks, inner);
            double extra = Math.Max(0.0, outer - inner);
            if (extra <= 1e-6 && scales.Count > 1) scales.RemoveRange(1, scales.Count - 1);
            bandTop = minY + outer;

            for (int i = 0; i < scales.Count; i++)
            {
                int scale = scales[i];
                double lower = i == 0
                    ? double.NegativeInfinity
                    : inner + extra * i / scales.Count;
                double upper = inner + extra * (i + 1) / scales.Count;

                List<VoxelPosition> band = BuildFloorWireBand(curve, scale, minY, lower, upper);
                ClaimFloorMarkers(band, points, scale, minY, lower, upper);
                if (band.Count > 0) layers.Add((scale, band));
            }
            return layers;
        }

        // One height slice of the curve, marched at `scale`. Segments are CLIPPED at the slice ceiling
        // before marching, so a shape with one long near-vertical run cannot quietly turn a thin band into
        // a full-resolution wireframe.
        private static List<VoxelPosition> BuildFloorWireBand(
            List<Vec3d> curve, int scale, double minY, double lowerHeight, double upperHeight)
        {
            var marched = new List<VoxelPosition>();
            var seen = new HashSet<(int, int, int)>();
            double guardTop = minY + upperHeight + scale * 0.125;

            if (curve.Count == 1)
            {
                if (curve[0] != null && curve[0].Y <= guardTop)
                    VoxelMarch.MarchInto(marched, seen, curve, scale);
            }
            else
            {
                for (int i = 1; i < curve.Count; i++)
                {
                    Vec3d a = curve[i - 1], b = curve[i];
                    if (a == null || b == null) continue;
                    if (a.Y > guardTop && b.Y > guardTop) continue;

                    Vec3d p = a, q = b;
                    if (a.Y > guardTop || b.Y > guardTop)
                    {
                        double span = b.Y - a.Y;
                        if (Math.Abs(span) < 1e-9) continue;
                        double t = (guardTop - a.Y) / span;
                        if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                        Vec3d crossing = new Vec3d(
                            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
                        if (a.Y > guardTop) p = crossing; else q = crossing;
                    }

                    VoxelMarch.MarchSegmentInto(marched, seen, p, q, scale);
                }
            }

            // Trim to the slice by CELL CENTRE, with half a cell of overlap at each edge so neighbouring
            // layers meet without a seam of missing wire between them.
            double half = scale / 32.0;
            double overlap = scale / 32.0;
            double low = double.IsNegativeInfinity(lowerHeight)
                ? double.NegativeInfinity : minY + lowerHeight - overlap;
            double high = minY + upperHeight + overlap;
            marched.RemoveAll(v =>
            {
                double centreY = v.Y / 16.0 + half;
                return centreY < low || centreY > high;
            });
            return marched;
        }

        // Re-claims control-point markers that fall inside a layer, at that layer's scale — they were
        // removed from the coarse wire by the floor cut, and a base anchor is exactly the thing a player
        // lines up against.
        private static void ClaimFloorMarkers(
            List<VoxelPosition> band, List<ControlPoint> points, int scale,
            double minY, double lowerHeight, double upperHeight)
        {
            if (points == null) return;
            double low = double.IsNegativeInfinity(lowerHeight)
                ? double.NegativeInfinity : minY + lowerHeight;
            double high = minY + upperHeight;

            for (int i = 0; i < points.Count; i++)
            {
                ControlPoint point = points[i];
                Vec3d world = point?.WorldPosition;
                if (world == null || point.IsPhantom) continue;
                if (world.Y < low || world.Y > high) continue;
                VoxelRenderType type = point.IsLocked ? VoxelRenderType.Locked
                    : point.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(band, scale, world, type);
            }
        }

        private void RebuildGrabWireframe(GuideData guide, IGuideShape shape, List<ControlPoint> points)
        {
            var timer = Stopwatch.StartNew();
            int selectedScale = guide.VoxelScale;
            int minimumScale = Math.Max(selectedScale,
                _grabAdaptiveScale > 0 ? _grabAdaptiveScale : selectedScale);
            List<Vec3d> curve = shape.SampleCurve(128);
            int movingScale = ChooseMovingWireframeScale(curve, minimumScale);
            List<VoxelPosition> coarse = BuildWireframe(shape, curve, movingScale);

            for (int i = 0; i < points.Count; i++)
            {
                ControlPoint point = points[i];
                if (point?.WorldPosition == null || point.IsPhantom) continue;
                VoxelRenderType type = point.IsLocked ? VoxelRenderType.Locked
                    : point.IsPrimary ? VoxelRenderType.Primary : VoxelRenderType.Anchor;
                ShapeGeometry.ClaimMarker(coarse, movingScale, point.WorldPosition, type);
            }

            Vec3d aim = ResolveGrabbedPoint(guide, points);
            if (movingScale > selectedScale && aim != null)
            {
                double half = movingScale / 32.0;
                double radius2 = PrecisionTransitionRadius * PrecisionTransitionRadius;
                coarse.RemoveAll(v =>
                {
                    double dx = v.X / 16.0 + half - aim.X;
                    double dy = v.Y / 16.0 + half - aim.Y;
                    double dz = v.Z / 16.0 + half - aim.Z;
                    return dx * dx + dy * dy + dz * dz <= radius2;
                });
            }

            bool isSurface = guide.Projection == ProjectionMode.Surface;
            int slabSide = 0;
            if (isSurface)
                coarse = FlattenToPlaneLayer(coarse, guide.Plane, movingScale, out slabSide, out _);
            Vec3d origin = ComputeOrigin(points);
            var options = GrabWireOptions(guide, points, origin, movingScale, isSurface, slabSide, aim);
            UploadOrReplaceGrab(guide.Id, GuideMeshBuilder.Build(coarse, options), origin);

            if (movingScale > selectedScale && aim != null
                && _guideMeshes.TryGetValue(guide.Id, out GuideMesh mesh))
            {
                var scales = new List<int>();
                for (int i = 0; i < GuideData.ValidVoxelScales.Length; i++)
                {
                    int scale = GuideData.ValidVoxelScales[i];
                    if (scale >= selectedScale && scale < movingScale) scales.Add(scale);
                }
                double innerRadius = selectedScale * 4.0 / 16.0;
                double outerRadius = Math.Max(PrecisionTransitionRadius, innerRadius);
                if (outerRadius <= innerRadius + 1e-6 && scales.Count > 1)
                    scales.RemoveRange(1, scales.Count - 1);

                for (int i = 0; i < scales.Count; i++)
                {
                    int scale = scales[i];
                    double extra = Math.Max(0.0, outerRadius - innerRadius);
                    double lower = i == 0 ? 0.0 : innerRadius + extra * i / scales.Count;
                    double upper = innerRadius + extra * (i + 1) / scales.Count;
                    List<VoxelPosition> band = BuildLocalWireBand(curve, scale, aim, lower, upper);
                    if (band.Count == 0) continue;
                    int bandSlabSide = 0;
                    if (isSurface)
                        band = FlattenToPlaneLayer(band, guide.Plane, scale, out bandSlabSide, out _);
                    GuideMeshOptions bandOptions = GrabWireOptions(
                        guide, points, origin, scale, isSurface, bandSlabSide, aim);
                    mesh.GrabAuxiliary.Add(
                        UploadTrackedMesh(GuideMeshBuilder.Build(band, bandOptions), bandOptions));
                }
            }

            timer.Stop();
            AdaptGrabScale(movingScale, selectedScale, timer.ElapsedMilliseconds);
        }

        private GuideMeshOptions GrabWireOptions(
            GuideData guide, List<ControlPoint> points, Vec3d origin, int scale,
            bool isSurface, int slabSide, Vec3d aim)
        {
            var options = new GuideMeshOptions
            {
                Scale = scale,
                Mode = ProjectionMode.Volumetric,
                Plane = guide.Plane,
                Origin = origin,
                Hidden = guide.IsHidden,
                PlaneInset = isSurface ? SurfacePlaneInset : 0f,
                SurfaceSlabThickness = isSurface ? SurfaceSlabThicknessWorld : 0f,
                SurfaceSlabSide = slabSide,
                GrabbedPoint = aim,
                PrivateAnchors = _network.ServerLayoutAvailable && _network.IsLocalGuide(guide.Id)
            };
            AssignAnchors(points, options);
            return options;
        }

        private void AdaptGrabScale(int renderedScale, int selectedScale, long workMilliseconds)
        {
            double targetFrame = Math.Max(16.67, _grabBaselineFrameMilliseconds * 1.25);
            double pressure = _smoothedFrameMilliseconds / targetFrame;
            if (workMilliseconds >= 6 || pressure > 1.15)
            {
                int scale = Math.Max(renderedScale, _grabAdaptiveScale);
                int steps = workMilliseconds >= 14 || pressure > 1.5 ? 2 : 1;
                while (steps-- > 0) scale = NextCoarserScale(scale);
                _grabAdaptiveScale = scale;
                _grabHealthyUpdates = 0;
                return;
            }
            if (workMilliseconds > 3 || pressure > 0.95)
            {
                _grabHealthyUpdates = 0;
                return;
            }
            if (++_grabHealthyUpdates < 7) return;
            _grabHealthyUpdates = 0;
            _grabAdaptiveScale = NextFinerScale(
                _grabAdaptiveScale > 0 ? _grabAdaptiveScale : selectedScale, selectedScale);
        }

        private void RequestSettledGrabRefinement()
        {
            if (_disposed || _grabbedGuide == Guid.Empty || _grabPoseFingerprint == 0
                || _grabRefinedFingerprint == _grabPoseFingerprint
                || _activeGrabBuild != null) return;
            long now = _capi.World?.ElapsedMilliseconds ?? 0;
            if (now - _grabLastMotionMs < GrabSettleDelayMs) return;
            if (!_network.Guides.TryGetValue(_grabbedGuide, out GuideData live) || live == null
                || live.CachedVoxelCount <= PreviewFullResVoxelCap) return;

            lock (_grabWorkGate)
            {
                if (_grabWorkBusy) return;
                _grabWorkBusy = true;
            }

            int generation = _grabGeneration;
            ulong fingerprint = _grabPoseFingerprint;
            int grabbedIndex = _grabbedIndex;
            GuideData snapshot = live.DeepClone();
            bool privateAnchors = _network.ServerLayoutAvailable && _network.IsLocalGuide(live.Id);
            var result = new GrabBuildResult
            {
                Generation = generation,
                Fingerprint = fingerprint,
                GuideId = snapshot.Id,
                GrabbedIndex = grabbedIndex,
                Guide = snapshot,
                IsMaterialization = snapshot.Projection == ProjectionMode.Volumetric,
                Cancellation = new CancellationTokenSource()
            };
            if (result.IsMaterialization)
            {
                result.ReadyMeshes = new BlockingCollection<MeshData>(
                    new ConcurrentQueue<MeshData>(), MaterializationReadyBatchCapacity);
                result.CleanReadyMeshes = new BlockingCollection<MeshData>(
                    new ConcurrentQueue<MeshData>(), MaterializationReadyBatchCapacity);
            }
            _activeGrabBuild = result;
            _grabMaterializationStarted = false;

            Task.Factory.StartNew(
                    () => BuildGrabRefinement(result, privateAnchors),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted && result.Error == null)
                        result.Error = task.Exception?.GetBaseException();
                    try
                    {
                        _capi.Event.EnqueueMainThreadTask(
                            () => FinishGrabRefinement(result), "layout-grab-refinement");
                    }
                    catch
                    {
                        lock (_grabWorkGate) _grabWorkBusy = false;
                    }
                });
        }

        private static void BuildGrabRefinement(
            GrabBuildResult result, bool privateAnchors)
        {
            Thread thread = Thread.CurrentThread;
            ThreadPriority originalPriority = ThreadPriority.Normal;
            bool priorityChanged = false;
            try
            {
                try
                {
                    originalPriority = thread.Priority;
                    thread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                }
                catch (Exception) { }

                CancellationToken token = result.Cancellation.Token;
                token.ThrowIfCancellationRequested();
                GuideData guide = result.Guide;
                IGuideShape shape = ShapeFactory.Adopt(guide);
                shape.RecalculatePhantomPoints();
                int scale = guide.VoxelScale;
                List<VoxelPosition> voxels = guide.IsWireframe
                    ? ShapeWireframe.GetVoxelPositions(shape, scale)
                    : shape.GetVoxelPositions(scale, guide.IsFilled);
                token.ThrowIfCancellationRequested();
                if (guide.Divisions > 1)
                    DivisionMarks.Apply(voxels, shape.SampleCurve(128), guide.Divisions, scale);

                result.Shape = shape;
                result.VoxelCount = voxels.Count;
                result.Extent = GuideMeshBuilder.MeasureShapeExtent(shape, scale);
                result.Origin = ComputeOrigin(shape.ControlPoints);
                result.MetadataReady = true;
                if (result.IsMaterialization)
                {
                    ProduceGrabMaterializationMeshes(
                        guide, shape, voxels, result.GrabbedIndex, result.Origin,
                        privateAnchors, result.ReadyMeshes, result.CleanReadyMeshes, token);
                }
                else
                {
                    result.Voxels = voxels;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                result.Error = e;
            }
            finally
            {
                if (result.ReadyMeshes != null && !result.ReadyMeshes.IsAddingCompleted)
                    result.ReadyMeshes.CompleteAdding();
                if (result.CleanReadyMeshes != null && !result.CleanReadyMeshes.IsAddingCompleted)
                    result.CleanReadyMeshes.CompleteAdding();
                result.Completed = true;
                if (priorityChanged)
                {
                    try { thread.Priority = originalPriority; }
                    catch (Exception) { }
                }
            }
        }

        private static void ProduceGrabMaterializationMeshes(
            GuideData guide, IGuideShape shape, List<VoxelPosition> voxels,
            int grabbedIndex, Vec3d origin, bool privateAnchors,
            BlockingCollection<MeshData> readyMeshes,
            BlockingCollection<MeshData> cleanMeshes, CancellationToken token,
            System.Func<int, int, int, bool> occupancyProbe = null)
        {
            Vec3d grabbed = grabbedIndex >= 0 && grabbedIndex < shape.ControlPoints.Count
                ? shape.ControlPoints[grabbedIndex]?.WorldPosition : null;
            var occupancy = new HashSet<(int, int, int)>(voxels.Count);
            int minimumY = int.MaxValue;
            for (int i = 0; i < voxels.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                VoxelPosition voxel = voxels[i];
                occupancy.Add((voxel.X, voxel.Y, voxel.Z));
                if (voxel.Y < minimumY) minimumY = voxel.Y;
            }

            var ordered = new List<VoxelPosition>(voxels);
            ordered.Sort((a, b) =>
            {
                int byX = a.X.CompareTo(b.X);
                if (byX != 0) return byX;
                int byY = a.Y.CompareTo(b.Y);
                return byY != 0 ? byY : a.Z.CompareTo(b.Z);
            });
            int cleanCursor = 0;

            OrganicVoxelGrowth.GrowBatches(
                voxels, guide.VoxelScale, MaterializationTargetVoxelsPerBatch,
                MaterializationMinimumBatches, MaterializationMaximumBatches, token,
                batch =>
            {
                token.ThrowIfCancellationRequested();
                if (batch == null || batch.Count == 0) return;

                int cleanCount = Math.Min(batch.Count, ordered.Count - cleanCursor);
                var cleanBatch = ordered.GetRange(cleanCursor, cleanCount);
                cleanCursor += cleanCount;
                var cleanOptions = new GuideMeshOptions
                {
                    Scale = guide.VoxelScale,
                    Mode = ProjectionMode.Volumetric,
                    Plane = guide.Plane,
                    Origin = origin,
                    Hidden = guide.IsHidden,
                    GrabbedPoint = grabbed,
                    PrivateAnchors = privateAnchors,
                    Occupancy = occupancy,
                    MinimumVoxelY = minimumY,
                    // Only the CLEAN mesh is recoloured. The preview batches below are the growth
                    // animation — transient, replaced within seconds, and not worth the world reads.
                    OccupancyProbe = occupancyProbe
                };
                AssignAnchors(shape.ControlPoints, cleanOptions);
                cleanMeshes.Add(GuideMeshBuilder.Build(cleanBatch, cleanOptions), token);

                var previewOptions = new GuideMeshOptions
                {
                    Scale = guide.VoxelScale,
                    Mode = ProjectionMode.Volumetric,
                    Plane = guide.Plane,
                    Origin = origin,
                    Hidden = guide.IsHidden,
                    GrabbedPoint = grabbed,
                    PrivateAnchors = privateAnchors,
                    Occupancy = occupancy
                };
                AssignAnchors(shape.ControlPoints, previewOptions);
                readyMeshes.Add(GuideMeshBuilder.Build(batch, previewOptions), token);
            });
        }

        private void FinishGrabRefinement(GrabBuildResult result)
        {
            lock (_grabWorkGate) _grabWorkBusy = false;
            if (_disposed || result == null) return;
            bool belongsToActive = ReferenceEquals(_activeGrabBuild, result);
            if (result.Error != null)
            {
                _capi.Logger.Warning("[Layout] Grab refinement failed: {0}", result.Error.Message);
                if (belongsToActive) _activeGrabBuild = null;
                result.CompletionHandled = true;
                return;
            }
            if (result.Cancellation?.IsCancellationRequested == true)
            {
                if (belongsToActive) _activeGrabBuild = null;
                result.CompletionHandled = true;
                return;
            }
            if (result.Generation != _grabGeneration || result.GuideId != _grabbedGuide
                || result.Fingerprint != _grabPoseFingerprint)
            {
                if (belongsToActive) _activeGrabBuild = null;
                result.CompletionHandled = true;
                return;
            }

            _grabExtent = result.Extent;
            if (result.IsMaterialization)
            {
                _grabRefinedFingerprint = result.Fingerprint;
                result.CompletionHandled = true;
                return;
            }

            if (belongsToActive)
            {
                List<VoxelPosition> voxels = result.Voxels ?? new List<VoxelPosition>();
                int slabSide = 0;
                voxels = FlattenToPlaneLayer(voxels, result.Guide.Plane,
                    result.Guide.VoxelScale, out slabSide, out _);
                Vec3d grabbed = result.GrabbedIndex >= 0
                    && result.GrabbedIndex < result.Shape.ControlPoints.Count
                    ? result.Shape.ControlPoints[result.GrabbedIndex]?.WorldPosition : null;
                var options = new GuideMeshOptions
                {
                    Scale = result.Guide.VoxelScale,
                    Mode = ProjectionMode.Volumetric,
                    Plane = result.Guide.Plane,
                    Origin = result.Origin,
                    Hidden = result.Guide.IsHidden,
                    PlaneInset = SurfacePlaneInset,
                    SurfaceSlabThickness = SurfaceSlabThicknessWorld,
                    SurfaceSlabSide = slabSide,
                    GrabbedPoint = grabbed,
                    PrivateAnchors = _network.ServerLayoutAvailable
                        && _network.IsLocalGuide(result.GuideId)
                };
                AssignAnchors(result.Shape.ControlPoints, options);
                UploadOrReplaceGrab(result.GuideId,
                    GuideMeshBuilder.Build(voxels, options), result.Origin);
            }
            _grabRefinedFingerprint = result.Fingerprint;
            if (belongsToActive) _activeGrabBuild = null;
            result.CompletionHandled = true;
        }

        private bool StartGrabMaterialization(GrabBuildResult build)
        {
            if (build == null || !_guideMeshes.TryGetValue(build.GuideId, out GuideMesh mesh))
            {
                return false;
            }
            DeleteGrabMeshes(mesh);
            _grabMaterializationGuide = build.GuideId;
            _grabMaterializationOrigin = build.Origin;
            _lastGrabMaterializationUploadMs = 0;
            _grabCleanSwapReadyMs = -1;
            _grabMaterializationStarted = true;
            return true;
        }

        private void AdvanceGrabMaterialization(bool force = false)
        {
            GrabBuildResult build = _activeGrabBuild;
            if (build == null || !build.IsMaterialization) return;
            if (build.Cancellation?.IsCancellationRequested == true
                || build.Generation != _grabGeneration || build.GuideId != _grabbedGuide
                || build.Fingerprint != _grabPoseFingerprint)
            {
                build.Cancellation?.Cancel();
                _activeGrabBuild = null;
                _grabMaterializationStarted = false;
                return;
            }

            BlockingCollection<MeshData> ready = build.ReadyMeshes;
            BlockingCollection<MeshData> cleanReady = build.CleanReadyMeshes;
            if (ready == null || cleanReady == null) return;
            if (!_grabMaterializationStarted)
            {
                if (!build.MetadataReady || ready.Count == 0)
                {
                    if (build.Completed && build.CompletionHandled
                        && ready.IsCompleted && cleanReady.IsCompleted)
                        _activeGrabBuild = null;
                    return;
                }
                if (!StartGrabMaterialization(build))
                {
                    build.Cancellation?.Cancel();
                    _activeGrabBuild = null;
                    return;
                }
            }
            if (!_guideMeshes.TryGetValue(_grabMaterializationGuide, out GuideMesh mesh))
            {
                build.Cancellation?.Cancel();
                _activeGrabBuild = null;
                _grabMaterializationStarted = false;
                return;
            }

            long now = _capi.World?.ElapsedMilliseconds ?? 0;
            if (build.Completed && build.CompletionHandled
                && ready.IsCompleted && ready.Count == 0
                && cleanReady.IsCompleted && cleanReady.Count == 0)
            {
                if (!CleanSwapDelayElapsed(ref _grabCleanSwapReadyMs, now)) return;
                CompleteGrabMaterialization(mesh);
                return;
            }

            int interval = MaterializationUploadInterval(build.VoxelCount);
            if (!force && _lastGrabMaterializationUploadMs != 0
                && now - _lastGrabMaterializationUploadMs < interval) return;

            bool uploadedAny = false;
            if (cleanReady.TryTake(out MeshData cleanData))
            {
                MeshRef uploadedClean = UploadTrackedMesh(cleanData, build.Guide?.VoxelScale ?? 0);
                if (mesh.GrabCleanRef == null)
                    mesh.GrabCleanRef = uploadedClean;
                else
                    mesh.GrabCleanAuxiliary.Add(uploadedClean);
                uploadedAny = true;
            }
            if (ready.TryTake(out MeshData next))
            {
                MeshRef uploaded = UploadTrackedMesh(next, build.Guide?.VoxelScale ?? 0);
                if (mesh.GrabRef == null)
                {
                    mesh.GrabRef = uploaded;
                    mesh.GrabOrigin = _grabMaterializationOrigin;
                }
                else
                {
                    mesh.GrabAuxiliary.Add(uploaded);
                }
                uploadedAny = true;
            }
            if (!uploadedAny)
            {
                if (build.Completed && build.CompletionHandled
                    && ready.IsCompleted && cleanReady.IsCompleted)
                {
                    if (!CleanSwapDelayElapsed(ref _grabCleanSwapReadyMs, now)) return;
                    CompleteGrabMaterialization(mesh);
                }
                return;
            }

            _lastGrabMaterializationUploadMs = now;
            if (build.Completed && build.CompletionHandled
                && ready.IsCompleted && ready.Count == 0
                && cleanReady.IsCompleted && cleanReady.Count == 0)
            {
                if (!CleanSwapDelayElapsed(ref _grabCleanSwapReadyMs, now)) return;
                CompleteGrabMaterialization(mesh);
            }
        }

        private void CompleteGrabMaterialization(GuideMesh mesh)
        {
            if (mesh?.GrabCleanRef == null) return;
            if (mesh.GrabRef != null) _retiredMaterializationMeshes.Enqueue(mesh.GrabRef);
            RetireMeshList(mesh.GrabAuxiliary);
            mesh.GrabRef = mesh.GrabCleanRef;
            mesh.GrabCleanRef = null;
            mesh.GrabOrigin = _grabMaterializationOrigin;
            mesh.GrabAuxiliary.AddRange(mesh.GrabCleanAuxiliary);
            mesh.GrabCleanAuxiliary.Clear();

            if (_releasedGrabPending)
            {
                _releasedGrabVisualComplete = true;
                TryFinalizeReleasedGrab();
            }
            _activeGrabBuild = null;
            _grabMaterializationStarted = false;
            _grabCleanSwapReadyMs = -1;
        }

        private void CompleteReleasedGrabAsOrdinary(GuideData guide)
        {
            Guid id = _grabbedGuide;
            _grabbedGuide = Guid.Empty;
            _grabbedIndex = -1;
            _grabAdaptiveScale = 0;
            _grabHealthyUpdates = 0;
            _grabExtent = GuideExtent.Empty;
            ResetGrabRefinementState();
            DeleteGrabMeshes(id);
            RebuildGuide(guide);
        }

        /// <summary>
        /// Promotes a released sculpt's bounded materialization into the guide's settled visual. Old batches
        /// retire over subsequent frames, so even the final swap has a fixed main-thread cost.
        /// </summary>
        private void TryFinalizeReleasedGrab()
        {
            if (!_releasedGrabPending || !_releasedGrabAuthorityConfirmed
                || !_releasedGrabVisualComplete) return;

            Guid id = _grabbedGuide;
            if (!_guideMeshes.TryGetValue(id, out GuideMesh mesh) || mesh.GrabRef == null) return;

            if (mesh.Ref != null) _retiredMaterializationMeshes.Enqueue(mesh.Ref);
            RetireMeshList(mesh.Auxiliary);
            mesh.Ref = mesh.GrabRef;
            mesh.Origin = mesh.GrabOrigin;
            mesh.Auxiliary.AddRange(mesh.GrabAuxiliary);
            mesh.GrabRef = null;
            mesh.GrabOrigin = null;
            mesh.GrabAuxiliary.Clear();

            _activeGrabBuild = null;
            _grabbedGuide = Guid.Empty;
            _grabbedIndex = -1;
            _grabGeneration++;
            _grabPoseFingerprint = 0;
            _grabRefinedFingerprint = 0;
            _grabLastMotionMs = 0;
            _grabMaterializationGuide = Guid.Empty;
            _grabMaterializationOrigin = null;
            _grabMaterializationStarted = false;
            _lastGrabMaterializationUploadMs = 0;
            _grabCleanSwapReadyMs = -1;
            _releasedGrabPending = false;
            _releasedGrabAuthorityConfirmed = false;
            _releasedGrabVisualComplete = false;
            _releasedGrabFingerprint = 0;
        }

        private void ResetGrabRefinementState()
        {
            _activeGrabBuild?.Cancellation?.Cancel();
            _activeGrabBuild = null;
            _grabGeneration++;
            _grabPoseFingerprint = 0;
            _grabRefinedFingerprint = 0;
            _grabLastMotionMs = 0;
            _grabMaterializationGuide = Guid.Empty;
            _grabMaterializationOrigin = null;
            _grabMaterializationStarted = false;
            _lastGrabMaterializationUploadMs = 0;
            _grabCleanSwapReadyMs = -1;
            _releasedGrabPending = false;
            _releasedGrabAuthorityConfirmed = false;
            _releasedGrabVisualComplete = false;
            _releasedGrabFingerprint = 0;
        }

        private static int NextCoarserScale(int scale)
        {
            for (int i = 0; i < GuideData.ValidVoxelScales.Length; i++)
                if (GuideData.ValidVoxelScales[i] > scale) return GuideData.ValidVoxelScales[i];
            return GuideData.ValidVoxelScales[GuideData.ValidVoxelScales.Length - 1];
        }

        private static int NextFinerScale(int scale, int targetScale)
        {
            if (scale <= targetScale) return targetScale;
            for (int i = GuideData.ValidVoxelScales.Length - 1; i >= 0; i--)
                if (GuideData.ValidVoxelScales[i] < scale
                    && GuideData.ValidVoxelScales[i] >= targetScale)
                    return GuideData.ValidVoxelScales[i];
            return targetScale;
        }

        // The world position of this guide's grabbed control point, or null if none is grabbed on it.
        private Vec3d ResolveGrabbedPoint(GuideData guide, List<ControlPoint> points)
        {
            if (guide.Id != _grabbedGuide) return null;
            if (_grabbedIndex < 0 || _grabbedIndex >= points.Count) return null;
            ControlPoint cp = points[_grabbedIndex];
            return cp?.IsPhantom == false ? cp.WorldPosition : null; // phantoms are never grabbed
        }

        // Feed the builder the start (first) and far (last) anchor so it can apply the active ownership
        // palette's coplanarity shade. Anchors are identified by role, not fixed indices.
        private static void AssignAnchors(List<ControlPoint> points, GuideMeshOptions options)
        {
            ControlPoint start = null, far = null;
            for (int i = 0; i < points.Count; i++)
            {
                ControlPoint cp = points[i];
                if (cp == null || !cp.IsAnchor) continue;
                if (start == null) start = cp;
                far = cp;
            }
            if (start != null && far != null && !ReferenceEquals(start, far))
            {
                options.StartAnchor = start.WorldPosition;
                options.FarAnchor = far.WorldPosition;
            }
        }

        // A camera-independent origin (floored to a block) near the geometry, so mesh vertices stay small and
        // the mesh need not rebuild when the camera moves — only its model matrix does.
        private static Vec3d ComputeOrigin(List<ControlPoint> points)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                ControlPoint cp = points[i];
                if (cp?.WorldPosition == null) continue;
                Vec3d p = cp.WorldPosition;
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
            }
            if (minX == double.MaxValue) return new Vec3d(); // no usable points
            return new Vec3d(Math.Floor(minX), Math.Floor(minY), Math.Floor(minZ));
        }

        private static void ComputeCullBounds(
            IGuideShape shape, int scale, out Vec3d centre, out double radius)
        {
            centre = null;
            radius = 0;
            if (shape == null) return;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            void Include(Vec3d point)
            {
                if (point == null) return;
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
                minZ = Math.Min(minZ, point.Z); maxZ = Math.Max(maxZ, point.Z);
            }

            List<Vec3d> curve = shape.SampleCurve(128);
            if (curve != null)
                for (int i = 0; i < curve.Count; i++) Include(curve[i]);
            if (shape.ControlPoints != null)
                for (int i = 0; i < shape.ControlPoints.Count; i++)
                    Include(shape.ControlPoints[i]?.WorldPosition);
            if (minX == double.MaxValue) return;

            centre = new Vec3d(
                (minX + maxX) * 0.5,
                (minY + maxY) * 0.5,
                (minZ + maxZ) * 0.5);
            double hx = (maxX - minX) * 0.5;
            double hy = (maxY - minY) * 0.5;
            double hz = (maxZ - minZ) * 0.5;
            radius = Math.Sqrt(hx * hx + hy * hy + hz * hz)
                + Math.Max(1, scale) / 16.0;
        }

        private void SetGuideCullBounds(Guid id, Vec3d centre, double radius)
        {
            if (!_guideMeshes.TryGetValue(id, out GuideMesh mesh)) return;
            mesh.CullCenter = centre;
            mesh.CullRadius = radius;
        }

        private void SetDraftCullBounds(IGuideShape shape, int scale)
        {
            ComputeCullBounds(shape, scale, out _draftCullCenter, out _draftCullRadius);
        }

        // Smallest valid scale >= the guide's own scale whose voxel count fits the preview cap; the coarsest
        // (16) if even that exceeds it. Coarsening only ever makes the preview cheaper, never finer.
        private static int ChooseRenderScale(IGuideShape shape, int guideScale, bool filled = false)
        {
            int[] scales = GuideData.ValidVoxelScales;
            int chosen = guideScale;
            for (int i = 0; i < scales.Length; i++)
            {
                if (scales[i] < guideScale) continue;
                chosen = scales[i];
                if (GuideShapeVoxelCounting.CountUpTo(
                    shape, scales[i], filled, PreviewFullResVoxelCap) <= PreviewFullResVoxelCap)
                    return scales[i];
            }
            return chosen;
        }

        /// <param name="voxelScale">
        /// The scale the mesh was built at, so the voxel-boundary frame can align its grid. 0 means "no
        /// voxel lattice" and disables the frame for that mesh — the safe default, since an unknown scale
        /// drawing a wrong-sized grid would be worse than drawing none.
        /// </param>
        private MeshRef UploadTrackedMesh(MeshData data, int voxelScale = 0)
        {
            MeshRef mesh = _capi.Render.UploadMesh(data);
            if (mesh != null)
                _meshCosts[mesh] = MeshCost.Measure(data, voxelScale);
            return mesh;
        }

        private MeshRef UploadTrackedMesh(MeshData data, GuideMeshOptions options) =>
            UploadTrackedMesh(data, options?.Scale ?? 0);

        private void DeleteTrackedMesh(MeshRef mesh)
        {
            if (mesh == null) return;
            _meshCosts.Remove(mesh);
            _capi.Render.DeleteMesh(mesh);
        }

        /// <param name="voxelScale">
        /// The scale this mesh was actually built at, when it differs from the guide's own. Recorded so the
        /// voxel-boundary frame draws the right grid: a coarse wireframe scaffold is NOT at the guide's
        /// scale, and leaving this 0 made the frame fall back to the guide's, painting a 1/16 grid over
        /// block-sized cubes and reading as "fine voxels that are somehow a whole block across".
        /// </param>
        private void UploadOrReplace(Guid id, MeshData data, Vec3d origin, int voxelScale = 0)
        {
            if (_guideMeshes.TryGetValue(id, out GuideMesh existing))
            {
                DeleteTrackedMesh(existing.Ref);
                DeleteAuxiliaryMeshes(existing);
                existing.Ref = UploadTrackedMesh(data, voxelScale);
                existing.Origin = origin;
            }
            else
            {
                _guideMeshes[id] = new GuideMesh
                {
                    Ref = UploadTrackedMesh(data, voxelScale),
                    Origin = origin
                };
            }
        }

        private void UploadOrReplaceGrab(Guid id, MeshData data, Vec3d origin)
        {
            if (!_guideMeshes.TryGetValue(id, out GuideMesh mesh))
            {
                mesh = new GuideMesh();
                _guideMeshes[id] = mesh;
            }

            MeshRef replacement = UploadTrackedMesh(data);
            DeleteTrackedMesh(mesh.GrabRef);
            DeleteMeshList(mesh.GrabAuxiliary);
            DeleteTrackedMesh(mesh.GrabCleanRef);
            mesh.GrabCleanRef = null;
            DeleteMeshList(mesh.GrabCleanAuxiliary);
            mesh.GrabRef = replacement;
            mesh.GrabOrigin = origin;
        }

        private void RemoveGuideMesh(Guid id)
        {
            _deferredSurface.Remove(id);
            _deferredOccupancy.Remove(id);
            if (_settledMaterializations.TryGetValue(id, out SettledMaterializationBuild build))
            {
                build.Cancellation?.Cancel();
                _settledMaterializations.Remove(id);
            }
            if (_guideMeshes.TryGetValue(id, out GuideMesh gm))
            {
                DeleteTrackedMesh(gm.Ref);
                DeleteAuxiliaryMeshes(gm);
                DeleteGrabMeshes(gm);
                DeleteTransitionMeshes(gm);
                _guideMeshes.Remove(id);
            }
        }

        private void DeleteGrabMeshes(Guid id)
        {
            if (_guideMeshes.TryGetValue(id, out GuideMesh mesh)) DeleteGrabMeshes(mesh);
        }

        private void DeleteGrabMeshes(GuideMesh mesh)
        {
            if (mesh == null) return;
            DeleteTrackedMesh(mesh.GrabRef);
            mesh.GrabRef = null;
            DeleteTrackedMesh(mesh.GrabCleanRef);
            mesh.GrabCleanRef = null;
            mesh.GrabOrigin = null;
            DeleteMeshList(mesh.GrabAuxiliary);
            DeleteMeshList(mesh.GrabCleanAuxiliary);
        }

        private void DeleteTransitionMeshes(GuideMesh mesh)
        {
            if (mesh == null) return;
            DeleteTrackedMesh(mesh.TransitionRef);
            mesh.TransitionRef = null;
            if (mesh.TransitionCleanRef != null)
                DeleteTrackedMesh(mesh.TransitionCleanRef);
            mesh.TransitionCleanRef = null;
            mesh.TransitionOrigin = null;
            DeleteMeshList(mesh.TransitionAuxiliary);
            DeleteMeshList(mesh.TransitionCleanAuxiliary);
        }

        private void DeleteAuxiliaryMeshes(GuideMesh mesh)
        {
            if (mesh == null) return;
            DeleteMeshList(mesh.Auxiliary);
        }

        private void DeleteMeshList(List<MeshRef> meshes)
        {
            if (meshes == null) return;
            for (int i = 0; i < meshes.Count; i++)
                DeleteTrackedMesh(meshes[i]);
            meshes.Clear();
        }

        private void RetireMeshList(List<MeshRef> meshes)
        {
            if (meshes == null) return;
            for (int i = 0; i < meshes.Count; i++)
                if (meshes[i] != null) _retiredMaterializationMeshes.Enqueue(meshes[i]);
            meshes.Clear();
        }

        private void AdvanceRetiredMaterializationDeletes()
        {
            int budget = _smoothedFrameMilliseconds > 28.0
                ? 1 : RetiredMaterializationDeletesPerFrame;
            while (budget-- > 0 && _retiredMaterializationMeshes.Count > 0)
                DeleteTrackedMesh(_retiredMaterializationMeshes.Dequeue());
        }

        private static ulong RenderFingerprint(GuideData guide)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                void Mix(long value) { h ^= (ulong)value; h *= 1099511628211UL; }

                Mix((long)guide.ShapeType); Mix((long)guide.Constraint); Mix((long)guide.ShapePlaneAxis);
                Mix(guide.VoxelScale); Mix(guide.IsHidden ? 1 : 0); Mix((long)guide.Projection);
                Mix((long)guide.Plane.FlattenedAxis); Mix(guide.Plane.PlaneOffset);
                Mix(guide.IsFilled ? 1 : 0); Mix(guide.Divisions); Mix(guide.Sides);
                Mix(guide.IsWireframe ? 1 : 0);
                Mix(guide.FlatSideAligned ? 1 : 0); Mix(guide.IsClosed ? 1 : 0);

                List<ControlPoint> points = guide.ControlPoints;
                Mix(points?.Count ?? 0);
                if (points != null)
                {
                    for (int i = 0; i < points.Count; i++)
                    {
                        ControlPoint point = points[i];
                        Vec3d p = point?.WorldPosition;
                        Mix(p == null ? 0 : BitConverter.DoubleToInt64Bits(p.X));
                        Mix(p == null ? 0 : BitConverter.DoubleToInt64Bits(p.Y));
                        Mix(p == null ? 0 : BitConverter.DoubleToInt64Bits(p.Z));
                        int flags = point == null ? 0
                            : (point.IsLocked ? 1 : 0)
                            | (point.IsPhantom ? 2 : 0)
                            | (point.IsAnchor ? 4 : 0)
                            | (point.IsPrimary ? 8 : 0)
                            | (point.IsLockMarker ? 16 : 0);
                        Mix(flags);
                    }
                }
                return h;
            }
        }

        private void EnsureMarkerMesh()
        {
            if (_markerMesh != null) return;
            var one = new List<VoxelPosition> { new VoxelPosition(0, 0, 0, VoxelRenderType.Anchor) };
            MeshData data = GuideMeshBuilder.Build(one, new GuideMeshOptions { Scale = DraftMarkerScale });
            _markerMesh = UploadTrackedMesh(data);
        }

        private void EnsureWhiteTexture()
        {
            if (_whiteTex != null && _whiteTex.TextureId != 0) return;

            // Primary: upload 2×2 white from raw pixels (channel-order-proof, no asset path involved).
            if (_whiteTex == null) _whiteTex = new LoadedTexture(_capi) { Width = 2, Height = 2 };
            try
            {
                int[] pixels = new int[4];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = unchecked((int)0xFFFFFFFF);
                _capi.Render.LoadOrUpdateTextureFromBgra(pixels, false, 0, ref _whiteTex);
            }
            catch (Exception e)
            {
                _capi.Logger.Warning("[Layout] Raw white-texture upload threw: {0}", e.Message);
            }

            // Fallback: the shipped asset (assets/layout/textures/white.png), trying both known
            // GetOrLoadTexture path conventions. _whiteTexBorrowed marks it engine-cached: never dispose it.
            if (_whiteTex.TextureId == 0)
            {
                foreach (string path in new[] { "textures/white.png", "white.png" })
                {
                    try
                    {
                        var tex = new LoadedTexture(_capi);
                        _capi.Render.GetOrLoadTexture(new AssetLocation("layout", path), ref tex);
                        if (tex.TextureId != 0)
                        {
                            _whiteTex = tex;
                            _whiteTexBorrowed = true;
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        _capi.Logger.Warning("[Layout] GetOrLoadTexture('layout:{0}') threw: {1}", path, e.Message);
                    }
                }
            }

            if (!_whiteTexLogged)
            {
                _whiteTexLogged = true;
                _capi.Logger.Notification("[Layout] Guide renderer white texture id: {0}{1}",
                    _whiteTex.TextureId, _whiteTexBorrowed ? " (from asset fallback)" : " (raw upload)");
            }
        }

        // -- Disposal ------------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _network.GuideAddedOrUpdated -= OnGuideAddedOrUpdated;
            _network.GuideRemoved -= OnGuideRemoved;
            _network.GuidesBulkSynced -= OnGuidesBulkSynced;
            _network.RemoteDraftAnchorChanged -= OnRemoteDraftAnchorChanged;
            _network.RemoteDraftAnchorRemoved -= OnRemoteDraftAnchorRemoved;
            _network.PlacementRejected -= OnPlacementRejected;

            _capi.Event.UnregisterRenderer(this, RenderStage);
            _capi.Event.UnregisterGameTickListener(_reprobeListenerId);
            SetOccupancySubscribed(false);   // drops the BlockChanged hook and its tick
            _capi.Event.ReloadShader -= LoadCustomShader;
            _guideShader?.Dispose();
            _guideShader = null;
            _deferredSurface.Clear();
            _deferredOccupancy.Clear();

            if (!_whiteTexBorrowed) _whiteTex?.Dispose();   // engine-cached fallback textures stay alive
            _whiteTex = null;

            if (_draftPreviewMesh != null)
            {
                DeleteTrackedMesh(_draftPreviewMesh);
                _draftPreviewMesh = null;
            }
            _activeDraftBuild?.Cancellation?.Cancel();
            _activeDraftBuild = null;
            _activeGrabBuild?.Cancellation?.Cancel();
            _activeGrabBuild = null;
            CancelAllSettledMaterializations();
            ClearDraftPrecisionMeshes();
            ClearDraftMaterialization();
            DiscardPendingPlacementVisual();

            foreach (GuideMesh gm in _guideMeshes.Values)
            {
                DeleteTrackedMesh(gm.Ref);
                DeleteAuxiliaryMeshes(gm);
                DeleteGrabMeshes(gm);
                DeleteTransitionMeshes(gm);
            }
            _guideMeshes.Clear();
            while (_retiredMaterializationMeshes.Count > 0)
                DeleteTrackedMesh(_retiredMaterializationMeshes.Dequeue());
            _remoteAnchors.Clear();

            if (_markerMesh != null)
            {
                DeleteTrackedMesh(_markerMesh);
                _markerMesh = null;
            }
            _meshCosts.Clear();
        }
    }
}
