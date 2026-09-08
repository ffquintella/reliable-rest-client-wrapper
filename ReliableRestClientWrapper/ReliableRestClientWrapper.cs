using Polly;
using RestSharp;
using RestSharp.Authenticators;
using RestSharp.Serializers;
//using RestSharp.Deserializers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Security;
using ReliableRestClient.Exceptions;
//using RestSharp.Serialization;

namespace ReliableRestClient
{
    public class ReliableRestClientWrapper : RestClient, IRestClient
    {
        private IRestClient _innerClient;

        private readonly IAsyncPolicy _retryPolicy;

        private int[] HttpStatusCodesWorthRetrying = { 500, 502, 503, 504 };

        private int[] HttpStatusCodesTimeout = { 0, 408 };


        public ReliableRestClientWrapper(IRestClient innerClient, IAsyncPolicy retryPolicy) : base()
        {
            _innerClient = innerClient;
            _retryPolicy = retryPolicy;
        }


        /// <summary>
        /// Sends the request through the injected policy — once per policy attempt, and no more.
        ///
        /// The retry count, the wait between attempts and the set of exceptions worth retrying all
        /// belong to the <see cref="IAsyncPolicy"/> the caller passed in; that is the whole point of
        /// passing one. This method's only job is to run a single request and translate the retryable
        /// status codes into the exceptions the policy is configured to handle.
        ///
        /// It used to run its own <c>while</c> loop of up to eleven attempts *inside* the policy, with
        /// no wait between them and a <c>catch (Exception)</c> that swallowed everything — so a caller's
        /// ten-retry policy sent up to 121 requests, most within milliseconds, and a JsonException took
        /// eleven round trips to surface. See the tests in <c>ExecuteAsyncRetryTest</c>.
        /// </summary>
        public new async Task<RestResponse> ExecuteAsync(RestRequest request, CancellationToken cancellationToken = new CancellationToken())
        {
            RestResponse response = null;

            // The response is captured rather than returned out of ExecuteAsync so that a policy which
            // handles the exception without rethrowing (a Fallback, for instance) still yields the last
            // response received, as it did before.
            await _retryPolicy.ExecuteAsync(async ct =>
            {
                response = await _innerClient.ExecuteAsync(request, ct);
                ProcessResponse(response);
            }, cancellationToken);

            return response;
        }


        private void ProcessResponse(RestResponse response)
        {

            if (HttpStatusCodesWorthRetrying.Contains((int)response.StatusCode))
            {
               throw new RestServerSideException((int)response.StatusCode, response.ErrorMessage, response.ErrorException);
            }
            else if (HttpStatusCodesTimeout.Contains((int)response.StatusCode))
            {
               throw new RestTimeoutException((int)response.StatusCode, response.ErrorMessage, response.ErrorException);
            }
        }



        
    }
}
