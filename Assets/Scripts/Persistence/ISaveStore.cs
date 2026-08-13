using System;
using UnityEngine;

namespace BlockMarbleRun.Persistence
{
    [Serializable]
    public struct SaveSlot
    {
        public string name;
        public long savedAtUnixSeconds;
    }

    /// <summary>
    /// Storage behind saving and loading. Two implementations, because the targets differ in kind
    /// rather than degree (DESIGN.md §8.1): macOS has a real filesystem, WebGL has IndexedDB reached
    /// through JavaScript.
    ///
    /// Every call is asynchronous even where the platform could answer immediately. On WebGL a write
    /// is not durable when the call returns, so a synchronous API would be lying; making the desktop
    /// path async as well costs nothing and keeps one shape for both.
    /// </summary>
    public interface ISaveStore
    {
        Awaitable InitialiseAsync();

        Awaitable<SaveSlot[]> ListAsync();

        /// <summary>Returns null when the slot does not exist.</summary>
        Awaitable<string> LoadAsync(string slot);

        /// <summary>Completes only once the data is durable.</summary>
        Awaitable SaveAsync(string slot, string json);

        Awaitable DeleteAsync(string slot);

        /// <summary>PNG bytes, or null when the slot has no thumbnail.</summary>
        Awaitable<byte[]> LoadThumbnailAsync(string slot);

        Awaitable SaveThumbnailAsync(string slot, byte[] png);
    }

    public static class SaveStoreFactory
    {
        public static ISaveStore Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new IndexedDbSaveStore();
#else
            return new FileSaveStore();
#endif
        }
    }
}
