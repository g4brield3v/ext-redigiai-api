using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Billing;

/// <summary>
/// O calendário do plano semanal, no horário de Brasília (regra 15 e especificação, seção 7): 3 correções toda
/// segunda-feira às 00h, que vencem no dia 1º seguinte às 00h.
/// </summary>
public static class PlanCalendar
{
    public const int WeeklyCredits = 3;

    /// <summary>A segunda-feira 00h da semana do instante.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset instant)
    {
        var date = BrasiliaTime.DateOf(instant);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return BrasiliaTime.StartOfDay(date.AddDays(-daysSinceMonday));
    }

    /// <summary>O dia 1º 00h do mês seguinte ao do instante: quando vencem os créditos concedidos nele.</summary>
    public static DateTimeOffset NextMonthStart(DateTimeOffset instant)
    {
        var date = BrasiliaTime.DateOf(instant);
        return BrasiliaTime.StartOfDay(new DateOnly(date.Year, date.Month, 1).AddMonths(1));
    }
}
