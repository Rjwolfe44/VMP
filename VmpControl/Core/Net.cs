using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VmpControl.Core;

public sealed record ProcResult(int ExitCode, string Output, string Error, bool TimedOut, string StartError)
{
    public bool Ok => StartError == null && !TimedOut && ExitCode == 0;

    public string LastErrorLine()
    {
        var lines = (Error ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.Length > 0 ? lines[^1] : (StartError ?? string.Empty);
        return line.Length > 200 ? line.Substring(0, 200) : line;
    }
}

/// <summary>Starts child programs with an argument array (no shell, no window) and collects their output.</summary>
public static class ProcessRunner
{
    public static string SshPath()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : "ssh.exe";
    }

    public static string PowerShellPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : "powershell.exe";
    }

    public static ProcessStartInfo Build(string file, IEnumerable<string> args, bool redirect = true)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = redirect,
        };
        if (redirect)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.Start(Build(file, args));
            if (process == null) return new ProcResult(-1, "", "", false, "could not start " + Path.GetFileName(file));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcResult(-1, "", "", false, "could not start " + Path.GetFileName(file) + ": " + e.Message);
        }

        using (process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (ct.IsCancellationRequested) throw;
                return new ProcResult(-1, "", "", true, null);
            }

            return new ProcResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false), false, null);
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

public enum TokenProblem
{
    None,
    /// <summary>The ssh/file read worked but there is no token file: a server without the admin API.</summary>
    FileMissing,
    /// <summary>ssh could not connect.</summary>
    Unreachable,
    Failed,
}

/// <summary>
/// Gets the admin token once and keeps it in memory only. It is never written to disk and never put in a log line (the activity log is told
/// the secret so it can blank it out). A 401 invalidates it; it is then fetched again, at most once every 20 seconds.
/// </summary>
public sealed class TokenSource
{
    private static readonly Regex ShapeRx = new("^[A-Za-z0-9._~+/=-]{8,512}$", RegexOptions.CultureInvariant);
    private readonly Func<CancellationToken, Task<(string Token, TokenProblem Problem, string Detail)>> _fetch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _token;
    private DateTime _lastFetchUtc = DateTime.MinValue;

    public TokenProblem Problem { get; private set; }
    public string Detail { get; private set; }
    public event Action<string> Acquired;

    public TokenSource(Func<CancellationToken, Task<(string Token, TokenProblem Problem, string Detail)>> fetch)
    {
        _fetch = fetch;
    }

    public static TokenSource ForFile(string serverFolder)
    {
        var path = Path.Combine(serverFolder ?? string.Empty, "Config", "admin-token.txt");
        return new TokenSource(_ =>
        {
            try
            {
                if (!File.Exists(path)) return Task.FromResult<(string, TokenProblem, string)>((null, TokenProblem.FileMissing, "no admin-token.txt in the server's Config folder"));
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return Task.FromResult(Validate(reader.ReadToEnd()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult<(string, TokenProblem, string)>((null, TokenProblem.Failed, "could not read the token file: " + e.Message));
            }
        });
    }

    public static TokenSource ForSsh(string alias, string remoteRoot)
    {
        return new TokenSource(async ct =>
        {
            var result = await ProcessRunner.RunAsync(ProcessRunner.SshPath(), SshCommands.TokenReadArgs(alias, remoteRoot), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (result.TimedOut) return (null, TokenProblem.Unreachable, "ssh timed out");
            if (result.StartError != null) return (null, TokenProblem.Failed, result.StartError);
            if (result.ExitCode == 255) return (null, TokenProblem.Unreachable, "ssh could not connect: " + result.LastErrorLine());
            if (result.ExitCode != 0) return (null, TokenProblem.FileMissing, "no admin-token.txt on the server");
            return Validate(result.Output);
        });
    }

    private static (string, TokenProblem, string) Validate(string text)
    {
        var token = (text ?? string.Empty).Trim();
        return ShapeRx.IsMatch(token) ? (token, TokenProblem.None, null) : (null, TokenProblem.Failed, "the token file does not look like a token");
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_token != null) return _token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token != null) return _token;
            if (Problem != TokenProblem.None && DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromSeconds(5)) return null;

            _lastFetchUtc = DateTime.UtcNow;
            var (token, problem, detail) = await _fetch(ct).ConfigureAwait(false);
            Problem = problem;
            Detail = detail;
            _token = token;
            if (token != null) Acquired?.Invoke(token);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The server refused the token (401): forget it so the next call reads it again, but not more than once every 20 seconds.</summary>
    public void Invalidate()
    {
        if (DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromSeconds(20)) return;
        _token = null;
        Problem = TokenProblem.None;
    }
}

public enum ApiOutcome
{
    Ok,
    Unreachable,
    NotSupported,
    Unauthorized,
    Failed,
}

public sealed class ApiResult<T> where T : class
{
    public ApiOutcome Outcome { get; init; }
    public T Value { get; init; }
    public int StatusCode { get; init; }
    public string Error { get; init; }
    public bool IsOk => Outcome == ApiOutcome.Ok;

    /// <summary>The raw body of a failed response, so callers can read details such as the per-setting "errors" list of a rejected settings change.</summary>
    public string Body { get; init; }

    public static ApiResult<T> Fail(ApiOutcome outcome, string error, int status = 0, string body = null) => new() { Outcome = outcome, Error = error, StatusCode = status, Body = body };
}

/// <summary>HTTP client for the admin API on 127.0.0.1. Async, 3 second default timeout, no proxy, never logs headers.</summary>
public sealed class AdminApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TokenSource _tokens;
    private readonly string _baseUrl;

    public AdminApiClient(int port, TokenSource tokens)
    {
        _tokens = tokens;
        _baseUrl = $"http://127.0.0.1:{port}/api/v1/";
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken ct, int timeoutSeconds = 3) where T : class
        => SendAsync<T>(HttpMethod.Get, path, null, ct, timeoutSeconds);

    public Task<ApiResult<T>> PostAsync<T>(string path, object body, CancellationToken ct, int timeoutSeconds = 10) where T : class
        => SendAsync<T>(HttpMethod.Post, path, body ?? new { }, ct, timeoutSeconds);

    public Task<ApiResult<T>> PutAsync<T>(string path, object body, CancellationToken ct, int timeoutSeconds = 15) where T : class
        => SendAsync<T>(HttpMethod.Put, path, body ?? new { }, ct, timeoutSeconds);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken ct, int timeoutSeconds) where T : class
    {
        var token = await _tokens.GetAsync(ct).ConfigureAwait(false);
        if (token == null)
        {
            return _tokens.Problem == TokenProblem.FileMissing
                ? ApiResult<T>.Fail(ApiOutcome.NotSupported, _tokens.Detail)
                : ApiResult<T>.Fail(ApiOutcome.Unreachable, _tokens.Detail ?? "no token");
        }

        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var value = ApiJson.TryParse<T>(text);
                return value != null ? new ApiResult<T> { Outcome = ApiOutcome.Ok, Value = value, StatusCode = status } : ApiResult<T>.Fail(ApiOutcome.Failed, "unexpected response from the server", status);
            }

            var message = ApiJson.ErrorMessageOf(text) ?? response.ReasonPhrase;
            switch (status)
            {
                case 401:
                    _tokens.Invalidate();
                    return ApiResult<T>.Fail(ApiOutcome.Unauthorized, "the server refused the admin token", status);
                case 404 when string.Equals(message, "not found", StringComparison.OrdinalIgnoreCase):
                    return ApiResult<T>.Fail(ApiOutcome.NotSupported, "the server does not have this admin API call", status);
                default:
                    return ApiResult<T>.Fail(ApiOutcome.Failed, message, status, text);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult<T>.Fail(ApiOutcome.Unreachable, "timed out");
        }
        catch (HttpRequestException e)
        {
            return ApiResult<T>.Fail(ApiOutcome.Unreachable, e.InnerException?.Message ?? e.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>A Windows job object that kills its processes when the app dies, so an ssh tunnel is never left behind.</summary>
internal static class ChildProcessJob
{
    private static IntPtr _job;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong A, B, C, D, E, F;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    /// <summary>Best effort: a failure only means the tunnel is killed by our own Dispose instead.</summary>
    public static void Add(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (_job == IntPtr.Zero)
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return;
                var info = new ExtendedLimit { Basic = { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<ExtendedLimit>())) return;
                _job = job;
            }

            AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

/// <summary>
/// Keeps `ssh -N -L` running as a hidden child process: restarts it with the <see cref="Backoff"/> schedule when it exits and kills it on dispose.
/// The tunnel carries nothing secret by itself; the admin token is sent over it as an HTTP header to 127.0.0.1 only.
/// </summary>
public sealed class TunnelManager : IDisposable
{
    private readonly string _executable;
    private readonly string[] _args;
    private readonly CancellationTokenSource _stop = new();
    private Process _process;
    private Task _loop;

    public string Status { get; private set; } = "starting";
    public string LastError { get; private set; }
    public event Action<string> Changed;
    /// <summary>How many times the child process has been started (tests use this to see the restarts).</summary>
    internal int StartCount { get; private set; }
    internal int? ProcessId => _process?.Id;

    /// <summary>Validates the alias and ports now, so a bad profile fails before anything runs.</summary>
    public TunnelManager(string alias, int localPort, int remotePort) : this(ProcessRunner.SshPath(), SshCommands.TunnelArgs(alias, localPort, remotePort))
    {
    }

    /// <summary>For tests: supervise any program instead of ssh.</summary>
    internal TunnelManager(string executable, string[] args)
    {
        _executable = executable;
        _args = args;
    }

    public void Start()
    {
        _loop ??= Task.Run(LoopAsync);
    }

    private void Set(string status)
    {
        Status = status;
        Changed?.Invoke(status);
    }

    private async Task LoopAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            Process process = null;
            try
            {
                Set("connecting");
                var psi = ProcessRunner.Build(_executable, _args);
                process = Process.Start(psi);
                if (process == null) throw new InvalidOperationException("ssh did not start");
                _process = process;
                StartCount++;
                ChildProcessJob.Add(process);
                process.StandardInput.Close();
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data.Trim().Length > 200 ? e.Data.Trim().Substring(0, 200) : e.Data.Trim();
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // If ssh is still alive after a moment the forward is in place (ExitOnForwardFailure makes it exit otherwise).
                try
                {
                    await process.WaitForExitAsync(CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    Set("up");
                    await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                LastError = e.Message;
            }
            finally
            {
                // Whatever ended the wait (exit, error, or the app shutting down), the child never outlives this iteration.
                if (process != null)
                {
                    ProcessRunner.TryKill(process);
                    process.Dispose();
                }

                _process = null;
            }

            if (_stop.IsCancellationRequested) break;
            failures = DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(30) ? 1 : failures + 1;
            var delay = Backoff.Delay(failures);
            Set($"down - retrying in {(int)delay.TotalSeconds}s");
            try
            {
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        var process = _process;
        _stop.Cancel();
        if (process != null) ProcessRunner.TryKill(process);
    }
}
