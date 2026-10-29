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
    /// After <see cref="AuthFailureLimit"/> failed authentications within <see cref="AuthFailureWindow"/> every request is answered with 429 for
    /// <see cref="AuthLockout"/>, without looking at the token. There is no restart, stop or file access; settings can be read and changed (not adminApi.*).
    /// </summary>
    public sealed class AdminApiHandler
    {
        public const string Prefix = "/api/v1/";
        public const int MaxBodyBytes = 4096;
        /// <summary>Body limit of PUT /api/v1/settings.</summary>
        public const int MaxSettingsBodyBytes = 65536;
        public const int AuthFailureLimit = 10;
        public static readonly TimeSpan AuthFailureWindow = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan AuthLockout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan AuthWarningInterval = TimeSpan.FromSeconds(60);
        public const int MaxLogLines = 500;
        public const int DefaultLogLines = 100;
        public const int MaxSayChars = 500;
        public const int MaxNameChars = 64;
        public const int MaxReasonChars = 200;

        private readonly IAdminApiBackend _backend;
        private readonly byte[] _tokenHash;
        private readonly Action<Exception> _onError;
        private readonly ISettingsAdmin _settings;
        private readonly Func<DateTime> _utcNow;
        private readonly Action<string> _onWarning;

        private readonly object _authGate = new object();
        private readonly Queue<DateTime> _authFailures = new Queue<DateTime>();
        private DateTime _lockedUntilUtc = DateTime.MinValue;
        private DateTime? _lastWarningUtc;
        private int _failedSinceWarning;
        private int _refusedSinceWarning;

        /// <param name="settings">The settings endpoints; without it they answer 404.</param>
        /// <param name="utcNow">The clock (tests pass a fake one).</param>
        /// <param name="onWarning">Gets the rate limit warning lines (never a token).</param>
        public AdminApiHandler(IAdminApiBackend backend, string token, Action<Exception> onError = null, ISettingsAdmin settings = null, Func<DateTime> utcNow = null, Action<string> onWarning = null)
        {
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("token must not be empty", nameof(token));
            _backend = backend;
            _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            _onError = onError;
            _settings = settings;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _onWarning = onWarning;
        }

        /// <summary>The largest request body accepted for this request: 64 KB for PUT /api/v1/settings, 4 KB for everything else.</summary>
        public static int BodyLimit(string method, string path)
        {
            var isSettings = string.Equals((path ?? string.Empty).TrimEnd('/'), Prefix + "settings", StringComparison.Ordinal);
            return isSettings && string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ? MaxSettingsBodyBytes : MaxBodyBytes;
        }

        public async Task<AdminApiResponse> HandleAsync(AdminApiRequest request)
        {
            if (request == null || !IsLoopback(request.RemoteAddress)) return AdminApiResponse.Error(403, "forbidden");
            if (IsLockedOut()) return AdminApiResponse.Error(429, "too many failed authentications, try again later");
            if (!IsAuthorized(request.Authorization))
            {
                RegisterAuthFailure();
                return AdminApiResponse.Error(401, "unauthorized");
            }

            if (request.Body != null && request.Body.Length > BodyLimit(request.Method, request.Path)) return AdminApiResponse.Error(413, "request body too large");

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
                    case "settings":
                        if (_settings == null) return NotFound();
                        if (method == "GET") return _settings.GetSettings();
                        if (method == "PUT") return PutSettings(request);
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

        private AdminApiResponse PutSettings(AdminApiRequest request)
        {
            if (request.Body == null || request.Body.Length == 0) return AdminApiResponse.Error(400, "a JSON body is required");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 8 });
            }
            catch (JsonException)
            {
                return AdminApiResponse.Error(400, "invalid JSON");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in root.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "changes", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                        {
                            return _settings.PutSettings(property.Value);
                        }
                    }
                }

                return AdminApiResponse.Error(400, "a \"changes\" object is required, for example {\"changes\":{\"general.ServerName\":\"My server\"}}");
            }
        }

        private bool IsLockedOut()
        {
            lock (_authGate)
            {
                var now = _utcNow();
                if (now >= _lockedUntilUtc) return false;

                _refusedSinceWarning++;
                WarnIfDue(now);
                return true;
            }
        }

        private void RegisterAuthFailure()
        {
            lock (_authGate)
            {
                var now = _utcNow();
                _failedSinceWarning++;
                _authFailures.Enqueue(now);
                while (_authFailures.Count > 0 && now - _authFailures.Peek() > AuthFailureWindow) _authFailures.Dequeue();

                if (_authFailures.Count >= AuthFailureLimit)
                {
                    _authFailures.Clear();
                    _lockedUntilUtc = now + AuthLockout;
                    WarnIfDue(now);
                }
            }
        }

        /// <summary>At most one warning line per <see cref="AuthWarningInterval"/>, counting everything since the previous one.</summary>
        private void WarnIfDue(DateTime now)
        {
            if (_lastWarningUtc.HasValue && now - _lastWarningUtc.Value < AuthWarningInterval) return;

            var line = _failedSinceWarning + " failed admin API authentications";
            if (_refusedSinceWarning > 0) line += ", " + _refusedSinceWarning + " requests refused with 429";
            line += " (requests are refused for " + (int)AuthLockout.TotalSeconds + " seconds after " + AuthFailureLimit + " failures within " + (int)AuthFailureWindow.TotalSeconds + " seconds)";
            _lastWarningUtc = now;
            _failedSinceWarning = 0;
            _refusedSinceWarning = 0;
            try
            {
                _onWarning?.Invoke(line);
            }
            catch (Exception)
            {
            }
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
