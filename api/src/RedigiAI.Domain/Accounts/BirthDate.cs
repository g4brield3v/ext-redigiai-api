using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Accounts;

public readonly record struct BirthDate
{
    public const int MinimumAge = 16;
    private const int MaximumAge = 120;

    private BirthDate(DateOnly value) => Value = value;

    public DateOnly Value { get; }

    /// <param name="value">Data informada no cadastro.</param>
    /// <param name="today">Data de hoje em Brasília.</param>
    public static BirthDate Create(DateOnly value, DateOnly today)
    {
        if (value > today || value < today.AddYears(-MaximumAge))
        {
            throw Errors.Validation("Informe uma data de nascimento válida.");
        }

        return new BirthDate(value);
    }

    public static BirthDate FromStorage(DateOnly value) => new(value);

    public int AgeOn(DateOnly today)
    {
        // Nascidos em 29/02 completam o ano em 01/03 nos anos não bissextos.
        var birthday = Value is { Month: 2, Day: 29 } && !DateTime.IsLeapYear(today.Year)
            ? new DateOnly(today.Year, 3, 1)
            : new DateOnly(today.Year, Value.Month, Value.Day);
        var age = today.Year - Value.Year;
        return birthday > today ? age - 1 : age;
    }

    /// <summary>16 ou 17 anos: compras exigem a declaração de autorização do responsável (regra 9).</summary>
    public bool IsMinorOn(DateOnly today) => AgeOn(today) < 18;

    public bool MeetsMinimumAgeOn(DateOnly today) => AgeOn(today) >= MinimumAge;
}
