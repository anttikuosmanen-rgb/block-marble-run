using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// Bends a part out of the way of a passing marble, and gives the marble a nudge for it.
    ///
    /// Unity has no soft-body solver. A jointed chain would be the honest simulation, but it needs
    /// the stalks modelled as separate segments and costs a rigidbody each; this is the cheap half of
    /// the effect - the geometry leans away and the marble feels something brush it, which is what a
    /// player reads as "soft". Only the base of the part carries a collider, so the marble is never
    /// stopped by a stalk that has visibly moved aside.
    ///
    /// The mesh is deformed rather than shaded, because it is also what the icon and the ghost use,
    /// and a bend that lives in a shader would not show in either.
    /// </summary>
    public sealed class SoftPart : MonoBehaviour
    {
        [Tooltip("How far the tips lean, in world units, at full strength.")]
        public float give = 0.09f;

        [Tooltip("How close a marble has to be, in world units. Measured from the part when it loads.")]
        public float reach = 0.12f;

        [Tooltip("How hard the part pushes back on a marble passing through it.")]
        public float push = 0.35f;

        [Tooltip("How quickly it follows a marble, and springs back after one.")]
        public float follow = 14f;

        /// <summary>Height above the part's base where bending starts. Below this it is rigid.</summary>
        public float pivotHeight = 0.192f;

        Mesh _mesh;
        Vector3[] _rest;
        Vector3[] _work;
        float _top;

        Vector2 _bend;

        public void Configure(Mesh source, float bodyHeight)
        {
            if (source == null || !source.isReadable)
                return;

            pivotHeight = bodyHeight;

            // A copy per instance: two stalks side by side bend independently, and writing to the
            // shared asset would bend every one of them at once - and permanently.
            _mesh = Instantiate(source);
            _mesh.name = source.name + " (soft)";
            _mesh.MarkDynamic();

            _rest = _mesh.vertices;
            _work = new Vector3[_rest.Length];
            _top = _mesh.bounds.max.y;

            // The pivot comes from the part's own softBodyLayers, set per part. Finding it from the
            // shape was tried and is not reliable: these stalks attach low on the stem and splay
            // gradually, so the cross-section only widens near the top, and the reading came out at
            // 19 mm on a stem 8 mm tall. One number on the part beats a rule that has to be right
            // about every shape anyone imports.
            //
            // Reach is measured, though. It was a fixed distance from the stem, and a stalk
            // leaning two centimetres out of it was well past the edge - so a marble brushing the
            // tips counted as touching nothing.
            Vector3 extents = _mesh.bounds.extents;
            reach = Mathf.Max(extents.x, extents.z) + 0.06f;

            // Enough lean to be seen from across the table: a third of the free length of a stalk.
            give = Mathf.Max(0.02f, (_top - pivotHeight) * 0.35f);

            GetComponent<MeshFilter>().sharedMesh = _mesh;
        }

        void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }

        void Update()
        {
            if (_mesh == null)
                return;

            Vector2 wanted = Vector2.zero;

            // Everything close enough to be brushing the stalks. A handful of marbles at most, and
            // only while one is nearby, so the sweep costs nothing the rest of the time.
            Vector3 centre = transform.position + Vector3.up * ((_top + pivotHeight) * 0.5f);

            foreach (Collider hit in Physics.OverlapSphere(centre, reach + _top))
            {
                var marble = hit.GetComponentInParent<Marble>();
                if (marble == null)
                    continue;

                Vector3 offset = marble.transform.position - transform.position;

                // The ball's top, not its centre. A marble rolling past a low piece has its middle
                // below the stalks and its shoulder among them, and testing the centre meant most
                // marbles went by as though nothing was there.
                float radius = marble.Definition != null ? marble.Definition.RadiusUnits : 0.1225f;

                if (offset.y + radius < pivotHeight)
                    continue;   // wholly below the stalks, where the part is solid anyway

                if (offset.y > _top + radius)
                    continue;   // over the top of them

                var flat = new Vector2(offset.x, offset.z);
                float distance = flat.magnitude;

                if (distance > reach + radius)
                    continue;

                float strength = Mathf.Clamp01(1f - (distance - radius) / Mathf.Max(0.01f, reach));
                Vector2 away = distance > 1e-4f ? flat / distance : Vector2.right;

                wanted += away * strength;

                // And the marble feels it. Small and against its travel, so a stalk slows a ball and
                // turns it a little rather than batting it away.
                if (marble.Body != null)
                    marble.Body.AddForce(
                        new Vector3(away.x, 0f, away.y) * (push * strength) -
                        marble.Body.linearVelocity * (push * strength),
                        ForceMode.Acceleration);
            }

            wanted = Vector2.ClampMagnitude(wanted, 1f);

            _bend = Vector2.Lerp(_bend, wanted, 1f - Mathf.Exp(-follow * Time.deltaTime));

            if (_bend.sqrMagnitude < 1e-6f && wanted.sqrMagnitude < 1e-6f)
                return;

            Deform();
        }

        /// <summary>
        /// Leans everything above the pivot, by the square of its height above it.
        ///
        /// Squared rather than straight, so the base of a stalk stays put and the tip travels - which
        /// is how a thing anchored at one end bends. A straight ramp shears it instead.
        /// </summary>
        void Deform()
        {
            float span = Mathf.Max(0.001f, _top - pivotHeight);
            var lean = new Vector3(_bend.x, 0f, _bend.y) * give;

            for (int i = 0; i < _rest.Length; i++)
            {
                Vector3 v = _rest[i];

                if (v.y <= pivotHeight)
                {
                    _work[i] = v;
                    continue;
                }

                float t = Mathf.Clamp01((v.y - pivotHeight) / span);
                _work[i] = v + lean * (t * t);
            }

            _mesh.SetVertices(_work);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }
    }
}
