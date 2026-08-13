using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BlockMarbleRun.Persistence
{
    /// <summary>
    /// WebGL store, backed by IndexedDB through <c>SaveStore.jslib</c>.
    ///
    /// Waiting on <see cref="BMR_PendingWrites"/> is the point of the whole design: on WebGL a write
    /// that has returned is not yet durable, so <see cref="SaveAsync"/> only completes once the
    /// database has actually flushed. Without that, closing the tab straight after saving loses the
    /// creation the player just saved.
    /// </summary>
    public sealed class IndexedDbSaveStore : ISaveStore
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void BMR_Init();
        [DllImport("__Internal")] static extern int BMR_IsReady();
        [DllImport("__Internal")] static extern int BMR_HasFailed();
        [DllImport("__Internal")] static extern int BMR_PendingWrites();
        [DllImport("__Internal")] static extern IntPtr BMR_List();
        [DllImport("__Internal")] static extern IntPtr BMR_Load(string key);
        [DllImport("__Internal")] static extern void BMR_Save(string key, string value);
        [DllImport("__Internal")] static extern void BMR_Delete(string key);
        [DllImport("__Internal")] static extern void BMR_Free(IntPtr ptr);
#else
        // Editor and desktop stubs, so this class still compiles everywhere and can be unit tested.
        static void BMR_Init() { }
        static int BMR_IsReady() => 1;
        static int BMR_HasFailed() => 0;
        static int BMR_PendingWrites() => 0;
        static IntPtr BMR_List() => IntPtr.Zero;
        static IntPtr BMR_Load(string key) => IntPtr.Zero;
        static void BMR_Save(string key, string value) { }
        static void BMR_Delete(string key) { }
        static void BMR_Free(IntPtr ptr) { }
#endif

        /// <summary>Give up rather than hang if the database never opens; private browsing can block it entirely.</summary>
        const float TimeoutSeconds = 10f;

        bool _initialised;

        [Serializable]
        class SlotNames
        {
            public string[] items;
        }

        static string ThumbKey(string slot) => $"thumb:{slot}";

        /// <summary>Reads a string the plugin allocated, then frees it. Skipping the free leaks the heap.</summary>
        static string TakeString(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUTF8(pointer);
            }
            finally
            {
                BMR_Free(pointer);
            }
        }

        public async Awaitable InitialiseAsync()
        {
            if (_initialised)
                return;

            BMR_Init();

            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (BMR_IsReady() == 0 && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();

            if (BMR_IsReady() == 0)
                Debug.LogError("[Save] IndexedDB did not become ready; this session cannot save.");
            else if (BMR_HasFailed() != 0)
                Debug.LogError("[Save] IndexedDB is unavailable (private browsing?); this session cannot save.");

            _initialised = true;
        }

        public async Awaitable<SaveSlot[]> ListAsync()
        {
            await InitialiseAsync();

            string json = TakeString(BMR_List());
            if (string.IsNullOrEmpty(json))
                return Array.Empty<SaveSlot>();

            // JsonUtility will not parse a bare array, so wrap it in an object with one field.
            SlotNames names = JsonUtility.FromJson<SlotNames>("{\"items\":" + json + "}");
            if (names?.items == null)
                return Array.Empty<SaveSlot>();

            var slots = new List<SaveSlot>(names.items.Length);
            foreach (string name in names.items)
            {
                // The timestamp lives inside the creation itself, so listing stays a cheap key scan.
                long savedAt = 0;
                string body = TakeString(BMR_Load(name));
                if (!string.IsNullOrEmpty(body))
                {
                    SaveModel model = JsonUtility.FromJson<SaveModel>(body);
                    if (model != null)
                        savedAt = model.savedAtUnixSeconds;
                }

                slots.Add(new SaveSlot { name = name, savedAtUnixSeconds = savedAt });
            }

            slots.Sort((a, b) => b.savedAtUnixSeconds.CompareTo(a.savedAtUnixSeconds));
            return slots.ToArray();
        }

        public async Awaitable<string> LoadAsync(string slot)
        {
            await InitialiseAsync();
            return TakeString(BMR_Load(slot));
        }

        public async Awaitable SaveAsync(string slot, string json)
        {
            await InitialiseAsync();
            BMR_Save(slot, json);
            await FlushAsync();
        }

        public async Awaitable DeleteAsync(string slot)
        {
            await InitialiseAsync();
            BMR_Delete(slot);
            await FlushAsync();
        }

        public async Awaitable<byte[]> LoadThumbnailAsync(string slot)
        {
            await InitialiseAsync();
            string base64 = TakeString(BMR_Load(ThumbKey(slot)));
            return string.IsNullOrEmpty(base64) ? null : Convert.FromBase64String(base64);
        }

        public async Awaitable SaveThumbnailAsync(string slot, byte[] png)
        {
            await InitialiseAsync();

            // IndexedDB through this bridge carries strings, so the PNG travels as base64.
            BMR_Save(ThumbKey(slot), Convert.ToBase64String(png));
            await FlushAsync();
        }

        /// <summary>Waits for queued writes to reach the database, so callers can trust a completed save.</summary>
        async Awaitable FlushAsync()
        {
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;

            while (BMR_PendingWrites() > 0 && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync();

            if (BMR_PendingWrites() > 0)
                Debug.LogError("[Save] Writes did not flush before the timeout; data may be lost.");
        }
    }
}
