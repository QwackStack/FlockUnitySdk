using UnityEngine;
using UnityEditor;
using System;
using Flock.Config;
using Flock.Auth;

namespace Flock.Editor
{
    public class FlockConfigWindow : EditorWindow
    {
        private string gameId = "";
        private string clientId = "";
        private string clientSecret = "";
        private string apiUrl = "https://api-flock.qwacks.com";
        private bool enableDebugLogs = false;
        private TimeSpan timeout = TimeSpan.FromSeconds(30);
        private bool[] enabledAuthMethods = new bool[Enum.GetValues(typeof(AuthProviderType)).Length];
        private string[] authMethodNames = Enum.GetNames(typeof(AuthProviderType));

        private Vector2 scrollPosition;
        private GUIStyle headerStyle;
        private GUIStyle sectionStyle;
        private GUIStyle errorStyle;
        private string errorMessage = "";

        [MenuItem("Window/Flock/Configuration")]
        public static void ShowWindow()
        {
            var window = GetWindow<FlockConfigWindow>("Flock Configuration");
            window.minSize = new Vector2(500, 600);
            window.LoadConfig();
        }

        private void OnEnable()
        {
            // Initialize styles
            headerStyle = new GUIStyle
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black }
            };

            sectionStyle = new GUIStyle
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                margin = new RectOffset(0, 0, 10, 5),
                normal = { textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black }
            };

            errorStyle = new GUIStyle
            {
                normal = { textColor = Color.red },
                wordWrap = true
            };
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            // Header
            GUILayout.Space(10);
            GUILayout.Label("Flock SDK Configuration", headerStyle);
            GUILayout.Space(20);

            // Required Settings
            GUILayout.Label("Required Settings", sectionStyle);
            EditorGUI.indentLevel++;

            gameId = EditorGUILayout.TextField(new GUIContent("Game ID", "Your Flock Game ID"), gameId);
            clientId = EditorGUILayout.TextField(new GUIContent("Client ID", "Your Flock Client ID"), clientId);
            
            // Client Secret with toggle to show/hide
            EditorGUILayout.BeginHorizontal();
            clientSecret = EditorGUILayout.PasswordField(new GUIContent("Client Secret", "Your Flock Client Secret"), clientSecret);
            if (GUILayout.Button(EditorGUIUtility.IconContent("d_ViewToolOrbit"), GUILayout.Width(30)))
            {
                // Toggle between password and normal text field
                var temp = GUI.skin.textField;
                GUI.skin.textField = GUI.skin.textArea;
                clientSecret = EditorGUILayout.TextField(clientSecret);
                GUI.skin.textField = temp;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
            GUILayout.Space(20);

            // Authentication Methods
            GUILayout.Label("Authentication Methods", sectionStyle);
            EditorGUI.indentLevel++;

            for (int i = 0; i < enabledAuthMethods.Length; i++)
            {
                enabledAuthMethods[i] = EditorGUILayout.Toggle(
                    new GUIContent(
                        authMethodNames[i],
                        $"Enable {authMethodNames[i]} authentication"
                    ),
                    enabledAuthMethods[i]
                );
            }

            EditorGUI.indentLevel--;
            GUILayout.Space(20);

            // Optional Settings
            GUILayout.Label("Optional Settings", sectionStyle);
            EditorGUI.indentLevel++;

            apiUrl = EditorGUILayout.TextField(new GUIContent("API URL", "The Flock API URL"), apiUrl);
            enableDebugLogs = EditorGUILayout.Toggle(new GUIContent("Enable Debug Logs", "Enable detailed logging for debugging"), enableDebugLogs);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Timeout", "Request timeout in seconds"));
            float timeoutSeconds = (float)timeout.TotalSeconds;
            timeoutSeconds = EditorGUILayout.Slider(timeoutSeconds, 1, 120);
            timeout = TimeSpan.FromSeconds(timeoutSeconds);
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
            GUILayout.Space(20);

            // Error message
            if (!string.IsNullOrEmpty(errorMessage))
            {
                EditorGUILayout.HelpBox(errorMessage, MessageType.Error);
            }

            // Buttons
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Save", GUILayout.Height(30)))
            {
                SaveConfig();
            }
            
            if (GUILayout.Button("Reset", GUILayout.Height(30)))
            {
                ResetConfig();
            }
            
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(10);

            EditorGUILayout.EndScrollView();
        }

        private void LoadConfig()
        {
            // Load from EditorPrefs
            gameId = EditorPrefs.GetString("Flock_GameId", "");
            clientId = EditorPrefs.GetString("Flock_ClientId", "");
            clientSecret = EditorPrefs.GetString("Flock_ClientSecret", "");
            apiUrl = EditorPrefs.GetString("Flock_ApiUrl", "https://api.flock.qwacks.com");
            enableDebugLogs = EditorPrefs.GetBool("Flock_EnableDebugLogs", false);
            timeout = TimeSpan.FromSeconds(EditorPrefs.GetFloat("Flock_TimeoutSeconds", 30));

            // Load enabled auth methods
            for (int i = 0; i < enabledAuthMethods.Length; i++)
            {
                enabledAuthMethods[i] = EditorPrefs.GetBool($"Flock_Auth_{authMethodNames[i]}", false);
            }
        }

        private void SaveConfig()
        {
            errorMessage = "";

            // Validate
            if (string.IsNullOrEmpty(gameId))
                errorMessage += "Game ID is required\n";
            if (string.IsNullOrEmpty(clientId))
                errorMessage += "Client ID is required\n";
            if (string.IsNullOrEmpty(clientSecret))
                errorMessage += "Client Secret is required\n";
            if (!Uri.IsWellFormedUriString(apiUrl, UriKind.Absolute))
                errorMessage += "API URL must be a valid URL\n";

            if (!string.IsNullOrEmpty(errorMessage))
                return;

            // Save to EditorPrefs
            EditorPrefs.SetString("Flock_GameId", gameId);
            EditorPrefs.SetString("Flock_ClientId", clientId);
            EditorPrefs.SetString("Flock_ClientSecret", clientSecret);
            EditorPrefs.SetString("Flock_ApiUrl", apiUrl);
            EditorPrefs.SetBool("Flock_EnableDebugLogs", enableDebugLogs);
            EditorPrefs.SetFloat("Flock_TimeoutSeconds", (float)timeout.TotalSeconds);

            // Save enabled auth methods
            for (int i = 0; i < enabledAuthMethods.Length; i++)
            {
                EditorPrefs.SetBool($"Flock_Auth_{authMethodNames[i]}", enabledAuthMethods[i]);
            }

            // Create runtime config
            var config = new FlockConfig.Builder()
                .SetGameId(gameId)
                .SetApiUrl(apiUrl)
                .SetEnableDebugLogs(enableDebugLogs)
                .SetTimeout(timeout)
                .SetEnabledAuthMethods(enabledAuthMethods)
                .Build();

            // Save to a ScriptableObject for runtime use
            var configAsset = CreateInstance<FlockConfigAsset>();
            configAsset.Config = config;

            const string configPath = "Assets/Resources/FlockConfig.asset";
            AssetDatabase.CreateAsset(configAsset, configPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("Flock configuration saved successfully!");
            ShowNotification(new GUIContent("Configuration saved!"));
        }

        private void ResetConfig()
        {
            if (EditorUtility.DisplayDialog("Reset Configuration",
                "Are you sure you want to reset all Flock configuration settings?",
                "Yes", "No"))
            {
                gameId = "";
                clientId = "";
                clientSecret = "";
                apiUrl = "https://api.flock.qwacks.com";
                enableDebugLogs = false;
                timeout = TimeSpan.FromSeconds(30);
                errorMessage = "";

                // Reset auth methods
                for (int i = 0; i < enabledAuthMethods.Length; i++)
                {
                    enabledAuthMethods[i] = false;
                }
            }
        }
    }

    // ScriptableObject to store runtime configuration
    public class FlockConfigAsset : ScriptableObject
    {
        public FlockConfig Config;
    }
} 