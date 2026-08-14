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
        public Track.ChannelWelder welder;
        public RunTester tester;

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

            // Slow motion belongs to watching a run. Carrying it into build mode would leave every
            // animation crawling with no obvious cause.
            if (building)
                Time.timeScale = 1f;

            // Welded runs have no per-part colliders, so building has to get them back.
            if (welder != null)
                welder.SetPlaying(!building);

            // A batch left running would keep releasing balls into a build being edited.
            if (building && tester != null)
                tester.Stop();

            // The ghost belongs to build mode and would otherwise hang in the air mid-run.
            if (!building)
                ghost.Hide();
        }
    }
}
