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
        return plugin is null ? NotFound() : ConfigSanitizer.Redacted(plugin.Configuration);
    }

    [HttpPost]
    public ActionResult Save([FromBody] PluginConfiguration config)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        // A blank token keeps the stored one; ClearFarewellToken clears it. Merged before the checks so "enabled needs a token" sees it.
        config.FarewellToken = ConfigSanitizer.MergeToken(config, plugin.Configuration.FarewellToken);
        config.ClearFarewellToken = false;
        config.FarewellTokenSet = false;
        config.VisaToken = ConfigSanitizer.MergeVisaToken(config, plugin.Configuration.VisaToken);
        config.ClearVisaToken = false;
        config.VisaTokenSet = false;
        var errors = ConfigSanitizer.Sanitize(config);
        if (errors.Count > 0)
        {
            return BadRequest(new { errors });
        }

        plugin.UpdateConfiguration(config);
        return Ok(ConfigSanitizer.Redacted(config));
    }
}
