using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Translucent preview of the part about to be placed, tinted by whether it can go there.
    ///
    /// One reused object rather than one per frame: the ghost updates every frame the cursor moves,
    /// and allocating a mesh renderer that often would churn the WebGL heap for no reason.
    /// </summary>
    public sealed class GhostPreview : MonoBehaviour
    {
        public Material ghostMaterial;

        [SerializeField] Color validTint = new Color(0.35f, 1f, 0.45f, 0.5f);
        [SerializeField] Color blockedTint = new Color(1f, 0.3f, 0.25f, 0.5f);

        [Tooltip("Unsupported parts are placeable but will need scaffolding in play mode (DESIGN.md §5.1).")]
        [SerializeField] Color unsupportedTint = new Color(1f, 0.75f, 0.2f, 0.5f);

        MeshFilter _filter;
        MeshRenderer _renderer;

        void Awake()
        {
            var go = new GameObject("Ghost");
            go.transform.SetParent(transform, false);

            _filter = go.AddComponent<MeshFilter>();
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = ghostMaterial;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            go.SetActive(false);
        }

        public void Show(PlacedPart part, PlacementResult result, Color partColor)
        {
            if (part?.Definition == null || part.Definition.mesh == null)
            {
                Hide();
                return;
            }

            _filter.sharedMesh = part.Definition.mesh;

            part.GetTransform(out Vector3 position, out Quaternion rotation);
            _filter.transform.SetPositionAndRotation(position, rotation);

            Color tint = result switch
            {
                PlacementResult.Valid => validTint,
                PlacementResult.Unsupported => unsupportedTint,
                _ => blockedTint,
            };

            // Keep a hint of the brick's own colour so the ghost still reads as the part being placed.
            Color blended = Color.Lerp(partColor, tint, 0.75f);
            blended.a = tint.a;

            PartFactory.ApplyColor(_renderer, blended);
            _filter.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_filter != null)
                _filter.gameObject.SetActive(false);
        }
    }
}
