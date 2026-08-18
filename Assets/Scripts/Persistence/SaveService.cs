using System;
using System.Collections.Generic;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Persistence
{
    /// <summary>
    /// Converts between the live grid and the stored model, and drives the store.
    /// </summary>
    public sealed class SaveService
    {
        readonly ISaveStore _store;
        readonly PartCatalog _catalog;

        public SaveService(ISaveStore store, PartCatalog catalog)
        {
            _store = store;
            _catalog = catalog;
        }

        public Awaitable InitialiseAsync() => _store.InitialiseAsync();
        public Awaitable<SaveSlot[]> ListAsync() => _store.ListAsync();
        public Awaitable DeleteAsync(string slot) => _store.DeleteAsync(slot);
        public Awaitable<byte[]> LoadThumbnailAsync(string slot) => _store.LoadThumbnailAsync(slot);

        public static SaveModel Capture(GridMap map, string name)
        {
            var parts = new List<SavedPart>(map.Parts.Count);

            foreach (PlacedPart part in map.Parts)
            {
                parts.Add(new SavedPart
                {
                    id = part.Definition.id,
                    x = part.Origin.x,
                    y = part.Origin.y,
                    layer = part.Origin.layer,
                    rot = part.Rotation,
                    color = part.ColorIndex,
                    role = (int)part.Role,
                });
            }

            BoundsInt bounds = map.OccupiedBounds;

            World.Scenery scenery = World.Scenery.Active;

            return new SaveModel
            {
                version = SaveModel.CurrentVersion,
                name = name,
                floorStyle = scenery != null ? (int)scenery.style : 0,
                waterLevel = scenery != null ? scenery.waterLevel : 0.12f,
                savedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                boundsMin = bounds.min,
                boundsMax = bounds.max,
                parts = parts.ToArray(),
            };
        }

        public async Awaitable SaveAsync(GridMap map, string name)
        {
            SaveModel model = Capture(map, name);
            await _store.SaveAsync(name, model.ToJson());
        }

        /// <summary>
        /// Rebuilds the grid from a stored model.
        ///
        /// Parts whose id is no longer in the catalog are skipped rather than treated as fatal: a
        /// creation that loses one retired piece is still worth opening, and refusing the whole file
        /// would throw away everything else the player built.
        /// </summary>
        public LoadReport Apply(SaveModel model, GridMap map, Func<PlacedPart, GameObject> spawn)
        {
            var report = new LoadReport();
            map.Clear();

            // Restored before the parts, so a build that ends in water is already standing in it by
            // the time the first piece appears rather than being flooded a frame later.
            World.Scenery scenery = World.Scenery.Active;
            if (scenery != null)
            {
                scenery.style = (World.FloorStyle)Mathf.Clamp(model.floorStyle, 0, 2);
                scenery.waterLevel = Mathf.Max(0f, model.waterLevel);
                scenery.Apply();
            }

            var byId = new Dictionary<string, PartDefinition>(_catalog.parts.Count);
            foreach (PartDefinition def in _catalog.parts)
                if (def != null && !string.IsNullOrEmpty(def.id))
                    byId[def.id] = def;

            foreach (SavedPart saved in model.parts)
            {
                // Generated pillars are not in the catalog - they are made on demand and named for
                // their height - so an id the catalog does not know is offered to the pillar factory
                // before being counted as a part this build no longer has.
                if (!byId.TryGetValue(saved.id, out PartDefinition def))
                    def = ProceduralPillars.Resolve(saved.id);

                if (def == null)
                {
                    report.UnknownParts.Add(saved.id);
                    continue;
                }

                var part = new PlacedPart(
                    def,
                    new GridCoord(saved.x, saved.y, saved.layer),
                    saved.rot,
                    (byte)saved.color,
                    (PartRole)saved.role);

                if (map.Add(part))
                {
                    part.Instance = spawn(part);
                    report.Loaded++;
                }
                else
                {
                    report.Rejected++;
                }
            }

            return report;
        }

        /// <summary>
        /// The creation as a file, handed to the player (DESIGN.md 8.2).
        ///
        /// Re-serialised from the loaded model rather than copied byte for byte out of the store: a
        /// save written by an older build is migrated on the way through, so what leaves is always in
        /// the current shape and can be bundled or loaded anywhere without a second migration.
        /// </summary>
        public async Awaitable<string> ExportAsync(string slot)
        {
            SaveModel model = await LoadAsync(slot);

            return model == null ? null : _transfer.Export(model.name ?? slot, model.ToJson());
        }

        /// <summary>The build as it stands, without saving it first.</summary>
        public string Export(GridMap map, string name) =>
            _transfer.Export(name, Capture(map, name).ToJson());

        readonly ICreationTransfer _transfer = CreationTransfer.Create();

        public async Awaitable<SaveModel> LoadAsync(string slot)
        {
            string json = await _store.LoadAsync(slot);
            return string.IsNullOrEmpty(json) ? null : SaveModel.FromJson(json);
        }

        public async Awaitable SaveThumbnailAsync(string slot, Camera camera, int size = 256)
        {
            byte[] png = CaptureThumbnail(camera, size);
            if (png != null)
                await _store.SaveThumbnailAsync(slot, png);
        }

        /// <summary>
        /// Renders the current view to an offscreen target. Uses an explicit RenderTexture rather
        /// than a screen grab so the thumbnail is a fixed size regardless of window dimensions, and
        /// so the HUD does not end up baked into it.
        /// </summary>
        public static byte[] CaptureThumbnail(Camera camera, int size)
        {
            if (camera == null)
                return null;

            RenderTexture target = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            var readback = new Texture2D(size, size, TextureFormat.RGB24, mipChain: false);

            try
            {
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                readback.Apply();

                return readback.EncodeToPNG();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.Destroy(readback);
            }
        }
    }

    public sealed class LoadReport
    {
        public int Loaded;
        public int Rejected;
        public readonly List<string> UnknownParts = new();

        public override string ToString()
        {
            string text = $"{Loaded} loaded";
            if (Rejected > 0) text += $", {Rejected} rejected";
            if (UnknownParts.Count > 0) text += $", {UnknownParts.Count} unknown part ids";
            return text;
        }
    }
}
