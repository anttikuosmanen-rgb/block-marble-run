using BlockMarbleRun.Parts;
using UnityEngine;

namespace BlockMarbleRun.Build
{
    /// <summary>
    /// Icon bar across the top for choosing a part, plus the tool buttons.
    ///
    /// IMGUI, like the rest of the HUD. DESIGN.md §5 puts the real palette in UI Toolkit at M5, and
    /// this was written on the understanding that building it twice would be waste - but the whole
    /// HUD is due to be rebuilt there anyway, and cycling parts with Q and E in the meantime is the
    /// single most awkward thing about using the game. A working bar now costs less than the detour.
    /// </summary>
    public sealed class PartPalette : MonoBehaviour
    {
        public BuildController controller;
        public BlockMarbleRun.Core.GameMode mode;

        [Tooltip("Largest icon edge length in pixels; the bar shrinks them to fit a narrow window.")]
        public float maxIconSize = 56f;

        [Tooltip("Below this the icons stop shrinking and the bar takes another row instead.")]
        public float minIconSize = 30f;

        [Tooltip("Rows the bar may grow to before icons shrink further.")]
        public int maxRows = 3;

        public float Height { get; private set; }

        /// <summary>
        /// Whether the bar covers this point, in Input's bottom-left screen coordinates.
        ///
        /// The build controller reads the mouse directly rather than through IMGUI, so without this a
        /// click on a palette icon also places a brick in the world behind it.
        /// </summary>
        public bool Covers(Vector2 screenPosition) =>
            Height > 0f && screenPosition.y >= Screen.height - Height;

        GUIStyle _iconStyle;
        GUIStyle _labelStyle;
        GUIStyle _badgeStyle;
        Texture2D _selectedBackground;
        Texture2D _barBackground;
        Texture2D _activeToolBackground;

        void OnGUI()
        {
            if (controller == null || controller.CatalogPartCount <= 0)
                return;

            // Building only. The bar would be dead weight over a run in progress.
            if (mode != null && mode.Current != BlockMarbleRun.Core.Mode.Play)
                Draw();
            else if (mode == null)
                Draw();
            else
                Height = 0f;
        }

        void Draw()
        {
            EnsureStyles();

            PartCatalog catalog = controller.factory.Catalog;

            const float pad = 6f;
            float available = Screen.width - pad * 2f;

            // Fit the whole set to the window rather than assuming a size: take another row before
            // shrinking past legibility, and cap the icons so a wide window does not blow them up.
            int count = Mathf.Max(1, catalog.parts.Count);
            int rows = 1;
            int perRow = count;
            float iconSize = 0f;

            for (; rows <= Mathf.Max(1, maxRows); rows++)
            {
                perRow = Mathf.CeilToInt(count / (float)rows);
                iconSize = (available - pad * (perRow - 1)) / perRow;

                if (iconSize >= minIconSize)
                    break;
            }

            rows = Mathf.Min(rows, Mathf.Max(1, maxRows));
            perRow = Mathf.Max(1, Mathf.CeilToInt(count / (float)rows));
            iconSize = Mathf.Clamp((available - pad * (perRow - 1)) / perRow, minIconSize, maxIconSize);

            float toolbar = 26f;
            Height = rows * (iconSize + pad) + pad + toolbar;

            GUI.DrawTexture(new Rect(0, 0, Screen.width, Height), _barBackground);

            DrawTools(pad, toolbar);

            for (int i = 0; i < catalog.parts.Count; i++)
            {
                PartDefinition def = catalog.parts[i];
                if (def == null)
                    continue;

                int row = i / perRow;
                int column = i % perRow;

                var rect = new Rect(
                    pad + column * (iconSize + pad),
                    toolbar + pad + row * (iconSize + pad),
                    iconSize, iconSize);

                if (i == controller.SelectedIndex)
                    GUI.DrawTexture(new Rect(rect.x - 3, rect.y - 3, rect.width + 6, rect.height + 6), _selectedBackground);

                // The tooltip carries the name, so the icons stay large enough to read at a glance.
                var content = def.icon != null
                    ? new GUIContent(def.icon, def.displayName)
                    : new GUIContent(Abbreviate(def.displayName), def.displayName);

                if (GUI.Button(rect, content, _iconStyle))
                    controller.SelectPart(i);
            }

            if (!string.IsNullOrEmpty(GUI.tooltip))
                GUI.Label(new Rect(pad, Height - 2f, 400f, 20f), GUI.tooltip, _labelStyle);

            DrawCursorBadge();
        }

        /// <summary>
        /// Names the live tool beside the pointer while it is anything but placing.
        ///
        /// The toolbar sits at the top of the screen and the cursor spends its time at the build, so
        /// a highlight up there is easy to forget about - and forgetting whether the next click paints
        /// or deletes is how a build gets damaged.
        /// </summary>
        void DrawCursorBadge()
        {
            BuildController.Tool tool = controller.CurrentTool;
            if (tool == BuildController.Tool.Place)
                return;

            Vector2 mouse = Event.current.mousePosition;
            var rect = new Rect(mouse.x + 16f, mouse.y + 12f, 62f, 20f);

            GUI.DrawTexture(rect, _activeToolBackground);
            GUI.Label(new Rect(rect.x + 6f, rect.y + 2f, rect.width, rect.height),
                tool == BuildController.Tool.Paint ? "Paint" : "Grab", _badgeStyle);

            if (tool != BuildController.Tool.Paint)
                return;

            // The brush colour travels with the cursor: which colour is loaded is the other half of
            // what a click is about to do.
            PartCatalog catalog = controller.factory.Catalog;
            var swatch = new Rect(rect.xMax + 4f, rect.y, 20f, 20f);
            GUI.DrawTexture(swatch, Swatch(controller.ColourIndex, catalog.ColorAt(controller.ColourIndex)));
        }

        void DrawTools(float pad, float toolbar)
        {
            BuildController.Tool tool = controller.CurrentTool;
            float x = pad;
            float height = toolbar - 6f;

            x = ToolButton(x, 3f, height, "Place", BuildController.Tool.Place, tool);
            x = ToolButton(x, 3f, height, "Grab", BuildController.Tool.Grab, tool);
            x = ToolButton(x, 3f, height, "Paint", BuildController.Tool.Paint, tool);

            x += 8f;
            x = DrawSwatches(x, 3f, height);

            GUI.Label(new Rect(x + 10f, 5f, 520f, toolbar), HintFor(tool), _labelStyle);
        }

        float ToolButton(float x, float y, float height, string label, BuildController.Tool tool,
                         BuildController.Tool current)
        {
            const float width = 76f;
            var rect = new Rect(x, y, width, height);

            // Filled block behind the live tool, not a glyph. Which tool is active decides what every
            // click does, so it has to be readable without hunting for a marker in the label.
            if (tool == current)
                GUI.DrawTexture(new Rect(rect.x - 3, rect.y - 3, width + 6, height + 6), _activeToolBackground);

            if (GUI.Button(rect, label))
                controller.SetTool(tool);

            return x + width + 4f;
        }

        /// <summary>
        /// Colour swatches. Picking one also switches to the brush, since choosing a colour with no
        /// way to apply it is a dead end.
        /// </summary>
        float DrawSwatches(float x, float y, float height)
        {
            PartCatalog catalog = controller.factory.Catalog;
            float size = height;

            for (int i = 0; i < catalog.palette.Length; i++)
            {
                var rect = new Rect(x, y, size, size);

                if (i == controller.ColourIndex)
                    GUI.DrawTexture(new Rect(rect.x - 2, rect.y - 2, size + 4, size + 4), _selectedBackground);

                GUI.DrawTexture(rect, Swatch(i, catalog.palette[i]));

                if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                    controller.SelectColour((byte)i);

                x += size + 4f;
            }

            return x;
        }

        static string HintFor(BuildController.Tool tool) => tool switch
        {
            BuildController.Tool.Grab => "click a piece to pick it    Del removes    shift adds",
            BuildController.Tool.Paint => "click or drag over pieces to paint them",
            _ => "click to place    V grab    B brush",
        };

        /// <summary>Fallback label when a part has no baked icon, so the bar still works.</summary>
        static string Abbreviate(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "?";

            return name.Length <= 6 ? name : name[..6];
        }

        Texture2D[] _swatches;

        Texture2D Swatch(int index, Color colour)
        {
            _swatches ??= new Texture2D[32];

            if (index >= _swatches.Length)
                return Texture2D.whiteTexture;

            return _swatches[index] ??= Solid(colour);
        }

        void EnsureStyles()
        {
            if (_iconStyle != null)
                return;

            _iconStyle = new GUIStyle(GUI.skin.button) { padding = new RectOffset(2, 2, 2, 2) };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = Color.white } };
            _badgeStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = Color.black } };

            _selectedBackground = Solid(new Color(0.35f, 0.75f, 1f, 0.9f));
            _activeToolBackground = Solid(new Color(1f, 0.72f, 0.15f, 1f));
            _barBackground = Solid(new Color(0.06f, 0.07f, 0.09f, 0.85f));
        }

        static Texture2D Solid(Color colour)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, colour);
            texture.Apply();
            return texture;
        }
    }
}
