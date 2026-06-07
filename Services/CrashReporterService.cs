using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Automatic crash reporter — on an unhandled exception, files (or comments on) a GitHub issue
/// in the project repo so the developer sees crashes from any user without that user needing a
/// GitHub account.
///
/// Design notes:
///   * Token: a FINE-GRAINED PAT scoped to ONLY this one repo + ONLY Issues:Read&Write. Even if
///     extracted from the shipped app it can do nothing beyond creating/editing issues on that one
///     repo (no code, no other repos, no account) — the fine-grained scope IS the safety boundary.
///     Stored in <see cref="TokenFileName"/> next to the .exe, gitignored so it's never committed
///     (a committed token in a public repo gets auto-revoked by GitHub secret scanning).
///   * Dedup: each crash gets a signature hash (exception type + top normalized stack frames). If
///     an OPEN issue already carries that signature, we add a comment ("happened again …") instead
///     of opening a duplicate. A rate-limit (max 1 NEW issue per window) is a secondary backstop.
///   * Privacy: secrets (wiki password, tokens, API keys) and the Windows username in paths are
///     scrubbed before sending.
///   * Resilience: reporting is best-effort + time-boxed; failures are swallowed (a crash handler
///     must never throw). Offline/failed reports are queued to disk and retried on next launch.
/// </summary>
public static class CrashReporterService
{
    // ── Configuration ──────────────────────────────────────────────────────────
    private const string RepoOwner = "jouki";
    private const string RepoName = "MergeMansionWikiTools";
    private const string IssueLabel = "auto-crash";
    private static readonly TimeSpan NewIssueRateLimit = TimeSpan.FromMinutes(5);
    private const int HttpTimeoutSeconds = 20;

    /// <summary>Filename of the fine-grained PAT, sitting next to the .exe. It is in .gitignore so
    /// it is NEVER committed (a committed token in a public repo gets auto-revoked by GitHub secret
    /// scanning), but it IS copied to the build output and shipped with the distributed app. Plaintext
    /// on disk — acceptable because the token's only power is creating/editing Issues on one repo.
    /// When the file is absent/empty, crash reporting is silently disabled (reports still go to the
    /// local offline queue for manual review).</summary>
    private const string TokenFileName = "crashreporter.token";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds) };
    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MergeMansionWikiTools", "crash");
    private static string RateStatePath => Path.Combine(StateDir, "ratelimit.json");
    private static string QueueDir => Path.Combine(StateDir, "queue");

    private static int _handled; // guards against re-entrancy storms
    // In-process dedup. By EXCEPTION REFERENCE: the SAME exception instance propagates from the
    // Dispatcher hook (e.Handled=false) to AppDomain.UnhandledException — reference equality nails
    // it even if their stack traces (→ signatures) differ. By SIGNATURE: also collapses two
    // distinct-but-identical crashes within one process.
    private static readonly HashSet<Exception> _reportedExceptions = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> _reportedSignatures = new();

    // ── Init / hooks ───────────────────────────────────────────────────────────

    /// <summary>Wires the three unhandled-exception sources. Call once at app startup (after
    /// AppLogger.Init). Also flushes any queued offline reports from previous sessions.</summary>
    public static void Init()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle(e.ExceptionObject as Exception, "AppDomain.UnhandledException", terminating: e.IsTerminating);

        System.Windows.Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            Handle(e.Exception, "Dispatcher (UI thread)", terminating: false);
            // Leave e.Handled=false — let WPF's default crash behavior proceed; we only report.
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle(e.Exception, "TaskScheduler.UnobservedTaskException", terminating: false);
            e.SetObserved(); // prevent process-kill for a merely-unobserved task exception
        };

        // Retry any reports that couldn't be sent in previous sessions (offline / earlier failure).
        _ = Task.Run(FlushOfflineQueueAsync);
    }

    private static void Handle(Exception? ex, string source, bool terminating)
    {
        if (ex == null) return;
        if (Interlocked.Increment(ref _handled) > 20) return; // runaway guard
        try
        {
            // Dedup within this process: a single crash often fires BOTH the Dispatcher hook
            // (which leaves e.Handled=false) AND then AppDomain.UnhandledException for the same
            // exception. Skip if we've already handled this exact instance OR signature.
            var sig = ComputeSignature(ex);
            lock (_reportedExceptions)
            {
                bool firstInstance = _reportedExceptions.Add(ex);
                bool firstSig = _reportedSignatures.Add(sig);
                if (!firstInstance || !firstSig) return;
            }

            AppLogger.Error($"[CRASH] caught via {source} (terminating={terminating})", ex);
            var report = BuildReport(ex, source);
            // CRITICAL: run reporting on a THREAD-POOL thread, not inline. Handle() may run on the
            // WPF UI thread (DispatcherUnhandledException); blocking it with .Wait() while the async
            // HTTP continuation needs that same thread = deadlock (observed: 22s freeze → timeout →
            // nothing sent). Task.Run gives the async chain a context-free thread to complete on.
            Task.Run(() => ReportAsync(report)).Wait(TimeSpan.FromSeconds(HttpTimeoutSeconds + 5));
        }
        catch (Exception inner)
        {
            // Last resort — never let the reporter throw. Persist locally so nothing is lost.
            try { QueueOffline(BuildFallbackReport(ex, source, inner)); } catch { }
        }
    }

    // ── Report model + builder ─────────────────────────────────────────────────

    private sealed class CrashReport
    {
        public string Signature { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
    }

    private static CrashReport BuildReport(Exception ex, string source)
    {
        var sig = ComputeSignature(ex);
        var top = ex.GetType().Name;
        var firstLine = (ex.Message ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (firstLine.Length > 120) firstLine = firstLine[..120] + "…";

        var sb = new StringBuilder();
        sb.AppendLine($"**Signature:** `{sig}`");
        sb.AppendLine($"**When:** {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.StandardName})");
        sb.AppendLine($"**Caught via:** {source}");
        sb.AppendLine();
        sb.AppendLine("### Environment");
        sb.AppendLine("```");
        sb.AppendLine($"App     : {AppVersion.Build}");
        sb.AppendLine($"OS      : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")} process)");
        sb.AppendLine($"Runtime : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Culture : {CultureInfo.CurrentCulture.Name} / UI {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"Memory  : working set {Environment.WorkingSet / (1024 * 1024)} MB");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Exception");
        sb.AppendLine("```");
        sb.AppendLine(Scrub(FormatExceptionChain(ex)));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Recent log (tail)");
        sb.AppendLine("```");
        sb.AppendLine(Scrub(AppLogger.GetRecentLogTail(200)));
        sb.AppendLine("```");

        return new CrashReport
        {
            Signature = sig,
            Title = $"[crash] {top}: {firstLine}",
            Body = sb.ToString(),
        };
    }

    private static CrashReport BuildFallbackReport(Exception? orig, string source, Exception reporterFailure)
    {
        var sig = orig != null ? ComputeSignature(orig) : "unknown";
        return new CrashReport
        {
            Signature = sig,
            Title = $"[crash] {(orig?.GetType().Name ?? "Unknown")} (reporter degraded)",
            Body = "Reporter hit an error while building the full report.\n\n" +
                   $"Original ({source}):\n```\n{Scrub(orig?.ToString() ?? "(null)")}\n```\n\n" +
                   $"Reporter failure:\n```\n{Scrub(reporterFailure.ToString())}\n```",
        };
    }

    /// <summary>Full exception chain incl. inner exceptions and stack traces.</summary>
    private static string FormatExceptionChain(Exception ex)
    {
        var sb = new StringBuilder();
        var e = ex;
        int depth = 0;
        while (e != null && depth < 8)
        {
            sb.AppendLine($"[{depth}] {e.GetType().FullName}: {e.Message}");
            if (!string.IsNullOrEmpty(e.StackTrace)) sb.AppendLine(e.StackTrace);
            e = e.InnerException;
            depth++;
            if (e != null) sb.AppendLine("--- inner ---");
        }
        return sb.ToString();
    }

    /// <summary>Stable hash from exception type + top normalized stack frames (method names only,
    /// line numbers/paths stripped so the same bug groups across builds).</summary>
    private static string ComputeSignature(Exception ex)
    {
        var root = ex;
        while (root.InnerException != null) root = root.InnerException;
        var frames = (root.StackTrace ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("at "))
            .Select(l => Regex.Replace(l, @"\sin\s.*$", "")) // drop " in file:line"
            .Take(5);
        var basis = root.GetType().FullName + "|" + string.Join("|", frames);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // ── Scrubbing ──────────────────────────────────────────────────────────────

    private static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Windows username in paths: C:\Users\<name>\ → C:\Users\%USER%\  (and forward-slash variant)
        text = Regex.Replace(text, @"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/\r\n]+", "$1%USER%", RegexOptions.IgnoreCase);
        var u = Environment.UserName;
        if (!string.IsNullOrEmpty(u))
            text = Regex.Replace(text, Regex.Escape(u), "%USER%", RegexOptions.IgnoreCase);
        // Known secret values from settings — redact verbatim occurrences.
        try
        {
            var s = SettingsService.Load();
            foreach (var secret in new[] { s.WikiPassword, s.TinifyApiKey, s.TinifyApiKey2, s.DiscordBotToken })
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 6)
                    text = text.Replace(secret, "«redacted»");
        }
        catch { /* settings unavailable — skip value-based redaction */ }
        // Generic token-shaped strings (GitHub PAT, bearer tokens) as a safety net.
        text = Regex.Replace(text, @"\bgh[pousr]_[A-Za-z0-9]{20,}\b", "«token»");
        text = Regex.Replace(text, @"\bgithub_pat_[A-Za-z0-9_]{20,}\b", "«token»");
        return text;
    }

    // ── GitHub reporting ───────────────────────────────────────────────────────

    private static async Task ReportAsync(CrashReport report)
    {
        var pat = LoadPat();
        if (string.IsNullOrEmpty(pat))
        {
            // No token embedded — keep the report locally so it isn't lost.
            QueueOffline(report);
            AppLogger.Warn("[CRASH] no embedded PAT — report saved to offline queue only");
            return;
        }

        if (!TryConsumeRateLimit(report.Signature, out _))
        {
            AppLogger.Info($"[CRASH] rate-limited (sig={report.Signature}); queued");
            QueueOffline(report);
            return;
        }

        try
        {
            await SendToGitHubAsync(report, pat).ConfigureAwait(false);
            AppLogger.Info($"[CRASH] reported to GitHub (sig={report.Signature})");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[CRASH] GitHub send failed ({ex.Message}); queued for retry");
            QueueOffline(report);
        }
    }

    private static async Task SendToGitHubAsync(CrashReport report, string pat)
    {
        // 1. Search for an OPEN issue carrying this signature → comment instead of duplicating.
        var existing = await FindOpenIssueBySignatureAsync(report.Signature, pat).ConfigureAwait(false);
        if (existing is int number)
        {
            await PostAsync(
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues/{number}/comments",
                pat, new { body = $"Happened again — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n{report.Body}" });
            return;
        }
        // 2. Otherwise create a new issue. Body carries an HTML-comment signature marker for dedup.
        await PostAsync(
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues",
            pat, new
            {
                title = report.Title,
                body = $"<!-- crash-signature: {report.Signature} -->\n\n{report.Body}",
                labels = new[] { IssueLabel },
            });
    }

    /// <summary>Finds an OPEN issue carrying this crash signature. Uses the Issues LIST endpoint
    /// (not /search/issues) on purpose: search has a multi-second indexing lag, so a just-created
    /// issue isn't found yet → duplicates. The list endpoint reflects writes immediately. We page
    /// through open auto-crash-labelled issues and match the signature marker in the body.</summary>
    private static async Task<int?> FindOpenIssueBySignatureAsync(string signature, string pat)
    {
        try
        {
            var marker = $"crash-signature: {signature}";
            for (int page = 1; page <= 5; page++) // up to 500 open crash issues
            {
                var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues" +
                          $"?state=open&labels={Uri.EscapeDataString(IssueLabel)}&per_page=100&page={page}";
                using var req = NewRequest(HttpMethod.Get, url, pat);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                var arr = doc.RootElement;
                if (arr.GetArrayLength() == 0) return null;
                foreach (var it in arr.EnumerateArray())
                {
                    // Skip PRs (the issues endpoint includes them; they have a pull_request field).
                    if (it.TryGetProperty("pull_request", out _)) continue;
                    var body = it.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                    if (body.Contains(marker))
                        return it.GetProperty("number").GetInt32();
                }
                if (arr.GetArrayLength() < 100) return null; // last page
            }
            return null;
        }
        catch { return null; }
    }

    private static async Task PostAsync(string url, string pat, object payload)
    {
        using var req = NewRequest(HttpMethod.Post, url, pat);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync().ConfigureAwait(false)}");
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string pat)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        req.Headers.UserAgent.ParseAdd("MergeMansionWikiTools-CrashReporter");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    // ── PAT loading ────────────────────────────────────────────────────────────

    private static string? _patCache;
    private static bool _patLoaded;

    /// <summary>Reads the PAT from <see cref="TokenFileName"/> next to the executable (cached).
    /// Returns "" when the file is missing/empty → crash reporting disabled (offline queue only).</summary>
    private static string LoadPat()
    {
        if (_patLoaded) return _patCache ?? "";
        _patLoaded = true;
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TokenFileName);
            if (File.Exists(path))
            {
                var token = File.ReadAllText(path).Trim();
                _patCache = string.IsNullOrWhiteSpace(token) ? "" : token;
            }
            else _patCache = "";
        }
        catch { _patCache = ""; }
        return _patCache ?? "";
    }

    // ── Rate-limit + dedup state ───────────────────────────────────────────────

    private sealed class RateState
    {
        public DateTime LastNewIssueUtc { get; set; } = DateTime.MinValue;
        public Dictionary<string, DateTime> SignatureLastSeenUtc { get; set; } = new();
    }

    /// <summary>Returns true if a NEW report send is allowed now. Same-signature crashes within the
    /// window are throttled; a brand-new signature always passes (so distinct bugs aren't lost).</summary>
    private static bool TryConsumeRateLimit(string signature, out bool dedupSameSig)
    {
        dedupSameSig = false;
        try
        {
            Directory.CreateDirectory(StateDir);
            var state = File.Exists(RateStatePath)
                ? JsonSerializer.Deserialize<RateState>(File.ReadAllText(RateStatePath)) ?? new RateState()
                : new RateState();
            var now = DateTime.UtcNow;

            if (state.SignatureLastSeenUtc.TryGetValue(signature, out var last))
            {
                dedupSameSig = true;
                if (now - last < NewIssueRateLimit) return false; // same crash too soon
            }
            else if (now - state.LastNewIssueUtc < NewIssueRateLimit)
            {
                // A different brand-new signature, but we just sent something — allow it anyway:
                // distinct bugs matter more than the global throttle. Only throttle repeats.
            }

            state.SignatureLastSeenUtc[signature] = now;
            state.LastNewIssueUtc = now;
            // Prune old signatures (keep file small)
            foreach (var k in state.SignatureLastSeenUtc.Where(kv => now - kv.Value > TimeSpan.FromDays(7))
                         .Select(kv => kv.Key).ToList())
                state.SignatureLastSeenUtc.Remove(k);
            File.WriteAllText(RateStatePath, JsonSerializer.Serialize(state));
            return true;
        }
        catch { return true; } // never block reporting due to state IO failure
    }

    // ── Offline queue ──────────────────────────────────────────────────────────

    private static void QueueOffline(CrashReport report)
    {
        try
        {
            Directory.CreateDirectory(QueueDir);
            var file = Path.Combine(QueueDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{report.Signature}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(report));
        }
        catch { /* nothing more we can do */ }
    }

    private static async Task FlushOfflineQueueAsync()
    {
        try
        {
            if (!Directory.Exists(QueueDir)) return;
            var pat = LoadPat();
            if (string.IsNullOrEmpty(pat)) return; // keep queued until a token exists
            foreach (var file in Directory.GetFiles(QueueDir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var report = JsonSerializer.Deserialize<CrashReport>(await File.ReadAllTextAsync(file).ConfigureAwait(false));
                    if (report == null) { File.Delete(file); continue; }
                    await SendToGitHubAsync(report, pat).ConfigureAwait(false);
                    File.Delete(file);
                    AppLogger.Info($"[CRASH] flushed queued report {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[CRASH] queued report {Path.GetFileName(file)} retry failed: {ex.Message}");
                    break; // stop on first failure (likely still offline) — retry next launch
                }
            }
        }
        catch { /* best-effort */ }
    }
}
