using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.World
{
    public struct BuildHit
    {
        public bool Valid;

        /// <summary>Cell the new part should occupy.</summary>
        public GridCoord Cell;

        /// <summary>Collider that was hit, or null when the ground plane answered.</summary>
        public Collider Collider;

        public Vector3 Point;
    }

    /// <summary>
    /// Turns a screen ray into the grid cell a part would be placed in.
    ///
    /// The ground is solved analytically rather than with a collider (DESIGN.md §4.2). A giant box
    /// standing in for an infinite plane would impose an arbitrary edge and waste precision far from
    /// the origin; a ray-plane intersection is exact everywhere and costs nothing.
    /// </summary>
    public sealed class BuildRaycaster : MonoBehaviour
    {
        public Camera buildCamera;
        [Tooltip("Which layers count as parts. The physics floor is excluded so it cannot be built on.")]
        public LayerMask partLayers = ~0;

        [Tooltip("How far a ray may travel before the ground answer is discarded as a grazing hit.")]
        [SerializeField] float maxDistance = 500f;

        static readonly Plane Ground = new Plane(Vector3.up, 0f);

        public Camera Camera => buildCamera;

        void Awake() => buildCamera = buildCamera != null ? buildCamera : Camera.main;

        /// <summary>Cell for placing a new part: on top of whatever was hit, or on the ground.</summary>
        public BuildHit RaycastPlacement(Vector2 screenPosition)
        {
            var hit = new BuildHit();
            if (buildCamera == null)
                return hit;

            Ray ray = buildCamera.ScreenPointToRay(screenPosition);

            bool hitPart = Physics.Raycast(ray, out RaycastHit partHit, maxDistance, partLayers);
            bool hitGround = Ground.Raycast(ray, out float groundDistance) && groundDistance <= maxDistance;

            // Nearest wins. A part standing on the ground would otherwise be shadowed by the plane
            // stretching out behind it.
            if (hitPart && (!hitGround || partHit.distance <= groundDistance))
            {
                hit.Valid = true;
                hit.Collider = partHit.collider;
                hit.Point = partHit.point;

                // Step inside the surface before converting, so a hit exactly on a face boundary
                // resolves to the part that was clicked rather than the empty cell beside it.
                Vector3 inside = partHit.point - partHit.normal * (GridCoord.StudUnits * 0.25f);
                GridCoord cell = GridCoord.FromWorld(inside);

                // A top face stacks. So does a side face, when the part offers studs in that column:
                // a pillar is fourteen layers of side and a couple of studs, so aiming at the studs
                // themselves means hitting a target a few pixels across, and everything else put the
                // piece on the floor beside it.
                //
                // A side face only extends sideways where nothing can be stacked anyway - the side of
                // a length of track, where there are no studs to catch.
                bool stacks = partHit.normal.y > 0.5f || HasStudAt(partHit.collider, cell);

                hit.Cell = stacks
                    ? new GridCoord(cell.x, cell.y, LayerAbove(partHit.collider, cell))
                    : new GridCoord(
                        cell.x + Mathf.RoundToInt(partHit.normal.x),
                        cell.y + Mathf.RoundToInt(partHit.normal.z),
                        cell.layer);

                return hit;
            }

            if (hitGround)
            {
                hit.Valid = true;
                hit.Point = ray.GetPoint(groundDistance);
                GridCoord cell = GridCoord.FromWorld(hit.Point);
                hit.Cell = new GridCoord(cell.x, cell.y, 0);
            }

            return hit;
        }

        /// <summary>
        /// Nearest world point under the cursor, counting the ground plane as well as parts.
        ///
        /// The visible ground has no collider - it would shadow the analytic placement raycast - so a
        /// collider-only query returns nothing at all when the player points at open floor.
        /// </summary>
        public bool RaycastPoint(Vector2 screenPosition, out Vector3 point)
        {
            point = default;
            if (buildCamera == null)
                return false;

            Ray ray = buildCamera.ScreenPointToRay(screenPosition);

            bool hitPart = Physics.Raycast(ray, out RaycastHit partHit, maxDistance, partLayers);
            bool hitGround = Ground.Raycast(ray, out float groundDistance) && groundDistance <= maxDistance;

            if (hitPart && (!hitGround || partHit.distance <= groundDistance))
            {
                point = partHit.point;
                return true;
            }

            if (!hitGround)
                return false;

            point = ray.GetPoint(groundDistance);
            return true;
        }

        /// <summary>
        /// Cell under the cursor on a horizontal plane at a given height, ignoring all geometry.
        ///
        /// For sliding a held placement. Reading the cursor off whatever the ray happens to strike
        /// works while it strikes the build, and jumps the moment it misses: a few pixels past the
        /// edge of a pillar the ray carries on to the floor behind it, and the piece leaps across the
        /// world. A plane at the piece's own height has no edges to fall off.
        /// </summary>
        public bool RaycastLevel(Vector2 screenPosition, float worldY, out GridCoord cell)
        {
            cell = default;
            if (buildCamera == null)
                return false;

            var plane = new Plane(Vector3.up, new Vector3(0f, worldY, 0f));
            Ray ray = buildCamera.ScreenPointToRay(screenPosition);

            if (!plane.Raycast(ray, out float distance) || distance > maxDistance)
                return false;

            Vector3 point = ray.GetPoint(distance);

            // The plane sits at the piece's base, so nudge up into the layer it occupies before
            // converting - a point exactly on a boundary belongs to the layer below it.
            cell = GridCoord.FromWorld(point + Vector3.up * (GridCoord.LayerUnits * 0.5f));
            return true;
        }

        /// <summary>Cell for picking an existing part - the cell actually hit, not the one above it.</summary>
        public BuildHit RaycastPick(Vector2 screenPosition)
        {
            var hit = new BuildHit();
            if (buildCamera == null)
                return hit;

            Ray ray = buildCamera.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out RaycastHit partHit, maxDistance, partLayers))
                return hit;

            hit.Valid = true;
            hit.Collider = partHit.collider;
            hit.Point = partHit.point;
            hit.Cell = GridCoord.FromWorld(partHit.point - partHit.normal * (GridCoord.StudUnits * 0.25f));
            return hit;
        }

        /// <summary>
        /// The layer above the part that was hit. Uses the part's own top rather than the hit cell,
        /// so clicking the top of a two-layer slide stacks above the whole part instead of halfway up.
        /// </summary>
        /// <summary>Whether the part hit offers a stud in the column that was hit.</summary>
        static bool HasStudAt(Collider collider, GridCoord hitCell)
        {
            var marker = collider.GetComponentInParent<PlacedPartMarker>();
            return marker != null && marker.Part.HasTopStudAt(hitCell.x, hitCell.y);
        }

        static int LayerAbove(Collider collider, GridCoord hitCell)
        {
            var marker = collider.GetComponentInParent<PlacedPartMarker>();
            return marker != null ? marker.Part.TopLayerAt(hitCell.x, hitCell.y) : hitCell.layer + 1;
        }
    }
}
