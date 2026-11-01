using System;
using System.Collections.Generic;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game.Components;
using VRage.Game.Models;
using VRage.Game.Voxels;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;

namespace ClientPlugin.Fixes;

// Raycasts against the terrain meshes of MyVoxelGeometry, behind MyVoxelBase.GetIntersectionWithLine (MyEntities raycasts,
// building, bullets) and the planet raycast of camera blocks. The game misses the terrain there in two ways:
// - Its cell walk (MyGridIntersection.Calculate) numbers the start cell by floor() of the start, but times the first boundary
//   for the cell the line moves into. The two differ when the line starts exactly on a cell boundary going down, and then the
//   whole walk is a cell off on that axis and never tests the cells the line passes.
// - It takes the line and the triangles to float storage space, in steps of 0.25 to 1 m on huge planets (storages of 2^23 and
//   2^24 voxels). Hits move by up to meters on slopes and normals scatter. The rounding also puts line starts on cell
//   boundaries, so the walk goes wrong for about 5% of rays on a 4000 km planet and 13% at the size of Earth.
// Here the walk numbers the start cell by the direction of the line, and every cell is tested in its packed space, where
// MyVoxelGeometry keeps the vertices relative to the cell origin. Only the line is moved there, in double.
internal static class VoxelRaycast
{
    private const int CellSize = MyVoxelConstants.GEOMETRY_CELL_SIZE_IN_VOXELS;

    // The game's early out: neighbouring cell meshes overlap by a voxel, so a cell entered up to the diagonal of 9 voxels
    // past the closest hit can still hold a closer one
    private const float CellReach = 15.588456f;

    [ThreadStatic]
    private static List<int> _candidates;

    // The closest hit so far, with the triangle to build the game's result from
    private struct Hit
    {
        public float Distance;
        public Vector3 Normal;
        public MyVoxelGeometry.CellData Cell;
        public MyVoxelTriangle Triangle;
    }

    // MyVoxelBase.GetIntersectionWithLine with the line in double up to the hit. The result is built like the game builds it,
    // in float storage space, then its world point and normals, which the game takes from rounded vertices, are replaced.
    public static bool IntersectWorld(MyVoxelBase voxel, ref LineD worldLine, IntersectionFlags flags, out MyIntersectionResultLineTriangleEx? t)
    {
        t = null;
        if (!voxel.PositionComp.WorldAABB.Intersects(ref worldLine, out _))
            return false;

        var corner = voxel.PositionLeftBottomCorner;
        var line = new LineD(worldLine.From - corner, worldLine.To - corner);
        if (!Intersect(voxel.Storage.GetGeometry(), ref line, flags, out var hit))
            return false;

        var localLine = new Line(line.From, line.To);
        var result = new MyIntersectionResultLineTriangleEx(hit, voxel, ref localLine)
            {
                IntersectionPointInWorldSpace = worldLine.From + line.Direction * hit.Distance
            };
        result.NormalInObjectSpace = result.NormalInWorldSpace = hit.InputTriangleNormal;
        t = result;
        return true;
    }

    // Replaces the planet raycast of MyCameraBlock.Raycast, MyVoxelGeometry.Intersect on the camera's float line, which goes
    // unused: the line is the camera's double ray, taken to storage space the way the camera takes it (WorldMatrixInvScaled
    // plus SizeInMetresHalf). The distance of the result is along that ray from its From, see CameraHit.
    public static bool IntersectCamera(MyVoxelGeometry geometry, ref Line localLine, out MyIntersectionResultLineTriangle result, IntersectionFlags flags, MyPlanet planet, ref LineD ray)
    {
        var matrix = planet.PositionComp.WorldMatrixInvScaled;
        Vector3D half = planet.SizeInMetresHalf;
        var line = new LineD(Vector3D.Transform(ray.From, matrix) + half, Vector3D.Transform(ray.To, matrix) + half);
        return Intersect(geometry, ref line, flags, out result);
    }

    // Replaces the camera's planet hit, which it computes from its float line: the point at the distance of IntersectCamera
    public static Vector3D CameraHit(ref LineD ray, ref MyIntersectionResultLineTriangle result) =>
        ray.From + ray.Direction * result.Distance;

    // Replaces MyVoxelGeometry.Intersect. The line is in storage space (voxels from the storage corner), the distance of the
    // result is along it from From. The triangle is in float storage space as the game returns it (GetPositionOnVoxel of
    // asteroids takes a vertex as a position), the normal is exact.
    public static bool Intersect(MyVoxelGeometry geometry, ref LineD line, IntersectionFlags flags, out MyIntersectionResultLineTriangle result)
    {
        result = default;
        var cells = geometry.CellsCount;
        var delta = line.To - line.From;
        if (!Clip(line.From, delta, cells * CellSize, out var t0, out var t1))
            return false;

        // The walk (Amanatides and Woo), t running from 0 to 1 along the line: per axis the cell, its step, the t of the next
        // boundary and the t from one boundary to the next
        Vector3I cell = default, step = default;
        Vector3D next = default, each = default;
        for (var axis = 0; axis < 3; axis++)
        {
            var start = (line.From.GetDim(axis) + delta.GetDim(axis) * t0) / CellSize;
            var speed = delta.GetDim(axis) / CellSize;

            // The clipped start can round past a storage face
            cell[axis] = MathHelper.Clamp(StartCell(start, speed), 0, cells[axis] - 1);
            step[axis] = Math.Sign(speed);
            next.SetDim(axis, speed > 0 ? t0 + (cell[axis] + 1 - start) / speed
                : speed < 0 ? t0 + (cell[axis] - start) / speed
                : double.PositiveInfinity);
            each.SetDim(axis, speed != 0 ? 1 / Math.Abs(speed) : double.PositiveInfinity);
        }

        var length = delta.Length();
        var hit = new Hit { Distance = float.PositiveInfinity };
        var enter = t0;
        while (enter * length <= hit.Distance + CellReach)
        {
            var coord = new MyCellCoord(0, cell);
            var data = geometry.GetCell(ref coord);
            if (data is { VoxelTrianglesCount: > 0 })
                TestCell(data, ref line, flags, ref hit);

            // Into the cell whose boundary the line crosses first, until the line ends or leaves the storage
            var axis = next.X < next.Y ? (next.X < next.Z ? 0 : 2) : (next.Y < next.Z ? 1 : 2);
            enter = next.GetDim(axis);
            cell[axis] += step[axis];
            if (enter > t1 || cell[axis] < 0 || cell[axis] >= cells[axis])
                break;

            next.SetDim(axis, enter + each.GetDim(axis));
        }

        if (hit.Cell == null)
            return false;

        MyTriangle_Vertices triangle;
        hit.Cell.GetUnpackedPosition(hit.Triangle.V0, out triangle.Vertex0);
        hit.Cell.GetUnpackedPosition(hit.Triangle.V1, out triangle.Vertex1);
        hit.Cell.GetUnpackedPosition(hit.Triangle.V2, out triangle.Vertex2);
        result = new MyIntersectionResultLineTriangle(0, ref triangle, ref hit.Normal, hit.Distance);
        return true;
    }

    // The cell the line is in right after its start, floor() of a point a hair further along. Only going down from a cell
    // boundary does that differ from floor() of the start: it is the cell below.
    private static int StartCell(double start, double speed) =>
        speed >= 0 ? (int)Math.Floor(start) : (int)Math.Ceiling(start) - 1;

    // One cell in its packed space: the vertices relative to the cell origin (0 to ~11 voxels) and the origin, a whole number
    // of voxels below 2^24 that float holds exactly. Only the line is converted, in double, then to small floats.
    private static void TestCell(MyVoxelGeometry.CellData cell, ref LineD line, IntersectionFlags flags, ref Hit hit)
    {
        Vector3D origin = cell.m_positionOffset;
        var scale = cell.m_positionScale.X;
        var from = (Vector3)((line.From - origin) / scale);
        var to = (Vector3)((line.To - origin) / scale);

        var triangles = _candidates ??= [];
        triangles.Clear();
        var ray = new Ray(from, to - from);
        cell.Octree.GetIntersectionWithLine(ref ray, triangles);

        var packed = new Line(from, to, false);
        foreach (var index in triangles)
        {
            var triangle = cell.VoxelTriangles[index];
            MyTriangle_Vertices vertices;
            vertices.Vertex0 = cell.m_positions[triangle.V0];
            vertices.Vertex1 = cell.m_positions[triangle.V1];
            vertices.Vertex2 = cell.m_positions[triangle.V2];

            // Back faces only when asked for, like the game
            var normal = MyUtils.GetNormalVectorFromTriangle(ref vertices);
            // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
            if (!normal.IsValid() || ((flags & IntersectionFlags.FLIPPED_TRIANGLES) == 0 && Vector3.Dot(packed.Direction, normal) > 0))
                continue;

            if (MyUtils.GetLineTriangleIntersection(ref packed, ref vertices) is not { } distance || distance * scale >= hit.Distance)
                continue;

            hit = new Hit { Distance = distance * scale, Normal = normal, Cell = cell, Triangle = triangle };
        }
    }

    // The part of From + delta * t, t from 0 to 1, within [0, max) on every axis. False when there is none, also for NaN.
    private static bool Clip(Vector3D from, Vector3D delta, Vector3I max, out double t0, out double t1)
    {
        t0 = 0;
        t1 = 1;
        for (var axis = 0; axis < 3; axis++)
        {
            var p = from.GetDim(axis);
            var d = delta.GetDim(axis);
            if (d == 0)
            {
                if (p < 0 || p >= max[axis])
                    return false;

                continue;
            }

            var a = -p / d;
            var b = (max[axis] - p) / d;
            t0 = Math.Max(t0, Math.Min(a, b));
            t1 = Math.Min(t1, Math.Max(a, b));
            if (!(t0 < t1))
                return false;
        }

        return true;
    }
}
