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
        [SerializeField] LayerMask partLayers = ~0;

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

                // Placing on a top face stacks; placing on a side face extends sideways.
                hit.Cell = partHit.normal.y > 0.5f
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
        static int LayerAbove(Collider collider, GridCoord hitCell)
        {
            var marker = collider.GetComponentInParent<PlacedPartMarker>();
            return marker != null ? marker.Part.TopLayer : hitCell.layer + 1;
        }
    }
}
