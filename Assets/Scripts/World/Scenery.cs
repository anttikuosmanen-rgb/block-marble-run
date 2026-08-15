using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.World
{
    /// <summary>What the world stands on.</summary>
    public enum FloorStyle
    {
        Grid,
        Sand,
        Water,
    }

    /// <summary>
    /// The floor the build stands on, and the water it can stand in.
    ///
    /// The water surface sits <em>above</em> the build plane rather than at it, with the sand bed at
    /// y=0 where the grid already is. That keeps the whole coordinate system untouched - no part ever
    /// goes below layer 0 - while a brick on the ground is genuinely half submerged and a pillar
    /// carrying a run out over the water visibly runs down into it.
    /// </summary>
    [ExecuteAlways]
    public sealed class Scenery : MonoBehaviour
    {
        /// <summary>
        /// The one in the scene, for the marbles to ask about water.
        ///
        /// A singleton rather than a reference threaded through every spawn: marbles are created at
        /// runtime by two different paths and neither of them is about scenery.
        /// </summary>
        public static Scenery Active { get; private set; }

        public FloorStyle style = FloorStyle.Grid;

        /// <summary>
        /// World size of one repeat of the sand grain. The ground's follow snaps to it, and the
        /// bootstrap sets the material's tiling from it - one number, so the two cannot drift apart.
        /// </summary>
        public const float SandTileUnits = 5f;

        public MeshRenderer ground;
        public Material gridMaterial;
        public Material sandMaterial;
        public Material waterMaterial;

        [Tooltip("Height of the still water surface, in world units. A brick layer is 0.192.")]
        [Range(0f, 4f)] public float waterLevel = 0.12f;

        [Tooltip("Peak-to-trough height of the ripples, in world units. A unit is 10 cm.")]
        [Range(0f, 0.4f)] public float waveHeight = 0.09f;

        [Tooltip("How fast the crests travel. Slow reads as a large body of water.")]
        [Range(0.02f, 2f)] public float waveSpeed = 0.22f;

        [Tooltip("Distance between crests. Long and slow together are what make water look big.")]
        [Range(1f, 20f)] public float waveLength = 5f;

        [Tooltip("Water density in g/cm3. Fresh water is 1.0, sea water about 1.025.")]
        [Range(0.5f, 1.5f)] public float waterDensity = 1f;

        [Tooltip("Drag coefficient. A smooth sphere is about 0.47.")]
        [Range(0.05f, 2f)] public float dragCoefficient = 0.47f;

        [Tooltip("How strongly water resists a ball's spin.")]
        [Range(0f, 20f)] public float spinDrag = 6f;

        /// <summary>Water level expressed in brick layers, which is how a builder thinks about it.</summary>
        public float WaterLayers
        {
            get => waterLevel / Grid.GridCoord.BrickUnits;
            set => waterLevel = Mathf.Max(0f, value) * Grid.GridCoord.BrickUnits;
        }

        public Camera targetCamera;

        GameObject _water;
        Mesh _waterMesh;
        Vector3[] _waterRest;
        Vector3[] _waterWork;
        Vector3[] _waterNormals;
        bool _built;

        const int Cells = 80;

        /// <summary>
        /// How far the water reaches. The same distance the ground quad covers, so neither one ends
        /// before the other - a shoreline where the water simply stops and dirt carries on is the
        /// most obviously wrong thing in the scene, and fog can only hide an edge that is far away.
        /// </summary>
        const float Span = 600f;

        void OnEnable()
        {
            Active = this;
            Apply();
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        public bool HasWater => style == FloorStyle.Water;

        /// <summary>The water height at a world position, ripples included. Below the surface is wet.</summary>
        public float SurfaceAt(Vector3 world)
        {
            if (!HasWater)
                return float.NegativeInfinity;

            return waterLevel + Ripple(world.x, world.z, Time.time);
        }

        float Ripple(float x, float z, float time)
        {
            // Two crossing waves at an irrational ratio, so the pattern does not visibly repeat.
            float a = Mathf.Sin((x / waveLength + time * waveSpeed) * Mathf.PI * 2f);
            float b = Mathf.Sin((z / (waveLength * 0.73f) - time * waveSpeed * 0.6f) * Mathf.PI * 2f);

            return (a + b) * 0.25f * waveHeight;
        }

        public void Cycle()
        {
            style = (FloorStyle)(((int)style + 1) % 3);
            Apply();
        }

        public void Apply()
        {
            if (ground != null)
            {
                Material floor = style switch
                {
                    FloorStyle.Sand => sandMaterial,
                    FloorStyle.Water => sandMaterial, // the bed under the water is the same sand
                    _ => gridMaterial,
                };

                if (floor != null)
                    ground.sharedMaterial = floor;

                var follow = ground.GetComponent<InfiniteGround>();
                if (follow != null)
                    follow.Snap = style == FloorStyle.Grid ? Grid.GridCoord.StudUnits : SandTileUnits;
            }

            if (HasWater)
                EnsureWater();

            if (_water != null)
                _water.SetActive(HasWater);
        }

        void EnsureWater()
        {
            if (_water != null)
                return;

            _water = new GameObject("Water") { hideFlags = HideFlags.DontSave };
            _water.transform.SetParent(transform, false);

            _waterMesh = BuildGrid();
            _water.AddComponent<MeshFilter>().sharedMesh = _waterMesh;

            var renderer = _water.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = waterMaterial;

            // No shadows: a translucent sheet casting them onto its own bed reads as dirt, and the
            // bed is the thing the water is meant to let you see.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>Index to world offset, squared so detail crowds the middle.</summary>
        static float Warp(int index)
        {
            float u = index / (float)Cells * 2f - 1f;   // -1 .. 1
            return Mathf.Sign(u) * u * u * (Span * 0.5f);
        }

        static Mesh BuildGrid()
        {
            var mesh = new Mesh { name = "WaterSurface" };

            int side = Cells + 1;
            var vertices = new Vector3[side * side];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[Cells * Cells * 6];

            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                // Spaced by the square of the distance from the middle, so the same vertex count
                // gives half-unit cells under the build and coarse ones at the horizon. Spread the
                // 600 unit span evenly and every cell would be twelve units across - wider than the
                // ripples themselves, which would then simply not exist.
                float px = Warp(x), pz = Warp(z);

                vertices[z * side + x] = new Vector3(px, 0f, pz);
                uvs[z * side + x] = new Vector2(px, pz) / 12f;
            }

            int t = 0;
            for (int z = 0; z < Cells; z++)
            for (int x = 0; x < Cells; x++)
            {
                int i = z * side + x;

                triangles[t++] = i;
                triangles[t++] = i + side;
                triangles[t++] = i + 1;

                triangles[t++] = i + 1;
                triangles[t++] = i + side;
                triangles[t++] = i + side + 1;
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(Span, 4f, Span));
            mesh.triangles = triangles;
            mesh.RecalculateNormals();

            // Rewritten every frame, so it goes in a buffer meant for that rather than one Unity
            // expects to upload once.
            mesh.MarkDynamic();

            return mesh;
        }

        void LateUpdate()
        {
            if (Keyboard.current != null && Keyboard.current.bKey.wasPressedThisFrame && Application.isPlaying)
                Cycle();

            if (!HasWater || _water == null || _waterMesh == null)
                return;

            Camera cam = targetCamera != null ? targetCamera : Camera.main;

            // Snapped to whole wavelengths, so following the camera does not drag the pattern across
            // the world - the same reason the ground quad snaps to stud pitches.
            Vector3 centre = cam != null ? cam.transform.position : Vector3.zero;
            var origin = new Vector3(
                Mathf.Round(centre.x / waveLength) * waveLength,
                waterLevel,
                Mathf.Round(centre.z / waveLength) * waveLength);

            _water.transform.position = origin;

            _waterRest ??= _waterMesh.vertices;
            _waterWork ??= new Vector3[_waterRest.Length];
            _waterNormals ??= new Vector3[_waterRest.Length];

            // Standing still in the editor, the surface is the same every frame. Rebuilding it there
            // is pure cost with nothing to show for it.
            if (!Application.isPlaying && _built)
                return;

            float time = Application.isPlaying ? Time.time : 0f;

            float amplitude = 0.25f * waveHeight;
            float kx = Mathf.PI * 2f / waveLength;
            float kz = Mathf.PI * 2f / (waveLength * 0.73f);

            for (int i = 0; i < _waterRest.Length; i++)
            {
                Vector3 rest = _waterRest[i];

                float x = rest.x + origin.x;
                float z = rest.z + origin.z;

                float a = (x / waveLength + time * waveSpeed) * Mathf.PI * 2f;
                float b = (z / (waveLength * 0.73f) - time * waveSpeed * 0.6f) * Mathf.PI * 2f;

                _waterWork[i] = new Vector3(rest.x, (Mathf.Sin(a) + Mathf.Sin(b)) * amplitude, rest.z);

                // The wave's own slope, rather than RecalculateNormals rebuilding them from the
                // triangles: the surface is a known function, so its gradient is two cosines and a
                // normalise instead of a pass over three thousand faces every frame.
                _waterNormals[i] = new Vector3(
                    -Mathf.Cos(a) * kx * amplitude, 1f, -Mathf.Cos(b) * kz * amplitude).normalized;
            }

            _waterMesh.vertices = _waterWork;
            _waterMesh.normals = _waterNormals;

            _built = true;
        }
    }
}
