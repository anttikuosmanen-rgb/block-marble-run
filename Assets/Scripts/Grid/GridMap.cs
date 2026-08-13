using System.Collections.Generic;
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

        public IReadOnlyCollection<PlacedPart> Parts => _parts;
        public int CellCount => _cells.Count;

        public PlacedPart At(GridCoord cell) => _cells.GetValueOrDefault(cell);
        public bool IsOccupied(GridCoord cell) => _cells.ContainsKey(cell);

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
        /// Ground level supports anything. Above it, at least one of the part's base cells must rest
        /// on a stud - which is why nothing stacks on track pieces, since they expose none.
        /// </summary>
        public bool IsSupported(PlacedPart part)
        {
            if (part.Origin.layer == 0)
                return true;

            foreach (GridCoord cell in part.OccupiedCells())
            {
                if (cell.layer != part.Origin.layer)
                    continue;

                PlacedPart below = _cells.GetValueOrDefault(cell.Below);
                if (below == null)
                    continue;

                // The supporting part must actually end at this layer; a tall part passing through
                // the cell below offers its side, not its top.
                if (below.TopLayer == part.Origin.layer && below.HasTopStudAt(cell.x, cell.y))
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

            _boundsValid = false;
            return true;
        }

        public void Clear()
        {
            _cells.Clear();
            _parts.Clear();
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
