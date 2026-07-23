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
        private readonly Dictionary<MeshRef, int> _meshTriangleCounts = new Dictionary<MeshRef, int>();

        // Surface guides whose air-side probe hit UNLOADED chunks (the world-load / approach-from-afar race):
        // their decal side is provisional and may render behind the block face. A low-frequency tick re-probes
        // and rebuilds each once its neighbourhood loads — fixing the "guide sinks behind the face after
        // reload" bug with no wire/persistence change. A guide sits here only while its chunk is unloaded.
        private readonly HashSet<Guid> _deferredSurface = new HashSet<Guid>();
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

        private sealed class RenderFrameStats
        {
            public int PlacedGuides;
            public int VisiblePlacedGuides;
            public int DistanceCulledGuides;
            public int FrustumCulledGuides;
            public int MeshDrawCalls;
            public long SubmittedTriangles;
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

        public string DescribeRenderStats()
        {
            if (!RenderingEnabled)
                return "Layout render stats: rendering is off.";

            RenderFrameStats stats = _lastRenderStats;
            return string.Format(
                "Layout render stats (last frame): {0}/{1} placed guide(s) visible; "
                + "{2} distance-culled; {3} off-screen; {4} mesh batch(es) and about {5:N0} triangle(s) "
                + "submitted. Extras drawn: {6} draft batch(es), {7} pending batch(es), "
                + "{8}/{9} remote marker(s). Smoothed frame time: {10:0.0} ms.",
                stats.VisiblePlacedGuides, stats.PlacedGuides,
                stats.DistanceCulledGuides, stats.FrustumCulledGuides,
                stats.MeshDrawCalls, stats.SubmittedTriangles,
                stats.VisibleDraftBatches, stats.VisiblePendingBatches,
                stats.VisibleRemoteMarkers, stats.TotalRemoteMarkers,
                SmoothedFrameMilliseconds);
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

            // PreparedStandardShader (NOT raw StandardShader.Use()): it configures ALL the standard
            // shader's uniforms — shadow map, ambient, fog — for the given world position and calls Use().
            // Module-7 in-game finding: with raw Use(), the unset shadow/ambient uniforms multiplied every
            // fragment to black in the Opaque stage. We then override the lighting inputs to full-bright so
            // the guide palette shows verbatim, day or night.
            IStandardShaderProgram prog = rpi.PreparedStandardShader(
                (int)camPos.X, (int)camPos.Y, (int)camPos.Z);
            prog.RgbaTint = new Vec4f(1f, 1f, 1f, 1f);
            prog.RgbaLightIn = new Vec4f(1f, 1f, 1f, 1f);   // full-bright: guide colours ignore block light
            prog.NormalShaded = 0;
            prog.ExtraGodray = 0f;
            prog.AddRenderFlags = 0;
            prog.Tex2D = _whiteTex.TextureId;               // real white texture — see _whiteTex remarks
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

            foreach (GuideMesh gm in _guideMeshes.Values)
            {
                MeshRef primary = gm.GrabRef ?? gm.TransitionRef ?? gm.Ref;
                Vec3d origin = gm.GrabRef != null ? gm.GrabOrigin
                    : gm.TransitionRef != null ? gm.TransitionOrigin : gm.Origin;
                if (primary == null || origin == null) continue;
                stats.PlacedGuides++;
                VisibilityResult visibility = ClassifyVisibility(
                    camPos, gm.CullCenter, gm.CullRadius, viewDistance, frustumCuller);
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
                SetModelMatrix(origin.X - camPos.X, origin.Y - camPos.Y, origin.Z - camPos.Z);
                prog.ModelMatrix = _modelMat;
                RenderMeshTracked(rpi, primary, stats);
                List<MeshRef> auxiliary = gm.GrabRef != null ? gm.GrabAuxiliary
                    : gm.TransitionRef != null ? gm.TransitionAuxiliary : gm.Auxiliary;
                for (int i = 0; i < auxiliary.Count; i++)
                    RenderMeshTracked(rpi, auxiliary[i], stats);
            }

            bool draftVisible = VisibleToPlayer(
                camPos, _draftCullCenter, _draftCullRadius, viewDistance, frustumCuller);
            if (_draftPreviewMesh != null && draftVisible)
            {
                SetModelMatrix(
                    _draftPreviewOrigin.X - camPos.X,
                    _draftPreviewOrigin.Y - camPos.Y,
                _draftPreviewOrigin.Z - camPos.Z);
                prog.ModelMatrix = _modelMat;
                RenderMeshTracked(rpi, _draftPreviewMesh, stats);
                stats.VisibleDraftBatches++;
            }

            for (int i = 0; draftVisible && i < _draftPrecisionMeshes.Count; i++)
            {
                SetModelMatrix(
                    _draftPreviewOrigin.X - camPos.X,
                    _draftPreviewOrigin.Y - camPos.Y,
                _draftPreviewOrigin.Z - camPos.Z);
                prog.ModelMatrix = _modelMat;
                RenderMeshTracked(rpi, _draftPrecisionMeshes[i], stats);
                stats.VisibleDraftBatches++;
            }

            for (int i = 0; draftVisible && i < _draftMaterializationMeshes.Count; i++)
            {
                SetModelMatrix(
                    _draftPreviewOrigin.X - camPos.X,
                    _draftPreviewOrigin.Y - camPos.Y,
                _draftPreviewOrigin.Z - camPos.Z);
                prog.ModelMatrix = _modelMat;
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
                prog.ModelMatrix = _modelMat;
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
                foreach (Vec3d anchor in _remoteAnchors.Values)
                {
                    if (!VisibleToPlayer(
                        camPos, anchor, DraftMarkerCullRadius, viewDistance, frustumCuller)) continue;
                    SetModelMatrix(
                        anchor.X - camPos.X - DraftMarkerHalf,
                        anchor.Y - camPos.Y - DraftMarkerHalf,
                    anchor.Z - camPos.Z - DraftMarkerHalf);
                    prog.ModelMatrix = _modelMat;
                    RenderMeshTracked(rpi, _markerMesh, stats);
                    stats.VisibleRemoteMarkers++;
                }
            }

            prog.Stop();
            rpi.GlEnableCullFace();
            rpi.GlToggleBlend(false);
            _lastRenderStats = stats;
        }

        private void SetModelMatrix(double dx, double dy, double dz)
        {
            // ANTI-Z-FIGHT NUDGE (Session-8): volumetric voxels sit flush against block faces and z-fought
            // slightly, exactly as Surface did. Per-cube shrinking is ruled out (Session-7 finding: all-axis
            // insets open visible gaps between neighbours), so instead the WHOLE mesh is pulled a hair toward
            // the camera each frame — the classic decal trick. It is view-dependent on purpose: whichever
            // face you are looking at is the face that gains depth separation, and glancing angles (where the
            // pull is least effective) are where z-fighting is least visible anyway. 3 mm is imperceptible as
            // displacement but far above depth-buffer precision. Applies to guides, ghost, and anchor dots
            // alike, since (dx,dy,dz) is always meshOrigin − cameraPos.
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len > 0.5) // skip when the camera is essentially inside the mesh origin
            {
                double k = CameraNudge / len;
                dx -= dx * k; dy -= dy * k; dz -= dz * k;
            }

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
            render.RenderMesh(mesh);
            stats.MeshDrawCalls++;
            if (_meshTriangleCounts.TryGetValue(mesh, out int triangles))
                stats.SubmittedTriangles += triangles;
        }

        // World-unit pull toward the camera applied to every guide mesh (see SetModelMatrix). Tune by eye.
        private const double CameraNudge = 0.003;

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

            if (TryStartSettledShellMaterialization(guide)) return;
            RebuildGuide(guide);
        }

        private void OnGuideRemoved(Guid id)
        {
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
                List<VoxelPosition> voxels = BuildWireframe(curve, movingScale);

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
                PrivateAnchors = privateAnchors,
                IsNeighborSolid = (_, _, _) => false
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
                    MinimumVoxelY = minimumY,
                    IsNeighborSolid = (_, _, _) => false
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
                    Occupancy = occupancy,
                    IsNeighborSolid = (_, _, _) => false
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
                    MinimumVoxelY = minimumY,
                    IsNeighborSolid = (_, _, _) => false
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
                    && _network.AuthorityMode == ClientAuthorityMode.Local,
                IsNeighborSolid = NeighborSolidProbe
            };
            AssignAnchors(result.Shape.ControlPoints, options);
            MeshData data = GuideMeshBuilder.Build(voxels, options);

            ReplaceDraftMesh(data, result.Origin);
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
                    && _network.AuthorityMode == ClientAuthorityMode.Local,
                IsNeighborSolid = NeighborSolidProbe
            };
            AssignAnchors(shape.ControlPoints, options);
            SetDraftCullBounds(shape, renderScale);
            ReplaceDraftMesh(GuideMeshBuilder.Build(voxels, options), origin);
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
                    && _network.AuthorityMode == ClientAuthorityMode.Local,
                IsNeighborSolid = NeighborSolidProbe
            };
            AssignAnchors(shape.ControlPoints, options);
            _draftPrecisionMeshes.Add(UploadTrackedMesh(GuideMeshBuilder.Build(voxels, options)));
            _draftPreviewOrigin = origin;
        }

        private void ReplaceDraftMesh(MeshData data, Vec3d origin)
        {
            ClearDraftPrecisionMeshes();
            ClearDraftMaterialization();
            MeshRef replacement = UploadTrackedMesh(data);
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
                MeshRef selectedScaleScaffold = UploadTrackedMesh(scaffoldData);
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
                _draftCleanMaterializationMeshes.Add(UploadTrackedMesh(cleanData));
                uploaded = true;
            }
            if (ready.TryTake(out MeshData previewData))
            {
                RemoveDraftScaffoldForOrganicGrowth(build);
                _draftMaterializationMeshes.Add(UploadTrackedMesh(previewData));
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
                List<VoxelPosition> voxels = BuildWireframe(curve, movingScale);
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
                MeshRef selectedScaleScaffold = UploadTrackedMesh(scaffoldData);
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
                pending.CleanMeshes.Add(UploadTrackedMesh(cleanData));
                uploadedPending = true;
            }
            if (ready.TryTake(out MeshData previewData))
            {
                if (build.Spec?.Settings.Wireframe != true)
                    DeletePendingProvisionalMeshes(pending);
                MeshRef uploadedPreview = UploadTrackedMesh(previewData);
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
                    && _network.AuthorityMode == ClientAuthorityMode.Local,
                IsNeighborSolid = NeighborSolidProbe
            };
            AssignAnchors(shape.ControlPoints, options);

            MeshData data = GuideMeshBuilder.Build(voxels, options);
            DeleteTrackedMesh(_draftPreviewMesh);
            _draftPreviewMesh = UploadTrackedMesh(data);
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
        // Cell-space (1/16) probe behind the mesh builder's z-fight clearances (0.2.16): is the world block
        // containing the neighbouring cell non-air? Same solidity semantics as the Surface probe below.
        // Unloaded chunks report solid (conservative — keep the clearance; the area is invisible anyway).
        private bool NeighborSolidProbe(int x16, int y16, int z16)
        {
            IBlockAccessor accessor = _capi.World?.BlockAccessor;
            if (accessor == null) return true;
            var pos = new BlockPos(x16 >> 4, y16 >> 4, z16 >> 4);
            if (accessor.GetChunkAtBlockPos(pos) == null) return true;
            var block = accessor.GetBlock(pos);
            return block != null && block.Id != 0;
        }

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
            if (_disposed || _deferredSurface.Count == 0) return;
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

            if (ready == null) return;
            foreach (Guid id in ready)
            {
                _deferredSurface.Remove(id);
                RebuildGuideById(id);   // re-probes; re-defers itself only if still unreliable
            }
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

        private void RebuildGuideById(Guid id)
        {
            if (_network.Guides.TryGetValue(id, out GuideData guide) && guide != null) RebuildGuide(guide);
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

            Task.Factory.StartNew(
                    () => BuildSettledShellMaterialization(build, privateAnchors),
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
            SettledMaterializationBuild build, bool privateAnchors)
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
                    build.ReadyMeshes, build.CleanReadyMeshes, token);
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
                RebuildGuide(live);
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
                    MeshRef clean = UploadTrackedMesh(cleanData);
                    if (mesh.TransitionCleanRef == null)
                        mesh.TransitionCleanRef = clean;
                    else
                        mesh.TransitionCleanAuxiliary.Add(clean);
                    uploaded = true;
                }
                if (ready.TryTake(out MeshData previewData))
                {
                    MeshRef preview = UploadTrackedMesh(previewData);
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
                RebuildGuideById(build.GuideId);
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

        private void RebuildGuide(GuideData guide)
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
                IsNeighborSolid = NeighborSolidProbe
            };
            AssignAnchors(points, options);

            MeshData data = GuideMeshBuilder.Build(voxels, options);
            if (locallyGrabbed) UploadOrReplaceGrab(guide.Id, data, origin);
            else UploadOrReplace(guide.Id, data, origin);
            if (_guideMeshes.TryGetValue(guide.Id, out GuideMesh rendered))
                rendered.RenderedWireframe = guide.IsWireframe;
            SetGuideCullBounds(guide.Id, cullCenter, cullRadius);
        }

        private void RebuildGrabWireframe(GuideData guide, IGuideShape shape, List<ControlPoint> points)
        {
            var timer = Stopwatch.StartNew();
            int selectedScale = guide.VoxelScale;
            int minimumScale = Math.Max(selectedScale,
                _grabAdaptiveScale > 0 ? _grabAdaptiveScale : selectedScale);
            List<Vec3d> curve = shape.SampleCurve(128);
            int movingScale = ChooseMovingWireframeScale(curve, minimumScale);
            List<VoxelPosition> coarse = BuildWireframe(curve, movingScale);

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
                    mesh.GrabAuxiliary.Add(UploadTrackedMesh(GuideMeshBuilder.Build(band, bandOptions)));
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
                PrivateAnchors = _network.ServerLayoutAvailable && _network.IsLocalGuide(guide.Id),
                IsNeighborSolid = NeighborSolidProbe
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
            BlockingCollection<MeshData> cleanMeshes, CancellationToken token)
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
                    IsNeighborSolid = (_, _, _) => false
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
                    Occupancy = occupancy,
                    IsNeighborSolid = (_, _, _) => false
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
                        && _network.IsLocalGuide(result.GuideId),
                    IsNeighborSolid = NeighborSolidProbe
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
                MeshRef uploadedClean = UploadTrackedMesh(cleanData);
                if (mesh.GrabCleanRef == null)
                    mesh.GrabCleanRef = uploadedClean;
                else
                    mesh.GrabCleanAuxiliary.Add(uploadedClean);
                uploadedAny = true;
            }
            if (ready.TryTake(out MeshData next))
            {
                MeshRef uploaded = UploadTrackedMesh(next);
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

        private MeshRef UploadTrackedMesh(MeshData data)
        {
            MeshRef mesh = _capi.Render.UploadMesh(data);
            if (mesh != null)
                _meshTriangleCounts[mesh] = Math.Max(0, data?.IndicesCount ?? 0) / 3;
            return mesh;
        }

        private void DeleteTrackedMesh(MeshRef mesh)
        {
            if (mesh == null) return;
            _meshTriangleCounts.Remove(mesh);
            _capi.Render.DeleteMesh(mesh);
        }

        private void UploadOrReplace(Guid id, MeshData data, Vec3d origin)
        {
            if (_guideMeshes.TryGetValue(id, out GuideMesh existing))
            {
                DeleteTrackedMesh(existing.Ref);
                DeleteAuxiliaryMeshes(existing);
                existing.Ref = UploadTrackedMesh(data);
                existing.Origin = origin;
            }
            else
            {
                _guideMeshes[id] = new GuideMesh { Ref = UploadTrackedMesh(data), Origin = origin };
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
            _deferredSurface.Clear();

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
            _meshTriangleCounts.Clear();
        }
    }
}
