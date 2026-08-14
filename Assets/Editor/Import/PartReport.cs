#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Runs <see cref="PartAnalysis"/> over every source STL and prints one table. This is the
    /// review step DESIGN.md §3.4 calls for - the point at which a human looks at the chirality
    /// verdicts and confirms them, rather than the pipeline silently acting on a guess.
    /// </summary>
    public static class PartReport
    {
        const string MeshFolder = "Assets/Art/Meshes";

        static string DescribePorts(PartAnalysis a)
        {
            if (a.Ports.Count == 0)
                return "-";

            var described = new List<string>(a.Ports.Count);
            foreach (BlockMarbleRun.Parts.TrackPort port in a.Ports)
                described.Add($"{port.facing.ToString()[0]}[{port.midlineHalfStuds.x},{port.midlineHalfStuds.y}]w{port.widthStuds}@{port.heightMm:0.#}");

            return string.Join(" ", described);
        }

        [MenuItem("Block Marble Run/Report Parts")]
        public static void Run()
        {
            string[] paths = Directory.GetFiles(MeshFolder, "*.stl", SearchOption.AllDirectories);
            System.Array.Sort(paths);

            var sb = new StringBuilder();
            sb.AppendLine($"Analysed {paths.Length} parts from {MeshFolder}");
            sb.AppendLine();
            sb.AppendLine($"{"part",-24} {"studs",-8} {"lay",-4} {"top",-5} {"mirror",-11} {"score",-6} ports");
            sb.AppendLine(new string('-', 110));

            var warnings = new List<string>();
            var mesh = new Dictionary<MirrorVerdict, int>();

            foreach (string path in paths)
            {
                PartAnalysis a = PartAnalysis.Analyse(path);
                string name = Path.GetFileNameWithoutExtension(path);

                int cells = 0;
                if (a.FootprintMask != null)
                    foreach (bool b in a.FootprintMask)
                        if (b) cells++;

                sb.AppendLine(
                    $"{name,-24} " +
                    $"{$"{a.FootprintSize.x}x{a.FootprintSize.y}",-8} " +
                    $"{a.HeightLayers,-4} " +
                    $"{(a.HasTopStuds ? "yes" : "no"),-5} " +
                    $"{a.MirrorVerdict,-11} " +
                    $"{a.MirrorScore,-6:0.00} " +
                    $"{DescribePorts(a)}");

                mesh.TryGetValue(a.MirrorVerdict, out int n);
                mesh[a.MirrorVerdict] = n + 1;

                foreach (string w in a.Warnings)
                    warnings.Add($"  {name}: {w}");
            }

            sb.AppendLine();
            foreach (KeyValuePair<MirrorVerdict, int> kv in mesh)
                sb.AppendLine($"{kv.Key}: {kv.Value}");

            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (string w in warnings)
                    sb.AppendLine(w);
            }

            Debug.Log(sb.ToString());
        }
    }
}
#endif
