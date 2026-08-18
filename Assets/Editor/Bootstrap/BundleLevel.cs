#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using BlockMarbleRun;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// Copies one of your own saved creations into the build, so it ships with the game.
    ///
    /// The authoring path is simply to play the game and save: in the editor the save store writes
    /// real files under <c>Application.persistentDataPath/creations</c>, and this picks one of them up
    /// and puts it where the build can read it (<see cref="BlockMarbleRun.Persistence.BundledLevels"/>).
    ///
    /// Copied rather than referenced. A bundled level is a snapshot of a creation at the moment it was
    /// judged good enough to ship, and leaving it pointing at a live save would let it change - or
    /// vanish - without anyone deciding that it should.
    /// </summary>
    public static class BundleLevel
    {
        const string Destination = "Assets/Resources/Levels";

        [MenuItem("Block Marble Run/Bundle a Saved Creation")]
        public static void Run()
        {
            string source = Path.Combine(Application.persistentDataPath, "creations");

            if (!Directory.Exists(source))
            {
                EditorUtility.DisplayDialog(
                    "Nothing to bundle",
                    $"No saved creations at {source}.\n\nPlay the game in the editor, build something " +
                    "and press S to save it, then run this again.",
                    "OK");

                return;
            }

            string picked = EditorUtility.OpenFilePanel("Bundle a saved creation", source, "json");

            if (string.IsNullOrEmpty(picked))
                return;

            Directory.CreateDirectory(Destination);

            string name = Path.GetFileNameWithoutExtension(picked);
            string json = Path.Combine(Destination, $"{name}.json");

            // Unity reads a text asset by extension, and .json is one it knows. The file keeps the
            // creation's own name, which is what the browser shows.
            File.Copy(picked, json, overwrite: true);

            // The picture too, if the save has one: a level chosen from a grid of thumbnails is
            // chosen by recognising it, and a bundled level with no picture is the one nobody opens.
            string thumbnail = Path.ChangeExtension(picked, ".png");

            if (File.Exists(thumbnail))
                File.Copy(thumbnail, Path.Combine(Destination, $"{name}.png"), overwrite: true);

            AssetDatabase.Refresh();

            Debug.Log($"[Levels] Bundled '{name}' from {picked}. {Describe(json)}");

            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<TextAsset>(json));
        }

        /// <summary>
        /// Reads the level back and checks it against this build's catalog.
        ///
        /// A level that ships naming parts the build does not have is worse than no level at all: it
        /// loads to a hole, silently, on someone else's machine. Better to hear about it here.
        /// </summary>
        static string Describe(string assetPath)
        {
            Persistence.SaveModel model;

            try
            {
                model = Persistence.SaveModel.FromJson(File.ReadAllText(assetPath));
            }
            catch (System.Exception e)
            {
                return $"It could not be read back ({e.Message}), so check it before shipping.";
            }

            if (model?.parts == null)
                return "It could not be read back, so check it before shipping.";

            var catalog = AssetDatabase.LoadAssetAtPath<Parts.PartCatalog>("Assets/Parts/PartCatalog.asset");
            var missing = new System.Collections.Generic.HashSet<string>();

            foreach (Persistence.SavedPart part in model.parts)
            {
                if (catalog == null || string.IsNullOrEmpty(part.id))
                    continue;

                bool known = part.id.StartsWith(Parts.ProceduralPillars.IdPrefix);

                foreach (Parts.PartDefinition def in catalog.parts)
                    if (def != null && def.id == part.id)
                        known = true;

                if (!known)
                    missing.Add(part.id);
            }

            string summary = $"{model.parts.Length} part(s), save version {model.version}.";

            return missing.Count == 0
                ? summary
                : $"{summary} WARNING: this build has no {string.Join(", ", missing)} - the level will " +
                  "load with those pieces missing.";
        }
    }
}
#endif
