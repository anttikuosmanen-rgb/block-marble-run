using BlockMarbleRun.Build;
using BlockMarbleRun.Play;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.Core
{
    public enum Mode
    {
        Build,
        Play,
    }

    /// <summary>
    /// Switches between building and playing (DESIGN.md §7).
    ///
    /// The build stays exactly as it is when play starts. Nothing is baked, frozen or rebuilt: the
    /// parts are already static colliders, so the only thing that changes is which controller is
    /// listening and whether marbles exist.
    /// </summary>
    public sealed class GameMode : MonoBehaviour
    {
        public BuildController build;
        public PlayController play;
        public Build.GhostPreview ghost;

        public Mode Current { get; private set; } = Mode.Build;

        void Start() => Apply();

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.tabKey.wasPressedThisFrame)
            {
                Current = Current == Mode.Build ? Mode.Play : Mode.Build;
                Apply();
            }
        }

        void Apply()
        {
            bool building = Current == Mode.Build;

            build.enabled = building;
            play.SetActive(!building);

            // The ghost belongs to build mode and would otherwise hang in the air mid-run.
            if (!building)
                ghost.Hide();
        }
    }
}
