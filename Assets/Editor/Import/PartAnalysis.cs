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
        /// <summary>One grid layer. Half a brick, so plates have somewhere to stand.</summary>
        public const float LayerHeightMm = 9.6f;

        /// <summary>
        /// One brick. What channel floors are spaced by.
        ///
        /// Deliberately not the grid layer. A channel floor sits 6.4 mm above a brick boundary, and
        /// that is what tells a mouth from a wall - measuring it in half-bricks would accept a wall
        /// at 16.0 as a mouth and invent ports all over parts that have none.
        /// </summary>
        public const float BrickPitchMm = 19.2f;
        public const float StudHeightMm = 4.6f;
        public const float ClearanceMm = 0.2f;

        /// <summary>The ball these channels are built for. What counts as a hole is measured against it.</summary>
        public const float BallDiameterMm = 24.5f;

        public string SourcePath;
        public Vector3 SizeMm;
        public Vector3 MinMm;

        public Vector2Int FootprintSize;
        public bool[] FootprintMask;
        public bool[] LayerMasks;
        public int HeightLayers;
        public bool HasTopStuds;
        public bool[] TopStuds;

        /// <summary>Cells whose underside is flat against the base - where the part can clutch down.</summary>
        public bool[] BottomSockets;

        /// <summary>
        /// Middle of the part's contact patch, where one was asked for. Zero means unset.
        /// </summary>
        public Vector2 ContactCentreMm;

        /// <summary>The centre of whatever the part rests on, taken from its lowest couple of millimetres.</summary>
        static Vector2 ContactCentre(List<StlFacet> facets, float floorZ)
        {
            const float sliceMm = 2f;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (StlFacet f in facets)
                foreach (Vector3 v in new[] { f.A, f.B, f.C })
                {
                    if (v.z > floorZ + sliceMm)
                        continue;

                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
                }

            return minX == float.MaxValue
                ? Vector2.zero
                : new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
        }

        /// <summary>
        /// Offset from the mesh's own origin to the centre of its bounding box, in world units.
        ///
        /// Most parts are modelled centred on their footprint, but not all: u_turn's mesh runs from
        /// -15.9 to +62.3 mm in X, and u_turn_slide is offset by a whole stud in Y. Positioning those
        /// as though they were centred draws them a stud away from the cells they occupy, so their
        /// channels appear to join one stud inside the neighbouring piece.
        /// </summary>
        public Vector2 PivotOffsetUnits;

        /// <summary>The underside arches clear of the base and opens out at an edge - a ball can pass under.</summary>
        public bool HasTunnel;

        /// <summary>
        /// Middle of the vertical shaft through the part, in mesh millimetres, and how wide it is.
        ///
        /// A funnel's whole purpose is the hole in the middle of it, and where that hole lands is the
        /// one thing the player is aiming at while placing one. Zero radius means the part has none.
        /// </summary>
        public Vector2 DropHoleCentreMm;

        public float DropHoleRadiusMm;

        /// <summary>Width and depth of the shaft's own bounding box, and how much of it it fills.</summary>
        public Vector2 DropHoleExtentMm;

        public float DropHoleFill;

        /// <summary>The shaft's samples, kept for the socket derivation. Null when there is no shaft.</summary>
        bool[] _holeSamples;
        int _holeW;
        int _holeH;

        public MirrorVerdict MirrorVerdict;
        public float MirrorScore;
        public int MirrorBestRotation;

        public readonly List<TrackPort> Ports = new();

        /// <summary>
        /// Height of a marble channel's floor above the part's own base. Measured across the whole
        /// part set and identical everywhere: 6.4 mm, exactly a third of a layer.
        /// </summary>
        public const float ChannelFloorMm = 6.4f;

        /// <summary>
        /// How far below the drawn mesh this part's collider should sit, in millimetres.
        ///
        /// Non-zero only for a part whose channel is referenced to a stud shelf rather than to its own
        /// base - the funnels, whose chute runs 7.1 mm above the shelf an incoming piece plugs onto
        /// where every other part carries its channel 6.4 mm above its base. The 0.7 mm difference is
        /// a step up at the junction, and a slow ball stops against it.
        ///
        /// Fixed in the collider rather than in the grid because that is where the ball is: dropping
        /// the collision geometry by the difference makes the two channels continuous, and 0.7 mm of
        /// collider-to-mesh disagreement is a fifth of a millimetre at the scale anything is drawn at.
        /// </summary>
        public float ColliderDropMm;

        public readonly List<string> Warnings = new();

        /// <summary>
        /// Reads a part's geometry.
        ///
        /// <paramref name="solidHeightMm"/> describes a part that is only solid near the floor - a
        /// stalk, whose stem sits on one stud while its fronds lean out over the neighbours. Above
        /// that height nothing counts: not for the footprint, not for where the part is centred, not
        /// for which cells it fills. The fronds are still drawn, and still bend, but a piece that
        /// takes a whole two-by-two because its leaves spread that far cannot be planted four to a
        /// brick as the real thing can.
        /// </summary>
        public static PartAnalysis Analyse(string stlPath, float solidHeightMm = 0f)
        {
            List<StlFacet> facets = StlFile.Read(stlPath);
            var a = new PartAnalysis { SourcePath = stlPath };

            if (facets.Count == 0)
            {
                a.Warnings.Add("No triangles in file.");
                return a;
            }

            if (solidHeightMm > 0f)
            {
                Bounds(facets, out Vector3 whole, out _);
                float ceiling = whole.z + solidHeightMm;

                var solid = new List<StlFacet>(facets.Count);

                foreach (StlFacet f in facets)
                    if (Mathf.Max(f.A.z, Mathf.Max(f.B.z, f.C.z)) <= ceiling)
                        solid.Add(f);

                // Only if there is a base to speak of. A part that is all fronds is measured whole
                // rather than reduced to nothing.
                if (solid.Count > 8)
                    facets = solid;

                // Where the part actually touches what it stands on, which is what has to line up
                // with the stud. Even the stem's full height is the wrong thing to centre: stalks
                // spring from its sides and reach down it, so the material around it is lopsided and
                // pulls the middle off the stud it is planted on.
                a.ContactCentreMm = ContactCentre(facets, whole.z);
            }

            Bounds(facets, out a.MinMm, out Vector3 max);
            a.SizeMm = max - a.MinMm;

            a.DeriveHeight();
            a.DeriveFootprint(facets);
            a.DerivePivot();
            a.DeriveLayerMasks(facets);
            a.DeriveTopStuds(facets);
            a.DeriveDropHole(facets);
            a.DeriveColliderDrop(facets);
            a.DeriveBottomSockets(facets);
            a.DerivePorts(facets);
            a.DeriveTunnel(facets);
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

        /// <summary>
        /// Whether a measurement is a whole number of steps, within the slop a modelled part carries.
        ///
        /// 0.8 mm rather than 0.5. Two of the imported pillars are about half a millimetre over their
        /// nominal height, and at 0.5 one of them fell outside while the other did not - so a pillar
        /// with studs on top was read as having none, and nothing could be stacked on it. The parts
        /// are correct; the tolerance was narrower than the accuracy anything gets exported with.
        ///
        /// Still far tighter than the smallest thing being told apart: a stud is 4.6 mm, so there is
        /// no reading of 0.8 that confuses a studded top with a flat one.
        /// </summary>
        /// <summary>How many studs a measurement spans, allowing for a modelled part's own slop.</summary>
        static int StudsAcross(float sizeMm) =>
            Mathf.Max(1, Mathf.CeilToInt((sizeMm + ClearanceMm - FootprintToleranceMm) / StudPitchMm));

        /// <summary>Overshoot a part may have and still count as the smaller footprint.</summary>
        const float FootprintToleranceMm = 0.6f;

        static bool IsNearMultiple(float value, float step, float tolerance = 0.8f) =>
            Mathf.Abs(value - Mathf.Round(value / step) * step) <= tolerance;

        /// <summary>
        /// Two-source derivation (DESIGN.md §1.1). The bounding box gives a candidate size; projecting
        /// the part's lower geometry onto the stud grid gives the real occupancy, which also catches
        /// non-rectangular parts. u_turn is the case that matters: its bounding box is 78.2 mm where
        /// five studs measure 79.8, because the outer wall stops short of the grid edge.
        /// </summary>
        void DeriveFootprint(List<StlFacet> facets)
        {
            // The slack is in millimetres, not in studs. A part is meant to measure n*16 - 0.2, and
            // the fractional 0.01 allowed only 0.16 mm of overshoot - so a 6-stud plate exported at
            // 96.00 instead of 95.80 was rounded up to seven studs, taking its whole grid with it.
            // 0.6 mm absorbs that while still rounding a genuinely larger part up.
            FootprintSize = new Vector2Int(
                StudsAcross(SizeMm.x),
                StudsAcross(SizeMm.y));

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

            // The middle of the geometry, lined up with the middle of the cells it occupies.
            //
            // The old reading assumed the mesh filled its footprint and worked from its lowest
            // corner. For a part that does fill it the two agree exactly - a 2x2 brick comes out at
            // zero either way - and for one that does not, this is the one that is right: a stalk's
            // stem is ten millimetres inside a sixteen millimetre cell and off-centre within its own
            // bounds, and the corner reading planted it two and a half millimetres off its stud.
            PivotOffsetUnits = ContactCentreMm != Vector2.zero
                ? ContactCentreMm * 0.01f
                : new Vector2(
                    (MinMm.x + SizeMm.x * 0.5f) * 0.01f,
                    (MinMm.y + SizeMm.y * 0.5f) * 0.01f);
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

                // Studs are not occupancy. They stand 4.6 mm proud, which is half of a 9.6 mm layer
                // and comfortably over the share needed to claim one - so a shelf with studs on it
                // claimed the layer above as well, and anything clutched onto that shelf was placed a
                // plate too high. The stud belongs to whatever clutches onto it, not to this part.
                float top = IsStudTop(highest[cell]) ? highest[cell] - StudHeightMm : highest[cell];

                for (int layer = 0; layer < layers; layer++)
                {
                    float from = MinMm.z + layer * LayerHeightMm;
                    float to = from + LayerHeightMm;

                    float overlap = Mathf.Min(top, to) - Mathf.Max(lowest[cell], from);
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
        /// Studs are cells whose highest point stands a stud's height above a layer boundary.
        ///
        /// The old rule was "anything poking above the part's body height", which works only while
        /// the studs are the tallest thing on the part. They are, on every brick and every length of
        /// track - and they are not on a funnel, whose rim stands higher than the shelf it offers to
        /// the next piece. That part came in with no studs at all and nothing could be clutched to
        /// it, though four of them are plainly there.
        ///
        /// Measuring each cell against the grid instead asks the question the geometry answers: a
        /// stud is 4.6 mm of boss on top of a whole number of layers. A rim at 28.4 is 23.8 above a
        /// boundary and fails; a shelf at 19.2 with studs to 23.8 passes; and a plain track top at
        /// 19.2 is 14.6 above the layer below it and fails, which is what keeps a flat piece flat.
        /// </summary>
        void DeriveTopStuds(List<StlFacet> facets)
        {
            int cells = FootprintSize.x * FootprintSize.y;
            TopStuds = new bool[cells];
            HasTopStuds = false;

            int w = Mathf.CeilToInt(SizeMm.x / HeightMapRes) + 1;
            int h = Mathf.CeilToInt(SizeMm.y / HeightMapRes) + 1;

            if (w <= 0 || h <= 0)
                return;

            // Rasterised exactly, not by bounding box. The map used for channel mouths deliberately
            // over-covers - only the minimum along an edge is read there, and over-covering can only
            // raise a cell into being a wall, never lower it into a false mouth. Here the maximum is
            // what matters, and over-covering smears a funnel's 28.4 mm rim into the neighbouring
            // shelf, whose studs then look like part of the rim and vanish.
            float[] height = BuildExactHeightMap(facets, w, h);

            // Samples grouped by cell, so each can be asked about its own shape rather than just its
            // highest point.
            var samples = new List<float>[cells];
            for (int i = 0; i < cells; i++)
                samples[i] = new List<float>();

            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float z = height[j * w + i];
                if (float.IsNegativeInfinity(z))
                    continue;

                float x = MinMm.x + i * HeightMapRes;
                float y = MinMm.y + j * HeightMapRes;

                int cx = Mathf.Clamp(Mathf.FloorToInt((x - MinMm.x) / StudPitchMm), 0, FootprintSize.x - 1);
                int cy = Mathf.Clamp(Mathf.FloorToInt((y - MinMm.y) / StudPitchMm), 0, FootprintSize.y - 1);

                samples[cy * FootprintSize.x + cx].Add(z);
            }

            for (int cell = 0; cell < cells; cell++)
            {
                List<float> column = samples[cell];
                if (column.Count == 0)
                    continue;

                float top = float.NegativeInfinity;
                foreach (float z in column)
                    top = Mathf.Max(top, z);

                if (!IsStudTop(top))
                    continue;

                // A stud is a flat disc standing on a flat surface, so the cell has two plateaus: the
                // stud's own top, and the part's surface 4.6 mm below it. A bowl sloping past those
                // heights has neither, and reading only the highest point put studs in a ring around
                // every large funnel - the slope simply passes through the height a stud would be at.
                const float flat = 0.4f;

                int atTop = 0, atBody = 0;

                foreach (float z in column)
                {
                    if (Mathf.Abs(z - top) <= flat) atTop++;
                    else if (Mathf.Abs(z - (top - StudHeightMm)) <= flat) atBody++;
                }

                // A 9.5 mm stud covers about a quarter of a 16 mm cell, and the surface around it the
                // rest. Held well under both so a stud at the edge of a part still counts.
                if (atTop < column.Count * 0.10f || atBody < column.Count * 0.15f)
                    continue;

                TopStuds[cell] = true;
                HasTopStuds = true;
            }
        }

        /// <summary>
        /// Whether a column's highest point is the top of a stud: a boss standing a stud's height
        /// above a whole number of layers.
        ///
        /// One rule, asked by both the stud mask and the occupancy mask, so the two cannot disagree
        /// about what a stud is - and disagreeing is exactly how a shelf ends up offering studs at
        /// one height while claiming to be a layer taller.
        /// </summary>
        bool IsStudTop(float highestZ)
        {
            float withoutStud = highestZ - MinMm.z - StudHeightMm;

            // At least one whole layer under it. Without this the test passes at zero, and a part
            // with nothing on it at all reads as studs sitting on the ground.
            return withoutStud >= LayerHeightMm - 0.8f && IsNearMultiple(withoutStud, LayerHeightMm);
        }

        void Raise(float[] highest, Vector3 v)
        {
            int cx = Mathf.Clamp(Mathf.FloorToInt((v.x - MinMm.x) / StudPitchMm), 0, FootprintSize.x - 1);
            int cy = Mathf.Clamp(Mathf.FloorToInt((v.y - MinMm.y) / StudPitchMm), 0, FootprintSize.y - 1);

            int index = cy * FootprintSize.x + cx;
            highest[index] = Mathf.Max(highest[index], v.z);
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

        /// <summary>
        /// Measures a channel that is referenced to a stud shelf instead of to the part's own base.
        ///
        /// The funnels are built this way: an incoming piece plugs onto a two-stud shelf, and the
        /// chute it feeds runs above that shelf rather than above the funnel's floor. Everything else
        /// in the set carries its channel 6.4 mm above its own base, and a piece standing on the shelf
        /// therefore arrives 0.7 mm below the chute - a step up, at the one place a ball is at its
        /// slowest, and it stops there.
        ///
        /// Measured rather than written down, and measured from the two surfaces that actually meet:
        /// the shelf the incoming piece stands on, and the flat the ball runs onto.
        /// </summary>
        void DeriveColliderDrop(List<StlFacet> facets)
        {
            ColliderDropMm = 0f;

            // Only for a part with studs and no channel mouths of its own. A part with ports carries
            // its channels at its own base like everything else, and a plain brick has studs but
            // nothing inward of them to run onto.
            if (TopStuds == null || Ports.Count > 0)
                return;

            float sx = 0f, sy = 0f;
            int studs = 0;

            for (int cy = 0; cy < FootprintSize.y; cy++)
            for (int cx = 0; cx < FootprintSize.x; cx++)
            {
                if (!TopStuds[cy * FootprintSize.x + cx])
                    continue;

                sx += (cx + 0.5f) * StudPitchMm;
                sy += (cy + 0.5f) * StudPitchMm;
                studs++;
            }

            if (studs == 0)
                return;

            sx /= studs;
            sy /= studs;

            // The shelf: the plateau the studs stand on, taken as the commonest height under them
            // rather than as the stud tops less a nominal stud, which assumes a moulding these were
            // not made to.
            float shelf = Plateau(facets, sx, sy, StudPitchMm * 0.5f, out _);

            if (float.IsNaN(shelf))
                return;

            // Inward along the shelf's own axis, not towards the middle of the part. A shelf in a
            // corner has the centre diagonally from it, and a diagonal walk crosses the bowl's slope
            // rather than the chute - which read a different height on each of the three funnels and
            // was the tell that the direction, not the geometry, was wrong.
            var toCentre = new Vector2(FootprintSize.x * StudPitchMm * 0.5f - sx,
                                       FootprintSize.y * StudPitchMm * 0.5f - sy);

            if (toCentre.sqrMagnitude < 1f)
                return;

            Vector2 inward = Mathf.Abs(toCentre.x) > Mathf.Abs(toCentre.y)
                ? new Vector2(Mathf.Sign(toCentre.x), 0f)
                : new Vector2(0f, Mathf.Sign(toCentre.y));

            // Two studs in, which clears the shelf and its lip on every funnel in the set, and read
            // as a plateau rather than as one sample: a single reading can land on a rib.
            float chute = Plateau(facets,
                                  sx + inward.x * StudPitchMm * 2f,
                                  sy + inward.y * StudPitchMm * 2f,
                                  StudPitchMm * 0.4f, out _);

            if (float.IsNaN(chute))
                return;

            // A channel floor's worth above the shelf, give or take. Outside that band what was found
            // is the top of the part, the inside of a wall, or the shelf itself continuing - all of
            // which mean this part simply does not have a shelf-referenced channel.
            float rise = chute - shelf;

            if (rise < ChannelFloorMm - 2f || rise > ChannelFloorMm + 4f)
                return;

            float lip = chute - shelf - ChannelFloorMm;

            // Under a tenth of a millimetre is the moulding, not a step: the set's own channels
            // measure 6.30 to 6.38 against a nominal 6.4.
            // Rounded up to a tenth of a millimetre. The measurement is 0.75; lifting 0.8 leaves the
            // ball arriving a twentieth of a millimetre high, which is a fall in the direction it is
            // already travelling rather than a step against it.
            ColliderDropMm = Mathf.Abs(lip) < 0.15f ? 0f : Mathf.Ceil(lip * 10f) / 10f;

            if (ColliderDropMm != 0f)
                Warnings.Add($"Channel sits {chute - shelf:0.0} mm above its stud shelf where the set " +
                             $"uses {ChannelFloorMm:0.0}; collider dropped {ColliderDropMm:0.00} mm so a " +
                             "ball does not have to climb into it.");
        }

        /// <summary>Top surface at a point given in millimetres from the part's minimum corner.</summary>
        float TopRelative(List<StlFacet> facets, float x, float y)
        {
            float top = float.NaN;

            foreach (StlFacet f in facets)
                if (Covers(f, MinMm.x + x, MinMm.y + y, out float z) && (float.IsNaN(top) || z > top))
                    top = z;

            return float.IsNaN(top) ? float.NaN : top - MinMm.z;
        }

        /// <summary>The commonest surface height within a radius, and the highest one, both above the base.</summary>
        float Plateau(List<StlFacet> facets, float atX, float atY, float radiusMm, out float highest)
        {
            highest = float.NaN;

            var bins = new Dictionary<int, int>();
            float best = float.NaN;
            int bestCount = 0;

            for (float dx = -radiusMm; dx <= radiusMm; dx += 0.5f)
            for (float dy = -radiusMm; dy <= radiusMm; dy += 0.5f)
            {
                float top = TopRelative(facets, atX + dx, atY + dy);
                if (float.IsNaN(top))
                    continue;

                if (float.IsNaN(highest) || top > highest)
                    highest = top;

                // Fine bins: at a fifth of a millimetre the answer quantises to the bin, and the
                // three funnels - identical in this region - came out 0.2 mm apart because of it.
                int bin = Mathf.RoundToInt(top / 0.05f);
                int n = bins.TryGetValue(bin, out int had) ? had + 1 : 1;
                bins[bin] = n;

                if (n > bestCount)
                {
                    bestCount = n;
                    best = bin * 0.05f;
                }
            }

            return best;
        }

        /// <summary>
        /// Finds the shaft a ball falls through: a column of the part, away from its edges, that no
        /// triangle covers at any height.
        ///
        /// Seen from above rather than from any single height, which is what makes it a shaft and not
        /// merely a dip. The funnel's bowl slopes inward, so at the top the opening is wide and near
        /// the bottom it is a throat; only the throat is open all the way through, and the throat is
        /// what the ball actually has to be over.
        ///
        /// Occupancy could not answer this. The masks are per cell, and at the throat the sloping
        /// wall passes through the same cells as the hole, so every one of them reads as solid.
        /// </summary>
        void DeriveDropHole(List<StlFacet> facets)
        {
            DropHoleRadiusMm = 0f;
            DropHoleCentreMm = Vector2.zero;
            _holeSamples = null;

            int w = Mathf.CeilToInt(SizeMm.x / HeightMapRes) + 1;
            int h = Mathf.CeilToInt(SizeMm.y / HeightMapRes) + 1;

            if (w <= 2 || h <= 2)
                return;

            var solid = new bool[w * h];

            foreach (StlFacet f in facets)
            {
                float minX = Mathf.Min(f.A.x, Mathf.Min(f.B.x, f.C.x));
                float maxX = Mathf.Max(f.A.x, Mathf.Max(f.B.x, f.C.x));
                float minY = Mathf.Min(f.A.y, Mathf.Min(f.B.y, f.C.y));
                float maxY = Mathf.Max(f.A.y, Mathf.Max(f.B.y, f.C.y));

                int i0 = Mathf.Clamp(Mathf.FloorToInt((minX - MinMm.x) / HeightMapRes), 0, w - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt((maxX - MinMm.x) / HeightMapRes), 0, w - 1);
                int j0 = Mathf.Clamp(Mathf.FloorToInt((minY - MinMm.y) / HeightMapRes), 0, h - 1);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((maxY - MinMm.y) / HeightMapRes), 0, h - 1);

                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    float x = MinMm.x + i * HeightMapRes;
                    float y = MinMm.y + j * HeightMapRes;

                    // Exact, not the bounding box: a box fill closes the hole from the outside in,
                    // since the triangles of the sloping wall around it span right across the throat.
                    if (Covers(f, x, y, out _))
                        solid[j * w + i] = true;
                }
            }

            // Everything open that can be reached from outside the part is outside the part. What is
            // left is enclosed by geometry on every side, which is what a shaft is.
            var outside = new bool[w * h];
            var queue = new Queue<int>();

            for (int i = 0; i < w; i++)
            {
                Seed(j: 0, i, w, solid, outside, queue);
                Seed(j: h - 1, i, w, solid, outside, queue);
            }

            for (int j = 0; j < h; j++)
            {
                Seed(j, i: 0, w, solid, outside, queue);
                Seed(j, i: w - 1, w, solid, outside, queue);
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int i = index % w;
                int j = index / w;

                if (i > 0) Seed(j, i - 1, w, solid, outside, queue);
                if (i < w - 1) Seed(j, i + 1, w, solid, outside, queue);
                if (j > 0) Seed(j - 1, i, w, solid, outside, queue);
                if (j < h - 1) Seed(j + 1, i, w, solid, outside, queue);
            }

            // The largest enclosed region. A moulded part has small ones too - the inside of every
            // antistud tube is a hole through nothing but air - and the shaft is the one worth
            // pointing at, so they are separated by size rather than by a guess about position.
            var region = new List<int>();
            var best = new List<int>();
            var visited = new bool[w * h];

            for (int start = 0; start < solid.Length; start++)
            {
                if (solid[start] || outside[start] || visited[start])
                    continue;

                region.Clear();
                queue.Enqueue(start);
                visited[start] = true;

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    region.Add(index);

                    int i = index % w;
                    int j = index / w;

                    if (i > 0) Visit(index - 1, solid, outside, visited, queue);
                    if (i < w - 1) Visit(index + 1, solid, outside, visited, queue);
                    if (j > 0) Visit(index - w, solid, outside, visited, queue);
                    if (j < h - 1) Visit(index + w, solid, outside, visited, queue);
                }

                if (region.Count > best.Count)
                {
                    best.Clear();
                    best.AddRange(region);
                }
            }

            float area = best.Count * HeightMapRes * HeightMapRes;
            float radius = Mathf.Sqrt(area / Mathf.PI);

            if (best.Count == 0)
                return;

            float sx = 0f, sy = 0f;

            foreach (int index in best)
            {
                sx += MinMm.x + index % w * HeightMapRes;
                sy += MinMm.y + index / w * HeightMapRes;
            }

            float holeMinX = float.MaxValue, holeMaxX = float.MinValue;
            float holeMinY = float.MaxValue, holeMaxY = float.MinValue;

            foreach (int index in best)
            {
                float x = MinMm.x + index % w * HeightMapRes;
                float y = MinMm.y + index / w * HeightMapRes;

                holeMinX = Mathf.Min(holeMinX, x); holeMaxX = Mathf.Max(holeMaxX, x);
                holeMinY = Mathf.Min(holeMinY, y); holeMaxY = Mathf.Max(holeMaxY, y);
            }

            DropHoleExtentMm = new Vector2(holeMaxX - holeMinX + HeightMapRes,
                                           holeMaxY - holeMinY + HeightMapRes);
            DropHoleFill = area / Mathf.Max(0.001f, DropHoleExtentMm.x * DropHoleExtentMm.y);

            // Whether this is a hole a ball drops through, which is the only kind worth pointing at.
            //
            // Measured against the ball rather than against a shape, because that is the actual
            // question. Both u-turns enclose a gap between their arms that is open from top to
            // bottom - the detector is right that it is a shaft - but it is 18 mm across and the ball
            // is 24.5, so nothing will ever go down it. The funnels' throats are 28 mm and round.
            //
            // The narrow side is what decides it: a slot is as passable as its width, however long it
            // is. Aspect is checked as well, so a wide slot cannot creep in on width alone.
            float narrow = Mathf.Min(DropHoleExtentMm.x, DropHoleExtentMm.y);
            float wide = Mathf.Max(DropHoleExtentMm.x, DropHoleExtentMm.y);

            if (narrow < BallDiameterMm || wide > narrow * 1.5f)
            {
                DropHoleExtentMm = Vector2.zero;
                DropHoleFill = 0f;
                return;
            }

            DropHoleCentreMm = new Vector2(sx / best.Count, sy / best.Count);
            DropHoleRadiusMm = radius;

            var samples = new bool[w * h];
            foreach (int index in best)
                samples[index] = true;

            _holeSamples = samples;
            _holeW = w;
            _holeH = h;
        }

        static void Seed(int j, int i, int w, bool[] solid, bool[] outside, Queue<int> queue)
        {
            int index = j * w + i;

            if (solid[index] || outside[index])
                return;

            outside[index] = true;
            queue.Enqueue(index);
        }

        static void Visit(int index, bool[] solid, bool[] outside, bool[] visited, Queue<int> queue)
        {
            if (solid[index] || outside[index] || visited[index])
                return;

            visited[index] = true;
            queue.Enqueue(index);
        }

        /// <summary>
        /// Whether the shaft takes up the place a stud would stand in this cell.
        ///
        /// The underside around a funnel's throat is flat and at the base plane, so by area alone the
        /// cells over the hole look exactly like cells with an antistud - and the mask claimed the
        /// funnel could be clutched down onto studs it would fall straight past. What decides it is
        /// not how much material the cell has but whether there is any where the stud goes.
        /// </summary>
        bool ShaftBlocksTheStud(int cx, int cy)
        {
            if (_holeSamples == null)
                return false;

            // A stud is 9.5 mm across on a 16 mm pitch, in the middle of its cell.
            const float studRadiusMm = 4.75f;

            float centreX = MinMm.x + (cx + 0.5f) * StudPitchMm;
            float centreY = MinMm.y + (cy + 0.5f) * StudPitchMm;

            int inside = 0;
            int hole = 0;

            int reach = Mathf.CeilToInt(studRadiusMm / HeightMapRes);

            int i0 = Mathf.FloorToInt((centreX - MinMm.x) / HeightMapRes);
            int j0 = Mathf.FloorToInt((centreY - MinMm.y) / HeightMapRes);

            for (int i = i0 - reach; i <= i0 + reach; i++)
            for (int j = j0 - reach; j <= j0 + reach; j++)
            {
                if (i < 0 || j < 0 || i >= _holeW || j >= _holeH)
                    continue;

                float x = MinMm.x + i * HeightMapRes;
                float y = MinMm.y + j * HeightMapRes;

                if ((x - centreX) * (x - centreX) + (y - centreY) * (y - centreY) > studRadiusMm * studRadiusMm)
                    continue;

                inside++;
                if (_holeSamples[j * _holeW + i])
                    hole++;
            }

            // A third of the stud gone is enough. A stud only half over a hole has nothing to grip
            // with, and a cell the shaft merely clips at its rim keeps its antistud.
            return inside > 0 && hole >= inside * 0.34f;
        }

        /// <summary>
        /// Which cells have an antistud: the ones whose underside is flat against the part's base.
        ///
        /// Derived rather than assumed. It used to be a copy of the footprint - every cell the part
        /// covered was declared to have a socket - which is true of a brick and false of half of
        /// anything else. A slide's underside is the back of its channel, curving well above the base
        /// plane, and a tunnel's underside is its roof; neither can clutch onto a stud, and neither
        /// should have a support pillar built up into it.
        ///
        /// Measured by area, not by a single point. Any cell touching an outer wall has some geometry
        /// reaching the base, so asking whether anything reaches it marks the whole part.
        /// </summary>
        void DeriveBottomSockets(List<StlFacet> facets)
        {
            int cells = FootprintSize.x * FootprintSize.y;
            BottomSockets = new bool[cells];

            int w = Mathf.CeilToInt(SizeMm.x / HeightMapRes) + 1;
            int h = Mathf.CeilToInt(SizeMm.y / HeightMapRes) + 1;

            if (w <= 0 || h <= 0)
                return;

            // The lowest surface over each sample, which is the part seen from below.
            var floor = new float[w * h];
            for (int i = 0; i < floor.Length; i++)
                floor[i] = float.PositiveInfinity;

            // Rasterised exactly. A bounding-box fill marks the samples a triangle merely spans, and
            // for the underside that means lowering cells the part does not reach into - a slide
            // curve came out with an antistud in a corner made entirely of empty air, because the
            // box of a face on the curve overhung it.
            foreach (StlFacet f in facets)
            {
                float minX = Mathf.Min(f.A.x, Mathf.Min(f.B.x, f.C.x));
                float maxX = Mathf.Max(f.A.x, Mathf.Max(f.B.x, f.C.x));
                float minY = Mathf.Min(f.A.y, Mathf.Min(f.B.y, f.C.y));
                float maxY = Mathf.Max(f.A.y, Mathf.Max(f.B.y, f.C.y));

                int i0 = Mathf.Clamp(Mathf.FloorToInt((minX - MinMm.x) / HeightMapRes), 0, w - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt((maxX - MinMm.x) / HeightMapRes), 0, w - 1);
                int j0 = Mathf.Clamp(Mathf.FloorToInt((minY - MinMm.y) / HeightMapRes), 0, h - 1);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((maxY - MinMm.y) / HeightMapRes), 0, h - 1);

                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    float x = MinMm.x + i * HeightMapRes;
                    float y = MinMm.y + j * HeightMapRes;

                    if (!Covers(f, x, y, out float z))
                        continue;

                    int index = j * w + i;
                    if (z < floor[index])
                        floor[index] = z;
                }
            }

            // A socket's rim sits on a layer boundary - not necessarily the part's own base. A slide
            // curve carries one pair of antistuds on the floor and another a whole brick up under its
            // raised mouth, and testing only the base plane found the first pair and missed the
            // second, so half the piece looked as though it could not be clutched to anything.
            //
            // Tight, because the question is whether the underside is flat there and not merely
            // whether it passes nearby. A moulded plane reads the same to a hundredth of a
            // millimetre - a real antistud gives sixty-four samples at exactly 0.0 - while a curve
            // crossing the same height gives a scatter through the band. At half a millimetre the
            // scatter counted, and a u-turn slide grew two antistuds in the middle of its ramp.
            const float slack = 0.15f;

            // Measured rather than picked. With the underside rasterised exactly, a cell with an
            // antistud reads between 21% and 36% - the rim and the tube walls, since the rest of the
            // underside is hollow - and a cell without one reads zero. Anywhere in between separates
            // them; 0.15 sits clear of both edges.
            //
            // The old 0.25 was tuned against a bounding-box fill, which smeared every rim across its
            // whole cell and inflated the real figures past it. Once the fill was corrected the
            // threshold was cutting through the middle of the true range, and a slide came out with
            // one antistud instead of eight.
            const float requiredShare = 0.15f;

            var covered = new int[cells];

            // Counted per layer, because a cell's underside is flat at one height and a part may
            // carry antistuds at more than one. The best-supported boundary in each cell is the one
            // that decides it.
            var flatAt = new Dictionary<int, int>[cells];
            for (int i = 0; i < cells; i++)
                flatAt[i] = new Dictionary<int, int>();

            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float z = floor[j * w + i];
                if (float.IsPositiveInfinity(z))
                    continue;

                float x = MinMm.x + i * HeightMapRes;
                float y = MinMm.y + j * HeightMapRes;

                int cx = Mathf.Clamp(Mathf.FloorToInt((x - MinMm.x) / StudPitchMm), 0, FootprintSize.x - 1);
                int cy = Mathf.Clamp(Mathf.FloorToInt((y - MinMm.y) / StudPitchMm), 0, FootprintSize.y - 1);

                int cell = cy * FootprintSize.x + cx;
                covered[cell]++;

                float above = z - MinMm.z;
                int layer = Mathf.RoundToInt(above / LayerHeightMm);

                if (layer < 0 || Mathf.Abs(above - layer * LayerHeightMm) > slack)
                    continue;

                flatAt[cell][layer] = flatAt[cell].TryGetValue(layer, out int n) ? n + 1 : 1;
            }

            for (int cell = 0; cell < cells; cell++)
            {
                if (covered[cell] == 0)
                    continue;

                int best = 0;
                foreach (KeyValuePair<int, int> at in flatAt[cell])
                    best = Mathf.Max(best, at.Value);

                int cx = cell % FootprintSize.x;
                int cy = cell / FootprintSize.x;

                BottomSockets[cell] = best >= covered[cell] * requiredShare &&
                                      !ShaftBlocksTheStud(cx, cy);
            }
        }

        /// <summary>
        /// Top surface height, sampling only the points a triangle actually covers.
        ///
        /// The same map as <see cref="BuildHeightMap"/> but with a point-in-triangle test instead of
        /// a bounding-box fill, for the readings where covering too much is the failure rather than
        /// the safe direction.
        /// </summary>
        float[] BuildExactHeightMap(List<StlFacet> facets, int w, int h)
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

                int i0 = Mathf.Clamp(Mathf.FloorToInt((minX - MinMm.x) / HeightMapRes), 0, w - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt((maxX - MinMm.x) / HeightMapRes), 0, w - 1);
                int j0 = Mathf.Clamp(Mathf.FloorToInt((minY - MinMm.y) / HeightMapRes), 0, h - 1);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((maxY - MinMm.y) / HeightMapRes), 0, h - 1);

                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    float x = MinMm.x + i * HeightMapRes;
                    float y = MinMm.y + j * HeightMapRes;

                    if (!Covers(f, x, y, out float z))
                        continue;

                    int index = j * w + i;
                    if (z > height[index])
                        height[index] = z;
                }
            }

            return height;
        }

        /// <summary>
        /// Whether a facet covers a point seen from above, and the height of it there.
        ///
        /// Barycentric, with the surface interpolated rather than taken from the highest corner: a
        /// large sloping triangle read at its peak everywhere would flatten a bowl into a plateau,
        /// which is exactly the shape this is meant to tell apart from a stud.
        /// </summary>
        static bool Covers(StlFacet f, float x, float y, out float z)
        {
            z = 0f;

            float x1 = f.A.x, y1 = f.A.y, x2 = f.B.x, y2 = f.B.y, x3 = f.C.x, y3 = f.C.y;
            float area = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);

            if (Mathf.Abs(area) < 1e-9f)
                return false;   // edge-on from above, so it covers nothing

            float a = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / area;
            float b = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / area;
            float c = 1f - a - b;

            const float slack = 1e-4f;
            if (a < -slack || b < -slack || c < -slack)
                return false;

            z = a * f.A.z + b * f.B.z + c * f.C.z;
            return true;
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

                    isChannel = !float.IsPositiveInfinity(lowest) && IsChannelFloor(lowest, out snapped) &&
                                HasWallBesideIt(snapped);
                }

                // A run also breaks when the height steps, which keeps two mouths at different
                // levels from merging into one.
                bool continues = isChannel && runStart >= 0 && Mathf.Approximately(snapped, runHeight);

                if (runStart >= 0 && !continues)
                {
                    EmitPort(facing, runStart, cell, runHeight, height, w, h);
                    runStart = -1;
                }

                if (isChannel && runStart < 0)
                {
                    runStart = cell;
                    runHeight = snapped;
                }
            }
        }

        void EmitPort(Facing facing, int fromCell, int toCell, float heightMm, float[] map, int w, int h)
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
                profileMm = SampleMouthProfile(facing, fromCell, toCell, map, w, h),
            });
        }

        /// <summary>
        /// Reads the channel's shape across a mouth.
        ///
        /// Taken a few millimetres inside the part rather than at the very edge, where a chamfer or
        /// the outer wall's rim would be measured instead of the channel the ball actually runs in.
        /// </summary>
        float[] SampleMouthProfile(Facing facing, int fromCell, int toCell, float[] map, int w, int h)
        {
            const int insetMm = 3;

            bool alongY = facing == Facing.West || facing == Facing.East;
            int samplesAlong = alongY ? h : w;
            int cellsAlong = alongY ? FootprintSize.y : FootprintSize.x;

            float perCell = samplesAlong / (float)cellsAlong;
            int from = Mathf.RoundToInt(fromCell * perCell);
            int to = Mathf.RoundToInt(toCell * perCell);

            var profile = new float[Mathf.Max(2, to - from)];

            for (int i = 0; i < profile.Length; i++)
            {
                int s = from + i;

                int across = facing switch
                {
                    Facing.West => insetMm,
                    Facing.East => w - 1 - insetMm,
                    Facing.South => insetMm,
                    _ => h - 1 - insetMm,
                };

                int x = alongY ? across : s;
                int y = alongY ? s : across;

                float value = x >= 0 && y >= 0 && x < w && y < h ? map[y * w + x] : float.NegativeInfinity;

                // Missing geometry reads as the channel floor, which keeps a bridge continuous rather
                // than punching a hole in it.
                profile[i] = float.IsNegativeInfinity(value) ? ChannelFloorMm : value;
            }

            return profile;
        }

        /// <summary>
        /// True when a measured height matches a channel floor: 6.4 mm above some layer boundary.
        /// Returns the exact expected height, so stored ports carry clean values rather than whatever
        /// the sampling happened to land on.
        /// </summary>
        static bool IsChannelFloor(float measured, out float snapped)
        {
            return SitsAt(measured, ChannelFloorMm, ChannelToleranceMm, out snapped);
        }

        /// <summary>
        /// Whether a candidate floor has enough part above it to be a mouth rather than an open top.
        ///
        /// A mouth is a gap in a wall, so there has to be a wall: at least one layer of part standing
        /// above the floor. Without this the funnels' bowl rims came out as mouths - the rim sits at
        /// 28.4 mm and the raised family allows 9.6 + 19.2 = 28.8, so the whole open top of the bowl
        /// read as a channel two millimetres wide of the truth.
        /// </summary>
        bool HasWallBesideIt(float floorMm) => SizeMm.z - floorMm >= LayerHeightMm - 0.8f;

        static bool SitsAt(float measured, float floorMm, float toleranceMm, out float snapped)
        {
            int layer = Mathf.RoundToInt((measured - floorMm) / BrickPitchMm);
            snapped = layer * BrickPitchMm + floorMm;

            return layer >= 0 && Mathf.Abs(measured - snapped) <= toleranceMm;
        }

        /// <summary>
        /// Detects a through-tunnel: an arch in the underside that opens out at an edge.
        ///
        /// bridge_2x3 has one, and it is the whole point of the piece - a ball runs along a channel
        /// beneath while the bridge carries studs above. Modelled as a solid box it simply walls the
        /// ball in.
        ///
        /// A hollow underside alone is not a tunnel: every brick is hollow. The difference is that a
        /// brick's hollow is enclosed by walls that reach the base, so its boundary reads solid all
        /// the way round, while a tunnel reaches the edge and opens out.
        /// </summary>
        void DeriveTunnel(List<StlFacet> facets)
        {
            const float clearanceMm = 4f; // enough to be an opening rather than a moulding detail

            int w = Mathf.CeilToInt(SizeMm.x / HeightMapRes) + 1;
            int h = Mathf.CeilToInt(SizeMm.y / HeightMapRes) + 1;
            if (w <= 4 || h <= 4)
                return;

            float[] underside = BuildUndersideMap(facets, w, h);

            // Read one sample in from each edge, as the port scan does, to skip the outer skin.
            const int inset = 1;

            HasTunnel =
                EdgeIsRaised(underside, w, h, Facing.West, inset, clearanceMm) ||
                EdgeIsRaised(underside, w, h, Facing.East, inset, clearanceMm) ||
                EdgeIsRaised(underside, w, h, Facing.South, inset, clearanceMm) ||
                EdgeIsRaised(underside, w, h, Facing.North, inset, clearanceMm);
        }

        float[] BuildUndersideMap(List<StlFacet> facets, int w, int h)
        {
            var lowest = new float[w * h];
            for (int i = 0; i < lowest.Length; i++)
                lowest[i] = float.PositiveInfinity;

            foreach (StlFacet f in facets)
            {
                float minX = Mathf.Min(f.A.x, Mathf.Min(f.B.x, f.C.x));
                float maxX = Mathf.Max(f.A.x, Mathf.Max(f.B.x, f.C.x));
                float minY = Mathf.Min(f.A.y, Mathf.Min(f.B.y, f.C.y));
                float maxY = Mathf.Max(f.A.y, Mathf.Max(f.B.y, f.C.y));
                float minZ = Mathf.Min(f.A.z, Mathf.Min(f.B.z, f.C.z));

                int i0 = Mathf.Clamp(Mathf.FloorToInt((minX - MinMm.x) / HeightMapRes), 0, w - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt((maxX - MinMm.x) / HeightMapRes), 0, w - 1);
                int j0 = Mathf.Clamp(Mathf.FloorToInt((minY - MinMm.y) / HeightMapRes), 0, h - 1);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((maxY - MinMm.y) / HeightMapRes), 0, h - 1);

                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    int index = j * w + i;
                    if (minZ < lowest[index])
                        lowest[index] = minZ;
                }
            }

            return lowest;
        }

        /// <summary>True when a run of this edge sits clear of the base - a tunnel mouth.</summary>
        static bool EdgeIsRaised(float[] underside, int w, int h, Facing facing, int inset, float clearanceMm)
        {
            bool alongY = facing == Facing.West || facing == Facing.East;
            int samples = alongY ? h : w;

            // A couple of stray samples are moulding detail; a mouth is several millimetres wide.
            const int minimumRun = 6;
            int run = 0;

            for (int s = 0; s < samples; s++)
            {
                int i, j;
                switch (facing)
                {
                    case Facing.West:  i = inset;         j = s; break;
                    case Facing.East:  i = w - 1 - inset; j = s; break;
                    case Facing.South: i = s;             j = inset; break;
                    default:           i = s;             j = h - 1 - inset; break;
                }

                float value = underside[j * w + i];
                bool raised = !float.IsPositiveInfinity(value) && value >= clearanceMm;

                run = raised ? run + 1 : 0;
                if (run >= minimumRun)
                    return true;
            }

            return false;
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
