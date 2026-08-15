using System;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    /// <summary>
    /// A cell in the build grid: stud columns in X and Y, brick layers in Z. Integer throughout, so
    /// the world is unbounded by construction (DESIGN.md §4.2) and float drift can never accumulate
    /// into a misplaced brick.
    /// </summary>
    [Serializable]
    public struct GridCoord : IEquatable<GridCoord>
    {
        /// <summary>Stud pitch in world units: 16 mm at the project's 0.01 import scale.</summary>
        public const float StudUnits = 0.16f;

        /// <summary>Brick layer height in world units: 19.2 mm.</summary>
        /// <summary>
        /// One grid layer, in world units. Half a brick.
        ///
        /// The grid used to step a whole brick at a time, which left nowhere for a plate to sit - and
        /// plates are not a special case, they are what half the interesting parts are made of: the
        /// funnel's shelf, anything that has to meet a channel halfway. Halving the step costs an
        /// integer per part and buys the whole half-height family.
        /// </summary>
        public const float LayerUnits = 0.096f;

        /// <summary>
        /// One brick, in world units. Two grid layers.
        ///
        /// For anything a person counts in bricks rather than in grid steps - how far a ball could
        /// climb, how deep the water is. Those numbers were tuned against bricks and should not
        /// silently double because the grid underneath them got finer.
        /// </summary>
        public const float BrickUnits = 0.192f;

        public int x;
        public int y;
        public int layer;

        public GridCoord(int x, int y, int layer)
        {
            this.x = x;
            this.y = y;
            this.layer = layer;
        }

        public GridCoord Below => new GridCoord(x, y, layer - 1);
        public GridCoord Above => new GridCoord(x, y, layer + 1);

        /// <summary>Centre of this cell in world space, at the base of its layer.</summary>
        public Vector3 CellCentre => new Vector3(
            (x + 0.5f) * StudUnits,
            layer * LayerUnits,
            (y + 0.5f) * StudUnits);

        /// <summary>
        /// The cell containing a world point. Floor rather than round: a point anywhere inside a cell
        /// belongs to that cell, whereas rounding would snap to the nearest cell centre and shift the
        /// boundary by half a stud.
        /// </summary>
        public static GridCoord FromWorld(Vector3 world) => new GridCoord(
            Mathf.FloorToInt(world.x / StudUnits),
            Mathf.FloorToInt(world.z / StudUnits),
            Mathf.FloorToInt(world.y / LayerUnits + 0.001f));

        public bool Equals(GridCoord other) => x == other.x && y == other.y && layer == other.layer;
        public override bool Equals(object obj) => obj is GridCoord other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, layer);
        public override string ToString() => $"({x}, {y}, L{layer})";

        public static bool operator ==(GridCoord a, GridCoord b) => a.Equals(b);
        public static bool operator !=(GridCoord a, GridCoord b) => !a.Equals(b);
    }
}
