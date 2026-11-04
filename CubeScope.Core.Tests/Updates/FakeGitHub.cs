using System.Net;
using System.Text;

namespace CubeScope.Core.Tests.Updates;

/// <summary>Stands in for GitHub: answers by exact address, anything not registered is a 404, and
/// records what was asked.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly Dictionary<Uri, Func<HttpResponseMessage>> _answers = [];

    public List<(Uri Uri, string UserAgent, string Accept)> Requests { get; } = [];

    public void Respond(string url, Func<HttpResponseMessage> answer) => _answers[new Uri(url)] = answer;

    public void Bytes(string url, byte[] body) =>
        Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    public void Text(string url, string body) => Bytes(url, Encoding.UTF8.GetBytes(body));

    public void Redirect(string url, string location) => Respond(url, () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    });

    public void Fail(string url, Exception failure) => Respond(url, () => throw failure);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add((uri, request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString()));
        return Task.FromResult(_answers.TryGetValue(uri, out var answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Reports on the calling thread, so that a test sees every report before the call returns.</summary>
internal sealed class SynchronousProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
