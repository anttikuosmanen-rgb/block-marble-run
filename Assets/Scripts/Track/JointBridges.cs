using System.Collections.Generic;
using BlockMarbleRun.Build;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Track
{
    /// <summary>
    /// Carries the channel across the gap between two joined parts.
    ///
    /// Every Duplo part measures n*16 - 0.2 mm, so two neighbours never quite touch. A ball crossing
    /// a joint loses support for that fifth of a millimetre, drops fractionally, and strikes the
    /// leading triangle edge of the next part. What it costs depends on where the ball happens to be
    /// in that drop when it arrives, which is why the loss varied so much from run to run.
    ///
    /// The bridge is the mouth's own cross-section extruded across the joint. A flat slab at channel
    /// height would never be touched: the ball rides on the groove walls, not on its floor.
    /// </summary>
    public sealed class JointBridges : MonoBehaviour
    {
        public BuildController controller;
        public PartFactory factory;

        /// <summary>
        /// Half-length of the bridge, in world units. 0.4 mm either side of a 0.2 mm gap.
        ///
        /// The first attempt used 12 mm, which is not a bridge over a gap but a plateau laid across
        /// twelve millimetres of channel on each side. Combined with any lift at all, the ball climbed
        /// onto it and dropped off the far end - two steps where there had been one small gap, and
        /// markedly worse than leaving it alone.
        /// </summary>
        public float reach = 0.004f;

        /// <summary>
        /// Vertical offset. Zero, and never positive.
        ///
        /// Anything proud of the surrounding channel is a bump the ball has to climb, which is the
        /// opposite of the intent. The bridge is a floor under the gap, not a ramp over it.
        /// </summary>
        public float lift;

        /// <summary>
        /// Off by default. Bridging a joint does not remove the seam that causes the trouble - it
        /// replaces one seam with two, and measurably made the ball worse. Kept only for comparison.
        /// </summary>
        public bool enabled_;

        readonly List<GameObject> _bridges = new();
        int _lastVersion = -1;
        bool _wasEnabled;

        public int Count { get; private set; }

        void Update()
        {
            if (controller?.Map == null)
                return;

            if (controller.Map.Version == _lastVersion && _wasEnabled == enabled_)
                return;

            _lastVersion = controller.Map.Version;
            _wasEnabled = enabled_;
            Rebuild(controller.Map);
        }

        /// <summary>Rebuilds with the current settings, for the tuning panel.</summary>
        public void Rebuild()
        {
            if (controller?.Map != null)
                Rebuild(controller.Map);
        }

        void Rebuild(GridMap map)
        {
            foreach (GameObject bridge in _bridges)
                Destroy(bridge);

            _bridges.Clear();
            Count = 0;

            if (!enabled_)
                return;

            // Each joint is found from both sides; the midline and height identify it uniquely.
            var built = new HashSet<(Vector2Int, int)>();

            foreach (PlacedPart part in map.Parts)
            {
                if (!part.HasPorts)
                    continue;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    if (map.FindConnection(part, port) == null)
                        continue;

                    var id = (port.MidlineHalfStuds, Mathf.RoundToInt(port.HeightUnits * 10000f));
                    if (!built.Add(id))
                        continue;

                    GameObject bridge = Build(part, port);
                    if (bridge != null)
                        _bridges.Add(bridge);
                }
            }

            Count = _bridges.Count;
        }

        GameObject Build(PlacedPart part, PlacedPart.WorldPort port)
        {
            float[] profile = FindProfile(part, port);
            if (profile == null || profile.Length < 2)
                return null;

            var go = new GameObject("Joint");
            go.transform.SetParent(transform, false);

            var mesh = new Mesh { name = "JointBridge" };

            // Across the mouth, and along the direction of travel.
            Vector3 outward = port.OutwardDirection;
            Vector3 sideways = Vector3.Cross(Vector3.up, outward).normalized;

            Vector3 centre = port.WorldPosition;
            float width = port.WidthStuds * GridCoord.StudUnits;

            var vertices = new List<Vector3>(profile.Length * 2);
            var triangles = new List<int>((profile.Length - 1) * 6);

            // The port's height already places the channel floor in the world, so the profile is used
            // only for how far each sample rises above that floor.
            float floor = ChannelFloor(profile);

            for (int i = 0; i < profile.Length; i++)
            {
                float across = (i / (float)(profile.Length - 1) - 0.5f) * width;

                float height = (profile[i] - floor) * PlacedPart.MmToUnits;

                Vector3 mid = centre + sideways * across + Vector3.up * (height + lift);

                vertices.Add(mid - outward * reach);
                vertices.Add(mid + outward * reach);
            }

            for (int i = 0; i < profile.Length - 1; i++)
            {
                int a = i * 2;

                triangles.Add(a); triangles.Add(a + 1); triangles.Add(a + 2);
                triangles.Add(a + 2); triangles.Add(a + 1); triangles.Add(a + 3);
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = false;
            collider.sharedMaterial = factory != null ? factory.surfacePhysics : null;

            return go;
        }

        /// <summary>
        /// The lowest point of the profile, which is the channel floor the port's height refers to.
        /// Everything else in the profile is measured relative to it.
        /// </summary>
        static float ChannelFloor(float[] profile)
        {
            float lowest = float.PositiveInfinity;
            foreach (float value in profile)
                lowest = Mathf.Min(lowest, value);

            return lowest;
        }

        /// <summary>Finds the definition-space port matching this world port, for its profile.</summary>
        static float[] FindProfile(PlacedPart part, PlacedPart.WorldPort port)
        {
            TrackPort[] ports = part.Definition.ports;
            if (ports == null)
                return null;

            int index = 0;
            foreach (PlacedPart.WorldPort candidate in part.WorldPorts())
            {
                if (candidate.MidlineHalfStuds == port.MidlineHalfStuds && candidate.Facing == port.Facing)
                    return index < ports.Length ? ports[index].profileMm : null;

                index++;
            }

            return null;
        }
    }
}
