using BlockMarbleRun.Parts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Minimal on-screen help and status for M1. IMGUI deliberately: the real palette is a UI Toolkit
    /// job in M5, and building it twice would be waste.
    /// </summary>
    public sealed class BuildHud : MonoBehaviour
    {
        public BuildController controller;
        public StressTest stressTest;
        public BlockMarbleRun.Track.OpenPortMarkers markers;
        public BlockMarbleRun.Track.JointBridges joints;
        public BlockMarbleRun.Play.PlayController play;
        public BlockMarbleRun.Core.GameMode mode;
        public PartPalette palette;

        GUIStyle _style;

        // Exponential average rather than an instantaneous reading: a per-frame number is unreadable,
        // and the spikes that matter show up in the worst-frame figure instead.
        float _smoothedMs;
        float _worstMs;
        BlockMarbleRun.Core.Mode _lastMode;

        void Awake()
        {
            controller = controller != null ? controller : GetComponent<BuildController>();
            stressTest = stressTest != null ? stressTest : GetComponent<StressTest>();
        }

        void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _smoothedMs = _smoothedMs <= 0f ? ms : Mathf.Lerp(_smoothedMs, ms, 0.05f);

            // Ignore the first second, and anything absurd: a tab-switch or an asset load stalls the
            // main thread for seconds, and one of those leaves the worst-frame figure permanently
            // reading a number that has nothing to do with the build.
            if (Time.unscaledTime > 1f && ms < 500f)
                _worstMs = Mathf.Max(_worstMs, ms);

            if (mode != null && mode.Current != _lastMode)
            {
                _lastMode = mode.Current;
                _worstMs = 0f;
            }

            if (Keyboard.current != null && Keyboard.current.kKey.wasPressedThisFrame)
                _worstMs = 0f;
        }

        void OnGUI()
        {
            if (controller == null)
                return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white },
            };

            bool playing = mode != null && mode.Current == BlockMarbleRun.Core.Mode.Play;

            // Start below the palette bar, which draws itself first and knows its own height.
            float top = 12f + (palette != null ? palette.Height : 0f);
            GUILayout.BeginArea(new Rect(12, top, 640, 300));

            if (playing)
            {
                DrawPlay();
                GUILayout.EndArea();
                return;
            }

            PartDefinition selected = controller.Selected;
            GUILayout.Label($"Part: {(selected != null ? selected.displayName : "none")}", _style);
            GUILayout.Label($"Placed: {controller.Map.Parts.Count}   Cells: {controller.Map.CellCount}", _style);
            GUILayout.Label(
                $"Frame: {_smoothedMs:0.0} ms ({(_smoothedMs > 0f ? 1000f / _smoothedMs : 0f):0} fps)   " +
                $"Worst: {_worstMs:0.0} ms", _style);

            if (stressTest != null && stressTest.Spawned > 0)
            {
                GUILayout.Label(
                    $"Stress [{stressTest.Mode}]: {stressTest.Spawned} parts, " +
                    $"{stressTest.Triangles / 1e6:0.00} M tris, spawned in {stressTest.SpawnMs:0} ms", _style);
            }

            // Catalog count stays visible: a null catalog leaves the game running but inert, with
            // camera control still working, which reads as "nothing is wrong" until you try to build.
            GUILayout.Label(
                $"Catalog: {controller.CatalogPartCount}   Selected: {controller.Selection?.Count ?? 0}   " +
                $"Slot: {controller.SlotName}", _style);

            if (markers != null)
                GUILayout.Label($"Open channel ends: {markers.OpenCount}" + (joints != null ? $"   bridged joints: {joints.Count}" : ""), _style);

            if (!string.IsNullOrEmpty(controller.Status))
                GUILayout.Label(controller.Status, _style);

            GUILayout.Space(6);
            GUILayout.Label(
                controller.VariantCount > 1
                    ? $"R cycles joins ({controller.VariantIndex + 1}/{controller.VariantCount})    C colour    X mark start/goal"
                    : "Q / E part    R rotate    C colour    X mark start/goal", _style);

            if (controller.Precise)
                GUILayout.Label("PRECISE - sliding stud by stud, R still turns / cycles joins", _style);
            GUILayout.Label("Left click place    Shift precise    V grab (click picks, drag selects)    Del remove", _style);
            GUILayout.Label("S save    L load", _style);
            GUILayout.Label("Right drag orbit    Middle drag pan    Scroll zoom", _style);
            GUILayout.Label("F frame build    Home origin    Cmd+Z undo    Shift+Cmd+Z redo", _style);
            GUILayout.Label("Tab play mode", _style);
            GUILayout.Label("Stress: T palette-mat   Y property-block(old)   U palette+sparse   G clear   K reset worst", _style);
            GUILayout.EndArea();

            DrawSelectionBox();
        }

        /// <summary>
        /// Draws the drag rectangle. GUI space has its origin at the top left while the mouse reports
        /// from the bottom left, so the rectangle has to be flipped or it appears mirrored vertically.
        /// </summary>
        void DrawPlay()
        {
            GUILayout.Label("PLAY", _style);
            GUILayout.Label(
                $"Frame: {_smoothedMs:0.0} ms ({(_smoothedMs > 0f ? 1000f / _smoothedMs : 0f):0} fps)   " +
                $"Worst: {_worstMs:0.0} ms", _style);

            GUILayout.Label(
                $"Marbles: {play.Alive} live, {play.Released} released   " +
                $"Finished: {play.Finished}   Lost: {play.Lost}", _style);

            if (!float.IsPositiveInfinity(play.BestSeconds))
                GUILayout.Label($"Best run: {play.BestSeconds:0.00} s", _style);

            GUILayout.Label(
                $"Speed: {BlockMarbleRun.Play.PlayController.ToMetresPerSecond(play.FastestSpeed):0.00} m/s   " +
                $"peak {BlockMarbleRun.Play.PlayController.ToMetresPerSecond(play.PeakSpeed):0.00} m/s   " +
                $"could climb {play.ClimbableLayers:0.0} layers", _style);

            GUILayout.Label(
                $"Energy kept: {play.EfficiencyPercent:0} % of the drop   " +
                $"contacts: {play.ContactRate:0}/s", _style);

            GUILayout.Space(6);
            if (play.CurrentType != null)
            {
                BlockMarbleRun.Play.MarbleDefinition ball = play.CurrentType;
                GUILayout.Label(
                    $"Ball: {ball.displayName}  {ball.diameterMm:0.#} mm  {ball.MassKg * 1000f:0.#} g", _style);
            }

            if (!string.IsNullOrEmpty(play.Status))
                GUILayout.Label(play.Status, _style);

            GUILayout.Space(6);
            GUILayout.Label("Space release from starts    Left click drop a ball    M change ball", _style);
            GUILayout.Label("R reset    Tab back to building    P physics panel", _style);
        }

        void DrawSelectionBox()
        {
            Rect rect = controller.BoxSelectRect;
            if (rect.width <= 0f && rect.height <= 0f)
                return;

            var flipped = new Rect(rect.x, Screen.height - rect.yMax, rect.width, rect.height);

            Color previous = GUI.color;
            GUI.color = new Color(0.35f, 0.75f, 1f, 0.18f);
            GUI.DrawTexture(flipped, Texture2D.whiteTexture);
            GUI.color = new Color(0.45f, 0.85f, 1f, 0.9f);

            const float edge = 1f;
            GUI.DrawTexture(new Rect(flipped.x, flipped.y, flipped.width, edge), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(flipped.x, flipped.yMax - edge, flipped.width, edge), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(flipped.x, flipped.y, edge, flipped.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(flipped.xMax - edge, flipped.y, edge, flipped.height), Texture2D.whiteTexture);

            GUI.color = previous;
        }
    }
}
