using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.World
{
    /// <summary>
    /// Links a scene object back to its grid entry, so a raycast hit can be resolved to the part that
    /// owns it without searching the map.
    /// </summary>
    public sealed class PlacedPartMarker : MonoBehaviour
    {
        public PlacedPart Part { get; private set; }

        public static void Attach(GameObject go, PlacedPart part) =>
            go.AddComponent<PlacedPartMarker>().Part = part;
    }
}
