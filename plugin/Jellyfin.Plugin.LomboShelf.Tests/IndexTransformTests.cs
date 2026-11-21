using System.Text.Json;
using Jellyfin.Plugin.LomboShelf.Configuration;
using Jellyfin.Plugin.LomboShelf.Services;
using Xunit;

namespace Jellyfin.Plugin.LomboShelf.Tests;

public class IndexTransformTests
{
    private const string Page = "<html><head></head><body><div id=\"app\"></div></body></html>";

    [Fact]
    public void Apply_InsertsBothScriptsBeforeClosingBody()
    {
        var json = IndexTransform.BuildConfigJson(new PluginConfiguration());
        var html = IndexTransform.Apply(Page, json);

        var marker = html.IndexOf(IndexTransform.Marker, StringComparison.Ordinal);
        var cfg = html.IndexOf("<script>window.LomboShelfConfig=", StringComparison.Ordinal);
        var loader = html.IndexOf("<script src=\"/LomboShelf/web/loader.js\"></script>", StringComparison.Ordinal);
        var body = html.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(marker > 0 && marker < cfg && cfg < loader && loader < body);
        Assert.StartsWith("<html><head></head><body><div id=\"app\"></div>", html);
        Assert.EndsWith("</body></html>", html);
    }

    [Fact]
    public void Apply_ClosingBodyIsCaseInsensitive()
    {
        var html = IndexTransform.Apply("<BODY>x</BODY>", "{}");
        Assert.True(html.IndexOf("loader.js", StringComparison.Ordinal) < html.IndexOf("</BODY>", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_AppendsWhenThereIsNoBodyTag()
    {
        var html = IndexTransform.Apply("<p>x</p>", "{}");
        Assert.StartsWith("<p>x</p>" + IndexTransform.Marker, html);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var json = IndexTransform.BuildConfigJson(new PluginConfiguration());
        var once = IndexTransform.Apply(Page, json);
        var twice = IndexTransform.Apply(once, json);
        Assert.Equal(once, twice);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "loader\\.js"));
    }

    [Fact]
    public void Apply_UsesTheGivenBasePath()
    {
        Assert.Contains("src=\"/x/loader.js\"", IndexTransform.Apply(Page, "{}", "/x/"));
    }

    [Fact]
    public void Apply_AddsTheVersionToTheLoaderUrl()
    {
        Assert.Contains("src=\"/x/loader.js?v=1.2.3.0\"", IndexTransform.Apply(Page, "{}", "/x/", "1.2.3.0"));
    }

    [Fact]
    public void Apply_DropsUnsafeVersionCharacters()
    {
        var html = IndexTransform.Apply(Page, "{}", "/x/", "1\"><script>");
        Assert.Contains("loader.js?v=1script\"", html);
        Assert.DoesNotContain("\"><script>", html);
    }

    [Fact]
    public void BuildConfigJson_EscapesSoItCannotCloseTheScriptTag()
    {
        var c = new PluginConfiguration
        {
            Accent = "</script><script>alert(1)</script>",
            BrowseMergeLibraries = new[] { "A & B <!-- x", "\"quoted\"" }
        };
        var json = IndexTransform.BuildConfigJson(c);
        Assert.DoesNotContain("<", json);
        Assert.DoesNotContain(">", json);
        Assert.DoesNotContain("&", json);

        // The escaped text still round-trips to the original value.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(c.Accent, doc.RootElement.GetProperty("theme").GetProperty("accent").GetString());
        Assert.Equal("A & B <!-- x", doc.RootElement.GetProperty("browse").GetProperty("mergeLibraries")[0].GetString());

        var html = IndexTransform.Apply(Page, json);
        Assert.Equal(1, CountOf(html, "</script>", before: html.IndexOf("loader.js", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildConfigJson_OmitsHeaderNavWhenOff_AndSendsGroupsWhenOn()
    {
        var c = new PluginConfiguration
        {
            HeaderNavOrder = new[] { "Movies" },
            HeaderNavGroupsJson = "[{\"label\":\"Anime\",\"items\":[[\"Anime\",\"Shows\"]]}]"
        };
        using (var off = JsonDocument.Parse(IndexTransform.BuildConfigJson(c)))
        {
            Assert.False(off.RootElement.TryGetProperty("headerNav", out _));
        }

        c.FeatureHeaderNav = true;
        using var on = JsonDocument.Parse(IndexTransform.BuildConfigJson(c));
        var nav = on.RootElement.GetProperty("headerNav");
        Assert.Equal("Movies", nav.GetProperty("order")[0].GetString());
        var g = nav.GetProperty("groups")[0];
        Assert.Equal("Anime", g.GetProperty("label").GetString());
        Assert.Equal("Anime", g.GetProperty("items")[0][0].GetString());
        Assert.Equal("Shows", g.GetProperty("items")[0][1].GetString());
    }

    [Fact]
    public void BuildConfigJson_InvalidGroupsFailOpenToAnEmptyList()
    {
        var c = new PluginConfiguration { FeatureHeaderNav = true, HeaderNavGroupsJson = "not json" };
        using var doc = JsonDocument.Parse(IndexTransform.BuildConfigJson(c));
        Assert.Equal(0, doc.RootElement.GetProperty("headerNav").GetProperty("groups").GetArrayLength());
    }

    [Fact]
    public void BuildConfigJson_EmitsCollectionRows_WithoutAnEmptyTitle_AndEscaped()
    {
        var c = new PluginConfiguration
        {
            CollectionRows = new[]
            {
                new CollectionRow { Name = "Leaving soon", Limit = 20 },
                new CollectionRow { Name = "</script><b>", Title = "A & B", Limit = 0 }
            }
        };
        var json = IndexTransform.BuildConfigJson(c);
        Assert.DoesNotContain("<", json);
        Assert.DoesNotContain(">", json);
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("collectionRows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("Leaving soon", rows[0].GetProperty("name").GetString());
        Assert.Equal(20, rows[0].GetProperty("limit").GetInt32());
        Assert.False(rows[0].TryGetProperty("title", out _));
        Assert.Equal("</script><b>", rows[1].GetProperty("name").GetString());
        Assert.Equal("A & B", rows[1].GetProperty("title").GetString());
        Assert.Equal(16, rows[1].GetProperty("limit").GetInt32());
    }

    [Fact]
    public void BuildConfigJson_CapsCollectionRowsEvenWithoutTheSanitiser()
    {
        var c = new PluginConfiguration
        {
            CollectionRows = Enumerable.Range(1, 9).Select(i => new CollectionRow { Name = "C" + i, Limit = 999 }).ToArray()
        };
        using var doc = JsonDocument.Parse(IndexTransform.BuildConfigJson(c));
        var rows = doc.RootElement.GetProperty("collectionRows");
        Assert.Equal(5, rows.GetArrayLength());
        Assert.Equal(50, rows[0].GetProperty("limit").GetInt32());
    }

    private static int CountOf(string s, string needle, int before)
    {
        var n = 0;
        for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0 && i < before; i = s.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
