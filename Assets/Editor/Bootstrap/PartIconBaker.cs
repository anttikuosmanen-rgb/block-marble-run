#if UNITY_EDITOR
using System.IO;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// Renders each part to a small texture for the palette.
    ///
    /// Baked ahead of time rather than drawn live. A palette of two dozen preview cameras would cost
    /// more every frame than the build itself, and the icons only change when the part set does.
    /// </summary>
    public static class PartIconBaker
    {
        const string IconFolder = "Assets/Art/Icons";
        const int Size = 128;

        [MenuItem("Block Marble Run/Bake Part Icons")]
        public static void Run()
        {
            Directory.CreateDirectory(IconFolder);

            Material material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/Part.mat");
            if (material == null)
            {
                Debug.LogError("[Icons] Part material missing; run Build Main Scene first.");
                return;
            }

            int baked = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def == null || def.mesh == null)
                    continue;

                Bake(def, material);
                baked++;
            }

            AssetDatabase.Refresh();
            AssignIcons();
            Debug.Log($"[Icons] Baked {baked} part icons into {IconFolder}.");
        }

        /// <summary>
        /// Links each part to the texture just written. Done after the refresh, since the importer
        /// has to see the file before there is an asset to point at.
        /// </summary>
        static void AssignIcons()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def == null)
                    continue;

                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>($"{IconFolder}/{def.id}.png");
                if (icon == null)
                    continue;

                def.icon = icon;
                EditorUtility.SetDirty(def);
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>Layer nothing else uses, so the icon camera sees only the part being baked.</summary>
        const int IconLayer = 31;

        static void Bake(PartDefinition def, Material material)
        {
            RenderTexture target = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;

            // A real object rather than Graphics.RenderMesh: that submits into the frame loop, which
            // an explicit camera.Render() does not pick up, so the icon captures whatever else the
            // editor happened to be drawing instead of the part.
            var subject = new GameObject("IconSubject") { layer = IconLayer };
            subject.AddComponent<MeshFilter>().sharedMesh = def.mesh;

            var renderer = subject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", new Color(0.88f, 0.90f, 0.94f));
            renderer.SetPropertyBlock(block);

            var cameraGo = new GameObject("IconCamera") { layer = IconLayer };
            Camera camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);
            camera.orthographic = true;
            camera.targetTexture = target;
            camera.cullingMask = 1 << IconLayer;

            var lightGo = new GameObject("IconLight") { layer = IconLayer };
            Light lamp = lightGo.AddComponent<Light>();
            lamp.type = LightType.Directional;
            lamp.intensity = 1.5f;
            lightGo.transform.rotation = Quaternion.Euler(45f, 150f, 0f);

            Bounds bounds = def.mesh.bounds;
            float extent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));

            // Half the view height, with a little margin so nothing touches the edge.
            camera.orthographicSize = extent * 0.62f;
            camera.nearClipPlane = 0.001f;
            camera.farClipPlane = extent * 10f;

            // Three-quarter view: seen from directly above, a channel and a flat plate look alike.
            var direction = new Vector3(0.62f, 0.6f, -0.75f).normalized;
            cameraGo.transform.position = bounds.center + direction * extent * 3f;
            cameraGo.transform.LookAt(bounds.center);

            try
            {
                camera.Render();

                RenderTexture.active = target;
                var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false);
                texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                texture.Apply();

                File.WriteAllBytes($"{IconFolder}/{def.id}.png", texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
            }
        }
    }
}
#endif
