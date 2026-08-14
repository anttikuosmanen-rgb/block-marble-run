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
        /// Pillars stand under the channel mouths, each rising to the height of the mouth it carries.
        ///
        /// Using the footprint's corners instead put bricks under the whole square of a curve - most
        /// of which is empty arc that needs nothing - while still leaving the raised end of a slide
        /// curve a layer short, because every pillar was sized to the part's base rather than to the
        /// channel above it. The ends are what a channel actually rests on, and a descending part's
        /// two ends sit at different heights by design.
        /// </summary>
        static IEnumerable<(Vector2Int anchor, int topLayer)> ChoosePillarAnchors(PlacedPart part, PartDefinition pillar)
        {
            Vector2Int step = pillar.footprintSize;
            var seen = new HashSet<Vector2Int>();

            foreach (PlacedPart.WorldPort port in part.WorldPorts())
            {
                // Take the cells just inside the mouth and back the pillar's footprint up over them,
                // so a 2x2 brick sits squarely under the channel end rather than half off it.
                var min = new Vector2Int(int.MaxValue, int.MaxValue);
                foreach (Vector2Int cell in port.InsideCells())
                    min = Vector2Int.Min(min, cell);

                if (min.x == int.MaxValue)
                    continue;

                var anchor = new Vector2Int(
                    port.Facing == Facing.East ? min.x - (step.x - 1) : min.x,
                    port.Facing == Facing.North ? min.y - (step.y - 1) : min.y);

                if (seen.Add(anchor))
                    yield return (anchor, port.FloorLayer);
            }
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
