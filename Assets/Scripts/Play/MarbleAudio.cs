using UnityEngine;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// The sound one ball makes: a rolling loop that follows its speed, and a knock on every impact.
    ///
    /// Driven by speed rather than by contact count, with contacts only opening the gate. A ball in
    /// free fall is silent even at speed, and a ball resting against a wall is silent even though it
    /// is touching something - it is the combination that means rolling.
    /// </summary>
    [RequireComponent(typeof(Marble))]
    public sealed class MarbleAudio : MonoBehaviour
    {
        public float rollVolume = 0.35f;
        public float clackVolume = 0.5f;

        [Tooltip("Speed, in world units per second, at which the roll reaches full volume.")]
        public float loudSpeed = 6f;

        Marble _marble;
        AudioSource _roll;
        AudioSource _oneShots;

        float _touching;

        void Awake()
        {
            _marble = GetComponent<Marble>();

            _roll = gameObject.AddComponent<AudioSource>();
            _roll.clip = SoundBank.Roll;
            _roll.loop = true;
            _roll.volume = 0f;
            Place(_roll);
            _roll.Play();

            _oneShots = gameObject.AddComponent<AudioSource>();
            _oneShots.playOnAwake = false;
            Place(_oneShots);

            _marble.Impact += OnImpact;
            _marble.EnteredWater += OnSplash;
        }

        /// <summary>
        /// Puts a source in the world so it fades with distance.
        ///
        /// The distances are in world units, where one unit is ten centimetres - left at Unity's
        /// defaults of 1 and 500 a marble would be at full volume until the camera was fifty metres
        /// away, which is to say always. Full volume out to about half a metre, inaudible by twelve.
        /// </summary>
        static void Place(AudioSource source)
        {
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 5f;
            source.maxDistance = 120f;

            // Panning stays gentle. The camera orbits, so a ball hard left one moment is hard right
            // the next, and full stereo separation turns that into a distraction.
            source.spread = 90f;
        }

        void OnDestroy()
        {
            if (_marble == null)
                return;

            _marble.Impact -= OnImpact;
            _marble.EnteredWater -= OnSplash;
        }

        void OnImpact(float impulse)
        {
            _touching = 0.12f;

            // Below this the ball is settling against something it is already resting on, and every
            // frame of that would fire a knock.
            if (impulse < 0.02f)
                return;

            float strength = Mathf.Clamp01(impulse / 0.35f);

            _oneShots.pitch = Random.Range(0.85f, 1.25f);
            _oneShots.PlayOneShot(SoundBank.Clack, strength * clackVolume);
        }

        void OnSplash(float speed)
        {
            _oneShots.pitch = Random.Range(0.9f, 1.1f);
            _oneShots.PlayOneShot(SoundBank.Splash, Mathf.Clamp01(speed / 6f) * 0.9f);
        }

        void Update()
        {
            _touching -= Time.deltaTime;

            float speed = _marble.Body != null ? _marble.Body.linearVelocity.magnitude : 0f;
            float wanted = _touching > 0f ? Mathf.Clamp01(speed / loudSpeed) * rollVolume : 0f;

            // Eased, so a ball crossing a joint does not chop the sound in and out.
            _roll.volume = Mathf.MoveTowards(_roll.volume, wanted, Time.deltaTime * 4f);
            _roll.pitch = Mathf.Lerp(0.55f, 1.7f, Mathf.Clamp01(speed / loudSpeed));
        }
    }
}
