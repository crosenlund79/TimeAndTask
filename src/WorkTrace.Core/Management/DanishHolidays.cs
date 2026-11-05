namespace WorkTrace.Core.Management;

public enum HolidayKind
{
    /// <summary>Official Danish public holiday (helligdag): not a working day.</summary>
    Public = 1,

    /// <summary>Commonly a day off by agreement or custom, but not an official public holiday: still planned as a working day.</summary>
    Customary = 2,
}

public sealed record Holiday(DateOnly Date, string Name, HolidayKind Kind);

/// <summary>
/// Danish public holidays (helligdagsloven), computed from the date of Easter, plus well-known customary days off.
/// Store bededag is a public holiday up to 2023; it was abolished from 2024 and is then shown as customary.
/// </summary>
public static class DanishHolidays
{
    public static Holiday? Get(DateOnly date)
    {
        var easter = EasterSunday(date.Year);
        var fromEaster = date.DayNumber - easter.DayNumber;
        Holiday Public(string name) => new(date, name, HolidayKind.Public);
        Holiday Customary(string name) => new(date, name, HolidayKind.Customary);

        return (date.Month, date.Day, fromEaster) switch
        {
            (1, 1, _) => Public("Nytårsdag"),
            (_, _, -3) => Public("Skærtorsdag"),
            (_, _, -2) => Public("Langfredag"),
            (_, _, 0) => Public("Påskedag"),
            (_, _, 1) => Public("2. påskedag"),
            (_, _, 26) when date.Year <= 2023 => Public("Store bededag"),
            (_, _, 26) => Customary("Store bededag (afskaffet 2024)"),
            (_, _, 39) => Public("Kristi himmelfartsdag"),
            (_, _, 40) => Customary("Fredag efter Kristi himmelfart"),
            (_, _, 49) => Public("Pinsedag"),
            (_, _, 50) => Public("2. pinsedag"),
            (12, 25, _) => Public("Juledag"),
            (12, 26, _) => Public("2. juledag"),
            (5, 1, _) => Customary("1. maj"),
            (6, 5, _) => Customary("Grundlovsdag"),
            (12, 24, _) => Customary("Juleaftensdag"),
            (12, 31, _) => Customary("Nytårsaftensdag"),
            _ => null,
        };
    }

    /// <summary>Easter Sunday (Gregorian calendar, anonymous algorithm).</summary>
    public static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }
}
