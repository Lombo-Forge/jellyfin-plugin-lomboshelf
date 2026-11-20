namespace Jellyfin.Plugin.LomboShelf.Tests;

internal static class TestPaths
{
    /// <summary>The repository's web/ folder, found by walking up from the test binaries.</summary>
    public static string WebDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var web = Path.Combine(d.FullName, "web");
            if (File.Exists(Path.Combine(web, "loader.js")))
            {
                return web;
            }
        }

        throw new DirectoryNotFoundException("web/loader.js not found above " + AppContext.BaseDirectory);
    }
}
