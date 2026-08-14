using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    /// <summary>
    /// Builds pillars of ordinary bricks under a part that would otherwise float.
    ///
    /// These are real bricks, placed at build time as part of the same action (DESIGN.md §5.1). The
    /// earlier plan deferred scaffolding to the switch into play mode, which reads badly while
    /// building: a piece hangs in mid-air with nothing under it and the player has to take on trust
    /// that something will appear later. Building the support immediately makes the structure
    /// truthful at every moment, and it can be edited or deleted like anything else the player made.
    /// </summary>
    public static class ScaffoldBuilder
    {
        /// <summary>
        /// Pillars needed to hold up <paramref name="part"/>, or an empty list when it already rests
        /// on something.
        ///
        /// Supports go under the extremes of the footprint rather than every cell: two pillars under
        /// the ends of a long piece hold it as well as eight would, and a solid wall of bricks under
        /// a track buries the thing the player is building.
        /// </summary>
        public static List<PlacedPart> BuildSupports(GridMap map, PlacedPart part, PartDefinition pillar)
        {
            var supports = new List<PlacedPart>();

            // Only channel parts prop themselves up. A brick is the player's own structure and may
            // cantilever as far as they like; a run of track is meant to look carried.
            if (pillar == null || !part.HasPorts)
            {
                // Said out loud rather than returning quietly. The first curve placed reported
                // nothing at all, which read as the log being broken.
                if (Verbose)
                    Report = $"{part.Definition.id} at layer {part.Origin.layer}: " +
                             (pillar == null ? "no pillar part"
                                             : "not a channel piece - bricks may cantilever");

                return supports;
            }

            // Sitting at layer 0 is not the same as resting on the ground. A slide curve's raised end
            // occupies only its upper layer, so even with the part's origin on the floor that end
            // hangs a layer up with nothing beneath it. Testing the origin skipped the whole piece;
            // the per-anchor test below asks each column separately and answers "nothing to fill" for
            // the ends that genuinely do reach the ground.

            // Pillars are built before the part goes in, so the map cannot yet see where the part
            // will be. Without this the scaffolding happily fills a cell the part needs, the part
            // then fails to place, and the whole action is abandoned - after the ghost had already
            // shown green.
            var partCells = new HashSet<GridCoord>(part.OccupiedCells());

            if (Verbose)
                Report = $"{part.Definition.id} r{part.Rotation} at layer {part.Origin.layer}";

            foreach ((Vector2Int anchor, int topLayer, string why) in ChoosePillarAnchors(part, pillar))
            {
                // Measured below the part's own underside, so it makes no difference whether the part
                // is in the map yet. Placement scaffolds before adding the piece; a lift scaffolds
                // after moving it, and asking the whole column there answered with the lifted part
                // itself - every anchor reported "rests already" and a run raised off the ground kept
                // no supports at all.
                int from = HighestObstructionBelow(map, pillar, anchor, topLayer);
                int built = 0;

                // Asked per column, not once for the whole part. A slide joined to an elevated run is
                // "supported" by that joint, yet its far end still hangs over nothing four studs away
                // - and a whole-part test reports it as fine and builds no pillars at all.
                if (from >= topLayer)
                {
                    if (Verbose)
                        Report += $"\n  ({anchor.x},{anchor.y}) {why}: top {topLayer} from {from} - rests already";

                    continue;
                }

                for (int layer = from; layer < topLayer; layer++)
                {
                    var brick = new PlacedPart(pillar, new GridCoord(anchor.x, anchor.y, layer), 0, ScaffoldColour);

                    // Stop this pillar at the first obstruction rather than abandoning it: the part of
                    // the column that fits is still load bearing.
                    // Named separately so the report can say which of the two stopped the column: a
                    // clash with the part itself and a clash with the existing build look identical
                    // from the outside and mean quite different things.
                    bool inPart = Intersects(brick, partCells);

                    if (inPart || map.CanPlace(brick) == PlacementResult.Blocked)
                    {
                        if (Verbose)
                            Report += $" - stopped at {layer} by {(inPart ? "the part" : "the build")}";

                        break;
                    }

                    map.Add(brick);
                    supports.Add(brick);
                    built++;
                }

                if (Verbose)
                    Report = Report.TrimEnd() +
                             $"\n  ({anchor.x},{anchor.y}) {why}: top {topLayer} from {from} - {built} brick(s)";
            }

            return supports;
        }

        /// <summary>
        /// Whether to record what the scaffolder decided. Off by default, toggled with J while
        /// building - which is where scaffolding happens, and so where the answer has to be readable.
        ///
        /// Three attempts at the slide curve's missing support were made by reading screenshots and
        /// reasoning about masks, and all three were wrong. Where the anchors actually land, and what
        /// stopped each column, is a fact the code knows and nobody could see.
        /// </summary>
        public static bool Verbose;

        /// <summary>The last decision, anchor by anchor, for the build HUD to display.</summary>
        public static string Report = "";

        /// <summary>
        /// Fills the gap under bricks that have been lifted, so a raised structure still stands.
        ///
        /// Ordinary placement lets a brick cantilever - it is the player's own structure and they may
        /// build what they like. A lift is different: the pillars that were holding the build up are
        /// now hanging a layer above where they were, and nobody asked for that. Their columns are
        /// grown back down to meet whatever is beneath.
        /// </summary>
        public static List<PlacedPart> ExtendLiftedColumns(GridMap map, List<PlacedPart> moved,
                                                           PartDefinition pillar)
        {
            var added = new List<PlacedPart>();
            if (pillar == null)
                return added;

            foreach (PlacedPart part in moved)
            {
                // Channel pieces are propped by the usual rules; this is about the bricks holding them.
                if (part.HasPorts || part.Origin.layer <= 0 || map.IsSupported(part))
                    continue;

                // Every pillar-sized patch of the part's base, not just its origin corner. A long
                // brick lifted off its supports needs its whole underside carried, and filling only
                // the corner leaves the rest of it hanging exactly as it was.
                Vector2Int size = part.RotatedSize;
                Vector2Int step = pillar.footprintSize;

                for (int dx = 0; dx < size.x; dx += step.x)
                for (int dy = 0; dy < size.y; dy += step.y)
                {
                    var anchor = new Vector2Int(part.Origin.x + dx, part.Origin.y + dy);
                    int from = HighestObstructionBelow(map, pillar, anchor, part.Origin.layer);

                    for (int layer = from; layer < part.Origin.layer; layer++)
                    {
                        var brick = new PlacedPart(pillar, new GridCoord(anchor.x, anchor.y, layer),
                            0, ScaffoldColour);

                        if (map.CanPlace(brick) == PlacementResult.Blocked)
                            break;

                        map.Add(brick);
                        added.Add(brick);
                    }
                }
            }

            return added;
        }

        /// <summary>Grey, so scaffolding reads as structure rather than as part of the design.</summary>
        public const byte ScaffoldColour = 5;

        static bool Intersects(PlacedPart brick, HashSet<GridCoord> cells)
        {
            foreach (GridCoord cell in brick.OccupiedCells())
                if (cells.Contains(cell))
                    return true;

            return false;
        }

        /// <summary>
        /// Pillars stand <em>astride</em> each channel mouth, each rising to the height of the mouth
        /// it carries: half the brick under the channel's end, half projecting past it.
        ///
        /// That outer half is the point. It leaves two studs already standing at the right height for
        /// whatever continues the run, so extending a track does not need a second pillar built by
        /// hand under the joint.
        ///
        /// It also avoids a clash. Tucking the whole brick under the part meant reaching cells further
        /// in, and on a descending piece such as slide_curve_4x4 those are exactly where the ramp
        /// comes down to meet its base - so the pillar fouled the very part it was carrying. Only the
        /// end cell at the mouth needs to be clear, and on a channel end it always is.
        ///
        /// Corners were the earlier choice and were wrong twice over: they put bricks under the whole
        /// square of a curve, most of which is empty arc needing nothing, while still leaving a slide
        /// curve's raised end a layer short because every pillar was sized to the part's base rather
        /// than to the channel above it.
        /// </summary>
        static IEnumerable<(Vector2Int anchor, int topLayer, string why)> ChoosePillarAnchors(
            PlacedPart part, PartDefinition pillar)
        {
            var seen = new HashSet<Vector2Int>();
            int mouths = 0;

            foreach (PlacedPart.WorldPort port in part.WorldPorts())
            {
                mouths++;

                bool alongX = port.Facing is Facing.North or Facing.South;

                int centreAlong = (alongX ? port.MidlineHalfStuds.x : port.MidlineHalfStuds.y) / 2;
                int across = (alongX ? port.MidlineHalfStuds.y : port.MidlineHalfStuds.x) / 2;

                // The mouth is centred on a stud boundary, so it spans half its width either side.
                int alongMin = centreAlong - Mathf.Max(1, port.WidthStuds) / 2;

                // The boundary line sits at `across`, so the two cell rows straddling it are
                // across-1 and across - one inside the part, one out, whichever way the mouth faces.
                int acrossMin = across - 1;

                Vector2Int anchor = alongX
                    ? new Vector2Int(alongMin, acrossMin)
                    : new Vector2Int(acrossMin, alongMin);

                int mouthUnderside = UndersideLayer(part, anchor, pillar, port.FloorLayer);

                if (seen.Add(anchor))
                    yield return (anchor, mouthUnderside, $"mouth {port.Facing}");

                // A raised mouth may hang past whatever carries the low end, and the straddling pillar
                // only holds its lip. This one holds the overhang just inside it.
                //
                // Measured from the part's underside, not from its channel height. A straight ramp's
                // channel climbs while its base stays flat on the ground - the far mouth's floor is a
                // layer up, yet nothing about the piece overhangs, and asking about the channel put a
                // surplus pillar under the middle of every slide_2x4.
                if (mouthUnderside <= part.Origin.layer)
                    continue;

                Vector2Int inward = port.Facing switch
                {
                    Facing.North => new Vector2Int(0, -1),
                    Facing.South => new Vector2Int(0, 1),
                    Facing.East => new Vector2Int(-1, 0),
                    _ => new Vector2Int(1, 0),
                };

                // Stepped by the pillar's own width, not one stud. A 2x2 brick moved one stud inward
                // overlaps the straddling pillar, which makes the placement blocked and drops it
                // silently - the support simply never appeared.
                Vector2Int deeper = anchor + new Vector2Int(inward.x * pillar.footprintSize.x,
                                                            inward.y * pillar.footprintSize.y);

                int deeperUnderside = UndersideLayer(part, deeper, pillar, port.FloorLayer);

                // Only while the overhang lasts. This pillar exists to carry a raised mouth whose
                // lip reaches further in than the pillar straddling it - so it belongs under the
                // raised region, and its underside has to be as high as the mouth's.
                //
                // On a curve the arc drops away immediately, and one step inward is already the
                // part's own base: a brick there stands wholly beneath the curved shell, carrying
                // something that was resting on the ground anyway. It was surplus, and on a piece
                // with no antistud under that curve it had nothing to clutch to either.
                if (deeperUnderside < mouthUnderside)
                    continue;

                if (seen.Add(deeper))
                    yield return (deeper, deeperUnderside, $"inside raised {port.Facing}");
            }

            Vector2Int size = part.RotatedSize;
            Vector2Int step = pillar.footprintSize;

            // A dead end is carried at one end only, so its closed end hangs unsupported. Terminal
            // pieces are exactly the case: one mouth, and nothing at all under the far half.
            if (mouths < 2)
            {
                var farCorner = new Vector2Int(
                    part.Origin.x + Mathf.Max(0, size.x - step.x),
                    part.Origin.y + Mathf.Max(0, size.y - step.y));

                if (seen.Add(farCorner))
                    yield return (farCorner, part.Origin.layer, "dead end");

                yield break;
            }

            // Mid-span propping was tried and removed. A curve fills a diagonal band of its square,
            // so anchors chosen from the bounding box land beside the arc rather than under it, and a
            // brick standing next to the piece it is meant to carry is worse than no brick at all.
            // Doing this properly means following the channel path, which is work the centreline
            // derivation would make straightforward and guesswork without it.
        }

        /// <summary>
        /// The layer the part's own underside sits at above this anchor - where a pillar has to reach.
        ///
        /// Taken from the geometry rather than from the mouth's channel height. The two agree on a
        /// flat piece, but a slide curve's raised end occupies only its upper layer while its channel
        /// floor sits 6.4 mm higher again, and carrying to the channel rather than to the underside
        /// leaves the brick a layer short of the thing it is holding up.
        /// </summary>
        static int UndersideLayer(PlacedPart part, Vector2Int anchor, PartDefinition pillar, int fallback)
        {
            int lowest = int.MaxValue;

            foreach (GridCoord cell in part.OccupiedCells())
            {
                // Only the columns this pillar actually stands under.
                if (cell.x < anchor.x || cell.x >= anchor.x + pillar.footprintSize.x ||
                    cell.y < anchor.y || cell.y >= anchor.y + pillar.footprintSize.y)
                    continue;

                lowest = Mathf.Min(lowest, cell.layer);
            }

            return lowest == int.MaxValue ? fallback : lowest;
        }

        /// <summary>
        /// As <see cref="HighestObstruction"/>, but blind to anything at <paramref name="below"/> or
        /// above.
        ///
        /// A part that has just been lifted is already in the map at its new layer, so asking the
        /// column what stands in it answers with the part itself: the rest layer came back one above
        /// the brick needing carrying, the fill loop ran zero times, and a raised build kept its
        /// supports hanging exactly where the lift left them.
        /// </summary>
        static int HighestObstructionBelow(GridMap map, PartDefinition pillar, Vector2Int anchor, int below)
        {
            for (int layer = below - 1; layer >= 0; layer--)
            for (int x = 0; x < pillar.footprintSize.x; x++)
            for (int y = 0; y < pillar.footprintSize.y; y++)
                if (map.At(new GridCoord(anchor.x + x, anchor.y + y, layer)) != null)
                    return layer + 1;

            return 0;
        }

    }
}
