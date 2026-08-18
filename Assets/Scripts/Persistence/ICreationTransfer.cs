using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BlockMarbleRun.Persistence
{
    /// <summary>
    /// Getting a creation out of the game as a file (DESIGN.md §8.2).
    ///
    /// Saves are scoped to the player and, in a browser, to the origin - so a creation built on one
    /// port cannot be opened from another, and nothing built in a browser can be reached from the
    /// editor at all. A file crosses all of those boundaries: it is the same JSON the store holds,
    /// and it is what the level-bundling tool reads.
    ///
    /// Separate from <see cref="ISaveStore"/> deliberately. A store is where creations live; this is
    /// how one leaves. Mixing them would put a browser download API into the interface that the file
    /// system implements and vice versa.
    /// </summary>
    public interface ICreationTransfer
    {
        /// <summary>
        /// Hands the player a file. Returns where it went, for saying so, or null if it failed.
        ///
        /// On the web the answer is the browser's own download folder, which the game cannot know -
        /// so what comes back is the file's name rather than a path.
        /// </summary>
        string Export(string name, string json);
    }

    public static class CreationTransfer
    {
        public static ICreationTransfer Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new BrowserCreationTransfer();
#else
            return new FileCreationTransfer();
#endif
        }

        /// <summary>A file name that survives every filesystem, taken from a creation's own name.</summary>
        public static string FileNameFor(string name)
        {
            string safe = string.IsNullOrWhiteSpace(name) ? "creation" : name;

            foreach (char bad in Path.GetInvalidFileNameChars())
                safe = safe.Replace(bad, '-');

            // Colons survive Path.GetInvalidFileNameChars on some platforms and confuse others, and a
            // timestamped save is full of them.
            safe = safe.Replace(':', '-').Trim();

            return $"{safe}.json";
        }
    }

    /// <summary>Desktop and editor: a real file, next to the saves themselves.</summary>
    public sealed class FileCreationTransfer : ICreationTransfer
    {
        public string Export(string name, string json)
        {
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, "exports");
                Directory.CreateDirectory(folder);

                string path = Path.Combine(folder, CreationTransfer.FileNameFor(name));
                File.WriteAllText(path, json);

                return path;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }
    }

    /// <summary>WebGL: a Blob the browser downloads. See Plugins/WebGL/FileTransfer.jslib.</summary>
    public sealed class BrowserCreationTransfer : ICreationTransfer
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern int BMR_Download(string name, string json);
#else
        static int BMR_Download(string name, string json) => 0;
#endif

        public string Export(string name, string json)
        {
            string file = CreationTransfer.FileNameFor(name);

            return BMR_Download(file, json) == 1 ? file : null;
        }
    }
}
