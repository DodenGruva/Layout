using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Systems
{
    /// <summary>
    /// Options controlling how <see cref="GuideMeshBuilder.Build"/> turns a voxel set into a mesh: the voxel
    /// scale, the projection mode/plane, the mesh origin, and the runtime overrides the pure shape cannot know
    /// (anchor-coplanarity shade, grabbed-point highlight, hidden-guide dimming).
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
        /// <see cref="VoxelRenderType.Anchor"/> voxels into the start foot (always Blue) and the far foot.
        /// Null → every anchor voxel renders Blue (no coplanarity split).
        /// </summary>
        public Vec3d StartAnchor = null;

        /// <summary>
        /// The far anchor world position — the arch's index-(N−2) foot. When it is NOT coplanar with
        /// <see cref="StartAnchor"/> (same elevation AND on a cardinal line from it), the far foot's anchor
        /// voxels take the Indigo off-shade instead of Blue.
        /// </summary>
        public Vec3d FarAnchor = null;

        /// <summary>
        /// World position of the control point currently held in an active drag, or null. Exactly ONE voxel —
        /// the rendered voxel whose centre is nearest to it — is overridden to White, which takes precedence
        /// over every resting colour. (Single-voxel nearest-claim, same approach as the shape's apex/anchor
        /// markers; the old marker-radius blob over-coloured, per Session-7 finding B2.)
        /// </summary>
        public Vec3d GrabbedPoint = null;

        /// <summary>
        /// Hidden guide: only anchor voxels are emitted, at reduced alpha, so the guide reads as two faint foot
        /// markers rather than its full body. The Blue/Indigo coplanarity cue is preserved.
        /// </summary>
        public bool Hidden = false;
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
    ///   Normal → Yellow, Locked → Red, Primary/apex → Green, Anchor → Blue,
    ///   far Anchor when not level+cardinal → Indigo off-shade, Grabbed → White,
    ///   hidden-guide anchors → the anchor colour at reduced alpha.
    ///
    /// ANCHOR SPLIT. The shape tags BOTH feet <see cref="VoxelRenderType.Anchor"/> and cannot express the
    /// pairwise "is the far foot clean?" test, so that lives here. Given the two anchor world positions, each
    /// Anchor voxel is bucketed to its nearer foot: the start foot is always Blue; the far foot is Blue when
    /// coplanar (same elevation AND on a cardinal line from the start) and Indigo otherwise. With no anchors
    /// supplied, every Anchor voxel falls back to Blue.
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
        // --- Colour table (RGBA, 0..1). ---------------------------------------------------------------
        // RGBs are fixed (the settled colour language); ALPHAS are client-configurable via
        // layout-client.json (Session-8, item 2) — ConfigureOpacities() overwrites the [3] slot of each
        // array at client start. The values below are only the pre-configuration defaults and MUST match
        // LayoutClientConfig's defaults (body/locked/apex 0.5; anchor 0.8; grabbed 0.95).
        private static readonly float[] ColYellow = { 1.00f, 0.85f, 0.10f, 0.50f }; // Normal body
        private static readonly float[] ColRed    = { 0.90f, 0.15f, 0.15f, 0.50f }; // Locked
        private static readonly float[] ColGreen  = { 0.20f, 0.90f, 0.30f, 0.50f }; // Primary / apex
        private static readonly float[] ColBlue   = { 0.20f, 0.50f, 1.00f, 0.80f }; // Anchor (aligned)
        private static readonly float[] ColIndigo = { 0.45f, 0.45f, 1.00f, 0.80f }; // Anchor far off-shade
        private static readonly float[] ColWhite  = { 1.00f, 1.00f, 1.00f, 0.95f }; // Grabbed
        // Session-9 division marks: magenta — the one hue distinct from all six existing roles
        // (yellow/red/green/blue/indigo/white). [Flagged: color choice open to review.]
        private static readonly float[] ColMagenta = { 0.90f, 0.20f, 0.90f, 0.80f }; // Division mark

        // Hidden guides show their anchors only, at this alpha (rgb still Blue/Indigo). Configurable too.
        private static float HiddenAnchorAlpha = 0.35f;

        /// <summary>
        /// Applies the client-configured opacities (see <c>LayoutClientConfig</c>). Called once by
        /// <c>LayoutModSystem</c> at client start, before any mesh is built; meshes built afterwards pick
        /// the values up automatically. Values are pre-clamped by the config's <c>Normalize()</c>.
        /// </summary>
        public static void ConfigureOpacities(
            float body, float locked, float apex, float anchor, float grabbed, float hiddenAnchor)
        {
            ColYellow[3] = body;
            ColRed[3] = locked;
            ColGreen[3] = apex;
            ColBlue[3] = anchor;
            ColIndigo[3] = anchor;   // the off-shade is the same role at the same alpha
            ColWhite[3] = grabbed;
            HiddenAnchorAlpha = hiddenAnchor;
        }

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

            MeshData mesh = NewMesh(voxels.Count);

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
                    r = ColWhite[0]; g = ColWhite[1]; b = ColWhite[2]; a = ColWhite[3];
                }
                else
                {
                    ResolveColor(v.Type, cx, cy, cz, start, far, haveBothAnchors, farAligned,
                                 out r, out g, out b, out a);
                    if (options.Hidden) a = HiddenAnchorAlpha; // only anchor voxels reach here when hidden
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
                    float lx = (float)(v.X / 16.0 - ox) + ix;
                    float ly = (float)(v.Y / 16.0 - oy) + iy;
                    float lz = (float)(v.Z / 16.0 - oz) + iz;
                    AddBox(mesh, lx, ly, lz, edge - 2f * ix, edge - 2f * iy, edge - 2f * iz, color);
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
        /// The resting RGBA (0..1) for a render type, ignoring the runtime overrides (anchors resolve to Blue).
        /// Handy for a HUD colour legend; the actual mesh applies the anchor split and grabbed override.
        /// </summary>
        public static (float r, float g, float b, float a) BaseColor(VoxelRenderType type)
        {
            float[] c = ColorArray(type);
            return (c[0], c[1], c[2], c[3]);
        }

        // --- colour resolution -----------------------------------------------------------------------------

        private static void ResolveColor(
            VoxelRenderType type, double cx, double cy, double cz,
            Vec3d start, Vec3d far, bool haveBothAnchors, bool farAligned,
            out float r, out float g, out float b, out float a)
        {
            float[] c = ColorArray(type);

            // Only Anchor voxels can take the far-foot off-shade, and only when the far foot is not aligned.
            if (type == VoxelRenderType.Anchor && haveBothAnchors && !farAligned)
            {
                double dStart = Dist2(cx, cy, cz, start);
                double dFar = Dist2(cx, cy, cz, far);
                if (dFar < dStart) c = ColIndigo; // this voxel belongs to the far foot
            }

            r = c[0]; g = c[1]; b = c[2]; a = c[3];
        }

        private static float[] ColorArray(VoxelRenderType type)
        {
            switch (type)
            {
                case VoxelRenderType.Locked:  return ColRed;
                case VoxelRenderType.Primary: return ColGreen;
                case VoxelRenderType.Anchor:  return ColBlue;
                case VoxelRenderType.Grabbed: return ColWhite;
                case VoxelRenderType.Division: return ColMagenta;
                default:                      return ColYellow; // Normal
            }
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
                    AddQuad(mesh, x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1, color);
                    break;
                }
                case PlaneAxis.Z: // vertical N–S: vary X/Y at z = offset
                {
                    float z = (float)(planeW - oz);
                    float x0 = (float)(v.X / 16.0 - ox), x1 = x0 + e;
                    float y0 = (float)(v.Y / 16.0 - oy), y1 = y0 + e;
                    AddQuad(mesh, x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z, color);
                    break;
                }
                default: // PlaneAxis.X — vertical E–W: vary Z/Y at x = offset
                {
                    float x = (float)(planeW - ox);
                    float z0 = (float)(v.Z / 16.0 - oz), z1 = z0 + e;
                    float y0 = (float)(v.Y / 16.0 - oy), y1 = y0 + e;
                    AddQuad(mesh, x, y0, z0, x, y0, z1, x, y1, z1, x, y1, z0, color);
                    break;
                }
            }
        }

        private static void AddQuad(MeshData mesh,
            float ax, float ay, float az, float bx, float by, float bz,
            float cx, float cy, float cz, float dx, float dy, float dz, int color)
        {
            int b0 = mesh.VerticesCount;
            mesh.AddVertex(ax, ay, az, 0f, 0f, color);
            mesh.AddVertex(bx, by, bz, 0f, 0f, color);
            mesh.AddVertex(cx, cy, cz, 0f, 0f, color);
            mesh.AddVertex(dx, dy, dz, 0f, 0f, color);
            mesh.AddIndex(b0);     mesh.AddIndex(b0 + 1); mesh.AddIndex(b0 + 2);
            mesh.AddIndex(b0);     mesh.AddIndex(b0 + 2); mesh.AddIndex(b0 + 3);
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
