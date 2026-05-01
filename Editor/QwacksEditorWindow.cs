using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Flock.Config;
using Flock.Editor.Codegen;

namespace Flock.Editor
{
    /// <summary>
    /// Editor window for the Flock SDK. The single FlockConfig asset is the source of
    /// truth for runtime values — this window is just a friendly view onto it. Edits
    /// here go straight into the asset (no separate Save step). Codegen and the
    /// optional FlockBootstrap component both read the same asset.
    /// </summary>
    public class QwacksEditorWindow : EditorWindow
    {
        // Logos are looked up by filename anywhere in the project.
        private const string QwacksLogoName = "QwacksLogo";
        private const string FlockLogoName = "FlockLogo";

        private const string ConfigAssetPath = "Assets/Resources/FlockConfig.asset";

        private static readonly Color PrimaryAction = new Color(0.30f, 0.70f, 0.40f);
        private static readonly Color DestructiveAction = new Color(0.85f, 0.40f, 0.40f);

        private enum Tab { Configuration, CodeGen }

        private Tab activeTab = Tab.Configuration;
        private Vector2 scroll;
        private bool analyticsExpanded;
        private bool analyticsEnabled;

        private FlockConfigAsset config;
        private SerializedObject configSerialized;

        private string statusMessage = "";
        private MessageType statusType = MessageType.None;
        private double statusExpiresAt;

        private Texture2D qwacksLogo;
        private Texture2D flockLogo;

        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle sectionHeaderStyle;
        private GUIStyle cardStyle;
        private GUIStyle logoPlaceholderStyle;

        [MenuItem("Qwacks/Flock")]
        public static void ShowWindow()
        {
            var window = GetWindow<QwacksEditorWindow>();
            window.titleContent = new GUIContent("Qwacks", EditorGUIUtility.IconContent("d_SettingsIcon").image);
            window.minSize = new Vector2(520, 640);
            window.Show();
        }

        private void OnEnable()
        {
            BindConfig();
            LoadLogos();
        }

        private void OnFocus()
        {
            // Re-bind in case the asset was created, deleted, or reimported externally.
            BindConfig();
            LoadLogos();
        }

        private void OnGUI()
        {
            EnsureStyles();
            titleContent.image = qwacksLogo;
            DrawHeader();
            DrawLinkBar();
            DrawTabBar();
            EditorGUILayout.Space(4);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            switch (activeTab)
            {
                case Tab.Configuration: DrawConfigurationTab(); break;
                case Tab.CodeGen: DrawCodegenTab(); break;
            }
            EditorGUILayout.EndScrollView();

            DrawStatusMessage();
        }


        // Header

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            DrawLogoSlot(flockLogo, "Flock", $"{FlockLogoName}.png");
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginVertical();
            GUILayout.Space(5);
            GUILayout.Label("Flock", titleStyle);
            GUILayout.Label("Configure the Flock SDK and run codegen", subtitleStyle);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawLogoSlot(Texture2D logo, string label, string expectedFilename)
        {
            const float size = 56f;
            EditorGUILayout.BeginVertical(GUILayout.Width(size + 12));
            GUILayout.Space(4);

            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));

            if (logo != null)
            {
                GUI.DrawTexture(rect, logo, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.12f));
                GUI.Label(rect, $"Drop\n{label}", logoPlaceholderStyle);
                if (rect.Contains(Event.current.mousePosition))
                    GUI.tooltip = $"Add {expectedFilename} to the project (any folder).";
            }

            EditorGUILayout.EndVertical();
        }


        // Tabs

        private void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            DrawTabButton("Configuration", Tab.Configuration);
            DrawTabButton("Code Generation", Tab.CodeGen);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTabButton(string label, Tab tab)
        {
            bool isActive = activeTab == tab;
            bool clicked = GUILayout.Toggle(isActive, label, EditorStyles.toolbarButton, GUILayout.MinWidth(110));
            if (clicked && !isActive) activeTab = tab;
        }


        // Configuration tab

        private void DrawConfigurationTab()
        {
            if (!EnsureAssetExistsCard()) return;

            configSerialized.Update();
            DrawAssetStatusCard();
            DrawCredentialsCard();
            DrawOptionalCard();
            DrawAnalyticsCard();
            DrawConfigToolsCard();
            configSerialized.ApplyModifiedProperties();
        }

        private void DrawAssetStatusCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            EditorGUILayout.LabelField("Asset", AssetDatabase.GetAssetPath(config), EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "Edits here are saved straight into the asset. There's no separate Save step.",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawCredentialsCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("API Credentials", sectionHeaderStyle);

            DrawProperty("apiUrl");
            DrawProperty("apiKey");
            DrawProperty("gameId");
            DrawProperty("gameVersion");

            if (!config.IsValid(out string validationError))
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);

            EditorGUILayout.EndVertical();
        }

        private void DrawOptionalCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("Optional", sectionHeaderStyle);
            DrawProperty("enableDebugLogs");
            EditorGUILayout.EndVertical();
        }

        private void DrawAnalyticsCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            analyticsExpanded = EditorGUILayout.Foldout(analyticsExpanded, "Analytics", true);
            if (analyticsExpanded)
            {
                SerializedProperty prop = configSerialized.FindProperty("analyticsEnabled");
                prop.boolValue = EditorGUILayout.Toggle("Analytics Enabled",prop.boolValue);
                if (prop.boolValue)
                {
                    DrawProperty("analyticsAutoStartSession");
                    DrawProperty("analyticsAutoEndOnQuit");
                    DrawProperty("analyticsSessionTimeout");
                    DrawProperty("analyticsHeartbeatInterval");
                    DrawProperty("analyticsBounceThreshold");
                    DrawProperty("analyticsPersistSession");
                    DrawProperty("analyticsTrackFps");
                    DrawProperty("analyticsFpsSampleInterval");
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawConfigToolsCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("Tools", sectionHeaderStyle);

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(config.apiUrl) || string.IsNullOrWhiteSpace(config.apiKey)))
            {
                if (GUILayout.Button(
                        new GUIContent("Test Connection", "Pings /healthz with your API key. Confirms the URL is reachable and the key is accepted by the backend."),
                        GUILayout.Height(28)))
                    TestConfiguration();
            }

            if (GUILayout.Button(
                    new GUIContent("Locate Asset", "Selects the FlockConfig asset in the Project view so you can inspect or move it."),
                    GUILayout.Height(28)))
            {
                EditorGUIUtility.PingObject(config);
                Selection.activeObject = config;
            }

            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!config.IsValid(out _)))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Add Flock Bootstrap to Scene",
                            "Creates a GameObject in the active scene with a FlockBootstrap component pointed at this asset. " +
                            "Recommended for projects that don't want to write their own SDK init code — drop this in a Boot scene and forget about it."),
                        GUILayout.Height(28)))
                    AddBootstrapToScene();
            }

            EditorGUILayout.EndVertical();
        }


        // Codegen tab

        private void DrawCodegenTab()
        {
            if (!EnsureAssetExistsCard()) return;

            configSerialized.Update();
            DrawCodegenHelpCard();
            DrawCodegenActionsCard();
            configSerialized.ApplyModifiedProperties();
        }

        private void DrawCodegenActionsCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("Actions", sectionHeaderStyle);

            if (!config.IsValid(out string validationError))
                EditorGUILayout.HelpBox(
                    $"Codegen needs valid credentials before it can run. {validationError}",
                    MessageType.Warning);

            using (new EditorGUI.DisabledScope(!config.IsValid(out _)))
                
                EditorGUILayout.BeginHorizontal();
            using (new BackgroundColorScope(PrimaryAction))
            {
                if (GUILayout.Button(
                        new GUIContent("Sync Schemas", "Fetch templates and configs from the backend and regenerate typed accessors."),
                        GUILayout.Height(36)))
                    FlockCodegenMenu.SyncSchemas();
            }

            using (new BackgroundColorScope(DestructiveAction))
            {
                if (GUILayout.Button(
                        new GUIContent("Delete Generated Code", "Remove the entire generated folder. Asks for confirmation."),
                        GUILayout.Height(36)))
                    FlockCodegenMenu.CleanGenerated();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawCodegenHelpCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("How It Works", sectionHeaderStyle);
            EditorGUILayout.LabelField(
                "Sync resolves Game Version to its ID, fetches all schemas, then writes typed accessors under the output folder.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                "Delete wipes the generated folder. Safe to run before re-syncing.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                "Watch the Console for sync output and per-field warnings.",
                EditorStyles.wordWrappedMiniLabel);
            //Code Gen Output path
            GUILayout.Label("Output Folder", sectionHeaderStyle);
            DrawProperty("generatedCodePath");
            EditorGUILayout.EndVertical();
        }


        // Asset binding / creation

        private void BindConfig()
        {
            config = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(ConfigAssetPath);

            if (config == null)
            {
                // Fall back to the first FlockConfigAsset anywhere in the project so users
                // who moved the asset don't see "no asset" by mistake.
                string[] guids = AssetDatabase.FindAssets("t:FlockConfigAsset");
                if (guids != null && guids.Length > 0)
                    config = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            configSerialized = config != null ? new SerializedObject(config) : null;
        }

        private bool EnsureAssetExistsCard()
        {
            if (config != null && configSerialized != null) return true;

            EditorGUILayout.BeginVertical(cardStyle);
            EditorGUILayout.HelpBox(
                $"No FlockConfig asset found. The SDK reads its values from a single asset (default location: {ConfigAssetPath}). " +
                "Click below to create it — you can edit fields here or by selecting the asset in the Project view; both edit the same data.",
                MessageType.Info);

            using (new BackgroundColorScope(PrimaryAction))
            {
                if (GUILayout.Button(
                        new GUIContent("Create FlockConfig Asset",
                            "Creates a FlockConfig asset at Assets/Resources/FlockConfig.asset with default values."),
                        GUILayout.Height(32)))
                {
                    CreateConfigAsset();
                }
            }

            EditorGUILayout.EndVertical();
            return false;
        }

        private void CreateConfigAsset()
        {
            try
            {
                const string resourcesPath = "Assets/Resources";
                if (!Directory.Exists(resourcesPath))
                {
                    Directory.CreateDirectory(resourcesPath);
                    AssetDatabase.Refresh();
                }

                FlockConfigAsset asset = CreateInstance<FlockConfigAsset>();
                AssetDatabase.CreateAsset(asset, ConfigAssetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                BindConfig();
                EditorGUIUtility.PingObject(config);
                ShowStatus($"Created {ConfigAssetPath}. Fill in the API credentials to continue.", MessageType.Info);
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to create asset: {ex.Message}", MessageType.Error);
                Debug.LogError($"[Flock] Failed to create config asset: {ex}");
            }
        }


        // Property helpers

        private void DrawProperty(string propertyName)
        {
            SerializedProperty prop = configSerialized.FindProperty(propertyName);
            if (prop != null)
                EditorGUILayout.PropertyField(prop, true);
        }
        
        // Bootstrap drop-in

        private void AddBootstrapToScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
            {
                ShowStatus("Open a scene first — there's nowhere to add the bootstrap.", MessageType.Warning);
                return;
            }

            var existing = UnityEngine.Object.FindAnyObjectByType<FlockBootstrap>();
            if (existing != null)
            {
                EditorGUIUtility.PingObject(existing.gameObject);
                Selection.activeGameObject = existing.gameObject;
                ShowStatus($"FlockBootstrap already exists on '{existing.gameObject.name}'.", MessageType.Info);
                return;
            }

            var go = new GameObject("Flock Bootstrap");
            Undo.RegisterCreatedObjectUndo(go, "Add Flock Bootstrap");
            var bootstrap = Undo.AddComponent<FlockBootstrap>(go);

            var so = new SerializedObject(bootstrap);
            SerializedProperty configProp = so.FindProperty("config");
            if (configProp != null)
            {
                configProp.objectReferenceValue = config;
                so.ApplyModifiedProperties();
            }

            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(activeScene);
            ShowStatus($"Added FlockBootstrap to '{activeScene.name}'. Save the scene to keep it.", MessageType.Info);
        }


        // Status / footer

        private void DrawStatusMessage()
        {
            if (string.IsNullOrEmpty(statusMessage)) return;
            if (EditorApplication.timeSinceStartup > statusExpiresAt)
            {
                statusMessage = "";
                return;
            }
            EditorGUILayout.HelpBox(statusMessage, statusType);
        }

        private void DrawLinkBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Documentation", EditorStyles.linkLabel))
                Application.OpenURL("https://www.qwacks.com/flock");
            GUILayout.Label("|", EditorStyles.miniLabel);
            if (GUILayout.Button("Support", EditorStyles.linkLabel))
                Application.OpenURL("https://www.qwacks.com/flock");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void ShowStatus(string message, MessageType type)
        {
            Debug.Log($"Status Update : {message}");
            statusMessage = message;
            statusType = type;
            statusExpiresAt = EditorApplication.timeSinceStartup + 5;
            Repaint();
        }


        // Connection test

        private async void TestConfiguration()
        {
            if (config == null) return;
            if (!Uri.IsWellFormedUriString(config.apiUrl, UriKind.Absolute))
            {
                ShowStatus("API URL is invalid.", MessageType.Error);
                return;
            }

            EditorUtility.DisplayProgressBar("Test Connection", "Pinging API...", 0.4f);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("X-Flock-API-Key", config.apiKey);

                HttpResponseMessage response = await http.GetAsync($"{config.apiUrl}/healthz");
                EditorUtility.ClearProgressBar();

                if (response.IsSuccessStatusCode)
                    ShowStatus("Connection OK.", MessageType.Info);
                else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    ShowStatus($"API key rejected ({(int)response.StatusCode} {response.StatusCode}).", MessageType.Error);
                else
                    ShowStatus($"API returned {(int)response.StatusCode} {response.StatusCode}.", MessageType.Warning);
            }
            catch (HttpRequestException ex)
            {
                EditorUtility.ClearProgressBar();
                ShowStatus($"Connection failed: {ex.Message}", MessageType.Error);
            }
            catch (TaskCanceledException)
            {
                EditorUtility.ClearProgressBar();
                ShowStatus("Connection timed out.", MessageType.Error);
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                ShowStatus($"Test failed: {ex.Message}", MessageType.Error);
            }
        }


        // Resource loading

        private void LoadLogos()
        {
            qwacksLogo = FindTextureByName(QwacksLogoName);
            flockLogo = FindTextureByName(FlockLogoName);
        }

        private static Texture2D FindTextureByName(string name)
        {
            string[] guids = AssetDatabase.FindAssets($"{name} t:Texture2D");
            if (guids == null || guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private void EnsureStyles()
        {
            if (titleStyle != null) return;

            titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(0, 0, 4, 0)
            };
            subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                wordWrap = true
            };
            sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                margin = new RectOffset(0, 0, 8, 4)
            };
            cardStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 10, 10),
                margin = new RectOffset(0, 0, 4, 4)
            };
            logoPlaceholderStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
        }

        private readonly struct BackgroundColorScope : IDisposable
        {
            private readonly Color previous;

            public BackgroundColorScope(Color color)
            {
                previous = GUI.backgroundColor;
                GUI.backgroundColor = color;
            }

            public void Dispose() => GUI.backgroundColor = previous;
        }
    }
}
