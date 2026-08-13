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

        [MenuItem("Block Marble Run/Report Parts")]
        public static void Run()
        {
            string[] paths = Directory.GetFiles(MeshFolder, "*.stl", SearchOption.AllDirectories);
            System.Array.Sort(paths);

            var sb = new StringBuilder();
            sb.AppendLine($"Analysed {paths.Length} parts from {MeshFolder}");
            sb.AppendLine();
            sb.AppendLine($"{"part",-24} {"size mm",-22} {"studs",-8} {"lay",-4} {"studs?",-7} {"mirror",-11} {"score",-6} cells");
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
                    $"{$"{a.SizeMm.x:0.0}x{a.SizeMm.y:0.0}x{a.SizeMm.z:0.0}",-22} " +
                    $"{$"{a.FootprintSize.x}x{a.FootprintSize.y}",-8} " +
                    $"{a.HeightLayers,-4} " +
                    $"{(a.HasTopStuds ? "yes" : "no"),-7} " +
                    $"{a.MirrorVerdict,-11} " +
                    $"{a.MirrorScore,-6:0.00} " +
                    $"{cells}/{a.FootprintSize.x * a.FootprintSize.y}");

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
