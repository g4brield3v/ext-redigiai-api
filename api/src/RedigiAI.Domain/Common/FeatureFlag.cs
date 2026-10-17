namespace RedigiAI.Domain.Common;

/// <summary>Chave liga/desliga de uma parte do sistema, guardada no banco para mudar sem novo deploy.</summary>
public sealed class FeatureFlag
{
    /// <summary>Envio dos e-mails da redação. Nasce desligado: por enquanto só o Clerk manda e-mail (login).</summary>
    public const string EmailSending = "email_sending";

    private FeatureFlag() { }

    public string Key { get; private set; } = null!;
    public bool Enabled { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}

public interface IFeatureFlagRepository
{
    /// <summary>Lê a chave na hora, sem cache. Chave que não existe vale como desligada.</summary>
    Task<bool> IsEnabledAsync(string key, CancellationToken ct);
}
