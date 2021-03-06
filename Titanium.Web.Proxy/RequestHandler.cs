﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using StreamExtended;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy
{
    /// <summary>
    /// Handle the request
    /// </summary>
    partial class ProxyServer
    {
        private bool isWindowsAuthenticationEnabledAndSupported => EnableWinAuth && RunTime.IsWindows && !RunTime.IsRunningOnMono;

        /// <summary>
        /// This is called when client is aware of proxy
        /// So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="tcpClient"></param>
        /// <returns></returns>
        private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClient tcpClient)
        {
            bool disposed = false;

            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            var clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

            Uri httpRemoteUri;

            try
            {
                //read the first line HTTP command
                string httpCmd = await clientStreamReader.ReadLineAsync();
                if (string.IsNullOrEmpty(httpCmd))
                {
                    return;
                }

                Request.ParseRequestLine(httpCmd, out string httpMethod, out string httpUrl, out var version);

                httpRemoteUri = httpMethod == "CONNECT" ? new Uri("http://" + httpUrl) : new Uri(httpUrl);

                //filter out excluded host names
                bool excluded = false;

                if (endPoint.ExcludedHttpsHostNameRegex != null)
                {
                    excluded = endPoint.ExcludedHttpsHostNameRegexList.Any(x => x.IsMatch(httpRemoteUri.Host));
                }

                if (endPoint.IncludedHttpsHostNameRegex != null)
                {
                    excluded = !endPoint.IncludedHttpsHostNameRegexList.Any(x => x.IsMatch(httpRemoteUri.Host));
                }

                ConnectRequest connectRequest = null;

                //Client wants to create a secure tcp tunnel (probably its a HTTPS or Websocket request)
                if (httpMethod == "CONNECT")
                {
                    connectRequest = new ConnectRequest
                    {
                        RequestUri = httpRemoteUri,
                        OriginalUrl = httpUrl,
                        HttpVersion = version,
                    };

                    await HeaderParser.ReadHeaders(clientStreamReader, connectRequest.Headers);

                    var connectArgs = new TunnelConnectSessionEventArgs(BufferSize, endPoint, connectRequest, ExceptionFunc);
                    connectArgs.ProxyClient.TcpClient = tcpClient;
                    connectArgs.ProxyClient.ClientStream = clientStream;

                    if (TunnelConnectRequest != null)
                    {
                        await TunnelConnectRequest.InvokeAsync(this, connectArgs, ExceptionFunc);
                    }

                    if (await CheckAuthorization(clientStreamWriter, connectArgs) == false)
                    {
                        if (TunnelConnectResponse != null)
                        {
                            await TunnelConnectResponse.InvokeAsync(this, connectArgs, ExceptionFunc);
                        }

                        return;
                    }

                    //write back successfull CONNECT response
                    connectArgs.WebSession.Response = ConnectResponse.CreateSuccessfullConnectResponse(version);
                    await clientStreamWriter.WriteResponseAsync(connectArgs.WebSession.Response);

                    var clientHelloInfo = await SslTools.PeekClientHello(clientStream);
                    bool isClientHello = clientHelloInfo != null;
                    if (isClientHello)
                    {
                        connectRequest.ClientHelloInfo = clientHelloInfo;
                    }

                    if (TunnelConnectResponse != null)
                    {
                        connectArgs.IsHttpsConnect = isClientHello;
                        await TunnelConnectResponse.InvokeAsync(this, connectArgs, ExceptionFunc);
                    }

                    if (!excluded && isClientHello)
                    {
                        httpRemoteUri = new Uri("https://" + httpUrl);
                        connectRequest.RequestUri = httpRemoteUri;

                        SslStream sslStream = null;

                        try
                        {
                            sslStream = new SslStream(clientStream);

                            string certName = HttpHelper.GetWildCardDomainName(httpRemoteUri.Host);

                            var certificate = endPoint.GenericCertificate ?? CertificateManager.CreateCertificate(certName, false);

                            //Successfully managed to authenticate the client using the fake certificate
                            await sslStream.AuthenticateAsServerAsync(certificate, false, SupportedSslProtocols, false);

                            //HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferSize);

                            clientStreamReader.Dispose();
                            clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);
                        }
                        catch
                        {
                            sslStream?.Dispose();
                            return;
                        }

                        if (await CanBeHttpMethod(clientStream))
                        {
                            //Now read the actual HTTPS request line
                            httpCmd = await clientStreamReader.ReadLineAsync();
                        }
                        else
                        {
                            // It can be for example some Google (Cloude Messaging for Chrome) magic
                            excluded = true;
                        }
                    }

                    //Hostname is excluded or it is not an HTTPS connect
                    if (excluded || !isClientHello)
                    {
                        //create new connection
                        using (var connection = await GetServerConnection(connectArgs, true))
                        {
                            if (isClientHello)
                            {
                                int available = clientStream.Available;
                                if (available > 0)
                                {
                                    //send the buffered data
                                    var data = BufferPool.GetBuffer(BufferSize);

                                    try
                                    {
                                        // clientStream.Available sbould be at most BufferSize because it is using the same buffer size
                                        await clientStream.ReadAsync(data, 0, available);
                                        await connection.StreamWriter.WriteAsync(data, 0, available, true);
                                    }
                                    finally
                                    {
                                        BufferPool.ReturnBuffer(data);
                                    }
                                }

                                var serverHelloInfo = await SslTools.PeekServerHello(connection.Stream);
                                ((ConnectResponse)connectArgs.WebSession.Response).ServerHelloInfo = serverHelloInfo;
                            }

                            await TcpHelper.SendRawApm(clientStream, connection.Stream, BufferSize,
                                (buffer, offset, count) => { connectArgs.OnDataSent(buffer, offset, count); },
                                (buffer, offset, count) => { connectArgs.OnDataReceived(buffer, offset, count); },
                                ExceptionFunc);
                        }

                        return;
                    }
                }

                //Now create the request
                disposed = await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    httpRemoteUri.Scheme == UriSchemeHttps ? httpRemoteUri.Host : null, endPoint, connectRequest);
            }
            catch (IOException e)
            {
                ExceptionFunc(new Exception("Connection was aborted", e));
            }
            catch (SocketException e)
            {
                ExceptionFunc(new Exception("Could not connect", e));
            }
            catch (Exception e)
            {
                // is this the correct error message?
                ExceptionFunc(new Exception("Error whilst authorizing request", e));
            }
            finally
            {
                if (!disposed)
                {
                    Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                }
            }
        }

        private async Task<bool> CanBeHttpMethod(CustomBufferedStream clientStream)
        {
            int legthToCheck = 10;
            for (int i = 0; i < legthToCheck; i++)
            {
                int b = await clientStream.PeekByteAsync(i);
                if (b == -1)
                {
                    return false;
                }

                if (b == ' ' && i > 2)
                {
                    // at least 3 letters and a space
                    return true;
                }

                if (!char.IsLetter((char)b))
                {
                    // non letter or too short
                    return false;
                }
            }

            // only letters
            return true;
        }

        /// <summary>
        /// This is called when this proxy acts as a reverse proxy (like a real http server)
        /// So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="tcpClient"></param>
        /// <returns></returns>
        private async Task HandleClient(TransparentProxyEndPoint endPoint, TcpClient tcpClient)
        {
            bool disposed = false;
            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            CustomBinaryReader clientStreamReader = null;
            HttpResponseWriter clientStreamWriter = null;

            try
            {
                if (endPoint.EnableSsl)
                {
                    var clientHelloInfo = await SslTools.PeekClientHello(clientStream);

                    if (clientHelloInfo != null)
                    {
                        var sslStream = new SslStream(clientStream);
                        clientStream = new CustomBufferedStream(sslStream, BufferSize);

                        string sniHostName = clientHelloInfo.GetServerName();
                        
                        string certName = HttpHelper.GetWildCardDomainName(sniHostName ?? endPoint.GenericCertificateName);
                        var certificate = CertificateManager.CreateCertificate(certName, false);

                        //Successfully managed to authenticate the client using the fake certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls, false);
                    }

                    //HTTPS server created - we can now decrypt the client's traffic
                }

                clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

                //now read the request line
                string httpCmd = await clientStreamReader.ReadLineAsync();

                //Now create the request
                disposed = await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    endPoint.EnableSsl ? endPoint.GenericCertificateName : null, endPoint, null, true);
            }
            finally
            {
                if (!disposed)
                {
                    Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                }
            }
        }

        /// <summary>
        /// This is the core request handler method for a particular connection from client
        /// Will create new session (request/response) sequence until 
        /// client/server abruptly terminates connection or by normal HTTP termination
        /// </summary>
        /// <param name="client"></param>
        /// <param name="httpCmd"></param>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="httpsConnectHostname"></param>
        /// <param name="endPoint"></param>
        /// <param name="connectRequest"></param>
        /// <param name="isTransparentEndPoint"></param>
        /// <returns></returns>
        private async Task<bool> HandleHttpSessionRequest(TcpClient client, string httpCmd, CustomBufferedStream clientStream,
            CustomBinaryReader clientStreamReader, HttpResponseWriter clientStreamWriter, string httpsConnectHostname,
            ProxyEndPoint endPoint, ConnectRequest connectRequest, bool isTransparentEndPoint = false)
        {
            bool disposed = false;

            TcpConnection connection = null;

            //Loop through each subsequest request on this particular client connection
            //(assuming HTTP connection is kept alive by client)
            while (true)
            {
                if (string.IsNullOrEmpty(httpCmd))
                {
                    break;
                }

                var args = new SessionEventArgs(BufferSize, endPoint, HandleHttpSessionResponse, ExceptionFunc)
                {
                    ProxyClient = { TcpClient = client },
                    WebSession = { ConnectRequest = connectRequest }
                };

                try
                {
                    Request.ParseRequestLine(httpCmd, out string httpMethod, out string httpUrl, out var version);

                    //Read the request headers in to unique and non-unique header collections
                    await HeaderParser.ReadHeaders(clientStreamReader, args.WebSession.Request.Headers);

                    var httpRemoteUri = new Uri(httpsConnectHostname == null
                        ? isTransparentEndPoint ? string.Concat("http://", args.WebSession.Request.Host, httpUrl) : httpUrl
                        : string.Concat("https://", args.WebSession.Request.Host ?? httpsConnectHostname, httpUrl));

                    args.WebSession.Request.RequestUri = httpRemoteUri;
                    args.WebSession.Request.OriginalUrl = httpUrl;

                    args.WebSession.Request.Method = httpMethod;
                    args.WebSession.Request.HttpVersion = version;
                    args.ProxyClient.ClientStream = clientStream;
                    args.ProxyClient.ClientStreamReader = clientStreamReader;
                    args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                    //proxy authorization check
                    if (httpsConnectHostname == null && await CheckAuthorization(clientStreamWriter, args) == false)
                    {
                        args.Dispose();
                        break;
                    }

                    PrepareRequestHeaders(args.WebSession.Request.Headers);
                    args.WebSession.Request.Host = args.WebSession.Request.RequestUri.Authority;

                    //if win auth is enabled
                    //we need a cache of request body
                    //so that we can send it after authentication in WinAuthHandler.cs
                    if (isWindowsAuthenticationEnabledAndSupported && args.WebSession.Request.HasBody)
                    {
                        await args.GetRequestBody();
                    }

                    //If user requested interception do it
                    if (BeforeRequest != null)
                    {
                        await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
                    }

                    if (args.WebSession.Request.CancelRequest)
                    {
                        args.Dispose();
                        break;
                    }

                    //create a new connection if hostname/upstream end point changes
                    if (connection != null
                        && (!connection.HostName.Equals(args.WebSession.Request.RequestUri.Host, StringComparison.OrdinalIgnoreCase)
                           || (args.WebSession.UpStreamEndPoint != null
                           && !args.WebSession.UpStreamEndPoint.Equals(connection.UpStreamEndPoint))))
                    {
                        connection.Dispose();
                        connection = null;
                    }

                    if (connection == null)
                    {
                        connection = await GetServerConnection(args, false);
                    }

                    //if upgrading to websocket then relay the requet without reading the contents
                    if (args.WebSession.Request.UpgradeToWebSocket)
                    {
                        //prepare the prefix content
                        var requestHeaders = args.WebSession.Request.Headers;
                        await connection.StreamWriter.WriteLineAsync(httpCmd);
                        await connection.StreamWriter.WriteHeadersAsync(requestHeaders);
                        string httpStatus = await connection.StreamReader.ReadLineAsync();

                        Response.ParseResponseLine(httpStatus, out var responseVersion, out int responseStatusCode, out string responseStatusDescription);
                        args.WebSession.Response.HttpVersion = responseVersion;
                        args.WebSession.Response.StatusCode = responseStatusCode;
                        args.WebSession.Response.StatusDescription = responseStatusDescription;

                        await HeaderParser.ReadHeaders(connection.StreamReader, args.WebSession.Response.Headers);

                        await clientStreamWriter.WriteResponseAsync(args.WebSession.Response);

                        //If user requested call back then do it
                        if (BeforeResponse != null && !args.WebSession.Response.ResponseLocked)
                        {
                            await BeforeResponse.InvokeAsync(this, args, ExceptionFunc);
                        }

                        await TcpHelper.SendRawApm(clientStream, connection.Stream, BufferSize,
                            (buffer, offset, count) => { args.OnDataSent(buffer, offset, count); },
                            (buffer, offset, count) => { args.OnDataReceived(buffer, offset, count); },
                            ExceptionFunc);

                        args.Dispose();
                        break;
                    }

                    //construct the web request that we are going to issue on behalf of the client.
                    disposed = await HandleHttpSessionRequestInternal(connection, args, false);

                    if (disposed)
                    {
                        //already disposed inside above method
                        args.Dispose();
                        break;
                    }

                    //if connection is closing exit
                    if (args.WebSession.Response.KeepAlive == false)
                    {
                        args.Dispose();
                        break;
                    }

                    args.Dispose();

                    // read the next request
                    httpCmd = await clientStreamReader.ReadLineAsync();
                }
                catch (Exception e)
                {
                    ExceptionFunc(new ProxyHttpException("Error occured whilst handling session request", e, args));
                    break;
                }
            }

            if (!disposed)
            {
                Dispose(clientStream, clientStreamReader, clientStreamWriter, connection);
            }

            return true;
        }

        /// <summary>
        /// Handle a specific session (request/response sequence)
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="args"></param>
        /// <param name="closeConnection"></param>
        /// <returns></returns>
        private async Task<bool> HandleHttpSessionRequestInternal(TcpConnection connection, SessionEventArgs args, bool closeConnection)
        {
            bool disposed = false;
            bool keepAlive = false;

            try
            {
                var request = args.WebSession.Request;
                request.RequestLocked = true;

                //if expect continue is enabled then send the headers first 
                //and see if server would return 100 conitinue
                if (request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour);
                }

                //If 100 continue was the response inform that to the client
                if (Enable100ContinueBehaviour)
                {
                    var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                    if (request.Is100Continue)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion, (int)HttpStatusCode.Continue, "Continue");
                        await clientStreamWriter.WriteLineAsync();
                    }
                    else if (request.ExpectationFailed)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion, (int)HttpStatusCode.ExpectationFailed, "Expectation Failed");
                        await clientStreamWriter.WriteLineAsync();
                    }
                }

                //If expect continue is not enabled then set the connectio and send request headers
                if (!request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour);
                }

                //check if content-length is > 0
                if (request.ContentLength > 0)
                {
                    //If request was modified by user
                    if (request.IsBodyRead)
                    {
                        if (request.ContentEncoding != null)
                        {
                            request.Body = await GetCompressedResponseBody(request.ContentEncoding, request.Body);
                        }

                        var body = request.Body;

                        //chunked send is not supported as of now
                        request.ContentLength = body.Length;

                        await args.WebSession.ServerConnection.StreamWriter.WriteAsync(body);
                    }
                    else
                    {
                        if (!request.ExpectationFailed)
                        {
                            //If its a post/put/patch request, then read the client html body and send it to server
                            if (request.HasBody)
                            {
                                HttpWriter writer = args.WebSession.ServerConnection.StreamWriter;
                                await args.CopyRequestBodyAsync(writer, false);
                            }
                        }
                    }
                }

                //If not expectation failed response was returned by server then parse response
                if (!request.ExpectationFailed)
                {
                    disposed = await HandleHttpSessionResponse(args);

                    //already disposed inside above method
                    if (disposed)
                    {
                        return true;
                    }
                }

                //if connection is closing exit
                if (args.WebSession.Response.KeepAlive == false)
                {
                    return true;
                }

                if (!closeConnection)
                {
                    keepAlive = true;
                    return false;
                }
            }
            catch (Exception e)
            {
                ExceptionFunc(new ProxyHttpException("Error occured whilst handling session request (internal)", e, args));
                return true;
            }
            finally
            {
                if (!disposed && !keepAlive)
                {
                    //dispose
                    Dispose(args.ProxyClient.ClientStream, args.ProxyClient.ClientStreamReader, args.ProxyClient.ClientStreamWriter,
                        args.WebSession.ServerConnection);
                }
            }

            return true;
        }

        /// <summary>
        /// Create a Server Connection
        /// </summary>
        /// <param name="args"></param>
        /// <param name="isConnect"></param>
        /// <returns></returns>
        private async Task<TcpConnection> GetServerConnection(SessionEventArgs args, bool isConnect)
        {
            ExternalProxy customUpStreamProxy = null;

            bool isHttps = args.IsHttps;
            if (GetCustomUpStreamProxyFunc != null)
            {
                customUpStreamProxy = await GetCustomUpStreamProxyFunc(args);
            }

            args.CustomUpStreamProxyUsed = customUpStreamProxy;

            return await tcpConnectionFactory.CreateClient(this,
                args.WebSession.Request.RequestUri.Host,
                args.WebSession.Request.RequestUri.Port,
                args.WebSession.Request.HttpVersion,
                isHttps, isConnect,
                args.WebSession.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? (isHttps ? UpStreamHttpsProxy : UpStreamHttpProxy));
        }

        /// <summary>
        /// prepare the request headers so that we can avoid encodings not parsable by this proxy
        /// </summary>
        /// <param name="requestHeaders"></param>
        private void PrepareRequestHeaders(HeaderCollection requestHeaders)
        {
            foreach (var header in requestHeaders)
            {
                switch (header.Name.ToLower())
                {
                    //these are the only encoding this proxy can read
                    case KnownHeaders.AcceptEncoding:
                        header.Value = "gzip,deflate";
                        break;
                }
            }

            requestHeaders.FixProxyHeaders();
        }
    }
}
