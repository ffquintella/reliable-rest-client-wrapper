using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace ReliableRestClient.Tests
{
    /// <summary>
    /// An <see cref="IRestClient"/> that answers from a script and counts what it was asked.
    ///
    /// It derives from <see cref="RestClient"/> and re-declares <see cref="IRestClient"/> for the same
    /// reason <see cref="ReliableRestClientWrapper"/> does: RestSharp seals its interface
    /// implementation, so hiding <c>ExecuteAsync</c> with <c>new</c> only reaches interface callers
    /// when the derived type re-implements the interface. No socket is ever opened.
    /// </summary>
    public sealed class CountingRestClient : RestClient, IRestClient
    {
        private readonly Func<int, RestResponse> _answer;

        /// <summary>Every request this client was asked to send, in order.</summary>
        public List<DateTime> Sent { get; } = new List<DateTime>();

        /// <summary>How many times the client was asked to send the request.</summary>
        public int Attempts => Sent.Count;

        /// <summary>The cancellation token handed to the most recent attempt.</summary>
        public CancellationToken LastToken { get; private set; }

        private CountingRestClient(Func<int, RestResponse> answer) => _answer = answer;

        /// <summary>Answers every attempt with <paramref name="status"/>.</summary>
        public static CountingRestClient Always(HttpStatusCode status)
            => new CountingRestClient(attempt => Respond(status));

        /// <summary>
        /// Answers the first <paramref name="failures"/> attempts with <paramref name="status"/> and
        /// every attempt after that with 200 — a server that recovers.
        /// </summary>
        public static CountingRestClient FailsThenRecovers(int failures, HttpStatusCode status)
            => new CountingRestClient(attempt =>
                Respond(attempt <= failures ? status : HttpStatusCode.OK));

        /// <summary>Throws <paramref name="error"/> instead of answering — a client-side fault.</summary>
        public static CountingRestClient Throws(Exception error)
            => new CountingRestClient(attempt => throw error);

        private static RestResponse Respond(HttpStatusCode status)
            => new RestResponse
            {
                StatusCode = status,
                ResponseStatus = ResponseStatus.Completed,
                IsSuccessStatusCode = (int)status >= 200 && (int)status <= 299
            };

        public new Task<RestResponse> ExecuteAsync(RestRequest request,
            CancellationToken cancellationToken = new CancellationToken())
        {
            Sent.Add(DateTime.UtcNow);
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_answer(Attempts));
        }
    }
}
