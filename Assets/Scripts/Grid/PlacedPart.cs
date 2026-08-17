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

        /// <summary>Mutable, like Role: a piece can be repainted after it is placed.</summary>
        public byte ColorIndex;

        public GameObject Instance;

        /// <summary>
        /// Mutable, unlike the rest of this type: a role is assigned after placement, by pointing at
        /// a piece already in the build rather than by choosing a different part to place.
        /// </summary>
        public PartRole Role;

        /// <summary>
        /// Only a dead end can take a role. A single mouth is what makes it unambiguous - a marble
        /// released into a through-piece has two ways to go, and "arrived" at one means only that it
        /// passed over.
        /// </summary>
        public bool CanTakeRole => Definition.ports is { Length: 1 };

        public PlacedPart(PartDefinition definition, GridCoord origin, int rotation, byte colorIndex,
                          PartRole role = PartRole.None)
        {
            Definition = definition;
            Origin = origin;
            Rotation = ((rotation % 4) + 4) % 4;
            ColorIndex = colorIndex;
            Role = role;
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
        /// The layer this part's surface reaches in one particular column, which is not always the
        /// top of the part.
        ///
        /// A brick is the same height everywhere and the distinction never arose. A funnel is not: it
        /// stands three layers tall at its rim while the shelf it offers the next piece is one layer
        /// up, and asking the part for its height answers about the rim. Anything stacked on the
        /// shelf was then judged to be floating two layers below where the part apparently ended, so
        /// nothing could be built on it at all.
        ///
        /// Read from the per-layer masks, which already know: no new data, and it cannot disagree
        /// with the occupancy the rest of the grid is using.
        /// </summary>
        public int TopLayerAt(int worldX, int worldY)
        {
            if (!LocalCell(worldX, worldY, out Vector2Int local))
                return TopLayer;

            int layers = Mathf.Max(1, Definition.heightLayers);

            for (int layer = layers - 1; layer >= 0; layer--)
                if (Definition.OccupiesCell(local.x, local.y, layer))
                    return Origin.layer + layer + 1;

            return Origin.layer;
        }

        /// <summary>
        /// The layer this part's underside sits at in one column, which on a stepped part is not its
        /// base. A slide curve carries antistuds on the floor at one end and a brick up at the other.
        /// </summary>
        public int UndersideLayerAt(int worldX, int worldY)
        {
            if (!LocalCell(worldX, worldY, out Vector2Int local))
                return Origin.layer;

            int layers = Mathf.Max(1, Definition.heightLayers);

            for (int layer = 0; layer < layers; layer++)
                if (Definition.OccupiesCell(local.x, local.y, layer))
                    return Origin.layer + layer;

            return Origin.layer;
        }

        /// <summary>The part's own cell under a world column, or false when the column is not its.</summary>
        bool LocalCell(int worldX, int worldY, out Vector2Int local)
        {
            Vector2Int size = Definition.footprintSize;

            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                Vector2Int r = RotateCell(new Vector2Int(x, y), size, Rotation);

                if (Origin.x + r.x == worldX && Origin.y + r.y == worldY)
                {
                    local = new Vector2Int(x, y);
                    return true;
                }
            }

            local = default;
            return false;
        }

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
                {
                    // Per layer, so a ramp's open underside stays free rather than being claimed by
                    // the part hanging above it.
                    if (Definition.OccupiesCell(x, y, layer))
                        yield return new GridCoord(Origin.x + r.x, Origin.y + r.y, Origin.layer + layer);
                }
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

            if (!LocalCell(worldX, worldY, out Vector2Int local))
                return false;

            int index = local.y * Definition.footprintSize.x + local.x;
            return index < studs.Length && studs[index];
        }

        /// <summary>
        /// Whether the part's underside is flat against its base in this world column - that is,
        /// whether there is anything there for a pillar to meet, or a stud to clutch into.
        /// </summary>
        public bool HasBottomSocketAt(int worldX, int worldY)
        {
            bool[] sockets = Definition.bottomSockets;
            if (sockets == null || sockets.Length == 0)
                return false;

            if (!LocalCell(worldX, worldY, out Vector2Int local))
                return false;

            int index = local.y * Definition.footprintSize.x + local.x;
            return index < sockets.Length && sockets[index];
        }

        /// <summary>
        /// A channel mouth in world terms: which cell it sits on, which way it faces, and the height
        /// of its channel floor.
        /// </summary>
        public readonly struct WorldPort
        {
            /// <summary>Mouth centre in world half-studs. Two mouths meet only when these are equal.</summary>
            public readonly Vector2Int MidlineHalfStuds;

            public readonly Facing Facing;
            public readonly float HeightUnits;
            public readonly int WidthStuds;

            public WorldPort(Vector2Int midlineHalfStuds, Facing facing, float heightUnits, int widthStuds)
            {
                MidlineHalfStuds = midlineHalfStuds;
                Facing = facing;
                HeightUnits = heightUnits;
                WidthStuds = widthStuds;
            }

            public static Facing Opposite(Facing facing) => (Facing)(((int)facing + 2) % 4);

            /// <summary>
            /// The cells immediately outside the mouth, where a part continuing this run would sit.
            /// Used only to find candidates; whether they actually join is decided by comparing
            /// midlines exactly.
            /// </summary>
            /// <summary>The cells just inside the mouth - the part's own geometry at the channel end.</summary>
            public IEnumerable<Vector2Int> InsideCells()
            {
                bool alongX = Facing == Facing.North || Facing == Facing.South;
                int halfWidth = Mathf.Max(1, WidthStuds) / 2;

                int centreAlong = (alongX ? MidlineHalfStuds.x : MidlineHalfStuds.y) / 2;
                int across = (alongX ? MidlineHalfStuds.y : MidlineHalfStuds.x) / 2;

                // The mirror of OutsideCells: whichever side of the boundary that one is not on.
                int inside = Facing is Facing.North or Facing.East ? across - 1 : across;

                for (int offset = -halfWidth; offset < halfWidth; offset++)
                {
                    int along = centreAlong + offset;
                    yield return alongX ? new Vector2Int(along, inside) : new Vector2Int(inside, along);
                }
            }

            /// <summary>Where the mouth sits in the world, for drawing a marker on it.</summary>
            public Vector3 WorldPosition => new Vector3(
                MidlineHalfStuds.x * GridCoord.StudUnits * 0.5f,
                HeightUnits,
                MidlineHalfStuds.y * GridCoord.StudUnits * 0.5f);

            /// <summary>Outward direction of the mouth, for orienting that marker.</summary>
            public Vector3 OutwardDirection => Facing switch
            {
                Facing.North => Vector3.forward,
                Facing.East => Vector3.right,
                Facing.South => Vector3.back,
                _ => Vector3.left,
            };

            /// <summary>Absolute layer the channel floor sits in, for sizing a pillar under it.</summary>
            public int FloorLayer => Mathf.FloorToInt(HeightUnits / GridCoord.LayerUnits + 0.001f);

            public IEnumerable<Vector2Int> OutsideCells()
            {
                bool alongX = Facing == Facing.North || Facing == Facing.South;

                // The midline sits on a stud boundary, so the mouth spans width/2 studs either side.
                int halfWidth = Mathf.Max(1, WidthStuds) / 2;

                int centreAlong = (alongX ? MidlineHalfStuds.x : MidlineHalfStuds.y) / 2;
                int across = (alongX ? MidlineHalfStuds.y : MidlineHalfStuds.x) / 2;

                // North and East open onto the cell the boundary line touches; South and West onto
                // the one behind it.
                int outside = Facing is Facing.North or Facing.East ? across : across - 1;

                for (int offset = -halfWidth; offset < halfWidth; offset++)
                {
                    int along = centreAlong + offset;
                    yield return alongX ? new Vector2Int(along, outside) : new Vector2Int(outside, along);
                }
            }
        }

        /// <summary>Millimetres to world units, matching the STL import scale.</summary>
        public const float MmToUnits = 0.01f;

        /// <summary>
        /// This part's channel mouths, rotated and placed into the world.
        ///
        /// A quarter turn maps +X to -Y, which advances a facing by one step - the same relationship
        /// the footprint rotation uses, so cells and facings can never disagree about which way a
        /// rotated part points.
        /// </summary>
        public IEnumerable<WorldPort> WorldPorts()
        {
            TrackPort[] ports = Definition.ports;
            if (ports == null)
                yield break;

            Vector2Int halfStudSize = Definition.footprintSize * 2;

            foreach (TrackPort port in ports)
            {
                Vector2Int midline = RotateHalfStudPoint(port.midlineHalfStuds, halfStudSize, Rotation);
                var facing = (Facing)(((int)port.facing + Rotation) % 4);

                yield return new WorldPort(
                    new Vector2Int(Origin.x * 2 + midline.x, Origin.y * 2 + midline.y),
                    facing,
                    Origin.layer * GridCoord.LayerUnits + port.heightMm * MmToUnits,
                    Mathf.Max(1, port.widthStuds));
            }
        }

        /// <summary>
        /// Rotates a point measured from the footprint's min corner, in half-studs.
        ///
        /// This is the continuous form of <see cref="RotateCell"/>: a quarter turn sends (x, y) to
        /// (y, width - x). Cell rotation subtracts one because it names a cell rather than a position,
        /// and using that form here would shift every mouth half a stud off the boundary it sits on.
        /// </summary>
        public static Vector2Int RotateHalfStudPoint(Vector2Int point, Vector2Int halfStudSize, int rotation)
        {
            for (int i = 0; i < rotation; i++)
            {
                point = new Vector2Int(point.y, halfStudSize.x - point.x);
                halfStudSize = new Vector2Int(halfStudSize.y, halfStudSize.x);
            }

            return point;
        }

        public bool HasPorts => Definition.ports is { Length: > 0 };

        /// <summary>
        /// World transform for the rendered mesh. Meshes are modelled centred on their footprint with
        /// the base at zero, so the part sits at the centre of the cells it occupies.
        /// </summary>
        public void GetTransform(out Vector3 position, out Quaternion rotation)
        {
            Vector2Int size = RotatedSize;

            var footprintCentre = new Vector3(
                (Origin.x + size.x * 0.5f) * GridCoord.StudUnits,
                Origin.layer * GridCoord.LayerUnits,
                (Origin.y + size.y * 0.5f) * GridCoord.StudUnits);

            rotation = Quaternion.Euler(0f, 90f * Rotation, 0f);

            // Line the mesh's own centre up with the footprint's, instead of assuming the mesh was
            // modelled centred on its origin. Most are, but u_turn runs from -15.9 to +62.3 mm in X
            // and u_turn_slide is offset by a full stud in Y; drawing those as centred puts the
            // geometry a stud away from the cells it occupies, so channels appear to join one stud
            // inside the neighbouring piece while the grid considers them correctly aligned.
            Vector2 pivot = Definition.pivotOffsetUnits;
            position = footprintCentre - rotation * new Vector3(pivot.x, 0f, pivot.y);

            // Lifted for parts that stand on the studs rather than between them.
            position.y += Definition.verticalOffsetUnits;
        }
    }
}
