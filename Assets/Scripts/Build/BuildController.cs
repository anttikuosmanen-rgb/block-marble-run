using System.Collections.Generic;
using BlockMarbleRun.CameraRig;
using BlockMarbleRun.Grid;
using BlockMarbleRun.Parts;
using BlockMarbleRun.Persistence;
using BlockMarbleRun.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

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

        [Tooltip("Alignment lines drawn from the piece being placed. Optional.")]
        public AlignmentGuides guides;
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
            // Leaving the grab tool drops the selection. It used to survive, and since a selection is
            // drawn by tinting the pieces, that read as having painted them - the tint stayed on the
            // build with no tool on screen that could explain or remove it.
            if (CurrentTool == Tool.Grab && tool != Tool.Grab)
                _selection?.Clear();

            CurrentTool = tool;

            if (tool != Tool.Place)
            {
                ghost.Hide();
                guides?.Hide();
            }
        }

        public int SelectedIndex => _partIndex;

        public void SelectPart(int index)
        {
            int count = CatalogPartCount;
            if (count == 0)
                return;

            _partIndex = ((index % count) + count) % count;
            _variant = 0;
            _held = null;
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

            // Is there something to come back to? A browser's back button takes the whole session
            // with it, and the player has no reason to expect that a build survives it.
            try
            {
                foreach (Persistence.SaveSlot slot in await _saves.ListAsync())
                    if (slot.name == AutosaveSlot)
                        HasAutosave = true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Save] Could not look for an autosave: {e.Message}");
            }
        }

        /// <summary>
        /// Where the build is kept between edits.
        ///
        /// One slot, overwritten. This is not a save history - it is the answer to closing the tab by
        /// accident, and a hundred numbered copies of the same build would bury the ones the player
        /// meant to keep.
        /// </summary>
        public const string AutosaveSlot = "Autosave";

        /// <summary>Whether there is an autosave to restore, so the HUD can say so.</summary>
        public bool HasAutosave { get; private set; }

        int _autosavedVersion = -1;
        float _changedAt;

        /// <summary>
        /// Writes the build to the autosave slot once it has stopped changing.
        ///
        /// Debounced rather than written on every edit: dragging out a wall is dozens of placements
        /// in a couple of seconds, and each one would otherwise be a round trip to IndexedDB.
        /// </summary>
        void Autosave()
        {
            if (_saves == null || Busy)
                return;

            // The state the session opened in is treated as already saved. It was not: the version
            // counter started below any real value, so an untouched empty scene read as a change and
            // wrote itself over the autosave a second and a half after launch - destroying the work
            // the moment the player came back for it.
            if (_autosavedVersion < 0)
            {
                _autosavedVersion = _map.Version;
                return;
            }

            if (_map.Version == _autosavedVersion)
                return;

            // And never an empty build over one that has something in it. Clearing is undoable and
            // the player may well be about to undo it; an autosave that can only ever be emptied by
            // accident is worth keeping through one.
            if (_map.Parts.Count == 0 && HasAutosave)
            {
                _autosavedVersion = _map.Version;
                _changedAt = 0f;
                return;
            }

            if (_changedAt <= 0f)
            {
                _changedAt = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _changedAt < 1.5f)
                return;

            _autosavedVersion = _map.Version;
            _changedAt = 0f;

            _ = AutosaveAsync();
        }

        async Awaitable AutosaveAsync()
        {
            try
            {
                await _saves.SaveAsync(_map, AutosaveSlot);
                HasAutosave = true;
            }
            catch (System.Exception e)
            {
                // Never a popup and never a Status: an autosave that fails must not interrupt the
                // thing the player is doing. It is reported once, quietly.
                Debug.LogWarning($"[Save] Autosave failed: {e.Message}");
            }
        }

        /// <summary>
        /// A part picked out of the build that is not on the bar, held until another is chosen.
        ///
        /// Plates below 2x2, generated pillars and anything else left off the palette can still be
        /// built with - copy and paste has always placed them - so refusing to hand one back when it
        /// is picked up was a rule with nothing behind it.
        /// </summary>
        PartDefinition _held;

        /// <summary>Whether the piece in hand came out of the build rather than off the bar.</summary>
        public bool HoldingOffPalette => _held != null;

        public PartDefinition Selected =>
            _held != null ? _held
                : factory != null && factory.Catalog != null ? factory.Catalog.Get(_partIndex) : null;

        /// <summary>Surfaced for the HUD, so a missing catalog is visible rather than silently inert.</summary>
        public int CatalogPartCount =>
            factory != null && factory.Catalog != null ? factory.Catalog.Selectable.Count : -1;

        void Update()
        {
            if (CatalogPartCount <= 0 || _selection == null)
                return;

            ReadKeys();
            Autosave();

            if (orbitCamera != null)
                orbitCamera.ZoomLocked = Pasting;

            if (UpdatePaste())
                return;

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
                guides?.Hide();
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

        // Characters actually typed this frame, as the OS and the browser report them.
        readonly List<char> _typed = new();
        System.Action<char> _textHandler;

        /// <summary>The last character seen, for the HUD - so a key that does nothing can be diagnosed.</summary>
        public string LastTyped { get; private set; } = "";

        void OnEnable()
        {
            _textHandler = character =>
            {
                _typed.Add(character);
                LastTyped = character.ToString();
            };

            if (Keyboard.current != null)
                Keyboard.current.onTextInput += _textHandler;
        }

        void OnDisable()
        {
            if (Keyboard.current != null && _textHandler != null)
                Keyboard.current.onTextInput -= _textHandler;
        }

        void LateUpdate() => _typed.Clear();

        /// <summary>
        /// +1, -1 or 0, from the character the key produced rather than from where the key sits.
        ///
        /// Three readings of this have now been wrong, each for its own reason. Named keys like
        /// equalsKey are positions on a US layout, not characters. displayName turned out to report
        /// the US-position character too, so on a Nordic keyboard the key marked plus read as minus
        /// and lowered what it was meant to raise, while the key marked minus read as slash and did
        /// nothing at all. Text input is the only source that knows what the player actually typed,
        /// because it is the one the operating system fills in after applying the layout.
        /// </summary>
        int LiftKey(Keyboard keyboard)
        {
            if (keyboard.numpadPlusKey.wasPressedThisFrame) return 1;
            if (keyboard.numpadMinusKey.wasPressedThisFrame) return -1;

            foreach (char character in _typed)
            {
                if (character == '+') return 1;
                if (character == '-') return -1;
            }

            return 0;
        }

        static bool Held(Keyboard keyboard) =>
            keyboard.leftCtrlKey.isPressed || keyboard.leftCommandKey.isPressed ||
            keyboard.rightCtrlKey.isPressed || keyboard.rightCommandKey.isPressed;

        void ReadKeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.rKey.wasPressedThisFrame && !Pasting &&
                !(CurrentTool == Tool.Grab && _selection.Count > 0))
            {
                // A channel piece beside an open mouth is placed by its joint, not its facing:
                // turning it a quarter at a time is meaningless when the solver re-faces it anyway,
                // so R steps through the joins it could make instead.
                //
                // Only while there are joins to step through. Away from any mouth the solver falls
                // back to the player's own facing, and there R was still incrementing a variant that
                // nothing in that path reads - so a channel piece placed out in the open could not be
                // turned at all. Bricks were never affected; they have no ports and always rotated.
                // A join outranks a turn, held or not. R has two jobs and they are not equal: where
                // the piece can meet a channel mouth, stepping through those meetings is what the
                // player is choosing between, and turning it a quarter is what R means only when
                // there is no mouth to meet.
                if (Selected?.ports is { Length: > 0 } && VariantCount > 1)
                    _variant++;
                else
                    _rotation = (_rotation + 1) % 4;

                // Holding a placement steady should not mean giving up the ability to turn it. The
                // lock is rebuilt around the new choice, keeping wherever the piece has been slid to.
                if (_precise)
                    RelockAfterTurn();
            }

            // Guarded against the copy shortcut, which is the same key with a modifier.
            if (keyboard.cKey.wasPressedThisFrame && !Held(keyboard))
                _colorIndex = (byte)((_colorIndex + 1) % Mathf.Max(1, factory.Catalog.palette.Length));

            if (keyboard.qKey.wasPressedThisFrame)
                CyclePart(-1);

            if (keyboard.eKey.wasPressedThisFrame)
                CyclePart(1);

            if (keyboard.fKey.wasPressedThisFrame)
                orbitCamera.Frame(_map);

            if (keyboard.homeKey.wasPressedThisFrame)
                orbitCamera.ReturnToOrigin();

            bool control = Held(keyboard);

            ReadSelectionKeys(keyboard, control);

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

            // L opens the browser rather than loading anything: with saves named by their moment
            // there is no one obvious creation to reopen, and picking the newest silently would be
            // wrong exactly when the player wants an older one.
            if (keyboard.lKey.wasPressedThisFrame && browser != null)
                browser.Toggle();

            if (keyboard.xKey.wasPressedThisFrame)
                CycleRoleUnderCursor();

            // Plain O, like save and load: the browser claims the modified keys.
            if (keyboard.oKey.wasPressedThisFrame && HasAutosave)
                _ = LoadAsync(AutosaveSlot);

            // Raising and lowering a structure. Not shift+click as first suggested: shift now holds a
            // placement steady, and a modifier that means two things is a modifier that surprises.
            // A group being placed owns the keys that would otherwise move the build or change tool.
            // Raising in particular would act on whatever is under the cursor rather than on the held
            // group - and since a preview carries no collider, that is the build showing through it.
            if (Pasting)
                return;

            int structureLift = LiftKey(keyboard);
            if (structureLift != 0)
                MoveAssemblyUnderCursor(structureLift);

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
        // --- selection editing ---------------------------------------------------------------

        List<PlacedPart> _clipboard;

        /// <summary>Turns, mirrors, copies and pastes. Only meaningful with something selected.</summary>
        void ReadSelectionKeys(Keyboard keyboard, bool control)
        {
            // A held paste reads R and M for itself.
            if (CurrentTool != Tool.Grab || Pasting)
                return;

            if (control && keyboard.cKey.wasPressedThisFrame)
            {
                _clipboard = SelectionOps.Duplicate(_selection.Parts);
                Status = _clipboard.Count > 0 ? $"Copied {_clipboard.Count}" : "Nothing selected";
                return;
            }

            if (control && keyboard.vKey.wasPressedThisFrame)
            {
                EnsurePasteSession();
                Paste();
                return;
            }

            // Plain A as well as Cmd/Ctrl A: the browser claims the modified one for "select all" on
            // the page before the canvas sees it, the same reason save and load are unmodified keys.
            if (keyboard.aKey.wasPressedThisFrame)
            {
                _selection.SetTo(_map.Parts);
                Status = $"Selected all {_selection.Count}";
                return;
            }

            if (_selection.Count == 0)
                return;

            if (keyboard.rKey.wasPressedThisFrame)
                Transform(SelectionOps.Rotate(_map, _selection.Parts, 1), "Turned");

            if (keyboard.mKey.wasPressedThisFrame)
                Transform(SelectionOps.Mirror(_map, _selection.Parts, MirrorTwin), "Mirrored");
        }

        /// <summary>
        /// Swaps a chiral part for its opposite hand.
        ///
        /// Found through the catalog in both directions: a generated mirror names its source, and the
        /// source names nothing, so the reverse lookup is a search rather than a field.
        /// </summary>
        PartDefinition MirrorTwin(PartDefinition def)
        {
            if (def == null)
                return null;

            if (def.IsMirror)
            {
                foreach (PartDefinition other in factory.Catalog.parts)
                    if (other != null && other.id == def.mirrorOf)
                        return other;

                return def;
            }

            foreach (PartDefinition other in factory.Catalog.parts)
                if (other != null && other.mirrorOf == def.id)
                    return other;

            // Symmetric parts are their own mirror, which is why nothing was generated for them.
            return def;
        }

        void Transform(List<PlacedPart> moved, string verb)
        {
            if (moved == null)
            {
                Status = $"Cannot be {verb.ToLowerInvariant()} here - something is in the way";
                return;
            }

            var before = new List<PlacedPart>(_selection.Parts);

            // The originals are about to be destroyed, and their tint is not something the
            // replacements inherit - so it comes off while the originals still exist to take it off.
            _selection.ClearHighlights();

            // No pillar: propping a selection the player is turning would add bricks they never asked
            // for, on every single turn.
            var command = new MoveAssemblyCommand(_map, before, moved, null, Spawn);

            if (!_history.Execute(command))
            {
                Status = $"Could not be {verb.ToLowerInvariant()}";
                return;
            }

            _selection.SetTo(moved);
            Status = $"{verb} {moved.Count}";
        }

        PasteSession _paste;

        Vector2 _pasteMouseDownAt;
        bool _pasteDragged;

        /// <summary>True while a copied group is being carried, waiting for a click to commit it.</summary>
        public bool Pasting => _paste is { Active: true };

        public int PastingCount => _paste?.Parts.Count ?? 0;
        public bool PasteFits => _paste is { Fits: true };

        /// <summary>
        /// Picks the group up rather than dropping it. The click that follows decides where it lands.
        /// </summary>
        void Paste()
        {
            if (_clipboard == null || _clipboard.Count == 0)
            {
                Status = "Nothing copied";
                return;
            }

            _selection.Clear();
            _paste.Begin(SelectionOps.Duplicate(_clipboard));

            Mouse mouse = Mouse.current;
            BuildHit hit = mouse != null ? raycaster.RaycastPlacement(mouse.position.ReadValue()) : default;

            if (hit.Valid)
                _paste.MoveTo(_map, hit.Cell);
            else
                _paste.Refresh(_map);

            Status = "Placing - move to position, R turn, M mirror, click to place, right click to cancel";
        }

        /// <summary>
        /// Carries the held group under the cursor, and commits it on a click.
        ///
        /// Runs before the ordinary placement pass and swallows the click, so the piece on the
        /// palette is not also placed by the same press.
        /// </summary>
        bool UpdatePaste()
        {
            if (!Pasting)
                return false;

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            ghost.Hide();
            guides?.Hide();

            if (mouse == null)
                return true;

            // Right click cancels, right drag still orbits. Escape cannot be the cancel here: the
            // browser takes it first to leave full screen, so in the build that matters it would look
            // as though nothing had happened.
            if (mouse.rightButton.wasPressedThisFrame)
            {
                _pasteMouseDownAt = mouse.position.ReadValue();
                _pasteDragged = false;
            }

            if (mouse.rightButton.isPressed &&
                (mouse.position.ReadValue() - _pasteMouseDownAt).sqrMagnitude > 25f)
                _pasteDragged = true;

            if (mouse.rightButton.wasReleasedThisFrame && !_pasteDragged)
            {
                _paste.Cancel();
                guides?.Hide();
                Status = "Paste cancelled";
                return true;
            }

            // Turning and mirroring while held, which is the whole point of holding it: the group can
            // be aimed at the join it is going to before anything is committed.
            if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            {
                List<PlacedPart> turned = SelectionOps.Rotate(_map, _paste.Parts, 1) ??
                                          RotateFreely(_paste.Parts);

                _paste.Begin(turned);
                _paste.Refresh(_map);
            }

            if (keyboard != null && keyboard.mKey.wasPressedThisFrame)
            {
                List<PlacedPart> flipped = SelectionOps.Mirror(_map, _paste.Parts, MirrorTwin) ??
                                           MirrorFreely(_paste.Parts);

                _paste.Begin(flipped);
                _paste.Refresh(_map);
            }

            int lift = 0;

            if (keyboard != null)
                lift = LiftKey(keyboard);

            // The wheel too, and it takes precedence over zooming while a group is held. Which
            // physical key carries + and - depends on the keyboard layout, and the Input System reads
            // keys by position rather than by the character printed on them - so on a Nordic layout
            // the key marked + is the one it calls minus. The wheel has no layout.
            float scroll = WheelDelta(mouse);
            if (Mathf.Abs(scroll) > 0.01f)
                lift = scroll > 0f ? 1 : -1;

            if (lift != 0)
                _paste.Nudge(_map, 0, 0, lift);

            if (!mouse.rightButton.isPressed && !mouse.middleButton.isPressed)
            {
                BuildHit hit = raycaster.RaycastPlacement(mouse.position.ReadValue());
                if (hit.Valid)
                    _paste.MoveTo(_map, hit.Cell);
            }

            // The held group, drawn down to whatever is under it. This is where the lines earn their
            // keep: a carried group has no contact with anything to give its height away, and the
            // wheel moves it through levels that all look alike from a fixed viewpoint.
            if (guides != null)
                guides.Show(_map, _paste.Parts);

            if (!mouse.leftButton.wasPressedThisFrame)
                return true;

            if (!_paste.Fits)
            {
                Status = "Will not fit there";
                return true;
            }

            List<PlacedPart> placed = _paste.Take();

            guides?.Hide();

            if (_history.Execute(new PasteCommand(_map, placed, PillarDefinition, Spawn)))
            {
                // Deliberately not left selected. A selection is drawn by tinting the pieces, so
                // keeping the group selected after it lands means every paste leaves coloured-looking
                // bricks behind - which is indistinguishable from having painted them.
                _selection.Clear();

                _clipboard = SelectionOps.Duplicate(placed);
                Status = $"Placed {placed.Count} - Cmd/Ctrl V again to place another";
            }

            return true;
        }

        /// <summary>
        /// Turning a held group ignores what is in the way.
        ///
        /// SelectionOps refuses a transform that will not fit, which is right for parts already in
        /// the build. A group in the air is not in the build yet, and refusing to turn it because its
        /// current position happens to clash would make it unturnable exactly where turning is what
        /// the player is trying to do.
        /// </summary>
        static List<PlacedPart> RotateFreely(List<PlacedPart> parts) =>
            SelectionOps.Rotate(null, parts, 1, checkFit: false);

        List<PlacedPart> MirrorFreely(List<PlacedPart> parts) =>
            SelectionOps.Mirror(null, parts, MirrorTwin, checkFit: false);

        void EnsurePasteSession()
        {
            _paste ??= new PasteSession(factory, partRoot, ghost != null ? ghost.ghostMaterial : null);
        }

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
        /// <summary>Empties the build, undoably. What G does.</summary>
        public void ClearAll()
        {
            var command = new ClearAllCommand(_map, Spawn);

            _selection.Clear();

            if (_history.Execute(command))
                Status = $"Cleared {command.Count} pieces - Cmd/Ctrl Z to put them back";
        }

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

        /// <summary>
        /// The creation last saved or loaded. Shown in the HUD; not where a new save goes.
        ///
        /// Saving used to write here every time, so there was only ever one creation and every S
        /// silently replaced it. A save now names itself by the moment it was taken, which needs no
        /// prompt, cannot collide, and sorts correctly on its own.
        /// </summary>
        public string SlotName = "(none)";

        [Tooltip("Brick used to prop up parts placed in mid-air. Falls back to the first block in the catalog.")]
        public string pillarPartId = "building_block_2x2";

        PartDefinition _pillar;

        /// <summary>Opened with L. Assigned by the scene builder.</summary>
        public SaveBrowser browser;

        /// <summary>Saves available to the browser.</summary>
        public SaveService Service => _saves;

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

            string slot = System.DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");

            try
            {
                await _saves.SaveAsync(_map, slot);

                // Captured after the save so a failed write never leaves a thumbnail without a
                // creation behind it.
                await _saves.SaveThumbnailAsync(slot, raycaster.Camera);

                SlotName = slot;
                Status = $"Saved '{slot}' ({_map.Parts.Count} parts)";
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

        /// <summary>Opens a saved creation by name. The browser calls this; L opens the browser.</summary>
        public async Awaitable LoadAsync(string slot)
        {
            if (Busy || _saves == null)
                return;

            Busy = true;
            Status = "Loading...";

            try
            {
                SaveModel model = await _saves.LoadAsync(slot);
                if (model == null)
                {
                    Status = $"No save named '{slot}'";
                    return;
                }

                SlotName = slot;

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
            int count = factory.Catalog.Selectable.Count;
            if (count == 0)
                return;

            _partIndex = ((_partIndex + step) % count + count) % count;
            _variant = 0;
            _held = null;
            FaceLastPlaced();
        }

        void UpdatePreviewAndPlacement()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            Vector2 screen = mouse.position.ReadValue();

            // Whether this reading can be believed at all - see PointerTrust, which owns the rule and
            // is exercised by the self test, because the previous version of it could strand the
            // editor: after a jump it doubted every frame that stayed far from the last trusted point,
            // and only trusted frames updated that point. Entering fullscreen moves every reading at
            // once, so the two held each other up and placing stopped working for good.
            //
            // Deliberately no test for the pointer being outside the window. It was added on a guess
            // and is not safe to make: the pointer and Screen.width are not always in the same pixels
            // - on a display where the browser reports one and the canvas the other, every reading
            // looks out of bounds, and the world stops accepting clicks entirely.
            bool suspect = _pointerTrust.IsSuspect(
                screen, new Vector2(Screen.width, Screen.height), Time.unscaledTime);

            if (suspect)
            {
                ghost.Hide();
                guides?.Hide();
                return;
            }

            // Before anything bows out. The pass returns early while the right button is held so the
            // camera can orbit, and a press-and-drag that is never seen here reads as a click when it
            // is finally released - which would pick up a piece every time the view was turned.
            Keyboard keyboard = Keyboard.current;
            bool holdingShift = keyboard != null &&
                                (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);

            if (CurrentTool == Tool.Place && !Pasting &&
                !(palette != null && (palette.Covers(screen) || palette.JustUsed)))
                UpdatePick(mouse, screen, holdingShift);

            // The palette sits over the world, and a click on it must not also land in the world.
            //
            // Judged from where the press began, not from where the pointer is now. A button changes
            // the tool the moment it is released, and the frame that follows has the pointer over the
            // bar with a click already spent - or, if the pointer has drifted off the bar by then,
            // over open ground with a live one. Either way the world sees a click the player aimed at
            // a button, and a piece lands somewhere out in the landscape.
            // The bar has just taken a click of its own, so this one is spent however the pointer is
            // reading. Ordering cannot fix it: the bar draws in OnGUI, after this has already run and
            // decided what to do with the same press.
            bool overPalette = palette != null && (palette.Covers(screen) || palette.JustUsed);

            if (mouse.leftButton.wasPressedThisFrame)
                _clickBeganOnPalette = overPalette;

            if (!mouse.leftButton.isPressed && !mouse.leftButton.wasReleasedThisFrame)
                _clickBeganOnPalette = false;

            if (overPalette || _clickBeganOnPalette)
            {
                ghost.Hide();
                guides?.Hide();
                return;
            }

            // Orbiting and panning should not scrub a ghost across the world.
            if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
            {
                ghost.Hide();
                guides?.Hide();
                return;
            }

            bool deleteMode = Keyboard.current != null &&
                              (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed);

            if (deleteMode)
            {
                ghost.Hide();
                guides?.Hide();
                if (mouse.leftButton.wasPressedThisFrame)
                    TryDelete(screen);
                return;
            }

            if (CurrentTool == Tool.Grab)
            {
                ghost.Hide();

                // What the selection is standing on, which is what you check before moving it.
                if (guides != null)
                    guides.Show(_map, _selection.Parts);

                return; // handled by the box-select pass, which also picks on a click
            }

            if (CurrentTool == Tool.Paint)
            {
                ghost.Hide();
                guides?.Hide();

                // Held, not just pressed, so a colour can be brushed across several pieces.
                if (mouse.leftButton.isPressed)
                    PaintUnderCursor(screen);

                return;
            }

            BuildHit hit = raycaster.RaycastPlacement(screen);
            if (!hit.Valid)
            {
                ghost.Hide();
                guides?.Hide();
                return;
            }

            bool wantsPrecise = holdingShift;

            // The wheel picks the level while a placement is held, so it must not also dolly.
            if (orbitCamera != null)
                orbitCamera.ZoomLocked = _precise;

            if (_precise)
            {
                float scroll = WheelDelta(mouse);

                if (Mathf.Abs(scroll) > 0.01f)
                    _levelStep = scroll > 0f ? 1 : -1;
            }

            // The cursor for this frame, worked out before anything is solved: on a plane at the held
            // piece's own height while a placement is held, otherwise whatever the ray struck.
            GridCoord cursor = hit.Cell;
            int wasAt = _lockedPlacement?.Origin.layer ?? 0;

            if (_precise && _lockedPlacement != null &&
                raycaster.RaycastLevel(screen, wasAt * GridCoord.LayerUnits, out GridCoord onPlane))
                cursor = onPlane;

            // Once. Solving twice a frame was the reason the wheel did nothing: the first solve
            // consumed the step and the second re-slid at the old level, so a turn of the wheel was
            // spent before the placement it was meant to move had been worked out.
            PlacedPart candidate = CandidateAt(cursor);

            UpdatePreciseLock(wantsPrecise, candidate, cursor);

            // A placement taken this frame is the one to show, rather than the free one that was on
            // screen a moment ago.
            if (_precise && _lockedPlacement != null)
                candidate = _lockedPlacement;

            // Changing level moves the plane the cursor is read from, so the reference is taken again
            // on the new one. Without this the height change is read as a sideways slide.
            if (_precise && _lockedPlacement != null && _lockedPlacement.Origin.layer != wasAt &&
                raycaster.RaycastLevel(screen,
                    _lockedPlacement.Origin.layer * GridCoord.LayerUnits, out GridCoord moved))
                _lockedCursor = moved;

            PlacementResult result = _map.CanPlace(candidate);

            // A placement below the ground is shown where it would be, sunk into the floor, so the
            // offer is visible rather than described. Choosing it lifts the build to make the room.
            bool needsGrowth = candidate.Origin.layer < 0;
            GrowthLayers = needsGrowth ? -candidate.Origin.layer : 0;

            ghost.Map = Map;
            ghost.Show(candidate, needsGrowth ? PlacementResult.Unsupported : result,
                factory.Catalog.ColorAt(_colorIndex));

            if (guides != null)
                guides.Show(_map, candidate);

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
        /// <summary>
        /// The placement to show and place, in the part's own colour where it has one.
        ///
        /// Applied here rather than at every call that solves a placement: the ghost, the lock and
        /// the click all come through this, and threading a colour choice through the solver would
        /// put the same decision in a dozen places.
        /// </summary>
        PlacedPart CandidateAt(GridCoord cursorCell)
        {
            PlacedPart candidate = SolveCandidate(cursorCell);

            if (candidate?.Definition == null || candidate.Definition.defaultColorIndex < 0)
                return candidate;

            var own = (byte)candidate.Definition.defaultColorIndex;

            return candidate.ColorIndex == own
                ? candidate
                : new PlacedPart(candidate.Definition, candidate.Origin, candidate.Rotation, own)
                {
                    Role = candidate.Role,
                };
        }

        PlacedPart SolveCandidate(GridCoord cursorCell)
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
            if (PlacementSolver.NearestOpenMouth(_map, cursorCell.CellCentre, MouthSearchRange,
                                                 out PlacedPart.WorldPort target))
            {
                // A part with mouths meets the run mouth to mouth. One without - a funnel - is caught
                // by its shelf instead, with the channel clutching down onto its studs.
                List<PlacedPart> matings = def.ports is { Length: > 0 }
                    ? PlacementSolver.MatingsWith(_map, def, _colorIndex, target, allowBelowGround: true)
                    : PlacementSolver.StudMatingsWith(_map, def, _colorIndex, target);

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
            PartDefinition def = _lockedPlacement.Definition;

            var moved = new GridCoord(
                origin.x + (cursorCell.x - _lockedCursor.x),
                origin.y + (cursorCell.y - _lockedCursor.y),
                origin.layer);

            // The levels available over the spot it has been slid to, and the one to settle at.
            _levels = PlacementSolver.LevelsAt(_map, def, moved.x, moved.y,
                _lockedPlacement.Rotation, _colorIndex);

            int layer = ChooseLevel(def, moved, _levelStep);
            _levelStep = 0;

            moved = new GridCoord(moved.x, moved.y, layer);

            // Fold the movement into the lock so R can re-solve from where the piece actually is.
            _lockedPlacement = new PlacedPart(def, moved, _lockedPlacement.Rotation, _colorIndex);
            _lockedCursor = cursorCell;

            return _lockedPlacement;
        }

        List<int> _levels = new();
        int _levelStep;

        /// <summary>
        /// The wheel. Vertical if there is any, horizontal only when there is not.
        ///
        /// A browser turns a vertical wheel into a horizontal one while shift is held - it is how a
        /// page is scrolled sideways - so in precise placement, which is held with shift, the y
        /// reading is always zero and the movement arrives on x. Reading only y left the wheel dead
        /// in the one mode it was added for.
        ///
        /// The two are read in order rather than by whichever is larger, because the platforms differ
        /// and only one of them needs the fallback. A native build reports the wheel on y with shift
        /// held like without it, so x there is a real sideways gesture - a trackpad swipe - and taking
        /// the larger of the two would let that scrub through levels by accident. Preferring y means
        /// the fallback can only fire where y is genuinely silent, which is the browser case alone.
        /// </summary>
        public static float WheelDelta(Mouse mouse)
        {
            Vector2 scroll = mouse.scroll.ReadValue();
            return Mathf.Abs(scroll.y) > 0.01f ? scroll.y : scroll.x;
        }

        /// <summary>Levels the wheel can reach where the held piece is - the floor is not one.</summary>
        public int LevelCount
        {
            get
            {
                int count = 0;

                foreach (int level in _levels)
                    if (level > 0)
                        count++;

                return count;
            }
        }

        /// <summary>
        /// Which of the available levels to settle at.
        ///
        /// The level is the player's, not the solver's: once a placement is held it stays at the
        /// height it was taken at however far it is slid, and only the wheel moves it. Re-deciding on
        /// every slide meant a piece dropped and rose as it crossed a build, which is the opposite of
        /// what holding it steady is for.
        ///
        /// The floor is never among the choices. Accurate placement is for meeting something already
        /// built; a piece that wants the ground can be dropped there without holding shift at all.
        /// </summary>
        int ChooseLevel(PartDefinition def, GridCoord at, int step)
        {
            if (step == 0 || _levels.Count == 0)
                return at.layer;

            // Levels that rest on the build. Sorted already, so the first at or above the current one
            // is where stepping starts from.
            var choices = new List<int>(_levels.Count);

            foreach (int level in _levels)
                if (level > 0)
                    choices.Add(level);

            if (choices.Count == 0)
                return at.layer;

            int index = choices.IndexOf(at.layer);

            if (index < 0)
            {
                // Not standing on one of them - start from whichever is nearest, so the first turn of
                // the wheel goes somewhere sensible rather than to the bottom of the list.
                int nearest = int.MaxValue;

                for (int i = 0; i < choices.Count; i++)
                {
                    int distance = Mathf.Abs(choices[i] - at.layer);

                    if (distance < nearest)
                    {
                        nearest = distance;
                        index = i;
                    }
                }

                // A step from nowhere lands on the nearest rather than passing over it.
                return choices[Mathf.Clamp(index, 0, choices.Count - 1)];
            }

            return choices[Mathf.Clamp(index + step, 0, choices.Count - 1)];
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

            // A join first: the placements this piece could make against a mouth near where it is
            // being held, stepped through by the same key. Only when there is no such join does R
            // mean turn it a quarter where it stands.
            if (def.ports is { Length: > 0 })
            {
                List<PlacedPart> ranked = PlacementSolver.SolveRanked(_map, def, at.x, at.y,
                    _rotation, _colorIndex);

                if (ranked.Count > 1)
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
                // player was looking at is the one they keep - lifted off the floor if the build
                // offers anything better, since the whole point of holding a placement is to meet
                // something already there.
                _precise = true;
                _lockedPlacement = LiftOffTheFloor(candidate);

                // Taken on the same plane the slide will use, so the first movement does not count a
                // step from wherever the geometry happened to put the cursor.
                _lockedCursor = raycaster.RaycastLevel(
                    Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero,
                    _lockedPlacement.Origin.layer * GridCoord.LayerUnits, out GridCoord onPlane)
                    ? onPlane
                    : cursorCell;
                Status = "Precise placement - slide to position, scroll for level";
            }
            else if (!wanted && _precise)
            {
                _precise = false;
                _lockedPlacement = null;

                // An unspent turn of the wheel does not carry into the next placement.
                _levelStep = 0;
                _levels.Clear();
            }
        }

        /// <summary>
        /// Moves a placement off layer 0 as it is picked up, when the build offers a level there.
        ///
        /// Without this a piece grabbed over open floor stays on the floor however far it is slid,
        /// because the level is deliberately sticky - and the one place accurate placement is never
        /// wanted is the ground, which needs no accuracy.
        /// </summary>
        PlacedPart LiftOffTheFloor(PlacedPart candidate)
        {
            if (candidate.Origin.layer != 0)
                return candidate;

            _levels = PlacementSolver.LevelsAt(_map, candidate.Definition,
                candidate.Origin.x, candidate.Origin.y, candidate.Rotation, _colorIndex);

            int lowest = int.MaxValue;

            foreach (int level in _levels)
                if (level > 0 && level < lowest)
                    lowest = level;

            if (lowest == int.MaxValue)
                return candidate;

            return new PlacedPart(candidate.Definition,
                new GridCoord(candidate.Origin.x, candidate.Origin.y, lowest),
                candidate.Rotation, candidate.ColorIndex);
        }

        Vector2 _pickDownAt;
        bool _pickDragged;

        /// <summary>Whether the click in progress started on the palette rather than in the world.</summary>
        bool _clickBeganOnPalette;

        /// <summary>Whether this frame's pointer reading can be believed.</summary>
        readonly PointerTrust _pointerTrust = new();

        /// <summary>
        /// Right click takes the piece under the cursor back into your hand.
        ///
        /// It is removed from the build and becomes the piece being placed, with the type, facing and
        /// colour it had - which is how a mistake gets corrected. Rebuilding that by hand means
        /// finding the part on the bar, turning it back to the angle it was at and matching its
        /// colour, and the piece is right there under the cursor already carrying all three.
        ///
        /// A right drag still orbits. The two are told apart by how far the pointer moved, as
        /// everywhere else in the editor.
        /// </summary>
        void UpdatePick(Mouse mouse, Vector2 screen, bool wantsPrecise)
        {
            if (mouse.rightButton.wasPressedThisFrame)
            {
                _pickDownAt = screen;
                _pickDragged = false;
            }

            if (mouse.rightButton.isPressed &&
                (screen - _pickDownAt).sqrMagnitude > 25f)
                _pickDragged = true;

            if (!mouse.rightButton.wasReleasedThisFrame || _pickDragged)
                return;

            BuildHit hit = raycaster.RaycastPick(screen);
            if (!hit.Valid || hit.Collider == null)
                return;

            var marker = hit.Collider.GetComponentInParent<PlacedPartMarker>();
            if (marker == null)
                return;

            PlacedPart picked = marker.Part;

            if (!_history.Execute(new RemovePartCommand(_map, picked, Spawn)))
                return;

            _selection.Clear();

            // Its own values, so the piece comes back exactly as it was unless something is changed.
            int index = factory.Catalog.Selectable.IndexOf(picked.Definition);

            if (index >= 0)
            {
                _partIndex = index;
                _held = null;
            }
            else
            {
                // Not on the bar, so it is held directly. Q, E or the palette let it go.
                _held = picked.Definition;
            }

            _rotation = picked.Rotation;
            _colorIndex = picked.ColorIndex;
            _variant = 0;

            if (!wantsPrecise)
            {
                Status = $"Picked up {picked.Definition.displayName}" +
                         (index >= 0 ? "" : " (held - not on the bar)");

                return;
            }

            // Held where it was, so shift and a right click together are "move this piece": it comes
            // out of the build already locked at its own position, and slides from there.
            _precise = true;
            _lockedPlacement = new PlacedPart(picked.Definition, picked.Origin, picked.Rotation,
                picked.ColorIndex);

            _lockedCursor = raycaster.RaycastLevel(screen,
                picked.Origin.layer * GridCoord.LayerUnits, out GridCoord onPlane)
                ? onPlane
                : hit.Cell;

            Status = $"Moving {picked.Definition.displayName} - slide to position, scroll for level";
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
