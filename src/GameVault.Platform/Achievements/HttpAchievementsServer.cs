using System.Net.Http.Headers;

namespace GameVault.Platform.Achievements;

/// <summary>Sends rcheevos' requests to RetroAchievements over HTTPS.</summary>
public sealed class HttpAchievementsServer(HttpClient http) : IAchievementsServer
{
    public async Task<AchievementsServerResponse> PostAsync(string url, string? postData, string? contentType)
    {
        try
        {
            using var request = new HttpRequestMessage(postData is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (postData is not null)
            {
                request.Content = new StringContent(postData);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/x-www-form-urlencoded");
            }

            using var response = await http.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return new AchievementsServerResponse((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // No connection: rcheevos keeps the request and retries.
            return new AchievementsServerResponse(RcheevosNative.ServerResponseRetryableClientError, []);
        }
    }
}
