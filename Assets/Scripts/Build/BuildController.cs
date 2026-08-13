using System.Collections.Generic;
using BlockMarbleRun.CameraRig;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using BlockMarbleRun.Persistence;
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
        public Material highlightMaterial;

        readonly GridMap _map = new();
        readonly CommandStack _history = new();

        int _partIndex;
        int _rotation;
        byte _colorIndex;

        Selection _selection;
        SaveService _saves;

        bool _boxSelecting;
        Vector2 _boxStart;
        Vector2 _boxEnd;

        public GridMap Map => _map;
        public Selection Selection => _selection;
        public SaveService Saves => _saves;
        public string Status { get; private set; } = "";
        public bool Busy { get; private set; }

        /// <summary>Screen rectangle of the drag in progress, for the HUD to draw. Empty when idle.</summary>
        public Rect BoxSelectRect => _boxSelecting ? RectFrom(_boxStart, _boxEnd) : Rect.zero;

        async void Start()
        {
            _selection = new Selection(factory, highlightMaterial);
            _saves = new SaveService(SaveStoreFactory.Create(), factory.Catalog);

            // Opening the store can genuinely fail - private browsing blocks IndexedDB outright - so
            // this is awaited up front rather than discovered on the player's first save.
            await _saves.InitialiseAsync();
        }

        public PartDefinition Selected =>
            factory != null && factory.Catalog != null ? factory.Catalog.Get(_partIndex) : null;

        /// <summary>Surfaced for the HUD, so a missing catalog is visible rather than silently inert.</summary>
        public int CatalogPartCount =>
            factory != null && factory.Catalog != null ? factory.Catalog.parts.Count : -1;

        void Update()
        {
            if (CatalogPartCount <= 0 || _selection == null)
                return;

            ReadKeys();

            if (UpdateBoxSelect())
                return;

            UpdatePreviewAndPlacement();
        }

        /// <summary>
        /// Shift-drag draws a selection box. Returns true while the drag owns the mouse, so placement
        /// does not also fire on the same click.
        /// </summary>
        bool UpdateBoxSelect()
        {
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            if (mouse == null || keyboard == null)
                return false;

            bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            Vector2 screen = mouse.position.ReadValue();

            if (!_boxSelecting && shift && mouse.leftButton.wasPressedThisFrame)
            {
                _boxSelecting = true;
                _boxStart = screen;
                _boxEnd = screen;
                ghost.Hide();
                return true;
            }

            if (!_boxSelecting)
                return false;

            _boxEnd = screen;

            if (mouse.leftButton.wasReleasedThisFrame)
            {
                _boxSelecting = false;

                Rect rect = RectFrom(_boxStart, _boxEnd);

                // A click without a drag means "clear", not "select nothing in a zero-width box".
                if (rect.width < 4f && rect.height < 4f)
                    _selection.Clear();
                else
                    _selection.SelectInScreenRect(_map, raycaster.Camera, rect, additive: false);

                Status = _selection.Count > 0 ? $"Selected {_selection.Count}" : "Selection cleared";
            }

            return true;
        }

        static Rect RectFrom(Vector2 a, Vector2 b) => Rect.MinMaxRect(
            Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
            Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

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

                // Undo rebuilds instances, so highlights have to be re-applied and dead entries dropped.
                _selection.Prune(_map);
                _selection.RefreshHighlights();
            }

            if (keyboard.deleteKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame)
                DeleteSelection();

            // Plain keys, not Cmd+S / Cmd+O: the browser claims those for "save page" and "open
            // file" before the canvas ever sees them, the same way macOS claims the function row.
            if (keyboard.sKey.wasPressedThisFrame)
                _ = SaveAsync();

            if (keyboard.lKey.wasPressedThisFrame)
                _ = LoadAsync();
        }

        void DeleteSelection()
        {
            if (_selection.Count == 0)
                return;

            var parts = new List<PlacedPart>(_selection.Parts);
            _selection.Clear();

            if (_history.Execute(new RemoveManyCommand(_map, parts, Spawn)))
                Status = $"Deleted {parts.Count}";
        }

        // --- persistence ---------------------------------------------------------------------

        public string SlotName = "My Creation";

        async Awaitable SaveAsync()
        {
            if (Busy || _saves == null)
                return;

            Busy = true;
            Status = "Saving...";

            try
            {
                await _saves.SaveAsync(_map, SlotName);

                // Captured after the save so a failed write never leaves a thumbnail without a
                // creation behind it.
                await _saves.SaveThumbnailAsync(SlotName, raycaster.Camera);

                Status = $"Saved '{SlotName}' ({_map.Parts.Count} parts)";
            }
            catch (System.Exception e)
            {
                Status = $"Save failed: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                Busy = false;
            }
        }

        async Awaitable LoadAsync()
        {
            if (Busy || _saves == null)
                return;

            Busy = true;
            Status = "Loading...";

            try
            {
                SaveModel model = await _saves.LoadAsync(SlotName);
                if (model == null)
                {
                    Status = $"No save named '{SlotName}'";
                    return;
                }

                _selection.Clear();
                ClearInstances();

                LoadReport report = _saves.Apply(model, _map, Spawn);
                _history.Clear(); // history from the previous build cannot apply to this one

                orbitCamera.Frame(_map);
                Status = $"Loaded '{model.name}': {report}";
            }
            catch (System.Exception e)
            {
                Status = $"Load failed: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                Busy = false;
            }
        }

        void ClearInstances()
        {
            foreach (PlacedPart part in _map.Parts)
                if (part.Instance != null)
                    Destroy(part.Instance);

            _map.Clear();
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

            PlacedPart candidate = CandidateAt(hit.Cell);
            PlacementResult result = _map.CanPlace(candidate);

            ghost.Show(candidate, result, factory.Catalog.ColorAt(_colorIndex));

            if (mouse.leftButton.wasPressedThisFrame && result == PlacementResult.Valid)
                _history.Execute(new PlacePartCommand(_map, candidate, Spawn));
        }

        /// <summary>
        /// Works out where the held part would actually go, from the column under the cursor.
        ///
        /// The height comes from the whole footprint, not from whatever the ray happened to hit. A
        /// brick rests on the highest thing beneath <em>any</em> part of it, exactly as it would in
        /// the hand - so a long piece laid across two towers with a gap between them sits on top of
        /// both. Taking the height from the hit point alone made that impossible: bridging means
        /// pointing at the gap, where the ray finds nothing but ground and drops the piece to the
        /// floor. Whether the studs then line up is a separate question, answered by
        /// <see cref="GridMap.IsSupported"/>.
        /// </summary>
        PlacedPart CandidateAt(GridCoord cursorCell)
        {
            PartDefinition def = Selected;

            Vector2Int size = _rotation % 2 == 0
                ? def.footprintSize
                : new Vector2Int(def.footprintSize.y, def.footprintSize.x);

            // Centre the footprint on the cursor, so a large part feels aimed rather than offset.
            int anchorX = cursorCell.x - (size.x - 1) / 2;
            int anchorY = cursorCell.y - (size.y - 1) / 2;

            var probe = new PlacedPart(def, new GridCoord(anchorX, anchorY, 0), _rotation, _colorIndex);

            int restLayer = 0;
            foreach (GridCoord cell in probe.OccupiedCells())
            {
                if (cell.layer != 0)
                    continue; // base layer only; the probe's upper layers say nothing about support

                restLayer = Mathf.Max(restLayer, _map.ColumnRestLayer(cell.x, cell.y));
            }

            return new PlacedPart(def, new GridCoord(anchorX, anchorY, restLayer), _rotation, _colorIndex);
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
