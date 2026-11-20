namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>Hooks index.html transformation into whatever mechanism is available. Result is false when unavailable.</summary>
public interface IFileTransformationRegistrar
{
    bool TryRegister();
}
