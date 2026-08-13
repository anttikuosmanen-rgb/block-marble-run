using System;
using UnityEngine;

namespace BlockMarbleRun.Persistence
{
    /// <summary>
    /// One placed part, as stored. Keyed on the part's stable string id rather than a catalog index,
    /// so adding, removing or reordering parts never silently rewrites what an old save meant.
    /// </summary>
    [Serializable]
    public struct SavedPart
    {
        public string id;
        public int x;
        public int y;
        public int layer;
        public int rot;
        public int color;
    }

    /// <summary>
    /// A creation on disk. See DESIGN.md §8.
    ///
    /// There is no grid size: the world is unbounded. The stored bounds are derived metadata, kept
    /// only so the loader can frame the camera without first walking every part.
    /// </summary>
    [Serializable]
    public class SaveModel
    {
        /// <summary>Bump whenever the stored shape changes, and add a matching step in <see cref="SaveMigrations"/>.</summary>
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public string name = "Untitled";
        public long savedAtUnixSeconds;

        public Vector3Int boundsMin;
        public Vector3Int boundsMax;

        public SavedPart[] parts = Array.Empty<SavedPart>();

        public string ToJson() => JsonUtility.ToJson(this);

        public static SaveModel FromJson(string json)
        {
            SaveModel model = JsonUtility.FromJson<SaveModel>(json);
            return model == null ? null : SaveMigrations.Upgrade(model);
        }
    }

    /// <summary>
    /// Brings older saves up to the current version.
    ///
    /// Present from v1 with nothing to do, deliberately: the moment a real migration is needed there
    /// will already be saves in the wild, and a chain that exists is far easier to extend than one
    /// that has to be introduced retroactively.
    /// </summary>
    public static class SaveMigrations
    {
        public static SaveModel Upgrade(SaveModel model)
        {
            if (model.version > SaveModel.CurrentVersion)
            {
                Debug.LogWarning(
                    $"[Save] '{model.name}' was written by version {model.version}, newer than this build " +
                    $"({SaveModel.CurrentVersion}). Loading anyway; unknown fields are ignored.");
                return model;
            }

            // while (model.version < SaveModel.CurrentVersion) { ... step by step ... }

            model.version = SaveModel.CurrentVersion;
            return model;
        }
    }
}
