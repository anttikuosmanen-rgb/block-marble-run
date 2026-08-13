#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace BlockMarbleRun.EditorTools.Bootstrap
{
    /// <summary>
    /// One-shot batchmode entry point that adds the project's package dependencies without
    /// pinning versions by hand. Package Manager resolves the newest version compatible with
    /// the current editor, which avoids guessing version strings that differ per Unity release.
    /// Run via: Unity -batchmode -quit -executeMethod BlockMarbleRun.EditorTools.Bootstrap.AddPackages.Run
    /// </summary>
    public static class AddPackages
    {
        static readonly string[] Wanted =
        {
            "com.unity.render-pipelines.universal",
            "com.unity.inputsystem",
        };

        public static void Run()
        {
            foreach (var id in Wanted)
            {
                AddRequest request = Client.Add(id);
                while (!request.IsCompleted)
                    System.Threading.Thread.Sleep(100);

                if (request.Status == StatusCode.Success)
                    Debug.Log($"[Bootstrap] Added {request.Result.packageId}");
                else
                    Debug.LogError($"[Bootstrap] Failed to add {id}: {request.Error?.message}");
            }
        }
    }
}
#endif
