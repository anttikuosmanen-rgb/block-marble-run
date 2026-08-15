using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Reports which collider a part actually gets from the factory.</summary>
    public static class ColliderProbe
    {
        [MenuItem("Block Marble Run/Probe Colliders")]
        public static void Run()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>("Assets/Parts/PartCatalog.asset");
            if (catalog == null) { Debug.Log("[Collider] no catalog"); return; }

            var host = new GameObject("ColliderProbe");
            var factory = host.AddComponent<PartFactory>();
            factory.catalog = catalog;
            factory.partMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

            foreach (string id in new[] { "funnel_6x7", "funnel_6x7_mirror", "slide_curve_4x4_mirror", "u_turn_slide_mirror" })
            {
                PartDefinition def = null;
                foreach (PartDefinition d in catalog.parts)
                    if (d != null && d.id == id) { def = d; break; }

                if (def == null) { Debug.Log($"[Collider] {id}: not in catalog"); continue; }

                var part = new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0);
                GameObject go = factory.Create(part, host.transform);

                var report = $"[Collider] {id}: hasTunnel {def.hasTunnel} ports {def.ports?.Length ?? 0} " +
                             $"readable {(def.mesh != null && def.mesh.isReadable)} -> ";

                foreach (Collider c in go.GetComponentsInChildren<Collider>())
                {
                    report += c switch
                    {
                        MeshCollider m => $"MeshCollider(convex {m.convex}, mesh '{(m.sharedMesh == null ? "none" : m.sharedMesh.name)}') ",
                        BoxCollider b => $"BoxCollider(size {b.size}) ",
                        _ => c.GetType().Name + " ",
                    };
                }

                Debug.Log(report);
                Object.DestroyImmediate(go);
            }

            Object.DestroyImmediate(host);
        }
    }
}
