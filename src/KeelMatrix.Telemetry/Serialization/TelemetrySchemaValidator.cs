// Copyright (c) KeelMatrix

using System.Globalization;
using System.Text.RegularExpressions;
using KeelMatrix.Telemetry.Events;

namespace KeelMatrix.Telemetry.Serialization {
    /// <summary>
    /// Validates telemetry events against the client-side schema rules.
    /// </summary>
    internal static class TelemetrySchemaValidator {
#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
        private static readonly Regex IsoWeekRegex = new(@"^\d{4}-W\d{2}$", RegexOptions.Compiled);
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.

        /// <summary>
        /// Validates the given telemetry event.
        /// </summary>
        internal static bool IsValid(TelemetryEventBase telemetryEvent, string expectedToolName) {
            if (telemetryEvent.SchemaVersion != TelemetryConfig.SchemaVersion)
                return false;

            if (!string.Equals(telemetryEvent.Tool, expectedToolName, StringComparison.Ordinal))
                return false;

            if (telemetryEvent.ToolVersion.Length > TelemetryConfig.ToolVersionMaxLength)
                return false;

            if (telemetryEvent.TelemetryVersion.Length > TelemetryConfig.ToolVersionMaxLength)
                return false;

            if (telemetryEvent.ProjectHash.Length > TelemetryConfig.ProjectHashMaxLength)
                return false;

            return telemetryEvent switch {
                ActivationEvent a => ValidateActivation(a),
                HeartbeatEvent h => ValidateHeartbeat(h),
                _ => false
            };
        }

        private static bool ValidateActivation(ActivationEvent a) {
            if (a.Runtime.Length > TelemetryConfig.RuntimeMaxLength)
                return false;

            if (a.Os.Length > TelemetryConfig.OsMaxLength)
                return false;

            if (!DateTimeOffset.TryParseExact(
                a.Timestamp,
                TelemetryConfig.TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
                return false;

            // Ensure it is exactly UTC
            if (parsed.Offset != TimeSpan.Zero)
                return false;

            return true;
        }

        private static bool ValidateHeartbeat(HeartbeatEvent h) {
            return IsoWeekRegex.IsMatch(h.Week);
        }
    }
}
