# Flock Unity SDK - Package Structure

This document describes the complete package structure of the Flock Unity SDK, and was generated.

## Package Information

- **Package Name**: `com.qwacks.flock-sdk`
- **Display Name**: Flock Unity SDK
- **Version**: 1.0.0
- **Unity Version**: 2020.3 or higher
- **Dependencies**: Newtonsoft.Json 3.0.2

## Directory Structure

```
Assets/FlockUnitySdk/
├── Runtime/                          # Runtime SDK code
│   ├── Achievements/                 # Achievement system
│   │   └── FlockAchievementProvider.cs
│   ├── Auth/                         # Authentication providers
│   │   └── FlockAuthProvider.cs
│   ├── Config/                       # Configuration management
│   │   ├── FlockConfigAsset.cs       # ScriptableObject for runtime config
│   │   ├── FlockConfigProvider.cs    # Config API provider
│   │   ├── FlockInitConfig.cs        # Initialization config
│   │   └── Environment.cs            # Environment enum
│   ├── Http/                         # HTTP client utilities
│   │   └── HttpClient.cs
│   ├── Leaderboard/                  # Leaderboard system
│   │   └── FlockLeaderboardProvider.cs
│   ├── Models/                       # Data models
│   │   ├── Achievement.cs
│   │   ├── AuthResponse.cs
│   │   ├── GameConfig.cs
│   │   ├── LeaderboardEntry.cs
│   │   ├── LeaderboardInfo.cs
│   │   ├── LoginRequest.cs
│   │   ├── LoginResponse.cs
│   │   ├── PaginatedResponse.cs
│   │   ├── PlayerData.cs
│   │   ├── RegisterRequest.cs
│   │   └── RegisterResponse.cs
│   ├── Services/                     # Core services
│   │   └── PlayerDataService.cs
│   ├── FlockClient.cs                # Main SDK client
│   └── Flock.Runtime.asmdef          # Runtime assembly definition
│
├── Editor/                           # Editor tools
│   ├── FlockConfigWindow.cs          # Configuration window
│   ├── FlockPackageBuilder.cs        # Package builder tool
│   └── Flock.Editor.asmdef           # Editor assembly definition
│
├── Tests/                            # Unit tests
│   └── Editor/
│       └── Flock.Tests.Editor.asmdef # Test assembly definition
│
├── Samples~/                         # Sample scenes and scripts
│   └── BasicUsage/
│       ├── Scenes/
│       │   └── FlockDemo.unity       # Demo scene
│       └── Scripts/
│           └── FlockSDKExample.cs    # Example integration script
│
├── Documentation~/                   # Additional documentation
│   ├── FlockSDK.md                   # SDK documentation
│   └── PackageStructure.md           # This file
│
├── package.json                      # Package manifest
├── README.md                         # Main documentation
├── CHANGELOG.md                      # Version history
└── LICENSE                           # License file
```

## Assembly Definitions

### Flock.Runtime
- **Location**: `Runtime/Flock.Runtime.asmdef`
- **Namespace**: Flock
- **References**: Unity.Newtonsoft.Json
- **Platforms**: All
- **Purpose**: Core SDK runtime functionality

### Flock.Editor
- **Location**: `Editor/Flock.Editor.asmdef`
- **Namespace**: Flock.Editor
- **References**: Flock.Runtime
- **Platforms**: Editor only
- **Purpose**: Unity Editor integration tools

### Flock.Tests.Editor
- **Location**: `Tests/Editor/Flock.Tests.Editor.asmdef`
- **Namespace**: Flock.Tests
- **References**: Flock.Runtime, Flock.Editor, UnityEngine.TestRunner
- **Platforms**: Editor only (tests)
- **Purpose**: Unit and integration tests

## Key Components

### Runtime Components

#### FlockClient
Main entry point for SDK. Provides access to all services.
- Location: `Runtime/FlockClient.cs`
- Namespace: `Flock`

#### Service Providers
- **FlockAchievementProvider**: Achievement management
- **FlockLeaderboardProvider**: Leaderboard management
- **FlockConfigProvider**: Game configuration
- **PlayerDataService**: Player data management

#### Configuration
- **FlockInitConfig**: SDK initialization configuration
- **FlockConfigAsset**: ScriptableObject for runtime config storage
- **Environment**: Enum for Production/Development environments

### Editor Components

#### FlockConfigWindow
User-friendly configuration window accessible via `Window > Flock SDK > Configuration`.

**Features:**
- Game ID and environment configuration
- API URL customization
- Debug log toggle
- Configuration validation
- Test configuration tool
- Quick actions

#### FlockPackageBuilder
Package builder tool accessible via `Window > Flock SDK > Package Builder`.

**Features:**
- Version control
- Output path configuration
- Meta file inclusion options
- Dependency inclusion
- Interactive/non-interactive modes
- Build validation

## Resources

The SDK uses Unity's Resources folder for runtime configuration:

```
Assets/Resources/
└── FlockConfig.asset              # Generated by Configuration Window
```

This file is automatically created when you save configuration in the FlockConfigWindow.

## Samples

Sample content is stored in `Samples~/` directory and can be imported through the Unity Package Manager.

### BasicUsage Sample
Demonstrates:
- SDK initialization
- Player authentication
- Achievement unlocking
- Leaderboard score submission
- Player data management

## Documentation Files

| File | Purpose |
|------|---------|
| `README.md` | Main documentation with quick start and API reference |
| `CHANGELOG.md` | Version history and release notes |
| `Documentation~/FlockSDK.md` | Detailed SDK documentation |
| `Documentation~/PackageStructure.md` | This file - package structure guide |
| `LICENSE` | License information |

## Package.json Structure

```json
{
    "name": "com.qwacks.flock-sdk",
    "version": "1.0.0",
    "displayName": "Flock Unity SDK",
    "description": "Comprehensive game backend SDK...",
    "unity": "2020.3",
    "dependencies": {
        "com.unity.nuget.newtonsoft-json": "3.0.2"
    },
    "samples": [
        {
            "displayName": "Basic Usage Example",
            "description": "Basic SDK examples",
            "path": "Samples~/BasicUsage"
        }
    ]
}
```

## Best Practices

### Adding New Features
1. Add runtime code to appropriate folder in `Runtime/`
2. Create models in `Runtime/Models/`
3. Add editor tools to `Editor/`
4. Write tests in `Tests/Editor/`
5. Update documentation
6. Update CHANGELOG.md

### Namespace Conventions
- Runtime: `Flock`, `Flock.Config`, `Flock.Auth`, etc.
- Editor: `Flock.Editor`
- Tests: `Flock.Tests`
- Models: `Flock.Models`

### File Organization
- One class per file
- File name matches class name
- Organize by feature (Achievements, Leaderboards, etc.)
- Keep models separate from logic

## Building the Package

Use the Package Builder tool:
1. Open `Window > Flock SDK > Package Builder`
2. Set version number
3. Configure output path
4. Click "Build Package"
5. Package will be saved as `FlockSDK-{version}.unitypackage`

## Installation Methods

### Method 1: Unity Package Manager (Local)
1. Open Package Manager
2. Click "+" → "Add package from disk"
3. Select `package.json` from FlockUnitySdk folder

### Method 2: .unitypackage File
1. Build package using Package Builder
2. Double-click the .unitypackage file
3. Import into Unity project

### Method 3: Git URL
```
https://github.com/QwackStack/FlockUnitySDK.git
```

## Version Control

### Recommended .gitignore

```gitignore
# Flock SDK - Do not commit
Assets/Resources/FlockConfig.asset
Assets/Resources/FlockConfig.asset.meta

# Build output
Build/
*.unitypackage
```

## Support

For questions about package structure or SDK development:
- Documentation: https://docs.flock.qwacks.com
- Support: https://support.qwacks.com
- Issues: https://github.com/QwackStack/FlockUnitySDK/issues
