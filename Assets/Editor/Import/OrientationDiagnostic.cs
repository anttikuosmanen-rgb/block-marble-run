#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Measures whether each built mesh actually faces outward, using the mesh's own geometry rather
    /// than the exporter's normals. Signed volume under Unity's cross-product convention is positive
    /// for an outward-facing closed solid and negative for an inverted one, so it settles orientation
    /// questions that eyeballing a render can only raise.
    /// </summary>
    public static class OrientationDiagnostic
    {
        const string MeshFolder = "Assets/Art/Meshes";

        [MenuItem("Block Marble Run/Diagnose Orientation")]
        public static void Run()
        {
            string[] paths = Directory.GetFiles(MeshFolder, "*.stl", SearchOption.TopDirectoryOnly);
            System.Array.Sort(paths);

            var sb = new StringBuilder();
            sb.AppendLine($"{"part",-24} {"builtVol",12} {"verdict",-12} {"vertsIn",9} {"vertsOut",9}");
            sb.AppendLine(new string('-', 72));

            foreach (string path in paths)
            {
                List<StlFacet> facets = StlFile.Read(path);
                int rawVerts = facets.Count * 3;

                Mesh mesh = StlMeshBuilder.Build(facets, 0.01f, 30f, "diag");
                float volume = SignedVolume(mesh);

                sb.AppendLine(
                    $"{Path.GetFileNameWithoutExtension(path),-24} " +
                    $"{volume,12:0.0000} " +
                    $"{(volume >= 0f ? "outward" : "INSIDE-OUT"),-12} " +
                    $"{rawVerts,9} {mesh.vertexCount,9}");

                Object.DestroyImmediate(mesh);
            }

            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Sum of the signed tetrahedron volumes from the origin to each triangle. Uses Unity's own
        /// Vector3.Cross so the sign reflects how Unity will actually rasterise the winding.
        /// </summary>
        static float SignedVolume(Mesh mesh)
        {
            Vector3[] v = mesh.vertices;
            int[] t = mesh.triangles;

            float total = 0f;
            for (int i = 0; i < t.Length; i += 3)
                total += Vector3.Dot(v[t[i]], Vector3.Cross(v[t[i + 1]], v[t[i + 2]])) / 6f;

            return total;
        }
    }
}
#endif
