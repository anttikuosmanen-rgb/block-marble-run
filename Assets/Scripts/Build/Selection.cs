using System.Collections.Generic;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Box selection over placed parts.
    ///
    /// Highlighting swaps in one shared highlight material rather than setting a
    /// MaterialPropertyBlock per renderer. The M1 measurement (DESIGN.md §5.2) showed property blocks
    /// cost every affected brick its SRP batching, and a selection can easily cover a whole build -
    /// exactly the case where that cost would bite hardest.
    /// </summary>
    public sealed class Selection
    {
        readonly HashSet<PlacedPart> _selected = new();
        readonly PartFactory _factory;
        readonly Material _highlightMaterial;

        public Selection(PartFactory factory, Material highlightMaterial)
        {
            _factory = factory;
            _highlightMaterial = highlightMaterial;
        }

        public IReadOnlyCollection<PlacedPart> Parts => _selected;
        public int Count => _selected.Count;
        public bool Contains(PlacedPart part) => _selected.Contains(part);

        public void Clear()
        {
            foreach (PlacedPart part in _selected)
                SetHighlight(part, false);

            _selected.Clear();
        }

        public void Add(PlacedPart part)
        {
            if (part == null || !_selected.Add(part))
                return;

            SetHighlight(part, true);
        }

        public void Remove(PlacedPart part)
        {
            if (part == null || !_selected.Remove(part))
                return;

            SetHighlight(part, false);
        }

        public void Toggle(PlacedPart part)
        {
            if (part == null)
                return;

            if (_selected.Contains(part)) Remove(part); else Add(part);
        }

        /// <summary>
        /// Drops parts that are no longer in the map. Deleting a selection destroys its members, and
        /// a stale entry would later be re-highlighted through a destroyed GameObject.
        /// </summary>
        public void Prune(GridMap map)
        {
            if (_selected.Count == 0)
                return;

            var stale = new List<PlacedPart>();
            foreach (PlacedPart part in _selected)
                if (part.Instance == null || !map.Contains(part))
                    stale.Add(part);

            foreach (PlacedPart part in stale)
                _selected.Remove(part);
        }

        /// <summary>
        /// Selects every part whose centre projects inside the screen rectangle.
        ///
        /// Testing the centre rather than the full bounds keeps the behaviour predictable: a part is
        /// caught when you drag over the middle of it, not when the box clips one distant corner.
        /// </summary>
        public void SelectInScreenRect(GridMap map, Camera camera, Rect rect, bool additive)
        {
            if (camera == null)
                return;

            if (!additive)
                Clear();

            foreach (PlacedPart part in map.Parts)
            {
                part.GetTransform(out Vector3 position, out _);

                // Aim at the middle of the part's height rather than its base, which would sit at the
                // very bottom edge of a tall piece.
                position.y += part.Definition.heightLayers * GridCoord.LayerUnits * 0.5f;

                Vector3 screen = camera.WorldToScreenPoint(position);
                if (screen.z <= 0f)
                    continue; // behind the camera

                if (rect.Contains(new Vector2(screen.x, screen.y)))
                    Add(part);
            }
        }

        readonly Dictionary<Material, Material> _tinted = new();

        /// <summary>
        /// The selected look: the piece's own colour pulled towards the highlight, not replaced by it.
        ///
        /// One flat highlight material for everything made selection indistinguishable from painting
        /// - a selected red brick and a selected blue one were the same mint, so the only way to know
        /// what you had was to deselect and look. Tinting keeps the piece recognisable while still
        /// reading as picked out.
        ///
        /// Still a shared material per colour rather than a property block, for the reason above:
        /// property blocks cost every affected renderer its SRP batching, and there are only as many
        /// of these as there are palette entries.
        /// </summary>
        Material TintFor(PlacedPart part)
        {
            Material basis = _factory.MaterialFor(part);
            if (_highlightMaterial == null || basis == null)
                return _highlightMaterial != null ? _highlightMaterial : basis;

            // Keyed by the basis material itself: there is one per palette colour plus the role
            // materials, so the dictionary stays the size of the palette however large the build is.
            Material key = basis;
            if (_tinted.TryGetValue(key, out Material cached) && cached != null)
                return cached;

            // Copied from the part's own material, not from the highlight.
            //
            // Building it the other way round was the reason selection still looked like paint: the
            // highlight's cyan emission came along with the template and lit every piece the same
            // colour, so a red brick and a blue one were both mint however carefully the base colour
            // was blended underneath it.
            var tint = new Material(basis) { name = $"{basis.name} (selected)" };

            Color own = basis.HasProperty(BaseColor) ? basis.GetColor(BaseColor) : Color.white;
            Color highlight = _highlightMaterial.HasProperty(BaseColor)
                ? _highlightMaterial.GetColor(BaseColor)
                : new Color(0.35f, 0.85f, 1f);

            tint.SetColor(BaseColor, Color.Lerp(own, highlight, 0.28f));

            if (tint.HasProperty(EmissionColor))
            {
                // Enough to read as picked out under any lighting, not enough to become the colour.
                tint.SetColor(EmissionColor, highlight * 0.12f);
                tint.EnableKeyword("_EMISSION");
            }

            _tinted[key] = tint;
            return tint;
        }

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        void SetHighlight(PlacedPart part, bool highlighted)
        {
            if (part?.Instance == null)
                return;

            var renderer = part.Instance.GetComponent<MeshRenderer>();
            if (renderer == null)
                return;

            renderer.sharedMaterial = highlighted ? TintFor(part) : _factory.MaterialFor(part);
        }

        /// <summary>
        /// Puts every selected part back to its own material without emptying the selection.
        ///
        /// Needed when the parts are about to be replaced by transformed copies: the originals are
        /// destroyed, and a highlight is not something the replacement inherits.
        /// </summary>
        public void ClearHighlights()
        {
            foreach (PlacedPart part in _selected)
                SetHighlight(part, false);
        }

        /// <summary>Replaces the selection wholesale, as after a transform rebuilds every member.</summary>
        public void SetTo(IEnumerable<PlacedPart> parts)
        {
            Clear();

            foreach (PlacedPart part in parts)
                Add(part);
        }

        /// <summary>Re-applies highlighting after instances are rebuilt, such as after an undo.</summary>
        public void RefreshHighlights()
        {
            foreach (PlacedPart part in _selected)
                SetHighlight(part, true);
        }
    }
}
