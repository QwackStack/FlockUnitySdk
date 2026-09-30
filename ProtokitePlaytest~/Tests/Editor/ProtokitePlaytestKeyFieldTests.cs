using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Protokite.Playtest.Editor;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Tests
{
    /// <summary>The Feedback Form Key field: Detect Key sets the key from the next key pressed, through the settings' real inspector drawing.</summary>
    public class ProtokitePlaytestKeyFieldTests
    {
        private const string FieldName = "feedbackFormKey";
        private static readonly Rect FieldArea = new Rect(0f, 0f, 420f, 18f);

        private ProtokitePlaytestSettings _settings;
        private ProtokitePlaytestSettings _otherSettings;
        private KeyFieldWindow _window;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();
            _otherSettings = ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_window != null)
                _window.Close();
            ProtokitePlaytestKeyFieldDrawer.StopDetectingForTesting();
            Undo.ClearUndo(_settings);
            Object.DestroyImmediate(_settings);
            Object.DestroyImmediate(_otherSettings);
        }

        // ---- What each key does while detecting

        [Test]
        public void AnyKeyboardKeyIsChosen()
        {
            foreach (KeyCode key in new[] { KeyCode.F8, KeyCode.A, KeyCode.Space, KeyCode.Alpha1, KeyCode.Keypad5, KeyCode.Tab, KeyCode.Return, KeyCode.Backspace, KeyCode.UpArrow })
                Assert.AreEqual(ProtokitePlaytestKeyDetection.KeyChosen, ProtokitePlaytestKeyFieldDrawer.DetectionFor(key), key.ToString());
        }

        [Test]
        public void EscapeCancels()
        {
            Assert.AreEqual(ProtokitePlaytestKeyDetection.Cancelled, ProtokitePlaytestKeyFieldDrawer.DetectionFor(KeyCode.Escape));
        }

        [Test]
        public void AModifierAloneOrAnEventWithNoKeyIsWaitedPast()
        {
            foreach (KeyCode key in new[] { KeyCode.None, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftAlt,
                         KeyCode.RightAlt, KeyCode.AltGr, KeyCode.LeftCommand, KeyCode.RightCommand, KeyCode.LeftWindows, KeyCode.RightWindows })
                Assert.AreEqual(ProtokitePlaytestKeyDetection.KeepDetecting, ProtokitePlaytestKeyFieldDrawer.DetectionFor(key), key.ToString());
        }

        [Test]
        public void AChosenKeyIsSetAndThePressUsedUp()
        {
            SerializedObject serialized = new SerializedObject(_settings);
            Event press = KeyDown(KeyCode.F8);

            Assert.AreEqual(ProtokitePlaytestKeyDetection.KeyChosen, ProtokitePlaytestKeyFieldDrawer.Detect(press, serialized.FindProperty(FieldName)));
            serialized.ApplyModifiedProperties();

            Assert.AreEqual(KeyCode.F8, _settings.FeedbackFormKey);
            Assert.AreEqual(EventType.Used, press.type, "No other control or shortcut acts on the press");
        }

        [Test]
        public void EscapeAndACharacterEventLeaveTheKeyAndAreUsedUp()
        {
            SerializedObject serialized = new SerializedObject(_settings);
            // IMGUI follows a printable key's press with a second event carrying only its character; taken as a key it would read None, turning the key off.
            Event character = new Event { type = EventType.KeyDown, keyCode = KeyCode.None, character = 'a' };
            Event escape = KeyDown(KeyCode.Escape);

            Assert.AreEqual(ProtokitePlaytestKeyDetection.KeepDetecting, ProtokitePlaytestKeyFieldDrawer.Detect(character, serialized.FindProperty(FieldName)));
            Assert.AreEqual(ProtokitePlaytestKeyDetection.Cancelled, ProtokitePlaytestKeyFieldDrawer.Detect(escape, serialized.FindProperty(FieldName)));
            serialized.ApplyModifiedProperties();

            Assert.AreEqual(KeyCode.F9, _settings.FeedbackFormKey);
            Assert.AreEqual(EventType.Used, character.type);
            Assert.AreEqual(EventType.Used, escape.type);
        }

        [Test]
        public void EscapesReleaseCancelsAndAnotherKeysReleaseIsWaitedPast()
        {
            // The editor keeps Escape's press from inspector fields, so its release is what arrives.
            SerializedObject serialized = new SerializedObject(_settings);
            Event otherRelease = new Event { type = EventType.KeyUp, keyCode = KeyCode.F8 };
            Event escapeRelease = new Event { type = EventType.KeyUp, keyCode = KeyCode.Escape };

            Assert.AreEqual(ProtokitePlaytestKeyDetection.KeepDetecting, ProtokitePlaytestKeyFieldDrawer.Detect(otherRelease, serialized.FindProperty(FieldName)));
            Assert.AreEqual(ProtokitePlaytestKeyDetection.Cancelled, ProtokitePlaytestKeyFieldDrawer.Detect(escapeRelease, serialized.FindProperty(FieldName)));
            serialized.ApplyModifiedProperties();

            Assert.AreEqual(KeyCode.F9, _settings.FeedbackFormKey, "A release sets nothing");
            Assert.AreEqual(EventType.KeyUp, otherRelease.type, "Another key's release is left for whatever it belongs to");
            Assert.AreEqual(EventType.Used, escapeRelease.type);
        }

        [Test]
        public void UndoPutsTheOldKeyBack()
        {
            SerializedObject serialized = new SerializedObject(_settings);
            Undo.IncrementCurrentGroup();
            ProtokitePlaytestKeyFieldDrawer.Detect(KeyDown(KeyCode.F8), serialized.FindProperty(FieldName));
            serialized.ApplyModifiedProperties();
            Assert.AreEqual(KeyCode.F8, _settings.FeedbackFormKey, "Precondition: the key was set");

            Undo.PerformUndo();

            Assert.AreEqual(KeyCode.F9, _settings.FeedbackFormKey);
        }

        // ---- Through the inspector's own drawing

        [Test]
        public void TheFeedbackFormKeyIsDrawnAsAKeyField()
        {
            FieldInfo field = typeof(ProtokitePlaytestSettings).GetField(FieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field);
            Assert.IsNotNull(field.GetCustomAttribute<ProtokitePlaytestKeyFieldAttribute>());
        }

        [Test]
        public void WhileDetectingTheNextKeyIsSetAndTheKeyAfterIsNot()
        {
            OpenWindowOn(_settings);
            Press(KeyCode.F7);
            Assert.AreEqual(KeyCode.F9, _settings.FeedbackFormKey, "A key pressed before detecting sets nothing");

            StartDetecting();
            Press(KeyCode.F8);
            Assert.AreEqual(KeyCode.F8, _settings.FeedbackFormKey, "The key pressed while detecting is set");
            Press(KeyCode.F7);

            Assert.AreEqual(KeyCode.F8, _settings.FeedbackFormKey, "Detecting stops at the first key");
        }

        [Test]
        public void EscapesReleaseStopsDetecting()
        {
            OpenWindowOn(_settings);
            StartDetecting();
            Release(KeyCode.Escape);
            Press(KeyCode.F8);
            Assert.AreEqual(KeyCode.F9, _settings.FeedbackFormKey);

            StartDetecting();
            Press(KeyCode.F7);
            Assert.AreEqual(KeyCode.F7, _settings.FeedbackFormKey, "Control: detecting again sets the key");
        }

        [Test]
        public void DetectKeySitsRightOfTheLabelEvenWhenThereIsNoRoom()
        {
            Rect wide = new Rect(150f, 0f, 270f, 18f);
            Rect button = ProtokitePlaytestKeyFieldDrawer.DetectButtonArea(wide);
            Assert.AreEqual(wide.xMax, button.xMax);
            Assert.AreEqual(ProtokitePlaytestKeyFieldDrawer.DetectButtonWidth, button.width);

            Rect narrow = new Rect(150f, 0f, 40f, 18f);
            Assert.AreEqual(narrow.x, ProtokitePlaytestKeyFieldDrawer.DetectButtonArea(narrow).x, "Never drawn over the label left of the field");
        }

        [Test]
        public void OnlyAPressOutsideTheFieldIsAClickElsewhere()
        {
            Vector2 onTheButton = ProtokitePlaytestKeyFieldDrawer.DetectButtonArea(FieldArea).center;
            Vector2 onTheBox = new Vector2(FieldArea.width / 2f, FieldArea.height / 2f);
            Vector2 below = new Vector2(10f, FieldArea.yMax + 40f);

            Assert.IsTrue(ProtokitePlaytestKeyFieldDrawer.IsAClickElsewhere(EventType.MouseDown, below, FieldArea));
            Assert.IsFalse(ProtokitePlaytestKeyFieldDrawer.IsAClickElsewhere(EventType.MouseDown, onTheBox, FieldArea), "A press on the field keeps detecting");
            Assert.IsFalse(ProtokitePlaytestKeyFieldDrawer.IsAClickElsewhere(EventType.MouseDown, onTheButton, FieldArea), "Cancel is pressed on the field, and stops detecting itself");
            Assert.IsFalse(ProtokitePlaytestKeyFieldDrawer.IsAClickElsewhere(EventType.MouseUp, below, FieldArea), "Only a press");
        }

        [Test]
        public void AnotherSettingsObjectDrawnInTheSamePlaceIsNotDetecting()
        {
            OpenWindowOn(_settings);
            StartDetecting();
            _window.Target = new SerializedObject(_otherSettings);
            Press(KeyCode.F8);
            Assert.AreEqual(KeyCode.F9, _otherSettings.FeedbackFormKey);
            Assert.AreEqual(KeyCode.F9, _settings.FeedbackFormKey);

            StartDetecting();
            Press(KeyCode.F7);
            Assert.AreEqual(KeyCode.F7, _otherSettings.FeedbackFormKey, "Control: the other object's own field detects");
        }

        private void OpenWindowOn(ProtokitePlaytestSettings settings)
        {
            _window = ScriptableObject.CreateInstance<KeyFieldWindow>();
            _window.Target = new SerializedObject(settings);
            _window.ShowUtility();
            // A developer's click on the field puts its window in front, which is where key presses go.
            _window.Focus();
            _window.SendEvent(KeyDown(KeyCode.None));
            if (!_window.Heard.Contains(EventType.KeyDown))
                Assert.Ignore("This editor delivers no key press to a test window, so the field's drawing cannot be driven here.");
        }

        // Clicks are checked in a real inspector: a click sent to a window here neither presses a button nor lands where it was sent.
        private void StartDetecting()
        {
            ProtokitePlaytestKeyFieldDrawer.StartDetectingForTesting(_window.Target.FindProperty(FieldName));
        }

        private void Press(KeyCode key) => Send(new Event { type = EventType.KeyDown, keyCode = key });

        private void Release(KeyCode key) => Send(new Event { type = EventType.KeyUp, keyCode = key });

        private void Send(Event sent)
        {
            EventType type = sent.type;
            int before = _window.Heard.FindAll(heard => heard == type).Count;
            _window.SendEvent(sent);
            Assert.AreEqual(before + 1, _window.Heard.FindAll(heard => heard == type).Count, $"Precondition: the {type} reached the window's drawing. Heard: {string.Join(", ", _window.Heard)}");
        }

        private static Event KeyDown(KeyCode key) => new Event { type = EventType.KeyDown, keyCode = key };

        // Draws the setting the way an inspector does: through its property drawer, then applying what changed.
        private sealed class KeyFieldWindow : EditorWindow
        {
            internal SerializedObject Target;
            internal readonly List<EventType> Heard = new List<EventType>();

            private void OnGUI()
            {
                Heard.Add(Event.current.type);
                if (Target == null)
                    return;
                Target.Update();
                EditorGUI.PropertyField(FieldArea, Target.FindProperty(FieldName));
                Target.ApplyModifiedProperties();
            }
        }
    }
}
