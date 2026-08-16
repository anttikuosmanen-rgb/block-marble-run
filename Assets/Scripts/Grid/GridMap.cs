using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    public enum PlacementResult
    {
        Valid,
        Blocked,
        Unsupported,
    }

    /// <summary>
    /// Sparse occupancy over an unbounded world (DESIGN.md §4.2). One dictionary entry per occupied
    /// cell gives O(1) collision tests with no fixed extent, so "infinite baseplate" costs nothing -
    /// it is the absence of a bounds check rather than a feature.
    /// </summary>
    public sealed class GridMap
    {
        readonly Dictionary<GridCoord, PlacedPart> _cells = new();
        readonly HashSet<PlacedPart> _parts = new();

        bool _boundsValid;
        BoundsInt _bounds;

        /// <summary>
        /// Bumped on every change. Lets views refresh when the build actually changed rather than
        /// rebuilding themselves every frame or wiring an event through each edit path.
        /// </summary>
        public int Version { get; private set; }

        public IReadOnlyCollection<PlacedPart> Parts => _parts;
        public int CellCount => _cells.Count;

        public PlacedPart At(GridCoord cell) => _cells.GetValueOrDefault(cell);
        public bool IsOccupied(GridCoord cell) => _cells.ContainsKey(cell);

        /// <summary>
        /// O(1) membership test. Going through the IReadOnlyCollection would fall back to a linear
        /// scan, which turns pruning a large selection into quadratic work.
        /// </summary>
        public bool Contains(PlacedPart part) => part != null && _parts.Contains(part);

        /// <summary>Highest layer any part has ever reached; an upper bound for downward scans.</summary>
        int _maxLayer;

        /// <summary>
        /// The layer at which something dropped into this column would come to rest: the top of the
        /// highest part there, or ground level.
        ///
        /// Scans downward from the tallest thing built rather than tracking a height map, because a
        /// height map has to be repaired on every removal and builds are only ever a few dozen layers
        /// tall. The bound is never lowered on removal - it stays a safe over-estimate.
        /// </summary>
        public int ColumnRestLayer(int x, int y)
        {
            for (int layer = _maxLayer; layer >= 0; layer--)
            {
                PlacedPart part = _cells.GetValueOrDefault(new GridCoord(x, y, layer));
                if (part != null)
                    return part.TopLayerAt(x, y);
            }

            return 0;
        }

        public PlacementResult CanPlace(PlacedPart part)
        {
            foreach (GridCoord cell in part.OccupiedCells())
            {
                if (cell.layer < 0)
                    return PlacementResult.Blocked;

                if (_cells.ContainsKey(cell))
                    return PlacementResult.Blocked;
            }

            return IsSupported(part) ? PlacementResult.Valid : PlacementResult.Unsupported;
        }

        /// <summary>
        /// Channel mouths clutch to each other exactly as studs clutch to anti-studs: two parts whose
        /// channels meet, face to face and at the same height, hold each other up.
        ///
        /// Without this a track laid out at height is "floating" no matter how solidly it is joined
        /// to the run it continues, and the build would sprout scaffolding under a track that is
        /// already attached at both ends.
        /// </summary>
        public bool HasPortConnection(PlacedPart part)
        {
            foreach (PlacedPart.WorldPort port in part.WorldPorts())
                if (FindConnection(part, port) != null)
                    return true;

            return false;
        }

        /// <summary>Half a millimetre at project scale; channel floors land on exact layer multiples.</summary>
        const float HeightTolerance = 0.005f;

        /// <summary>
        /// The part whose channel joins this mouth, or null.
        ///
        /// Mouths meet only when their centre lines coincide exactly. Matching per-cell instead let a
        /// run join while offset by one stud, since one cell of a two-stud mouth still overlapped one
        /// cell of its neighbour - the joint reported as connected while the channels visibly stepped
        /// sideways.
        /// </summary>
        public PlacedPart FindConnection(PlacedPart part, PlacedPart.WorldPort port)
        {
            Facing wanted = PlacedPart.WorldPort.Opposite(port.Facing);

            foreach (Vector2Int cell in port.OutsideCells())
            {
                foreach (PlacedPart other in PartsInColumn(cell.x, cell.y))
                {
                    if (ReferenceEquals(other, part))
                        continue;

                    foreach (PlacedPart.WorldPort otherPort in other.WorldPorts())
                    {
                        if (otherPort.Facing != wanted)
                            continue;

                        if (otherPort.MidlineHalfStuds != port.MidlineHalfStuds)
                            continue;

                        if (Mathf.Abs(otherPort.HeightUnits - port.HeightUnits) <= HeightTolerance)
                            return other;
                    }
                }
            }

            return null;
        }

        /// <summary>Distinct parts occupying any layer of a column, lowest first.</summary>
        public IEnumerable<PlacedPart> PartsInColumn(int x, int y)
        {
            PlacedPart previous = null;

            for (int layer = 0; layer <= _maxLayer; layer++)
            {
                PlacedPart part = _cells.GetValueOrDefault(new GridCoord(x, y, layer));
                if (part == null || ReferenceEquals(part, previous))
                    continue;

                previous = part;
                yield return part;
            }
        }

        /// <summary>
        /// Ground level supports anything. Above it, a part is held either by a stud underneath or by
        /// a channel joined to a neighbouring channel - the two clutch systems are equal in standing.
        /// </summary>
        public bool IsSupported(PlacedPart part)
        {
            if (part.Origin.layer == 0)
                return true;

            if (HasPortConnection(part))
                return true;

            // Asked per column, at that column's own underside. Testing only the part's base layer
            // works while a part is flat underneath and misses everything else: a slide curve carries
            // antistuds on the floor at one end and a whole brick up at the other, and the raised
            // pair could never meet a stud however exactly it was lined up over one.
            var asked = new HashSet<Vector2Int>();

            foreach (GridCoord cell in part.OccupiedCells())
            {
                var column = new Vector2Int(cell.x, cell.y);
                if (!asked.Add(column))
                    continue;

                int underside = part.UndersideLayerAt(column.x, column.y);
                if (underside <= 0)
                    return true;   // that column reaches the ground

                PlacedPart below = _cells.GetValueOrDefault(new GridCoord(column.x, column.y, underside - 1));
                if (below == null || below == part)
                    continue;

                // The supporting part must actually end at this layer; a tall part passing through
                // the cell below offers its side, not its top.
                if (below.TopLayerAt(column.x, column.y) == underside &&
                    below.HasTopStudAt(column.x, column.y))
                    return true;
            }

            return false;
        }

        public bool Add(PlacedPart part)
        {
            if (CanPlace(part) == PlacementResult.Blocked)
                return false;

            foreach (GridCoord cell in part.OccupiedCells())
                _cells[cell] = part;

            _parts.Add(part);
            Version++;
            _maxLayer = Mathf.Max(_maxLayer, part.TopLayer);
            _boundsValid = false;
            return true;
        }

        public bool Remove(PlacedPart part)
        {
            if (!_parts.Remove(part))
                return false;

            foreach (GridCoord cell in part.OccupiedCells())
                if (_cells.TryGetValue(cell, out PlacedPart occupant) && occupant == part)
                    _cells.Remove(cell);

            Version++;
            _boundsValid = false;
            return true;
        }

        public void Clear()
        {
            _cells.Clear();
            _parts.Clear();
            Version++;
            _maxLayer = 0;
            _boundsValid = false;
        }

        /// <summary>
        /// Occupied extent, for framing the camera. Cached because an unbounded world has no cheap
        /// upper limit to iterate and the camera asks for this on demand.
        /// </summary>
        public BoundsInt OccupiedBounds
        {
            get
            {
                if (_boundsValid)
                    return _bounds;

                _boundsValid = true;

                if (_cells.Count == 0)
                    return _bounds = new BoundsInt(Vector3Int.zero, Vector3Int.one);

                var min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
                var max = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

                foreach (GridCoord c in _cells.Keys)
                {
                    min = Vector3Int.Min(min, new Vector3Int(c.x, c.layer, c.y));
                    max = Vector3Int.Max(max, new Vector3Int(c.x, c.layer, c.y));
                }

                return _bounds = new BoundsInt(min, max - min + Vector3Int.one);
            }
        }
    }
}
