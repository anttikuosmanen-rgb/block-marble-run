#if UNITY_EDITOR
using System.IO;
using System.Text;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Prints each part's footprint mask beside its channel mouths, drawn on the same grid. Port
    /// coordinates alone cannot show whether a mouth sits where the geometry actually is; overlaying
    /// them does.
    /// </summary>
    public static class PortDiagnostic
    {
        const string MeshFolder = "Assets/Art/Meshes";

        [MenuItem("Block Marble Run/Diagnose Ports")]
        public static void Run()
        {
            var sb = new StringBuilder();

            foreach (string name in new[]
                     {
                         "track_2x2", "curve_4x4", "slide_2x4", "slide_curve_4x4", "u_turn", "u_turn_slide",
                     })
            {
                string path = $"{MeshFolder}/{name}.stl";
                if (!File.Exists(path))
                    continue;

                PartAnalysis a = PartAnalysis.Analyse(path);

                sb.AppendLine($"=== {name}  footprint {a.FootprintSize.x}x{a.FootprintSize.y}  " +
                              $"layers {a.HeightLayers}  meshSize {a.SizeMm.x:0.0}x{a.SizeMm.y:0.0}  " +
                              $"meshMin {a.MinMm.x:0.0},{a.MinMm.y:0.0}");

                // Mask drawn with +Y upward, so it reads like the part seen from above.
                for (int y = a.FootprintSize.y - 1; y >= 0; y--)
                {
                    var row = new StringBuilder("   ");
                    for (int x = 0; x < a.FootprintSize.x; x++)
                        row.Append(a.FootprintMask[y * a.FootprintSize.x + x] ? '#' : '.');

                    sb.AppendLine(row.ToString());
                }

                foreach (TrackPort p in a.Ports)
                {
                    // Convert the half-stud midline back to studs to show which cells it spans.
                    bool alongX = p.facing is Facing.North or Facing.South;
                    float centreStuds = (alongX ? p.midlineHalfStuds.x : p.midlineHalfStuds.y) / 2f;
                    float edgeStuds = (alongX ? p.midlineHalfStuds.y : p.midlineHalfStuds.x) / 2f;

                    sb.AppendLine($"   port {p.facing,-5} midline[{p.midlineHalfStuds.x},{p.midlineHalfStuds.y}] " +
                                  $"= centre {centreStuds:0.#} studs on edge {edgeStuds:0.#}  " +
                                  $"width {p.widthStuds}  height {p.heightMm:0.#}mm " +
                                  $"(layer {Mathf.FloorToInt(p.heightMm / PartAnalysis.LayerHeightMm)})");
                }

                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        }
    }
}
#endif
