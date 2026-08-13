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

        void SetHighlight(PlacedPart part, bool highlighted)
        {
            if (part?.Instance == null)
                return;

            var renderer = part.Instance.GetComponent<MeshRenderer>();
            if (renderer == null)
                return;

            renderer.sharedMaterial = highlighted
                ? _highlightMaterial
                : _factory.MaterialFor(part.ColorIndex);
        }

        /// <summary>Re-applies highlighting after instances are rebuilt, such as after an undo.</summary>
        public void RefreshHighlights()
        {
            foreach (PlacedPart part in _selected)
                SetHighlight(part, true);
        }
    }
}
