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
        [Range(0f, 1f)] public float bounciness = 0.15f;

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
