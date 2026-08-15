using System.Collections.Generic;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>What the solver offers when a funnel is brought up to an open channel end.</summary>
    public static class SnapProbe
    {
        [MenuItem("Block Marble Run/Probe Funnel Snap")]
        public static void Run()
        {
            PartDefinition track = Find("track_2x4");
            PartDefinition funnel = Find("funnel_6x7");
            PartDefinition brick = Find("building_block_2x2");

            if (track == null || funnel == null) { Debug.Log("[Snap] parts missing"); return; }

            // A run held up in the air, with one mouth open.
            var map = new GridMap();
            var run = new PlacedPart(track, new GridCoord(0, 0, 2), 0, 0);
            map.Add(run);

            foreach (PlacedPart.WorldPort port in run.WorldPorts())
                Debug.Log($"[Snap] run mouth: midline {port.MidlineHalfStuds} facing {port.Facing} " +
                          $"floor layer {port.FloorLayer} height {port.HeightUnits:0.000}");

            Debug.Log($"[Snap] funnel ports: {funnel.ports?.Length ?? 0}, " +
                      $"studs at layer {StudLayer(funnel)}, height {funnel.heightLayers}");

            // What the solver produces with the cursor beside the open mouth.
            if (PlacementSolver.NearestOpenMouth(map, new GridCoord(0, 5, 2).CellCentre, 3, out var target))
            {
                Debug.Log($"[Snap] nearest open mouth found facing {target.Facing}");

                List<PlacedPart> matings = PlacementSolver.MatingsWith(map, funnel, 0, target, true);
                Debug.Log($"[Snap] funnel mouth matings: {matings.Count}");

                List<PlacedPart> studs = PlacementSolver.StudMatingsWith(map, funnel, 0, target);
                Debug.Log($"[Snap] funnel stud matings: {studs.Count}");

                foreach (PlacedPart m in studs.GetRange(0, Mathf.Min(3, studs.Count)))
                    Debug.Log($"[Snap]    at {m.Origin.x},{m.Origin.y},{m.Origin.layer} rot {m.Rotation} " +
                              $"-> {map.CanPlace(m)}");

                List<PlacedPart> trackMatings = PlacementSolver.MatingsWith(map, track, 0, target, true);
                Debug.Log($"[Snap] track matings offered: {trackMatings.Count}");
            }
            else
            {
                Debug.Log("[Snap] no open mouth found near the cursor");
            }

            // And the other direction: a channel piece brought up to a funnel's shelf.
            var map2 = new GridMap();
            var f = new PlacedPart(funnel, new GridCoord(0, 0, 0), 0, 0);
            map2.Add(f);

            int studLayer = -1;
            var studCell = new Vector2Int(-1, -1);

            for (int y = 0; y < funnel.footprintSize.y; y++)
            for (int x = 0; x < funnel.footprintSize.x; x++)
                if (f.HasTopStudAt(x, y))
                {
                    studCell = new Vector2Int(x, y);
                    studLayer = f.TopLayerAt(x, y);
                }

            Debug.Log($"[Snap] funnel shelf at cell {studCell}, top layer {studLayer}");

            PlacedPart solved = PlacementSolver.Solve(map2, track, studCell.x, studCell.y, 0, 0);
            Debug.Log($"[Snap] track solved onto the shelf at layer {solved.Origin.layer} " +
                      $"(shelf is {studLayer}), result {map2.CanPlace(solved)}");
        }

        static int StudLayer(PartDefinition def)
        {
            var probe = new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0);

            for (int y = 0; y < def.footprintSize.y; y++)
            for (int x = 0; x < def.footprintSize.x; x++)
                if (probe.HasTopStudAt(x, y))
                    return probe.TopLayerAt(x, y);

            return -1;
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
