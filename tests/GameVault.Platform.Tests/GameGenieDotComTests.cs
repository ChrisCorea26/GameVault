using System.Net;
using GameVault.Platform.Online;

namespace GameVault.Platform.Tests;

/// <summary>
/// The GameGenie.com lookup (SPEC §7.8), against pages written for the test in the site's layout; nothing
/// from the real site is stored in the repository.
/// </summary>
public sealed class GameGenieDotComTests : IDisposable
{
    private const string Index = """
        <html><body><a href="/index.html">Home</a>
        <a href="star_courier.html">Star Courier</a>
        <a href="moon_miner_2.html">Moon Miner II</a>
        </body></html>
        """;

    private const string StarCourier = """
        <html><body><!-- TEXT --><pre id="content">
        PASIOALE        Start with 9 ships
        SXIOPO + AEKPIL         Infinite fuel &amp; speed
        Some note that isn't a code
        </pre></body></html>
        """;

    private readonly DirectoryInfo _cache = Directory.CreateTempSubdirectory("gamevault-gg-");
    private readonly FakeSite _site = new();

    public void Dispose() => _cache.Delete(recursive: true);

    private GameGenieDotCom Lookup() => new(new HttpClient(_site), _cache.FullName);

    [Fact]
    public async Task Finds_the_games_page_by_title_and_reads_its_codes()
    {
        var page = await Lookup().GetCodesAsync("Star Courier (USA)");

        page.Should().NotBeNull();
        page!.Title.Should().Be("Star Courier");
        page.PageUrl.Should().Be(new Uri("https://gamegenie.com/cheats/gamegenie/nes/star_courier.html"));
        page.Codes.Should().Equal(("PASIOALE", "Start with 9 ships"), ("SXIOPO + AEKPIL", "Infinite fuel & speed"));
        (await Lookup().GetCodesAsync("Moon Miner 2")).Should().NotBeNull("roman numerals match digits");
        (await Lookup().GetCodesAsync("Unknown Game")).Should().BeNull();
    }

    [Fact]
    public async Task Pages_are_cached_and_an_old_copy_serves_when_offline()
    {
        await Lookup().GetCodesAsync("Star Courier");
        var requests = _site.Requests;

        await Lookup().GetCodesAsync("Star Courier");
        _site.Requests.Should().Be(requests, "the site isn't asked again for the same game");

        foreach (var file in _cache.GetFiles())
        {
            file.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-60);
        }

        _site.Offline = true;
        (await Lookup().GetCodesAsync("Star Courier"))!.Codes.Should().HaveCount(2);
    }

    [Fact]
    public async Task No_connection_and_no_copy_is_an_error()
    {
        _site.Offline = true;
        var act = () => Lookup().GetCodesAsync("Star Courier");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private sealed class FakeSite : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            Requests++;
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/cheats/gamegenie/nes/index.html" => Index,
                "/cheats/gamegenie/nes/star_courier.html" => StarCourier,
                "/cheats/gamegenie/nes/moon_miner_2.html" => "<pre id=\"content\">GXOOZTEY   Slower timer</pre>",
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
