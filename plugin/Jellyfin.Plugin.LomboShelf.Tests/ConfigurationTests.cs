using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LomboShelf.Configuration;
using Jellyfin.Plugin.LomboShelf.Services;
using Xunit;

namespace Jellyfin.Plugin.LomboShelf.Tests;

public class ConfigurationTests
{
    /// <summary>Reads the `defaults` object literal of web/loader.js and flattens it to path = value.</summary>
    private static SortedDictionary<string, string> LoaderDefaults()
    {
        var js = File.ReadAllText(Path.Combine(TestPaths.WebDir(), "loader.js"));
        var m = Regex.Match(js, @"var defaults = (\{.*?\n  \});", RegexOptions.Singleline);
        Assert.True(m.Success, "could not find `var defaults` in loader.js");
        var json = Regex.Replace(m.Groups[1].Value, @"(?<=[{,\s])([A-Za-z0-9]+):", "\"$1\":");
        return Flatten(JsonDocument.Parse(json).RootElement);
    }

    private static SortedDictionary<string, string> Flatten(JsonElement e)
    {
        var o = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Walk(string path, JsonElement x)
        {
            switch (x.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in x.EnumerateObject()) Walk(path + "." + p.Name, p.Value);
                    break;
                case JsonValueKind.Null:
                    break; // loader.js uses null for "not set"; the plugin leaves the key out
                default:
                    o[path] = x.GetRawText();
                    break;
            }
        }

        Walk("", e);
        return o;
    }

    [Fact]
    public void DefaultConfigurationEqualsLoaderJsDefaults()
    {
        var fromPlugin = Flatten(JsonDocument.Parse(IndexTransform.BuildConfigJson(new PluginConfiguration())).RootElement);
        Assert.Equal(LoaderDefaults(), fromPlugin);
    }

    [Fact]
    public void Defaults_HaveNoProblems()
    {
        Assert.Empty(ConfigSanitizer.Sanitize(new PluginConfiguration()));
    }

    [Theory]
    [InlineData("red; background:url(x)")]
    [InlineData("#12")]
    [InlineData("expression(alert(1))")]
    [InlineData("</style>")]
    [InlineData("")]
    public void Sanitize_RejectsUnsafeColours(string value)
    {
        var c = new PluginConfiguration { Accent = value };
        Assert.Contains(ConfigSanitizer.Sanitize(c), e => e.StartsWith("Accent"));
    }

    [Theory]
    [InlineData("#e50914")]
    [InlineData("rgba(1, 2, 3, 0.5)")]
    [InlineData(" #fff ")]
    public void Sanitize_AcceptsSafeColours(string value)
    {
        var c = new PluginConfiguration { Accent = value };
        Assert.Empty(ConfigSanitizer.Sanitize(c));
        Assert.Equal(value.Trim(), c.Accent);
    }

    [Fact]
    public void Sanitize_CleansListsAndRejectsNegativeLimit()
    {
        var c = new PluginConfiguration { HeaderNavOrder = new[] { " Movies ", "", "  " }, ShelfGenreLimit = -1 };
        var errors = ConfigSanitizer.Sanitize(c);
        Assert.Equal(new[] { "Movies" }, c.HeaderNavOrder);
        Assert.Single(errors);
    }

    [Fact]
    public void Groups_RoundTripThroughSanitizeAndTransform()
    {
        var c = new PluginConfiguration
        {
            FeatureHeaderNav = true,
            HeaderNavGroupsJson = " [ { \"label\" : \"Anime\", \"items\": [ [\"Anime\",\"Shows\"], [\"Anime Movies\",\"Movies\"] ] } ] "
        };
        Assert.Empty(ConfigSanitizer.Sanitize(c));
        Assert.Equal("[{\"label\":\"Anime\",\"items\":[[\"Anime\",\"Shows\"],[\"Anime Movies\",\"Movies\"]]}]", c.HeaderNavGroupsJson);

        // Same shape web/header-nav.js documents.
        using var doc = JsonDocument.Parse(IndexTransform.BuildConfigJson(c));
        var g = doc.RootElement.GetProperty("headerNav").GetProperty("groups");
        Assert.Equal(1, g.GetArrayLength());
        Assert.Equal(2, g[0].GetProperty("items").GetArrayLength());
        Assert.Equal("Anime Movies", g[0].GetProperty("items")[1][0].GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[{\"label\":\"A\"}]")]
    [InlineData("[{\"label\":\"\",\"items\":[]}]")]
    [InlineData("[{\"label\":\"A\",\"items\":[[\"x\"]]}]")]
    [InlineData("[{\"label\":\"A\",\"items\":[[\"x\",\"y\",\"z\"]]}]")]
    [InlineData("[{\"label\":\"A\",\"items\":[[\"x\",1]]}]")]
    [InlineData("nope")]
    public void Groups_RejectBadShapes(string json)
    {
        var c = new PluginConfiguration { HeaderNavGroupsJson = json };
        Assert.Single(ConfigSanitizer.Sanitize(c));
        Assert.Empty(HeaderNavGroups.ParseOrEmpty(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    public void Groups_EmptyIsFine(string? json)
    {
        Assert.NotNull(HeaderNavGroups.TryParse(json, out _));
    }

    [Fact]
    public void CollectionRows_AreOffByDefault()
    {
        Assert.Empty(new PluginConfiguration().CollectionRows);
    }

    [Fact]
    public void CollectionRows_AreTrimmedAndBlankNamesDropped_AndLimitZeroTakesTheDefault()
    {
        var c = new PluginConfiguration
        {
            CollectionRows = new[]
            {
                new CollectionRow { Name = "  Leaving soon ", Title = " Going ", Limit = 0 },
                new CollectionRow { Name = "   ", Title = "x", Limit = 5 },
                new CollectionRow { Name = "Picks", Limit = 50 }
            }
        };
        Assert.Empty(ConfigSanitizer.Sanitize(c));
        Assert.Equal(2, c.CollectionRows.Length);
        Assert.Equal("Leaving soon", c.CollectionRows[0].Name);
        Assert.Equal("Going", c.CollectionRows[0].Title);
        Assert.Equal(16, c.CollectionRows[0].Limit);
        Assert.Equal(50, c.CollectionRows[1].Limit);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    public void CollectionRows_RejectALimitOutsideOneToFifty(int limit)
    {
        var c = new PluginConfiguration { CollectionRows = new[] { new CollectionRow { Name = "A", Limit = limit } } };
        Assert.Single(ConfigSanitizer.Sanitize(c));
    }

    [Fact]
    public void CollectionRows_RejectMoreThanFiveAndTooLongText()
    {
        var six = Enumerable.Range(1, 6).Select(i => new CollectionRow { Name = "C" + i }).ToArray();
        Assert.Single(ConfigSanitizer.Sanitize(new PluginConfiguration { CollectionRows = six }));

        var longName = new PluginConfiguration { CollectionRows = new[] { new CollectionRow { Name = new string('x', 101) } } };
        Assert.Single(ConfigSanitizer.Sanitize(longName));
        var longTitle = new PluginConfiguration { CollectionRows = new[] { new CollectionRow { Name = "A", Title = new string('x', 101) } } };
        Assert.Single(ConfigSanitizer.Sanitize(longTitle));
    }

    [Fact]
    public void Configuration_SurvivesAJsonRoundTrip()
    {
        // The settings page posts the whole object as JSON and the controller binds it back.
        var c = new PluginConfiguration { Accent = "#123456", HeaderNavGroupsJson = "[]", BrowseMergeLibraries = new[] { "A", "B" }, ShelfGenreLimit = 3 };
        var back = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(c))!;
        Assert.Equal(IndexTransform.BuildConfigJson(c), IndexTransform.BuildConfigJson(back));
    }
}
