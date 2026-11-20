using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ForgeMission.Core.Resolution;
using ForgeMission.Cli.Contracts;
using ForgeMission.Cli.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Commands;

// forge login: platform sign-in via loopback auth-code + PKCE against Entra External ID.
// Hand-rolled public client (no MSAL — AOT-first): one localhost listener + one token call.
// Produces Entra tokens proving identity; the access token is then exchanged for a platform key.
internal static class PlatformLogin
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private const string UnknownUser = "(unknown user)";

    internal static async Task<int> RunAsync(IOptions<PlatformLoginOptions> options, IHttpClientFactory clients,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var http = clients.CreateClient(CliHttpClients.Login);
        var (verifier, challenge) = CreatePkcePair();
        var state = RandomUrlSafe(16);

        using var listener = StartLoopbackListener(out var redirectUri);
        var authorizeUrl = BuildAuthorizeUrl(settings, challenge, state, redirectUri);

        Console.WriteLine("Opening your browser to sign in…");
        Console.WriteLine($"(if it doesn't open, visit: {authorizeUrl})");
        OpenBrowser(authorizeUrl);

        var code = await WaitForCallbackAsync(listener, state, cancellationToken);
        if (code is null) return 1;

        var tokens = await ExchangeCodeAsync(http, settings, code, verifier, redirectUri, cancellationToken);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken)) return 1;

        // Exchange the Entra access token for a platform key (issuance endpoint on the control plane).
        var issued = await IssuePlatformKeyAsync(http, settings.PlatformEndpoint, tokens.AccessToken, cancellationToken);
        if (issued is null) return 1;

        var user = issued.Email ?? ReadUserLabel(tokens.IdToken);
        CredentialStore.SavePlatform(new PlatformCredential
        {
            Key = issued.Key,
            User = user,
            Endpoint = settings.PlatformEndpoint,
        });

        Console.WriteLine($"✓ signed in as {user}");
        Console.WriteLine($"  {FormatBalance(issued.BalanceMicroUsd)} credit · key stored in ~/.forge");
        return 0;
    }

    // --- forge whoami / forge logout -------------------------------------------------------

    // Show the signed-in user + live balance (reads the stored key, calls /me on its endpoint).
    internal static async Task<int> WhoAmIAsync(IHttpClientFactory clients, CancellationToken cancellationToken)
    {
        if (SignedInPlatform() is not { } platform) return 1;

        var me = await FetchMeAsync(clients.CreateClient(CliHttpClients.Login), platform.Endpoint, platform.Key,
            cancellationToken);
        if (me is null) return 1;

        Console.WriteLine($"Signed in as {me.Email ?? platform.User}");
        Console.WriteLine($"  {FormatBalance(me.BalanceMicroUsd)} credit · {platform.Endpoint}");
        return 0;
    }

    // The stored platform credential (GetPlatform already treats a blank key as none), or null
    // after telling the user to sign in.
    internal static PlatformCredential? SignedInPlatform()
    {
        var platform = CredentialStore.GetPlatform();
        if (platform is null)
            Console.Error.WriteLine("Not signed in. Run `forge login`.");
        return platform;
    }

    // Clear the stored platform key.
    public static int Logout()
    {
        if (CredentialStore.GetPlatform() is null)
        {
            Console.WriteLine("Not signed in.");
            return 0;
        }

        CredentialStore.ClearPlatform();
        Console.WriteLine("Signed out — platform key removed from ~/.forge.");
        return 0;
    }

    private static Task<MeResponse?> FetchMeAsync(HttpClient http, string endpoint, string key,
        CancellationToken cancellationToken)
    {
        var url = $"{endpoint.TrimEnd('/')}/me";
        return SendBearerAsync(http, HttpMethod.Get, url, key, PlatformLoginJsonContext.Default.MeResponse,
            async (resp, ct) =>
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Console.Error.WriteLine("Your platform key is no longer valid (expired or revoked). Run `forge login`.");
                    return;
                }
                var json = await resp.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"whoami failed ({(int)resp.StatusCode}) at {url}: {json}");
            },
            "whoami returned an unreadable response.", cancellationToken);
    }

    // Exchange the validated Entra access token for a long-lived platform key (fg_live_…).
    private static Task<IssueKeyResponse?> IssuePlatformKeyAsync(HttpClient http, string platformEndpoint, string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{platformEndpoint}/platform/keys";
        return SendBearerAsync(http, HttpMethod.Post, url, accessToken, PlatformLoginJsonContext.Default.IssueKeyResponse,
            async (resp, ct) =>
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"Key issuance failed ({(int)resp.StatusCode}) at {url}: {json}");
                var wwwAuth = resp.Headers.WwwAuthenticate.ToString();
                if (!string.IsNullOrEmpty(wwwAuth)) Console.Error.WriteLine($"  WWW-Authenticate: {wwwAuth}");
            },
            "Key issuance returned an unreadable response.", cancellationToken);
    }

    // Sends one bearer-authorised request and reads its JSON body. A failed status goes to
    // reportFailure and an unreadable body prints unreadableMessage; both return null.
    private static async Task<T?> SendBearerAsync<T>(HttpClient http, HttpMethod method, string url, string token,
        JsonTypeInfo<T> responseType, Func<HttpResponseMessage, CancellationToken, Task> reportFailure,
        string unreadableMessage, CancellationToken cancellationToken) where T : class
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            await reportFailure(resp, cancellationToken);
            return null;
        }

        try { return await resp.Content.ReadFromJsonAsync(responseType, cancellationToken); }
        catch (JsonException) { Console.Error.WriteLine(unreadableMessage); return null; }
    }

    private static string FormatBalance(long microUsd) => $"${microUsd / 1_000_000.0:0.00}";

    // --- PKCE -----------------------------------------------------------------------------

    private static (string Verifier, string Challenge) CreatePkcePair()
    {
        var verifier = RandomUrlSafe(32);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string RandomUrlSafe(int bytes) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    // --- Loopback listener ----------------------------------------------------------------

    private static HttpListener StartLoopbackListener(out string redirectUri)
    {
        var port = FreePort.Find();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        redirectUri = $"http://localhost:{port}";
        return listener;
    }

    // The sign-in deadline is one CTS linked to the caller's token: the deadline reports the timeout,
    // the caller's own cancellation (Ctrl+C) propagates.
    private static async Task<string?> WaitForCallbackAsync(HttpListener listener, string expectedState,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SignInTimeout);
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Timed out waiting for the browser sign-in ({SignInTimeout.TotalMinutes} minutes).");
                return null;
            }

            var query = ctx.Request.QueryString;
            var code = query["code"];
            var error = query["error"];

            if (code is null && error is null) { Respond(ctx, 404, "Not the sign-in callback."); continue; } // favicon etc.

            if (error is not null)
            {
                Respond(ctx, 200, "Sign-in was cancelled — you can close this tab.");
                Console.Error.WriteLine($"Sign-in error: {error}: {query["error_description"]}");
                return null;
            }

            if (query["state"] != expectedState)
            {
                Respond(ctx, 400, "State mismatch.");
                Console.Error.WriteLine("Sign-in error: state mismatch — try again.");
                return null;
            }

            Respond(ctx, 200, "✓ Signed in — return to your terminal.");
            return code;
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string message)
    {
        var body = Encoding.UTF8.GetBytes($"<html><body style=\"font-family:system-ui;margin:3em\"><h2>{message}</h2></body></html>");
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    // --- Authorize + token calls ------------------------------------------------------------

    private static string BuildAuthorizeUrl(PlatformLoginOptions settings, string challenge, string state, string redirectUri) =>
        $"{settings.Authority}/oauth2/v2.0/authorize" +
        $"?client_id={settings.ClientId}" +
        "&response_type=code&response_mode=query" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(settings.Scope)}" +
        $"&state={state}" +
        $"&code_challenge={challenge}&code_challenge_method=S256";

    private static async Task<TokenResponse?> ExchangeCodeAsync(HttpClient http, PlatformLoginOptions settings, string code, string verifier,
        string redirectUri, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["scope"] = settings.Scope,
        });

        using var resp = await http.PostAsync($"{settings.Authority}/oauth2/v2.0/token", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        TokenResponse? tokens = null;
        try { tokens = JsonSerializer.Deserialize(json, PlatformLoginJsonContext.Default.TokenResponse); }
        catch (JsonException) { /* non-JSON error body — handled below */ }

        if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(tokens?.AccessToken))
        {
            Console.Error.WriteLine($"Token exchange failed ({(int)resp.StatusCode}): {tokens?.Error ?? json}");
            if (tokens?.ErrorDescription is not null) Console.Error.WriteLine($"  {tokens.ErrorDescription}");
            return null;
        }

        return tokens;
    }

    // --- Helpers -----------------------------------------------------------------------------

    // Display label from the id_token payload. No signature check needed client-side: the token
    // arrived over the TLS channel we initiated (standard native-client posture).
    private static string ReadUserLabel(string? idToken)
    {
        if (idToken?.Split('.') is not [_, var payload, _]) return UnknownUser;
        try
        {
            var claims = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(payload), PlatformLoginJsonContext.Default.IdTokenClaims);
            return claims?.Email ?? claims?.PreferredUsername ?? claims?.Name ?? claims?.Sub ?? UnknownUser;
        }
        catch (Exception e) when (e is JsonException or FormatException) { return UnknownUser; }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            using var browser = OperatingSystem.IsMacOS() ? Process.Start("open", url)
                : OperatingSystem.IsWindows() ? Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })
                : Process.Start("xdg-open", url);
        }
        catch { /* the URL is printed as fallback */ }
    }
}

// forge login's sign-in settings, bound by the CLI host from its configuration: each property is
// read from the environment variable its key names. Public identifiers, not secrets (both appear in
// the sign-in URL); the variables point sign-in at another tenant/environment.
internal sealed class PlatformLoginOptions
{
    [ConfigurationKeyName("FORGE_AUTH_AUTHORITY")]
    public string Authority { get; set; } = "https://forgeids.ciamlogin.com/79c07ac3-4f45-4ea8-9701-94fd2ef1decd";

    // Forge CLI (dev) public client — forge-infra/dev/entra/create-cli-app-registration.sh
    [ConfigurationKeyName("FORGE_AUTH_CLIENT_ID")]
    public string ClientId { get; set; } = "33595d97-0296-4868-9217-dfab35faa314";

    // Request an access token for the cli.login scope (the issuance endpoint
    // validates this bearer), alongside the reserved OIDC scopes for id_token + refresh.
    [ConfigurationKeyName("FORGE_AUTH_SCOPE")]
    public string Scope { get; set; } = "api://4f8a95d6-2d41-416c-a1b9-9177ddec1227/cli.login openid profile email offline_access";

    // Base URL of the platform that issues the key (the forge-ui control plane). Override for local
    // dev against a locally-run ForgeUI.
    [ConfigurationKeyName("FORGE_PLATFORM_ENDPOINT")]
    public string PlatformEndpoint { get; set; } = "https://forge.katasec.com";

    /// <summary>Trims trailing slashes from the platform endpoint.</summary>
    public static void TrimPlatformEndpoint(PlatformLoginOptions options) =>
        options.PlatformEndpoint = options.PlatformEndpoint.TrimEnd('/');
}
