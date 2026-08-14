#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// One-shot project configuration: creates the URP assets and applies the physics tuning from
    /// DESIGN.md §2. Kept as a script rather than hand-edited ProjectSettings so the reasoning
    /// stays next to the values and CI can reproduce the setup from scratch.
    /// </summary>
    public static class SetupProject
    {
        const string RenderingFolder = "Assets/Settings";

        [MenuItem("Block Marble Run/Setup Project")]
        public static void Run()
        {
            ConfigureRendering();
            ConfigurePhysics();
            ConfigureInput();
            EnsureMainScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[Setup] Rendering, physics and build scene configured.");
        }

        /// <summary>
        /// Switches the player to the Input System package. Until this is set, Mouse.current and
        /// Keyboard.current return null at runtime and every input read silently does nothing - the
        /// project would build and run with no controls at all.
        /// </summary>
        static void ConfigureInput()
        {
            const int InputSystemOnly = 1;

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[Setup] Could not open ProjectSettings.asset; input handler unchanged.");
                return;
            }

            var so = new SerializedObject(assets[0]);
            SerializedProperty handler = so.FindProperty("activeInputHandler");

            if (handler == null)
            {
                Debug.LogError("[Setup] activeInputHandler not found; input handler unchanged.");
                return;
            }

            if (handler.intValue == InputSystemOnly)
                return;

            handler.intValue = InputSystemOnly;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log("[Setup] Active input handler set to Input System.");
        }

        /// <summary>
        /// CI needs at least one scene in the build settings or every target fails before it starts.
        /// </summary>
        static void EnsureMainScene()
        {
            const string ScenePath = "Assets/Scenes/Main.unity";
            Directory.CreateDirectory("Assets/Scenes");

            if (!File.Exists(ScenePath))
            {
                UnityEngine.SceneManagement.Scene scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, ScenePath);
            }

            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.path == ScenePath)
                    return;

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        static void ConfigureRendering()
        {
            Directory.CreateDirectory(RenderingFolder);

            string rendererPath = $"{RenderingFolder}/UniversalRenderer.asset";
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, rendererPath);
            }

            string pipelinePath = $"{RenderingFolder}/UniversalRenderPipeline.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }

            // Forward on WebGL2: the target has no compute shaders, so deferred is not on the table
            // (DESIGN.md §0.1).
            rendererData.renderingMode = RenderingMode.Forward;
            EditorUtility.SetDirty(rendererData);

            ConfigureShadows(pipeline);

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            PlayerSettings.colorSpace = ColorSpace.Linear;
        }

        /// <summary>
        /// Pushes shadow sampling off the surface it is shading.
        ///
        /// A channel is a smooth-shaded curve barely a couple of millimetres thick, and at default
        /// bias it shadows itself: the depth comparison lands on the wrong side of its own surface
        /// and paints dark bands across the inside of the trough. They read as structure hidden
        /// inside the part, which is exactly what they are not.
        /// </summary>
        static void ConfigureShadows(UniversalRenderPipelineAsset pipeline)
        {
            var so = new SerializedObject(pipeline);

            SerializedProperty depth = so.FindProperty("m_ShadowDepthBias");
            SerializedProperty normal = so.FindProperty("m_ShadowNormalBias");
            SerializedProperty distance = so.FindProperty("m_ShadowDistance");
            SerializedProperty resolution = so.FindProperty("m_MainLightShadowmapResolution");

            if (depth != null) depth.floatValue = 2.0f;
            if (normal != null) normal.floatValue = 2.0f;

            // A build spans metres at most, so a long shadow distance only wastes map resolution on
            // ground nobody is looking at - and coarse texels are half of why the bias is needed.
            if (distance != null) distance.floatValue = 12f;
            if (resolution != null) resolution.intValue = 2048;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
        }

        /// <summary>
        /// A 13 mm marble is far below PhysX's comfortable range - the stock 10 mm contact offset is
        /// wider than the marble's radius. The world is therefore built at 1 unit = 10 cm and gravity
        /// scaled to match, which keeps real-time dynamics while giving the solver room to work.
        /// See DESIGN.md §2 for the full derivation.
        /// </summary>
        static void ConfigurePhysics()
        {
            // Length scaled by 10 relative to metric, so gravity scales by 10 to keep a marble
            // falling at its real-world rate.
            Physics.gravity = new Vector3(0f, -98.1f, 0f);

            Physics.defaultContactOffset = 0.002f;
            Physics.defaultSolverIterations = 10;
            Physics.defaultSolverVelocityIterations = 4;
            Physics.bounceThreshold = 1.0f;

            ConfigureTimestep();
        }

        /// <summary>
        /// Time settings have to go through the TimeManager asset. Assigning Time.fixedDeltaTime in
        /// the editor is a runtime override that is discarded on reload, so it never reaches the
        /// project settings and CI would build at the stock 50 Hz.
        ///
        /// Unity 6 stores the timestep as an exact rational (count over rate) rather than a float,
        /// which is why this writes a count instead of a fraction.
        /// </summary>
        static void ConfigureTimestep()
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TimeManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[Setup] Could not open TimeManager.asset; fixed timestep left at its default.");
                return;
            }

            var so = new SerializedObject(assets[0]);

            SerializedProperty rate = so.FindProperty("Fixed Timestep.m_Rate.m_Numerator");
            SerializedProperty count = so.FindProperty("Fixed Timestep.m_Count");

            if (rate == null || count == null)
            {
                Debug.LogError("[Setup] TimeManager layout not as expected; fixed timestep left at its default.");
                return;
            }

            // 120 Hz. Small fast spheres tunnel at the stock 50 Hz even with continuous detection.
            const int TargetHz = 120;
            count.intValue = rate.intValue / TargetHz;

            // Stops a tab-switch or GC stall on WebGL from triggering a physics death spiral.
            so.FindProperty("Maximum Allowed Timestep").floatValue = 0.1f;

            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
