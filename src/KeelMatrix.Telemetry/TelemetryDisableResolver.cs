// Copyright (c) KeelMatrix

using System.Linq;
using System.Text.Json;
using GitDiscovery = KeelMatrix.Telemetry.ProjectIdentity.GitDiscovery;

namespace KeelMatrix.Telemetry {
    internal static class TelemetryDisableResolver {
        private const string RepositoryConfigFileName = "keelmatrix.telemetry.json";
        private const string DotEnvFileName = ".env";
        private const string DotEnvLocalFileName = ".env.local";
        private const int MaxRepositoryConfigBytes = 16 * 1024;
        private const int MaxRepositoryEnvFileBytes = 16 * 1024;

        private static readonly string[] OptOutVariableNames = [
            "KEELMATRIX_NO_TELEMETRY",
            "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DO_NOT_TRACK"
        ];
        private static Func<bool?>? repositoryDisableOverrideForTests;

        internal static bool IsProcessTelemetryDisabled() {
            return GetProcessTelemetryDisableDecision() == true;
        }

        internal static bool? GetProcessTelemetryDisableDecision() {
            return EvaluateProcessEnvironment() switch {
                DisableDecision.Disabled => true,
                DisableDecision.Enabled => false,
                _ => null
            };
        }

        internal static bool IsRepositoryTelemetryDisabledOnWorkerThread() {
            return IsRepositoryTelemetryDisabledOnWorkerThread(GetCandidateRepositoryRoots());
        }

        internal static bool IsRepositoryTelemetryDisabledOnWorkerThread(IReadOnlyList<string> repositoryRoots) {
            var overrideResolver = Volatile.Read(ref repositoryDisableOverrideForTests);
            if (overrideResolver is not null) {
                try {
                    return overrideResolver() == true;
                }
                catch {
                    return false;
                }
            }

            foreach (var repositoryRoot in repositoryRoots) {
                if (EvaluateRepository(repositoryRoot) == DisableDecision.Disabled)
                    return true;
            }

            return false;
        }

        internal static IReadOnlyList<string> GetCandidateRepositoryRoots() {
            var repositoryRoots = new List<string>();
            var seen = new HashSet<string>(
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

            foreach (var startingPoint in GitDiscovery.GetStartingPoints()) {
                if (!GitDiscovery.TryFindRepositoryRoot(startingPoint, out var repositoryRoot))
                    continue;

                if (seen.Add(repositoryRoot))
                    repositoryRoots.Add(repositoryRoot);
            }

            return repositoryRoots;
        }

        internal static void SetRepositoryDisableOverrideForTests(Func<bool?>? resolver) {
            Volatile.Write(ref repositoryDisableOverrideForTests, resolver);
        }

        private static DisableDecision EvaluateProcessEnvironment() {
            bool anyPresent = false;

            foreach (var variableName in OptOutVariableNames) {
                string? value;
                try {
                    value = Environment.GetEnvironmentVariable(variableName);
                }
                catch {
                    continue;
                }

                if (value is null)
                    continue;

                anyPresent = true;
                if (IsTruthyValue(value))
                    return DisableDecision.Disabled;
            }

            return anyPresent ? DisableDecision.Enabled : DisableDecision.Unspecified;
        }

        private static DisableDecision EvaluateRepository(string repositoryRoot) {
            var configDecision = EvaluateRepositoryConfig(Path.Combine(repositoryRoot, RepositoryConfigFileName));
            if (configDecision != DisableDecision.Unspecified)
                return configDecision;

            var dotEnvLocalDecision = EvaluateDotEnvFile(Path.Combine(repositoryRoot, DotEnvLocalFileName));
            if (dotEnvLocalDecision != DisableDecision.Unspecified)
                return dotEnvLocalDecision;

            var dotEnvDecision = EvaluateDotEnvFile(Path.Combine(repositoryRoot, DotEnvFileName));
            if (dotEnvDecision != DisableDecision.Unspecified)
                return dotEnvDecision;

            return DisableDecision.Unspecified;
        }

        private static DisableDecision EvaluateRepositoryConfig(string path) {
            if (!TryReadTextFileCapped(path, MaxRepositoryConfigBytes, out var text))
                return DisableDecision.Unspecified;

            try {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return DisableDecision.Unspecified;

                if (!TryGetPropertyCaseInsensitive(doc.RootElement, "disabled", out var disabledElement))
                    return DisableDecision.Unspecified;

                return IsTruthyJsonValue(disabledElement)
                    ? DisableDecision.Disabled
                    : DisableDecision.Enabled;
            }
            catch {
                return DisableDecision.Unspecified;
            }
        }

        private static DisableDecision EvaluateDotEnvFile(string path) {
            if (!TryReadTextFileCapped(path, MaxRepositoryEnvFileBytes, out var text))
                return DisableDecision.Unspecified;

            bool anyRecognizedAssignment = false;

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null) {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;

                if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring("export ".Length).TrimStart();

                int equalsIndex = trimmed.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                var key = trimmed.Substring(0, equalsIndex).Trim();
                if (!IsRecognizedOptOutKey(key))
                    continue;

                anyRecognizedAssignment = true;

                var value = NormalizeDotEnvValue(trimmed.Substring(equalsIndex + 1));
                if (IsTruthyValue(value))
                    return DisableDecision.Disabled;
            }

            return anyRecognizedAssignment ? DisableDecision.Enabled : DisableDecision.Unspecified;
        }

        private static bool TryReadTextFileCapped(string path, int maxBytes, out string text) {
            text = string.Empty;

            try {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                    return false;

                if (fi.Length <= 0 || fi.Length > maxBytes)
                    return false;

                text = File.ReadAllText(path);
                return text.Length > 0;
            }
            catch {
                return false;
            }
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value) {
            var property = element.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (!property.Equals(default(JsonProperty))) {
                value = property.Value;
                return true;
            }

            value = default;
            return false;
        }

        private static string NormalizeDotEnvValue(string value) {
            value = value.Trim();

            if (value.Length >= 2) {
                char first = value[0];
                char last = value[value.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\'')) {
                    value = value.Substring(1, value.Length - 2).Trim();
                }
            }

            return value;
        }

        private static bool IsTruthyJsonValue(JsonElement element) {
            return element.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.String => IsTruthyValue(element.GetString()),
                JsonValueKind.Number => IsTruthyValue(element.GetRawText()),
                _ => false
            };
        }

        private static bool IsRecognizedOptOutKey(string key) {
            return OptOutVariableNames.Any(variableName => key.Equals(variableName, StringComparison.Ordinal));
        }

        private static bool IsTruthyValue(string? value) {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value!.Trim();

            return value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("y", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private enum DisableDecision {
            Unspecified = 0,
            Enabled = 1,
            Disabled = 2
        }
    }
}
