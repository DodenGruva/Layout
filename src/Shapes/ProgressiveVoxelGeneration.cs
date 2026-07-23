using System;
using System.Collections.Generic;
using System.Threading;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// Optional exact-generation path for shapes whose shell scan can yield useful bounded pieces before
    /// the complete voxel set exists. The returned list is still the canonical final set; emitted chunks
    /// are transient preview material and may be untyped until final control-point markers are applied.
    /// </summary>
    public interface IProgressiveVoxelShape
    {
        List<VoxelPosition> GetVoxelPositionsProgressively(
            int scale, bool filled, int targetVoxelsPerChunk,
            CancellationToken cancellationToken, Action<List<VoxelPosition>> emitChunk);
    }

    /// <summary>
    /// Accumulates the canonical set while optionally releasing bounded immutable scan chunks. The renderer
    /// normally withholds those scan-order chunks now: once the exact set exists, <see cref="OrganicVoxelGrowth"/>
    /// grows genuinely connected regions across it instead of exposing the scanner's rectangular tiles.
    /// </summary>
    internal sealed class ProgressiveVoxelCollector
    {
        private const int FrayWindowChunks = 8;
        private readonly int _target;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<List<VoxelPosition>> _emitChunk;
        private readonly List<VoxelPosition> _pending;
        private int _acceptedSinceCancellationCheck;

        internal List<VoxelPosition> Result { get; } = new List<VoxelPosition>();

        internal ProgressiveVoxelCollector(
            int targetVoxelsPerChunk, CancellationToken cancellationToken,
            Action<List<VoxelPosition>> emitChunk)
        {
            _target = Math.Max(64, targetVoxelsPerChunk);
            _cancellationToken = cancellationToken;
            _emitChunk = emitChunk;
            _pending = emitChunk == null
                ? null
                : new List<VoxelPosition>(_target * FrayWindowChunks);
        }

        internal void Add(VoxelPosition voxel)
        {
            if ((++_acceptedSinceCancellationCheck & 255) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            Result.Add(voxel);
            if (_pending == null) return;
            _pending.Add(voxel);
            if (_pending.Count >= _target * FrayWindowChunks) FlushWindow();
        }

        internal void Flush()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            FlushWindow();
        }

        internal void ThrowIfCancellationRequested() =>
            _cancellationToken.ThrowIfCancellationRequested();

        private void FlushWindow()
        {
            if (_pending == null || _pending.Count == 0) return;
            _pending.Sort((a, b) => RevealHash(a).CompareTo(RevealHash(b)));
            for (int start = 0; start < _pending.Count; start += _target)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(_target, _pending.Count - start);
                _emitChunk?.Invoke(_pending.GetRange(start, count));
            }
            _pending.Clear();
        }

        private static ulong RevealHash(VoxelPosition voxel)
        {
            unchecked
            {
                ulong h = (uint)voxel.X * 0x9e3779b185ebca87UL;
                h ^= (uint)voxel.Y * 0xc2b2ae3d27d4eb4fUL;
                h ^= (uint)voxel.Z * 0x165667b19e3779f9UL;
                h ^= h >> 30;
                h *= 0xbf58476d1ce4e5b9UL;
                h ^= h >> 27;
                h *= 0x94d049bb133111ebUL;
                h ^= h >> 31;
                return h;
            }
        }
    }

    /// <summary>A bounded block of cell indices, with exclusive upper bounds.</summary>
    internal readonly struct ProgressiveVoxelTile
    {
        internal readonly int X0, X1, Y0, Y1, Z0, Z1;

        internal ProgressiveVoxelTile(int x0, int x1, int y0, int y1, int z0, int z1)
        {
            X0 = x0; X1 = x1;
            Y0 = y0; Y1 = y1;
            Z0 = z0; Z1 = z1;
        }
    }

    /// <summary>
    /// Produces a deterministic bit-reversed visitation order. Early work is spread across the complete
    /// axis rather than sweeping from one end, so every preview chunk contributes distant shell spots.
    /// </summary>
    internal static class ProgressiveVoxelOrder
    {
        private const int SpatialTileEdge = 8;

        /// <summary>
        /// Divides a scan volume into small patches and visits those patches in a deterministic shuffled
        /// order. Accepted cells remain locally clustered, but successive clusters come from unrelated
        /// parts of the whole guide, producing a splotchy reveal without changing the final voxel set.
        /// </summary>
        internal static ProgressiveVoxelTile[] ShuffledSpatialTiles(
            int xCount, int yCount, int zCount)
        {
            if (xCount <= 0 || yCount <= 0 || zCount <= 0)
                return Array.Empty<ProgressiveVoxelTile>();

            int xt = (xCount + SpatialTileEdge - 1) / SpatialTileEdge;
            int yt = (yCount + SpatialTileEdge - 1) / SpatialTileEdge;
            int zt = (zCount + SpatialTileEdge - 1) / SpatialTileEdge;
            int tileCount = checked(xt * yt * zt);
            int seed = unchecked(xCount * 73856093 ^ yCount * 19349663 ^ zCount * 83492791);
            int[] order = ShuffledIndices(tileCount, seed);
            var result = new ProgressiveVoxelTile[tileCount];
            for (int visit = 0; visit < order.Length; visit++)
            {
                int linear = order[visit];
                int tx = linear % xt;
                int ty = (linear / xt) % yt;
                int tz = linear / (xt * yt);
                int x0 = tx * SpatialTileEdge;
                int y0 = ty * SpatialTileEdge;
                int z0 = tz * SpatialTileEdge;
                result[visit] = new ProgressiveVoxelTile(
                    x0, Math.Min(xCount, x0 + SpatialTileEdge),
                    y0, Math.Min(yCount, y0 + SpatialTileEdge),
                    z0, Math.Min(zCount, z0 + SpatialTileEdge));
            }
            return result;
        }

        internal static int[] ShuffledIndices(int count, int seed = unchecked((int)0x9e3779b9u))
        {
            if (count <= 0) return Array.Empty<int>();
            var result = new int[count];
            for (int i = 0; i < count; i++) result[i] = i;

            uint state = unchecked((uint)seed) ^ unchecked((uint)count * 0x85ebca6bu);
            if (state == 0) state = 0xa341316cu;
            for (int i = count - 1; i > 0; i--)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                int j = (int)(state % (uint)(i + 1));
                int swap = result[i]; result[i] = result[j]; result[j] = swap;
            }
            return result;
        }

    }

    /// <summary>
    /// Turns an exact voxel set into deterministic mold-like growth. Several well-separated seeds begin at
    /// once, then every later voxel is admitted only from an already-reached neighbour. Smooth multi-scale
    /// noise advances some edges and holds others back, producing torn fronts, branches, bays, and merging
    /// splotches without changing a single voxel in the final geometry.
    /// </summary>
    internal static class OrganicVoxelGrowth
    {
        internal static List<List<VoxelPosition>> BuildBatches(
            IReadOnlyList<VoxelPosition> voxels, int scale, int targetVoxelsPerBatch,
            int minimumBatches, int maximumBatches, CancellationToken cancellationToken)
        {
            var result = new List<List<VoxelPosition>>();
            GrowBatches(voxels, scale, targetVoxelsPerBatch,
                minimumBatches, maximumBatches, cancellationToken, result.Add);
            return result;
        }

        /// <summary>
        /// Streaming form used by the renderer. Once the exact occupancy and separated seeds are known, each
        /// completed growth step can be meshed and shown immediately; the entire organic order does not have
        /// to finish before the first splotches become visible.
        /// </summary>
        internal static void GrowBatches(
            IReadOnlyList<VoxelPosition> voxels, int scale, int targetVoxelsPerBatch,
            int minimumBatches, int maximumBatches, CancellationToken cancellationToken,
            Action<List<VoxelPosition>> emitBatch)
        {
            int count = voxels?.Count ?? 0;
            if (count == 0 || emitBatch == null) return;

            int target = Math.Max(64, targetVoxelsPerBatch);
            int batchCount = Math.Max(Math.Max(1, minimumBatches),
                (count + target - 1) / target);
            batchCount = Math.Min(Math.Max(1, maximumBatches), batchCount);
            batchCount = Math.Min(count, batchCount);
            int batchSize = Math.Max(1, (count + batchCount - 1) / batchCount);

            int step = Math.Max(1, scale);
            var at = new Dictionary<(int, int, int), int>(count);
            for (int i = 0; i < count; i++)
            {
                if ((i & 2047) == 0) cancellationToken.ThrowIfCancellationRequested();
                VoxelPosition voxel = voxels[i];
                at[(voxel.X, voxel.Y, voxel.Z)] = i;
            }

            int seedGoal = Math.Max(6, Math.Min(28,
                (int)Math.Ceiling(Math.Sqrt(batchCount) * 1.8)));
            seedGoal = Math.Min(count, seedGoal);
            List<int> seeds = SelectSeeds(voxels, seedGoal, cancellationToken);

            var reached = new bool[count];
            var distance = new double[count];
            var frontier = new PriorityQueue<int, double>();
            for (int i = 0; i < seeds.Count; i++)
            {
                int seed = seeds[i];
                if (reached[seed]) continue;
                reached[seed] = true;
                distance[seed] = 0;
                VoxelPosition voxel = voxels[seed];
                frontier.Enqueue(seed, OrganicNoise(voxel.X, voxel.Y, voxel.Z, step) * 0.35);
            }

            int[] disconnectedOrder = ProgressiveVoxelOrder.ShuffledIndices(
                count, unchecked(count * 486187739 ^ step * 16777619));
            int disconnectedCursor = 0;
            int emitted = 0;
            List<VoxelPosition> batch = null;

            while (emitted < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frontier.Count == 0)
                {
                    while (disconnectedCursor < disconnectedOrder.Length
                        && reached[disconnectedOrder[disconnectedCursor]])
                        disconnectedCursor++;
                    if (disconnectedCursor >= disconnectedOrder.Length) break;
                    int seed = disconnectedOrder[disconnectedCursor++];
                    reached[seed] = true;
                    distance[seed] = 0;
                    VoxelPosition voxel = voxels[seed];
                    frontier.Enqueue(seed,
                        OrganicNoise(voxel.X, voxel.Y, voxel.Z, step) * 0.35);
                }

                int current = frontier.Dequeue();
                VoxelPosition here = voxels[current];
                if (batch == null)
                {
                    batch = new List<VoxelPosition>(Math.Min(batchSize, count - emitted));
                }
                else if (batch.Count >= batchSize)
                {
                    emitBatch(batch);
                    batch = new List<VoxelPosition>(Math.Min(batchSize, count - emitted));
                }
                batch.Add(here);
                emitted++;

                int neighborCounter = 0;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    if ((++neighborCounter & 15) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (!at.TryGetValue(
                        (here.X + dx * step, here.Y + dy * step, here.Z + dz * step),
                        out int neighbor) || reached[neighbor])
                        continue;

                    reached[neighbor] = true;
                    int axes = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                    double travel = axes == 1 ? 1.0 : axes == 2 ? 1.38 : 1.72;
                    distance[neighbor] = distance[current] + travel;
                    VoxelPosition next = voxels[neighbor];
                    double tornFront = OrganicNoise(next.X, next.Y, next.Z, step) * 3.4;
                    double edgeJitter = (HashUnit(next.X ^ here.X, next.Y ^ here.Y,
                        next.Z ^ here.Z) - 0.5) * 1.1;
                    frontier.Enqueue(neighbor,
                        distance[neighbor] + tornFront + edgeJitter);
                }
            }

            if (batch != null && batch.Count > 0) emitBatch(batch);
        }

        private static List<int> SelectSeeds(
            IReadOnlyList<VoxelPosition> voxels, int seedGoal,
            CancellationToken cancellationToken)
        {
            int count = voxels.Count;
            var seeds = new List<int>(seedGoal);

            // Functional colours are natural nucleation points, but cap them so division marks cannot turn
            // the entire reveal back into a regular line.
            var special = new List<(ulong hash, int index)>();
            for (int i = 0; i < count; i++)
            {
                if ((i & 2047) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (voxels[i].Type == VoxelRenderType.Normal) continue;
                VoxelPosition voxel = voxels[i];
                special.Add((Hash(voxel.X, voxel.Y, voxel.Z), i));
            }
            special.Sort((a, b) => a.hash.CompareTo(b.hash));
            int specialCount = Math.Min(Math.Min(6, seedGoal / 3), special.Count);
            for (int i = 0; i < specialCount; i++) seeds.Add(special[i].index);

            if (seeds.Count == 0)
            {
                int first = 0;
                ulong bestHash = ulong.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    ulong hash = Hash(voxels[i].X, voxels[i].Y, voxels[i].Z);
                    if (hash >= bestHash) continue;
                    bestHash = hash;
                    first = i;
                }
                seeds.Add(first);
            }

            var nearestDistance = new double[count];
            for (int i = 0; i < count; i++) nearestDistance[i] = double.PositiveInfinity;
            var selected = new bool[count];
            for (int i = 0; i < seeds.Count; i++)
            {
                selected[seeds[i]] = true;
                UpdateNearestDistances(voxels, seeds[i], nearestDistance, cancellationToken);
            }

            while (seeds.Count < seedGoal)
            {
                int nextSeed = -1;
                double bestScore = double.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    if ((i & 2047) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (selected[i]) continue;
                    VoxelPosition voxel = voxels[i];
                    double jitter = 0.82 + HashUnit(voxel.X, voxel.Y, voxel.Z) * 0.36;
                    double score = nearestDistance[i] * jitter;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    nextSeed = i;
                }
                if (nextSeed < 0) break;
                selected[nextSeed] = true;
                seeds.Add(nextSeed);
                UpdateNearestDistances(voxels, nextSeed, nearestDistance, cancellationToken);
            }

            return seeds;
        }

        private static void UpdateNearestDistances(
            IReadOnlyList<VoxelPosition> voxels, int seedIndex, double[] nearestDistance,
            CancellationToken cancellationToken)
        {
            VoxelPosition seed = voxels[seedIndex];
            for (int i = 0; i < voxels.Count; i++)
            {
                if ((i & 2047) == 0) cancellationToken.ThrowIfCancellationRequested();
                VoxelPosition voxel = voxels[i];
                double dx = voxel.X - seed.X;
                double dy = voxel.Y - seed.Y;
                double dz = voxel.Z - seed.Z;
                double distance = dx * dx + dy * dy + dz * dz;
                if (distance < nearestDistance[i]) nearestDistance[i] = distance;
            }
        }

        private static double OrganicNoise(int x, int y, int z, int step)
        {
            int gx = FloorDiv(x, step);
            int gy = FloorDiv(y, step);
            int gz = FloorDiv(z, step);
            double broad = SmoothValueNoise(gx, gy, gz, 9);
            double detail = SmoothValueNoise(gx, gy, gz, 4);
            double grain = HashUnit(gx, gy, gz) * 2.0 - 1.0;
            return broad * 0.58 + detail * 0.30 + grain * 0.12;
        }

        private static double SmoothValueNoise(int x, int y, int z, int period)
        {
            int x0 = FloorDiv(x, period);
            int y0 = FloorDiv(y, period);
            int z0 = FloorDiv(z, period);
            double tx = Fade((x - x0 * period) / (double)period);
            double ty = Fade((y - y0 * period) / (double)period);
            double tz = Fade((z - z0 * period) / (double)period);

            double c000 = HashUnit(x0, y0, z0) * 2.0 - 1.0;
            double c100 = HashUnit(x0 + 1, y0, z0) * 2.0 - 1.0;
            double c010 = HashUnit(x0, y0 + 1, z0) * 2.0 - 1.0;
            double c110 = HashUnit(x0 + 1, y0 + 1, z0) * 2.0 - 1.0;
            double c001 = HashUnit(x0, y0, z0 + 1) * 2.0 - 1.0;
            double c101 = HashUnit(x0 + 1, y0, z0 + 1) * 2.0 - 1.0;
            double c011 = HashUnit(x0, y0 + 1, z0 + 1) * 2.0 - 1.0;
            double c111 = HashUnit(x0 + 1, y0 + 1, z0 + 1) * 2.0 - 1.0;

            double x00 = Lerp(c000, c100, tx);
            double x10 = Lerp(c010, c110, tx);
            double x01 = Lerp(c001, c101, tx);
            double x11 = Lerp(c011, c111, tx);
            return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            if (value < 0 && value % divisor != 0) quotient--;
            return quotient;
        }

        private static double Fade(double value) =>
            value * value * (3.0 - 2.0 * value);

        private static double Lerp(double a, double b, double t) =>
            a + (b - a) * t;

        private static double HashUnit(int x, int y, int z) =>
            (Hash(x, y, z) >> 11) * (1.0 / 9007199254740992.0);

        private static ulong Hash(int x, int y, int z)
        {
            unchecked
            {
                ulong h = (uint)x * 0x9e3779b185ebca87UL;
                h ^= (uint)y * 0xc2b2ae3d27d4eb4fUL;
                h ^= (uint)z * 0x165667b19e3779f9UL;
                h ^= h >> 30;
                h *= 0xbf58476d1ce4e5b9UL;
                h ^= h >> 27;
                h *= 0x94d049bb133111ebUL;
                h ^= h >> 31;
                return h;
            }
        }
    }
}
