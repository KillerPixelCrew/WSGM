using System.Net;
using System.Net.Http.Headers;
using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     What the client does with a response it did not want. The rules are separated out because the
///     costly mistake is retrying something that will never succeed: that spends the user's rate
///     limit to be told the same thing three times, and then reports the wrong reason.
/// </summary>
public sealed class SteamGridDbRetryTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void AResponseThatCouldPlausiblySucceedIsAskedForAgain(HttpStatusCode status)
    {
        Assert.True(SteamGridDb.IsTransient(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void ARejectedRequestIsNeverRepeated(HttpStatusCode status)
    {
        // A rejected key does not become accepted by asking twice, and a missing game does not
        // appear. Repeating either only burns the rate limit the user has left.
        Assert.False(SteamGridDb.IsTransient(status));
    }

    [Fact]
    public void ARetryAfterDelayIsHonoured()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), SteamGridDb.RetryAfter(response));
    }

    [Fact]
    public void ARetryAfterDateIsHonoured()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddSeconds(4));

        var wait = SteamGridDb.RetryAfter(response);

        Assert.NotNull(wait);
        Assert.InRange(wait.Value, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ALongRetryAfterIsCappedRatherThanObeyed()
    {
        // The header is a stranger's number. Honouring an hour of it would leave the page on a
        // spinner with no way to tell it apart from a hang.
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromSeconds(10), SteamGridDb.RetryAfter(response));
    }

    [Fact]
    public void ARetryAfterAlreadyInThePastIsIgnored()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.Null(SteamGridDb.RetryAfter(response));
    }

    [Fact]
    public void AResponseWithNoRetryAfterNamesNoWait()
    {
        using HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);

        Assert.Null(SteamGridDb.RetryAfter(response));
    }
}
