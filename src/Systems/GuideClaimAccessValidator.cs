using System;
using System.Collections.Generic;
using Layout.Guide;
using Layout.Shapes;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Layout.Systems
{
    /// <summary>Result returned by the operation-scoped public-guide access policy.</summary>
    public readonly struct GuideMutationAccessResult
    {
        public bool Allowed { get; }
        public BlockPos DeniedPosition { get; }

        private GuideMutationAccessResult(bool allowed, BlockPos deniedPosition)
        {
            Allowed = allowed;
            DeniedPosition = deniedPosition;
        }

        public static GuideMutationAccessResult Permit() =>
            new GuideMutationAccessResult(true, null);

        public static GuideMutationAccessResult Deny(BlockPos position) =>
            new GuideMutationAccessResult(false, position);
    }

    /// <summary>
    /// Validates a candidate guide after a mutation but before it is committed. <paramref name="before"/>
    /// is null for new/restored guides and an independent snapshot for edits.
    /// </summary>
    public delegate GuideMutationAccessResult GuideMutationAccessValidator(
        GuideData before, GuideData after, IGuideShape afterShape);

    /// <summary>
    /// Converts the guide's exact rendered cells to world blocks and asks Vintage Story's authoritative
    /// land-claim API whether the acting player has BuildOrBreak access to each one.
    /// </summary>
    public sealed class GuideClaimAccessValidator
    {
        private readonly ICoreServerAPI _sapi;
        private readonly IServerPlayer _player;

        public GuideClaimAccessValidator(ICoreServerAPI sapi, IServerPlayer player)
        {
            _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
            _player = player ?? throw new ArgumentNullException(nameof(player));
        }

        public GuideMutationAccessResult Validate(
            GuideData before, GuideData after, IGuideShape afterShape)
        {
            if (after == null || afterShape == null) return GuideMutationAccessResult.Permit();

            HashSet<(int X, int Y, int Z)> deniedAfter = DeniedBlocks(after, afterShape);
            if (deniedAfter.Count == 0) return GuideMutationAccessResult.Permit();

            // Existing guides may become overlapped after a new claim is made. Let a player progressively
            // retreat from that legacy overlap, but never keep it unchanged or introduce a different
            // protected block. New/restored guides have no prior footprint and therefore cannot use this.
            HashSet<(int X, int Y, int Z)> deniedBefore = null;
            if (before != null)
            {
                IGuideShape beforeShape = ShapeFactory.Adopt(before);
                beforeShape.RecalculatePhantomPoints();
                deniedBefore = DeniedBlocks(before, beforeShape);
                if (deniedAfter.IsProperSubsetOf(deniedBefore))
                    return GuideMutationAccessResult.Permit();
            }

            foreach ((int X, int Y, int Z) block in deniedAfter)
            {
                if (deniedBefore == null || !deniedBefore.Contains(block))
                    return GuideMutationAccessResult.Deny(new BlockPos(block.X, block.Y, block.Z));
            }

            foreach ((int X, int Y, int Z) block in deniedAfter)
                return GuideMutationAccessResult.Deny(new BlockPos(block.X, block.Y, block.Z));

            return GuideMutationAccessResult.Permit();
        }

        /// <summary>Checks one draft anchor for immediate, advisory server feedback.</summary>
        public GuideMutationAccessResult ValidateAnchor(Vec3d position)
        {
            if (position == null) return GuideMutationAccessResult.Permit();
            var block = new BlockPos(
                (int)Math.Floor(position.X),
                (int)Math.Floor(position.Y),
                (int)Math.Floor(position.Z));
            return HasBuildAccess(block)
                ? GuideMutationAccessResult.Permit()
                : GuideMutationAccessResult.Deny(block);
        }

        private HashSet<(int X, int Y, int Z)> DeniedBlocks(GuideData guide, IGuideShape shape)
        {
            List<VoxelPosition> voxels = guide.IsWireframe
                ? ShapeWireframe.GetVoxelPositions(shape, guide.VoxelScale)
                : shape.GetVoxelPositions(guide.VoxelScale, guide.IsFilled);
            var tested = new HashSet<(int X, int Y, int Z)>();
            var denied = new HashSet<(int X, int Y, int Z)>();

            for (int i = 0; i < voxels.Count; i++)
            {
                VoxelPosition voxel = voxels[i];
                int bx = BlockCoordinate(voxel.X);
                int by = BlockCoordinate(voxel.Y);
                int bz = BlockCoordinate(voxel.Z);

                if (guide.Projection != ProjectionMode.Surface)
                {
                    TestBlock(bx, by, bz, tested, denied);
                    continue;
                }

                int plane = guide.Plane.PlaneOffset;
                int planeBlock = BlockCoordinate(plane);
                switch (guide.Plane.FlattenedAxis)
                {
                    case PlaneAxis.X: bx = planeBlock; break;
                    case PlaneAxis.Y: by = planeBlock; break;
                    default: bz = planeBlock; break;
                }
                TestBlock(bx, by, bz, tested, denied);

                // A surface tile drawn exactly on a block boundary touches both sides. Protecting both
                // prevents a guide on the outside face of a claim from slipping through via rounding.
                if (plane % 16 == 0)
                {
                    int adjacent = BlockCoordinate(plane - 1);
                    switch (guide.Plane.FlattenedAxis)
                    {
                        case PlaneAxis.X: TestBlock(adjacent, by, bz, tested, denied); break;
                        case PlaneAxis.Y: TestBlock(bx, adjacent, bz, tested, denied); break;
                        default: TestBlock(bx, by, adjacent, tested, denied); break;
                    }
                }
            }

            return denied;
        }

        private void TestBlock(int x, int y, int z,
            HashSet<(int X, int Y, int Z)> tested,
            HashSet<(int X, int Y, int Z)> denied)
        {
            var key = (x, y, z);
            if (!tested.Add(key)) return;
            if (!HasBuildAccess(new BlockPos(x, y, z))) denied.Add(key);
        }

        private bool HasBuildAccess(BlockPos position) =>
            _sapi.World.Claims.TestAccess(_player, position, EnumBlockAccessFlags.BuildOrBreak)
                == EnumWorldAccessResponse.Granted;

        private static int BlockCoordinate(int sixteenths) =>
            (int)Math.Floor(sixteenths / 16.0);
    }
}
