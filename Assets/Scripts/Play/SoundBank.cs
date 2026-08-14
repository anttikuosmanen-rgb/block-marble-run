using UnityEngine;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// The game's sounds, synthesised at load rather than imported.
    ///
    /// Three short clips are less code than an asset pipeline for three short clips, and they cost
    /// nothing in the build - which matters on WebGL, where every imported clip is bytes the player
    /// waits for before the first frame. Synthesising also means the roll can be a genuinely seamless
    /// loop instead of a recording that ticks once a second.
    /// </summary>
    public static class SoundBank
    {
        const int Rate = 44100;

        static AudioClip _roll;
        static AudioClip _clack;
        static AudioClip _splash;

        public static AudioClip Roll => _roll ??= BuildRoll();
        public static AudioClip Clack => _clack ??= BuildClack();
        public static AudioClip Splash => _splash ??= BuildSplash();

        /// <summary>
        /// A loop of filtered noise: the sound of a hard ball on a plastic channel is broadband, not
        /// tonal, and pitching it by speed is what makes it read as rolling rather than as hiss.
        ///
        /// The last few milliseconds cross-fade into the first, because a loop that does not meet
        /// itself clicks once per revolution and nothing about the mix will hide it.
        /// </summary>
        static AudioClip BuildRoll()
        {
            const int length = Rate; // one second
            var data = new float[length];

            var random = new System.Random(1);
            float low = 0f, band = 0f;

            for (int i = 0; i < length; i++)
            {
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);

                // Two one-pole filters in series: a rolling ball has energy in the low mids and very
                // little above it. Unfiltered noise sounds like radio static at any pitch.
                low += (noise - low) * 0.12f;
                band += (low - band) * 0.35f;

                data[i] = band * 3.2f;
            }

            const int blend = 1200;
            for (int i = 0; i < blend; i++)
            {
                float t = i / (float)blend;
                data[i] = Mathf.Lerp(data[length - blend + i], data[i], t);
            }

            return Make("Roll", data);
        }

        /// <summary>A short knock: a click sharpened by a fast decay, with a little body under it.</summary>
        static AudioClip BuildClack()
        {
            int length = Rate / 16;
            var data = new float[length];

            var random = new System.Random(2);
            float low = 0f;

            for (int i = 0; i < length; i++)
            {
                float t = i / (float)length;
                float envelope = Mathf.Exp(-t * 28f);

                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                low += (noise - low) * 0.5f;

                // A touch of pitch so it reads as plastic rather than as a hi-hat.
                float body = Mathf.Sin(i / (float)Rate * 2f * Mathf.PI * 320f) * 0.4f;

                data[i] = (low + body) * envelope * 0.9f;
            }

            return Make("Clack", data);
        }

        /// <summary>
        /// Water: a bright burst that darkens as it decays, which is what a splash does - the fine
        /// spray dies away first and leaves the low gulp of the cavity closing.
        /// </summary>
        static AudioClip BuildSplash()
        {
            int length = Rate / 2;
            var data = new float[length];

            var random = new System.Random(3);
            float low = 0f;

            for (int i = 0; i < length; i++)
            {
                float t = i / (float)length;

                // Rises over a few milliseconds rather than starting at full - an instant onset
                // sounds like a click in front of the splash.
                float attack = Mathf.Clamp01(t * 60f);
                float envelope = attack * Mathf.Exp(-t * 7f);

                float noise = (float)(random.NextDouble() * 2.0 - 1.0);

                // The filter closes as the sound decays, carrying the spray away and leaving the gulp.
                low += (noise - low) * Mathf.Lerp(0.6f, 0.05f, t);

                float gulp = Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(220f, 90f, t) * 0.5f) * 0.35f * (1f - attack + 0.2f);

                data[i] = (low * 1.6f + gulp) * envelope;
            }

            return Make("Splash", data);
        }

        static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
