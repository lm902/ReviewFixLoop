using ReviewFixLoop;
using Xunit;

namespace ReviewFixLoop.Tests;

public class GhRetryTests
{
    private static GhResult Fail(string stderr, string stdout = "") => new(1, stdout, stderr);

    [Fact]
    public void SuccessIsNotAFailure() =>
        Assert.Equal(GhFailure.None, GhRetry.Classify(new GhResult(0, "{}", "")));

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    public void ServerErrorsAreTransient(int status) =>
        Assert.Equal(GhFailure.Transient, GhRetry.Classify(Fail($"gh: Server Error (HTTP {status})")));

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(422)]
    public void ClientErrorsAreFatal(int status) =>
        Assert.Equal(GhFailure.Fatal, GhRetry.Classify(Fail($"gh: Not Found (HTTP {status})")));

    [Fact]
    public void TooManyRequestsIsRateLimited() =>
        Assert.Equal(GhFailure.RateLimited, GhRetry.Classify(Fail("gh: Too Many Requests (HTTP 429)")));

    [Fact]
    public void ForbiddenIsRateLimitedOnlyWhenTheMessageSaysSo()
    {
        Assert.Equal(GhFailure.RateLimited,
            GhRetry.Classify(Fail("gh: API rate limit exceeded (HTTP 403)")));
        Assert.Equal(GhFailure.RateLimited,
            GhRetry.Classify(Fail("gh: You have exceeded a secondary rate limit (HTTP 403)")));
        Assert.Equal(GhFailure.Fatal,
            GhRetry.Classify(Fail("gh: Resource not accessible by integration (HTTP 403)")));
    }

    [Theory]
    [InlineData("dial tcp: lookup api.github.com: no such host")]
    [InlineData("read tcp 10.0.0.1:443: connection reset by peer")]
    [InlineData("Post \"https://api.github.com\": net/http: TLS handshake timeout")]
    public void NetworkErrorsWithoutAStatusAreTransient(string stderr) =>
        Assert.Equal(GhFailure.Transient, GhRetry.Classify(Fail(stderr)));

    [Fact]
    public void UnrecognizedErrorWithoutAStatusIsFatal() =>
        Assert.Equal(GhFailure.Fatal, GhRetry.Classify(Fail("gh: unknown command \"nope\"")));

    [Fact]
    public void ProcessStartFailureIsFatal() =>
        Assert.Equal(GhFailure.Fatal, GhRetry.Classify(new GhResult(-1, "", "file not found")));

    [Fact]
    public void StatusIsReadFromTheJsonBodyWhenStderrIsEmpty() =>
        Assert.Equal(GhFailure.Transient, GhRetry.Classify(Fail("", """{"message":"Bad gateway","status":"502"}""")));

    [Fact]
    public void BackoffGrowsThenPlateaus()
    {
        var delays = Enumerable.Range(1, 6).Select(GhRetry.BackoffFor).ToList();
        Assert.Equal(delays.OrderBy(d => d), delays);
        Assert.Equal(delays[^1], delays[^2]);
    }

    [Fact]
    public void RateLimitDelayCountsToTheResetPlusMargin()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddSeconds(30).ToUnixTimeSeconds();
        var delay = GhRetry.RateLimitDelay(reset, now, TimeSpan.FromMinutes(15));

        Assert.InRange(delay, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(34));
    }

    [Fact]
    public void RateLimitDelayIsClampedToTheCap()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(3).ToUnixTimeSeconds();

        Assert.Equal(TimeSpan.FromMinutes(15), GhRetry.RateLimitDelay(reset, now, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void PastResetTimeMeansNoWait()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddMinutes(-10).ToUnixTimeSeconds();

        Assert.Equal(TimeSpan.Zero, GhRetry.RateLimitDelay(reset, now, TimeSpan.FromMinutes(15)));
    }
}
