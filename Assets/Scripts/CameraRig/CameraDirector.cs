using BlockMarbleRun.Play;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.CameraRig
{
    /// <summary>
    /// Chooses what the camera watches and how.
    ///
    /// Separate from the rig itself because choosing a ball needs to know about play mode and the
    /// balls in it, and the rig should not: it moves a transform towards a subject and has no opinion
    /// about where subjects come from.
    /// </summary>
    public sealed class CameraDirector : MonoBehaviour
    {
        public OrbitCamera rig;
        public PlayController play;
        public Core.GameMode mode;
        public Camera pickCamera;

        /// <summary>Screen pixels of movement that turn a right click into a right drag.</summary>
        public float clickSlack = 5f;

        Marble _subject;
        int _lastCount;

        Vector2 _pressedAt;
        bool _dragged;

        public OrbitCamera.View View => rig != null ? rig.view : OrbitCamera.View.Orbit;
        public Marble Subject => _subject;

        void Update()
        {
            if (rig == null || play == null)
                return;

            bool playing = mode == null || mode.Current == Core.Mode.Play;

            // Building has no subject to watch, and leaving one attached means the free camera drifts
            // off after a ball that is no longer moving.
            if (!playing)
            {
                rig.Subject = null;
                return;
            }

            FollowNewest();
            ReadKeys();
            ReadPick();

            if (_subject == null)
            {
                rig.Subject = null;
                return;
            }

            rig.Subject = _subject.transform;
            rig.SubjectVelocity = _subject.Body != null ? _subject.Body.linearVelocity : Vector3.zero;
        }

        /// <summary>
        /// Watches whatever was released last, and finds something else when it is retired.
        ///
        /// Tracked by the list growing rather than by an event on release: balls are also retired at
        /// goals and kill height, and a subject that has been destroyed has to be replaced whether or
        /// not anything new was released.
        /// </summary>
        void FollowNewest()
        {
            int count = play.Marbles.Count;

            if (count > _lastCount || _subject == null)
                _subject = count > 0 ? play.Marbles[count - 1] : null;

            _lastCount = count;
        }

        void ReadKeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.cKey.wasPressedThisFrame)
                rig.view = (OrbitCamera.View)(((int)rig.view + 1) % 4);

            if (keyboard.nKey.wasPressedThisFrame)
                Step(1);
        }

        void Step(int by)
        {
            int count = play.Marbles.Count;
            if (count == 0)
            {
                _subject = null;
                return;
            }

            // Searched rather than IndexOf: the list is exposed read-only, and a handful of balls is
            // not worth a LINQ dependency to walk.
            int index = -1;
            for (int i = 0; i < count; i++)
                if (play.Marbles[i] == _subject)
                {
                    index = i;
                    break;
                }

            _subject = play.Marbles[((index + by) % count + count) % count];
        }

        /// <summary>
        /// A right click picks a ball; a right drag still orbits.
        ///
        /// Told apart by how far the pointer moved between press and release rather than by how long
        /// the button was held: a slow deliberate orbit is a long press, and a flick is a short one,
        /// so time separates the wrong two things.
        /// </summary>
        void ReadPick()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || pickCamera == null)
                return;

            if (mouse.rightButton.wasPressedThisFrame)
            {
                _pressedAt = mouse.position.ReadValue();
                _dragged = false;
            }

            if (mouse.rightButton.isPressed &&
                (mouse.position.ReadValue() - _pressedAt).sqrMagnitude > clickSlack * clickSlack)
                _dragged = true;

            if (!mouse.rightButton.wasReleasedThisFrame || _dragged)
                return;

            Ray ray = pickCamera.ScreenPointToRay(mouse.position.ReadValue());

            if (Physics.Raycast(ray, out RaycastHit hit, 500f) &&
                hit.collider.TryGetComponent(out Marble picked))
                _subject = picked;
        }
    }
}
