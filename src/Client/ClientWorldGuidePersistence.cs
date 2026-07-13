using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Client;
using Layout.Systems;

namespace Layout.Client
{
    /// <summary>
    /// File-backed guide storage isolated by Vintage Story's globally unique savegame identifier.
    /// This is used only by client-only authority; networked worlds continue to use server save data.
    /// </summary>
    public sealed class ClientWorldGuidePersistence : IGuidePersistence
    {
        private readonly string _filePath;

        public string FilePath => _filePath;

        public ClientWorldGuidePersistence(ICoreClientAPI capi)
        {
            if (capi == null) throw new ArgumentNullException(nameof(capi));

            string savegameId = capi.World?.SavegameIdentifier;
            if (string.IsNullOrWhiteSpace(savegameId))
                throw new InvalidOperationException("The current world's savegame identifier is unavailable.");

            string playerUid = capi.World?.Player?.PlayerUID;
            if (string.IsNullOrWhiteSpace(playerUid))
                throw new InvalidOperationException("The current player's UID is unavailable.");

            string worldKey = CreateStableKey(savegameId);
            string playerKey = CreateStableKey(playerUid);
            string layoutDataPath = capi.GetOrCreateDataPath("Layout");
            string guideFolder = Path.Combine(layoutDataPath, "ClientOnlyGuides");
            Directory.CreateDirectory(guideFolder);
            _filePath = Path.Combine(guideFolder, worldKey + "-" + playerKey + ".json");

            // One-time migration from the earlier world-only filename. Move only after the player UID is
            // known so private guides on a shared computer do not remain visible to every account.
            string legacyPath = Path.Combine(guideFolder, worldKey + ".json");
            if (!File.Exists(_filePath) && File.Exists(legacyPath))
                File.Move(legacyPath, _filePath);
        }

        public byte[] Load(string key)
        {
            return File.Exists(_filePath) ? File.ReadAllBytes(_filePath) : null;
        }

        public void Store(string key, byte[] data)
        {
            string temporaryPath = _filePath + ".tmp";
            File.WriteAllBytes(temporaryPath, data ?? Array.Empty<byte>());
            File.Move(temporaryPath, _filePath, overwrite: true);
        }

        private static string CreateStableKey(string value)
        {
            if (Guid.TryParse(value, out Guid guid)) return guid.ToString("N");

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
