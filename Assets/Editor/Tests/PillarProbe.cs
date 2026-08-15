using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Checks the shaft detection and the stretched result against the modelled pillar.</summary>
    public static class PillarProbe
    {
        [MenuItem("Block Marble Run/Probe Pillars")]
        public static void Run()
        {
            PartDefinition source = Find("pillar_2x2x7");
            if (source?.mesh == null) { Debug.Log("pillar_2x2x7 missing"); return; }

            Mesh mesh = source.mesh;
            Debug.Log($"[Pillar] readable {mesh.isReadable}  bounds {mesh.bounds.size}  " +
                      $"layers {source.heightLayers}");

            if (!PillarMeshBuilder.FindShaft(mesh, out float from, out float to))
            {
                Debug.Log("[Pillar] no shaft found");
                return;
            }

            Debug.Log($"[Pillar] shaft {from:0.0000} .. {to:0.0000} units " +
                      $"({(to - from) * 100f:0.0} mm of {mesh.bounds.size.y * 100f:0.0} mm)");

            var pillars = new ProceduralPillars(source);
            ProceduralPillars.Active = pillars;

            Debug.Log($"[Pillar] shortest {pillars.ShortestLayers} layers");

            foreach (int layers in new[] { 2, 3, 4, 7, 9, 14, 30 })
            {
                PartDefinition def = pillars.ForLayers(layers);

                if (def == null)
                {
                    Debug.Log($"[Pillar] {layers}: refused");
                    continue;
                }

                float expected = layers * GridCoord.LayerUnits +
                                 (mesh.bounds.size.y - source.heightLayers * GridCoord.LayerUnits);

                Debug.Log($"[Pillar] {layers}: '{def.id}' height {def.mesh.bounds.size.y:0.0000} " +
                          $"expected {expected:0.0000}  verts {def.mesh.vertexCount}");
            }
        }

        static PartDefinition Find(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def != null && def.id == id) return def;
            }
            return null;
        }
    }
}
