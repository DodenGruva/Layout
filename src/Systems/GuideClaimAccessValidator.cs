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
            var denied = new HashSet<(int X, int Y, int Z)>();
            List<BlockPos> footprint = BuildFootprint(guide, shape);
            for (int i = 0; i < footprint.Count; i++)
                if (!HasBuildAccess(footprint[i]))
                    denied.Add((footprint[i].X, footprint[i].Y, footprint[i].Z));

            return denied;
        }

        /// <summary>
        /// Pure geometry half of claim validation. It materialises and collapses a guide's render cells to
        /// the distinct world blocks they touch, but deliberately makes no world/claim API calls. Immense
        /// create validation can therefore run this part on its isolated worker and time-slice only the
        /// resulting authoritative claim lookups on the server thread.
        /// </summary>
        /// <param name="cancelled">
        /// Optional abandon probe for the off-thread immense callers. When it returns true this method
        /// returns <c>null</c> — NOT an empty list. An empty footprint reads as "no blocks to check", which
        /// would skip claim validation entirely; a null is impossible to mistake for a completed one and
        /// forces the caller to discard the whole result. The synchronous caller passes nothing and is
        /// therefore unaffected.
        /// </param>
        internal static List<BlockPos> BuildFootprint(
            GuideData guide, IGuideShape shape, Func<bool> cancelled = null)
        {
            var result = new List<BlockPos>();
            if (guide == null || shape == null) return result;

            List<VoxelPosition> voxels = guide.IsWireframe
                ? ShapeWireframe.GetVoxelPositions(shape, guide.VoxelScale)
                : shape.GetVoxelPositions(guide.VoxelScale, guide.IsFilled);
            var tested = new HashSet<(int X, int Y, int Z)>();

            void Add(int x, int y, int z)
            {
                if (tested.Add((x, y, z))) result.Add(new BlockPos(x, y, z));
            }

            for (int i = 0; i < voxels.Count; i++)
            {
                // Every 2048 cells — often enough to drop a cancelled immense guide promptly, rare enough
                // that the delegate call is nothing beside the collapse itself.
                if ((i & 2047) == 0 && cancelled != null && cancelled()) return null;

                VoxelPosition voxel = voxels[i];
                int bx = BlockCoordinate(voxel.X);
                int by = BlockCoordinate(voxel.Y);
                int bz = BlockCoordinate(voxel.Z);

                if (guide.Projection != ProjectionMode.Surface)
                {
                    Add(bx, by, bz);
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
                Add(bx, by, bz);

                if (plane % 16 == 0)
                {
                    int adjacent = BlockCoordinate(plane - 1);
                    switch (guide.Plane.FlattenedAxis)
                    {
                        case PlaneAxis.X: Add(adjacent, by, bz); break;
                        case PlaneAxis.Y: Add(bx, adjacent, bz); break;
                        default: Add(bx, by, adjacent); break;
                    }
                }
            }

            return result;
        }

        private bool HasBuildAccess(BlockPos position) =>
            _sapi.World.Claims.TestAccess(_player, position, EnumBlockAccessFlags.BuildOrBreak)
                == EnumWorldAccessResponse.Granted;

        private static int BlockCoordinate(int sixteenths) =>
            (int)Math.Floor(sixteenths / 16.0);
    }
}
