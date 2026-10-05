using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Protokite.Playtest
{
    /// <summary>
    /// A full-screen UI Toolkit panel the playtest draws over the game, built from code with the package's empty theme: nothing
    /// to import, and no EventSystem of the game's needed. It holds one view at a time, so a second question can follow the first.
    /// </summary>
    internal sealed class ProtokitePlaytestPanel
    {
        // Above the game's own UI Toolkit panels and canvases.
        private const float SortingOrder = 30000f;
        // Runtime/Resources/ProtokitePlaytestPanelSettings.asset, which holds Runtime/Resources/ProtokitePlaytestPanelTheme.tss.
        internal const string PanelSettingsResource = "ProtokitePlaytestPanelSettings";

        internal static readonly Color TextColour = new Color(0.92f, 0.93f, 0.96f, 1f);
        internal static readonly Color HelpColour = new Color(0.62f, 0.65f, 0.72f, 1f);
        internal static readonly Color ProblemColour = new Color(1f, 0.55f, 0.5f, 1f);
        internal static readonly Color OptionColour = new Color(0.12f, 0.14f, 0.18f, 1f);
        internal static readonly Color OptionHighlightColour = new Color(0.19f, 0.23f, 0.30f, 1f);
        internal static readonly Color OptionChosenColour = new Color(0.22f, 0.36f, 0.62f, 1f);
        internal static readonly Color OptionBorderColour = new Color(0.25f, 0.29f, 0.36f, 1f);
        internal static readonly Color OptionFocusBorderColour = new Color(0.55f, 0.68f, 0.95f, 1f);
        private static readonly Color CardColour = new Color(0.04f, 0.05f, 0.07f, 0.97f);
        private static readonly Color ScreenDimColour = new Color(0f, 0f, 0f, 0.6f);

        private readonly GameObject _host;
        private readonly PanelSettings _panelSettings;
        private readonly CursorLockMode _cursorLockBefore;
        private readonly bool _cursorVisibleBefore;
        private bool _freedTheCursor;
        private int _frameTheGameLockedTheCursorAgain = int.MinValue / 2;

        private ProtokitePlaytestPanel(GameObject host, PanelSettings panelSettings, VisualElement root, VisualElement card)
        {
            _host = host;
            _panelSettings = panelSettings;
            Root = root;
            Card = card;
            _cursorLockBefore = Cursor.lockState;
            _cursorVisibleBefore = Cursor.visible;
        }

        /// <summary>The whole panel, over the whole screen.</summary>
        internal VisualElement Root { get; }

        /// <summary>The box in the middle a view is shown in.</summary>
        internal VisualElement Card { get; }

        /// <summary>The view on show, or null.</summary>
        internal VisualElement View { get; private set; }

        /// <summary>Whether a panel can be drawn now: playing, not in batch mode, and with graphics.</summary>
        internal static bool CanBeDrawn
            => Application.isPlaying && !Application.isBatchMode && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Puts an empty panel over the game. Main thread only.</summary>
        internal static ProtokitePlaytestPanel Open(string name)
        {
            GameObject host = new GameObject(name) { hideFlags = HideFlags.HideInHierarchy };
            Object.DontDestroyOnLoad(host);
            // The document reads its panel settings as it is enabled, so they are given before it is.
            host.SetActive(false);
            UIDocument document = host.AddComponent<UIDocument>();
            PanelSettings panelSettings = NewPanelSettings();
            panelSettings.name = name;
            panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panelSettings.referenceResolution = new Vector2Int(1280, 720);
            panelSettings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panelSettings.match = 0.5f;
            panelSettings.sortingOrder = SortingOrder;
            document.panelSettings = panelSettings;
            host.SetActive(true);

            VisualElement root = document.rootVisualElement;
            root.style.position = Position.Absolute;
            root.style.left = 0;
            root.style.top = 0;
            root.style.right = 0;
            root.style.bottom = 0;
            root.style.backgroundColor = ScreenDimColour;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;
            // No theme is loaded, so the text has a font only if one is given; children take it from here.
            root.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(BuiltInFont()));
            root.style.color = TextColour;

            VisualElement card = new VisualElement { name = "protokite-panel-card" };
            card.style.width = 640;
            card.style.maxWidth = Length.Percent(92);
            card.style.backgroundColor = CardColour;
            card.style.paddingLeft = 28;
            card.style.paddingRight = 28;
            card.style.paddingTop = 24;
            card.style.paddingBottom = 24;
            SetRadius(card.style, 8);
            root.Add(card);
            return new ProtokitePlaytestPanel(host, panelSettings, root, card);
        }

        /// <summary>Shows this view in place of the one before, with the view itself focused and no button in it.</summary>
        // A focused button would be pressed by a game's own Submit key (Space, in Unity's default input), so a player jumping at the
        // game would answer unread. With nothing focused, keys reach no element at all, so the view holds focus: a keyboard or pad
        // player's first move goes to an answer.
        internal void Show(VisualElement view)
        {
            Card.Clear();
            Card.Add(view);
            View = view;
            view.focusable = true;
            view.Focus();
        }

        /// <summary>
        /// Once a frame while open: a game that locks or hides its cursor would leave the player no way to answer, so the cursor
        /// is shown and free until the panel closes, and put back as the game had it then.
        /// </summary>
        internal void KeepCursorFree()
        {
            if (Cursor.lockState != CursorLockMode.None)
            {
                // Freed before and locked again since: the game locks it every frame, and a click lands at the centre.
                if (_freedTheCursor && Cursor.lockState == CursorLockMode.Locked)
                    _frameTheGameLockedTheCursorAgain = Time.frameCount;
                Cursor.lockState = CursorLockMode.None;
                _freedTheCursor = true;
            }
            if (!Cursor.visible)
                Cursor.visible = true;
        }

        /// <summary>Whether the game locked the cursor again within the last frames, so a mouse press says nothing about where the player aimed.</summary>
        internal bool TheGameKeepsTheCursorLocked => Time.frameCount - _frameTheGameLockedTheCursorAgain <= 2;

        /// <summary>Takes the panel off the screen and gives the cursor back as the game had it.</summary>
        internal void Close()
        {
            // Gone already when its launch ended (a Play Mode session with domain reload off): that launch's cursor is not this one's.
            bool wasOnScreen = _host != null;
            if (wasOnScreen)
                Object.Destroy(_host);
            // The clone only: its theme is the package's own asset, which every panel shares.
            if (_panelSettings != null)
                Object.Destroy(_panelSettings);
            if (!wasOnScreen)
                return;
            Cursor.lockState = _cursorLockBefore;
            Cursor.visible = _cursorVisibleBefore;
        }

        /// <summary>
        /// A press this soon after a question appears is ignored: a player still clicking at the game (firing, say), or clicking twice on
        /// the question before, would otherwise answer one they never read, with whichever button sits under the cursor.
        /// </summary>
        internal const float SecondsBeforeAnAnswerCounts = 0.5f;

        /// <summary>
        /// Hands <paramref name="chosen"/> only a deliberate answer: none in the question's first half second, and no mouse press while
        /// <paramref name="theGameKeepsTheCursorLocked"/> says so (it lands at the centre, not where the player aimed), which
        /// <paramref name="pressIgnoredForALockedCursor"/> is told of. Made as the question is shown.
        /// </summary>
        internal static System.Action<TChoice, EventBase> DeliberateAnswers<TChoice>(System.Action<TChoice> chosen, System.Func<bool> theGameKeepsTheCursorLocked,
            System.Action pressIgnoredForALockedCursor)
        {
            double shownAt = Time.realtimeSinceStartupAsDouble;
            return (choice, press) =>
            {
                if (Time.realtimeSinceStartupAsDouble - shownAt < SecondsBeforeAnAnswerCounts)
                    return;
                if ((press is IPointerEvent || press is IMouseEvent) && theGameKeepsTheCursorLocked())
                {
                    pressIgnoredForALockedCursor();
                    return;
                }
                chosen(choice);
            };
        }

        /// <summary>A question's heading and introduction, its answers, and a footnote, as one view.</summary>
        internal static VisualElement QuestionView(string name, string heading, string introduction, IEnumerable<Button> answers, string footnote)
        {
            VisualElement view = new VisualElement { name = name };
            view.Add(Text(heading, 22, TextColour, bold: true));

            Label introductionLabel = Text(introduction, 13, HelpColour);
            introductionLabel.style.marginTop = 8;
            introductionLabel.style.marginBottom = 18;
            view.Add(introductionLabel);

            foreach (Button answer in answers)
                view.Add(answer);

            Label footnoteLabel = Text(footnote, 12, HelpColour);
            footnoteLabel.style.marginTop = 6;
            view.Add(footnoteLabel);
            return view;
        }

        /// <summary>An answer as a button, found by its name: its title over its explanation, with a look of its own for hover and focus.</summary>
        internal static Button AnswerButton(string name, string title, string explanation, System.Action<EventBase> pressed)
        {
            Button button = new Button { name = name, text = "" };
            // With the press that made it, so a mouse press is told from a key or pad press.
            button.clickable.clickedWithEventInfo += pressed;
            button.style.flexDirection = FlexDirection.Column;
            button.style.alignItems = Align.Stretch;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.marginTop = 0;
            button.style.marginBottom = 10;
            button.style.paddingLeft = 14;
            button.style.paddingRight = 14;
            button.style.paddingTop = 11;
            button.style.paddingBottom = 11;
            button.style.backgroundColor = OptionColour;
            SetBorder(button.style, 1, OptionBorderColour);
            SetRadius(button.style, 6);

            Label titleLabel = Text(title, 15, TextColour, bold: true);
            titleLabel.pickingMode = PickingMode.Ignore;
            Label explanationLabel = Text(explanation, 12, HelpColour);
            explanationLabel.pickingMode = PickingMode.Ignore;
            explanationLabel.style.marginTop = 3;
            button.Add(titleLabel);
            button.Add(explanationLabel);

            // No theme gives hover or focus a look, so the button gives itself one.
            button.RegisterCallback<PointerEnterEvent>(_ => button.style.backgroundColor = OptionHighlightColour);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.style.backgroundColor = OptionColour);
            button.RegisterCallback<FocusInEvent>(_ => SetBorder(button.style, 2, OptionFocusBorderColour));
            button.RegisterCallback<FocusOutEvent>(_ => SetBorder(button.style, 1, OptionBorderColour));
            return button;
        }

        internal static Label Text(string text, int size, Color colour, bool bold = false)
        {
            Label label = new Label(text);
            label.style.fontSize = size;
            label.style.color = colour;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
            label.style.marginLeft = 0;
            label.style.marginRight = 0;
            label.style.paddingLeft = 0;
            label.style.paddingRight = 0;
            return label;
        }

        internal static void SetRadius(IStyle style, float radius)
        {
            style.borderTopLeftRadius = radius;
            style.borderTopRightRadius = radius;
            style.borderBottomLeftRadius = radius;
            style.borderBottomRightRadius = radius;
        }

        internal static void SetBorder(IStyle style, float width, Color colour)
        {
            style.borderLeftWidth = width;
            style.borderRightWidth = width;
            style.borderTopWidth = width;
            style.borderBottomWidth = width;
            style.borderLeftColor = colour;
            style.borderRightColor = colour;
            style.borderTopColor = colour;
            style.borderBottomColor = colour;
        }

        // A copy of the package's asset with its empty theme: on Unity 2021.3 settings made in code warn the moment they exist, and an empty theme made in code throws (measured).
        private static PanelSettings NewPanelSettings()
        {
            PanelSettings asset = Resources.Load<PanelSettings>(PanelSettingsResource);
            if (asset != null)
                return Object.Instantiate(asset);
            // Only if the package's asset was deleted: no theme, which logs the warning, never the exception.
            return ScriptableObject.CreateInstance<PanelSettings>();
        }

        // The font every player has: Unity renamed it in 2022.2.
        private static Font BuiltInFont()
        {
#if UNITY_2022_2_OR_NEWER
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }
    }
}
