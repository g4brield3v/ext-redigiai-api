using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RedigiAI.Domain.Accounts;

namespace RedigiAI.Infrastructure.Persistence;

/// <summary>Grava enums como texto em snake_case (<c>ComingSoon</c> → <c>coming_soon</c>).</summary>
public sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => SnakeCaseEnum<TEnum>.ToName(value),
    name => SnakeCaseEnum<TEnum>.FromName(name))
    where TEnum : struct, Enum;

public static class SnakeCaseEnum<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Names = Enum.GetValues<TEnum>()
        .ToDictionary(v => v, v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()));

    private static readonly Dictionary<string, TEnum> Values = Names.ToDictionary(p => p.Value, p => p.Key);

    public static string ToName(TEnum value) => Names[value];

    public static TEnum FromName(string name) => Values[name];
}

public sealed class BirthDateConverter() : ValueConverter<BirthDate, DateOnly>(
    value => value.Value,
    stored => BirthDate.FromStorage(stored));

/// <summary>Coluna <c>jsonb</c> para valores imutáveis do domínio.</summary>
public sealed class JsonConverter<T>() : ValueConverter<T, string>(
    value => JsonSerializer.Serialize(value, JsonColumn.Options),
    json => JsonSerializer.Deserialize<T>(json, JsonColumn.Options)!);

public sealed class JsonComparer<T>() : ValueComparer<T>(
    (a, b) => JsonColumn.Serialize(a) == JsonColumn.Serialize(b),
    value => JsonColumn.Serialize(value).GetHashCode(),
    value => value);

public static class JsonColumn
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
