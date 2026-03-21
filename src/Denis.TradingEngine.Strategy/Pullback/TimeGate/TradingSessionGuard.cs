using System;

namespace Denis.TradingEngine.Strategy.Pullback.TimeGate;

/// <summary>
/// Vremenska vrata: radni dani (Belgrade), US berza (RTH, praznici, early close).
/// </summary>
public static class TradingSessionGuard
{
    private static readonly TimeZoneInfo BelgradeTz = GetBelgradeTimeZone();
    private static readonly TimeZoneInfo EasternTz = GetEasternTimeZone();

    private static TimeZoneInfo GetBelgradeTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        }
    }

    private static TimeZoneInfo GetEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    /// <summary>
    /// Belgrade radni prozor: vikend zatvoren, petak 22:00 gas, ponedeljak 14:20 paljenje.
    /// </summary>
    public static bool IsWeekendGapClosed(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, BelgradeTz);
        var day = local.DayOfWeek;
        var t = local.TimeOfDay;

        if (day == DayOfWeek.Saturday || day == DayOfWeek.Sunday)
            return false;
        if (day == DayOfWeek.Friday && t >= new TimeSpan(22, 0, 0))
            return false;
        if (day == DayOfWeek.Monday && t < new TimeSpan(14, 20, 0))
            return false;
        return true;
    }

    // ---------- US market (NYSE/NASDAQ): praznici + early close ----------

    /// <summary>
    /// Da li je datum (u Eastern) US berzanski praznik (berza zatvorena).
    /// </summary>
    public static bool IsUsMarketHoliday(DateTime dateEt)
    {
        var y = dateEt.Year;
        var m = dateEt.Month;
        var d = dateEt.Day;

        // New Year's Day (1. jan; ako padne vikend, observed u petak/ponedeljak – po praksi berza ga praznuje tog dana)
        if (m == 1 && d == 1) return true;

        // MLK Jr Day – 3. ponedeljak u januaru
        if (m == 1 && dateEt.DayOfWeek == DayOfWeek.Monday && NthWeekdayInMonth(y, 1, DayOfWeek.Monday) == d) return true;

        // Presidents Day – 3. ponedeljak u februaru
        if (m == 2 && dateEt.DayOfWeek == DayOfWeek.Monday && NthWeekdayInMonth(y, 2, DayOfWeek.Monday) == d) return true;

        // Good Friday – petak pre Uskrsa
        var goodFriday = GetGoodFriday(y);
        if (m == goodFriday.Month && d == goodFriday.Day) return true;

        // Memorial Day – poslednji ponedeljak u maju
        if (m == 5 && dateEt.DayOfWeek == DayOfWeek.Monday && LastWeekdayInMonth(y, 5, DayOfWeek.Monday) == d) return true;

        // Juneteenth – 19. jun; ako padne vikend, observed (npr. petak 18 ili ponedeljak 20)
        if (m == 6)
        {
            if (d == 19) return true;
            if (d == 18 && dateEt.DayOfWeek == DayOfWeek.Friday) return true; // 19. subota
            if (d == 20 && dateEt.DayOfWeek == DayOfWeek.Monday) return true;  // 19. nedelja
        }

        // Independence Day – 4. jul; observed 3. jul ako 4. subota, 5. jul ako 4. nedelja
        if (m == 7 && d == 4) return true;
        if (m == 7 && d == 3 && dateEt.DayOfWeek == DayOfWeek.Friday) return true;
        if (m == 7 && d == 5 && dateEt.DayOfWeek == DayOfWeek.Monday) return true;

        // Labor Day – 1. ponedeljak u septembru
        if (m == 9 && dateEt.DayOfWeek == DayOfWeek.Monday && NthWeekdayInMonth(y, 9, DayOfWeek.Monday, 1) == d) return true;

        // Thanksgiving – 4. četvrtak u novembru
        var thanksgivingDay = NthWeekdayInMonth(y, 11, DayOfWeek.Thursday, 4);
        if (m == 11 && d == thanksgivingDay) return true;

        // Christmas – 25. dec zatvoreno; 24. dec zatvoreno samo ako je 25. subota (observed)
        if (m == 12 && d == 25) return true;
        if (m == 12 && d == 24 && new DateTime(y, 12, 25).DayOfWeek == DayOfWeek.Saturday) return true;

        return false;
    }

    /// <summary>
    /// Kraj US sesije za taj dan (ET): 16:00 normalno, 13:00 na early-close danu.
    /// </summary>
    public static TimeSpan GetUsSessionEndTimeEt(DateTime dateEt)
    {
        // Early close 13:00 ET: dan posle Thanksgiving, Christmas Eve (24. dec kada je radni)
        var y = dateEt.Year;
        var m = dateEt.Month;
        var d = dateEt.Day;

        // Day after Thanksgiving (petak posle 4. četvrtka u novembru)
        var thanksgiving = NthWeekdayInMonth(y, 11, DayOfWeek.Thursday, 4);
        var dayAfterThanksgiving = new DateTime(y, 11, thanksgiving).AddDays(1);
        if (dateEt.Date == dayAfterThanksgiving.Date) return new TimeSpan(13, 0, 0);

        // Christmas Eve – 24. dec (early close kada nije vikend)
        if (m == 12 && d == 24 && dateEt.DayOfWeek != DayOfWeek.Saturday && dateEt.DayOfWeek != DayOfWeek.Sunday)
            return new TimeSpan(13, 0, 0);

        return new TimeSpan(16, 0, 0);
    }

    /// <summary>
    /// Da li je trenutak (UTC) unutar US RTH: 9:30–16:00 ET (ili 9:30–13:00 na early-close dan).
    /// Uključuje: vikend (ET) = closed, US praznik = closed, inače provera vremena.
    /// </summary>
    public static bool IsInsideUsRth(DateTime utcNow)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(utcNow, EasternTz);
        var dateEt = et.Date;

        // Vikend u ET
        if (dateEt.DayOfWeek == DayOfWeek.Saturday || dateEt.DayOfWeek == DayOfWeek.Sunday)
            return false;

        if (IsUsMarketHoliday(dateEt))
            return false;

        var sessionStartEt = new TimeSpan(9, 30, 0);
        var sessionEndEt = GetUsSessionEndTimeEt(dateEt);
        var t = et.TimeOfDay;
        return t >= sessionStartEt && t <= sessionEndEt;
    }

    private static int NthWeekdayInMonth(int year, int month, DayOfWeek dayOfWeek, int n = 3)
    {
        var first = new DateTime(year, month, 1);
        var day = (int)dayOfWeek - (int)first.DayOfWeek;
        if (day < 0) day += 7;
        var firstOccurrence = first.AddDays(day);
        var nth = firstOccurrence.AddDays(7 * (n - 1));
        return nth.Day;
    }

    private static int LastWeekdayInMonth(int year, int month, DayOfWeek dayOfWeek)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var diff = (int)last.DayOfWeek - (int)dayOfWeek;
        if (diff < 0) diff += 7;
        return last.AddDays(-diff).Day;
    }

    private static DateTime GetGoodFriday(int year)
    {
        var easter = GetEaster(year);
        return easter.AddDays(-2);
    }

    private static DateTime GetEaster(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int g = (8 * b + 13) / 25;
        int h = (19 * a + b - d - g + 15) % 30;
        int j = c / 4;
        int k = c % 4;
        int m = (a + 11 * h) / 319;
        int r = (2 * e + 2 * j - k - h + m + 32) % 7;
        int n = (h - m + r + 90) / 25;
        int p = (h - m + r + n + 19) % 32;
        return new DateTime(year, n, p);
    }
}
