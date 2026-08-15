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

        /// <summary>0 none, 1 start, 2 goal. Added after v1; absent in older saves, where it reads 0.</summary>
        public int role;
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
        public const int CurrentVersion = 3;

        public int version = CurrentVersion;
        public string name = "Untitled";
        public long savedAtUnixSeconds;

        public Vector3Int boundsMin;
        public Vector3Int boundsMax;

        public SavedPart[] parts = Array.Empty<SavedPart>();

        /// <summary>
        /// The world the build stands in: 0 grid, 1 sand, 2 water, and how deep the water is.
        ///
        /// Saved with the creation because a run built to end in water is not the same creation
        /// without it - the water is as much a part of what was made as the bricks are. Older saves
        /// have neither field and JsonUtility leaves them at these defaults, which is the grid the
        /// build was made on.
        /// </summary>
        public int floorStyle;

        public float waterLevel = 0.12f;

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

            // The first real step, and the reason the chain was built before it was needed.
            //
            // Up to v2 the grid stepped a whole brick; from v3 it steps a half, so plates have
            // somewhere to stand. Every stored layer index therefore means half of what it used to,
            // and a creation saved before the change would load at half its height with its channels
            // meeting nothing. Doubling is the whole migration - the grid got finer, not different.
            if (model.version < 3)
            {
                for (int i = 0; i < model.parts.Length; i++)
                    model.parts[i].layer *= 2;

                model.boundsMin = new Vector3Int(model.boundsMin.x, model.boundsMin.y * 2, model.boundsMin.z);
                model.boundsMax = new Vector3Int(model.boundsMax.x, model.boundsMax.y * 2, model.boundsMax.z);
            }

            // Earlier additions needed no step, and both explain why. The role field was added
            // without a bump at all: JsonUtility leaves an absent field at its default, and "no role"
            // is exactly what an older save means. The floor and water level took a bump to v2
            // because they are worth telling apart in a file, but they still read correctly without a
            // step, since a v1 save was made on the grid and the grid is what the default says.
            //
            // A step is for a change that cannot be read correctly without one.

            model.version = SaveModel.CurrentVersion;
            return model;
        }
    }
}
