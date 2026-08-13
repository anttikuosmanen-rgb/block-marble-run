#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// CI build entry points. The two WebGL variants exist because the hosts differ in what they can
    /// serve, not because of a preference - see DESIGN.md §9.1.
    /// </summary>
    public static class BuildScript
    {
        static string[] Scenes => EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        /// <summary>
        /// Self-hosted: the server can send `Content-Encoding: br`, so the browser decompresses
        /// natively and the build ships without a JavaScript decompressor.
        /// </summary>
        public static void BuildWebGLSelfHost() => BuildWebGL("build/webgl-selfhost", decompressionFallback: false);

        /// <summary>
        /// GitHub Pages cannot set response headers. Without the fallback the browser receives Brotli
        /// bytes it was never told to decode and the build fails to load, so the embedded decompressor
        /// is the price of hosting there.
        /// </summary>
        public static void BuildWebGLPages() => BuildWebGL("build/webgl-pages", decompressionFallback: true);

        /// <summary>
        /// Opt-in escape hatch for local smoke tests. A Unity install without the "Mac Build Support
        /// (IL2CPP)" module cannot build the shipping configuration at all, and quietly downgrading
        /// would let local builds diverge from CI without anyone noticing - so the downgrade has to
        /// be asked for by name. CI never sets this, so released builds are always IL2CPP.
        /// </summary>
        static ScriptingImplementation RequestedBackend =>
            Environment.GetEnvironmentVariable("BMR_SCRIPTING_BACKEND") == "mono"
                ? ScriptingImplementation.Mono2x
                : ScriptingImplementation.IL2CPP;

        public static void BuildMacOS()
        {
            ScriptingImplementation backend = RequestedBackend;
            if (backend != ScriptingImplementation.IL2CPP)
                Debug.LogWarning("[Build] BMR_SCRIPTING_BACKEND=mono - local smoke test only, not a shippable build.");

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.High);
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, 2); // Intel + Apple Silicon

            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = "build/macos/BlockMarbleRun.app",
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
            });
        }

        static void BuildWebGL(string outputPath, bool decompressionFallback)
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.WebGL, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.High);

            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = decompressionFallback;

            // The heap has to hold every part mesh at once; growth avoids a hard ceiling on large builds.
            PlayerSettings.WebGL.memoryGrowthMode = WebGLMemoryGrowthMode.Geometric;

            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = outputPath,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
            });
        }

        static void Run(BuildPlayerOptions options)
        {
            if (options.scenes.Length == 0)
                throw new Exception("No enabled scenes in build settings; run Setup Project first.");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            Debug.Log($"[Build] {summary.platform} {summary.result} " +
                      $"({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:0} s) -> {options.locationPathName}");

            if (summary.result != BuildResult.Succeeded)
                throw new Exception($"Build failed: {summary.result}");

            StripDebugArtifacts(options.locationPathName);
        }

        /// <summary>
        /// Unity emits a BurstDebugInformation_DoNotShip folder beside the player. The name is the
        /// instruction: without this it would be published to Pages along with the build.
        /// </summary>
        static void StripDebugArtifacts(string outputPath)
        {
            string parent = Directory.Exists(outputPath) ? outputPath : Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                return;

            foreach (string dir in Directory.GetDirectories(parent, "*DoNotShip*", SearchOption.AllDirectories))
            {
                Directory.Delete(dir, recursive: true);
                Debug.Log($"[Build] Removed {Path.GetFileName(dir)}");
            }
        }
    }
}
#endif
