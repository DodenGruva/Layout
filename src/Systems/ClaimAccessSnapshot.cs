using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Layout.Systems
{
    /// <summary>Inclusive bounds of the exact world blocks in an immense guide footprint.</summary>
    internal readonly struct ClaimFootprintBounds
    {
        public bool HasBlocks { get; }
        public int MinX { get; }
        public int MinY { get; }
        public int MinZ { get; }
        public int MaxX { get; }
        public int MaxY { get; }
        public int MaxZ { get; }

        private ClaimFootprintBounds(
            bool hasBlocks, int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            HasBlocks = hasBlocks;
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
        }

        public static ClaimFootprintBounds FromFootprint(IReadOnlyList<Vintagestory.API.MathTools.BlockPos> blocks)
        {
            if (blocks == null || blocks.Count == 0) return default;

            int minX = blocks[0].X, minY = blocks[0].InternalY, minZ = blocks[0].Z;
            int maxX = minX, maxY = minY, maxZ = minZ;
            for (int i = 1; i < blocks.Count; i++)
            {
                Vintagestory.API.MathTools.BlockPos block = blocks[i];
                if (block.X < minX) minX = block.X;
                if (block.InternalY < minY) minY = block.InternalY;
                if (block.Z < minZ) minZ = block.Z;
                if (block.X > maxX) maxX = block.X;
                if (block.InternalY > maxY) maxY = block.InternalY;
                if (block.Z > maxZ) maxZ = block.Z;
            }

            return new ClaimFootprintBounds(true, minX, minY, minZ, maxX, maxY, maxZ);
        }

        public bool Intersects(Vintagestory.API.MathTools.Cuboidi area) =>
            HasBlocks && area != null
            && area.MinX <= MaxX && area.MaxX > MinX
            && area.MinY <= MaxY && area.MaxY > MinY
            && area.MinZ <= MaxZ && area.MaxZ > MinZ;
    }

    /// <summary>
    /// Immutable, exact snapshot of the built-in state that can change a BuildOrBreak answer for one
    /// player. It detects stale time-sliced access walks; it never replaces
    /// <see cref="ILandClaimAPI.TestAccess"/> or its mod-extensible final event.
    /// </summary>
    internal sealed class ClaimAccessSnapshot : IEquatable<ClaimAccessSnapshot>
    {
        private readonly PlayerAccessState _player;
        private readonly ClaimState[] _claims;

        private ClaimAccessSnapshot(PlayerAccessState player, ClaimState[] claims)
        {
            _player = player;
            _claims = claims ?? Array.Empty<ClaimState>();
        }

        public static ClaimAccessSnapshot Capture(
            IEnumerable<LandClaim> claims, IPlayer player, ClaimFootprintBounds bounds)
        {
            return Capture(
                claims,
                bounds,
                PlayerAccessState.Capture(player),
                claim => player != null
                    && claim.TestPlayerAccess(player, EnumBlockAccessFlags.BuildOrBreak)
                        != EnumPlayerAccessResult.Denied);
        }

        // Primitive overload keeps the state mapping independently testable without constructing the
        // game's concrete server-player implementation. Production always enters through the overload above.
        internal static ClaimAccessSnapshot Capture(
            IEnumerable<LandClaim> claims, ClaimFootprintBounds bounds,
            string playerUid, EnumGameMode gameMode, bool alive,
            bool canBuild, bool canBuildEverywhere, int privilegeLevel,
            IEnumerable<int> groupIds)
        {
            var groups = groupIds == null ? new HashSet<int>() : new HashSet<int>(groupIds);
            return Capture(
                claims,
                bounds,
                PlayerAccessState.Capture(
                    playerUid, gameMode, alive, canBuild,
                    canBuildEverywhere, privilegeLevel, groups),
                claim => AllowsBuild(
                    claim, playerUid, gameMode, privilegeLevel, groups));
        }

        private static ClaimAccessSnapshot Capture(
            IEnumerable<LandClaim> claims, ClaimFootprintBounds bounds, PlayerAccessState player,
            System.Func<LandClaim, bool> allowsBuild)
        {
            var captured = new List<ClaimState>();
            if (claims != null && bounds.HasBlocks && !player.BypassesClaims)
                foreach (LandClaim claim in claims)
                    if (ClaimState.TryCapture(claim, bounds, allowsBuild, out ClaimState state))
                        captured.Add(state);

            // The public API has no rectangular claim query, so finding candidates still makes one cheap
            // pass over Claims.All. Claims and claim areas wholly outside the exact footprint bounds are not
            // copied, permission-tested or compared, and therefore cannot cause a restart.
            return new ClaimAccessSnapshot(player, captured.ToArray());
        }

        public bool Equals(ClaimAccessSnapshot other)
        {
            if (other == null
                || !_player.Equals(other._player)
                || _claims.Length != other._claims.Length)
                return false;

            for (int i = 0; i < _claims.Length; i++)
                if (!_claims[i].Equals(other._claims[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => Equals(obj as ClaimAccessSnapshot);

        public override int GetHashCode() =>
            HashCode.Combine(_player, _claims.Length);

        // Mirrors LandClaim.TestPlayerAccess for the primitive test seam only. Production invokes the
        // installed game's method directly above, so a future engine change cannot silently diverge here.
        private static bool AllowsBuild(
            LandClaim claim, string playerUid, EnumGameMode gameMode,
            int privilegeLevel, HashSet<int> groupIds)
        {
            if (claim == null) return false;
            if (playerUid != null && playerUid.Equals(claim.OwnedByPlayerUid)) return true;
            if (claim.OwnedByPlayerGroupUid != 0
                && groupIds.Contains(unchecked((int)claim.OwnedByPlayerGroupUid)))
                return true;
            if (privilegeLevel > claim.ProtectionLevel && gameMode == EnumGameMode.Creative)
                return true;
            if (claim.PermittedPlayerUids != null
                && playerUid != null
                && claim.PermittedPlayerUids.TryGetValue(
                    playerUid, out EnumBlockAccessFlags playerGrant)
                && (playerGrant & EnumBlockAccessFlags.BuildOrBreak) != 0)
                return true;

            if (claim.PermittedPlayerGroupIds != null)
                foreach (int groupId in groupIds)
                    if (claim.PermittedPlayerGroupIds.TryGetValue(
                        groupId, out EnumBlockAccessFlags groupGrant)
                        && (groupGrant & EnumBlockAccessFlags.BuildOrBreak) != 0)
                        return true;

            return false;
        }

        private readonly struct PlayerAccessState : IEquatable<PlayerAccessState>
        {
            private readonly bool _exists;
            private readonly string _playerUid;
            private readonly EnumGameMode _gameMode;
            private readonly bool _alive;
            private readonly bool _canBuild;
            private readonly bool _canBuildEverywhere;
            private readonly int _privilegeLevel;
            private readonly int[] _groupIds;

            public bool BypassesClaims =>
                _exists && _gameMode == EnumGameMode.Creative && _canBuildEverywhere;

            private PlayerAccessState(
                bool exists, string playerUid, EnumGameMode gameMode, bool alive,
                bool canBuild, bool canBuildEverywhere, int privilegeLevel, int[] groupIds)
            {
                _exists = exists;
                _playerUid = playerUid;
                _gameMode = gameMode;
                _alive = alive;
                _canBuild = canBuild;
                _canBuildEverywhere = canBuildEverywhere;
                _privilegeLevel = privilegeLevel;
                _groupIds = groupIds ?? Array.Empty<int>();
            }

            public static PlayerAccessState Capture(IPlayer player)
            {
                if (player == null)
                    return new PlayerAccessState(
                        false, null, default, false, false, false, int.MinValue, null);
                PlayerGroupMembership[] groups = player.Groups;
                var groupIds = new int[groups?.Length ?? 0];
                for (int i = 0; i < groupIds.Length; i++) groupIds[i] = groups[i].GroupUid;
                Array.Sort(groupIds);

                return new PlayerAccessState(
                    true, player.PlayerUID, player.WorldData.CurrentGameMode,
                    player.Entity != null && player.Entity.Alive,
                    player.HasPrivilege(Privilege.buildblocks),
                    player.HasPrivilege(Privilege.buildblockseverywhere),
                    player.Role?.PrivilegeLevel ?? int.MinValue,
                    groupIds);
            }

            public static PlayerAccessState Capture(
                string playerUid, EnumGameMode gameMode, bool alive,
                bool canBuild, bool canBuildEverywhere, int privilegeLevel,
                IEnumerable<int> groupIds)
            {
                var sortedGroupIds = groupIds == null
                    ? Array.Empty<int>()
                    : new List<int>(groupIds).ToArray();
                Array.Sort(sortedGroupIds);
                return new PlayerAccessState(
                    true, playerUid, gameMode, alive, canBuild,
                    canBuildEverywhere, privilegeLevel, sortedGroupIds);
            }

            public bool Equals(PlayerAccessState other)
            {
                if (_exists != other._exists
                    || !string.Equals(_playerUid, other._playerUid, StringComparison.Ordinal)
                    || _gameMode != other._gameMode
                    || _alive != other._alive
                    || _canBuild != other._canBuild
                    || _canBuildEverywhere != other._canBuildEverywhere
                    || _privilegeLevel != other._privilegeLevel
                    || _groupIds.Length != other._groupIds.Length)
                    return false;

                for (int i = 0; i < _groupIds.Length; i++)
                    if (_groupIds[i] != other._groupIds[i]) return false;
                return true;
            }

            public override int GetHashCode() =>
                HashCode.Combine(
                    _exists, _playerUid, _gameMode, _alive,
                    _canBuild, _canBuildEverywhere, _privilegeLevel, _groupIds.Length);
        }

        private readonly struct ClaimState : IEquatable<ClaimState>
        {
            private readonly bool _exists;
            private readonly bool _allowsBuild;
            private readonly AreaState[] _areas;

            private ClaimState(bool exists, bool allowsBuild, AreaState[] areas)
            {
                _exists = exists;
                _allowsBuild = allowsBuild;
                _areas = areas ?? Array.Empty<AreaState>();
            }

            public static bool TryCapture(
                LandClaim claim, ClaimFootprintBounds bounds,
                System.Func<LandClaim, bool> allowsBuild, out ClaimState state)
            {
                state = default;
                if (claim?.Areas == null) return false;

                List<AreaState> areas = null;
                for (int i = 0; i < claim.Areas.Count; i++)
                    if (AreaState.TryCapture(claim.Areas[i], bounds, out AreaState area))
                    {
                        areas ??= new List<AreaState>();
                        areas.Add(area);
                    }
                if (areas == null) return false;

                state = new ClaimState(true, allowsBuild(claim), areas.ToArray());
                return true;
            }

            public bool Equals(ClaimState other)
            {
                if (_exists != other._exists
                    || _allowsBuild != other._allowsBuild
                    || _areas.Length != other._areas.Length)
                    return false;

                for (int i = 0; i < _areas.Length; i++)
                    if (!_areas[i].Equals(other._areas[i])) return false;
                return true;
            }
        }

        private readonly struct AreaState : IEquatable<AreaState>
        {
            private readonly bool _exists;
            private readonly long _x1;
            private readonly long _y1;
            private readonly long _z1;
            private readonly long _x2;
            private readonly long _y2;
            private readonly long _z2;

            private AreaState(
                bool exists, long x1, long y1, long z1, long x2, long y2, long z2)
            {
                _exists = exists;
                _x1 = x1;
                _y1 = y1;
                _z1 = z1;
                _x2 = x2;
                _y2 = y2;
                _z2 = z2;
            }

            public static bool TryCapture(
                Vintagestory.API.MathTools.Cuboidi area, ClaimFootprintBounds bounds,
                out AreaState state)
            {
                state = default;
                if (!bounds.Intersects(area)) return false;

                // Cuboidi.Contains is min-inclusive/max-exclusive. Clip to the block bounds expressed the
                // same way, using long for Max+1 so a footprint at int.MaxValue cannot wrap.
                state = new AreaState(
                    true,
                    Math.Max((long)area.MinX, bounds.MinX),
                    Math.Max((long)area.MinY, bounds.MinY),
                    Math.Max((long)area.MinZ, bounds.MinZ),
                    Math.Min((long)area.MaxX, (long)bounds.MaxX + 1),
                    Math.Min((long)area.MaxY, (long)bounds.MaxY + 1),
                    Math.Min((long)area.MaxZ, (long)bounds.MaxZ + 1));
                return true;
            }

            public bool Equals(AreaState other) =>
                _exists == other._exists
                && _x1 == other._x1 && _y1 == other._y1 && _z1 == other._z1
                && _x2 == other._x2 && _y2 == other._y2 && _z2 == other._z2;
        }
    }
}
