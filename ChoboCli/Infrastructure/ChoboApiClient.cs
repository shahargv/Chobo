using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Chobo.Contracts;

namespace ChoboCli.Infrastructure;

public sealed class ChoboApiClient : IDisposable
{
    private readonly HttpClient _client;

    public ChoboApiClient(string serverUrl, string? accessToken = null)
    {
        _client = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    public async Task EnsureCompatibleServerAsync()
    {
        var version = await _client.GetFromJsonAsync<ServerVersionDto>(Api("server/version"), JsonOutputWriter.JsonOptions);
        if (version is null || version.ApiVersion != ChoboApi.ApiVersion)
        {
            throw new InvalidOperationException($"CLI supports API v{ChoboApi.ApiVersion}, but server returned API v{version?.ApiVersion.ToString() ?? "unknown"}.");
        }
    }


    public async Task<InstallResponse> InstallAsync(InstallRequest request)
    {
        using var response = await _client.PostAsJsonAsync(Api("server/install"), request, JsonOutputWriter.JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase : text);
        }

        return JsonSerializer.Deserialize<InstallResponse>(text, JsonOutputWriter.JsonOptions)
            ?? throw new InvalidOperationException("Server returned an empty install response.");
    }

    public Task<object?> GetAsync(string relativePath) =>
        ReadResponseAsync(_client.GetAsync(Api(relativePath)));

    public async Task<T?> GetAsync<T>(string relativePath)
    {
        using var response = await _client.GetAsync(Api(relativePath));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase : text);
        }

        return string.IsNullOrWhiteSpace(text)
            ? default
            : JsonSerializer.Deserialize<T>(text, JsonOutputWriter.JsonOptions);
    }

    public async Task<T?> GetOptionalAsync<T>(string relativePath)
    {
        using var response = await _client.GetAsync(Api(relativePath));
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase : text);
        }

        return string.IsNullOrWhiteSpace(text)
            ? default
            : JsonSerializer.Deserialize<T>(text, JsonOutputWriter.JsonOptions);
    }

    public Task<object?> PostAsync(string relativePath, object body) =>
        ReadResponseAsync(_client.PostAsJsonAsync(Api(relativePath), body, JsonOutputWriter.JsonOptions));

    public Task<object?> PutAsync(string relativePath, object body) =>
        ReadResponseAsync(_client.PutAsJsonAsync(Api(relativePath), body, JsonOutputWriter.JsonOptions));

    public Task<object?> DeleteAsync(string relativePath) =>
        ReadResponseAsync(_client.DeleteAsync(Api(relativePath)));

    private static string Api(string relativePath) =>
        $"{ChoboApi.ApiPrefix}/{relativePath}";

    private static async Task<object?> ReadResponseAsync(Task<HttpResponseMessage> responseTask)
    {
        using var response = await responseTask;
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase : text);
        }

        return string.IsNullOrWhiteSpace(text)
            ? null
            : JsonSerializer.Deserialize<object>(text, JsonOutputWriter.JsonOptions);
    }

    public void Dispose() => _client.Dispose();
}
