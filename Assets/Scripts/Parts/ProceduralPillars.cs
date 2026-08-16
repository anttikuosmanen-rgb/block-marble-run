using System.Collections.Generic;
using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Parts
{
    /// <summary>
    /// Support columns of any height, cut from one modelled pillar.
    ///
    /// A ten-layer column used to be ten separate bricks: ten parts in the map, ten colliders, ten
    /// seams for a marble that strays onto it, and ten entries in a save file. One pillar of the
    /// right height is one of each.
    ///
    /// Definitions are made at runtime and named for their height, so a saved creation refers to
    /// pillar_2x2x9 and gets a pillar_2x2x9 back - see <see cref="Resolve"/>, which the loader asks
    /// when the catalog does not recognise an id.
    /// </summary>
    public sealed class ProceduralPillars
    {
        /// <summary>The one in use, for the scaffolder and the loader to reach without wiring.</summary>
        public static ProceduralPillars Active { get; set; }

        readonly PartDefinition _source;
        readonly Dictionary<int, PartDefinition> _byHeight = new();

        readonly float _shaftFrom;
        readonly float _shaftTo;
        readonly bool _usable;

        /// <summary>Prefix every generated pillar's id carries, so the loader can recognise one.</summary>
        public const string IdPrefix = "pillar_2x2x";

        public ProceduralPillars(PartDefinition source)
        {
            _source = source;

            if (source?.mesh == null)
                return;

            _usable = PillarMeshBuilder.FindShaft(source.mesh, out _shaftFrom, out _shaftTo);

            if (!_usable)
            {
                Debug.LogWarning($"[Pillars] '{source.id}' has no plain shaft to stretch; support " +
                                 "columns will be built from bricks.");
            }
        }

        /// <summary>
        /// Shortest column worth generating.
        ///
        /// Below this the base and the top would overlap - there is no shaft left between them - and
        /// a brick is the right answer anyway for one or two layers.
        /// </summary>
        public int ShortestLayers
        {
            get
            {
                if (!_usable)
                    return int.MaxValue;

                float shaft = _shaftTo - _shaftFrom;

                // The most the source can lose is its whole shaft, less a sliver to keep the base and
                // the top from meeting.
                int canLose = Mathf.FloorToInt((shaft - 0.002f) / GridCoord.LayerUnits);

                return Mathf.Max(2, _source.heightLayers - canLose);
            }
        }

        /// <summary>A pillar exactly this many layers tall, or null when one cannot be made.</summary>
        public PartDefinition ForLayers(int layers)
        {
            if (!_usable || layers < ShortestLayers)
                return null;

            if (layers == _source.heightLayers)
                return _source;

            if (_byHeight.TryGetValue(layers, out PartDefinition cached) && cached != null)
                return cached;

            float delta = (layers - _source.heightLayers) * GridCoord.LayerUnits;

            string id = IdPrefix + layers;
            Mesh mesh = PillarMeshBuilder.Stretch(_source.mesh, _shaftFrom, _shaftTo, delta, id);

            if (mesh == null)
                return null;

            var def = ScriptableObject.CreateInstance<PartDefinition>();

            def.id = id;
            def.displayName = $"Pillar {layers}";
            def.category = _source.category;
            def.mesh = mesh;
            def.heightLayers = layers;
            def.footprintSize = _source.footprintSize;
            def.footprintMask = _source.footprintMask;
            def.topStuds = _source.topStuds;
            def.bottomSockets = _source.bottomSockets;
            def.pivotOffsetUnits = _source.pivotOffsetUnits;
            def.rotation = _source.rotation;
            def.mirrorVerdict = _source.mirrorVerdict;

            // A solid column occupies every layer of its own footprint, which is what the default
            // says when no per-layer mask is present - and it is the truth here, unlike for a ramp.
            def.layerMasks = null;

            _byHeight[layers] = def;
            return def;
        }

        /// <summary>Whether this part is one of the support columns, modelled or generated.</summary>
        public bool IsPillar(PartDefinition def) =>
            def != null && (def == _source || (def.id != null && def.id.StartsWith(IdPrefix)));

        /// <summary>Rebuilds a pillar named in a save file, for ids the catalog has never heard of.</summary>
        public static PartDefinition Resolve(string id)
        {
            if (Active == null || string.IsNullOrEmpty(id) || !id.StartsWith(IdPrefix))
                return null;

            return int.TryParse(id[IdPrefix.Length..], out int layers) ? Active.ForLayers(layers) : null;
        }
    }
}
