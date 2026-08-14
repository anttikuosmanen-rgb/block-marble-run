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

        /// <summary>Highest point reached, so the descent so far can be measured against what is left.</summary>
        public float PeakHeight { get; private set; }

        /// <summary>Fresh contacts per second. A ball rolling cleanly registers few; one clattering registers many.</summary>
        public float ContactsPerSecond { get; private set; }

        int _contacts;
        float _contactWindow;

        /// <summary>Every fresh contact since launch, for comparing whole runs against each other.</summary>
        public int TotalContacts { get; private set; }
        public Rigidbody Body => _body;
        public float Age { get; private set; }

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _sphere = GetComponent<SphereCollider>();

            _body.linearDamping = 0f;

            _body.interpolation = RigidbodyInterpolation.Interpolate;

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
            ApplyTunables();
        }

        /// <summary>
        /// Re-reads everything the physics panel can change, so a ball already rolling responds to a
        /// slider rather than only the next one spawned. Tuning against balls you have to re-release
        /// each time hides the very moment a change matters.
        /// </summary>
        public void ApplyTunables()
        {
            if (Definition == null)
                return;

            _body.angularDamping = Definition.angularDamping;
            _body.linearDamping = Definition.linearDamping;
            _body.collisionDetectionMode = Definition.collisionDetection;
            _body.solverIterations = Definition.solverIterations;
            _body.solverVelocityIterations = Definition.solverVelocityIterations;

            _sphere.contactOffset = Definition.contactOffset;
        }

        void FixedUpdate()
        {
            Age += Time.fixedDeltaTime;
            PeakHeight = Mathf.Max(PeakHeight, transform.position.y);

            _contactWindow += Time.fixedDeltaTime;
            if (_contactWindow < 0.25f)
                return;

            ContactsPerSecond = _contacts / _contactWindow;
            _contacts = 0;
            _contactWindow = 0f;
        }

        // Counted rather than inspected: what matters is how often the ball starts touching something
        // new, which is the difference between rolling along a surface and bouncing down it.
        void OnCollisionEnter(Collision collision)
        {
            _contacts++;
            TotalContacts++;
        }

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
            PeakHeight = position.y;
            TotalContacts = 0;
        }
    }
}
