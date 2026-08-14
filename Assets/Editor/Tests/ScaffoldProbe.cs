using System.Collections.Generic;
using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Dumps what the scaffolder sees for one part, at every rotation.
    ///
    /// Three fixes for the slide curve's missing support were reasoned out from screenshots and all
    /// three were wrong. The occupancy mask and the port heights are in the asset, so the answer can
    /// be read directly rather than inferred from a picture of the result.
    /// </summary>
    public static class ScaffoldProbe
    {
        [MenuItem("Block Marble Run/Probe Part Geometry")]
        public static void Run()
        {
            var report = new StringBuilder();

            foreach (string id in new[] { "slide_curve_4x4", "slide_curve_4x4_mirror" })
            {
                PartDefinition def = Find(id);
                if (def == null) { report.AppendLine($"{id}: not found"); continue; }

                report.AppendLine($"=== {id}  footprint {def.footprintSize}");

                for (int r = 0; r < 4; r++)
                {
                    var part = new PlacedPart(def, new GridCoord(0, 0, 1), r, 0);
                    report.AppendLine($"-- rotation {r}  size {part.RotatedSize}");

                    var byColumn = new Dictionary<Vector2Int, List<int>>();
                    foreach (GridCoord cell in part.OccupiedCells())
                    {
                        var key = new Vector2Int(cell.x, cell.y);
                        if (!byColumn.TryGetValue(key, out List<int> layers))
                            byColumn[key] = layers = new List<int>();

                        layers.Add(cell.layer);
                    }

                    // Occupancy as a grid of "lowest layer" per column, which is what a pillar carries.
                    for (int y = part.RotatedSize.y - 1; y >= 0; y--)
                    {
                        var row = new StringBuilder("     ");
                        for (int x = 0; x < part.RotatedSize.x; x++)
                        {
                            byColumn.TryGetValue(new Vector2Int(x, y), out List<int> layers);
                            row.Append(layers == null ? " . " : $" {Lowest(layers)}{Highest(layers)}");
                        }

                        report.AppendLine($"{row}   y={y}");
                    }

                    if (r == 0)
                    {
                        report.AppendLine("     bottom sockets:");
                        for (int y = def.footprintSize.y - 1; y >= 0; y--)
                        {
                            var row = new StringBuilder("       ");
                            for (int x = 0; x < def.footprintSize.x; x++)
                            {
                                int i = y * def.footprintSize.x + x;
                                bool socket = def.bottomSockets != null &&
                                              i < def.bottomSockets.Length && def.bottomSockets[i];

                                bool solid = def.footprintMask == null ||
                                             (i < def.footprintMask.Length && def.footprintMask[i]);

                                row.Append(socket ? " S" : solid ? " o" : " .");
                            }

                            report.AppendLine(row.ToString());
                        }
                    }

                    foreach (PlacedPart.WorldPort port in part.WorldPorts())
                        report.AppendLine($"     port {port.Facing} midline {port.MidlineHalfStuds} " +
                                          $"floorLayer {port.FloorLayer} width {port.WidthStuds}");
                }
            }

            Debug.Log(report.ToString());
        }

        static int Lowest(List<int> layers)
        {
            int lowest = int.MaxValue;
            foreach (int layer in layers) lowest = Mathf.Min(lowest, layer);
            return lowest;
        }

        static int Highest(List<int> layers)
        {
            int highest = int.MinValue;
            foreach (int layer in layers) highest = Mathf.Max(highest, layer);
            return highest;
        }

        static PartDefinition Find(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def != null && def.id == id)
                    return def;
            }

            return null;
        }
    }
}
