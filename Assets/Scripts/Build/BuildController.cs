using BlockMarbleRun.CameraRig;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using BlockMarbleRun.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Build mode: point, preview, place, delete. See DESIGN.md §5.
    ///
    /// Input is read through the Input System's device APIs directly. M1 has one scheme
    /// (mouse and keyboard, the only one both targets need), so the indirection of an action asset
    /// would buy nothing yet - but every read is funnelled through this one class so adding one later
    /// does not touch the placement logic.
    /// </summary>
    public sealed class BuildController : MonoBehaviour
    {
        public PartFactory factory;
        public BuildRaycaster raycaster;
        public OrbitCamera orbitCamera;
        public GhostPreview ghost;
        public Transform partRoot;

        readonly GridMap _map = new();
        readonly CommandStack _history = new();

        int _partIndex;
        int _rotation;
        byte _colorIndex;

        public GridMap Map => _map;

        public PartDefinition Selected =>
            factory != null && factory.Catalog != null ? factory.Catalog.Get(_partIndex) : null;

        /// <summary>Surfaced for the HUD, so a missing catalog is visible rather than silently inert.</summary>
        public int CatalogPartCount =>
            factory != null && factory.Catalog != null ? factory.Catalog.parts.Count : -1;

        void Update()
        {
            if (CatalogPartCount <= 0)
                return;

            ReadKeys();
            UpdatePreviewAndPlacement();
        }

        void ReadKeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.rKey.wasPressedThisFrame)
                _rotation = (_rotation + 1) % 4;

            if (keyboard.cKey.wasPressedThisFrame)
                _colorIndex = (byte)((_colorIndex + 1) % Mathf.Max(1, factory.Catalog.palette.Length));

            if (keyboard.qKey.wasPressedThisFrame)
                CyclePart(-1);

            if (keyboard.eKey.wasPressedThisFrame)
                CyclePart(1);

            if (keyboard.fKey.wasPressedThisFrame)
                orbitCamera.Frame(_map);

            if (keyboard.homeKey.wasPressedThisFrame)
                orbitCamera.ReturnToOrigin();

            bool control = keyboard.leftCtrlKey.isPressed || keyboard.leftCommandKey.isPressed ||
                           keyboard.rightCtrlKey.isPressed || keyboard.rightCommandKey.isPressed;

            if (control && keyboard.zKey.wasPressedThisFrame)
            {
                bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                if (shift) _history.Redo(); else _history.Undo();
            }
        }

        void CyclePart(int step)
        {
            int count = factory.Catalog.parts.Count;
            _partIndex = ((_partIndex + step) % count + count) % count;
        }

        void UpdatePreviewAndPlacement()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            Vector2 screen = mouse.position.ReadValue();

            // Orbiting and panning should not scrub a ghost across the world.
            if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
            {
                ghost.Hide();
                return;
            }

            bool deleteMode = Keyboard.current != null &&
                              (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed);

            if (deleteMode)
            {
                ghost.Hide();
                if (mouse.leftButton.wasPressedThisFrame)
                    TryDelete(screen);
                return;
            }

            BuildHit hit = raycaster.RaycastPlacement(screen);
            if (!hit.Valid)
            {
                ghost.Hide();
                return;
            }

            var candidate = new PlacedPart(Selected, AnchorFor(hit.Cell), _rotation, _colorIndex);
            PlacementResult result = _map.CanPlace(candidate);

            ghost.Show(candidate, result, factory.Catalog.ColorAt(_colorIndex));

            if (mouse.leftButton.wasPressedThisFrame && result == PlacementResult.Valid)
                _history.Execute(new PlacePartCommand(_map, candidate, Spawn));
        }

        /// <summary>
        /// The cursor cell names where the part's <em>corner</em> goes. Centring the footprint on the
        /// cursor instead keeps a large part under the pointer, which is what makes placing a 4x4
        /// curve feel aimed rather than offset.
        /// </summary>
        GridCoord AnchorFor(GridCoord cell)
        {
            Vector2Int size = _rotation % 2 == 0
                ? Selected.footprintSize
                : new Vector2Int(Selected.footprintSize.y, Selected.footprintSize.x);

            return new GridCoord(cell.x - (size.x - 1) / 2, cell.y - (size.y - 1) / 2, cell.layer);
        }

        void TryDelete(Vector2 screen)
        {
            BuildHit hit = raycaster.RaycastPick(screen);
            if (!hit.Valid || hit.Collider == null)
                return;

            var marker = hit.Collider.GetComponentInParent<PlacedPartMarker>();
            if (marker != null)
                _history.Execute(new RemovePartCommand(_map, marker.Part, Spawn));
        }

        GameObject Spawn(PlacedPart part)
        {
            GameObject go = factory.Create(part, partRoot);
            PlacedPartMarker.Attach(go, part);
            return go;
        }
    }
}
