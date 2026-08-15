using System.Collections.Generic;
using UnityEngine;

namespace BlockMarbleRun.Parts
{
    /// <summary>Ordered palette of the parts the player can place.</summary>
    [CreateAssetMenu(menuName = "Block Marble Run/Part Catalog", fileName = "PartCatalog")]
    public sealed class PartCatalog : ScriptableObject
    {
        public List<PartDefinition> parts = new();

        [Tooltip("Brick colours, indexed by PlacedPart.ColorIndex.")]
        public Color[] palette =
        {
            new Color(0.85f, 0.18f, 0.16f),
            new Color(0.95f, 0.68f, 0.10f),
            new Color(0.13f, 0.42f, 0.72f),
            new Color(0.25f, 0.60f, 0.28f),
            new Color(0.92f, 0.92f, 0.90f),
            new Color(0.30f, 0.32f, 0.36f),
        };

        List<PartDefinition> _selectable;

        /// <summary>
        /// The parts the palette offers, in catalog order.
        ///
        /// Everything else still lives in <see cref="parts"/>, which is what a save file and the
        /// scaffolder look through: a part that cannot be picked can still be built by the game and
        /// must still load by name.
        /// </summary>
        public List<PartDefinition> Selectable
        {
            get
            {
                if (_selectable != null)
                    return _selectable;

                _selectable = new List<PartDefinition>(parts.Count);

                foreach (PartDefinition def in parts)
                    if (def != null && def.selectable)
                        _selectable.Add(def);

                return _selectable;
            }
        }

        /// <summary>Indexes the offered parts, which is what the palette and Q/E step through.</summary>
        public PartDefinition Get(int index) =>
            Selectable.Count == 0 ? null : Selectable[Mathf.Clamp(index, 0, Selectable.Count - 1)];

        public Color ColorAt(int index) =>
            palette.Length == 0 ? Color.white : palette[((index % palette.Length) + palette.Length) % palette.Length];
    }
}
