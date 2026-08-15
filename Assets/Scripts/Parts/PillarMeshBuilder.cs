using System.Collections.Generic;
using UnityEngine;

namespace BlockMarbleRun.Parts
{
    /// <summary>
    /// Cuts a support column of any height from one modelled pillar.
    ///
    /// The mesh has three parts: a base, a plain round shaft, and a top with the studs on it. Only
    /// the shaft is stretched, so the ends keep the shapes that make them fit - which is the whole
    /// reason for doing this rather than scaling the part, since scaling a pillar to twice the height
    /// gives it studs twice as tall that no longer fit an anti-stud.
    ///
    /// The stretch is a continuous piecewise map of z, which means no triangle has to be cut: one
    /// spanning a boundary simply has its vertices moved by different amounts and stays a triangle.
    /// Normals survive untouched because the only stretched region is a vertical cylinder, whose
    /// normals are horizontal and so unchanged by a change of height.
    /// </summary>
    public static class PillarMeshBuilder
    {
        /// <summary>
        /// Finds the plain shaft: the longest run of the mesh whose cross-section does not change.
        ///
        /// Measured rather than written down. Hardcoding the two heights would work for the pillar in
        /// front of me and break silently the first time the model is edited - and the failure would
        /// be a stretched stud rather than an error.
        /// </summary>
        public static bool FindShaft(Mesh mesh, out float from, out float to) =>
            FindVerticalBand(mesh, out from, out to);

        /// <summary>
        /// The tallest run of the mesh that is purely vertical, and so can be made longer or shorter
        /// without distorting anything.
        ///
        /// Tested by the facing of each triangle rather than by its shape: a wall, a tube and a round
        /// shaft are all vertical, and stretching any of them along their own axis moves geometry
        /// without changing it. A chamfer or a stud cap is not, and stretching one is immediately
        /// visible.
        ///
        /// Normals are recomputed from the corners rather than read from the mesh, because the ones
        /// stored there have been smoothed across the seams - a wall next to a fillet ends up with a
        /// normal tilted off the horizontal by the fillet it sits beside, and would be judged as not
        /// vertical when it is.
        /// </summary>
        public static bool FindVerticalBand(Mesh mesh, out float from, out float to)
        {
            from = to = 0f;

            if (mesh == null || !mesh.isReadable)
                return false;

            Vector3[] v = mesh.vertices;
            int[] tris = mesh.triangles;

            if (v.Length == 0 || tris.Length == 0)
                return false;

            Bounds bounds = mesh.bounds;

            const float slice = 0.001f;    // 0.1 mm
            int steps = Mathf.Max(1, Mathf.CeilToInt(bounds.size.y / slice));

            var vertical = new bool[steps];
            var present = new bool[steps];

            for (int i = 0; i < steps; i++)
                vertical[i] = true;

            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 a = v[tris[i]], b = v[tris[i + 1]], c = v[tris[i + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a);

                if (normal.sqrMagnitude < 1e-16f)
                    continue;   // no area, so no facing to speak of

                bool flat = Mathf.Abs(normal.normalized.y) > 0.02f;

                float lo = Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - bounds.min.y;
                float hi = Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - bounds.min.y;

                int first = Mathf.Clamp(Mathf.FloorToInt(lo / slice), 0, steps - 1);
                int last = Mathf.Clamp(Mathf.FloorToInt(hi / slice), 0, steps - 1);

                for (int k = first; k <= last; k++)
                {
                    present[k] = true;

                    if (flat)
                        vertical[k] = false;
                }
            }

            int best = 0, bestStart = -1, run = 0, runStart = -1;

            for (int k = 0; k < steps; k++)
            {
                if (present[k] && vertical[k])
                {
                    if (run == 0)
                        runStart = k;

                    run++;

                    if (run > best)
                    {
                        best = run;
                        bestStart = runStart;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            if (bestStart < 0)
                return false;

            from = bounds.min.y + bestStart * slice;
            to = bounds.min.y + (bestStart + best) * slice;

            return to - from > bounds.size.y * 0.1f;
        }

        /// <summary>
        /// The source mesh with its shaft lengthened or shortened by <paramref name="deltaUnits"/>.
        /// Null when the shaft is too short to absorb the change.
        /// </summary>
        public static Mesh Stretch(Mesh source, float from, float to, float deltaUnits, string name)
        {
            float shaft = to - from;
            if (source == null || !source.isReadable || shaft + deltaUnits <= 0f)
                return null;

            float scale = (shaft + deltaUnits) / shaft;

            Vector3[] vertices = source.vertices;
            var moved = new Vector3[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];

                if (v.y <= from)
                    moved[i] = v;                                        // base, untouched
                else if (v.y >= to)
                    moved[i] = new Vector3(v.x, v.y + deltaUnits, v.z);  // top, carried up bodily
                else
                    moved[i] = new Vector3(v.x, from + (v.y - from) * scale, v.z);
            }

            var mesh = new Mesh
            {
                name = name,
                indexFormat = source.indexFormat,
            };

            mesh.SetVertices(moved);
            mesh.SetNormals(source.normals);
            mesh.SetTriangles(source.triangles, 0);
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
