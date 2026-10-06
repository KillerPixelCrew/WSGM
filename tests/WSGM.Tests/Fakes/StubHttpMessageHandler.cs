namespace WSGM.Tests.Fakes;

/// <summary>Answers HTTP requests from a fixture and records the last user agent.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    internal string? LastUserAgent { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUserAgent = request.Headers.UserAgent.ToString();
        return Task.FromResult(answer(request));
    }
}
