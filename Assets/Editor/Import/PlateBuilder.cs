#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Makes a plate out of a brick by taking a layer out of its middle.
    ///
    /// Duplo has no plates, but the grid needs them: it steps half a brick so that half-height parts
    /// - a funnel's shelf, anything that meets a channel halfway - have somewhere to stand, and a
    /// column one layer short of its load has nothing else that fits.
    ///
    /// Cut from the brick rather than modelled, because a plate is a brick with less wall. Its studs,
    /// its underside sockets and every chamfer come across untouched; the only thing that changes is
    /// how much straight wall there is between them.
    /// </summary>
    public static class PlateBuilder
    {
        /// <summary>Height removed, in millimetres. One grid layer.</summary>
        public const float RemoveMm = 9.6f;

        /// <summary>
        /// The brick's mesh with one layer taken out, or null when it has no plain wall to take it
        /// from - in which case the part is not a brick and has no business being a plate.
        /// </summary>
        public static Mesh BuildMesh(string stlPath, float scale, float smoothingAngle, string name)
        {
            List<StlFacet> facets = StlFile.Read(stlPath);

            if (!FindVerticalBand(facets, out float from, out float to) || to - from <= RemoveMm)
                return null;

            var squashed = new List<StlFacet>(facets.Count);
            float shrink = (to - from - RemoveMm) / (to - from);

            Vector3 Move(Vector3 v)
            {
                // Continuous and monotonic, so a triangle spanning a boundary keeps its corners in
                // order and never needs cutting. Below the band nothing moves; above it, everything
                // drops by the same amount; inside, the wall shortens.
                if (v.z <= from)
                    return v;

                if (v.z >= to)
                    return new Vector3(v.x, v.y, v.z - RemoveMm);

                return new Vector3(v.x, v.y, from + (v.z - from) * shrink);
            }

            foreach (StlFacet f in facets)
            {
                squashed.Add(new StlFacet
                {
                    A = Move(f.A),
                    B = Move(f.B),
                    C = Move(f.C),

                    // Unchanged, and correct: the band is vertical, so its faces point sideways and
                    // shortening them does not turn them. Everything else is carried, not scaled.
                    Normal = f.Normal,
                });
            }

            return StlMeshBuilder.Build(squashed, scale, smoothingAngle, name);
        }

        /// <summary>
        /// The tallest run of the part that is purely vertical, measured in the file's own z-up
        /// millimetres. Facing is recomputed from the corners rather than trusted from the file.
        /// </summary>
        static bool FindVerticalBand(List<StlFacet> facets, out float from, out float to)
        {
            from = to = 0f;
            if (facets.Count == 0)
                return false;

            float lowest = float.MaxValue, highest = float.MinValue;

            foreach (StlFacet f in facets)
                foreach (Vector3 v in new[] { f.A, f.B, f.C })
                {
                    lowest = Mathf.Min(lowest, v.z);
                    highest = Mathf.Max(highest, v.z);
                }

            const float slice = 0.1f;   // millimetres
            int steps = Mathf.Max(1, Mathf.CeilToInt((highest - lowest) / slice));

            var vertical = new bool[steps];
            var present = new bool[steps];

            for (int i = 0; i < steps; i++)
                vertical[i] = true;

            foreach (StlFacet f in facets)
            {
                Vector3 normal = Vector3.Cross(f.B - f.A, f.C - f.A);
                if (normal.sqrMagnitude < 1e-9f)
                    continue;

                bool sloped = Mathf.Abs(normal.normalized.z) > 0.02f;

                int first = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(f.A.z, Mathf.Min(f.B.z, f.C.z)) - lowest) / slice), 0, steps - 1);
                int last = Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(f.A.z, Mathf.Max(f.B.z, f.C.z)) - lowest) / slice), 0, steps - 1);

                for (int k = first; k <= last; k++)
                {
                    present[k] = true;

                    if (sloped)
                        vertical[k] = false;
                }
            }

            int best = 0, bestStart = -1, run = 0, runStart = -1;

            for (int k = 0; k < steps; k++)
            {
                if (present[k] && vertical[k])
                {
                    if (run == 0)
                        runStart = k;

                    run++;

                    if (run > best)
                    {
                        best = run;
                        bestStart = runStart;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            if (bestStart < 0)
                return false;

            from = lowest + bestStart * slice;
            to = lowest + (bestStart + best) * slice;

            return true;
        }
    }
}
#endif
