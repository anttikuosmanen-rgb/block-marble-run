using UnityEngine;

namespace BlockMarbleRun.World
{
    /// <summary>
    /// Routes browser keyboard events to the player.
    ///
    /// The generated page gives the canvas tabindex="-1", so it only takes focus when clicked - and
    /// keys pressed while focus sits on the document go nowhere. Capturing all keyboard input makes
    /// Unity listen at the document level instead, so the controls work as soon as the page loads
    /// rather than only after the player happens to click the right element.
    /// </summary>
    public static class WebGLInputBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialise()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = true;
#endif
        }
    }
}
