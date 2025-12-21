# Changelog
All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2025-12-22

### Added
#### Core SDK
- Initial release of Flock Unity SDK
- FlockClient main SDK client for managing all services
- Async/await support for all network operations
- Thread-safe implementation

#### Authentication
- Multiple authentication methods:
  - Email/Password login and registration
  - Steam authentication
  - Game Center authentication (iOS)
  - Play Store authentication (Android)
  - Device ID authentication
- Secure token management (access & refresh tokens)
- OTP support for email authentication

#### Features
- **Achievements System**
  - Get all achievements for game
  - Get specific achievement by ID
  - Get player achievements
  - Unlock achievements
  - Update achievement progress
- **Leaderboards System**
  - Get all leaderboards
  - Get specific leaderboard by ID
  - Get leaderboard entries with pagination
  - Get top N entries
  - Get player's entry and rank
  - Submit scores
- **Player Data Management**
  - Create player data with custom fields
  - Update player data
  - Get player data by ID
  - Support for complex nested data structures
- **Game Configuration**
  - Get all game configurations
  - Get specific configuration by ID
  - Runtime configuration loading

#### Editor Tools
- **Configuration Window** (`Window > Flock SDK > Configuration`)
  - User-friendly configuration interface
  - Required settings: Game ID, Environment
  - Advanced settings: API URL, Debug Logs
  - Real-time validation with inline warnings
  - Status bar showing config state and unsaved changes
  - Quick Actions: Test Configuration, Locate Config File
  - Documentation and support links
  - Auto-save to FlockConfig.asset in Resources
- **Package Builder** (`Window > Flock SDK > Package Builder`)
  - Build .unitypackage files for distribution
  - Version control for packages
  - Customizable output path
  - Include/exclude .meta files option
  - Include dependencies option
  - Interactive and non-interactive build modes
  - Real-time file count and validation
  - Quick access to output folder

#### Documentation
  - Installation instructions
  - Configuration guide
  - Quick start examples
  - Complete API reference
  - Code examples for all features
  - Best practices
  - Error handling guide
  - Thread safety guidelines
  - Security best practices
  - Platform support matrix
  - Troubleshooting section
- IntelliSense XML documentation for all public APIs

#### Package Structure
- Proper Unity package structure with:
  - Runtime/ - Core SDK code
  - Editor/ - Editor tools and windows
  - Tests/ - Unit tests
  - Samples~/ - Example scenes and scripts
  - Documentation~/ - Additional documentation
- Assembly definition files for proper compilation
- Package.json with metadata and dependencies

#### Platform Support
- Windows (Standalone)
- macOS (Standalone)
- Linux (Standalone)
- iOS (with Game Center support)
- Android (with Play Store support)
- WebGL (limited)

### Technical Details
- Minimum Unity version: 2020.3
- Dependency: Newtonsoft.Json 3.0.2
- C# namespace: Flock
- Assembly: Flock.Runtime
- Editor Assembly: Flock.Editor

### Known Limitations
- WebGL has limited auth method support
- Console platforms not yet supported
- Requires internet connectivity for all features 