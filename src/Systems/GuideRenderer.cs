using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;
using Layout.Network;

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

        // Above this voxel count the preview is coarsened (rendered at the next larger voxel scale) to keep the
        // mesh cheap. Purely a render optimisation; the guide's real scale and the HUD readout are unaffected.
        private const int PreviewFullResVoxelCap = 8000;

        // Remote draft-anchor marker: a small blue cube (a fraction of a block) centred on the anchor.
        private const int DraftMarkerScale = 4;                          // 1/16 units → 0.25-block cube
        private const double DraftMarkerHalf = DraftMarkerScale / 32.0;  // half its edge, for centring

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
        // server will build — including the Blue/Indigo far-foot coplanarity shade while aiming.
        private MeshRef _draftPreviewMesh;
        private Vec3d _draftPreviewOrigin;
        private Vec3d _previewStart, _previewEnd;
        private GuideRenderSettings _previewSettings;
        private GuideShapeType _previewShapeType = GuideShapeType.Arch;      // Session 8: shape is part of
        private ShapeConstraint _previewConstraint = ShapeConstraint.None;   //   the ghost's rebuild key
        private PlaneAxis _previewPlaneAxis = PlaneAxis.Y;
        private bool _hasPreviewKey;

        private readonly Dictionary<Guid, GuideMesh> _guideMeshes = new Dictionary<Guid, GuideMesh>();
        private readonly Dictionary<string, Vec3d> _remoteAnchors = new Dictionary<string, Vec3d>();

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

        private bool _disposed;

        /// <summary>A compiled guide mesh plus the world-space origin its vertices are relative to.</summary>
        private sealed class GuideMesh
        {
            public MeshRef Ref;
            public Vec3d Origin;
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

            _capi.Event.RegisterRenderer(this, RenderStage, "layout-guides");
            _reprobeListenerId = _capi.Event.RegisterGameTickListener(OnReprobeTick, ReprobeIntervalMs);

            // Pick up anything already synced before the renderer existed (e.g. tool equipped after join).
            RebuildAll();
        }

        // -- IRenderer -----------------------------------------------------------------------------

        /// <summary>Draw order within the stage. Mid-range is fine for translucent overlay geometry.</summary>
        public double RenderOrder => 0.5;

        /// <summary>Nominal range in blocks; the renderer itself draws every loaded guide (no per-guide cull).</summary>
        public int RenderRange => 128;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (_disposed || stage != RenderStage) return;
            if (_guideMeshes.Count == 0 && _remoteAnchors.Count == 0 && _draftPreviewMesh == null) return;

            IClientPlayer player = _capi.World?.Player;
            if (player?.Entity == null) return;
            Vec3d camPos = player.Entity.CameraPos;

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
                if (gm.Ref == null) continue;
                SetModelMatrix(gm.Origin.X - camPos.X, gm.Origin.Y - camPos.Y, gm.Origin.Z - camPos.Z);
                prog.ModelMatrix = _modelMat;
                rpi.RenderMesh(gm.Ref);
            }

            if (_draftPreviewMesh != null)
            {
                SetModelMatrix(
                    _draftPreviewOrigin.X - camPos.X,
                    _draftPreviewOrigin.Y - camPos.Y,
                    _draftPreviewOrigin.Z - camPos.Z);
                prog.ModelMatrix = _modelMat;
                rpi.RenderMesh(_draftPreviewMesh);
            }

            if (_markerMesh != null)
            {
                foreach (Vec3d anchor in _remoteAnchors.Values)
                {
                    SetModelMatrix(
                        anchor.X - camPos.X - DraftMarkerHalf,
                        anchor.Y - camPos.Y - DraftMarkerHalf,
                        anchor.Z - camPos.Z - DraftMarkerHalf);
                    prog.ModelMatrix = _modelMat;
                    rpi.RenderMesh(_markerMesh);
                }
            }

            prog.Stop();
            rpi.GlEnableCullFace();
            rpi.GlToggleBlend(false);
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

        // World-unit pull toward the camera applied to every guide mesh (see SetModelMatrix). Tune by eye.
        private const double CameraNudge = 0.003;

        // -- Network events (main thread) ----------------------------------------------------------

        private void OnGuideAddedOrUpdated(GuideData guide)
        {
            if (guide != null) RebuildGuide(guide);
        }

        private void OnGuideRemoved(Guid id) => RemoveGuideMesh(id);

        private void OnGuidesBulkSynced() => RebuildAll();

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
            _grabbedGuide = Guid.Empty;
            _grabbedIndex = -1;
            RebuildGuideById(was);
        }

        // -- Local draft preview (driven by the held tool, Module 7) --------------------------------

        /// <summary>
        /// Shows (or updates) the acting player's live draft ghost: the arch that WOULD be created from
        /// <paramref name="start"/> to <paramref name="end"/> with the given settings. Cheap to call every
        /// tick — rebuilds only when start/end/settings actually change. Cleared automatically when a real
        /// build lands via <see cref="ClearDraftPreview"/> from the controller.
        /// </summary>
        public void SetDraftPreview(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType = GuideShapeType.Arch,
            ShapeConstraint constraint = ShapeConstraint.None,
            PlaneAxis shapePlaneAxis = PlaneAxis.Y)
        {
            if (_disposed || start == null || end == null) return;

            if (_hasPreviewKey
                && _previewSettings.Equals(settings)
                && _previewShapeType == shapeType && _previewConstraint == constraint
                && _previewPlaneAxis == shapePlaneAxis
                && SamePos(_previewStart, start)
                && SamePos(_previewEnd, end)) return;

            _previewShapeType = shapeType; _previewConstraint = constraint; _previewPlaneAxis = shapePlaneAxis;
            IGuideShape shape = ShapeFactory.Create(shapeType, constraint, shapePlaneAxis, start, end);
            int renderScale = ChooseRenderScale(shape, settings.Scale, settings.Filled);
            List<VoxelPosition> voxels = shape.GetVoxelPositions(renderScale, settings.Filled);
            if (settings.Divisions > 1)
                DivisionMarks.Apply(voxels, shape.SampleCurve(128), settings.Divisions, renderScale);
            if (voxels.Count == 0) { ClearDraftPreview(); return; }

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
                GrabbedPoint = null
            };
            AssignAnchors(shape.ControlPoints, options);

            MeshData data = GuideMeshBuilder.Build(voxels, options);
            if (_draftPreviewMesh != null) _capi.Render.DeleteMesh(_draftPreviewMesh);
            _draftPreviewMesh = _capi.Render.UploadMesh(data);
            _draftPreviewOrigin = origin;

            _previewStart = new Vec3d(start.X, start.Y, start.Z);
            _previewEnd = new Vec3d(end.X, end.Y, end.Z);
            _previewSettings = settings;
            _hasPreviewKey = true;
        }

        /// <summary>Removes the draft ghost (draft completed, cancelled, aim lost, or tool put away).</summary>
        public void ClearDraftPreview()
        {
            _hasPreviewKey = false;
            if (_draftPreviewMesh != null)
            {
                _capi.Render.DeleteMesh(_draftPreviewMesh);
                _draftPreviewMesh = null;
            }
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
        private const float SurfaceSlabThicknessWorld = 0.01f;

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
                if (kv.Value != null) RebuildGuide(kv.Value);

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

            // Session-8 playtest fix: PLACED guides always render at their TRUE scale. The coarsening
            // fallback (ChooseRenderScale) is a drafting-only courtesy — while the second foot is still
            // being aimed, a huge ghost may temporarily render coarser to stay cheap — and it was leaking
            // into settled guides here, permanently degrading anything over the preview cap. Once the
            // second anchor is placed, what you see is exactly the resolution you chose.
            int renderScale = guide.VoxelScale;
            List<VoxelPosition> voxels = shape.GetVoxelPositions(renderScale, guide.IsFilled);

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
                GrabbedPoint = ResolveGrabbedPoint(guide, points)
            };
            AssignAnchors(points, options);

            MeshData data = GuideMeshBuilder.Build(voxels, options);
            UploadOrReplace(guide.Id, data, origin);
        }

        // The world position of this guide's grabbed control point, or null if none is grabbed on it.
        private Vec3d ResolveGrabbedPoint(GuideData guide, List<ControlPoint> points)
        {
            if (guide.Id != _grabbedGuide) return null;
            if (_grabbedIndex < 0 || _grabbedIndex >= points.Count) return null;
            ControlPoint cp = points[_grabbedIndex];
            return cp?.IsPhantom == false ? cp.WorldPosition : null; // phantoms are never grabbed
        }

        // Feed the builder the start (first) and far (last) anchor so it can apply the Blue/Indigo coplanarity
        // shade. Anchors are identified by role, not fixed indices, to stay robust to future layouts.
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
                if (shape.GetVoxelCount(scales[i], filled) <= PreviewFullResVoxelCap) return scales[i];
            }
            return chosen;
        }

        private void UploadOrReplace(Guid id, MeshData data, Vec3d origin)
        {
            if (_guideMeshes.TryGetValue(id, out GuideMesh existing))
            {
                if (existing.Ref != null) _capi.Render.DeleteMesh(existing.Ref);
                existing.Ref = _capi.Render.UploadMesh(data);
                existing.Origin = origin;
            }
            else
            {
                _guideMeshes[id] = new GuideMesh { Ref = _capi.Render.UploadMesh(data), Origin = origin };
            }
        }

        private void RemoveGuideMesh(Guid id)
        {
            _deferredSurface.Remove(id);
            if (_guideMeshes.TryGetValue(id, out GuideMesh gm))
            {
                if (gm.Ref != null) _capi.Render.DeleteMesh(gm.Ref);
                _guideMeshes.Remove(id);
            }
        }

        private void EnsureMarkerMesh()
        {
            if (_markerMesh != null) return;
            var one = new List<VoxelPosition> { new VoxelPosition(0, 0, 0, VoxelRenderType.Anchor) };
            MeshData data = GuideMeshBuilder.Build(one, new GuideMeshOptions { Scale = DraftMarkerScale });
            _markerMesh = _capi.Render.UploadMesh(data);
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

            _capi.Event.UnregisterRenderer(this, RenderStage);
            _capi.Event.UnregisterGameTickListener(_reprobeListenerId);
            _deferredSurface.Clear();

            if (!_whiteTexBorrowed) _whiteTex?.Dispose();   // engine-cached fallback textures stay alive
            _whiteTex = null;

            if (_draftPreviewMesh != null)
            {
                _capi.Render.DeleteMesh(_draftPreviewMesh);
                _draftPreviewMesh = null;
            }

            foreach (GuideMesh gm in _guideMeshes.Values)
                if (gm.Ref != null) _capi.Render.DeleteMesh(gm.Ref);
            _guideMeshes.Clear();
            _remoteAnchors.Clear();

            if (_markerMesh != null)
            {
                _capi.Render.DeleteMesh(_markerMesh);
                _markerMesh = null;
            }
        }
    }
}
