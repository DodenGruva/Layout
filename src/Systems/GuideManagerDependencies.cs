using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Layout.Systems
{
    /// <summary>Storage seam used by GuideManager on either side of the game.</summary>
    public interface IGuidePersistence
    {
        byte[] Load(string key);
        void Store(string key, byte[] data);
    }

    /// <summary>World-save-backed storage for normal server-authoritative Layout.</summary>
    public sealed class ServerGuidePersistence : IGuidePersistence
    {
        private readonly ICoreServerAPI _sapi;

        public ServerGuidePersistence(ICoreServerAPI sapi)
        {
            _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
        }

        public byte[] Load(string key) => _sapi.WorldManager.SaveGame.GetData(key);

        public void Store(string key, byte[] data) => _sapi.WorldManager.SaveGame.StoreData(key, data);
    }

    /// <summary>No-op storage for guides that intentionally live only for the current client session.</summary>
    public sealed class TransientGuidePersistence : IGuidePersistence
    {
        public byte[] Load(string key) => null;
        public void Store(string key, byte[] data) { }
    }

    /// <summary>Answers solidity probes without coupling GuideManager to a client or server API.</summary>
    public interface IGuideBlockProbe
    {
        bool TryIsSolid(BlockPos position, out bool solid);
    }

    /// <summary>Common client/server block-accessor implementation of the guide solidity probe.</summary>
    public sealed class BlockAccessorGuideProbe : IGuideBlockProbe
    {
        private readonly Func<IBlockAccessor> _accessorProvider;

        public BlockAccessorGuideProbe(Func<IBlockAccessor> accessorProvider)
        {
            _accessorProvider = accessorProvider ?? throw new ArgumentNullException(nameof(accessorProvider));
        }

        public bool TryIsSolid(BlockPos position, out bool solid)
        {
            IBlockAccessor accessor = _accessorProvider();
            if (accessor == null || accessor.GetChunkAtBlockPos(position) == null)
            {
                solid = false;
                return false;
            }

            Block block = accessor.GetBlock(position);
            solid = block != null && block.Id != 0;
            return true;
        }
    }
}
