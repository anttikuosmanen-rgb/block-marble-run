#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// Lays every part out in a grid so the import pipeline can be checked by eye. The analysis
    /// report proves the numbers are right; only looking at the meshes proves the geometry is.
    /// Two importer behaviours are visible here and nowhere else: whether the winding vote got the
    /// triangle orientation right (a wrong call renders parts inside out) and whether the vertex
    /// weld produced smooth curves rather than faceted ones.
    ///
    /// A placeholder until M1 replaces it with real placement.
    /// </summary>
    public static class PartGalleryScene
    {
        const string ScenePath = "Assets/Scenes/Main.unity";
        const string MaterialFolder = "Assets/Art/Materials";
        const int Columns = 6;

        /// <summary>Stud pitch in world units: 16 mm at the project's 0.01 import scale.</summary>
        const float StudUnits = 0.16f;

        static readonly Color[] Palette =
        {
            new Color(0.85f, 0.18f, 0.16f), // red
            new Color(0.95f, 0.68f, 0.10f), // yellow
            new Color(0.13f, 0.42f, 0.72f), // blue
            new Color(0.25f, 0.60f, 0.28f), // green
            new Color(0.92f, 0.92f, 0.90f), // white
            new Color(0.30f, 0.32f, 0.36f), // grey
        };

        [MenuItem("Block Marble Run/Build Part Gallery Scene")]
        public static void Run()
        {
            List<PartDefinition> parts = LoadParts();
            if (parts.Count == 0)
            {
                Debug.LogError("[Gallery] No PartDefinitions found. Run Generate Part Definitions first.");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Material[] materials = EnsureMaterials();

            var root = new GameObject("Parts");
            Bounds bounds = LayOut(parts, materials, root.transform);

            CreateGround(bounds);
            CreateLighting();
            CreateCamera(bounds);

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[Gallery] Placed {parts.Count} parts, bounds {bounds.size}.");
        }

        static List<PartDefinition> LoadParts() =>
            AssetDatabase.FindAssets("t:PartDefinition")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<PartDefinition>)
                .Where(p => p != null && p.mesh != null)
                .OrderBy(p => p.category)
                .ThenBy(p => p.id)
                .ToList();

        static Bounds LayOut(List<PartDefinition> parts, Material[] materials, Transform parent)
        {
            // Spacing from the widest and deepest footprint in the set, so nothing overlaps
            // regardless of what gets added later.
            float cell = parts.Max(p => Mathf.Max(p.footprintSize.x, p.footprintSize.y)) * StudUnits * 1.4f;

            var bounds = new Bounds(Vector3.zero, Vector3.zero);

            for (int i = 0; i < parts.Count; i++)
            {
                PartDefinition part = parts[i];
                int col = i % Columns;
                int row = i / Columns;

                var go = new GameObject(part.id);
                go.transform.SetParent(parent, false);
                go.transform.position = new Vector3(col * cell, 0f, -row * cell);

                go.AddComponent<MeshFilter>().sharedMesh = part.mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = materials[i % materials.Length];

                bounds.Encapsulate(new Bounds(go.transform.position, Vector3.one * cell));
            }

            return bounds;
        }

        static Material[] EnsureMaterials()
        {
            Directory.CreateDirectory(MaterialFolder);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");

            var materials = new Material[Palette.Length];
            for (int i = 0; i < Palette.Length; i++)
            {
                string path = $"{MaterialFolder}/Part_{i}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (material == null)
                {
                    material = new Material(shader);
                    AssetDatabase.CreateAsset(material, path);
                }

                material.shader = shader;
                material.SetColor("_BaseColor", Palette[i]);
                material.SetFloat("_Smoothness", 0.35f);
                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
                materials[i] = material;
            }

            AssetDatabase.SaveAssets();
            return materials;
        }

        static void CreateGround(Bounds bounds)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = new Vector3(bounds.center.x, 0f, bounds.center.z);
            // Unity's plane primitive is 10 units across, hence the tenth.
            ground.transform.localScale = Vector3.one * (Mathf.Max(bounds.size.x, bounds.size.z) * 0.2f);

            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", new Color(0.16f, 0.17f, 0.19f));
            material.SetFloat("_Smoothness", 0f);
            AssetDatabase.CreateAsset(material, $"{MaterialFolder}/Ground.mat");
            ground.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        static void CreateLighting()
        {
            var go = new GameObject("Directional Light");
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.intensity = 1.3f;
            go.transform.rotation = Quaternion.Euler(48f, 41f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.42f, 0.46f, 0.54f);
            RenderSettings.ambientEquatorColor = new Color(0.28f, 0.29f, 0.32f);
            RenderSettings.ambientGroundColor = new Color(0.14f, 0.14f, 0.15f);
        }

        static void CreateCamera(Bounds bounds)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            Camera camera = go.AddComponent<Camera>();
            camera.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.nearClipPlane = 0.05f;

            float extent = Mathf.Max(bounds.size.x, bounds.size.z);
            var pivot = new Vector3(bounds.center.x, 0f, bounds.center.z);

            go.transform.position = pivot + new Vector3(0f, extent * 0.75f, extent * 0.62f);
            go.transform.LookAt(pivot);
            camera.farClipPlane = extent * 4f;

            go.AddComponent<GalleryOrbit>().pivot = pivot;
        }
    }
}
#endif
