using System.Text.Json;
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
    public string? AnnulmentMessageGuid { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime DatePublish { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? CourtDecisionType { get; set; }
    public string? Content { get; set; }
    public string? LockReason { get; set; }
    public bool? HasViolation { get; set; }
    public FedresursBankruptItem? BankruptInfo { get; set; }
}


public sealed class FedresursReportItem
{
    public string Guid { get; set; } = string.Empty;
    public string? BankruptGuid { get; set; }
    public string? AnnulmentReportGuid { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime DatePublish { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? ProcedureType { get; set; }
    public string? Content { get; set; }
    public string? LockReason { get; set; }
    public FedresursBankruptItem? BankruptInfo { get; set; }
}


public sealed class FedresursPublicCompanyResponse
{
    [JsonPropertyName("found")]
    public int Found { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("pageData")]
    public List<FedresursPublicCompanyItem> PageData { get; set; } = new();

    [JsonIgnore]
    public int Count => Found > 0 ? Found : Total;
}

public sealed class FedresursPublicCompanyItem
{
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonPropertyName("ogrn")]
    public string? Ogrn { get; set; }

    [JsonPropertyName("inn")]
    public string? Inn { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("egrulAddress")]
    public string? EgrulAddress { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class FedresursPublicPublicationResponse
{
    [JsonPropertyName("found")]
    public int Found { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("pageData")]
    public List<FedresursPublicPublicationItem> PageData { get; set; } = new();

    [JsonIgnore]
    public int Count => Found > 0 ? Found : Total;
}

public sealed class FedresursPublicPublicationItem
{
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("datePublish")]
    public DateTime DatePublish { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; set; }

    [JsonPropertyName("publisherType")]
    public string? PublisherType { get; set; }

    [JsonPropertyName("bankruptName")]
    public string? BankruptName { get; set; }

    [JsonPropertyName("participants")]
    public JsonElement? Participants { get; set; }

    [JsonPropertyName("isAnnuled")]
    public bool? IsAnnuled { get; set; }

    [JsonPropertyName("isAnnulled")]
    public bool? IsAnnulledRaw { get; set; }

    [JsonPropertyName("isLocked")]
    public bool? IsLockedRaw { get; set; }

    [JsonPropertyName("isRefuted")]
    public bool? IsRefuted { get; set; }

    [JsonIgnore]
    public bool IsAnnulled => IsAnnuled == true || IsAnnulledRaw == true || IsRefuted == true;

    [JsonIgnore]
    public bool IsLocked => IsLockedRaw == true;
}

public sealed class FedresursBankruptItem
{
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    // REST API ЕФРСБ возвращает идентификационные данные должника во вложенном объекте data.
    // Старый код ожидал flat-поля name/inn/ogrn, из-за этого после добавления компании
    // в карточке оставались пустыми ИНН, ОГРН и название.
    [JsonPropertyName("data")]
    public FedresursBankruptData? Data { get; set; }

    [JsonPropertyName("name")]
    public string? RawName { get; set; }

    [JsonPropertyName("inn")]
    public string? RawInn { get; set; }

    [JsonPropertyName("ogrn")]
    public string? RawOgrn { get; set; }

    [JsonIgnore]
    public string? Name => FirstNotEmpty(Data?.Name, RawName, BuildPersonName());

    [JsonIgnore]
    public string? Inn => FirstNotEmpty(Data?.Inn, RawInn);

    [JsonIgnore]
    public string? Ogrn => FirstNotEmpty(Data?.Ogrn, RawOgrn, Data?.Ogrnip);

    [JsonIgnore]
    public string? Address => FirstNotEmpty(Data?.Address);

    private string? BuildPersonName()
    {
        var parts = new[] { Data?.LastName, Data?.FirstName, Data?.MiddleName }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToArray();

        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static string? FirstNotEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}

public sealed class FedresursBankruptData
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ogrn")]
    public string? Ogrn { get; set; }

    [JsonPropertyName("inn")]
    public string? Inn { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("middleName")]
    public string? MiddleName { get; set; }

    [JsonPropertyName("ogrnip")]
    public string? Ogrnip { get; set; }

    [JsonPropertyName("snils")]
    public string? Snils { get; set; }

    [JsonPropertyName("birthplace")]
    public string? Birthplace { get; set; }

    [JsonPropertyName("birthdate")]
    public DateTime? Birthdate { get; set; }

    [JsonPropertyName("nameHistory")]
    public List<string>? NameHistory { get; set; }
}
