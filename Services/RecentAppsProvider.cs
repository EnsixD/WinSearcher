using System.IO;
using WinSearcher.Models;

namespace WinSearcher.Services;

public class RecentAppsProvider
{
    private static readonly string[] RecentFolders =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Recent)),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Recent")
    ];

    public async Task<List<SearchResult>> GetRecentAppsAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<(SearchResult result, DateTime lastWrite)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in RecentFolders)
            {
                if (!Directory.Exists(folder)) continue;

                try
                {
                    var files = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.EnumerateFiles(folder, "*.url", SearchOption.TopDirectoryOnly));

                    foreach (string file in files)
                    {
                        try
                        {
                            string? targetPath = ResolveShortcut(file);
                            if (string.IsNullOrEmpty(targetPath)) continue;

                            // Skip system folders and non-existent targets
                            if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) continue;

                            // Deduplicate by target path
                            if (!seen.Add(targetPath)) continue;

                            // Skip if it's a temp or system file
                            if (IsSystemOrTemp(targetPath)) continue;

                            string name = Path.GetFileNameWithoutExtension(file);
                            var lastWrite = new FileInfo(file).LastWriteTime;

                            bool isDir = Directory.Exists(targetPath);
                            var type = isDir ? ResultType.Folder : GetResultType(targetPath);

                            var result = new SearchResult
                            {
                                Name = name,
                                FullPath = targetPath,
                                Description = ShortenPath(targetPath),
                                Type = ResultType.Recent,
                                Icon = IconHelper.GetIcon(targetPath)
                            };

                            results.Add((result, lastWrite));
                        }
                        catch { /* skip problematic files */ }
                    }
                }
                catch { /* skip inaccessible folders */ }
            }

            return results
                .OrderByDescending(x => x.lastWrite)
                .Take(10)
                .Select(x => x.result)
                .ToList();
        });
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            // Use Windows Script Host to resolve .lnk files
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;

            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            // Fallback: just return the lnk path itself
            return lnkPath;
        }
    }

    private static bool IsSystemOrTemp(string path)
    {
        string lower = path.ToLowerInvariant();
        return lower.Contains(@"\windows\temp\") ||
               lower.Contains(@"\appdata\local\temp\") ||
               lower.Contains(@"\$recycle.bin\");
    }

    private static ResultType GetResultType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".exe" => ResultType.App,
            ".lnk" => ResultType.Shortcut,
            _ => ResultType.File
        };
    }

    private static string ShortenPath(string path)
    {
        const int maxLen = 60;
        if (path.Length <= maxLen) return path;

        string root = Path.GetPathRoot(path) ?? "";
        string fileName = Path.GetFileName(path);
        string middle = "...\\";

        int available = maxLen - root.Length - fileName.Length - middle.Length;
        if (available <= 0) return $"{root}{middle}{fileName}";

        string dir = Path.GetDirectoryName(path) ?? "";
        dir = dir.Length > root.Length ? dir[root.Length..] : "";
        if (dir.Length > available)
            dir = dir[..available];

        return $"{root}{dir}{middle}{fileName}";
    }
}

// Needed for COM interop
file static class Marshal
{
    public static void ReleaseComObject(object obj)
    {
        System.Runtime.InteropServices.Marshal.ReleaseComObject(obj);
    }
}
