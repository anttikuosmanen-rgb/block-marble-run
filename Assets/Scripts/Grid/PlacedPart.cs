using System.Collections.Generic;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Grid
{
    /// <summary>
    /// One part placed in the world: which definition, where, and turned which way. Rotation is
    /// resolved in grid space rather than by transforming geometry, so validation never depends on
    /// floating-point transforms.
    /// </summary>
    public sealed class PlacedPart
    {
        public readonly PartDefinition Definition;
        public readonly GridCoord Origin;

        /// <summary>Yaw in 90-degree steps, 0-3.</summary>
        public readonly int Rotation;

        public readonly byte ColorIndex;

        public GameObject Instance;

        public PlacedPart(PartDefinition definition, GridCoord origin, int rotation, byte colorIndex)
        {
            Definition = definition;
            Origin = origin;
            Rotation = ((rotation % 4) + 4) % 4;
            ColorIndex = colorIndex;
        }

        /// <summary>Footprint bounding size after rotation. Odd quarter turns swap the axes.</summary>
        public Vector2Int RotatedSize
        {
            get
            {
                Vector2Int size = Definition.footprintSize;
                return Rotation % 2 == 0 ? size : new Vector2Int(size.y, size.x);
            }
        }

        public int TopLayer => Origin.layer + Mathf.Max(1, Definition.heightLayers);

        /// <summary>
        /// Maps a cell of the unrotated footprint to its position after rotation.
        ///
        /// Derived to match a Quaternion.Euler(0, 90*rot, 0) applied to the mesh, so the collision
        /// grid and the rendered geometry can never disagree: a quarter turn about Y sends +X to -Z,
        /// which in cell terms is (x, y) -> (y, width-1-x).
        /// </summary>
        public static Vector2Int RotateCell(Vector2Int cell, Vector2Int size, int rotation)
        {
            for (int i = 0; i < rotation; i++)
            {
                cell = new Vector2Int(cell.y, size.x - 1 - cell.x);
                size = new Vector2Int(size.y, size.x);
            }
            return cell;
        }

        /// <summary>Every world cell this part occupies, across all of its layers.</summary>
        public IEnumerable<GridCoord> OccupiedCells()
        {
            Vector2Int size = Definition.footprintSize;
            int layers = Mathf.Max(1, Definition.heightLayers);

            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                if (!Definition.OccupiesCell(x, y))
                    continue;

                Vector2Int r = RotateCell(new Vector2Int(x, y), size, Rotation);

                for (int layer = 0; layer < layers; layer++)
                    yield return new GridCoord(Origin.x + r.x, Origin.y + r.y, Origin.layer + layer);
            }
        }

        /// <summary>
        /// Whether this part exposes a stud at the given world column, on its top surface. Parts with
        /// no studs (track pieces) return false everywhere, which is what stops anything stacking on
        /// them.
        /// </summary>
        public bool HasTopStudAt(int worldX, int worldY)
        {
            bool[] studs = Definition.topStuds;
            if (studs == null || studs.Length == 0)
                return false;

            Vector2Int size = Definition.footprintSize;

            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                if (!studs[y * size.x + x])
                    continue;

                Vector2Int r = RotateCell(new Vector2Int(x, y), size, Rotation);
                if (Origin.x + r.x == worldX && Origin.y + r.y == worldY)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// World transform for the rendered mesh. Meshes are modelled centred on their footprint with
        /// the base at zero, so the part sits at the centre of the cells it occupies.
        /// </summary>
        public void GetTransform(out Vector3 position, out Quaternion rotation)
        {
            Vector2Int size = RotatedSize;

            position = new Vector3(
                (Origin.x + size.x * 0.5f) * GridCoord.StudUnits,
                Origin.layer * GridCoord.LayerUnits,
                (Origin.y + size.y * 0.5f) * GridCoord.StudUnits);

            rotation = Quaternion.Euler(0f, 90f * Rotation, 0f);
        }
    }
}
