#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlockMarbleRun.EditorTools.Import;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Measures the height of the channel floor at every mouth, straight from the mesh.
    ///
    /// The derivation snaps a mouth onto the nearest layer boundary plus 6.4 mm, so the stored height
    /// cannot show a part whose channel is a millimetre out - the snap hides exactly the error worth
    /// finding. This reads the geometry itself: the top surface a few millimetres inside the mouth,
    /// sampled across its width, which is the surface a ball actually rolls on.
    /// </summary>
    public static class ChannelFloorProbe
    {
        const string MeshFolder = "Assets/Art/Meshes";

        /// <summary>How far inside the boundary to sample, clear of the chamfer at the mouth's lip.</summary>
        const float InsetMm = 5f;

        [MenuItem("Block Marble Run/Probe Channel Floors")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Channel floor height at each mouth, measured from the mesh.");
            sb.AppendLine("ideal = 6.4 + k*19.2 above the part's base; drift = measured - ideal.");
            sb.AppendLine();

            foreach (string path in Directory.GetFiles(MeshFolder, "*.stl"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                PartAnalysis a = PartAnalysis.Analyse(path);

                if (a.Ports.Count == 0)
                    continue;

                List<StlFacet> facets = StlFile.Read(path);

                for (int i = 0; i < a.Ports.Count; i++)
                {
                    TrackPort port = a.Ports[i];

                    float measured = FloorAt(a, facets, port);

                    if (float.IsNaN(measured))
                    {
                        sb.AppendLine($"{name,-20} mouth {i} {port.facing,-5}  no surface found");
                        continue;
                    }

                    float above = measured - a.MinMm.z;

                    // Nearest of the heights a Duplo channel floor is allowed to sit at.
                    int k = Mathf.RoundToInt((above - PartAnalysis.ChannelFloorMm) / PartAnalysis.BrickPitchMm);
                    float ideal = PartAnalysis.ChannelFloorMm + k * PartAnalysis.BrickPitchMm;

                    sb.AppendLine(
                        $"{name,-20} mouth {i} {port.facing,-5} " +
                        $"measured {above,6:0.00}   stored {port.heightMm,6:0.00}   " +
                        $"ideal {ideal,6:0.00}   drift {above - ideal,6:+0.00;-0.00}");
                }
            }

            // The funnels derive no ports at all, so there is nothing above to measure. Their outlets
            // are found the hard way instead: the top surface a little inside each boundary, one
            // reading per stud, which shows where an edge dips and how far down it goes.
            sb.AppendLine();
            sb.AppendLine("Edge profiles - top surface 3 mm inside each boundary, per stud:");

            foreach (string name in new[] { "funnel_6x7", "funnel_8x9", "funnel_10x10", "track_2x2" })
            {
                string path = $"{MeshFolder}/{name}.stl";
                if (!File.Exists(path))
                    continue;

                PartAnalysis a = PartAnalysis.Analyse(path);
                List<StlFacet> facets = StlFile.Read(path);

                sb.AppendLine($"== {name}  {a.FootprintSize.x}x{a.FootprintSize.y}, " +
                              $"height {a.SizeMm.z:0.0} mm, ports {a.Ports.Count}");

                foreach (Facing facing in new[] { Facing.West, Facing.East, Facing.South, Facing.North })
                {
                    bool alongX = facing is Facing.North or Facing.South;
                    int count = alongX ? a.FootprintSize.x : a.FootprintSize.y;

                    sb.Append($"   {facing,-5}");

                    for (int i = 0; i < count; i++)
                    {
                        float along = (i + 0.5f) * PartAnalysis.StudPitchMm;

                        float x = facing == Facing.West ? 3f
                            : facing == Facing.East ? a.SizeMm.x - 3f
                            : along;

                        float y = facing == Facing.South ? 3f
                            : facing == Facing.North ? a.SizeMm.y - 3f
                            : along;

                        float top = float.NaN;

                        foreach (StlFacet f in facets)
                            if (Covers(f, a.MinMm.x + x, a.MinMm.y + y, out float z) &&
                                (float.IsNaN(top) || z > top))
                                top = z;

                        sb.Append(float.IsNaN(top) ? "     -" : $"{top - a.MinMm.z,6:0.0}");
                    }

                    sb.AppendLine();
                }
            }

            // Walking inward along an outlet's centre line, which is the only way to see where the
            // lip stops and the floor the ball actually runs on begins.
            sb.AppendLine();
            sb.AppendLine("Inward profile along each outlet's centre line, 1 mm steps from the edge:");

            foreach ((string name, Facing facing) in new[]
                     {
                         ("track_2x2", Facing.North), ("track_2x4", Facing.North),
                         ("funnel_6x7", Facing.North), ("funnel_8x9", Facing.North),
                         ("funnel_10x10", Facing.North),
                     })
            {
                string path = $"{MeshFolder}/{name}.stl";
                if (!File.Exists(path))
                    continue;

                PartAnalysis a = PartAnalysis.Analyse(path);
                List<StlFacet> facets = StlFile.Read(path);

                // The lowest stud along that edge is the outlet: on a funnel the rest of the edge is
                // bowl wall, and on a track piece the whole edge is the mouth.
                int count = a.FootprintSize.x;
                float bestAlong = 0f;
                float bestTop = float.MaxValue;

                for (int i = 0; i < count; i++)
                {
                    float along = (i + 0.5f) * PartAnalysis.StudPitchMm;
                    float top = TopAt(facets, a.MinMm.x + along, a.MinMm.y + a.SizeMm.y - 3f);

                    if (!float.IsNaN(top) && top < bestTop)
                    {
                        bestTop = top;
                        bestAlong = along;
                    }
                }

                sb.Append($"   {name,-14} {facing}: ");

                for (float inset = 2f; inset <= 24f; inset += 2f)
                {
                    float top = TopAt(facets, a.MinMm.x + bestAlong, a.MinMm.y + a.SizeMm.y - inset);
                    sb.Append(float.IsNaN(top) ? "     -" : $"{top - a.MinMm.z,6:0.0}");
                }

                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("Outlet floor, lowest across the channel's width 3-8 mm inside the edge:");

            foreach ((string name, Facing facing, float alongMm) in new[]
                     {
                         ("track_2x2", Facing.North, 16f), ("track_2x4", Facing.North, 16f),
                         ("track_2x8", Facing.North, 16f), ("crossing_2x2", Facing.North, 16f),
                         ("slide_2x4", Facing.South, 16f), ("u_turn", Facing.West, 16f),
                         ("funnel_6x7", Facing.North, 64f), ("funnel_8x9", Facing.North, 96f),
                         ("funnel_10x10", Facing.North, 128f),
                     })
            {
                string path = $"{MeshFolder}/{name}.stl";
                if (!File.Exists(path))
                    continue;

                PartAnalysis a = PartAnalysis.Analyse(path);
                List<StlFacet> facets = StlFile.Read(path);

                float floor = OutletFloor(a, facets, alongMm, facing) - a.MinMm.z;

                sb.AppendLine($"   {name,-14} {facing,-5} floor {floor,6:0.00} mm   " +
                              $"against 6.40: {floor - 6.4f,+6:0.00}");
            }

            sb.AppendLine();
            sb.AppendLine("Across the boundary at each derived mouth (mm inside the edge -> top surface):");

            foreach (string name in new[] { "funnel_6x7", "funnel_10x10", "track_2x2" })
            {
                string path = $"{MeshFolder}/{name}.stl";
                if (!File.Exists(path))
                    continue;

                PartAnalysis a = PartAnalysis.Analyse(path);
                List<StlFacet> facets = StlFile.Read(path);

                foreach (TrackPort port in a.Ports)
                {
                    var pivotMm = a.PivotOffsetUnits * 100f;

                    var footprintCentre = new Vector2(a.FootprintSize.x * PartAnalysis.StudPitchMm * 0.5f,
                                                      a.FootprintSize.y * PartAnalysis.StudPitchMm * 0.5f);

                    var mouth = new Vector2(port.midlineHalfStuds.x * PartAnalysis.StudPitchMm * 0.5f,
                                            port.midlineHalfStuds.y * PartAnalysis.StudPitchMm * 0.5f);

                    Vector2 atMm = pivotMm + (mouth - footprintCentre);

                    Vector2 inward = port.facing switch
                    {
                        Facing.North => Vector2.down,
                        Facing.South => Vector2.up,
                        Facing.East => Vector2.left,
                        _ => Vector2.right,
                    };

                    sb.Append($"   {name,-14} {port.facing,-5} at {port.heightMm,5:0.0}: ");

                    for (float inset = -4f; inset <= 10f; inset += 2f)
                    {
                        Vector2 at = atMm + inward * inset;
                        float top = TopAt(facets, at.x, at.y);
                        sb.Append(float.IsNaN(top) ? "     -" : $"{top - a.MinMm.z,6:0.0}");
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine("Top surface of funnel_6x7, 4 mm grid, +y upward. . = no geometry,");
            sb.AppendLine("digits = height/4 mm (2 = the 9.6 channel floor, 7 = the 28 mm rim):");

            {
                PartAnalysis a = PartAnalysis.Analyse($"{MeshFolder}/funnel_6x7.stl");
                List<StlFacet> facets = StlFile.Read($"{MeshFolder}/funnel_6x7.stl");

                for (float y = a.SizeMm.y; y >= 0f; y -= 4f)
                {
                    sb.Append("   ");

                    for (float x = 0f; x <= a.SizeMm.x; x += 4f)
                    {
                        float top = TopAt(facets, a.MinMm.x + x, a.MinMm.y + y);

                        sb.Append(float.IsNaN(top)
                            ? "."
                            : Mathf.Clamp(Mathf.RoundToInt((top - a.MinMm.z) / 4f), 0, 9).ToString());
                    }

                    sb.AppendLine();
                }

                foreach (TrackPort port in a.Ports)
                    sb.AppendLine($"   port {port.facing} midline {port.midlineHalfStuds} " +
                                  $"width {port.widthStuds} height {port.heightMm:0.0}");
            }

            sb.AppendLine();
            sb.AppendLine("Surface heights around each funnel's stud shelf, 0.2 mm bins.");
            sb.AppendLine("Plateaus show up as spikes: base, shelf, stud tops, chute floor, walls.");

            foreach (string name in new[] { "funnel_6x7", "funnel_8x9", "funnel_10x10" })
            {
                string path = $"{MeshFolder}/{name}.stl";
                if (!File.Exists(path))
                    continue;

                PartAnalysis a = PartAnalysis.Analyse(path);
                List<StlFacet> facets = StlFile.Read(path);

                // The region around the studs, which is where an incoming piece plugs in and where
                // its channel has to meet the funnel's. Located from the derived stud cells rather
                // than written down, so it follows each funnel's own layout.
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;

                for (int cy = 0; cy < a.FootprintSize.y; cy++)
                for (int cx = 0; cx < a.FootprintSize.x; cx++)
                {
                    if (!a.TopStuds[cy * a.FootprintSize.x + cx])
                        continue;

                    minX = Mathf.Min(minX, cx * PartAnalysis.StudPitchMm);
                    maxX = Mathf.Max(maxX, (cx + 1) * PartAnalysis.StudPitchMm);
                    minY = Mathf.Min(minY, cy * PartAnalysis.StudPitchMm);
                    maxY = Mathf.Max(maxY, (cy + 1) * PartAnalysis.StudPitchMm);
                }

                if (minX > maxX)
                {
                    sb.AppendLine($"   {name}: no studs found");
                    continue;
                }

                // Two studs further in, to take in the chute the shelf leads to.
                minY -= 2f * PartAnalysis.StudPitchMm;
                minX -= 2f * PartAnalysis.StudPitchMm;

                var bins = new Dictionary<int, int>();
                int total = 0;

                for (float x = minX; x <= maxX; x += 0.5f)
                for (float y = minY; y <= maxY; y += 0.5f)
                {
                    float top = TopAt(facets, a.MinMm.x + x, a.MinMm.y + y);
                    if (float.IsNaN(top))
                        continue;

                    int bin = Mathf.RoundToInt((top - a.MinMm.z) / 0.2f);
                    bins[bin] = bins.TryGetValue(bin, out int n) ? n + 1 : 1;
                    total++;
                }

                sb.AppendLine($"   == {name}  ({total} samples)");

                var heights = new List<int>(bins.Keys);
                heights.Sort();

                foreach (int bin in heights)
                {
                    float share = bins[bin] / (float)total;
                    if (share < 0.02f)
                        continue;

                    sb.AppendLine($"      {bin * 0.2f,6:0.0} mm  {share * 100f,5:0.0}%  " +
                                  new string('#', Mathf.RoundToInt(share * 100f)));
                }
            }

            sb.AppendLine();
            sb.AppendLine("Down the middle of the inlet, from the shelf inward, 1 mm steps.");
            sb.AppendLine("A track on the shelf carries its ball at 9.6 + 6.3 = 15.9 mm.");

            foreach (string name in new[] { "funnel_6x7", "funnel_8x9", "funnel_10x10" })
            {
                string path = $"{MeshFolder}/{name}.stl";
                if (!File.Exists(path))
                    continue;

                PartAnalysis a = PartAnalysis.Analyse(path);
                List<StlFacet> facets = StlFile.Read(path);

                // Middle of the studded shelf, then straight in towards the bowl.
                float sx = 0f, sy = 0f;
                int studs = 0;

                for (int cy = 0; cy < a.FootprintSize.y; cy++)
                for (int cx = 0; cx < a.FootprintSize.x; cx++)
                {
                    if (!a.TopStuds[cy * a.FootprintSize.x + cx])
                        continue;

                    sx += (cx + 0.5f) * PartAnalysis.StudPitchMm;
                    sy += (cy + 0.5f) * PartAnalysis.StudPitchMm;
                    studs++;
                }

                if (studs == 0)
                    continue;

                sx /= studs;
                sy /= studs;

                sb.AppendLine($"   == {name}  shelf centre at ({sx:0.#}, {sy:0.#}) mm");
                sb.Append("      ");

                for (float step = 0f; step <= 40f; step += 2f)
                {
                    float top = TopAt(facets, a.MinMm.x + sx, a.MinMm.y + sy - step);
                    sb.Append(float.IsNaN(top) ? "    - " : $"{top - a.MinMm.z,6:0.0}");
                }

                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// The deepest point of an outlet: the lowest top surface in a window across its width and a
        /// few millimetres inside the edge.
        ///
        /// Across the width because a Duplo channel is a trough. Sampling down the middle of a stud
        /// reads its wall rather than its floor - 2.7 mm higher on a plain track piece - which would
        /// make every comparison here meaningless.
        /// </summary>
        static float OutletFloor(PartAnalysis a, List<StlFacet> facets, float alongMm, Facing facing)
        {
            float lowest = float.NaN;

            for (float across = -16f; across <= 16f; across += 0.5f)
            for (float inset = 3f; inset <= 8f; inset += 0.5f)
            {
                float x = facing is Facing.North or Facing.South
                    ? a.MinMm.x + alongMm + across
                    : a.MinMm.x + (facing == Facing.West ? inset : a.SizeMm.x - inset);

                float y = facing is Facing.North or Facing.South
                    ? a.MinMm.y + (facing == Facing.South ? inset : a.SizeMm.y - inset)
                    : a.MinMm.y + alongMm + across;

                float top = TopAt(facets, x, y);

                if (!float.IsNaN(top) && (float.IsNaN(lowest) || top < lowest))
                    lowest = top;
            }

            return lowest;
        }

        static float TopAt(List<StlFacet> facets, float x, float y)
        {
            float top = float.NaN;

            foreach (StlFacet f in facets)
                if (Covers(f, x, y, out float z) && (float.IsNaN(top) || z > top))
                    top = z;

            return top;
        }

        /// <summary>
        /// The channel floor just inside a mouth: the lowest of the top surfaces across its width.
        ///
        /// Lowest, because the samples run across the whole mouth and the outer ones climb the walls
        /// on either side. The floor is what the middle of the mouth is standing on.
        /// </summary>
        static float FloorAt(PartAnalysis a, List<StlFacet> facets, TrackPort port)
        {
            // Half-studs from the footprint's corner, and the footprint's centre is the pivot.
            var pivotMm = a.PivotOffsetUnits * 100f;

            var footprintCentre = new Vector2(a.FootprintSize.x * PartAnalysis.StudPitchMm * 0.5f,
                                              a.FootprintSize.y * PartAnalysis.StudPitchMm * 0.5f);

            var mouth = new Vector2(port.midlineHalfStuds.x * PartAnalysis.StudPitchMm * 0.5f,
                                    port.midlineHalfStuds.y * PartAnalysis.StudPitchMm * 0.5f);

            Vector2 atMm = pivotMm + (mouth - footprintCentre);

            Vector2 inward = port.facing switch
            {
                Facing.North => Vector2.down,
                Facing.South => Vector2.up,
                Facing.East => Vector2.left,
                _ => Vector2.right,
            };

            Vector2 across = new(-inward.y, inward.x);

            Vector2 from = atMm + inward * InsetMm;

            float lowest = float.NaN;

            // Across the middle half of the mouth only: a two-stud channel is 32 mm wide and its
            // walls start climbing well before the edge.
            float halfWidth = Mathf.Max(1, port.widthStuds) * PartAnalysis.StudPitchMm * 0.25f;

            for (float offset = -halfWidth; offset <= halfWidth; offset += 1f)
            {
                Vector2 at = from + across * offset;
                float top = float.NaN;

                foreach (StlFacet f in facets)
                    if (Covers(f, at.x, at.y, out float z) && (float.IsNaN(top) || z > top))
                        top = z;

                if (!float.IsNaN(top) && (float.IsNaN(lowest) || top < lowest))
                    lowest = top;
            }

            return lowest;
        }

        static bool Covers(StlFacet f, float x, float y, out float z)
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
