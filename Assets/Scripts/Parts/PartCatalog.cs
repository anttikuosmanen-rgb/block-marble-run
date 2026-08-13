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

        public PartDefinition Get(int index) =>
            parts.Count == 0 ? null : parts[Mathf.Clamp(index, 0, parts.Count - 1)];

        public Color ColorAt(int index) =>
            palette.Length == 0 ? Color.white : palette[((index % palette.Length) + palette.Length) % palette.Length];
    }
}
