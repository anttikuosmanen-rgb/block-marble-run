using UnityEngine;

namespace BlockMarbleRun.World
{
    /// <summary>
    /// Droplets thrown up where a ball breaks the surface.
    ///
    /// One shared particle system emitting bursts, not one system per splash. Creating a system per
    /// event costs an allocation and a component add at exactly the moment several balls are landing
    /// at once, which is when the frame can least afford it.
    /// </summary>
    public sealed class Splash : MonoBehaviour
    {
        public static Splash Active { get; private set; }

        public Material dropletMaterial;

        ParticleSystem _system;
        ParticleSystem.EmitParams _params;

        void OnEnable() => Active = this;

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        void Awake()
        {
            _system = gameObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = _system.main;
            main.startLifetime = 0.55f;
            main.startSize = 0.035f;
            main.gravityModifier = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 400;
            main.playOnAwake = false;

            // Each droplet is given its own velocity when it is emitted, so nothing here should add
            // one: a start speed would be applied along the shape's axis and fight the aim.
            main.startSpeed = 0f;

            ParticleSystem.EmissionModule emission = _system.emission;
            emission.enabled = false;     // bursts only, driven by Emit

            ParticleSystem.ShapeModule shape = _system.shape;
            shape.enabled = false;        // position and direction both come from the impact

            var renderer = GetComponent<ParticleSystemRenderer>();
            renderer.material = dropletMaterial;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _system.Stop();
        }

        /// <summary>
        /// Throws up a crown of droplets, carried along the direction the ball was travelling.
        ///
        /// Speeds are in this world's units, where gravity is 98.1 rather than 9.81 because a unit is
        /// ten centimetres. Sized for the real number, a droplet leaving at 2 units/s rises two
        /// centimetres and is back down in forty milliseconds - which is why the first version looked
        /// like it only ever went downwards. Rising a hand's width takes about fourteen.
        /// </summary>
        public void Emit(Vector3 position, Vector3 arrival)
        {
            if (_system == null)
                return;

            float force = Mathf.Clamp01(arrival.magnitude / 12f);
            int count = Mathf.RoundToInt(Mathf.Lerp(6f, 34f, force));

            // Only the horizontal part carries. The downward part is what made the splash, and
            // droplets that inherited it would be thrown into the water rather than out of it.
            Vector3 carry = new Vector3(arrival.x, 0f, arrival.z) * 0.4f;

            float launch = Mathf.Lerp(5f, 20f, force);

            for (int i = 0; i < count; i++)
            {
                // A ring around the entry point: a splash opens outward in every direction, and the
                // ball's own travel then leans the whole crown one way.
                float angle = Random.Range(0f, Mathf.PI * 2f);
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                Vector3 velocity = Vector3.up * (launch * Random.Range(0.55f, 1f)) +
                                   outward * (launch * Random.Range(0.15f, 0.5f)) +
                                   carry;

                _params = new ParticleSystem.EmitParams
                {
                    position = position + outward * Random.Range(0f, 0.06f),
                    velocity = velocity,
                    startLifetime = Mathf.Lerp(0.35f, 0.75f, force),
                    startSize = Random.Range(0.02f, 0.05f),
                };

                _system.Emit(_params, 1);
            }
        }
    }
}
