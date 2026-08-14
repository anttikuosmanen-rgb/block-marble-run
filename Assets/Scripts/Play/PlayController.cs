using System.Collections.Generic;
using BlockMarbleRun.Build;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// Play mode: release marbles and watch them run. See DESIGN.md §7.
    /// </summary>
    public sealed class PlayController : MonoBehaviour
    {
        public BuildController build;
        public World.BuildRaycaster raycaster;
        public Material marbleMaterial;

        [Tooltip("Ball types the player can cycle through. The first is the default.")]
        public List<MarbleDefinition> marbleTypes = new();

        [Tooltip("Concurrent marbles. WebGL runs physics on one thread, so this is a real budget (DESIGN.md §0.1).")]
        public int maxMarbles = 16;

        [Tooltip("Below this height a marble has left the build and is despawned.")]
        public float killHeight = -1f;

        [Tooltip("Nudge given to a ball leaving a start, in world units per second. 1 unit is 10 cm.")]
        public float releaseSpeed = 1.1f;

        readonly List<Marble> _marbles = new();
        readonly Dictionary<MarbleDefinition, PhysicsMaterial> _physics = new();
        readonly Dictionary<MarbleDefinition, Material> _renderMaterials = new();

        Mesh _sphereMesh;
        int _typeIndex;

        public MarbleDefinition CurrentType =>
            marbleTypes.Count == 0 ? null : marbleTypes[_typeIndex % marbleTypes.Count];

        public bool Active { get; private set; }
        public int Released { get; private set; }
        public int Finished { get; private set; }
        public int Lost { get; private set; }
        public float BestSeconds { get; private set; } = float.PositiveInfinity;

        /// <summary>Fastest ball in play right now, in world units per second.</summary>
        public float FastestSpeed { get; private set; }

        /// <summary>Fastest any ball has gone this run.</summary>
        public float PeakSpeed { get; private set; }

        /// <summary>
        /// How many layers the fastest ball could climb on the speed it has, if nothing were lost.
        ///
        /// The number that actually settles an argument about a stalling run. A rolling sphere holds
        /// 7/10 m v² between travel and spin, so this is that energy expressed as height - and
        /// comparing it against the rise the ball failed to clear says whether the problem is energy
        /// or geometry. Arriving with three layers in hand and stopping at a one-layer ramp is not a
        /// physics-tuning problem.
        /// </summary>
        public float ClimbableLayers
        {
            get
            {
                float g = Mathf.Abs(Physics.gravity.y);
                if (g <= 0f)
                    return 0f;

                float height = 7f * FastestSpeed * FastestSpeed / (20f * g);
                return height / GridCoord.LayerUnits;
            }
        }

        /// <summary>Metres per second, for a figure that means something outside the project's scale.</summary>
        public static float ToMetresPerSecond(float unitsPerSecond) => unitsPerSecond * 0.1f;
        public int Alive => _marbles.Count;

        void Awake() => _sphereMesh = BuildSphereMesh();

        public void SetActive(bool active)
        {
            Active = active;

            if (!active)
                Reset();
        }

        void Update()
        {
            if (!Active)
                return;

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            if (keyboard != null)
            {
                if (keyboard.spaceKey.wasPressedThisFrame)
                    ReleaseFromStarts();

                if (keyboard.rKey.wasPressedThisFrame)
                    Reset();

                if (keyboard.mKey.wasPressedThisFrame && marbleTypes.Count > 0)
                    _typeIndex = (_typeIndex + 1) % marbleTypes.Count;
            }

            // Free drop: put a marble wherever the player points, so a run can be tested from the
            // middle without first building a start.
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                if (raycaster.RaycastPoint(mouse.position.ReadValue(), out Vector3 point))
                {
                    float radius = CurrentType != null ? CurrentType.RadiusUnits : 0.12f;
                    Spawn(point + Vector3.up * (radius + Clearance));
                }
            }

            Sweep();
            MeasureSpeeds();
        }

        /// <summary>Drops one marble just above each piece marked as a start.</summary>
        public void ReleaseFromStarts()
        {
            int released = 0;

            if (!HasStart())
            {
                (PlacedPart part, PlacedPart.WorldPort port) = HighestOpenMouth();
                if (part != null)
                {
                    Marble ball = Spawn(StartPosition(part, port));
                    if (ball != null)
                        Nudge(ball, DepartureDirection(part, port));

                    Status = "Released from the highest open channel (no start marked)";
                    return;
                }
            }

            foreach (PlacedPart part in build.Map.Parts)
            {
                if (part.Role != PartRole.Start)
                    continue;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    Marble marble = Spawn(StartPosition(part, port));

                    if (marble != null)
                        Nudge(marble, DepartureDirection(part, port));

                    released++;
                    break;
                }
            }

            Status = released > 0
                ? $"Released {released}"
                : "No start marked - point at a dead-end piece in build mode and press X";
        }

        /// <summary>
        /// Sends a ball out of the start mouth with a small push, varied slightly each time.
        ///
        /// Without it a ball dropped into a level start just sits there, since nothing is pushing it
        /// anywhere. The variation matters as much as the push: identical releases down identical
        /// track produce identical runs, and a marble run that plays out the same way twice stops
        /// being worth watching.
        /// </summary>
        void Nudge(Marble marble, Vector3 direction)
        {
            Vector3 sideways = Vector3.Cross(Vector3.up, direction);

            Vector3 push =
                direction * Random.Range(0.85f, 1.15f) +
                sideways * Random.Range(-0.10f, 0.10f);

            marble.Body.linearVelocity = push.normalized * releaseSpeed;
        }

        /// <summary>
        /// Which way a ball should leave the start.
        ///
        /// Taken from the piece it is joined to rather than from the mouth's own facing. The two
        /// agree when everything is the right way round, so a disagreement means some part of the
        /// facing chain is inverted - and aiming at the neighbour is self-correcting either way,
        /// because the neighbour is where the run demonstrably continues.
        /// </summary>
        Vector3 DepartureDirection(PlacedPart start, PlacedPart.WorldPort port)
        {
            PlacedPart neighbour = build.Map.FindConnection(start, port);
            if (neighbour == null)
                return port.OutwardDirection;

            start.GetTransform(out Vector3 from, out _);
            neighbour.GetTransform(out Vector3 to, out _);

            Vector3 along = to - from;
            along.y = 0f;

            return along.sqrMagnitude > 1e-6f ? along.normalized : port.OutwardDirection;
        }

        /// <summary>
        /// Where a ball begins its run: over the middle of the start piece, resting on its channel.
        ///
        /// Both parts of this were wrong and both looked like the ball being pushed the wrong way.
        /// It was placed half a stud inside the mouth, which is less than a 24.5 mm ball's radius, so
        /// the ball hung out through the opening; and it was raised five millimetres above the
        /// channel floor when it needed a whole radius, so it began buried in the geometry. PhysX
        /// resolves that overlap by shoving the ball out along whichever contact is deepest - a hard
        /// push in an arbitrary direction, before the nudge has any say.
        /// </summary>
        Vector3 StartPosition(PlacedPart part, PlacedPart.WorldPort port)
        {
            float radius = CurrentType != null ? CurrentType.RadiusUnits : 0.12f;

            // Horizontally over the part's centre, which is clear of every wall on a dead end.
            part.GetTransform(out Vector3 centre, out _);

            return new Vector3(
                centre.x,
                port.HeightUnits + radius + Clearance,
                centre.z);
        }

        /// <summary>A hair above resting, so the ball settles rather than starting in contact.</summary>
        const float Clearance = 0.004f;

        public string Status { get; private set; } = "";

        /// <summary>
        /// Releases a single ball from the first start piece, for repeated measurement.
        ///
        /// Separate from the ordinary release, which drops one at every start: a test needs exactly
        /// one ball so the figures belong to one run rather than to whichever ball happened to be
        /// fastest.
        /// </summary>
        public Marble ReleaseOne()
        {
            foreach (PlacedPart part in build.Map.Parts)
            {
                if (part.Role != PartRole.Start)
                    continue;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    Marble marble = Spawn(StartPosition(part, port));

                    if (marble != null)
                        Nudge(marble, DepartureDirection(part, port));

                    return marble;
                }
            }

            (PlacedPart top, PlacedPart.WorldPort mouth) = HighestOpenMouth();
            if (top == null)
                return null;

            Marble ball = Spawn(StartPosition(top, mouth));
            if (ball != null)
                Nudge(ball, DepartureDirection(top, mouth));

            return ball;
        }

        bool HasStart()
        {
            foreach (PlacedPart part in build.Map.Parts)
                if (part.Role == PartRole.Start)
                    return true;

            return false;
        }

        /// <summary>
        /// The highest channel mouth that leads nowhere, which is where a run begins if nobody has
        /// said otherwise.
        ///
        /// Marking a start is a thing to remember before every test, and forgetting it produced a
        /// ball dropped at the world origin and a confusing message. The top of the track is almost
        /// always what was meant, and it costs nothing to work out.
        /// </summary>
        (PlacedPart, PlacedPart.WorldPort) HighestOpenMouth()
        {
            PlacedPart best = null;
            PlacedPart.WorldPort bestPort = default;
            float highest = float.NegativeInfinity;

            foreach (PlacedPart part in build.Map.Parts)
            {
                if (!part.HasPorts)
                    continue;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    if (port.HeightUnits <= highest)
                        continue;

                    // Open only: a joined mouth has a run continuing through it, not starting there.
                    if (build.Map.FindConnection(part, port) != null)
                        continue;

                    highest = port.HeightUnits;
                    best = part;
                    bestPort = port;
                }
            }

            return (best, bestPort);
        }

        public Marble Spawn(Vector3 position)
        {
            MarbleDefinition type = CurrentType;
            if (type == null)
                return null;

            // Oldest goes first, so holding the button never grows the simulation without bound.
            if (_marbles.Count >= maxMarbles)
                Despawn(_marbles[0]);

            var go = new GameObject($"Marble ({type.displayName})");
            go.transform.SetParent(transform, false);

            // Placed before the Rigidbody exists. An interpolated body renders from its own recorded
            // pose, and a body created at the origin keeps rendering there until a physics step
            // catches up - so a position written after the fact is simply not seen, and every ball
            // appears at the origin no matter where it was asked for.
            go.transform.position = position;

            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = RenderMaterialFor(type);

            var marble = go.AddComponent<Marble>();
            marble.Configure(type, PhysicsFor(type));
            marble.Launch(position);

            _marbles.Add(marble);
            Released++;
            return marble;
        }

        /// <summary>
        /// One physics material and one render material per ball type, built once. Both are shared by
        /// every ball of that type, which keeps them batched and avoids allocating a material per
        /// marble spawned.
        /// </summary>
        PhysicsMaterial PhysicsFor(MarbleDefinition type)
        {
            if (_physics.TryGetValue(type, out PhysicsMaterial existing) && existing != null)
                return existing;

            var material = new PhysicsMaterial($"{type.displayName} ball")
            {
                dynamicFriction = type.dynamicFriction,
                staticFriction = type.staticFriction,
                bounciness = type.bounciness,
                frictionCombine = PhysicsMaterialCombine.Average,
                bounceCombine = PhysicsMaterialCombine.Average,
            };

            _physics[type] = material;
            return material;
        }

        Material RenderMaterialFor(MarbleDefinition type)
        {
            if (_renderMaterials.TryGetValue(type, out Material existing) && existing != null)
                return existing;

            var material = new Material(marbleMaterial) { name = $"{type.displayName} ball" };
            material.SetColor("_BaseColor", type.colour);
            material.SetFloat("_Smoothness", type.smoothness);
            material.SetFloat("_Metallic", type.metallic);

            _renderMaterials[type] = material;
            return material;
        }

        /// <summary>
        /// Share of the drop the fastest ball still has in hand, as a percentage.
        ///
        /// The number that says whether the run is losing energy at all. A ball six slides down has
        /// fallen six layers; if it can still climb five, the track is nearly lossless, and if it can
        /// climb half of one then something is taking almost all of it. Speed alone cannot say that,
        /// because a slow ball near the top and a slow ball at the bottom mean opposite things.
        /// </summary>
        public float EfficiencyPercent { get; private set; }

        /// <summary>Contacts per second on the fastest ball: rolling registers few, clattering many.</summary>
        public float ContactRate { get; private set; }

        /// <summary>
        /// Pushes the current ball type's values onto its shared material and every ball already in
        /// play, so a change is felt immediately rather than on the next release.
        /// </summary>
        public void RefreshPhysics()
        {
            MarbleDefinition type = CurrentType;
            if (type == null)
                return;

            if (_physics.TryGetValue(type, out PhysicsMaterial material) && material != null)
            {
                material.dynamicFriction = type.dynamicFriction;
                material.staticFriction = type.staticFriction;
                material.bounciness = type.bounciness;
            }

            foreach (Marble marble in _marbles)
                if (marble != null && marble.Definition == type)
                    marble.ApplyTunables();
        }

        void MeasureSpeeds()
        {
            float fastest = 0f;
            Marble leader = null;

            foreach (Marble marble in _marbles)
            {
                if (marble == null)
                    continue;

                float speed = marble.Body.linearVelocity.magnitude;
                if (speed < fastest && leader != null)
                    continue;

                fastest = Mathf.Max(fastest, speed);
                leader = marble;
            }

            FastestSpeed = fastest;
            PeakSpeed = Mathf.Max(PeakSpeed, fastest);

            if (leader == null)
            {
                EfficiencyPercent = 0f;
                ContactRate = 0f;
                return;
            }

            ContactRate = leader.ContactsPerSecond;

            float dropped = (leader.PeakHeight - leader.transform.position.y) / GridCoord.LayerUnits;
            EfficiencyPercent = dropped > 0.1f ? 100f * ClimbableLayers / dropped : 0f;
        }

        /// <summary>Retires marbles that reached a goal or fell out of the world.</summary>
        void Sweep()
        {
            for (int i = _marbles.Count - 1; i >= 0; i--)
            {
                Marble marble = _marbles[i];
                if (marble == null)
                {
                    _marbles.RemoveAt(i);
                    continue;
                }

                if (marble.transform.position.y < killHeight)
                {
                    Lost++;
                    Despawn(marble);
                    continue;
                }

                if (!ReachedGoal(marble))
                    continue;

                Finished++;
                BestSeconds = Mathf.Min(BestSeconds, marble.Age);
                Despawn(marble);
            }
        }

        /// <summary>
        /// Goals are tested by proximity rather than trigger colliders. The goal piece already has a
        /// solid mesh collider for the marble to roll into, and adding an overlapping trigger to the
        /// same object invites the two to disagree about what counts as arrival.
        /// </summary>
        readonly List<Vector3> _goalCentres = new();
        int _goalsVersion = -1;

        bool ReachedGoal(Marble marble)
        {
            const float reach = GridCoord.StudUnits; // one stud

            // Goal positions are rebuilt only when the build changes. Walking every part for every
            // ball on every frame is work that grows with the size of the creation while answering
            // the same question each time.
            if (_goalsVersion != build.Map.Version)
            {
                _goalsVersion = build.Map.Version;
                _goalCentres.Clear();

                foreach (PlacedPart part in build.Map.Parts)
                {
                    if (part.Role != PartRole.Goal)
                        continue;

                    part.GetTransform(out Vector3 centre, out _);
                    centre.y += GridCoord.LayerUnits * 0.5f;
                    _goalCentres.Add(centre);
                }
            }

            foreach (Vector3 centre in _goalCentres)
                if ((marble.transform.position - centre).sqrMagnitude <= reach * reach)
                    return true;

            return false;
        }

        void Despawn(Marble marble)
        {
            _marbles.Remove(marble);

            if (marble != null)
                Destroy(marble.gameObject);
        }

        public void Reset()
        {
            for (int i = _marbles.Count - 1; i >= 0; i--)
                Despawn(_marbles[i]);

            Released = 0;
            Finished = 0;
            PeakSpeed = 0f;
            FastestSpeed = 0f;
            Lost = 0;
            BestSeconds = float.PositiveInfinity;
        }

        /// <summary>
        /// A low-detail UV sphere, built rather than shipped. The marble is a few pixels across most
        /// of the time and there is no sphere primitive to hand without pulling in Unity's default
        /// assets.
        /// </summary>
        static Mesh BuildSphereMesh(int rings = 12, int segments = 16)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();

            for (int ring = 0; ring <= rings; ring++)
            {
                float v = ring / (float)rings;
                float phi = v * Mathf.PI;

                for (int seg = 0; seg <= segments; seg++)
                {
                    float u = seg / (float)segments;
                    float theta = u * Mathf.PI * 2f;

                    var point = new Vector3(
                        Mathf.Sin(phi) * Mathf.Cos(theta),
                        Mathf.Cos(phi),
                        Mathf.Sin(phi) * Mathf.Sin(theta)) * 0.5f;

                    vertices.Add(point);
                    normals.Add(point.normalized);
                }
            }

            int stride = segments + 1;
            for (int ring = 0; ring < rings; ring++)
            for (int seg = 0; seg < segments; seg++)
            {
                int a = ring * stride + seg;
                int b = a + stride;

                triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
            }

            var mesh = new Mesh { name = "Marble" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
