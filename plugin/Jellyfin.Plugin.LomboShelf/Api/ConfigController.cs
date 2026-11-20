using Jellyfin.Plugin.LomboShelf.Configuration;
using Jellyfin.Plugin.LomboShelf.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LomboShelf.Api;

/// <summary>Reads and saves the settings for the settings page. Administrators only; saved settings are checked first.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("LomboShelf/config")]
public class ConfigController : ControllerBase
{
    [HttpGet]
    public ActionResult<PluginConfiguration> Get()
    {
        var plugin = Plugin.Instance;
        return plugin is null ? NotFound() : plugin.Configuration;
    }

    [HttpPost]
    public ActionResult Save([FromBody] PluginConfiguration config)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var errors = ConfigSanitizer.Sanitize(config);
        if (errors.Count > 0)
        {
            return BadRequest(new { errors });
        }

        plugin.UpdateConfiguration(config);
        return Ok(config);
    }
}
