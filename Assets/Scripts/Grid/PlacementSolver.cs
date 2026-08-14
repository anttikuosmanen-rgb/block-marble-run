using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    /// <summary>
    /// Decides where a held part should come to rest.
    ///
    /// Two clutch systems have to be satisfied at once. Studs pull a part <em>down</em> onto whatever
    /// is beneath it; channels pull it <em>sideways</em> to meet a neighbouring channel at that
    /// channel's height. Resting height alone cannot express the second: continuing a run that is
    /// three layers up means placing at layer three over empty ground, which no downward rule would
    /// ever suggest.
    ///
    /// So candidate layers are gathered from both, then scored.
    /// </summary>
    public static class PlacementSolver
    {
        const float HeightTolerance = 0.005f;

        /// <summary>Layers to consider around the resting height when nothing connects.</summary>
        const int FallbackSearch = 0;

        public static PlacedPart Solve(GridMap map, PartDefinition def, int anchorX, int anchorY,
                                       int rotation, byte colorIndex)
        {
            int restLayer = RestLayerFor(map, def, anchorX, anchorY, rotation, colorIndex);

            var candidates = new HashSet<int> { restLayer };
            CollectPortLayers(map, def, anchorX, anchorY, rotation, colorIndex, candidates);

            PlacedPart best = null;
            int bestScore = int.MinValue;

            foreach (int layer in candidates)
            {
                if (layer < 0)
                    continue;

                var candidate = new PlacedPart(def, new GridCoord(anchorX, anchorY, layer), rotation, colorIndex);
                PlacementResult result = map.CanPlace(candidate);

                if (result == PlacementResult.Blocked)
                    continue;

                int score = 0;

                // A joined channel is the strongest signal of intent: the player is continuing a run.
                if (map.HasPortConnection(candidate))
                    score += 1000;

                if (result == PlacementResult.Valid)
                    score += 100;

                // Among equals prefer the lowest, so a piece settles rather than hovering at the
                // highest height that happens to work.
                score -= layer;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            // Every candidate was blocked; return the resting placement so the ghost can show why.
            return best ?? new PlacedPart(def, new GridCoord(anchorX, anchorY, restLayer), rotation, colorIndex);
        }

        /// <summary>
        /// Height at which the part comes to rest on what is beneath it: the highest column under any
        /// of its base cells, as it would sit in the hand.
        /// </summary>
        public static int RestLayerFor(GridMap map, PartDefinition def, int anchorX, int anchorY,
                                       int rotation, byte colorIndex)
        {
            var probe = new PlacedPart(def, new GridCoord(anchorX, anchorY, 0), rotation, colorIndex);

            int rest = 0;
            foreach (GridCoord cell in probe.OccupiedCells())
            {
                if (cell.layer != 0)
                    continue;

                rest = Mathf.Max(rest, map.ColumnRestLayer(cell.x, cell.y));
            }

            return rest;
        }

        /// <summary>
        /// Layers at which one of this part's channels would line up with a neighbouring channel.
        ///
        /// Works backwards from the neighbour: given where their channel floor sits in the world and
        /// how high ours sits within our own part, only one layer can make the two meet.
        /// </summary>
        static void CollectPortLayers(GridMap map, PartDefinition def, int anchorX, int anchorY,
                                      int rotation, byte colorIndex, HashSet<int> candidates)
        {
            if (def.ports == null || def.ports.Length == 0)
                return;

            var probe = new PlacedPart(def, new GridCoord(anchorX, anchorY, 0), rotation, colorIndex);

            foreach (PlacedPart.WorldPort port in probe.WorldPorts())
            {
                // Height of this mouth measured from the part's own base, since the probe sits at 0.
                float localHeight = port.HeightUnits;

                Facing wanted = PlacedPart.WorldPort.Opposite(port.Facing);

                foreach (Vector2Int cell in port.OutsideCells())
                {
                    foreach (PlacedPart other in map.PartsInColumn(cell.x, cell.y))
                    {
                        foreach (PlacedPart.WorldPort otherPort in other.WorldPorts())
                        {
                            if (otherPort.Facing != wanted)
                                continue;

                            // Mouths must share a centre line exactly; a run offset by one stud is a
                            // different run, not a loose fit to be snapped together.
                            if (otherPort.MidlineHalfStuds != port.MidlineHalfStuds)
                                continue;

                            float layerFloat = (otherPort.HeightUnits - localHeight) / GridCoord.LayerUnits;
                            int layer = Mathf.RoundToInt(layerFloat);

                            // Only whole layers can align; a slide meeting flat track half a layer off
                            // is not a connection, and rounding it into one would snap parts into a
                            // join that does not exist.
                            if (Mathf.Abs(layerFloat - layer) * GridCoord.LayerUnits <= HeightTolerance)
                                candidates.Add(layer);
                        }
                    }
                }
            }
        }
    }
}
