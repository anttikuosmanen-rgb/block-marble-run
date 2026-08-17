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

    /// <summary>What a placed part does in play mode, beyond carrying a marble.</summary>
    public enum PartRole
    {
        None,
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
    /// Where a marble can enter or leave a part: one whole channel mouth, not one cell of it.
    /// See DESIGN.md section 6.
    ///
    /// The mouth is located by its centre line rather than by the cells it covers. A channel is two
    /// studs wide, so its centre falls on the boundary <em>between</em> studs and cannot be named in
    /// whole studs at all. Recording it per-cell also let two runs join while offset by one stud,
    /// because a single cell of one mouth still lined up with a single cell of the other - the joint
    /// looked connected while the channels were visibly misaligned.
    ///
    /// Half-studs (8 mm) give an exact integer for both cases, so alignment is an equality test
    /// rather than a tolerance.
    /// </summary>
    [System.Serializable]
    public struct TrackPort
    {
        [Tooltip("Centre of the mouth on the part boundary, in half-studs from the footprint's min corner.")]
        public Vector2Int midlineHalfStuds;

        public Facing facing;

        [Tooltip("Channel floor height above the part's base, in millimetres.")]
        public float heightMm;

        [Tooltip("Mouth width in studs. Two for every part in the current set.")]
        public int widthStuds;

        /// <summary>
        /// The channel's cross-section at this mouth: heights in millimetres above the part's base,
        /// sampled at 1 mm across the opening.
        ///
        /// Carried so a joint can be bridged with the actual groove shape. A flat slab at floor
        /// height would never be touched - the ball rides on the groove walls, not its bottom.
        /// </summary>
        public float[] profileMm;
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

        [Tooltip("Primitive-compound collider, for parts that do not use their own geometry (DESIGN.md §3.3).")]
        public GameObject colliderPrefab;

        [Tooltip("Baked preview for the palette. See PartIconBaker.")]
        public Texture2D icon;

        /// <summary>
        /// Whether the part is offered in the palette.
        ///
        /// A part can be needed without being wanted on the bar. Every brick has a plate, but only
        /// the 2x2 is worth a slot - the rest exist so the scaffolder can reach for one and so a
        /// creation that used one still loads. Leaving them out of the catalog entirely would break
        /// both; leaving them in the palette makes the bar mostly plates.
        /// </summary>
        public bool selectable = true;

        /// <summary>
        /// Whether the part bends when a marble passes through it.
        ///
        /// For growing things - stalks, fronds - which in real Duplo are soft plastic that a marble
        /// pushes aside. Unity has no soft bodies, so a soft part is drawn bending and nudges the
        /// marble as it goes by, while only its base is solid.
        /// </summary>
        public bool soft;

        [Tooltip("Grid layers of the part that are solid. Above this it bends and has no collider.")]
        public int softBodyLayers = 2;

        /// <summary>
        /// Palette index this part is always placed in, or -1 to use whatever colour is picked.
        ///
        /// Some pieces have a colour of their own - a plant is green, and a red one is a decision
        /// rather than an accident. The paint tool still works on it afterwards, so this is a default
        /// and not a restriction.
        /// </summary>
        public int defaultColorIndex = -1;

        /// <summary>
        /// How far above its layer the part is drawn, in world units.
        ///
        /// For pieces that rest on the tops of studs rather than on the surface between them. A Lego
        /// part on a Duplo brick does exactly that: the flanges around its underside sit on the stud,
        /// so the whole piece stands a stud's height proud of the brick.
        /// </summary>
        public float verticalOffsetUnits;

        [Header("Grid")]
        [Tooltip("Footprint bounding size in studs.")]
        public Vector2Int footprintSize = Vector2Int.one;

        [Tooltip("Row-major occupancy over footprintSize, unioned across every layer.")]
        public bool[] footprintMask;

        [Tooltip("Per-layer occupancy: heightLayers planes of footprintSize, layer-major.")]
        public bool[] layerMasks;

        [Tooltip("Height in brick layers (1 layer = 19.2 mm).")]
        public int heightLayers = 1;

        [Tooltip("Offset from the mesh's own origin to the centre of its footprint, in world units (XZ).")]
        public Vector2 pivotOffsetUnits;

        [Tooltip("True when the underside arches into a through-tunnel, as bridge_2x3 does. Such a part needs its real geometry for collision.")]
        public bool hasTunnel;

        /// <summary>
        /// Where the shaft through the part is, measured from the part's own pivot in world units,
        /// and how wide it is. Radius zero means the part has no shaft.
        ///
        /// A funnel is placed by where its hole lands, and that is the one thing a ghost floating in
        /// the air cannot show: the hole is inside the piece and the surface it has to line up with
        /// is underneath. The build guides draw it down onto whatever is below (AlignmentGuides).
        /// </summary>
        public Vector2 dropHoleOffsetUnits;

        public float dropHoleRadiusUnits;

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

        /// <summary>
        /// Whether this part fills a cell on a particular layer.
        ///
        /// Parts are not always solid prisms. slide_curve_4x4's underside ramps from 18 mm down to 0,
        /// so its raised end occupies only the upper layer and leaves real space beneath - space a
        /// support pillar needs to stand in. Claiming every layer under the whole footprint both
        /// blocks that pillar and makes the part collide with the scaffolding meant to hold it up.
        /// </summary>
        public bool OccupiesCell(int x, int y, int layer)
        {
            if (!OccupiesCell(x, y))
                return false;

            int layers = Mathf.Max(1, heightLayers);
            if (layer < 0 || layer >= layers)
                return false;

            if (layerMasks == null || layerMasks.Length != footprintSize.x * footprintSize.y * layers)
                return true; // no per-layer data: fall back to a solid prism

            return layerMasks[layer * footprintSize.x * footprintSize.y + y * footprintSize.x + x];
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
