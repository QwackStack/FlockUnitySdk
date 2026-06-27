namespace Flock.Editor
{
    /// What the play-mode guard should do at Play-enter.
    public enum FlockSetupVerdict
    {
        Ok,
        Warn,
        Block
    }

    /// Snapshot of the project's Flock setup. Pure data (no Unity calls) so the
    /// policy below is unit-testable without entering play mode.
    public readonly struct FlockSetupState
    {
        public readonly bool ConfigAssetExists;
        public readonly bool ConfigValid;
        public readonly bool GuardEnabled;
        public readonly bool AutoInitializeEnabled;
        public readonly bool BootstrapPresent;

        public FlockSetupState(bool configAssetExists, bool configValid, bool guardEnabled,
            bool autoInitializeEnabled, bool bootstrapPresent)
        {
            ConfigAssetExists = configAssetExists;
            ConfigValid = configValid;
            GuardEnabled = guardEnabled;
            AutoInitializeEnabled = autoInitializeEnabled;
            BootstrapPresent = bootstrapPresent;
        }
    }

    /// Maps a setup snapshot to a verdict. Priority order matches the design spec's table.
    public static class FlockSetupClassifier
    {
        public static FlockSetupVerdict Classify(FlockSetupState state)
        {
            // 0. Opted out on an existing asset → never interfere.
            if (state.ConfigAssetExists && !state.GuardEnabled)
                return FlockSetupVerdict.Ok;

            // 1. Asset-only init: no asset means nothing can initialize.
            if (!state.ConfigAssetExists)
                return FlockSetupVerdict.Block;

            // 2. Asset present but incomplete.
            if (!state.ConfigValid)
                return FlockSetupVerdict.Block;

            // 3. Valid config, but auto-init is off and there's no bootstrap to init it. A manual
            //    Create() call would still be fine, so this is a soft warning, not a block.
            if (!state.AutoInitializeEnabled && !state.BootstrapPresent)
                return FlockSetupVerdict.Warn;

            // 4. Auto-init on, or a bootstrap is present → init will happen.
            return FlockSetupVerdict.Ok;
        }
    }
}
