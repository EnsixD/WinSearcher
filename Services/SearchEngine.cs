using System.IO;
using WinSearcher.Models;

namespace WinSearcher.Services;

public class SearchEngine
{
    // ── Search roots ────────────────────────────────────────────────────────

    private static readonly string[] SearchRoots;

    private static readonly string[] AllExtensions =
    [
        ".exe", ".lnk", ".url",
        ".pdf", ".docx", ".xlsx", ".pptx", ".txt",
        ".png", ".jpg", ".jpeg", ".mp3", ".mp4",
        ".zip", ".7z", ".rar"
    ];

    static SearchEngine()
    {
        var roots = new List<string>();
        TryAdd(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"));
        TryAdd(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"));
        TryAdd(roots, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        TryAdd(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        TryAdd(roots, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        TryAdd(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        SearchRoots = [.. roots.Distinct()];

        // Start building the in-memory index immediately in the background
        Task.Run(BuildIndex);
    }

    private static void TryAdd(List<string> list, string path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            list.Add(path);
    }

    // ── In-memory file index ─────────────────────────────────────────────────
    // Tuple: (fullPath, displayName lowercased, isDirectory)

    private static volatile IndexEntry[]? _index;

    private record struct IndexEntry(string FullPath, string NameLower, bool IsDir);

    private static void BuildIndex()
    {
        var list = new List<IndexEntry>(4096);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in SearchRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (!IsSearchableExtension(ext)) continue;
                        if (!seen.Add(f)) continue;
                        list.Add(new IndexEntry(f, Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), false));
                    }
                    catch { }
                }
                foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (!seen.Add(d)) continue;
                        list.Add(new IndexEntry(d, Path.GetFileName(d).ToLowerInvariant(), true));
                    }
                    catch { }
                }
            }
            catch { }
        }

        _index = [.. list];
    }

    // ── Public search API ────────────────────────────────────────────────────

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return await Task.Run(() => SearchInternal(query.Trim(), ct), ct);
    }

    private List<SearchResult> SearchInternal(string query, CancellationToken ct)
    {
        string q = query.ToLowerInvariant();
        var scored = new List<(SearchResult r, int score)>(32);
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Fast path: in-memory index ──────────────────────────────────────
        var snapshot = _index;
        if (snapshot != null)
        {
            foreach (var entry in snapshot)
            {
                if (ct.IsCancellationRequested) break;

                int score = ScoreMatch(entry.NameLower, q);
                if (score <= 0) continue;
                if (!seen.Add(entry.FullPath)) continue;

                scored.Add((MakeResult(entry.FullPath, entry.IsDir), entry.IsDir ? score - 5 : score));
            }
        }
        else
        {
            // ── Slow path: enumerate file system (index not ready yet) ──────
            foreach (string root in SearchRoots)
            {
                if (ct.IsCancellationRequested) break;
                ScanDirectory(root, q, scored, seen, ct);
            }
        }

        return scored
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.r.Name.Length)
            .Take(12)
            .Select(x => x.r)
            .ToList();
    }

    // ── File-system fallback (used until index is ready) ────────────────────

    private static void ScanDirectory(
        string directory, string q,
        List<(SearchResult, int)> results, HashSet<string> seen, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (string f in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested || results.Count >= 60) return;
                try
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (!IsSearchableExtension(ext)) continue;
                    string nameLower = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    int score = ScoreMatch(nameLower, q);
                    if (score <= 0 || !seen.Add(f)) continue;
                    if (File.GetAttributes(f).HasFlag(FileAttributes.System)) continue;
                    results.Add((MakeResult(f, false), score));
                }
                catch { }
            }
        }
        catch { }
    }

    // ── Result factory ───────────────────────────────────────────────────────

    private static SearchResult MakeResult(string path, bool isDir)
    {
        string name = isDir
            ? Path.GetFileName(path)
            : Path.GetFileNameWithoutExtension(path);

        string ext = isDir ? "" : Path.GetExtension(path).ToLowerInvariant();

        return new SearchResult
        {
            Name        = name,
            FullPath    = path,
            Description = ShortenPath(Path.GetDirectoryName(path) ?? ""),
            Type        = isDir ? ResultType.Folder : GetResultType(ext),
            Icon        = IconHelper.GetIcon(path)
        };
    }

    // ── Scoring ──────────────────────────────────────────────────────────────

    private static int ScoreMatch(string name, string query)
    {
        if (name == query)                                        return 100;
        if (name.StartsWith(query, StringComparison.Ordinal))    return 80;
        if (name.Contains(query,   StringComparison.Ordinal))    return 50;
        if (WordBoundaryMatch(name, query))                       return 40;
        if (FuzzyMatch(name, query))                              return 20;
        return 0;
    }

    private static bool WordBoundaryMatch(string name, string query)
    {
        bool prevDelim = true;
        int qi = 0;
        foreach (char c in name)
        {
            bool delim = c is ' ' or '_' or '-' or '.' or '(' or ')';
            if (prevDelim && !delim && qi < query.Length && c == query[qi]) qi++;
            else if (prevDelim && !delim) qi = 0;
            prevDelim = delim;
            if (qi == query.Length) return true;
        }
        return false;
    }

    private static bool FuzzyMatch(string name, string query)
    {
        if (query.Length > name.Length) return false;
        int qi = 0;
        foreach (char c in name)
        {
            if (qi < query.Length && c == query[qi]) qi++;
            if (qi == query.Length) return true;
        }
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsSearchableExtension(string ext)
        => AllExtensions.Contains(ext) || ext == "";

    private static ResultType GetResultType(string ext) => ext switch
    {
        ".exe"              => ResultType.App,
        ".lnk" or ".url"   => ResultType.Shortcut,
        _                   => ResultType.File
    };

    private static string ShortenPath(string path)
    {
        const int max = 55;
        if (path.Length <= max) return path;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            path = "~" + path[home.Length..];
        return path.Length <= max ? path : "…" + path[^(max - 1)..];
    }
}
