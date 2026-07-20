using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Shapes
{
    /// <summary>
    /// The single construction point for shapes (Session 8) — every `new ArchShape(...)` call site in
    /// GuideManager and GuideRenderer routes through here now that there is more than one primitive, so
    /// adding a shape touches exactly this file plus the shape itself. Unknown/legacy type values fall
    /// back to the arch (forward-compatible with saves from newer versions, per the pinned-enum rule).
    /// </summary>
    public static class ShapeFactory
    {
        /// <summary>
        /// Builds a brand-new shape from the two draft clicks. <paramref name="inverted"/> (Session 11:
        /// SHIFT at placement) mirrors the born apex/arc below the base for the shapes that have an "up"
        /// (the arch family and triangles); symmetric shapes ignore it. <paramref name="sides"/> is the
        /// polygon's side count; every other shape ignores it. The Free-Shape is the one multi-click
        /// primitive: pass its full corner chain via <paramref name="chain"/> (+ <paramref name="closed"/>);
        /// with no chain it degrades to a two-corner open polyline from start/end.
        /// </summary>
        public static IGuideShape Create(
            GuideShapeType type, ShapeConstraint constraint, PlaneAxis shapePlaneAxis, Vec3d start, Vec3d end,
            bool inverted = false, int sides = 0,
            IReadOnlyList<Vec3d> chain = null, bool closed = false, bool flatSideAligned = false)
        {
            switch (type)
            {
                case GuideShapeType.Ellipse:
                    return new EllipseShape(start, end, shapePlaneAxis, constraint);
                case GuideShapeType.Line:
                    return new LineShape(start, end);
                case GuideShapeType.Triangle:
                    return new TriangleShape(start, end, shapePlaneAxis, constraint, inverted);
                case GuideShapeType.Rectangle:
                    return new RectangleShape(start, end, shapePlaneAxis, constraint);
                case GuideShapeType.Polygon:
                    return new PolygonShape(start, end, shapePlaneAxis, sides, flatSideAligned);
                case GuideShapeType.FreeShape:
                    return new FreeShape(chain != null && chain.Count >= 2 ? chain : new[] { start, end },
                        closed && chain != null && chain.Count >= 3);
                case GuideShapeType.Sphere:
                    return new SphereShape(start, end);
                case GuideShapeType.Dome:
                    return new DomeShape(start, end, shapePlaneAxis, inverted);
                case GuideShapeType.Cylinder:
                    return new CylinderShape(start, end, shapePlaneAxis, inverted);
                case GuideShapeType.TaperedCylinder:
                    return new TaperedCylinderShape(start, end, shapePlaneAxis, inverted);
                case GuideShapeType.PolygonalPrism:
                    return new PolygonalPrismShape(start, end, shapePlaneAxis, sides,
                        tapered: false, inverted: inverted, flatSideAligned: flatSideAligned);
                case GuideShapeType.TaperedPolygonalPrism:
                    return new PolygonalPrismShape(start, end, shapePlaneAxis, sides,
                        tapered: true, inverted: inverted, flatSideAligned: flatSideAligned);
                case GuideShapeType.Cone:
                    return new ConeShape(start, end, shapePlaneAxis, inverted);
                case GuideShapeType.Box:
                    return new BoxShape(start, end, shapePlaneAxis, inverted);
                default:
                    return new ArchShape(start, end, constraint: constraint, inverted: inverted);
            }
        }

        /// <summary>
        /// Rebuilds a shape as a behavioural view over an EXISTING list (load / restore / mirror path).
        /// The list is adopted by reference — the GuideData↔shape shared-list binding, unchanged.
        /// </summary>
        public static IGuideShape Adopt(GuideData g)
        {
            switch (g.ShapeType)
            {
                case GuideShapeType.Ellipse:
                    return new EllipseShape(g.ControlPoints, g.ShapePlaneAxis, g.Constraint);
                case GuideShapeType.Line:
                    return new LineShape(g.ControlPoints);
                case GuideShapeType.Triangle:
                    return new TriangleShape(g.ControlPoints, g.ShapePlaneAxis, g.Constraint);
                case GuideShapeType.Rectangle:
                    return new RectangleShape(g.ControlPoints, g.ShapePlaneAxis, g.Constraint);
                case GuideShapeType.Polygon:
                    return new PolygonShape(g.ControlPoints, g.ShapePlaneAxis, g.Sides, g.FlatSideAligned);
                case GuideShapeType.FreeShape:
                    return new FreeShape(g.ControlPoints, g.IsClosed);
                case GuideShapeType.Sphere:
                    return new SphereShape(g.ControlPoints);
                case GuideShapeType.Dome:
                    return new DomeShape(g.ControlPoints, g.ShapePlaneAxis);
                case GuideShapeType.Cylinder:
                    return new CylinderShape(g.ControlPoints, g.ShapePlaneAxis);
                case GuideShapeType.TaperedCylinder:
                    return new TaperedCylinderShape(g.ControlPoints, g.ShapePlaneAxis);
                case GuideShapeType.PolygonalPrism:
                    return new PolygonalPrismShape(g.ControlPoints, g.ShapePlaneAxis, g.Sides,
                        tapered: false, flatSideAligned: g.FlatSideAligned);
                case GuideShapeType.TaperedPolygonalPrism:
                    return new PolygonalPrismShape(g.ControlPoints, g.ShapePlaneAxis, g.Sides,
                        tapered: true, flatSideAligned: g.FlatSideAligned);
                case GuideShapeType.Cone:
                    return new ConeShape(g.ControlPoints, g.ShapePlaneAxis);
                case GuideShapeType.Box:
                    return new BoxShape(g.ControlPoints, g.ShapePlaneAxis);
                default:
                    return new ArchShape(g.ControlPoints, constraint: g.Constraint);
            }
        }

        /// <summary>Adopt for transient lists that have no GuideData (the renderer's draft ghost).</summary>
        public static IGuideShape Adopt(
            GuideShapeType type, ShapeConstraint constraint, PlaneAxis shapePlaneAxis, List<ControlPoint> points,
            int sides = 0, bool closed = false, bool flatSideAligned = false)
        {
            switch (type)
            {
                case GuideShapeType.Ellipse:
                    return new EllipseShape(points, shapePlaneAxis, constraint);
                case GuideShapeType.Line:
                    return new LineShape(points);
                case GuideShapeType.Triangle:
                    return new TriangleShape(points, shapePlaneAxis, constraint);
                case GuideShapeType.Rectangle:
                    return new RectangleShape(points, shapePlaneAxis, constraint);
                case GuideShapeType.Polygon:
                    return new PolygonShape(points, shapePlaneAxis, sides, flatSideAligned);
                case GuideShapeType.FreeShape:
                    return new FreeShape(points, closed);
                case GuideShapeType.Sphere:
                    return new SphereShape(points);
                case GuideShapeType.Dome:
                    return new DomeShape(points, shapePlaneAxis);
                case GuideShapeType.Cylinder:
                    return new CylinderShape(points, shapePlaneAxis);
                case GuideShapeType.TaperedCylinder:
                    return new TaperedCylinderShape(points, shapePlaneAxis);
                case GuideShapeType.PolygonalPrism:
                    return new PolygonalPrismShape(points, shapePlaneAxis, sides,
                        tapered: false, flatSideAligned: flatSideAligned);
                case GuideShapeType.TaperedPolygonalPrism:
                    return new PolygonalPrismShape(points, shapePlaneAxis, sides,
                        tapered: true, flatSideAligned: flatSideAligned);
                case GuideShapeType.Cone:
                    return new ConeShape(points, shapePlaneAxis);
                case GuideShapeType.Box:
                    return new BoxShape(points, shapePlaneAxis);
                default:
                    return new ArchShape(points, constraint: constraint);
            }
        }
    }
}
