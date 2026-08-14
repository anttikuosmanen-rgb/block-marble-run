#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Turns raw STL facets into a Unity mesh: CAD axes to Unity axes, millimetres to world units,
    /// and - the part that actually matters visually - welding the triangle soup back into shared
    /// vertices so curved surfaces shade smoothly instead of faceted.
    /// </summary>
    public static class StlMeshBuilder
    {
        /// <summary>
        /// STL is right-handed Z-up (CAD); Unity is left-handed Y-up. Swapping Y and Z performs the
        /// handedness change, but because that swap reverses orientation it also inverts triangle
        /// winding - hence the majority-vote correction in <see cref="Build"/>.
        /// </summary>
        static Vector3 CadToUnity(Vector3 v, float scale) => new Vector3(v.x, v.z, v.y) * scale;

        public static Mesh Build(List<StlFacet> facets, float scale, float smoothingAngleDeg, string name)
        {
            facets = MakeOrientationConsistent(facets);

            // Area-weighted, and deliberately un-normalised. Two reasons, both learned the hard way:
            //
            // Vector3.normalized silently returns zero below a magnitude of 1e-5, and a triangle's
            // cross product is twice its area - so every sliver on a stud or tube normalised to zero,
            // scored Dot == 0, and fell on the "agrees" side of the test. Parts with dense stud
            // tessellation then outvoted themselves and imported inside out, while coarser parts
            // survived. building_block_2x6 decided it by 716 votes out of 16852.
            //
            // Weighting by area also means large, numerically trustworthy faces dominate, instead of
            // a thousand slivers with poor float precision counting as much as the walls they sit on.
            double windingScore = 0.0;

            // Transform first, then decide winding from the transformed data so the vote is made in
            // the same space the mesh will live in.
            var transformed = new List<StlFacet>(facets.Count);
            foreach (StlFacet f in facets)
            {
                var t = new StlFacet
                {
                    A = CadToUnity(f.A, scale),
                    B = CadToUnity(f.B, scale),
                    C = CadToUnity(f.C, scale),
                    Normal = new Vector3(f.Normal.x, f.Normal.z, f.Normal.y),
                };
                transformed.Add(t);

                // Many exporters write zero or garbage facet normals, so only vote on usable ones.
                if (t.Normal.sqrMagnitude > 1e-12f)
                {
                    Vector3 geometric = Vector3.Cross(t.B - t.A, t.C - t.A);
                    windingScore += Vector3.Dot(geometric, t.Normal.normalized);
                }
            }

            // A global flip rather than a per-triangle one: a mesh where some faces were flipped and
            // others were not would be worse than either consistent choice, and disagreement here
            // means the file's normals are unreliable, not that the model is genuinely mixed.
            bool flipWinding = windingScore < 0.0;

            var welder = new VertexWelder(smoothingAngleDeg);
            var indices = new List<int>(facets.Count * 3);

            foreach (StlFacet f in transformed)
            {
                Vector3 a = f.A;
                Vector3 b = flipWinding ? f.C : f.B;
                Vector3 c = flipWinding ? f.B : f.C;

                Vector3 faceNormal = Vector3.Cross(b - a, c - a);
                if (faceNormal.sqrMagnitude <= 1e-20f)
                    continue; // degenerate sliver; contributes nothing and would poison the normal average
                faceNormal.Normalize();

                indices.Add(welder.Add(a, faceNormal));
                indices.Add(welder.Add(b, faceNormal));
                indices.Add(welder.Add(c, faceNormal));
            }

            var mesh = new Mesh { name = name };

            // building_block_2x10 alone is 22k triangles; unwelded that is far past the 16-bit limit,
            // and welding is not guaranteed to bring every future part back under it.
            if (welder.Count > 65534)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(welder.Positions);
            mesh.SetNormals(welder.BuildNormals());
            mesh.SetTriangles(indices, 0);
            // No UVs: parts are flat-coloured, and STL carries no texture coordinates to preserve.
            // No tangents either - they would be meaningless without UVs, and nothing here is
            // normal-mapped.
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>
        /// Makes every triangle agree with its neighbours about which side is out.
        ///
        /// STL stores triangles independently, so nothing stops an exporter emitting a few with the
        /// opposite winding. Those render as holes - looking straight through the surface into the
        /// inside of the part - and slide_2x4 ships with 66 of them.
        ///
        /// Works by walking the mesh through shared edges: two triangles that agree traverse their
        /// shared edge in opposite directions, so any neighbour that traverses it the same way is
        /// flipped. This only makes the mesh self-consistent; which way "out" is remains the winding
        /// vote's decision.
        /// </summary>
        static List<StlFacet> MakeOrientationConsistent(List<StlFacet> facets)
        {
            var byEdge = new Dictionary<(Vector3Int, Vector3Int), List<int>>();

            for (int i = 0; i < facets.Count; i++)
            {
                foreach ((Vector3Int a, Vector3Int b) in Edges(facets[i]))
                {
                    (Vector3Int, Vector3Int) key = EdgeKey(a, b);

                    if (!byEdge.TryGetValue(key, out List<int> list))
                        byEdge[key] = list = new List<int>(2);

                    list.Add(i);
                }
            }

            // Leave a mesh alone unless it actually disagrees with itself. Nineteen of the twenty
            // parts are already consistent, and a repair pass that runs regardless is a repair pass
            // that can only introduce faults.
            if (!HasOrientationConflict(byEdge, facets))
                return facets;

            var result = new List<StlFacet>(facets);
            var visited = new bool[facets.Count];
            var queue = new Queue<int>();
            int flipped = 0;

            for (int seed = 0; seed < result.Count; seed++)
            {
                if (visited[seed])
                    continue;

                visited[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();

                    foreach ((Vector3Int a, Vector3Int b) in Edges(result[current]))
                    {
                        (Vector3Int, Vector3Int) key = EdgeKey(a, b);
                        if (!byEdge.TryGetValue(key, out List<int> neighbours))
                            continue;

                        foreach (int other in neighbours)
                        {
                            if (other == current || visited[other])
                                continue;

                            visited[other] = true;

                            // Same direction along a shared edge means the two disagree about facing.
                            if (Traverses(result[other], a, b))
                            {
                                StlFacet f = result[other];
                                (f.B, f.C) = (f.C, f.B);
                                f.Normal = -f.Normal;
                                result[other] = f;
                                flipped++;
                            }

                            queue.Enqueue(other);
                        }
                    }
                }
            }

            if (flipped > 0)
                Debug.Log($"[STL] Reoriented {flipped} triangle(s) that faced the wrong way.");

            return result;
        }

        /// <summary>
        /// Canonical key for an undirected edge.
        ///
        /// Ordered by coordinate, not by hash. Hash codes collide and do not form a total order, so
        /// keying on them scattered shared edges into different buckets - the walk then saw almost no
        /// neighbours as connected and "repaired" three quarters of the mesh into facing inward.
        /// </summary>
        static (Vector3Int, Vector3Int) EdgeKey(Vector3Int a, Vector3Int b) =>
            Precedes(a, b) ? (a, b) : (b, a);

        static bool Precedes(Vector3Int a, Vector3Int b)
        {
            if (a.x != b.x) return a.x < b.x;
            if (a.y != b.y) return a.y < b.y;
            return a.z < b.z;
        }

        /// <summary>True when two triangles traverse a shared edge the same way, meaning one is flipped.</summary>
        static bool HasOrientationConflict(Dictionary<(Vector3Int, Vector3Int), List<int>> byEdge,
                                           List<StlFacet> facets)
        {
            foreach (KeyValuePair<(Vector3Int, Vector3Int), List<int>> pair in byEdge)
            {
                if (pair.Value.Count != 2)
                    continue;

                (Vector3Int from, Vector3Int to) = pair.Key;

                if (Traverses(facets[pair.Value[0]], from, to) == Traverses(facets[pair.Value[1]], from, to))
                    return true;
            }

            return false;
        }

        static IEnumerable<(Vector3Int, Vector3Int)> Edges(StlFacet f)
        {
            Vector3Int a = Quantise(f.A);
            Vector3Int b = Quantise(f.B);
            Vector3Int c = Quantise(f.C);

            yield return (a, b);
            yield return (b, c);
            yield return (c, a);
        }

        static bool Traverses(StlFacet f, Vector3Int from, Vector3Int to)
        {
            foreach ((Vector3Int a, Vector3Int b) in Edges(f))
                if (a == from && b == to)
                    return true;

            return false;
        }

        static Vector3Int Quantise(Vector3 v) => new Vector3Int(
            Mathf.RoundToInt(v.x * 10000f),
            Mathf.RoundToInt(v.y * 10000f),
            Mathf.RoundToInt(v.z * 10000f));

        /// <summary>
        /// Signed volume of a closed mesh under Unity's winding convention: positive when faces point
        /// outward, negative when the mesh is inverted. Independent of the facet normals the vote
        /// relies on, which is the point - it is a second opinion derived purely from geometry, so it
        /// catches an inside-out import that the vote got wrong.
        ///
        /// Meaningless for an open mesh, so callers should treat a near-zero result as "no opinion".
        /// </summary>
        public static double SignedVolume(Mesh mesh)
        {
            Vector3[] v = mesh.vertices;
            int[] t = mesh.triangles;

            double total = 0.0;
            for (int i = 0; i < t.Length; i += 3)
                total += Vector3.Dot(v[t[i]], Vector3.Cross(v[t[i + 1]], v[t[i + 2]]));

            return total / 6.0;
        }

        /// <summary>
        /// Merges coincident vertices, but only when their faces are within the smoothing angle.
        /// Vertices on a hard edge stay split so the edge renders crisp; vertices around a curve
        /// merge and average their normals so the curve renders smooth.
        /// </summary>
        sealed class VertexWelder
        {
            // 1e-5 world units at the project's 0.01 import scale is one micron of real geometry -
            // far below any real feature on a Duplo part, so nothing distinct is ever merged.
            const float QuantiseStep = 1e-5f;
            const float MergeToleranceSqr = (QuantiseStep * 1.5f) * (QuantiseStep * 1.5f);

            readonly float _cosThreshold;
            readonly Dictionary<Vector3Int, List<int>> _byCell = new();
            readonly List<Vector3> _normalSums = new();

            public readonly List<Vector3> Positions = new();

            public VertexWelder(float smoothingAngleDeg) =>
                _cosThreshold = Mathf.Cos(smoothingAngleDeg * Mathf.Deg2Rad);

            public int Count => Positions.Count;

            public int Add(Vector3 position, Vector3 faceNormal)
            {
                Vector3Int cell = Quantise(position);

                // Search the surrounding cells, not just the containing one. Two vertices meant to be
                // identical can differ in the last float bit and land either side of a cell boundary,
                // which would leave a hairline seam of split normals down an otherwise smooth surface.
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var neighbour = new Vector3Int(cell.x + dx, cell.y + dy, cell.z + dz);
                    if (!_byCell.TryGetValue(neighbour, out List<int> candidates))
                        continue;

                    foreach (int index in candidates)
                    {
                        if ((Positions[index] - position).sqrMagnitude > MergeToleranceSqr)
                            continue;

                        // Compare against the running average, not the first face seen: that is what
                        // lets a smooth fan of many faces accumulate correctly around a curve.
                        Vector3 sum = _normalSums[index];
                        if (sum.sqrMagnitude > 1e-20f &&
                            Vector3.Dot(sum.normalized, faceNormal) >= _cosThreshold)
                        {
                            _normalSums[index] = sum + faceNormal;
                            return index;
                        }
                    }
                }

                int created = Positions.Count;
                Positions.Add(position);
                _normalSums.Add(faceNormal);

                if (!_byCell.TryGetValue(cell, out List<int> own))
                {
                    own = new List<int>(1);
                    _byCell[cell] = own;
                }
                own.Add(created);

                return created;
            }

            public List<Vector3> BuildNormals()
            {
                var normals = new List<Vector3>(_normalSums.Count);
                foreach (Vector3 sum in _normalSums)
                    normals.Add(sum.sqrMagnitude > 1e-20f ? sum.normalized : Vector3.up);
                return normals;
            }

            static Vector3Int Quantise(Vector3 v) => new Vector3Int(
                Mathf.RoundToInt(v.x / QuantiseStep),
                Mathf.RoundToInt(v.y / QuantiseStep),
                Mathf.RoundToInt(v.z / QuantiseStep));
        }
    }
}
#endif
