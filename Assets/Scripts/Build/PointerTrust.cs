using UnityEngine;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Decides whether the pointer reading of this frame can be believed.
    ///
    /// Where the pointer is reported to be is not always where it is. Around a focus change, or the
    /// frame an IMGUI button is released, the reading lands somewhere near the origin of the window
    /// for a few frames - not exactly at zero, which is why guarding that one point did not help -
    /// and the corner of the screen is perfectly good ground, so the click went through and planted a
    /// piece down there.
    ///
    /// The tell is not where the reading is but how far it moved: a hand cannot carry a mouse across
    /// the window between two frames. A jump therefore opens a short window of doubt, during which
    /// nothing is drawn and no click is acted on.
    ///
    /// This lives apart from <see cref="BuildController"/> because it is a small state machine that
    /// can strand the whole editor when it is wrong, and the way it was wrong is not visible by
    /// reading it: doubt used to be re-armed on every frame the pointer sat far from the last trusted
    /// position, and the last trusted position was only updated on frames that were not in doubt. Once
    /// the pointer genuinely moved a long way in one step - which is exactly what entering fullscreen
    /// does to every reading at once - the two conditions held each other up and the world stopped
    /// accepting clicks until the pointer wandered back to where it had been. Being a plain object
    /// with no Unity types in its API, it can be run against that sequence in the self test.
    /// </summary>
    public sealed class PointerTrust
    {
        /// <summary>How long doubt lasts. Long enough to cover the false readings, short enough not to be felt.</summary>
        const float DoubtSeconds = 0.25f;

        /// <summary>Share of the screen's height a pointer cannot cross between two frames.</summary>
        const float ImpossibleShare = 0.2f;

        Vector2 _lastTrusted;
        Vector2 _screenSize;
        float _doubtUntil;
        bool _started;

        /// <summary>Whether this frame's reading should be ignored.</summary>
        public bool IsSuspect(Vector2 pointer, Vector2 screenSize, float now)
        {
            // Nothing to compare the first reading against, so it is the truth by definition.
            if (!_started)
            {
                _started = true;
                _screenSize = screenSize;
                _lastTrusted = pointer;
                return false;
            }

            // A resolution change is not a mouse movement. Entering fullscreen moves every reading at
            // once, so measuring against a position from the old canvas makes an ordinary pointer look
            // like it crossed the window - and it stays looking that way, because it never goes back.
            //
            // Doubted rather than trusted, because the readings around a resize are as unreliable as
            // the ones around a focus change and produce the same flash in the corner.
            if (screenSize != _screenSize)
            {
                _screenSize = screenSize;
                _doubtUntil = now + DoubtSeconds;
                return true;
            }

            if (_doubtUntil > 0f)
            {
                // While in doubt, everything is doubted - not only readings that still look like a
                // jump. The false reading sits near the corner for several frames and only its first
                // looks like one, which is why the ghost went on flashing down there after the click
                // itself was refused.
                if (now < _doubtUntil)
                    return true;

                // The window is over, so wherever the pointer is now is where it is. Adopted without
                // a second look at how far it has travelled: judging it against the position from
                // before the jump re-arms the doubt on the spot, which is the whole bug in miniature -
                // caution that renews itself is not caution, it is a dead editor.
                _doubtUntil = 0f;
                _lastTrusted = pointer;
                return false;
            }

            if ((pointer - _lastTrusted).magnitude > screenSize.y * ImpossibleShare)
            {
                _doubtUntil = now + DoubtSeconds;
                return true;
            }

            _lastTrusted = pointer;
            return false;
        }
    }
}
