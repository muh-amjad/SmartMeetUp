using MeetUp.Api.Jobs;
using System.Net;

namespace MeetUp.Api.Tests;

/// <summary>
/// The messages below are the real ones Gemini returned in production.
/// </summary>
public class AiAnalysisRetryDelayTests
{
    private static HttpRequestException Error(HttpStatusCode status, string message) =>
        new(message, inner: null, statusCode: status);

    [Fact]
    public void Waits_As_Long_As_A_Per_Minute_Limit_Asks()
    {
        var ex = Error(HttpStatusCode.TooManyRequests,
            "Gemini returned 429: Quota exceeded … limit: 5\nPlease retry in 8.473006252s.");

        // Rounded up to 9s, plus a second so the retry lands after the window.
        Assert.Equal(TimeSpan.FromSeconds(10), AiAnalysisJob.RetryDelay(ex, attempt: 1));
    }

    [Fact]
    public void Gives_Up_At_Once_When_The_Daily_Quota_Is_Spent()
    {
        var ex = Error(HttpStatusCode.TooManyRequests,
            "Gemini returned 429: Quota exceeded … limit: 20\nPlease retry in 9h12m4.859196106s.");

        // Null means "do not retry": the job moves straight on to the next provider.
        Assert.Null(AiAnalysisJob.RetryDelay(ex, attempt: 1));
    }

    [Fact]
    public void Gives_Up_When_Asked_To_Wait_Minutes()
    {
        var ex = Error(HttpStatusCode.TooManyRequests, "Please retry in 2m30s.");

        Assert.Null(AiAnalysisJob.RetryDelay(ex, attempt: 1));
    }

    [Fact]
    public void Backs_Off_When_No_Wait_Is_Given()
    {
        var ex = Error(HttpStatusCode.ServiceUnavailable,
            "Gemini returned 503: This model is currently experiencing high demand. Please try again later.");

        Assert.Equal(TimeSpan.FromSeconds(5), AiAnalysisJob.RetryDelay(ex, attempt: 1));
        Assert.Equal(TimeSpan.FromSeconds(15), AiAnalysisJob.RetryDelay(ex, attempt: 2));
    }
}
