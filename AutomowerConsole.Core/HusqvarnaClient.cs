using System.Net.Http.Headers;
using System.Text.Json;

namespace AutomowerConsole.Core;

internal class HusqvarnaClient(HttpClient http, string appKey, string appSecret)
{
    private const string TokenUrl = "https://api.authentication.husqvarnagroup.dev/v1/oauth2/token";
    private const string ApiBaseUrl = "https://api.amc.husqvarna.dev/v1";

    private string? _accessToken;

    public async Task AuthenticateAsync()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = appKey,
            ["client_secret"] = appSecret,
        });

        var response = await http.PostAsync(TokenUrl, form);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Token request failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(body)
                    ?? throw new InvalidOperationException("Failed to parse token response");

        _accessToken = token.AccessToken;
    }

    public async Task<MowerData[]> GetMowersAsync()
    {
        var body = await SendAsync(HttpMethod.Get, $"{ApiBaseUrl}/mowers");
        var result = JsonSerializer.Deserialize<MowersResponse>(body)
                     ?? throw new InvalidOperationException("Failed to parse mowers response");
        return result.Data;
    }

    public async Task<MowerData> GetMowerAsync(string mowerId)
    {
        var body = await GetMowerRawAsync(mowerId);
        var result = JsonSerializer.Deserialize<MowerResponse>(body)
                     ?? throw new InvalidOperationException("Failed to parse mower response");
        return result.Data;
    }

    public Task<string> GetMowerRawAsync(string mowerId)
        => SendAsync(HttpMethod.Get, $"{ApiBaseUrl}/mowers/{mowerId}");

    public async Task<WorkArea> GetWorkAreaAsync(string mowerId, long workAreaId)
    {
        var body = await SendAsync(HttpMethod.Get, $"{ApiBaseUrl}/mowers/{mowerId}/workAreas/{workAreaId}");
        var result = JsonSerializer.Deserialize<WorkAreaResponse>(body)
                     ?? throw new InvalidOperationException("Failed to parse work area response");
        return result.Data.Attributes;
    }

    public async Task<MessageItem[]> GetMessagesAsync(string mowerId)
    {
        var body = await SendAsync(HttpMethod.Get, $"{ApiBaseUrl}/mowers/{mowerId}/messages");
        var result = JsonSerializer.Deserialize<MessagesResponse>(body)
                     ?? throw new InvalidOperationException("Failed to parse messages response");
        return result.Data.Attributes.Messages;
    }

    private async Task<string> SendAsync(HttpMethod method, string url)
    {
        if (_accessToken is null)
        {
            throw new InvalidOperationException("Not authenticated. Call AuthenticateAsync first.");
        }

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Add("Authorization-Provider", "husqvarna");
        request.Headers.Add("X-Api-Key", appKey);

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Request to {url} failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
        }

        return body;
    }
}