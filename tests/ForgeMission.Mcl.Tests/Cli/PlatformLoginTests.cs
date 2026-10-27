using System.Collections.Specialized;
using System.Text.Json;
using System.Web;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge login's sign-in settings, pinned through the process: by default it signs in against the dev
// tenant, client and scope and fetches the key from the hosted platform; FORGE_AUTH_AUTHORITY,
// FORGE_AUTH_CLIENT_ID, FORGE_AUTH_SCOPE and FORGE_PLATFORM_ENDPOINT (trailing slashes trimmed)
// override them. Each test reads the authorize URL forge prints and answers its loopback callback;
// the harness's no-op browser keeps a real one closed.
public sealed class PlatformLoginTests
{
    private const string DefaultAuthority = "https://forgeids.ciamlogin.com/79c07ac3-4f45-4ea8-9701-94fd2ef1decd";
    private const string DefaultClientId = "33595d97-0296-4868-9217-dfab35faa314";
    private const string DefaultScope = "api://4f8a95d6-2d41-416c-a1b9-9177ddec1227/cli.login openid profile email offline_access";
    private const string AuthorizePath = "/oauth2/v2.0/authorize";
    private const string BrowserCallback = "browser callback";

    [Fact]
    public async Task By_default_login_signs_in_against_the_dev_tenant_client_and_scope()
    {
        await using var harness = ForgeCliHarness.Start();

        var (result, authorize) = await LoginAsync(harness, _ => "error=access_denied&error_description=cancelled");

        Assert.Equal(DefaultAuthority + AuthorizePath, authorize.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal((DefaultClientId, DefaultScope), (query["client_id"], query["scope"]));
        Assert.Equal((1, "Sign-in error: access_denied: cancelled"), (result.ExitCode, result.StandardError.Trim()));
        Assert.Empty(harness.Proxy.Attempts);
    }

    [Fact]
    public async Task By_default_login_fetches_the_key_from_the_hosted_platform()
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(SignInResponses);
        harness.Variables["FORGE_AUTH_AUTHORITY"] = stub.Address + "tenant";

        var (result, _) = await LoginAsync(harness, state => $"code=test-code&state={state}");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["POST /tenant/oauth2/v2.0/token"], stub.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"));
        Assert.Contains(new ProxyAttempt("CONNECT", "forge.katasec.com:443"), harness.Proxy.Attempts);
        Assert.False(File.Exists(CredentialsPath(harness)));
    }

    [Fact]
    public async Task The_sign_in_variables_override_the_tenant_client_scope_and_platform()
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(SignInResponses);
        var platform = stub.Address.ToString().TrimEnd('/');
        harness.Variables["FORGE_AUTH_AUTHORITY"] = platform + "/tenant";
        harness.Variables["FORGE_AUTH_CLIENT_ID"] = "test-client";
        harness.Variables["FORGE_AUTH_SCOPE"] = "test.scope openid";
        harness.Variables["FORGE_PLATFORM_ENDPOINT"] = platform + "//";

        var (result, authorize) = await LoginAsync(harness, state => $"code=test-code&state={state}");

        Assert.Equal(platform + "/tenant" + AuthorizePath, authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(("test-client", "test.scope openid"), Settings(HttpUtility.ParseQueryString(authorize.Query)));
        Assert.Equal((0, $"✓ signed in as pilot@test{Environment.NewLine}  $2.50 credit · key stored in ~/.forge{Environment.NewLine}"),
            (result.ExitCode, result.StandardOutput));
        var (token, key) = Assert.Single(stub.Requests.Chunk(2).Select(pair => (pair[0], pair[1])));
        Assert.Equal(("POST /tenant/oauth2/v2.0/token", ("test-client", "test.scope openid")),
            ($"{token.Method} {token.PathAndQuery}", Settings(HttpUtility.ParseQueryString(token.Body))));
        Assert.Equal(("POST /platform/keys", "Bearer entra-access-token"),
            ($"{key.Method} {key.PathAndQuery}", key.Headers["Authorization"]));
        Assert.Equal(platform, SavedEndpoint(harness));
        Assert.Empty(harness.Proxy.Attempts);
    }

    // Ctrl+C while login waits (for the browser's callback, the token exchange or the key issuance)
    // exits 130, prints nothing more and stores no key.
    [SkippableTheory]
    [InlineData(BrowserCallback)]
    [InlineData("/tenant/oauth2/v2.0/token")]
    [InlineData("/platform/keys")]
    public async Task Ctrl_C_while_login_waits_exits_130(string waitingFor)
    {
        Skip.If(OperatingSystem.IsWindows(), "SIGINT is POSIX.");
        await using var harness = ForgeCliHarness.Start();
        var (reached, release) = (new TaskCompletionSource(), new TaskCompletionSource());
        var stub = await harness.StartStubAsync(request =>
        {
            if (request.PathAndQuery != waitingFor) return SignInResponses(request);
            reached.TrySetResult();
            release.Task.Wait();
            return new StubResponse(500, "released");
        });
        var platform = stub.Address.ToString().TrimEnd('/');
        harness.Variables["FORGE_AUTH_AUTHORITY"] = platform + "/tenant";
        harness.Variables["FORGE_PLATFORM_ENDPOINT"] = platform;

        CliResult result;
        try
        {
            result = await harness.InterruptWhileAsync(waitingFor == BrowserCallback
                ? ReadAuthorizeUrlAsync
                : async stdout => { await SignInInBrowserAsync(stdout, state => $"code=test-code&state={state}"); await reached.Task; },
                "login");
        }
        finally { release.TrySetResult(); }

        Assert.Equal((130, "", ""), (result.ExitCode, result.StandardOutput, result.StandardError));
        Assert.False(File.Exists(CredentialsPath(harness)));
        Assert.Empty(harness.Proxy.Attempts);
    }

    // Runs `forge login` and signs in through its browser callback (SignInInBrowserAsync).
    private static async Task<(CliResult Result, Uri Authorize)> LoginAsync(ForgeCliHarness harness, Func<string, string> callback)
    {
        Uri? authorize = null;
        var result = await harness.ConverseAsync(async (_, stdout) => authorize = await SignInInBrowserAsync(stdout, callback), "login");
        return (result, authorize!);
    }

    // Reads the authorize URL forge login prints and answers its loopback callback with the query
    // `callback` builds from the URL's state.
    private static async Task<Uri> SignInInBrowserAsync(StreamReader stdout, Func<string, string> callback)
    {
        var authorize = await ReadAuthorizeUrlAsync(stdout);
        var query = HttpUtility.ParseQueryString(authorize.Query);
        await AnswerCallbackAsync(query["redirect_uri"]!, callback(query["state"]!));
        return authorize;
    }

    private static async Task<Uri> ReadAuthorizeUrlAsync(StreamReader stdout)
    {
        const string prefix = "(if it doesn't open, visit: ";
        while (await stdout.ReadLineAsync() is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return new Uri(line[prefix.Length..^1]);
        }
        throw new InvalidOperationException("forge login exited without printing the authorize URL.");
    }

    private static async Task AnswerCallbackAsync(string redirectUri, string query)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var response = await http.GetAsync($"{redirectUri}/?{query}");
    }

    private static StubResponse SignInResponses(StubRequest request) => request.PathAndQuery switch
    {
        "/tenant/oauth2/v2.0/token" => new StubResponse(200, """{"access_token":"entra-access-token"}"""),
        "/platform/keys" => new StubResponse(200, """{"key":"fg_live_test","email":"pilot@test","balanceMicroUsd":2500000}"""),
        _ => new StubResponse(404, "{}"),
    };

    private static (string?, string?) Settings(NameValueCollection query) => (query["client_id"], query["scope"]);

    private static string CredentialsPath(ForgeCliHarness harness) => Path.Combine(harness.Home, ".forge", "credentials.json");

    private static string? SavedEndpoint(ForgeCliHarness harness)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(CredentialsPath(harness)));
        return saved.RootElement.GetProperty("platform").GetProperty("endpoint").GetString();
    }
}
