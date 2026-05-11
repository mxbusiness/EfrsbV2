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

    public FedresursClient(HttpClient httpClient, IOptions<FedresursOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<FedresursPagedResponse<FedresursBankruptItem>> SearchBankruptsAsync(string query, CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);
        var url = $"v1/bankrupts?searchString={Uri.EscapeDataString(query)}&limit=20&offset=0";
        return await GetJsonAsync<FedresursPagedResponse<FedresursBankruptItem>>(url, cancellationToken) ?? new();
    }

    public async Task<FedresursPagedResponse<FedresursMessageItem>> GetMessagesAsync(string? bankruptGuid, DateTime dateFrom, DateTime dateTo, int limit = 1000, int offset = 0, CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Clamp(offset, 0, 1_000_000);
        if ((dateTo - dateFrom).TotalDays > 31) dateFrom = dateTo.AddDays(-31);

        var parts = new List<string>
        {
            $"datePublishBegin={Uri.EscapeDataString("gte:" + dateFrom.ToString("yyyy-MM-ddTHH:mm:ss"))}",
            $"datePublishEnd={Uri.EscapeDataString("lte:" + dateTo.ToString("yyyy-MM-ddTHH:mm:ss"))}",
            "includeContent=true",
            "includeBankruptInfo=true",
            "isLocked=false",
            $"limit={limit}",
            $"offset={offset}",
            "sort=DatePublish:desc"
        };
        if (!string.IsNullOrWhiteSpace(bankruptGuid))
            parts.Add($"bankruptGUID={Uri.EscapeDataString(bankruptGuid)}");

        return await GetJsonAsync<FedresursPagedResponse<FedresursMessageItem>>("v1/messages?" + string.Join('&', parts), cancellationToken) ?? new();
    }

    public async Task<FedresursMessageItem?> GetMessageAsync(string guid, CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);
        return await GetJsonAsync<FedresursMessageItem>($"v1/messages/{Uri.EscapeDataString(guid)}", cancellationToken);
    }

    public async Task<byte[]> DownloadMessageFilesArchiveAsync(string guid, bool onlySafe = true, CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(cancellationToken);
        var response = await _httpClient.GetAsync($"v1/messages/{Uri.EscapeDataString(guid)}/files/archive?onlySafe={onlySafe.ToString().ToLowerInvariant()}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return Array.Empty<byte>();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_jwt) && DateTime.UtcNow < _jwtExpiresAtUtc)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
            return;
        }

        var response = await _httpClient.PostAsJsonAsync("v1/auth", new { login = _options.Login, password = _options.Password }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<FedresursAuthResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Fedresurs auth returned empty response.");
        _jwt = auth.Jwt;
        _jwtExpiresAtUtc = DateTime.UtcNow.AddHours(7).AddMinutes(45);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }
}
