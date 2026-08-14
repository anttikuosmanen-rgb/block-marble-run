using System.Collections.Generic;
using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Parts
{
    /// <summary>
    /// Builds collision geometry for studded bricks: the body, plus a stud on every cell that has one.
    ///
    /// A flat box lid would be simpler, but a marble that leaves the channel and lands on a brick
    /// should behave as it would on the real thing - riding the studs and settling into the shallow
    /// dimples between them, rather than sliding across a sheet of glass. Escaping the track is a
    /// normal part of the game, so what happens next is worth modelling.
    ///
    /// Generated rather than taken from the render mesh. The bricks are the heavy parts - up to 22k
    /// triangles against 2-5k for track - and almost all of that detail is invisible to a marble.
    /// A body box and one prism per stud is a few dozen triangles, cooked once per part type and
    /// shared by every instance, so a wall of bricks costs one cooking rather than thousands.
    /// </summary>
    public static class BrickColliderBuilder
    {
        /// <summary>Measured from the source geometry: 9.5 mm across on a 16 mm pitch, 4.6 mm tall.</summary>
        const float StudDiameterMm = 9.5f;
        const float StudHeightMm = 4.6f;

        /// <summary>Eight sides is enough for a marble to roll over smoothly; a box would catch on its corners.</summary>
        const int StudSides = 8;

        const float MmToUnits = 0.01f;

        static readonly Dictionary<PartDefinition, Mesh> Cache = new();

        public static Mesh For(PartDefinition def)
        {
            if (Cache.TryGetValue(def, out Mesh cached) && cached != null)
                return cached;

            Mesh mesh = Build(def);
            Cache[def] = mesh;
            return mesh;
        }

        static Mesh Build(PartDefinition def)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            Vector2Int size = def.footprintSize;
            float bodyTop = Mathf.Max(1, def.heightLayers) * GridCoord.LayerUnits;

            // Local space matches the render mesh: the footprint centre sits at the pivot offset.
            Vector2 pivot = def.pivotOffsetUnits;
            var origin = new Vector3(
                pivot.x - size.x * GridCoord.StudUnits * 0.5f,
                0f,
                pivot.y - size.y * GridCoord.StudUnits * 0.5f);

            AddBox(vertices, triangles,
                origin,
                origin + new Vector3(size.x * GridCoord.StudUnits, bodyTop, size.y * GridCoord.StudUnits));

            bool[] studs = def.topStuds;
            if (studs != null && studs.Length == size.x * size.y)
            {
                float radius = StudDiameterMm * MmToUnits * 0.5f;
                float height = StudHeightMm * MmToUnits;

                for (int y = 0; y < size.y; y++)
                for (int x = 0; x < size.x; x++)
                {
                    if (!studs[y * size.x + x])
                        continue;

                    var centre = origin + new Vector3(
                        (x + 0.5f) * GridCoord.StudUnits,
                        bodyTop,
                        (y + 0.5f) * GridCoord.StudUnits);

                    AddPrism(vertices, triangles, centre, radius, height);
                }
            }

            var mesh = new Mesh { name = $"{def.id}_collider" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddBox(List<Vector3> vertices, List<int> triangles, Vector3 min, Vector3 max)
        {
            int b = vertices.Count;

            vertices.Add(new Vector3(min.x, min.y, min.z));
            vertices.Add(new Vector3(max.x, min.y, min.z));
            vertices.Add(new Vector3(max.x, min.y, max.z));
            vertices.Add(new Vector3(min.x, min.y, max.z));
            vertices.Add(new Vector3(min.x, max.y, min.z));
            vertices.Add(new Vector3(max.x, max.y, min.z));
            vertices.Add(new Vector3(max.x, max.y, max.z));
            vertices.Add(new Vector3(min.x, max.y, max.z));

            int[] faces =
            {
                4, 6, 5, 4, 7, 6, // top
                0, 1, 2, 0, 2, 3, // bottom
                0, 5, 1, 0, 4, 5, // -Z
                3, 2, 6, 3, 6, 7, // +Z
                0, 3, 7, 0, 7, 4, // -X
                1, 5, 6, 1, 6, 2, // +X
            };

            foreach (int index in faces)
                triangles.Add(b + index);
        }

        /// <summary>An eight-sided prism standing on the body: near enough a round stud for a marble.</summary>
        static void AddPrism(List<Vector3> vertices, List<int> triangles, Vector3 baseCentre, float radius, float height)
        {
            int b = vertices.Count;

            for (int i = 0; i < StudSides; i++)
            {
                float angle = i / (float)StudSides * Mathf.PI * 2f;
                var offset = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                vertices.Add(baseCentre + offset);
                vertices.Add(baseCentre + offset + Vector3.up * height);
            }

            vertices.Add(baseCentre + Vector3.up * height); // top centre
            int top = vertices.Count - 1;

            for (int i = 0; i < StudSides; i++)
            {
                int a = b + i * 2;
                int next = b + (i + 1) % StudSides * 2;

                // side
                triangles.Add(a); triangles.Add(a + 1); triangles.Add(next);
                triangles.Add(next); triangles.Add(a + 1); triangles.Add(next + 1);

                // cap
                triangles.Add(a + 1); triangles.Add(top); triangles.Add(next + 1);
            }
        }
    }
}
