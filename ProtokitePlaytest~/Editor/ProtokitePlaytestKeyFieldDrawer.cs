using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Editor
{
    /// <summary>What one key press does to a key field that is detecting a key.</summary>
    internal enum ProtokitePlaytestKeyDetection
    {
        KeepDetecting,
        Cancelled,
        KeyChosen
    }

    /// <summary>A key setting set by pressing the key: Detect Key takes the next key pressed, and the list beside it offers every key.</summary>
    [CustomPropertyDrawer(typeof(ProtokitePlaytestKeyFieldAttribute))]
    internal sealed class ProtokitePlaytestKeyFieldDrawer : PropertyDrawer
    {
        internal const float DetectButtonWidth = 90f;
        private const float Gap = 2f;
        private static readonly int FieldHint = "ProtokitePlaytestKeyField".GetHashCode();
        private static readonly GUIContent DetectButton = new GUIContent("Detect Key", "Click, then press the key. Escape cancels.");
        private static readonly GUIContent CancelButton = new GUIContent("Cancel", "Stop detecting and keep the key.");
        private static readonly GUIContent PressAKey = new GUIContent("Press a key (Escape cancels)");

        // The field detecting a key, by object and property, so another object's field drawn in its place is not.
        private static string _detectingFor;
        // Whether Detect Key gave the field the keyboard, in which window: losing the keyboard there stops detecting.
        private static bool _detectingHasTheKeyboard;
        private static EditorWindow _detectingWindow;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            label = EditorGUI.BeginProperty(position, label, property);
            // Passive: only Detect Key gives this field the keyboard, never Tab.
            int id = GUIUtility.GetControlID(FieldHint, FocusType.Passive, position);
            Rect field = EditorGUI.PrefixLabel(position, id, label);
            Rect button = DetectButtonArea(field);
            Rect list = new Rect(field.x, field.y, Mathf.Max(0f, button.x - Gap - field.x), field.height);

            Event current = Event.current;
            if (IsDetecting(property))
            {
                // Another control took the keyboard, or a click on empty space cleared it; another window showing these settings never had it.
                if (_detectingHasTheKeyboard && (_detectingWindow == null || IsInTheDetectingWindow(position)) && GUIUtility.keyboardControl != id)
                {
                    StopDetecting(id);
                }
                else if (current.type == EventType.KeyDown || current.type == EventType.KeyUp)
                {
                    ProtokitePlaytestKeyDetection detection = Detect(current, property);
                    if (detection == ProtokitePlaytestKeyDetection.KeyChosen)
                        GUI.changed = true;
                    if (detection != ProtokitePlaytestKeyDetection.KeepDetecting)
                        StopDetecting(id);
                }
                // The raw type, since a field drawn above this one uses the press up. Not used here: the click goes on to what it was for.
                else if (IsAClickElsewhere(current.rawType, current.mousePosition, position))
                {
                    StopDetecting(id);
                }
            }

            bool detecting = IsDetecting(property);
            if (detecting)
            {
                if (current.type == EventType.Repaint)
                    EditorStyles.textField.Draw(list, PressAKey, id);
            }
            else
            {
                EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                KeyCode chosen = (KeyCode)EditorGUI.EnumPopup(list, (KeyCode)property.intValue);
                if (EditorGUI.EndChangeCheck())
                    property.intValue = (int)chosen;
                EditorGUI.showMixedValue = false;
            }

            if (GUI.Button(button, detecting ? CancelButton : DetectButton))
            {
                if (detecting)
                    StopDetecting(id);
                else
                    StartDetecting(property, id);
            }
            EditorGUI.EndProperty();
        }

        /// <summary>Where Detect Key is drawn in the part of the field right of its label: its right-hand end, never over the label.</summary>
        internal static Rect DetectButtonArea(Rect field)
        {
            float width = Mathf.Min(DetectButtonWidth, field.width);
            return new Rect(field.xMax - width, field.y, width, EditorGUIUtility.singleLineHeight);
        }

        /// <summary>What <paramref name="key"/> does while a field is detecting: Escape cancels, and a modifier, or an event with no key, is waited past.</summary>
        internal static ProtokitePlaytestKeyDetection DetectionFor(KeyCode key)
        {
            if (key == KeyCode.Escape)
                return ProtokitePlaytestKeyDetection.Cancelled;
            if (key == KeyCode.None || IsModifier(key))
                return ProtokitePlaytestKeyDetection.KeepDetecting;
            return ProtokitePlaytestKeyDetection.KeyChosen;
        }

        /// <summary>Whether an event is a press outside the field at <paramref name="field"/>, which stops detecting. A press on the field, its button included, does not.</summary>
        internal static bool IsAClickElsewhere(EventType type, Vector2 mouse, Rect field) => type == EventType.MouseDown && !field.Contains(mouse);

        /// <summary>Takes one key event for <paramref name="property"/>, which is set when a key is chosen. A press is used up either way, so no other control or shortcut acts on it.</summary>
        internal static ProtokitePlaytestKeyDetection Detect(Event keyEvent, SerializedProperty property)
        {
            KeyCode key = keyEvent.keyCode;
            // The editor keeps Escape's press from inspector fields (measured, 6000.3): only its release arrives.
            if (keyEvent.type == EventType.KeyUp)
            {
                if (key != KeyCode.Escape)
                    return ProtokitePlaytestKeyDetection.KeepDetecting;
                keyEvent.Use();
                return ProtokitePlaytestKeyDetection.Cancelled;
            }
            ProtokitePlaytestKeyDetection detection = DetectionFor(key);
            keyEvent.Use();
            if (detection == ProtokitePlaytestKeyDetection.KeyChosen)
                property.intValue = (int)key;
            return detection;
        }

        // Games use these with other keys, so a key pressed with one held is the key meant.
        private static bool IsModifier(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                case KeyCode.AltGr:
                case KeyCode.LeftCommand:
                case KeyCode.RightCommand:
                case KeyCode.LeftWindows:
                case KeyCode.RightWindows:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsDetecting(SerializedProperty property) => _detectingFor != null && _detectingFor == WhichField(property);

        private static void StartDetecting(SerializedProperty property, int id)
        {
            StopDetecting(0);
            _detectingFor = WhichField(property);
            // Taken so a text field that had the keyboard does not take the key too.
            GUIUtility.keyboardControl = id;
            _detectingHasTheKeyboard = true;
            // The window under the click; the focused window may still be the one the click came from.
            _detectingWindow = EditorWindow.mouseOverWindow;
        }

        // Whether the field is being drawn in the window Detect Key was clicked in, by where it is on the screen.
        private static bool IsInTheDetectingWindow(Rect field) => _detectingWindow.position.Contains(GUIUtility.GUIToScreenPoint(field.center));

        /// <summary>Starts detecting for <paramref name="property"/> without taking the keyboard, for tests: a window there neither lets a sent click press a button nor holds the keyboard.</summary>
        internal static void StartDetectingForTesting(SerializedProperty property)
        {
            StopDetecting(0);
            _detectingFor = WhichField(property);
        }

        /// <summary>Stops any detecting, for a test's tear-down.</summary>
        internal static void StopDetectingForTesting() => StopDetecting(0);

        // An id of 0 leaves the keyboard where it is.
        private static void StopDetecting(int id)
        {
            _detectingFor = null;
            _detectingHasTheKeyboard = false;
            _detectingWindow = null;
            if (id != 0 && GUIUtility.keyboardControl == id)
                GUIUtility.keyboardControl = 0;
        }

        private static string WhichField(SerializedProperty property) =>
            property.serializedObject.targetObject.GetInstanceID() + "/" + property.propertyPath;
    }
}
