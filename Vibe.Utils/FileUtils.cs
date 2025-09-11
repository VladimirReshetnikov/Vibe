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
        catch { }
        return null;
    }

    public static string? FindFile(string pattern) =>
        Directory.EnumerateFiles(
            FindRepoRoot() ?? AppContext.BaseDirectory,
            pattern,
            SearchOption.AllDirectories).FirstOrDefault();

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
