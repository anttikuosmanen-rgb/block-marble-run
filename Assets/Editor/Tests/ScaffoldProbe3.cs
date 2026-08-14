using System.Collections.Generic;
using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Walks the underground-placement path headlessly, part by part.</summary>
    public static class ScaffoldProbe3
    {
        [MenuItem("Block Marble Run/Probe Underground Placement")]
        public static void Run()
        {
            var report = new StringBuilder();
            PartDefinition pillar = Find("building_block_2x2");

            foreach (string id in new[] { "slide_2x2", "slide_2x4", "slide_curve_4x4", "u_turn" })
            {
                PartDefinition def = Find(id);
                if (def == null) { report.AppendLine($"{id}: missing"); continue; }

                // A seeded run on the ground, then the same part offered underground against its mouth.
                report.AppendLine($"### {id}");

                for (int seedLayer = 0; seedLayer <= 2; seedLayer++)
                ProbeSeed(report, def, pillar, seedLayer);
            }

            Debug.Log(report.ToString());
        }

        static void ProbeSeed(StringBuilder report, PartDefinition def, PartDefinition pillar, int seedLayer)
        {
            {
                var map = new GridMap();
                var seed = new PlacedPart(def, new GridCoord(0, 0, seedLayer), 0, 0);

                List<PlacedPart> seeded = ScaffoldBuilder.BuildSupports(map, seed, pillar);
                if (!map.Add(seed)) { report.AppendLine($"  seed at {seedLayer} refused"); return; }

                report.AppendLine($"  -- seed layer {seedLayer}, {seeded.Count} support bricks, " +
                                  $"{map.Parts.Count} parts");

                foreach (PlacedPart.WorldPort target in seed.WorldPorts())
                {
                    List<PlacedPart> matings = PlacementSolver.MatingsWith(map, def, 0, target,
                        allowBelowGround: true);

                    var layers_ = new List<int>();
                    foreach (PlacedPart m in matings) layers_.Add(m.Origin.layer);
                    report.AppendLine($"    mouth {target.Facing}: {matings.Count} matings at layers " +
                                      string.Join(",", layers_));

                    foreach (PlacedPart candidate in matings)
                    {
                        if (candidate.Origin.layer >= 0)
                            continue;

                        report.AppendLine($"  underground candidate at layer {candidate.Origin.layer}");

                        PlacedPart joined = null;
                        foreach (PlacedPart.WorldPort port in candidate.WorldPorts())
                        {
                            PlacedPart other = map.FindConnection(candidate, port);
                            if (other != null) { joined = other; break; }
                        }

                        report.AppendLine($"    joined: {(joined == null ? "NONE - falls back to all parts" : joined.Definition.id)}");

                        List<PlacedPart> group = joined != null
                            ? Assembly.Connected(map, joined)
                            : new List<PlacedPart>(map.Parts);

                        report.AppendLine($"    group {group.Count} of {map.Parts.Count}");

                        int layers = -candidate.Origin.layer;
                        List<PlacedPart> moved = Assembly.Shift(map, group, layers);

                        report.AppendLine($"    Shift by {layers}: {(moved == null ? "NULL - refused" : moved.Count + " moved")}");

                        if (moved == null)
                            continue;

                        var raised = new PlacedPart(candidate.Definition,
                            new GridCoord(candidate.Origin.x, candidate.Origin.y, candidate.Origin.layer + layers),
                            candidate.Rotation, 0);

                        // The map as the command will leave it, then the placement tested against it.
                        foreach (PlacedPart p in group) map.Remove(p);
                        foreach (PlacedPart p in moved) map.Add(p);

                        report.AppendLine($"    raised CanPlace: {map.CanPlace(raised)}");

                        foreach (PlacedPart p in moved) map.Remove(p);
                        foreach (PlacedPart p in group) map.Add(p);
                    }
                }
            }
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
