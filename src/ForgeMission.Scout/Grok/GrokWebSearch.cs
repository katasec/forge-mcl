using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace ForgeMission.Scout.Grok;

/// <summary>
/// Grok-backed <see cref="IWebSearch"/> via the xAI Responses API's server-side <c>web_search</c> tool.
/// Grok is an <b>answer engine</b>: it runs a server-side search+reason loop and returns a synthesized
/// answer plus URL citations (no raw page content). So this populates <see cref="WebSearchResult.Answer"/>
/// and fills <see cref="WebSearchResult.Sources"/> from the citation URLs.
/// <para>Direct <see cref="HttpClient"/> + STJ source-gen — the OpenAI SDK cannot emit a
/// <c>{"type":"web_search"}</c> server-side tool, and this keeps the path AOT-clean.</para>
/// </summary>
public sealed class GrokWebSearch(HttpClient http, string apiKey, string model = "grok-4.5") : IWebSearch
{
    private const string Endpoint = "https://api.x.ai/v1/responses";
    private const string ProviderName = "grok";

    // One request, one response object, mapped.
    public async Task<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken ct = default)
    {
        using var httpResp = await SendAsync(request, ct).ConfigureAwait(false);

        var parsed = await httpResp.Content
            .ReadFromJsonAsync(GrokJsonContext.Default.GrokResponse, ct)
            .ConfigureAwait(false)
            ?? throw new WebSearchException("xAI Responses API returned an empty/unparseable body.");

        return Map(parsed);
    }

    // Build + send the Responses request. Throws WebSearchException on a non-2xx.
    private async Task<HttpResponseMessage> SendAsync(WebSearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new WebSearchException("Query must be non-empty.");

        var tool = new GrokTool
        {
            Type = "web_search",
            Filters = request.AllowedDomains is { Count: > 0 } domains
                ? new GrokToolFilters { AllowedDomains = domains }
                : null,
        };
        var payload = new GrokRequest
        {
            Model = model,
            Input = [new GrokInputMessage { Role = "user", Content = request.Query }],
            Tools = [tool],
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload, GrokJsonContext.Default.GrokRequest),
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var httpResp = await http.SendAsync(httpReq, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (httpResp.IsSuccessStatusCode) return httpResp;

        using (httpResp)
        {
            var errBody = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var snippet = errBody.Length > 500 ? errBody[..500] : errBody;
            throw new WebSearchException($"xAI Responses API returned {(int)httpResp.StatusCode}: {snippet}");
        }
    }

    // Pull the synthesized answer + citation URLs off the single "message" item in output[].
    private static WebSearchResult Map(GrokResponse resp)
    {
        var answer = new StringBuilder();
        var sources = new List<SourceRef>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var texts = (resp.Output ?? [])
            .Where(item => item.Type == "message" && item.Content is not null)
            .SelectMany(item => item.Content!)
            .Where(content => content.Type == "output_text");

        foreach (var content in texts)
        {
            if (!string.IsNullOrEmpty(content.Text))
                answer.Append(content.Text);

            // Grok's annotation "title" is a numeric citation label ("1"), not a page title —
            // leave SourceRef.Title null rather than mislabel. Provider tag travels with each.
            foreach (var ann in content.Annotations ?? [])
                if (ann.Type == "url_citation" && !string.IsNullOrEmpty(ann.Url) && seenUrls.Add(ann.Url))
                    sources.Add(new SourceRef(ann.Url, Title: null, ProviderName));
        }

        return new WebSearchResult(
            Provider: ProviderName,
            Answer: answer.Length > 0 ? answer.ToString() : null,
            Sources: sources);
    }
}
