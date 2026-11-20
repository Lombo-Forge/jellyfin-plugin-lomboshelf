using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LomboShelf.Api;

/// <summary>Serves the embedded web files at /LomboShelf/web/{file}. Allow-list only, read-only, anonymous.</summary>
[ApiController]
[AllowAnonymous]
[Route("LomboShelf/web")]
public class WebAssetsController : ControllerBase
{
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["loader.js"] = "text/javascript",
        ["ui.js"] = "text/javascript",
        ["home-shelf.js"] = "text/javascript",
        ["browse-genres.js"] = "text/javascript",
        ["next-season.js"] = "text/javascript",
        ["header-nav.js"] = "text/javascript",
        ["lomboshelf.css"] = "text/css"
    };

    [HttpGet("{file}")]
    public ActionResult Get(string file)
    {
        if (!Allowed.TryGetValue(file, out var type))
        {
            return NotFound();
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + file);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return File(stream, type);
    }
}
