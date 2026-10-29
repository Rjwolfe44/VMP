using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Server.Admin
{
    public sealed class AdminApiRequest
    {
        public string Method = "GET";
        /// <summary>Path without the query string, e.g. /api/v1/status.</summary>
        public string Path = "/";
        /// <summary>Everything after the question mark (no leading ?).</summary>
        public string Query = string.Empty;
        /// <summary>The raw Authorization header value, e.g. "Bearer abc".</summary>
        public string Authorization;
        public byte[] Body = new byte[0];
        public IPAddress RemoteAddress;
    }

    public sealed class AdminApiResponse
    {
        public int StatusCode { get; set; }
        public string Json { get; set; }

        public static AdminApiResponse Ok(object value)
        {
            return new AdminApiResponse { StatusCode = 200, Json = JsonSerializer.Serialize(value, AdminApiJson.Options) };
        }

        public static AdminApiResponse Error(int statusCode, string message)
        {
            return new AdminApiResponse { StatusCode = statusCode, Json = JsonSerializer.Serialize(new { error = message }, AdminApiJson.Options) };
        }
    }

    public sealed class SayRequest
    {
        public string Text { get; set; }
    }

    public sealed class KickRequest
    {
        public string Name { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// All request logic of the admin API with no sockets in it: a request goes in, a response comes out. Order of checks: remote must be
    /// loopback (403), bearer token (401, never any detail), body size (413), then the route (404 for unknown ones, 405 for a wrong verb).
    /// There is no restart, stop, settings write or file access.
    /// </summary>
    public sealed class AdminApiHandler
    {
        public const string Prefix = "/api/v1/";
        public const int MaxBodyBytes = 4096;
        public const int MaxLogLines = 500;
        public const int DefaultLogLines = 100;
        public const int MaxSayChars = 500;
        public const int MaxNameChars = 64;
        public const int MaxReasonChars = 200;

        private readonly IAdminApiBackend _backend;
        private readonly byte[] _tokenHash;
        private readonly Action<Exception> _onError;

        public AdminApiHandler(IAdminApiBackend backend, string token, Action<Exception> onError = null)
        {
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("token must not be empty", nameof(token));
            _backend = backend;
            _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            _onError = onError;
        }

        public async Task<AdminApiResponse> HandleAsync(AdminApiRequest request)
        {
            if (request == null || !IsLoopback(request.RemoteAddress)) return AdminApiResponse.Error(403, "forbidden");
            if (!IsAuthorized(request.Authorization)) return AdminApiResponse.Error(401, "unauthorized");
            if (request.Body != null && request.Body.Length > MaxBodyBytes) return AdminApiResponse.Error(413, "request body too large");

            var path = request.Path ?? string.Empty;
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return NotFound();

            var route = path.Substring(Prefix.Length).TrimEnd('/');
            var method = (request.Method ?? string.Empty).ToUpperInvariant();

            try
            {
                switch (route)
                {
                    case "status":
                        return method == "GET" ? AdminApiResponse.Ok(_backend.GetStatus()) : MethodNotAllowed();
                    case "network":
                        return method == "GET" ? AdminApiResponse.Ok(_backend.GetNetwork()) : MethodNotAllowed();
                    case "clients":
                        return method == "GET" ? AdminApiResponse.Ok(new { clients = _backend.GetClients() }) : MethodNotAllowed();
                    case "log":
                        return method == "GET" ? GetLog(request) : MethodNotAllowed();
                    case "reports":
                        return method == "GET" ? AdminApiResponse.Ok(_backend.GetReports()) : MethodNotAllowed();
                    case "backups":
                        if (method == "GET") return AdminApiResponse.Ok(new { backups = _backend.ListBackups() });
                        if (method == "POST") return await CreateBackup().ConfigureAwait(false);
                        return MethodNotAllowed();
                    case "say":
                        return method == "POST" ? Say(request) : MethodNotAllowed();
                    case "kick":
                        return method == "POST" ? Kick(request) : MethodNotAllowed();
                    default:
                        return NotFound();
                }
            }
            catch (AdminApiException e)
            {
                return AdminApiResponse.Error(e.StatusCode, e.Message);
            }
            catch (Exception e)
            {
                _onError?.Invoke(e);
                return AdminApiResponse.Error(500, "internal error");
            }
        }

        private AdminApiResponse GetLog(AdminApiRequest request)
        {
            var lines = DefaultLogLines;
            var raw = QueryValue(request.Query, "lines");
            if (raw != null && (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out lines) || lines < 1))
            {
                return AdminApiResponse.Error(400, "lines must be a positive number");
            }

            if (lines > MaxLogLines) lines = MaxLogLines;

            var result = new LogInfo { Requested = lines };
            var source = _backend.ReadLog(lines) ?? new List<string>();
            var skip = Math.Max(0, source.Count - lines);
            for (var i = skip; i < source.Count; i++) result.Lines.Add(AdminLogTail.Clean(source[i]));
            result.Count = result.Lines.Count;
            return AdminApiResponse.Ok(result);
        }

        private async Task<AdminApiResponse> CreateBackup()
        {
            var info = await _backend.CreateBackupAsync().ConfigureAwait(false);
            return info == null ? AdminApiResponse.Error(409, "a backup is already running") : AdminApiResponse.Ok(info);
        }

        private AdminApiResponse Say(AdminApiRequest request)
        {
            if (!TryReadJson<SayRequest>(request, out var body, out var problem)) return AdminApiResponse.Error(400, problem);

            var text = AdminLogTail.Clean(body.Text ?? string.Empty).Trim();
            if (text.Length == 0) return AdminApiResponse.Error(400, "text is required");
            if (text.Length > MaxSayChars) return AdminApiResponse.Error(400, "text is too long");

            _backend.Say(text);
            return AdminApiResponse.Ok(new { ok = true });
        }

        private AdminApiResponse Kick(AdminApiRequest request)
        {
            if (!TryReadJson<KickRequest>(request, out var body, out var problem)) return AdminApiResponse.Error(400, problem);

            var name = AdminLogTail.Clean(body.Name ?? string.Empty).Trim();
            var reason = AdminLogTail.Clean(body.Reason ?? string.Empty).Trim();
            if (name.Length == 0) return AdminApiResponse.Error(400, "name is required");
            if (name.Length > MaxNameChars) return AdminApiResponse.Error(400, "name is too long");
            if (reason.Length > MaxReasonChars) return AdminApiResponse.Error(400, "reason is too long");

            return _backend.Kick(name, reason) ? AdminApiResponse.Ok(new { ok = true }) : AdminApiResponse.Error(404, "player not found");
        }

        private static bool TryReadJson<T>(AdminApiRequest request, out T value, out string problem) where T : class
        {
            value = null;
            problem = null;
            if (request.Body == null || request.Body.Length == 0)
            {
                problem = "a JSON body is required";
                return false;
            }

            try
            {
                value = JsonSerializer.Deserialize<T>(request.Body, AdminApiJson.Options);
            }
            catch (JsonException)
            {
                problem = "invalid JSON";
                return false;
            }

            if (value == null)
            {
                problem = "a JSON object is required";
                return false;
            }

            return true;
        }

        internal static bool IsLoopback(IPAddress address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        /// <summary>Compares SHA-256 digests with a fixed-time compare, so neither the content nor the length of the token leaks through timing.</summary>
        private bool IsAuthorized(string header)
        {
            const string scheme = "Bearer ";
            var presented = string.Empty;
            var wellFormed = !string.IsNullOrEmpty(header) && header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase);
            if (wellFormed) presented = header.Substring(scheme.Length).Trim();

            var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
            var equal = CryptographicOperations.FixedTimeEquals(presentedHash, _tokenHash);
            return equal && wellFormed && presented.Length > 0;
        }

        internal static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var name = equals < 0 ? pair : pair.Substring(0, equals);
                if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase)) continue;
                return equals < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(equals + 1));
            }

            return null;
        }

        private static AdminApiResponse NotFound()
        {
            return AdminApiResponse.Error(404, "not found");
        }

        private static AdminApiResponse MethodNotAllowed()
        {
            return AdminApiResponse.Error(405, "method not allowed");
        }
    }
}
