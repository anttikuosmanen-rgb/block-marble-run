#if UNITY_EDITOR
using System.Text;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Prints per-layer occupancy for the funnels, to see whether the drop hole is already in the
    /// data or has to be derived from the mesh.
    /// </summary>
    public static class HoleProbe
    {
        [MenuItem("Block Marble Run/Probe Funnel Holes")]
        public static void Run()
        {
            var sb = new StringBuilder();

            foreach (string id in new[] { "funnel_6x7", "funnel_8x9", "funnel_10x10", "u_turn" })
            {
                PartDefinition def = Load(id);
                if (def == null)
                {
                    sb.AppendLine($"{id}: not found");
                    continue;
                }

                sb.AppendLine($"== {id}  {def.footprintSize.x}x{def.footprintSize.y}, " +
                              $"{def.heightLayers} layers, masks {def.layerMasks?.Length ?? 0}");
                sb.AppendLine($"   hole r={def.dropHoleRadiusUnits * 100f:0.#} mm at " +
                              $"({def.dropHoleOffsetUnits.x * 100f:0.#}, " +
                              $"{def.dropHoleOffsetUnits.y * 100f:0.#}) mm from the pivot");
                sb.AppendLine($"   antistuds: {Count(def.bottomSockets)}");

                // Where the hole lands over the footprint, so a false one can be seen for what it is.
                if (def.dropHoleRadiusUnits > 0f)
                {
                    sb.AppendLine("  hole over the footprint (O = inside the circle):");

                    // Footprint space, which is what the offset is relative to: a placed part draws
                    // the hole at footprintCentre + rotation * offset.
                    var centre = new Vector2(def.footprintSize.x * 0.5f * 0.16f,
                                             def.footprintSize.y * 0.5f * 0.16f) +
                                 def.dropHoleOffsetUnits;

                    for (int y = def.footprintSize.y - 1; y >= 0; y--)
                    {
                        sb.Append("    ");
                        for (int x = 0; x < def.footprintSize.x; x++)
                        {
                            var cell = new Vector2((x + 0.5f) * 0.16f, (y + 0.5f) * 0.16f);
                            bool inside = (cell - centre).magnitude < def.dropHoleRadiusUnits;
                            sb.Append(inside ? "O" : def.OccupiesCell(x, y) ? "#" : ".").Append(' ');
                        }

                        sb.AppendLine();
                    }
                }

                for (int layer = 0; layer < def.heightLayers; layer++)
                {
                    sb.AppendLine($"  layer {layer}:");
                    for (int y = def.footprintSize.y - 1; y >= 0; y--)
                    {
                        sb.Append("    ");
                        for (int x = 0; x < def.footprintSize.x; x++)
                            sb.Append(def.OccupiesCell(x, y, layer) ? "#" : ".").Append(' ');

                        sb.AppendLine();
                    }
                }
            }

            Debug.Log(sb.ToString());
        }

        static int Count(bool[] mask)
        {
            if (mask == null)
                return 0;

            int n = 0;
            foreach (bool set in mask)
                if (set) n++;

            return n;
        }

        static PartDefinition Load(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:PartDefinition {id}"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (def != null && def.id == id)
                    return def;
            }

            return null;
        }
    }
}
#endif
