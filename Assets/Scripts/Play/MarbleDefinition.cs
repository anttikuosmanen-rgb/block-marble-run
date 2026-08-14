using UnityEngine;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// One kind of ball. Size and material are data, not constants, so a run can be tried with a
    /// heavy steel ball or a light plastic one and behave differently.
    ///
    /// Mass is derived from density rather than entered directly. Gravity accelerates every ball the
    /// same regardless of mass, so the number does nothing on its own - it matters where momentum
    /// meets something else, and entering a mass by hand invites values that no real material would
    /// have.
    /// </summary>
    [CreateAssetMenu(menuName = "Block Marble Run/Marble", fileName = "marble")]
    public sealed class MarbleDefinition : ScriptableObject
    {
        public string displayName = "Plastic";

        [Tooltip("Ball diameter in millimetres. The balls these channels are made for are 24.5 mm.")]
        public float diameterMm = 24.5f;

        [Tooltip("Grams per cubic centimetre. Polystyrene ~1.05, glass ~2.5, steel ~7.8.")]
        public float densityGramsPerCm3 = 1.05f;

        [Range(0f, 1f)] public float dynamicFriction = 0.08f;
        [Range(0f, 1f)] public float staticFriction = 0.10f;
        /// <summary>
        /// Low, on measurement rather than argument.
        ///
        /// The case for keeping it high was that a bounce preserves the energy an inelastic impact
        /// would absorb. What actually happens on an S-shaped slide is that one bad bounce launches
        /// the ball, and it comes back down having traded forward motion for a vertical hop the run
        /// never gets back. Losing a little at every crest beats losing most of it at one.
        /// </summary>
        [Range(0f, 1f)] public float bounciness = 0.05f;

        /// <summary>Spin damping, applied every step whether or not the ball is touching anything.</summary>
        [Range(0f, 0.5f)] public float angularDamping = 0.02f;

        /// <summary>Drag on travel, as opposed to spin. Zero unless a ball is meant to feel heavy in air.</summary>
        [Range(0f, 0.5f)] public float linearDamping;

        [Tooltip("How far ahead of the surface contacts are generated, in world units.")]
        [Range(0.0002f, 0.02f)] public float contactOffset = 0.002f;

        [Tooltip("Swept detection is costlier but will not tunnel; speculative can catch a ball on an edge it has not reached.")]
        public CollisionDetectionMode collisionDetection = CollisionDetectionMode.ContinuousDynamic;

        [Tooltip("Solver iterations for this ball. Overrides the project default.")]
        [Range(4, 40)] public int solverIterations = 10;

        [Range(1, 20)] public int solverVelocityIterations = 4;

        public Color colour = new Color(0.85f, 0.9f, 1f);
        [Range(0f, 1f)] public float smoothness = 0.9f;
        [Range(0f, 1f)] public float metallic = 0.1f;

        /// <summary>Radius in world units, at the project's 10x scale.</summary>
        public float RadiusUnits => diameterMm * 0.5f * 0.01f;

        /// <summary>
        /// Kilograms, from the sphere's volume and the material's density. A 24.5 mm plastic ball
        /// works out around 8 g; the same ball in steel is nearer 60 g.
        /// </summary>
        public float MassKg
        {
            get
            {
                float radiusCm = diameterMm * 0.05f;
                float volumeCm3 = 4f / 3f * Mathf.PI * radiusCm * radiusCm * radiusCm;
                return Mathf.Max(0.0001f, volumeCm3 * densityGramsPerCm3 * 0.001f);
            }
        }
    }
}
