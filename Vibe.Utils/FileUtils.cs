namespace Vibe.Utils;

/// <summary>
/// Helper methods for discovering project directories and well-known
/// file locations used by the application.
/// </summary>
public static class FileUtils
{
    /// <summary>
    /// Walks upward from the application's base directory looking for a
    /// <c>.git</c> folder and returns the containing path.
    /// </summary>
    /// <returns>The repository root if found; otherwise <c>null</c>.</returns>
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

    /// <summary>
    /// Searches the repository (or application directory when outside a repo)
    /// for the first file matching the provided glob pattern.
    /// </summary>
    /// <param name="pattern">Glob pattern to match.</param>
    /// <returns>The first matching file path or <c>null</c> if none.</returns>
    public static string? FindFile(string pattern) =>
        Directory.EnumerateFiles(
            FindRepoRoot() ?? AppContext.BaseDirectory,
            pattern,
            SearchOption.AllDirectories).FirstOrDefault();

    /// <summary>
    /// Enumerates directories that may contain cached NuGet packages
    /// based on environment variables and platform conventions.
    /// </summary>
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

