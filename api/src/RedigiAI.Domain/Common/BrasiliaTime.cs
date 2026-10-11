namespace RedigiAI.Domain.Common;

/// <summary>Horário de Brasília: usado só em regras de calendário e na exibição.</summary>
public static class BrasiliaTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);
}
