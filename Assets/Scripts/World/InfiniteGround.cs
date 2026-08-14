using UnityEngine;

namespace BlockMarbleRun.World
{
    /// <summary>
    /// Keeps the ground quad centred under the camera so the grid appears to extend forever
    /// (DESIGN.md §4.2).
    ///
    /// Snapping the follow to whole stud pitches is the part that matters: sliding the quad
    /// continuously would drag the world-space pattern against the geometry standing on it, and the
    /// grid would visibly swim as the camera moves.
    /// </summary>
    [ExecuteAlways]
    public sealed class InfiniteGround : MonoBehaviour
    {
        public Camera targetCamera;
        [SerializeField] float snap = Grid.GridCoord.StudUnits;

        /// <summary>
        /// The step the follow snaps to. Whatever pattern the ground carries has to repeat over
        /// exactly this distance, or it slides against the world as the camera moves.
        ///
        /// The stud grid is drawn from world position and so does not care. A texture does: its UVs
        /// are fixed to the quad, so a quad that steps by anything other than a whole tile drags the
        /// grain along with the camera - which reads as the ground being much closer than it is.
        /// </summary>
        public float Snap
        {
            get => snap;
            set => snap = value;
        }

        void LateUpdate()
        {
            Camera cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam == null)
                return;

            Vector3 p = cam.transform.position;
            transform.position = new Vector3(
                Mathf.Round(p.x / snap) * snap,
                0f,
                Mathf.Round(p.z / snap) * snap);
        }
    }
}
