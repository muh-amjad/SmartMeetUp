namespace MeetUp.Api.Services.Ai;

/// <summary>
/// Shared response check for the AI HTTP clients. EnsureSuccessStatusCode throws away the response
/// body, and the body is where every provider explains what went wrong ("model not found", "quota
/// exceeded", "API key not valid"). Without it a failed analysis logs only "404 (Not Found)".
/// </summary>
internal static class AiHttp
{
    private const int MaxBodyChars = 600;

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            body = "(response body could not be read)";
        }

        if (body.Length > MaxBodyChars)
        {
            body = body[..MaxBodyChars] + "...";
        }

        throw new HttpRequestException(
            $"{provider} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
