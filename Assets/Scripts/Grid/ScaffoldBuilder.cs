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
            if (pillar == null || !NeedsCarrying(part))
            {
                // Said out loud rather than returning quietly. The first curve placed reported
                // nothing at all, which read as the log being broken.
                if (Verbose)
                    Report = $"{part.Definition.id} at layer {part.Origin.layer}: " +
                             (pillar == null ? "no pillar part"
                                             : "a brick - the player's own structure may cantilever");

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

                // Asked per column, not once for the whole part. A slide joined to an elevated run is
                // "supported" by that joint, yet its far end still hangs over nothing four studs away
                // - and a whole-part test reports it as fine and builds no pillars at all.
                if (from >= topLayer)
                {
                    if (Verbose)
                        Report += $"\n  ({anchor.x},{anchor.y}) {why}: top {topLayer} from {from} - rests already";

                    continue;
                }

                int built = FillColumn(map, anchor, from, topLayer, pillar, partCells, supports,
                                       out string stoppedBy);

                if (Verbose)
                    Report = Report.TrimEnd() +
                             $"\n  ({anchor.x},{anchor.y}) {why}: top {topLayer} from {from} - {built} piece(s)" +
                             (stoppedBy == null ? "" : $" - {stoppedBy}");
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
                                                           PartDefinition pillar,
                                                           List<(PlacedPart Old, PlacedPart New)> lengthened = null)
        {
            var added = new List<PlacedPart>();
            if (pillar == null)
                return added;

            foreach (PlacedPart part in moved)
            {
                // Channel pieces are propped by the usual rules; this is about the bricks holding them.
                if (part.HasPorts || part.Origin.layer <= 0 || map.IsSupported(part))
                    continue;

                // A pillar that has been lifted is made longer rather than stood on a tower of bricks.
                // It is one part cut to a height; the height it needs has simply changed. Stacking
                // under it works structurally and looks like scaffolding holding up scaffolding.
                if (lengthened != null && Lengthen(map, part, out PlacedPart taller))
                {
                    lengthened.Add((part, taller));
                    continue;
                }

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

                    FillColumn(map, anchor, from, part.Origin.layer, pillar, Empty, added, out _);
                }
            }

            return added;
        }

        /// <summary>
        /// Whether the game props this part up, or leaves it to the player.
        ///
        /// Bricks and plates are the player's own structure and may cantilever as far as they like.
        /// Everything else is track in the broad sense - a run, a funnel - and is meant to look
        /// carried. Testing for channel mouths was the old rule and left the funnel unsupported: it
        /// has no mouths of its own, only a shelf that a channel clutches onto.
        /// </summary>
        static bool NeedsCarrying(PlacedPart part) =>
            part.HasPorts || part.Definition.category != PartCategory.Block;

        /// <summary>Grey, so scaffolding reads as structure rather than as part of the design.</summary>
        public const byte ScaffoldColour = 5;

        /// <summary>
        /// Half-height brick, for the odd layer a whole one cannot fill.
        ///
        /// The grid steps half a brick, so a column can need an odd number of layers - and until
        /// there was a plate, the filler simply stopped one short and left the run it was carrying
        /// resting on nothing.
        /// </summary>
        public static PartDefinition Plate;

        /// <summary>No cells to avoid. A lifted column has no part of its own to clash with.</summary>
        static readonly HashSet<GridCoord> Empty = new();

        /// <summary>
        /// Fills a column between two layers, using the tallest piece that fits at each step.
        ///
        /// Stepping by the piece's own height rather than one layer at a time is what makes this
        /// work at all now that a brick is two layers: advancing by one put the next brick halfway
        /// inside the last, which the map refused, and the column stopped at its first brick.
        /// </summary>
        static int FillColumn(GridMap map, Vector2Int anchor, int from, int topLayer,
                              PartDefinition brick, HashSet<GridCoord> partCells,
                              List<PlacedPart> supports, out string stoppedBy)
        {
            stoppedBy = null;
            int layer = from;
            int built = 0;

            while (layer < topLayer)
            {
                int remaining = topLayer - layer;

                // Tallest first: one pillar cut to the whole remaining height, else a brick, else a
                // plate for an odd last layer. A pillar is one part, one collider and one line in a
                // save file where a stack of bricks is many, and has no seams down it for a marble
                // that wanders off the track.
                PlacedPart support = null;
                bool inPart = false;

                foreach (PartDefinition piece in new[]
                         {
                             ProceduralPillars.Active?.ForLayers(remaining),
                             brick,
                             Plate,
                         })
                {
                    if (piece == null || piece.heightLayers > remaining)
                        continue;

                    var candidate = new PlacedPart(piece, new GridCoord(anchor.x, anchor.y, layer),
                        0, ScaffoldColour);

                    if (Intersects(candidate, partCells))
                    {
                        inPart = true;
                        continue;
                    }

                    if (map.CanPlace(candidate) == PlacementResult.Blocked)
                        continue;

                    support = candidate;
                    break;
                }

                if (support == null)
                {
                    stoppedBy = inPart
                        ? $"stopped at {layer} by the part"
                        : $"nothing fits the last {remaining} layer(s)";

                    break;
                }

                map.Add(support);
                supports.Add(support);

                built++;
                layer += Mathf.Max(1, support.Definition.heightLayers);
            }

            return built;
        }

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
            var chosen = new List<(Vector2Int anchor, int topLayer, string why)>();
            int mouths = 0;

            // A part with no mouths is carried where it carries something. For the funnel that is its
            // shelf, which is both the loaded corner and the only part of it with solid ground
            // underneath - the middle is a hole, and a pillar there would stand in the way of the
            // very balls the hole is for.
            if (!part.HasPorts)
            {
                foreach ((Vector2Int anchor, int topLayer, string why) in StuddedAnchors(part, pillar))
                    yield return (anchor, topLayer, why);

                yield break;
            }

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
                    chosen.Add((anchor, mouthUnderside, $"mouth {port.Facing}"));

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
                    chosen.Add((deeper, deeperUnderside, $"inside raised {port.Facing}"));
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
                    chosen.Add((farCorner, part.Origin.layer, "dead end"));

                foreach (var a in chosen)
                    yield return a;

                yield break;
            }

            // A piece with a way through it stands on its own flat underside when it has one, and
            // then only there: a pillar at the mouth of a u-turn slide sits right under the channel
            // opening, and two of them plus the middle is three supports where one carries it.
            //
            // Only when it has one, though. A slide curve has a tunnel too, and no patch of flat
            // underside big enough to stand a pillar under - its raised end is carried at the mouth
            // or not at all, and not at all was the bug reported three times before this.
            if (part.Definition.hasTunnel)
            {
                var flat = new List<(Vector2Int anchor, int topLayer, string why)>(SocketAnchors(part, pillar));

                if (flat.Count > 0)
                {
                    // One pillar, in the middle of the flat part. A piece that stands on its own
                    // underside is carried by a single column under the centre of it - lining the
                    // whole edge with them props nothing extra and buries the piece.
                    yield return Central(part, flat);
                    yield break;
                }
            }

            foreach (var a in chosen)
                yield return a;

            // Mid-span propping was tried and removed. A curve fills a diagonal band of its square,
            // so anchors chosen from the bounding box land beside the arc rather than under it, and a
            // brick standing next to the piece it is meant to carry is worse than no brick at all.
            // Doing this properly means following the channel path, which is work the centreline
            // derivation would make straightforward and guesswork without it.
        }

        /// <summary>
        /// Whether every cell of a pillar-sized patch that lies under the part has a flat underside.
        ///
        /// Cells outside the part do not count - a pillar at a mouth deliberately projects past the
        /// piece. What matters is that the half beneath it meets something solid: build under a
        /// tunnel and the pillar fills the passage, build under a slide and it meets the back of the
        /// channel at whatever height that happens to be.
        /// </summary>
        /// <summary>Whether the part stands over this world column at all.</summary>
        static bool Covers(PlacedPart part, int worldX, int worldY)
        {
            foreach (GridCoord cell in part.OccupiedCells())
                if (cell.x == worldX && cell.y == worldY)
                    return true;

            return false;
        }

        static bool Solid(PlacedPart part, Vector2Int anchor, PartDefinition pillar)
        {
            bool anyUnder = false;

            for (int x = 0; x < pillar.footprintSize.x; x++)
            for (int y = 0; y < pillar.footprintSize.y; y++)
            {
                int wx = anchor.x + x, wy = anchor.y + y;

                if (!Covers(part, wx, wy))
                    continue;

                anyUnder = true;

                if (!part.HasBottomSocketAt(wx, wy))
                    return false;
            }

            return anyUnder;
        }

        /// <summary>
        /// Re-cuts a lifted pillar to reach whatever is now beneath it.
        ///
        /// False when the part is not a pillar, when nothing needs adding, or when no pillar of the
        /// required height can be made - in which case the caller falls back to filling the gap with
        /// bricks, which is right for a brick and merely ugly for a pillar.
        /// </summary>
        static bool Lengthen(GridMap map, PlacedPart part, out PlacedPart taller)
        {
            taller = null;

            ProceduralPillars pillars = ProceduralPillars.Active;
            if (pillars == null)
                return false;

            // Pillars, and the bricks the scaffolder placed before there were pillars to place. A
            // brick the player put there is their own structure and is left exactly as they built it,
            // which is why this asks about the scaffold colour rather than just the shape.
            bool ours = pillars.IsPillar(part.Definition) || part.ColorIndex == ScaffoldColour;

            if (!ours || part.HasPorts)
                return false;

            Vector2Int size = part.RotatedSize;
            int from = 0;

            // The highest thing under any column the pillar stands on. It has to clear all of them.
            for (int dx = 0; dx < size.x; dx++)
            for (int dy = 0; dy < size.y; dy++)
                from = Mathf.Max(from, ColumnUnder(map, part, part.Origin.x + dx, part.Origin.y + dy));

            int gap = part.Origin.layer - from;
            if (gap <= 0)
                return false;

            PartDefinition longer = pillars.ForLayers(part.Definition.heightLayers + gap);
            if (longer == null)
                return false;

            var candidate = new PlacedPart(longer, new GridCoord(part.Origin.x, part.Origin.y, from),
                part.Rotation, part.ColorIndex)
            {
                Role = part.Role,
            };

            // Tested with the old one out of the way, since the new one stands where it stood.
            map.Remove(part);

            if (map.CanPlace(candidate) == PlacementResult.Blocked || !map.Add(candidate))
            {
                map.Add(part);
                return false;
            }

            taller = candidate;
            return true;
        }

        /// <summary>Rest layer of one column, blind to the part itself.</summary>
        static int ColumnUnder(GridMap map, PlacedPart part, int x, int y)
        {
            for (int layer = part.Origin.layer - 1; layer >= 0; layer--)
            {
                PlacedPart occupant = map.At(new GridCoord(x, y, layer));

                if (occupant != null && occupant != part)
                    return occupant.TopLayerAt(x, y);
            }

            return 0;
        }

        /// <summary>
        /// The anchor nearest the middle of the part's flat-bottomed region.
        ///
        /// Measured against the centre of the sockets themselves rather than of the whole footprint:
        /// on a u-turn slide the flat half is one edge, and the middle of the piece is out over the
        /// tunnel where no pillar may go.
        /// </summary>
        static (Vector2Int anchor, int topLayer, string why) Central(
            PlacedPart part, List<(Vector2Int anchor, int topLayer, string why)> anchors)
        {
            float sumX = 0f, sumY = 0f;
            int counted = 0;

            foreach (GridCoord cell in part.OccupiedCells())
            {
                if (!part.HasBottomSocketAt(cell.x, cell.y))
                    continue;

                sumX += cell.x;
                sumY += cell.y;
                counted++;
            }

            if (counted == 0)
                return anchors[0];

            var centre = new Vector2(sumX / counted, sumY / counted);

            var best = anchors[0];
            float nearest = float.MaxValue;

            foreach (var candidate in anchors)
            {
                // Measured from the pillar's own middle, not its corner.
                var middle = new Vector2(candidate.anchor.x + 0.5f, candidate.anchor.y + 0.5f);
                float distance = (middle - centre).sqrMagnitude;

                if (distance < nearest)
                {
                    nearest = distance;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>Pillar-sized patches under the flat-bottomed part of a piece, working outward.</summary>
        static IEnumerable<(Vector2Int anchor, int topLayer, string why)> SocketAnchors(
            PlacedPart part, PartDefinition pillar)
        {
            var seen = new HashSet<Vector2Int>();
            Vector2Int size = part.RotatedSize;
            Vector2Int step = pillar.footprintSize;

            for (int dx = 0; dx < size.x; dx += step.x)
            for (int dy = 0; dy < size.y; dy += step.y)
            {
                var anchor = new Vector2Int(part.Origin.x + dx, part.Origin.y + dy);

                if (!Solid(part, anchor, pillar) || !seen.Add(anchor))
                    continue;

                yield return (anchor, UndersideLayer(part, anchor, pillar, part.Origin.layer), "flat underside");
            }
        }

        /// <summary>
        /// Pillar-sized patches under the cells a part offers studs on.
        ///
        /// Studs are where the next piece goes, so they are where the weight is - and on a part built
        /// around a hole they are also the only place with anything solid to stand a pillar under.
        /// </summary>
        static IEnumerable<(Vector2Int anchor, int topLayer, string why)> StuddedAnchors(
            PlacedPart part, PartDefinition pillar)
        {
            var seen = new HashSet<Vector2Int>();
            Vector2Int size = part.RotatedSize;
            Vector2Int step = pillar.footprintSize;

            for (int dx = 0; dx < size.x; dx += step.x)
            for (int dy = 0; dy < size.y; dy += step.y)
            {
                var anchor = new Vector2Int(part.Origin.x + dx, part.Origin.y + dy);

                // Every cell of the patch that lies under the part has to be a stud cell. Cells
                // outside it do not count - the funnel's shelf is at the very edge of its footprint,
                // so half of any patch under it hangs over open air, and demanding studs there finds
                // nowhere at all to build. What matters is not standing under the hole.
                bool solid = true;

                for (int x = 0; x < step.x && solid; x++)
                for (int y = 0; y < step.y; y++)
                {
                    int wx = anchor.x + x, wy = anchor.y + y;

                    if (!Covers(part, wx, wy))
                        continue;

                    if (!part.HasTopStudAt(wx, wy))
                    {
                        solid = false;
                        break;
                    }
                }

                if (!solid || !Solid(part, anchor, pillar) || !seen.Add(anchor))
                    continue;

                yield return (anchor, UndersideLayer(part, anchor, pillar, part.Origin.layer), "under studs");
            }
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
