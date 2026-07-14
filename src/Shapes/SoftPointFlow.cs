using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The soft-point model (Session 8; trigger generalised in the Session-9 revision — the real fix for
    /// "the apex and every previously-grabbed point pin the arch like a lock"). STRUCTURAL points — anchors
    /// and Red-locked points — define the guide; every other real point is SOFT (the original apex
    /// included: its green marker is a visual reference, never a constraint). On EVERY drag, the flow
    /// baseline is the polyline through the structural points PLUS the point currently being held, and
    /// every other soft point is repositioned against it. TWO REGIMES (Session-9 batch-3, the final
    /// playtest-settled model):
    ///
    /// • INTERIOR GRAB (the held point is unlocked, non-anchor): the curve is defined by structural + hand
    ///   and NOTHING else — every other unlocked point is SLAVED directly ONTO that curve (station kept,
    ///   offset ZERO). The apex and every previously-grabbed point contribute no pull whatsoever: "as if
    ///   those points never existed and the arch isn't aware of them" (the human's spec, verbatim). The
    ///   visible consequence is intended: grab the body of a tall arch and its apex SETTLES onto the curve
    ///   through (anchor, hand, anchor) — its height is gone unless it was locked first. Locking is the
    ///   one and only way to make a point matter.
    ///
    /// • STRUCTURAL GRAB (the held point is an anchor or a lock): shape-preserving proportional flow —
    ///   each soft point keeps its station plus a frame-local, length-scaled offset, so dragging a foot
    ///   translates/stretches the arch instead of flattening it to the anchor chord. (Zero offsets here
    ///   would collapse every unlocked point onto the straight line between the feet the instant an
    ///   anchor was nudged — obviously not "only moves when I move it".) [Flagged: this regime split is
    ///   Claude's call; playtest arbitrates.]
    ///
    /// The baseline is a CURVE, not a polyline: a centripetal Catmull-Rom through the baseline points,
    /// densely sampled, so slaved points land on a smooth natural shape and structural-drag offsets are
    /// measured against the curve rather than chords. Offsets re-capture on every grab. Drag an anchor and the apex + inserts flow (the Session-8 behaviour); drag an
    /// inserted point and the apex flows around YOUR gesture (the Session-9 fix — previously soft-point
    /// drags fired no flow at all, so every untouched knot froze in place and read as a lock). No unlocked
    /// point can ever resist a drag; locking is the only pin. Released points keep the shape the drag left
    /// (nothing snaps back) — they simply cannot fight the next drag.
    /// </summary>
    /// <remarks>
    /// WHY PROPORTIONAL, FRAME-RELATIVE OFFSETS (Session-8 playtest revision; the absolute-offset first
    /// cut is superseded): with absolute world offsets, shrinking an arch by dragging its feet together
    /// left the apex at its FULL original height — every soft point kept its world-frame bump, so the
    /// curve stayed pinned at those heights and soft points still FELT like constraints. Offsets are now
    /// stored in the baseline's LOCAL FRAME (tangent / projected-up / side), scaled by the baseline's
    /// total length: drag the feet apart and the whole arch scales up with the span (matching the
    /// apex-height-proportional-to-chord design of a fresh arch); drag them together and it shrinks;
    /// swing one foot around the other and the shape follows, staying upright because the frame's "up"
    /// is world-up projected out of the tangent. Only anchors and locked points define the guide.
    ///
    /// WHO RUNS IT. Pure math over a control-point list, so the server (authoritative, per move update in
    /// ServerNetworkHandler) and the client (local drag preview in GuideToolController) run the identical
    /// code and land on the same values; the authoritative broadcast simply confirms the preview.
    /// Capture happens ONCE per drag from PRE-MOVE positions; reflow is recomputed per update from the
    /// same capture, so the mapping never drifts mid-drag. Only the ARCH family flows (the ellipse family
    /// has handles, not interior points).
    /// </remarks>
    public sealed class SoftPointFlow
    {
        private readonly int[] _structuralIdx;      // indices of structural points, in list order
        private readonly int[] _softIdx;            // indices of soft points
        private readonly double[] _station;         // per soft point: arc-length station in [0,1]
        private readonly Vec3d[] _offset;           // per soft point: offset in the LOCAL FRAME at its
                                                    //   station (X=along tangent, Y=projected-up, Z=side),
                                                    //   PRE-DIVIDED by the captured baseline length

        public bool HasWork => _softIdx.Length > 0 && _structuralIdx.Length >= 2;

        /// <summary>True if the point at <paramref name="index"/> is structural (anchor or locked).</summary>
        public static bool IsStructural(ControlPoint cp) => cp.IsAnchor || cp.IsLocked;

        private SoftPointFlow(int[] structural, int[] soft, double[] station, Vec3d[] offset)
        {
            _structuralIdx = structural; _softIdx = soft; _station = station; _offset = offset;
        }

        // The local frame at a station: tangent = the containing segment's direction; up = world-up with
        // the tangent projected out (falls back to +X for vertical segments); side = tangent × up. The
        // same construction runs at capture and at reflow, so offsets transported through it rotate and
        // scale with the baseline instead of staying world-pinned.
        private static bool TryFrameAt(Vec3d[] poly, double[] cum, double station,
            out Vec3d onPoly, out Vec3d tangent, out Vec3d up, out Vec3d side)
        {
            onPoly = PointAtStation(poly, cum, station);
            tangent = up = side = null;

            int seg = SegmentAtStation(poly, cum, station);
            double tx = poly[seg + 1].X - poly[seg].X;
            double ty = poly[seg + 1].Y - poly[seg].Y;
            double tz = poly[seg + 1].Z - poly[seg].Z;
            double tlen = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tlen < 1e-9) return false;
            tx /= tlen; ty /= tlen; tz /= tlen;
            tangent = new Vec3d(tx, ty, tz);

            // up' = worldUp − t̂(t̂·Ŷ), i.e. world-up with the tangent projected out.
            double ux = -tx * ty, uy = 1.0 - ty * ty, uz = -tz * ty;
            double ulen = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ulen < 1e-6)
            {
                // Vertical segment: fall back to +X projected out of the tangent.
                ux = 1.0 - tx * tx; uy = -ty * tx; uz = -tz * tx;
                ulen = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                if (ulen < 1e-6) return false;
            }
            up = new Vec3d(ux / ulen, uy / ulen, uz / ulen);

            side = new Vec3d(
                tangent.Y * up.Z - tangent.Z * up.Y,
                tangent.Z * up.X - tangent.X * up.Z,
                tangent.X * up.Y - tangent.Y * up.X);
            return true;
        }

        private static int SegmentAtStation(Vec3d[] poly, double[] cum, double station)
        {
            double target = station * cum[cum.Length - 1];
            for (int i = 1; i < poly.Length; i++)
                if (target <= cum[i] || i == poly.Length - 1) return i - 1;
            return poly.Length - 2;
        }

        /// <summary>
        /// Captures the soft↔baseline mapping from CURRENT (pre-move) positions, or null when there is
        /// nothing to flow. <paramref name="grabbedIndex"/> is the point about to be dragged: it joins the
        /// baseline for this drag (structural or not — the point in the player's hand transiently defines
        /// the shape alongside anchors and locks), and everything else unlocked flows.
        /// </summary>
        public static SoftPointFlow Capture(IReadOnlyList<ControlPoint> pts, int grabbedIndex)
        {
            var structural = new List<int>();
            var soft = new List<int>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].IsPhantom) continue;
                if (pts[i].IsLockMarker && !pts[i].IsLocked) continue;
                if (IsStructural(pts[i]) || i == grabbedIndex) structural.Add(i);
                else soft.Add(i);
            }
            if (structural.Count < 2 || soft.Count == 0) return null;

            Vec3d[] basePts = BaselinePositions(pts, structural);
            Vec3d[] poly = SampleBaselineCurve(basePts);
            if (poly == null) return null;
            double[] cum = CumulativeLengths(poly);
            double total = cum[cum.Length - 1];
            if (total < 1e-9) return null;

            // Regime selection (see class remarks): an interior grab slaves everything unlocked ONTO the
            // defining curve (zero offsets); a structural grab preserves shape (offsets kept).
            bool interiorGrab = grabbedIndex >= 0 && grabbedIndex < pts.Count
                && !IsStructural(pts[grabbedIndex]);

            var station = new double[soft.Count];
            var offset = new Vec3d[soft.Count];
            for (int s = 0; s < soft.Count; s++)
            {
                Vec3d p = pts[soft[s]].WorldPosition;
                NearestOnPolyline(poly, cum, p, out double st, out Vec3d on);
                station[s] = st;

                if (interiorGrab)
                {
                    offset[s] = new Vec3d(0, 0, 0);       // pure slave: the point IS the curve at its station
                    continue;
                }

                if (!TryFrameAt(poly, cum, st, out _, out Vec3d t, out Vec3d u, out Vec3d w))
                    return null;                          // degenerate baseline — nothing can flow safely

                double ox = p.X - on.X, oy = p.Y - on.Y, oz = p.Z - on.Z;
                offset[s] = new Vec3d(                    // frame components, scaled by 1/length
                    (ox * t.X + oy * t.Y + oz * t.Z) / total,
                    (ox * u.X + oy * u.Y + oz * u.Z) / total,
                    (ox * w.X + oy * w.Y + oz * w.Z) / total);
            }
            return new SoftPointFlow(structural.ToArray(), soft.ToArray(), station, offset);
        }

        /// <summary>
        /// Computes where every soft point should sit given a hypothetical move of one structural point,
        /// WITHOUT mutating anything — the caller folds these into the same edit batch as the grabbed
        /// point, so cap checks, undo origins, broadcast, and persistence all see one atomic update.
        /// </summary>
        public List<(int index, Vec3d position)> ComputeReflowEdits(
            IReadOnlyList<ControlPoint> pts, int movedIndex, Vec3d movedPosition)
        {
            var edits = new List<(int, Vec3d)>(_softIdx.Length);
            if (!HasWork) return edits;

            // Baseline from current positions, with the pending move substituted in — then curved.
            var basePts = new Vec3d[_structuralIdx.Length];
            for (int i = 0; i < _structuralIdx.Length; i++)
            {
                int idx = _structuralIdx[i];
                Vec3d src = idx == movedIndex ? movedPosition : pts[idx].WorldPosition;
                basePts[i] = new Vec3d(src.X, src.Y, src.Z);
            }
            Vec3d[] poly = SampleBaselineCurve(basePts);
            if (poly == null) return edits;
            double[] cum = CumulativeLengths(poly);
            double length = cum[cum.Length - 1];
            if (length < 1e-9) return edits;

            for (int s = 0; s < _softIdx.Length; s++)
            {
                if (!TryFrameAt(poly, cum, _station[s], out Vec3d on, out Vec3d t, out Vec3d u, out Vec3d w))
                    continue;
                double a = _offset[s].X * length, b = _offset[s].Y * length, cOff = _offset[s].Z * length;
                edits.Add((_softIdx[s], new Vec3d(
                    on.X + t.X * a + u.X * b + w.X * cOff,
                    on.Y + t.Y * a + u.Y * b + w.Y * cOff,
                    on.Z + t.Z * a + u.Z * b + w.Z * cOff)));
            }
            return edits;
        }

        // --- baseline curve construction ---------------------------------------------------------------

        // Densely samples a centripetal Catmull-Rom (α = 0.5) through the baseline points, with mirrored
        // phantom endpoints for natural end tangents. Self-contained on purpose: the flow baseline has its
        // own phantom needs (mirrored, not the arch's vertical-feet phantoms) and must never depend on any
        // one shape's spline plumbing. Two points → the straight segment (exactly the pre-revision
        // behaviour for plain anchor drags). Near-coincident consecutive points are dropped defensively
        // (centripetal knot spacing divides by distance^α). Null when degenerate.
        private static Vec3d[] SampleBaselineCurve(Vec3d[] basePts)
        {
            // Dedupe near-coincident consecutive points.
            var clean = new List<Vec3d>(basePts.Length);
            foreach (Vec3d p in basePts)
                if (clean.Count == 0 || Dist2(clean[clean.Count - 1], p) > 1e-12)
                    clean.Add(p);
            if (clean.Count < 2) return null;
            if (clean.Count == 2) return new[] { clean[0], clean[1] };

            int n = clean.Count;
            var pts = new Vec3d[n + 2];
            for (int i = 0; i < n; i++) pts[i + 1] = clean[i];
            pts[0] = Mirror(clean[0], clean[1]);
            pts[n + 1] = Mirror(clean[n - 1], clean[n - 2]);

            int totalSamples = Math.Min(256, Math.Max(48, n * 24));
            int perSegment = Math.Max(4, totalSamples / (n - 1));

            var outPts = new List<Vec3d>(perSegment * (n - 1) + 1) { new Vec3d(clean[0].X, clean[0].Y, clean[0].Z) };
            for (int seg = 0; seg < n - 1; seg++)
            {
                Vec3d p0 = pts[seg], p1 = pts[seg + 1], p2 = pts[seg + 2], p3 = pts[seg + 3];
                for (int i = 1; i <= perSegment; i++)
                    outPts.Add(EvalCentripetal(p0, p1, p2, p3, (double)i / perSegment));
            }
            return outPts.ToArray();
        }

        private static Vec3d Mirror(Vec3d end, Vec3d neighbor) =>
            new Vec3d(2 * end.X - neighbor.X, 2 * end.Y - neighbor.Y, 2 * end.Z - neighbor.Z);

        // Standard centripetal Catmull-Rom segment evaluation (α = 0.5) between p1 and p2, local t ∈ [0,1].
        private static Vec3d EvalCentripetal(Vec3d p0, Vec3d p1, Vec3d p2, Vec3d p3, double t)
        {
            double t0 = 0;
            double t1 = t0 + Math.Pow(Math.Max(1e-9, Dist(p0, p1)), 0.5);
            double t2 = t1 + Math.Pow(Math.Max(1e-9, Dist(p1, p2)), 0.5);
            double t3 = t2 + Math.Pow(Math.Max(1e-9, Dist(p2, p3)), 0.5);
            double tt = t1 + (t2 - t1) * t;

            Vec3d a1 = Lerp2(p0, p1, t0, t1, tt);
            Vec3d a2 = Lerp2(p1, p2, t1, t2, tt);
            Vec3d a3 = Lerp2(p2, p3, t2, t3, tt);
            Vec3d b1 = Lerp2(a1, a2, t0, t2, tt);
            Vec3d b2 = Lerp2(a2, a3, t1, t3, tt);
            return Lerp2(b1, b2, t1, t2, tt);
        }

        private static Vec3d Lerp2(Vec3d a, Vec3d b, double ta, double tb, double t)
        {
            double f = tb - ta < 1e-12 ? 0 : (t - ta) / (tb - ta);
            return new Vec3d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
        }

        // --- baseline polyline math -----------------------------------------------------------------

        private static Vec3d[] BaselinePositions(IReadOnlyList<ControlPoint> pts, List<int> structural)
        {
            var poly = new Vec3d[structural.Count];
            for (int i = 0; i < structural.Count; i++)
            {
                Vec3d p = pts[structural[i]].WorldPosition;
                poly[i] = new Vec3d(p.X, p.Y, p.Z);
            }
            return poly;
        }

        private static double[] CumulativeLengths(Vec3d[] poly)
        {
            var cum = new double[poly.Length];
            for (int i = 1; i < poly.Length; i++)
                cum[i] = cum[i - 1] + Dist(poly[i - 1], poly[i]);
            return cum;
        }

        private static Vec3d PointAtStation(Vec3d[] poly, double[] cum, double station)
        {
            double target = station * cum[cum.Length - 1];
            for (int i = 1; i < poly.Length; i++)
            {
                if (target <= cum[i] || i == poly.Length - 1)
                {
                    double segLen = cum[i] - cum[i - 1];
                    double f = segLen < 1e-9 ? 0 : (target - cum[i - 1]) / segLen;
                    f = f < 0 ? 0 : f > 1 ? 1 : f;
                    return Lerp(poly[i - 1], poly[i], f);
                }
            }
            return new Vec3d(poly[0].X, poly[0].Y, poly[0].Z);
        }

        private static void NearestOnPolyline(Vec3d[] poly, double[] cum, Vec3d p,
            out double station, out Vec3d onPoly)
        {
            double bestD2 = double.MaxValue, bestLen = 0;
            Vec3d best = poly[0];
            for (int i = 1; i < poly.Length; i++)
            {
                Vec3d a = poly[i - 1], b = poly[i];
                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                double denom = ux * ux + uy * uy + uz * uz;
                double f = denom < 1e-12 ? 0
                    : ((p.X - a.X) * ux + (p.Y - a.Y) * uy + (p.Z - a.Z) * uz) / denom;
                f = f < 0 ? 0 : f > 1 ? 1 : f;
                var q = new Vec3d(a.X + ux * f, a.Y + uy * f, a.Z + uz * f);
                double d2 = Dist2(q, p);
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = q;
                    bestLen = cum[i - 1] + Math.Sqrt(denom) * f;
                }
            }
            station = cum[cum.Length - 1] < 1e-9 ? 0 : bestLen / cum[cum.Length - 1];
            onPoly = best;
        }

        private static Vec3d Lerp(Vec3d a, Vec3d b, double f) =>
            new Vec3d(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
        private static double Dist(Vec3d a, Vec3d b) => Math.Sqrt(Dist2(a, b));
        private static double Dist2(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
