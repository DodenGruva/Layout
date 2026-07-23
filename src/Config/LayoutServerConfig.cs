using Newtonsoft.Json;

namespace Layout.Config
{
    /// <summary>
    /// Server-side configuration, persisted as <c>layout.json</c> in the game's ModConfig folder. Loaded by
    /// <see cref="LayoutModSystem"/> via <c>sapi.LoadModConfig</c> on server start; if the file is absent (or
    /// unreadable) a default instance is created and stored back so admins always have a file to edit.
    /// The values are injected into the managers at construction — nothing reads this class after startup,
    /// so edits take effect on the next server restart.
    /// </summary>
    /// <remarks>
    /// UNLIMITED SEMANTICS. For all five caps, <c>0</c> or any negative value means "unlimited" — the check
    /// is skipped entirely. <see cref="Normalize"/> folds negatives to 0 so "unlimited" has one on-disk
    /// representation, and folds a non-positive undo depth back to the default (an undo history of zero is a
    /// config error, not a feature).
    ///
    /// JSON KEYS are camelCase (via <see cref="JsonPropertyAttribute"/>) to match the documented key names in
    /// the plan, independent of the C# property names.
    /// </remarks>
    public class LayoutServerConfig
    {
        /// <summary>Internal schema marker used only to migrate unchanged historical defaults.</summary>
        [JsonProperty("configVersion")]
        public int ConfigVersion { get; set; } = 0;

        /// <summary>Max voxels a single guide may occupy. 0 = unlimited. Synced to clients on join.</summary>
        [JsonProperty("perGuideVoxelCap")]
        public int PerGuideVoxelCap { get; set; } = 500000;

        /// <summary>Max voxels across ALL guides. 0 = unlimited. Synced to clients on join.</summary>
        [JsonProperty("totalVoxelCap")]
        public int TotalVoxelCap { get; set; } = 0;

        /// <summary>
        /// Max voxels across all currently existing guides attributed to one original creator.
        /// 0 = unlimited. Existing over-cap guides still load, but cannot grow.
        /// </summary>
        [JsonProperty("perPlayerTotalVoxelCap")]
        public int PerPlayerTotalVoxelCap { get; set; } = 1000000;

        /// <summary>Max guides one player may have created at once. 0 = unlimited (the default).</summary>
        [JsonProperty("maxGuidesPerPlayer")]
        public int MaxGuidesPerPlayer { get; set; } = 0;

        /// <summary>Max guides that may exist world-wide at once. 0 = unlimited (the default).</summary>
        [JsonProperty("maxGuidesWorldWide")]
        public int MaxGuidesWorldWide { get; set; } = 0;

        /// <summary>Per-player undo/redo history depth. Non-positive values fall back to the default (50).</summary>
        [JsonProperty("undoHistoryDepth")]
        public int UndoHistoryDepth { get; set; } = 50;

        /// <summary>
        /// Privilege a player must hold to use the Layout tool (create / edit / dispel / undo). Empty =
        /// everyone may use it (the default, matching the fully-open model). Any privilege string the server
        /// knows works here — a vanilla one or one granted via a role.
        /// </summary>
        [JsonProperty("requiredPrivilege")]
        public string RequiredPrivilege { get; set; } = "";

        /// <summary>
        /// When true (the default), a player with the <c>controlserver</c> privilege may dispel or toggle a
        /// guide even while it is edit-locked by another player — the administrative remedy for a lock held
        /// indefinitely (e.g. a player who swapped away mid-edit and left). Geometry edits are never
        /// overridable; they genuinely require holding the lock.
        /// </summary>
        [JsonProperty("adminCanOverrideLocks")]
        public bool AdminCanOverrideLocks { get; set; } = true;

        /// <summary>
        /// Allows compliant clients to place private, client-stored guides while remaining connected to
        /// this Layout-enabled server. False by default: server-installed Layout remains public/shared.
        /// </summary>
        [JsonProperty("allowClientOnlyMode")]
        public bool AllowClientOnlyMode { get; set; } = false;

        /// <summary>
        /// F5 chalk durability: when true (the default), each completed server-authoritative guide placement
        /// spends the Chalking Kit's chalk (2D −1, 3D volume −2; refilled with Chalking Powder), and a kit
        /// at 0 cannot place NEW guides (editing/dispelling always stays open). False disables consumption
        /// entirely — e.g. for creative-leaning servers. Creative-mode players never consume regardless.
        /// </summary>
        [JsonProperty("enableChalkDurability")]
        public bool EnableChalkDurability { get; set; } = true;

        // NOTE (v0.2.22): the two chalk REFILL-CHANNEL flags that lived here in 0.2.21
        // (allowHotbarChalkRefill / allowInventoryChalkRefill) moved to the CLIENT config — they are player
        // convenience toggles, not server policy (a refill costs the same powder wherever it happens).
        // Stale keys left in an existing layout.json are simply ignored by the deserializer.

        /// <summary>Folds out-of-range values to their canonical forms. Call once after loading.</summary>
        public void Normalize()
        {
            // Existing layout.json files contain the old generated defaults explicitly. Migrate only those
            // exact inherited values; administrators who chose any other limits keep their configuration.
            if (ConfigVersion < 1)
            {
                if (PerGuideVoxelCap == 25000) PerGuideVoxelCap = 500000;
                if (TotalVoxelCap == 250000) TotalVoxelCap = 0;
                ConfigVersion = 1;
            }
            if (PerGuideVoxelCap < 0) PerGuideVoxelCap = 0;
            if (TotalVoxelCap < 0) TotalVoxelCap = 0;
            if (PerPlayerTotalVoxelCap < 0) PerPlayerTotalVoxelCap = 0;
            if (MaxGuidesPerPlayer < 0) MaxGuidesPerPlayer = 0;
            if (MaxGuidesWorldWide < 0) MaxGuidesWorldWide = 0;
            if (UndoHistoryDepth <= 0) UndoHistoryDepth = 50;
            if (RequiredPrivilege == null) RequiredPrivilege = "";
        }
    }
}
