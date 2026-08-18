#if UNITY_EDITOR
using System.IO;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using BlockMarbleRun.Persistence;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// Renders a picture of every bundled level, so the save browser has something to show.
    ///
    /// The browser is a grid of thumbnails - a creation is chosen by recognising it - so a level that
    /// ships without one reads as broken rather than as new. A creation saved from the running game
    /// brings its own picture; one bundled from a file has none, and this makes it.
    ///
    /// Built the same way the game builds a creation: the save is applied to a real GridMap and the
    /// parts come from the same factory. A separate renderer would drift from what the pieces
    /// actually look like, which is the one thing a thumbnail has to be right about.
    /// </summary>
    public static class LevelThumbnailBaker
    {
        const string Folder = "Assets/Resources/Levels";
        const int Size = 512;

        /// <summary>A layer nothing else uses, so the camera sees the level and nothing of the scene.</summary>
        const int BakeLayer = 31;

        [MenuItem("Block Marble Run/Bake Level Thumbnails")]
        public static void Run()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>("Assets/Parts/PartCatalog.asset");

            if (catalog == null)
            {
                Debug.LogError("[Levels] No part catalog.");
                return;
            }

            int baked = 0;

            foreach (string path in Directory.GetFiles(Folder, "*.json"))
            {
                SaveModel model = SaveModel.FromJson(File.ReadAllText(path));

                if (model?.parts == null)
                {
                    Debug.LogWarning($"[Levels] {Path.GetFileName(path)} could not be read.");
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(path);

                if (Bake(model, catalog, $"{Folder}/{name}.png"))
                    baked++;
            }

            AssetDatabase.Refresh();

            // Thumbnails are pictures, not textures to be sampled: read them in without compression,
            // which at this size costs nothing and keeps the blocks' edges clean.
            foreach (string path in Directory.GetFiles(Folder, "*.png"))
            {
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
                    continue;

                importer.textureType = TextureImporterType.GUI;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            Debug.Log($"[Levels] Baked {baked} thumbnail(s) into {Folder}.");
        }

        static bool Bake(SaveModel model, PartCatalog catalog, string outputPath)
        {
            var host = new GameObject("ThumbnailHost");
            var factory = host.AddComponent<PartFactory>();
            factory.catalog = catalog;
            factory.partMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

            // Awake does not run for a component added by an editor tool, and without this the parts
            // come out white and every generated pillar is missing - the pillar factory is what
            // rebuilds a support named in a save.
            factory.Initialise();

            var map = new GridMap();
            var service = new SaveService(SaveStoreFactory.Create(), catalog);

            RenderTexture target = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;

            var cameraGo = new GameObject("ThumbnailCamera");
            var lightGo = new GameObject("ThumbnailLight");

            try
            {
                // No colliders: nothing is simulated here, and cooking a mesh collider for every part
                // of a 129-piece creation costs seconds for a picture.
                service.Apply(model, map, part =>
                {
                    GameObject go = factory.Create(part, host.transform, withCollider: false);
                    SetLayer(go, BakeLayer);
                    return go;
                });

                if (map.CellCount == 0)
                    return false;

                // The lifts a run needs to meet a funnel, as in the game (ChannelNetwork). A picture
                // of a build standing differently from how it will open is a small lie.
                ChannelNetwork.Recompute(map);

                Bounds bounds = Framed(map, host.transform);

                Camera camera = cameraGo.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.30f, 0.42f, 0.52f);
                camera.cullingMask = 1 << BakeLayer;
                camera.orthographic = false;
                camera.fieldOfView = 35f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 500f;
                camera.targetTexture = target;

                // Three quarters on and above, the angle a build is drawn at in the game, so the
                // picture and the thing it stands for read as the same object.
                var direction = new Vector3(0.6f, 0.62f, -0.8f).normalized;
                float radius = Mathf.Max(0.25f, bounds.extents.magnitude);
                float distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;

                cameraGo.transform.position = bounds.center + direction * distance;
                cameraGo.transform.LookAt(bounds.center);

                Light lamp = lightGo.AddComponent<Light>();
                lamp.type = LightType.Directional;
                lamp.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(50f, 150f, 0f);

                camera.Render();

                RenderTexture.active = target;
                var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false);
                texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                texture.Apply();

                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);

                return true;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);

                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>What the camera has to take in: every renderer that was actually built.</summary>
        static Bounds Framed(GridMap map, Transform host)
        {
            var bounds = new Bounds();
            bool first = true;

            foreach (Renderer renderer in host.GetComponentsInChildren<Renderer>())
            {
                if (first)
                {
                    bounds = renderer.bounds;
                    first = false;
                    continue;
                }

                bounds.Encapsulate(renderer.bounds);
            }

            return first ? new Bounds(Vector3.zero, Vector3.one) : bounds;
        }

        static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;

            foreach (Transform child in go.transform)
                SetLayer(child.gameObject, layer);
        }
    }
}
#endif
