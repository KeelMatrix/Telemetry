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

    [Theory]
    [InlineData(2020, 12, 31, "2020-W53")]
    [InlineData(2021, 1, 1, "2020-W53")]
    [InlineData(2021, 1, 4, "2021-W01")]
    [InlineData(2026, 12, 31, "2026-W53")]
    public void GetIsoWeek_UsesUtcIsoYearAtCalendarBoundaries(int year, int month, int day, string expected) {
        var date = new DateTime(year, month, day, 23, 59, 59, DateTimeKind.Utc);

        TelemetryClock.GetIsoWeek(date).Should().Be(expected);
    }
}
