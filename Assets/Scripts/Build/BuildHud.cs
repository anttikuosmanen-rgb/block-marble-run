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
        public BlockMarbleRun.CameraRig.CameraDirector director;

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

            if (Keyboard.current == null)
                return;

            if (Keyboard.current.kKey.wasPressedThisFrame)
                _worstMs = 0f;

            if (Keyboard.current.jKey.wasPressedThisFrame)
            {
                BlockMarbleRun.Grid.ScaffoldBuilder.Verbose = !BlockMarbleRun.Grid.ScaffoldBuilder.Verbose;
                BlockMarbleRun.Grid.ScaffoldBuilder.Report = "";
            }
        }

        /// <summary>
        /// The scaffolder's last decision, in its own panel on the right.
        ///
        /// Not appended to the help text: that column is already the full height of the window, so a
        /// multi-line report at the end of it is the first thing to fall off the bottom.
        /// </summary>
        void DrawScaffoldLog(float top)
        {
            if (!BlockMarbleRun.Grid.ScaffoldBuilder.Verbose)
                return;

            string report = BlockMarbleRun.Grid.ScaffoldBuilder.Report;

            GUILayout.BeginArea(new Rect(UiScale.Width - 392f, top, 380f, 320f));
            GUILayout.Label("[Scaffold] J to turn off", _style);
            GUILayout.Label(string.IsNullOrEmpty(report) ? "place a channel piece..." : report, _style);
            GUILayout.EndArea();
        }

        /// <summary>
        /// The water level, as a slider along the bottom.
        ///
        /// In layers rather than world units, because the thing being decided is how deep the build
        /// stands in it - "up to the third brick" is the question, and 0.576 is not an answer to it.
        /// Bricks and track ending up underwater is the point, not a case to guard against.
        /// </summary>
        void DrawWaterPanel()
        {
            BlockMarbleRun.World.Scenery scenery = BlockMarbleRun.World.Scenery.Active;
            if (scenery == null || !scenery.HasWater)
                return;

            const float width = 380f;
            GUILayout.BeginArea(new Rect((UiScale.Width - width) * 0.5f, UiScale.Height - 62f, width, 54f));

            GUILayout.Label($"Water level {scenery.WaterLayers:0.0} layers   ({scenery.waterLevel:0.000} units)",
                            _style);

            float layers = GUILayout.HorizontalSlider(scenery.WaterLayers, 0f, 20f);

            if (!Mathf.Approximately(layers, scenery.WaterLayers))
                scenery.WaterLayers = layers;

            GUILayout.EndArea();
        }

        void OnGUI()
        {
            if (controller == null)
                return;

            UiScale.Begin();

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white },
            };

            bool playing = mode != null && mode.Current == BlockMarbleRun.Core.Mode.Play;

            // Start below the palette bar, which draws itself first and knows its own height.
            float top = 12f + (palette != null ? palette.Height : 0f);

            // Down to the bottom of the window rather than a fixed 300. An area clips its contents,
            // and the help text had already grown past that height - anything added to the end was
            // drawn outside the box and simply never appeared.
            GUILayout.BeginArea(new Rect(12, top, 640, Mathf.Max(300f, UiScale.Height - top - 12f)));

            if (playing)
            {
                DrawPlay();
                GUILayout.EndArea();
                DrawWaterPanel();
                UiScale.End();
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
                    : $"Q / E part    R rotate ({controller.Rotation * 90}°)    C colour    X mark start/goal", _style);

            if (controller.GrowthLayers > 0)
                GUILayout.Label($"Click to lift the build {controller.GrowthLayers} layer(s) and place here", _style);

            if (controller.Precise)
                GUILayout.Label("PRECISE - sliding stud by stud, R still turns / cycles joins", _style);
            GUILayout.Label("Left click place    Shift precise    V grab (click picks, drag selects)    Del remove", _style);

            if (controller.Pasting)
            {
                GUILayout.Label(
                    $"PLACING {controller.PastingCount} piece(s) - " +
                    (controller.PasteFits ? "click to place" : "will not fit here"), _style);

                GUILayout.Label("Move to position    R turn    M mirror    + / - raise or lower    right click cancel",
                                _style);
            }
            else if (controller.CurrentTool == BuildController.Tool.Grab)
            {
                GUILayout.Label($"GRAB - {controller.Selection?.Count ?? 0} selected    " +
                                "A select all    shift click to add    R turn    M mirror", _style);
                GUILayout.Label("Cmd/Ctrl C copy    Cmd/Ctrl V paste under the cursor    Del remove", _style);
            }
            GUILayout.Label("S save (stamped with the time)    L saved creations    + / - raise or lower a structure" +
                            (string.IsNullOrEmpty(controller.LastTyped) ? "" : $"    (last key: {controller.LastTyped})"),
                            _style);
            GUILayout.Label("Right drag orbit    Middle drag pan    Scroll zoom", _style);
            GUILayout.Label("F frame build    Home origin    Cmd+Z undo    Shift+Cmd+Z redo", _style);
            GUILayout.Label("Tab play mode    B floor: grid / sand / water", _style);
            GUILayout.Label("Stress: T palette-mat   Y property-block(old)   U palette+sparse   G clear   K reset worst", _style);

            // Deliberately in the building HUD and not the physics panel. Scaffolding is decided when
            // a piece is placed, which only happens here - the panel could show the text but never
            // while the thing it describes was happening.
            GUILayout.Label("J scaffold log" + (BlockMarbleRun.Grid.ScaffoldBuilder.Verbose ? " (on)" : ""),
                            _style);

            GUILayout.EndArea();

            DrawScaffoldLog(top);
            DrawWaterPanel();

            // Inside the scaled matrix, not after it: the rectangle is converted into GUI space, so
            // drawing it once the matrix has been put back lands it at a fraction of its position.
            DrawSelectionBox();

            UiScale.End();
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
            if (director != null)
            {
                string watching = director.Subject != null
                    ? director.Subject.Definition?.displayName ?? "ball"
                    : "nothing";

                GUILayout.Label($"View: {director.View}   watching {watching}", _style);
            }

            GUILayout.Label("R reset    Tab back to building    P physics panel    B floor", _style);
            GUILayout.Label("C view: orbit / follow / chase / ride    N next ball    right click a ball to watch it",
                            _style);

            if (director != null && director.View == BlockMarbleRun.CameraRig.OrbitCamera.View.Ride)
                GUILayout.Label("Riding: scroll for distance behind    right drag for height and angle", _style);
        }

        void DrawSelectionBox()
        {
            Rect rect = controller.BoxSelectRect;
            if (rect.width <= 0f && rect.height <= 0f)
                return;

            // Divided into GUI space: the rectangle is measured in pointer pixels while the matrix
            // this is drawn through is scaled, so an unconverted rect lands somewhere else entirely.
            rect = new Rect(UiScale.ToGui(rect.position), UiScale.ToGui(rect.size));

            var flipped = new Rect(rect.x, UiScale.Height - rect.yMax, rect.width, rect.height);

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
