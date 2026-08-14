using System.Collections.Generic;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    /// <summary>
    /// Finds everything physically attached to a part.
    ///
    /// Both of the operations that need this - raising a support, and growing a build upward to make
    /// room underneath - move a structure rather than a piece. Moving one brick out of a tower and
    /// leaving the rest is never what was meant.
    /// </summary>
    public static class Assembly
    {
        /// <summary>
        /// Everything joined to <paramref name="seed"/>, through channels or through stacking.
        ///
        /// Both relations count. A run of track holds together by its channels, a tower by its studs,
        /// and a real build is a mixture: the pillars under a slide are as much part of it as the
        /// slide the ball runs on.
        /// </summary>
        public static List<PlacedPart> Connected(GridMap map, PlacedPart seed)
        {
            var found = new List<PlacedPart>();
            if (seed == null || !map.Contains(seed))
                return found;

            var seen = new HashSet<PlacedPart> { seed };
            var queue = new Queue<PlacedPart>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                PlacedPart current = queue.Dequeue();
                found.Add(current);

                foreach (PlacedPart neighbour in Neighbours(map, current))
                    if (seen.Add(neighbour))
                        queue.Enqueue(neighbour);
            }

            return found;
        }

        static IEnumerable<PlacedPart> Neighbours(GridMap map, PlacedPart part)
        {
            foreach (PlacedPart.WorldPort port in part.WorldPorts())
            {
                PlacedPart joined = map.FindConnection(part, port);
                if (joined != null)
                    yield return joined;
            }

            foreach (GridCoord cell in part.OccupiedCells())
            {
                // Directly under, and directly over: whatever this rests on and whatever rests on it.
                PlacedPart below = map.At(cell.Below);
                if (below != null && below != part)
                    yield return below;

                PlacedPart above = map.At(cell.Above);
                if (above != null && above != part)
                    yield return above;
            }
        }

        /// <summary>
        /// Copies of these parts shifted by whole layers, or null when the move will not fit.
        ///
        /// Checked against the map with the movers themselves taken out, since a structure moving as
        /// one is allowed to occupy cells its own members are vacating - testing each piece against
        /// the others where they currently stand would refuse almost every move.
        /// </summary>
        public static List<PlacedPart> Shift(GridMap map, List<PlacedPart> parts, int layers)
        {
            if (layers == 0 || parts.Count == 0)
                return null;

            var moving = new HashSet<PlacedPart>(parts);
            var vacated = new HashSet<GridCoord>();

            foreach (PlacedPart part in parts)
                foreach (GridCoord cell in part.OccupiedCells())
                    vacated.Add(cell);

            var moved = new List<PlacedPart>(parts.Count);

            foreach (PlacedPart part in parts)
            {
                var origin = new GridCoord(part.Origin.x, part.Origin.y, part.Origin.layer + layers);
                if (origin.layer < 0)
                    return null;

                var candidate = new PlacedPart(part.Definition, origin, part.Rotation, part.ColorIndex)
                {
                    Role = part.Role,
                };

                foreach (GridCoord cell in candidate.OccupiedCells())
                {
                    if (cell.layer < 0)
                        return null;

                    PlacedPart occupant = map.At(cell);
                    if (occupant != null && !moving.Contains(occupant))
                        return null;
                }

                moved.Add(candidate);
            }

            return moved;
        }
    }
}
