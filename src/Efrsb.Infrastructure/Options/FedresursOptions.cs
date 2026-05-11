namespace Efrsb.Infrastructure.Options;

public sealed class FedresursOptions
{
    public string BaseUrl { get; set; } = "https://bank-publications-prod.fedresurs.ru";
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
