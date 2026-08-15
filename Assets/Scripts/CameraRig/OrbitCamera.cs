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
        // A part is 3.2 cm across and the near plane sits at 3 mm, so anything closer than this lets
        // the camera inside the geometry - where you see the underside tubes through the shell and it
        // looks like the model is faulty.
        [SerializeField] float minDistance = 0.75f;
        [SerializeField] float maxDistance = 60f;

        [Header("Sensitivity")]
        [SerializeField] float orbitSpeed = 0.25f;
        [SerializeField] float panSpeed = 1.0f;
        [SerializeField] float zoomSpeed = 0.12f;
        [SerializeField] float smoothing = 12f;

        Vector3 _targetPivot;
        float _targetDistance;

        public Vector3 Pivot => pivot;

        /// <summary>How the rig is currently behaving.</summary>
        public enum View
        {
            /// <summary>Free orbit around a fixed point. What building uses.</summary>
            Orbit,

            /// <summary>Orbit, but the point being orbited rides along with the subject.</summary>
            Follow,

            /// <summary>Behind and above, swinging round to face the way the subject is going.</summary>
            Chase,

            /// <summary>Level with the subject, close, looking along its path.</summary>
            Ride,
        }

        public View view = View.Orbit;

        /// <summary>What the following views watch. Null falls back to orbiting.</summary>
        public Transform Subject { get; set; }

        /// <summary>The subject's velocity, which is what Chase and Ride aim along.</summary>
        public Vector3 SubjectVelocity { get; set; }

        /// <summary>
        /// Stops the wheel dollying, for whoever else wants it.
        ///
        /// Placing a copied group uses the wheel to raise and lower it; without this the same turn of
        /// the wheel would also pull the camera in, and the group would appear to move twice.
        /// </summary>
        public bool ZoomLocked { get; set; }

        /// <summary>How fast Chase swings round to a new heading, in degrees per second.</summary>
        [SerializeField] float headingSpeed = 220f;

        [Header("Ride")]
        [Tooltip("How far behind the ball the camera sits, in world units. A unit is 10 cm.")]
        [SerializeField] float rideBack = 0.28f;

        [Tooltip("How far above it.")]
        [SerializeField] float rideUp = 0.12f;

        // Deliberately narrow. Riding is a close view by definition - let it pull back far enough and
        // it becomes a worse version of Chase, with none of Chase's framing.
        [SerializeField] float minRideBack = 0.14f;
        [SerializeField] float maxRideBack = 1.6f;
        [SerializeField] float minRideUp = -0.06f;
        [SerializeField] float maxRideUp = 0.9f;

        [Tooltip("How far the view can be swung round the ball while riding, in degrees.")]
        [SerializeField] float maxRideSwing = 75f;

        float _heading;
        float _rideSwing;

        void Awake()
        {
            _targetPivot = pivot;
            _targetDistance = distance;
            ApplyImmediate();
        }

        void LateUpdate()
        {
            Mouse mouse = Mouse.current;
            bool riding = view == View.Ride && Subject != null;

            if (mouse != null && riding)
            {
                ReadRideInput(mouse);
            }
            else if (mouse != null)
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

                float scroll = ZoomLocked ? 0f : mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    // Proportional zoom: a fixed step crawls when far out and overshoots when close in.
                    _targetDistance = Mathf.Clamp(
                        _targetDistance * (1f - Mathf.Sign(scroll) * zoomSpeed),
                        minDistance, maxDistance);
                }
            }

            bool following = view != View.Orbit && Subject != null;

            if (following)
            {
                // The subject is the point of interest, so panning it away makes no sense while a
                // view is locked to it - the pan above still runs, it is simply overruled here.
                _targetPivot = Subject.position;
            }

            float t = 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            pivot = Vector3.Lerp(pivot, _targetPivot, t);
            distance = Mathf.Lerp(distance, _targetDistance, t);

            if (following && view != View.Follow)
            {
                AimAlongTravel();

                if (view == View.Ride)
                {
                    Ride();
                    return;
                }
            }

            ApplyImmediate();
        }

        /// <summary>
        /// The rider's own controls: wheel for how far back, drag for how high and how far round.
        ///
        /// Taken instead of the orbit controls rather than alongside them. The wheel would otherwise
        /// be driving the orbit distance that Ride never reads, so it would feel like a dead control
        /// that quietly changed what you saw on leaving the view.
        /// </summary>
        void ReadRideInput(Mouse mouse)
        {
            float scroll = mouse.scroll.ReadValue().y;

            if (Mathf.Abs(scroll) > 0.01f)
            {
                // Proportional, like the orbit zoom: a fixed step is coarse at 14 cm and glacial at 1.
                rideBack = Mathf.Clamp(rideBack * (1f - Mathf.Sign(scroll) * zoomSpeed),
                                       minRideBack, maxRideBack);
            }

            if (!mouse.rightButton.isPressed)
                return;

            Vector2 delta = mouse.delta.ReadValue();

            // Scaled by how far back the camera is, so raising it feels the same close in as far out.
            rideUp = Mathf.Clamp(rideUp + delta.y * orbitSpeed * rideBack * 0.02f, minRideUp, maxRideUp);

            _rideSwing = Mathf.Clamp(_rideSwing + delta.x * orbitSpeed, -maxRideSwing, maxRideSwing);
        }

        /// <summary>
        /// Turns the rig to look the way the subject is travelling.
        ///
        /// Only the horizontal part, and only while there is enough of it to mean something: a ball
        /// dropping straight down or coming to rest has no heading, and reading one out of the noise
        /// spins the camera on the spot at exactly the moment the player is trying to watch something.
        /// </summary>
        void AimAlongTravel()
        {
            var flat = new Vector3(SubjectVelocity.x, 0f, SubjectVelocity.z);

            if (flat.sqrMagnitude > 0.25f)
                _heading = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;

            // Turned towards rather than snapped to: a bounce can reverse the heading between one
            // frame and the next, and a camera that follows that exactly is unwatchable.
            yaw = Mathf.MoveTowardsAngle(yaw, _heading + 180f, headingSpeed * Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Just behind and a little above the subject, looking along its path.
        ///
        /// Not at the subject itself: the camera would sit inside the ball's own mesh, and the near
        /// plane at 3 mm is not close enough to save it.
        /// </summary>
        void Ride()
        {
            Quaternion heading = Quaternion.Euler(0f, _heading + _rideSwing, 0f);

            Vector3 back = heading * Vector3.back * rideBack;
            Vector3 up = Vector3.up * rideUp;

            Vector3 position = pivot + back + up;

            // Looking ahead of the ball rather than at it, so the track it is about to take is what
            // fills the frame - the point of riding is seeing what is coming.
            Vector3 look = pivot + Quaternion.Euler(0f, _heading, 0f) * Vector3.forward * 1.5f;

            transform.SetPositionAndRotation(position, Quaternion.LookRotation(look - position, Vector3.up));
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
