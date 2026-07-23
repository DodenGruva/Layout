using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace Layout.Systems
{
    /// <summary>Persistent, world-scoped administrative restrictions and per-player cap overrides.</summary>
    public sealed class LayoutAdminPolicyManager
    {
        public const string StorageKey = "layout:adminpolicies";
        private const int CurrentVersion = 2;

        private readonly IGuidePersistence _persistence;
        private readonly ILogger _logger;
        private readonly Dictionary<string, PlayerPolicy> _policies =
            new Dictionary<string, PlayerPolicy>(StringComparer.Ordinal);

        public LayoutAdminPolicyManager(IGuidePersistence persistence, ILogger logger)
        {
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IReadOnlyCollection<PlayerPolicy> Policies => _policies.Values;
        public int JailedCount => _policies.Values.Count(p => p.Jailed);
        public int CustomGuideLimitCount => _policies.Values.Count(p => p.GuideLimit > 0);
        public int CustomVoxelCapCount => _policies.Values.Count(p => p.PerGuideVoxelCap > 0);
        public int CustomPlayerTotalVoxelCapCount =>
            _policies.Values.Count(p => p.PerPlayerTotalVoxelCap > 0);

        public bool IsJailed(string playerUid) =>
            !string.IsNullOrEmpty(playerUid)
            && _policies.TryGetValue(playerUid, out PlayerPolicy policy)
            && policy.Jailed;

        public int GuideLimitOverride(string playerUid) =>
            TryGet(playerUid, out PlayerPolicy policy) ? policy.GuideLimit : 0;

        public int VoxelCapOverride(string playerUid) =>
            TryGet(playerUid, out PlayerPolicy policy) ? policy.PerGuideVoxelCap : 0;

        public int PlayerTotalVoxelCapOverride(string playerUid) =>
            TryGet(playerUid, out PlayerPolicy policy) ? policy.PerPlayerTotalVoxelCap : 0;

        public int EffectiveGuideLimit(string playerUid, int serverDefault)
        {
            int custom = GuideLimitOverride(playerUid);
            return custom > 0 ? custom : Math.Max(0, serverDefault);
        }

        public int EffectiveVoxelCap(string playerUid, int serverDefault)
        {
            int custom = VoxelCapOverride(playerUid);
            return custom > 0 ? custom : Math.Max(0, serverDefault);
        }

        public int EffectivePlayerTotalVoxelCap(string playerUid, int serverDefault)
        {
            int custom = PlayerTotalVoxelCapOverride(playerUid);
            return custom > 0 ? custom : Math.Max(0, serverDefault);
        }

        public PlayerPolicy Get(string playerUid) =>
            TryGet(playerUid, out PlayerPolicy policy) ? policy : null;

        public bool SetJailed(string playerUid, string playerName, bool jailed)
        {
            PlayerPolicy policy = GetOrCreate(playerUid, playerName);
            bool changed = policy.Jailed != jailed;
            policy.Jailed = jailed;
            TouchName(policy, playerName);
            RemoveIfEmpty(policy);
            if (changed) Persist();
            return changed;
        }

        public bool SetGuideLimit(string playerUid, string playerName, int limit)
        {
            PlayerPolicy policy = GetOrCreate(playerUid, playerName);
            int normalized = Math.Max(0, limit);
            bool changed = policy.GuideLimit != normalized;
            policy.GuideLimit = normalized;
            TouchName(policy, playerName);
            RemoveIfEmpty(policy);
            if (changed) Persist();
            return changed;
        }

        public bool SetVoxelCap(string playerUid, string playerName, int cap)
        {
            PlayerPolicy policy = GetOrCreate(playerUid, playerName);
            int normalized = Math.Max(0, cap);
            bool changed = policy.PerGuideVoxelCap != normalized;
            policy.PerGuideVoxelCap = normalized;
            TouchName(policy, playerName);
            RemoveIfEmpty(policy);
            if (changed) Persist();
            return changed;
        }

        public bool SetPlayerTotalVoxelCap(string playerUid, string playerName, int cap)
        {
            PlayerPolicy policy = GetOrCreate(playerUid, playerName);
            int normalized = Math.Max(0, cap);
            bool changed = policy.PerPlayerTotalVoxelCap != normalized;
            policy.PerPlayerTotalVoxelCap = normalized;
            TouchName(policy, playerName);
            RemoveIfEmpty(policy);
            if (changed) Persist();
            return changed;
        }

        public void RememberName(string playerUid, string playerName)
        {
            if (!_policies.TryGetValue(playerUid ?? "", out PlayerPolicy policy)) return;
            if (string.IsNullOrWhiteSpace(playerName) || policy.LastKnownName == playerName.Trim()) return;
            policy.LastKnownName = playerName.Trim();
            Persist();
        }

        public void Load()
        {
            _policies.Clear();
            try
            {
                byte[] bytes = _persistence.Load(StorageKey);
                if (bytes == null || bytes.Length == 0) return;
                PersistedRoot root = JsonConvert.DeserializeObject<PersistedRoot>(Encoding.UTF8.GetString(bytes));
                if (root?.Players == null) return;
                foreach (PlayerPolicy policy in root.Players)
                {
                    if (policy == null || string.IsNullOrWhiteSpace(policy.PlayerUid)) continue;
                    policy.PlayerUid = policy.PlayerUid.Trim();
                    policy.LastKnownName = CleanName(policy.LastKnownName);
                    policy.GuideLimit = Math.Max(0, policy.GuideLimit);
                    policy.PerGuideVoxelCap = Math.Max(0, policy.PerGuideVoxelCap);
                    policy.PerPlayerTotalVoxelCap = Math.Max(0, policy.PerPlayerTotalVoxelCap);
                    if (!policy.IsEmpty) _policies[policy.PlayerUid] = policy;
                }
                _logger.Notification("[Layout] Loaded {0} player moderation polic{1}.",
                    _policies.Count, _policies.Count == 1 ? "y" : "ies");
            }
            catch (Exception e)
            {
                _policies.Clear();
                _logger.Error("[Layout] Failed to load player moderation policies; starting with none: {0}", e);
            }
        }

        public void Persist()
        {
            try
            {
                var root = new PersistedRoot
                {
                    Version = CurrentVersion,
                    Players = _policies.Values.OrderBy(p => p.LastKnownName).ToList()
                };
                _persistence.Store(StorageKey,
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(root, Formatting.None)));
            }
            catch (Exception e)
            {
                _logger.Error("[Layout] Failed to persist player moderation policies: {0}", e);
            }
        }

        private bool TryGet(string playerUid, out PlayerPolicy policy)
        {
            if (string.IsNullOrEmpty(playerUid))
            {
                policy = null;
                return false;
            }
            return _policies.TryGetValue(playerUid, out policy);
        }

        private PlayerPolicy GetOrCreate(string playerUid, string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerUid))
                throw new ArgumentException("Player UID must be non-empty.", nameof(playerUid));
            if (!_policies.TryGetValue(playerUid, out PlayerPolicy policy))
            {
                policy = new PlayerPolicy { PlayerUid = playerUid.Trim(), LastKnownName = CleanName(playerName) };
                _policies[policy.PlayerUid] = policy;
            }
            return policy;
        }

        private void RemoveIfEmpty(PlayerPolicy policy)
        {
            if (policy != null && policy.IsEmpty) _policies.Remove(policy.PlayerUid);
        }

        private static void TouchName(PlayerPolicy policy, string playerName)
        {
            if (!string.IsNullOrWhiteSpace(playerName)) policy.LastKnownName = playerName.Trim();
        }

        private static string CleanName(string name) =>
            string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();

        private sealed class PersistedRoot
        {
            public int Version { get; set; }
            public List<PlayerPolicy> Players { get; set; }
        }
    }

    public sealed class PlayerPolicy
    {
        public string PlayerUid { get; set; }
        public string LastKnownName { get; set; }
        public bool Jailed { get; set; }
        public int GuideLimit { get; set; }
        public int PerGuideVoxelCap { get; set; }
        public int PerPlayerTotalVoxelCap { get; set; }

        [JsonIgnore]
        public bool IsEmpty =>
            !Jailed && GuideLimit <= 0 && PerGuideVoxelCap <= 0 && PerPlayerTotalVoxelCap <= 0;
    }
}
