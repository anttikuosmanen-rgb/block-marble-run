using UnityEngine;

namespace BlockMarbleRun.Parts
{
    public enum PartCategory
    {
        Block,
        Track,
        Curve,
        Slide,
        Crossing,
        Terminal,
        Bridge,
        Start,
        Goal,
    }

    public enum RotationMode
    {
        Free90,
        Half180,
        None,
    }

    public enum Facing
    {
        North,
        East,
        South,
        West,
    }

    /// <summary>
    /// Where a marble can enter or leave a part. Rotated with the part when placed.
    /// See DESIGN.md section 6.
    /// </summary>
    [System.Serializable]
    public struct TrackPort
    {
        [Tooltip("Which cell of the part's own footprint this port sits on.")]
        public Vector2Int cell;

        public Facing facing;

        [Tooltip("Channel floor height above the part's base, in millimetres.")]
        public float heightMm;
    }

    /// <summary>
    /// Everything the game needs to know about one kind of part. Mostly derived from the source
    /// STL by the import tooling (see DESIGN.md section 3.2), with ports and category left to a
    /// human.
    /// </summary>
    [CreateAssetMenu(menuName = "Block Marble Run/Part Definition", fileName = "part")]
    public sealed class PartDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id referenced by save files. Never reuse or renumber - saves are keyed on it.")]
        public string id;

        public string displayName;
        public PartCategory category;

        [Header("Geometry")]
        public Mesh mesh;

        [Tooltip("Primitive-compound collider. The render mesh is never used for collision (DESIGN.md §3.3).")]
        public GameObject colliderPrefab;

        [Header("Grid")]
        [Tooltip("Footprint bounding size in studs.")]
        public Vector2Int footprintSize = Vector2Int.one;

        [Tooltip("Row-major occupancy over footprintSize; supports non-rectangular parts such as u_turn.")]
        public bool[] footprintMask;

        [Tooltip("Height in brick layers (1 layer = 19.2 mm).")]
        public int heightLayers = 1;

        [Tooltip("Row-major: which cells expose a stud on top. Empty means nothing can stack on this part.")]
        public bool[] topStuds;

        [Tooltip("Row-major: which cells have a socket underneath.")]
        public bool[] bottomSockets;

        public RotationMode rotation = RotationMode.Free90;

        [Header("Track")]
        public TrackPort[] ports;

        [Tooltip("Rough channel path in local space for soft assist (DESIGN.md §13.1). Empty = pure physics on this part.")]
        public Vector3[] centerline;

        [Header("Mirroring")]
        [Tooltip("Set on generated mirror parts; points at the source part id (DESIGN.md §3.4).")]
        public string mirrorOf;

        [Tooltip("Author-confirmed chirality verdict, so re-imports do not re-ask.")]
        public MirrorVerdict mirrorVerdict = MirrorVerdict.Unreviewed;

        public bool IsMirror => !string.IsNullOrEmpty(mirrorOf);

        public bool OccupiesCell(int x, int y)
        {
            if (x < 0 || y < 0 || x >= footprintSize.x || y >= footprintSize.y)
                return false;

            int index = y * footprintSize.x + x;
            // An unset mask means "solid rectangle", which is true for most parts and saves
            // authoring every cell by hand.
            return footprintMask == null || footprintMask.Length == 0 || footprintMask[index];
        }
    }

    public enum MirrorVerdict
    {
        Unreviewed,

        /// <summary>Mirror is reproducible by a yaw rotation the game already supports; generating one would duplicate a palette entry.</summary>
        Redundant,

        /// <summary>Genuinely handed; needs a generated mirror part.</summary>
        Chiral,

        /// <summary>Detector could not tell. Needs a human to look at both meshes.</summary>
        Ambiguous,
    }
}
