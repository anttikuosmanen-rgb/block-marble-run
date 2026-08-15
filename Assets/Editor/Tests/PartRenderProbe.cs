using System.IO;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Renders one part from several angles, for looking at rather than measuring.</summary>
    public static class PartRenderProbe
    {
        const int Size = 640;
        const int Layer = 30;

        [MenuItem("Block Marble Run/Render Part Views")]
        public static void Run()
        {
            string id = System.Environment.GetEnvironmentVariable("BMR_RENDER_PART") ?? "spiral_6x6";
            string outFolder = System.Environment.GetEnvironmentVariable("BMR_RENDER_OUT") ?? "Temp/PartViews";

            PartDefinition def = Find(id);
            if (def?.mesh == null)
            {
                Debug.Log($"[Render] '{id}' not found");
                return;
            }

            Directory.CreateDirectory(outFolder);

            // Unlit when asked: a black patch under a lit material can be a hole in the mesh or a
            // normal pointing the wrong way, and only one of those survives having the lighting taken
            // away. Rendering both tells them apart without guessing.
            bool unlit = System.Environment.GetEnvironmentVariable("BMR_RENDER_UNLIT") == "1";

            var material = new Material(Shader.Find(unlit
                ? "Universal Render Pipeline/Unlit"
                : "Universal Render Pipeline/Lit"));

            material.SetColor("_BaseColor", new Color(0.85f, 0.55f, 0.35f));

            if (!unlit)
                material.SetFloat("_Smoothness", 0.2f);

            var go = new GameObject("Subject") { layer = Layer };
            go.AddComponent<MeshFilter>().sharedMesh = def.mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;

            var lightGo = new GameObject("Light") { layer = Layer };
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.4f;
            lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            Bounds b = def.mesh.bounds;
            float radius = b.extents.magnitude;

            var views = new (string Name, Vector3 Direction)[]
            {
                ("top", new Vector3(0f, 1f, 0.0001f)),
                ("angled", new Vector3(0.6f, 0.75f, -0.6f)),
                ("side", new Vector3(0f, 0.12f, -1f)),
                ("under", new Vector3(0.2f, -1f, -0.2f)),
            };

            foreach ((string name, Vector3 direction) in views)
                Shot(def, b, radius, direction, $"{outFolder}/{id}_{name}.png");

            Object.DestroyImmediate(go);
            Object.DestroyImmediate(lightGo);

            Debug.Log($"[Render] wrote {views.Length} views of '{id}' to {outFolder}");
        }

        static void Shot(PartDefinition def, Bounds b, float radius, Vector3 direction, string path)
        {
            RenderTexture target = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;

            var cameraGo = new GameObject("Cam") { layer = Layer };
            Camera camera = cameraGo.AddComponent<Camera>();

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.17f, 0.20f);
            camera.orthographic = true;
            camera.orthographicSize = radius * 0.85f;
            camera.cullingMask = 1 << Layer;
            camera.targetTexture = target;
            camera.nearClipPlane = 0.001f;
            camera.farClipPlane = radius * 8f;

            cameraGo.transform.position = b.center + direction.normalized * radius * 3f;
            cameraGo.transform.LookAt(b.center);

            try
            {
                camera.Render();
                RenderTexture.active = target;

                var texture = new Texture2D(Size, Size, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                texture.Apply();

                File.WriteAllBytes(path, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(cameraGo);
                RenderTexture.ReleaseTemporary(target);
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
