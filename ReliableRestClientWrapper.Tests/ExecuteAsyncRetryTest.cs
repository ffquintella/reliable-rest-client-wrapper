using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using ReliableRestClient.Exceptions;
using RestSharp;
using Xunit;

namespace ReliableRestClient.Tests
{
    /// <summary>
    /// One invariant: the injected <see cref="IAsyncPolicy"/> decides how many times a request is
    /// sent, and the wrapper sends it exactly once per policy attempt.
    ///
    /// <c>ExecuteAsync</c> used to run its own <c>while (retry)</c> loop of up to eleven attempts
    /// inside the policy, with no wait between them and a <c>catch (Exception)</c> that swallowed
    /// every exception rather than the retryable ones. The two consequences are asserted separately
    /// below, because they fail for different reasons and a maintainer reintroducing either should
    /// see which one they broke:
    ///
    ///  * <see cref="ServerErrorIsSentOncePerPolicyAttempt"/> — a caller's N-retry policy sent up to
    ///    11*(N+1) requests. With the ten-retry policy in the wild that is 121 requests for one
    ///    logical call, most of them within milliseconds of each other; downstream that read as
    ///    eleven concurrent runs of a job the user started once.
    ///  * <see cref="ClientSideFaultIsNotRetried"/> — a JsonException, an ArgumentException or a
    ///    cancellation was retried ten times and only the eleventh was rethrown, so a deterministic
    ///    error arrived eleven round trips late having wasted every attempt the policy had.
    ///
    /// The counts are the tests: eleven of the fourteen are red on the pre-fix wrapper. The three
    /// that are not — <see cref="SuccessIsSentOnce"/>, <see cref="RetryStillRecoversFromATransientFailure"/>
    /// and <see cref="PolicyWaitsBetweenAttempts"/> — are here to stop the fix being taken too far:
    /// removing the inner loop must not remove retrying, or the backoff, or turn a 200 into two
    /// requests.
    /// </summary>
    public class ExecuteAsyncRetryTest
    {
        private static RestRequest ARequest() => new RestRequest("/anything", Method.Post);

        /// <summary>A policy that retries <paramref name="retries"/> times, waiting <paramref name="waitMs"/> between attempts.</summary>
        private static IAsyncPolicy RetryPolicy(int retries, int waitMs = 0)
            => Policy.Handle<RestException>()
                     .WaitAndRetryAsync(retries, _ => TimeSpan.FromMilliseconds(waitMs));

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        public async Task ServerErrorIsSentOncePerPolicyAttempt(HttpStatusCode status)
        {
            var backend = CountingRestClient.Always(status);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 3));

            await Assert.ThrowsAsync<RestServerSideException>(
                () => client.ExecuteAsync(ARequest()));

            // Three retries after the first attempt. Not 4 * 11.
            Assert.Equal(4, backend.Attempts);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(408)]
        public async Task TimeoutIsSentOncePerPolicyAttempt(int status)
        {
            var backend = CountingRestClient.Always((HttpStatusCode)status);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 2));

            await Assert.ThrowsAsync<RestTimeoutException>(
                () => client.ExecuteAsync(ARequest()));

            Assert.Equal(3, backend.Attempts);
        }

        [Fact]
        public async Task ExhaustedRetriesRethrowRatherThanReturning()
        {
            var backend = CountingRestClient.Always(HttpStatusCode.BadGateway);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 1));

            var error = await Assert.ThrowsAsync<RestServerSideException>(
                () => client.ExecuteAsync(ARequest()));

            Assert.Equal(502, error.HttpCode);
            Assert.Equal(2, backend.Attempts);
        }

        [Fact]
        public async Task PolicyWaitsBetweenAttempts()
        {
            var backend = CountingRestClient.Always(HttpStatusCode.BadGateway);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 3, waitMs: 100));

            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<RestServerSideException>(
                () => client.ExecuteAsync(ARequest()));
            clock.Stop();

            // Three waits of 100ms. The old inner loop spent its eleven attempts inside a single
            // policy attempt, so the wait the caller configured applied to a tenth of the traffic.
            Assert.True(clock.ElapsedMilliseconds >= 250,
                $"Expected the configured backoff to be observed; the call took {clock.ElapsedMilliseconds}ms.");
        }

        [Fact]
        public async Task SuccessIsSentOnce()
        {
            var backend = CountingRestClient.Always(HttpStatusCode.OK);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 5));

            var response = await client.ExecuteAsync(ARequest());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, backend.Attempts);
        }

        [Fact]
        public async Task RetryStillRecoversFromATransientFailure()
        {
            // The fix removes an attempt loop; it must not remove retrying. A server that fails twice
            // and then answers is the case the wrapper exists for.
            var backend = CountingRestClient.FailsThenRecovers(2, HttpStatusCode.ServiceUnavailable);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 5));

            var response = await client.ExecuteAsync(ARequest());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, backend.Attempts);
        }

        [Theory]
        [MemberData(nameof(ClientSideFaults))]
        public async Task ClientSideFaultIsNotRetried(Exception fault)
        {
            // The policy handles RestException only, so nothing here is retryable. The old
            // catch (Exception) did not care, and burned ten attempts before rethrowing the eleventh.
            var backend = CountingRestClient.Throws(fault);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 10));

            var thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => client.ExecuteAsync(ARequest()));

            Assert.Same(fault, thrown);
            Assert.Equal(1, backend.Attempts);
        }

        public static TheoryData<Exception> ClientSideFaults() => new TheoryData<Exception>
        {
            new JsonException("unexpected token"),
            new ArgumentException("baseUrl"),
            new InvalidOperationException("client already disposed"),
        };

        [Fact]
        public async Task CancellationReachesTheInnerClient()
        {
            // The wrapper used to drop the caller's token on the floor, so a cancelled call kept
            // hammering the server until the inner loop ran out.
            var backend = CountingRestClient.Always(HttpStatusCode.BadGateway);
            var client = new ReliableRestClientWrapper(backend, RetryPolicy(retries: 10, waitMs: 50));

            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(120));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ExecuteAsync(ARequest(), cancellation.Token));

            Assert.True(backend.Attempts < 11,
                $"A cancelled call should stop early, but it sent {backend.Attempts} requests.");
        }
    }
}
