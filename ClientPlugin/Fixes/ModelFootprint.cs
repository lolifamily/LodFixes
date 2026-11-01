using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Tools;
using Sandbox.Definitions;
using Sandbox.Game.WorldEnvironment;
using Sandbox.Game.WorldEnvironment.Definitions;
using Sandbox.Game.WorldEnvironment.Modules;
using VRage.Game;
using VRage.Game.Models;
using VRage.Library.Utils;
using VRageMath;

namespace ClientPlugin.Fixes;

// Where a model meets the ground and how far it may move up or down there, measured once per session from its file (see
// ModelFootprints). The game puts the pivot of a model on the terrain, so the plane through the pivot is the ground the
// model was made for:
// - Feet: per direction around the model, its points that can be the first of that direction to reach a plane, however
//   the plane slopes. On a slope the model goes down until every direction stands on the ground as it does on flat
//   ground: a tree barely moves, its trunk reaches deep, a bush only as far as its drooping branches need on the downhill
//   side, not as far as the ground drops under its outermost leaves. Where the ground is bumpy another point may reach it
//   first, the model then sits deeper than it needs to, never above the ground.
// - Capacity: how far it may rise before some direction loses contact, or sink before geometry outside the outline
//   reaches the ground (low branches, leaves), whichever is less. Up to 2 m on trees, about nothing on bushes and
//   forageables, whose leaves lie on the ground.
// A function of the file alone, in basic double operations and sqrt over the triangles in file order: the same bits in
// every session and on every machine.
internal sealed class ModelFootprint
{
    // Nothing measured: a foot at the pivot, which goes on the ground
    private static readonly ModelFootprint None = new(0, [[new Foot(0, 0, 0)]], 0);

    // Resolution of the feet and the capacity: triangles are sampled every SampleStep metres (at most MaxSamples along an
    // edge), in 16 directions around the outline (see Sector) and rings SampleStep wide (see Directions)
    private const double SampleStep = 0.05;
    private const int MaxSamples = 48;
    private const int Sectors = 16;

    private static readonly double Tan22 = Math.Sqrt(2) - 1;

    // Height of the plane the model meets the ground at, in model space: 0, or its bottom if it floats above its pivot
    public readonly double ReferenceHeight;

    // Per direction around the model that has any (see Sector)
    public readonly Foot[][] Feet;

    // The highest the reference plane can be over flat ground with every direction still standing on it: the shallowest
    // of the deepest feet of the directions, below 0 where a direction only reaches down to above the plane
    public readonly double Embed;

    public readonly double Capacity;

    private ModelFootprint(double referenceHeight, Foot[][] feet, double capacity)
    {
        ReferenceHeight = referenceHeight;
        Feet = feet;
        Embed = feet.Min(direction => direction.Max(foot => foot.Depth));
        Capacity = capacity;
    }

    // Footprint of the model MyStaticEnvironmentModule.ProcessItems shows for this item. Boulders and bot spawns have no
    // model here and keep None, so does everything outside a session.
    public static ModelFootprint Of(IMyEnvironmentOwner environment, ItemInfo item, int index)
    {
        var footprints = ModelFootprints.Static;
        if (footprints == null || item.DefinitionIndex < 0)
            return None;

        environment.GetDefinition((ushort)item.DefinitionIndex, out var definition);
        var collection = CollectionOf(definition);
        if (collection == null)
            return None;

        // Picked with the module's seed, the item index
        var id = collection.Items.Sample(MyHashRandomUtils.UniformFloatFromSeed(index));
        var model = MyDefinitionManager.Static.GetDefinition<MyPhysicalModelDefinition>(id)?.Model;
        return model == null ? None : footprints.Get(model);
    }

    // Every model the items of an environment can show
    public static IEnumerable<string> ModelsOf(MyWorldEnvironmentDefinition environment) =>
        environment.Items
            .Select(CollectionOf)
            .Where(collection => collection != null)
            .SelectMany(collection => collection.Items)
            .Select(id => MyDefinitionManager.Static.GetDefinition<MyPhysicalModelDefinition>(id)?.Model)
            .Where(model => model != null)
            .Distinct();

    // Models of an item the static module shows (trees, bushes, forageables)
    private static MyPhysicalModelCollectionDefinition CollectionOf(MyRuntimeEnvironmentItemInfo definition) =>
        definition?.Type?.StorageModule.Type == typeof(MyStaticEnvironmentModule)
            ? MyDefinitionManager.Static.GetDefinition<MyPhysicalModelCollectionDefinition>(new MyDefinitionId(typeof(MyObjectBuilder_PhysicalModelCollectionDefinition), definition.Subtype))
            : null;

    // Measures the model file, the error model the game shows for a missing one included (definitions already point to
    // it). A file the importer cannot read gives None: the game fails on it only once it needs that model, and reads LOD1
    // for its data where this reads LOD0, so failing here could break worlds the game runs.
    public static ModelFootprint Measure(string model)
    {
        try
        {
            // A copy of its own, of the full detail mesh: the shared MyModels copy holds LOD1 or LOD0 depending on who
            // loaded it first, and blocks rescale shared vertices in place
            var mesh = new MyModel(model);
            mesh.LoadData(forceFullDetail: true);
            try
            {
                return Measure(Triangles(mesh));
            }
            finally
            {
                mesh.UnloadData();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"Could not measure the model {model}, its items stay where the game puts them: {e.Message}");
            return None;
        }
    }

    private static ModelFootprint Measure(List<Triangle> triangles)
    {
        if (triangles.Count == 0)
            return None;

        // Entirely above its pivot: it meets the ground with its lowest points and has nothing to spare
        var lowest = LowestPoints(triangles, out var bottom);
        if (bottom >= 0)
        {
            var directions = new Directions(Center(lowest));
            foreach (var p in lowest)
                directions.Add(p.X, p.Y, 0);

            return new ModelFootprint(bottom, directions.Feet(), 0);
        }

        // Entirely below its pivot, buried on purpose: left where the game puts it
        if (triangles.Max(t => t.Top) <= 0)
            return None;

        var cut = Cut(triangles);
        if (cut.Count == 0)
            return None;

        // What reaches below the plane, by direction around the center of the outline
        var center = Center(cut);
        var below = new Directions(center);
        foreach (var triangle in triangles.Where(t => t.Bottom < 0))
            Sample(triangle, p => below.Add(p.X, p.Z, -p.Y));

        var radius = cut.Max(p => (p - center).Length());
        return new ModelFootprint(0, below.Feet(), CapacityOf(triangles, center, radius, below.Lift));
    }

    // The lift, or how far the model may sink before geometry above the plane outside the outline reaches the ground,
    // whichever is less. 0 where some direction has nothing below the plane, as the leaves of bushes and forageables.
    private static double CapacityOf(List<Triangle> triangles, Vector2D center, double radius, double lift)
    {
        if (!(lift > 0))
            return 0;

        // From the lowest triangles up, until none can be lower than what was found
        var clearance = double.PositiveInfinity;
        foreach (var triangle in triangles.Where(t => t.Top > 0).OrderBy(t => t.Bottom))
        {
            if (triangle.Bottom >= Math.Min(clearance, lift))
                break;

            Sample(triangle, p =>
            {
                if (p.Y > 0 && (new Vector2D(p.X, p.Z) - center).Length() > radius)
                    clearance = Math.Min(clearance, p.Y);
            });
        }

        return Math.Min(lift, clearance);
    }

    // One of 16 sectors of 22.5 degrees around the outline, from comparisons alone (atan2 may differ in its last bit
    // between runtimes): the octant from the signs and the longer axis, then its half by tan(22.5) = sqrt(2) - 1
    private static int Sector(double dx, double dz)
    {
        double ax = Math.Abs(dx), az = Math.Abs(dz);
        var octant = (dx < 0 ? 4 : 0) + (dz < 0 ? 2 : 0) + (az > ax ? 1 : 0);
        return octant * 2 + (Math.Min(ax, az) > Tan22 * Math.Max(ax, az) ? 1 : 0);
    }

    // Points of the triangle at most SampleStep apart along its edges
    private static void Sample(Triangle t, Action<Vector3D> visit)
    {
        var edge = Math.Max((t.B - t.A).Length(), Math.Max((t.C - t.A).Length(), (t.C - t.B).Length()));
        var n = Math.Max(1, Math.Min(MaxSamples, (int)Math.Ceiling(edge / SampleStep)));
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= n - i; j++)
                visit(t.A + (t.B - t.A) * (i / (double)n) + (t.C - t.A) * (j / (double)n));
        }
    }

    // Where the triangles reaching below the plane y = 0 cross it, seen from just below: geometry lying in the plane counts
    // only with a vertex strictly below it, so no epsilon is needed
    private static List<Vector2D> Cut(List<Triangle> triangles)
    {
        var points = new List<Vector2D>();
        foreach (var t in triangles.Where(t => t.Bottom < 0))
        {
            Crossing(t.A, t.B, points);
            Crossing(t.B, t.C, points);
            Crossing(t.C, t.A, points);
        }

        return points;
    }

    // The point at y = 0 of an edge from strictly below the plane to it or above
    private static void Crossing(Vector3D p, Vector3D q, List<Vector2D> points)
    {
        if (p.Y < 0 == q.Y < 0)
            return;

        if (q.Y < 0)
            (p, q) = (q, p);

        var f = -p.Y / (q.Y - p.Y);
        points.Add(new Vector2D(p.X + (q.X - p.X) * f, p.Z + (q.Z - p.Z) * f));
    }

    // The lowest vertices in file order and their height: a lower vertex starts the list anew, one as low joins it
    private static List<Vector2D> LowestPoints(List<Triangle> triangles, out double bottom)
    {
        var points = new List<Vector2D>();
        bottom = double.PositiveInfinity;
        foreach (var v in triangles.SelectMany(t => new[] { t.A, t.B, t.C }))
        {
            if (v.Y > bottom)
                continue;

            if (v.Y < bottom)
            {
                bottom = v.Y;
                points.Clear();
            }

            points.Add(new Vector2D(v.X, v.Z));
        }

        return points;
    }

    // Center of the bounding box of the points
    private static Vector2D Center(List<Vector2D> points) =>
        new((points.Min(p => p.X) + points.Max(p => p.X)) / 2, (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);

    private static double Cross(Vector2D o, Vector2D a, Vector2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static List<Triangle> Triangles(MyModel mesh)
    {
        var triangles = new List<Triangle>(mesh.GetTrianglesCount());
        for (var i = 0; i < mesh.GetTrianglesCount(); i++)
        {
            var indices = mesh.Triangles[i];
            var triangle = new Triangle(mesh.GetVertex(indices.I0), mesh.GetVertex(indices.I1), mesh.GetVertex(indices.I2));
            if (IsFinite(triangle.A) && IsFinite(triangle.B) && IsFinite(triangle.C))
                triangles.Add(triangle);
        }

        return triangles;
    }

    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    // A point of the model, (x, z) in model space and how far below the reference plane it is (below 0 above it)
    public readonly struct Foot(double x, double z, double depth)
    {
        public readonly double X = x, Z = z, Depth = depth;
    }

    // Points of a model by direction around a center (see Sector), each direction in rings SampleStep wide that keep their
    // deepest point: any other point of a ring could reach the ground first by at most the slope times the ring width
    private sealed class Directions(Vector2D center)
    {
        // Per sector: ring index -> deepest point
        private readonly Dictionary<int, Foot>[] sectors = Enumerable.Range(0, Sectors).Select(_ => new Dictionary<int, Foot>()).ToArray();

        // The shallowest of the deepest points of the directions, -infinity where one has none: how far the model may rise
        // before some direction loses contact
        public double Lift => sectors.Min(rings => rings.Count == 0 ? double.NegativeInfinity : rings.Values.Max(foot => foot.Depth));

        public void Add(double x, double z, double depth)
        {
            var rings = sectors[Sector(x - center.X, z - center.Y)];
            var ring = (int)(Distance(x, z) / SampleStep);
            if (!rings.TryGetValue(ring, out var deepest) || depth > deepest.Depth)
                rings[ring] = new Foot(x, z, depth);
        }

        // Of each direction that has points, the upper convex hull of their (distance from the center, depth), Andrew's
        // monotone chain over the rings from the center out: the points that can be the first of the direction to reach a
        // plane, however it slopes
        public Foot[][] Feet() => sectors.Where(rings => rings.Count > 0).Select(UpperHull).ToArray();

        private Foot[] UpperHull(Dictionary<int, Foot> rings)
        {
            var hull = new Foot[rings.Count];
            var at = new Vector2D[rings.Count];
            var count = 0;
            foreach (var foot in rings.OrderBy(ring => ring.Key).Select(ring => ring.Value))
            {
                var point = new Vector2D(Distance(foot.X, foot.Z), foot.Depth);
                while (count >= 2 && Cross(at[count - 2], at[count - 1], point) >= 0)
                    count--;

                at[count] = point;
                hull[count++] = foot;
            }

            return hull.Take(count).ToArray();
        }

        private double Distance(double x, double z)
        {
            double dx = x - center.X, dz = z - center.Y;
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }

    private readonly struct Triangle(Vector3D a, Vector3D b, Vector3D c)
    {
        public readonly Vector3D A = a, B = b, C = c;
        public readonly double Bottom = Math.Min(a.Y, Math.Min(b.Y, c.Y));
        public readonly double Top = Math.Max(a.Y, Math.Max(b.Y, c.Y));
    }
}
