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
        public int HeightLayers;
        public bool HasTopStuds;
        public bool[] TopStuds;

        public MirrorVerdict MirrorVerdict;
        public float MirrorScore;
        public int MirrorBestRotation;

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
            a.DeriveTopStuds(facets);
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
