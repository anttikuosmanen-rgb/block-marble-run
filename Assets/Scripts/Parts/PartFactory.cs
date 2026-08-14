using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Parts
{
    /// <summary>
    /// Builds the scene object for a placed part: mesh, colour, and a collider derived from the grid
    /// footprint rather than the render mesh (DESIGN.md §3.3).
    /// </summary>
    public sealed class PartFactory : MonoBehaviour
    {
        public Material partMaterial;
        public PartCatalog catalog;

        [Tooltip("Tints for designated start and goal pieces, so a role reads at a glance.")]
        public Material startMaterial;
        public Material goalMaterial;

        static MaterialPropertyBlock _block;

        /// <summary>
        /// One shared material per palette colour, built once from the base material.
        ///
        /// The obvious way to colour a brick is a MaterialPropertyBlock, but that opts the renderer
        /// out of the SRP Batcher, and measurement on WebGL put the cost at roughly 20 ms versus 13 ms
        /// for 2000 parts - while cutting triangles fourfold changed nothing at all. The bottleneck is
        /// per-draw CPU work, not geometry. A handful of shared materials keeps every brick batched
        /// and still gives each one its own colour.
        /// </summary>
        Material[] _paletteMaterials;

        public PartCatalog Catalog => catalog;

        void Awake() => BuildPaletteMaterials();

        void BuildPaletteMaterials()
        {
            if (partMaterial == null || catalog == null || catalog.palette.Length == 0)
                return;

            _paletteMaterials = new Material[catalog.palette.Length];
            for (int i = 0; i < _paletteMaterials.Length; i++)
            {
                _paletteMaterials[i] = new Material(partMaterial)
                {
                    name = $"{partMaterial.name}_{i}",
                    enableInstancing = true,
                };
                _paletteMaterials[i].SetColor("_BaseColor", catalog.palette[i]);
            }
        }

        /// <summary>Role tint wins over the brick's own colour, so a designated piece is unmistakable.</summary>
        public Material MaterialFor(PlacedPart part)
        {
            if (part.Role == PartRole.Start && startMaterial != null)
                return startMaterial;

            if (part.Role == PartRole.Goal && goalMaterial != null)
                return goalMaterial;

            return MaterialFor(part.ColorIndex);
        }

        public Material MaterialFor(byte colorIndex)
        {
            if (_paletteMaterials == null || _paletteMaterials.Length == 0)
                return partMaterial;

            return _paletteMaterials[colorIndex % _paletteMaterials.Length];
        }

        /// <summary>
        /// <paramref name="perInstanceColor"/> forces the old MaterialPropertyBlock path, kept so the
        /// stress test can still measure the difference the palette materials make rather than take
        /// it on trust.
        /// </summary>
        public GameObject Create(PlacedPart part, Transform parent, bool withCollider = true,
                                 bool perInstanceColor = false)
        {
            var go = new GameObject(part.Definition.id);
            go.transform.SetParent(parent, false);

            part.GetTransform(out Vector3 position, out Quaternion rotation);
            go.transform.SetPositionAndRotation(position, rotation);

            go.AddComponent<MeshFilter>().sharedMesh = part.Definition.mesh;

            var renderer = go.AddComponent<MeshRenderer>();

            if (perInstanceColor)
            {
                renderer.sharedMaterial = partMaterial;
                ApplyColor(renderer, catalog.ColorAt(part.ColorIndex));
            }
            else
            {
                renderer.sharedMaterial = MaterialFor(part);
            }

            if (withCollider)
                AddColliders(go, part);

            part.Instance = go;
            return go;
        }

        public static void ApplyColor(Renderer renderer, Color color)
        {
            _block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_block);
            _block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(_block);
        }

        /// <summary>
        /// Primitive colliders sized from the footprint, never the render mesh: the meshes run to
        /// 22k triangles and a marble channel is concave, so neither a mesh collider nor a convex
        /// hull would do.
        ///
        /// A solid rectangular footprint collapses to a single box. Anything else - u_turn's open
        /// middle, for instance - gets one box per occupied cell, so the hole stays a hole instead of
        /// becoming a wall the player cannot click through.
        /// </summary>
        static void AddColliders(GameObject go, PlacedPart part)
        {
            PartDefinition def = part.Definition;
            Vector2Int size = def.footprintSize;
            float height = Mathf.Max(1, def.heightLayers) * GridCoord.LayerUnits;

            if (IsSolidRectangle(def))
            {
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(size.x * GridCoord.StudUnits, height, size.y * GridCoord.StudUnits);
                box.center = new Vector3(0f, height * 0.5f, 0f);
                return;
            }

            // Local space: the mesh pivot sits at the footprint centre, base at zero.
            Vector3 corner = new Vector3(-size.x * 0.5f, 0f, -size.y * 0.5f) * GridCoord.StudUnits;

            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                if (!def.OccupiesCell(x, y))
                    continue;

                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(GridCoord.StudUnits, height, GridCoord.StudUnits);
                box.center = corner + new Vector3(
                    (x + 0.5f) * GridCoord.StudUnits,
                    height * 0.5f,
                    (y + 0.5f) * GridCoord.StudUnits);
            }
        }

        static bool IsSolidRectangle(PartDefinition def)
        {
            if (def.footprintMask == null || def.footprintMask.Length == 0)
                return true;

            foreach (bool occupied in def.footprintMask)
                if (!occupied)
                    return false;

            return true;
        }
    }
}
