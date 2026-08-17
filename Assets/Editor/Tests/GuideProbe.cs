using System.Collections.Generic;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>What the selection guides would draw for a small run, mouth by mouth.</summary>
    public static class GuideProbe
    {
        [MenuItem("Block Marble Run/Probe Guides")]
        public static void Run()
        {
            PartDefinition ramp = Find("slide_2x4");
            if (ramp == null) { Debug.Log("[Guide] slide_2x4 missing"); return; }

            var map = new GridMap();

            // Two ramps end to end, as a copied run would be.
            var a = new PlacedPart(ramp, new GridCoord(0, 0, 4), 0, 0);
            var b = new PlacedPart(ramp, new GridCoord(0, 4, 6), 0, 0);

            var group = new List<PlacedPart> { a, b };

            foreach (PlacedPart part in group)
            {
                Debug.Log($"[Guide] {part.Definition.id} at ({part.Origin.x},{part.Origin.y},{part.Origin.layer}) " +
                          $"size {part.RotatedSize}");

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    var cells = new List<Vector2Int>(MouthCells(port));
                    var detail = "";

                    foreach (Vector2Int cell in cells)
                    {
                        int under = part.UndersideLayerAt(cell.x, cell.y);
                        int top = part.TopLayerAt(cell.x, cell.y);
                        detail += $"  cell ({cell.x},{cell.y}) underside {under} top {top};";
                    }

                    Debug.Log($"[Guide]   mouth {port.Facing} midline {port.MidlineHalfStuds} " +
                              $"floorLayer {port.FloorLayer} height {port.HeightUnits:0.000}{detail}");
                }
            }
        }

        static IEnumerable<Vector2Int> MouthCells(PlacedPart.WorldPort port)
        {
            bool alongX = port.Facing is Facing.North or Facing.South;

            int centreAlong = (alongX ? port.MidlineHalfStuds.x : port.MidlineHalfStuds.y) / 2;
            int across = (alongX ? port.MidlineHalfStuds.y : port.MidlineHalfStuds.x) / 2;

            int width = Mathf.Max(1, port.WidthStuds);
            int alongMin = centreAlong - width / 2;
            int inside = port.Facing is Facing.North or Facing.East ? across - 1 : across;

            for (int i = 0; i < width; i++)
                yield return alongX
                    ? new Vector2Int(alongMin + i, inside)
                    : new Vector2Int(inside, alongMin + i);
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
