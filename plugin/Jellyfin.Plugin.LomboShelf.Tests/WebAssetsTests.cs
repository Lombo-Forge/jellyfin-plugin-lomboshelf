using System.Reflection;
using Jellyfin.Plugin.LomboShelf.Api;
using Jellyfin.Plugin.LomboShelf.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.LomboShelf.Tests;

public class WebAssetsTests
{
    public static TheoryData<string?> Bad => new()
    {
        null, "", " ", "..", "../loader.js", "..\\loader.js", "%2e%2e/loader.js", "..%2floader.js", "/etc/passwd",
        "web/loader.js", "loader.js/", "loader.js ", "loader.js\0", "LOADER.JS", "Jellyfin.Plugin.LomboShelf.dll",
        "configPage.html", "loader.js.map", "loader.js::$DATA"
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public void Refuses_AnythingNotOnTheList(string? file)
    {
        Assert.False(WebAssets.TryGetContentType(file, out _));
    }

    [Theory]
    [MemberData(nameof(Bad))]
    public void Controller_Returns404_ForAnythingNotOnTheList(string? file)
    {
        Assert.IsType<NotFoundResult>(NewController().Get(file!));
    }

    [Fact]
    public void ListEqualsTheFilesInWebFolder()
    {
        var onDisk = Directory.GetFiles(TestPaths.WebDir()).Select(Path.GetFileName)
            .Where(f => f!.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".css", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(onDisk, WebAssets.Allowed.Keys.OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryAllowedFileIsEmbeddedAndServed()
    {
        var assembly = typeof(Plugin).Assembly;
        foreach (var (file, type) in WebAssets.Allowed)
        {
            Assert.NotNull(assembly.GetManifestResourceStream("web/" + file));
            var result = Assert.IsType<FileStreamResult>(NewController().Get(file));
            Assert.Equal(type, result.ContentType);
            result.FileStream.Dispose();
        }
    }

    [Fact]
    public void EmbeddedFilesAreExactlyTheAllowedOnes()
    {
        var web = typeof(Plugin).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal))
            .Select(n => n.Substring(4)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(WebAssets.Allowed.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray(), web);
    }

    [Fact]
    public void Controller_SetsCacheControl()
    {
        var c = NewController();
        using var r = Assert.IsType<FileStreamResult>(c.Get("loader.js")).FileStream;
        Assert.Equal("public, max-age=3600", c.Response.Headers.CacheControl.ToString());
    }

    private static WebAssetsController NewController() =>
        new() { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
}
