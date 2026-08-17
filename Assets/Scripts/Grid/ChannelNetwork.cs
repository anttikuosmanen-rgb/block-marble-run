using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    /// <summary>
    /// Lifts a run of channel to meet a part whose channel is higher than the grid says it should be.
    ///
    /// The funnels are the reason this exists. Their chute is measured from the stud shelf an incoming
    /// piece plugs onto rather than from the funnel's own base - 7.2 mm above it, where every other
    /// part carries its channel 6.4 mm above its base - so a track standing on that shelf delivers its
    /// ball 0.8 mm below the chute. A ball moving on a nudge stops against that step.
    ///
    /// None of the three pieces of geometry is wrong, so none of them is edited. What is adjusted is
    /// where a piece is drawn: the run feeding the funnel rises the 0.8 mm to meet it.
    ///
    /// **The lift spreads along the whole joined run, not just the piece that touches the funnel.**
    /// Raising one piece of a run would move the step from the funnel's mouth to the next joint back,
    /// which is no better and harder to see. A run has to stay flush with itself, so the lift is a
    /// property of the network rather than of the join.
    ///
    /// The grid is untouched. Every piece occupies the cells it always did and clutches what it always
    /// clutched; a lifted piece simply stands 0.8 mm proud of its studs, which is a hairline and the
    /// cheapest of the available costs - lowering the funnel instead puts its skirt through the brick
    /// beneath it, and lowering only its collider leaves the ball resting inside the visible funnel,
    /// which then draws over the ball.
    /// </summary>
    public static class ChannelNetwork
    {
        /// <summary>
        /// Works out every part's lift. Returns true if any of them changed.
        ///
        /// Cheap enough to run on every edit: it walks the parts that have channels, which is a small
        /// share of a build, and only when the map says it changed.
        /// </summary>
        public static bool Recompute(GridMap map)
        {
            bool changed = false;

            foreach (PlacedPart part in map.Parts)
            {
                float wanted = 0f;

                if (part.HasPorts)
                    wanted = LiftFor(map, part);

                if (part.LiftUnits != wanted)
                {
                    part.LiftUnits = wanted;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// How far this part has to rise, found by walking the run it belongs to.
        ///
        /// Works for a part that is not in the map yet, which is what the placement ghost needs: a
        /// funnel's feed should show at the height it will settle at rather than jumping on release.
        /// </summary>
        public static float LiftFor(GridMap map, PlacedPart part)
        {
            if (part == null || !part.HasPorts)
                return 0f;

            float lift = 0f;

            var seen = new HashSet<PlacedPart> { part };
            var queue = new Queue<PlacedPart>();
            queue.Enqueue(part);

            while (queue.Count > 0)
            {
                PlacedPart current = queue.Dequeue();

                lift = Mathf.Max(lift, Demanded(map, current));

                foreach (PlacedPart.WorldPort port in current.WorldPorts())
                {
                    PlacedPart neighbour = map.FindConnection(current, port);

                    if (neighbour == null || !neighbour.HasPorts || !seen.Add(neighbour))
                        continue;

                    queue.Enqueue(neighbour);
                }
            }

            return lift;
        }

        /// <summary>
        /// What the part directly beneath asks of this one.
        ///
        /// A funnel does not join a channel mouth to mouth - it offers a stud shelf and the feed
        /// clutches onto it - so the demand is found through the studs rather than through the channel
        /// graph, and then carried along the channel graph from there.
        /// </summary>
        static float Demanded(GridMap map, PlacedPart part)
        {
            float lift = 0f;

            var below = new HashSet<PlacedPart>();

            foreach (GridCoord cell in part.OccupiedCells())
            {
                if (cell.layer <= 0)
                    continue;

                PlacedPart under = map.At(new GridCoord(cell.x, cell.y, cell.layer - 1));

                if (under == null || under == part || !below.Add(under))
                    continue;

                if (under.Definition.channelLipUnits > lift)
                    lift = under.Definition.channelLipUnits;
            }

            return lift;
        }
    }
}
