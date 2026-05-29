using System.Diagnostics;

var options = CleanupOptions.Parse(args);
if (string.IsNullOrWhiteSpace(options.RootPath))
{
    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    options.RootPath = Path.Combine(localAppData, "TeslaCamViewer");
}

try
{
    WaitForOwnerExit(options.WaitPid);
    RunCleanup(options);
}
catch (Exception ex)
{
    Log(options.RootPath, ex.ToString());
}

static void WaitForOwnerExit(int waitPid)
{
    if (waitPid <= 0) return;

    try
    {
        using Process process = Process.GetProcessById(waitPid);
        process.WaitForExit(30000);
    }
    catch { }
}

static void RunCleanup(CleanupOptions options)
{
    Directory.CreateDirectory(options.RootPath);

    string tempRoot = Path.Combine(options.RootPath, "StitchTemp");
    string cacheRoot = Path.Combine(options.RootPath, "StitchedDrives");

    CleanupTempRoot(tempRoot, options.CutoffUtc);
    CleanupTempFiles(cacheRoot, options.CutoffUtc);
    CleanupOldCacheDirectories(cacheRoot, options.CutoffUtc, options.MaxAgeDays);
    EnforceCacheBudget(cacheRoot, options.CutoffUtc, options.MaxBytes, options.MinFreeBytes);
}

static void CleanupTempRoot(string tempRoot, DateTime cutoffUtc)
{
    if (!Directory.Exists(tempRoot)) return;

    foreach (string dir in SafeEnumerateDirectories(tempRoot))
    {
        DeleteDirectoryIfStale(dir, cutoffUtc);
    }

    foreach (string file in SafeEnumerateFiles(tempRoot, "*", SearchOption.TopDirectoryOnly))
    {
        DeleteFileIfStale(file, cutoffUtc);
    }

    TryDeleteEmptyDirectory(tempRoot);
}

static void CleanupTempFiles(string cacheRoot, DateTime cutoffUtc)
{
    if (!Directory.Exists(cacheRoot)) return;

    foreach (string file in SafeEnumerateFiles(cacheRoot, "*.tmp.mp4", SearchOption.AllDirectories))
    {
        DeleteFileIfStale(file, cutoffUtc);
    }
}

static void CleanupOldCacheDirectories(string cacheRoot, DateTime cutoffUtc, int maxAgeDays)
{
    if (!Directory.Exists(cacheRoot)) return;

    DateTime ageCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, maxAgeDays));
    foreach (string dir in SafeEnumerateDirectories(cacheRoot))
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (info.LastAccessTimeUtc <= ageCutoff && info.LastWriteTimeUtc <= cutoffUtc)
            {
                TryDeleteDirectory(dir);
            }
        }
        catch { }
    }
}

static void EnforceCacheBudget(string cacheRoot, DateTime cutoffUtc, long maxBytes, long minFreeBytes)
{
    if (!Directory.Exists(cacheRoot)) return;

    var dirs = SafeEnumerateDirectories(cacheRoot)
        .Select(path => new DirectoryInfo(path))
        .Select(info => new CacheDirectory(info, GetDirectorySize(info.FullName)))
        .OrderBy(item => item.LastAccessUtc)
        .ToList();

    long totalBytes = dirs.Sum(item => item.SizeBytes);
    foreach (CacheDirectory dir in dirs)
    {
        if (totalBytes <= maxBytes && HasMinimumFreeSpace(cacheRoot, minFreeBytes))
        {
            break;
        }

        if (dir.LastWriteUtc > cutoffUtc)
        {
            continue;
        }

        totalBytes -= dir.SizeBytes;
        TryDeleteDirectory(dir.Path);
    }
}

static void DeleteDirectoryIfStale(string path, DateTime cutoffUtc)
{
    try
    {
        var info = new DirectoryInfo(path);
        if (info.LastWriteTimeUtc <= cutoffUtc)
        {
            TryDeleteDirectory(path);
        }
    }
    catch { }
}

static void DeleteFileIfStale(string path, DateTime cutoffUtc)
{
    try
    {
        var info = new FileInfo(path);
        if (info.LastWriteTimeUtc <= cutoffUtc)
        {
            TryDeleteFile(path);
        }
    }
    catch { }
}

static IEnumerable<string> SafeEnumerateDirectories(string path)
{
    try { return Directory.EnumerateDirectories(path).ToList(); }
    catch { return Array.Empty<string>(); }
}

static IEnumerable<string> SafeEnumerateFiles(string path, string pattern, SearchOption searchOption)
{
    try { return Directory.EnumerateFiles(path, pattern, searchOption).ToList(); }
    catch { return Array.Empty<string>(); }
}

static long GetDirectorySize(string path)
{
    long total = 0;
    foreach (string file in SafeEnumerateFiles(path, "*", SearchOption.AllDirectories))
    {
        try { total += new FileInfo(file).Length; }
        catch { }
    }

    return total;
}

static bool HasMinimumFreeSpace(string cacheRoot, long minFreeBytes)
{
    try
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(cacheRoot));
        if (string.IsNullOrWhiteSpace(root)) return true;

        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace >= minFreeBytes;
    }
    catch
    {
        return true;
    }
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch { }
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch { }
}

static void TryDeleteEmptyDirectory(string path)
{
    try
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }
    catch { }
}

static void Log(string rootPath, string message)
{
    try
    {
        Directory.CreateDirectory(rootPath);
        string logPath = Path.Combine(rootPath, "cleanup.log");
        File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
    catch { }
}

sealed record CacheDirectory(DirectoryInfo Info, long SizeBytes)
{
    public string Path => Info.FullName;
    public DateTime LastAccessUtc => Info.LastAccessTimeUtc;
    public DateTime LastWriteUtc => Info.LastWriteTimeUtc;
}

sealed class CleanupOptions
{
    private const int DefaultMaxAgeDays = 14;
    private const long DefaultMaxBytes = 64L * 1024L * 1024L * 1024L;
    private const long DefaultMinFreeBytes = 12L * 1024L * 1024L * 1024L;

    public string RootPath { get; set; } = "";
    public DateTime CutoffUtc { get; set; } = DateTime.UtcNow;
    public int WaitPid { get; set; }
    public int MaxAgeDays { get; set; } = DefaultMaxAgeDays;
    public long MaxBytes { get; set; } = DefaultMaxBytes;
    public long MinFreeBytes { get; set; } = DefaultMinFreeBytes;

    public static CleanupOptions Parse(string[] args)
    {
        var options = new CleanupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string value = i + 1 < args.Length ? args[i + 1] : "";

            if (arg.Equals("--root", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                options.RootPath = value;
                i++;
            }
            else if (arg.Equals("--cutoff-utc-ticks", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out long ticks))
            {
                options.CutoffUtc = new DateTime(ticks, DateTimeKind.Utc);
                i++;
            }
            else if (arg.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int waitPid))
            {
                options.WaitPid = waitPid;
                i++;
            }
            else if (arg.Equals("--max-age-days", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int maxAgeDays))
            {
                options.MaxAgeDays = maxAgeDays;
                i++;
            }
            else if (arg.Equals("--max-bytes", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out long maxBytes))
            {
                options.MaxBytes = maxBytes;
                i++;
            }
            else if (arg.Equals("--min-free-bytes", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out long minFreeBytes))
            {
                options.MinFreeBytes = minFreeBytes;
                i++;
            }
        }

        return options;
    }
}
