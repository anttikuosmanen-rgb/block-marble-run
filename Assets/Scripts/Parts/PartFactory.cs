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

        [Tooltip("Surface properties for the track itself. Without one, parts use Unity's default 0.6 friction.")]
        public PhysicsMaterial surfacePhysics;

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

        void Awake()
        {
            BuildPaletteMaterials();
            BuildPillarFactory();
            FindScaffoldPlate();
        }

        [Tooltip("Modelled pillar that support columns of any height are cut from.")]
        public string pillarSourceId = "pillar_2x2x7";

        [Tooltip("Half-height brick, for the odd layer a whole brick cannot fill.")]
        public string plateId = "building_block_2x2_plate";

        /// <summary>
        /// Sets up the procedural pillars from whichever modelled pillar is in the catalog.
        ///
        /// Nothing fails if it is missing: the scaffolder falls back to bricks, which is what it did
        /// before there were pillars at all.
        /// </summary>
        void BuildPillarFactory()
        {
            if (catalog == null)
                return;

            foreach (PartDefinition def in catalog.parts)
            {
                if (def == null || def.id != pillarSourceId)
                    continue;

                ProceduralPillars.Active = new ProceduralPillars(def);
                return;
            }

            Debug.LogWarning($"[Pillars] No part named '{pillarSourceId}' in the catalog; support " +
                             "columns will be built from bricks.");
        }

        void FindScaffoldPlate()
        {
            if (catalog == null)
                return;

            foreach (PartDefinition def in catalog.parts)
                if (def != null && def.id == plateId)
                {
                    Grid.ScaffoldBuilder.Plate = def;
                    return;
                }

            Debug.LogWarning($"[Parts] No part named '{plateId}' in the catalog; support columns " +
                             "cannot fill an odd last layer.");
        }

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

            // Colliders first, before the MeshFilter exists.
            //
            // A MeshCollider added to an object that already has a MeshFilter adopts that filter's
            // mesh the moment it is created - before any sharedMesh of ours is assigned. For a brick
            // that means PhysX immediately tries to cook the render mesh, which is deliberately
            // GPU-only, and logs "CollisionMeshData couldn't be created" naming a mesh rather than a
            // part. The collider we then assign is correct, so nothing was ever broken - it simply
            // complained once per brick placed, which made it look like a fault in the scaffolding.
            if (withCollider)
                AddColliders(go, part);

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

            // Added after the filter, since it swaps in a mesh of its own to bend.
            if (part.Definition.soft && withCollider && part.Definition.mesh != null)
            {
                go.AddComponent<Play.SoftPart>().Configure(
                    part.Definition.mesh,
                    Mathf.Max(1, part.Definition.softBodyLayers) * GridCoord.LayerUnits);
            }

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
        /// Collision comes from the footprint for bricks and from the mesh for channels.
        ///
        /// DESIGN.md §3.3 originally ruled the render mesh out entirely, on the grounds that parts run
        /// to 22k triangles and a marble channel is concave. Both facts are true and neither is an
        /// obstacle once the numbers are split by category. A *static* mesh collider handles concave
        /// geometry natively - only convex ones need decomposition - and PhysX cooks the collision
        /// data once per unique mesh, not per instance, so a hundred track pieces cost one cooking.
        /// The parts that are actually heavy are the bricks (8k-22k triangles), and a brick is a box.
        /// The channels are the cheap ones: 2-5k triangles for the straight and curved track.
        ///
        /// Approximating a trough out of boxes was the alternative, and it would have been both more
        /// code and less accurate at exactly the thing the game is about - how a marble rolls.
        /// </summary>
        void AddColliders(GameObject go, PlacedPart part)
        {
            PartDefinition def = part.Definition;


            // A tunnelled part needs its real geometry too: a bridge modelled as a solid box
            // walls in the very ball it is meant to arch over.
            //
            // The readability check is not belt and braces: only channel and tunnel meshes keep a CPU
            // copy, and handing a collider one without it fails at runtime with an error naming a mesh
            // rather than a part. If this ever trips, the part's readable flag and its ports disagree,
            // and the generated collider below is a working answer either way.
            // A soft part is solid only at its base. Its stalks bend out of the way, and a collider
            // that stayed where the geometry used to be would stop a marble against something it can
            // see has moved aside.
            if (def.soft)
            {
                AddSoftBaseCollider(go, part);
                return;
            }

            if ((def.ports is { Length: > 0 } || def.hasTunnel) && def.mesh != null)
            {
                if (!def.mesh.isReadable)
                {
                    Debug.LogWarning($"[Parts] '{def.id}' wants a mesh collider but its mesh is not " +
                                     "readable; re-run Generate Part Definitions. Falling back.", def);
                }
                else
                {
                    var channel = go.AddComponent<MeshCollider>();
                    channel.sharedMesh = def.mesh;
                    channel.convex = false; // static, so concave is fine and the trough survives
                    channel.sharedMaterial = surfacePhysics;
                    return;
                }
            }

            if (def.topStuds is { Length: > 0 })
            {
                // Body plus studs, so a marble that leaves the track rides the bumps rather than a
                // flat lid. One shared mesh per part type, not per instance.
                var studded = go.AddComponent<MeshCollider>();
                studded.sharedMesh = BrickColliderBuilder.For(def);
                studded.convex = false;
                studded.sharedMaterial = surfacePhysics;
                return;
            }

            AddFootprintColliders(go, part);
        }

        /// <summary>
        /// Box colliders sized from the grid, for parts a marble only ever rolls across the top of.
        ///
        /// A solid rectangular footprint collapses to a single box. Anything else - u_turn's open
        /// middle, for instance - gets one box per occupied cell, so the hole stays a hole instead of
        /// becoming a wall the player cannot click through.
        /// </summary>
        /// <summary>
        /// A box around the solid base of a soft part, measured off the mesh.
        ///
        /// Not off the grid cells it occupies. A stalk's footprint is two studs across because the
        /// stalks splay, while the thing actually resting on the build is eight millimetres of stem
        /// underneath them - a collider the size of the footprint is a wall around a piece a marble
        /// is meant to brush past.
        /// </summary>
        void AddSoftBaseCollider(GameObject go, PlacedPart part)
        {
            PartDefinition def = part.Definition;
            float height = Mathf.Max(1, def.softBodyLayers) * GridCoord.LayerUnits;

            var box = go.AddComponent<BoxCollider>();
            box.sharedMaterial = surfacePhysics;

            if (def.mesh == null || !def.mesh.isReadable)
            {
                // Nothing to measure, so the footprint it is - wrong, but present.
                box.size = new Vector3(def.footprintSize.x * GridCoord.StudUnits, height,
                                       def.footprintSize.y * GridCoord.StudUnits);
                box.center = new Vector3(0f, height * 0.5f, 0f);
                return;
            }

            // As tall as the part says it is solid, and as wide as the mesh actually is down there.
            float lowest = def.mesh.bounds.min.y;
            float slice = lowest + height;

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            foreach (Vector3 v in def.mesh.vertices)
            {
                if (v.y > slice)
                    continue;

                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
            }

            if (minX == float.MaxValue)
            {
                minX = maxX = minZ = maxZ = 0f;
            }

            box.size = new Vector3(Mathf.Max(0.01f, maxX - minX), height, Mathf.Max(0.01f, maxZ - minZ));
            box.center = new Vector3((minX + maxX) * 0.5f, lowest + height * 0.5f, (minZ + maxZ) * 0.5f);
        }

        void AddFootprintColliders(GameObject go, PlacedPart part)
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
