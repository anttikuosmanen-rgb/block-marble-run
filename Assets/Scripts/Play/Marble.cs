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

        /// <summary>Raised on every fresh contact, with the impulse behind it.</summary>
        public event System.Action<float> Impact;

        /// <summary>Raised when the ball breaks the water surface, with the speed it arrived at.</summary>
        public event System.Action<float> EnteredWater;

        bool _submerged;

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

            Swim();

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

            Impact?.Invoke(collision.impulse.magnitude);
        }

        /// <summary>
        /// Buoyancy and drag over the submerged part of the ball, and the splash on the way in.
        ///
        /// Buoyancy is Archimedes rather than a tuning knob: the upward force is the weight of the
        /// water actually pushed aside, so whether a ball floats falls out of its density against the
        /// water's instead of being decided anywhere in code. A 24.5 mm plastic ball at 1.05 g/cm3
        /// sinks slowly in fresh water and floats in brine, which is what it does in a bucket.
        ///
        /// The displaced volume is a spherical cap, not the whole ball scaled by a fraction: a ball
        /// dipping its lower quarter displaces far less than a quarter of its volume, and treating it
        /// linearly makes shallow water feel like a trampoline.
        /// </summary>
        void Swim()
        {
            World.Scenery scenery = World.Scenery.Active;

            if (scenery == null || !scenery.HasWater)
            {
                _submerged = false;
                return;
            }

            float radius = Definition != null ? Definition.RadiusUnits : 0.1225f;
            float surface = scenery.SurfaceAt(transform.position);
            float depth = surface - (transform.position.y - radius);

            if (depth <= 0f)
            {
                _submerged = false;
                return;
            }

            if (!_submerged)
            {
                _submerged = true;
                EnteredWater?.Invoke(_body.linearVelocity.magnitude);

                // The droplets come from the surface, not from wherever the ball had reached by the
                // time the step noticed - at speed those are a visible distance apart.
                World.Splash.Active?.Emit(
                    new Vector3(transform.position.x, surface, transform.position.z),
                    _body.linearVelocity);
            }

            float submergedDepth = Mathf.Min(depth, radius * 2f);

            // Volume of a spherical cap of height h on a sphere of radius r.
            float h = submergedDepth;
            float capUnits3 = Mathf.PI * h * h * (3f * radius - h) / 3f;

            // One world unit is 10 cm, so a cubic unit is 1000 cm3. Density is given in g/cm3, and a
            // gram is a thousandth of the kilogram the rigidbody's mass is in.
            float displacedKg = capUnits3 * 1000f * scenery.waterDensity * 0.001f;

            // Force, not acceleration: PhysX then weighs it against the ball's own mass, which is the
            // whole point - a light ball rises and a dense one sinks without either being a case.
            _body.AddForce(-Physics.gravity * displacedKg, ForceMode.Force);

            float share = Mathf.Clamp01(submergedDepth / (radius * 2f));
            float speed = _body.linearVelocity.magnitude;

            if (speed > 0.0001f)
            {
                // Quadratic drag, which is what gives a sinking ball a terminal velocity instead of
                // letting it accelerate all the way down.
                float areaM2 = Mathf.PI * radius * radius * 0.01f * share;
                float speedMs = speed * 0.1f;

                float newtons = 0.5f * (scenery.waterDensity * 1000f) * scenery.dragCoefficient *
                                areaM2 * speedMs * speedMs;

                // Newtons are kg*m/s2 and this world's forces are kg*units/s2, ten units to the metre.
                _body.AddForce(-_body.linearVelocity / speed * (newtons * 10f), ForceMode.Force);
            }

            _body.AddTorque(-_body.angularVelocity * (scenery.spinDrag * share), ForceMode.Acceleration);
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
            _submerged = false;
        }
    }
}
