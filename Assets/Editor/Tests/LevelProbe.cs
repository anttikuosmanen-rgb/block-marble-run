using System.Collections.Generic;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Two pillars of different heights, and what levels a long piece is offered over them.</summary>
    public static class LevelProbe
    {
        [MenuItem("Block Marble Run/Probe Levels")]
        public static void Run()
        {
            PartDefinition shortPillar = Find("pillar_2x2x7");
            PartDefinition tallPillar = Find("pillar_2x2x10");
            PartDefinition piece = Find("building_block_2x6");

            if (shortPillar == null || tallPillar == null || piece == null)
            {
                Debug.Log("[Levels] parts missing");
                return;
            }

            var map = new GridMap();

            var a = new PlacedPart(shortPillar, new GridCoord(0, 0, 0), 0, 0);
            var b = new PlacedPart(tallPillar, new GridCoord(2, 0, 0), 0, 0);

            Debug.Log($"[Levels] short pillar {shortPillar.heightLayers} layers, added {map.Add(a)}");
            Debug.Log($"[Levels] tall pillar {tallPillar.heightLayers} layers, added {map.Add(b)}");
            Debug.Log($"[Levels] short top {a.TopLayerAt(0, 0)}, tall top {b.TopLayerAt(2, 0)}");

            // The long piece lying across both, as it would be while slid over them.
            const int rotation = 1;

            for (int x = 0; x <= 2; x++)
            {
                List<int> levels = PlacementSolver.LevelsAt(map, piece, x, 0, rotation, 0);
                Debug.Log($"[Levels] at x={x}: {(levels.Count == 0 ? "none" : string.Join(",", levels))}");

                foreach (int layer in new[] { a.TopLayerAt(0, 0), b.TopLayerAt(2, 0) })
                {
                    var candidate = new PlacedPart(piece, new GridCoord(x, 0, layer), rotation, 0);
                    Debug.Log($"[Levels]    layer {layer}: {map.CanPlace(candidate)}");
                }
            }
        }

        static PartDefinition Find(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var d = AssetDatabase.LoadAssetAtPath<PartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (d != null && d.id == id) return d;
            }
            return null;
        }
    }
}
