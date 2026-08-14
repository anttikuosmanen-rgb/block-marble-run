using UnityEngine;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// One ball. Configured per DESIGN.md §2, where the settings matter more than usual because the
    /// object is small and fast relative to everything PhysX assumes.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public sealed class Marble : MonoBehaviour
    {
        Rigidbody _body;
        SphereCollider _sphere;

        public MarbleDefinition Definition { get; private set; }
        public Rigidbody Body => _body;
        public float Age { get; private set; }

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _sphere = GetComponent<SphereCollider>();

            _body.linearDamping = 0f;
            _body.angularDamping = 0.02f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;

            // A ball moving fast enough to leave a track will pass straight through a wall between
            // two fixed steps without this.
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // The default cap is 7 rad/s. A 24.5 mm ball rolling at even walking pace exceeds that,
            // and the clamp shows up as a ball sliding down the channel instead of rolling - the
            // single most misleading physics default at this scale.
            _body.maxAngularVelocity = 200f;
        }

        /// <summary>
        /// Applies a ball type. The collider carries the true radius while the transform is scaled to
        /// match, so a 24.5 mm ball and a 16 mm one differ physically rather than only on screen.
        /// </summary>
        public void Configure(MarbleDefinition definition, PhysicsMaterial physics)
        {
            Definition = definition;

            _sphere.radius = 0.5f;                       // unit sphere, scaled by the transform
            _sphere.sharedMaterial = physics;
            transform.localScale = Vector3.one * (definition.RadiusUnits * 2f);

            _body.mass = definition.MassKg;
        }

        void FixedUpdate() => Age += Time.fixedDeltaTime;

        public void Launch(Vector3 position)
        {
            // Both, deliberately. The transform is what the renderer reads and the body pose is what
            // physics and interpolation read; setting only one leaves the ball drawn in a different
            // place from where it is simulated until the next step reconciles them.
            transform.position = position;
            _body.position = position;

            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            Age = 0f;
        }
    }
}
