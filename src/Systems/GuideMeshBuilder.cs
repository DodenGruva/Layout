using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Shapes;

namespace Layout.Systems
{
    /// <summary>
    /// Options controlling how <see cref="GuideMeshBuilder.Build"/> turns a voxel set into a mesh: the voxel
    /// scale, the projection mode/plane, the mesh origin, and the runtime overrides the pure shape cannot know
    /// (anchor-coplanarity shade, private-guide anchor palette, grabbed-point highlight, hidden-guide dimming).
    /// </summary>
    /// <remarks>
    /// VEC3D FIELDS ARE READ-ONLY AND NEVER RETAINED. The builder only reads these positions — to classify and
    /// place voxels — and never mutates or stores them past the call, so passing live control-point
    /// <c>Vec3d</c> instances is safe; the <c>Vec3d</c> aliasing rule (see <c>ControlPoint</c>) is not at risk
    /// here. All positions are absolute world coordinates in blocks (the same space as <c>ControlPoint</c>).
    /// </remarks>
    public sealed class GuideMeshOptions
    {
        /// <summary>Voxel edge length in 1/16-block units (1, 2, 4, 8, 16). Must match the voxels' own scale.</summary>
        public int Scale = 1;

        /// <summary>Volumetric → solid cubes; Surface → flat tiles on <see cref="Plane"/> (see class remarks).</summary>
        public ProjectionMode Mode = ProjectionMode.Volumetric;

        /// <summary>
        /// World-unit shrink applied along <see cref="Plane"/>'s flattened axis ONLY (cubes become slightly
        /// thinner slabs: [x+inset, x+e−inset] on that one axis). Module-7 in-game findings, both of them:
        /// Surface guides hug block faces exactly, so wall-parallel cube faces z-fought the wall (needs the
        /// inset) — but insetting ALL axes opened visible gaps between neighbouring voxels (so tangential
        /// axes stay full-size and neighbours stay seamless). 0 (the default) = no inset (volumetric).
        /// </summary>
        public float PlaneInset = 0f;

        /// <summary>
        /// World-unit thickness of a Surface guide's slab along <see cref="Plane"/>'s flattened axis.
        /// 0 (the default) = full-depth voxel cubes (volumetric, and the pre-fix Surface look). When &gt; 0,
        /// each Surface voxel is emitted as a paper-thin slab of this thickness instead of a full cube —
        /// the Session-8 finding: after the air-side probe fix the WHOLE voxel sat in the air cell, which
        /// only reads correctly if the voxel is paper-thin and hugs the wall face. Tangential axes stay
        /// full-size (seamless neighbours); <see cref="PlaneInset"/> still provides the anti-z-fight gap
        /// between the slab and the wall face.
        /// </summary>
        public float SurfaceSlabThickness = 0f;

        /// <summary>
        /// Which face of the flattened cell the slab hugs (only meaningful when
        /// <see cref="SurfaceSlabThickness"/> &gt; 0): −1 = the cell's MIN face along the plane axis (the
        /// solid wall is on the negative side), +1 = the cell's MAX face (wall on the positive side),
        /// 0 = no adjacent wall was identified (free-floating / mid-cell plane) — the slab is centred in
        /// the cell. Computed by the renderer's world-solidity probe alongside the layer choice.
        /// </summary>
        public int SurfaceSlabSide = 0;

        /// <summary>Plane for the Surface tile path. Ignored in Volumetric.</summary>
        public ProjectionPlane Plane = ProjectionPlane.Default;

        /// <summary>
        /// World-space origin (in blocks) that emitted vertices are made RELATIVE to, so mesh coordinates stay
        /// small and float-precise far from the world origin. The renderer sets the model-matrix translation to
        /// (Origin − cameraPos) each frame. Null → vertices are emitted in absolute world coordinates.
        /// </summary>
        public Vec3d Origin = null;

        /// <summary>
        /// The start (reference) anchor world position — the arch's index-1 foot. Used to split
        /// <see cref="VoxelRenderType.Anchor"/> voxels into the start foot (the aligned ownership colour)
        /// and the far foot. Null → every anchor voxel uses the aligned ownership colour.
        /// </summary>
        public Vec3d StartAnchor = null;

        /// <summary>
        /// The far anchor world position — the arch's index-(N−2) foot. When it is NOT coplanar with
        /// <see cref="StartAnchor"/> (same elevation AND on a cardinal line from it), the far foot's anchor
        /// voxels take the ownership palette's off-shade (Indigo public, Burnt Orange private).
        /// </summary>
        public Vec3d FarAnchor = null;

        /// <summary>
        /// Uses the private-guide Orange/Burnt-Orange anchor palette instead of the public Blue/Indigo
        /// palette. Render-only ownership cue; never persisted or sent over the network.
        /// </summary>
        public bool PrivateAnchors = false;

        /// <summary>
        /// World position of the control point currently held in an active drag, or null. Exactly ONE voxel —
        /// the rendered voxel whose centre is nearest to it — is overridden to White, which takes precedence
        /// over every resting colour. (Single-voxel nearest-claim, same approach as the shape's apex/anchor
        /// markers; the old marker-radius blob over-coloured, per Session-7 finding B2.)
        /// </summary>
        public Vec3d GrabbedPoint = null;

        /// <summary>
        /// Hidden guide: only anchor voxels are emitted, at reduced alpha, so the guide reads as two faint foot
        /// markers rather than its full body. The public/private palette and coplanarity cue are preserved.
        /// </summary>
        public bool Hidden = false;

        /// <summary>
        /// Block-occupancy recolour probe (v0.3.79): given CELL-space (1/16) coordinates, returns whether
        /// world material occupies that cell. Body voxels that come back true are drawn in the "built"
        /// colour. Null disables the feature entirely, which is what the player's toggle does — so a guide
        /// built with the toggle off is byte-identical to one built before the feature existed.
        /// </summary>
        /// <remarks>
        /// Called once per body voxel during the build, including on background materialization threads, so
        /// whatever backs it must tolerate concurrent calls. <see cref="BlockOccupancy"/> is safe for this
        /// and takes NO lock — its cache has been a <c>ConcurrentDictionary</c> since v0.3.84. This remark
        /// claimed a lock for three sessions after it was gone; see `GOTCHAS` G30 for what that cost.
        ///
        /// This is NOT a return of the retired z-fight probe. That one asked about a voxel's NEIGHBOUR and
        /// fed geometry, which is exactly why it had to go (v0.3.70): a rebuild after the player filled the
        /// volume would have flipped face offsets inward. This one asks about the voxel's OWN cell and feeds
        /// colour only, so a rebuild simply re-reads the truth — the failure mode that killed the old probe
        /// does not exist here.
        /// </remarks>
        public Func<int, int, int, bool> OccupancyProbe = null;

        // REMOVED v0.3.70 — `IsNeighborSolid`, the world-solidity probe that drove the z-fight clearance
        // from 0.2.16. The face offset is now a uniform outward push decided by the guide's own voxel set;
        // see the face-offset note in the cube path below for why the world must not influence it.

        /// <summary>
        /// Optional complete voxel occupancy used when meshing only one materialization subset. Neighbour
        /// faces are culled against the final shell even when the neighbouring voxel belongs to a later batch.
        /// </summary>
        public HashSet<(int, int, int)> Occupancy = null;

        /// <summary>
        /// Optional global minimum Y for a spatially partitioned final mesh. Without it, every subset would
        /// treat its own lowest exposed layer as the guide floor and apply the floor clearance repeatedly.
        /// </summary>
        public int? MinimumVoxelY = null;

        /// <summary>
        /// Share vertices between faces that meet at the same position with the same colour, instead of
        /// emitting four fresh vertices per quad (v0.3.57, Stage 1 of <c>PLAN_RENDER_PERFORMANCE.md</c>).
        /// </summary>
        /// <remarks>
        /// This is DEDUPLICATION, NOT MERGING. The triangle set — count, positions, winding, colours, and
        /// submission order — is unchanged; only the index buffer changes to point at shared vertices. That
        /// distinction is the whole point: Sessions 25–26 rejected two renderer experiments because
        /// regrouping primitives changed the picture, and welding cannot, because it does not regroup them.
        ///
        /// Measured on the v0.3.55 baseline, an 8M-voxel guide submitted 64,716,392 vertices against
        /// 97,074,588 indices — exactly 4 vertices and 6 indices per quad, so every shared corner was
        /// duplicated across all faces touching it. Because the vertex format carries no per-face data (no
        /// normals, and uv is always (0,0)), any two faces meeting at one position with one colour can share.
        ///
        /// Cube path only. The Surface tile and slab paths are small 2D geometry and stay on the legacy
        /// unshared path. Set false to fall back exactly to pre-v0.3.57 output — kept as the revert switch
        /// and used by the equivalence harness.
        /// </remarks>
        public bool WeldVertices = GuideMeshBuilder.WeldByDefault;
    }

    /// <summary>
    /// The bounding size of a guide's voxel set in two units at once: current-scale voxels and whole blocks.
    /// Backs the HUD's live width × height readout (arrows ↔ width / ↕ height).
    /// </summary>
    public readonly struct GuideExtent
    {
        /// <summary>Horizontal extent in current-scale voxels (foot-to-foot bounding-footprint diagonal).</summary>
        public int VoxelWidth { get; }

        /// <summary>Vertical extent in current-scale voxels (feet-to-apex).</summary>
        public int VoxelHeight { get; }

        /// <summary>Horizontal extent in whole blocks: ceil(VoxelWidth × scale ÷ 16).</summary>
        public int BlockWidth { get; }

        /// <summary>Vertical extent in whole blocks: ceil(VoxelHeight × scale ÷ 16).</summary>
        public int BlockHeight { get; }

        public GuideExtent(int voxelWidth, int voxelHeight, int blockWidth, int blockHeight)
        {
            VoxelWidth = voxelWidth;
            VoxelHeight = voxelHeight;
            BlockWidth = blockWidth;
            BlockHeight = blockHeight;
        }

        /// <summary>The zero extent, for an empty or not-yet-formed guide.</summary>
        public static GuideExtent Empty => new GuideExtent(0, 0, 0, 0);

        public override string ToString() =>
            $"{VoxelWidth}\u00d7{VoxelHeight} vox ({BlockWidth}\u00d7{BlockHeight} blk)";
    }

    /// <summary>
    /// Stateless client-side geometry builder: turns a shape's <see cref="VoxelPosition"/> set into a
    /// <see cref="MeshData"/> the renderer can upload, mapping each voxel's <see cref="VoxelRenderType"/> to an
    /// RGBA colour. It also owns the two things the pure shape cannot know — the anchor-coplanarity shade and
    /// the transient grabbed-point highlight — plus the bounding-extent helper behind the HUD readout.
    /// </summary>
    /// <remarks>
    /// COLOUR TABLE (authoritative — this file is the single source of truth for guide colours; the role
    /// comments on <see cref="VoxelRenderType"/> and <c>ControlPoint</c> agree with it):
    ///   Normal → Yellow, Locked → Red, Primary/apex → Green, public Anchor → Blue,
    ///   private Anchor → Orange; far anchors use Indigo/Burnt-Orange off-shades; Grabbed → White,
    ///   hidden-guide anchors → the anchor colour at reduced alpha.
    ///
    /// ANCHOR SPLIT. The shape tags BOTH feet <see cref="VoxelRenderType.Anchor"/> and cannot express the
    /// pairwise "is the far foot clean?" test, so that lives here. Given the two anchor world positions, each
    /// Anchor voxel is bucketed to its nearer foot. The far foot uses the aligned ownership colour when
    /// coplanar (same elevation AND on a cardinal line from the start) and that palette's off-shade otherwise.
    ///
    /// GRABBED HIGHLIGHT. The grabbed control point may be an unmarked body point (Yellow), so a fresh White
    /// marker is painted on it — SINGLE-VOXEL NEAREST-CLAIM (the one rendered voxel whose centre is nearest
    /// to the grabbed position), matching the shape's own Green/Blue/Red markers, which use the same
    /// approach. (The old marker-radius blob is gone — Session-7 finding B2.) The renderer decides WHICH
    /// point (if any) is grabbed and passes it in.
    ///
    /// SURFACE PATH IS PRESENT BUT DORMANT. The Surface (flat-tile) path is implemented, but the built shape
    /// still emits only the hollow-volumetric voxel subset, so the renderer drives the cube path for every
    /// guide until the shape's Surface projection lands (per PROJECT_STATUS.md). When it does, the renderer
    /// simply sets <see cref="GuideMeshOptions.Mode"/> to Surface — no change is needed here.
    ///
    /// VS-API NOTE. The mesh is built unlit via <c>MeshData.AddVertex(x, y, z, 0, 0, color)</c> — uv (0,0)
    /// against a white texture, so colour comes purely from the packed vertex rgba — with a packed
    /// RGBA int (R in the low byte). Vertex colour — not lighting — carries the guide's appearance; the renderer
    /// selects a blended, depth-tested draw. The <c>MeshData</c> constructor flags and <c>AddVertex</c> are the
    /// only VS-rendering-API surface in this file.
    /// </remarks>
    public static class GuideMeshBuilder
    {
        /// <summary>
        /// Default for <see cref="GuideMeshOptions.WeldVertices"/> on newly created options, so
        /// <c>.layout weld on|off</c> can flip every subsequent rebuild at once. Diagnostic only: it exists
        /// so the welded and unwelded builds can be compared live, from one camera position, on one guide —
        /// the only way to settle an "it looks slightly different" report. Not persisted; always on at start.
        /// </summary>
        public static bool WeldByDefault = true;

        // --- Colour table -----------------------------------------------------------------------------
        // The whole table now lives in GuidePalette, as an IMMUTABLE instance behind one reference. Both the
        // scheme (which hue each role gets, v0.4.8) and the alphas (layout-client.json, Session-8 item 2)
        // are client-configurable, and both are applied by rebuilding the palette and swapping this field.
        //
        // THE SWAP IS WHY THIS IS A REFERENCE AND NOT MUTABLE ARRAYS. Meshes build on background workers, so
        // the old in-place mutation could be seen by some batches of a large guide and not others, leaving
        // one guide wearing two palettes until the rebuild settled. A build reads this field ONCE (see
        // Build) and uses that instance throughout, so it is always internally consistent.
        // The seed matches LayoutClientConfig's defaults (body/locked/apex 0.5; anchor 0.8; grabbed 0.95).
        private static GuidePalette _palette = GuidePalette.Build(
            GuidePaletteScheme.Default, 0.50f, 0.50f, 0.50f, 0.80f, 0.95f, 0.35f);

        /// <summary>The palette meshes are currently being built with.</summary>
        public static GuidePalette Palette => _palette;

        /// <summary>
        /// Applies the client-configured colour scheme and opacities (see <c>LayoutClientConfig</c>). Called
        /// by <c>LayoutModSystem</c> at client start and again whenever the player changes either on the
        /// settings page; guides already built keep their old colours until they are rebuilt, which is what
        /// <c>ApplyOpacitiesAndRebuild</c> then does. Values are pre-clamped by the config's
        /// <c>Normalize()</c>.
        /// </summary>
        public static void ConfigurePalette(
            GuidePaletteScheme scheme,
            float body, float locked, float apex, float anchor, float grabbed, float hiddenAnchor,
            float[][] customRoles = null)
        {
            _palette = GuidePalette.Build(
                scheme, body, locked, apex, anchor, grabbed, hiddenAnchor, customRoles);
        }

        // Anti-z-fight face clearance, in world blocks. SINCE v0.3.70 THIS IS AN OUTSET, not an inset: it
        // is applied outward to every exposed face of a volumetric guide (the name is kept so `/layout
        // inset`, the client config key, and the playtest history all still line up). Magnitude semantics
        // are unchanged, so the value below is still the one three rounds of playtest settled on.
        //
        // Tuned by playtest twice, once per direction. As an INSET: 0.004 was safe but seamy; 0.001
        // shimmered when moving toward/away from the guide (depth precision falls with distance); 0.003 was
        // the human's call (0.2.14). As an OUTSET: 0.0006, confirmed in play (v0.3.71) — five times smaller.
        //
        // The drop is the expected consequence of the flip, not a re-tune of the same problem. An inset had
        // to open a gap WIDE ENOUGH TO SEE PAST the world surface sitting in front of it, so it paid for
        // depth precision and for the surface's own thickness. An outset only has to land in front of that
        // surface and win the depth comparison, so it needs barely more than the depth buffer's resolution
        // at the distances guides are read from. Anything larger just inflates the guide off its own cells.
        //
        // Since exposed-face meshing (0.2.14) faces BETWEEN adjacent guide voxels are not emitted at all,
        // so the clearance produces no interior seams whatsoever.
        /// <remarks>
        /// TUNABLE SINCE v0.3.65 — a field, not a const, so <c>/layout inset</c> can dial it in play. A
        /// reported ground z-fight cannot be reproduced or judged from outside the game, and this value was
        /// settled by three rounds of playtest; guessing at a new one blind is how the Session 25–26
        /// regressions happened.
        ///
        /// Worth knowing before raising it: the reason 0.004 was rejected as "seamy" no longer applies. That
        /// seam was between two guide voxels meeting across a block boundary, and exposed-face meshing means
        /// those faces are not emitted at all any more (see the note above). The inset now only ever
        /// separates a guide face from a WORLD BLOCK face, so the old ceiling on it is obsolete and larger
        /// values are safer than they were when this was tuned.
        /// </remarks>
        private static float BlockPlaneInset = 0.0006f;

        /// <summary>Sets the anti-z-fight inset, in world blocks. Meshes must be rebuilt to take effect.</summary>
        public static void ConfigureBlockPlaneInset(float inset) => BlockPlaneInset = inset;

        /// <summary>Current anti-z-fight inset, in world blocks.</summary>
        public static float CurrentBlockPlaneInset => BlockPlaneInset;

        // Coplanarity tolerance, world blocks. Anchors placed by voxel/block targeting (and Shift-to-constrain)
        // land on exact grid coordinates, so an effectively-exact epsilon is right: a clean foot reads clean, a
        // deliberately raised or angled one reads off.
        private const double CoplanarEpsilon = 1e-6;

        // Outward-facing (CCW) triangle winding for the 8 cube corners laid out in <see cref="AddBox"/>.
        private static readonly int[] CubeIndices =
        {
            0, 3, 2, 0, 2, 1,   // bottom  (-Y)
            4, 5, 6, 4, 6, 7,   // top     (+Y)
            0, 1, 5, 0, 5, 4,   // front   (-Z)
            3, 7, 6, 3, 6, 2,   // back    (+Z)
            0, 4, 7, 0, 7, 3,   // left    (-X)
            1, 2, 6, 1, 6, 5    // right   (+X)
        };

        /// <summary>
        /// Builds a mesh for the given voxel set under the given options. Never returns null; returns an empty
        /// mesh for an empty/degenerate input. The result is ready to hand to <c>capi.Render.UploadMesh</c>.
        /// </summary>
        public static MeshData Build(IReadOnlyList<VoxelPosition> voxels, GuideMeshOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            int scale = options.Scale;
            if (voxels == null || voxels.Count == 0 || scale <= 0) return NewMesh(0);

            double ox = options.Origin?.X ?? 0.0;
            double oy = options.Origin?.Y ?? 0.0;
            double oz = options.Origin?.Z ?? 0.0;

            bool surface = options.Mode == ProjectionMode.Surface;
            float edge = scale / 16f;      // cube / tile edge, world blocks
            double half = scale / 2.0;     // half a voxel, 1/16 units (for the voxel centre)

            // Anchor split.
            Vec3d start = options.StartAnchor;
            Vec3d far = options.FarAnchor;
            bool haveBothAnchors = start != null && far != null;
            bool farAligned = !haveBothAnchors || IsFarAnchorAligned(start, far);

            // Grabbed highlight — SINGLE-VOXEL NEAREST-CLAIM (B2): exactly the one rendered voxel whose
            // centre is nearest to the grabbed point goes White, mirroring the shape's apex/anchor markers.
            // The pre-pass respects the hidden filter so the claim can never land on a voxel we skip below.
            // The distance gate is a sanity guard, NOT a blob radius: a legitimately grabbed point always has
            // a sampled voxel within ~a cell (the curve passes through it), but on a HIDDEN guide only the
            // anchors render — grabbing a body point there must not paint a far-away anchor White.
            Vec3d grab = options.GrabbedPoint;
            int grabbedIdx = -1;
            if (grab != null)
            {
                double gate = 1.5 * scale / 16.0;          // world blocks; ~1.5 cells
                double bestD2 = gate * gate;
                for (int i = 0; i < voxels.Count; i++)
                {
                    VoxelPosition v = voxels[i];
                    if (options.Hidden && v.Type != VoxelRenderType.Anchor) continue;
                    double d2 = Dist2((v.X + half) / 16.0, (v.Y + half) / 16.0, (v.Z + half) / 16.0, grab);
                    if (d2 < bestD2) { bestD2 = d2; grabbedIdx = i; }
                }
            }

            // Z-fight pre-pass: the lowest voxel layer's bottom face is always lifted (guides resting on
            // slab/chiseled tops sit off-grid); grid-coplanar faces are handled per-face in the cube
            // branch below — see BlockPlaneInset.
            int minY = options.MinimumVoxelY ?? int.MaxValue;
            if (!options.MinimumVoxelY.HasValue)
                for (int i = 0; i < voxels.Count; i++)
                    if (voxels[i].Y < minY) minY = voxels[i].Y;

            // EXPOSED-FACE MESHING (Stage A of the SESSION_14 large-guide mesh plan, 0.2.14). The ordinary
            // Volumetric cube path no longer emits six faces per voxel: a presence set of the RENDERED
            // cells (hidden-filtered, so a hidden guide's anchor cubes stay complete) lets each voxel emit
            // only the faces whose neighbouring cell is absent. Faces shared by adjacent voxels — the vast
            // majority in filled bodies and shell laterals — are never generated at all: per-voxel role
            // colours are preserved exactly (each face belongs to its own voxel), and the mesh is allocated
            // EXACTLY from a face pre-count. The Surface tile and slab paths stay on the legacy
            // whole-box/quad path per the staged plan.
            // Read the palette ONCE for the whole batch. This is the guarantee described on _palette: a
            // scheme or opacity change landing mid-build cannot colour half of this mesh one way and half
            // the other, because every voxel below reads from this one captured instance.
            GuidePalette pal = _palette;

            bool cubePath = !surface && options.SurfaceSlabThickness <= 0f;
            HashSet<(int, int, int)> present = null;
            VertexWelder welder = null;
            MeshData mesh;
            if (cubePath)
            {
                present = options.Occupancy ?? new HashSet<(int, int, int)>(voxels.Count);
                if (options.Occupancy == null)
                {
                    for (int i = 0; i < voxels.Count; i++)
                    {
                        VoxelPosition p = voxels[i];
                        if (options.Hidden && p.Type != VoxelRenderType.Anchor) continue;
                        present.Add((p.X, p.Y, p.Z));
                    }
                }

                int faces = 0;
                for (int i = 0; i < voxels.Count; i++)
                {
                    VoxelPosition p = voxels[i];
                    if (options.Hidden && p.Type != VoxelRenderType.Anchor) continue;
                    int px = p.X, py = p.Y, pz = p.Z;
                    if (!present.Contains((px - scale, py, pz))) faces++;
                    if (!present.Contains((px + scale, py, pz))) faces++;
                    if (!present.Contains((px, py - scale, pz))) faces++;
                    if (!present.Contains((px, py + scale, pz))) faces++;
                    if (!present.Contains((px, py, pz - scale))) faces++;
                    if (!present.Contains((px, py, pz + scale))) faces++;
                }
                // Capacity stays at the unwelded worst case (4 vertices per face). Welding only ever uses
                // FEWER, so this keeps the "no growth reallocation is possible" guarantee the exact
                // pre-count was introduced for; the unused tail is transient per-batch memory.
                mesh = NewMeshForFaces(faces);
                if (options.WeldVertices) welder = new VertexWelder(mesh, faces);
            }
            else
            {
                mesh = NewMesh(voxels.Count);
            }

            for (int vi = 0; vi < voxels.Count; vi++)
            {
                VoxelPosition v = voxels[vi];
                if (options.Hidden && v.Type != VoxelRenderType.Anchor) continue;

                // Voxel centre in world blocks — the same reference ArchShape uses for its markers.
                double cx = (v.X + half) / 16.0;
                double cy = (v.Y + half) / 16.0;
                double cz = (v.Z + half) / 16.0;

                float r, g, b, a;
                if (vi == grabbedIdx)
                {
                    r = pal.Grabbed[0]; g = pal.Grabbed[1]; b = pal.Grabbed[2]; a = pal.Grabbed[3];
                }
                else
                {
                    ResolveColor(pal, v.Type, cx, cy, cz, start, far, haveBothAnchors, farAligned,
                                 options.PrivateAnchors,
                                 out r, out g, out b, out a);
                    if (options.Hidden) a = pal.HiddenAnchorAlpha; // only anchors reach here when hidden

                    // BLOCK-OCCUPANCY RECOLOUR (v0.3.79, PLAN_BLOCK_OCCUPANCY stage 2). A voxel whose own
                    // cell already holds world material is drawn in the "built" colour instead of its role
                    // colour, so the player can see at a glance which parts of the plan exist.
                    //
                    // Alpha is deliberately kept from the role colour: the guide's transparency is a
                    // separate, player-tuned setting, and changing it here would read as the guide fading
                    // rather than as a state change. Only the hue moves.
                    //
                    // The probe is skipped for anchors and the grabbed point — those mark the GUIDE's own
                    // structure, not the world's, and recolouring them would lose information the player
                    // needs while placing. Body voxels are the ones this is about.
                    if (options.OccupancyProbe != null && v.Type == VoxelRenderType.Normal)
                    {
                        // Sample the CENTRE cell of the voxel. At scale 1 that is the voxel itself and the
                        // correspondence with a chisel voxel is exact; at coarser scales one guide voxel
                        // spans many world cells and the centre is the cheapest defensible verdict (plan
                        // §2.1 — revisit if it reads badly at scale 16).
                        int mid = scale >> 1;
                        if (options.OccupancyProbe(v.X + mid, v.Y + mid, v.Z + mid))
                        {
                            r = pal.Built[0]; g = pal.Built[1]; b = pal.Built[2];
                        }
                    }
                }

                int color = PackRgba(r, g, b, a);

                if (surface)
                {
                    AddTile(mesh, options.Plane, v, edge, ox, oy, oz, color);
                }
                else if (options.SurfaceSlabThickness > 0f)
                {
                    // PAPER-THIN SURFACE SLAB (Session-8 finding): the flattened voxel occupies the AIR cell
                    // (correct side, per the probe fix), but a full-depth cube there reads as floating in
                    // front of the wall. So along the plane axis only, emit a thin slab that hugs the
                    // wall-side face of the cell (side −1 = min face, +1 = max face, 0 = centred), pulled
                    // off the wall by PlaneInset so it never z-fights. Tangential axes stay full-size.
                    float lx = (float)(v.X / 16.0 - ox);
                    float ly = (float)(v.Y / 16.0 - oy);
                    float lz = (float)(v.Z / 16.0 - oz);

                    float inset = options.PlaneInset;
                    float t = Math.Min(options.SurfaceSlabThickness, edge - 2f * inset); // sanity at tiny scales
                    float axisOffset = options.SurfaceSlabSide < 0 ? inset
                                     : options.SurfaceSlabSide > 0 ? edge - inset - t
                                     : (edge - t) * 0.5f;

                    float ex = edge, ey = edge, ez = edge;
                    switch (options.Plane.FlattenedAxis)
                    {
                        case PlaneAxis.X: lx += axisOffset; ex = t; break;
                        case PlaneAxis.Y: ly += axisOffset; ey = t; break;
                        default:          lz += axisOffset; ez = t; break;
                    }
                    AddBox(mesh, lx, ly, lz, ex, ey, ez, color);
                }
                else
                {
                    // Plane-axis-only inset — see GuideMeshOptions.PlaneInset.
                    float ix = 0f, iy = 0f, iz = 0f;
                    if (options.PlaneInset > 0f)
                    {
                        switch (options.Plane.FlattenedAxis)
                        {
                            case PlaneAxis.X: ix = options.PlaneInset; break;
                            case PlaneAxis.Y: iy = options.PlaneInset; break;
                            default: iz = options.PlaneInset; break;
                        }
                    }
                    // Which faces are exposed (no neighbouring cell) — only those are emitted below.
                    bool expXn = !present.Contains((v.X - scale, v.Y, v.Z));
                    bool expXp = !present.Contains((v.X + scale, v.Y, v.Z));
                    bool expYn = !present.Contains((v.X, v.Y - scale, v.Z));
                    bool expYp = !present.Contains((v.X, v.Y + scale, v.Z));
                    bool expZn = !present.Contains((v.X, v.Y, v.Z - scale));
                    bool expZp = !present.Contains((v.X, v.Y, v.Z + scale));

                    // Z-fight clearance — UNIFORM OUTSET (v0.3.70). Every exposed face is pushed a hair
                    // OUT of the voxel. The offset is a property of the GUIDE ALONE: exposure is already
                    // decided against the guide's own voxel set (expXn..expZp above), and nothing here
                    // consults the world.
                    //
                    // WHAT THIS REPLACES. From 0.2.16 to v0.3.69 the offset was driven by a world-solidity
                    // probe: "is a solid block across this face?" → if so, pull the face INWARD off the
                    // shared plane. Correct for a guide standing beside blocks, and exactly backwards for
                    // the workflow this now serves — over-fill the guide volume with material, then chisel
                    // back down until guide voxels appear and stop there. In that case the guide face is
                    // coplanar with the surface just cut, and it must sit PROUD of it to read at all.
                    //
                    // WHY NOTHING NEEDS REBUILDING. The offset cannot go stale, because it never depended
                    // on the world in the first place: no re-probe queue, no block-change subscription, no
                    // invalidation. This is load-bearing, not merely convenient — a rebuild triggered after
                    // the player fills the volume would see solid neighbours everywhere and flip these
                    // faces inward, breaking the guide at exactly the moment it is needed.
                    //
                    // A guide drawn THROUGH pre-existing terrain therefore gets outset faces from the
                    // start, and they appear the instant the player chisels down to them — the probing
                    // version got that case wrong until something happened to rebuild it.
                    //
                    // THE COST, accepted deliberately: a guide face flush against a pre-existing block now
                    // sits a hair inside it and is hidden, where the inset used to hold it visible in a
                    // hairline gap. Those are faces viewed from the far side anyway — a guide's underside
                    // resting on the ground, its back against a wall. Flagged for playtest.
                    //
                    // Side effect on welding: every exposed face now takes the SAME offset, so coplanar
                    // faces of adjacent voxels always agree and the v0.3.57 welder can share them. The
                    // probe-driven version varied face to face and blocked some of those welds.
                    //
                    // Surface guides are untouched — they run the slab/decal path above with their own
                    // plane-axis inset, and still need it not to z-fight the wall they are drawn on.
                    // The offsets below are NEGATIVE — every exposed face is pushed OUTWARD, growing the
                    // box by a hair. Faces shared with a neighbouring guide voxel keep 0 and stay exactly
                    // flush; any offset there reopens the 0.2.14 seam.
                    float outset = -BlockPlaneInset;
                    float fx0 = expXn ? outset : 0f;
                    float fx1 = expXp ? outset : 0f;
                    float fy0 = expYn ? outset : 0f;
                    float fy1 = expYp ? outset : 0f;
                    float fz0 = expZn ? outset : 0f;
                    float fz1 = expZp ? outset : 0f;

                    // The inset box this voxel occupies. CANONICAL LATTICE DERIVATION (0.3.57): each bound
                    // is computed from ITS OWN integer voxel coordinate rather than as "min corner + edge".
                    // The two are equal in exact arithmetic, but not in floats — rounding (v.X/16 − ox) to
                    // float and then adding `edge` lands up to one ULP away from rounding
                    // ((v.X + scale)/16 − ox) directly. That last bit is invisible on screen (~1e-7 blocks)
                    // but it is the difference between two adjacent voxels agreeing on their shared plane or
                    // not, and the welder matches bit-exactly — the old form would have blocked nearly every
                    // cross-voxel weld. Insets still apply per face, so faces that genuinely sit at
                    // different positions correctly refuse to share.
                    float x0 = (float)(v.X / 16.0 - ox) + ix + fx0;
                    float y0 = (float)(v.Y / 16.0 - oy) + iy + fy0;
                    float z0 = (float)(v.Z / 16.0 - oz) + iz + fz0;
                    float x1 = (float)((v.X + scale) / 16.0 - ox) - ix - fx1;
                    float y1 = (float)((v.Y + scale) / 16.0 - oy) - iy - fy1;
                    float z1 = (float)((v.Z + scale) / 16.0 - oz) - iz - fz1;

                    // Emit only the exposed faces, wound to match the outward (CCW) orientation the old
                    // cube path used.
                    if (expYn) AddQuad(mesh, welder, x0, y0, z0, x0, y0, z1, x1, y0, z1, x1, y0, z0, color); // bottom −Y
                    if (expYp) AddQuad(mesh, welder, x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1, color); // top +Y
                    if (expZn) AddQuad(mesh, welder, x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0, color); // front −Z
                    if (expZp) AddQuad(mesh, welder, x0, y0, z1, x0, y1, z1, x1, y1, z1, x1, y0, z1, color); // back +Z
                    if (expXn) AddQuad(mesh, welder, x0, y0, z0, x0, y1, z0, x0, y1, z1, x0, y0, z1, color); // left −X
                    if (expXp) AddQuad(mesh, welder, x1, y0, z0, x1, y0, z1, x1, y1, z1, x1, y1, z0, color); // right +X
                }
            }

            return mesh;
        }

        /// <summary>
        /// Measures the guide's bounding size for the HUD readout, in both current-scale voxels and whole
        /// blocks. Width is the horizontal (foot-to-foot) footprint diagonal; height is the vertical span.
        /// </summary>
        /// <remarks>
        /// The horizontal-width definition for a DIAGONAL arch (footprint diagonal vs. per-axis extent) is the
        /// open question flagged in PROJECT_STATUS.md §6; the diagonal is used here and reads correctly for a
        /// clean level+cardinal arch (where one horizontal axis is a single voxel thick). It is easily retuned
        /// for the Module-6 HUD without touching the mesh path.
        /// </remarks>
        public static GuideExtent MeasureExtent(IReadOnlyList<VoxelPosition> voxels, int scale)
        {
            if (voxels == null || voxels.Count == 0 || scale <= 0) return GuideExtent.Empty;

            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
            foreach (VoxelPosition v in voxels)
            {
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z;
                if (v.Z > maxZ) maxZ = v.Z;
            }

            int spanX = maxX - minX, spanY = maxY - minY, spanZ = maxZ - minZ; // 1/16 units, multiples of scale
            double hSpan = Math.Sqrt((double)spanX * spanX + (double)spanZ * spanZ);

            // +1 → inclusive voxel COUNT across the span (a single voxel is 1 wide and 1 tall).
            int voxelWidth = (int)Math.Round(hSpan / scale) + 1;
            int voxelHeight = spanY / scale + 1;

            int blockWidth = CeilDiv(voxelWidth * scale, 16);
            int blockHeight = CeilDiv(voxelHeight * scale, 16);

            return new GuideExtent(voxelWidth, voxelHeight, blockWidth, blockHeight);
        }

        /// <summary>
        /// Cheap metadata measurement from a shape's fixed-size targeting wireframe. This intentionally
        /// avoids shell voxelisation and is suitable for guide names, hover text, and live grab dimensions.
        /// </summary>
        public static GuideExtent MeasureShapeExtent(IGuideShape shape, int scale)
        {
            if (shape == null || scale <= 0) return GuideExtent.Empty;
            if (shape is IIntrinsicGuideExtent intrinsic
                && intrinsic.TryGetIntrinsicDimensions(
                    out double intrinsicWidth, out double intrinsicHeight))
                return ExtentFromWorldDimensions(intrinsicWidth, intrinsicHeight, scale);

            List<Vec3d> curve = shape.SampleCurve(128);
            if ((curve == null || curve.Count == 0) && shape.ControlPoints == null)
                return GuideExtent.Empty;

            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
            void Include(Vec3d point)
            {
                if (point == null) return;
                int x = (int)Math.Floor(point.X * 16.0 / scale) * scale;
                int y = (int)Math.Floor(point.Y * 16.0 / scale) * scale;
                int z = (int)Math.Floor(point.Z * 16.0 / scale) * scale;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            }

            if (curve != null)
                for (int i = 0; i < curve.Count; i++) Include(curve[i]);
            if (shape.ControlPoints != null)
                for (int i = 0; i < shape.ControlPoints.Count; i++)
                    Include(shape.ControlPoints[i]?.WorldPosition);
            if (minX == int.MaxValue) return GuideExtent.Empty;

            double horizontal = Math.Sqrt(
                (double)(maxX - minX) * (maxX - minX)
                + (double)(maxZ - minZ) * (maxZ - minZ));
            int voxelWidth = (int)Math.Round(horizontal / scale) + 1;
            int voxelHeight = (maxY - minY) / scale + 1;
            return new GuideExtent(voxelWidth, voxelHeight,
                CeilDiv(voxelWidth * scale, 16), CeilDiv(voxelHeight * scale, 16));
        }

        private static GuideExtent ExtentFromWorldDimensions(
            double width, double height, int scale)
        {
            const double roundingEpsilon = 1e-9;
            int voxelWidth = Math.Max(1,
                (int)Math.Ceiling(Math.Max(0.0, width) * 16.0 / scale
                    - roundingEpsilon));
            int voxelHeight = Math.Max(1,
                (int)Math.Ceiling(Math.Max(0.0, height) * 16.0 / scale
                    - roundingEpsilon));
            int blockWidth = Math.Max(1,
                (int)Math.Ceiling(Math.Max(0.0, width) - roundingEpsilon));
            int blockHeight = Math.Max(1,
                (int)Math.Ceiling(Math.Max(0.0, height) - roundingEpsilon));
            return new GuideExtent(
                voxelWidth, voxelHeight, blockWidth, blockHeight);
        }

        /// <summary>
        /// The resting RGBA (0..1) for a render type, ignoring the runtime overrides (anchors resolve to Blue).
        /// Handy for a HUD colour legend; the actual mesh applies the anchor split and grabbed override.
        /// </summary>
        public static (float r, float g, float b, float a) BaseColor(VoxelRenderType type)
        {
            float[] c = _palette.ForType(type);
            return (c[0], c[1], c[2], c[3]);
        }

        // --- colour resolution -----------------------------------------------------------------------------

        private static void ResolveColor(
            GuidePalette pal,
            VoxelRenderType type, double cx, double cy, double cz,
            Vec3d start, Vec3d far, bool haveBothAnchors, bool farAligned,
            bool privateAnchors,
            out float r, out float g, out float b, out float a)
        {
            float[] c = type == VoxelRenderType.Anchor && privateAnchors
                ? pal.PrivateAnchor
                : pal.ForType(type);

            // Only Anchor voxels can take the far-foot off-shade, and only when the far foot is not aligned.
            if (type == VoxelRenderType.Anchor && haveBothAnchors && !farAligned)
            {
                double dStart = Dist2(cx, cy, cz, start);
                double dFar = Dist2(cx, cy, cz, far);
                if (dFar < dStart)
                    c = privateAnchors ? pal.PrivateAnchorFar : pal.AnchorFar; // belongs to the far foot
            }

            r = c[0]; g = c[1]; b = c[2]; a = c[3];
        }

        private static bool IsFarAnchorAligned(Vec3d start, Vec3d far)
        {
            bool level = Math.Abs(start.Y - far.Y) <= CoplanarEpsilon;
            bool cardinal = Math.Abs(start.X - far.X) <= CoplanarEpsilon
                         || Math.Abs(start.Z - far.Z) <= CoplanarEpsilon;
            return level && cardinal;
        }

        // --- geometry --------------------------------------------------------------------------------------

        // Axis-aligned box with independent per-axis extents (equal extents = the classic voxel cube;
        // Surface guides shrink just the plane axis — see GuideMeshOptions.PlaneInset).
        private static void AddBox(MeshData mesh, float x, float y, float z,
                                   float ex, float ey, float ez, int color)
        {
            int b = mesh.VerticesCount;
            mesh.AddVertex(x,      y,      z, 0f, 0f, color); // 0
            mesh.AddVertex(x + ex, y,      z, 0f, 0f, color); // 1
            mesh.AddVertex(x + ex, y,      z + ez, 0f, 0f, color); // 2
            mesh.AddVertex(x,      y,      z + ez, 0f, 0f, color); // 3
            mesh.AddVertex(x,      y + ey, z, 0f, 0f, color); // 4
            mesh.AddVertex(x + ex, y + ey, z, 0f, 0f, color); // 5
            mesh.AddVertex(x + ex, y + ey, z + ez, 0f, 0f, color); // 6
            mesh.AddVertex(x,      y + ey, z + ez, 0f, 0f, color); // 7
            for (int k = 0; k < CubeIndices.Length; k++) mesh.AddIndex(b + CubeIndices[k]);
        }

        // A single quad per voxel on the projection plane: the kept axes span the voxel, the flattened axis is
        // pinned to the plane offset (projecting the voxel onto the plane). Wound CCW on the visible face; the
        // renderer draws guides double-sided so the back face shows too. (Dormant until Surface voxels exist.)
        private static void AddTile(MeshData mesh, ProjectionPlane plane, VoxelPosition v, float e,
                                    double ox, double oy, double oz, int color)
        {
            float planeW = (float)(plane.PlaneOffset / 16.0);
            switch (plane.FlattenedAxis)
            {
                case PlaneAxis.Y: // horizontal: vary X/Z at y = offset
                {
                    float y = (float)(planeW - oy);
                    float x0 = (float)(v.X / 16.0 - ox), x1 = x0 + e;
                    float z0 = (float)(v.Z / 16.0 - oz), z1 = z0 + e;
                    AddQuad(mesh, null, x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1, color);
                    break;
                }
                case PlaneAxis.Z: // vertical N–S: vary X/Y at z = offset
                {
                    float z = (float)(planeW - oz);
                    float x0 = (float)(v.X / 16.0 - ox), x1 = x0 + e;
                    float y0 = (float)(v.Y / 16.0 - oy), y1 = y0 + e;
                    AddQuad(mesh, null, x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z, color);
                    break;
                }
                default: // PlaneAxis.X — vertical E–W: vary Z/Y at x = offset
                {
                    float x = (float)(planeW - ox);
                    float z0 = (float)(v.Z / 16.0 - oz), z1 = z0 + e;
                    float y0 = (float)(v.Y / 16.0 - oy), y1 = y0 + e;
                    AddQuad(mesh, null, x, y0, z0, x, y0, z1, x, y1, z1, x, y1, z0, color);
                    break;
                }
            }
        }

        private static void AddQuad(MeshData mesh, VertexWelder welder,
            float ax, float ay, float az, float bx, float by, float bz,
            float cx, float cy, float cz, float dx, float dy, float dz, int color)
        {
            int b0, b1, b2, b3;
            if (welder == null)
            {
                b0 = mesh.VerticesCount;
                mesh.AddVertex(ax, ay, az, 0f, 0f, color);
                mesh.AddVertex(bx, by, bz, 0f, 0f, color);
                mesh.AddVertex(cx, cy, cz, 0f, 0f, color);
                mesh.AddVertex(dx, dy, dz, 0f, 0f, color);
                b1 = b0 + 1; b2 = b0 + 2; b3 = b0 + 3;
            }
            else
            {
                b0 = welder.Emit(ax, ay, az, color);
                b1 = welder.Emit(bx, by, bz, color);
                b2 = welder.Emit(cx, cy, cz, color);
                b3 = welder.Emit(dx, dy, dz, color);
            }

            // Identical winding either way — only which vertex slots the indices point at can differ.
            mesh.AddIndex(b0); mesh.AddIndex(b1); mesh.AddIndex(b2);
            mesh.AddIndex(b0); mesh.AddIndex(b2); mesh.AddIndex(b3);
        }

        /// <summary>
        /// Per-<see cref="Build"/> vertex cache: returns the index of an existing vertex at the same exact
        /// position and colour, or appends a new one. Scoped to a single call — never shared between meshes
        /// and never static, so background materialization batches stay thread-safe.
        /// </summary>
        /// <remarks>
        /// MATCHING IS BIT-EXACT on the float bit patterns, not tolerance-based. That is only sound because
        /// <see cref="Build"/> derives every face coordinate from its OWN integer lattice coordinate (see the
        /// canonical-derivation note in the cube branch); a tolerance match would be both slower and capable
        /// of welding two genuinely distinct positions, e.g. either side of a z-fight inset.
        ///
        /// A hash collision or an unmatched key costs nothing but a duplicate vertex, which is exactly the
        /// pre-welding behaviour — this optimization degrades into correctness rather than corruption.
        /// </remarks>
        private sealed class VertexWelder
        {
            private readonly MeshData _mesh;
            private readonly Dictionary<VertexKey, int> _shared;

            public VertexWelder(MeshData mesh, int expectedFaces)
            {
                _mesh = mesh;
                // A closed voxel shell shares each corner between roughly 2–3 faces, so unique vertices land
                // near 1.5x the face count. Pre-sizing avoids rehashing the largest batches mid-build.
                _shared = new Dictionary<VertexKey, int>(
                    expectedFaces > 0 ? expectedFaces + (expectedFaces >> 1) : 4);
            }

            public int Emit(float x, float y, float z, int color)
            {
                var key = new VertexKey(x, y, z, color);
                if (_shared.TryGetValue(key, out int existing)) return existing;

                int index = _mesh.VerticesCount;
                _mesh.AddVertex(x, y, z, 0f, 0f, color);
                _shared[key] = index;
                return index;
            }
        }

        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            private readonly int _x, _y, _z, _color;

            public VertexKey(float x, float y, float z, int color)
            {
                _x = BitConverter.SingleToInt32Bits(x);
                _y = BitConverter.SingleToInt32Bits(y);
                _z = BitConverter.SingleToInt32Bits(z);
                _color = color;
            }

            public bool Equals(VertexKey other) =>
                _x == other._x && _y == other._y && _z == other._z && _color == other._color;

            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _x;
                    hash = (hash * 397) ^ _y;
                    hash = (hash * 397) ^ _z;
                    return (hash * 397) ^ _color;
                }
            }
        }

        // --- small helpers ---------------------------------------------------------------------------------

        // Textureless, unlit, coloured mesh. Constructor args are (capacityVertices, capacityIndices,
        // withNormals, withUv, withRgba, withFlags) — only RGBA is written here. If a compile flags this line,
        // Module-7 in-game finding (the "solid black arch"): the standard shader expects the full
        // pos+uv+rgba vertex layout; built WITHOUT the uv array, the rgba bytes shifted into the uv slot
        // and the shader's colour input read its default (0,0,0,1) — opaque black (and, before the white
        // texture was bound, the colour bytes were being sampled AS texture coordinates — the "rainbow").
        // So: withUv = true, and every vertex carries uv (0,0), which samples the white texture's corner.
        private static MeshData NewMesh(int voxelCount)
        {
            int vCap = voxelCount > 0 ? voxelCount * 8 : 4;
            int iCap = voxelCount > 0 ? voxelCount * 36 : 6;
            return new MeshData(vCap, iCap, false, true, true, false);
        }

        // Exact allocation for the exposed-face path (Stage A): 4 vertices + 6 indices per emitted face —
        // the pre-count makes over-allocation and growth reallocations both impossible.
        private static MeshData NewMeshForFaces(int faceCount)
        {
            int vCap = faceCount > 0 ? faceCount * 4 : 4;
            int iCap = faceCount > 0 ? faceCount * 6 : 6;
            return new MeshData(vCap, iCap, false, true, true, false);
        }

        private static int PackRgba(float r, float g, float b, float a) =>
            ToByte(r) | (ToByte(g) << 8) | (ToByte(b) << 16) | (ToByte(a) << 24);

        private static int ToByte(float v)
        {
            int i = (int)(v * 255f + 0.5f);
            return i < 0 ? 0 : (i > 255 ? 255 : i);
        }

        private static double Dist2(double x, double y, double z, Vec3d p)
        {
            double dx = x - p.X, dy = y - p.Y, dz = z - p.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static int CeilDiv(int num, int den) => (num + den - 1) / den;
    }
}
