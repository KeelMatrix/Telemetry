// Copyright (c) KeelMatrix

using System.Globalization;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class TelemetryClockTests {
    [Fact]
    public void GetCurrentIsoWeek_MatchesFormat_YYYY_Www() {
        var week = TelemetryClock.GetCurrentIsoWeek();

        week.Should().NotBeNullOrWhiteSpace();
        week.Should().MatchRegex("^\\d{4}-W\\d{2}$");

        // Basic bounds sanity for week number.
        int weekNumber = int.Parse(week.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        weekNumber.Should().BeInRange(1, 53);
    }

    [Fact]
    public void GetCurrentIsoWeek_MatchesISOWeekForToday() {
        // TelemetryClock uses UTC date component.
        var todayUtc = DateTimeOffset.UtcNow.UtcDateTime.Date;

        int isoYear = ISOWeek.GetYear(todayUtc);
        int isoWeek = ISOWeek.GetWeekOfYear(todayUtc);
        var expected = $"{isoYear}-W{isoWeek:D2}";

        TelemetryClock.GetCurrentIsoWeek().Should().Be(expected);
    }
}
