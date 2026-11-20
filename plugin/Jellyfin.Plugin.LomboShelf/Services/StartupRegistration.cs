using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>On server start, registers the index.html transformation. If it cannot, logs it and does nothing else.</summary>
public class StartupRegistration : IHostedService
{
    private readonly IFileTransformationRegistrar _registrar;
    private readonly ILogger<StartupRegistration> _logger;

    public StartupRegistration(IFileTransformationRegistrar registrar, ILogger<StartupRegistration> logger)
    {
        _registrar = registrar;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_registrar.TryRegister())
        {
            _logger.LogInformation("LomboShelf: index.html transformation registered");
        }
        else
        {
            _logger.LogWarning("LomboShelf: File Transformation plugin not found or registration failed; the web files will not be injected");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
