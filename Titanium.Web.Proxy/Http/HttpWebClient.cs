using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// Used to communicate with the server over HTTP(S)
    /// </summary>
    public class HttpWebClient
    {
        private readonly int bufferSize;

        /// <summary>
        /// Connection to server
        /// </summary>
        internal TcpConnection ServerConnection { get; set; }

        /// <summary>
        /// Request ID.
        /// </summary>
        public Guid RequestId { get; }

        /// <summary>
        /// Override UpStreamEndPoint for this request; Local NIC via request is made
        /// </summary>
        public IPEndPoint UpStreamEndPoint { get; set; }

        /// <summary>
        /// Headers passed with Connect.
        /// </summary>
        public ConnectRequest ConnectRequest { get; internal set; }

        /// <summary>
        /// Web Request.
        /// </summary>
        public Request Request { get; internal set; }

        /// <summary>
        /// Web Response.
        /// </summary>
        public Response Response { get; internal set; }

        /// <summary>
        /// PID of the process that is created the current session when client is running in this machine
        /// If client is remote then this will return 
        /// </summary>
        public Lazy<int> ProcessId { get; internal set; }

        /// <summary>
        /// Is Https?
        /// </summary>
        public bool IsHttps => Request.IsHttps;

        internal HttpWebClient(int bufferSize)
        {
            this.bufferSize = bufferSize;

            RequestId = Guid.NewGuid();
            Request = new Request();
            Response = new Response();
        }

        /// <summary>
        /// Set the tcp connection to server used by this webclient
        /// </summary>
        /// <param name="connection">Instance of <see cref="TcpConnection"/></param>
        internal void SetConnection(TcpConnection connection)
        {
            connection.LastAccess = DateTime.Now;
            ServerConnection = connection;
        }
        
        /// <summary>
        /// Prepare and send the http(s) request
        /// </summary>
        /// <returns></returns>
        internal async Task SendRequest(bool enable100ContinueBehaviour)
        {
            var stream = ServerConnection.Stream;

            byte[] requestBytes;
            using (var ms = new MemoryStream())
            using (var writer = new HttpRequestWriter(ms, bufferSize))
            {
                var upstreamProxy = ServerConnection.UpStreamProxy;

                bool useUpstreamProxy = upstreamProxy != null && ServerConnection.IsHttps == false;

                //prepare the request & headers
                if (useUpstreamProxy)
                {
                    writer.WriteLine($"{Request.Method} {Request.OriginalUrl} HTTP/{Request.HttpVersion.Major}.{Request.HttpVersion.Minor}");
                }
                else
                {
                    writer.WriteLine($"{Request.Method} {Request.RequestUri.PathAndQuery} HTTP/{Request.HttpVersion.Major}.{Request.HttpVersion.Minor}");
                }


                //Send Authentication to Upstream proxy if needed
                if (upstreamProxy != null
                    && ServerConnection.IsHttps == false
                    && !string.IsNullOrEmpty(upstreamProxy.UserName)
                    && upstreamProxy.Password != null)
                {
                    HttpHeader.ProxyConnectionKeepAlive.WriteToStream(writer);
                    HttpHeader.GetProxyAuthorizationHeader(upstreamProxy.UserName, upstreamProxy.Password).WriteToStream(writer);
                }

                //write request headers
                foreach (var header in Request.Headers)
                {
                    if (header.Name != "Proxy-Authorization")
                    {
                        header.WriteToStream(writer);
                    }
                }

                writer.WriteLine();
                writer.Flush();

                requestBytes = ms.ToArray();
            }

            await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
            await stream.FlushAsync();

            if (enable100ContinueBehaviour)
            {
                if (Request.ExpectContinue)
                {
                    string httpStatus = await ServerConnection.StreamReader.ReadLineAsync();

                    Version version;
                    int responseStatusCode;
                    string responseStatusDescription;
                    Response.ParseResponseLine(httpStatus, out version, out responseStatusCode, out responseStatusDescription);

                    //find if server is willing for expect continue
                    if (responseStatusCode == (int)HttpStatusCode.Continue
                        && responseStatusDescription.Equals("continue", StringComparison.CurrentCultureIgnoreCase))
                    {
                        Request.Is100Continue = true;
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                    else if (responseStatusCode == (int)HttpStatusCode.ExpectationFailed
                             && responseStatusDescription.Equals("expectation failed", StringComparison.CurrentCultureIgnoreCase))
                    {
                        Request.ExpectationFailed = true;
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Receive and parse the http response from server
        /// </summary>
        /// <returns></returns>
        internal async Task ReceiveResponse()
        {
            //return if this is already read
            if (Response.StatusCode != 0)
                return;

            string httpStatus = await ServerConnection.StreamReader.ReadLineAsync();
            if (httpStatus == null)
            {
                throw new IOException();
            }

            if (httpStatus == string.Empty)
            {
                //Empty content in first-line, try again
                httpStatus = await ServerConnection.StreamReader.ReadLineAsync();
            }

            Version version;
            int statusCode;
            string statusDescription;
            Response.ParseResponseLine(httpStatus, out version, out statusCode, out statusDescription);

            Response.HttpVersion = version;
            Response.StatusCode = statusCode;
            Response.StatusDescription = statusDescription;

            //For HTTP 1.1 comptibility server may send expect-continue even if not asked for it in request
            if (Response.StatusCode == (int)HttpStatusCode.Continue
                && Response.StatusDescription.Equals("continue", StringComparison.CurrentCultureIgnoreCase))
            {
                //Read the next line after 100-continue 
                Response.Is100Continue = true;
                Response.StatusCode = 0;
                await ServerConnection.StreamReader.ReadLineAsync();
                //now receive response
                await ReceiveResponse();
                return;
            }

            if (Response.StatusCode == (int)HttpStatusCode.ExpectationFailed
                && Response.StatusDescription.Equals("expectation failed", StringComparison.CurrentCultureIgnoreCase))
            {
                //read next line after expectation failed response
                Response.ExpectationFailed = true;
                Response.StatusCode = 0;
                await ServerConnection.StreamReader.ReadLineAsync();
                //now receive response 
                await ReceiveResponse();
                return;
            }

            //Read the response headers in to unique and non-unique header collections
            await HeaderParser.ReadHeaders(ServerConnection.StreamReader, Response.Headers);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        internal void FinishSession()
        {
            ServerConnection = null;

            ConnectRequest?.FinishSession();
            Request?.FinishSession();
            Response?.FinishSession();
        }
    }
}
