using System.Diagnostics;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using BlockMarbleRun.World;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Fills the world with parts to answer the M1 question: does one GameObject per part hold up on
    /// WebGL, or is the instanced rendering path needed (DESIGN.md §5, §10)?
    ///
    /// Deliberately measured on the target rather than reasoned about. WebGL2 pays far more per draw
    /// call than macOS does, so a figure taken in the editor would say nothing useful.
    /// </summary>
    public sealed class StressTest : MonoBehaviour
    {
        public BuildController controller;
        public PartFactory factory;
        public Transform partRoot;
        public BlockMarbleRun.Core.GameMode mode;

        [SerializeField] int targetParts = 2000;

        [Tooltip("Spawn colliders too. Turning them off isolates rendering cost from physics cost.")]
        [SerializeField] bool withColliders = true;

        int _spawned;
        double _spawnMs;
        long _triangles;
        string _mode = "none";

        public int Spawned => _spawned;
        public double SpawnMs => _spawnMs;
        public long Triangles => _triangles;
        public string Mode => _mode;

        void Awake()
        {
            controller = controller != null ? controller : GetComponent<BuildController>();
            factory = factory != null ? factory : GetComponent<PartFactory>();
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            // Build mode only. These keys clear and repopulate the whole map, which in play mode
            // means G silently deletes the build the player is running a ball through.
            if (mode != null && mode.Current != BlockMarbleRun.Core.Mode.Build)
                return;

            // Measured on WebGL: cutting triangles fourfold changed nothing (20 ms either way), while
            // dropping the MaterialPropertyBlock took 2000 parts from 20 ms to 13 ms. The cost is
            // per-draw CPU work, not geometry - so palette materials are the shipped path, and these
            // variants stay so the comparison can be re-run rather than remembered.
            if (keyboard.tKey.wasPressedThisFrame)
                Spawn(targetParts, "building_block_2x2", perInstanceColor: false, "palette materials");

            if (keyboard.yKey.wasPressedThisFrame)
                Spawn(targetParts, "building_block_2x2", perInstanceColor: true, "property block (old)");

            if (keyboard.uKey.wasPressedThisFrame)
                Spawn(targetParts, "crossing_2x2", perInstanceColor: false, "palette, sparse mesh");

            if (keyboard.gKey.wasPressedThisFrame)
                Clear();
        }

        /// <summary>
        /// Builds a solid slab of stacked bricks. A dense block is the worst realistic case for
        /// culling - nothing can be frustum-rejected when the camera frames the whole build.
        /// </summary>
        public void Spawn(int count, string partId, bool perInstanceColor, string label)
        {
            Clear();

            PartCatalog catalog = factory.Catalog;
            PartDefinition def = catalog.parts.Find(p => p.id == partId) ?? catalog.Get(0);
            if (def == null)
                return;

            var timer = Stopwatch.StartNew();

            Vector2Int size = def.footprintSize;
            int perSide = Mathf.CeilToInt(Mathf.Sqrt(count / 4f));

            int placed = 0;
            for (int layer = 0; layer < 4 && placed < count; layer++)
            for (int y = 0; y < perSide && placed < count; y++)
            for (int x = 0; x < perSide && placed < count; x++)
            {
                var part = new PlacedPart(
                    def,
                    new GridCoord(x * size.x, y * size.y, layer),
                    0,
                    (byte)((x + y + layer) % Mathf.Max(1, catalog.palette.Length)));

                if (!controller.Map.Add(part))
                    continue;

                GameObject go = factory.Create(part, partRoot, withColliders, perInstanceColor);
                PlacedPartMarker.Attach(go, part);
                placed++;
            }

            timer.Stop();
            _spawned = placed;
            _spawnMs = timer.Elapsed.TotalMilliseconds;
            _mode = label;
            // GetIndexCount, not mesh.triangles: the render meshes are uploaded non-readable to save
            // heap on WebGL, so the CPU-side triangle array is not there to count.
            _triangles = def.mesh != null ? (long)(def.mesh.GetIndexCount(0) / 3) * placed : 0;

            Debug.Log($"[Stress] {label}: {placed} x {def.id} in {_spawnMs:0} ms, " +
                      $"{_triangles / 1e6:0.0} M triangles (colliders: {withColliders}).");
        }

        public void Clear()
        {
            foreach (PlacedPart part in new System.Collections.Generic.List<PlacedPart>(controller.Map.Parts))
            {
                if (part.Instance != null)
                    Destroy(part.Instance);
                controller.Map.Remove(part);
            }

            _spawned = 0;
            _spawnMs = 0;
        }
    }
}
