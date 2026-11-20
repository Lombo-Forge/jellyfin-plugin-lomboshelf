using System.Reflection;
using Jellyfin.Plugin.LomboShelf.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LomboShelf.Api;

/// <summary>Serves the embedded web files at /LomboShelf/web/{file}. Allow-list only, read-only, anonymous.</summary>
[ApiController]
[AllowAnonymous]
[Route("LomboShelf/web")]
public class WebAssetsController : ControllerBase
{
    [HttpGet("{file}")]
    public ActionResult Get(string file)
    {
        if (!WebAssets.TryGetContentType(file, out var type))
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
