using System.IO.Compression;

namespace Vibe.Utils;

/// <summary>
/// TODO
/// </summary>
public static class FileUtils
{
    public static string? FindRepoRoot()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;
                string? parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
        }
        return null;
    }

    public static string? FindFile(string pattern) =>
        Directory.EnumerateFiles(
            FindRepoRoot() ?? AppContext.BaseDirectory,
            pattern,
            SearchOption.AllDirectories).FirstOrDefault();

    /// <summary>
    /// Attempts to populate the constant database with Win32 metadata so that APIs and constants
    /// can be rendered with meaningful names during decompilation. The method searches standard
    /// locations such as the Windows SDK and the local NuGet cache.
    /// </summary>
    /// <param name="loader">The callback from the database that receives the metadata.</param>
    public static void TryLoadWin32Metadata(Action<string> loader)
    {
        try
        {
            var winmdRegistry = FileUtils.FindFile("Windows.Win32.winmd");
            if (winmdRegistry is not null)
            {
                loader(winmdRegistry);
                return;
            }

            foreach (var cache in FileUtils.GetNuGetCacheDirectories())
            {
                if (!Directory.Exists(cache)) continue;

                // First try extracted packages (more common in global packages folder)
                foreach (var packageDir in Directory.EnumerateDirectories(cache, "microsoft.windows.sdk.win32metadata*",
                             SearchOption.TopDirectoryOnly))
                {
                    foreach (var winmdFile in Directory.EnumerateFiles(packageDir, "Windows.Win32.winmd",
                                 SearchOption.AllDirectories))
                    {
                        loader(winmdFile);
                        return;
                    }
                }

                // Fallback to .nupkg files (for HTTP cache locations)
                foreach (var nupkg in Directory.EnumerateFiles(cache, "Microsoft.Windows.SDK.Win32Metadata*.nupkg",
                             SearchOption.AllDirectories))
                {
                        try
                        {
                            using var zip = ZipFile.OpenRead(nupkg);
                            var entry = zip.Entries.FirstOrDefault(e =>
                                e.FullName.EndsWith("Windows.Win32.winmd", StringComparison.OrdinalIgnoreCase));
                            if (entry is null) continue;
                            string tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".winmd");
                            entry.ExtractToFile(tempPath, true);
                            try
                            {
                                loader(tempPath);
                            }
                            finally
                            {
                                try { File.Delete(tempPath); } catch (Exception ex) { Logger.LogException(ex); }
                            }
                            return;
                        }
                        catch (Exception ex)
                        {
                            Logger.LogException(ex);
                        }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
        }
    }

    public static IEnumerable<string> GetNuGetCacheDirectories()
    {
        var dirs = new List<string>();
        string? env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env))
            dirs.Add(env);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            dirs.Add(Path.Combine(profile, ".nuget", "packages"));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            dirs.Add(Path.Combine(local, "NuGet", "Cache"));
            dirs.Add(Path.Combine(local, "NuGet", "v3-cache"));
        }

        return dirs;
    }
}
