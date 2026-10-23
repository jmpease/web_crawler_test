using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Crawler.IntegrationTests;

/// <summary>
/// A fake website behind an HttpMessageHandler, so crawls never touch the network. Unknown URLs return 404.
/// Records every requested URL in order.
/// </summary>
public sealed class StubSite : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];
    private readonly ConcurrentQueue<string> _requests = new();

    public IReadOnlyList<string> Requests => [.. _requests];

    public string? LastUserAgent { get; private set; }

    /// <summary>Serves each file in <paramref name="directory"/> under <paramref name="origin"/>; index.html is "/".</summary>
    public static StubSite FromDirectory(string origin, string directory)
    {
        var site = new StubSite();
        foreach (var file in Directory.GetFiles(directory))
        {
            var name = Path.GetFileName(file);
            var path = name == "index.html" ? "/" : "/" + name;
            var mediaType = Path.GetExtension(file) == ".html" ? "text/html" : "application/pdf";
            site.Respond(origin + path, () => Content(File.ReadAllBytes(file), mediaType));
        }

        return site;
    }

    public StubSite Html(string url, string html) =>
        Respond(url, () => Content(Encoding.UTF8.GetBytes(html), "text/html"));

    public StubSite Redirect(string url, string location, HttpStatusCode status = HttpStatusCode.MovedPermanently) =>
        Respond(url, () => new HttpResponseMessage(status) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } });

    public StubSite Status(string url, HttpStatusCode status) => Respond(url, () => new HttpResponseMessage(status));

    public StubSite Respond(string url, Func<HttpResponseMessage> response)
    {
        _routes[url] = response;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        _requests.Enqueue(url);
        LastUserAgent = request.Headers.UserAgent.ToString();
        var response = _routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Content(byte[] body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" } } },
    };
}
