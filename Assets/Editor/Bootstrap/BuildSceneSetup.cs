#if UNITY_EDITOR
using System.IO;
using System.Linq;
using BlockMarbleRun.Build;
using BlockMarbleRun.CameraRig;
using BlockMarbleRun.Parts;
using BlockMarbleRun.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// Assembles the M1 build scene and the catalog it draws from. Scripted rather than hand-authored
    /// so the scene can be rebuilt from scratch after any change to the part set.
    /// </summary>
    public static class BuildSceneSetup
    {
        const string ScenePath = "Assets/Scenes/Main.unity";
        const string CatalogPath = "Assets/Parts/PartCatalog.asset";
        const string MaterialFolder = "Assets/Art/Materials";

        [MenuItem("Block Marble Run/Build Main Scene")]
        public static void Run()
        {
            PartCatalog catalog = EnsureCatalog();
            Material partMaterial = EnsureMaterial("Part", "Universal Render Pipeline/Lit", opaque: true);
            Material ghostMaterial = EnsureMaterial("Ghost", "Universal Render Pipeline/Lit", opaque: false);
            Material groundMaterial = EnsureMaterial("Ground", "Block Marble Run/Infinite Stud Grid", opaque: true);
            Material highlightMaterial = EnsureMaterial("Highlight", "Universal Render Pipeline/Lit", opaque: true);
            highlightMaterial.SetColor("_BaseColor", new Color(0.35f, 0.85f, 1f));
            highlightMaterial.SetColor("_EmissionColor", new Color(0.10f, 0.35f, 0.5f));
            highlightMaterial.EnableKeyword("_EMISSION");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject cameraGo = CreateCamera();
            var orbit = cameraGo.GetComponent<OrbitCamera>();
            var cam = cameraGo.GetComponent<Camera>();

            CreateLighting();
            CreateGround(groundMaterial, cam);

            var systems = new GameObject("Systems");
            var partRoot = new GameObject("Parts").transform;

            // Plain assignment rather than SerializedObject. Wiring by string field name silently
            // produced a null catalog - the property was found and reported as written, yet the value
            // never reached the saved scene, and the game shipped with an inert palette. Direct
            // assignment is checked by the compiler, so a renamed field breaks the build instead.
            var factory = systems.AddComponent<PartFactory>();
            factory.partMaterial = partMaterial;
            factory.catalog = catalog;

            var raycaster = systems.AddComponent<BuildRaycaster>();
            raycaster.buildCamera = cam;

            var ghost = systems.AddComponent<GhostPreview>();
            ghost.ghostMaterial = ghostMaterial;

            var controller = systems.AddComponent<BuildController>();
            controller.factory = factory;
            controller.raycaster = raycaster;
            controller.orbitCamera = orbit;
            controller.ghost = ghost;
            controller.partRoot = partRoot;
            controller.highlightMaterial = highlightMaterial;

            var stress = systems.AddComponent<StressTest>();
            stress.controller = controller;
            stress.factory = factory;
            stress.partRoot = partRoot;

            var hud = systems.AddComponent<BuildHud>();
            hud.controller = controller;
            hud.stressTest = stress;

            foreach (Component component in systems.GetComponents<Component>())
                EditorUtility.SetDirty(component);

            Verify(factory.catalog, "PartFactory.catalog");
            Verify(controller.factory, "BuildController.factory");
            Verify(raycaster.buildCamera, "BuildRaycaster.buildCamera");
            Verify(ghost.ghostMaterial, "GhostPreview.ghostMaterial");
            Verify(controller.highlightMaterial, "BuildController.highlightMaterial");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log($"[Scene] Main scene rebuilt with {catalog.parts.Count} catalog parts.");
        }

        static PartCatalog EnsureCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<PartCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            catalog.parts = AssetDatabase.FindAssets("t:PartDefinition")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<PartDefinition>)
                .Where(p => p != null && p.mesh != null)
                .OrderBy(p => p.category)
                .ThenBy(p => p.id)
                .ToList();

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            // Re-load after saving. SaveAssets reimports the asset, which destroys and recreates the
            // native object behind this reference - assigning the stale one into the scene silently
            // writes a null, which is how the catalog went missing from the build while the log still
            // reported the right part count.
            return AssetDatabase.LoadAssetAtPath<PartCatalog>(CatalogPath);
        }

        static Material EnsureMaterial(string name, string shaderName, bool opaque)
        {
            Directory.CreateDirectory(MaterialFolder);
            string path = $"{MaterialFolder}/{name}.mat";

            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[Scene] Shader '{shaderName}' not found.");
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;

            if (shaderName.Contains("Lit"))
            {
                material.SetFloat("_Smoothness", 0.35f);
                material.enableInstancing = true;

                if (!opaque)
                {
                    // URP transparency needs the surface type, blend modes, queue and keyword all set
                    // together; changing only the colour's alpha leaves the material opaque.
                    material.SetFloat("_Surface", 1f);
                    material.SetFloat("_Blend", 0f);
                    material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 0f);
                    material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.SetShaderPassEnabled("ShadowCaster", false);
                }
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        static GameObject CreateCamera()
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };

            Camera camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f);
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 200f;

            go.AddComponent<OrbitCamera>();
            return go;
        }

        static void CreateLighting()
        {
            var go = new GameObject("Directional Light");
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.intensity = 1.25f;
            go.transform.rotation = Quaternion.Euler(48f, 41f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.40f, 0.44f, 0.52f);
            RenderSettings.ambientEquatorColor = new Color(0.26f, 0.27f, 0.30f);
            RenderSettings.ambientGroundColor = new Color(0.12f, 0.12f, 0.14f);
        }

        static void CreateGround(Material material, Camera camera)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";

            // View-only. A collider here would shadow the analytic ground raycast and impose an edge
            // on a world that is meant not to have one.
            Object.DestroyImmediate(ground.GetComponent<Collider>());

            ground.transform.localScale = Vector3.one * 30f; // Unity's plane primitive is 10 units across
            ground.GetComponent<MeshRenderer>().sharedMaterial = material;

            ground.AddComponent<InfiniteGround>().targetCamera = camera;
            EditorUtility.SetDirty(ground);
        }

        /// <summary>
        /// Checks that a reference was stored, using reference identity rather than Unity's null
        /// operator. An asset loaded in batchmode can have no native object resident yet, so the
        /// overloaded == reports it as null while the reference itself is perfectly valid and
        /// serializes fine - which would make this guard cry wolf on every build.
        /// </summary>
        static void Verify(Object assigned, string what)
        {
            if (ReferenceEquals(assigned, null))
                Debug.LogError($"[Scene] {what} is null after wiring; the scene would ship non-functional.");
        }

    }
}
#endif
