using System.Text.Json.Serialization;

namespace Efrsb.Contracts.Fedresurs;

public sealed record FedresursAuthRequest(string Login, string Password);
public sealed record FedresursAuthResponse([property: JsonPropertyName("jwt")] string Jwt);

public sealed class FedresursPagedResponse<T>
{
    public int Total { get; set; }
    public List<T> PageData { get; set; } = new();
}

public sealed class FedresursMessageItem
{
    public string Guid { get; set; } = string.Empty;
    public string? BankruptGuid { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime DatePublish { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? CourtDecisionType { get; set; }
    public string? Content { get; set; }
    public string? LockReason { get; set; }
    public bool? HasViolation { get; set; }
    public object? BankruptInfo { get; set; }
}

public sealed class FedresursBankruptItem
{
    public string Guid { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Inn { get; set; }
    public string? Ogrn { get; set; }
}
