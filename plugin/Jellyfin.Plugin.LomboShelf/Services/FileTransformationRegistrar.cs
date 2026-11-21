using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LomboShelf.Configuration;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>
/// Registers an index.html transformation with the File Transformation plugin by reflection: find the assembly whose
/// name contains ".FileTransformation" in AssemblyLoadContext.All and call the static
/// Jellyfin.Plugin.FileTransformation.PluginInterface.RegisterTransformation(JObject) with the payload
/// {id, fileNamePattern, callbackAssembly, callbackClass, callbackMethod}. File Transformation then calls the static
/// <see cref="Transform"/> with {"contents": "..."} (Newtonsoft matches the property name without regard to case) and
/// replaces the page with the returned string.
/// Checked once: the contract above was read in File Transformation 3.0.3.0's source, and the whole path ran in a
/// throwaway Jellyfin 12.4.0 on 2026-11-21. Not checked: File Transformation loading after this plugin, other versions.
/// </summary>
public class FileTransformationRegistrar : IFileTransformationRegistrar
{
    public static readonly Guid TransformationId = Guid.Parse("a3d7e2f0-58c1-4b6e-8f19-0c7b4d2e6a35");

    public bool TryRegister()
    {
        try
        {
            var ftAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
            var method = ftAssembly?
                .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?
                .GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            if (method is null)
            {
                return false;
            }

            var payload = new JsonObject
            {
                ["id"] = TransformationId.ToString(),
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = typeof(FileTransformationRegistrar).Assembly.FullName,
                ["callbackClass"] = typeof(FileTransformationRegistrar).FullName,
                ["callbackMethod"] = nameof(Transform)
            };

            method.Invoke(null, new[] { BuildArgument(method, payload) });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The callback File Transformation is told to call. Fails open: returns the input on any error.</summary>
    public static string Transform(PatchRequestPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return IndexTransform.Apply(html, IndexTransform.BuildConfigJson(config), version: Plugin.Instance?.Version.ToString());
        }
        catch (Exception)
        {
            return html;
        }
    }

    // File Transformation 3.0.3.0 takes a Newtonsoft JObject (read in its source); any other parameter type gets the JSON string.
    // (Jellyfin ships Newtonsoft.Json), otherwise the JSON string.
    private static object BuildArgument(MethodInfo method, JsonObject payload)
    {
        var p = method.GetParameters().FirstOrDefault()?.ParameterType;
        if (p is not null && p.Name == "JObject")
        {
            return p.GetMethod("Parse", new[] { typeof(string) })!.Invoke(null, new object[] { payload.ToJsonString() })!;
        }

        return payload.ToJsonString();
    }
}
