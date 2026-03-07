using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using Flock.Config;

namespace Flock.Editor
{
    public class FlockConfigWindow : EditorWindow
    {
        private string apiUrl = "https://api-flock.qwacks.com";
        private string apiKey = "";
        private string gameId = "";
        private string gameVersionId = "";
        private bool enableDebugLogs = false;

        private Vector2 scrollPosition;
        private GUIStyle headerStyle;
        private GUIStyle sectionStyle;
        private GUIStyle boxStyle;
        private string errorMessage = "";
        private string successMessage = "";
        private double messageTimer = 0;
        private FlockConfigAsset existingConfig;
        private bool _hasUnsavedChanges = false;

        [MenuItem("Qwacks/Configuration")]
        public static void ShowWindow()
        {
            var window = GetWindow<FlockConfigWindow>("Flock SDK Config");
            window.minSize = new Vector2(500, 500);
            window.Show();
        }

        private void OnEnable()
        {
            LoadConfig();
        }

        private void InitializeStyles()
        {
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter,
                    margin = new RectOffset(0, 0, 10, 10)
                };
            }

            if (sectionStyle == null)
            {
                sectionStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12,
                    margin = new RectOffset(0, 0, 15, 5)
                };
            }

            if (boxStyle == null)
            {
                boxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(10, 10, 10, 10)
                };
            }
        }

        private void OnGUI()
        {
            InitializeStyles();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Space(10);

            EditorGUILayout.BeginVertical(boxStyle);
            GUILayout.Label("Flock SDK Configuration", headerStyle);
            GUILayout.Label("Configure your Flock SDK API credentials", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndVertical();

            GUILayout.Space(15);

            if (existingConfig != null)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label($"Config Status: Loaded from FlockConfig.asset", EditorStyles.miniLabel);
                if (_hasUnsavedChanges)
                {
                    GUILayout.Label("You have unsaved changes", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
                GUILayout.Space(10);
            }

            EditorGUILayout.BeginVertical(boxStyle);
            GUILayout.Label("API Configuration (Required)", sectionStyle);
            GUILayout.Space(5);

            EditorGUI.BeginChangeCheck();

            string newApiUrl = EditorGUILayout.TextField(
                new GUIContent("API URL *", "Flock API endpoint URL"),
                apiUrl);

            if (string.IsNullOrWhiteSpace(newApiUrl))
                EditorGUILayout.HelpBox("API URL is required", MessageType.Warning);

            GUILayout.Space(5);

            string newApiKey = EditorGUILayout.PasswordField(
                new GUIContent("API Key *", "Your Flock API Key (stored securely, never logged)"),
                apiKey);

            if (string.IsNullOrWhiteSpace(newApiKey))
                EditorGUILayout.HelpBox("API Key is required for SDK authentication", MessageType.Warning);

            GUILayout.Space(5);

            string newGameId = EditorGUILayout.TextField(
                new GUIContent("Game ID *", "Your Flock Game ID"),
                gameId);

            if (string.IsNullOrWhiteSpace(newGameId))
                EditorGUILayout.HelpBox("Game ID is required", MessageType.Warning);

            GUILayout.Space(5);

            string newGameVersionId = EditorGUILayout.TextField(
                new GUIContent("Game Version ID *", "Your Flock Game Version ID (sent as X-Game-Version-ID header)"),
                gameVersionId);

            if (string.IsNullOrWhiteSpace(newGameVersionId))
                EditorGUILayout.HelpBox("Game Version ID is required", MessageType.Warning);

            GUILayout.Space(5);

            bool newEnableDebugLogs = EditorGUILayout.Toggle(
                new GUIContent("Enable Debug Logs", "Show detailed SDK logs in console (useful for troubleshooting)"),
                enableDebugLogs);

            if (EditorGUI.EndChangeCheck())
            {
                apiUrl = newApiUrl;
                apiKey = newApiKey;
                gameId = newGameId;
                gameVersionId = newGameVersionId;
                enableDebugLogs = newEnableDebugLogs;
                _hasUnsavedChanges = true;
            }

            GUILayout.Space(5);
            EditorGUILayout.LabelField("Tip: Get your API Key, Game ID and Game Version ID from the Flock dashboard", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();

            GUILayout.Space(10);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Headers sent with every API call:", EditorStyles.boldLabel);
            GUILayout.Label("  X-Flock-API-Key: <your API key>", EditorStyles.miniLabel);
            GUILayout.Label("  X-Game-Version-ID: <your game version ID>", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            GUILayout.Space(10);

            if (!string.IsNullOrEmpty(errorMessage) && EditorApplication.timeSinceStartup < messageTimer)
                EditorGUILayout.HelpBox(errorMessage, MessageType.Error);

            if (!string.IsNullOrEmpty(successMessage) && EditorApplication.timeSinceStartup < messageTimer)
                EditorGUILayout.HelpBox(successMessage, MessageType.Info);

            GUILayout.Space(10);

            if (existingConfig != null && !string.IsNullOrWhiteSpace(apiKey))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label("Quick Actions", EditorStyles.boldLabel);
                GUILayout.Space(5);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Test Configuration", GUILayout.Height(30)))
                    TestConfiguration();
                if (GUILayout.Button("Locate Config File", GUILayout.Height(30)))
                {
                    EditorGUIUtility.PingObject(existingConfig);
                    Selection.activeObject = existingConfig;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
                GUILayout.Space(10);
            }

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = _hasUnsavedChanges || string.IsNullOrWhiteSpace(apiKey) == false;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("Save Configuration", GUILayout.Height(35)))
                SaveConfig();
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            GUI.backgroundColor = new Color(0.8f, 0.6f, 0.3f);
            if (GUILayout.Button("Reset", GUILayout.Height(35), GUILayout.Width(100)))
                ResetConfig();
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndHorizontal();

            GUILayout.Space(5);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Documentation", EditorStyles.linkLabel))
                Application.OpenURL("https://docs.flock.qwacks.com");
            GUILayout.Label("|", EditorStyles.miniLabel);
            if (GUILayout.Button("Support", EditorStyles.linkLabel))
                Application.OpenURL("https://support.qwacks.com");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            GUILayout.Space(10);

            EditorGUILayout.EndScrollView();
        }

        private void LoadConfig()
        {
            existingConfig = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>("Assets/Resources/FlockConfig.asset");

            if (existingConfig != null)
            {
                apiUrl = existingConfig.apiUrl ?? "https://api-flock.qwacks.com";
                apiKey = existingConfig.ApiKey ?? "";
                gameId = existingConfig.gameId ?? "";
                gameVersionId = existingConfig.gameVersionId ?? "";
                enableDebugLogs = existingConfig.enableDebugLogs;
            }
            else
            {
                apiUrl = EditorPrefs.GetString("Flock_ApiUrl", "https://api-flock.qwacks.com");
                apiKey = EditorPrefs.GetString("Flock_ApiKey", "");
                gameId = EditorPrefs.GetString("Flock_GameId", "");
                gameVersionId = EditorPrefs.GetString("Flock_GameVersionId", "");
                enableDebugLogs = EditorPrefs.GetBool("Flock_EnableDebugLogs", false);
            }

            _hasUnsavedChanges = false;
        }

        private void SaveConfig()
        {
            errorMessage = "";
            successMessage = "";

            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                errorMessage = "API URL is required!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                errorMessage = "API Key is required!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            if (string.IsNullOrWhiteSpace(gameId))
            {
                errorMessage = "Game ID is required!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            if (string.IsNullOrWhiteSpace(gameVersionId))
            {
                errorMessage = "Game Version ID is required!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            apiUrl = apiUrl.Trim();
            apiKey = apiKey.Trim();
            gameId = gameId.Trim();
            gameVersionId = gameVersionId.Trim();

            if (!Uri.IsWellFormedUriString(apiUrl, UriKind.Absolute))
            {
                errorMessage = "API URL must be a valid URL (e.g., https://api-flock.qwacks.com)";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            try
            {
                EditorPrefs.SetString("Flock_ApiUrl", apiUrl);
                EditorPrefs.SetString("Flock_ApiKey", apiKey);
                EditorPrefs.SetString("Flock_GameId", gameId);
                EditorPrefs.SetString("Flock_GameVersionId", gameVersionId);
                EditorPrefs.SetBool("Flock_EnableDebugLogs", enableDebugLogs);

                string resourcesPath = "Assets/Resources";
                if (!Directory.Exists(resourcesPath))
                {
                    Directory.CreateDirectory(resourcesPath);
                    AssetDatabase.Refresh();
                }

                string configPath = "Assets/Resources/FlockConfig.asset";
                FlockConfigAsset configAsset = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(configPath);

                if (configAsset == null)
                {
                    configAsset = CreateInstance<FlockConfigAsset>();
                    AssetDatabase.CreateAsset(configAsset, configPath);
                    Debug.Log("Created new FlockConfig.asset");
                }

                configAsset.apiUrl = apiUrl;
                configAsset.ApiKey = apiKey;
                configAsset.gameId = gameId;
                configAsset.gameVersionId = gameVersionId;
                configAsset.enableDebugLogs = enableDebugLogs;

                EditorUtility.SetDirty(configAsset);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                existingConfig = configAsset;
                _hasUnsavedChanges = false;

                successMessage = "Configuration saved successfully!";
                messageTimer = EditorApplication.timeSinceStartup + 3;

                Debug.Log($"Flock SDK configuration saved: API={apiUrl}, GameId={gameId}, GameVersionId={gameVersionId}");
            }
            catch (System.Exception ex)
            {
                errorMessage = $"Failed to save configuration: {ex.Message}";
                messageTimer = EditorApplication.timeSinceStartup + 5;
                Debug.LogError($"Failed to save Flock configuration: {ex}");
            }
        }

        private void ResetConfig()
        {
            if (EditorUtility.DisplayDialog("Reset Configuration",
                "Are you sure you want to reset all Flock SDK settings to defaults?",
                "Yes", "Cancel"))
            {
                apiUrl = "https://api-flock.qwacks.com";
                apiKey = "";
                gameId = "";
                gameVersionId = "";
                enableDebugLogs = false;
                errorMessage = "";
                successMessage = "";
                _hasUnsavedChanges = true;

                successMessage = "Configuration reset to defaults. Click Save to apply changes.";
                messageTimer = EditorApplication.timeSinceStartup + 4;
            }
        }

        private async void TestConfiguration()
        {
            errorMessage = "";
            successMessage = "";

            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                errorMessage = "Cannot test: API URL is missing!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                errorMessage = "Cannot test: API Key is missing!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            if (!Uri.IsWellFormedUriString(apiUrl, UriKind.Absolute))
            {
                errorMessage = "Cannot test: API URL is invalid!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                return;
            }

            EditorUtility.DisplayProgressBar("Testing Configuration", "Validating API connection...", 0.3f);

            try
            {
                var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                httpClient.DefaultRequestHeaders.Add("X-Flock-API-Key", apiKey);
                httpClient.DefaultRequestHeaders.Add("X-Game-Version-ID", gameVersionId);

                var response = await httpClient.GetAsync($"{apiUrl}/healthz");

                EditorUtility.DisplayProgressBar("Testing Configuration", "Verifying API key...", 0.6f);

                string testResults = "";

                if (response.IsSuccessStatusCode)
                {
                    testResults = $"Configuration Test Results:\n\n" +
                                $"API URL: {apiUrl} (Connection successful)\n" +
                                $"API Key: {new string('*', Math.Min(apiKey.Length, 20))} (Authenticated)\n" +
                                $"Game ID: {gameId}\n" +
                                $"Game Version ID: {gameVersionId}\n" +
                                $"Debug Logs: {(enableDebugLogs ? "Enabled" : "Disabled")}\n\n" +
                                $"Configuration is valid and ready to use!";

                    successMessage = "Configuration test passed!";
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    testResults = $"Configuration Test Results:\n\n" +
                                $"API URL: {apiUrl} (Connection successful)\n" +
                                $"API Key: Invalid or unauthorized\n\n" +
                                $"Please check your API key and try again.";

                    errorMessage = "API Key authentication failed!";
                }
                else
                {
                    testResults = $"Configuration Test Results:\n\n" +
                                $"API URL: {apiUrl} (HTTP {(int)response.StatusCode})\n" +
                                $"API returned unexpected status code: {response.StatusCode}";

                    errorMessage = $"API test returned status: {response.StatusCode}";
                }

                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Configuration Test", testResults, "OK");

                messageTimer = EditorApplication.timeSinceStartup + 3;
                Debug.Log($"Flock SDK Configuration Test:\n{testResults}");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                EditorUtility.ClearProgressBar();

                string testResults = $"Configuration Test Results:\n\n" +
                                    $"API URL: {apiUrl} (Connection failed)\n" +
                                    $"Error: {ex.Message}\n\n" +
                                    $"Please check your API URL and network connection.";

                EditorUtility.DisplayDialog("Configuration Test Failed", testResults, "OK");

                errorMessage = "Failed to connect to API!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                Debug.LogError($"Flock SDK Configuration Test Failed: {ex.Message}");
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                EditorUtility.ClearProgressBar();

                string testResults = $"Configuration Test Results:\n\n" +
                                    $"API URL: {apiUrl} (Connection timeout)\n\n" +
                                    $"The API request timed out after 10 seconds.\n" +
                                    $"Please check your API URL and network connection.";

                EditorUtility.DisplayDialog("Configuration Test Failed", testResults, "OK");

                errorMessage = "API connection timeout!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                Debug.LogError("Flock SDK Configuration Test: Connection timeout");
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();

                string testResults = $"Configuration Test Results:\n\n" +
                                    $"Test failed with error:\n{ex.Message}";

                EditorUtility.DisplayDialog("Configuration Test Failed", testResults, "OK");

                errorMessage = "Configuration test failed!";
                messageTimer = EditorApplication.timeSinceStartup + 3;
                Debug.LogError($"Flock SDK Configuration Test Error: {ex}");
            }
        }
    }
}
