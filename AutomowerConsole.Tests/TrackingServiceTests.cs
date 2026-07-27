using AutomowerConsole.Core;

namespace AutomowerConsole.Tests;

[TestFixture]
public class TrackingServiceAggregateDailyActivityTests
{
    // Real 'sessions' history captured from AM430X NERA, 2026-07-21 through
    // 2026-07-23 (2026-07-23), reproduced here as the AggregateDailyActivity
    // fixture rather than reconstructed as raw JSONL polls - the session
    // boundaries/activities/work areas/durations are what the real data
    // showed; only the seconds component is normalized to :00 (the original
    // 'sessions' display is minute-precision, so exact seconds aren't
    // recoverable - expected values below are computed from these same
    // fixture timestamps, not from the tool's original minute-rounded
    // display, so there's no double-rounding to chase).
    //
    // Notable real-world texture worth keeping in a regression fixture:
    // mixed "Charging" vs "Parked" activity labels for what's functionally
    // the same "at the charger" state (the API's own label is inconsistent -
    // per the user, a flat-battery "Parked" session can still represent real
    // charging time, only visible via a much-higher battery% on the *next*
    // session - not something AggregateDailyActivity attempts to detect;
    // CHARGING and PARKED_IN_CS are deliberately summed together as one
    // "Charging" total, not split by accuracy), work-area-less Parked/Going
    // home sessions (WorkAreaName null), NOT_APPLICABLE activity, and one
    // session spanning midnight (2026-07-22 14:00 -> 2026-07-23 06:57).
    private static TrackSession[] RealSessionHistory()
    {
        DateTimeOffset T(int day, int hour, int minute) => new(2026, 7, day, hour, minute, 0, TimeSpan.FromHours(2));
        TrackSession S(int startDay, string start, int endDay, string end, string activity, int batteryStart, int batteryEnd, string? workArea = null)
        {
            var (sh, sm) = Parse(start);
            var (eh, em) = Parse(end);
            return new TrackSession(T(startDay, sh, sm), T(endDay, eh, em), activity, batteryStart, batteryEnd, workArea, null, null);
        }
        static (int, int) Parse(string hhmm)
        {
            var parts = hhmm.Split(':');
            return (int.Parse(parts[0]), int.Parse(parts[1]));
        }

        return
        [
            // 2026-07-21
            S(21, "21:42", 22, "07:23", "PARKED_IN_CS", 100, 100),

            // 2026-07-22
            S(22, "07:23", 22, "07:24", "LEAVING", 71, 71, "oversiden"),
            S(22, "07:24", 22, "08:30", "MOWING", 71, 31, "oversiden"),
            S(22, "08:30", 22, "08:33", "GOING_HOME", 31, 31, "oversiden"),
            S(22, "08:33", 22, "08:34", "NOT_APPLICABLE", 30, 30, "oversiden"),
            S(22, "08:34", 22, "09:14", "CHARGING", 35, 35, "oversiden"),
            S(22, "09:14", 22, "09:16", "LEAVING", 100, 100, "oversiden"),
            S(22, "09:16", 22, "10:30", "MOWING", 100, 50, "oversiden"),
            S(22, "10:30", 22, "10:33", "GOING_HOME", 50, 50, "oversiden"),
            S(22, "10:33", 22, "12:00", "PARKED_IN_CS", 47, 100),
            S(22, "12:00", 22, "12:01", "NOT_APPLICABLE", 100, 100),
            S(22, "12:01", 22, "12:10", "LEAVING", 100, 95, "oversiden"),
            S(22, "12:10", 22, "12:34", "MOWING", 95, 70, "oversiden"),
            S(22, "12:34", 22, "12:36", "GOING_HOME", 70, 70, "oversiden"),
            S(22, "12:36", 22, "12:38", "GOING_HOME", 70, 67),
            S(22, "12:38", 22, "13:34", "PARKED_IN_CS", 77, 77),
            S(22, "13:34", 22, "13:43", "LEAVING", 95, 95, "oversiden"),
            S(22, "13:43", 22, "13:56", "MOWING", 95, 80, "oversiden"),
            S(22, "13:56", 22, "13:59", "GOING_HOME", 80, 80, "oversiden"),
            S(22, "13:59", 22, "14:00", "GOING_HOME", 77, 77),
            S(22, "14:00", 23, "06:57", "PARKED_IN_CS", 87, 100),

            // 2026-07-23
            S(23, "06:57", 23, "06:59", "LEAVING", 77, 72, "oversiden"),
            S(23, "06:59", 23, "08:09", "MOWING", 72, 32, "oversiden"),
            S(23, "08:09", 23, "08:12", "GOING_HOME", 32, 32, "oversiden"),
            S(23, "08:12", 23, "08:57", "CHARGING", 31, 31, "oversiden"),
            S(23, "08:57", 23, "10:35", "MOWING", 100, 35, "oversiden"),
            S(23, "10:35", 23, "10:39", "GOING_HOME", 30, 30, "oversiden"),
            S(23, "10:39", 23, "11:21", "CHARGING", 34, 34, "oversiden"),
            S(23, "11:21", 23, "11:24", "LEAVING", 100, 100, "oversiden"),
            S(23, "11:24", 23, "11:31", "MOWING", 100, 95, "oversiden"), // was still ongoing (+7m) when captured
        ];
    }

    [Test]
    public void ReturnsOneEntryPerDayNewestFirst()
    {
        var days = TrackingService.AggregateDailyActivity(RealSessionHistory());

        Assert.That(days.Select(d => d.Date), Is.EqualTo([
            new DateOnly(2026, 7, 23),
            new DateOnly(2026, 7, 22),
            new DateOnly(2026, 7, 21)
        ]));
    }

    [Test]
    public void SumsMowingAndChargingIndependentlyOfTheSutForEachDay()
    {
        var sessions = RealSessionHistory();
        var days = TrackingService.AggregateDailyActivity(sessions);

        foreach (var day in days)
        {
            var daySessions = sessions.Where(s => DateOnly.FromDateTime(s.Start.Date) == day.Date).ToList();

            var expectedCharging = daySessions
                .Where(s => TrackingService.IsAtCharger(s.Activity))
                .Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.End!.Value - s.Start));
            Assert.That(day.Charging, Is.EqualTo(expectedCharging), $"Charging total wrong for {day.Date}");

            // "(none)" stands in for a null WorkAreaName, only so it can be a
            // non-null dictionary key - not a real work area name.
            var expectedMowingByArea = daySessions
                .Where(s => s.Activity == "MOWING")
                .GroupBy(s => s.WorkAreaName ?? "(none)")
                .ToDictionary(g => g.Key, g => g.Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.End!.Value - s.Start)));

            Assert.That(day.Mowing.Select(m => m.WorkAreaName ?? "(none)"), Is.EquivalentTo(expectedMowingByArea.Keys), $"Mowing areas wrong for {day.Date}");
            foreach (var m in day.Mowing)
            {
                Assert.That(m.Duration, Is.EqualTo(expectedMowingByArea[m.WorkAreaName ?? "(none)"]), $"Mowing duration wrong for {day.Date} [{m.WorkAreaName}]");
            }
        }
    }

    [Test]
    public void MowingIsAlwaysJustOversidenAndChargingIsNeverZeroOnThisHistory()
    {
        // Spot checks against the real data, independent of the LINQ
        // cross-check above: every mowing session in this history is in
        // "oversiden" (single work area across all 3 days), and every day
        // includes at least some charger time.
        var days = TrackingService.AggregateDailyActivity(RealSessionHistory());

        Assert.That(days, Has.Count.EqualTo(3));
        foreach (var day in days)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(day.Charging, Is.GreaterThan(TimeSpan.Zero), $"expected some charging time on {day.Date}");
                Assert.That(day.Mowing.Select(m => m.WorkAreaName), Is.All.EqualTo("oversiden").Or.Empty);
            }
        }

        // 2026-07-21 was charger-only (the mower didn't mow that day in this history).
        var july21 = days.Single(d => d.Date == new DateOnly(2026, 7, 21));
        Assert.That(july21.Mowing, Is.Empty);
    }

    [Test]
    public void OvernightChargingSessionCountsEntirelyTowardItsStartDay()
    {
        // The 2026-07-22 14:00 -> 2026-07-23 06:57 PARKED_IN_CS session spans
        // midnight. AggregateDailyActivity deliberately doesn't split a
        // session's duration across the boundary it crosses - the whole
        // ~16h57m counts toward 07-22, matching the same simplification
        // 'sessions' makes for its own single date column.
        var days = TrackingService.AggregateDailyActivity(RealSessionHistory());

        var july22 = days.Single(d => d.Date == new DateOnly(2026, 7, 22));
        var overnight = TimeSpan.FromHours(16) + TimeSpan.FromMinutes(57);
        Assert.That(july22.Charging, Is.GreaterThanOrEqualTo(overnight),
            "expected 2026-07-22's charging total to include the full overnight span, not a fraction split into 07-23");

        var july23 = days.Single(d => d.Date == new DateOnly(2026, 7, 23));
        var july23OwnCharging = TimeSpan.FromMinutes(45) + TimeSpan.FromMinutes(42); // the two same-day Charging sessions only
        Assert.That(july23.Charging, Is.EqualTo(july23OwnCharging),
            "2026-07-23 should only include its own two Charging sessions, not any part of the overnight stay that started the day before");
    }

    [Test]
    public void ChargingSplitsIntoChargingAndParkedWhenChargeCompleteAtIsKnown()
    {
        DateTimeOffset T(int hour, int minute) => new(2026, 7, 24, hour, minute, 0, TimeSpan.FromHours(2));

        // Reached 100% partway through - Charging should cover only up to
        // that point, Parked the remainder until it left.
        var midSession = new TrackSession(T(7, 0), T(9, 0), "CHARGING", 40, 100, null, null, null, T(8, 0));

        // Arrived already at 100% - the whole stay is Parked, none of it
        // Charging (nothing left to charge).
        var arrivedFullSession = new TrackSession(T(10, 0), T(10, 30), "PARKED_IN_CS", 100, 100, null, null, null, T(10, 0));

        // Left before ever reaching 100% - no ChargeCompleteAt, so the whole
        // stay counts as Charging, same as before this feature existed.
        var neverFullSession = new TrackSession(T(11, 0), T(11, 20), "CHARGING", 60, 90, null, null, null, null);

        var days = TrackingService.AggregateDailyActivity([midSession, arrivedFullSession, neverFullSession]);
        var day = days.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(day.Charging, Is.EqualTo(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(20)),
                    "expected 1h (mid-session, 07:00-08:00) + 20m (never-full session) of real Charging time");
            Assert.That(day.Parked, Is.EqualTo(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(30)),
                "expected 1h (mid-session, 08:00-09:00) + 30m (arrived-full session) of Parked time");
        }
    }

    [Test]
    public void SplitChargerSessionsExpandsAChargerSessionWithChargeCompleteAtIntoTwo()
    {
        DateTimeOffset T(int hour, int minute) => new(2026, 7, 24, hour, minute, 0, TimeSpan.FromHours(2));

        var mowing = new TrackSession(T(6, 0), T(7, 0), "MOWING", 90, 60, "oversiden", null, null, null);
        var chargerSession = new TrackSession(T(20, 0), T(23, 30), "PARKED_IN_CS", 53, 100, null, null, null, T(22, 0));

        var result = TrackingService.SplitChargerSessions([mowing, chargerSession]);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(mowing), "non-charger session should pass through unchanged");

        // Parked half (the more recent of the two) takes the original
        // session's position, Charging follows right after - preserves
        // newest-first order the same way the original single row did.
        var parked = result[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parked.Activity, Is.EqualTo("PARKED_IN_CS"));
            Assert.That(parked.Start, Is.EqualTo(T(22, 0)));
            Assert.That(parked.End, Is.EqualTo(chargerSession.End));
            Assert.That(parked.BatteryStart, Is.EqualTo(100));
            Assert.That(parked.BatteryEnd, Is.EqualTo(chargerSession.BatteryEnd));
        }

        var charging = result[2];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(charging.Activity, Is.EqualTo("CHARGING"));
            Assert.That(charging.Start, Is.EqualTo(chargerSession.Start));
            Assert.That(charging.End, Is.EqualTo(T(22, 0)));
            Assert.That(charging.BatteryStart, Is.EqualTo(53));
            Assert.That(charging.BatteryEnd, Is.EqualTo(100));
        }
    }

    [Test]
    public void SplitChargerSessionsLeavesSessionsWithoutAChargeCompleteAtUnchanged()
    {
        DateTimeOffset T(int hour, int minute) => new(2026, 7, 24, hour, minute, 0, TimeSpan.FromHours(2));
        var stillCharging = new TrackSession(T(9, 0), T(11, 0), "CHARGING", 40, 90, null, null, null, null);

        var result = TrackingService.SplitChargerSessions([stillCharging]);

        Assert.That(result, Is.EqualTo([stillCharging]));
    }

    [Test]
    public void SplitChargerSessionsLeavesAnArrivedAlreadyFullSessionUnchanged()
    {
        // ChargeCompleteAt == Start - nothing to split, it was already full
        // on arrival.
        DateTimeOffset T(int hour, int minute) => new(2026, 7, 24, hour, minute, 0, TimeSpan.FromHours(2));
        var arrivedFull = new TrackSession(T(10, 0), T(10, 30), "PARKED_IN_CS", 100, 100, null, null, null, T(10, 0));

        var result = TrackingService.SplitChargerSessions([arrivedFull]);

        Assert.That(result, Is.EqualTo([arrivedFull]));
    }

    [Test]
    public void AggregateMonthlyActivityCollapsesAllThreeDaysIntoOneJuly2026Row()
    {
        // The whole RealSessionHistory fixture sits within July 2026 - all
        // three of AggregateDailyActivity's days should collapse into a
        // single month row whose totals equal the sum of the three days.
        var sessions = RealSessionHistory();
        var days = TrackingService.AggregateDailyActivity(sessions);
        var months = TrackingService.AggregateMonthlyActivity(sessions);

        Assert.That(months, Has.Count.EqualTo(1));
        var month = months[0];
        Assert.That(month.Month, Is.EqualTo(new DateOnly(2026, 7, 1)));

        var expectedCharging = days.Aggregate(TimeSpan.Zero, (sum, d) => sum + d.Charging);
        var expectedParked = days.Aggregate(TimeSpan.Zero, (sum, d) => sum + d.Parked);
        Assert.That(month.Charging, Is.EqualTo(expectedCharging));
        Assert.That(month.Parked, Is.EqualTo(expectedParked));

        // "(none)" stands in for a null WorkAreaName, only so it can be a
        // non-null dictionary key - not a real work area name.
        var expectedMowing = days.SelectMany(d => d.Mowing)
            .GroupBy(m => m.WorkAreaName ?? "(none)")
            .ToDictionary(g => g.Key, g => g.Aggregate(TimeSpan.Zero, (sum, m) => sum + m.Duration));
        Assert.That(month.Mowing.Select(m => m.WorkAreaName ?? "(none)"), Is.EquivalentTo(expectedMowing.Keys));
        foreach (var m in month.Mowing)
        {
            Assert.That(m.Duration, Is.EqualTo(expectedMowing[m.WorkAreaName ?? "(none)"]), $"Mowing duration wrong for [{m.WorkAreaName}]");
        }
    }

    [Test]
    public void AggregateMonthlyActivitySplitsSessionsAcrossMonthsIntoSeparateNewestFirstRows()
    {
        DateTimeOffset T(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, TimeSpan.FromHours(2));

        var juneMowing = new TrackSession(T(6, 30, 10), T(6, 30, 11), "MOWING", 90, 60, "oversiden", null, null, null);
        var julyMowing = new TrackSession(T(7, 1, 10), T(7, 1, 11), "MOWING", 90, 60, "oversiden", null, null, null);

        var months = TrackingService.AggregateMonthlyActivity([juneMowing, julyMowing]);

        Assert.That(months.Select(m => m.Month), Is.EqualTo([
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 6, 1)
        ]), "expected newest month first, matching AggregateDailyActivity's own ordering");
        Assert.That(months, Has.All.Matches<MonthlyActivity>(m => m.Mowing.Single().Duration == TimeSpan.FromHours(1)));
    }

    [Test]
    public void BackfillChargingEndBatteryFixesTheRealAM308VGapCase()
    {
        // Reproduces the real 2026-07-27 AM308V case: a Charging session
        // logged only its arrival point (26%->26%, no interim poll caught
        // the actual charge-to value), immediately followed - no gap - by a
        // Parked session that starts at 100%. The Charging session's own
        // BatteryEnd should be corrected to 100.
        DateTimeOffset T(int hour, int minute) => new(2026, 7, 27, hour, minute, 0, TimeSpan.FromHours(2));

        var charging = new TrackSession(T(12, 37), T(14, 20), "CHARGING", 26, 26, "hovedomrade", null, null, null);
        var parked = new TrackSession(T(14, 20), null, "PARKED_IN_CS", 100, 100, null, null, null, null);

        var result = TrackingService.BackfillChargingEndBattery([charging, parked]);

        Assert.That(result[0].BatteryEnd, Is.EqualTo(100), "Charging's end battery should be backfilled from the contiguous Parked session's start");
        Assert.That(result[1], Is.EqualTo(parked), "the following session itself should be untouched");
    }

    [Test]
    public void BackfillChargingEndBatteryLeavesACleanHandoffUnchanged()
    {
        // The working case (case 2 from the same real history): the mower
        // was still reporting CHARGING right through reaching 100%, so
        // BatteryEnd is already correct - nothing to backfill.
        DateTimeOffset T(int hour, int minute) => new(2026, 7, 26, hour, minute, 0, TimeSpan.FromHours(2));

        var charging = new TrackSession(T(14, 7), T(14, 47), "CHARGING", 65, 100, null, null, null, null);
        var parked = new TrackSession(T(14, 47), null, "PARKED_IN_CS", 100, 100, null, null, null, null);

        var result = TrackingService.BackfillChargingEndBattery([charging, parked]);

        Assert.That(result, Is.EqualTo([charging, parked]));
    }

    [Test]
    public void BackfillChargingEndBatteryDoesNotBridgeAGap()
    {
        // Same batteries as the real gap case, but End != next.Start (a real
        // gap between sessions, e.g. a tracking outage) - not something this
        // can safely attribute to "still at the charger the whole time", so
        // it must be left alone.
        DateTimeOffset T(int day, int hour, int minute) => new(2026, 7, day, hour, minute, 0, TimeSpan.FromHours(2));

        var charging = new TrackSession(T(27, 12, 37), T(27, 14, 20), "CHARGING", 26, 26, null, null, null, null);
        var parkedLater = new TrackSession(T(27, 15, 0), null, "PARKED_IN_CS", 100, 100, null, null, null, null);

        var result = TrackingService.BackfillChargingEndBattery([charging, parkedLater]);

        Assert.That(result[0].BatteryEnd, Is.EqualTo(26), "no contiguous boundary to justify backfilling across a real gap");
    }

    [Test]
    public void BackfillChargingEndBatteryIgnoresNonChargerNeighbors()
    {
        // The next session isn't at-charger at all (e.g. it left again
        // immediately) - nothing to backfill from.
        DateTimeOffset T(int hour, int minute) => new(2026, 7, 27, hour, minute, 0, TimeSpan.FromHours(2));

        var charging = new TrackSession(T(12, 37), T(14, 20), "CHARGING", 26, 26, null, null, null, null);
        var leaving = new TrackSession(T(14, 20), T(14, 22), "LEAVING", 26, 26, null, null, null, null);

        var result = TrackingService.BackfillChargingEndBattery([charging, leaving]);

        Assert.That(result[0].BatteryEnd, Is.EqualTo(26));
    }

    [Test]
    public void SplitChargerSessionsSplitsAStillOngoingSessionUsingNowAsTheImplicitEnd()
    {
        // A real currently-ongoing stay (End is null) that reached 100% a
        // while ago should still split - the Parked half just keeps growing
        // (End null) rather than being frozen at whatever moment this ran.
        var completedAt = DateTimeOffset.Now.AddHours(-2);
        var ongoing = new TrackSession(DateTimeOffset.Now.AddHours(-5), null, "PARKED_IN_CS", 60, 100, null, null, null, completedAt);

        var result = TrackingService.SplitChargerSessions([ongoing]);

        Assert.That(result, Has.Count.EqualTo(2));
        var parked = result[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parked.Activity, Is.EqualTo("PARKED_IN_CS"));
            Assert.That(parked.Start, Is.EqualTo(completedAt));
            Assert.That(parked.End, Is.Null, "the still-ongoing half must stay open-ended, not frozen at 'now'");
        }

        var charging = result[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(charging.Activity, Is.EqualTo("CHARGING"));
            Assert.That(charging.Start, Is.EqualTo(ongoing.Start));
            Assert.That(charging.End, Is.EqualTo(completedAt));
        }
    }
}
