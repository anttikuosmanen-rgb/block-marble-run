using System.Collections.Generic;
using System.IO;
using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Writes every part's studs and sockets as ASCII, for reading by eye.
    ///
    /// These masks are derived from geometry by rules that have been wrong more than once, and every
    /// time it was someone looking at a part and saying "there are no studs there" that caught it.
    /// A file that can be regenerated and diffed makes that check a minute's work rather than a
    /// question of loading each piece in turn.
    /// </summary>
    public static class PartMaskReport
    {
        const string Path = "PartMasks.txt";

        [MenuItem("Block Marble Run/Write Part Mask Report")]
        public static void Run()
        {
            var report = new StringBuilder();

            report.AppendLine("Studs and antistuds, as derived from each part's geometry.");
            report.AppendLine();
            report.AppendLine("  T  stud on top          o  antistud underneath");
            report.AppendLine("  B  both                 .  part of the piece, neither");
            report.AppendLine("  -  outside the piece's footprint   O  the hole a ball drops through");
            report.AppendLine();
            report.AppendLine("Rows run with +y upward, columns with +x rightward, as the part sits");
            report.AppendLine("unrotated. Grid layers are half a brick: a brick is 2, a plate 1.");
            report.AppendLine();

            var parts = new List<PartDefinition>();

            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def != null)
                    parts.Add(def);
            }

            parts.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

            foreach (PartDefinition def in parts)
                Write(report, def);

            File.WriteAllText(Path, report.ToString());
            Debug.Log($"[Masks] wrote {parts.Count} parts to {Path}");
        }

        /// <summary>Whether a cell's middle falls inside the part's drop hole.</summary>
        static bool InHole(PartDefinition def, int x, int y)
        {
            if (def.dropHoleRadiusUnits <= 0f)
                return false;

            Vector2 centre = new Vector2(def.footprintSize.x * 0.5f, def.footprintSize.y * 0.5f) *
                             GridCoord.StudUnits + def.dropHoleOffsetUnits;

            var cell = new Vector2((x + 0.5f) * GridCoord.StudUnits, (y + 0.5f) * GridCoord.StudUnits);

            return (cell - centre).magnitude < def.dropHoleRadiusUnits;
        }

        static void Write(StringBuilder report, PartDefinition def)
        {
            int w = def.footprintSize.x, h = def.footprintSize.y;

            report.AppendLine(new string('=', 60));
            report.AppendLine($"{def.id}");
            report.AppendLine($"  {w} x {h} studs, {Mathf.Max(1, def.heightLayers)} grid layers " +
                              $"({Mathf.Max(1, def.heightLayers) / 2f:0.#} bricks)" +
                              $"{(def.hasTunnel ? ", has a way through it" : "")}" +
                              $"{(def.ports is { Length: > 0 } ? $", {def.ports.Length} channel mouth(s)" : "")}" +
                              $"{(def.selectable ? "" : ", not offered in the palette")}");

            report.AppendLine();

            int studs = 0, sockets = 0;

            for (int y = h - 1; y >= 0; y--)
            {
                var row = new StringBuilder("    ");

                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;

                    bool inside = def.footprintMask == null || i >= def.footprintMask.Length || def.footprintMask[i];
                    bool stud = def.topStuds != null && i < def.topStuds.Length && def.topStuds[i];
                    bool socket = def.bottomSockets != null && i < def.bottomSockets.Length && def.bottomSockets[i];

                    if (stud) studs++;
                    if (socket) sockets++;

                    // The drop hole, drawn over everything else: a cell it covers cannot carry an
                    // antistud, so the two appearing together in the diagram is the bug this exists
                    // to catch. Marked from the stored circle rather than from the derivation, so
                    // what is checked by eye is what the guides will actually draw.
                    row.Append(InHole(def, x, y) ? 'O'
                        : !inside && !stud && !socket ? '-'
                        : stud && socket ? 'B'
                        : stud ? 'T'
                        : socket ? 'o'
                        : '.');

                    row.Append(' ');
                }

                report.AppendLine(row.ToString());
            }

            report.AppendLine();
            report.AppendLine($"    {studs} stud cell(s), {sockets} antistud cell(s)");
            report.AppendLine();
        }
    }
}
