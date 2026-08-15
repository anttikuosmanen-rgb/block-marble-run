using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Reports what the importer actually produced for one part's mesh.</summary>
    public static class MeshProbe
    {
        [MenuItem("Block Marble Run/Probe Mesh")]
        public static void Run()
        {
            string id = System.Environment.GetEnvironmentVariable("BMR_PROBE_PART") ?? "spiral_6x6";

            PartDefinition def = null;
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var d = AssetDatabase.LoadAssetAtPath<PartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (d != null && d.id == id) { def = d; break; }
            }

            if (def?.mesh == null) { Debug.Log($"[Mesh] '{id}' not found"); return; }

            Mesh m = def.mesh;
            Debug.Log($"[Mesh] {id}: readable {m.isReadable} verts {m.vertexCount} " +
                      $"tris {m.triangles.Length / 3} submeshes {m.subMeshCount} format {m.indexFormat}");

            if (!m.isReadable)
                return;

            Vector3[] normals = m.normals;
            int bad = 0, zero = 0;

            foreach (Vector3 n in normals)
            {
                if (float.IsNaN(n.x) || float.IsNaN(n.y) || float.IsNaN(n.z)) bad++;
                else if (n.sqrMagnitude < 0.5f) zero++;
            }

            Debug.Log($"[Mesh] normals: {normals.Length}, NaN {bad}, degenerate {zero}");

            // Triangles with no area survive welding as slivers and shade as holes.
            Vector3[] v = m.vertices;
            int[] t = m.triangles;
            int slivers = 0;

            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 g = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                if (g.sqrMagnitude < 1e-16f) slivers++;
            }

            Debug.Log($"[Mesh] zero-area triangles after welding: {slivers}");
        }
    }
}
