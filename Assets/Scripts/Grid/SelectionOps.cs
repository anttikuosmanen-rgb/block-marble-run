using System;
using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    /// <summary>
    /// Turning, mirroring and copying a group of parts.
    ///
    /// All of it works on the group's own bounding box rather than on the world, so a selection turns
    /// where it stands instead of swinging around the origin - which for anything built away from the
    /// centre would throw it off the screen.
    /// </summary>
    public static class SelectionOps
    {
        /// <summary>Cell bounds of a group, with the maxima exclusive.</summary>
        public static RectInt Footprint(IEnumerable<PlacedPart> parts)
        {
            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;

            foreach (PlacedPart part in parts)
            {
                Vector2Int size = part.RotatedSize;

                minX = Mathf.Min(minX, part.Origin.x);
                minY = Mathf.Min(minY, part.Origin.y);
                maxX = Mathf.Max(maxX, part.Origin.x + size.x);
                maxY = Mathf.Max(maxY, part.Origin.y + size.y);
            }

            return minX == int.MaxValue
                ? new RectInt(0, 0, 0, 0)
                : new RectInt(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// The group turned a quarter at a time, or null when the result will not fit.
        ///
        /// Each part turns on the spot and then travels round the group's box, which is what makes a
        /// turned run still a run: turning the pieces without moving them scrambles the joins, and
        /// moving them without turning leaves every channel facing the way it did before.
        /// </summary>
        public static List<PlacedPart> Rotate(GridMap map, IReadOnlyCollection<PlacedPart> parts, int quarters,
                                              bool checkFit = true)
        {
            quarters = ((quarters % 4) + 4) % 4;
            if (parts.Count == 0 || quarters == 0)
                return null;

            var moved = new List<PlacedPart>(parts.Count);
            RectInt before = Footprint(parts);

            foreach (PlacedPart part in parts)
            {
                int x = part.Origin.x, y = part.Origin.y, rotation = part.Rotation;
                Vector2Int size = part.RotatedSize;

                for (int turn = 0; turn < quarters; turn++)
                {
                    // A quarter turn clockwise about the box's corner. The width appears in the y
                    // term because the part's own far edge becomes its near edge in the new axis.
                    int nx = before.xMin + (y - before.yMin);
                    int ny = before.yMin + before.xMin - x - size.x;

                    x = nx;
                    y = ny;
                    rotation = (rotation + 1) % 4;
                    size = new Vector2Int(size.y, size.x);
                }

                moved.Add(new PlacedPart(part.Definition, new GridCoord(x, y, part.Origin.layer),
                    rotation, part.ColorIndex)
                {
                    Role = part.Role,
                });
            }

            // Put the turned group back where the old one stood. Without this a rectangular selection
            // walks away from itself every time it is turned.
            RectInt after = Footprint(moved);
            Translate(moved, before.xMin - after.xMin, before.yMin - after.yMin, 0);

            return !checkFit || Fits(map, parts, moved) ? moved : null;
        }

        /// <summary>
        /// The group flipped in x, with chiral parts swapped for their mirrors.
        ///
        /// Without the swap a mirrored run is not a mirror of anything: a left-handed curve reflected
        /// is a right-handed curve, and leaving the same part in place puts the bend the wrong way
        /// while the channel mouths still line up, which looks almost right and never works.
        /// </summary>
        public static List<PlacedPart> Mirror(GridMap map, IReadOnlyCollection<PlacedPart> parts,
                                              Func<PartDefinition, PartDefinition> twin,
                                              bool checkFit = true)
        {
            if (parts.Count == 0)
                return null;

            var moved = new List<PlacedPart>(parts.Count);
            RectInt box = Footprint(parts);

            foreach (PlacedPart part in parts)
            {
                Vector2Int size = part.RotatedSize;

                // Reflecting a rotation about the x axis: north and south are unchanged, east and
                // west trade places.
                int rotation = (4 - part.Rotation) % 4;

                int x = box.xMin + box.xMax - (part.Origin.x + size.x);

                PartDefinition def = twin != null ? twin(part.Definition) ?? part.Definition : part.Definition;

                moved.Add(new PlacedPart(def, new GridCoord(x, part.Origin.y, part.Origin.layer),
                    rotation, part.ColorIndex)
                {
                    Role = part.Role,
                });
            }

            return !checkFit || Fits(map, parts, moved) ? moved : null;
        }

        public static void Translate(List<PlacedPart> parts, int dx, int dy, int dLayer)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                PlacedPart part = parts[i];

                parts[i] = new PlacedPart(part.Definition,
                    new GridCoord(part.Origin.x + dx, part.Origin.y + dy, part.Origin.layer + dLayer),
                    part.Rotation, part.ColorIndex)
                {
                    Role = part.Role,
                };
            }
        }

        /// <summary>
        /// Whether the transformed group can stand where it has landed.
        ///
        /// Tested with the group itself taken out, since a set moving as one is allowed to occupy
        /// cells its own members are vacating - the same rule assemblies move under.
        /// </summary>
        static bool Fits(GridMap map, IReadOnlyCollection<PlacedPart> leaving, List<PlacedPart> arriving)
        {
            if (map == null)
                return true;

            var vacating = new HashSet<PlacedPart>(leaving);

            foreach (PlacedPart part in arriving)
                foreach (GridCoord cell in part.OccupiedCells())
                {
                    if (cell.layer < 0)
                        return false;

                    PlacedPart occupant = map.At(cell);
                    if (occupant != null && !vacating.Contains(occupant))
                        return false;
                }

            return true;
        }

        /// <summary>
        /// Copies, ready to be placed elsewhere, roles included.
        ///
        /// Roles were dropped at first on the grounds that a run has one start - but a terminal that
        /// arrives as a plain terminal has lost the thing that made it worth copying, and the player
        /// has to hunt for it and mark it again. Several starts release several balls, which is a
        /// feature; several goals all count. Nothing here needs to be unique.
        /// </summary>
        public static List<PlacedPart> Duplicate(IReadOnlyCollection<PlacedPart> parts)
        {
            var copies = new List<PlacedPart>(parts.Count);

            foreach (PlacedPart part in parts)
                copies.Add(new PlacedPart(part.Definition, part.Origin, part.Rotation, part.ColorIndex)
                {
                    Role = part.Role,
                });

            return copies;
        }

        /// <summary>Whether every part of a pasted group can go where it is being put.</summary>
        public static bool CanPlaceAll(GridMap map, List<PlacedPart> parts)
        {
            if (map == null || parts.Count == 0)
                return false;

            foreach (PlacedPart part in parts)
                foreach (GridCoord cell in part.OccupiedCells())
                {
                    if (cell.layer < 0 || map.At(cell) != null)
                        return false;
                }

            return true;
        }
    }
}
