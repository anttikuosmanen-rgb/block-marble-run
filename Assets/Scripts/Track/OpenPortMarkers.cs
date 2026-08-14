using System.Collections.Generic;
using BlockMarbleRun.Build;
using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Track
{
    /// <summary>
    /// Marks every channel mouth that leads nowhere (DESIGN.md §6).
    ///
    /// Shows the leaks rather than the joins. A glow on every connected seam lights up the entire run
    /// and draws the eye to what is already working; the open ends are the actionable information -
    /// they are exactly the places a marble will leave the track. Absence of a marker then means
    /// "connected", which needs no explanation.
    /// </summary>
    public sealed class OpenPortMarkers : MonoBehaviour
    {
        public BuildController controller;
        public Material markerMaterial;

        [Tooltip("Marker size in world units. One stud is 0.16.")]
        public float size = 0.11f;

        [SerializeField] Color openColour = new Color(1f, 0.55f, 0.15f);

        readonly List<GameObject> _pool = new();
        Mesh _mesh;
        int _lastVersion = -1;

        /// <summary>How many channel mouths currently lead nowhere.</summary>
        public int OpenCount { get; private set; }

        void Awake() => _mesh = BuildQuad();

        void Update()
        {
            if (controller == null || controller.Map == null)
                return;

            // Rebuild only when the build actually changed. Open mouths are re-derived by walking
            // every port, which is wasted work on a frame where nothing moved.
            if (controller.Map.Version != _lastVersion)
            {
                _lastVersion = controller.Map.Version;
                Rebuild(controller.Map);
            }

            Pulse();
        }

        void Rebuild(GridMap map)
        {
            int used = 0;

            foreach (PlacedPart part in map.Parts)
            {
                if (!part.HasPorts)
                    continue;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    if (map.FindConnection(part, port) != null)
                        continue;

                    GameObject marker = Take(used++);
                    marker.transform.SetPositionAndRotation(
                        // Lift clear of the channel floor so the marker is not buried in the trough.
                        port.WorldPosition + Vector3.up * (size * 0.5f),
                        Quaternion.LookRotation(Vector3.up, port.OutwardDirection));

                    marker.transform.localScale = Vector3.one * size;
                }
            }

            for (int i = used; i < _pool.Count; i++)
                _pool[i].SetActive(false);

            OpenCount = used;
        }

        /// <summary>
        /// Animates the shared material rather than each marker.
        ///
        /// A per-renderer property block would opt every marker out of batching, which is the cost
        /// DESIGN.md §5.2 measured; driving the one material makes all of them pulse together for
        /// free.
        /// </summary>
        void Pulse()
        {
            if (markerMaterial == null)
                return;

            float wave = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 4f);
            markerMaterial.SetColor("_BaseColor", openColour * wave);
        }

        GameObject Take(int index)
        {
            while (_pool.Count <= index)
            {
                var go = new GameObject("OpenPort");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = markerMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _pool.Add(go);
            }

            _pool[index].SetActive(true);
            return _pool[index];
        }

        static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "PortMarker" };

            mesh.SetVertices(new List<Vector3>
            {
                new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(0.5f, 0.5f, 0f), new(-0.5f, 0.5f, 0f),
            });

            mesh.SetTriangles(new List<int> { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
