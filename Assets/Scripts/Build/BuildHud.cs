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

        GUIStyle _style;

        // Exponential average rather than an instantaneous reading: a per-frame number is unreadable,
        // and the spikes that matter show up in the worst-frame figure instead.
        float _smoothedMs;
        float _worstMs;

        void Awake()
        {
            controller = controller != null ? controller : GetComponent<BuildController>();
            stressTest = stressTest != null ? stressTest : GetComponent<StressTest>();
        }

        void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _smoothedMs = _smoothedMs <= 0f ? ms : Mathf.Lerp(_smoothedMs, ms, 0.05f);

            // Ignore the first frames, where load hitches would poison the worst-case reading.
            if (Time.frameCount > 60)
                _worstMs = Mathf.Max(_worstMs, ms);

            if (Keyboard.current != null && Keyboard.current.bKey.wasPressedThisFrame)
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

            PartDefinition selected = controller.Selected;

            GUILayout.BeginArea(new Rect(12, 12, 640, 260));
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
            GUILayout.Label($"Catalog: {controller.CatalogPartCount}", _style);

            GUILayout.Space(6);
            GUILayout.Label("Q / E part    R rotate    C colour", _style);
            GUILayout.Label("Left click place    Alt + click delete", _style);
            GUILayout.Label("Right drag orbit    Middle drag pan    Scroll zoom", _style);
            GUILayout.Label("F frame build    Home origin    Cmd+Z undo    Shift+Cmd+Z redo", _style);
            GUILayout.Label("Stress: T palette-mat   Y property-block(old)   U palette+sparse   G clear   B reset worst", _style);
            GUILayout.EndArea();
        }
    }
}
