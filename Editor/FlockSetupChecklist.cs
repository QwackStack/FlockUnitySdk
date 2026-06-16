using System.Collections.Generic;

namespace Flock.Editor
{
    /// One checklist row's state. Done = satisfied; Required = a hard blocker; Advisory =
    /// recommended but not required; Manual = needs an explicit user action to confirm.
    public enum FlockSetupItemState
    {
        Done,
        Required,
        Advisory,
        Manual
    }

    /// A single checklist row.
    public readonly struct FlockSetupItem
    {
        public readonly string Key;
        public readonly string Label;
        public readonly FlockSetupItemState State;
        public readonly string Detail;

        public FlockSetupItem(string key, string label, FlockSetupItemState state, string detail)
        {
            Key = key;
            Label = label;
            State = state;
            Detail = detail;
        }
    }

    /// The facts the checklist is built from. Plain data (no Unity calls) so the mapping below
    /// is unit-testable; the window gathers these from the asset / scene / SessionState.
    public readonly struct FlockSetupFacts
    {
        public readonly bool ConfigAssetExists;
        public readonly bool CredentialsValid;
        public readonly string CredentialsError;
        public readonly bool ConnectionVerified;
        public readonly string ConnectionDetail;
        public readonly bool BootstrapPresent;
        public readonly bool IncludeSchemas;
        public readonly bool SchemasGenerated;

        public FlockSetupFacts(bool configAssetExists, bool credentialsValid, string credentialsError,
            bool connectionVerified, string connectionDetail, bool bootstrapPresent,
            bool includeSchemas, bool schemasGenerated)
        {
            ConfigAssetExists = configAssetExists;
            CredentialsValid = credentialsValid;
            CredentialsError = credentialsError;
            ConnectionVerified = connectionVerified;
            ConnectionDetail = connectionDetail;
            BootstrapPresent = bootstrapPresent;
            IncludeSchemas = includeSchemas;
            SchemasGenerated = schemasGenerated;
        }
    }

    /// Maps gathered setup facts to an ordered checklist. Pure; see the design spec's table.
    public static class FlockSetupChecklist
    {
        public static List<FlockSetupItem> Build(FlockSetupFacts facts)
        {
            List<FlockSetupItem> items = new List<FlockSetupItem>();

            items.Add(new FlockSetupItem(
                "config", "FlockConfig asset",
                facts.ConfigAssetExists ? FlockSetupItemState.Done : FlockSetupItemState.Required,
                facts.ConfigAssetExists ? "Found." : "Not created yet."));

            // The remaining items only make sense once the asset exists.
            if (!facts.ConfigAssetExists)
                return items;

            items.Add(new FlockSetupItem(
                "credentials", "Credentials",
                facts.CredentialsValid ? FlockSetupItemState.Done : FlockSetupItemState.Required,
                facts.CredentialsValid ? "All set." : facts.CredentialsError));

            items.Add(new FlockSetupItem(
                "connection", "Connection",
                facts.ConnectionVerified ? FlockSetupItemState.Done : FlockSetupItemState.Manual,
                facts.ConnectionVerified
                    ? "Verified."
                    : string.IsNullOrEmpty(facts.ConnectionDetail) ? "Not verified yet." : facts.ConnectionDetail));

            items.Add(new FlockSetupItem(
                "bootstrap", "Scene bootstrap",
                facts.BootstrapPresent ? FlockSetupItemState.Done : FlockSetupItemState.Advisory,
                facts.BootstrapPresent ? "In the open scene." : "Not in the open scene (fine if you init in code)."));

            if (facts.IncludeSchemas)
            {
                items.Add(new FlockSetupItem(
                    "schemas", "Schemas",
                    facts.SchemasGenerated ? FlockSetupItemState.Done : FlockSetupItemState.Advisory,
                    facts.SchemasGenerated ? "Generated." : "Open the Code Generation tab to generate typed accessors."));
            }

            return items;
        }
    }
}
