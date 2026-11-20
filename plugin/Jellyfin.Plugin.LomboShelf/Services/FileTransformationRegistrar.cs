using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LomboShelf.Configuration;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>
/// Registers an index.html transformation with the File Transformation plugin by reflection.
///
/// UNVERIFIED. The registration steps (find the assembly whose name contains ".FileTransformation" in
/// AssemblyLoadContext.All, call static Jellyfin.Plugin.FileTransformation.PluginInterface.RegisterTransformation with a
/// JSON payload {id, fileNamePattern, callbackAssembly, callbackClass, callbackMethod}) come from that plugin's
/// README. The README does not state the callback signature. The shape used here (static method taking an object with
/// a "contents" property and returning the new text) is taken from another plugin that uses it, and the payload is
/// passed as a JObject-like type only if the method asks for one (see BuildArgument). None of this has run
/// against a real File Transformation or Jellyfin.
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
            return IndexTransform.Apply(html, IndexTransform.BuildConfigJson(config));
        }
        catch (Exception)
        {
            return html;
        }
    }

    // Unverified: the README does not say what type RegisterTransformation takes. Guess: a Newtonsoft JObject
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
