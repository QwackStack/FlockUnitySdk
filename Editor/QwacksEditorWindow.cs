using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Flock.Config;
using Flock.Docs;
#if !FLOCK_NO_SCHEMA
using Flock.Editor.Codegen;
using Flock.Editor.Catalog;
#endif

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
        public const string QwacksLogoName = "QwacksLogo";
        private const string FlockLogoName = "FlockLogo";

        private const string ConfigAssetPath = "Assets/Resources/FlockConfig.asset";
        private const string SdkGuideAssetPath = "Assets/Flock/FlockSdkGuide.asset";

        private static readonly Color PrimaryAction = new Color(0.30f, 0.70f, 0.40f);
        private static readonly Color DestructiveAction = new Color(0.85f, 0.40f, 0.40f);
        private static readonly Color HighlightAction = new Color(0.95f, 0.75f, 0.25f);

        private enum Tab { Configuration, Advanced, CodeGen }

        private Tab activeTab = Tab.Configuration;
        private Vector2 scroll;
        private bool analyticsExpanded = true;

        private FlockConfigAsset config;
        private SerializedObject configSerialized;

        private string statusMessage = "";
        private MessageType statusType = MessageType.None;
        private double statusExpiresAt;

        private bool _resolvingVersion;
        private string _versionResolveStatus;
        private bool _versionResolveOk;
        private string _lastResolvedKey;

        private Texture2D qwacksLogo;
        private Texture2D flockLogo;

        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle sectionHeaderStyle;
        private GUIStyle cardStyle;
        private GUIStyle logoPlaceholderStyle;

        [MenuItem("Flock/Settings")]
        public static void ShowWindow()
        {
            var window = GetWindow<QwacksEditorWindow>();
            window.titleContent = new GUIContent("Flock", EditorGUIUtility.IconContent("d_SettingsIcon").image);
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
                case Tab.Advanced: DrawAdvancedTab(); break;
#if !FLOCK_NO_SCHEMA
                case Tab.CodeGen: DrawCodegenTab(); break;
#endif
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
#if !FLOCK_NO_SCHEMA
            DrawTabButton("Code Generation", Tab.CodeGen);
#else
            // Schema provider is excluded — codegen folder isn't shipped, so the tab is hidden
            // and we snap any stale selection back to Configuration.
            if (activeTab == Tab.CodeGen) activeTab = Tab.Configuration;
#endif
            DrawTabButton("Advanced Settings", Tab.Advanced);
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
            DrawSetupCard();

            // The Setup card's "FlockConfig asset" row handles the create-asset step, so the
            // detailed cards below only render once an asset exists.
            if (config == null || configSerialized == null) return;

            configSerialized.Update();
            DrawCredentialsCard();
            DrawAssetStatusCard();
            configSerialized.ApplyModifiedProperties();
        }

        // Advanced tab — extra/optional settings most projects can leave at their defaults
        // (debug logging, analytics tuning, asset cache, HTTP retry, and editor tools).
        private void DrawAdvancedTab()
        {
            if (config == null || configSerialized == null)
            {
                EditorGUILayout.HelpBox(
                    "Create a FlockConfig asset on the Configuration tab first — these settings live on it.",
                    MessageType.Info);
                return;
            }

            configSerialized.Update();
            DrawOptionalCard();
            DrawAnalyticsCard();
            DrawAssetCacheCard();
            DrawRetryPolicyCard();
            DrawConfigToolsCard();
            configSerialized.ApplyModifiedProperties();
        }

        // Setup checklist — pinned at the top of the Configuration tab. Renders even with no
        // asset (the Config row creates one). Consolidates the validity / connection / bootstrap
        // / schema signals into one at-a-glance, one-click-fix panel.
        private void DrawSetupCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("Setup", sectionHeaderStyle);

            bool configExists = config != null;
            string credentialsError = "";
            bool credentialsValid = configExists && config.IsValid(out credentialsError);
            bool connectionVerified = ConnectionVerified(out string connectionDetail);
            bool bootstrapPresent = UnityEngine.Object.FindAnyObjectByType<FlockBootstrap>() != null;
            bool autoInitializeEnabled = configExists && config.autoInitializeOnLoad;

            bool includeSchemas;
            bool schemasGenerated = false;
#if !FLOCK_NO_SCHEMA
            includeSchemas = true;
            if (configExists && !string.IsNullOrEmpty(config.generatedCodePath) && Directory.Exists(config.generatedCodePath))
                schemasGenerated = Directory.GetFiles(config.generatedCodePath, "*.cs", SearchOption.AllDirectories).Length > 0;
#else
            includeSchemas = false;
#endif

            FlockSetupFacts facts = new FlockSetupFacts(
                configExists, credentialsValid, credentialsError,
                connectionVerified, connectionDetail, bootstrapPresent,
                autoInitializeEnabled, includeSchemas, schemasGenerated);

            foreach (FlockSetupItem item in FlockSetupChecklist.Build(facts))
                DrawSetupRow(item);

            EditorGUILayout.EndVertical();
        }

        private void DrawSetupRow(FlockSetupItem item)
        {
            EditorGUILayout.BeginHorizontal();

            Color previous = GUI.color;
            GUI.color = StateColor(item.State);
            GUILayout.Label(StateTag(item.State), EditorStyles.miniBoldLabel, GUILayout.Width(70));
            GUI.color = previous;

            EditorGUILayout.BeginVertical();
            GUILayout.Label(item.Label, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(item.Detail))
                GUILayout.Label(item.Detail, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            DrawSetupRowAction(item.Key, item.State);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void DrawSetupRowAction(string key, FlockSetupItemState state)
        {
            switch (key)
            {
                case "config":
                    if (state != FlockSetupItemState.Done)
                        using (new BackgroundColorScope(PrimaryAction))
                            if (GUILayout.Button("Create", GUILayout.Width(110), GUILayout.Height(22)))
                                CreateConfigAsset();
                    break;

                case "connection":
                    using (new EditorGUI.DisabledScope(config == null || string.IsNullOrWhiteSpace(config.apiUrl) || string.IsNullOrWhiteSpace(config.apiKey)))
                        if (GUILayout.Button(state == FlockSetupItemState.Done ? "Re-verify" : "Verify", GUILayout.Width(110), GUILayout.Height(22)))
                            TestConfiguration();
                    break;

                case "bootstrap":
                    if (state != FlockSetupItemState.Done)
                        using (new EditorGUI.DisabledScope(config == null || !config.IsValid(out _)))
                        using (new BackgroundColorScope(HighlightAction))
                            if (GUILayout.Button("Add to Scene", GUILayout.Width(110), GUILayout.Height(22)))
                                AddBootstrapToScene();
                    break;

#if !FLOCK_NO_SCHEMA
                case "schemas":
                    if (GUILayout.Button("Open Codegen", GUILayout.Width(110), GUILayout.Height(22)))
                        activeTab = Tab.CodeGen;
                    break;
#endif
            }
        }

        private static string StateTag(FlockSetupItemState state)
        {
            switch (state)
            {
                case FlockSetupItemState.Done: return "DONE";
                case FlockSetupItemState.Required: return "REQUIRED";
                case FlockSetupItemState.Manual: return "VERIFY";
                default: return "OPTIONAL";
            }
        }

        private static Color StateColor(FlockSetupItemState state)
        {
            switch (state)
            {
                case FlockSetupItemState.Done: return new Color(0.40f, 0.80f, 0.45f);
                case FlockSetupItemState.Required: return new Color(0.90f, 0.50f, 0.50f);
                case FlockSetupItemState.Manual: return new Color(0.95f, 0.75f, 0.25f);
                default: return new Color(0.70f, 0.70f, 0.70f);
            }
        }

        private void DrawAssetStatusCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            EditorGUILayout.LabelField("Asset", AssetDatabase.GetAssetPath(config), EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "Edits here are saved straight into the asset. There's no separate Save step.",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            if (GUILayout.Button(
                    new GUIContent("Locate Asset", "Selects the FlockConfig asset in the Project view so you can inspect or move it."),
                    GUILayout.Height(24)))
            {
                EditorGUIUtility.PingObject(config);
                Selection.activeObject = config;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawCredentialsCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("API Credentials", sectionHeaderStyle);

            DrawProperty("apiUrl");
            DrawProperty("apiKey");
            DrawProperty("gameId", "Game Name");
            DrawProperty("gameVersion");

            // Baked Game Version ID — resolved at edit time, read-only here.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(
                    new GUIContent("Resolved Version ID",
                        "Baked from your Game Version at edit time. Runtime init uses this directly — no server call."),
                    config.gameVersionId);

            using (new EditorGUI.DisabledScope(
                _resolvingVersion || config == null ||
                string.IsNullOrWhiteSpace(config.apiUrl) || string.IsNullOrWhiteSpace(config.apiKey) ||
                string.IsNullOrWhiteSpace(config.gameVersion)))
            {
                if (GUILayout.Button(_resolvingVersion ? "Resolving…" : "Resolve Game Version"))
                    RunVersionResolve();
            }

            if (!string.IsNullOrEmpty(_versionResolveStatus))
                EditorGUILayout.HelpBox(_versionResolveStatus, _versionResolveOk ? MessageType.Info : MessageType.Error);

            MaybeAutoResolveVersion();

            if (!config.IsValid(out string validationError))
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);

            EditorGUILayout.EndVertical();
        }

        // Auto-resolve when the credential/version fields change and the user isn't mid-edit.
        private void MaybeAutoResolveVersion()
        {
            if (_resolvingVersion || config == null) return;
            if (EditorGUIUtility.editingTextField) return; // mid-edit — wait for commit
            if (string.IsNullOrWhiteSpace(config.apiUrl) || string.IsNullOrWhiteSpace(config.apiKey) ||
                string.IsNullOrWhiteSpace(config.gameVersion))
                return;

            string key = $"{config.apiUrl}|{config.apiKey}|{config.gameId}|{config.gameVersion}";
            if (key == _lastResolvedKey) return;

            _lastResolvedKey = key;
            RunVersionResolve();
        }

        private async void RunVersionResolve()
        {
            if (_resolvingVersion || config == null) return;
            _resolvingVersion = true;
            _versionResolveStatus = "Resolving Game Version…";
            _versionResolveOk = false;
            Repaint();

            FlockVersionResolver.ResolveResult result =
                await FlockVersionResolver.ResolveAndBakeAsync(config);

            _resolvingVersion = false;
            _versionResolveOk = result.Success;
            _versionResolveStatus = result.Success
                ? $"Resolved: {result.GameVersionId}"
                : result.Error;
            // Keep the key in sync so a successful resolve doesn't immediately re-fire.
            _lastResolvedKey = $"{config.apiUrl}|{config.apiKey}|{config.gameId}|{config.gameVersion}";

            // A successful resolve hit the same by-name endpoint the Connection check uses, so record it
            // as a verified connection — the Setup row turns green without a separate manual click.
            if (result.Success)
                StoreConnectionResult(CredHash(config), true, $"Connection OK — Game Version '{config.gameVersion}' resolved.");

            Repaint();
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

            SerializedProperty enabledProp = configSerialized.FindProperty("analyticsEnabled");
            enabledProp.boolValue = EditorGUILayout.Toggle("Analytics Enabled", enabledProp.boolValue);

            if (enabledProp.boolValue)
            {
                analyticsExpanded = EditorGUILayout.Foldout(analyticsExpanded, "Analytics Settings", toggleOnLabelClick: true);
                if (analyticsExpanded)
                {
                    DrawProperty("analyticsRequireExplicitConsent");
                    GUILayout.Space(4);
                    DrawProperty("analyticsAutoStartSession");
                    DrawProperty("analyticsAutoEndOnQuit");
                    DrawProperty("analyticsSessionTimeout");
                    DrawProperty("analyticsHeartbeatInterval");
                    DrawProperty("analyticsBounceThreshold");
                    DrawProperty("analyticsPersistSession");
                    DrawProperty("analyticsTrackFps");
                    DrawProperty("analyticsFpsSampleInterval");

                    GUILayout.Space(4);
                    GUILayout.Label("Caching", EditorStyles.miniBoldLabel);
                    DrawProperty("analyticsCacheFailedEvents");
                    using (new EditorGUI.DisabledScope(!configSerialized.FindProperty("analyticsCacheFailedEvents").boolValue))
                    {
                        DrawProperty("analyticsMaxCachedEvents");
                        DrawProperty("analyticsCacheFlushBatchSize");
                        DrawProperty("analyticsEventBufferFlushInterval");
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRetryPolicyCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("HTTP Retry Policy", sectionHeaderStyle);
            DrawProperty("retryMaxRetries");
            using (new EditorGUI.DisabledScope(configSerialized.FindProperty("retryMaxRetries").intValue <= 0))
            {
                DrawProperty("retryUseJitter");
            }
            DrawProperty("httpTimeoutSeconds");
            EditorGUILayout.EndVertical();
        }

        private void DrawAssetCacheCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("Asset Cache", sectionHeaderStyle);
            DrawProperty("enableAssetCache");
            using (new EditorGUI.DisabledScope(!configSerialized.FindProperty("enableAssetCache").boolValue))
            {
                DrawProperty("assetCacheDirectory");
                DrawProperty("assetCacheMaxSizeMB");
            }
            DrawProperty("assetDownloadTimeoutSeconds");
            DrawProperty("assetDownloadRetryCount");
            EditorGUILayout.EndVertical();
        }

        private void DrawConfigToolsCard()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            GUILayout.Label("Tools", sectionHeaderStyle);

            SerializedProperty guardProp = configSerialized.FindProperty("playModeGuardEnabled");
            guardProp.boolValue = EditorGUILayout.Toggle(
                new GUIContent("Play-Mode Setup Guard",
                    "When ON, entering Play with Flock not set up shows a fixable dialog. Editor-only; saved on the asset."),
                guardProp.boolValue);

            SerializedProperty failBuildProp = configSerialized.FindProperty("failBuildIfVersionUnresolved");
            failBuildProp.boolValue = EditorGUILayout.Toggle(
                new GUIContent("Fail build if Game Version unresolved",
                    "Blocks player builds when the Resolved Version ID is empty or has drifted from the generated schemas, so a build that can't init (or targets the wrong version) can't ship."),
                failBuildProp.boolValue);

            SerializedProperty autoInitProp = configSerialized.FindProperty("autoInitializeOnLoad");
            autoInitProp.boolValue = EditorGUILayout.Toggle(
                new GUIContent("Auto-Initialize On Load",
                    "Start the SDK automatically at launch from this asset — no FlockBootstrap or Create() call needed — and restore a persisted session in the background."),
                autoInitProp.boolValue);

            EditorGUILayout.EndVertical();
        }


        // Codegen tab — only present when the SCHEMA provider is included in the build
        // (codegen consumes SchemaTag from ISchemaProvider, and the Editor/Codegen folder
        // isn't shipped without it).
#if !FLOCK_NO_SCHEMA
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

            EditorGUILayout.BeginHorizontal();

            // Sync needs valid credentials; Delete is local cleanup and stays available regardless.
            using (new EditorGUI.DisabledScope(!config.IsValid(out _)))
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

            // Appears once a sync has produced the catalog. Designer-facing browse of shops/configs/templates.
            FlockContentCatalog catalog = LoadCatalogAsset();
            if (catalog != null)
                using (new BackgroundColorScope(HighlightAction))
                {
                    if (GUILayout.Button(
                            new GUIContent("Open Catalog", "Select the read-only content catalog so designers can browse shops, configs and templates in the Inspector."),
                            GUILayout.Height(36)))
                    {
                        EditorGUIUtility.PingObject(catalog);
                        Selection.activeObject = catalog;
                    }
                }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // The generated catalog asset, or null until the first successful sync creates it.
        private FlockContentCatalog LoadCatalogAsset()
        {
            if (config == null) return null;
            if (!FlockCodegenMenu.TryResolveGeneratedPath(config.generatedCodePath, out string generatedRoot, out _))
                return null;
            return AssetDatabase.LoadAssetAtPath<FlockContentCatalog>(CatalogEmitter.AssetPath(generatedRoot));
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
#endif


        // Asset binding / creation

        private void BindConfig()
        {
            config = FlockConfigLocator.FindConfigAsset();
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


        // SDK Guide — opened from the 'Getting Started' link in the header bar.
        // Loads the asset if it already exists anywhere in the project, otherwise
        // creates one at the default path. The asset is just a ScriptableObject
        // with TextArea fields, so the docs render in the Inspector on selection.

        private void OpenSdkGuide()
        {
            FlockSdkGuide guide = AssetDatabase.LoadAssetAtPath<FlockSdkGuide>(SdkGuideAssetPath);

            if (guide == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:FlockSdkGuide");
                if (guids != null && guids.Length > 0)
                    guide = AssetDatabase.LoadAssetAtPath<FlockSdkGuide>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            if (guide == null)
                guide = CreateSdkGuideAsset();

            if (guide == null) return;

            Selection.activeObject = guide;
            EditorGUIUtility.PingObject(guide);
        }

        // Opens the full online documentation in a browser. Warns in the window
        // if the DocsUrl placeholder in FlockSdkGuide hasn't been filled in yet.
        private void OpenDocs()
        {
            string url = FlockSdkGuide.DocsUrl;
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http"))
            {
                ShowStatus("Documentation link isn't set yet — fill in FlockSdkGuide.DocsUrl.", MessageType.Warning);
                return;
            }
            Application.OpenURL(url);
        }

        private FlockSdkGuide CreateSdkGuideAsset()
        {
            try
            {
                string folder = Path.GetDirectoryName(SdkGuideAssetPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    AssetDatabase.Refresh();
                }

                FlockSdkGuide guide = CreateInstance<FlockSdkGuide>();
                AssetDatabase.CreateAsset(guide, SdkGuideAssetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                ShowStatus($"Created SDK guide at {SdkGuideAssetPath}.", MessageType.Info);
                return guide;
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to create SDK guide: {ex.Message}", MessageType.Error);
                Debug.LogError($"[Flock] Failed to create SDK guide: {ex}");
                return null;
            }
        }


        // Property helpers

        private void DrawProperty(string propertyName)
        {
            SerializedProperty prop = configSerialized.FindProperty(propertyName);
            if (prop != null)
                EditorGUILayout.PropertyField(prop, true);
        }

        // Same, but overrides the display label (e.g. show "Game Name" for the gameId field without renaming it).
        private void DrawProperty(string propertyName, string label)
        {
            SerializedProperty prop = configSerialized.FindProperty(propertyName);
            if (prop != null)
                EditorGUILayout.PropertyField(prop, new GUIContent(label, prop.tooltip), true);
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
            // Errors/warnings stay until the next action; only transient Info auto-expires.
            bool transient = statusType == MessageType.Info || statusType == MessageType.None;
            if (transient && EditorApplication.timeSinceStartup > statusExpiresAt)
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
            if (GUILayout.Button("Getting Started", EditorStyles.linkLabel))
                OpenSdkGuide();
            GUILayout.Label("|", EditorStyles.miniLabel);
            if (GUILayout.Button("Documentation", EditorStyles.linkLabel))
                OpenDocs();
            GUILayout.Label("|", EditorStyles.miniLabel);
            if (GUILayout.Button("Support", EditorStyles.linkLabel))
                Application.OpenURL(FlockSdkGuide.SupportUrl);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void ShowStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusType = type;
            statusExpiresAt = EditorApplication.timeSinceStartup + 5;
            Repaint();
        }


        // Connection test — the result is cached in SessionState (per editor session), keyed by
        // a hash of the four credentials so any edit invalidates a prior green result. The Setup
        // card's Connection row reads this cache; there is never an automatic network ping.

        private const string ConnHashKey = "Flock.Setup.ConnHash";
        private const string ConnOkKey = "Flock.Setup.ConnOk";
        private const string ConnMsgKey = "Flock.Setup.ConnMsg";

        private static string CredHash(FlockConfigAsset c)
        {
            string raw = (c.apiUrl ?? "") + "|" + (c.apiKey ?? "") + "|" + (c.gameId ?? "") + "|" + (c.gameVersion ?? "");
            using (MD5 md5 = MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(bytes);
            }
        }

        private static void StoreConnectionResult(string credHash, bool ok, string message)
        {
            SessionState.SetString(ConnHashKey, credHash);
            SessionState.SetBool(ConnOkKey, ok);
            SessionState.SetString(ConnMsgKey, message);
        }

        private bool ConnectionVerified(out string detail)
        {
            if (config == null) { detail = "Not verified yet."; return false; }

            string stored = SessionState.GetString(ConnHashKey, "");
            if (stored.Length == 0 || stored != CredHash(config))
            {
                detail = "Not verified for the current credentials.";
                return false;
            }

            detail = SessionState.GetString(ConnMsgKey, "");
            return SessionState.GetBool(ConnOkKey, false);
        }

        private async void TestConfiguration()
        {
            if (config == null) return;

            string apiUrl = (config.apiUrl ?? string.Empty).Trim();
            string apiKey = (config.apiKey ?? string.Empty).Trim();
            string gameVersion = (config.gameVersion ?? string.Empty).Trim();
            string credHash = CredHash(config);

            if (!Uri.IsWellFormedUriString(apiUrl, UriKind.Absolute))
            {
                StoreConnectionResult(credHash, false, "API URL is invalid.");
                ShowStatus("API URL is invalid.", MessageType.Error);
                return;
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                StoreConnectionResult(credHash, false, "API Key is required.");
                ShowStatus("API Key is required.", MessageType.Error);
                return;
            }

            string baseUrl = apiUrl.TrimEnd('/');
            // Mirror the edit-time version resolve (FlockVersionResolver): a green test means the
            // Game Version can be resolved & baked, which is what synchronous init needs.
            // When Game Version is missing, we can't run that call — fall back to a bare key check.
            bool hasGameVersion = !string.IsNullOrEmpty(gameVersion);
            string url = hasGameVersion
                ? FlockVersionResolver.ByNameUrl(apiUrl, gameVersion)
                : $"{baseUrl}/healthz";

            EditorUtility.DisplayProgressBar("Test Connection", "Pinging API...", 0.4f);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("X-Flock-API-Key", apiKey);

                HttpResponseMessage response = await http.GetAsync(url);
                EditorUtility.ClearProgressBar();

                bool verified;
                string message;
                MessageType type;

                if (response.IsSuccessStatusCode)
                {
                    if (hasGameVersion)
                    {
                        verified = true;
                        message = $"Connection OK — Game Version '{gameVersion}' resolved.";
                        type = MessageType.Info;
                    }
                    else
                    {
                        verified = false;
                        message = "API key accepted. Set Game Version to fully verify init will succeed.";
                        type = MessageType.Warning;
                    }
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    string detail = await BodyDetail(response);
                    verified = false;
                    message = $"API key rejected ({(int)response.StatusCode} {response.StatusCode}).{detail}";
                    type = MessageType.Error;
                }
                else if (response.StatusCode == HttpStatusCode.NotFound && hasGameVersion)
                {
                    verified = false;
                    message = $"Game Version '{gameVersion}' not found on the backend.";
                    type = MessageType.Error;
                }
                else
                {
                    string detail = await BodyDetail(response);
                    verified = false;
                    message = $"API returned {(int)response.StatusCode} {response.StatusCode}.{detail}";
                    type = MessageType.Warning;
                }

                StoreConnectionResult(credHash, verified, message);
                ShowStatus(message, type);
            }
            catch (HttpRequestException ex)
            {
                EditorUtility.ClearProgressBar();
                StoreConnectionResult(credHash, false, "Connection failed.");
                ShowStatus($"Connection failed: {(ex.InnerException ?? ex).Message}", MessageType.Error);
            }
            catch (TaskCanceledException)
            {
                EditorUtility.ClearProgressBar();
                StoreConnectionResult(credHash, false, "Connection timed out.");
                ShowStatus("Connection timed out.", MessageType.Error);
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                StoreConnectionResult(credHash, false, "Test failed.");
                ShowStatus($"Test failed: {(ex.InnerException ?? ex).Message}", MessageType.Error);
            }
        }

        // Reads a failed response's body and formats it as a short, capped suffix so a stray HTML error page can't flood the status line.
        private static async Task<string> BodyDetail(HttpResponseMessage response)
        {
            string body;
            try { body = (await response.Content.ReadAsStringAsync())?.Trim(); }
            catch { return string.Empty; }
            if (string.IsNullOrEmpty(body)) return string.Empty;
            string trimmed = body.Length > 300 ? body.Substring(0, 300) + "…" : body;
            return $" Server said: {trimmed}";
        }


        // Resource loading

        private void LoadLogos()
        {
            qwacksLogo = FindTextureByName(QwacksLogoName);
            flockLogo = FindTextureByName(FlockLogoName);
        }

        public static Texture2D FindTextureByName(string name)
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

            titleStyle.normal.textColor = Color.white;
            subtitleStyle.normal.textColor = Color.white;
            sectionHeaderStyle.normal.textColor = Color.white;
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
