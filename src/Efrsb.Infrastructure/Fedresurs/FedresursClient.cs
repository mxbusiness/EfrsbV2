using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Efrsb.Application.Abstractions;
using Efrsb.Contracts.Fedresurs;
using Efrsb.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Efrsb.Infrastructure.Fedresurs;

public sealed class FedresursClient : IFedresursClient
{
    private readonly HttpClient _httpClient;
    private readonly FedresursOptions _options;
    private string? _jwt;
    private DateTime _jwtExpiresAtUtc;

    public FedresursClient(
        HttpClient httpClient,
        IOptions<FedresursOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<FedresursPagedResponse<FedresursBankruptItem>> SearchBankruptsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        var normalized = query.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return new FedresursPagedResponse<FedresursBankruptItem>();

        var parts = new List<string>
        {
            "limit=20",
            "offset=0"
        };

        if (normalized.Length == 10 && normalized.All(char.IsDigit))
        {
            parts.Add("type=Company");
            parts.Add($"inn={Uri.EscapeDataString(normalized)}");
        }
        else if (normalized.Length == 13 && normalized.All(char.IsDigit))
        {
            parts.Add("type=Company");
            parts.Add($"ogrn={Uri.EscapeDataString(normalized)}");
        }
        else if (Guid.TryParse(normalized, out _))
        {
            // Карточка в этом сервисе предназначена для юридических лиц.
            // Указываем type=Company и при поиске по GUID, чтобы ЕФРСБ вернул data компании.
            parts.Add("type=Company");
            parts.Add($"guid={Uri.EscapeDataString(normalized)}");
        }
        else
        {
            parts.Add("type=Company");
            parts.Add($"name={Uri.EscapeDataString(normalized)}");
        }

        return await GetJsonAsync<FedresursPagedResponse<FedresursBankruptItem>>(
            "v1/bankrupts?" + string.Join('&', parts),
            cancellationToken) ?? new FedresursPagedResponse<FedresursBankruptItem>();
    }

    public async Task<FedresursPagedResponse<FedresursMessageItem>> GetMessagesAsync(
        string? bankruptGuid,
        DateTime dateFrom,
        DateTime dateTo,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Clamp(offset, 0, 1_000_000);

        if ((dateTo - dateFrom).TotalDays > 31)
            dateFrom = dateTo.AddDays(-31);

        var parts = new List<string>
        {
            $"datePublishBegin={Uri.EscapeDataString("gte:" + dateFrom.ToString("yyyy-MM-ddTHH:mm:ss"))}",
            $"datePublishEnd={Uri.EscapeDataString("lte:" + dateTo.ToString("yyyy-MM-ddTHH:mm:ss"))}",
            "includeContent=true",
            "includeBankruptInfo=true",
            $"limit={limit}",
            $"offset={offset}",
            "sort=DatePublish:desc"
        };

        if (!string.IsNullOrWhiteSpace(bankruptGuid))
            parts.Add($"bankruptGUID={Uri.EscapeDataString(bankruptGuid)}");

        return await GetJsonAsync<FedresursPagedResponse<FedresursMessageItem>>(
            "v1/messages?" + string.Join('&', parts),
            cancellationToken) ?? new FedresursPagedResponse<FedresursMessageItem>();
    }

    public async Task<FedresursPagedResponse<FedresursMessageItem>> GetMessagesMetadataAsync(
        string bankruptGuid,
        string sort,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        var parts = new List<string>
        {
            $"bankruptGUID={Uri.EscapeDataString(bankruptGuid)}",
            "includeContent=false",
            "includeBankruptInfo=false",
            "limit=1",
            "offset=0",
            $"sort={Uri.EscapeDataString(sort)}"
        };

        return await GetJsonAsync<FedresursPagedResponse<FedresursMessageItem>>(
            "v1/messages?" + string.Join('&', parts),
            cancellationToken) ?? new FedresursPagedResponse<FedresursMessageItem>();
    }

    public async Task<FedresursMessageItem?> GetMessageAsync(
        string guid,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        var response = await _httpClient.GetAsync(
            $"v1/messages/{Uri.EscapeDataString(guid)}",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<FedresursMessageItem>(
            cancellationToken: cancellationToken);
    }

    public async Task<byte[]> DownloadMessageFilesArchiveAsync(
        string guid,
        bool onlySafe = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        var response = await _httpClient.GetAsync(
            $"v1/messages/{Uri.EscapeDataString(guid)}/files/archive?onlySafe={onlySafe.ToString().ToLowerInvariant()}",
            cancellationToken);

        if (IsIgnorableFileArchiveStatus(response.StatusCode))
            return Array.Empty<byte>();

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<FedresursPagedResponse<FedresursReportItem>> GetReportsAsync(
        string? bankruptGuid,
        DateTime dateFrom,
        DateTime dateTo,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Clamp(offset, 0, 1_000_000);

        if ((dateTo - dateFrom).TotalDays > 31)
            dateFrom = dateTo.AddDays(-31);

        var parts = new List<string>
        {
            $"datePublishBegin={Uri.EscapeDataString("gte:" + dateFrom.ToString("yyyy-MM-ddTHH:mm:ss"))}",
            $"datePublishEnd={Uri.EscapeDataString("lte:" + dateTo.ToString("yyyy-MM-ddTHH:mm:ss"))}",
            "includeContent=true",
            "includeBankruptInfo=true",
            $"limit={limit}",
            $"offset={offset}",
            "sort=DatePublish:desc"
        };

        if (!string.IsNullOrWhiteSpace(bankruptGuid))
            parts.Add($"bankruptGUID={Uri.EscapeDataString(bankruptGuid)}");

        return await GetJsonAsync<FedresursPagedResponse<FedresursReportItem>>(
            "v1/reports?" + string.Join('&', parts),
            cancellationToken) ?? new FedresursPagedResponse<FedresursReportItem>();
    }

    public async Task<FedresursPagedResponse<FedresursReportItem>> GetReportsMetadataAsync(
        string bankruptGuid,
        string sort,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        var parts = new List<string>
        {
            $"bankruptGUID={Uri.EscapeDataString(bankruptGuid)}",
            "includeContent=false",
            "includeBankruptInfo=false",
            "limit=1",
            "offset=0",
            $"sort={Uri.EscapeDataString(sort)}"
        };

        return await GetJsonAsync<FedresursPagedResponse<FedresursReportItem>>(
            "v1/reports?" + string.Join('&', parts),
            cancellationToken) ?? new FedresursPagedResponse<FedresursReportItem>();
    }

    public async Task<FedresursReportItem?> GetReportAsync(
        string guid,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        var response = await _httpClient.GetAsync(
            $"v1/reports/{Uri.EscapeDataString(guid)}",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<FedresursReportItem>(
            cancellationToken: cancellationToken);
    }

    public async Task<byte[]> DownloadReportFilesArchiveAsync(
        string guid,
        bool onlySafe = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        var response = await _httpClient.GetAsync(
            $"v1/reports/{Uri.EscapeDataString(guid)}/files/archive?onlySafe={onlySafe.ToString().ToLowerInvariant()}",
            cancellationToken);

        if (IsIgnorableFileArchiveStatus(response.StatusCode))
            return Array.Empty<byte>();

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }


    public async Task<FedresursPublicCompanyItem?> FindPublicCompanyAsync(
        string? inn,
        string? ogrn,
        string? name,
        CancellationToken cancellationToken = default)
    {
        // Для одной организации публичный поиск fedresurs.ru может вернуть несколько карточек.
        // Нельзя брать первый результат: в логах это давало карточки с 36/37 публикациями
        // вместо основной карточки с полным списком. Поэтому пробуем ОГРН первым и
        // среди точных совпадений выбираем карточку с самым большим числом публикаций.
        var probes = new[] { ogrn, inn, name }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allCandidates = new List<FedresursPublicCompanyItem>();
        var knownGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var probe in probes)
        {
            var url = "https://fedresurs.ru/backend/companies?limit=15&offset=0&code=" + Uri.EscapeDataString(probe);
            var page = await GetPublicJsonAsync<FedresursPublicCompanyResponse>(
                url,
                "https://fedresurs.ru/search/entity?code=" + Uri.EscapeDataString(probe),
                cancellationToken) ?? new FedresursPublicCompanyResponse();

            foreach (var item in page.PageData.Where(x => !string.IsNullOrWhiteSpace(x.Guid)))
            {
                if (knownGuids.Add(item.Guid))
                    allCandidates.Add(item);
            }

        }

        return await SelectBestPublicCompanyAsync(
            allCandidates,
            inn,
            ogrn,
            name,
            cancellationToken);
    }

    public async Task<FedresursPublicPublicationResponse> GetPublicCompanyPublicationsAsync(
        string publicCompanyGuid,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        int limit = 500,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicCompanyGuid))
            return new FedresursPublicPublicationResponse();

        // Для этого endpoint на части IP даже limit=20 получает 451.
        // Limit=1 при тех же фильтрах стабильно работает, поэтому грузим публикации по одной.
        limit = 1;
        offset = Math.Max(0, offset);

        var parts = new List<string>
        {
            $"limit={limit}",
            $"offset={offset}",
            "searchCompanyEfrsb=true",
            "searchAmReport=true",
            "searchFirmBankruptMessage=true",
            "searchFirmBankruptMessageWithoutLegalCase=false",
            "searchSfactsMessage=true",
            "searchSroAmMessage=true",
            "searchTradeOrgMessage=true"
        };

        if (dateFrom.HasValue && dateTo.HasValue)
        {
            parts.Add("startDate=" + Uri.EscapeDataString(NormalizePublicDate(dateFrom.Value, false)));
            parts.Add("endDate=" + Uri.EscapeDataString(NormalizePublicDate(dateTo.Value, true)));
        }

        var url = $"https://fedresurs.ru/backend/companies/{Uri.EscapeDataString(publicCompanyGuid)}/publications?" + string.Join('&', parts);
        var referer = $"https://fedresurs.ru/companies/{Uri.EscapeDataString(publicCompanyGuid)}/publications";

        return await GetPublicJsonAsync<FedresursPublicPublicationResponse>(
            url,
            referer,
            cancellationToken) ?? new FedresursPublicPublicationResponse();
    }

    private async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_jwt) && DateTime.UtcNow < _jwtExpiresAtUtc)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
            return;
        }

        var response = await _httpClient.PostAsJsonAsync(
            "v1/auth",
            new { login = _options.Login, password = _options.Password },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<FedresursAuthResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Fedresurs auth returned empty response.");

        _jwt = auth.Jwt;
        _jwtExpiresAtUtc = DateTime.UtcNow.AddHours(7).AddMinutes(45);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
    }


    private async Task<T?> GetPublicJsonAsync<T>(
        string url,
        string referer,
        CancellationToken cancellationToken)
    {
        var previousAuthorization = _httpClient.DefaultRequestHeaders.Authorization;
        _httpClient.DefaultRequestHeaders.Authorization = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(referer);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (IsIgnorablePublicStatus(response.StatusCode))
                return default;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
        }
        finally
        {
            _httpClient.DefaultRequestHeaders.Authorization = previousAuthorization;
        }
    }


    private async Task<FedresursPublicCompanyItem?> SelectBestPublicCompanyAsync(
        IReadOnlyList<FedresursPublicCompanyItem> source,
        string? inn,
        string? ogrn,
        string? name,
        CancellationToken cancellationToken)
    {
        var candidates = source
            .Where(x => !string.IsNullOrWhiteSpace(x.Guid))
            .ToList();

        if (candidates.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(inn) && !string.IsNullOrWhiteSpace(ogrn))
        {
            var strict = candidates
                .Where(x => Same(x.Inn, inn) && Same(x.Ogrn, ogrn))
                .ToList();

            if (strict.Count > 0)
                candidates = strict;
        }
        else
        {
            var byIdentifier = candidates
                .Where(x =>
                    (!string.IsNullOrWhiteSpace(inn) && Same(x.Inn, inn)) ||
                    (!string.IsNullOrWhiteSpace(ogrn) && Same(x.Ogrn, ogrn)))
                .ToList();

            if (byIdentifier.Count > 0)
                candidates = byIdentifier;
        }

        FedresursPublicCompanyItem? best = null;
        var bestScore = int.MinValue;
        var bestCount = -1;

        foreach (var candidate in candidates)
        {
            var publicationCount = await TryGetPublicPublicationCountAsync(
                candidate.Guid,
                cancellationToken);

            var score = ScorePublicCompany(candidate, inn, ogrn, name) + Math.Min(publicationCount, 1000);

            if (score > bestScore || (score == bestScore && publicationCount > bestCount))
            {
                best = candidate;
                bestScore = score;
                bestCount = publicationCount;
            }
        }

        return best;
    }

    private async Task<int> TryGetPublicPublicationCountAsync(
        string publicCompanyGuid,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await GetPublicCompanyPublicationsAsync(
                publicCompanyGuid,
                null,
                null,
                1,
                0,
                cancellationToken);

            return page.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static int ScorePublicCompany(
        FedresursPublicCompanyItem company,
        string? inn,
        string? ogrn,
        string? name)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(inn) && Same(company.Inn, inn))
            score += 2000;

        if (!string.IsNullOrWhiteSpace(ogrn) && Same(company.Ogrn, ogrn))
            score += 3000;

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(company.Name))
        {
            var left = NormalizeSearchText(company.Name);
            var right = NormalizeSearchText(name);

            if (left == right)
                score += 500;
            else if (left.Contains(right, StringComparison.OrdinalIgnoreCase) || right.Contains(left, StringComparison.OrdinalIgnoreCase))
                score += 250;
        }

        return score;
    }

    private static bool Same(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSearchText(string value)
    {
        return value
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("ООО", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static bool IsIgnorableFileArchiveStatus(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.NotFound
            || statusCode == HttpStatusCode.Forbidden
            || statusCode == HttpStatusCode.Unauthorized
            || statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode == 451;
    }

    private static bool IsIgnorablePublicStatus(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.NotFound
            || statusCode == HttpStatusCode.Forbidden
            || statusCode == HttpStatusCode.Unauthorized
            || statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode == 451;
    }

    private static string NormalizePublicDate(DateTime value, bool endOfDay)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        if (endOfDay)
            utc = utc.Date.AddDays(1).AddMilliseconds(-1);
        else
            utc = utc.Date;

        return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    private async Task<T?> GetJsonAsync<T>(
        string url,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(
            cancellationToken: cancellationToken);
    }
}
