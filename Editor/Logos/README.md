# Editor Logos
This is just an inital ReadMe for content set up, might be changed.

Drop logo PNGs in this folder to have them appear in the Qwacks editor window
(Flock > Settings).

## Expected filenames

| File            | Slot in window           |
| --------------- | ------------------------ |
| `QwacksLogo.png` | Top-left header logo     |
| `FlockLogo.png`  | Top-right header logo    |

The window finds these textures by name via `AssetDatabase.FindAssets`, so
they will be picked up from anywhere in the project — this folder is just
the recommended location to keep them with the SDK.

To use different filenames, edit the `QwacksLogoName` and `FlockLogoName`
constants at the top of `Editor/QwacksEditorWindow.cs`.