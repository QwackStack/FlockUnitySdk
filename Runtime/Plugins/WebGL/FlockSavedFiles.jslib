// Copies the SDK's saved files to the browser's storage after it changed one, so they outlive the page.
var FlockSavedFilesLibrary = {
  $FlockSavedFilesCopy: { running: false, again: false, warned: false },

  FlockCopySavedFilesToBrowserStorage__deps: ['$FlockSavedFilesCopy'],
  FlockCopySavedFilesToBrowserStorage: function () {
    // Said once a page: with browser storage unavailable (a private window) every copy fails.
    var warnOnce = function (error) {
      if (FlockSavedFilesCopy.warned) return;
      FlockSavedFilesCopy.warned = true;
      console.warn('[Flock SDK] Saved files could not be copied to browser storage: ' + error);
    };
    try {
      // The game asked Unity to copy every change itself.
      if (Module["autoSyncPersistentDataPath"]) return;
      // Unity's own queue: one copy at a time, and one more when changes land during it.
      var unityMount = Module.__unityIdbfsMount;
      if (unityMount && typeof IDBFS !== 'undefined' && IDBFS.queuePersist) {
        IDBFS.queuePersist(unityMount.mount);
        return;
      }
      // Unity versions without that queue: the same rule, kept here.
      if (FlockSavedFilesCopy.running) {
        FlockSavedFilesCopy.again = true;
        return;
      }
      FlockSavedFilesCopy.running = true;
      var copy = function () {
        try {
          FS.syncfs(false, function (error) {
            if (error) warnOnce(error);
            if (FlockSavedFilesCopy.again) {
              FlockSavedFilesCopy.again = false;
              copy();
            } else {
              FlockSavedFilesCopy.running = false;
            }
          });
        } catch (error) {
          // A copy that could not start must not leave every later one waiting behind it.
          FlockSavedFilesCopy.running = false;
          FlockSavedFilesCopy.again = false;
          warnOnce(error);
        }
      };
      copy();
    } catch (error) {
      warnOnce(error);
    }
  }
};

mergeInto(LibraryManager.library, FlockSavedFilesLibrary);
