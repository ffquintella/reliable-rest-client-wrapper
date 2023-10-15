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

        private int[] HttpStatusCodesWorthRetrying = { 500, 502, 503 };

        private int[] HttpStatusCodesTimeout = { 0, 408, 504 };


        public ReliableRestClientWrapper(IRestClient innerClient, IAsyncPolicy retryPolicy) : base()
        {
            _innerClient = innerClient;
            _retryPolicy = retryPolicy;
        }


        public new async Task<RestResponse> ExecuteAsync(RestRequest request, CancellationToken cancellationToken = new CancellationToken())
        {
            RestResponse response = null;
            
            await _retryPolicy.ExecuteAsync(async () =>
            {
                response = await _innerClient.ExecuteAsync(request);
                ProcessResponse(response);

            });


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
