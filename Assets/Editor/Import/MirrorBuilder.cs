#if UNITY_EDITOR
using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Import
{
    /// <summary>
    /// Builds the left/right-handed counterpart of a chiral part (DESIGN.md §3.4). Mirrors are
    /// always generated, never authored, so the pair cannot drift apart.
    /// </summary>
    public static class MirrorBuilder
    {
        /// <summary>
        /// Mirrors in the STL's own CAD space and hands the result to the normal import path.
        /// Negating X on positions and on the stored facet normals is enough: the winding reversal
        /// that a mirror requires is picked up automatically by the majority vote in
        /// <see cref="StlMeshBuilder"/>, which compares winding against those normals. Skipping the
        /// reversal would render the part inside out.
        /// </summary>
        public static Mesh BuildMesh(string stlPath, float scale, float smoothingAngle, string name)
        {
            List<StlFacet> facets = StlFile.Read(stlPath);
            var mirrored = new List<StlFacet>(facets.Count);

            foreach (StlFacet f in facets)
            {
                mirrored.Add(new StlFacet
                {
                    A = NegateX(f.A),
                    B = NegateX(f.B),
                    C = NegateX(f.C),
                    Normal = NegateX(f.Normal),
                });
            }

            return StlMeshBuilder.Build(mirrored, scale, smoothingAngle, name);
        }

        static Vector3 NegateX(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        /// <summary>Mirrors a row-major footprint/stud mask across X.</summary>
        public static bool[] MirrorMask(bool[] mask, Vector2Int size)
        {
            if (mask == null || mask.Length != size.x * size.y)
                return mask;

            var flipped = new bool[mask.Length];
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
                flipped[y * size.x + (size.x - 1 - x)] = mask[y * size.x + x];

            return flipped;
        }

        public static TrackPort[] MirrorPorts(TrackPort[] ports, Vector2Int size)
        {
            if (ports == null)
                return null;

            var flipped = new TrackPort[ports.Length];
            for (int i = 0; i < ports.Length; i++)
            {
                TrackPort p = ports[i];

                // Mirroring across X reflects the centre line about the footprint's mid-plane, in the
                // same half-stud units the port is stored in.
                p.midlineHalfStuds = new Vector2Int(size.x * 2 - p.midlineHalfStuds.x, p.midlineHalfStuds.y);
                if (p.profileMm != null)
                    System.Array.Reverse(p.profileMm = (float[])p.profileMm.Clone());

                p.facing = MirrorFacing(p.facing);
                flipped[i] = p;
            }
            return flipped;
        }

        /// <summary>An X mirror swaps east and west; north and south are unaffected.</summary>
        static Facing MirrorFacing(Facing f) => f switch
        {
            Facing.East => Facing.West,
            Facing.West => Facing.East,
            _ => f,
        };

        public static Vector3[] MirrorCenterline(Vector3[] points)
        {
            if (points == null)
                return null;

            var flipped = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++)
                flipped[i] = new Vector3(-points[i].x, points[i].y, points[i].z);
            return flipped;
        }
    }
}
#endif
