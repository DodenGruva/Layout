namespace Layout.Guide
{
    /// <summary>The coordinate grid for Layout geometry and transforms in this build.</summary>
    internal static class VoxelGrid
    {
        public const int ChiselShift = 4;
        public const int ChiselCellsPerBlock = 1 << ChiselShift;
        public const int BlockShift = 6;
        public const int CellsPerBlock = 1 << BlockShift;
        public const double UnitsPerBlock = CellsPerBlock;
        public const int ChiselScale = CellsPerBlock / ChiselCellsPerBlock;

        // Arithmetic shifts also floor cells just below zero.
        public static int ToBlock(int cell) => cell >> BlockShift;
        public static int ToChiselCell(int cell) => cell >> (BlockShift - ChiselShift);
    }
}
