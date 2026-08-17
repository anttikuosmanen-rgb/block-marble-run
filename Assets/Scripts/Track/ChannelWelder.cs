using System.Collections.Generic;
using BlockMarbleRun.Build;
using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Track
{
    /// <summary>
    /// Welds every joined run of channel into one collider.
    ///
    /// PhysX generates contacts per collider, and where two of them meet a ball can catch on the
    /// boundary edge itself: the contact normal comes from the edge rather than from the surface, so
    /// it points somewhere the surface never faces and takes the ball's forward motion with it. It is
    /// the same effect that catches characters on the seams of tiled floors.
    ///
    /// That explains why bridging the joints made things worse rather than better - a bridge does not
    /// remove a seam, it adds two more. The only fix that removes a seam is not having one, so a run
    /// of joined parts becomes a single mesh and the ball crosses what used to be a joint without
    /// anything changing underneath it.
    /// </summary>
    public sealed class ChannelWelder : MonoBehaviour
    {
        public BuildController controller;
        public Parts.PartFactory factory;

        /// <summary>
        /// Whether to weld at all. Welding itself only happens in play mode.
        ///
        /// A welded run has no per-part colliders, so nothing can be picked, painted, marked as a
        /// start or deleted - the raycast lands on one anonymous merged collider that belongs to no
        /// part. Building needs the parts to exist separately; only the ball needs them merged.
        /// </summary>
        public bool weldInPlay = true;

        bool _playing;

        /// <summary>Called on entering and leaving play mode.</summary>
        public void SetPlaying(bool playing)
        {
            _playing = playing;
            Rebuild();
        }

        bool ShouldWeld => _playing && weldInPlay;

        /// <summary>
        /// Ceiling on a welded mesh. Six slides is already 86k triangles, and cooking a collider is
        /// a recursive spatial build - handing it an unbounded mesh on a WebGL stack is exactly the
        /// shape of failure that was reported.
        /// </summary>
        public int maxTriangles = 120000;

        readonly List<GameObject> _welded = new();
        readonly List<Collider> _suppressed = new();

        int _lastVersion = -1;
        bool _wasWelding;

        public int Groups { get; private set; }
        public int WeldedParts { get; private set; }

        void Update()
        {
            if (controller?.Map == null)
                return;

            if (controller.Map.Version == _lastVersion && _wasWelding == ShouldWeld)
                return;

            _lastVersion = controller.Map.Version;
            _wasWelding = ShouldWeld;
            Rebuild();
        }

        public void Rebuild()
        {
            Clear();

            _wasWelding = ShouldWeld;

            if (!ShouldWeld || controller?.Map == null)
                return;

            foreach (List<PlacedPart> group in FindConnectedRuns(controller.Map))
            {
                // A lone part has no seam to remove, and welding it would only cost a second collider.
                if (group.Count < 2)
                    continue;

                Weld(group);
            }
        }

        void Clear()
        {
            foreach (GameObject go in _welded)
                Destroy(go);

            _welded.Clear();

            // Hand collision back to the parts themselves.
            foreach (Collider collider in _suppressed)
                if (collider != null)
                    collider.enabled = true;

            _suppressed.Clear();
            Groups = 0;
            WeldedParts = 0;
        }

        /// <summary>Groups of parts joined mouth to mouth, walked through the connection graph.</summary>
        static IEnumerable<List<PlacedPart>> FindConnectedRuns(GridMap map)
        {
            var seen = new HashSet<PlacedPart>();

            foreach (PlacedPart start in map.Parts)
            {
                if (!start.HasPorts || !seen.Add(start))
                    continue;

                var group = new List<PlacedPart> { start };
                var queue = new Queue<PlacedPart>();
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    PlacedPart current = queue.Dequeue();

                    foreach (PlacedPart.WorldPort port in current.WorldPorts())
                    {
                        PlacedPart other = map.FindConnection(current, port);
                        if (other == null || !other.HasPorts || !seen.Add(other))
                            continue;

                        group.Add(other);
                        queue.Enqueue(other);
                    }

                    // And whatever this piece is feeding.
                    //
                    // A funnel has no mouth on its perimeter to be joined to: its chute begins a stud
                    // inside its own footprint, where the stud shelf ends, so the piece that feeds it
                    // stands on top of it rather than beside it. Nothing in the port graph can reach
                    // it, and left out of the run it stays a collider of its own - which is precisely
                    // the seam this class exists to remove, at the one junction where a ball is
                    // slowest and least able to survive being caught.
                    //
                    // Not traversed onward: a funnel has no ports to follow, so it joins the group and
                    // the walk continues from the pieces that do.
                    foreach (PlacedPart fed in Feeding(map, current))
                        if (seen.Add(fed))
                            group.Add(fed);
                }

                yield return group;
            }
        }

        /// <summary>
        /// The parts this one stands on that take a channel feed - today, the funnels.
        ///
        /// Recognised by asking for a lift (PartDefinition.channelLipUnits), which is the same fact
        /// from the other side: a part whose channel is measured from its stud shelf is a part that
        /// something plugs onto and runs into.
        /// </summary>
        static IEnumerable<PlacedPart> Feeding(GridMap map, PlacedPart part)
        {
            var below = new HashSet<PlacedPart>();

            foreach (GridCoord cell in part.OccupiedCells())
            {
                if (cell.layer <= 0)
                    continue;

                PlacedPart under = map.At(new GridCoord(cell.x, cell.y, cell.layer - 1));

                if (under == null || under == part || under.Definition.channelLipUnits <= 0f)
                    continue;

                if (below.Add(under))
                    yield return under;
            }
        }

        void Weld(List<PlacedPart> group)
        {
            var combines = new List<CombineInstance>(group.Count);

            foreach (PlacedPart part in group)
            {
                Mesh mesh = part.Definition.mesh;
                if (mesh == null || part.Instance == null)
                    continue;

                // Combining reads the source geometry, so a mesh without a CPU copy cannot take part.
                // Leaving it out would silently drop a piece of the run's collision, so the whole weld
                // is abandoned instead and every part keeps its own collider.
                if (!mesh.isReadable)
                {
                    Debug.LogWarning($"[Weld] '{part.Definition.id}' has no readable mesh; leaving this " +
                                     "run unwelded.");

                    foreach (Collider collider in _suppressed)
                        if (collider != null)
                            collider.enabled = true;

                    _suppressed.Clear();
                    return;
                }

                // The part's own transform, which carries any lift the channel network gave it
                // (ChannelNetwork) - so a welded run is welded where it is drawn.
                combines.Add(new CombineInstance
                {
                    mesh = mesh,
                    transform = part.Instance.transform.localToWorldMatrix,
                });

                // Suppress rather than destroy: the part keeps its collider for when welding is turned
                // off again, and nothing else has to know this happened.
                // In children too: an offset part carries its colliders on one, and leaving those
                // live would have the run collided against twice, once at each height.
                foreach (Collider collider in part.Instance.GetComponentsInChildren<Collider>())
                {
                    if (!collider.enabled)
                        continue;

                    collider.enabled = false;
                    _suppressed.Add(collider);
                }
            }

            if (combines.Count < 2)
                return;

            long triangles = 0;
            foreach (CombineInstance instance in combines)
                triangles += instance.mesh.GetIndexCount(0) / 3;

            if (triangles > maxTriangles)
            {
                Debug.LogWarning($"[Weld] Skipping a run of {combines.Count} parts: {triangles} triangles " +
                                 $"exceeds the {maxTriangles} ceiling. Colliders left per-part.");

                // Hand the colliders straight back rather than leaving the run with none at all.
                foreach (Collider collider in _suppressed)
                    if (collider != null)
                        collider.enabled = true;

                _suppressed.Clear();
                return;
            }

            Mesh combined;

            try
            {
                combined = new Mesh
                {
                    name = $"WeldedChannel ({combines.Count})",
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                };

                combined.CombineMeshes(combines.ToArray(), mergeSubMeshes: true, useMatrices: true);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Weld] Could not combine {combines.Count} parts: {e.Message}");

                foreach (Collider collider in _suppressed)
                    if (collider != null)
                        collider.enabled = true;

                _suppressed.Clear();
                return;
            }

            Debug.Log($"[Weld] {combines.Count} parts into one collider, {triangles} triangles.");

            var go = new GameObject($"Welded channel x{combines.Count}");
            go.transform.SetParent(transform, false);

            var welded = go.AddComponent<MeshCollider>();
            welded.sharedMesh = combined;
            welded.convex = false;
            welded.sharedMaterial = factory != null ? factory.surfacePhysics : null;

            _welded.Add(go);
            Groups++;
            WeldedParts += combines.Count;
        }
    }
}
