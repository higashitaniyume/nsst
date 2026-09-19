using System.Reflection;

namespace Nsst.Server.WebUi;

/// <summary>
/// The compiled single-page console, embedded in the assembly.
/// </summary>
/// <remarks>
/// The resources are linked from <c>web/dist</c>, the directory the web build
/// (<c>npm run build</c> in <c>web/</c>) writes, so the assembly and the console are always
/// built from the same output. Resource names are mapped to URL paths once at startup
/// rather than per request.
/// </remarks>
public sealed class WebUiAssets
{
    private const string ResourcePrefix = "nsst.webui/";

    private readonly Dictionary<string, string> _byPath;

    private WebUiAssets(Dictionary<string, string> byPath) => _byPath = byPath;

    /// <summary>How many files the console bundle contains.</summary>
    public int Count => _byPath.Count;

    /// <summary>
    /// The URL path of every embedded file.
    /// </summary>
    /// <remarks>
    /// Exposed so <see cref="WebUiEndpoints.MapWebUi"/> can check the whole bundle against
    /// the MIME table once at startup rather than discovering a gap on the first request.
    /// </remarks>
    public IReadOnlyCollection<string> Names => _byPath.Keys;

    /// <summary>Builds the index from the manifest resource names of <paramref name="assembly"/>.</summary>
    /// <exception cref="InvalidOperationException">The console was never embedded.</exception>
    public static WebUiAssets Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // MSBuild expands %(RecursiveDir) with the host's directory separator, so a
            // Windows build produces "assets\index.js" and a Linux build produces
            // "assets/index.js". Normalising here keeps the URL paths identical.
            var path = name[ResourcePrefix.Length..].Replace('\\', '/');
            byPath[path] = name;
        }

        if (byPath.Count == 0)
        {
            throw new InvalidOperationException(
                $"the web console was not embedded: no resources beginning with \"{ResourcePrefix}\" were found. " +
                "Run `npm run build` in web/ so that web/dist is populated.");
        }

        return new WebUiAssets(byPath);
    }

    /// <summary>Whether a URL path (relative, forward slashes, no leading slash) exists.</summary>
    public bool Contains(string path) => _byPath.ContainsKey(path);

    /// <summary>Opens a file for reading. The caller owns the returned stream.</summary>
    public Stream Open(string path)
    {
        if (!_byPath.TryGetValue(path, out var resource))
        {
            throw new FileNotFoundException($"no embedded web asset at \"{path}\"", path);
        }

        return typeof(WebUiAssets).Assembly.GetManifestResourceStream(resource)
               ?? throw new FileNotFoundException($"embedded web asset \"{resource}\" could not be opened", path);
    }

    /// <summary>The path of the single-page application's entry document.</summary>
    public const string IndexPath = "index.html";
}
