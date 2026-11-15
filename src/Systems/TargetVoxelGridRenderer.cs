using System;
using Layout.Client;
using Layout.Config;
using Layout.Guide;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace Layout.Systems
{
    /// <summary>Draws the face grid and the targeted voxel outline at the selected guide scale.</summary>
    internal sealed class TargetVoxelGridRenderer : IRenderer
    {
        private const EnumRenderStage Stage = EnumRenderStage.Opaque;
        private const EnumRenderStage OutlineStage = EnumRenderStage.AfterFinalComposition;
        private readonly ICoreClientAPI _capi;
        private readonly LayoutClientConfig _config;
        private readonly Func<int?> _activeScale;
        private readonly WireframeCube _wireframe = new WireframeCube();
        private WireframeCube _outline;
        private FaceGrid? _cachedFace;

        public double RenderOrder => 0.9;
        public int RenderRange => 24;

        public TargetVoxelGridRenderer(ICoreClientAPI capi, LayoutClientConfig config, Func<int?> activeScale)
        {
            _capi = capi;
            _config = config;
            _activeScale = activeScale;
            _capi.Event.RegisterRenderer(this, Stage, "layout-target-grid");
            _capi.Event.RegisterRenderer(this, OutlineStage, "layout-target-voxel");
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            bool drawOutline = stage == OutlineStage && _config.OutlineTargetedVoxel;
            bool drawGrid = stage == Stage && _config.DisplayVoxelGrid;
            if ((!drawOutline && !drawGrid) || _activeScale() is not int scale
                || !GuideData.IsValidVoxelScale(scale)) return;
            var player = _capi.World?.Player;
            BlockSelection selection = player?.CurrentBlockSelection;
            if (player?.Entity == null || selection?.Position == null || selection.Face == null) return;

            var accessor = _capi.World.BlockAccessor;
            BlockPos pos = selection.DidOffset
                ? selection.Position.AddCopy(selection.Face.Opposite) : selection.Position;
            Block block = accessor.GetBlock(pos);
            Cuboidf[] boxes = block?.GetSelectionBoxes(accessor, pos);
            int index = selection.SelectionBoxIndex;
            Cuboidf box = (uint)index < (uint)(boxes?.Length ?? 0) ? boxes[index] : null;
            if (drawOutline)
            {
                DrawOutline(selection, box, pos, block, scale, player.Entity.CameraPosOffset);
                return;
            }
            FaceGrid face = FaceGrid.FromSelection(selection, box, scale);
            if (_cachedFace != face)
            {
                MeshData data = face.BuildMesh();
                MeshRef mesh = data.VerticesCount > 0 ? _capi.Render.UploadMesh(data) : null;
                _wireframe.modelRef?.Dispose();
                _wireframe.modelRef = mesh;
                _cachedFace = face;
            }

            if (_wireframe.modelRef == null) return;
            try
            {
                _wireframe.Render(_capi, pos.X, pos.InternalY, pos.Z, 1f, 1f, 1f, lineWidth: 1f);
            }
            finally
            {
                _capi.Render.GlToggleBlend(false);
            }
        }

        private void DrawOutline(BlockSelection selection, Cuboidf box, BlockPos pos, Block block,
            int scale, Vec3d cameraOffset)
        {
            if (_capi.HideGuis || selection.HitPosition == null) return;
            Vec3d voxel = TargetVoxelOrigin(selection, box, scale);
            float edge = (float)scale / VoxelGrid.CellsPerBlock;

            // Vanilla only picks a contrasting color while a chisel is held. Use the same
            // first material and brightness cutoff while the Layout tool is held.
            if (block is BlockMicroBlock)
            {
                var microBlock = _capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityMicroBlock;
                block = microBlock?.BlockIds is { Length: > 0 } ids ? _capi.World.GetBlock(ids[0]) : null;
            }
            Vec4f color = block == null ? new Vec4f(0f, 0f, 0f, 0.6f) : OutlineColor(block.GetColor(_capi, pos));

            // Match the engine's selected-block outline pass, geometry, color and line thickness.
            _outline ??= WireframeCube.CreateUnitCube(_capi, -1);
            _outline.Render(_capi,
                pos.X + voxel.X + cameraOffset.X, pos.InternalY + voxel.Y + cameraOffset.Y,
                pos.Z + voxel.Z + cameraOffset.Z, edge, edge, edge,
                1.6f * _capi.Settings.Float["wireframethickness"], color);
        }

        internal static Vec4f OutlineColor(int color)
        {
            int brightnessSum = (color & 255) + ((color >> 8) & 255) + ((color >> 16) & 255);
            return brightnessSum < 3 * 102 ? new Vec4f(1f, 1f, 1f, 0.6f) : new Vec4f(0f, 0f, 0f, 0.5f);
        }

        internal static Vec3d TargetVoxelOrigin(BlockSelection selection, Cuboidf box, int scale)
        {
            // A flat selection box can have no interior along a tangential axis.
            if (box != null && Math.Min(box.XSize, Math.Min(box.YSize, box.ZSize)) <= 0.000002f)
                box = null;
            Vec3d center = GuideToolController.ResolveBlockPlacement(
                selection, scale, ProjectionMode.Volumetric, embed: true, chiselBox: box);
            double half = scale / (2 * VoxelGrid.UnitsPerBlock);
            return new Vec3d(center.X - selection.Position.X - half,
                center.Y - selection.Position.Y - half, center.Z - selection.Position.Z - half);
        }

        public void Dispose()
        {
            _capi.Event.UnregisterRenderer(this, Stage);
            _capi.Event.UnregisterRenderer(this, OutlineStage);
            _wireframe.Dispose();
            _outline?.Dispose();
        }

        internal readonly record struct FaceGrid(
            PlaneAxis Axis, float Offset, float MinU, float MaxU, float MinV, float MaxV, int Scale)
        {
            public static FaceGrid FromSelection(BlockSelection selection, Cuboidf box, int scale)
            {
                Vec3i n = selection.Face.Normali;
                Vec3d hit = selection.HitPosition;
                const float outset = 0.0005f;

                float Face(float min, float max, double? hitCoordinate, int sign) => (box == null && hitCoordinate.HasValue
                    ? (float)hitCoordinate.Value
                    : sign > 0 ? max : min) + sign * outset;

                if (n.X != 0)
                    return new FaceGrid(PlaneAxis.X, Face(box?.X1 ?? 0, box?.X2 ?? 1, hit?.X, n.X),
                        box?.Y1 ?? 0, box?.Y2 ?? 1, box?.Z1 ?? 0, box?.Z2 ?? 1, scale);
                if (n.Z != 0)
                    return new FaceGrid(PlaneAxis.Z, Face(box?.Z1 ?? 0, box?.Z2 ?? 1, hit?.Z, n.Z),
                        box?.X1 ?? 0, box?.X2 ?? 1, box?.Y1 ?? 0, box?.Y2 ?? 1, scale);
                return new FaceGrid(PlaneAxis.Y, Face(box?.Y1 ?? 0, box?.Y2 ?? 1, hit?.Y, n.Y),
                    box?.X1 ?? 0, box?.X2 ?? 1, box?.Z1 ?? 0, box?.Z2 ?? 1, scale);
            }

            public MeshData BuildMesh()
            {
                int cells = VoxelGrid.CellsPerBlock / Scale;
                int capacity = 4 * (cells + 1);
                var mesh = new MeshData(capacity, capacity, withNormals: false, withUv: false);
                mesh.SetMode(EnumDrawMode.Lines);
                float left = Math.Max(0, MinU), right = Math.Min(1, MaxU);
                float bottom = Math.Max(0, MinV), top = Math.Min(1, MaxV);
                float depth = Offset;
                PlaneAxis axis = Axis;
                if (!(left < right && bottom < top) || !float.IsFinite(depth)) return mesh;

                Line(left, bottom, left, top);
                Line(right, bottom, right, top);
                Line(left, bottom, right, bottom);
                Line(left, top, right, top);
                for (int i = 1; i < cells; i++)
                {
                    float t = (float)i / cells;
                    if (t > left && t < right) Line(t, bottom, t, top);
                    if (t > bottom && t < top) Line(left, t, right, t);
                }

                mesh.Flags = new int[mesh.VerticesCount];
                return mesh;

                void Line(float u1, float v1, float u2, float v2)
                {
                    Vertex(u1, v1);
                    Vertex(u2, v2);
                }

                void Vertex(float u, float v)
                {
                    // The engine's wireframe shader uses the same packed RGBA order as guide meshes.
                    const int color = unchecked((int)0xcce2e2e2);
                    mesh.AddIndex(mesh.VerticesCount);
                    switch (axis)
                    {
                        case PlaneAxis.X: mesh.AddVertexSkipTex(depth, u, v, color); break;
                        case PlaneAxis.Z: mesh.AddVertexSkipTex(u, v, depth, color); break;
                        default: mesh.AddVertexSkipTex(u, depth, v, color); break;
                    }
                }
            }
        }
    }
}
