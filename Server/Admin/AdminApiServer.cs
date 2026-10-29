using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Admin
{
    /// <summary>
    /// Minimal HTTP/1.1 front for <see cref="AdminApiHandler"/>. Binds 127.0.0.1 only (hard-coded), refuses any non-loopback peer, answers
    /// one request per connection, reads at most 8 KB of headers and 4 KB of body (a larger Content-Length is answered with 413 without
    /// reading it), no chunked bodies, a few seconds to send the request, at most 16 connections at once. It sends no CORS headers.
    /// </summary>
    public sealed class AdminApiServer : IDisposable
    {
        public const int MaxHeaderBytes = 8192;
        public const int MaxConnections = 16;
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

        private sealed class Parsed
        {
            public AdminApiRequest Request;
            public AdminApiResponse Error;
        }

        private readonly AdminApiHandler _handler;
        private readonly Action<Exception> _onError;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly SemaphoreSlim _connections = new SemaphoreSlim(MaxConnections, MaxConnections);
        private TcpListener _listener;

        public AdminApiServer(AdminApiHandler handler, Action<Exception> onError = null)
        {
            _handler = handler;
            _onError = onError;
        }

        /// <summary>The port actually bound (useful when 0 was asked for).</summary>
        public int Port { get; private set; }

        public void Start(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (_stop.IsCancellationRequested) return;
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }

                if (!_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    var stream = client.GetStream();

                    Parsed parsed;
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        timeout.CancelAfter(ReadTimeout);
                        parsed = await ReadRequestAsync(stream, remote, timeout.Token).ConfigureAwait(false);
                    }

                    if (parsed == null) return;

                    AdminApiResponse response;
                    if (!AdminApiHandler.IsLoopback(remote)) response = AdminApiResponse.Error(403, "forbidden");
                    else response = parsed.Error ?? await _handler.HandleAsync(parsed.Request).ConfigureAwait(false);

                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        timeout.CancelAfter(WriteTimeout);
                        await WriteResponseAsync(stream, response, timeout.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // A client that vanished or sent garbage is not worth more than this.
                if (!(e is IOException || e is OperationCanceledException || e is SocketException || e is ObjectDisposedException)) _onError?.Invoke(e);
            }
            finally
            {
                _connections.Release();
            }
        }

        private static async Task<Parsed> ReadRequestAsync(Stream stream, IPAddress remote, CancellationToken token)
        {
            var buffer = new byte[MaxHeaderBytes];
            var filled = 0;
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                if (filled == buffer.Length) return Fail(431, "request headers too large");
                var read = await stream.ReadAsync(buffer, filled, buffer.Length - filled, token).ConfigureAwait(false);
                if (read == 0) return null;
                filled += read;
                headerEnd = FindHeaderEnd(buffer, filled);
            }

            var head = Encoding.Latin1.GetString(buffer, 0, headerEnd);
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !requestLine[1].StartsWith("/", StringComparison.Ordinal))
            {
                return Fail(400, "bad request");
            }

            var request = new AdminApiRequest { Method = requestLine[0], RemoteAddress = remote };
            var question = requestLine[1].IndexOf('?');
            request.Path = question < 0 ? requestLine[1] : requestLine[1].Substring(0, question);
            request.Query = question < 0 ? string.Empty : requestLine[1].Substring(question + 1);

            long contentLength = 0;
            var sawLength = false;
            var sawAuthorization = false;
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) return Fail(400, "bad request");

                var name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                var value = lines[i].Substring(colon + 1).Trim();
                switch (name)
                {
                    case "authorization":
                        if (sawAuthorization) return Fail(400, "bad request");
                        sawAuthorization = true;
                        request.Authorization = value;
                        break;
                    case "content-length":
                        if (sawLength || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength)) return Fail(400, "bad request");
                        sawLength = true;
                        break;
                    case "transfer-encoding":
                        return Fail(400, "transfer-encoding is not supported");
                }
            }

            if (contentLength > AdminApiHandler.MaxBodyBytes) return Fail(413, "request body too large");

            var body = new byte[contentLength];
            var already = Math.Min(filled - (headerEnd + 4), body.Length);
            if (already > 0) Buffer.BlockCopy(buffer, headerEnd + 4, body, 0, already);
            while (already < body.Length)
            {
                var read = await stream.ReadAsync(body, already, body.Length - already, token).ConfigureAwait(false);
                if (read == 0) return null;
                already += read;
            }

            request.Body = body;
            return new Parsed { Request = request };
        }

        private static Parsed Fail(int status, string message)
        {
            return new Parsed { Error = AdminApiResponse.Error(status, message) };
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (var i = 0; i + 3 < length; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
            }

            return -1;
        }

        private static async Task WriteResponseAsync(Stream stream, AdminApiResponse response, CancellationToken token)
        {
            var body = Encoding.UTF8.GetBytes(response.Json ?? "{}");
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(response.StatusCode)).Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            head.Append("Connection: close\r\n\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, 0, headBytes.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 431: return "Request Header Fields Too Large";
                default: return "Error";
            }
        }
    }
}
