using System.Collections.Generic;
using UnityEngine;

namespace BlockMarbleRun.Persistence
{
    /// <summary>
    /// Creations that ship inside the build, the same for everyone who opens it.
    ///
    /// Saves are per player and, on the web, per origin: a build served on a different port has its
    /// own IndexedDB and therefore an empty save list. So a creation that is meant to be *part of the
    /// game* - an example to open, a level to try - cannot live in the save store at all. These are
    /// compiled in as text assets and read at startup.
    ///
    /// Read-only, and not because they are protected: there is nowhere to write them back to. Loading
    /// one and saving it puts a copy in the player's own store, which is the behaviour a player
    /// expects from an example anyway.
    /// </summary>
    public static class BundledLevels
    {
        /// <summary>Folder under Resources holding one .json per level, and optionally a .png beside it.</summary>
        public const string Folder = "Levels";

        public readonly struct Level
        {
            public readonly string Name;
            public readonly string Json;

            public Level(string name, string json)
            {
                Name = name;
                Json = json;
            }
        }

        static Level[] _levels;

        /// <summary>Every level in the build, in name order. Read once; they cannot change at runtime.</summary>
        public static IReadOnlyList<Level> All
        {
            get
            {
                if (_levels != null)
                    return _levels;

                var found = new List<Level>();

                foreach (TextAsset asset in Resources.LoadAll<TextAsset>(Folder))
                {
                    if (asset == null || string.IsNullOrWhiteSpace(asset.text))
                        continue;

                    // Unity hands back every text asset in the folder, and a note left beside the
                    // levels is a text asset too. A creation is an object; anything else in there is
                    // documentation and should not appear as something to open.
                    if (!asset.text.TrimStart().StartsWith("{"))
                        continue;

                    found.Add(new Level(asset.name, asset.text));
                }

                found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

                _levels = found.ToArray();
                return _levels;
            }
        }

        /// <summary>The picture for a level, or null. Named for the level, beside it in the folder.</summary>
        public static Texture2D ThumbnailFor(string name) =>
            Resources.Load<Texture2D>($"{Folder}/{name}");

        public static bool TryFind(string name, out Level level)
        {
            foreach (Level candidate in All)
            {
                if (candidate.Name != name)
                    continue;

                level = candidate;
                return true;
            }

            level = default;
            return false;
        }
    }
}
