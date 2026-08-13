using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BlockMarbleRun.Persistence
{
    /// <summary>
    /// Desktop store: one JSON file per creation, with the thumbnail alongside it.
    /// </summary>
    public sealed class FileSaveStore : ISaveStore
    {
        string _root;

        string Root => _root ??= Path.Combine(Application.persistentDataPath, "creations");

        string PathFor(string slot) => Path.Combine(Root, $"{Sanitise(slot)}.json");
        string ThumbnailPathFor(string slot) => Path.Combine(Root, $"{Sanitise(slot)}.png");

        /// <summary>
        /// Slot names come from the player, so they cannot be trusted as path fragments. Anything
        /// outside a safe set becomes an underscore, which also stops a name like "../config" from
        /// escaping the creations folder.
        /// </summary>
        static string Sanitise(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot))
                return "untitled";

            var sb = new System.Text.StringBuilder(slot.Length);
            foreach (char c in slot)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' ? c : '_');

            return sb.ToString().Trim();
        }

        public async Awaitable InitialiseAsync()
        {
            Directory.CreateDirectory(Root);
            await Awaitable.NextFrameAsync();
        }

        public async Awaitable<SaveSlot[]> ListAsync()
        {
            await Awaitable.NextFrameAsync();

            if (!Directory.Exists(Root))
                return System.Array.Empty<SaveSlot>();

            var slots = new List<SaveSlot>();
            foreach (string path in Directory.GetFiles(Root, "*.json"))
            {
                slots.Add(new SaveSlot
                {
                    name = Path.GetFileNameWithoutExtension(path),
                    savedAtUnixSeconds = new System.DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds(),
                });
            }

            slots.Sort((a, b) => b.savedAtUnixSeconds.CompareTo(a.savedAtUnixSeconds));
            return slots.ToArray();
        }

        public async Awaitable<string> LoadAsync(string slot)
        {
            await Awaitable.NextFrameAsync();
            string path = PathFor(slot);
            return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        }

        public async Awaitable SaveAsync(string slot, string json)
        {
            Directory.CreateDirectory(Root);

            // Write to a temporary file and move it into place, so an interrupted save leaves the
            // previous creation intact rather than a half-written file that will not parse.
            string path = PathFor(slot);
            string temp = path + ".tmp";

            await File.WriteAllTextAsync(temp, json);

            // File.Move has no overwrite overload on this profile, so clear the target first. The
            // window between the two is why the temporary file exists at all: if this is interrupted,
            // the .tmp is still on disk and complete.
            if (File.Exists(path))
                File.Delete(path);

            File.Move(temp, path);
        }

        public async Awaitable DeleteAsync(string slot)
        {
            await Awaitable.NextFrameAsync();

            foreach (string path in new[] { PathFor(slot), ThumbnailPathFor(slot) })
                if (File.Exists(path))
                    File.Delete(path);
        }

        public async Awaitable<byte[]> LoadThumbnailAsync(string slot)
        {
            await Awaitable.NextFrameAsync();
            string path = ThumbnailPathFor(slot);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        }

        public async Awaitable SaveThumbnailAsync(string slot, byte[] png)
        {
            Directory.CreateDirectory(Root);
            await File.WriteAllBytesAsync(ThumbnailPathFor(slot), png);
        }
    }
}
