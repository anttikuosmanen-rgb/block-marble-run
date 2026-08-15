using BlockMarbleRun.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// Live physics tuning while a run is in progress.
    ///
    /// Built because tuning this by editing constants and rebuilding cost around ninety seconds a
    /// try, and several rounds of that produced four confident explanations that were all wrong. A
    /// slider that answers in one frame, next to a readout of how much energy the run is keeping,
    /// turns the question from an argument into a measurement.
    ///
    /// Everything here writes to the ball definition or to the engine's own settings, and both are
    /// re-applied to balls already rolling - the moment a value starts to matter is usually mid-run.
    /// </summary>
    public sealed class PhysicsPanel : MonoBehaviour
    {
        public PlayController play;
        public GameMode mode;
        public BlockMarbleRun.Parts.PartFactory factory;
        public BlockMarbleRun.Track.JointBridges joints;
        public BlockMarbleRun.Track.ChannelWelder welder;
        public RunTester tester;

        [Tooltip("Gravity at 1.0 is the project's -98.1, which is real gravity at the 10x world scale.")]
        public float gravityScale = 1f;

        bool _open = true;
        Vector2 _scroll;
        GUIStyle _label;

        /// <summary>Project defaults, so a session of experimenting can always be undone.</summary>
        const float DefaultGravity = -98.1f;
        const float DefaultBounceThreshold = 1.0f;
        const int DefaultHz = 120;

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.pKey.wasPressedThisFrame)
                _open = !_open;
        }

        void OnGUI()
        {
            if (play == null || mode == null || mode.Current != Mode.Play)
                return;

            // Wrapped rather than scaled inline: the body has several early returns, and each one
            // would otherwise have to remember to put the GUI matrix back.
            BlockMarbleRun.Build.UiScale.Begin();
            Draw();
            BlockMarbleRun.Build.UiScale.End();
        }

        void Draw()
        {
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = Color.white } };

            float width = 320f;
            var area = new Rect(BlockMarbleRun.Build.UiScale.Width - width - 12f, 12f, width, BlockMarbleRun.Build.UiScale.Height - 24f);

            if (!_open)
            {
                if (GUI.Button(new Rect(area.x + width - 90f, area.y, 90f, 24f), "Physics (P)"))
                    _open = true;

                return;
            }

            GUILayout.BeginArea(area, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("PHYSICS  —  P to hide", _label);

            MarbleDefinition ball = play.CurrentType;
            if (ball == null)
            {
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }

            GUILayout.Space(4);
            GUILayout.Label($"Ball: {ball.displayName}   (M changes)", _label);

            bool changed = false;

            changed |= Slider("Diameter (mm)", ref ball.diameterMm, 8f, 40f, "0.0");
            changed |= Slider("Density (g/cm³)", ref ball.densityGramsPerCm3, 0.2f, 9f, "0.00");
            GUILayout.Label($"   mass {ball.MassKg * 1000f:0.#} g", _label);

            GUILayout.Space(6);
            changed |= Slider("Dynamic friction", ref ball.dynamicFriction, 0f, 1f, "0.000");
            changed |= Slider("Static friction", ref ball.staticFriction, 0f, 1f, "0.000");
            changed |= Slider("Bounciness", ref ball.bounciness, 0f, 1f, "0.00");
            changed |= Slider("Angular damping", ref ball.angularDamping, 0f, 0.5f, "0.000");
            changed |= Slider("Linear damping", ref ball.linearDamping, 0f, 0.5f, "0.000");
            changed |= Slider("Contact offset", ref ball.contactOffset, 0.0002f, 0.02f, "0.0000");

            GUILayout.Space(6);
            changed |= IntSlider("Solver iterations", ref ball.solverIterations, 4, 40);
            changed |= IntSlider("Solver velocity iters", ref ball.solverVelocityIterations, 1, 20);

            GUILayout.Space(4);
            GUILayout.Label($"Collision: {ball.collisionDetection}", _label);
            if (GUILayout.Button("Cycle collision mode"))
            {
                ball.collisionDetection = (CollisionDetectionMode)(((int)ball.collisionDetection + 1) % 4);
                changed = true;
            }

            if (changed)
                play.RefreshPhysics();

            GUILayout.Space(10);
            GUILayout.Label("SURFACE  (bricks and channels)", _label);

            PhysicsMaterial surface = factory != null ? factory.surfacePhysics : null;
            if (surface != null)
            {
                // Edited straight on the shared material, which every part collider already points at,
                // so a change lands on the whole build at once.
                float sd = surface.dynamicFriction, ss = surface.staticFriction, sb = surface.bounciness;

                if (Slider("Surface dyn friction", ref sd, 0f, 1f, "0.000")) surface.dynamicFriction = sd;
                if (Slider("Surface static friction", ref ss, 0f, 1f, "0.000")) surface.staticFriction = ss;
                if (Slider("Surface bounciness", ref sb, 0f, 1f, "0.00")) surface.bounciness = sb;

                GUILayout.Label($"   combined with ball: friction {(sd + ball.dynamicFriction) * 0.5f:0.000}, " +
                                $"bounce {(sb + ball.bounciness) * 0.5f:0.00}", _label);
            }
            else
            {
                GUILayout.Label("   none assigned - parts use Unity's default 0.6 / 0.6 / 0", _label);
            }


            if (welder != null)
            {
                GUILayout.Space(6);
                GUILayout.Label("SEAMS", _label);

                bool welding = GUILayout.Toggle(welder.weldInPlay,
                    $"  Weld joined channels ({welder.Groups} run(s), {welder.WeldedParts} parts)");

                if (welding != welder.weldInPlay)
                {
                    welder.weldInPlay = welding;
                    welder.Rebuild();
                }

                GUILayout.Label("   one collider per run, so there is no joint to catch on", _label);
                GUILayout.Label("   play mode only - building needs the parts back", _label);
            }

            if (joints != null)
            {
                GUILayout.Space(6);

                bool bridging = GUILayout.Toggle(joints.enabled_, $"  Bridge joints ({joints.Count})");
                if (bridging != joints.enabled_)
                    joints.enabled_ = bridging;

                if (Slider("Bridge reach", ref joints.reach, 0.001f, 0.02f, "0.0000"))
                    joints.Rebuild();

                if (Slider("Bridge lift", ref joints.lift, -0.002f, 0.002f, "0.0000"))
                    joints.Rebuild();
            }

            GUILayout.Space(10);
            GUILayout.Label("WORLD", _label);

            // Engine-wide, so these are written straight through rather than onto the ball.
            if (Slider("Gravity scale", ref gravityScale, 0.1f, 2f, "0.00"))
                Physics.gravity = new Vector3(0f, DefaultGravity * gravityScale, 0f);

            float threshold = Physics.bounceThreshold;
            if (Slider("Bounce threshold", ref threshold, 0.05f, 5f, "0.00"))
                Physics.bounceThreshold = threshold;

            GUILayout.Label("   below this speed, impacts stop bouncing at all", _label);

            float hz = Mathf.Round(1f / Time.fixedDeltaTime);
            if (Slider("Physics rate (Hz)", ref hz, 50f, 240f, "0"))
                Time.fixedDeltaTime = 1f / Mathf.Max(30f, Mathf.Round(hz));

            // Scales simulated time only. The fixed timestep is untouched, so each physics step is
            // exactly the step it would have been - the run is the same run, played slower. Scaling
            // the timestep alongside is the version that would change the physics.
            float slow = Time.timeScale;
            if (Slider("Slow motion", ref slow, 0.05f, 1f, "0.00"))
                Time.timeScale = slow;

            float release = play.releaseSpeed;
            if (Slider("Release nudge", ref release, 0f, 4f, "0.00"))
                play.releaseSpeed = release;

            if (tester != null)
            {
                GUILayout.Space(10);
                GUILayout.Label("BATCH TEST", _label);

                float count = tester.runs;
                if (Slider("Runs", ref count, 1f, 30f, "0"))
                    tester.runs = Mathf.RoundToInt(count);

                Slider("Max seconds per run", ref tester.timeoutSeconds, 1f, 20f, "0.0");

                if (!tester.Running)
                {
                    if (GUILayout.Button($"Run {tester.runs} balls"))
                        tester.Begin();
                }
                else
                {
                    GUILayout.Label($"   running... {tester.Completed}/{tester.runs}", _label);

                    if (GUILayout.Button("Stop"))
                        tester.Stop();
                }

                if (!string.IsNullOrEmpty(tester.Report))
                    GUILayout.Label(tester.Report, _label);
            }

            GUILayout.Space(10);
            GUILayout.Label("READOUT", _label);
            GUILayout.Label($"speed {PlayController.ToMetresPerSecond(play.FastestSpeed):0.00} m/s   " +
                            $"peak {PlayController.ToMetresPerSecond(play.PeakSpeed):0.00}", _label);
            GUILayout.Label($"could climb {play.ClimbableLayers:0.00} layers", _label);
            GUILayout.Label($"energy kept {play.EfficiencyPercent:0} %   contacts {play.ContactRate:0}/s", _label);

            GUILayout.Space(8);
            if (GUILayout.Button("Reset world settings"))
            {
                gravityScale = 1f;
                Physics.gravity = new Vector3(0f, DefaultGravity, 0f);
                Physics.bounceThreshold = DefaultBounceThreshold;
                Time.fixedDeltaTime = 1f / DefaultHz;
                Time.timeScale = 1f;
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        bool Slider(string label, ref float value, float min, float max, string format)
        {
            GUILayout.Label($"{label}: {value.ToString(format)}", _label);

            float updated = GUILayout.HorizontalSlider(value, min, max);
            if (Mathf.Approximately(updated, value))
                return false;

            value = updated;
            return true;
        }

        bool IntSlider(string label, ref int value, int min, int max)
        {
            GUILayout.Label($"{label}: {value}", _label);

            int updated = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));
            if (updated == value)
                return false;

            value = updated;
            return true;
        }
    }
}
