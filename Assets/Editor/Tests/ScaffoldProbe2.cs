using System.Collections.Generic;
using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Runs the scaffolder for real and prints what it did, for cases seen in the build.</summary>
    public static class ScaffoldProbe2
    {
        [MenuItem("Block Marble Run/Probe Scaffolding")]
        public static void Run()
        {
            var report = new StringBuilder();
            ScaffoldBuilder.Verbose = true;

            PartDefinition pillar = Find("building_block_2x2");

            foreach (string id in new[] { "funnel_6x7", "funnel_8x9" })
            {
                PartDefinition def = Find(id);
                if (def == null) { report.AppendLine($"{id}: missing"); continue; }

                for (int rot = 0; rot < 2; rot++)
                {
                    var map = new GridMap();
                    var part = new PlacedPart(def, new GridCoord(0, 0, 2), rot, 0);
                    List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, part, pillar);
                    map.Add(part);

                    report.AppendLine($"### {id} rot {rot} at layer 2 -> {supports.Count} bricks");
                    report.AppendLine(ScaffoldBuilder.Report);
                }
            }

            // --- lifting an existing support ---
            {
                PartDefinition slide = Find("slide_2x4");
                var map = new GridMap();

                var brick = new PlacedPart(pillar, new GridCoord(0, 0, 0), 0, 0);
                var track = new PlacedPart(slide, new GridCoord(0, 0, 1), 0, 0);

                report.AppendLine($"### lift: seeded brick {map.Add(brick)}, track {map.Add(track)}");

                var all = new List<PlacedPart>(map.Parts);
                List<PlacedPart> moved = Assembly.Shift(map, all, 1);

                if (moved == null)
                {
                    report.AppendLine("  Shift returned null");
                }
                else
                {
                    foreach (PlacedPart p in all) map.Remove(p);
                    foreach (PlacedPart p in moved) map.Add(p);

                    foreach (PlacedPart p in moved)
                        report.AppendLine($"  moved {p.Definition.id} to layer {p.Origin.layer}  " +
                                          $"hasPorts {p.HasPorts}  supported {map.IsSupported(p)}");

                    List<PlacedPart> grown = ScaffoldBuilder.ExtendLiftedColumns(map, moved, pillar);
                    report.AppendLine($"  ExtendLiftedColumns added {grown.Count}");

                    foreach (PlacedPart p in grown)
                        report.AppendLine($"    brick at ({p.Origin.x},{p.Origin.y},{p.Origin.layer})");
                }
            }

            Debug.Log(report.ToString());
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
