using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>What the solver does with the rotation it is handed, on an empty map.</summary>
    public static class RotationProbe
    {
        [MenuItem("Block Marble Run/Probe Rotation")]
        public static void Run()
        {
            var report = new StringBuilder();

            foreach (string id in new[] { "building_block_2x6", "building_block_2x2", "slide_2x4", "u_turn" })
            {
                PartDefinition def = Find(id);
                if (def == null) { report.AppendLine($"{id}: missing"); continue; }

                report.AppendLine($"=== {id}  ports {(def.ports?.Length ?? 0)}  mode {def.rotation}");

                for (int rot = 0; rot < 4; rot++)
                {
                    var map = new GridMap();
                    PlacedPart solved = PlacementSolver.Solve(map, def, 0, 0, rot, 0);

                    report.AppendLine($"  asked {rot} -> got {solved.Rotation} " +
                                      $"at ({solved.Origin.x},{solved.Origin.y},{solved.Origin.layer}) " +
                                      $"size {solved.RotatedSize}");
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
