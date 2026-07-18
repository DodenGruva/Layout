using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Layout.Systems
{
    /// <summary>
    /// The chalk feedback effects (F5 polish): a yellow chalk-dust puff and the chalk-line "snap" heard
    /// when a guide is placed. Pure fire-and-forget helpers — callable from either side (a server world
    /// broadcasts the particles/sound to every client in range; a client world plays them locally, which
    /// is exactly right for PRIVATE placements only that player can see).
    /// </summary>
    public static class ChalkEffects
    {
        // Chalk-dust yellow, matching the guide body's colour language.
        private static readonly int ChalkColor = ColorUtil.ToRgba(180, 235, 205, 90);

        /// <summary>A short-lived puff of yellow chalk dust centred on <paramref name="pos"/>.</summary>
        public static void SpawnChalkPuff(IWorldAccessor world, Vec3d pos)
        {
            if (world == null || pos == null) return;

            var puff = new SimpleParticleProperties(
                8f, 16f, ChalkColor,
                new Vec3d(pos.X - 0.15, pos.Y, pos.Z - 0.15),
                new Vec3d(pos.X + 0.15, pos.Y + 0.15, pos.Z + 0.15),
                new Vec3f(-0.4f, 0.1f, -0.4f),
                new Vec3f(0.4f, 0.6f, 0.4f),
                lifeLength: 0.7f,
                gravityEffect: 0.35f,
                minSize: 0.08f,
                maxSize: 0.22f,
                model: EnumParticleModel.Quad);

            world.SpawnParticles(puff);
        }

        /// <summary>
        /// The completed-placement feedback: the taut-string snap of a real chalk line (the vanilla
        /// bow-release twang, slightly quiet) at the guide's midpoint, plus a dust puff at each anchor.
        /// Deliberately independent of the chalk-durability system — it is placement feedback, so it also
        /// plays for creative players and on durability-disabled servers.
        /// </summary>
        public static void PlacementEffects(IWorldAccessor world, Vec3d start, Vec3d end)
        {
            if (world == null || start == null) return;

            Vec3d mid = end == null
                ? start
                : new Vec3d((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2);
            world.PlaySoundAt(new AssetLocation("sounds/bow-release"),
                mid.X, mid.Y, mid.Z, null, true, 32f, 0.55f);

            SpawnChalkPuff(world, start);
            if (end != null && end.SquareDistanceTo(start) > 0.01) SpawnChalkPuff(world, end);
        }
    }
}
