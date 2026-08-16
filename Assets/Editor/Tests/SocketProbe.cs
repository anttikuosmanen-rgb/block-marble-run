using System.Collections.Generic;
using BlockMarbleRun.EditorTools.Import;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>Re-runs the underside reading for one part and reports it cell by cell.</summary>
    public static class SocketProbe
    {
        [MenuItem("Block Marble Run/Probe Sockets")]
        public static void Run()
        {
            string path = "Assets/Art/Meshes/slide_curve_4x4.stl";
            List<StlFacet> facets = StlFile.Read(path);

            Vector3 min = facets[0].A, max = facets[0].A;

            foreach (StlFacet f in facets)
                foreach (Vector3 v in new[] { f.A, f.B, f.C })
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }

            Debug.Log($"[Socket] bounds {min} .. {max}");

            const float res = 1f;
            int w = Mathf.CeilToInt((max.x - min.x) / res) + 1;
            int h = Mathf.CeilToInt((max.y - min.y) / res) + 1;

            var floor = new float[w * h];
            for (int i = 0; i < floor.Length; i++)
                floor[i] = float.PositiveInfinity;

            foreach (StlFacet f in facets)
            {
                int i0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(f.A.x, Mathf.Min(f.B.x, f.C.x)) - min.x) / res), 0, w - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(f.A.x, Mathf.Max(f.B.x, f.C.x)) - min.x) / res), 0, w - 1);
                int j0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(f.A.y, Mathf.Min(f.B.y, f.C.y)) - min.y) / res), 0, h - 1);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(f.A.y, Mathf.Max(f.B.y, f.C.y)) - min.y) / res), 0, h - 1);

                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    float x = min.x + i * res, y = min.y + j * res;

                    float x1 = f.A.x, y1 = f.A.y, x2 = f.B.x, y2 = f.B.y, x3 = f.C.x, y3 = f.C.y;
                    float area = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
                    if (Mathf.Abs(area) < 1e-9f) continue;

                    float a = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / area;
                    float b = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / area;
                    float c = 1f - a - b;
                    if (a < -1e-4f || b < -1e-4f || c < -1e-4f) continue;

                    float z = a * f.A.z + b * f.B.z + c * f.C.z;
                    int index = j * w + i;
                    if (z < floor[index]) floor[index] = z;
                }
            }

            for (int cy = 3; cy >= 0; cy--)
            {
                var line = $"  y={cy}  ";

                for (int cx = 0; cx < 4; cx++)
                {
                    var byLayer = new Dictionary<int, int>();
                    int covered = 0;

                    for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        float z = floor[j * w + i];
                        if (float.IsPositiveInfinity(z)) continue;

                        float x = min.x + i * res, y = min.y + j * res;
                        if (Mathf.FloorToInt((x - min.x) / 16f) != cx) continue;
                        if (Mathf.FloorToInt((y - min.y) / 16f) != cy) continue;

                        covered++;

                        float above = z - min.z;
                        int layer = Mathf.RoundToInt(above / 9.6f);
                        if (layer >= 0 && Mathf.Abs(above - layer * 9.6f) <= 0.5f)
                            byLayer[layer] = byLayer.TryGetValue(layer, out int n) ? n + 1 : 1;
                    }

                    int best = 0, bestLayer = -1;
                    foreach (KeyValuePair<int, int> at in byLayer)
                        if (at.Value > best) { best = at.Value; bestLayer = at.Key; }

                    line += covered == 0
                        ? "    --     "
                        : $" L{bestLayer}:{(covered > 0 ? 100f * best / covered : 0f):0}%/{covered,-4}";
                }

                Debug.Log("[Socket]" + line);
            }
        }
    }
}
