using BlockMarbleRun.Build;
using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Track
{
    /// <summary>
    /// Keeps every part standing at the height its channel run needs (see <see cref="ChannelNetwork"/>).
    ///
    /// Watches the map's version rather than listening for edits, like the welder does: every path
    /// that changes a build bumps it - placing, deleting, pasting, undoing, loading - and there is no
    /// value in each of them knowing that channel heights exist.
    /// </summary>
    public sealed class ChannelLifter : MonoBehaviour
    {
        public BuildController controller;

        int _lastVersion = -1;

        void LateUpdate()
        {
            if (controller?.Map == null || controller.Map.Version == _lastVersion)
                return;

            _lastVersion = controller.Map.Version;

            if (!ChannelNetwork.Recompute(controller.Map))
                return;

            // Only the parts that are actually standing somewhere. Recompute has already decided
            // every part's lift; this puts the instances where their parts now say they are.
            foreach (PlacedPart part in controller.Map.Parts)
            {
                if (part.Instance == null)
                    continue;

                part.GetTransform(out Vector3 position, out Quaternion rotation);
                part.Instance.transform.SetPositionAndRotation(position, rotation);
            }
        }
    }
}
