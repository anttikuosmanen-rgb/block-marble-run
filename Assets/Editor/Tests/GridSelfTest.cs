#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Exercises the placement rules headlessly. Grid logic is the one part of build mode that cannot
    /// be checked by looking at the scene - a wrong rotation or support rule shows up as a brick that
    /// merely feels off, so it needs assertions rather than eyes.
    /// </summary>
    public static class GridSelfTest
    {
        static int _passed;
        static int _failed;
        static StringBuilder _log;

        [MenuItem("Block Marble Run/Run Grid Self Test")]
        public static void Run()
        {
            _passed = 0;
            _failed = 0;
            _log = new StringBuilder();

            TestRotationMatchesTransform();
            TestOccupancyBlocks();
            TestGroundSupport();
            TestStudSupport();
            TestTrackRejectsStacking();
            TestMultiLayerOccupancy();
            TestRemoveFreesCells();

            string summary = $"[GridSelfTest] {_passed} passed, {_failed} failed.\n{_log}";
            if (_failed > 0)
                Debug.LogError(summary);
            else
                Debug.Log(summary);
        }

        // --- tests ---------------------------------------------------------------------------

        /// <summary>
        /// The grid cells a rotated part claims must match where its mesh actually ends up. If these
        /// drift apart, parts collide with things they do not visually touch.
        /// </summary>
        static void TestRotationMatchesTransform()
        {
            PartDefinition def = MakeDef("test_2x1", new Vector2Int(2, 1), layers: 1, studs: true);

            for (int rot = 0; rot < 4; rot++)
            {
                var part = new PlacedPart(def, new GridCoord(3, 5, 0), rot, 0);
                part.GetTransform(out Vector3 position, out Quaternion rotation);

                List<GridCoord> cells = part.OccupiedCells().ToList();

                // Centre of the claimed cells, in world space.
                var centre = Vector3.zero;
                foreach (GridCoord c in cells)
                    centre += new Vector3((c.x + 0.5f) * GridCoord.StudUnits, 0f, (c.y + 0.5f) * GridCoord.StudUnits);
                centre /= cells.Count;

                Check($"rot {rot}: transform sits at the centre of its cells",
                    Mathf.Abs(centre.x - position.x) < 1e-4f && Mathf.Abs(centre.z - position.z) < 1e-4f,
                    $"cells centre {centre}, transform {position}");

                Vector2Int size = part.RotatedSize;
                Check($"rot {rot}: footprint size swaps on quarter turns",
                    rot % 2 == 0 ? size == new Vector2Int(2, 1) : size == new Vector2Int(1, 2),
                    $"got {size}");
            }
        }

        static void TestOccupancyBlocks()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            var first = new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0);
            Check("first placement succeeds", map.Add(first));

            var overlapping = new PlacedPart(def, new GridCoord(1, 1, 0), 0, 0);
            Check("overlapping placement is blocked",
                map.CanPlace(overlapping) == PlacementResult.Blocked);

            var adjacent = new PlacedPart(def, new GridCoord(2, 0, 0), 0, 0);
            Check("adjacent placement is valid",
                map.CanPlace(adjacent) == PlacementResult.Valid);
        }

        static void TestGroundSupport()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            Check("ground layer is always supported",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0)) == PlacementResult.Valid);

            Check("floating part is unsupported",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, 3), 0, 0)) == PlacementResult.Unsupported);

            Check("below ground is blocked",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, -1), 0, 0)) == PlacementResult.Blocked);
        }

        static void TestStudSupport()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            map.Add(new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0));

            Check("stacking on studs is valid",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, 1), 0, 0)) == PlacementResult.Valid);

            Check("partial overlap onto studs is valid",
                map.CanPlace(new PlacedPart(def, new GridCoord(1, 1, 1), 0, 0)) == PlacementResult.Valid);

            Check("clear of the studs is unsupported",
                map.CanPlace(new PlacedPart(def, new GridCoord(5, 5, 1), 0, 0)) == PlacementResult.Unsupported);
        }

        /// <summary>Track pieces expose no studs, which is what stops anything stacking on them.</summary>
        static void TestTrackRejectsStacking()
        {
            var map = new GridMap();
            PartDefinition track = MakeDef("track_2x2", new Vector2Int(2, 2), layers: 1, studs: false);
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            map.Add(new PlacedPart(track, new GridCoord(0, 0, 0), 0, 0));

            Check("nothing stacks on a studless part",
                map.CanPlace(new PlacedPart(block, new GridCoord(0, 0, 1), 0, 0)) == PlacementResult.Unsupported);
        }

        static void TestMultiLayerOccupancy()
        {
            var map = new GridMap();
            PartDefinition slide = MakeDef("slide_2x2", new Vector2Int(2, 2), layers: 2, studs: false);
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            var placed = new PlacedPart(slide, new GridCoord(0, 0, 0), 0, 0);
            Check("two-layer part claims eight cells", placed.OccupiedCells().Count() == 8,
                $"got {placed.OccupiedCells().Count()}");

            map.Add(placed);

            Check("upper layer of a tall part blocks placement",
                map.CanPlace(new PlacedPart(block, new GridCoord(0, 0, 1), 0, 0)) == PlacementResult.Blocked);
        }

        static void TestRemoveFreesCells()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            var part = new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0);
            map.Add(part);
            Check("cells are occupied after add", map.CellCount == 4, $"got {map.CellCount}");

            map.Remove(part);
            Check("cells are freed after remove", map.CellCount == 0, $"got {map.CellCount}");
            Check("part list is empty after remove", map.Parts.Count == 0);
        }

        // --- helpers -------------------------------------------------------------------------

        static PartDefinition MakeDef(string id, Vector2Int size, int layers, bool studs)
        {
            var def = ScriptableObject.CreateInstance<PartDefinition>();
            def.id = id;
            def.displayName = id;
            def.footprintSize = size;
            def.heightLayers = layers;

            int count = size.x * size.y;
            def.footprintMask = Enumerable.Repeat(true, count).ToArray();
            def.bottomSockets = Enumerable.Repeat(true, count).ToArray();
            def.topStuds = Enumerable.Repeat(studs, count).ToArray();

            return def;
        }

        static void Check(string what, bool condition, string detail = null)
        {
            if (condition)
            {
                _passed++;
                return;
            }

            _failed++;
            _log.AppendLine($"  FAIL: {what}{(detail != null ? $" - {detail}" : "")}");
        }
    }
}
#endif
