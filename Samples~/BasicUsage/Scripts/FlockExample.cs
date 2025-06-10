using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;
using Flock;
using Flock.Config;
using Flock.Auth;

public class FlockExample : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private Button steamLoginButton;
    [SerializeField] private Button gameCenterLoginButton;
    [SerializeField] private Button deviceLoginButton;
    [SerializeField] private Text statusText;

    private FlockClient _client;

    private void Start()
    {
        InitializeSDK();
        SetupUI();
    }

    private void InitializeSDK()
    {
        // Load configuration from ScriptableObject
        var configAsset = Resources.Load<FlockConfigAsset>("FlockConfig");
        if (configAsset == null)
        {
            Debug.LogError("FlockConfig not found in Resources folder. Please configure the SDK first.");
            return;
        }

        _client = new FlockClient(configAsset.Config);
    }

    private void SetupUI()
    {
        if (steamLoginButton != null)
        {
            steamLoginButton.onClick.AddListener(OnSteamLoginClicked);
        }

        if (gameCenterLoginButton != null)
        {
            gameCenterLoginButton.onClick.AddListener(OnGameCenterLoginClicked);
        }

        if (deviceLoginButton != null)
        {
            deviceLoginButton.onClick.AddListener(OnDeviceLoginClicked);
        }
    }

    private async void OnSteamLoginClicked()
    {
        if (_client == null) return;

        try
        {
            UpdateStatus("Authenticating with Steam...");
            
            // In a real game, you would get this from the Steam SDK
            string steamTicket = "your-steam-ticket";
            
            var steamProvider = new SteamAuthProvider(_client, steamTicket);
            _client.SetAuthProvider(steamProvider);
            
            var authResult = await _client.AuthenticateAsync();
            
            if (authResult.Success)
            {
                UpdateStatus("Steam authentication successful!");
                await LoadPlayerData();
            }
            else
            {
                UpdateStatus($"Steam authentication failed: {authResult.ErrorMessage}");
            }
        }
        catch (System.Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private async void OnGameCenterLoginClicked()
    {
        if (_client == null) return;

        try
        {
            UpdateStatus("Authenticating with Game Center...");
            
            var gameCenterProvider = new GameCenterAuthProvider(_client);
            _client.SetAuthProvider(gameCenterProvider);
            
            var authResult = await _client.AuthenticateAsync();
            
            if (authResult.Success)
            {
                UpdateStatus("Game Center authentication successful!");
                await LoadPlayerData();
            }
            else
            {
                UpdateStatus($"Game Center authentication failed: {authResult.ErrorMessage}");
            }
        }
        catch (System.Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private async void OnDeviceLoginClicked()
    {
        if (_client == null) return;

        try
        {
            UpdateStatus("Authenticating with Device ID...");
            
            var deviceProvider = new DeviceIdAuthProvider(_client);
            _client.SetAuthProvider(deviceProvider);
            
            var authResult = await _client.AuthenticateAsync();
            
            if (authResult.Success)
            {
                UpdateStatus("Device authentication successful!");
                await LoadPlayerData();
            }
            else
            {
                UpdateStatus($"Device authentication failed: {authResult.ErrorMessage}");
            }
        }
        catch (System.Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private async Task LoadPlayerData()
    {
        try
        {
            UpdateStatus("Loading player data...");
            
            var playerData = await _client.PlayerData.GetAllAsync(
                page: 1,
                limit: 10
            );
            
            UpdateStatus($"Loaded {playerData.Count} player data entries");
        }
        catch (System.Exception ex)
        {
            UpdateStatus($"Error loading player data: {ex.Message}");
        }
    }

    private void UpdateStatus(string message)
    {
        Debug.Log(message);
        if (statusText != null)
        {
            statusText.text = message;
        }
    }

    private void OnDestroy()
    {
        if (_client != null)
        {
            _client.LogoutAsync().ConfigureAwait(false);
        }
    }
} 