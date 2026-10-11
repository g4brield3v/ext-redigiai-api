namespace RedigiAI.Application;

public sealed class LegalOptions
{
    public const string Section = "Legal";

    /// <summary>Versão vigente dos Termos de Uso e da Política de Privacidade.</summary>
    public string TermsVersion { get; set; } = "1";
}
