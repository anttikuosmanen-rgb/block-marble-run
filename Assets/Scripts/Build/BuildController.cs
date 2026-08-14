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
        public PartPalette palette;

        readonly GridMap _map = new();
        readonly CommandStack _history = new();

        int _partIndex;
        int _rotation;
        byte _colorIndex;

        /// <summary>Which of the ranked placements the player has stepped to with R.</summary>
        int _variant;
        GridCoord _variantCell;

        /// <summary>How far from the cursor an open mouth still counts as the one being aimed at.</summary>
        const float MouthSearchRange = 0.55f;

        // Precise mode: the placement is frozen and slid by whole studs from where it was locked.
        bool _precise;
        PlacedPart _lockedPlacement;
        GridCoord _lockedCursor;

        Selection _selection;
        SaveService _saves;

        bool _boxSelecting;
        Vector2 _boxStart;
        Vector2 _boxEnd;

        /// <summary>What a left click does. Placing is the default; grabbing picks pieces instead.</summary>
        public enum Tool
        {
            Place,
            Grab,
            Paint,
        }

        public Tool CurrentTool { get; private set; } = Tool.Place;

        public void SetTool(Tool tool)
        {
            CurrentTool = tool;
            if (tool != Tool.Place)
                ghost.Hide();
        }

        public int SelectedIndex => _partIndex;

        public void SelectPart(int index)
        {
            int count = CatalogPartCount;
            if (count == 0)
                return;

            _partIndex = ((index % count) + count) % count;
            _variant = 0;
            FaceLastPlaced();
        }

        PlacedPart _lastPlaced;

        /// <summary>
        /// Turns the newly chosen piece to meet the last one placed.
        ///
        /// The solver already re-faces a piece once the cursor is beside an open mouth, but until
        /// then the ghost carries whatever rotation the previous piece happened to leave behind - so
        /// picking a curve mid-run shows it pointing the wrong way and invites a pointless press of R.
        /// </summary>
        void FaceLastPlaced()
        {
            PartDefinition def = Selected;
            if (_lastPlaced == null || def?.ports == null || def.ports.Length == 0)
                return;

            foreach (PlacedPart.WorldPort open in _lastPlaced.WorldPorts())
            {
                if (_map.FindConnection(_lastPlaced, open) != null)
                    continue; // already joined; not somewhere the next piece can go

                Facing wanted = PlacedPart.WorldPort.Opposite(open.Facing);

                for (int rotation = 0; rotation < 4; rotation++)
                {
                    var probe = new PlacedPart(def, new GridCoord(0, 0, 0), rotation, _colorIndex);

                    foreach (PlacedPart.WorldPort port in probe.WorldPorts())
                    {
                        if (port.Facing != wanted)
                            continue;

                        _rotation = rotation;
                        return;
                    }
                }
            }
        }

        /// <summary>Quarter turns applied to a piece that is placed by facing rather than by joint.</summary>
        public int Rotation => _rotation;

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

            // Dragging in the grab tool, rather than shift-dragging while placing: shift now holds a
            // placement steady, and one key cannot mean both without one of them losing.
            bool selecting = CurrentTool == Tool.Grab;
            Vector2 screen = mouse.position.ReadValue();

            if (!_boxSelecting && selecting && mouse.leftButton.wasPressedThisFrame)
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

                // A click without a drag picks the one piece under the cursor; a real drag selects a
                // region. Treating a click as an empty box would clear the selection on every stray tap.
                if (rect.width < 4f && rect.height < 4f)
                    GrabUnderCursor(screen);
                else
                    _selection.SelectInScreenRect(_map, raycaster.Camera, rect, additive: false);

                if (rect.width >= 4f || rect.height >= 4f)
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
            {
                // A channel piece beside an open mouth is placed by its joint, not its facing:
                // turning it a quarter at a time is meaningless when the solver re-faces it anyway,
                // so R steps through the joins it could make instead.
                //
                // Only while there are joins to step through. Away from any mouth the solver falls
                // back to the player's own facing, and there R was still incrementing a variant that
                // nothing in that path reads - so a channel piece placed out in the open could not be
                // turned at all. Bricks were never affected; they have no ports and always rotated.
                if (Selected?.ports is { Length: > 0 } && VariantCount > 1)
                    _variant++;
                else
                    _rotation = (_rotation + 1) % 4;

                // Holding a placement steady should not mean giving up the ability to turn it. The
                // lock is rebuilt around the new choice, keeping wherever the piece has been slid to.
                if (_precise)
                    RelockAfterTurn();
            }

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

            if (keyboard.xKey.wasPressedThisFrame)
                CycleRoleUnderCursor();

            // Raising and lowering a structure. Not shift+click as first suggested: shift now holds a
            // placement steady, and a modifier that means two things is a modifier that surprises.
            if (keyboard.equalsKey.wasPressedThisFrame || keyboard.numpadPlusKey.wasPressedThisFrame)
                MoveAssemblyUnderCursor(1);

            if (keyboard.minusKey.wasPressedThisFrame || keyboard.numpadMinusKey.wasPressedThisFrame)
                MoveAssemblyUnderCursor(-1);

            if (keyboard.vKey.wasPressedThisFrame)
                SetTool(CurrentTool == Tool.Place ? Tool.Grab : Tool.Place);

            if (keyboard.bKey.wasPressedThisFrame)
                SetTool(CurrentTool == Tool.Paint ? Tool.Place : Tool.Paint);
        }

        /// <summary>
        /// Cycles the pointed-at dead end through plain, start and goal.
        ///
        /// Applied to a piece already in the build rather than chosen before placing: a role is a
        /// property of one particular end of one particular run, and picking it from a palette would
        /// mean carrying two nearly identical parts around.
        /// </summary>
        void CycleRoleUnderCursor()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            BuildHit hit = raycaster.RaycastPick(mouse.position.ReadValue());
            if (!hit.Valid || hit.Collider == null)
                return;

            var marker = hit.Collider.GetComponentInParent<PlacedPartMarker>();
            if (marker == null)
                return;

            if (!marker.Part.CanTakeRole)
            {
                Status = "Only a dead-end piece can be a start or goal";
                return;
            }

            var next = (PartRole)(((int)marker.Part.Role + 1) % 3);

            if (_history.Execute(new SetRoleCommand(marker.Part, next, RefreshMaterial)))
                Status = next == PartRole.None ? "Role cleared" : $"Marked as {next}";
        }

        /// <summary>Re-applies a part's material after its role changes, without rebuilding the object.</summary>
        void RefreshMaterial(PlacedPart part)
        {
            if (part.Instance == null)
                return;

            var renderer = part.Instance.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = factory.MaterialFor(part);
        }

        /// <summary>
        /// Picks the piece under the cursor into the selection, so it can be deleted, inspected or
        /// added to. Holding shift adds rather than replaces, matching the box-select behaviour.
        /// </summary>
        void GrabUnderCursor(Vector2 screen)
        {
            BuildHit hit = raycaster.RaycastPick(screen);
            if (!hit.Valid || hit.Collider == null)
            {
                _selection.Clear();
                Status = "Nothing there";
                return;
            }

            var marker = hit.Collider.GetComponentInParent<PlacedPartMarker>();
            if (marker == null)
                return;

            Keyboard keyboard = Keyboard.current;
            bool add = keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);

            if (!add)
                _selection.Clear();

            _selection.Add(marker.Part);
            Status = $"Grabbed {marker.Part.Definition.displayName} ({_selection.Count} selected)";
        }

        PlacedPart _lastPainted;

        void PaintUnderCursor(Vector2 screen)
        {
            BuildHit hit = raycaster.RaycastPick(screen);
            if (!hit.Valid || hit.Collider == null)
                return;

            var marker = hit.Collider.GetComponentInParent<PlacedPartMarker>();
            if (marker == null || marker.Part.ColorIndex == _colorIndex)
                return;

            // One history entry per piece, but never two for the same piece in a row: a brush held
            // still over one brick would otherwise fill the undo stack with no-ops.
            if (ReferenceEquals(marker.Part, _lastPainted))
                return;

            _lastPainted = marker.Part;

            if (_history.Execute(new PaintCommand(new[] { marker.Part }, _colorIndex, RefreshMaterial)))
                Status = $"Painted {marker.Part.Definition.displayName}";
        }

        /// <summary>Sets the brush colour, and switches to painting so the choice does something.</summary>
        public void SelectColour(byte index)
        {
            _colorIndex = index;
            SetTool(Tool.Paint);
        }

        public byte ColourIndex => _colorIndex;

        /// <summary>
        /// Raises or lowers the structure under the cursor by a layer, carrying everything attached.
        /// </summary>
        void MoveAssemblyUnderCursor(int layers)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            PlacedPart seed = PartUnderCursor(mouse.position.ReadValue());
            if (seed == null)
            {
                Status = "Point at a piece to raise or lower its structure";
                return;
            }

            List<PlacedPart> group = Assembly.Connected(_map, seed);
            List<PlacedPart> moved = Assembly.Shift(_map, group, layers);

            if (moved == null)
            {
                Status = layers > 0 ? "Something is in the way above" : "Cannot go below the ground";
                return;
            }

            _selection.Clear();

            var command = new MoveAssemblyCommand(_map, group, moved, PillarDefinition, Spawn);
            if (_history.Execute(command))
                Status = $"{(layers > 0 ? "Raised" : "Lowered")} {group.Count} pieces";
        }

        PlacedPart PartUnderCursor(Vector2 screen)
        {
            BuildHit hit = raycaster.RaycastPick(screen);
            if (!hit.Valid || hit.Collider == null)
                return null;

            var marker = hit.Collider.GetComponentInParent<PlacedPartMarker>();
            return marker != null ? marker.Part : null;
        }

        /// <summary>
        /// The structure a pending placement would join, found through the mouth it is mating with.
        ///
        /// Falls back to everything only when no join can be found, which should not happen - the
        /// placement exists because the solver mated it with an existing mouth.
        /// </summary>
        List<PlacedPart> ConnectedToPlacement(PlacedPart candidate)
        {
            foreach (PlacedPart.WorldPort port in candidate.WorldPorts())
            {
                PlacedPart joined = _map.FindConnection(candidate, port);
                if (joined != null)
                    return Assembly.Connected(_map, joined);
            }

            return new List<PlacedPart>(_map.Parts);
        }

        /// <summary>How many layers the pending placement would need the build lifted by, or zero.</summary>
        public int GrowthLayers { get; private set; }

        /// <summary>
        /// Lifts everything already built and places the piece in the room that makes.
        ///
        /// The piece keeps the join it was showing; only the world moves under it, which is why the
        /// placement is simply the same one shifted by the same amount.
        /// </summary>
        void GrowAndPlace(PlacedPart below, int layers)
        {
            // Only what the new piece is joining, not the whole world. Lifting every part in the map
            // carried unrelated builds along with the one being extended - they moved for a reason
            // that had nothing to do with them.
            List<PlacedPart> all = ConnectedToPlacement(below);

            List<PlacedPart> moved = Assembly.Shift(_map, all, layers);
            if (moved == null)
            {
                Status = "Cannot lift the build to make room";
                return;
            }

            var raised = new PlacedPart(below.Definition,
                new GridCoord(below.Origin.x, below.Origin.y, below.Origin.layer + layers),
                below.Rotation, _colorIndex);

            _selection.Clear();

            var command = new GrowAndPlaceCommand(
                new MoveAssemblyCommand(_map, all, moved, PillarDefinition, Spawn),
                new PlaceWithSupportsCommand(_map, raised, PillarDefinition, Spawn));

            if (!_history.Execute(command))
            {
                // Silence here read as the whole feature being broken: the build lifted, the piece
                // was refused, and the rollback put everything back looking untouched.
                Status = "Could not place underneath - something is in the way";
                return;
            }

            {
                _lastPlaced = raised;
                Status = $"Lifted the build {layers} layer(s) and placed underneath";
            }
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

        [Tooltip("Brick used to prop up parts placed in mid-air. Falls back to the first block in the catalog.")]
        public string pillarPartId = "building_block_2x2";

        PartDefinition _pillar;

        /// <summary>The brick auto-scaffolding is built from (DESIGN.md §5.1).</summary>
        PartDefinition PillarDefinition
        {
            get
            {
                if (_pillar != null)
                    return _pillar;

                foreach (PartDefinition def in factory.Catalog.parts)
                    if (def != null && def.id == pillarPartId)
                        return _pillar = def;

                // Any studded, single-layer part will hold weight; better a different brick than none.
                foreach (PartDefinition def in factory.Catalog.parts)
                    if (def != null && def.category == PartCategory.Block && def.heightLayers == 1)
                        return _pillar = def;

                return null;
            }
        }

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
            _variant = 0;
            FaceLastPlaced();
        }

        void UpdatePreviewAndPlacement()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            Vector2 screen = mouse.position.ReadValue();

            // The palette sits over the world, and a click on it must not also land in the world.
            if (palette != null && palette.Covers(screen))
            {
                ghost.Hide();
                return;
            }

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

            if (CurrentTool == Tool.Grab)
            {
                ghost.Hide();
                return; // handled by the box-select pass, which also picks on a click
            }

            if (CurrentTool == Tool.Paint)
            {
                ghost.Hide();

                // Held, not just pressed, so a colour can be brushed across several pieces.
                if (mouse.leftButton.isPressed)
                    PaintUnderCursor(screen);

                return;
            }

            BuildHit hit = raycaster.RaycastPlacement(screen);
            if (!hit.Valid)
            {
                ghost.Hide();
                return;
            }

            Keyboard keys = Keyboard.current;
            bool wantsPrecise = keys != null && (keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed);

            PlacedPart candidate = CandidateAt(hit.Cell);
            UpdatePreciseLock(wantsPrecise, candidate, hit.Cell);

            // Re-solve once locked, so the first frame of precise mode already follows the lock.
            if (_precise)
                candidate = CandidateAt(hit.Cell);

            PlacementResult result = _map.CanPlace(candidate);

            // A placement below the ground is shown where it would be, sunk into the floor, so the
            // offer is visible rather than described. Choosing it lifts the build to make the room.
            bool needsGrowth = candidate.Origin.layer < 0;
            GrowthLayers = needsGrowth ? -candidate.Origin.layer : 0;

            ghost.Show(candidate, needsGrowth ? PlacementResult.Unsupported : result,
                factory.Catalog.ColorAt(_colorIndex));

            if (mouse.leftButton.wasPressedThisFrame && needsGrowth)
            {
                GrowAndPlace(candidate, GrowthLayers);
                return;
            }

            // Unsupported is placeable, not refused: the piece gets pillars built under it. Only a
            // genuine collision blocks placement, in precise mode as much as out of it.
            if (mouse.leftButton.wasPressedThisFrame && result != PlacementResult.Blocked)
            {
                var command = new PlaceWithSupportsCommand(_map, candidate, PillarDefinition, Spawn);

                if (_history.Execute(command))
                {
                    _lastPlaced = candidate;

                    if (command.SupportCount > 0)
                        Status = $"Placed with {command.SupportCount} support brick(s)";
                }
            }
        }

        /// <summary>
        /// Works out where the held part would actually go, from the column under the cursor.
        ///
        /// The cursor supplies the column only; <see cref="PlacementSolver"/> chooses the height,
        /// weighing resting on what is beneath against joining a neighbouring channel. Taking the
        /// height from the ray's hit point instead made whole classes of build impossible - bridging
        /// two towers means pointing at the gap, and continuing an elevated run means pointing at
        /// empty air beside it.
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

            if (_precise && _lockedPlacement != null)
                return SlideLocked(cursorCell);

            // Moving the cursor starts again from the best placement. Carrying the choice across the
            // build meant that one press of R left every later placement showing a worse alternative
            // - the snapping looked broken when it was only being overruled.
            if (cursorCell.x != _variantCell.x || cursorCell.y != _variantCell.y)
            {
                _variantCell = cursorCell;
                _variant = 0;
            }

            // Cycling only makes sense against a joint. With an open mouth nearby, the alternatives
            // are this piece's own mouths meeting it; with none, there is nothing to choose between.
            if (def.ports is { Length: > 1 } &&
                PlacementSolver.NearestOpenMouth(_map, cursorCell.CellCentre, MouthSearchRange, out PlacedPart.WorldPort target))
            {
                List<PlacedPart> matings = PlacementSolver.MatingsWith(_map, def, _colorIndex, target,
                    allowBelowGround: true);

                if (matings.Count > 0)
                {
                    _variant = ((_variant % matings.Count) + matings.Count) % matings.Count;
                    VariantCount = matings.Count;
                    return matings[_variant];
                }
            }

            VariantCount = 1;
            return PlacementSolver.Solve(_map, def, anchorX, anchorY, _rotation, _colorIndex);
        }

        /// <summary>How many placements the current position offers, for the HUD.</summary>
        public int VariantCount { get; private set; }

        public int VariantIndex => _variant;
        public bool Precise => _precise;

        /// <summary>
        /// Moves the locked placement by whole studs, following the cursor.
        ///
        /// Keeps the rotation and height the player accepted and gives them the position back, which
        /// the snapping otherwise decides for them. Only overlap is refused here - a piece that hangs
        /// unsupported is a legitimate thing to want, and the scaffolding will carry it.
        /// </summary>
        PlacedPart SlideLocked(GridCoord cursorCell)
        {
            GridCoord origin = _lockedPlacement.Origin;

            var moved = new GridCoord(
                origin.x + (cursorCell.x - _lockedCursor.x),
                origin.y + (cursorCell.y - _lockedCursor.y),
                origin.layer);

            // Fold the movement into the lock so R can re-solve from where the piece actually is.
            _lockedPlacement = new PlacedPart(_lockedPlacement.Definition, moved,
                _lockedPlacement.Rotation, _colorIndex);
            _lockedCursor = cursorCell;

            return _lockedPlacement;
        }

        /// <summary>
        /// Rebuilds the lock after R, so shift and R compose.
        ///
        /// The piece keeps the position it has been slid to; only its facing or its choice of join
        /// changes. Re-solving from the slid position rather than the original cursor is what stops
        /// it jumping back to where the lock was first taken.
        /// </summary>
        void RelockAfterTurn()
        {
            if (_lockedPlacement == null)
                return;

            GridCoord at = _lockedPlacement.Origin;
            PartDefinition def = _lockedPlacement.Definition;

            if (def.ports is { Length: > 0 })
            {
                List<PlacedPart> ranked = PlacementSolver.SolveRanked(_map, def, at.x, at.y, _rotation, _colorIndex);
                if (ranked.Count > 0)
                {
                    _variant = ((_variant % ranked.Count) + ranked.Count) % ranked.Count;
                    _lockedPlacement = ranked[_variant];
                    VariantCount = ranked.Count;
                    return;
                }
            }

            _lockedPlacement = new PlacedPart(def, at, _rotation, _colorIndex);
        }

        void UpdatePreciseLock(bool wanted, PlacedPart candidate, GridCoord cursorCell)
        {
            if (wanted && !_precise && candidate != null)
            {
                // Locked from whatever was on screen when the key went down, so the placement the
                // player was looking at is the one they keep.
                _precise = true;
                _lockedPlacement = candidate;
                _lockedCursor = cursorCell;
                Status = "Precise placement - slide with the mouse";
            }
            else if (!wanted && _precise)
            {
                _precise = false;
                _lockedPlacement = null;
            }
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
