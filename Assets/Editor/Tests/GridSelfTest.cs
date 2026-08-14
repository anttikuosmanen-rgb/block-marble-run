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
            TestAssemblyMovesTogether();
            TestMirrorsKeepTheirLayerMasks();
            TestRaisedEndIsCarriedAtGroundLevel();
            TestNoPillarUnderTheArcOfACurve();
            TestStraightRampIsCarriedAtItsEndsOnly();
            TestLiftedSupportColumnGrows();
            TestLiftedGroundLevelTrackGetsSupports();
            TestScaffoldingLeavesRoomForTheDescendingPiece();

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

            // The pillar straddles the far mouth: the slide's last cell is y=5, so the brick spans
            // y 5-6. slide_2x4 is solid to its base, so the pillar stops at the part's underside
            // rather than reaching the raised mouth, which it could only do by occupying the part.
            Check("a pillar reaches the far end", map.ColumnRestLayer(0, 5) == 2,
                $"rest layer {map.ColumnRestLayer(0, 5)}");

            // The outer half projects past the mouth, leaving studs already standing at the right
            // height for whatever continues the run.
            Check("the outer half is left ready for the next piece", map.ColumnRestLayer(0, 6) == 2,
                $"rest layer {map.ColumnRestLayer(0, 6)}");
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

        /// <summary>
        /// Raising a structure has to carry everything attached, through studs and through channels
        /// alike, and has to refuse a move that will not fit rather than half-perform it.
        /// </summary>
        static void TestAssemblyMovesTogether()
        {
            var map = new GridMap();
            PartDefinition brick = MakeDef("block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition track = MakeTrack("track_2x2", new Vector2Int(2, 2));

            // A brick, a track resting on it, and a second track joined to the first.
            var bottom = new PlacedPart(brick, new GridCoord(0, 0, 0), 0, 0);
            var carried = new PlacedPart(track, new GridCoord(0, 0, 1), 0, 0);
            var joined = new PlacedPart(track, new GridCoord(0, 2, 1), 0, 0);

            map.Add(bottom);
            map.Add(carried);
            map.Add(joined);

            List<PlacedPart> group = Assembly.Connected(map, bottom);
            Check("stacking and channels both hold an assembly together", group.Count == 3,
                $"found {group.Count}");

            List<PlacedPart> up = Assembly.Shift(map, group, 1);
            Check("a clear move is allowed", up != null && up.Count == 3);
            Check("everything moves by the same amount",
                up != null && up[0].Origin.layer == bottom.Origin.layer + 1);

            // Ground is the floor: the structure cannot be pushed through it.
            Check("a move below the ground is refused", Assembly.Shift(map, group, -1) == null);

            // Something in the way above.
            map.Add(new PlacedPart(brick, new GridCoord(0, 0, 3), 0, 0));
            Check("a blocked move is refused", Assembly.Shift(map, group, 2) == null);

            // A part is allowed into cells its own assembly is vacating.
            List<PlacedPart> single = Assembly.Connected(map, joined);
            Check("an assembly may move into its own vacated cells", Assembly.Shift(map, single, 0) == null);
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

        /// <summary>
        /// Every generated mirror carries per-layer occupancy of its own.
        ///
        /// A part whose layerMasks array does not match its footprint is treated as having none, and
        /// a part with none is a solid prism. Mirrors were being built without them, so a mirrored
        /// slide curve claimed both layers in every column - its raised end looked as though it
        /// reached the ground and the pillar under it stopped a layer short. Nothing announced this:
        /// the fallback is deliberate and silent, and the part still placed and still looked right.
        /// </summary>
        static void TestMirrorsKeepTheirLayerMasks()
        {
            int checkedParts = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (def == null || string.IsNullOrEmpty(def.mirrorOf))
                    continue;

                PartDefinition source = FindDefinition(def.mirrorOf);
                if (source == null || source.layerMasks == null)
                    continue;

                checkedParts++;

                int plane = def.footprintSize.x * def.footprintSize.y;
                int layers = Mathf.Max(1, def.heightLayers);

                Check($"{def.id} has per-layer masks",
                    def.layerMasks != null && def.layerMasks.Length == plane * layers);

                if (def.layerMasks == null || def.layerMasks.Length != plane * layers)
                    continue;

                // And they are the source's, flipped in x - not a copy, which would be just as wrong
                // and would pass a length check.
                bool mirrored = true;

                for (int layer = 0; layer < layers && mirrored; layer++)
                for (int y = 0; y < def.footprintSize.y && mirrored; y++)
                for (int x = 0; x < def.footprintSize.x; x++)
                {
                    int from = layer * plane + y * def.footprintSize.x + x;
                    int to = layer * plane + y * def.footprintSize.x + (def.footprintSize.x - 1 - x);

                    if (def.layerMasks[to] != source.layerMasks[from])
                    {
                        mirrored = false;
                        break;
                    }
                }

                Check($"{def.id} layer masks are {def.mirrorOf} flipped in x", mirrored);
            }

            Check("some mirrors exist to check", checkedParts > 0);
        }

        static PartDefinition FindDefinition(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (def != null && def.id == id)
                    return def;
            }

            return null;
        }

        /// <summary>
        /// A curve sitting at layer 0 still gets a pillar under its raised end.
        ///
        /// The scaffolder used to return early for anything whose origin was on the floor, reading
        /// "layer 0" as "resting on the ground". A slide curve's raised end occupies only its upper
        /// layer, so it hangs over nothing even when the rest of the piece is on the floor.
        /// </summary>
        static void TestRaisedEndIsCarriedAtGroundLevel()
        {
            PartDefinition curve = FindDefinition("slide_curve_4x4");
            PartDefinition pillar = FindDefinition("building_block_2x2");

            if (curve == null || pillar == null)
            {
                Check("slide curve and pillar parts exist", false);
                return;
            }

            var map = new GridMap();
            var part = new PlacedPart(curve, new GridCoord(0, 0, 0), 0, 0);

            List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, part, pillar);

            Check("a curve on the ground still props its raised end", supports.Count > 0,
                $"got {supports.Count} bricks");

            // Under the raised end, not somewhere convenient: the columns whose lowest occupied layer
            // is above the floor are exactly the ones needing carrying.
            bool underRaised = false;

            foreach (PlacedPart brick in supports)
                foreach (GridCoord cell in brick.OccupiedCells())
                    if (cell.layer == 0)
                        underRaised = true;

            Check("that pillar stands on the ground layer", underRaised);
        }

        /// <summary>
        /// No pillar stands under the falling part of a curve's arc.
        ///
        /// The rule that carries a raised mouth's overhang used to step one pillar-width inward
        /// unconditionally. On a curve that is already past the raised end and into the part's own
        /// base, so the brick stood wholly beneath the curved shell - propping something that rested
        /// on the ground regardless, with no antistud above it to clutch to.
        /// </summary>
        static void TestNoPillarUnderTheArcOfACurve()
        {
            PartDefinition curve = FindDefinition("slide_curve_4x4");
            PartDefinition pillar = FindDefinition("building_block_2x2");

            if (curve == null || pillar == null)
            {
                Check("slide curve and pillar parts exist", false);
                return;
            }

            for (int rot = 0; rot < 4; rot++)
            {
                var map = new GridMap();
                var part = new PlacedPart(curve, new GridCoord(0, 0, 1), rot, 0);

                List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, part, pillar);

                // Every brick must be directly under a column whose lowest occupied layer is above it:
                // that is what "carrying" means. A brick under a column the part already rests on with
                // its base is propping nothing.
                foreach (PlacedPart brick in supports)
                {
                    var top = new GridCoord(brick.Origin.x, brick.Origin.y, brick.Origin.layer + 1);

                    bool carries = false;

                    foreach (GridCoord cell in part.OccupiedCells())
                        if (cell.layer >= top.layer &&
                            cell.x >= brick.Origin.x && cell.x < brick.Origin.x + pillar.footprintSize.x &&
                            cell.y >= brick.Origin.y && cell.y < brick.Origin.y + pillar.footprintSize.y)
                            carries = true;

                    Check($"rot {rot}: pillar at ({brick.Origin.x},{brick.Origin.y},{brick.Origin.layer}) carries something",
                        carries);
                }
            }
        }

        /// <summary>
        /// A straight ramp gets pillars under its two ends and nothing in between.
        ///
        /// Its channel climbs while its base stays flat, so the far mouth's channel floor is a layer
        /// above the near one - but nothing about the piece overhangs. Deciding from the channel
        /// height rather than the underside put a surplus pillar under the middle of every one.
        /// </summary>
        static void TestStraightRampIsCarriedAtItsEndsOnly()
        {
            PartDefinition ramp = FindDefinition("slide_2x4");
            PartDefinition pillar = FindDefinition("building_block_2x2");

            if (ramp == null || pillar == null)
            {
                Check("slide_2x4 and pillar parts exist", false);
                return;
            }

            for (int rot = 0; rot < 4; rot++)
            {
                var map = new GridMap();
                var part = new PlacedPart(ramp, new GridCoord(0, 0, 2), rot, 0);

                List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, part, pillar);

                // Two columns of two, one at each mouth.
                var columns = new HashSet<Vector2Int>();
                foreach (PlacedPart brick in supports)
                    columns.Add(new Vector2Int(brick.Origin.x, brick.Origin.y));

                Check($"rot {rot}: a straight ramp stands on two pillars", columns.Count == 2,
                    $"got {columns.Count} columns from {supports.Count} bricks");
            }
        }

        /// <summary>
        /// Raising a build grows the columns under it back down to the ground.
        ///
        /// The lifted parts are already in the map at their new height when the columns are measured,
        /// so asking what stands in a column answered with the very brick needing carrying: the fill
        /// started above it and ran zero times, and every support stayed hanging where the lift left
        /// it.
        /// </summary>
        static void TestLiftedSupportColumnGrows()
        {
            PartDefinition pillar = FindDefinition("building_block_2x2");
            PartDefinition ramp = FindDefinition("slide_2x4");

            if (pillar == null || ramp == null)
            {
                Check("lift test parts exist", false);
                return;
            }

            var map = new GridMap();
            var brick = new PlacedPart(pillar, new GridCoord(0, 0, 0), 0, 0);
            var track = new PlacedPart(ramp, new GridCoord(0, 0, 1), 0, 0);

            map.Add(brick);
            map.Add(track);

            var all = new List<PlacedPart>(map.Parts);
            List<PlacedPart> moved = Assembly.Shift(map, all, 1);

            Check("the build shifts up a layer", moved != null);
            if (moved == null)
                return;

            foreach (PlacedPart part in all) map.Remove(part);
            foreach (PlacedPart part in moved) map.Add(part);

            List<PlacedPart> grown = ScaffoldBuilder.ExtendLiftedColumns(map, moved, pillar);

            Check("the lifted column is grown back to the ground", grown.Count > 0,
                $"added {grown.Count}");

            bool onGround = false;
            foreach (PlacedPart part in grown)
                if (part.Origin.layer == 0)
                    onGround = true;

            Check("and it reaches layer 0", onGround);
        }

        /// <summary>
        /// Track that was resting on the ground is propped once a lift puts air under it.
        ///
        /// Placement scaffolds before the piece joins the map, a lift scaffolds after it has already
        /// moved - and measuring the whole column in the second case answered with the lifted piece
        /// itself, so every anchor decided it was already resting on something.
        /// </summary>
        static void TestLiftedGroundLevelTrackGetsSupports()
        {
            PartDefinition ramp = FindDefinition("slide_2x4");
            PartDefinition pillar = FindDefinition("building_block_2x2");

            if (ramp == null || pillar == null)
            {
                Check("lifted track test parts exist", false);
                return;
            }

            var map = new GridMap();
            var track = new PlacedPart(ramp, new GridCoord(0, 0, 0), 0, 0);
            map.Add(track);

            var all = new List<PlacedPart>(map.Parts);
            List<PlacedPart> moved = Assembly.Shift(map, all, 1);

            Check("ground-level track shifts up", moved != null);
            if (moved == null)
                return;

            foreach (PlacedPart part in all) map.Remove(part);
            foreach (PlacedPart part in moved) map.Add(part);

            // Scaffolded after the move, exactly as the lift command does it.
            int built = 0;
            foreach (PlacedPart part in moved)
                built += ScaffoldBuilder.BuildSupports(map, part, pillar).Count;

            Check("lifted track is given supports", built > 0, $"built {built}");
        }

        /// <summary>
        /// Propping a lifted run must not fill the space the piece being placed is descending into.
        ///
        /// Growing the build and placing underneath is one action, and its order is load bearing.
        /// Scaffolding the raised run first puts a pillar exactly where the new piece was heading,
        /// the placement is refused, and the action rolls itself back leaving no trace of why.
        /// </summary>
        static void TestScaffoldingLeavesRoomForTheDescendingPiece()
        {
            PartDefinition ramp = FindDefinition("slide_2x4");
            PartDefinition pillar = FindDefinition("building_block_2x2");

            if (ramp == null || pillar == null)
            {
                Check("underground test parts exist", false);
                return;
            }

            var map = new GridMap();
            var seed = new PlacedPart(ramp, new GridCoord(0, 0, 0), 0, 0);
            map.Add(seed);

            // The offer the build makes: a mating that lands below the ground.
            PlacedPart candidate = null;

            foreach (PlacedPart.WorldPort target in seed.WorldPorts())
            foreach (PlacedPart mating in PlacementSolver.MatingsWith(map, ramp, 0, target,
                                                                     allowBelowGround: true))
                if (mating.Origin.layer < 0)
                    candidate = mating;

            Check("an underground placement is offered", candidate != null);
            if (candidate == null)
                return;

            int layers = -candidate.Origin.layer;
            var all = new List<PlacedPart>(map.Parts);
            List<PlacedPart> moved = Assembly.Shift(map, all, layers);

            Check("the build lifts to make room", moved != null);
            if (moved == null)
                return;

            foreach (PlacedPart part in all) map.Remove(part);
            foreach (PlacedPart part in moved) map.Add(part);

            var raised = new PlacedPart(candidate.Definition,
                new GridCoord(candidate.Origin.x, candidate.Origin.y, candidate.Origin.layer + layers),
                candidate.Rotation, 0);

            // Placed before the lifted run is propped, which is the order the command uses.
            Check("the piece goes in before the props do", map.CanPlace(raised) != PlacementResult.Blocked);
            map.Add(raised);

            // And propping afterwards must not disturb it.
            foreach (PlacedPart part in moved)
                if (part.HasPorts)
                    ScaffoldBuilder.BuildSupports(map, part, pillar);

            ScaffoldBuilder.ExtendLiftedColumns(map, moved, pillar);

            Check("the placed piece survives the propping", map.Contains(raised));
        }
    }
}
#endif
