using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// Shared geometry for the Session-9 polygon family (Line / Triangle / Rectangle): the intrinsic
    /// planar frame derivation (the EllipseShape recipe, extracted) and single-voxel nearest-claim marker
    /// placement with the settled precedence (Locked > Primary > Anchor > Division > Normal). Pure math.
    /// </summary>
    internal static class ShapeGeometry
    {
        /// <summary>
        /// The in-plane frame for a shape whose base direction is a→b under a preferred plane normal:
        /// û along a→b, m̂ the in-plane perpendicular (normal × û). Robust to arbitrary 3D drags — the
        /// normal is the preferred axis with û projected out, with axis fallbacks when parallel.
        /// False when a≈b.
        /// </summary>
        /// <remarks>
        /// DEFAULT-UP (Session 11, human-requested): m̂'s SIGN used to follow the anchor order (Cross of
        /// the derived normal with û flips when a and b swap), so a shape whose "up" derives from +m̂ —
        /// notably a fresh triangle's apex — spawned upside-down depending on which way the base was
        /// clicked. m̂ is now sign-normalised: world-up-biased when it has any vertical component, and
        /// deterministically axis-biased (+Z, then +X) when it lies flat (a shape drawn on the ground,
        /// where no direction is "up"). Placement order can no longer flip a shape; SHIFT-at-placement is
        /// the one way to invert.
        /// </remarks>
        public static bool TryGetFrame(Vec3d a, Vec3d b, PlaneAxis preferredAxis,
            out Vec3d uHat, out Vec3d mHat, out double baseLen)
        {
            uHat = mHat = null;
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            baseLen = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (baseLen < 0.05) return false;
            uHat = new Vec3d(dx / baseLen, dy / baseLen, dz / baseLen);

            Vec3d n = ProjectOutAndNormalise(AxisVec(preferredAxis), uHat)
                   ?? ProjectOutAndNormalise(AxisVec(PlaneAxis.Y), uHat)
                   ?? ProjectOutAndNormalise(AxisVec(PlaneAxis.X), uHat);
            mHat = Cross(n, uHat);

            const double eps = 1e-9;
            if (mHat.Y < -eps
                || (Math.Abs(mHat.Y) <= eps && (mHat.Z < -eps
                || (Math.Abs(mHat.Z) <= eps && mHat.X < -eps))))
            {
                mHat.X = -mHat.X; mHat.Y = -mHat.Y; mHat.Z = -mHat.Z;
            }
            return true;
        }

        /// <summary>
        /// The DETERMINISTIC base-plane normal for a 3D volume whose base direction is <paramref name="uHat"/>
        /// under a preferred plane normal (0.1.23): the preferred axis with û projected out, sign fixed to
        /// the preferred axis's positive direction. Unlike <c>Cross(û, m̂)</c> — whose sign flips when the
        /// base anchors are dragged to the other side, inverting the volume — this points the SAME way
        /// regardless of anchor order (a ground volume always rises +Y; SHIFT is the only way to invert).
        /// Falls back through Y then X when û is parallel to the preferred axis.
        /// </summary>
        public static Vec3d BaseNormal(Vec3d uHat, PlaneAxis preferredAxis)
        {
            Vec3d Signed(PlaneAxis axis)
            {
                Vec3d ax = AxisVec(axis);
                Vec3d n = ProjectOutAndNormalise(ax, uHat);
                if (n == null) return null;
                // Keep the sign aligned with the positive axis direction (so ground → +Y, not −Y).
                if (Dot(n, ax) < 0) { n = new Vec3d(-n.X, -n.Y, -n.Z); }
                return n;
            }
            return Signed(preferredAxis) ?? Signed(PlaneAxis.Y) ?? Signed(PlaneAxis.X);
        }

        /// <summary>The two in-plane world axes of an axis-aligned plane (normal = <paramref name="axis"/>).</summary>
        public static void InPlaneAxes(PlaneAxis axis, out Vec3d u1, out Vec3d u2)
        {
            switch (axis)
            {
                case PlaneAxis.X: u1 = new Vec3d(0, 1, 0); u2 = new Vec3d(0, 0, 1); break;
                case PlaneAxis.Z: u1 = new Vec3d(1, 0, 0); u2 = new Vec3d(0, 1, 0); break;
                default:          u1 = new Vec3d(1, 0, 0); u2 = new Vec3d(0, 0, 1); break;
            }
        }

        /// <summary>
        /// Claims the single cell nearest <paramref name="worldPos"/> for <paramref name="type"/>, if that
        /// beats the cell's current role (Locked > Primary > Anchor > Division > Normal).
        /// </summary>
        public static void ClaimMarker(List<VoxelPosition> cells, int scale, Vec3d worldPos, VoxelRenderType type)
        {
            if (cells.Count == 0 || type == VoxelRenderType.Normal) return;
            double half = scale * 0.5;
            int best = -1; double bestD2 = double.MaxValue;
            for (int i = 0; i < cells.Count; i++)
            {
                double dx = (cells[i].X + half) / 16.0 - worldPos.X;
                double dy = (cells[i].Y + half) / 16.0 - worldPos.Y;
                double dz = (cells[i].Z + half) / 16.0 - worldPos.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            if (best >= 0 && Precedence(type) > Precedence(cells[best].Type))
                cells[best] = cells[best].WithType(type);
        }

        public static int Precedence(VoxelRenderType t) => t switch
        {
            VoxelRenderType.Locked => 4,
            VoxelRenderType.Primary => 3,
            VoxelRenderType.Anchor => 2,
            VoxelRenderType.Division => 1,
            _ => 0
        };

        /// <summary>
        /// Claims the nearest cell for <paramref name="type"/>, AND the runner-up when <paramref name="worldPos"/>
        /// lies ~midway between two cells (their centre distances tie within <paramref name="pairToleranceCells"/>).
        /// This is the arch apex's even-span pairing generalised: when a boundary falls between two voxels a lone
        /// mark reads half a cell off, so both are claimed to keep the mark visually centred / the parts even.
        /// </summary>
        public static void ClaimMarkerPaired(List<VoxelPosition> cells, int scale, Vec3d worldPos,
            VoxelRenderType type, double pairToleranceCells = 0.35)
        {
            if (cells.Count == 0 || type == VoxelRenderType.Normal) return;
            double half = scale * 0.5;
            int best = -1, second = -1;
            double bestD2 = double.MaxValue, secondD2 = double.MaxValue;
            for (int i = 0; i < cells.Count; i++)
            {
                double dx = (cells[i].X + half) / 16.0 - worldPos.X;
                double dy = (cells[i].Y + half) / 16.0 - worldPos.Y;
                double dz = (cells[i].Z + half) / 16.0 - worldPos.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestD2) { secondD2 = bestD2; second = best; bestD2 = d2; best = i; }
                else if (d2 < secondD2) { secondD2 = d2; second = i; }
            }
            if (best >= 0 && Precedence(type) > Precedence(cells[best].Type))
                cells[best] = cells[best].WithType(type);

            // Runner-up only when it near-ties the winner (boundary between two cells). Odd/aligned boundaries
            // sit on a cell centre, so the winner is ~a full cell closer and the pair does not trigger.
            double pairTol = (scale / 16.0) * pairToleranceCells;
            if (second >= 0 && Math.Sqrt(secondD2) - Math.Sqrt(bestD2) <= pairTol
                && Precedence(type) > Precedence(cells[second].Type))
                cells[second] = cells[second].WithType(type);
        }

        // --- small vector helpers (component form, per codebase style) --------------------------------

        public static Vec3d AxisVec(PlaneAxis axis) => axis switch
        {
            PlaneAxis.X => new Vec3d(1, 0, 0),
            PlaneAxis.Z => new Vec3d(0, 0, 1),
            _ => new Vec3d(0, 1, 0)
        };

        public static Vec3d ProjectOutAndNormalise(Vec3d v, Vec3d unit)
        {
            double d = Dot(v, unit);
            var r = new Vec3d(v.X - unit.X * d, v.Y - unit.Y * d, v.Z - unit.Z * d);
            double len = Len(r);
            return len < 1e-6 ? null : new Vec3d(r.X / len, r.Y / len, r.Z / len);
        }

        public static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(
            a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static double Len(Vec3d a) => Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        public static double Dist(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        public static Vec3d Lerp(Vec3d a, Vec3d b, double f) =>
            new Vec3d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
    }
}
