using System.Collections.Generic;
using BlockMarbleRun.Persistence;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// The saved creations, as a wall of thumbnails.
    ///
    /// Saves have always carried a picture of themselves - one was captured on every save - and until
    /// now nothing ever showed them. A list of timestamps is a poor way to find a build you made last
    /// week; the picture is the only part of a save anyone actually recognises.
    /// </summary>
    public sealed class SaveBrowser : MonoBehaviour
    {
        public BuildController controller;

        public bool IsOpen { get; private set; }

        struct Entry
        {
            public SaveSlot Slot;
            public Texture2D Thumbnail;

            /// <summary>A creation that ships with the build rather than one of the player's own.</summary>
            public bool Bundled;
        }

        readonly List<Entry> _entries = new();

        Vector2 _scroll;
        GUIStyle _title;
        GUIStyle _label;
        string _status = "";
        bool _loading;

        public void Toggle()
        {
            if (IsOpen)
            {
                Close();
                return;
            }

            IsOpen = true;
            _ = RefreshAsync();
        }

        public void Close()
        {
            IsOpen = false;
            Release();
        }

        /// <summary>
        /// Thumbnails are created here, so they have to be destroyed here.
        ///
        /// A Texture2D built from PNG bytes is not garbage collected with the list holding it - it is
        /// a native allocation, and reopening the browser a few dozen times would leak every picture
        /// it had ever shown.
        /// </summary>
        void Release()
        {
            foreach (Entry entry in _entries)
                if (entry.Thumbnail != null)
                    Destroy(entry.Thumbnail);

            _entries.Clear();
        }

        void OnDestroy() => Release();

        async Awaitable RefreshAsync()
        {
            SaveService saves = controller != null ? controller.Service : null;
            if (saves == null)
                return;

            _loading = true;
            _status = "Reading saves...";
            Release();

            try
            {
                // The ones that come with the game, first and always: a player who has saved nothing
                // still has something to open, and on the web a build served from a new address has
                // an empty save list however much has been built before (BundledLevels).
                foreach (BundledLevels.Level level in BundledLevels.All)
                    _entries.Add(new Entry
                    {
                        Slot = new SaveSlot { name = level.Name },
                        Thumbnail = BundledLevels.ThumbnailFor(level.Name),
                        Bundled = true,
                    });

                SaveSlot[] slots = await saves.ListAsync();

                foreach (SaveSlot slot in slots)
                {
                    byte[] png = await saves.LoadThumbnailAsync(slot.name);

                    Texture2D texture = null;
                    if (png is { Length: > 0 })
                    {
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);

                        // A save from an older build may have no picture, or a broken one; the entry
                        // is still worth listing, so a failed decode costs the thumbnail and nothing else.
                        if (!texture.LoadImage(png))
                        {
                            Destroy(texture);
                            texture = null;
                        }
                    }

                    _entries.Add(new Entry { Slot = slot, Thumbnail = texture });
                }

                _status = _entries.Count == 0 ? "No saved creations yet - press S to save this one" : "";
            }
            catch (System.Exception e)
            {
                _status = $"Could not read saves: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                _loading = false;
            }
        }

        async Awaitable DeleteAsync(string slot)
        {
            SaveService saves = controller != null ? controller.Service : null;
            if (saves == null)
                return;

            try
            {
                await saves.DeleteAsync(slot);
                await RefreshAsync();
            }
            catch (System.Exception e)
            {
                _status = $"Could not delete: {e.Message}";
                Debug.LogException(e);
            }
        }

        void Update()
        {
            if (!IsOpen || Keyboard.current == null)
                return;

            if (Keyboard.current.escapeKey.wasPressedThisFrame)
                Close();
        }

        const float CardWidth = 200f;
        const float ThumbHeight = 120f;

        void OnGUI()
        {
            if (!IsOpen)
                return;

            UiScale.Begin();
            Draw();
            UiScale.End();
        }

        void Draw()
        {
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 17, normal = { textColor = Color.white } };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = Color.white } };

            float width = Mathf.Min(UiScale.Width - 80f, 900f);
            float height = Mathf.Min(UiScale.Height - 120f, 620f);
            var panel = new Rect((UiScale.Width - width) * 0.5f, (UiScale.Height - height) * 0.5f, width, height);

            // Drawn behind the panel so a click that misses a card does not fall through to the build
            // underneath and place a piece where the player was only looking.
            GUI.Box(panel, GUIContent.none);
            GUILayout.BeginArea(new Rect(panel.x + 14f, panel.y + 12f, panel.width - 28f, panel.height - 24f));

            GUILayout.Label($"Saved creations ({_entries.Count})    Esc or L to close", _title);

            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status, _label);

            if (_loading)
            {
                GUILayout.EndArea();
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll);

            int columns = Mathf.Max(1, Mathf.FloorToInt((panel.width - 40f) / (CardWidth + 10f)));
            int column = 0;

            GUILayout.BeginHorizontal();

            foreach (Entry entry in _entries)
            {
                if (column == columns)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    column = 0;
                }

                DrawCard(entry);
                column++;
            }

            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawCard(Entry entry)
        {
            GUILayout.BeginVertical(GUILayout.Width(CardWidth));

            // The whole picture is the button: a save is chosen by recognising it, so the thing being
            // recognised should be the thing you click.
            var content = entry.Thumbnail != null
                ? new GUIContent(entry.Thumbnail)
                : new GUIContent("no picture");

            if (GUILayout.Button(content, GUILayout.Width(CardWidth), GUILayout.Height(ThumbHeight)))
            {
                Close();

                if (entry.Bundled && BundledLevels.TryFind(entry.Slot.name, out BundledLevels.Level level))
                    controller.LoadBundled(level);
                else
                    _ = controller.LoadAsync(entry.Slot.name);

                return;
            }

            if (entry.Bundled)
            {
                // Named rather than dated, and with no Delete: it is part of the build, there is
                // nowhere to write it back to, and a button that could not work would only puzzle.
                GUILayout.Label(entry.Slot.name, _label);
                GUILayout.Label("comes with the game", _label);
            }
            else
            {
                System.DateTime when = System.DateTimeOffset
                    .FromUnixTimeSeconds(entry.Slot.savedAtUnixSeconds).ToLocalTime().DateTime;

                GUILayout.Label(when.ToString("d MMM yyyy, HH:mm"), _label);

                if (GUILayout.Button("Delete", GUILayout.Width(70f)))
                    _ = DeleteAsync(entry.Slot.name);
            }

            GUILayout.Space(8f);
            GUILayout.EndVertical();
        }
    }
}
