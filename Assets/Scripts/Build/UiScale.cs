using UnityEngine;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// One scale factor for every IMGUI panel in the game.
    ///
    /// IMGUI measures in raw pixels, and a raw pixel on a dense laptop display is a third the size of
    /// one on the monitor these panels were laid out against - so a 13 point label that reads fine at
    /// 1080p is unreadable on the same physical area at 4K. Scaling the whole GUI matrix rather than
    /// each font size keeps the panels' proportions and their spacing intact; the alternative is
    /// every size in every panel carrying its own multiplier and drifting apart.
    ///
    /// Screen height rather than Screen.dpi, which WebGL reports as zero often enough to be useless.
    /// </summary>
    public static class UiScale
    {
        /// <summary>Design height the panels were laid out for.</summary>
        const float Reference = 900f;

        public static float Factor => Mathf.Clamp(Screen.height / Reference, 1f, 3f);

        /// <summary>Screen size in the scaled space panels should lay themselves out in.</summary>
        public static float Width => Screen.width / Factor;
        public static float Height => Screen.height / Factor;

        /// <summary>Pointer position in that same space, so hit tests line up with what is drawn.</summary>
        public static Vector2 ToGui(Vector2 screenPixels) => screenPixels / Factor;

        public static void Begin() => GUI.matrix = Matrix4x4.Scale(Vector3.one * Factor);
        public static void End() => GUI.matrix = Matrix4x4.identity;
    }
}
