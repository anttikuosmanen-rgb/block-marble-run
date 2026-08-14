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
                    Spawn(point + Vector3.up * DropClearance);
            }

            Sweep();
        }

        /// <summary>Drops one marble just above each piece marked as a start.</summary>
        public void ReleaseFromStarts()
        {
            int released = 0;

            foreach (PlacedPart part in build.Map.Parts)
            {
                if (part.Role != PartRole.Start)
                    continue;

                foreach (PlacedPart.WorldPort port in part.WorldPorts())
                {
                    // Just inside the mouth and a little above its floor, so the marble drops into
                    // the channel rather than being born inside the geometry.
                    Vector3 inward = -port.OutwardDirection * (GridCoord.StudUnits * 0.5f);
                    Spawn(port.WorldPosition + inward + Vector3.up * DropClearance);
                    released++;
                    break;
                }
            }

            Status = released > 0
                ? $"Released {released}"
                : "No start marked - point at a dead-end piece in build mode and press X";
        }

        /// <summary>Enough to clear the surface without dropping the ball from a height.</summary>
        const float DropClearance = 0.05f;

        public string Status { get; private set; } = "";

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
                frictionCombine = PhysicsMaterialCombine.Multiply,
                bounceCombine = PhysicsMaterialCombine.Maximum,
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
        bool ReachedGoal(Marble marble)
        {
            const float reach = GridCoord.StudUnits; // one stud

            foreach (PlacedPart part in build.Map.Parts)
            {
                if (part.Role != PartRole.Goal)
                    continue;

                part.GetTransform(out Vector3 centre, out _);
                centre.y += GridCoord.LayerUnits * 0.5f;

                if ((marble.transform.position - centre).sqrMagnitude <= reach * reach)
                    return true;
            }

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
