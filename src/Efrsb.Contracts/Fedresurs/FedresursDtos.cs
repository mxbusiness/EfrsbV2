using System.Text.Json.Serialization;

namespace Efrsb.Contracts.Fedresurs;

public sealed record FedresursAuthRequest(string Login, string Password);

public sealed record FedresursAuthResponse([property: JsonPropertyName("jwt")] string Jwt);

public sealed class FedresursPagedResponse<T>
{
    public int Total { get; set; }
    public List<T> PageData { get; set; } = new();
}



// Эти DTO больше не используются рабочей логикой. Они оставлены только как временная
// совместимость, чтобы сборка не цеплялась за старые obj/bin после удаления публичного
// backend fedresurs.ru. HTTP-запросы к публичному backend не выполняются.
public sealed class FedresursPublicCompanyResponse
{
    public int Found { get; set; }
    public int Total { get; set; }
    public List<FedresursPublicCompanyItem> PageData { get; set; } = new();

    [JsonIgnore]
    public int Count => Found > 0 ? Found : Total;
}

public sealed class FedresursPublicCompanyItem
{
    public string Guid { get; set; } = string.Empty;
    public string? Ogrn { get; set; }
    public string? Inn { get; set; }
    public string? Name { get; set; }
    public string? EgrulAddress { get; set; }
    public string? Status { get; set; }
}

public sealed class FedresursPublicPublicationResponse
{
    public int Found { get; set; }
    public int Total { get; set; }
    public List<FedresursPublicPublicationItem> PageData { get; set; } = new();

    [JsonIgnore]
    public int Count => Found > 0 ? Found : Total;
}

public sealed class FedresursPublicPublicationItem
{
    public string Guid { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public DateTime DatePublish { get; set; }
    public string? Title { get; set; }
    public string? Type { get; set; }
    public bool? IsAnnuled { get; set; }
    public bool? IsAnnulledRaw { get; set; }
    public bool? IsLockedRaw { get; set; }
    public bool? IsRefuted { get; set; }

    [JsonIgnore]
    public bool IsAnnulled => IsAnnuled == true || IsAnnulledRaw == true || IsRefuted == true;

    [JsonIgnore]
    public bool IsLocked => IsLockedRaw == true;
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

public sealed class FedresursBankruptItem
{
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("dateLastModif")]
    public DateTime? DateLastModif { get; set; }

    // REST API ЕФРСБ возвращает идентификационные данные должника во вложенном объекте data.
    [JsonPropertyName("data")]
    public FedresursBankruptData? Data { get; set; }

    [JsonPropertyName("name")]
    public string? RawName { get; set; }

    [JsonPropertyName("inn")]
    public string? RawInn { get; set; }

    [JsonPropertyName("ogrn")]
    public string? RawOgrn { get; set; }

    [JsonIgnore]
    public string? Name => FirstNotEmpty(Data?.Name, Data?.ShortName, Data?.FullName, RawName, BuildPersonName());

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

    [JsonPropertyName("shortName")]
    public string? ShortName { get; set; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

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
