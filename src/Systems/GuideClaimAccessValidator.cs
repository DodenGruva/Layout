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

            // ⚠️ DO NOT ADD A BOUNDING-BOX PRE-FILTER HERE. One was written and shipped in v0.4.48 and
            // withdrawn in v0.4.49 — see the block comment below TryConservativeFootprintRect for why the
            // geometry was sound and the premise was not.

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

        /// <summary>
        /// Blocks a guide's own footprint could ever reach beyond the box its control points span, in
        /// blocks, ON TOP of the span itself. Pure slack — see <see cref="TryConservativeFootprintRect"/>.
        /// </summary>
        private const int FootprintPadBlocks = 4;

        // ==========================================================================================
        //  WITHDRAWN: the bounding-box claim pre-filter (added v0.4.48, removed v0.4.49)
        // ==========================================================================================
        //
        // THE IDEA. Before voxelising a guide and asking the claim API about every world block it touches
        // — work that runs on every drag update — test a generous box around its control points against
        // the world's land claims. If nothing intersects, skip the exact check. Measured 1,800x cheaper
        // than the work it replaced, and it still won at 5,000 claims.
        //
        // THE GEOMETRY WAS RIGHT. TryConservativeFootprintRect below is retained and was verified across
        // 5,400 configurations — every shape, size, scale, orientation and projection — with every voxel
        // falling inside the box and four blocks of margin to spare.
        //
        // ⚠️ THE PREMISE WAS WRONG, AND THAT IS THE WHOLE LESSON. The filter treated
        // ILandClaimAPI.All as the authority behind TestAccess. It is not. TestAccess answers with SEVEN
        // possible responses, and only LandClaimed comes from that list:
        //
        //     Granted · LandClaimed · NoPrivilege · InSpectatorMode · InGuestMode · PlayerDead
        //     · DeniedByMod   <-- ANY other mod may deny ANY position, for reasons we cannot enumerate
        //
        // So "no land claim overlaps this box" never meant "TestAccess would grant every block in it".
        // A player without the build privilege, a dead player, or an area protected by another mod would
        // all have been waved through. **A filter in front of a permission check has to be certain about
        // the WHOLE check, not the part of it you happened to read.**
        //
        // IF IT IS REVISITED: a single TestAccess probe would settle the player-global reasons (privilege,
        // spectator, guest, dead) cheaply, and the box handles LandClaimed — but DeniedByMod is positional
        // and unknowable, so a sound design needs an answer for that FIRST. Do not start from the box.
        //
        // Found the honest way: reported from play on a trader's plot, 2026-08-01, where guides could be
        // placed on protected land. Correctness over performance — the standing rule — decides this.

        /// <summary>
        /// A horizontal rectangle, in block coordinates, that is guaranteed to CONTAIN every world block
        /// <see cref="BuildFootprint"/> could produce for this guide. Deliberately loose.
        ///
        /// <para>**NOTHING CALLS THIS TODAY.** It is kept because it is verified correct and would be the
        /// geometry half of any future pre-filter — but read the withdrawal note above before wiring it to
        /// anything, because being right about the geometry was never the hard part.</para>
        /// </summary>
        /// <remarks>
        /// WHY THE CONTROL POINTS ALONE ARE NOT ENOUGH. A shape is not confined to the box its control
        /// points span: an ellipse or sphere is a centre plus a rim point and extends a full radius in every
        /// direction; a Catmull-Rom spline bows outside its own points; a voxel cell reaches half a cell
        /// past the geometry it came from. So the box is padded by its own DIAGONAL, which is at least the
        /// distance between any two control points and therefore at least any radius they imply, plus
        /// <see cref="FootprintPadBlocks"/> of pure slack.
        ///
        /// TIGHTNESS IS WORTH NOTHING HERE. The filter only has to reject guides that are FAR from a claim,
        /// and a claim is either absent or usually a long way off. Making the box three times bigger than it
        /// needs to be costs almost no filtering power and buys a lot of margin against a shape whose
        /// extent surprises us. **If in doubt, pad more.**
        ///
        /// ⚠️ SURFACE PROJECTION MOVES THE FOOTPRINT OFF THE CONTROL POINTS ENTIRELY. A flattened guide
        /// reports its blocks at the projection PLANE, which can sit anywhere — so the plane is unioned in
        /// rather than assumed to be nearby. Union, never replace: the un-flattened axes still span normally.
        ///
        /// Public so it can be verified directly against real voxel output rather than by argument.
        /// </remarks>
        public static bool TryConservativeFootprintRect(GuideData guide, out HorRectanglei rect)
        {
            rect = default;
            List<ControlPoint> points = guide?.ControlPoints;
            if (points == null || points.Count == 0) return false;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            // PHANTOMS INCLUDED. They steer the curve's ends and sit outside the real points — an arch's
            // are below its feet — so excluding them would shrink the very box that has to contain them.
            for (int i = 0; i < points.Count; i++)
            {
                Vec3d p = points[i]?.WorldPosition;
                if (p == null) continue;
                if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(p.Z)) return false;
                if (double.IsInfinity(p.X) || double.IsInfinity(p.Y) || double.IsInfinity(p.Z)) return false;
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
                any = true;
            }
            if (!any) return false;

            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            double span = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (double.IsNaN(span) || double.IsInfinity(span)) return false;

            double x1 = Math.Floor(minX - span), x2 = Math.Ceiling(maxX + span);
            double z1 = Math.Floor(minZ - span), z2 = Math.Ceiling(maxZ + span);

            if (guide.Projection == ProjectionMode.Surface)
            {
                // Mirrors BuildFootprint exactly, including the adjacent block it adds on a plane boundary.
                int planeBlock = BlockCoordinate(guide.Plane.PlaneOffset);
                int adjacent = BlockCoordinate(guide.Plane.PlaneOffset - 1);
                int lo = Math.Min(planeBlock, adjacent), hi = Math.Max(planeBlock, adjacent);
                switch (guide.Plane.FlattenedAxis)
                {
                    case PlaneAxis.X: x1 = Math.Min(x1, lo); x2 = Math.Max(x2, hi); break;
                    case PlaneAxis.Y: break;              // height only — a 2D rect is unaffected
                    default: z1 = Math.Min(z1, lo); z2 = Math.Max(z2, hi); break;
                }
            }

            // ⚠️ THE SLACK GOES ON LAST, AFTER the projection plane is unioned in — not folded into the
            // span above. Verified 2026-08-01 across 5,400 configurations: with the slack applied before
            // the union, a flattened guide's blocks landed EXACTLY on the rectangle's edge, zero margin,
            // because the union pins the edge at the plane itself. Whether the claim API's own test treats
            // that edge as inside is not something this code should have to know.
            x1 -= FootprintPadBlocks; x2 += FootprintPadBlocks;
            z1 -= FootprintPadBlocks; z2 += FootprintPadBlocks;

            // The rect is int-based; a guide near the world edge must not wrap into a valid coordinate.
            if (x1 < int.MinValue / 2 || x2 > int.MaxValue / 2
                || z1 < int.MinValue / 2 || z2 > int.MaxValue / 2) return false;

            rect = new HorRectanglei((int)x1, (int)z1, (int)x2, (int)z2);
            return true;
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

            VoxelScanCancellation.ThrowIfRequested(cancelled);

            List<VoxelPosition> voxels = guide.IsWireframe
                ? ShapeWireframe.GetVoxelPositions(shape, guide.VoxelScale)
                : GuideShapeVoxelGeneration.GetPositions(
                    shape, guide.VoxelScale, guide.IsFilled, cancelled);
            VoxelScanCancellation.ThrowIfRequested(cancelled);
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
