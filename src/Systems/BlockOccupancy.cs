using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Layout.Systems
{
    /// <summary>How much of a world block is occupied by material.</summary>
    public enum BlockFill
    {
        /// <summary>Air, or a chunk that is not loaded (see the note on <see cref="BlockOccupancy"/>).</summary>
        Empty,

        /// <summary>Every 1/16 cell is material — a normal full block.</summary>
        Solid,

        /// <summary>Some cells are material and some are not — chiselled blocks, slabs, stairs, fences.</summary>
        Partial
    }

    /// <summary>
    /// Sub-block material occupancy of the world, at 1/16 resolution — "is there material in this cell?"
    /// STAGE 1 of <c>PLAN_BLOCK_OCCUPANCY.md</c>: the world-reading half, with no rendering attached.
    /// </summary>
    /// <remarks>
    /// THE SHAPE OF THE DATA is the human's correction to an earlier design (plan §0.9). A dense 1/16 grid
    /// over a guide's bounding box is infeasible — 33 MB for a 40-block cube, half a gigabyte for a
    /// 100-block one, growing with the CUBE of guide size — because guides are sparse (hollow shells) while
    /// such a grid is dense. The world is not dense either: virtually every block is wholly solid or wholly
    /// air, and only chiselled ones carry sub-block detail. So this stores
    ///
    ///   * one <see cref="BlockFill"/> per world block, and
    ///   * a 4096-bit brick ONLY for blocks that are actually partial.
    ///
    /// Cost then tracks how much has been chiselled, not the volume being looked at.
    ///
    /// STAGE 1 DELIBERATELY KEEPS THIS A DICTIONARY, not the packed array the GPU will eventually want.
    /// The question this stage answers — can sub-block occupancy be read correctly and cheaply? — does not
    /// depend on the storage layout, and a dictionary needs no bounding box, so nothing here has to agree
    /// with how a guide computes its extent yet. Packing is a Stage 2 concern.
    ///
    /// UNLOADED CHUNKS READ AS EMPTY. This is the opposite of the conservative direction the retired
    /// z-fight probe used, and deliberately so (plan §7.3): a spurious "material here" is a lie about the
    /// player's own building work, while a missing one merely fails to help.
    ///
    /// COLLISION BOXES ARE THE SOURCE, for every block type. For a chiselled block they ARE the chiselled
    /// cuboids, so the answer is exact in the case that matters most, and no per-block-type knowledge is
    /// needed for anything else. The plan's Path B (<c>BlockEntityMicroBlock.GetVoxelMaterialAt</c>) stays
    /// available as a precision refinement if collision shape turns out to disagree with visible material
    /// on blocks people actually build with (plan §7.2) — <see cref="HasMicroBlockEntity"/> exists so the
    /// diagnostic can flag exactly those blocks for comparison.
    ///
    /// THREAD SAFE, by one lock around the cache. Guide meshing runs on background materialization threads
    /// and calls this once per body voxel, so it has to be. The lock is uncontended in practice — the world
    /// read only happens on a miss, and a large guide's voxels cluster into few blocks — but on a guide with
    /// millions of voxels it is still millions of acquisitions, which is why the cost is deliberately paid
    /// on the background thread rather than the render thread. <c>Block.GetCollisionBoxes</c> is documented
    /// as thread-safe, so the world read inside the lock is legitimate.
    /// </remarks>
    public sealed class BlockOccupancy
    {
        /// <summary>Cells along one block edge. A chisel voxel and a Layout scale-1 voxel are both 1/16.</summary>
        public const int CellsPerBlock = 16;

        private const int CellsPerBrick = CellsPerBlock * CellsPerBlock * CellsPerBlock;   // 4096
        private const int BrickWords = CellsPerBrick / 64;                                  // 64 ulongs = 512 B

        private readonly struct Entry
        {
            public readonly BlockFill Fill;

            /// <summary>4096 bits, one per 1/16 cell. Null unless <see cref="Fill"/> is Partial.</summary>
            public readonly ulong[] Brick;

            public Entry(BlockFill fill, ulong[] brick) { Fill = fill; Brick = brick; }
        }

        // CONCURRENT, not a lock (v0.3.84). Meshing calls this once per body voxel — hundreds of thousands
        // of times for one guide — and a lock around every one of those cost real milliseconds on top of a
        // rebuild that was already too slow. Reads here are lock-free; the only cost is that two threads
        // racing on the same missing block may both read the world, which wastes a little work and produces
        // the same answer.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Entry> _blocks =
            new System.Collections.Concurrent.ConcurrentDictionary<long, Entry>();

        // Running totals, for the diagnostic command. Not load-bearing, hence plain interlocked counters.
        private int _solid, _empty, _partial;

        public int CachedBlocks => _blocks.Count;
        public int SolidBlocks => _solid;
        public int EmptyBlocks => _empty;
        public int PartialBlocks => _partial;

        /// <summary>Approximate resident size in bytes: the per-block entries plus one brick per partial.</summary>
        public long ApproximateBytes => (long)_blocks.Count * 24 + (long)PartialBlocks * BrickWords * 8;

        public void Clear()
        {
            _blocks.Clear();
            _solid = _empty = _partial = 0;
        }

        /// <summary>Drops one block's cached answer, so the next query re-reads it. Stage 3 uses this.</summary>
        public void Invalidate(BlockPos pos)
        {
            if (pos == null) return;
            if (!_blocks.TryRemove(Key(pos.X, pos.Y, pos.Z), out Entry old)) return;
            switch (old.Fill)
            {
                case BlockFill.Solid: System.Threading.Interlocked.Decrement(ref _solid); break;
                case BlockFill.Empty: System.Threading.Interlocked.Decrement(ref _empty); break;
                default: System.Threading.Interlocked.Decrement(ref _partial); break;
            }
        }

        /// <summary>
        /// THE QUERY. Coordinates are absolute 1/16 cell units — the same space
        /// <see cref="Layout.Guide.VoxelPosition"/> uses, so a scale-1 guide voxel maps 1:1 with no
        /// approximation.
        /// </summary>
        public bool IsMaterialAt(IBlockAccessor accessor, int x16, int y16, int z16)
        {
            if (accessor == null) return false;

            // Arithmetic shift floors correctly for negative coordinates; division would truncate toward
            // zero and put x16 = -1 in block 0 instead of block -1.
            int bx = x16 >> 4, by = y16 >> 4, bz = z16 >> 4;
            Entry e = GetOrBuild(accessor, bx, by, bz);

            switch (e.Fill)
            {
                case BlockFill.Empty: return false;
                case BlockFill.Solid: return true;
                default:
                    int index = CellIndex(x16 & 15, y16 & 15, z16 & 15);
                    return (e.Brick[index >> 6] & (1UL << (index & 63))) != 0;
            }
        }

        /// <summary>Classification of one world block, reading and caching it if not already known.</summary>
        public BlockFill FillAt(IBlockAccessor accessor, BlockPos pos) =>
            accessor == null || pos == null
                ? BlockFill.Empty
                : GetOrBuild(accessor, pos.X, pos.Y, pos.Z).Fill;

        /// <summary>Number of the 4096 cells that carry material. Cheap: reads the cached brick.</summary>
        public int FilledCellCount(IBlockAccessor accessor, BlockPos pos)
        {
            if (accessor == null || pos == null) return 0;
            Entry e = GetOrBuild(accessor, pos.X, pos.Y, pos.Z);
            if (e.Fill == BlockFill.Empty) return 0;
            if (e.Fill == BlockFill.Solid) return CellsPerBrick;

            int n = 0;
            for (int i = 0; i < BrickWords; i++) n += System.Numerics.BitOperations.PopCount(e.Brick[i]);
            return n;
        }

        /// <summary>
        /// Whether this position carries a chiselled block entity. Diagnostic only — the occupancy answer
        /// does not depend on it (see the Path A / Path B note on this class), but being able to tell which
        /// blocks are microblocks is what lets a playtest check collision shape against visible material.
        /// </summary>
        public static bool HasMicroBlockEntity(IBlockAccessor accessor, BlockPos pos) =>
            accessor?.GetBlockEntity<Vintagestory.GameContent.BlockEntityMicroBlock>(pos) != null;

        // ---------------------------------------------------------------------------------------------

        private Entry GetOrBuild(IBlockAccessor accessor, int bx, int by, int bz)
        {
            long key = Key(bx, by, bz);
            if (_blocks.TryGetValue(key, out Entry cached)) return cached;   // the overwhelmingly common path

            Entry built = Read(accessor, bx, by, bz);
            if (!_blocks.TryAdd(key, built)) return _blocks[key];            // lost a race; same answer
            switch (built.Fill)
            {
                case BlockFill.Solid: System.Threading.Interlocked.Increment(ref _solid); break;
                case BlockFill.Empty: System.Threading.Interlocked.Increment(ref _empty); break;
                default: System.Threading.Interlocked.Increment(ref _partial); break;
            }
            return built;
        }

        private static readonly Entry EmptyEntry = new Entry(BlockFill.Empty, null);
        private static readonly Entry SolidEntry = new Entry(BlockFill.Solid, null);

        private static Entry Read(IBlockAccessor accessor, int bx, int by, int bz)
        {
            var pos = new BlockPos(bx, by, bz);

            // An unloaded chunk reads as empty, not as material — see the class remarks.
            if (accessor.GetChunkAtBlockPos(pos) == null) return EmptyEntry;

            Block block = accessor.GetBlock(pos);
            if (block == null || block.Id == 0) return EmptyEntry;

            Cuboidf[] boxes;
            try { boxes = block.GetCollisionBoxes(accessor, pos); }
            catch { boxes = null; }

            if (boxes == null || boxes.Length == 0)
            {
                // No collision at all. Grass, torches, snow layers on some blocks — nothing a player
                // chisels or builds a wall out of, and nothing that should read as "you have built here".
                return EmptyEntry;
            }

            if (boxes.Length == 1 && CoversWholeBlock(boxes[0])) return SolidEntry;

            // RASTERISE BY RANGE, NOT BY SAMPLING (v0.3.80). The obvious loop — test all 4096 cell centres
            // against every cuboid — costs 4096 x cuboids point tests per block, and a heavily chiselled
            // block can carry dozens of cuboids. That was the bulk of the refresh lag spike the human
            // reported at v0.3.79.
            //
            // Each cuboid instead resolves directly to the range of cells it covers. The SEMANTICS ARE
            // UNCHANGED — a cell counts when its centre lies inside the cuboid, exactly as before — but the
            // work becomes proportional to the cells actually filled rather than to the whole block. For a
            // chisel cuboid, which sits on exact 1/16 boundaries, the range is exact with no rounding.
            var brick = new ulong[BrickWords];
            int filled = 0;
            for (int b = 0; b < boxes.Length; b++)
            {
                Cuboidf c = boxes[b];
                if (c == null) continue;

                int xa = FirstCellCentreInside(c.X1), xb = LastCellCentreInside(c.X2);
                int ya = FirstCellCentreInside(c.Y1), yb = LastCellCentreInside(c.Y2);
                int za = FirstCellCentreInside(c.Z1), zb = LastCellCentreInside(c.Z2);

                for (int cy = ya; cy <= yb; cy++)
                    for (int cz = za; cz <= zb; cz++)
                    {
                        int rowBase = (cy * CellsPerBlock + cz) * CellsPerBlock;
                        for (int cx = xa; cx <= xb; cx++)
                        {
                            int index = rowBase + cx;
                            ulong bit = 1UL << (index & 63);
                            int word = index >> 6;
                            if ((brick[word] & bit) != 0) continue;   // cuboids may overlap
                            brick[word] |= bit;
                            filled++;
                        }
                    }
            }

            // Several boxes can still add up to a full block (a fence post plus its rails never does, but a
            // door in some states can). Collapsing those drops a brick we would otherwise carry for nothing.
            if (filled == 0) return EmptyEntry;
            if (filled == CellsPerBrick) return SolidEntry;
            return new Entry(BlockFill.Partial, brick);
        }

        // A collision box is the whole block when it spans the full 0..1 cube. Tolerance is one hundredth
        // of a cell: tight enough that a 15/16 slab never passes, loose enough to absorb float noise.
        private const float WholeBlockEpsilon = 1f / (CellsPerBlock * 100f);

        private static bool CoversWholeBlock(Cuboidf c) =>
            c != null
            && c.X1 <= WholeBlockEpsilon && c.Y1 <= WholeBlockEpsilon && c.Z1 <= WholeBlockEpsilon
            && c.X2 >= 1f - WholeBlockEpsilon && c.Y2 >= 1f - WholeBlockEpsilon && c.Z2 >= 1f - WholeBlockEpsilon;

        // Cell c's centre sits at (c + 0.5)/16, so it lies inside [lo, hi] exactly when
        // c >= lo*16 - 0.5 and c <= hi*16 - 0.5. These two turn a cuboid bound into that cell range,
        // clamped to the block. An empty range (first > last) simply skips the loop.
        private static int FirstCellCentreInside(float lo)
        {
            int c = (int)Math.Ceiling(lo * CellsPerBlock - 0.5);
            return c < 0 ? 0 : c;
        }

        private static int LastCellCentreInside(float hi)
        {
            int c = (int)Math.Floor(hi * CellsPerBlock - 0.5);
            return c > CellsPerBlock - 1 ? CellsPerBlock - 1 : c;
        }

        // Y-major so a horizontal slice is contiguous — that is the order the diagnostic prints in, and the
        // order a packed upload will most likely want.
        private static int CellIndex(int cx, int cy, int cz) =>
            (cy * CellsPerBlock + cz) * CellsPerBlock + cx;

        // Packs a block position into one key. 26 bits of X and Z (+-33.5M blocks, far beyond any world)
        // and 12 bits of Y (0..4095, beyond any build height).
        private static long Key(int x, int y, int z) =>
            ((long)(x & 0x3FFFFFF) << 38) | ((long)(z & 0x3FFFFFF) << 12) | (uint)(y & 0xFFF);
    }
}
