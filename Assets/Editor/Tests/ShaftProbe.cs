#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using BlockMarbleRun.EditorTools.Import;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Looks at what the drop-hole detector actually found, straight from the mesh: how much material
    /// stands over the region it picked, and at what heights.
    ///
    /// A funnel's throat should have nothing over it at any height. A U-turn's channel should have a
    /// floor under it and walls beside it, and the question is why the detector disagrees.
    /// </summary>
    public static class ShaftProbe
    {
        [MenuItem("Block Marble Run/Probe Shafts")]
        public static void Run()
        {
            var sb = new StringBuilder();

            foreach (string name in new[] { "funnel_6x7", "u_turn", "u_turn_slide", "curve_4x4" })
            {
                string path = $"Assets/Art/Meshes/{name}.stl";
                PartAnalysis a = PartAnalysis.Analyse(path);
                List<StlFacet> facets = StlFile.Read(path);

                sb.AppendLine($"== {name}: hole r={a.DropHoleRadiusMm:0.#} mm at " +
                              $"({a.DropHoleCentreMm.x:0.#}, {a.DropHoleCentreMm.y:0.#}) mm; " +
                              $"bounds x {a.MinMm.x:0.#}..{a.MinMm.x + a.SizeMm.x:0.#}, " +
                              $"y {a.MinMm.y:0.#}..{a.MinMm.y + a.SizeMm.y:0.#}, " +
                              $"z {a.MinMm.z:0.#}..{a.MinMm.z + a.SizeMm.z:0.#}");

                sb.AppendLine($"   extent {a.DropHoleExtentMm.x:0.#} x {a.DropHoleExtentMm.y:0.#} mm, " +
                              $"fill {a.DropHoleFill:0.00}, " +
                              $"aspect {Mathf.Max(a.DropHoleExtentMm.x, a.DropHoleExtentMm.y) / Mathf.Max(0.001f, Mathf.Min(a.DropHoleExtentMm.x, a.DropHoleExtentMm.y)):0.00}");

                if (a.DropHoleRadiusMm <= 0f)
                    continue;

                // Every surface directly over the middle of what was found. Nothing at all means a
                // genuine shaft; a floor at channel height means the detector was fooled.
                var heights = new List<float>();

                foreach (StlFacet f in facets)
                    if (CoversPoint(f, a.DropHoleCentreMm.x, a.DropHoleCentreMm.y, out float z))
                        heights.Add(z);

                heights.Sort();

                sb.Append($"   surfaces over the centre: {heights.Count}");

                foreach (float z in heights)
                    sb.Append($"  {z - a.MinMm.z:0.#}");

                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        }

        static bool CoversPoint(StlFacet f, float x, float y, out float z)
        {
            z = 0f;

            float d = (f.B.y - f.C.y) * (f.A.x - f.C.x) + (f.C.x - f.B.x) * (f.A.y - f.C.y);
            if (Mathf.Abs(d) < 1e-9f)
                return false;

            float u = ((f.B.y - f.C.y) * (x - f.C.x) + (f.C.x - f.B.x) * (y - f.C.y)) / d;
            float v = ((f.C.y - f.A.y) * (x - f.C.x) + (f.A.x - f.C.x) * (y - f.C.y)) / d;
            float w = 1f - u - v;

            if (u < 0f || v < 0f || w < 0f)
                return false;

            z = u * f.A.z + v * f.B.z + w * f.C.z;
            return true;
        }
    }
}
#endif
