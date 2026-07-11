using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The arch guide shape. Implements <see cref="IGuideShape"/> on top of a non-uniform
    /// <see cref="CatmullRomSpline"/>, and owns the control-point list that defines the arch.
    /// </summary>
    /// <remarks>
    /// CONTROL-POINT LAYOUT. The list is always ordered:
    ///   index 0          phantom P0 (tangent handle for the start foot; never rendered/grabbed)
    ///   index 1          start anchor (green)
    ///   index 2 .. N−3   interior body points — one is the apex (primary/blue); any others are
    ///                    plain inserted points
    ///   index N−2        end anchor (green)
    ///   index N−1        phantom P4 (tangent handle for the end foot)
    /// A freshly created arch has exactly five points (P0, start, apex, end, P4 → two spline segments).
    /// Insertions only ever land in the interior, so the phantom/anchor positions in the list are stable.
    ///
    /// GUIDEDATA BINDING. The list returned by <see cref="ControlPoints"/> is the SAME instance the shape
    /// operates on. GuideData holds this exact reference, so mutations made through the shape's methods are
    /// immediately visible to GuideData with no copy step. Conversely, mutating that list from outside
    /// bypasses the shape's invariants (phantom upkeep, ordering) and should be avoided — go through the
    /// shape's methods instead.
    ///
    /// SPLINE USE. Each query builds a fresh CatmullRomSpline from the current point positions (cheap for
    /// the handful of points an arch has). The spline deep-copies those positions, so the shape never
    /// aliases its Vec3d into the spline.
    ///
    /// VERTICAL FEET. Phantom positions are derived so the curve leaves the start foot and arrives at the
    /// end foot vertically. Because the spline's tangent at an interior point is proportional to the secant
    /// (P[i+1] − P[i−1]), placing each phantom so that secant is purely vertical makes the foot tangent
    /// exactly vertical. See <see cref="RecalculatePhantomPoints"/>.
    /// </remarks>
    public sealed class ArchShape : IGuideShape
    {
        // Apex rises this fraction of the chord length above the chord midpoint — a natural arch proportion.
        private const double ApexHeightFraction = 0.4;
        // Minimum vertical phantom offset, so a near-flat foot can't collapse the tangent to ~zero.
        private const double MinPhantomDrop = 0.1;
        // Module-7 refinement: control-point markers are SINGLE-voxel — each special point (anchor /
        // primary / locked) claims exactly the one sampled voxel nearest to it, maximally precise at every
        // scale. (The old radius-blob constants are gone.)

        private readonly List<ControlPoint> _controlPoints;
        private readonly double _alpha;
        private ShapeConstraint _constraint;

        public ShapeConstraint Constraint => _constraint;

        /// <summary>
        /// The live control-point list (see remarks on GuideData binding). Mutate only via this shape's
        /// methods; external mutation bypasses the arch's invariants.
        /// </summary>
        public List<ControlPoint> ControlPoints => _controlPoints;

        /// <summary>
        /// Creates a new arch from two foot positions, auto-generating the five-point spine (phantoms,
        /// anchors, and an apex placed vertically above the chord midpoint at
        /// <see cref="ApexHeightFraction"/> of the chord length). The shape owns the new list; transfer it
        /// into a GuideData via <see cref="ControlPoints"/> to persist the guide.
        /// </summary>
        public ArchShape(Vec3d start, Vec3d end, double alpha = 0.5,
            ShapeConstraint constraint = ShapeConstraint.None, bool inverted = false)
        {
            _alpha = ClampAlpha(alpha);
            _constraint = constraint == ShapeConstraint.SemiCircle ? ShapeConstraint.SemiCircle : ShapeConstraint.None;
            _controlPoints = BuildInitialSpine(start, end, inverted);
            if (_constraint == ShapeConstraint.SemiCircle)
            {
                // A half-circle stores only its feet — the arc (and its apex marker) derives from them, so
                // the free apex control point is dropped. Spine: [phantom, A, B, phantom]. The opening
                // direction survives the removal because the phantoms carry it (see RecalculatePhantomsOn).
                _controlPoints.RemoveAt(2);
                RecalculatePhantomPoints();
            }
        }

        /// <summary>
        /// Reconstructs an arch as a behavioural view over an EXISTING control-point list (e.g. one loaded
        /// into a GuideData). The list is adopted by reference, not copied. Phantoms are taken as-is; callers
        /// restoring from save should call <see cref="RecalculatePhantomPoints"/> afterward if they want them
        /// re-derived against the current formula.
        /// </summary>
        public ArchShape(List<ControlPoint> controlPoints, double alpha = 0.5,
            ShapeConstraint constraint = ShapeConstraint.None)
        {
            _controlPoints = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            _alpha = ClampAlpha(alpha);
            _constraint = constraint == ShapeConstraint.SemiCircle ? ShapeConstraint.SemiCircle : ShapeConstraint.None;
        }

        // --- IGuideShape -----------------------------------------------------------------------

        public List<VoxelPosition> GetVoxelPositions(int scale, bool filled = false)
        {
            List<VoxelPosition> raw = SampleBodyCells(scale, filled);
            if (raw.Count == 0) return raw;

            // Collect only the control points that actually paint a colour (locked / primary / anchor);
            // plain unlocked points leave the body yellow and need no marker.
            int cpCount = _controlPoints.Count;
            var mx = new double[cpCount + 1];
            var my = new double[cpCount + 1];
            var mz = new double[cpCount + 1];
            var mType = new VoxelRenderType[cpCount + 1];   // +1: room for the derived arc apex marker
            int markerCount = 0;
            for (int i = 0; i < cpCount; i++)
            {
                ControlPoint cp = _controlPoints[i];
                if (cp.IsPhantom) continue;
                VoxelRenderType t = TypeOf(cp);
                if (t == VoxelRenderType.Normal) continue;
                Vec3d p = cp.WorldPosition;
                mx[markerCount] = p.X; my[markerCount] = p.Y; mz[markerCount] = p.Z;
                mType[markerCount] = t;
                markerCount++;
            }

            // Under SemiCircle the apex is DERIVED (there is no apex control point), but it still deserves
            // its Green marker — synthesised at the arc's top, with the B3 even-span pairing intact.
            if (_constraint == ShapeConstraint.SemiCircle &&
                TryGetArcFrame(out Vec3d arcC, out Vec3d arcU, out Vec3d arcUp, out double arcR))
            {
                Vec3d apex = ArcPoint(arcC, arcU, arcUp, arcR, 0.5);
                mx[markerCount] = apex.X; my[markerCount] = apex.Y; mz[markerCount] = apex.Z;
                mType[markerCount] = VoxelRenderType.Primary;
                markerCount++;
            }

            double half = scale / 2.0;

            // SINGLE-VOXEL MARKERS (Module-7 refinement): each special point claims exactly the ONE sampled
            // voxel whose centre is nearest to it — never a radius blob, never zero (nearest always exists),
            // and immune to the floor() boundary ambiguity of anchors sitting exactly on cell corners. Where
            // two special points claim the same voxel, precedence decides (Locked > Primary > Anchor).
            //
            // APEX PARITY (Session-7 finding B3): when the span is EVEN, the true apex lies BETWEEN two
            // voxels — a lone marker would read half a cell off-centre. So for the PRIMARY marker only, if
            // the two nearest sampled voxels are near-equidistant from the apex point (measured at the
            // geometric / spine-parameter apex, which generalises to future shapes), BOTH are claimed. Odd
            // spans put the apex at a cell centre, making the nearest ~a full cell closer than the runner-up,
            // so the tolerance below (a fraction of a cell) cleanly separates the two cases. Anchors and
            // locked points stay strictly single — they mark exact endpoints, not midpoints.
            const double apexPairToleranceCells = 0.35;
            double worldVoxel = scale / 16.0;
            double pairTol = worldVoxel * apexPairToleranceCells;

            var result = new List<VoxelPosition>(raw.Count);
            result.AddRange(raw);

            for (int m = 0; m < markerCount; m++)
            {
                int bestIdx = -1, secondIdx = -1;
                double bestD2 = double.MaxValue, secondD2 = double.MaxValue;
                for (int i = 0; i < result.Count; i++)
                {
                    VoxelPosition v = result[i];
                    double cx = (v.X + half) / 16.0;   // voxel centre in world space
                    double cy = (v.Y + half) / 16.0;
                    double cz = (v.Z + half) / 16.0;
                    double dx = cx - mx[m], dy = cy - my[m], dz = cz - mz[m];
                    double d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < bestD2)
                    {
                        secondD2 = bestD2; secondIdx = bestIdx;
                        bestD2 = d2; bestIdx = i;
                    }
                    else if (d2 < secondD2)
                    {
                        secondD2 = d2; secondIdx = i;
                    }
                }

                if (bestIdx >= 0 &&
                    (result[bestIdx].Type == VoxelRenderType.Normal ||
                     Precedence(mType[m]) > Precedence(result[bestIdx].Type)))
                {
                    result[bestIdx] = result[bestIdx].WithType(mType[m]);
                }

                // Even-span apex: claim the runner-up too when it ties the winner within tolerance.
                if (mType[m] == VoxelRenderType.Primary && secondIdx >= 0 &&
                    Math.Sqrt(secondD2) - Math.Sqrt(bestD2) <= pairTol &&
                    (result[secondIdx].Type == VoxelRenderType.Normal ||
                     Precedence(mType[m]) > Precedence(result[secondIdx].Type)))
                {
                    result[secondIdx] = result[secondIdx].WithType(mType[m]);
                }
            }

            return result;
        }

        public int GetVoxelCount(int scale, bool filled = false)
        {
            if (filled || _constraint == ShapeConstraint.SemiCircle)
                return SampleBodyCells(scale, filled).Count;   // arc/fill paths share the marching sampler

            // 1:1 with GetVoxelPositions (typing neither adds nor removes voxels), so the spline's own
            // deduplicated cell count is the exact answer — and skips the typing pass and list allocation.
            return BuildSpline().CountVoxels(scale);
        }

        public Vec3d GetPointAt(float t)
        {
            if (_constraint == ShapeConstraint.SemiCircle && TryGetArcFrame(out Vec3d c, out Vec3d ua, out Vec3d up, out double r))
            {
                double arcT = t < 0f ? 0.0 : t > 1f ? 1.0 : t;
                return ArcPoint(c, ua, up, r, arcT);
            }
            CatmullRomSpline spline = BuildSpline();
            if (!spline.HasCurve)
            {
                // Degenerate shape: fall back to the first real point so callers always get something sane.
                ControlPoint first = _controlPoints.Count > 0 ? _controlPoints[0] : null;
                return first != null
                    ? new Vec3d(first.WorldPosition.X, first.WorldPosition.Y, first.WorldPosition.Z)
                    : new Vec3d();
            }
            double tt = t < 0f ? 0.0 : t > 1f ? 1.0 : t;
            Vec3d p = spline.Evaluate(tt);
            return new Vec3d(p.X, p.Y, p.Z);    // fresh instance per the Vec3d ownership contract
        }

        public float GetNearestT(Vec3d worldPos)
        {
            if (_constraint == ShapeConstraint.SemiCircle && TryGetArcFrame(out Vec3d ac, out Vec3d ua, out Vec3d up, out double ar))
            {
                const int n = 128;
                double arcBestT = 0, arcBestD2 = double.MaxValue;
                for (int i = 0; i <= n; i++)
                {
                    double arcT = (double)i / n;
                    double d2 = Dist2(ArcPoint(ac, ua, up, ar, arcT), worldPos);
                    if (d2 < arcBestD2) { arcBestD2 = d2; arcBestT = arcT; }
                }
                return (float)arcBestT;
            }
            CatmullRomSpline spline = BuildSpline();
            if (!spline.HasCurve) return 0f;

            const int coarse = 128;
            double bestT = 0.0;
            double bestD2 = double.MaxValue;
            for (int j = 0; j <= coarse; j++)
            {
                double t = (double)j / coarse;
                double d2 = Dist2(spline.Evaluate(t), worldPos);
                if (d2 < bestD2) { bestD2 = d2; bestT = t; }
            }

            // Local refinement around the coarse minimum.
            double window = 1.0 / coarse;
            double lo = Math.Max(0.0, bestT - window);
            double hi = Math.Min(1.0, bestT + window);
            for (int iter = 0; iter < 4; iter++)
            {
                const int sub = 10;
                for (int j = 0; j <= sub; j++)
                {
                    double t = lo + (hi - lo) * j / sub;
                    double d2 = Dist2(spline.Evaluate(t), worldPos);
                    if (d2 < bestD2) { bestD2 = d2; bestT = t; }
                }
                double w = (hi - lo) / sub;
                lo = Math.Max(0.0, bestT - w);
                hi = Math.Min(1.0, bestT + w);
            }

            return (float)bestT;
        }

        public int GetNearestControlPointIndex(Vec3d worldPos)
        {
            // Index of the nearest non-phantom point by distance, or -1 if there are none. Locked points are
            // still reported (so they can be hover-highlighted or targeted to unlock); refusing to MOVE a
            // locked point is enforced upstream, not here.
            int bestIndex = -1;
            double bestD2 = double.MaxValue;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                if (cp.IsPhantom) continue;
                double d2 = Dist2(cp.WorldPosition, worldPos);
                if (d2 < bestD2) { bestD2 = d2; bestIndex = i; }
            }
            return bestIndex;
        }

        public void InsertControlPoint(float t, Vec3d position)
        {
            // An arbitrary interpolation point cannot live on a perfect half-circle; the manager breaks
            // the constraint before inserting, but the shape stays safe if called directly.
            if (_constraint == ShapeConstraint.SemiCircle) BreakConstraint();
            if (_controlPoints.Count < 4) return; // no curve to insert into

            CatmullRomSpline spline = BuildSpline();
            if (!spline.HasCurve) return;

            int segStart = spline.SegmentStartIndexForT(Clamp(t, 0f, 1f));
            int insertIndex = segStart + 1; // always interior: never 0 and never the trailing phantom

            // Plain body point; the ControlPoint constructor deep-copies the position.
            var inserted = new ControlPoint(position);
            _controlPoints.Insert(insertIndex, inserted);

            RecalculatePhantomPoints();
            // Post-condition (per IGuideShape): the inserted point sits exactly at `position`, so it is now
            // the nearest grabbable control point to `position`.
        }

        public void MoveControlPoint(int index, Vec3d newPosition)
        {
            if (index < 0 || index >= _controlPoints.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            // In-place mutation of the owned Vec3d — no allocation, no aliasing. Point-lock policy is NOT
            // enforced here (that is an upstream concern). Moving a phantom index would be a logical no-op
            // since RecalculatePhantomPoints overwrites phantoms below.
            ControlPoint cp = _controlPoints[index];
            cp.SetPosition(newPosition.X, newPosition.Y, newPosition.Z);

            RecalculatePhantomPoints();
        }

        public void RecalculatePhantomPoints() => RecalculatePhantomsOn(_controlPoints);

        // --- construction helpers --------------------------------------------------------------

        private List<ControlPoint> BuildInitialSpine(Vec3d start, Vec3d end, bool inverted = false)
        {
            double mxp = (start.X + end.X) / 2.0;
            double myp = (start.Y + end.Y) / 2.0;
            double mzp = (start.Z + end.Z) / 2.0;
            double span = Distance(start, end);
            // SHIFT-inverted (Session 11): the apex is born BELOW the chord — the arch opens downward.
            double rise = (inverted ? -1.0 : 1.0) * ApexHeightFraction * span;
            var apex = new Vec3d(mxp, myp + rise, mzp);

            var list = new List<ControlPoint>(5)
            {
                new ControlPoint(new Vec3d(), isPhantom: true),   // P0 — position filled in by recalc
                new ControlPoint(start, isAnchor: true),          // P1
                new ControlPoint(apex, isPrimary: true),          // P2 (apex)
                new ControlPoint(end, isAnchor: true),            // P3
                new ControlPoint(new Vec3d(), isPhantom: true),   // P4 — position filled in by recalc
            };

            RecalculatePhantomsOn(list);
            return list;
        }

        private static void RecalculatePhantomsOn(List<ControlPoint> list)
        {
            int n = list.Count;
            if (n < 4) return;

            Vec3d startAnchor = list[1].WorldPosition;
            Vec3d startNeighbor = list[2].WorldPosition;
            Vec3d endAnchor = list[n - 2].WorldPosition;
            Vec3d endNeighbor = list[n - 3].WorldPosition;

            // OPENING DIRECTION (Session 11, SHIFT-invert). The phantoms used to drop unconditionally
            // BELOW the feet (up-opening arches only); a downward-opening arch needs them ABOVE so the
            // curve still departs the feet vertically, toward its own body. The side is derived from the
            // stored geometry, so it persists, syncs, and survives every re-derivation:
            //   • interior points present → their average height vs the anchors' decides (an arch whose
            //     body sits below its feet opens downward);
            //   • no interior (the semicircle's [phantom, A, B, phantom] spine) → the phantoms KEEP the
            //     side they already have (they were seeded correctly at construction, before the derived
            //     apex was removed).
            double openSign = 1.0;                              // +1 = opens up (phantoms below the feet)
            if (n >= 5)
            {
                double interiorY = 0;
                for (int i = 2; i <= n - 3; i++) interiorY += list[i].WorldPosition.Y;
                interiorY /= n - 4;
                double anchorY = (startAnchor.Y + endAnchor.Y) * 0.5;
                const double eps = 1e-9;
                if (interiorY < anchorY - eps) openSign = -1.0;
            }
            else
            {
                double phantomY = (list[0].WorldPosition.Y + list[n - 1].WorldPosition.Y) * 0.5;
                double anchorY = (startAnchor.Y + endAnchor.Y) * 0.5;
                if (phantomY > anchorY) openSign = -1.0;        // phantoms above = a downward arc
            }

            // Session-9 batch-3 revision ("the entire arch shifts when I lock a voxel"): the phantom DROP
            // is now derived from the ANCHOR CHORD, not reflected from the neighbor's height. The old
            // reflection made the feet's tangent tension a function of whichever knot happened to sit next
            // to each anchor — inserting a locked point near a foot (or any near-foot point drifting during
            // a drag) re-derived the phantom and re-tilted the ENTIRE curve; worst case, a near-foot,
            // foot-height insert collapsed the drop from 0.4·span to the 0.1 minimum, a ~whole-arch lurch
            // from a single right-click. The chord is a function of the anchors alone, so end tension is
            // now invariant under every interior operation. A fresh arch is pixel-identical (its apex sits
            // at ApexHeightFraction·span, so the old reflection equalled ApexHeightFraction·chord anyway).
            // The phantom's X/Z must still match the neighbor — that equality IS the vertical-feet
            // invariant (secant tangent P2−P0 has zero horizontal component only when their X/Z agree) —
            // so a small residual parameterisation effect near a foot remains when its neighbor changes;
            // the dominant shift term is gone.
            double chord = Distance(startAnchor, endAnchor);
            double drop = openSign * Math.Max(MinPhantomDrop, ApexHeightFraction * chord);

            Vec3d p0 = VerticalFootPhantom(startAnchor, startNeighbor, drop);
            Vec3d pEnd = VerticalFootPhantom(endAnchor, endNeighbor, drop);

            list[0].SetPosition(p0.X, p0.Y, p0.Z);
            list[n - 1].SetPosition(pEnd.X, pEnd.Y, pEnd.Z);
        }

        // Places a phantom so the spline's secant tangent at the foot is purely vertical: matching the
        // neighbor's X/Z zeroes the horizontal tangent; the chord-derived drop sets a stable handle
        // magnitude away from the body (below the feet for an up-opening arch, above for an inverted one —
        // the caller passes the drop pre-signed).
        private static Vec3d VerticalFootPhantom(Vec3d anchor, Vec3d neighbor, double drop)
        {
            return new Vec3d(neighbor.X, anchor.Y - drop, neighbor.Z);
        }

        // --- small helpers ---------------------------------------------------------------------

        // ==========================================================================================
        //  Session-8 machinery: constraint (SemiCircle), fill, curve sampling
        // ==========================================================================================

        /// <summary>
        /// Every body cell of the shape: the curve (spline, or true circular arc under SemiCircle) plus,
        /// when filled, the ruled region between the curve and the foot-to-foot chord — the settled arch
        /// fill: "the two feet connected with a straight line and the rest filled in". Each curve sample
        /// at parameter t rules to the chord at the same t, marched at ≤ half-cell steps so the region has
        /// no holes; for a half-circle this yields the half-disc exactly.
        /// </summary>
        private List<VoxelPosition> SampleBodyCells(int scale, bool filled)
        {
            bool arc = _constraint == ShapeConstraint.SemiCircle
                && TryGetArcFrame(out Vec3d c, out Vec3d ua, out Vec3d up, out double r);

            List<VoxelPosition> cells;
            var seen = new HashSet<(int, int, int)>();

            if (arc)
            {
                TryGetArcFrame(out c, out ua, out up, out r);
                double cell = scale / 16.0;
                int steps = Math.Max(32, Math.Min(8192, (int)Math.Ceiling(Math.PI * r / (cell * 0.5))));
                var samples = new List<Vec3d>(steps + 1);
                for (int i = 0; i <= steps; i++) samples.Add(ArcPoint(c, ua, up, r, (double)i / steps));
                cells = new List<VoxelPosition>();
                VoxelMarch.MarchInto(cells, seen, samples, scale);
            }
            else
            {
                CatmullRomSpline spline = BuildSpline();
                cells = spline.SampleVoxelPositions(scale);
                if (filled)
                    foreach (VoxelPosition v in cells) seen.Add((v.X, v.Y, v.Z));
            }

            if (filled && TryGetAnchors(out Vec3d footA, out Vec3d footB))
            {
                double cell = scale / 16.0;
                // Rule density along t: at least one rule per half-cell of the LONGER of curve/chord.
                double chordLen = Dist(footA, footB);
                double curveLen = arc ? Math.PI * Dist(footA, footB) * 0.5 : BuildSpline().GetArcLength();
                int rules = Math.Max(16, Math.Min(8192,
                    (int)Math.Ceiling(Math.Max(chordLen, curveLen) / (cell * 0.5))));
                for (int i = 0; i <= rules; i++)
                {
                    double t = (double)i / rules;
                    Vec3d onCurve = GetPointAt((float)t);
                    var onChord = new Vec3d(
                        footA.X + (footB.X - footA.X) * t,
                        footA.Y + (footB.Y - footA.Y) * t,
                        footA.Z + (footB.Z - footA.Z) * t);
                    VoxelMarch.MarchSegmentInto(cells, seen, onCurve, onChord, scale);
                }
            }
            return cells;
        }

        public List<Vec3d> SampleCurve(int samples)
        {
            var list = new List<Vec3d>();
            int n = Math.Max(8, samples);
            if (_constraint == ShapeConstraint.SemiCircle &&
                TryGetArcFrame(out Vec3d c, out Vec3d ua, out Vec3d up, out double r))
            {
                for (int i = 0; i <= n; i++) list.Add(ArcPoint(c, ua, up, r, (double)i / n));
                return list;
            }
            CatmullRomSpline spline = BuildSpline();
            if (!spline.HasCurve) return list;
            for (int i = 0; i <= n; i++)
            {
                Vec3d p = spline.Evaluate((double)i / n);
                list.Add(new Vec3d(p.X, p.Y, p.Z));
            }
            return list;
        }

        public bool WouldBreakOnMove(int index) => false;   // the feet always absorb; inserts break instead

        /// <summary>
        /// SemiCircle → free arch, materialised with three interior points sampled ON the arc (quarter,
        /// apex — marked Primary — and three-quarter) so the spline hugs the circle and the break is
        /// visually seamless. Circle-family breaks live in EllipseShape.
        /// </summary>
        public bool BreakConstraint()
        {
            if (_constraint != ShapeConstraint.SemiCircle) return false;
            _constraint = ShapeConstraint.None;
            if (!TryGetArcFrame(out Vec3d c, out Vec3d ua, out Vec3d up, out double r)) return true;
            if (_controlPoints.Count < 4) return true;

            // Spine is [phantom, A, B, phantom] → becomes [phantom, A, q1, apex, q3, B, phantom].
            int insertAt = _controlPoints.Count - 2;        // just before the end anchor
            _controlPoints.Insert(insertAt, new ControlPoint(ArcPoint(c, ua, up, r, 0.75)));
            _controlPoints.Insert(insertAt, new ControlPoint(ArcPoint(c, ua, up, r, 0.50), isPrimary: true));
            _controlPoints.Insert(insertAt, new ControlPoint(ArcPoint(c, ua, up, r, 0.25)));
            RecalculatePhantomPoints();
            return true;
        }

        // The half-circle's frame: centre and radius from the feet; the arc rises along the in-plane
        // perpendicular that points most upward (flipped if needed so arches open toward the sky). A
        // vertical foot chord falls back to a horizontal arc direction. SHIFT-inverted half-circles
        // (Session 11) open downward instead: the stored phantoms carry the side (they sit ABOVE the feet
        // on an inverted arc — see RecalculatePhantomsOn), and the arc direction follows them.
        private bool TryGetArcFrame(out Vec3d center, out Vec3d uAxis, out Vec3d upAxis, out double radius)
        {
            center = uAxis = upAxis = null; radius = 0;
            if (!TryGetAnchors(out Vec3d a, out Vec3d b)) return false;
            center = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            uAxis = new Vec3d(a.X - center.X, a.Y - center.Y, a.Z - center.Z);   // t=0 lands on foot A
            radius = Math.Sqrt(uAxis.X * uAxis.X + uAxis.Y * uAxis.Y + uAxis.Z * uAxis.Z);
            if (radius < 0.05) return false;

            double ux = uAxis.X / radius, uy = uAxis.Y / radius, uz = uAxis.Z / radius;
            // up' = worldUp with the chord direction projected out; near-vertical chords fall back to +X.
            double d = uy;                                   // dot(worldUp, û)
            double px = -ux * d, py = 1.0 - uy * d, pz = -uz * d;
            double plen = Math.Sqrt(px * px + py * py + pz * pz);
            if (plen < 1e-6) { px = 1.0 - ux * ux; py = -uy * ux; pz = -uz * ux; plen = Math.Sqrt(px * px + py * py + pz * pz); }
            if (plen < 1e-6) return false;
            upAxis = new Vec3d(px / plen, py / plen, pz / plen);

            // Inverted arc: phantoms above the feet → mirror the arc to the other side of the chord.
            if (_controlPoints.Count >= 4 && _controlPoints[0].IsPhantom)
            {
                double phantomY = (_controlPoints[0].WorldPosition.Y
                    + _controlPoints[_controlPoints.Count - 1].WorldPosition.Y) * 0.5;
                if (phantomY > (a.Y + b.Y) * 0.5)
                {
                    upAxis.X = -upAxis.X; upAxis.Y = -upAxis.Y; upAxis.Z = -upAxis.Z;
                }
            }
            return true;
        }

        private static Vec3d ArcPoint(Vec3d c, Vec3d uAxis, Vec3d upAxis, double r, double t)
        {
            double theta = t * Math.PI;                      // t: 0 = foot A, 0.5 = apex, 1 = foot B
            double ct = Math.Cos(theta), st = Math.Sin(theta);
            return new Vec3d(
                c.X + uAxis.X * ct + upAxis.X * r * st,
                c.Y + uAxis.Y * ct + upAxis.Y * r * st,
                c.Z + uAxis.Z * ct + upAxis.Z * r * st);
        }

        private bool TryGetAnchors(out Vec3d a, out Vec3d b)
        {
            a = b = null;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                ControlPoint cp = _controlPoints[i];
                if (cp.IsPhantom || !cp.IsAnchor) continue;
                if (a == null) a = cp.WorldPosition;
                else b = cp.WorldPosition;
            }
            return a != null && b != null;
        }

        private static double Dist(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private CatmullRomSpline BuildSpline()
        {
            var pts = new List<Vec3d>(_controlPoints.Count);
            for (int i = 0; i < _controlPoints.Count; i++)
                pts.Add(_controlPoints[i].WorldPosition); // spline deep-copies; no aliasing
            return new CatmullRomSpline(pts, _alpha);
        }

        // Resting render type of a control point, by precedence: a locked point shows as locked even if it
        // is also an anchor or the apex.
        private static VoxelRenderType TypeOf(ControlPoint cp)
        {
            if (cp.IsLocked) return VoxelRenderType.Locked;
            if (cp.IsPrimary) return VoxelRenderType.Primary;
            if (cp.IsAnchor) return VoxelRenderType.Anchor;
            return VoxelRenderType.Normal;
        }

        private static int Precedence(VoxelRenderType t)
        {
            switch (t)
            {
                case VoxelRenderType.Locked: return 3;
                case VoxelRenderType.Primary: return 2;
                case VoxelRenderType.Anchor: return 1;
                default: return 0;
            }
        }

        private static double Distance(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Dist2(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static double ClampAlpha(double a) => a < 0.0 ? 0.0 : (a > 1.0 ? 1.0 : a);
    }
}
