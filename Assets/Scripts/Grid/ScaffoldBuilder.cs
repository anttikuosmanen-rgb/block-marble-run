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
            if (pillar == null || part.Origin.layer <= 0 || !part.HasPorts)
                return supports;

            // Pillars are built before the part goes in, so the map cannot yet see where the part
            // will be. Without this the scaffolding happily fills a cell the part needs, the part
            // then fails to place, and the whole action is abandoned - after the ghost had already
            // shown green.
            var partCells = new HashSet<GridCoord>(part.OccupiedCells());

            foreach ((Vector2Int anchor, int topLayer) in ChoosePillarAnchors(part, pillar))
            {
                int from = HighestObstruction(map, pillar, anchor);

                // Asked per column, not once for the whole part. A slide joined to an elevated run is
                // "supported" by that joint, yet its far end still hangs over nothing four studs away
                // - and a whole-part test reports it as fine and builds no pillars at all.
                if (from >= topLayer)
                    continue;

                for (int layer = from; layer < topLayer; layer++)
                {
                    var brick = new PlacedPart(pillar, new GridCoord(anchor.x, anchor.y, layer), 0, ScaffoldColour);

                    // Stop this pillar at the first obstruction rather than abandoning it: the part of
                    // the column that fits is still load bearing.
                    if (Intersects(brick, partCells) || map.CanPlace(brick) == PlacementResult.Blocked)
                        break;

                    map.Add(brick);
                    supports.Add(brick);
                }
            }

            return supports;
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
        static IEnumerable<(Vector2Int anchor, int topLayer)> ChoosePillarAnchors(PlacedPart part, PartDefinition pillar)
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

                if (seen.Add(anchor))
                    yield return (anchor, UndersideLayer(part, anchor, pillar, port.FloorLayer));
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
                    yield return (farCorner, part.Origin.layer);

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
        /// Layer to start the pillar from: on top of whatever already stands in that column, so a
        /// support added beside an existing tower begins at the tower's height rather than burrowing
        /// down beside it.
        /// </summary>
        static int HighestObstruction(GridMap map, PartDefinition pillar, Vector2Int anchor)
        {
            int highest = 0;

            for (int x = 0; x < pillar.footprintSize.x; x++)
            for (int y = 0; y < pillar.footprintSize.y; y++)
                highest = Mathf.Max(highest, map.ColumnRestLayer(anchor.x + x, anchor.y + y));

            return highest;
        }
    }
}
