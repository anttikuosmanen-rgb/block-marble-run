using UnityEngine;

namespace BlockMarbleRun
{
    /// <summary>
    /// Slow automatic orbit for the M0 part gallery, so the WebGL build shows every face of the
    /// imported meshes without needing input wired up. Replaced by the real orbit camera in M1.
    /// </summary>
    public sealed class GalleryOrbit : MonoBehaviour
    {
        public Vector3 pivot;
        public float degreesPerSecond = 6f;

        void Update() => transform.RotateAround(pivot, Vector3.up, degreesPerSecond * Time.deltaTime);
    }
}
