#if UNITY_EDITOR
using System.Linq;
using System.Text;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using BlockMarbleRun.Persistence;
using UnityEditor;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Tests
{
    /// <summary>
    /// Round-trips a build through the save model.
    ///
    /// Persistence defects are the expensive kind: they surface as a creation that loads back subtly
    /// wrong, or empty, long after the data that would prove what happened is gone. Cheaper to assert
    /// the shape here than to debug it from a player's lost build.
    /// </summary>
    public static class SaveSelfTest
    {
        static int _passed;
        static int _failed;
        static StringBuilder _log;

        [MenuItem("Block Marble Run/Run Save Self Test")]
        public static void Run()
        {
            _passed = 0;
            _failed = 0;
            _log = new StringBuilder();

            TestRoundTripPreservesEveryPart();
            TestJsonSurvivesSerialisation();
            TestUnknownPartIsSkippedNotFatal();
            TestVersionIsStamped();
            TestNewerVersionStillLoads();

            string summary = $"[SaveSelfTest] {_passed} passed, {_failed} failed.\n{_log}";
            if (_failed > 0) Debug.LogError(summary); else Debug.Log(summary);
        }

        static void TestRoundTripPreservesEveryPart()
        {
            (GridMap map, PartCatalog catalog) = BuildSample();

            SaveModel model = SaveService.Capture(map, "test");
            Check("captures every part", model.parts.Length == map.Parts.Count,
                $"{model.parts.Length} vs {map.Parts.Count}");

            var restored = new GridMap();
            var service = new SaveService(new FileSaveStore(), catalog);
            LoadReport report = service.Apply(model, restored, _ => null);

            Check("restores every part", report.Loaded == model.parts.Length,
                $"loaded {report.Loaded} of {model.parts.Length}");
            Check("restores the same cell count", restored.CellCount == map.CellCount,
                $"{restored.CellCount} vs {map.CellCount}");
            Check("rejects nothing on a clean round trip", report.Rejected == 0, $"{report.Rejected} rejected");

            // Rotation and colour are the fields most likely to be quietly dropped, since a build
            // still looks plausible without them.
            PlacedPart original = map.Parts.First();
            PlacedPart match = restored.Parts.FirstOrDefault(p => p.Origin == original.Origin);
            Check("preserves rotation", match != null && match.Rotation == original.Rotation);
            Check("preserves colour", match != null && match.ColorIndex == original.ColorIndex);
        }

        static void TestJsonSurvivesSerialisation()
        {
            (GridMap map, PartCatalog catalog) = BuildSample();

            string json = SaveService.Capture(map, "json test").ToJson();
            Check("produces non-trivial json", !string.IsNullOrEmpty(json) && json.Length > 32,
                $"length {json?.Length ?? 0}");

            SaveModel parsed = SaveModel.FromJson(json);
            Check("parses back", parsed != null);
            Check("keeps the name", parsed?.name == "json test", parsed?.name);
            Check("keeps every part through json", parsed != null && parsed.parts.Length == map.Parts.Count,
                $"{parsed?.parts.Length} vs {map.Parts.Count}");
        }

        static void TestUnknownPartIsSkippedNotFatal()
        {
            (GridMap map, PartCatalog catalog) = BuildSample();
            SaveModel model = SaveService.Capture(map, "unknown");

            // Simulate a part that has since been retired from the catalog.
            model.parts[0].id = "a_part_that_no_longer_exists";

            var restored = new GridMap();
            var service = new SaveService(new FileSaveStore(), catalog);
            LoadReport report = service.Apply(model, restored, _ => null);

            Check("keeps the rest of the build", report.Loaded == model.parts.Length - 1,
                $"loaded {report.Loaded} of {model.parts.Length - 1}");
            Check("reports the unknown id", report.UnknownParts.Count == 1);
        }

        static void TestVersionIsStamped()
        {
            (GridMap map, _) = BuildSample();
            SaveModel model = SaveService.Capture(map, "version");

            Check("stamps the current version", model.version == SaveModel.CurrentVersion);
            Check("stamps a timestamp", model.savedAtUnixSeconds > 0);
        }

        /// <summary>A save from a newer build should degrade, not refuse.</summary>
        static void TestNewerVersionStillLoads()
        {
            (GridMap map, _) = BuildSample();
            SaveModel model = SaveService.Capture(map, "future");
            model.version = SaveModel.CurrentVersion + 5;

            SaveModel parsed = SaveModel.FromJson(model.ToJson());
            Check("loads a newer save rather than refusing", parsed != null && parsed.parts.Length > 0);
        }

        // --- helpers -------------------------------------------------------------------------

        static (GridMap, PartCatalog) BuildSample()
        {
            var catalog = ScriptableObject.CreateInstance<PartCatalog>();
            PartDefinition block = MakeDef("block_2x2", new Vector2Int(2, 2), 1, studs: true);
            PartDefinition track = MakeDef("track_2x2", new Vector2Int(2, 2), 1, studs: false);
            catalog.parts.Add(block);
            catalog.parts.Add(track);

            var map = new GridMap();
            map.Add(new PlacedPart(block, new GridCoord(0, 0, 0), 1, 3));
            map.Add(new PlacedPart(block, new GridCoord(2, 0, 0), 2, 1));
            map.Add(new PlacedPart(block, new GridCoord(0, 0, 1), 0, 5));
            map.Add(new PlacedPart(track, new GridCoord(4, 4, 0), 3, 2));

            return (map, catalog);
        }

        static PartDefinition MakeDef(string id, Vector2Int size, int layers, bool studs)
        {
            var def = ScriptableObject.CreateInstance<PartDefinition>();
            def.id = id;
            def.displayName = id;
            def.footprintSize = size;
            def.heightLayers = layers;

            int count = size.x * size.y;
            def.footprintMask = Enumerable.Repeat(true, count).ToArray();
            def.bottomSockets = Enumerable.Repeat(true, count).ToArray();
            def.topStuds = Enumerable.Repeat(studs, count).ToArray();
            return def;
        }

        static void Check(string what, bool condition, string detail = null)
        {
            if (condition) { _passed++; return; }
            _failed++;
            _log.AppendLine($"  FAIL: {what}{(detail != null ? $" - {detail}" : "")}");
        }
    }
}
#endif
