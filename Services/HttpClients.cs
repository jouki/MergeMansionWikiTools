using System.Net;
using System.Net.Http;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Sdílené HttpClient instance pro celou aplikaci. KAŽDÝ klient MUSÍ mít User-Agent —
/// Fandom/Cloudflare vrací 403 "error code: 1010" (non-JSON body, rozbije JsonDocument.Parse)
/// na requesty bez UA. Viz _CONTEXT\Wiki\ApiAccess.md.
///
/// Pravidla:
///   * Auth tokeny (Discord Bot, GitHub PAT) NIKDY nedávat do DefaultRequestHeaders sdíleného
///     klienta — vždy per-request přes HttpRequestMessage.Headers.
///   * Per-request headery (User-Agent, Accept, …) nastavené na HttpRequestMessage mají přednost —
///     DefaultRequestHeaders se doplní jen, pokud request daný header nemá.
///   * Kratší per-call timeout řešit přes CancellationTokenSource(timeout), ne vlastním klientem.
///   * Klienti vyžadující CookieContainer per session (wiki login, APKPure download) zůstávají
///     lokální v příslušném service — cookies nelze sdílet napříč sezeními.
/// </summary>
public static class HttpClients
{
    public const string UserAgent = "MergeMansionWikiTools/1.0";

    /// <summary>Obecný klient (GitHub API, Uptodown, nightly.link, …). Timeout 100 s (default).</summary>
    public static HttpClient Default { get; } = Create();

    /// <summary>Fandom MediaWiki API (neautentizované GET/parse). Timeout 100 s (default).
    /// Autentizované edity používají per-session klienta z WikiMappingService.CreateAuthenticatedClientAsync
    /// (vlastní CookieContainer).</summary>
    public static HttpClient WikiApi { get; } = Create();

    /// <summary>Discord Bot API — VYHRAZENÝ pro Discord services (uploady příloh mohou trvat).
    /// Timeout 120 s. Bot token se předává per-request (HttpRequestMessage.Headers.Authorization),
    /// nikdy přes DefaultRequestHeaders.</summary>
    public static HttpClient Discord { get; } = Create(timeoutSeconds: 120);

    /// <summary>Dlouhé streamované downloady (release ZIP, Discord dump .7z přílohy).
    /// Timeout 15 min — HttpClient.Timeout platí i pro čtení response streamu
    /// (ResponseHeadersRead), takže download velkého souboru NESMÍ běžet přes 100s klienty.</summary>
    public static HttpClient LongDownload { get; } = Create(timeoutSeconds: 900, autoDecompress: true);

    private static HttpClient Create(int? timeoutSeconds = null, bool autoDecompress = false)
    {
        var handler = new HttpClientHandler { UseProxy = false };
        if (autoDecompress)
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        if (timeoutSeconds is int seconds)
            client.Timeout = TimeSpan.FromSeconds(seconds);
        return client;
    }
}
