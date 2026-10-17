namespace RedigiAI.Application.Notifications;

/// <summary>Endereço do site, para os botões dos e-mails.</summary>
public sealed class SiteOptions
{
    public const string Section = "App";

    /// <summary>Ex.: <c>https://redigiai.com.br</c>; em desenvolvimento, o endereço do Vite.</summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>Identificação da empresa no rodapé dos e-mails (especificação, seção 15). Campo vazio não aparece.</summary>
public sealed class CompanyOptions
{
    public const string Section = "Company";

    public string LegalName { get; set; } = "";
    public string Cnpj { get; set; } = "";
    public string Address { get; set; } = "";
    public string SupportEmail { get; set; } = "";
}
