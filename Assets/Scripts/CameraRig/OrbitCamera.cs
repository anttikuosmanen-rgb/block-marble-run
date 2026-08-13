using BlockMarbleRun.Grid;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.CameraRig
{
    /// <summary>
    /// Orbit, pan and dolly around a focus point.
    ///
    /// Drag-to-orbit rather than pointer lock: WebGL only grants pointer lock in response to a user
    /// gesture, so an FPS-style capture would fail exactly where the game has to work (DESIGN.md §0.1).
    /// </summary>
    public sealed class OrbitCamera : MonoBehaviour
    {
        [SerializeField] Vector3 pivot;
        [SerializeField] float distance = 3f;
        [SerializeField] float yaw = 35f;
        [SerializeField] float pitch = 38f;

        [Header("Limits")]
        [SerializeField] float minPitch = 5f;
        [SerializeField] float maxPitch = 85f;
        [SerializeField] float minDistance = 0.4f;
        [SerializeField] float maxDistance = 60f;

        [Header("Sensitivity")]
        [SerializeField] float orbitSpeed = 0.25f;
        [SerializeField] float panSpeed = 1.0f;
        [SerializeField] float zoomSpeed = 0.12f;
        [SerializeField] float smoothing = 12f;

        Vector3 _targetPivot;
        float _targetDistance;

        public Vector3 Pivot => pivot;

        void Awake()
        {
            _targetPivot = pivot;
            _targetDistance = distance;
            ApplyImmediate();
        }

        void LateUpdate()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();

                // Right or middle drag, so left stays free for placing parts.
                if (mouse.rightButton.isPressed)
                {
                    yaw += delta.x * orbitSpeed;
                    pitch = Mathf.Clamp(pitch - delta.y * orbitSpeed, minPitch, maxPitch);
                }
                else if (mouse.middleButton.isPressed)
                {
                    // Pan in the camera's own plane, scaled by distance so the world keeps pace with
                    // the cursor whether zoomed in on one brick or out over the whole build.
                    float scale = _targetDistance * panSpeed * 0.002f;
                    _targetPivot -= (transform.right * delta.x + transform.up * delta.y) * scale;
                }

                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    // Proportional zoom: a fixed step crawls when far out and overshoots when close in.
                    _targetDistance = Mathf.Clamp(
                        _targetDistance * (1f - Mathf.Sign(scroll) * zoomSpeed),
                        minDistance, maxDistance);
                }
            }

            float t = 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            pivot = Vector3.Lerp(pivot, _targetPivot, t);
            distance = Mathf.Lerp(distance, _targetDistance, t);
            ApplyImmediate();
        }

        void ApplyImmediate()
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
        }

        public void Focus(Vector3 target, float newDistance)
        {
            _targetPivot = target;
            _targetDistance = Mathf.Clamp(newDistance, minDistance, maxDistance);
        }

        /// <summary>Frames the whole build. In an unbounded world this is the way home (DESIGN.md §4.2).</summary>
        public void Frame(GridMap map)
        {
            if (map.CellCount == 0)
            {
                Focus(Vector3.zero, 3f);
                return;
            }

            BoundsInt b = map.OccupiedBounds;

            var centre = new Vector3(
                (b.xMin + b.size.x * 0.5f) * GridCoord.StudUnits,
                (b.yMin + b.size.y * 0.5f) * GridCoord.LayerUnits,
                (b.zMin + b.size.z * 0.5f) * GridCoord.StudUnits);

            float extent = Mathf.Max(b.size.x, b.size.z) * GridCoord.StudUnits;
            Focus(centre, Mathf.Max(1f, extent * 1.6f));
        }

        public void ReturnToOrigin() => Focus(Vector3.zero, 3f);
    }
}
