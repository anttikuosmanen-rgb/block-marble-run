#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Exercises the placement rules headlessly. Grid logic is the one part of build mode that cannot
    /// be checked by looking at the scene - a wrong rotation or support rule shows up as a brick that
    /// merely feels off, so it needs assertions rather than eyes.
    /// </summary>
    public static class GridSelfTest
    {
        static int _passed;
        static int _failed;
        static StringBuilder _log;

        [MenuItem("Block Marble Run/Run Grid Self Test")]
        public static void Run()
        {
            _passed = 0;
            _failed = 0;
            _log = new StringBuilder();

            TestRotationMatchesTransform();
            TestOccupancyBlocks();
            TestGroundSupport();
            TestStudSupport();
            TestTrackRejectsStacking();
            TestMultiLayerOccupancy();
            TestRemoveFreesCells();
            TestColumnRestLayer();
            TestBridgingTwoBlocks();
            TestChannelsClutchToChannels();
            TestChannelSnapsToElevatedRun();
            TestMismatchedChannelHeightsDoNotConnect();
            TestOffsetChannelsDoNotConnect();
            TestScaffoldingUnderFloatingPart();
            TestScaffoldingUnderConnectedSlide();
            TestDescendingPartPillarHeights();
            TestOffCentreMeshPositioning();
            TestScaffoldingNeverBlocksItsOwnPart();
            TestRampLeavesLowerLayerFree();

            string summary = $"[GridSelfTest] {_passed} passed, {_failed} failed.\n{_log}";
            if (_failed > 0)
                Debug.LogError(summary);
            else
                Debug.Log(summary);
        }

        // --- tests ---------------------------------------------------------------------------

        /// <summary>
        /// The grid cells a rotated part claims must match where its mesh actually ends up. If these
        /// drift apart, parts collide with things they do not visually touch.
        /// </summary>
        static void TestRotationMatchesTransform()
        {
            PartDefinition def = MakeDef("test_2x1", new Vector2Int(2, 1), layers: 1, studs: true);

            for (int rot = 0; rot < 4; rot++)
            {
                var part = new PlacedPart(def, new GridCoord(3, 5, 0), rot, 0);
                part.GetTransform(out Vector3 position, out Quaternion rotation);

                List<GridCoord> cells = part.OccupiedCells().ToList();

                // Centre of the claimed cells, in world space.
                var centre = Vector3.zero;
                foreach (GridCoord c in cells)
                    centre += new Vector3((c.x + 0.5f) * GridCoord.StudUnits, 0f, (c.y + 0.5f) * GridCoord.StudUnits);
                centre /= cells.Count;

                Check($"rot {rot}: transform sits at the centre of its cells",
                    Mathf.Abs(centre.x - position.x) < 1e-4f && Mathf.Abs(centre.z - position.z) < 1e-4f,
                    $"cells centre {centre}, transform {position}");

                Vector2Int size = part.RotatedSize;
                Check($"rot {rot}: footprint size swaps on quarter turns",
                    rot % 2 == 0 ? size == new Vector2Int(2, 1) : size == new Vector2Int(1, 2),
                    $"got {size}");
            }
        }

        static void TestOccupancyBlocks()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            var first = new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0);
            Check("first placement succeeds", map.Add(first));

            var overlapping = new PlacedPart(def, new GridCoord(1, 1, 0), 0, 0);
            Check("overlapping placement is blocked",
                map.CanPlace(overlapping) == PlacementResult.Blocked);

            var adjacent = new PlacedPart(def, new GridCoord(2, 0, 0), 0, 0);
            Check("adjacent placement is valid",
                map.CanPlace(adjacent) == PlacementResult.Valid);
        }

        static void TestGroundSupport()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            Check("ground layer is always supported",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0)) == PlacementResult.Valid);

            Check("floating part is unsupported",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, 3), 0, 0)) == PlacementResult.Unsupported);

            Check("below ground is blocked",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, -1), 0, 0)) == PlacementResult.Blocked);
        }

        static void TestStudSupport()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            map.Add(new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0));

            Check("stacking on studs is valid",
                map.CanPlace(new PlacedPart(def, new GridCoord(0, 0, 1), 0, 0)) == PlacementResult.Valid);

            Check("partial overlap onto studs is valid",
                map.CanPlace(new PlacedPart(def, new GridCoord(1, 1, 1), 0, 0)) == PlacementResult.Valid);

            Check("clear of the studs is unsupported",
                map.CanPlace(new PlacedPart(def, new GridCoord(5, 5, 1), 0, 0)) == PlacementResult.Unsupported);
        }

        /// <summary>Track pieces expose no studs, which is what stops anything stacking on them.</summary>
        static void TestTrackRejectsStacking()
        {
            var map = new GridMap();
            PartDefinition track = MakeDef("track_2x2", new Vector2Int(2, 2), layers: 1, studs: false);
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            map.Add(new PlacedPart(track, new GridCoord(0, 0, 0), 0, 0));

            Check("nothing stacks on a studless part",
                map.CanPlace(new PlacedPart(block, new GridCoord(0, 0, 1), 0, 0)) == PlacementResult.Unsupported);
        }

        static void TestMultiLayerOccupancy()
        {
            var map = new GridMap();
            PartDefinition slide = MakeDef("slide_2x2", new Vector2Int(2, 2), layers: 2, studs: false);
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            var placed = new PlacedPart(slide, new GridCoord(0, 0, 0), 0, 0);
            Check("two-layer part claims eight cells", placed.OccupiedCells().Count() == 8,
                $"got {placed.OccupiedCells().Count()}");

            map.Add(placed);

            Check("upper layer of a tall part blocks placement",
                map.CanPlace(new PlacedPart(block, new GridCoord(0, 0, 1), 0, 0)) == PlacementResult.Blocked);
        }

        static void TestRemoveFreesCells()
        {
            var map = new GridMap();
            PartDefinition def = MakeDef("block_2x2", new Vector2Int(2, 2), layers: 1, studs: true);

            var part = new PlacedPart(def, new GridCoord(0, 0, 0), 0, 0);
            map.Add(part);
            Check("cells are occupied after add", map.CellCount == 4, $"got {map.CellCount}");

            map.Remove(part);
            Check("cells are freed after remove", map.CellCount == 0, $"got {map.CellCount}");
            Check("part list is empty after remove", map.Parts.Count == 0);
        }

        static void TestColumnRestLayer()
        {
            var map = new GridMap();
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition slide = MakeDef("slide_2x2", new Vector2Int(2, 2), 2, studs: false);

            Check("empty column rests on the ground", map.ColumnRestLayer(0, 0) == 0);

            map.Add(new PlacedPart(block, new GridCoord(0, 0, 0), 0, 0));
            Check("column rests above a one-layer part", map.ColumnRestLayer(0, 0) == 1,
                $"got {map.ColumnRestLayer(0, 0)}");

            // A two-layer part must lift the rest height by two, not one.
            map.Add(new PlacedPart(slide, new GridCoord(4, 0, 0), 0, 0));
            Check("column clears a two-layer part", map.ColumnRestLayer(4, 0) == 2,
                $"got {map.ColumnRestLayer(4, 0)}");

            Check("neighbouring column is unaffected", map.ColumnRestLayer(9, 9) == 0);
        }

        /// <summary>
        /// The case that prompted the change: a long part laid across two separated blocks should
        /// rest on top of both, even though the cursor sits over the gap between them where there is
        /// nothing but ground.
        /// </summary>
        static void TestBridgingTwoBlocks()
        {
            var map = new GridMap();
            PartDefinition small = MakeDef("block_1x2", new Vector2Int(2, 1), 1, studs: true);
            PartDefinition longPart = MakeDef("block_2x8", new Vector2Int(8, 1), 1, studs: true);

            // Two supports with a two-stud gap: cells 0-1 and 6-7 occupied, 2-5 empty.
            map.Add(new PlacedPart(small, new GridCoord(0, 0, 0), 0, 0));
            map.Add(new PlacedPart(small, new GridCoord(6, 0, 0), 0, 0));

            var spanning = new PlacedPart(longPart, new GridCoord(0, 0, 0), 0, 0);

            int rest = 0;
            foreach (GridCoord cell in spanning.OccupiedCells())
                if (cell.layer == 0)
                    rest = Mathf.Max(rest, map.ColumnRestLayer(cell.x, cell.y));

            Check("a spanning part rests on top of both supports", rest == 1, $"got layer {rest}");

            var bridged = new PlacedPart(longPart, new GridCoord(0, 0, rest), 0, 0);
            Check("the bridged placement is valid", map.CanPlace(bridged) == PlacementResult.Valid,
                map.CanPlace(bridged).ToString());

            // The gap itself must stay empty underneath - bridging must not fill it in.
            Check("the gap under the bridge stays open", !map.IsOccupied(new GridCoord(3, 0, 0)));
        }

        /// <summary>
        /// A track joined end to end with another track is held up by that joint, exactly as a brick
        /// is held by studs. Without this an elevated run counts as floating along its whole length.
        /// </summary>
        static void TestChannelsClutchToChannels()
        {
            var map = new GridMap();
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition track = MakeTrack("track_2x2", new Vector2Int(2, 2));

            // A block at ground level, with a track resting on top of it at layer 1.
            map.Add(new PlacedPart(block, new GridCoord(0, 0, 0), 0, 0));
            var anchored = new PlacedPart(track, new GridCoord(0, 0, 1), 0, 0);
            Check("track sits on a block", map.CanPlace(anchored) == PlacementResult.Valid,
                map.CanPlace(anchored).ToString());
            map.Add(anchored);

            // The next track continues the run over empty ground - nothing underneath at all.
            var continued = new PlacedPart(track, new GridCoord(0, 2, 1), 0, 0);

            Check("a joined channel is a connection", map.HasPortConnection(continued));
            Check("a joined channel supports the part",
                map.CanPlace(continued) == PlacementResult.Valid, map.CanPlace(continued).ToString());

            // Away from the run, the same part at the same height is unsupported.
            var stranded = new PlacedPart(track, new GridCoord(20, 20, 1), 0, 0);
            Check("an unjoined track at height stays unsupported",
                map.CanPlace(stranded) == PlacementResult.Unsupported);
        }

        /// <summary>
        /// The placement solver must offer the elevated layer when continuing a run, even though the
        /// ground under the cursor is empty and every downward rule would say layer zero.
        /// </summary>
        static void TestChannelSnapsToElevatedRun()
        {
            var map = new GridMap();
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition track = MakeTrack("track_2x2", new Vector2Int(2, 2));

            // Build a track three layers up on a stack of blocks.
            for (int layer = 0; layer < 3; layer++)
                map.Add(new PlacedPart(block, new GridCoord(0, 0, layer), 0, 0));

            map.Add(new PlacedPart(track, new GridCoord(0, 0, 3), 0, 0));

            PlacedPart solved = PlacementSolver.Solve(map, track, 0, 2, 0, 0);

            Check("continuing a run snaps to the run's height", solved.Origin.layer == 3,
                $"got layer {solved.Origin.layer}");
            Check("the snapped placement is valid",
                map.CanPlace(solved) == PlacementResult.Valid, map.CanPlace(solved).ToString());

            // Far from the run there is nothing to join, so it falls to the ground.
            PlacedPart alone = PlacementSolver.Solve(map, track, 30, 30, 0, 0);
            Check("with nothing to join, a track rests on the ground", alone.Origin.layer == 0,
                $"got layer {alone.Origin.layer}");
        }

        /// <summary>
        /// Channel floors sit at 6.4 mm above a layer boundary, so a mouth one layer up is a genuine
        /// mismatch rather than something to be rounded into a joint.
        /// </summary>
        static void TestMismatchedChannelHeightsDoNotConnect()
        {
            var map = new GridMap();
            PartDefinition track = MakeTrack("track_2x2", new Vector2Int(2, 2));

            map.Add(new PlacedPart(track, new GridCoord(0, 0, 0), 0, 0));

            // Adjacent, facing each other, but a layer higher: the channels do not meet.
            var offset = new PlacedPart(track, new GridCoord(0, 2, 1), 0, 0);
            Check("channels at different heights do not connect", !map.HasPortConnection(offset));

            // Side by side rather than end to end: the walls face each other, not the mouths.
            var sideways = new PlacedPart(track, new GridCoord(2, 0, 0), 0, 0);
            Check("walls facing each other do not connect", !map.HasPortConnection(sideways));
        }

        /// <summary>
        /// A channel is two studs wide, so its centre falls between studs. Two runs offset by one
        /// stud must not join: recording ports per cell let exactly that happen, because one cell of
        /// each mouth still overlapped and the joint reported as connected while visibly stepping
        /// sideways.
        /// </summary>
        static void TestOffsetChannelsDoNotConnect()
        {
            var map = new GridMap();
            PartDefinition track = MakeTrack("track_2x2", new Vector2Int(2, 2));

            map.Add(new PlacedPart(track, new GridCoord(0, 0, 0), 0, 0));

            var aligned = new PlacedPart(track, new GridCoord(0, 2, 0), 0, 0);
            Check("aligned channels connect", map.HasPortConnection(aligned));

            var offsetByOne = new PlacedPart(track, new GridCoord(1, 2, 0), 0, 0);
            Check("channels offset by one stud do not connect", !map.HasPortConnection(offsetByOne));

            var offsetByTwo = new PlacedPart(track, new GridCoord(2, 2, 0), 0, 0);
            Check("channels offset clear of each other do not connect", !map.HasPortConnection(offsetByTwo));
        }

        /// <summary>
        /// Placing a channel in mid-air should build its own pillars, immediately, and one undo
        /// should take them away again.
        /// </summary>
        static void TestScaffoldingUnderFloatingPart()
        {
            var map = new GridMap();
            PartDefinition pillar = MakeDef("building_block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition track = MakeTrack("track_2x2", new Vector2Int(2, 2));

            var floating = new PlacedPart(track, new GridCoord(0, 0, 3), 0, 0);
            Check("a floating part starts unsupported",
                map.CanPlace(floating) == PlacementResult.Unsupported);

            List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, floating, pillar);

            Check("pillars are built under it", supports.Count > 0, $"got {supports.Count}");
            Check("the pillar reaches the part", map.ColumnRestLayer(0, 0) == 3,
                $"rest layer {map.ColumnRestLayer(0, 0)}");
            Check("the part is supported once propped", map.CanPlace(floating) == PlacementResult.Valid,
                map.CanPlace(floating).ToString());

            // Ground-level placement needs nothing.
            var grounded = new PlacedPart(track, new GridCoord(20, 20, 0), 0, 0);
            Check("a grounded part needs no pillars",
                ScaffoldBuilder.BuildSupports(map, grounded, pillar).Count == 0);
        }

        /// <summary>
        /// A slide joined to an elevated run at one end still hangs over nothing at the other. The
        /// joint holds it, so a whole-part support test reports it as fine and no pillars appear -
        /// but four studs of unsupported channel is exactly what the player expects to see propped.
        /// </summary>
        static void TestScaffoldingUnderConnectedSlide()
        {
            var map = new GridMap();
            PartDefinition pillar = MakeDef("building_block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition track = MakeTrack("track_2x2", new Vector2Int(2, 2));
            PartDefinition slide = MakeSlide("slide_2x4", new Vector2Int(2, 4));

            // A track two layers up, standing on its own bricks.
            map.Add(new PlacedPart(pillar, new GridCoord(0, 0, 0), 0, 0));
            map.Add(new PlacedPart(pillar, new GridCoord(0, 0, 1), 0, 0));
            map.Add(new PlacedPart(track, new GridCoord(0, 0, 2), 0, 0));

            // A slide continuing north from it, joined at its south mouth.
            var joined = new PlacedPart(slide, new GridCoord(0, 2, 2), 0, 0);
            Check("the slide joins the run", map.HasPortConnection(joined));
            Check("the joint alone makes it supported", map.IsSupported(joined));

            List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, joined, pillar);
            Check("its far end is still propped", supports.Count > 0, $"got {supports.Count}");

            // slide_2x4 is solid to its base, so the pillar stops at the part's underside; it cannot
            // reach the raised mouth without occupying the part itself.
            Check("a pillar reaches the far end", map.ColumnRestLayer(0, 4) == 2,
                $"rest layer {map.ColumnRestLayer(0, 4)}");
        }

        /// <summary>
        /// A descending part has its two mouths a layer apart, so their pillars must differ in height
        /// by one brick. Sizing every pillar to the part's base leaves the raised end a layer short.
        /// </summary>
        static void TestDescendingPartPillarHeights()
        {
            var map = new GridMap();
            PartDefinition pillar = MakeDef("building_block_2x2", new Vector2Int(2, 2), 1, studs: true);

            // A ramp, as slide_curve_4x4 really is: solid at the low end, open underneath the raised
            // end. slide_2x4 by contrast reaches its base everywhere, and there a taller pillar would
            // simply be occupying the part.
            PartDefinition ramp = MakeSlide("ramp_2x4", new Vector2Int(2, 4));
            ramp.layerMasks = new[]
            {
                true, true, true, true, false, false, false, false,  // layer 0: south half only
                true, true, true, true, true, true, true, true,      // layer 1: all of it
            };

            var floating = new PlacedPart(ramp, new GridCoord(0, 0, 2), 0, 0);
            ScaffoldBuilder.BuildSupports(map, floating, pillar);

            // South mouth is the low end, carried to the part's own base.
            Check("the low end is carried to the part's base", map.ColumnRestLayer(0, 0) == 2,
                $"got {map.ColumnRestLayer(0, 0)}");

            // North mouth sits a layer higher and the ramp leaves that space free, so the pillar
            // under it is one brick taller.
            Check("the high end is carried one layer higher", map.ColumnRestLayer(0, 3) == 3,
                $"got {map.ColumnRestLayer(0, 3)}");
        }

        /// <summary>
        /// Not every mesh is modelled centred on its footprint. When the pivot is ignored the geometry
        /// is drawn a stud away from the cells it occupies, which is what made u_turn and u_turn_slide
        /// look like they joined one stud inside the neighbouring piece.
        /// </summary>
        static void TestOffCentreMeshPositioning()
        {
            PartDefinition centred = MakeDef("centred", new Vector2Int(2, 2), 1, studs: true);
            centred.pivotOffsetUnits = Vector2.zero;

            var a = new PlacedPart(centred, new GridCoord(0, 0, 0), 0, 0);
            a.GetTransform(out Vector3 centredPosition, out _);
            Check("a centred mesh sits at the footprint centre",
                Mathf.Abs(centredPosition.x - GridCoord.StudUnits) < 1e-4f,
                $"got {centredPosition.x}");

            // One stud of offset, as u_turn_slide has.
            PartDefinition offset = MakeDef("offset", new Vector2Int(2, 2), 1, studs: true);
            offset.pivotOffsetUnits = new Vector2(0f, GridCoord.StudUnits);

            var b = new PlacedPart(offset, new GridCoord(0, 0, 0), 0, 0);
            b.GetTransform(out Vector3 offsetPosition, out _);
            Check("an off-centre mesh is pulled back by its pivot",
                Mathf.Abs(offsetPosition.z - (centredPosition.z - GridCoord.StudUnits)) < 1e-4f,
                $"got {offsetPosition.z}, expected {centredPosition.z - GridCoord.StudUnits}");

            // Rotating must carry the correction round with the part.
            var rotated = new PlacedPart(offset, new GridCoord(0, 0, 0), 1, 0);
            rotated.GetTransform(out Vector3 rotatedPosition, out _);
            Check("the correction rotates with the part",
                Mathf.Abs(rotatedPosition.x - (GridCoord.StudUnits - GridCoord.StudUnits)) < 1e-3f ||
                Mathf.Abs(rotatedPosition.z - GridCoord.StudUnits) < 1e-3f,
                $"got {rotatedPosition}");
        }

        /// <summary>
        /// Scaffolding is built before the part goes in, so the map cannot see where the part will
        /// be. If a pillar takes a cell the part needs, the part fails to place and the whole action
        /// is silently abandoned - after the ghost has already shown green.
        /// </summary>
        static void TestScaffoldingNeverBlocksItsOwnPart()
        {
            var map = new GridMap();
            PartDefinition pillar = MakeDef("building_block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition slide = MakeSlide("slide_2x4", new Vector2Int(2, 4));

            var part = new PlacedPart(slide, new GridCoord(0, 0, 2), 0, 0);

            List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, part, pillar);
            Check("supports were built", supports.Count > 0, $"got {supports.Count}");

            // The whole point: the part must still fit once its scaffolding exists.
            Check("the part still places after its supports",
                map.CanPlace(part) != PlacementResult.Blocked, map.CanPlace(part).ToString());
            Check("the part actually goes in", map.Add(part));
        }

        /// <summary>
        /// A ramp whose underside rises leaves genuine space below its raised end - space a pillar
        /// has to be able to stand in.
        /// </summary>
        static void TestRampLeavesLowerLayerFree()
        {
            PartDefinition ramp = MakeSlide("ramp_2x2", new Vector2Int(2, 2));

            // Lower layer solid at the south end only; upper layer solid throughout.
            ramp.layerMasks = new[]
            {
                true, true, false, false,   // layer 0
                true, true, true, true,     // layer 1
            };

            var placed = new PlacedPart(ramp, new GridCoord(0, 0, 0), 0, 0);
            var occupied = new HashSet<GridCoord>(placed.OccupiedCells());

            Check("the solid end fills the lower layer", occupied.Contains(new GridCoord(0, 0, 0)));
            Check("the raised end leaves the lower layer free", !occupied.Contains(new GridCoord(0, 1, 0)));
            Check("the raised end fills the upper layer", occupied.Contains(new GridCoord(0, 1, 1)));

            var map = new GridMap();
            map.Add(placed);

            // A brick can therefore be tucked underneath the raised end.
            PartDefinition brick = MakeDef("brick", new Vector2Int(2, 1), 1, studs: true);
            Check("a brick fits under the raised end",
                map.CanPlace(new PlacedPart(brick, new GridCoord(0, 1, 0), 0, 0)) != PlacementResult.Blocked);
        }

        // --- helpers -------------------------------------------------------------------------

        /// <summary>
        /// A straight channel running along Y, mirroring the real track parts: mouths on the south
        /// and north edges with floors 6.4 mm above the part's base, and no studs on top.
        /// </summary>
        static PartDefinition MakeTrack(string id, Vector2Int size)
        {
            PartDefinition def = MakeDef(id, size, 1, studs: false);

            // One mouth per end, two studs wide, centred on the footprint: in half-studs the centre
            // of a 2-wide part is at 2, which is a stud boundary rather than a stud centre.
            def.ports = new[]
            {
                new TrackPort
                {
                    midlineHalfStuds = new Vector2Int(size.x, 0),
                    facing = Facing.South,
                    heightMm = 6.4f,
                    widthStuds = size.x,
                },
                new TrackPort
                {
                    midlineHalfStuds = new Vector2Int(size.x, size.y * 2),
                    facing = Facing.North,
                    heightMm = 6.4f,
                    widthStuds = size.x,
                },
            };

            return def;
        }

        /// <summary>A descending channel: enters low at the south, leaves one layer up at the north.</summary>
        static PartDefinition MakeSlide(string id, Vector2Int size)
        {
            PartDefinition def = MakeDef(id, size, 2, studs: false);

            def.ports = new[]
            {
                new TrackPort
                {
                    midlineHalfStuds = new Vector2Int(size.x, 0),
                    facing = Facing.South,
                    heightMm = 6.4f,
                    widthStuds = size.x,
                },
                new TrackPort
                {
                    midlineHalfStuds = new Vector2Int(size.x, size.y * 2),
                    facing = Facing.North,
                    heightMm = 25.6f,
                    widthStuds = size.x,
                },
            };

            return def;
        }

        static PartDefinition MakeDef(string id, Vector2Int size, int layers, bool studs)
        {
            var def = ScriptableObject.CreateInstance<PartDefinition>();
            def.id = id;
            def.displayName = id;
            def.footprintSize = size;
            def.heightLayers = layers;

            int count = size.x * size.y;
            def.footprintMask = Enumerable.Repeat(true, count).ToArray();
            def.bottomSockets = Enumerable.Repeat(true, count).ToArray();
            def.topStuds = Enumerable.Repeat(studs, count).ToArray();

            return def;
        }

        static void Check(string what, bool condition, string detail = null)
        {
            if (condition)
            {
                _passed++;
                return;
            }

            _failed++;
            _log.AppendLine($"  FAIL: {what}{(detail != null ? $" - {detail}" : "")}");
        }
    }
}
#endif
