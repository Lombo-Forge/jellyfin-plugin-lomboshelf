using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.LomboShelf.Api;
using Jellyfin.Plugin.LomboShelf.Configuration;
using Jellyfin.Plugin.LomboShelf.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LomboShelf.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lshelf-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string name, string content)
    {
        var f = System.IO.Path.Combine(Path, name);
        File.WriteAllText(f, content);
        return f;
    }

    public void Dispose() => Directory.Delete(Path, true);
}

internal sealed class FakeConfig : IConfigSource
{
    public PluginConfiguration Current { get; set; } = new();
}

internal sealed class FakeItems : IItemAccess
{
    public Dictionary<Guid, ItemFacts> Known { get; } = new();

    public Dictionary<Guid, string> Names { get; } = new();

    public ItemFacts? Find(Guid itemId, Guid userId) => Known.TryGetValue(itemId, out var f) ? f : null;

    public string? UserName(Guid userId) => Names.TryGetValue(userId, out var n) ? n : null;
}

internal sealed class FakeFarewell : IFarewellClient
{
    public FarewellReply Reply { get; set; } = new(200, "kept");
    public List<(string Url, string Token, string Id)> Calls { get; } = new();
    public List<string?> Names { get; } = new();

    public Task<FarewellReply> KeepAsync(string baseUrl, string token, string itemIdHex, CancellationToken ct, string? by = null)
    {
        Calls.Add((baseUrl, token, itemIdHex));
        Names.Add(by);
        return Task.FromResult(Reply);
    }
}

internal sealed class FakeVisa : IVisaClient
{
    public VisaReply StatusReply { get; set; } = new(200, null, new TravelState(null, null, 14));
    public VisaReply OpenReply { get; set; } = new(200, "window opened");
    public List<(string Url, string Token, string User, int Days)> Calls { get; } = new();

    public Task<VisaReply> StatusAsync(string baseUrl, string token, string userIdHex, CancellationToken ct)
    {
        Calls.Add((baseUrl, token, userIdHex, 0));
        return Task.FromResult(StatusReply);
    }

    public Task<VisaReply> OpenAsync(string baseUrl, string token, string userIdHex, int days, CancellationToken ct)
    {
        Calls.Add((baseUrl, token, userIdHex, days));
        return Task.FromResult(OpenReply);
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
    public HttpRequestMessage? Seen { get; private set; }
    public string? SeenBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Seen = request;
        SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return Respond(request);
    }
}

public class IntegrationTests : IDisposable
{
    private static readonly Guid Movie = Guid.Parse("0123456789abcdef0123456789abcdef");
    private static readonly Guid Hidden = Guid.Parse("fedcba9876543210fedcba9876543210");
    private const string MovieHex = "0123456789abcdef0123456789abcdef";
    private static readonly Guid User1 = Guid.Parse("11111111111111111111111111111111");

    private readonly TempDir _dir = new();
    private readonly FakeConfig _cfg = new();
    private readonly FakeItems _items = new();
    private readonly FakeFarewell _farewell = new();
    private readonly KeepGuard _guard;
    private readonly TravelGuard _travelGuard;
    private readonly FakeVisa _visa = new();
    private readonly ContractReader _reader = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public IntegrationTests()
    {
        _guard = new KeepGuard(() => _now);
        _travelGuard = new TravelGuard(() => _now);
    }

    public void Dispose() => _dir.Dispose();

    private IntegrationController Controller(string? body = null, Guid? user = null)
    {
        var http = new DefaultHttpContext();
        var id = user ?? User1;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", id.ToString("N")) }, "test"));
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
        return new IntegrationController(_cfg, _items, _reader, _farewell, _guard, _visa, _travelGuard, NullLogger<IntegrationController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static string Text(IActionResult r) => Assert.IsType<ContentResult>(r).Content!;

    private static string Tiers(string units, int version = 1) => $"{{\"version\":{version},\"updated\":1,\"units\":[{units}]}}";

    private void StorageOn(string tiersJson, bool adminsOnly = false, string prefix = "/media/")
    {
        _cfg.Current = new PluginConfiguration
        {
            StorageTagsEnabled = true, StorageTagsAdminsOnly = adminsOnly, TiersPathPrefix = prefix,
            TiersFile = _dir.Write("tiers.json", tiersJson)
        };
    }

    private void KeepOn(string leavingJson)
    {
        _cfg.Current = new PluginConfiguration
        {
            KeepEnabled = true, LeavingFile = _dir.Write("leaving.json", leavingJson),
            FarewellUrl = "http://farewell.example:8098", FarewellToken = "secret-token-value"
        };
    }

    private static string Leaving(string id, string days = "3", string canKeep = "true") =>
        $"{{\"version\":1,\"updated\":1,\"entries\":[{{\"id\":\"{id}\",\"title\":\"A\",\"days_left\":{days},\"can_keep\":{canKeep}}}]}}";

    // ---------- sanitizer ----------

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("http://user:pw@host")]
    [InlineData("http://host/path")]
    [InlineData("http://host?x=1")]
    [InlineData("http://host#a")]
    [InlineData("host:8098")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://ho st")]
    public void Sanitizer_RejectsBadFarewellUrls(string url)
    {
        var c = new PluginConfiguration { FarewellUrl = url };
        Assert.Contains(ConfigSanitizer.Sanitize(c), e => e.StartsWith("Farewell address"));
    }

    [Theory]
    [InlineData("http://farewell:8098", "http://farewell:8098")]
    [InlineData("https://farewell.example/", "https://farewell.example")]
    public void Sanitizer_AcceptsAndNormalisesFarewellUrls(string url, string expected)
    {
        var c = new PluginConfiguration { FarewellUrl = url };
        Assert.Empty(ConfigSanitizer.Sanitize(c));
        Assert.Equal(expected, c.FarewellUrl);
    }

    [Theory]
    [InlineData("relative/tiers.json")]
    [InlineData("tiers.json")]
    [InlineData("/data/../etc/tiers.json")]
    [InlineData("/data/ti\0ers.json")]
    public void Sanitizer_RejectsBadFilePaths(string path)
    {
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { TiersFile = path }), e => e.StartsWith("Tiers file"));
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { LeavingFile = path }), e => e.StartsWith("Leaving file"));
    }

    [Fact]
    public void Sanitizer_AcceptsAbsolutePaths()
    {
        Assert.Empty(ConfigSanitizer.Sanitize(new PluginConfiguration { TiersFile = "/config/tiers.json", LeavingFile = "C:\\data\\leaving.json" }));
    }

    [Theory]
    [InlineData("/media/../x")]
    [InlineData("..")]
    public void Sanitizer_RejectsPrefixWithDotDot(string prefix)
    {
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { TiersPathPrefix = prefix }), e => e.StartsWith("Tiers path prefix"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("nul\0x")]
    public void Sanitizer_RejectsBadTokens(string token)
    {
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { FarewellToken = token }), e => e.StartsWith("Farewell token"));
    }

    [Fact]
    public void Sanitizer_RejectsAnOverLongTokenButAcceptsTheLimit()
    {
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { FarewellToken = new string('a', 257) }), e => e.StartsWith("Farewell token"));
        Assert.Empty(ConfigSanitizer.Sanitize(new PluginConfiguration { FarewellToken = new string('a', 256) }));
    }

    [Fact]
    public void Sanitizer_RejectsFeaturesWithoutTheirSettings()
    {
        Assert.Single(ConfigSanitizer.Sanitize(new PluginConfiguration { StorageTagsEnabled = true }));
        Assert.Equal(3, ConfigSanitizer.Sanitize(new PluginConfiguration { KeepEnabled = true }).Count);
        Assert.Empty(ConfigSanitizer.Sanitize(new PluginConfiguration
        {
            StorageTagsEnabled = true, TiersFile = "/t.json", KeepEnabled = true, LeavingFile = "/l.json", FarewellUrl = "http://f:1", FarewellToken = "abc"
        }));
    }

    [Fact]
    public void IntegrationsAreOffByDefault()
    {
        var c = new PluginConfiguration();
        Assert.False(c.StorageTagsEnabled);
        Assert.True(c.StorageTagsAdminsOnly);
        Assert.False(c.KeepEnabled);
        Assert.False(FeatureGate.StorageTags(c));
        Assert.False(FeatureGate.Keep(c));
    }

    [Fact]
    public void OnlyTheThreeSwitchesReachTheBrowser()
    {
        var c = new PluginConfiguration
        {
            StorageTagsEnabled = true, TiersFile = "/secret/tiers.json", TiersPathPrefix = "/media/", KeepEnabled = true,
            LeavingFile = "/secret/leaving.json", FarewellUrl = "http://farewell.example:8098", FarewellToken = "secret-token-value",
            TravelEnabled = true, VisaUrl = "http://visa.example:8097", VisaToken = "visa-secret-value"
        };
        var json = IndexTransform.BuildConfigJson(c);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("farewell", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("visa", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/media/", json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("features").GetProperty("storageTags").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("features").GetProperty("keep").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("features").GetProperty("travel").GetBoolean());
    }

    // ---------- the token ----------

    [Fact]
    public void Token_IsNeverReturnedToTheSettingsPage()
    {
        var c = new PluginConfiguration { FarewellToken = "secret-token-value" };
        var red = ConfigSanitizer.Redacted(c);
        Assert.Equal(string.Empty, red.FarewellToken);
        Assert.True(red.FarewellTokenSet);
        Assert.DoesNotContain("secret-token-value", JsonSerializer.Serialize(red));
        Assert.Equal("secret-token-value", c.FarewellToken);   // the stored object is untouched
        Assert.False(ConfigSanitizer.Redacted(new PluginConfiguration()).FarewellTokenSet);
    }

    [Fact]
    public void Token_BlankKeepsClearFlagClears()
    {
        // ConfigController (needs the Jellyfin host) calls exactly this function.
        Assert.Equal("old", ConfigSanitizer.MergeToken(new PluginConfiguration { FarewellToken = "" }, "old"));
        Assert.Equal("old", ConfigSanitizer.MergeToken(new PluginConfiguration { FarewellToken = "   " }, "old"));
        Assert.Equal("new", ConfigSanitizer.MergeToken(new PluginConfiguration { FarewellToken = " new " }, "old"));
        Assert.Equal(string.Empty, ConfigSanitizer.MergeToken(new PluginConfiguration { FarewellToken = "", ClearFarewellToken = true }, "old"));
        Assert.Equal(string.Empty, ConfigSanitizer.MergeToken(new PluginConfiguration { FarewellToken = "new", ClearFarewellToken = true }, "old"));
        Assert.Equal(string.Empty, ConfigSanitizer.MergeToken(new PluginConfiguration(), null));
    }

    [Fact]
    public void SavedXmlHasNoTokenFlags()
    {
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var w = new StringWriter();
        xml.Serialize(w, new PluginConfiguration { FarewellTokenSet = true, ClearFarewellToken = true });
        Assert.DoesNotContain("FarewellTokenSet", w.ToString());
        Assert.DoesNotContain("ClearFarewellToken", w.ToString());
    }

    // ---------- path resolution ----------

    private static readonly IReadOnlyDictionary<string, string> Units = new Dictionary<string, string>
    {
        ["movies/Jaws (1975)"] = "ssd", ["tv/Some Show/Season 2"] = "hdd", ["tv/Some Show"] = "ssd"
    };

    [Theory]
    [InlineData("/media/movies/Jaws (1975)/Jaws (1975).mkv", "/media/", "ssd")]
    [InlineData("/media/movies/Jaws (1975)/Jaws (1975).mkv", "/media", "ssd")]
    [InlineData("/media/movies/Jaws (1975)", "/media/", "ssd")]
    [InlineData("/media/tv/Some Show/Season 2/S02E01.mkv", "/media/", "hdd")]   // the season folder beats the show folder
    [InlineData("/media/tv/Some Show/Season 1/S01E01.mkv", "/media/", "ssd")]   // no season row: a parent folder matches
    [InlineData("movies\\Jaws (1975)\\a.mkv", "", "ssd")]
    [InlineData("/movies//Jaws (1975)/./a.mkv", "", "ssd")]
    public void Resolve_FindsTheUnit(string path, string prefix, string tier)
    {
        Assert.Equal(tier, StorageResolver.Resolve(path, prefix, Units));
    }

    [Theory]
    [InlineData("/media/movies/Other (2000)/a.mkv", "/media/")]
    [InlineData("/other/movies/Jaws (1975)/a.mkv", "/media/")]          // wrong prefix
    [InlineData("/mediax/movies/Jaws (1975)/a.mkv", "/media/")]         // prefix must end at a folder boundary
    [InlineData("/media/movies/Jaws (1975)/../x/a.mkv", "/media/")]
    [InlineData("/media/movies/Jaws (1975)/a.mkv", "/media/../x")]
    [InlineData("/media/movies/jaws (1975)/a.mkv", "/media/")]          // case matters
    [InlineData("/media/movies", "/media/")]                           // the parent of a unit is not the unit
    [InlineData("", "/media/")]
    [InlineData(null, "/media/")]
    public void Resolve_NoMatch(string? path, string prefix)
    {
        Assert.Null(StorageResolver.Resolve(path, prefix, Units));
    }

    [Fact]
    public void Storage_MovieAndEpisodeAndSeasonResolve()
    {
        StorageOn(Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"},{\"path\":\"tv/Some Show/Season 2\",\"tier\":\"hdd\"}"));
        var ep = Guid.NewGuid();
        var season = Guid.NewGuid();
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/Jaws.mkv", true);
        _items.Known[ep] = new ItemFacts(ItemKind.Episode, "/media/tv/Some Show/Season 2/S02E01.mkv", true);
        _items.Known[season] = new ItemFacts(ItemKind.Season, "/media/tv/Some Show/Season 2", true);
        Assert.Equal("{\"tier\":\"ssd\"}", Text(Controller().Storage(Movie.ToString())));
        Assert.Equal("{\"tier\":\"hdd\"}", Text(Controller().Storage(ep.ToString("N"))));
        Assert.Equal("{\"tier\":\"hdd\"}", Text(Controller().Storage(season.ToString("D"))));
    }

    [Fact]
    public void Storage_Answers204ForEverythingElse()
    {
        StorageOn(Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}"));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Nothing/a.mkv", true);
        var other = Guid.NewGuid();
        _items.Known[other] = new ItemFacts(ItemKind.Other, "/media/movies/Jaws (1975)/a.mkv", true);
        var traversal = Guid.NewGuid();
        _items.Known[traversal] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/../../etc/a.mkv", true);
        Assert.IsType<NoContentResult>(Controller().Storage(Movie.ToString("N")));          // no matching unit
        Assert.IsType<NoContentResult>(Controller().Storage(other.ToString("N")));          // not a movie, season or episode
        Assert.IsType<NoContentResult>(Controller().Storage(traversal.ToString("N")));      // ".."
        Assert.IsType<NoContentResult>(Controller().Storage(Hidden.ToString("N")));         // unknown or not visible to this user
        Assert.IsType<NoContentResult>(Controller().Storage("not-an-id"));
        Assert.IsType<NoContentResult>(Controller().Storage("../etc/passwd"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"units\":[{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}]}")]
    [InlineData("{\"units\":[{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}]}")]
    [InlineData("{\"version\":\"1\",\"units\":[]}")]
    [InlineData("[]")]
    [InlineData("")]
    public void Storage_UnreadableOrWrongVersionFileMeansNoData(string content)
    {
        StorageOn(content);
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/a.mkv", true);
        Assert.IsType<NoContentResult>(Controller().Storage(Movie.ToString("N")));
    }

    [Fact]
    public void Storage_MissingOversizeAndUnknownTierEntries()
    {
        StorageOn(Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}"));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/a.mkv", true);
        Assert.IsType<ContentResult>(Controller().Storage(Movie.ToString("N")));

        File.Delete(_cfg.Current.TiersFile);
        Assert.IsType<NoContentResult>(Controller().Storage(Movie.ToString("N")));

        // Over 4 MB: refused without being parsed.
        var big = Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}") + new string(' ', (int)ContractReader.MaxBytes);
        File.WriteAllText(_cfg.Current.TiersFile, big);
        Assert.IsType<NoContentResult>(Controller().Storage(Movie.ToString("N")));

        // A tier that is not ssd or hdd is ignored.
        File.WriteAllText(_cfg.Current.TiersFile, Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"tape\"}"));
        Assert.IsType<NoContentResult>(Controller().Storage(Movie.ToString("N")));
    }

    [Fact]
    public void Storage_FileChangeIsPickedUpAndNeverThrows()
    {
        StorageOn(Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}"));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/a.mkv", true);
        Assert.Equal("{\"tier\":\"ssd\"}", Text(Controller().Storage(Movie.ToString("N"))));
        File.WriteAllText(_cfg.Current.TiersFile, Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"hdd\"}"));
        File.SetLastWriteTimeUtc(_cfg.Current.TiersFile, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal("{\"tier\":\"hdd\"}", Text(Controller().Storage(Movie.ToString("N"))));
        _cfg.Current.TiersFile = _dir.Path;   // a folder, not a file
        Assert.IsType<NoContentResult>(Controller().Storage(Movie.ToString("N")));
    }

    [Fact]
    public void Storage_NonAdminGets204WhenAdminsOnly_AdminGetsTheTier()
    {
        StorageOn(Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}"), adminsOnly: true);
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/a.mkv", false);
        Assert.IsType<NoContentResult>(Controller().Storage(Movie.ToString("N")));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/a.mkv", true);
        Assert.Equal("{\"tier\":\"ssd\"}", Text(Controller().Storage(Movie.ToString("N"))));
        _cfg.Current.StorageTagsAdminsOnly = false;
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/a.mkv", false);
        Assert.Equal("{\"tier\":\"ssd\"}", Text(Controller().Storage(Movie.ToString("N"))));
    }

    [Fact]
    public async Task FeaturesOff_EveryRouteAnswers404()
    {
        var c = Controller("{\"itemId\":\"" + MovieHex + "\"}");
        Assert.IsType<NotFoundResult>(c.Storage(MovieHex));
        Assert.IsType<NotFoundResult>(c.Leaving(MovieHex));
        Assert.IsType<NotFoundResult>(await c.Keep(CancellationToken.None));
        // Switched on but a required setting missing (hand-edited XML) counts as off.
        _cfg.Current = new PluginConfiguration { StorageTagsEnabled = true, KeepEnabled = true, LeavingFile = "/x.json" };
        Assert.IsType<NotFoundResult>(c.Storage(MovieHex));
        Assert.IsType<NotFoundResult>(c.Leaving(MovieHex));
        Assert.Empty(_farewell.Calls);
        _cfg.Current = new PluginConfiguration { TravelEnabled = true, VisaUrl = "http://visa.example:8097" };   // no token: off
        Assert.IsType<NotFoundResult>(await c.TravelStatus(CancellationToken.None));
        Assert.IsType<NotFoundResult>(await Controller("{\"days\":3}").TravelOpen(CancellationToken.None));
        Assert.Empty(_visa.Calls);
    }

    [Fact]
    public void Answers_AreNeverCached_AndPathsNeverAppear()
    {
        StorageOn(Tiers("{\"path\":\"movies/Jaws (1975)\",\"tier\":\"ssd\"}"));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/media/movies/Jaws (1975)/a.mkv", true);
        var c = Controller();
        var text = Text(c.Storage(Movie.ToString("N")));
        Assert.Equal("no-store", c.Response.Headers.CacheControl.ToString());
        Assert.DoesNotContain("media", text);
        Assert.DoesNotContain("Jaws", text);
    }

    // ---------- leaving ----------

    [Fact]
    public void Leaving_FindsAnEntryByPlainOrDashedId()
    {
        KeepOn(Leaving(MovieHex, "3"));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/x", false);
        Assert.Equal("{\"days_left\":3,\"can_keep\":true}", Text(Controller().Leaving(MovieHex)));
        Assert.Equal("{\"days_left\":3,\"can_keep\":true}", Text(Controller().Leaving(Movie.ToString("D"))));
        Assert.Equal("{\"days_left\":3,\"can_keep\":true}", Text(Controller().Leaving(MovieHex.ToUpperInvariant())));
    }

    [Fact]
    public void Leaving_NullDaysAndCanKeepFalse()
    {
        KeepOn(Leaving(MovieHex, "null", "false"));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/x", false);
        Assert.Equal("{\"days_left\":null,\"can_keep\":false}", Text(Controller().Leaving(MovieHex)));
    }

    [Fact]
    public void Leaving_204ForNotListedUnknownHiddenBadIdOrBadFile()
    {
        KeepOn(Leaving(MovieHex));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/x", false);
        var notListed = Guid.NewGuid();
        _items.Known[notListed] = new ItemFacts(ItemKind.Movie, "/x", false);
        Assert.IsType<NoContentResult>(Controller().Leaving(notListed.ToString("N")));
        Assert.IsType<NoContentResult>(Controller().Leaving("zzz"));
        _items.Known.Remove(Movie);   // listed, but this user cannot see it
        Assert.IsType<NoContentResult>(Controller().Leaving(MovieHex));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/x", false);
        File.WriteAllText(_cfg.Current.LeavingFile, "{\"version\":2,\"entries\":[]}");
        Assert.IsType<NoContentResult>(Controller().Leaving(MovieHex));
    }

    // ---------- keep ----------

    private string Body() => "{\"itemId\":\"" + MovieHex + "\"}";

    private async Task<(int Status, string Text, IntegrationController C)> Keep(string? body = null, Guid? user = null)
    {
        var c = Controller(body ?? Body(), user);
        var r = await c.Keep(CancellationToken.None);
        var content = Assert.IsType<ContentResult>(r);
        return (content.StatusCode ?? 0, content.Content ?? string.Empty, c);
    }

    private void Ready()
    {
        KeepOn(Leaving(MovieHex));
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/x", false);
    }

    [Fact]
    public async Task Keep_HappyPath_CallsFarewellWithTheHexIdAndTheToken()
    {
        Ready();
        var (status, text, _) = await Keep();
        Assert.Equal(200, status);
        Assert.Equal("{\"ok\":true,\"message\":\"kept\"}", text);
        var call = Assert.Single(_farewell.Calls);
        Assert.Equal(("http://farewell.example:8098", "secret-token-value", MovieHex), call);
        Assert.DoesNotContain("secret-token-value", text);
    }

    [Fact]
    public async Task Keep_SettingOff_SendsNoName()
    {
        Ready();
        _items.Names[User1] = "Ann";
        Assert.False(_cfg.Current.KeepSendsName);
        Assert.Equal(200, (await Keep()).Status);
        Assert.Null(Assert.Single(_farewell.Names));
    }

    [Fact]
    public async Task Keep_SettingOn_SendsTheCallersNameFromTheClaimAndShowsItNowhere()
    {
        Ready();
        _cfg.Current.KeepSendsName = true;
        _items.Names[User1] = "Ann Lee";
        _items.Names[Guid.Parse("22222222222222222222222222222222")] = "Bob";
        var (status, text, c) = await Keep();
        Assert.Equal(200, status);
        Assert.Equal("Ann Lee", Assert.Single(_farewell.Names));
        Assert.DoesNotContain("Ann", text);
        Assert.DoesNotContain("Ann", string.Join(" ", c.Response.Headers.Select(h => h.Key + h.Value)));
    }

    [Fact]
    public async Task Keep_NameComesOnlyFromTheClaim_ABodyNamingSomeoneElseIs400()
    {
        Ready();
        _cfg.Current.KeepSendsName = true;
        _items.Names[User1] = "Ann";
        Assert.Equal(400, (await Keep("{\"itemId\":\"" + MovieHex + "\",\"by\":\"Mallory\"}")).Status);
        Assert.Equal(400, (await Keep("{\"by\":\"Mallory\"}")).Status);
        Assert.Empty(_farewell.Calls);
        // Another signed-in user gets their own name.
        var other = Guid.Parse("22222222222222222222222222222222");
        _items.Names[other] = "Bob";
        Assert.Equal(200, (await Keep(user: other)).Status);
        Assert.Equal("Bob", Assert.Single(_farewell.Names));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Keep_SettingOn_LookupFailureOrUnusableNameIsA502AndFarewellIsNotCalled(bool blank)
    {
        Ready();
        _cfg.Current.KeepSendsName = true;
        if (blank)
        {
            _items.Names[User1] = " \u0007\u200b ";
        }

        var (status, text, _) = await Keep();
        Assert.Equal(502, status);
        Assert.Equal("{\"ok\":false,\"message\":\"could not complete the request\"}", text);
        Assert.Empty(_farewell.Calls);
        // The refusal did not use up the person's cooldown.
        _items.Names[User1] = "Ann";
        Assert.Equal(200, (await Keep()).Status);
    }

    [Theory]
    [InlineData("Ann", "Ann")]
    [InlineData("  Ann  Lee ", "Ann Lee")]
    [InlineData("Ann\r\nLee", "Ann Lee")]
    [InlineData("A\u00a0B", "A B")]
    [InlineData("Zoë", "Zoë")]
    [InlineData("a\u0007b", "a b")]
    [InlineData("a\u200bb", "a b")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\u0007\u200b", null)]
    [InlineData(null, null)]
    public void KeepName_Clean(string? input, string? expected) => Assert.Equal(expected, KeepName.Clean(input));

    [Fact]
    public void KeepName_CutsToSixtyCharactersWithNoSpaceAtTheEnd()
    {
        Assert.Equal(new string('x', 60), KeepName.Clean(new string('x', 200)));
        var cut = KeepName.Clean(new string('x', 59) + " yyyy")!;
        Assert.Equal(new string('x', 59), cut);
        Assert.Equal(new string('x', 58) + " y", KeepName.Clean(new string('x', 58) + " yyyy"));
        // Counted in code points, like Farewell: 60 emoji are 60 characters (120 UTF-16 units).
        var emoji = KeepName.Clean(string.Concat(Enumerable.Repeat("\U0001F600", 80)))!;
        Assert.Equal(60, emoji.EnumerateRunes().Count());
        Assert.Null(KeepName.Clean("\ud800"));
    }

    [Fact]
    public async Task Client_BodyWithAndWithoutTheName()
    {
        var h = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"message\":\"done\"}") } };
        await Client(h).KeepAsync("http://f:1", "t", MovieHex, CancellationToken.None);
        Assert.Equal("{\"title\":\"" + MovieHex + "\"}", h.SeenBody);
        await Client(h).KeepAsync("http://f:1", "t", MovieHex, CancellationToken.None, "Ann Lee");
        Assert.Equal("{\"title\":\"" + MovieHex + "\",\"by\":\"Ann Lee\"}", h.SeenBody);
    }

    [Fact]
    public void KeepSendsName_IsOffByDefaultKeepsItsValueAndNeverReachesTheBrowserPage()
    {
        Assert.False(new PluginConfiguration().KeepSendsName);
        var c = new PluginConfiguration { KeepSendsName = true, KeepEnabled = true, LeavingFile = "/l.json", FarewellUrl = "http://f:1", FarewellToken = "abc" };
        Assert.Empty(ConfigSanitizer.Sanitize(c));
        Assert.True(c.KeepSendsName);
        Assert.True(ConfigSanitizer.Redacted(c).KeepSendsName);
        Assert.DoesNotContain("SendsName", IndexTransform.BuildConfigJson(c), StringComparison.OrdinalIgnoreCase);
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var w = new StringWriter();
        xml.Serialize(w, c);
        using var r = new StringReader(w.ToString());
        Assert.True(((PluginConfiguration)xml.Deserialize(r)!).KeepSendsName);
    }

    [Fact]
    public async Task Keep_AcceptsADashedId()
    {
        Ready();
        var (status, _, _) = await Keep("{\"itemId\":\"" + Movie.ToString("D") + "\"}");
        Assert.Equal(200, status);
        Assert.Equal(MovieHex, _farewell.Calls[0].Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"itemId\":5}")]
    [InlineData("{\"itemId\":\"zz\"}")]
    [InlineData("{\"itemId\":\"0123456789abcdef0123456789abcdef\",\"extra\":1}")]
    [InlineData("{\"title\":\"0123456789abcdef0123456789abcdef\"}")]
    public async Task Keep_BadBodyIs400AndNothingIsCalled(string body)
    {
        Ready();
        var (status, _, _) = await Keep(body);
        Assert.Equal(400, status);
        Assert.Empty(_farewell.Calls);
    }

    [Fact]
    public async Task Keep_OversizeBodyIs400()
    {
        Ready();
        var (status, _, _) = await Keep("{\"itemId\":\"" + new string('a', 2000) + "\"}");
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Keep_RefusesWhatIsNotKeepable_WithoutCallingFarewell()
    {
        Ready();
        _items.Known.Remove(Movie);   // not visible to this user
        Assert.Equal(422, (await Keep()).Status);
        _items.Known[Movie] = new ItemFacts(ItemKind.Movie, "/x", false);
        File.WriteAllText(_cfg.Current.LeavingFile, Leaving(MovieHex, "3", "false"));   // listed but not keepable
        Assert.Equal(422, (await Keep()).Status);
        File.WriteAllText(_cfg.Current.LeavingFile, Leaving("ffffffffffffffffffffffffffffffff"));   // not listed
        File.SetLastWriteTimeUtc(_cfg.Current.LeavingFile, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(422, (await Keep()).Status);
        Assert.Empty(_farewell.Calls);
    }

    [Fact]
    public async Task Keep_FarewellRefusal422IsPassedThroughCleaned()
    {
        Ready();
        _farewell.Reply = new FarewellReply(422, "already kept");
        var (status, text, _) = await Keep();
        Assert.Equal(422, status);
        Assert.Equal("{\"ok\":false,\"message\":\"already kept\"}", text);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(404)]
    [InlineData(502)]
    [InlineData(0)]
    public async Task Keep_AnythingElseIs502WithNoDetail(int farewellStatus)
    {
        Ready();
        _farewell.Reply = new FarewellReply(farewellStatus, "internal detail with secret-token-value");
        var (status, text, _) = await Keep();
        Assert.Equal(502, status);
        Assert.Equal("{\"ok\":false,\"message\":\"could not complete the request\"}", text);
    }

    [Fact]
    public async Task Keep_CooldownIsPerUserTenSeconds()
    {
        Ready();
        Assert.Equal(200, (await Keep()).Status);
        _now += TimeSpan.FromSeconds(4);
        var (status, _, c) = await Keep();
        Assert.Equal(429, status);
        Assert.Equal("6", c.Response.Headers.RetryAfter.ToString());
        Assert.Single(_farewell.Calls);
        // Another user is not held back by the first one's cooldown.
        Assert.Equal(200, (await Keep(user: Guid.NewGuid())).Status);
        _now += TimeSpan.FromSeconds(7);
        Assert.Equal(200, (await Keep()).Status);
    }

    [Fact]
    public void Guard_OnlyOneInFlight()
    {
        var g = new KeepGuard(() => _now);
        Assert.True(g.TryEnter(Guid.NewGuid(), out _));
        Assert.False(g.TryEnter(Guid.NewGuid(), out var retry));
        Assert.True(retry > 0);
        g.Exit();
        Assert.True(g.TryEnter(Guid.NewGuid(), out _));
    }

    // ---------- the real client against a fake handler (no network) ----------

    private static FarewellClient Client(StubHandler h) => new(new HttpClient(h));

    [Fact]
    public async Task Client_SendsTheTokenOnlyInTheHeaderAndTheIdInTheBody()
    {
        var h = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"message\":\"done\"}") } };
        var r = await Client(h).KeepAsync("http://farewell.example:8098", "tok-123", MovieHex, CancellationToken.None);
        Assert.Equal(new FarewellReply(200, "done"), r);
        Assert.Equal("http://farewell.example:8098/v1/keep", h.Seen!.RequestUri!.ToString());
        Assert.Equal("Bearer", h.Seen.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", h.Seen.Headers.Authorization.Parameter);
        Assert.Equal("application/json", h.Seen.Content!.Headers.ContentType!.ToString());
        Assert.Equal("{\"title\":\"" + MovieHex + "\"}", h.SeenBody);
        Assert.DoesNotContain("tok-123", h.SeenBody);
        Assert.DoesNotContain("tok-123", h.Seen.RequestUri.ToString());
    }

    [Fact]
    public async Task Client_Maps422_AndOtherStatusesDropTheBody()
    {
        var h = new StubHandler { Respond = _ => new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"ok\":false,\"message\":\"no such title\"}") } };
        Assert.Equal(new FarewellReply(422, "no such title"), await Client(h).KeepAsync("http://f:1", "t", MovieHex, CancellationToken.None));
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"message\":\"stack trace\"}") };
        Assert.Equal(new FarewellReply(500, null), await Client(h).KeepAsync("http://f:1", "t", MovieHex, CancellationToken.None));
    }

    [Fact]
    public async Task Client_RedirectIsNotFollowedAndAnExceptionIsStatusZero()
    {
        var h = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) };
        Assert.Equal(new FarewellReply(302, null), await Client(h).KeepAsync("http://f:1", "t", MovieHex, CancellationToken.None));   // an "other" status, mapped to 502 above
        h.Respond = _ => throw new HttpRequestException("boom http://f:1 token t");
        Assert.Equal(new FarewellReply(0, null), await Client(h).KeepAsync("http://f:1", "t", MovieHex, CancellationToken.None));
        // The default client does not follow redirects.
        var field = typeof(FarewellClient).GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal(TimeSpan.FromSeconds(10), ((HttpClient)field.GetValue(new FarewellClient())!).Timeout);
    }

    [Fact]
    public async Task Client_ReadsAtMost64KB()
    {
        var big = "{\"ok\":true,\"message\":\"" + new string('a', FarewellClient.MaxResponseBytes) + "\"}";
        var h = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(big) } };
        Assert.Equal(new FarewellReply(0, null), await Client(h).KeepAsync("http://f:1", "t", MovieHex, CancellationToken.None));
    }

    [Fact]
    public void Clean_TrimsToTwoHundredPrintableCharacters()
    {
        Assert.Equal("a  b", FarewellClient.Clean("a\r\nb"));
        Assert.Equal(200, FarewellClient.Clean(new string('x', 500))!.Length);
        Assert.Null(FarewellClient.Clean("  \t "));
        Assert.Null(FarewellClient.Clean(null));
    }

    // ---------- travel ----------

    private const string VisaToken = "visa-token-value-1234567890";

    private void TravelOn() =>
        _cfg.Current = new PluginConfiguration { TravelEnabled = true, VisaUrl = "http://visa.example:8097", VisaToken = VisaToken };

    private async Task<(int Status, string Text, IntegrationController C)> Open(string? body = "{\"days\":5}", Guid? user = null)
    {
        var c = Controller(body, user);
        var content = Assert.IsType<ContentResult>(await c.TravelOpen(CancellationToken.None));
        return (content.StatusCode ?? 0, content.Content ?? string.Empty, c);
    }

    [Fact]
    public void Travel_IsOffByDefault()
    {
        var c = new PluginConfiguration();
        Assert.False(c.TravelEnabled);
        Assert.False(FeatureGate.Travel(c));
        Assert.False(FeatureGate.Travel(new PluginConfiguration { TravelEnabled = true, VisaUrl = "http://v:1" }));
        Assert.True(FeatureGate.Travel(new PluginConfiguration { TravelEnabled = true, VisaUrl = "http://v:1", VisaToken = "t" }));
    }

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("http://user:pw@host")]
    [InlineData("http://host/path")]
    [InlineData("http://host?x=1")]
    [InlineData("host:8097")]
    [InlineData("http://ho st")]
    public void Sanitizer_RejectsBadVisaUrls(string url)
    {
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { VisaUrl = url }), e => e.StartsWith("Visa address"));
    }

    [Fact]
    public void Sanitizer_NormalisesVisaUrlAndChecksTheToken()
    {
        var c = new PluginConfiguration { VisaUrl = "https://visa.example/" };
        Assert.Empty(ConfigSanitizer.Sanitize(c));
        Assert.Equal("https://visa.example", c.VisaUrl);
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { VisaToken = "has space" }), e => e.StartsWith("Visa token"));
        Assert.Contains(ConfigSanitizer.Sanitize(new PluginConfiguration { VisaToken = new string('a', 257) }), e => e.StartsWith("Visa token"));
        Assert.Empty(ConfigSanitizer.Sanitize(new PluginConfiguration { VisaToken = new string('a', 256) }));
    }

    [Fact]
    public void Sanitizer_TravelNeedsItsSettings()
    {
        Assert.Equal(2, ConfigSanitizer.Sanitize(new PluginConfiguration { TravelEnabled = true }).Count);
        Assert.Empty(ConfigSanitizer.Sanitize(new PluginConfiguration { TravelEnabled = true, VisaUrl = "http://v:1", VisaToken = "abc" }));
    }

    [Fact]
    public void VisaToken_IsNeverReturned_BlankKeeps_ClearFlagClears()
    {
        var c = new PluginConfiguration { VisaToken = VisaToken, FarewellToken = "farewell-secret" };
        var red = ConfigSanitizer.Redacted(c);
        Assert.Equal(string.Empty, red.VisaToken);
        Assert.True(red.VisaTokenSet);
        Assert.DoesNotContain(VisaToken, JsonSerializer.Serialize(red));
        Assert.Equal(VisaToken, c.VisaToken);
        Assert.False(ConfigSanitizer.Redacted(new PluginConfiguration()).VisaTokenSet);
        Assert.Equal("old", ConfigSanitizer.MergeVisaToken(new PluginConfiguration { VisaToken = "  " }, "old"));
        Assert.Equal("new", ConfigSanitizer.MergeVisaToken(new PluginConfiguration { VisaToken = " new " }, "old"));
        Assert.Equal(string.Empty, ConfigSanitizer.MergeVisaToken(new PluginConfiguration { VisaToken = "new", ClearVisaToken = true }, "old"));
        // The two tokens are independent.
        Assert.Equal("farewell-old", ConfigSanitizer.MergeToken(new PluginConfiguration { ClearVisaToken = true }, "farewell-old"));
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var w = new StringWriter();
        xml.Serialize(w, new PluginConfiguration { VisaTokenSet = true, ClearVisaToken = true });
        Assert.DoesNotContain("VisaTokenSet", w.ToString());
        Assert.DoesNotContain("ClearVisaToken", w.ToString());
    }

    [Fact]
    public async Task Travel_Status_MapsVisaAndUsesTheCallerFromTheClaim()
    {
        TravelOn();
        _visa.StatusReply = new VisaReply(200, null, new TravelState(1900000000, null, 14));
        var c = Controller();
        var text = Text(await c.TravelStatus(CancellationToken.None));
        Assert.Equal("{\"active\":{\"ends\":1900000000},\"next_allowed\":null,\"max_days\":14}", text);
        Assert.Equal("no-store", c.Response.Headers.CacheControl.ToString());
        var call = Assert.Single(_visa.Calls);
        Assert.Equal(("http://visa.example:8097", VisaToken, User1.ToString("N"), 0), call);
        Assert.DoesNotContain(VisaToken, text);

        _visa.StatusReply = new VisaReply(200, null, new TravelState(null, 1900000500, 7));
        Assert.Equal("{\"active\":null,\"next_allowed\":1900000500,\"max_days\":7}", Text(await Controller().TravelStatus(CancellationToken.None)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(0)]
    public async Task Travel_Status_AnythingElseIs502WithNoDetail(int visaStatus)
    {
        TravelOn();
        _visa.StatusReply = new VisaReply(visaStatus, "internal detail " + VisaToken);
        var content = Assert.IsType<ContentResult>(await Controller().TravelStatus(CancellationToken.None));
        Assert.Equal(502, content.StatusCode);
        Assert.Equal("{\"ok\":false,\"message\":\"could not complete the request\"}", content.Content);
    }

    [Fact]
    public async Task Travel_Open_SendsTheDaysForTheCallerOnly()
    {
        TravelOn();
        var other = Guid.NewGuid();
        // A hostile body naming someone else is refused outright: the only key allowed is "days".
        var (badStatus, _, _) = await Open("{\"days\":3,\"user_id\":\"" + other.ToString("N") + "\"}");
        Assert.Equal(400, badStatus);
        Assert.Empty(_visa.Calls);

        var (status, text, _) = await Open("{\"days\":5}");
        Assert.Equal(200, status);
        Assert.Equal("{\"ok\":true,\"message\":\"window opened\"}", text);
        var call = Assert.Single(_visa.Calls);
        Assert.Equal(("http://visa.example:8097", VisaToken, User1.ToString("N"), 5), call);
        Assert.DoesNotContain(VisaToken, text);

        // A different signed-in user is sent as that user, not as the first.
        Assert.Equal(200, (await Open("{\"days\":2}", other)).Status);
        Assert.Equal(other.ToString("N"), _visa.Calls[1].User);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"days\":\"5\"}")]
    [InlineData("{\"days\":5.5}")]
    [InlineData("{\"days\":0}")]
    [InlineData("{\"days\":-1}")]
    [InlineData("{\"days\":1001}")]
    [InlineData("{\"days\":99999999999}")]
    [InlineData("{\"days\":null}")]
    [InlineData("{\"day\":3}")]
    [InlineData("{\"days\":3,\"days\":4}")]
    public async Task Travel_Open_BadBodyIs400AndNothingIsCalled(string body)
    {
        TravelOn();
        Assert.Equal(400, (await Open(body)).Status);
        Assert.Empty(_visa.Calls);
    }

    [Fact]
    public async Task Travel_Open_RefusalIsPassedThroughCleaned_AlsoForAdministrators()
    {
        TravelOn();
        _visa.OpenReply = new VisaReply(422, "use your admin tools");
        var (status, text, _) = await Open();
        Assert.Equal(422, status);
        Assert.Equal("{\"ok\":false,\"message\":\"use your admin tools\"}", text);
        _now += TimeSpan.FromSeconds(11);
        _visa.OpenReply = new VisaReply(422, null);
        Assert.Equal("{\"ok\":false,\"message\":\"travel cannot be opened right now\"}", (await Open()).Text);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(404)]
    [InlineData(0)]
    public async Task Travel_Open_AnythingElseIs502WithNoDetail(int visaStatus)
    {
        TravelOn();
        _visa.OpenReply = new VisaReply(visaStatus, "internal detail " + VisaToken);
        var (status, text, _) = await Open();
        Assert.Equal(502, status);
        Assert.Equal("{\"ok\":false,\"message\":\"could not complete the request\"}", text);
    }

    [Fact]
    public async Task Travel_Open_CooldownIsPerUserTenSeconds_AndSeparateFromKeep()
    {
        TravelOn();
        Assert.Equal(200, (await Open()).Status);
        _now += TimeSpan.FromSeconds(4);
        var (status, _, c) = await Open();
        Assert.Equal(429, status);
        Assert.Equal("6", c.Response.Headers.RetryAfter.ToString());
        Assert.Single(_visa.Calls);
        Assert.Equal(200, (await Open(user: Guid.NewGuid())).Status);
        _now += TimeSpan.FromSeconds(7);
        Assert.Equal(200, (await Open()).Status);
        // A keep press does not use up the travel cooldown.
        Assert.True(_guard.TryEnter(User1, out _));
        _guard.Exit();
        _now += TimeSpan.FromSeconds(11);
        Assert.Equal(200, (await Open()).Status);
    }

    [Fact]
    public async Task Travel_WithoutAVerifiedUserClaim_Is401AndNothingIsCalled()
    {
        TravelOn();
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"days\":3}"));
        var c = new IntegrationController(_cfg, _items, _reader, _farewell, _guard, _visa, _travelGuard, NullLogger<IntegrationController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        Assert.IsType<UnauthorizedResult>(await c.TravelStatus(CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await c.TravelOpen(CancellationToken.None));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", "not-a-guid") }, "test"));
        Assert.IsType<UnauthorizedResult>(await c.TravelOpen(CancellationToken.None));
        Assert.Empty(_visa.Calls);
    }

    [Fact]
    public async Task Travel_Status_IsNotHeldByTheCooldown()
    {
        TravelOn();
        Assert.Equal(200, (await Open()).Status);
        Assert.Equal(200, Assert.IsType<ContentResult>(await Controller().TravelStatus(CancellationToken.None)).StatusCode);
    }

    // ---------- the Visa client against a fake handler (no network) ----------

    private static VisaClient VisaHttp(StubHandler h) => new(new HttpClient(h));

    [Fact]
    public async Task VisaClient_Status_SendsTheTokenOnlyInTheHeaderAndReadsTheState()
    {
        var h = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true,\"active\":{\"id\":3,\"ends\":1900000000},\"next_allowed\":1900001000,\"max_days\":14}") }
        };
        var r = await VisaHttp(h).StatusAsync("http://visa.example:8097", "tok-123", MovieHex, CancellationToken.None);
        Assert.Equal(200, r.Status);
        Assert.Equal(new TravelState(1900000000, 1900001000, 14), r.State);
        Assert.Equal("http://visa.example:8097/v1/travel/" + MovieHex, h.Seen!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, h.Seen.Method);
        Assert.Equal("Bearer", h.Seen.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", h.Seen.Headers.Authorization.Parameter);
        Assert.DoesNotContain("tok-123", h.Seen.RequestUri.ToString());
    }

    [Theory]
    [InlineData("{\"active\":null,\"next_allowed\":null,\"max_days\":14}", true)]
    [InlineData("{\"active\":null,\"next_allowed\":null}", false)]
    [InlineData("{\"active\":{},\"next_allowed\":null,\"max_days\":14}", false)]
    [InlineData("{\"active\":5,\"next_allowed\":null,\"max_days\":14}", false)]
    [InlineData("{\"active\":null,\"next_allowed\":\"x\",\"max_days\":14}", false)]
    [InlineData("{\"active\":null,\"next_allowed\":null,\"max_days\":0}", false)]
    [InlineData("{\"active\":null,\"next_allowed\":null,\"max_days\":99999}", false)]
    [InlineData("{\"active\":{\"ends\":-5},\"next_allowed\":null,\"max_days\":14}", false)]
    [InlineData("[]", false)]
    [InlineData("nope", false)]
    public async Task VisaClient_Status_OnlyAnExactShapeCounts(string body, bool good)
    {
        var h = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) } };
        var r = await VisaHttp(h).StatusAsync("http://v:1", "t", MovieHex, CancellationToken.None);
        Assert.Equal(good ? 200 : 0, r.Status);
        Assert.Equal(good, r.State is not null);
    }

    [Fact]
    public async Task VisaClient_Open_PostsExactlyUserAndDays_Maps422_DropsOtherBodies()
    {
        var h = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"message\":\"done\"}") } };
        Assert.Equal(new VisaReply(200, "done"), await VisaHttp(h).OpenAsync("http://v:1", "tok-123", MovieHex, 5, CancellationToken.None));
        Assert.Equal("http://v:1/v1/travel", h.Seen!.RequestUri!.ToString());
        Assert.Equal("application/json", h.Seen.Content!.Headers.ContentType!.ToString());
        Assert.Equal("{\"user_id\":\"" + MovieHex + "\",\"days\":5}", h.SeenBody);
        Assert.DoesNotContain("tok-123", h.SeenBody);
        Assert.Equal("tok-123", h.Seen.Headers.Authorization!.Parameter);

        h.Respond = _ => new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"ok\":false,\"message\":\"too long\"}") };
        Assert.Equal(new VisaReply(422, "too long"), await VisaHttp(h).OpenAsync("http://v:1", "t", MovieHex, 99, CancellationToken.None));
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"message\":\"stack trace\"}") };
        Assert.Equal(new VisaReply(500, null), await VisaHttp(h).OpenAsync("http://v:1", "t", MovieHex, 1, CancellationToken.None));
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found);
        Assert.Equal(new VisaReply(302, null), await VisaHttp(h).OpenAsync("http://v:1", "t", MovieHex, 1, CancellationToken.None));
        h.Respond = _ => throw new HttpRequestException("boom http://v:1 token t");
        Assert.Equal(new VisaReply(0, null), await VisaHttp(h).OpenAsync("http://v:1", "t", MovieHex, 1, CancellationToken.None));
        Assert.Equal(new VisaReply(0, null), await VisaHttp(h).StatusAsync("http://v:1", "t", MovieHex, CancellationToken.None));
        var field = typeof(VisaClient).GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal(TimeSpan.FromSeconds(10), ((HttpClient)field.GetValue(new VisaClient())!).Timeout);
    }

    [Fact]
    public async Task VisaClient_ReadsAtMost64KB()
    {
        var big = "{\"ok\":true,\"message\":\"" + new string('a', FarewellClient.MaxResponseBytes) + "\"}";
        var h = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(big) } };
        Assert.Equal(new VisaReply(0, null), await VisaHttp(h).OpenAsync("http://v:1", "t", MovieHex, 1, CancellationToken.None));
    }
}
