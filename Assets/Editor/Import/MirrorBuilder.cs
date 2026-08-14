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
        /// <summary>
        /// Mirrors a stack of per-layer masks, one layer at a time.
        ///
        /// Not covered by <see cref="MirrorMask"/>: that one takes a single footprint-sized plane and
        /// rejects anything longer, so a layered mask passed to it came straight back unmirrored - and
        /// a mask whose length no longer matches the footprint is treated by the part as absent, which
        /// falls back to a solid prism. Every mirrored slide curve therefore claimed both its layers
        /// in every column, its raised end looked as though it reached the ground, and the pillar
        /// under it stopped a layer short of the part it was carrying.
        /// </summary>
        public static bool[] MirrorLayerMasks(bool[] masks, Vector2Int size, int layers)
        {
            int plane = size.x * size.y;
            if (masks == null || plane == 0 || layers <= 0 || masks.Length != plane * layers)
                return masks;

            var flipped = new bool[masks.Length];

            for (int layer = 0; layer < layers; layer++)
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
                flipped[layer * plane + y * size.x + (size.x - 1 - x)] =
                    masks[layer * plane + y * size.x + x];

            return flipped;
        }

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
