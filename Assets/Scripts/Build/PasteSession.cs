using System.Collections.Generic;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// A copied group held under the cursor until the player says where it goes.
    ///
    /// Pasting used to land the copy immediately and leave the player to drag it afterwards, which
    /// meant every paste was two edits in the history and the first one was always wrong. Holding it
    /// first makes the position part of the paste rather than something to correct after the fact.
    ///
    /// Previews are real part objects without colliders, not the single-mesh ghost: a group can be
    /// any number of pieces, and one ghost cannot show a shape made of twelve.
    /// </summary>
    public sealed class PasteSession
    {
        readonly PartFactory _factory;
        readonly Transform _root;
        readonly Material _ghostMaterial;

        readonly List<GameObject> _previews = new();

        public PasteSession(PartFactory factory, Transform root, Material ghostMaterial)
        {
            _factory = factory;
            _root = root;
            _ghostMaterial = ghostMaterial;
        }

        /// <summary>The copies as they currently stand. Empty when nothing is being pasted.</summary>
        public List<PlacedPart> Parts { get; private set; } = new();

        public bool Active => Parts.Count > 0;
        public bool Fits { get; private set; }

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public void Begin(List<PlacedPart> copies)
        {
            Cancel();

            Parts = copies;
            Rebuild();
        }

        /// <summary>
        /// Rebuilds the preview objects.
        ///
        /// Called after a turn or a mirror as well as at the start, because both replace every part
        /// in the group - a mirrored curve is a different definition, so its preview cannot simply be
        /// moved to a new place.
        /// </summary>
        public void Rebuild()
        {
            Release();

            foreach (PlacedPart part in Parts)
            {
                GameObject go = _factory.Create(part, _root, withCollider: false);

                foreach (MeshRenderer renderer in go.GetComponentsInChildren<MeshRenderer>())
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }

                _previews.Add(go);
            }
        }

        /// <summary>Moves the held group so its footprint is centred on a cell, and re-checks it.</summary>
        public void MoveTo(GridMap map, GridCoord cell)
        {
            if (!Active)
                return;

            RectInt box = SelectionOps.Footprint(Parts);

            int dx = cell.x - box.xMin - box.width / 2;
            int dy = cell.y - box.yMin - box.height / 2;

            if (dx != 0 || dy != 0)
                SelectionOps.Translate(Parts, dx, dy, 0);

            Refresh(map);
        }

        public void Nudge(GridMap map, int dx, int dy, int dLayer)
        {
            if (!Active)
                return;

            // Never below the ground: the group would be legal nowhere and the preview would vanish
            // into the floor with no way to tell why it had stopped responding.
            RectInt _ = SelectionOps.Footprint(Parts);
            int lowest = int.MaxValue;

            foreach (PlacedPart part in Parts)
                lowest = Mathf.Min(lowest, part.Origin.layer);

            if (lowest + dLayer < 0)
                dLayer = 0;

            SelectionOps.Translate(Parts, dx, dy, dLayer);
            Refresh(map);
        }

        /// <summary>Re-aims the previews at wherever the parts now are, and tints them by validity.</summary>
        public void Refresh(GridMap map)
        {
            Fits = SelectionOps.CanPlaceAll(map, Parts);

            for (int i = 0; i < Parts.Count && i < _previews.Count; i++)
            {
                if (_previews[i] == null)
                    continue;

                Parts[i].GetTransform(out Vector3 position, out Quaternion rotation);
                _previews[i].transform.SetPositionAndRotation(position, rotation);
            }

            Tint(Fits ? new Color(0.45f, 1f, 0.5f) : new Color(1f, 0.4f, 0.35f));
        }

        /// <summary>
        /// Washes the whole group one colour so it reads as held rather than built.
        ///
        /// A property block, not a material swap: this is one group for a few seconds, and swapping
        /// materials here would mean a second set of tinted materials for every palette colour that
        /// only ever exists while something is being pasted.
        /// </summary>
        void Tint(Color colour)
        {
            var block = new MaterialPropertyBlock();

            foreach (GameObject preview in _previews)
            {
                if (preview == null)
                    continue;

                foreach (MeshRenderer renderer in preview.GetComponentsInChildren<MeshRenderer>())
                {
                    renderer.GetPropertyBlock(block);
                    block.SetColor(BaseColor, colour);
                    renderer.SetPropertyBlock(block);
                }
            }
        }

        /// <summary>Hands the parts over to be committed, and ends the session.</summary>
        public List<PlacedPart> Take()
        {
            List<PlacedPart> parts = Parts;

            Parts = new List<PlacedPart>();
            Release();

            return parts;
        }

        public void Cancel()
        {
            Parts = new List<PlacedPart>();
            Release();
        }

        void Release()
        {
            foreach (GameObject preview in _previews)
                if (preview != null)
                    Object.Destroy(preview);

            _previews.Clear();
        }
    }
}
