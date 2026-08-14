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

        /// <summary>How many studs either side of the cursor a channel part may snap to find a join.</summary>
        const int SnapReach = 1;

        /// <summary>
        /// Chooses height and, for channel parts, facing.
        ///
        /// A track piece turns itself to meet whatever open mouth is beside it. Aligning a curve by
        /// hand means reading which way its channel points and pressing R the right number of times -
        /// on every single piece - when the geometry already says which way it has to go. The
        /// player's own rotation is kept as a tie-break, so R still chooses between equally good
        /// facings and still governs pieces with no channel at all.
        /// </summary>
        public static PlacedPart Solve(GridMap map, PartDefinition def, int anchorX, int anchorY,
                                       int rotation, byte colorIndex)
        {
            List<PlacedPart> ranked = SolveRanked(map, def, anchorX, anchorY, rotation, colorIndex);
            return ranked.Count > 0
                ? ranked[0]
                : new PlacedPart(def, new GridCoord(anchorX, anchorY,
                    RestLayerFor(map, def, anchorX, anchorY, rotation, colorIndex)), rotation, colorIndex);
        }

        /// <summary>
        /// The ways this piece could meet a given neighbouring mouth - one per mouth of its own.
        ///
        /// This is what cycling should offer. A curve has an entry and an exit, and which of the two
        /// meets the run is the only real choice; ranking every placement in reach instead offered
        /// alternatives at other positions and heights, which is not a question anyone was asking.
        ///
        /// Each mating is solved rather than searched: a mouth must end up facing the opposite way to
        /// its partner, which fixes the rotation, and its centre line and floor must coincide with the
        /// partner's, which fixes the position and the layer.
        /// </summary>
        public static List<PlacedPart> MatingsWith(GridMap map, PartDefinition def, byte colorIndex,
                                                   PlacedPart.WorldPort target)
        {
            var matings = new List<PlacedPart>();

            if (def.ports == null)
                return matings;

            Vector2Int halfStudSize = def.footprintSize * 2;
            Facing wanted = PlacedPart.WorldPort.Opposite(target.Facing);

            foreach (TrackPort port in def.ports)
            {
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    if ((Facing)(((int)port.facing + rotation) % 4) != wanted)
                        continue;

                    Vector2Int midline = PlacedPart.RotateHalfStudPoint(port.midlineHalfStuds, halfStudSize, rotation);

                    // The origin that puts this mouth exactly on the partner's centre line.
                    int originHalfX = target.MidlineHalfStuds.x - midline.x;
                    int originHalfY = target.MidlineHalfStuds.y - midline.y;

                    // Only whole studs are placements; a half-stud offset is not a position on the grid.
                    if ((originHalfX & 1) != 0 || (originHalfY & 1) != 0)
                        continue;

                    float layerFloat = (target.HeightUnits - port.heightMm * PlacedPart.MmToUnits) / GridCoord.LayerUnits;
                    int layer = Mathf.RoundToInt(layerFloat);

                    if (Mathf.Abs(layerFloat - layer) * GridCoord.LayerUnits > HeightTolerance || layer < 0)
                        continue;

                    var candidate = new PlacedPart(def,
                        new GridCoord(originHalfX / 2, originHalfY / 2, layer), rotation, colorIndex);

                    if (map.CanPlace(candidate) != PlacementResult.Blocked)
                        matings.Add(candidate);
                }
            }

            return matings;
        }

        /// <summary>
        /// The open mouth nearest a point, which is the joint a piece brought there is aiming at.
        /// </summary>
        public static bool NearestOpenMouth(GridMap map, Vector3 near, float maxDistance,
                                            out PlacedPart.WorldPort target)
        {
            target = default;
            float best = maxDistance * maxDistance;
            bool found = false;

            foreach (PlacedPart part in map.Parts)
            {
                if (!part.HasPorts)
                    continue;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    if (map.FindConnection(part, port) != null)
                        continue;

                    float distance = (port.WorldPosition - near).sqrMagnitude;
                    if (distance >= best)
                        continue;

                    best = distance;
                    target = port;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Every placement worth offering, best first.
        ///
        /// Returned as a list rather than a single answer so the player can step through the
        /// alternatives. A curve beside an open mouth usually has two or three ways it could join, and
        /// picking one for them is a guess - cycling is the difference between the piece being helpful
        /// and the piece being stubborn.
        /// </summary>
        public static List<PlacedPart> SolveRanked(GridMap map, PartDefinition def, int anchorX, int anchorY,
                                                   int rotation, byte colorIndex)
        {
            var scored = new List<(PlacedPart part, int score)>();
            Gather(map, def, anchorX, anchorY, rotation, colorIndex, scored);

            scored.Sort((a, b) => b.score.CompareTo(a.score));

            var ranked = new List<PlacedPart>(scored.Count);
            foreach ((PlacedPart part, int _) in scored)
                ranked.Add(part);

            return ranked;
        }

        static void Gather(GridMap map, PartDefinition def, int anchorX, int anchorY,
                           int rotation, byte colorIndex, List<(PlacedPart, int)> scored)
        {
            bool hasPorts = def.ports is { Length: > 0 };
            bool autoFace = hasPorts && def.rotation == RotationMode.Free90;

            int rotations = autoFace ? 4 : 1;

            // Channel parts also search the studs around the cursor. Searching only the exact cursor
            // cell meant a join happened only when the player landed on precisely the right stud, so
            // slides mostly gave up and sat on the ground instead - and the piece looked like it was
            // refusing to connect when it was really never offered the chance.
            int reach = hasPorts ? SnapReach : 0;

            for (int offsetX = -reach; offsetX <= reach; offsetX++)
            for (int offsetY = -reach; offsetY <= reach; offsetY++)
            for (int step = 0; step < rotations; step++)
            {
                int x = anchorX + offsetX;
                int y = anchorY + offsetY;
                int candidateRotation = autoFace ? (rotation + step) % 4 : rotation;

                int restLayer = RestLayerFor(map, def, x, y, candidateRotation, colorIndex);

                var layers = new HashSet<int> { restLayer };
                CollectPortLayers(map, def, x, y, candidateRotation, colorIndex, layers);

                foreach (int layer in layers)
                {
                    if (layer < 0)
                        continue;

                    var candidate = new PlacedPart(def, new GridCoord(x, y, layer),
                        candidateRotation, colorIndex);

                    PlacementResult result = map.CanPlace(candidate);
                    if (result == PlacementResult.Blocked)
                        continue;

                    int score = 0;

                    // A joined channel is the strongest signal of intent: the player is continuing a
                    // run. It outranks everything, including sitting exactly where the cursor points.
                    if (map.HasPortConnection(candidate))
                        score += 1000;

                    if (result == PlacementResult.Valid)
                        score += 100;

                    // Only after connecting does the player's own choice of facing matter.
                    if (candidateRotation == rotation)
                        score += 10;

                    // Drifting from the cursor is a cost, so an equally good placement under the
                    // pointer always beats one a stud away, and the snap never feels like a fight.
                    score -= 4 * (Mathf.Abs(offsetX) + Mathf.Abs(offsetY));

                    // Among equals prefer the lowest, so a piece settles rather than hovering at the
                    // highest height that happens to work.
                    score -= layer;

                    scored.Add((candidate, score));
                }
            }
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
