#if UNITY_EDITOR
using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Everything the importer can work out about a part on its own, derived from the source STL
    /// facets in CAD space (millimetres, Z-up) rather than from the imported mesh - which lets the
    /// runtime meshes stay non-readable for WebGL's sake (DESIGN.md §0.1).
    ///
    /// This proposes; a human confirms in <see cref="PartValidatorWindow"/>. See DESIGN.md §3.2/§3.4
    /// for why full automation is the wrong goal.
    /// </summary>
    public sealed class PartAnalysis
    {
        public const float StudPitchMm = 16.0f;
        public const float LayerHeightMm = 19.2f;
        public const float StudHeightMm = 4.6f;
        public const float ClearanceMm = 0.2f;

        public string SourcePath;
        public Vector3 SizeMm;
        public Vector3 MinMm;

        public Vector2Int FootprintSize;
        public bool[] FootprintMask;
        public bool[] LayerMasks;
        public int HeightLayers;
        public bool HasTopStuds;
        public bool[] TopStuds;

        /// <summary>
        /// Offset from the mesh's own origin to the centre of its bounding box, in world units.
        ///
        /// Most parts are modelled centred on their footprint, but not all: u_turn's mesh runs from
        /// -15.9 to +62.3 mm in X, and u_turn_slide is offset by a whole stud in Y. Positioning those
        /// as though they were centred draws them a stud away from the cells they occupy, so their
        /// channels appear to join one stud inside the neighbouring piece.
        /// </summary>
        public Vector2 PivotOffsetUnits;

        public MirrorVerdict MirrorVerdict;
        public float MirrorScore;
        public int MirrorBestRotation;

        public readonly List<TrackPort> Ports = new();

        /// <summary>
        /// Height of a marble channel's floor above the part's own base. Measured across the whole
        /// part set and identical everywhere: 6.4 mm, exactly a third of a layer.
        /// </summary>
        public const float ChannelFloorMm = 6.4f;

        public readonly List<string> Warnings = new();

        public static PartAnalysis Analyse(string stlPath)
        {
            List<StlFacet> facets = StlFile.Read(stlPath);
            var a = new PartAnalysis { SourcePath = stlPath };

            if (facets.Count == 0)
            {
                a.Warnings.Add("No triangles in file.");
                return a;
            }

            Bounds(facets, out a.MinMm, out Vector3 max);
            a.SizeMm = max - a.MinMm;

            a.DeriveHeight();
            a.DeriveFootprint(facets);
            a.DerivePivot();
            a.DeriveLayerMasks(facets);
            a.DeriveTopStuds(facets);
            a.DerivePorts(facets);
            a.DeriveChirality(facets);

            return a;
        }

        static void Bounds(List<StlFacet> facets, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (StlFacet f in facets)
            {
                min = Vector3.Min(min, Vector3.Min(f.A, Vector3.Min(f.B, f.C)));
                max = Vector3.Max(max, Vector3.Max(f.A, Vector3.Max(f.B, f.C)));
            }
        }

        /// <summary>
        /// Every observed part height decomposes as layers*19.2 (+4.6 when studded): 19.2, 23.8,
        /// 38.4, 43.0. Detect the stud crown first, then the layer count from what remains.
        /// </summary>
        void DeriveHeight()
        {
            float h = SizeMm.z;

            float withoutStuds = h - StudHeightMm;
            bool studCrown = IsNearMultiple(withoutStuds, LayerHeightMm) && !IsNearMultiple(h, LayerHeightMm);

            float body = studCrown ? withoutStuds : h;
            HeightLayers = Mathf.Max(1, Mathf.RoundToInt(body / LayerHeightMm));
            HasTopStuds = studCrown;

            if (!IsNearMultiple(body, LayerHeightMm))
                Warnings.Add($"Height {h:0.##} mm is not layers*{LayerHeightMm} (+{StudHeightMm}); guessed {HeightLayers} layer(s).");
        }

        static bool IsNearMultiple(float value, float step, float tolerance = 0.5f) =>
            Mathf.Abs(value - Mathf.Round(value / step) * step) <= tolerance;

        /// <summary>
        /// Two-source derivation (DESIGN.md §1.1). The bounding box gives a candidate size; projecting
        /// the part's lower geometry onto the stud grid gives the real occupancy, which also catches
        /// non-rectangular parts. u_turn is the case that matters: its bounding box is 78.2 mm where
        /// five studs measure 79.8, because the outer wall stops short of the grid edge.
        /// </summary>
        void DeriveFootprint(List<StlFacet> facets)
        {
            FootprintSize = new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt((SizeMm.x + ClearanceMm) / StudPitchMm - 0.01f)),
                Mathf.Max(1, Mathf.CeilToInt((SizeMm.y + ClearanceMm) / StudPitchMm - 0.01f)));

            float expectedX = FootprintSize.x * StudPitchMm - ClearanceMm;
            float expectedY = FootprintSize.y * StudPitchMm - ClearanceMm;
            if (Mathf.Abs(SizeMm.x - expectedX) > 0.5f || Mathf.Abs(SizeMm.y - expectedY) > 0.5f)
            {
                Warnings.Add(
                    $"Bounding box {SizeMm.x:0.##}x{SizeMm.y:0.##} mm is inset from its {FootprintSize.x}x{FootprintSize.y} " +
                    $"footprint ({expectedX:0.##}x{expectedY:0.##}); confirm the grid size is right.");
            }

            // Project the whole model, not just its base. A descending part such as slide_curve_4x4
            // carries its far end a full layer up, so sampling only a lower band reports a fraction
            // of the cells it actually occupies (4 of 16, in that case) and the part would be placed
            // as though most of its area were free.
            var mask = new bool[FootprintSize.x * FootprintSize.y];
            foreach (StlFacet f in facets)
            {
                MarkCell(mask, f.A);
                MarkCell(mask, f.B);
                MarkCell(mask, f.C);
            }

            FootprintMask = mask;

            int occupied = 0;
            foreach (bool b in mask)
                if (b) occupied++;

            if (occupied == 0)
            {
                Warnings.Add("No geometry found in the lower band; footprint mask fell back to solid.");
                for (int i = 0; i < mask.Length; i++) mask[i] = true;
            }
        }

        /// <summary>
        /// Offset from the mesh's origin to the centre of its footprint.
        ///
        /// Measured from the mesh's minimum corner rather than from its bounding box centre. A part
        /// that stops short of its footprint does so on one side only - u_turn's open mouth side has
        /// no wall to reach the boundary - so centring splits that shortfall across both sides and
        /// leaves the piece sitting a fraction of a stud out of true. The walled side is flush with
        /// the grid, and that is what the geometry should be aligned to.
        /// </summary>
        void DerivePivot()
        {
            float halfClearance = ClearanceMm * 0.5f;

            PivotOffsetUnits = new Vector2(
                (MinMm.x - halfClearance + FootprintSize.x * StudPitchMm * 0.5f) * 0.01f,
                (MinMm.y - halfClearance + FootprintSize.y * StudPitchMm * 0.5f) * 0.01f);
        }

        /// <summary>
        /// Works out which layers each cell actually fills, from the height of the part's underside
        /// as well as its top.
        ///
        /// Not every part is a solid prism. slide_2x4 reaches the base everywhere, but
        /// slide_curve_4x4's underside ramps from 18 mm down to 0, so its raised end occupies only
        /// the upper layer. Treating that end as solid claims space a support pillar needs, and the
        /// pillar then collides with the very part it was meant to carry - which is silent, because
        /// the placement had already validated.
        /// </summary>
        void DeriveLayerMasks(List<StlFacet> facets)
        {
            int layers = Mathf.Max(1, HeightLayers);
            int cells = FootprintSize.x * FootprintSize.y;

            LayerMasks = new bool[cells * layers];

            var lowest = new float[cells];
            var highest = new float[cells];
            for (int i = 0; i < cells; i++)
            {
                lowest[i] = float.PositiveInfinity;
                highest[i] = float.NegativeInfinity;
            }

            foreach (StlFacet f in facets)
            {
                AccumulateSpan(lowest, highest, f.A);
                AccumulateSpan(lowest, highest, f.B);
                AccumulateSpan(lowest, highest, f.C);
            }

            // A layer counts as filled when the geometry covers a decent share of it. A sliver of
            // ramp poking into the layer below is not something to stand a brick on.
            const float requiredShare = 0.35f;

            for (int cell = 0; cell < cells; cell++)
            {
                if (float.IsPositiveInfinity(lowest[cell]))
                    continue;

                for (int layer = 0; layer < layers; layer++)
                {
                    float from = MinMm.z + layer * LayerHeightMm;
                    float to = from + LayerHeightMm;

                    float overlap = Mathf.Min(highest[cell], to) - Mathf.Max(lowest[cell], from);
                    if (overlap >= LayerHeightMm * requiredShare)
                        LayerMasks[layer * cells + cell] = true;
                }
            }
        }

        void AccumulateSpan(float[] lowest, float[] highest, Vector3 v)
        {
            int cx = Mathf.Clamp(Mathf.FloorToInt((v.x - MinMm.x) / StudPitchMm), 0, FootprintSize.x - 1);
            int cy = Mathf.Clamp(Mathf.FloorToInt((v.y - MinMm.y) / StudPitchMm), 0, FootprintSize.y - 1);

            int index = cy * FootprintSize.x + cx;
            lowest[index] = Mathf.Min(lowest[index], v.z);
            highest[index] = Mathf.Max(highest[index], v.z);
        }

        void MarkCell(bool[] mask, Vector3 v)
        {
            int cx = Mathf.Clamp(Mathf.FloorToInt((v.x - MinMm.x) / StudPitchMm), 0, FootprintSize.x - 1);
            int cy = Mathf.Clamp(Mathf.FloorToInt((v.y - MinMm.y) / StudPitchMm), 0, FootprintSize.y - 1);
            mask[cy * FootprintSize.x + cx] = true;
        }

        /// <summary>
        /// Studs are whatever pokes above the part's body height. Their cells are the connection mask,
        /// which is what separates a studded bridge from a terminal track piece.
        /// </summary>
        void DeriveTopStuds(List<StlFacet> facets)
        {
            TopStuds = new bool[FootprintSize.x * FootprintSize.y];
            if (!HasTopStuds)
                return;

            float bodyTop = MinMm.z + HeightLayers * LayerHeightMm + 0.5f;

            foreach (StlFacet f in facets)
            {
                MarkStud(f.A, bodyTop);
                MarkStud(f.B, bodyTop);
                MarkStud(f.C, bodyTop);
            }
        }

        void MarkStud(Vector3 v, float bodyTop)
        {
            if (v.z < bodyTop)
                return;

            int cx = Mathf.Clamp(Mathf.FloorToInt((v.x - MinMm.x) / StudPitchMm), 0, FootprintSize.x - 1);
            int cy = Mathf.Clamp(Mathf.FloorToInt((v.y - MinMm.y) / StudPitchMm), 0, FootprintSize.y - 1);
            TopStuds[cy * FootprintSize.x + cx] = true;
        }

        // --- Ports (DESIGN.md §6) -------------------------------------------------------------

        /// <summary>How far a measured channel floor may sit from the expected height and still count.</summary>
        const float ChannelToleranceMm = 1.0f;

        /// <summary>Sample spacing for the surface height map, in millimetres.</summary>
        const float HeightMapRes = 1.0f;

        /// <summary>
        /// Finds where marble channels reach the edge of the part.
        ///
        /// Builds a height map of the top surface, then reads the minimum height along each boundary
        /// stud cell. A wall reads at the part's own height; a channel mouth reads far lower.
        ///
        /// The discriminator is that a channel floor always sits at 6.4 mm above some layer boundary -
        /// 6.4, 25.6, 44.8 and so on. That single rule separates mouths from walls across every part
        /// in the set, where a simple "lower than the top" threshold does not: a two-layer slide has
        /// walls at 19.2 and 20.0 mm that are far below its 43 mm top and would otherwise register as
        /// openings.
        /// </summary>
        void DerivePorts(List<StlFacet> facets)
        {
            Ports.Clear();

            int w = Mathf.CeilToInt(SizeMm.x / HeightMapRes) + 1;
            int h = Mathf.CeilToInt(SizeMm.y / HeightMapRes) + 1;
            if (w <= 4 || h <= 4)
                return;

            float[] height = BuildHeightMap(facets, w, h);

            // Read one sample in from the boundary; the outermost column catches wall skin and
            // rasterisation noise rather than the surface behind it.
            const int inset = 1;

            ScanEdge(height, w, h, Facing.West, inset);
            ScanEdge(height, w, h, Facing.East, inset);
            ScanEdge(height, w, h, Facing.South, inset);
            ScanEdge(height, w, h, Facing.North, inset);
        }

        float[] BuildHeightMap(List<StlFacet> facets, int w, int h)
        {
            var height = new float[w * h];
            for (int i = 0; i < height.Length; i++)
                height[i] = float.NegativeInfinity;

            foreach (StlFacet f in facets)
            {
                float minX = Mathf.Min(f.A.x, Mathf.Min(f.B.x, f.C.x));
                float maxX = Mathf.Max(f.A.x, Mathf.Max(f.B.x, f.C.x));
                float minY = Mathf.Min(f.A.y, Mathf.Min(f.B.y, f.C.y));
                float maxY = Mathf.Max(f.A.y, Mathf.Max(f.B.y, f.C.y));
                float maxZ = Mathf.Max(f.A.z, Mathf.Max(f.B.z, f.C.z));

                int i0 = Mathf.Clamp(Mathf.FloorToInt((minX - MinMm.x) / HeightMapRes), 0, w - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt((maxX - MinMm.x) / HeightMapRes), 0, w - 1);
                int j0 = Mathf.Clamp(Mathf.FloorToInt((minY - MinMm.y) / HeightMapRes), 0, h - 1);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((maxY - MinMm.y) / HeightMapRes), 0, h - 1);

                // Bounding-box fill rather than exact rasterisation: only the minimum along an edge
                // is read, and over-covering can only raise a cell, never lower it into a false mouth.
                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    int index = j * w + i;
                    if (maxZ > height[index])
                        height[index] = maxZ;
                }
            }

            return height;
        }

        /// <summary>
        /// Reads one edge and emits a port per channel mouth.
        ///
        /// Contiguous cells at the same channel height form one mouth, recorded by its centre line
        /// rather than cell by cell. A two-stud channel has its centre on a stud boundary, so this is
        /// also the only way to state it exactly. u_turn is the case that needs the grouping: its
        /// west edge carries two separate mouths with a wall between them, which must stay two ports
        /// rather than merging into one four-stud opening.
        /// </summary>
        void ScanEdge(float[] height, int w, int h, Facing facing, int inset)
        {
            bool alongY = facing == Facing.West || facing == Facing.East;
            int cellsAlong = alongY ? FootprintSize.y : FootprintSize.x;
            int samplesAlong = alongY ? h : w;

            float perCell = samplesAlong / (float)cellsAlong;

            int runStart = -1;
            float runHeight = 0f;

            for (int cell = 0; cell <= cellsAlong; cell++)
            {
                bool isChannel = false;
                float snapped = 0f;

                if (cell < cellsAlong)
                {
                    int from = Mathf.RoundToInt(cell * perCell);
                    int to = Mathf.RoundToInt((cell + 1) * perCell);

                    float lowest = float.PositiveInfinity;

                    for (int s = from; s < to; s++)
                    {
                        int i, j;
                        switch (facing)
                        {
                            case Facing.West:  i = inset;         j = s; break;
                            case Facing.East:  i = w - 1 - inset; j = s; break;
                            case Facing.South: i = s;             j = inset; break;
                            default:           i = s;             j = h - 1 - inset; break;
                        }

                        if (i < 0 || j < 0 || i >= w || j >= h)
                            continue;

                        float value = height[j * w + i];
                        if (float.IsNegativeInfinity(value))
                            continue; // no geometry in this column at all

                        lowest = Mathf.Min(lowest, value);
                    }

                    isChannel = !float.IsPositiveInfinity(lowest) && IsChannelFloor(lowest, out snapped);
                }

                // A run also breaks when the height steps, which keeps two mouths at different
                // levels from merging into one.
                bool continues = isChannel && runStart >= 0 && Mathf.Approximately(snapped, runHeight);

                if (runStart >= 0 && !continues)
                {
                    EmitPort(facing, runStart, cell, runHeight);
                    runStart = -1;
                }

                if (isChannel && runStart < 0)
                {
                    runStart = cell;
                    runHeight = snapped;
                }
            }
        }

        void EmitPort(Facing facing, int fromCell, int toCell, float heightMm)
        {
            int widthStuds = toCell - fromCell;

            // Centre of the run in half-studs: the run spans [from, to) studs, so its middle is at
            // from + width/2 studs, which is (2*from + width) half-studs - always an integer.
            int centreAlong = fromCell * 2 + widthStuds;

            bool alongY = facing == Facing.West || facing == Facing.East;

            int across = facing switch
            {
                Facing.West => 0,
                Facing.East => FootprintSize.x * 2,
                Facing.South => 0,
                _ => FootprintSize.y * 2,
            };

            Vector2Int midline = alongY
                ? new Vector2Int(across, centreAlong)
                : new Vector2Int(centreAlong, across);

            Ports.Add(new TrackPort
            {
                midlineHalfStuds = midline,
                facing = facing,
                heightMm = heightMm,
                widthStuds = widthStuds,
            });
        }

        /// <summary>
        /// True when a measured height matches a channel floor: 6.4 mm above some layer boundary.
        /// Returns the exact expected height, so stored ports carry clean values rather than whatever
        /// the sampling happened to land on.
        /// </summary>
        static bool IsChannelFloor(float measured, out float snapped)
        {
            int layer = Mathf.RoundToInt((measured - ChannelFloorMm) / LayerHeightMm);
            snapped = layer * LayerHeightMm + ChannelFloorMm;

            return layer >= 0 && Mathf.Abs(measured - snapped) <= ChannelToleranceMm;
        }

        // --- Chirality (DESIGN.md §3.4) -------------------------------------------------------

        /// <summary>Above this, the mirror is a rotation the game already offers, so generating one duplicates a part.</summary>
        const float RedundantThreshold = 0.90f;

        /// <summary>Below this, the part is genuinely handed and needs a generated mirror.</summary>
        const float ChiralThreshold = 0.75f;

        const int SampleCount = 250_000;
        const float VoxelMm = 2.0f;

        /// <summary>
        /// The useful question is not "is this part asymmetric" but "is its mirror reproducible by one
        /// of the four yaw rotations the game already supports". A quarter-turn curve is asymmetric,
        /// yet its mirror is just a rotation - generating one would put the same piece in the palette
        /// twice.
        ///
        /// Comparison is by occupied volume, sampled over the surface, rather than by vertex
        /// positions. An earlier version compared vertex sets and was wrong on four parts: mirroring
        /// a triangulated mesh does not reproduce the original vertex positions even when the shape
        /// is identical, so that test was really measuring tessellation. Area-weighted sampling
        /// answers the question actually being asked - does the same solid come back.
        /// </summary>
        void DeriveChirality(List<StlFacet> facets)
        {
            List<Vector3> points = SampleSurface(facets, SampleCount);

            HashSet<Vector3Int> baseSet = Voxelise(points, mirror: false, rotations: 0);

            MirrorScore = 0f;
            MirrorBestRotation = 0;

            for (int rot = 0; rot < 4; rot++)
            {
                float score = Jaccard(baseSet, Voxelise(points, mirror: true, rotations: rot));
                if (score > MirrorScore)
                {
                    MirrorScore = score;
                    MirrorBestRotation = rot;
                }
            }

            MirrorVerdict = MirrorScore >= RedundantThreshold ? MirrorVerdict.Redundant
                : MirrorScore <= ChiralThreshold ? MirrorVerdict.Chiral
                : MirrorVerdict.Ambiguous;
        }

        /// <summary>
        /// Scatters points across the surface, giving each triangle a share proportional to its area
        /// so dense tessellation does not weight one region more than another.
        ///
        /// The generator is a fixed-seed xorshift rather than System.Random: a verdict that changed
        /// between runs, or between Unity versions, would be worse than a wrong one.
        /// </summary>
        static List<Vector3> SampleSurface(List<StlFacet> facets, int target)
        {
            double totalArea = 0.0;
            var areas = new double[facets.Count];

            for (int i = 0; i < facets.Count; i++)
            {
                areas[i] = Vector3.Cross(facets[i].B - facets[i].A, facets[i].C - facets[i].A).magnitude * 0.5;
                totalArea += areas[i];
            }

            var points = new List<Vector3>(target + facets.Count);
            if (totalArea <= 0.0)
                return points;

            uint state = 0x9E3779B9;

            for (int i = 0; i < facets.Count; i++)
            {
                int count = Mathf.Max(1, (int)(target * (areas[i] / totalArea)));
                StlFacet f = facets[i];
                Vector3 ab = f.B - f.A;
                Vector3 ac = f.C - f.A;

                for (int s = 0; s < count; s++)
                {
                    float u = NextFloat(ref state);
                    float v = NextFloat(ref state);

                    // Fold the far half of the unit square back into the triangle.
                    if (u + v > 1f)
                    {
                        u = 1f - u;
                        v = 1f - v;
                    }

                    points.Add(f.A + ab * u + ac * v);
                }
            }

            return points;
        }

        static float NextFloat(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFF) / (float)0x1000000;
        }

        HashSet<Vector3Int> Voxelise(List<Vector3> points, bool mirror, int rotations)
        {
            // Centre on the bounding box so mirroring and rotation are about the part's own axis.
            float cx = MinMm.x + SizeMm.x * 0.5f;
            float cy = MinMm.y + SizeMm.y * 0.5f;

            var set = new HashSet<Vector3Int>(points.Count / 4);

            foreach (Vector3 p in points)
            {
                float x = p.x - cx;
                float y = p.y - cy;

                if (mirror)
                    x = -x;

                for (int i = 0; i < rotations; i++)
                    (x, y) = (-y, x);

                set.Add(new Vector3Int(
                    Mathf.RoundToInt(x / VoxelMm),
                    Mathf.RoundToInt(y / VoxelMm),
                    Mathf.RoundToInt(p.z / VoxelMm)));
            }

            return set;
        }

        static float Jaccard(HashSet<Vector3Int> a, HashSet<Vector3Int> b)
        {
            int intersection = 0;
            foreach (Vector3Int p in a)
                if (b.Contains(p)) intersection++;

            int union = a.Count + b.Count - intersection;
            return union == 0 ? 0f : intersection / (float)union;
        }
    }
}
#endif
