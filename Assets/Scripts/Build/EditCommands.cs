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
