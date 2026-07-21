using System;
using System.Collections.Generic;
using Layout.Guide;
using Layout.Shapes;
using Vintagestory.API.MathTools;

namespace Layout.Systems
{
    /// <summary>
    /// Immutable, deep-copied description of one local draft pose.  The controller creates these on the
    /// main thread; the renderer may safely turn them into geometry on a worker thread.  A generation id
    /// makes late results harmless when the player moves again before a refinement finishes.
    /// </summary>
    public sealed class DraftPreviewSpec
    {
        public int Generation { get; }
        public GuideRenderSettings Settings { get; }
        public GuideShapeType ShapeType { get; }
        public ShapeConstraint Constraint { get; }
        public PlaneAxis PlaneAxis { get; }
        public int Sides { get; }
        public bool Inverted { get; }
        public bool FlatSideAligned { get; }
        public bool IsChain { get; }
        public bool ChainClosing { get; }
        public Vec3d Start { get; }
        public Vec3d End { get; }
        public Vec3d Apex { get; }
        public Vec3d Rim { get; }
        public IReadOnlyList<Vec3d> Chain { get; }

        /// <summary>The placement point currently controlled by the crosshair for this draft stage.</summary>
        public Vec3d ActiveAim { get; }

        public DraftPreviewSpec(
            int generation,
            GuideRenderSettings settings,
            GuideShapeType shapeType,
            ShapeConstraint constraint,
            PlaneAxis planeAxis,
            Vec3d start,
            Vec3d end,
            int sides = 0,
            bool inverted = false,
            Vec3d apex = null,
            Vec3d rim = null,
            bool flatSideAligned = false)
        {
            Generation = generation;
            Settings = settings;
            ShapeType = shapeType;
            Constraint = constraint;
            PlaneAxis = planeAxis;
            Sides = sides;
            Inverted = inverted;
            FlatSideAligned = flatSideAligned;
            Start = Copy(start);
            End = Copy(end);
            Apex = Copy(apex);
            Rim = Copy(rim);
            ActiveAim = Copy(rim ?? apex ?? end);
            Chain = Array.Empty<Vec3d>();
        }

        public DraftPreviewSpec(
            int generation,
            GuideRenderSettings settings,
            IReadOnlyList<Vec3d> chain,
            Vec3d aim,
            bool closing)
        {
            Generation = generation;
            Settings = settings;
            ShapeType = GuideShapeType.FreeShape;
            Constraint = ShapeConstraint.None;
            PlaneAxis = PlaneAxis.Y;
            IsChain = true;
            ChainClosing = closing;

            var copy = new List<Vec3d>((chain?.Count ?? 0) + (closing || aim == null ? 0 : 1));
            if (chain != null)
                for (int i = 0; i < chain.Count; i++) copy.Add(Copy(chain[i]));
            if (!closing && aim != null) copy.Add(Copy(aim));
            Chain = copy;
            Start = copy.Count > 0 ? Copy(copy[0]) : null;
            End = copy.Count > 1 ? Copy(copy[copy.Count - 1]) : null;
            ActiveAim = Copy(closing && copy.Count > 0 ? copy[0] : End);
        }

        public DraftPreviewSpec WithGeneration(int generation)
        {
            if (IsChain)
                return new DraftPreviewSpec(generation, Settings, Chain, null, ChainClosing);
            return new DraftPreviewSpec(generation, Settings, ShapeType, Constraint, PlaneAxis,
                Start, End, Sides, Inverted, Apex, Rim, FlatSideAligned);
        }

        public IGuideShape CreateShape()
        {
            if (IsChain) return new FreeShape(new List<Vec3d>(Chain), ChainClosing);

            IGuideShape shape = ShapeFactory.Create(ShapeType, Constraint, PlaneAxis, Start, End,
                Inverted, Sides, flatSideAligned: FlatSideAligned);
            DraftManager.ApplyPlacementPoints(shape, ShapeType, Constraint, Apex, Rim);
            return shape;
        }

        /// <summary>
        /// Grid-quantized content identity. Sub-cell mouse jitter does not restart settling, while every
        /// setting/stage/point change that alters visible geometry does.
        /// </summary>
        public ulong Fingerprint()
        {
            ulong h = 1469598103934665603UL;
            Mix(ref h, (long)ShapeType); Mix(ref h, (long)Constraint); Mix(ref h, (long)PlaneAxis);
            Mix(ref h, Settings.Scale); Mix(ref h, (long)Settings.Mode); Mix(ref h, Settings.Filled ? 1 : 0);
            Mix(ref h, Settings.Wireframe ? 1 : 0);
            Mix(ref h, (long)Settings.Plane.FlattenedAxis); Mix(ref h, Settings.Plane.PlaneOffset);
            Mix(ref h, Settings.Divisions); Mix(ref h, Sides); Mix(ref h, Inverted ? 1 : 0);
            Mix(ref h, FlatSideAligned ? 1 : 0); Mix(ref h, IsChain ? 1 : 0); Mix(ref h, ChainClosing ? 1 : 0);
            MixPoint(ref h, Start); MixPoint(ref h, End); MixPoint(ref h, Apex); MixPoint(ref h, Rim);
            if (IsChain)
                for (int i = 0; i < Chain.Count; i++) MixPoint(ref h, Chain[i]);
            return h;
        }

        private static Vec3d Copy(Vec3d p) => p == null ? null : new Vec3d(p.X, p.Y, p.Z);

        private static void MixPoint(ref ulong h, Vec3d p)
        {
            if (p == null) { Mix(ref h, long.MinValue); return; }
            Mix(ref h, (long)Math.Floor(p.X * 16.0));
            Mix(ref h, (long)Math.Floor(p.Y * 16.0));
            Mix(ref h, (long)Math.Floor(p.Z * 16.0));
        }

        private static void Mix(ref ulong h, long value)
        {
            unchecked
            {
                h ^= (ulong)value;
                h *= 1099511628211UL;
            }
        }
    }

    public sealed class DraftPreviewCompletedEventArgs : EventArgs
    {
        public int Generation { get; }
        public int RenderScale { get; }
        public int VoxelCount { get; }
        public GuideExtent Extent { get; }
        public long WorkMilliseconds { get; }

        public DraftPreviewCompletedEventArgs(
            int generation, int renderScale, int voxelCount, GuideExtent extent, long workMilliseconds)
        {
            Generation = generation;
            RenderScale = renderScale;
            VoxelCount = voxelCount;
            Extent = extent;
            WorkMilliseconds = workMilliseconds;
        }
    }

    public sealed class MovingDraftPreviewResult
    {
        public int RenderScale { get; }
        public bool IsFullShell { get; }
        public int VoxelCount { get; }
        public GuideExtent Extent { get; }
        public long WorkMilliseconds { get; }

        public MovingDraftPreviewResult(
            int renderScale, bool isFullShell, int voxelCount, GuideExtent extent, long workMilliseconds)
        {
            RenderScale = renderScale;
            IsFullShell = isFullShell;
            VoxelCount = voxelCount;
            Extent = extent;
            WorkMilliseconds = workMilliseconds;
        }
    }
}
