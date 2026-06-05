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
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(160);

    private readonly HttpClient _httpClient;
    private readonly FedresursOptions _options;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private string? _jwt;
    private DateTime _jwtExpiresAtUtc;
    private DateTime _lastRequestUtc = DateTime.MinValue;

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
        var response = await GetAsync(
            $"v1/messages/{Uri.EscapeDataString(guid)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
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
        var response = await GetAsync(
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
        var response = await GetAsync(
            $"v1/reports/{Uri.EscapeDataString(guid)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
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
        var response = await GetAsync(
            $"v1/reports/{Uri.EscapeDataString(guid)}/files/archive?onlySafe={onlySafe.ToString().ToLowerInvariant()}",
            cancellationToken);

        if (IsIgnorableFileArchiveStatus(response.StatusCode))
            return Array.Empty<byte>();

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(
        string url,
        CancellationToken cancellationToken)
    {
        await EnsureAuthorizedAsync(cancellationToken);
        await ThrottleAsync(cancellationToken);
        return await _httpClient.GetAsync(url, cancellationToken);
    }

    private async Task<T?> GetJsonAsync<T>(
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(
            cancellationToken: cancellationToken);
    }

    private async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_jwt) && DateTime.UtcNow < _jwtExpiresAtUtc)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
            return;
        }

        await ThrottleAsync(cancellationToken);

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

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            var delay = RequestInterval - elapsed;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static bool IsIgnorableFileArchiveStatus(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.NotFound
            || statusCode == HttpStatusCode.Forbidden
            || statusCode == HttpStatusCode.Unauthorized
            || statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode == 451;
    }
}
