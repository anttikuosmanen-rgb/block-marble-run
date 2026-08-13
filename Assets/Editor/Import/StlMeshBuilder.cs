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
            int windingVotes = 0;

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
                    if (geometric.sqrMagnitude > 1e-20f)
                        windingVotes += Vector3.Dot(geometric.normalized, t.Normal.normalized) < 0f ? -1 : 1;
                }
            }

            // A global flip rather than a per-triangle one: a mesh where some faces were flipped and
            // others were not would be worse than either consistent choice, and disagreement here
            // means the file's normals are unreliable, not that the model is genuinely mixed.
            bool flipWinding = windingVotes < 0;

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
