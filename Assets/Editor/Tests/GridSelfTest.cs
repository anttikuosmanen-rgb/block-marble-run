#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BlockMarbleRun.Build;
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
            TestSelectionTurnsInPlace();
            TestSelectionMirrorSwapsHandedParts();
            TestProceduralPillarHeights();
            TestStudsAreFoundByHeightAboveTheGrid();
            TestPointerTrustRecoversAfterAJump();
            TestPointerTrustRecoversAfterAResolutionChange();
            TestFunnelsHaveADropHoleAndOthersDoNot();
            TestMirrorsKeepTheirDropHole();
            TestABowlIsNotMistakenForStuds();
            TestALiftedPillarIsRecutRatherThanStackedOn();
            TestALiftedScaffoldBrickBecomesAPillar();
            TestATallGapIsFilledByOnePillar();
            TestLevelsOfferedOverABuild();
            TestARaisedUndersideCanMeetAStud();
            TestClearingTheBuildCanBeUndone();
            TestPlatesAreHalfABrick();
            TestStackingOnAStepAboveTheGround();
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

        /// <summary>
        /// Doubt about the pointer ends by itself, wherever the pointer went.
        ///
        /// The bug this exists for: doubt was re-armed on every frame the reading sat far from the
        /// last trusted position, and only trusted frames updated that position. A pointer that
        /// jumped and stayed - not a false reading at all, just a fast movement or a window that
        /// changed underneath it - satisfied both conditions forever, and placing pieces stopped
        /// working until the pointer happened to wander back.
        /// </summary>
        static void TestPointerTrustRecoversAfterAJump()
        {
            var trust = new PointerTrust();
            var screen = new Vector2(1600f, 900f);
            float now = 10f;

            Check("first reading is trusted", !trust.IsSuspect(new Vector2(800f, 400f), screen, now));

            // Straight across the window in one frame: nobody's hand does that.
            now += 0.016f;
            Check("a jump is doubted", trust.IsSuspect(new Vector2(20f, 20f), screen, now));

            // Still down in the corner a frame later. This is the case the old rule got wrong: the
            // reading no longer looks like a jump, it looks like a stationary pointer.
            now += 0.016f;
            Check("the frames after a jump are doubted too",
                  trust.IsSuspect(new Vector2(22f, 21f), screen, now));

            // Past the window, with the pointer still nowhere near where it was trusted last.
            now += 0.3f;
            Check("doubt expires where the pointer now is",
                  !trust.IsSuspect(new Vector2(22f, 21f), screen, now));

            now += 0.016f;
            Check("and the new position is what the next frame is measured against",
                  !trust.IsSuspect(new Vector2(40f, 30f), screen, now));
        }

        /// <summary>
        /// A resolution change is not a mouse movement.
        ///
        /// Entering fullscreen moves every reading at once, and measuring the first one against a
        /// position from the old canvas makes an ordinary pointer look like it crossed the window.
        /// Under the old rule it went on looking that way, because it never went back - which is why
        /// the editor came up able to place pieces and lost the ability on going fullscreen.
        /// </summary>
        static void TestPointerTrustRecoversAfterAResolutionChange()
        {
            var trust = new PointerTrust();
            var windowed = new Vector2(1600f, 900f);
            var full = new Vector2(3456f, 2160f);
            float now = 10f;

            trust.IsSuspect(new Vector2(800f, 400f), windowed, now);

            now += 0.016f;
            Check("the frame the screen changes is doubted",
                  trust.IsSuspect(new Vector2(1700f, 1200f), full, now));

            now += 0.3f;
            Check("and trust returns in the new coordinates",
                  !trust.IsSuspect(new Vector2(1700f, 1200f), full, now));

            now += 0.016f;
            Check("with movement judged at the new size",
                  !trust.IsSuspect(new Vector2(1750f, 1240f), full, now));
        }

        /// <summary>
        /// A funnel has a hole a ball fits through; nothing else in the set claims one.
        ///
        /// Both u-turns enclose a gap between their arms that is genuinely open from top to bottom,
        /// so "no material above" is not the test - the gap is 18 mm across against a 24.5 mm ball,
        /// and marking it would draw a target on the one place a ball cannot go.
        /// </summary>
        static void TestFunnelsHaveADropHoleAndOthersDoNot()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (def == null)
                    continue;

                bool funnel = def.id.StartsWith("funnel");

                if (funnel)
                {
                    // In world units, where a unit is 10 cm: the ball is 0.245 across.
                    Check($"{def.id} has a hole", def.dropHoleRadiusUnits > 0f);
                    Check($"{def.id}'s hole passes a ball",
                          def.dropHoleRadiusUnits * 2f >= 0.245f,
                          $"{def.dropHoleRadiusUnits * 200f:0.#} mm across");
                }
                else
                {
                    Check($"{def.id} has no hole", def.dropHoleRadiusUnits <= 0f,
                          $"claims {def.dropHoleRadiusUnits * 200f:0.#} mm across");
                }
            }
        }

        /// <summary>
        /// A mirrored funnel's hole is mirrored with it.
        ///
        /// The same omission as the layer masks and the tunnel flag before it, and with the same
        /// shape of consequence: the piece looks right, places right, and points at the wrong spot.
        /// </summary>
        static void TestMirrorsKeepTheirDropHole()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:PartDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (def == null || string.IsNullOrEmpty(def.mirrorOf))
                    continue;

                PartDefinition source = null;

                foreach (string other in AssetDatabase.FindAssets("t:PartDefinition"))
                {
                    var candidate = AssetDatabase.LoadAssetAtPath<PartDefinition>(
                        AssetDatabase.GUIDToAssetPath(other));

                    if (candidate != null && candidate.id == def.mirrorOf)
                        source = candidate;
                }

                if (source == null)
                    continue;

                Check($"{def.id} keeps its source's hole size",
                      Mathf.Approximately(def.dropHoleRadiusUnits, source.dropHoleRadiusUnits));

                Check($"{def.id}'s hole is mirrored in x",
                      Mathf.Approximately(def.dropHoleOffsetUnits.x, -source.dropHoleOffsetUnits.x) &&
                      Mathf.Approximately(def.dropHoleOffsetUnits.y, source.dropHoleOffsetUnits.y));
            }
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

                // Everything a mirror inherits unchanged, checked as a set rather than one at a time.
                // Mirror generation copies field by field, so the failure mode is not a wrong value
                // but a missing line - and a missing line is invisible until a part behaves oddly.
                // hasTunnel was the one that got away: the mirrored funnel lost the flag that routes
                // a part to its real geometry, and was given a solid box for a collider.
                Check($"{def.id} keeps its source's tunnel flag", def.hasTunnel == source.hasTunnel);
                Check($"{def.id} keeps its source's height", def.heightLayers == source.heightLayers);
                Check($"{def.id} keeps its source's footprint", def.footprintSize == source.footprintSize);
                Check($"{def.id} keeps its source's port count",
                    (def.ports?.Length ?? 0) == (source.ports?.Length ?? 0));

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

            ScaffoldBuilder.Plate = FindDefinition("building_block_2x2_plate");

            var map = new GridMap();
            var brick = new PlacedPart(pillar, new GridCoord(0, 0, 0), 0, 0);

            // On top of the brick, not one layer above the ground: the grid steps half a brick now,
            // so a brick standing on the floor fills layers 0 and 1 and the track goes at 2.
            var track = new PlacedPart(ramp, new GridCoord(0, 0, pillar.heightLayers), 0, 0);

            Check("the seeded brick goes down", map.Add(brick));
            Check("the seeded track goes on top of it", map.Add(track));

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

            ScaffoldBuilder.Plate = FindDefinition("building_block_2x2_plate");

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

        /// <summary>
        /// Turning a selection four times puts every piece back exactly where it started.
        ///
        /// The strongest check available without hand-computing coordinates: any error in the corner
        /// arithmetic accumulates, so four turns that close the loop means the pivot and the width
        /// term are both right. Turning once and eyeballing one part would not catch a drift.
        /// </summary>
        static void TestSelectionTurnsInPlace()
        {
            PartDefinition ramp = FindDefinition("slide_2x4");
            PartDefinition brick = FindDefinition("building_block_2x6");

            if (ramp == null || brick == null)
            {
                Check("turn test parts exist", false);
                return;
            }

            var map = new GridMap();

            var a = new PlacedPart(ramp, new GridCoord(2, 3, 0), 0, 0);
            var b = new PlacedPart(brick, new GridCoord(2, 7, 0), 1, 0);

            map.Add(a);
            map.Add(b);

            var group = new List<PlacedPart> { a, b };
            var expected = new List<(GridCoord origin, int rotation)> { (a.Origin, a.Rotation), (b.Origin, b.Rotation) };

            for (int turn = 0; turn < 4; turn++)
            {
                List<PlacedPart> moved = SelectionOps.Rotate(map, group, 1);

                Check($"turn {turn + 1} fits", moved != null);
                if (moved == null)
                    return;

                foreach (PlacedPart part in group) map.Remove(part);
                foreach (PlacedPart part in moved) map.Add(part);

                group = moved;
            }

            for (int i = 0; i < group.Count; i++)
            {
                Check("four turns restore the origin", group[i].Origin.Equals(expected[i].origin),
                    $"{group[i].Origin} vs {expected[i].origin}");

                Check("four turns restore the rotation", group[i].Rotation == expected[i].rotation,
                    $"{group[i].Rotation} vs {expected[i].rotation}");
            }
        }

        /// <summary>
        /// Mirroring swaps a chiral part for its opposite hand, and mirroring twice is the identity.
        ///
        /// Leaving the same part in place would look almost right - the mouths still line up - while
        /// bending the wrong way, which is the kind of wrong that is only noticed once a ball runs.
        /// </summary>
        static void TestSelectionMirrorSwapsHandedParts()
        {
            PartDefinition curve = FindDefinition("slide_curve_4x4");
            PartDefinition mirrored = FindDefinition("slide_curve_4x4_mirror");

            if (curve == null || mirrored == null)
            {
                Check("mirror test parts exist", false);
                return;
            }

            PartDefinition Twin(PartDefinition def) =>
                def == curve ? mirrored : def == mirrored ? curve : def;

            var map = new GridMap();
            var part = new PlacedPart(curve, new GridCoord(1, 1, 0), 1, 0);
            map.Add(part);

            var group = new List<PlacedPart> { part };

            List<PlacedPart> once = SelectionOps.Mirror(map, group, Twin);
            Check("a curve can be mirrored", once != null);
            if (once == null)
                return;

            Check("mirroring swaps in the other hand", once[0].Definition == mirrored,
                once[0].Definition.id);

            foreach (PlacedPart p in group) map.Remove(p);
            foreach (PlacedPart p in once) map.Add(p);

            List<PlacedPart> twice = SelectionOps.Mirror(map, once, Twin);
            Check("it can be mirrored back", twice != null);
            if (twice == null)
                return;

            Check("mirroring twice restores the part", twice[0].Definition == curve);
            Check("mirroring twice restores the origin", twice[0].Origin.Equals(part.Origin),
                $"{twice[0].Origin} vs {part.Origin}");
            Check("mirroring twice restores the rotation", twice[0].Rotation == part.Rotation);
        }

        /// <summary>
        /// A generated pillar is exactly as tall as it was asked to be, and refuses when it cannot be.
        ///
        /// Height is the whole contract: a support column one layer short leaves the run it carries
        /// hanging, and one layer long lifts it off the thing it was joined to. The stretch has to
        /// land on the grid exactly, not nearly.
        /// </summary>
        static void TestProceduralPillarHeights()
        {
            PartDefinition source = FindDefinition("pillar_2x2x7");

            if (source?.mesh == null)
            {
                Check("pillar source part exists", false);
                return;
            }

            Check("the pillar mesh is readable", source.mesh.isReadable,
                "a template that cannot be read cannot be cut");

            var pillars = new ProceduralPillars(source);

            // What the modelled pillar carries over a whole number of layers - studs, and whatever
            // the model is out by. The generated ones must carry exactly the same.
            float overshoot = source.mesh.bounds.size.y - source.heightLayers * GridCoord.LayerUnits;

            foreach (int layers in new[] { 3, 4, 7, 11, 25 })
            {
                PartDefinition def = pillars.ForLayers(layers);

                if (layers < pillars.ShortestLayers)
                {
                    Check($"{layers} layers is refused as too short", def == null);
                    continue;
                }

                Check($"a {layers} layer pillar is made", def != null);
                if (def == null)
                    continue;

                Check($"{layers} layer pillar is {layers} layers", def.heightLayers == layers);

                float expected = layers * GridCoord.LayerUnits + overshoot;

                Check($"{layers} layer pillar measures its height",
                    Mathf.Abs(def.mesh.bounds.size.y - expected) < 0.0005f,
                    $"{def.mesh.bounds.size.y:0.0000} vs {expected:0.0000}");

                // Stretching must not lose or add geometry: the shaft is moved, never rebuilt.
                Check($"{layers} layer pillar keeps the source geometry",
                    def.mesh.vertexCount == source.mesh.vertexCount);
            }

            Check("a pillar shorter than the base and top together is refused",
                pillars.ForLayers(1) == null);

            // The name is the contract with the save file.
            ProceduralPillars.Active = pillars;
            PartDefinition resolved = ProceduralPillars.Resolve(ProceduralPillars.IdPrefix + 9);

            Check("a saved pillar id resolves back", resolved != null && resolved.heightLayers == 9);
        }

        /// <summary>
        /// Studs are recognised on the parts that have them and not on the parts that do not.
        ///
        /// The rule changed from "anything above the part's body height" to "a boss standing a stud's
        /// height above a layer boundary", because the first only works while the studs are the
        /// tallest thing on the part - true of every brick, false of a funnel whose rim stands over
        /// the shelf it offers. This pins the parts that were already right, since the risk in a rule
        /// like this is not the part it was written for but the twenty it was not.
        /// </summary>
        static void TestStudsAreFoundByHeightAboveTheGrid()
        {
            void Studded(string id, bool expected)
            {
                PartDefinition def = FindDefinition(id);
                if (def == null)
                    return;   // a part this build does not carry is not a failure of the rule

                bool any = false;
                if (def.topStuds != null)
                    foreach (bool stud in def.topStuds)
                        any |= stud;

                Check($"{id} {(expected ? "has" : "has no")} studs", any == expected);
            }

            // Things you stack on.
            Studded("building_block_2x2", true);
            Studded("building_block_2x6", true);
            Studded("pillar_2x2x7", true);
            Studded("pillar_2x2x10", true);

            // A bridge is studded so track can cross over it.
            Studded("bridge_2x3", true);

            // Track a marble runs along has a clear top, and a terminal is a dead end, not a platform.
            Studded("track_2x4", false);
            Studded("slide_2x4", false);
            Studded("terminal_2x2", false);
        }

        /// <summary>
        /// Every brick has a plate, and a plate is exactly half of it.
        ///
        /// Half is the whole point: the grid steps half a brick so that half-height parts have
        /// somewhere to stand, and a plate that came out a hair over would sit its studs off the grid
        /// and refuse to carry anything. Its footprint and studs must be the brick's own, since it is
        /// the same part with less wall.
        /// </summary>
        static void TestPlatesAreHalfABrick()
        {
            int checkedPlates = 0;

            foreach (string id in new[] { "building_block_2x2", "building_block_2x6", "building_block_1x2" })
            {
                PartDefinition brick = FindDefinition(id);
                PartDefinition plate = FindDefinition(id + "_plate");

                if (brick == null || plate == null)
                    continue;

                checkedPlates++;

                Check($"{id} plate is half the brick's layers",
                    plate.heightLayers * 2 == brick.heightLayers,
                    $"{plate.heightLayers} vs {brick.heightLayers}");

                Check($"{id} plate keeps the footprint", plate.footprintSize == brick.footprintSize);

                // Measured, not assumed: the mesh is where the compression could go wrong.
                if (plate.mesh != null && brick.mesh != null)
                {
                    float removed = brick.mesh.bounds.size.y - plate.mesh.bounds.size.y;

                    Check($"{id} plate mesh is one layer shorter",
                        Mathf.Abs(removed - GridCoord.LayerUnits) < 0.0005f,
                        $"removed {removed:0.0000}, wanted {GridCoord.LayerUnits:0.0000}");

                    Check($"{id} plate keeps its footprint in x and z",
                        Mathf.Abs(plate.mesh.bounds.size.x - brick.mesh.bounds.size.x) < 0.0005f &&
                        Mathf.Abs(plate.mesh.bounds.size.z - brick.mesh.bounds.size.z) < 0.0005f);
                }

                bool brickStuds = false, plateStuds = false;
                foreach (bool s in brick.topStuds ?? System.Array.Empty<bool>()) brickStuds |= s;
                foreach (bool s in plate.topStuds ?? System.Array.Empty<bool>()) plateStuds |= s;

                Check($"{id} plate keeps its studs", plateStuds == brickStuds);
            }

            Check("some plates exist to check", checkedPlates > 0);
        }

        /// <summary>
        /// A part whose surface steps can be built on at each step, not only at its highest point.
        ///
        /// The funnel is the case: three layers tall at the rim, with a shelf one layer up that the
        /// next piece is meant to clutch onto. Asking the part how tall it is answers about the rim,
        /// so anything placed on the shelf was judged to be floating two layers below where the part
        /// ended, and nothing could be stacked there at all.
        /// </summary>
        static void TestStackingOnAStepAboveTheGround()
        {
            PartDefinition funnel = FindDefinition("funnel_6x7");
            PartDefinition brick = FindDefinition("building_block_2x2");

            if (funnel == null || brick == null)
            {
                Check("funnel and brick exist", false);
                return;
            }

            var map = new GridMap();
            var part = new PlacedPart(funnel, new GridCoord(0, 0, 0), 0, 0);

            Check("the funnel places on the ground", map.Add(part));

            // Every column carrying a stud, and how high that column stands.
            var shelves = new List<(int X, int Y, int Top)>();

            for (int y = 0; y < funnel.footprintSize.y; y++)
            for (int x = 0; x < funnel.footprintSize.x; x++)
            {
                if (!part.HasTopStudAt(x, y))
                    continue;

                shelves.Add((x, y, part.TopLayerAt(x, y)));
            }

            Check("the funnel offers studs somewhere", shelves.Count > 0);
            if (shelves.Count == 0)
                return;

            // Below the rim, which is the whole point - a stud at the part's full height would have
            // worked under the old rule and proves nothing.
            bool stepped = false;
            foreach ((int _, int _, int top) in shelves)
                stepped |= top < part.TopLayer;

            Check("its studs sit below the part's full height", stepped,
                $"studs at {shelves[0].Top}, part top {part.TopLayer}");

            foreach ((int x, int y, int top) in shelves)
            {
                var stacked = new PlacedPart(brick, new GridCoord(x, y, top), 0, 0);

                Check($"a brick rests on the shelf at ({x},{y},{top})",
                    map.CanPlace(stacked) != PlacementResult.Blocked && map.IsSupported(stacked));
            }
        }

        /// <summary>
        /// A funnel has studs on its shelf and nowhere else.
        ///
        /// Its bowl slopes continuously from the rim down to the throat, so somewhere in that slope
        /// every cell passes through the height a stud top would be at - and reading only a cell's
        /// highest point put a ring of studs around every large funnel that has none. A stud is a
        /// flat disc standing on a flat surface, and a slope is neither.
        /// </summary>
        static void TestABowlIsNotMistakenForStuds()
        {
            int checkedFunnels = 0;

            foreach (string id in new[] { "funnel_6x7", "funnel_8x9", "funnel_10x10" })
            {
                PartDefinition funnel = FindDefinition(id);
                if (funnel?.topStuds == null)
                    continue;

                checkedFunnels++;

                int studs = 0;
                foreach (bool stud in funnel.topStuds)
                    if (stud)
                        studs++;

                // The lip, and only the lip. Two studs is what the part is modelled with.
                Check($"{id} has exactly its lip studs", studs == 2, $"found {studs}");

                // And they are together on one edge, not scattered around a rim.
                var found = new List<Vector2Int>();
                for (int y = 0; y < funnel.footprintSize.y; y++)
                for (int x = 0; x < funnel.footprintSize.x; x++)
                    if (funnel.topStuds[y * funnel.footprintSize.x + x])
                        found.Add(new Vector2Int(x, y));

                if (found.Count == 2)
                    Check($"{id} lip studs are side by side",
                        Mathf.Abs(found[0].x - found[1].x) + Mathf.Abs(found[0].y - found[1].y) == 1,
                        $"{found[0]} and {found[1]}");
            }

            Check("some funnels exist to check", checkedFunnels > 0);
        }

        /// <summary>
        /// A pillar carried up with a copied group is made longer, not stood on a tower of bricks.
        ///
        /// It is one part cut to a height, and lifting it changes the height it needs. Filling the
        /// gap underneath works structurally and looks like scaffolding holding up scaffolding.
        /// </summary>
        static void TestALiftedPillarIsRecutRatherThanStackedOn()
        {
            PartDefinition source = FindDefinition("pillar_2x2x7");
            PartDefinition brick = FindDefinition("building_block_2x2");

            if (source?.mesh == null || brick == null)
            {
                Check("pillar test parts exist", false);
                return;
            }

            ProceduralPillars.Active = new ProceduralPillars(source);
            ScaffoldBuilder.Plate = FindDefinition("building_block_2x2_plate");

            var map = new GridMap();

            // A pillar standing clear of the ground, as one pasted higher up arrives.
            const int lift = 4;
            var hanging = new PlacedPart(source, new GridCoord(0, 0, lift), 0, 0);
            map.Add(hanging);

            var lengthened = new List<(PlacedPart Old, PlacedPart New)>();
            List<PlacedPart> added = ScaffoldBuilder.ExtendLiftedColumns(
                map, new List<PlacedPart> { hanging }, brick, lengthened);

            Check("the pillar is re-cut rather than propped", lengthened.Count == 1,
                $"{lengthened.Count} re-cut, {added.Count} bricks added");

            if (lengthened.Count != 1)
                return;

            (PlacedPart old, PlacedPart taller) = lengthened[0];

            Check("nothing was stacked underneath it", added.Count == 0, $"{added.Count} added");
            Check("the new pillar reaches the ground", taller.Origin.layer == 0);

            Check("and is longer by exactly the gap",
                taller.Definition.heightLayers == old.Definition.heightLayers + lift,
                $"{taller.Definition.heightLayers} vs {old.Definition.heightLayers} + {lift}");

            Check("it is still a pillar", ProceduralPillars.Active.IsPillar(taller.Definition));

            // The old one must be gone from the map, or the two occupy the same cells.
            Check("the shorter pillar was taken out", !map.Contains(old));
            Check("the longer one is in", map.Contains(taller));
        }

        /// <summary>
        /// A scaffold brick carried up becomes a pillar, and a brick the player laid does not.
        ///
        /// The first is the game's own propping and may be replaced by whatever does the job best.
        /// The second is someone's build, and quietly turning it into a pillar because it happened to
        /// be lifted would be editing their creation for them.
        /// </summary>
        static void TestALiftedScaffoldBrickBecomesAPillar()
        {
            PartDefinition source = FindDefinition("pillar_2x2x7");
            PartDefinition brick = FindDefinition("building_block_2x2");

            if (source?.mesh == null || brick == null)
            {
                Check("pillar test parts exist", false);
                return;
            }

            ProceduralPillars.Active = new ProceduralPillars(source);
            ScaffoldBuilder.Plate = FindDefinition("building_block_2x2_plate");

            const int lift = 8;

            // The scaffolder's own brick, in the colour it places them.
            var map = new GridMap();
            var ours = new PlacedPart(brick, new GridCoord(0, 0, lift), 0, ScaffoldBuilder.ScaffoldColour);
            map.Add(ours);

            var lengthened = new List<(PlacedPart Old, PlacedPart New)>();
            ScaffoldBuilder.ExtendLiftedColumns(map, new List<PlacedPart> { ours }, brick, lengthened);

            Check("a lifted scaffold brick is replaced by a pillar", lengthened.Count == 1);

            if (lengthened.Count == 1)
            {
                Check("the pillar reaches the ground", lengthened[0].New.Origin.layer == 0);
                Check("and it is a pillar", ProceduralPillars.Active.IsPillar(lengthened[0].New.Definition));
            }

            // The player's own brick, in a colour they chose.
            var theirs = new GridMap();
            var mine = new PlacedPart(brick, new GridCoord(0, 0, lift), 0, 0);
            theirs.Add(mine);

            var untouched = new List<(PlacedPart Old, PlacedPart New)>();
            ScaffoldBuilder.ExtendLiftedColumns(theirs, new List<PlacedPart> { mine }, brick, untouched);

            Check("a brick the player laid is left alone", untouched.Count == 0);
            Check("and it is still in the map where they put it", theirs.Contains(mine));
        }

        /// <summary>
        /// A tall gap under a lifted run is closed by one pillar, not a stack of bricks.
        ///
        /// The filler used to reach only for bricks and plates; the pillar was offered separately and
        /// only when a part was first placed, so anything lifted afterwards got a tower.
        /// </summary>
        static void TestATallGapIsFilledByOnePillar()
        {
            PartDefinition source = FindDefinition("pillar_2x2x7");
            PartDefinition brick = FindDefinition("building_block_2x2");
            PartDefinition ramp = FindDefinition("slide_2x4");

            if (source?.mesh == null || brick == null || ramp == null)
            {
                Check("pillar and ramp parts exist", false);
                return;
            }

            ProceduralPillars.Active = new ProceduralPillars(source);
            ScaffoldBuilder.Plate = FindDefinition("building_block_2x2_plate");

            var map = new GridMap();
            var track = new PlacedPart(ramp, new GridCoord(0, 0, 10), 0, 0);
            map.Add(track);

            List<PlacedPart> supports = ScaffoldBuilder.BuildSupports(map, track, brick);

            Check("a run ten layers up is propped", supports.Count > 0);

            int pillars = 0, others = 0;
            foreach (PlacedPart support in supports)
                if (ProceduralPillars.Active.IsPillar(support.Definition)) pillars++; else others++;

            Check("the props are pillars rather than stacks of brick", pillars > 0,
                $"{pillars} pillars, {others} bricks");
        }

        /// <summary>
        /// The levels offered over a spot include under a raised run, not just on top of the pile.
        ///
        /// Space beneath something already built is where a piece often belongs on a marble run, and
        /// no ray can point at a gap - so it has to be reachable by stepping through levels rather
        /// than by aiming.
        /// </summary>
        static void TestLevelsOfferedOverABuild()
        {
            PartDefinition brick = FindDefinition("building_block_2x2");
            PartDefinition pillar = FindDefinition("pillar_2x2x7");

            if (brick == null || pillar == null)
            {
                Check("level test parts exist", false);
                return;
            }

            var map = new GridMap();

            // A run held up on a pillar, with clear air underneath it.
            var column = new PlacedPart(pillar, new GridCoord(4, 0, 0), 0, 0);
            var above = new PlacedPart(brick, new GridCoord(0, 0, pillar.heightLayers), 0, 0);

            map.Add(column);
            map.Add(above);

            List<int> levels = PlacementSolver.LevelsAt(map, brick, 0, 0, 0, 0);

            Check("the ground is offered", levels.Contains(0));

            Check("so is the top of what is built there",
                levels.Contains(above.TopLayerAt(0, 0)), string.Join(",", levels));

            // The point of the exercise: a level under the raised part, which resting alone misses.
            bool underneath = false;
            foreach (int level in levels)
                if (level > 0 && level + brick.heightLayers <= above.Origin.layer)
                    underneath = true;

            Check("and a level in the space beneath it", underneath, string.Join(",", levels));

            // Every level offered has to be one the piece actually fits at.
            foreach (int level in levels)
            {
                var candidate = new PlacedPart(brick, new GridCoord(0, 0, level), 0, 0);

                Check($"level {level} is placeable",
                    map.CanPlace(candidate) != PlacementResult.Blocked);
            }
        }

        /// <summary>
        /// A stepped part can be carried by the studs under its raised end.
        ///
        /// A slide curve has antistuds on the floor at one end and a whole brick up at the other. Both
        /// are real connections, but support was asked only about the part's base layer, so the raised
        /// pair could never meet a stud however exactly it was lined up over one - and the placement
        /// where it does was not even offered, since resting only ever answers about the lowest point.
        /// </summary>
        static void TestARaisedUndersideCanMeetAStud()
        {
            PartDefinition curve = FindDefinition("slide_curve_4x4");
            PartDefinition brick = FindDefinition("building_block_2x2");

            if (curve == null || brick == null)
            {
                Check("curve and brick exist", false);
                return;
            }

            // The column whose underside is highest - the raised mouth.
            var probe = new PlacedPart(curve, new GridCoord(0, 0, 0), 0, 0);

            var raised = new Vector2Int(-1, -1);
            int highest = 0;

            foreach (GridCoord cell in probe.OccupiedCells())
            {
                int underside = probe.UndersideLayerAt(cell.x, cell.y);

                if (underside > highest && probe.HasBottomSocketAt(cell.x, cell.y))
                {
                    highest = underside;
                    raised = new Vector2Int(cell.x, cell.y);
                }
            }

            Check("the curve has an antistud above its base", highest > 0,
                $"highest socketed underside at {highest}");

            if (highest == 0)
                return;

            // A brick standing where that raised underside would come down on it.
            var map = new GridMap();
            var tower = new PlacedPart(brick, new GridCoord(raised.x, raised.y, 0), 0, 0);
            map.Add(tower);

            int studTop = tower.TopLayerAt(raised.x, raised.y);
            var placed = new PlacedPart(curve, new GridCoord(0, 0, studTop - highest), 0, 0);

            Check("the curve fits over it", map.CanPlace(placed) != PlacementResult.Blocked);
            Check("and its raised end is carried by the studs", map.IsSupported(placed),
                $"curve at {placed.Origin.layer}, studs top out at {studTop}");

            // And the solver offers that placement rather than only the resting one.
            List<PlacedPart> ranked = PlacementSolver.SolveRanked(map, curve, 0, 0, 0, 0);

            bool offered = false;
            foreach (PlacedPart candidate in ranked)
                if (candidate.Origin.layer == placed.Origin.layer)
                    offered = true;

            Check("the solver offers it", offered,
                $"{ranked.Count} placements, none at layer {placed.Origin.layer}");
        }

        /// <summary>
        /// Emptying the build puts everything back on undo.
        ///
        /// It was the one action in the editor that reached into the map directly, which made it the
        /// only one that could not be taken back - and the most expensive to press by accident.
        /// </summary>
        static void TestClearingTheBuildCanBeUndone()
        {
            PartDefinition brick = FindDefinition("building_block_2x2");
            PartDefinition ramp = FindDefinition("slide_2x4");

            if (brick == null || ramp == null)
            {
                Check("clear test parts exist", false);
                return;
            }

            var map = new GridMap();
            var a = new PlacedPart(brick, new GridCoord(0, 0, 0), 0, 0);
            var b = new PlacedPart(ramp, new GridCoord(4, 0, 0), 1, 0);

            map.Add(a);
            map.Add(b);

            int before = map.Parts.Count;

            var command = new ClearAllCommand(map, _ => null);

            Check("clearing reports something to do", command.Do());
            Check("the build is empty", map.Parts.Count == 0, $"{map.Parts.Count} left");

            command.Undo();

            Check("undo puts every piece back", map.Parts.Count == before,
                $"{map.Parts.Count} of {before}");

            Check("and in the same places", map.Contains(a) && map.Contains(b));

            // Nothing to clear is not an edit, or the history fills with empty entries.
            var empty = new GridMap();
            Check("clearing an empty build is not an edit",
                !new ClearAllCommand(empty, _ => null).Do());
        }
    }
}
#endif
