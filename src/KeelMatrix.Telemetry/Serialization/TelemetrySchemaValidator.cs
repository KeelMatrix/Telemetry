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

            if (!TelemetryConfig.IsValidToolName(telemetryEvent.Tool))
                return false;

            if (telemetryEvent.ToolVersion.Length > TelemetryConfig.ToolVersionMaxLength)
                return false;

            if (telemetryEvent.TelemetryVersion.Length > TelemetryConfig.ToolVersionMaxLength)
                return false;

            if (!IsValidHash(telemetryEvent.ProjectHash, TelemetryConfig.ProjectHashMaxLength))
                return false;

            if (!IsValidHash(telemetryEvent.InstallationHash, TelemetryConfig.InstallationHashMaxLength))
                return false;

            return telemetryEvent switch {
                ActivationEvent a => ValidateActivation(a),
                HeartbeatEvent h => ValidateHeartbeat(h),
                _ => false
            };
        }

        internal static bool IsValidHash(string value, int maxLength) {
            if (value.Length != maxLength)
                return false;

            foreach (var character in value) {
                if (character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))
                    return false;
            }

            return true;
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
            if (!IsoWeekRegex.IsMatch(h.Week))
                return false;

            var year = ParseDigits(h.Week, 0, 4);
            var week = ParseDigits(h.Week, 6, 2);
            return week <= GetIsoWeeksInYear(year);
        }

        private static int ParseDigits(string value, int start, int length) {
            var result = 0;
            for (var i = start; i < start + length; i++)
                result = result * 10 + value[i] - '0';

            return result;
        }

        private static int GetIsoWeeksInYear(int year) {
#if NET8_0_OR_GREATER
            return ISOWeek.GetWeeksInYear(year);
#else
            var januaryFirst = new DateTime(year, 1, 1);
            var dayOfWeek = ((int)januaryFirst.DayOfWeek + 6) % 7;
            return dayOfWeek == 3 || (dayOfWeek == 2 && DateTime.IsLeapYear(year)) ? 53 : 52;
#endif
        }
    }
}
