using System.Collections.Generic;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Thin lines from the corners of the piece being placed to whatever they line up with.
    ///
    /// A ghost floating in the air tells you where a piece is but not what it is over, and at any
    /// distance a stud is a few pixels. The lines answer the question the player is actually asking -
    /// "is this above that one" - by drawing the relationship rather than leaving it to be judged by
    /// eye against a perspective view.
    ///
    /// Four of them, from the corners. Every connecting cell of a large piece would be a cage, and
    /// the corners are what a shape is lined up by.
    /// </summary>
    public sealed class AlignmentGuides : MonoBehaviour
    {
        public Material guideMaterial;

        [Tooltip("Thickness of the lines, in world units. A stud is 0.095 across.")]
        public float thickness = 0.004f;

        [Tooltip("How many corners to draw from.")]
        public int maxLines = 4;

        [SerializeField] Color mouthColour = new Color(0.5f, 1f, 0.6f, 0.55f);
        [SerializeField] Color downColour = new Color(0.45f, 0.9f, 1f, 0.4f);
        [SerializeField] Color upColour = new Color(1f, 0.8f, 0.35f, 0.3f);

        readonly List<GameObject> _pieces = new();
        int _used;

        /// <summary>
        /// Draws the guides for a placement. Called every frame the ghost is up, so it reuses the
        /// same objects rather than making new ones.
        /// </summary>
        public void Show(GridMap map, PlacedPart part)
        {
            _used = 0;

            if (part?.Definition == null || guideMaterial == null)
            {
                Hide();
                return;
            }

            foreach (Vector2Int cell in Anchors(part, maxLines))
            {
                // The column's own underside and top, not the part's. On a stepped piece the two
                // ends sit at different heights, and a line drawn from the part's base would start
                // inside the geometry at the raised end.
                int baseLayer = part.UndersideLayerAt(cell.x, cell.y);
                int topLayer = part.TopLayerAt(cell.x, cell.y);

                // Downward: to the top of whatever is below, or to the ground.
                Vector2Int outward = Outward(part, cell);

                int floor = Below(map, part, cell, baseLayer);
                if (floor < baseLayer)
                {
                    Draw(cell, outward, floor, baseLayer, downColour);
                    Mark(cell, floor, downColour);
                }

                // Upward: only when something is actually over it. A line into empty sky says
                // nothing, and four of them would be clutter over every piece placed in the open.
                int ceiling = Above(map, part, cell, topLayer);
                if (ceiling > topLayer)
                {
                    Draw(cell, outward, topLayer, ceiling, upColour);
                    Mark(cell, ceiling, upColour);
                }
            }

            for (int i = _used; i < _pieces.Count; i++)
                _pieces[i].SetActive(false);
        }

        [Tooltip("Total lines drawn for a selection, however many pieces are in it.")]
        public int maxSelectionLines = 8;

        /// <summary>
        /// Guides for a selection rather than for a piece about to be placed.
        ///
        /// Track first, and the columns under it: what a player checks before moving a run is what it
        /// is standing on, and a selection of a dozen bricks drawing from every corner would be a
        /// thicket. Two lines per piece, track before brick, until the budget runs out.
        /// </summary>
        public void Show(GridMap map, IReadOnlyCollection<PlacedPart> selection)
        {
            _used = 0;

            if (selection == null || selection.Count == 0 || guideMaterial == null)
            {
                Hide();
                return;
            }

            var ordered = new List<PlacedPart>(selection);
            ordered.Sort((a, b) => (b.HasPorts ? 1 : 0).CompareTo(a.HasPorts ? 1 : 0));

            int drawn = 0;

            // The mouths first. When a run is picked up, what matters is where its channels end and
            // what they are sitting over - a line from an arbitrary corner says nothing about whether
            // the piece will meet the one it is being carried towards.
            foreach (PlacedPart part in ordered)
            {
                if (drawn >= maxSelectionLines)
                    break;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    if (drawn >= maxSelectionLines)
                        break;

                    // Open ones only. Every piece of a run has two mouths, and all but the two at the
                    // ends are joined to the piece beside them - marking those spends the budget in
                    // the middle of the group and leaves the ends, which are the only places it can
                    // join anything, undrawn.
                    if (!IsOpen(map, selection, part, port))
                        continue;

                    foreach (Vector2Int cell in MouthCells(port))
                    {
                        if (drawn >= maxSelectionLines)
                            break;

                        // At the mouth's own height, not at the part's underside. On a ramp's raised
                        // end the antistuds are a whole brick below the channel, so a ring drawn on
                        // the underside sits nowhere near the thing it is meant to point at.
                        Mark(cell, port.FloorLayer, mouthColour);
                        drawn++;

                        // The line still runs from what the piece stands on, which is what places it
                        // in space - so on a raised end the two are deliberately apart, and the gap
                        // between them is the brick of ramp holding the channel up.
                        int under = part.UndersideLayerAt(cell.x, cell.y);
                        int floor = Below(map, part, cell, under);

                        if (floor < under)
                            Draw(cell, Outward(part, cell), floor, under, downColour);
                    }
                }
            }

            // Nothing with a mouth in it: a stack of bricks, a wall. Framed at the far corners of
            // the whole selection rather than the corners of each piece in it - what the player is
            // judging is where the group sits, and eight lines scattered through the middle of it
            // describe the pieces instead of the shape.
            if (drawn == 0)
                FrameCorners(map, ordered);

            for (int i = _used; i < _pieces.Count; i++)
                _pieces[i].SetActive(false);
        }

        /// <summary>
        /// Four lines at the outermost corners of a selection, down to whatever is under each.
        ///
        /// A line to the floor is what places a thing in space: without one a raised build could be
        /// anywhere along the line of sight, and no amount of turning the camera settles it as
        /// quickly as a post to the ground does.
        /// </summary>
        void FrameCorners(GridMap map, List<PlacedPart> selection)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            foreach (PlacedPart part in selection)
            foreach (GridCoord cell in part.OccupiedCells())
            {
                minX = Mathf.Min(minX, cell.x); maxX = Mathf.Max(maxX, cell.x);
                minY = Mathf.Min(minY, cell.y); maxY = Mathf.Max(maxY, cell.y);
            }

            if (minX == int.MaxValue)
                return;

            var corners = new[]
            {
                new Vector2Int(minX, minY), new Vector2Int(maxX, minY),
                new Vector2Int(minX, maxY), new Vector2Int(maxX, maxY),
            };

            foreach (Vector2Int corner in corners)
            {
                // The piece nearest that corner, since a selection is rarely a solid rectangle.
                PlacedPart owner = null;
                var at = corner;
                int nearest = int.MaxValue;

                foreach (PlacedPart part in selection)
                foreach (GridCoord cell in part.OccupiedCells())
                {
                    int distance = Mathf.Abs(cell.x - corner.x) + Mathf.Abs(cell.y - corner.y);

                    if (distance >= nearest)
                        continue;

                    nearest = distance;
                    owner = part;
                    at = new Vector2Int(cell.x, cell.y);
                }

                if (owner == null)
                    continue;

                int under = owner.UndersideLayerAt(at.x, at.y);
                int floor = Below(map, owner, at, under);

                Mark(at, floor, downColour);

                if (floor < under)
                    Draw(at, new Vector2Int(at.x >= (minX + maxX) * 0.5f ? 1 : 0,
                                            at.y >= (minY + maxY) * 0.5f ? 1 : 0),
                         floor, under, downColour);
            }
        }

        public void Hide()
        {
            foreach (GameObject piece in _pieces)
                piece.SetActive(false);

            _used = 0;
        }

        /// <summary>
        /// Which corner of the cell to draw on: the one facing away from the piece.
        ///
        /// Four lines all on the same corner of their cells would sit inside the piece on two sides.
        /// Taken outward, they frame it.
        /// </summary>
        static Vector2Int Outward(PlacedPart part, Vector2Int cell)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            foreach (GridCoord occupied in part.OccupiedCells())
            {
                minX = Mathf.Min(minX, occupied.x);
                minY = Mathf.Min(minY, occupied.y);
                maxX = Mathf.Max(maxX, occupied.x);
                maxY = Mathf.Max(maxY, occupied.y);
            }

            float midX = (minX + maxX) * 0.5f;
            float midY = (minY + maxY) * 0.5f;

            return new Vector2Int(cell.x >= midX ? 1 : 0, cell.y >= midY ? 1 : 0);
        }

        /// <summary>
        /// Whether a mouth has nothing joined to it, counting the rest of the selection as well as
        /// the build.
        ///
        /// The build alone is not enough: a group being carried is not in the map at all, so every
        /// one of its mouths would look open - including the many where its own pieces meet.
        /// </summary>
        static bool IsOpen(GridMap map, IReadOnlyCollection<PlacedPart> selection,
                           PlacedPart part, PlacedPart.WorldPort port)
        {
            foreach (PlacedPart other in selection)
            {
                if (other == part)
                    continue;

                foreach (PlacedPart.WorldPort candidate in other.WorldPorts())
                    if (candidate.MidlineHalfStuds == port.MidlineHalfStuds &&
                        Mathf.Abs(candidate.HeightUnits - port.HeightUnits) < 0.001f)
                        return false;
            }

            return map.FindConnection(part, port) == null;
        }

        /// <summary>
        /// The cells a channel mouth stands on - the width of the mouth, just inside the part.
        ///
        /// Read from the midline the same way a join is, so what is marked is exactly the ground a
        /// connection is judged against rather than an approximation of it.
        /// </summary>
        static IEnumerable<Vector2Int> MouthCells(PlacedPart.WorldPort port)
        {
            bool alongX = port.Facing is Facing.North or Facing.South;

            int centreAlong = (alongX ? port.MidlineHalfStuds.x : port.MidlineHalfStuds.y) / 2;
            int across = (alongX ? port.MidlineHalfStuds.y : port.MidlineHalfStuds.x) / 2;

            int width = Mathf.Max(1, port.WidthStuds);
            int alongMin = centreAlong - width / 2;

            // The mouth sits on a boundary; the cells belonging to the part are the ones behind it.
            int inside = port.Facing is Facing.North or Facing.East ? across - 1 : across;

            for (int i = 0; i < width; i++)
                yield return alongX
                    ? new Vector2Int(alongMin + i, inside)
                    : new Vector2Int(inside, alongMin + i);
        }

        /// <summary>Layer of the surface below this column, or the piece's own base if nothing is there.</summary>
        static int Below(GridMap map, PlacedPart part, Vector2Int cell, int baseLayer)
        {
            for (int layer = baseLayer - 1; layer >= 0; layer--)
            {
                PlacedPart occupant = map.At(new GridCoord(cell.x, cell.y, layer));

                if (occupant != null && occupant != part)
                    return occupant.TopLayerAt(cell.x, cell.y);
            }

            return 0;
        }

        /// <summary>Layer of the underside above this column, or the piece's own top if nothing is there.</summary>
        static int Above(GridMap map, PlacedPart part, Vector2Int cell, int topLayer)
        {
            for (int layer = topLayer; layer <= topLayer + 64; layer++)
            {
                PlacedPart occupant = map.At(new GridCoord(cell.x, cell.y, layer));

                if (occupant != null && occupant != part)
                    return layer;
            }

            return topLayer;
        }

        /// <summary>
        /// Which columns to draw from: the corners of the cells that actually connect.
        ///
        /// Studs first. On a funnel the connecting cells are scattered pads under a round bowl, and
        /// the two that matter are the shelf it offers - which are the ones carrying studs.
        /// </summary>
        static List<Vector2Int> Anchors(PlacedPart part, int max)
        {
            var studded = new List<Vector2Int>();
            var socketed = new List<Vector2Int>();
            var plain = new List<Vector2Int>();

            var seen = new HashSet<Vector2Int>();

            foreach (GridCoord cell in part.OccupiedCells())
            {
                var column = new Vector2Int(cell.x, cell.y);
                if (!seen.Add(column))
                    continue;

                if (part.HasTopStudAt(column.x, column.y)) studded.Add(column);
                else if (part.HasBottomSocketAt(column.x, column.y)) socketed.Add(column);
                else plain.Add(column);
            }

            // Studs first, because a part that offers them is placed by them - a funnel is caught by
            // its shelf and nothing else about it matters for lining up.
            //
            // Everything else is lined up by its outline. Choosing socket cells here was wrong for a
            // slide curve, whose antistuds run along one edge only, so all four lines ended up at one
            // mouth and told the player nothing about the other three corners of the piece.
            var all = new List<Vector2Int>(studded);
            all.AddRange(socketed);
            all.AddRange(plain);

            List<Vector2Int> pool = studded.Count > 0 ? studded
                : socketed.Count > 0 ? socketed
                : all;

            if (pool.Count <= max)
                return pool;

            // The four furthest apart, taken as the extremes of the pool's own bounds. A shape is
            // lined up by its corners, and picking the first four in scan order would put them all
            // along one edge.
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            foreach (Vector2Int column in pool)
            {
                minX = Mathf.Min(minX, column.x);
                minY = Mathf.Min(minY, column.y);
                maxX = Mathf.Max(maxX, column.x);
                maxY = Mathf.Max(maxY, column.y);
            }

            var corners = new[]
            {
                new Vector2Int(minX, minY), new Vector2Int(maxX, minY),
                new Vector2Int(minX, maxY), new Vector2Int(maxX, maxY),
            };

            var chosen = new List<Vector2Int>(max);

            foreach (Vector2Int corner in corners)
            {
                Vector2Int best = pool[0];
                int nearest = int.MaxValue;

                foreach (Vector2Int column in pool)
                {
                    int distance = Mathf.Abs(column.x - corner.x) + Mathf.Abs(column.y - corner.y);

                    if (distance < nearest && !chosen.Contains(column))
                    {
                        nearest = distance;
                        best = column;
                    }
                }

                if (!chosen.Contains(best))
                    chosen.Add(best);

                if (chosen.Count == max)
                    break;
            }

            return chosen;
        }

        void Draw(Vector2Int cell, Vector2Int outward, int fromLayer, int toLayer, Color colour)
        {
            GameObject line = Take();

            float bottom = fromLayer * GridCoord.LayerUnits;
            float top = toLayer * GridCoord.LayerUnits;

            // On the grid corner of the cell, not through the middle of the stud. A line down the
            // centre is hidden by the stud it is describing and by the piece above it; on the corner
            // it runs in clear air beside both, and lines up with the grid it is measuring against.
            var corner = new Vector3(
                (cell.x + (outward.x > 0 ? 1f : 0f)) * GridCoord.StudUnits,
                (bottom + top) * 0.5f,
                (cell.y + (outward.y > 0 ? 1f : 0f)) * GridCoord.StudUnits);

            Vector3 centre = corner;

            line.transform.SetPositionAndRotation(centre, Quaternion.identity);
            line.transform.localScale = new Vector3(thickness, Mathf.Max(0.001f, top - bottom), thickness);

            var renderer = line.GetComponent<MeshRenderer>();
            renderer.GetPropertyBlock(_block);
            _block.SetColor(BaseColor, colour);
            renderer.SetPropertyBlock(_block);

            line.SetActive(true);
        }

        /// <summary>
        /// A flat ring of a marker on the stud a line lands on.
        ///
        /// The line says which column; this says which stud, which is the thing being aimed at. Sized
        /// to a stud rather than a cell so it reads as picking one out rather than filling the square.
        /// </summary>
        void Mark(Vector2Int cell, int layer, Color colour)
        {
            GameObject marker = Take();

            // A hair above the surface. A ring drawn exactly on the ground is inside it, and a
            // selection resting on the floor showed nothing at all.
            var centre = new Vector3(
                (cell.x + 0.5f) * GridCoord.StudUnits,
                layer * GridCoord.LayerUnits + thickness,
                (cell.y + 0.5f) * GridCoord.StudUnits);

            marker.transform.SetPositionAndRotation(centre, Quaternion.identity);

            // A stud is 9.5 mm across; a touch wider so it rings the stud rather than hiding it.
            marker.transform.localScale = new Vector3(0.11f, thickness, 0.11f);

            var renderer = marker.GetComponent<MeshRenderer>();
            renderer.GetPropertyBlock(_block);
            _block.SetColor(BaseColor, colour);
            renderer.SetPropertyBlock(_block);

            marker.SetActive(true);
        }

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock _block;

        GameObject Take()
        {
            _block ??= new MaterialPropertyBlock();

            if (_used < _pieces.Count)
                return _pieces[_used++];

            GameObject line = GameObject.CreatePrimitive(PrimitiveType.Cube);
            line.name = "Guide";
            line.transform.SetParent(transform, false);

            Destroy(line.GetComponent<Collider>());

            var renderer = line.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = guideMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _pieces.Add(line);
            _used++;

            return line;
        }
    }
}
