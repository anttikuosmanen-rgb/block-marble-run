#if UNITY_EDITOR
using System.IO;
using System.Collections.Generic;
using System.Linq;
using BlockMarbleRun.Build;
using BlockMarbleRun.CameraRig;
using BlockMarbleRun.Parts;
using BlockMarbleRun.Play;
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
            Material markerMaterial = EnsureMaterial("PortMarker", "Universal Render Pipeline/Unlit", opaque: true);

            Material sandMaterial = EnsureMaterial("Sand", "Universal Render Pipeline/Lit", opaque: true);
            sandMaterial.SetTexture("_BaseMap", EnsureSandTexture());
            // 600 units of quad divided into tiles the ground's follow can step by exactly.
            sandMaterial.SetTextureScale("_BaseMap", Vector2.one * (600f / Scenery.SandTileUnits));
            sandMaterial.SetColor("_BaseColor", new Color(0.93f, 0.86f, 0.68f));
            sandMaterial.SetFloat("_Smoothness", 0.08f);

            Material waterMaterial = EnsureMaterial("Water", "Universal Render Pipeline/Lit", opaque: false);
            waterMaterial.SetColor("_BaseColor", new Color(0.20f, 0.52f, 0.62f, 0.55f));
            waterMaterial.SetFloat("_Smoothness", 0.95f);
            waterMaterial.SetFloat("_Metallic", 0.1f);

            Material dropletMaterial = EnsureMaterial("Droplet", "Universal Render Pipeline/Unlit", opaque: false);
            dropletMaterial.SetColor("_BaseColor", new Color(0.78f, 0.92f, 1f, 0.85f));

            Material startMaterial = EnsureMaterial("RoleStart", "Universal Render Pipeline/Lit", opaque: true);
            startMaterial.SetColor("_BaseColor", new Color(0.25f, 0.85f, 0.35f));
            Material goalMaterial = EnsureMaterial("RoleGoal", "Universal Render Pipeline/Lit", opaque: true);
            goalMaterial.SetColor("_BaseColor", new Color(0.98f, 0.78f, 0.15f));

            Material marbleMaterial = EnsureMaterial("Marble", "Universal Render Pipeline/Lit", opaque: true);
            marbleMaterial.SetColor("_BaseColor", new Color(0.85f, 0.9f, 1f));
            marbleMaterial.SetFloat("_Smoothness", 0.9f);
            marbleMaterial.SetFloat("_Metallic", 0.6f);


            highlightMaterial.SetColor("_BaseColor", new Color(0.35f, 0.85f, 1f));
            highlightMaterial.SetColor("_EmissionColor", new Color(0.10f, 0.35f, 0.5f));
            highlightMaterial.EnableKeyword("_EMISSION");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject cameraGo = CreateCamera();
            var orbit = cameraGo.GetComponent<OrbitCamera>();
            var cam = cameraGo.GetComponent<Camera>();

            CreateLighting();
            MeshRenderer groundRenderer = CreateGround(groundMaterial, cam);
            CreatePhysicsFloor();

            var sceneryGo = new GameObject("Scenery");
            var scenery = sceneryGo.AddComponent<Scenery>();
            scenery.ground = groundRenderer;
            scenery.gridMaterial = groundMaterial;
            scenery.sandMaterial = sandMaterial;
            scenery.waterMaterial = waterMaterial;
            scenery.targetCamera = cam;

            var splashGo = new GameObject("Splash");
            splashGo.AddComponent<Splash>().dropletMaterial = dropletMaterial;

            var systems = new GameObject("Systems");
            var partRoot = new GameObject("Parts").transform;

            // Plain assignment rather than SerializedObject. Wiring by string field name silently
            // produced a null catalog - the property was found and reported as written, yet the value
            // never reached the saved scene, and the game shipped with an inert palette. Direct
            // assignment is checked by the compiler, so a renamed field breaks the build instead.
            var factory = systems.AddComponent<PartFactory>();
            factory.partMaterial = partMaterial;
            factory.catalog = catalog;
            factory.startMaterial = startMaterial;
            factory.goalMaterial = goalMaterial;
            factory.surfacePhysics = EnsureSurfacePhysics();
            Verify(factory.surfacePhysics, "PartFactory.surfacePhysics");

            var raycaster = systems.AddComponent<BuildRaycaster>();
            raycaster.buildCamera = cam;

            // Everything except the physics floor. Without this the floor would be treated as a part
            // to stack on, and the analytic ground raycast it exists alongside would be shadowed.
            raycaster.partLayers = ~(1 << IgnoreRaycastLayer);

            var ghost = systems.AddComponent<GhostPreview>();
            ghost.ghostMaterial = ghostMaterial;

            Material guideMaterial = EnsureMaterial("Guide", "Universal Render Pipeline/Unlit", opaque: false);
            guideMaterial.SetColor("_BaseColor", new Color(0.45f, 0.9f, 1f, 0.85f));
            guideMaterial.enableInstancing = true;

            var guides = systems.AddComponent<AlignmentGuides>();
            guides.guideMaterial = guideMaterial;
            guides.thickness = 0.004f;

            var controller = systems.AddComponent<BuildController>();
            controller.factory = factory;
            controller.raycaster = raycaster;
            controller.orbitCamera = orbit;
            controller.ghost = ghost;
            controller.guides = guides;
            controller.partRoot = partRoot;
            controller.highlightMaterial = highlightMaterial;

            var stress = systems.AddComponent<StressTest>();
            stress.controller = controller;
            stress.factory = factory;
            stress.partRoot = partRoot;

            var joints = systems.AddComponent<BlockMarbleRun.Track.JointBridges>();
            joints.controller = controller;
            joints.factory = factory;

            var welder = systems.AddComponent<BlockMarbleRun.Track.ChannelWelder>();
            welder.controller = controller;
            welder.factory = factory;

            var markers = systems.AddComponent<BlockMarbleRun.Track.OpenPortMarkers>();
            markers.controller = controller;
            markers.markerMaterial = markerMaterial;

            var play = systems.AddComponent<BlockMarbleRun.Play.PlayController>();
            play.build = controller;
            play.raycaster = raycaster;
            play.marbleMaterial = marbleMaterial;
            play.marbleTypes = EnsureMarbleTypes();

            var mode = systems.AddComponent<BlockMarbleRun.Core.GameMode>();
            mode.build = controller;
            mode.play = play;
            mode.ghost = ghost;
            mode.welder = welder;

            var palette = systems.AddComponent<PartPalette>();
            palette.controller = controller;
            palette.mode = mode;

            // After the mode exists: the stress keys clear the whole map, so they must not fire mid-run.
            stress.mode = mode;
            controller.palette = palette;

            var tester = systems.AddComponent<RunTester>();
            tester.play = play;

            // After the tester exists: a batch must stop when the build is edited underneath it.
            mode.tester = tester;

            var physicsPanel = systems.AddComponent<PhysicsPanel>();
            physicsPanel.play = play;
            physicsPanel.mode = mode;
            physicsPanel.factory = factory;
            physicsPanel.joints = joints;
            physicsPanel.welder = welder;
            physicsPanel.tester = tester;

            var hud = systems.AddComponent<BuildHud>();
            hud.controller = controller;
            hud.stressTest = stress;
            hud.markers = markers;
            hud.joints = joints;
            hud.play = play;
            hud.mode = mode;

            var director = systems.AddComponent<CameraDirector>();
            director.rig = orbit;
            director.play = play;
            director.mode = mode;
            director.pickCamera = cam;

            hud.director = director;

            var browser = systems.AddComponent<SaveBrowser>();
            browser.controller = controller;
            controller.browser = browser;
            hud.palette = palette;

            foreach (Component component in systems.GetComponents<Component>())
                EditorUtility.SetDirty(component);

            Verify(factory.catalog, "PartFactory.catalog");
            Verify(controller.factory, "BuildController.factory");
            Verify(raycaster.buildCamera, "BuildRaycaster.buildCamera");
            Verify(ghost.ghostMaterial, "GhostPreview.ghostMaterial");
            Verify(controller.highlightMaterial, "BuildController.highlightMaterial");
            Verify(markers.markerMaterial, "OpenPortMarkers.markerMaterial");
            Verify(play.marbleMaterial, "PlayController.marbleMaterial");


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

            bool unlit = shaderName.Contains("Unlit");

            if (shaderName.Contains("Lit") || unlit)
            {
                if (!unlit)
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

        /// <summary>
        /// The ball types on offer. 24.5 mm is the size these channels are built around; the others
        /// exist so a run can be tried with something heavier or bouncier.
        ///
        /// Friction and bounce carry the difference between materials rather than mass, since gravity
        /// accelerates every ball alike and mass only tells once momentum meets something else.
        /// </summary>
        static List<MarbleDefinition> EnsureMarbleTypes()
        {
            var types = new List<MarbleDefinition>
            {
                EnsureMarbleType("Plastic", 24.5f, 1.05f, 0.10f, 0.12f, 0.05f,
                    new Color(0.90f, 0.35f, 0.30f), smoothness: 0.55f, metallic: 0.0f),

                EnsureMarbleType("Glass", 24.5f, 2.50f, 0.06f, 0.08f, 0.08f,
                    new Color(0.70f, 0.90f, 1.00f), smoothness: 0.95f, metallic: 0.0f),

                EnsureMarbleType("Steel", 24.5f, 7.80f, 0.05f, 0.07f, 0.04f,
                    new Color(0.75f, 0.78f, 0.82f), smoothness: 0.90f, metallic: 1.0f),

                // Smaller balls rattle in a channel built for 24.5 mm, which is the point of offering
                // one - it shows how much the channel width is doing.
                EnsureMarbleType("Small glass", 16.0f, 2.50f, 0.06f, 0.08f, 0.10f,
                    new Color(0.65f, 1.00f, 0.75f), smoothness: 0.95f, metallic: 0.0f),

                // Lighter than water, so these float rather than sink. Nothing in the code decides
                // that - buoyancy is the weight of the water displaced, so it falls out of the
                // density alone, and wood settles low while the hollow ball rides high.
                EnsureMarbleType("Wood", 24.5f, 0.68f, 0.16f, 0.20f, 0.03f,
                    new Color(0.68f, 0.48f, 0.26f), smoothness: 0.25f, metallic: 0.0f),

                EnsureMarbleType("Hollow", 24.5f, 0.30f, 0.12f, 0.14f, 0.06f,
                    new Color(0.98f, 0.85f, 0.30f), smoothness: 0.60f, metallic: 0.0f),
            };

            AssetDatabase.SaveAssets();
            return types;
        }

        static MarbleDefinition EnsureMarbleType(string name, float diameterMm, float density,
                                                 float dynamicFriction, float staticFriction,
                                                 float bounciness, Color colour,
                                                 float smoothness, float metallic)
        {
            Directory.CreateDirectory("Assets/Parts/Marbles");
            string path = $"Assets/Parts/Marbles/{name}.asset";

            var def = AssetDatabase.LoadAssetAtPath<MarbleDefinition>(path);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<MarbleDefinition>();
                AssetDatabase.CreateAsset(def, path);
            }

            def.displayName = name;
            def.diameterMm = diameterMm;
            def.densityGramsPerCm3 = density;
            def.dynamicFriction = dynamicFriction;
            def.staticFriction = staticFriction;
            def.bounciness = bounciness;
            def.colour = colour;
            def.smoothness = smoothness;
            def.metallic = metallic;

            EditorUtility.SetDirty(def);

            // Re-load after saving, for the same reason the part catalog does (see EnsureCatalog).
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<MarbleDefinition>(path);
        }

        /// <summary>
        /// Track and brick surfaces. Without an explicit material every part uses Unity's default
        /// 0.6 friction, which combines with the ball's to something nobody chose - and the run's
        /// whole character is in how much speed a descent keeps.
        /// </summary>
        static PhysicsMaterial EnsureSurfacePhysics()
        {
            // ".asset", not ".physicsMaterial". Unity 6 does not associate that extension, so the
            // file imported under DefaultImporter as an unrecognised blob and loading it back as a
            // PhysicsMaterial returned null - which then assigned as null, silently, and left every
            // part on the engine's invisible defaults.
            const string path = "Assets/Settings/Surface.asset";

            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (material == null)
            {
                material = new PhysicsMaterial("Surface");
                AssetDatabase.CreateAsset(material, path);
            }

            // Unity's default is 0.6/0.6 with no bounce, which is what every part silently used while
            // there was no material here at all - half the contact behaviour, invisible and untunable.
            material.dynamicFriction = 0.20f;
            material.staticFriction = 0.30f;
            material.bounciness = 0.40f;

            // Average, not Multiply: multiplying two already-low numbers lands somewhere neither the
            // ball nor the track asked for, and makes each one impossible to reason about alone.
            // Average, so each side's number means something on its own. Multiply lands the pair
            // somewhere neither the ball nor the track asked for.
            material.frictionCombine = PhysicsMaterialCombine.Average;
            material.bounceCombine = PhysicsMaterialCombine.Average;

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
        }

        static GameObject CreateCamera()
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };

            Camera camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f);
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 200f;

            // Nothing is audible without one, and its absence is silent in every sense: no warning,
            // no error, just a game with no sound.
            go.AddComponent<AudioListener>();

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
            RenderSettings.ambientSkyColor = new Color(0.52f, 0.60f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.43f, 0.44f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.20f, 0.17f);

            RenderSettings.sun = light;
            RenderSettings.skybox = EnsureSky();

            // Exponential squared, and dense enough to close before the far plane at 200. The ground
            // and the water both end at 300 units; the point of the fog is that neither edge is ever
            // reached by anything the eye can still resolve.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.011f;
            RenderSettings.fogColor = HorizonColour;
        }

        /// <summary>
        /// Colour the world dissolves into. Shared by the fog and the sky's own horizon so the two
        /// meet without a seam - a fog colour that differs from the sky puts a visible band exactly
        /// where the ground was supposed to disappear.
        /// </summary>
        static readonly Color HorizonColour = new Color(0.66f, 0.72f, 0.78f);

        /// <summary>
        /// A procedural sky, which is a gradient with a sun in it and costs nothing to store.
        ///
        /// Kept as an asset rather than built at runtime so the shader is certainly in the build:
        /// WebGL strips shaders nothing references, and a stripped skybox renders as the black that
        /// was there before.
        /// </summary>
        static Material EnsureSky()
        {
            Material sky = EnsureMaterial("Sky", "Skybox/Procedural", opaque: true);
            if (sky == null)
                return null;

            sky.SetFloat("_SunSize", 0.04f);
            sky.SetFloat("_SunSizeConvergence", 5f);
            sky.SetFloat("_AtmosphereThickness", 1.1f);
            sky.SetColor("_SkyTint", new Color(0.55f, 0.66f, 0.80f));
            sky.SetColor("_GroundColor", HorizonColour);
            sky.SetFloat("_Exposure", 1.15f);

            EditorUtility.SetDirty(sky);
            return sky;
        }

        /// <summary>Unity's built-in "Ignore Raycast" layer, used here to keep the floor out of picking.</summary>
        const int IgnoreRaycastLayer = 2;

        /// <summary>
        /// A floor for balls to land on.
        ///
        /// The visible ground deliberately has no collider, because one would shadow the analytic
        /// raycast that placement relies on. That left play mode with no floor whatsoever: a ball
        /// dropped anywhere but onto track fell straight through the world and was culled below the
        /// kill height about a sixth of a second later, which reads as the ball flashing and
        /// vanishing.
        ///
        /// Finite, unlike the buildable world. 2000 units is 200 m of table - about 12,500 studs from
        /// the origin - which no build will reach, and a static box costs nothing to keep around.
        /// </summary>
        static void CreatePhysicsFloor()
        {
            var floor = new GameObject("Physics Floor") { layer = IgnoreRaycastLayer };

            var box = floor.AddComponent<BoxCollider>();
            box.size = new Vector3(2000f, 1f, 2000f);
            box.center = new Vector3(0f, -0.5f, 0f); // top face exactly at ground level
        }

        static MeshRenderer CreateGround(Material material, Camera camera)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";

            // View-only. A collider here would shadow the analytic ground raycast and impose an edge
            // on a world that is meant not to have one.
            Object.DestroyImmediate(ground.GetComponent<Collider>());

            ground.transform.localScale = Vector3.one * 60f; // Unity's plane primitive is 10 units across
            ground.GetComponent<MeshRenderer>().sharedMaterial = material;

            ground.AddComponent<InfiniteGround>().targetCamera = camera;
            EditorUtility.SetDirty(ground);

            return ground.GetComponent<MeshRenderer>();
        }

        /// <summary>
        /// Sand, as a texture rather than a flat colour.
        ///
        /// A plain brown plane 300 units across reads as a void with a colour: there is nothing in it
        /// for the eye to measure distance or motion against. Grain at two scales - fine speckle over
        /// slow drifts - is enough to make it a surface.
        /// </summary>
        static Texture2D EnsureSandTexture()
        {
            const string path = MaterialFolder + "/SandGrain.asset";

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
                return existing;

            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
            {
                name = "SandGrain",
                wrapMode = TextureWrapMode.Repeat,
            };

            var random = new System.Random(7);
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float speckle = (float)random.NextDouble();

                // Perlin at two frequencies for the drifts, tiling because the sample coordinates are
                // whole multiples of the texture width.
                float drift = Mathf.PerlinNoise(x * 8f / size, y * 8f / size) * 0.6f +
                              Mathf.PerlinNoise(x * 24f / size, y * 24f / size) * 0.4f;

                float shade = Mathf.Clamp01(0.72f + (drift - 0.5f) * 0.28f + (speckle - 0.5f) * 0.12f);

                pixels[y * size + x] = new Color(shade, shade * 0.94f, shade * 0.76f, 1f);
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            AssetDatabase.CreateAsset(texture, path);
            return texture;
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
