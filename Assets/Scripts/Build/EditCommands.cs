using System.Collections.Generic;
using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Build
{
    public interface IEditCommand
    {
        bool Do();
        void Undo();
    }

    /// <summary>
    /// Bounded undo/redo history.
    ///
    /// Built in from the first placement rather than retrofitted: once edits mutate the grid directly,
    /// reconstructing the inverse of each one after the fact means touching every call site.
    /// </summary>
    public sealed class CommandStack
    {
        readonly List<IEditCommand> _done = new();
        readonly List<IEditCommand> _undone = new();
        readonly int _limit;

        public CommandStack(int limit = 200) => _limit = limit;

        public bool CanUndo => _done.Count > 0;
        public bool CanRedo => _undone.Count > 0;

        public bool Execute(IEditCommand command)
        {
            if (!command.Do())
                return false;

            _done.Add(command);

            // A redo branch is invalidated by any new edit.
            _undone.Clear();

            if (_done.Count > _limit)
                _done.RemoveAt(0);

            return true;
        }

        public void Undo()
        {
            if (_done.Count == 0)
                return;

            IEditCommand command = _done[^1];
            _done.RemoveAt(_done.Count - 1);
            command.Undo();
            _undone.Add(command);
        }

        public void Redo()
        {
            if (_undone.Count == 0)
                return;

            IEditCommand command = _undone[^1];
            _undone.RemoveAt(_undone.Count - 1);
            command.Do();
            _done.Add(command);
        }

        public void Clear()
        {
            _done.Clear();
            _undone.Clear();
        }
    }

    /// <summary>
    /// Removes many parts as one history entry, so a box delete undoes in a single step rather than
    /// making the player press undo once per brick.
    /// </summary>
    public sealed class RemoveManyCommand : IEditCommand
    {
        readonly GridMap _map;
        readonly List<PlacedPart> _parts;
        readonly System.Func<PlacedPart, GameObject> _spawn;

        public RemoveManyCommand(GridMap map, IEnumerable<PlacedPart> parts, System.Func<PlacedPart, GameObject> spawn)
        {
            _map = map;
            _parts = new List<PlacedPart>(parts);
            _spawn = spawn;
        }

        public bool Do()
        {
            bool removedAny = false;

            foreach (PlacedPart part in _parts)
            {
                if (!_map.Remove(part))
                    continue;

                if (part.Instance != null)
                    Object.Destroy(part.Instance);

                part.Instance = null;
                removedAny = true;
            }

            return removedAny;
        }

        public void Undo()
        {
            foreach (PlacedPart part in _parts)
                if (_map.Add(part))
                    part.Instance = _spawn(part);
        }
    }

    /// <summary>
    /// Places a part together with any scaffolding it needs, as a single history entry.
    ///
    /// One undo has to remove the pillars too. Leaving them behind would strand a tower of bricks
    /// under nothing, and the player would have to delete by hand what they never asked to place.
    /// </summary>
    public sealed class PlaceWithSupportsCommand : IEditCommand
    {
        readonly GridMap _map;
        readonly PlacedPart _part;
        readonly Parts.PartDefinition _pillar;
        readonly System.Func<PlacedPart, GameObject> _spawn;

        List<PlacedPart> _supports = new();

        public PlaceWithSupportsCommand(GridMap map, PlacedPart part, Parts.PartDefinition pillar,
                                        System.Func<PlacedPart, GameObject> spawn)
        {
            _map = map;
            _part = part;
            _pillar = pillar;
            _spawn = spawn;
        }

        public int SupportCount => _supports.Count;

        public bool Do()
        {
            // Supports first: the part is only unsupported until they exist, and adding it first
            // would make the placement look valid for the wrong reason.
            _supports = ScaffoldBuilder.BuildSupports(_map, _part, _pillar);

            foreach (PlacedPart support in _supports)
                support.Instance = _spawn(support);

            if (_map.Add(_part))
            {
                _part.Instance = _spawn(_part);
                return true;
            }

            // The part would not go in after all; take the pillars back out rather than leaving them.
            UndoSupports();
            return false;
        }

        public void Undo()
        {
            _map.Remove(_part);

            if (_part.Instance != null)
                Object.Destroy(_part.Instance);

            _part.Instance = null;
            UndoSupports();
        }

        void UndoSupports()
        {
            foreach (PlacedPart support in _supports)
            {
                _map.Remove(support);

                if (support.Instance != null)
                    Object.Destroy(support.Instance);

                support.Instance = null;
            }
        }
    }

    /// <summary>
    /// Cycles a piece's role. Undoable like any other edit, so designating a goal by mistake costs
    /// one keypress to reverse rather than deleting and rebuilding the piece.
    /// </summary>
    public sealed class SetRoleCommand : IEditCommand
    {
        readonly PlacedPart _part;
        readonly Parts.PartRole _role;
        readonly System.Action<PlacedPart> _refresh;

        Parts.PartRole _previous;

        public SetRoleCommand(PlacedPart part, Parts.PartRole role, System.Action<PlacedPart> refresh)
        {
            _part = part;
            _role = role;
            _refresh = refresh;
        }

        public bool Do()
        {
            if (!_part.CanTakeRole || _part.Role == _role)
                return false;

            _previous = _part.Role;
            _part.Role = _role;
            _refresh(_part);
            return true;
        }

        public void Undo()
        {
            _part.Role = _previous;
            _refresh(_part);
        }
    }

    /// <summary>
    /// Repaints pieces. Batched, so painting across a drag undoes in one step rather than one per
    /// brick touched.
    /// </summary>
    public sealed class PaintCommand : IEditCommand
    {
        readonly List<PlacedPart> _parts;
        readonly byte _colour;
        readonly System.Action<PlacedPart> _refresh;

        byte[] _previous;

        public PaintCommand(IEnumerable<PlacedPart> parts, byte colour, System.Action<PlacedPart> refresh)
        {
            _parts = new List<PlacedPart>(parts);
            _colour = colour;
            _refresh = refresh;
        }

        public bool Do()
        {
            _previous = new byte[_parts.Count];

            bool changed = false;
            for (int i = 0; i < _parts.Count; i++)
            {
                _previous[i] = _parts[i].ColorIndex;
                if (_parts[i].ColorIndex == _colour)
                    continue;

                _parts[i].ColorIndex = _colour;
                _refresh(_parts[i]);
                changed = true;
            }

            return changed;
        }

        public void Undo()
        {
            for (int i = 0; i < _parts.Count; i++)
            {
                _parts[i].ColorIndex = _previous[i];
                _refresh(_parts[i]);
            }
        }
    }

    /// <summary>
    /// Moves a whole assembly up or down by whole layers, as one history entry.
    ///
    /// The parts themselves are immutable in position, so this swaps the originals for shifted
    /// copies. Undo swaps them back, which is why both sets are kept rather than recomputed - a
    /// recomputed original would be a different object and anything still referring to the old one,
    /// such as the selection, would be pointing at a piece that no longer exists.
    /// </summary>
    public sealed class MoveAssemblyCommand : IEditCommand
    {
        readonly GridMap _map;
        readonly List<PlacedPart> _before;
        readonly List<PlacedPart> _after;
        readonly Parts.PartDefinition _pillar;
        readonly System.Func<PlacedPart, GameObject> _spawn;

        List<PlacedPart> _supports = new();

        public MoveAssemblyCommand(GridMap map, List<PlacedPart> before, List<PlacedPart> after,
                                   Parts.PartDefinition pillar, System.Func<PlacedPart, GameObject> spawn)
        {
            _map = map;
            _before = before;
            _after = after;
            _pillar = pillar;
            _spawn = spawn;
        }

        public int SupportCount => _supports.Count;

        /// <summary>
        /// Hold the scaffolding back until the caller says so.
        ///
        /// Growing the build to fit a piece underneath makes room and then fills it: the pillars that
        /// prop the raised run stand in exactly the space the descending piece was heading for, so the
        /// placement is refused and the whole action rolls back without a word. The piece goes in
        /// first, and the supports are built around it.
        /// </summary>
        public bool DeferSupports { get; set; }

        public bool Do() => Swap(_before, _after, buildSupports: !DeferSupports);

        /// <summary>Runs the held-back scaffolding pass, once the rest of the action is in place.</summary>
        public void BuildDeferredSupports() => Scaffold(_after);

        public void Undo()
        {
            foreach (PlacedPart support in _supports)
            {
                _map.Remove(support);

                if (support.Instance != null)
                    Object.Destroy(support.Instance);

                support.Instance = null;
            }

            _supports.Clear();
            Swap(_after, _before, buildSupports: false);
        }

        bool Swap(List<PlacedPart> from, List<PlacedPart> to, bool buildSupports)
        {
            foreach (PlacedPart part in from)
            {
                _map.Remove(part);

                if (part.Instance != null)
                    Object.Destroy(part.Instance);

                part.Instance = null;
            }

            foreach (PlacedPart part in to)
                if (_map.Add(part))
                    part.Instance = _spawn(part);

            if (buildSupports)
                Scaffold(to);

            return true;
        }

        void Scaffold(List<PlacedPart> parts)
        {
            if (_pillar == null)
                return;

            // Raising a run leaves air under it; the same rule that props a newly placed piece applies.
            foreach (PlacedPart part in parts)
            {
                if (!ScaffoldBuilder.NeedsCarrying(part))
                    continue;

                foreach (PlacedPart support in ScaffoldBuilder.BuildSupports(_map, part, _pillar))
                {
                    support.Instance = _spawn(support);
                    _supports.Add(support);
                }
            }

            // And the bricks that were doing the holding are now hanging too.
            var lengthened = new List<(PlacedPart Old, PlacedPart New)>();

            foreach (PlacedPart support in ScaffoldBuilder.ExtendLiftedColumns(_map, parts, _pillar, lengthened))
            {
                support.Instance = _spawn(support);
                _supports.Add(support);
            }

            SwapLengthened(parts, lengthened);
        }

        /// <summary>
        /// Puts re-cut pillars in place of the ones they replace.
        ///
        /// The list the caller holds is what undo walks, so the replacement has to take the old
        /// one's seat in it - otherwise undo removes a pillar that is no longer there and leaves the
        /// longer one standing.
        /// </summary>
        void SwapLengthened(List<PlacedPart> parts, List<(PlacedPart Old, PlacedPart New)> lengthened)
        {
            foreach ((PlacedPart old, PlacedPart taller) in lengthened)
            {
                if (old.Instance != null)
                    Object.Destroy(old.Instance);

                old.Instance = null;
                taller.Instance = _spawn(taller);

                int seat = parts.IndexOf(old);
                if (seat >= 0)
                    parts[seat] = taller;
            }
        }
    }

    /// <summary>
    /// Takes the whole build away, and can put it back.
    ///
    /// Clearing used to reach into the map directly, which made it the one action in the editor that
    /// could not be undone - and the most expensive one to get wrong. It is an edit like any other
    /// and belongs in the history with them.
    /// </summary>
    public sealed class ClearAllCommand : IEditCommand
    {
        readonly GridMap _map;
        readonly List<PlacedPart> _parts;
        readonly System.Func<PlacedPart, GameObject> _spawn;

        public ClearAllCommand(GridMap map, System.Func<PlacedPart, GameObject> spawn)
        {
            _map = map;
            _parts = new List<PlacedPart>(map.Parts);
            _spawn = spawn;
        }

        public int Count => _parts.Count;

        public bool Do()
        {
            if (_parts.Count == 0)
                return false;

            foreach (PlacedPart part in _parts)
            {
                _map.Remove(part);

                if (part.Instance != null)
                    Object.Destroy(part.Instance);

                part.Instance = null;
            }

            return true;
        }

        public void Undo()
        {
            foreach (PlacedPart part in _parts)
                if (_map.Add(part))
                    part.Instance = _spawn(part);
        }
    }

    /// <summary>
    /// Adds a group of parts that were copied from elsewhere, propping whatever floats.
    ///
    /// The pillars are built once the whole group is down, not piece by piece as it goes in: a run
    /// scaffolded part by part would prop the second piece against nothing, since the first has no
    /// support under it yet and the third has not arrived. They are tracked separately from the
    /// pasted parts so undo can take back exactly what this command added and no more.
    /// </summary>
    public sealed class PasteCommand : IEditCommand
    {
        readonly GridMap _map;
        readonly List<PlacedPart> _parts;
        readonly Parts.PartDefinition _pillar;
        readonly System.Func<PlacedPart, GameObject> _spawn;

        readonly List<PlacedPart> _supports = new();

        public PasteCommand(GridMap map, List<PlacedPart> parts, Parts.PartDefinition pillar,
                            System.Func<PlacedPart, GameObject> spawn)
        {
            _map = map;
            _parts = parts;
            _pillar = pillar;
            _spawn = spawn;
        }

        public int SupportCount => _supports.Count;

        public bool Do()
        {
            foreach (PlacedPart part in _parts)
                if (_map.At(part.Origin) != null)
                    return false;

            foreach (PlacedPart part in _parts)
                if (_map.Add(part))
                    part.Instance = _spawn(part);

            if (_pillar == null)
                return true;

            foreach (PlacedPart part in _parts)
            {
                if (!ScaffoldBuilder.NeedsCarrying(part))
                    continue;

                foreach (PlacedPart support in ScaffoldBuilder.BuildSupports(_map, part, _pillar))
                {
                    support.Instance = _spawn(support);
                    _supports.Add(support);
                }
            }

            // And the bricks that came along in the copy. A selection usually includes the pillars
            // that were holding it up, and those arrive at the same height above the ground as they
            // left - which, pasted anywhere higher, leaves them hanging. Only new pillars were being
            // built, so the copy stood on fresh supports beside a column of its own that reached
            // nothing.
            var lengthened = new List<(PlacedPart Old, PlacedPart New)>();

            foreach (PlacedPart support in ScaffoldBuilder.ExtendLiftedColumns(_map, _parts, _pillar, lengthened))
            {
                support.Instance = _spawn(support);
                _supports.Add(support);
            }

            // A pillar that came along in the copy is re-cut to reach the ground rather than stood on
            // a tower of bricks. It takes the old one's place in the list undo walks.
            foreach ((PlacedPart old, PlacedPart taller) in lengthened)
            {
                if (old.Instance != null)
                    Object.Destroy(old.Instance);

                old.Instance = null;
                taller.Instance = _spawn(taller);

                int seat = _parts.IndexOf(old);
                if (seat >= 0)
                    _parts[seat] = taller;
            }

            return true;
        }

        public void Undo()
        {
            foreach (PlacedPart support in _supports)
            {
                _map.Remove(support);

                if (support.Instance != null)
                    Object.Destroy(support.Instance);

                support.Instance = null;
            }

            _supports.Clear();

            foreach (PlacedPart part in _parts)
            {
                _map.Remove(part);

                if (part.Instance != null)
                    Object.Destroy(part.Instance);

                part.Instance = null;
            }
        }
    }

    /// <summary>
    /// Lifts the whole build and places a piece in the room that makes, as one action.
    ///
    /// Growing and placing were two clicks before, which read as the build lurching upward for no
    /// stated reason and then needing the same placement made again. They are one intention and so
    /// they are one history entry: undo puts the build back down and takes the piece away together.
    /// </summary>
    public sealed class GrowAndPlaceCommand : IEditCommand
    {
        readonly MoveAssemblyCommand _grow;
        readonly PlaceWithSupportsCommand _place;

        public GrowAndPlaceCommand(MoveAssemblyCommand grow, PlaceWithSupportsCommand place)
        {
            _grow = grow;
            _place = place;

            // The order matters, so it is set here rather than left to the caller: lift, place, then
            // prop. Propping the lifted run first fills the very space the piece is descending into.
            _grow.DeferSupports = true;
        }

        public bool Do()
        {
            if (!_grow.Do())
                return false;

            if (_place.Do())
            {
                _grow.BuildDeferredSupports();
                return true;
            }

            // The room was made and the piece still would not go: put the build back rather than
            // leaving it raised around a placement that never happened.
            _grow.Undo();
            return false;
        }

        public void Undo()
        {
            _place.Undo();
            _grow.Undo();
        }
    }

    /// <summary>Adds a part to the grid and builds its scene object.</summary>
    public sealed class PlacePartCommand : IEditCommand
    {
        readonly GridMap _map;
        readonly PlacedPart _part;
        readonly System.Func<PlacedPart, GameObject> _spawn;

        public PlacePartCommand(GridMap map, PlacedPart part, System.Func<PlacedPart, GameObject> spawn)
        {
            _map = map;
            _part = part;
            _spawn = spawn;
        }

        public bool Do()
        {
            if (!_map.Add(_part))
                return false;

            _part.Instance = _spawn(_part);
            return true;
        }

        public void Undo()
        {
            _map.Remove(_part);

            if (_part.Instance != null)
                Object.Destroy(_part.Instance);

            _part.Instance = null;
        }
    }

    /// <summary>
    /// Removes a part. Undo re-spawns it, so the scene object is rebuilt rather than hidden - keeping
    /// one code path for creating part instances.
    /// </summary>
    public sealed class RemovePartCommand : IEditCommand
    {
        readonly GridMap _map;
        readonly PlacedPart _part;
        readonly System.Func<PlacedPart, GameObject> _spawn;

        public RemovePartCommand(GridMap map, PlacedPart part, System.Func<PlacedPart, GameObject> spawn)
        {
            _map = map;
            _part = part;
            _spawn = spawn;
        }

        public bool Do()
        {
            if (!_map.Remove(_part))
                return false;

            if (_part.Instance != null)
                Object.Destroy(_part.Instance);

            _part.Instance = null;
            return true;
        }

        public void Undo()
        {
            if (_map.Add(_part))
                _part.Instance = _spawn(_part);
        }
    }
}
