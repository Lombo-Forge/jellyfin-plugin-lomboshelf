using Jellyfin.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.LomboShelf.Services;

public enum ItemKind
{
    Other,
    Movie,
    Season,
    Episode
}

/// <summary>What the routes need to know about an item for one user. Null from <see cref="IItemAccess.Find"/> means unknown or not visible.</summary>
public sealed record ItemFacts(ItemKind Kind, string? Path, bool CallerIsAdmin);

/// <summary>The seam in front of Jellyfin's library, so the routes can be tested without a server.</summary>
public interface IItemAccess
{
    ItemFacts? Find(Guid itemId, Guid userId);
}

public sealed class JellyfinItemAccess : IItemAccess
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    public JellyfinItemAccess(ILibraryManager library, IUserManager users)
    {
        _library = library;
        _users = users;
    }

    public ItemFacts? Find(Guid itemId, Guid userId)
    {
        var user = _users.GetUserById(userId);
        var item = user is null ? null : _library.GetItemById(itemId);
        if (user is null || item is null || !item.IsVisible(user))
        {
            return null;
        }

        var kind = item switch
        {
            Movie => ItemKind.Movie,
            Season => ItemKind.Season,
            Episode => ItemKind.Episode,
            _ => ItemKind.Other
        };
        return new ItemFacts(kind, item.Path, user.HasPermission(PermissionKind.IsAdministrator));
    }
}
