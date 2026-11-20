namespace DominiusPlay.Domain.Services;

/// <summary>Horário de funcionamento genérico (agentes). Mesma regra de CampaignWindow, sem depender de Campaign.</summary>
public static class BusinessHours
{
    public static HashSet<DayOfWeek> ParseDays(string days) =>
        days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => int.TryParse(d, out var n) && n is >= 0 and <= 6).Select(d => (DayOfWeek)int.Parse(d)).ToHashSet();

    public static TimeZoneInfo Zone(string? id)
    {
        foreach (var candidate in new[] { id, "America/Sao_Paulo", "E. South America Standard Time" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Retorna "now" se estiver no horário; senão o próximo início permitido (MaxValue se nenhum dia válido).</summary>
    public static DateTimeOffset NextAllowed(DateTimeOffset now, TimeOnly start, TimeOnly end, string days, TimeZoneInfo tz)
    {
        var set = ParseDays(days);
        if (set.Count == 0 || end <= start) return DateTimeOffset.MaxValue;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        for (var i = 0; i < 8; i++)
        {
            var date = DateOnly.FromDateTime(local.Date).AddDays(i);
            if (!set.Contains(date.DayOfWeek)) continue;
            var s = ToOffset(date, start, tz); var e = ToOffset(date, end, tz);
            if (now < s) return s;
            if (now < e) return now;
        }
        return DateTimeOffset.MaxValue;
    }

    private static DateTimeOffset ToOffset(DateOnly d, TimeOnly t, TimeZoneInfo tz)
    {
        var dt = d.ToDateTime(t, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, tz.GetUtcOffset(dt));
    }
}
