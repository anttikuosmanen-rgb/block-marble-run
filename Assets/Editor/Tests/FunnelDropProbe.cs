using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Drops a ball onto a part and reports where it ends up.
    ///
    /// Transforms are pushed to physics explicitly. Without that the colliders stay wherever they
    /// were created while the renderers move, and everything falls through everything - which looks
    /// exactly like a collider that is not working, and is not.
    /// </summary>
    public static class FunnelDropProbe
    {
        [MenuItem("Block Marble Run/Probe Funnel Drop")]
        public static void Run()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>("Assets/Parts/PartCatalog.asset");

            PartDefinition def = null;
            foreach (PartDefinition d in catalog.parts)
                if (d != null && d.id == "funnel_6x7") { def = d; break; }

            if (def == null) { Debug.Log("[Drop] funnel not found"); return; }

            var host = new GameObject("DropProbe");
            var factory = host.AddComponent<PartFactory>();
            factory.catalog = catalog;
            factory.partMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

            var part = new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0);
            GameObject funnel = factory.Create(part, host.transform);

            part.GetTransform(out Vector3 origin, out _);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = new Vector3(0f, -1.5f, 0f);
            floor.transform.localScale = new Vector3(40f, 0.2f, 40f);

            Physics.gravity = new Vector3(0f, -98.1f, 0f);
            Physics.simulationMode = SimulationMode.Script;

            Vector3 bowl = origin + new Vector3(def.mesh.bounds.center.x, 0f, def.mesh.bounds.center.z);
            float rim = origin.y + def.mesh.bounds.size.y;

            Debug.Log($"[Drop] bowl centre {bowl}, rim at y={rim:0.000}, floor at -1.4");

            foreach (float offset in new[] { 0f, 0.20f, 0.30f, 0.38f })
            {
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.transform.localScale = Vector3.one * 0.245f;
                ball.transform.position = bowl + new Vector3(offset, 0.45f, 0f);

                var body = ball.AddComponent<Rigidbody>();
                body.mass = 0.008f;
                body.maxAngularVelocity = 200f;

                // Otherwise a ball moving 14 units a step passes clean through a 0.2 thick floor,
                // and "fell forever" gets confused with "was never stopped by the funnel".
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                Physics.SyncTransforms();

                float lowestSeen = float.MaxValue;
                bool everSlowed = false;

                for (int step = 0; step < 400; step++)
                {
                    Physics.Simulate(0.02f);

                    lowestSeen = Mathf.Min(lowestSeen, ball.transform.position.y);

                    // Falling freely for 0.02 s gains about 2 units/s; anything less means it touched.
                    if (step > 2 && body.linearVelocity.y > -1f)
                        everSlowed = true;
                }

                float y = ball.transform.position.y;

                string verdict =
                    y < origin.y ? "PASSED THROUGH the hole" :
                    y > rim - 0.05f ? "RESTING ON A FLAT TOP at rim height" :
                    "caught in the bowl";

                Debug.Log($"[Drop] released {offset * 100f:0} mm off centre -> y={y:0.000}  {verdict}" +
                          $"   touched something: {everSlowed}");

                Object.DestroyImmediate(ball);
            }

            Physics.simulationMode = SimulationMode.FixedUpdate;
            Object.DestroyImmediate(floor);
            Object.DestroyImmediate(host);
        }
    }
}
