using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace TeslaCamViewer
{
    public partial class MainWindow : Window
    {
        // --- DATA STATE ---
        private string _activeCategory = "RecentClips";
        private List<TeslaClip> _allClips = new List<TeslaClip>();
        private List<TeslaClip> _filteredClips = new List<TeslaClip>();
        private TeslaClip _activeClip;
        private TeslaClipSegment _activeSegment;
        private List<TeslaClipSegment> _activePlaybackSegments = new List<TeslaClipSegment>();
        private int _clipSelectionVersion = 0;
        private CancellationTokenSource _stitchCancellation = new CancellationTokenSource();
        private readonly object _ffmpegProcessLock = new object();
        private readonly List<Process> _activeFfmpegProcesses = new List<Process>();
        private FileSystemWatcher _teslaCamWatcher;
        private DispatcherTimer _clipRefreshDebounceTimer;
        private CancellationTokenSource _clipTelemetrySummaryCancellation = new CancellationTokenSource();
        private CancellationTokenSource _disengagementMarkerCancellation = new CancellationTokenSource();
        private string _currentSourcePath = "";
        private string _watchedTeslaCamPath = "";
        private int _scanRequestVersion = 0;
        private bool _isAutoAdvancing = false;
        private bool _isCollageMode = false;
        private bool _isSyncingClipSelection = false;
        private bool _isResizingSidebar = false;
        private bool _isSidebarResizeEdgePointerOver = false;
        private double _sidebarResizeStartX = 0.0;
        private double _sidebarResizeStartWidth = 320.0;
        private readonly HashSet<string> _thumbnailJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _thumbnailSemaphore = new SemaphoreSlim(1, 1);
        private readonly object _clipTelemetrySummaryCacheLock = new object();
        private readonly Dictionary<string, AutopilotTelemetrySummary> _clipTelemetrySummaryCache = new Dictionary<string, AutopilotTelemetrySummary>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _clipTelemetrySummaryCacheAccessTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly object _clipTelemetrySummaryCacheSaveLock = new object();
        private CancellationTokenSource _clipTelemetrySummaryCacheSaveCancellation = new CancellationTokenSource();
        private bool _clipTelemetrySummaryCacheDirty = false;
        private List<SeiMetadata> _activeTelemetry = new List<SeiMetadata>();
        private List<double> _activeDisengagementMarkerSeconds = new List<double>();
        private List<double> _activeSegmentStarts = new List<double>();
        private List<double> _activeSegmentDurations = new List<double>();
        private int _activeSegmentIndex = -1;
        private double _activeClipDurationSeconds = 0.0;
        
        // Maps current camera role to the active MediaPlayerElement
        private Dictionary<string, MediaPlayerElement> _auxPlayers = new Dictionary<string, MediaPlayerElement>();
        private string _mainAngle = "front"; // tracks which camera angle is on the main player
        
        // Timer for high frequency HUD ticks and player sync
        private DispatcherTimer _hudTimer;
        private bool _isSliderDragging = false;
        private bool _resumePlaybackAfterScrub = false;
        private bool _isUpdatingTimeline = false;
        private bool _isLoadingSegment = false;
        private bool _isPlaybackActivityVisible = false;
        private bool _isStitchActivityVisible = false;
        private bool _isExportActivityVisible = false;
        private bool _isExporting = false;
        private string _playbackActivityText = "";
        private string _stitchActivityText = "";
        private string _exportActivityText = "";
        private double? _exportStartSeconds = null;
        private double? _exportEndSeconds = null;
        private int _segmentLoadVersion = 0;
        private double _playbackRate = 1.0;
        private double _activeLat = 0.0;
        private double _activeLon = 0.0;
        private bool _isWindowClosing = false;

        private const double SoftSyncThresholdSec = 0.06;
        private const double HardSyncThresholdSec = 0.45;
        private const double RecentClipSessionGapSeconds = 90;
        private const double SidebarMinWidth = 300.0;
        private const double SidebarMaxWidth = 720.0;
        private const double MainContentMinWidth = 760.0;
        private const int IdcArrow = 32512;
        private const int IdcSizeWestEast = 32644;
        private const int StitchCacheMaxAgeDays = 14;
        private const int ClipTelemetrySummaryMaxParallelism = 6;
        private const int ClipTelemetrySummaryCacheMaxEntries = 5000;
        private const int ClipTelemetrySummaryCacheSaveDelayMs = 1500;
        private const long StitchCacheMaxBytes = 64L * 1024L * 1024L * 1024L;
        private const long StitchCacheMinFreeBytes = 12L * 1024L * 1024L * 1024L;

        // Blinker flash helper
        private bool _blinkerFlashState = false;
        private DispatcherTimer _blinkerTimer;

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        public MainWindow()
        {
            this.InitializeComponent();
            Closed += MainWindow_Closed;

            try
            {
                this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
            catch
            {
                // The XAML fallback background still carries the glass theme on unsupported hosts.
            }
            ConfigureNativeTitleBar();
            CleanupOwnedFfmpegProcesses();
            QueueStartupCleanup();
            LoadClipTelemetrySummaryCache();

            // Register KeyDown handler on Content Grid (since Window doesn't support KeyDown directly)
            var rootGrid = this.Content as Grid;
            if (rootGrid != null)
            {
                rootGrid.KeyDown += Window_KeyDown;
            }

            // CRITICAL: Pre-initialize every MediaPlayerElement with a dedicated MediaPlayer.
            // In WinUI 3, MediaPlayer is NOT auto-created — calling .MediaPlayer before
            // SetMediaPlayer() returns null and crashes the XAML engine (0xc000027b).
            MainPlayer.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            PlayerBack.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            PlayerLeftRepeater.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            PlayerRightRepeater.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            PlayerLeftPillar.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            PlayerRightPillar.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());

            // Auto-advance through each drive's internal one-minute files.
            MainPlayer.MediaPlayer.MediaEnded += (sender, e) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (_isWindowClosing) return;

                        if (_activePlaybackSegments != null && _activeSegmentIndex >= 0 && _activeSegmentIndex < _activePlaybackSegments.Count - 1)
                        {
                            SelectClipSegment(_activePlaybackSegments[_activeSegmentIndex + 1], wasPlaying: true);
                        }
                        else if (ClipsListView != null && ClipsListView.SelectedIndex > 0)
                        {
                            _isAutoAdvancing = true;
                            ClipsListView.SelectedIndex = ClipsListView.SelectedIndex - 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log("MediaEnded auto advance", ex);
                    }
                });
            };

            // Setup high-performance HUD ticker
            _hudTimer = new DispatcherTimer();
            _hudTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 fps
            _hudTimer.Tick += HudTimer_Tick;

            // Setup blinker flasher timer
            _blinkerTimer = new DispatcherTimer();
            _blinkerTimer.Interval = TimeSpan.FromMilliseconds(350);
            _blinkerTimer.Tick += (s, e) => { _blinkerFlashState = !_blinkerFlashState; };
            _blinkerTimer.Start();

            _clipRefreshDebounceTimer = new DispatcherTimer();
            _clipRefreshDebounceTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _clipRefreshDebounceTimer.Tick += ClipRefreshDebounceTimer_Tick;

            TimelineScrubberHost.SizeChanged += (s, e) => UpdateTimelineScrubberVisual();

            ResetCameraLayout();

            // Auto-detect TeslaCam folder on launch
            AutoDetectTeslaCam(initialScan: true);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _isWindowClosing = true;
            CancelActiveStitch();
            CancelClipTelemetrySummaryScan();
            CancelDisengagementMarkerScan();
            FlushClipTelemetrySummaryCache();

            try { _hudTimer?.Stop(); } catch { }
            try { _blinkerTimer?.Stop(); } catch { }
            try { _clipRefreshDebounceTimer?.Stop(); } catch { }
            StopTeslaCamWatcher();

            LaunchCleanupHelper(waitForCurrentProcess: true);
        }

        private void ConfigureNativeTitleBar()
        {
            Title = "";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);

            try
            {
                AppWindow.Title = "";

                if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    return;
                }

                var titleBar = AppWindow.TitleBar;
                var transparent = Windows.UI.Color.FromArgb(0, 255, 255, 255);
                var captionForeground = Windows.UI.Color.FromArgb(255, 248, 250, 252);
                var captionDisabled = Windows.UI.Color.FromArgb(130, 148, 163, 184);

                titleBar.BackgroundColor = transparent;
                titleBar.InactiveBackgroundColor = transparent;
                titleBar.ForegroundColor = captionForeground;
                titleBar.InactiveForegroundColor = captionDisabled;
                titleBar.ButtonBackgroundColor = transparent;
                titleBar.ButtonInactiveBackgroundColor = transparent;
                titleBar.ButtonForegroundColor = captionForeground;
                titleBar.ButtonInactiveForegroundColor = captionDisabled;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(42, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(72, 255, 255, 255);
                titleBar.ButtonHoverForegroundColor = captionForeground;
                titleBar.ButtonPressedForegroundColor = captionForeground;
            }
            catch
            {
                // Title bar coloring is optional; blank Title still removes the duplicate caption text.
            }
        }

        // --- SCAN LOGIC ---
        private void TriggerScan(string path)
        {
            path = (path ?? "").Trim();
            int scanVersion = Interlocked.Increment(ref _scanRequestVersion);
            CancelClipTelemetrySummaryScan();
            bool isArchive = IsSupportedArchivePath(path);
            bool isDirectory = Directory.Exists(path);
            bool pathChanged = !string.Equals(path, _currentSourcePath, StringComparison.OrdinalIgnoreCase);

            if (!isDirectory && !isArchive)
            {
                if (string.Equals(path, _watchedTeslaCamPath, StringComparison.OrdinalIgnoreCase))
                {
                    StopTeslaCamWatcher();
                }

                EmptyStateText.Visibility = Visibility.Visible;
                EmptyStateText.Text = "TeslaCam source unavailable. Choose a folder or a .zip archive.";
                ScanProgressRing.IsActive = false;
                SetAppStatus("Source unavailable", false);
                return;
            }

            _currentSourcePath = path;
            if (isArchive)
            {
                StopTeslaCamWatcher();
                _watchedTeslaCamPath = "";
                SetAppStatus("Extracting compressed source", true);
                ActiveClipSubtitle.Text = "Extracting compressed TeslaCam source...";
            }
            else
            {
                ConfigureTeslaCamWatcher(path);
                SetAppStatus("Scanning TeslaCam source", true);
            }

            EmptyStateText.Visibility = Visibility.Collapsed;
            ScanProgressRing.IsActive = true;
            if (pathChanged)
            {
                _allClips.Clear();
                _filteredClips.Clear();
                ClipsListView.ItemsSource = _filteredClips;
                ClipsCollageView.ItemsSource = _filteredClips;
            }

            Task.Run(() =>
            {
                try
                {
                string scanRoot = path;
                if (isArchive)
                {
                    scanRoot = ResolveTeslaCamScanRoot(ExtractArchiveToCache(path));
                }

                List<TeslaClip> loaded = LoadClipsFromDirectory(scanRoot);
                if (isArchive && loaded.Count == 0)
                {
                    loaded = BuildArchiveVideoClips(scanRoot, path);
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isWindowClosing || scanVersion != _scanRequestVersion) return;
                    _allClips = loaded;
                    ScanProgressRing.IsActive = false;
                    if (isArchive && loaded.Count > 0 && !loaded.Any(c => string.Equals(c.Category, _activeCategory, StringComparison.OrdinalIgnoreCase)))
                    {
                        SetActiveCategory(loaded[0].Category);
                    }

                    ActiveClipSubtitle.Text = isArchive
                        ? $"Loaded compressed source: {Path.GetFileName(path)}"
                        : ActiveClipSubtitle.Text;
                    FilterAndRenderClips();
                    StartClipTelemetrySummaryScan(loaded, scanVersion);
                });
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("Scan task", ex);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isWindowClosing || scanVersion != _scanRequestVersion) return;
                        ScanProgressRing.IsActive = false;
                        EmptyStateText.Visibility = Visibility.Visible;
                        EmptyStateText.Text = "Scan failed. See crash.txt for details.";
                        SetAppStatus("Scan failed", false);
                    });
                }
            });
        }

        private List<TeslaClip> LoadClipsFromDirectory(string path)
        {
            List<TeslaClip> loaded = new List<TeslaClip>();

            // Scan directories: RecentClips, SavedClips, SentryClips
            string[] categories = new string[] { "RecentClips", "SavedClips", "SentryClips" };
            string[] suffixes = GetCameraOrder();

            foreach (var cat in categories)
            {
                string catPath = Path.Combine(path, cat);
                if (!Directory.Exists(catPath)) continue;

                if (cat == "RecentClips")
                {
                    var files = Directory.GetFiles(catPath, "*.mp4");
                    var dict = BuildSegmentDictionaryFromFiles(files, suffixes);

                    // Group continuous one-minute TeslaCam files into drive sessions.
                    var sortedSegments = dict.Values.OrderBy(s => s.Timestamp).ToList();
                    loaded.AddRange(BuildRecentClipSessions(sortedSegments, cat));
                }
                else
                {
                    var eventDirs = Directory.GetDirectories(catPath);
                    foreach (var ed in eventDirs)
                    {
                        var files = Directory.GetFiles(ed, "*.mp4");
                        var dict = BuildSegmentDictionaryFromFiles(files, suffixes);

                        if (dict.Count > 0)
                        {
                            var sortedSegs = dict.Values.OrderBy(s => s.Timestamp).ToList();
                            var firstTimestamp = sortedSegs[0].Timestamp;
                            var lastTimestamp = sortedSegs[sortedSegs.Count - 1].Timestamp;
                            string eventReason = ReadEventReason(ed);
                            var display = BuildClipDisplayMetadata(firstTimestamp, lastTimestamp, sortedSegs.Count, eventReason);

                            var eventClip = new TeslaClip
                            {
                                Timestamp = Path.GetFileName(ed), // use event directory name for sorting
                                Category = cat,
                                Title = display.Title,
                                DateText = display.DateText,
                                TimeRangeText = display.TimeRangeText,
                                DurationText = display.DurationText,
                                ClipTypeText = display.TypeText,
                                Segments = sortedSegs
                            };
                            loaded.Add(eventClip);
                        }
                    }
                }
            }

            return loaded.OrderByDescending(c => c.Timestamp).ToList();
        }

        private Dictionary<string, TeslaClipSegment> BuildSegmentDictionaryFromFiles(IEnumerable<string> files, string[] suffixes)
        {
            var dict = new Dictionary<string, TeslaClipSegment>();
            foreach (var f in files)
            {
                if (!TryParseTeslaClipFileName(f, suffixes, out string timestamp, out string cam))
                {
                    continue;
                }

                if (!dict.ContainsKey(timestamp))
                {
                    dict[timestamp] = new TeslaClipSegment
                    {
                        Timestamp = timestamp,
                        Cameras = new Dictionary<string, string>()
                    };
                }
                dict[timestamp].Cameras[cam] = f;
            }

            return dict;
        }

        private bool TryParseTeslaClipFileName(string filePath, string[] suffixes, out string timestamp, out string camera)
        {
            timestamp = null;
            camera = null;
            string fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            foreach (var suffix in suffixes)
            {
                string marker = $"-{suffix}.mp4";
                if (fileName.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    timestamp = fileName.Substring(0, fileName.Length - marker.Length);
                    camera = suffix;
                    return !string.IsNullOrWhiteSpace(timestamp);
                }
            }

            return false;
        }

        private string ReadEventReason(string eventDirectory)
        {
            string jsonPath = Path.Combine(eventDirectory, "event.json");
            if (!File.Exists(jsonPath))
            {
                return "Event Alert";
            }

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath)))
                {
                    if (doc.RootElement.TryGetProperty("reason", out var p))
                    {
                        string reason = p.GetString();
                        if (!string.IsNullOrWhiteSpace(reason))
                        {
                            return reason;
                        }
                    }
                }
            }
            catch { }

            return "Event Alert";
        }

        private bool IsSupportedArchivePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   File.Exists(path) &&
                   string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
        }

        private string ExtractArchiveToCache(string archivePath)
        {
            var archiveInfo = new FileInfo(archivePath);
            if (!archiveInfo.Exists)
            {
                throw new FileNotFoundException("Archive source was not found.", archivePath);
            }

            string cacheKeyInput = $"{archiveInfo.FullName}|{archiveInfo.Length}|{archiveInfo.LastWriteTimeUtc.Ticks}";
            string cacheKey;
            using (var sha = SHA256.Create())
            {
                cacheKey = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(cacheKeyInput))).Substring(0, 24).ToLowerInvariant();
            }

            string extractRoot = Path.Combine(GetArchiveImportCacheRoot(), cacheKey);
            string completeMarker = Path.Combine(extractRoot, ".complete");
            if (Directory.Exists(extractRoot) && File.Exists(completeMarker))
            {
                return extractRoot;
            }

            TryDeleteDirectory(extractRoot);
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(archivePath, extractRoot, overwriteFiles: true);
            File.WriteAllText(completeMarker, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), Encoding.UTF8);
            return extractRoot;
        }

        private string GetArchiveImportCacheRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeslaCamViewer",
                "imports");
        }

        private string ResolveTeslaCamScanRoot(string extractedRoot)
        {
            if (HasTeslaCamCategoryFolders(extractedRoot))
            {
                return extractedRoot;
            }

            try
            {
                foreach (string directory in Directory.GetDirectories(extractedRoot, "*", SearchOption.AllDirectories))
                {
                    if (HasTeslaCamCategoryFolders(directory))
                    {
                        return directory;
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Resolve archive TeslaCam root", ex);
            }

            return extractedRoot;
        }

        private bool HasTeslaCamCategoryFolders(string path)
        {
            return Directory.Exists(Path.Combine(path, "RecentClips")) ||
                   Directory.Exists(Path.Combine(path, "SavedClips")) ||
                   Directory.Exists(Path.Combine(path, "SentryClips"));
        }

        private List<TeslaClip> BuildArchiveVideoClips(string scanRoot, string archivePath)
        {
            var files = Directory.GetFiles(scanRoot, "*.mp4", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                return new List<TeslaClip>();
            }

            string[] suffixes = GetCameraOrder();
            var flatTeslaSegments = BuildSegmentDictionaryFromFiles(files, suffixes).Values.OrderBy(segment => segment.Timestamp).ToList();
            if (flatTeslaSegments.Count > 0)
            {
                return BuildRecentClipSessions(flatTeslaSegments, "RecentClips")
                    .Select(clip =>
                    {
                        clip.ClipTypeText = "Archive";
                        return clip;
                    })
                    .OrderByDescending(clip => clip.Timestamp)
                    .ToList();
            }

            var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (!TryGetArchiveCameraFromFileName(file, out string camera))
                {
                    continue;
                }

                string groupKey = GetArchiveExportGroupKey(file, camera);
                if (!groups.TryGetValue(groupKey, out var cameras))
                {
                    cameras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    groups[groupKey] = cameras;
                }

                cameras[camera] = file;
            }

            var clips = new List<TeslaClip>();
            int index = 0;
            foreach (var group in groups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Value.Count == 0)
                {
                    continue;
                }

                string firstFile = group.Value.Values.First();
                double durationSeconds = TryParseExportDurationFromFileName(firstFile, out double parsedDuration)
                    ? parsedDuration
                    : 60.0;

                DateTime archiveTime = File.GetLastWriteTime(archivePath);
                string timestamp = archiveTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + $"_archive_{index:00}";
                string dateText = archiveTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
                string timeRangeText = TryParseExportTimeRangeFromFileName(firstFile, out string parsedRange)
                    ? parsedRange
                    : "Imported archive";
                string durationText = FormatDuration(durationSeconds);

                clips.Add(new TeslaClip
                {
                    Timestamp = timestamp,
                    Category = "RecentClips",
                    Title = $"{dateText} | {timeRangeText} | {durationText} | Archive",
                    DateText = dateText,
                    TimeRangeText = timeRangeText,
                    DurationText = durationText,
                    ClipTypeText = "Archive",
                    Segments = new List<TeslaClipSegment>
                    {
                        new TeslaClipSegment
                        {
                            Timestamp = timestamp,
                            Cameras = group.Value,
                            EstimatedDurationSeconds = durationSeconds
                        }
                    }
                });
                index++;
            }

            return clips.OrderByDescending(clip => clip.Timestamp).ToList();
        }

        private bool TryGetArchiveCameraFromFileName(string filePath, out string camera)
        {
            camera = null;
            string normalized = Path.GetFileNameWithoutExtension(filePath)
                ?.ToLowerInvariant()
                .Replace("_", "-")
                .Replace(" ", "-");

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.Contains("front-camera") || normalized.EndsWith("-front"))
            {
                camera = "front";
            }
            else if (normalized.Contains("rear-view") || normalized.EndsWith("-back"))
            {
                camera = "back";
            }
            else if (normalized.Contains("l-repeater") || normalized.Contains("left-repeater"))
            {
                camera = "left_repeater";
            }
            else if (normalized.Contains("r-repeater") || normalized.Contains("right-repeater"))
            {
                camera = "right_repeater";
            }
            else if (normalized.Contains("l-pillar") || normalized.Contains("left-pillar"))
            {
                camera = "left_pillar";
            }
            else if (normalized.Contains("r-pillar") || normalized.Contains("right-pillar"))
            {
                camera = "right_pillar";
            }

            return !string.IsNullOrWhiteSpace(camera);
        }

        private string GetArchiveExportGroupKey(string filePath, string camera)
        {
            string name = Path.GetFileNameWithoutExtension(filePath) ?? filePath;
            string normalized = name.ToLowerInvariant()
                .Replace("_", "-")
                .Replace(" ", "-");

            foreach (string token in GetArchiveCameraNameTokens(camera))
            {
                normalized = normalized.Replace(token, "");
            }

            return normalized.Trim('-');
        }

        private IEnumerable<string> GetArchiveCameraNameTokens(string camera)
        {
            switch (camera)
            {
                case "front":
                    return new[] { "front-camera", "front" };
                case "back":
                    return new[] { "rear-view", "back" };
                case "left_repeater":
                    return new[] { "l-repeater-(fender)", "left-repeater", "l-repeater" };
                case "right_repeater":
                    return new[] { "r-repeater-(fender)", "right-repeater", "r-repeater" };
                case "left_pillar":
                    return new[] { "l-pillar-(b-pillar)", "left-pillar", "l-pillar" };
                case "right_pillar":
                    return new[] { "r-pillar-(b-pillar)", "right-pillar", "r-pillar" };
                default:
                    return new[] { camera.Replace("_", "-") };
            }
        }

        private bool TryParseExportDurationFromFileName(string filePath, out double durationSeconds)
        {
            durationSeconds = 0.0;
            if (!TryParseExportRangeSeconds(filePath, out double startSeconds, out double endSeconds))
            {
                return false;
            }

            durationSeconds = Math.Max(1.0, endSeconds - startSeconds);
            return true;
        }

        private bool TryParseExportTimeRangeFromFileName(string filePath, out string rangeText)
        {
            rangeText = null;
            if (!TryParseExportRangeSeconds(filePath, out double startSeconds, out double endSeconds))
            {
                return false;
            }

            rangeText = $"{FormatSecs(startSeconds)} - {FormatSecs(endSeconds)}";
            return true;
        }

        private bool TryParseExportRangeSeconds(string filePath, out double startSeconds, out double endSeconds)
        {
            startSeconds = 0.0;
            endSeconds = 0.0;
            string name = Path.GetFileNameWithoutExtension(filePath) ?? "";
            Match match = Regex.Match(name, @"(?<sm>\d{2})-(?<ss>\d{2})\s+to\s+(?<em>\d{2})-(?<es>\d{2})", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            startSeconds = (int.Parse(match.Groups["sm"].Value, CultureInfo.InvariantCulture) * 60.0) +
                           int.Parse(match.Groups["ss"].Value, CultureInfo.InvariantCulture);
            endSeconds = (int.Parse(match.Groups["em"].Value, CultureInfo.InvariantCulture) * 60.0) +
                         int.Parse(match.Groups["es"].Value, CultureInfo.InvariantCulture);
            return endSeconds > startSeconds;
        }

        private void SetActiveCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return;
            }

            _activeCategory = category;
            if (TabRecent != null) TabRecent.IsChecked = string.Equals(category, "RecentClips", StringComparison.OrdinalIgnoreCase);
            if (TabSaved != null) TabSaved.IsChecked = string.Equals(category, "SavedClips", StringComparison.OrdinalIgnoreCase);
            if (TabSentry != null) TabSentry.IsChecked = string.Equals(category, "SentryClips", StringComparison.OrdinalIgnoreCase);
        }

        private void ConfigureTeslaCamWatcher(string path)
        {
            if (string.Equals(path, _watchedTeslaCamPath, StringComparison.OrdinalIgnoreCase) && _teslaCamWatcher != null)
            {
                return;
            }

            StopTeslaCamWatcher();
            _watchedTeslaCamPath = path;

            try
            {
                _teslaCamWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size
                };

                _teslaCamWatcher.Created += TeslaCamWatcher_Changed;
                _teslaCamWatcher.Deleted += TeslaCamWatcher_Changed;
                _teslaCamWatcher.Changed += TeslaCamWatcher_Changed;
                _teslaCamWatcher.Renamed += TeslaCamWatcher_Changed;
                _teslaCamWatcher.Error += TeslaCamWatcher_Error;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Configure TeslaCam watcher", ex);
            }
        }

        private void StopTeslaCamWatcher()
        {
            try
            {
                _clipRefreshDebounceTimer?.Stop();
            }
            catch { }

            try
            {
                if (_teslaCamWatcher != null)
                {
                    _teslaCamWatcher.EnableRaisingEvents = false;
                    _teslaCamWatcher.Created -= TeslaCamWatcher_Changed;
                    _teslaCamWatcher.Deleted -= TeslaCamWatcher_Changed;
                    _teslaCamWatcher.Changed -= TeslaCamWatcher_Changed;
                    _teslaCamWatcher.Renamed -= TeslaCamWatcher_Changed;
                    _teslaCamWatcher.Error -= TeslaCamWatcher_Error;
                    _teslaCamWatcher.Dispose();
                    _teslaCamWatcher = null;
                }
            }
            catch { }
        }

        private void TeslaCamWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (_isWindowClosing || !IsTeslaCamClipPath(e.FullPath))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isWindowClosing) return;
                _clipRefreshDebounceTimer.Stop();
                _clipRefreshDebounceTimer.Start();
            });
        }

        private void TeslaCamWatcher_Error(object sender, ErrorEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isWindowClosing) return;
                StopTeslaCamWatcher();
                ScanProgressRing.IsActive = false;
                EmptyStateText.Visibility = Visibility.Visible;
                EmptyStateText.Text = "TeslaCam drive detached or unavailable.";
                ActiveClipSubtitle.Text = "Drive watcher stopped. Reconnect the drive or auto-detect again.";
                CrashLogger.Log("TeslaCam watcher", e.GetException());
            });
        }

        private void ClipRefreshDebounceTimer_Tick(object sender, object e)
        {
            _clipRefreshDebounceTimer.Stop();
            if (_isWindowClosing || string.IsNullOrWhiteSpace(_watchedTeslaCamPath))
            {
                return;
            }

            if (!Directory.Exists(_watchedTeslaCamPath))
            {
                StopTeslaCamWatcher();
                ScanProgressRing.IsActive = false;
                EmptyStateText.Visibility = Visibility.Visible;
                EmptyStateText.Text = "TeslaCam drive detached or unavailable.";
                ActiveClipSubtitle.Text = "Drive removed. Reconnect it or choose another TeslaCam folder.";
                return;
            }

            ActiveClipSubtitle.Text = "TeslaCam folder changed. Refreshing clips...";
            TriggerScan(_watchedTeslaCamPath);
        }

        private bool IsTeslaCamClipPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileName(path), "event.json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return path.IndexOf("RecentClips", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("SavedClips", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("SentryClips", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void FilterAndRenderClips()
        {
            string query = SearchBox.Text.ToLower().Trim();
            _filteredClips = _allClips
                .Where(c => c.Category == _activeCategory)
                .Where(c => string.IsNullOrEmpty(query) || ClipMatchesSearch(c, query))
                .ToList();

            ClipsListView.ItemsSource = _filteredClips;
            ClipsCollageView.ItemsSource = _filteredClips;
            EmptyStateText.Visibility = _filteredClips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_filteredClips.Count == 0)
            {
                EmptyStateText.Text = $"No clips found in {_activeCategory}.";
            }

            UpdateClipViewMode();
            QueueThumbnailsForRealizedClips();
        }

        private bool ClipMatchesSearch(TeslaClip clip, string query)
        {
            if (clip == null) return false;

            string searchable = string.Join(" ", new[]
            {
                clip.Title,
                clip.DateText,
                clip.TimeRangeText,
                clip.DurationText,
                clip.SegmentCountText,
                clip.CameraCountText,
                clip.FsdPercentText,
                clip.DisengagementCountText,
                clip.ClipTypeText,
                clip.Category
            });

            return searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetAppStatus(string text, bool isActive)
        {
            if (AppStatusHost == null || AppStatusText == null || AppStatusProgressRing == null)
            {
                return;
            }

            AppStatusHost.Visibility = Visibility.Visible;
            AppStatusText.Text = string.IsNullOrWhiteSpace(text) ? "Ready" : text;
            AppStatusProgressRing.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            AppStatusProgressRing.IsActive = isActive;
        }

        private CancellationToken BeginNewClipTelemetrySummaryScan()
        {
            try
            {
                _clipTelemetrySummaryCancellation?.Cancel();
                _clipTelemetrySummaryCancellation?.Dispose();
            }
            catch { }

            _clipTelemetrySummaryCancellation = new CancellationTokenSource();
            return _clipTelemetrySummaryCancellation.Token;
        }

        private void CancelClipTelemetrySummaryScan()
        {
            try
            {
                _clipTelemetrySummaryCancellation?.Cancel();
            }
            catch { }
        }

        private void StartClipTelemetrySummaryScan(List<TeslaClip> clips, int scanVersion)
        {
            if (clips == null || clips.Count == 0 || _isWindowClosing)
            {
                SetAppStatus("No clips to index", false);
                return;
            }

            List<TeslaClip> clipSnapshot = clips
                .OrderBy(clip => string.Equals(clip.Category, _activeCategory, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(clip => clip.Timestamp)
                .ToList();
            CancellationToken cancellationToken = BeginNewClipTelemetrySummaryScan();
            int totalClips = clipSnapshot.Count;
            int completedClips = 0;
            int workerCount = GetClipTelemetrySummaryWorkerCount(totalClips);

            SetAppStatus($"Indexing telemetry 0/{totalClips}", true);

            _ = Task.Run(() =>
            {
                try
                {
                    var parallelOptions = new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = workerCount
                    };

                    Parallel.ForEach(clipSnapshot, parallelOptions, clip =>
                    {
                        parallelOptions.CancellationToken.ThrowIfCancellationRequested();
                        if (_isWindowClosing || scanVersion != _scanRequestVersion)
                        {
                            return;
                        }

                        ClipTelemetrySummary summary = BuildClipTelemetrySummary(clip, parallelOptions.CancellationToken);
                        int currentCompleted = Interlocked.Increment(ref completedClips);

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isWindowClosing || cancellationToken.IsCancellationRequested || scanVersion != _scanRequestVersion)
                            {
                                return;
                            }

                            clip.FsdPercentText = FormatFsdPercentPill(summary);
                            clip.DisengagementCountText = FormatDisengagementPill(summary);
                            SetAppStatus($"Indexing telemetry {currentCompleted}/{totalClips}", true);
                        });
                    });

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isWindowClosing || cancellationToken.IsCancellationRequested || scanVersion != _scanRequestVersion)
                        {
                            return;
                        }

                        SetAppStatus($"Telemetry indexed {totalClips} clips", false);
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("Clip telemetry summary scan", ex);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_isWindowClosing && scanVersion == _scanRequestVersion)
                        {
                            SetAppStatus("Telemetry index failed", false);
                        }
                    });
                }
            }, cancellationToken);
        }

        private int GetClipTelemetrySummaryWorkerCount(int clipCount)
        {
            if (clipCount <= 1)
            {
                return 1;
            }

            if (IsCurrentSourceOnRemovableDrive())
            {
                return 1;
            }

            int processorBound = Math.Max(1, Environment.ProcessorCount / 2);
            return Math.Max(1, Math.Min(clipCount, Math.Min(ClipTelemetrySummaryMaxParallelism, processorBound)));
        }

        private bool IsCurrentSourceOnRemovableDrive()
        {
            if (string.IsNullOrWhiteSpace(_currentSourcePath) || IsSupportedArchivePath(_currentSourcePath))
            {
                return false;
            }

            try
            {
                string root = Path.GetPathRoot(_currentSourcePath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                return new DriveInfo(root).DriveType == DriveType.Removable;
            }
            catch
            {
                return false;
            }
        }

        private ClipTelemetrySummary BuildClipTelemetrySummary(TeslaClip clip, CancellationToken cancellationToken)
        {
            var summary = new ClipTelemetrySummary();
            bool? wasFsdEngaged = null;

            foreach (TeslaClipSegment segment in (clip?.Segments ?? new List<TeslaClipSegment>()).OrderBy(s => s.Timestamp))
            {
                cancellationToken.ThrowIfCancellationRequested();

                AutopilotTelemetrySummary segmentSummary = GetOrBuildSegmentTelemetrySummary(segment, cancellationToken);
                if (segmentSummary == null || segmentSummary.TelemetryRecordCount <= 0)
                {
                    wasFsdEngaged = null;
                    continue;
                }

                summary.TelemetryRecordCount += segmentSummary.TelemetryRecordCount;
                summary.FsdRecordCount += segmentSummary.FsdRecordCount;
                summary.TelemetrySeconds += segmentSummary.TelemetrySeconds;
                summary.FsdSeconds += segmentSummary.FsdSeconds;
                summary.FsdDisengagementCount += segmentSummary.FsdDisengagementCount;

                if (wasFsdEngaged == true && segmentSummary.FirstIsFsdEngaged == false)
                {
                    summary.FsdDisengagementCount++;
                }

                wasFsdEngaged = segmentSummary.LastIsFsdEngaged;
            }

            return summary;
        }

        private AutopilotTelemetrySummary GetOrBuildSegmentTelemetrySummary(TeslaClipSegment segment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (segment?.Cameras == null ||
                !segment.Cameras.TryGetValue("front", out string frontPath) ||
                string.IsNullOrWhiteSpace(frontPath))
            {
                return AutopilotTelemetrySummary.Empty;
            }

            FileInfo fileInfo = new FileInfo(frontPath);
            if (!fileInfo.Exists)
            {
                return AutopilotTelemetrySummary.Empty;
            }

            string cacheKey = BuildSegmentTelemetrySummaryCacheKey(fileInfo);
            lock (_clipTelemetrySummaryCacheLock)
            {
                if (_clipTelemetrySummaryCache.TryGetValue(cacheKey, out AutopilotTelemetrySummary cachedSummary))
                {
                    _clipTelemetrySummaryCacheAccessTicks[cacheKey] = DateTime.UtcNow.Ticks;
                    return cachedSummary;
                }
            }

            AutopilotTelemetrySummary summary = BuildSegmentTelemetrySummary(fileInfo.FullName, Math.Max(1.0, segment.EstimatedDurationSeconds), cancellationToken);
            lock (_clipTelemetrySummaryCacheLock)
            {
                PruneClipTelemetrySummaryCacheForInsert();
                _clipTelemetrySummaryCache[cacheKey] = summary;
                _clipTelemetrySummaryCacheAccessTicks[cacheKey] = DateTime.UtcNow.Ticks;
                _clipTelemetrySummaryCacheDirty = true;
            }

            QueueClipTelemetrySummaryCacheSave();
            return summary;
        }

        private void PruneClipTelemetrySummaryCacheForInsert()
        {
            if (_clipTelemetrySummaryCache.Count < ClipTelemetrySummaryCacheMaxEntries)
            {
                return;
            }

            int removeCount = Math.Max(1, _clipTelemetrySummaryCache.Count - ClipTelemetrySummaryCacheMaxEntries + 1);
            var keysToRemove = _clipTelemetrySummaryCache
                .Keys
                .OrderBy(key => _clipTelemetrySummaryCacheAccessTicks.TryGetValue(key, out long ticks) ? ticks : 0L)
                .Take(removeCount)
                .ToList();

            foreach (string key in keysToRemove)
            {
                _clipTelemetrySummaryCache.Remove(key);
                _clipTelemetrySummaryCacheAccessTicks.Remove(key);
            }
        }

        private string BuildSegmentTelemetrySummaryCacheKey(FileInfo fileInfo)
        {
            return $"{fileInfo.FullName}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        }

        private string GetClipTelemetrySummaryCachePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TeslaCamViewer", "TelemetrySummaryCache", "summary-cache-v1.json");
        }

        private void LoadClipTelemetrySummaryCache()
        {
            try
            {
                string cachePath = GetClipTelemetrySummaryCachePath();
                if (!File.Exists(cachePath))
                {
                    return;
                }

                string json = File.ReadAllText(cachePath);
                var entries = JsonSerializer.Deserialize<List<PersistentClipTelemetrySummaryCacheEntry>>(json);
                if (entries == null || entries.Count == 0)
                {
                    return;
                }

                lock (_clipTelemetrySummaryCacheLock)
                {
                    _clipTelemetrySummaryCache.Clear();
                    _clipTelemetrySummaryCacheAccessTicks.Clear();

                    foreach (PersistentClipTelemetrySummaryCacheEntry entry in entries
                        .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Key) && entry.Summary != null)
                        .OrderByDescending(entry => entry.LastAccessUtcTicks)
                        .Take(ClipTelemetrySummaryCacheMaxEntries))
                    {
                        _clipTelemetrySummaryCache[entry.Key] = entry.Summary;
                        _clipTelemetrySummaryCacheAccessTicks[entry.Key] = entry.LastAccessUtcTicks > 0
                            ? entry.LastAccessUtcTicks
                            : DateTime.UtcNow.Ticks;
                    }

                    _clipTelemetrySummaryCacheDirty = false;
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Load telemetry summary cache", ex);
            }
        }

        private void QueueClipTelemetrySummaryCacheSave()
        {
            try
            {
                CancellationTokenSource previousCancellation;
                CancellationTokenSource saveCancellation = new CancellationTokenSource();
                lock (_clipTelemetrySummaryCacheSaveLock)
                {
                    previousCancellation = _clipTelemetrySummaryCacheSaveCancellation;
                    _clipTelemetrySummaryCacheSaveCancellation = saveCancellation;
                }

                try { previousCancellation?.Cancel(); } catch { }
                try { previousCancellation?.Dispose(); } catch { }

                CancellationToken token = saveCancellation.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(ClipTelemetrySummaryCacheSaveDelayMs, token);
                        SaveClipTelemetrySummaryCacheIfDirty(token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log("Save telemetry summary cache task", ex);
                    }
                }, token);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Queue telemetry summary cache save", ex);
            }
        }

        private void FlushClipTelemetrySummaryCache()
        {
            try
            {
                CancellationTokenSource cancellation;
                lock (_clipTelemetrySummaryCacheSaveLock)
                {
                    cancellation = _clipTelemetrySummaryCacheSaveCancellation;
                    _clipTelemetrySummaryCacheSaveCancellation = new CancellationTokenSource();
                }

                try { cancellation?.Cancel(); } catch { }
                try { cancellation?.Dispose(); } catch { }

                SaveClipTelemetrySummaryCacheIfDirty(CancellationToken.None);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Flush telemetry summary cache", ex);
            }
        }

        private void SaveClipTelemetrySummaryCacheIfDirty(CancellationToken cancellationToken)
        {
            List<PersistentClipTelemetrySummaryCacheEntry> entries;
            lock (_clipTelemetrySummaryCacheLock)
            {
                if (!_clipTelemetrySummaryCacheDirty)
                {
                    return;
                }

                entries = _clipTelemetrySummaryCache
                    .Select(pair => new PersistentClipTelemetrySummaryCacheEntry
                    {
                        Key = pair.Key,
                        LastAccessUtcTicks = _clipTelemetrySummaryCacheAccessTicks.TryGetValue(pair.Key, out long ticks) ? ticks : DateTime.UtcNow.Ticks,
                        Summary = pair.Value
                    })
                    .OrderByDescending(entry => entry.LastAccessUtcTicks)
                    .Take(ClipTelemetrySummaryCacheMaxEntries)
                    .ToList();

                _clipTelemetrySummaryCacheDirty = false;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                string cachePath = GetClipTelemetrySummaryCachePath();
                string cacheDirectory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(cacheDirectory))
                {
                    Directory.CreateDirectory(cacheDirectory);
                }

                string tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var options = new JsonSerializerOptions { WriteIndented = false };
                string json = JsonSerializer.Serialize(entries, options);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            catch
            {
                lock (_clipTelemetrySummaryCacheLock)
                {
                    _clipTelemetrySummaryCacheDirty = true;
                }

                throw;
            }
        }

        private AutopilotTelemetrySummary BuildSegmentTelemetrySummary(string frontPath, double segmentDurationSeconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TeslaSeiParser.ExtractAutopilotSummary(frontPath, segmentDurationSeconds, cancellationToken);
        }

        private string FormatFsdPercentPill(ClipTelemetrySummary summary)
        {
            if (summary == null || summary.TelemetryRecordCount <= 0)
            {
                return "--% FSD";
            }

            double percent = summary.TelemetrySeconds > 0.0
                ? summary.FsdSeconds * 100.0 / summary.TelemetrySeconds
                : summary.FsdRecordCount * 100.0 / summary.TelemetryRecordCount;
            return $"{Math.Round(percent):0}% FSD";
        }

        private string FormatDisengagementPill(ClipTelemetrySummary summary)
        {
            if (summary == null || summary.TelemetryRecordCount <= 0)
            {
                return "-- diseng.";
            }

            return $"{summary.FsdDisengagementCount} diseng.";
        }

        private void QueueThumbnailsForRealizedClips()
        {
            if (!_isCollageMode || ClipsCollageView == null || ClipsCollageView.Items == null)
            {
                return;
            }

            try
            {
                foreach (object item in ClipsCollageView.Items)
                {
                    if (item is TeslaClip clip && ClipsCollageView.ContainerFromItem(item) != null)
                    {
                        QueueThumbnailForClip(clip);
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Queue realized thumbnails", ex);
            }
        }

        private void QueueThumbnailForClip(TeslaClip clip)
        {
            if (clip == null || clip.ThumbnailSource != null || _isWindowClosing)
            {
                return;
            }

            string sourceVideo = GetClipThumbnailSourceVideoPath(clip);
            if (string.IsNullOrWhiteSpace(sourceVideo))
            {
                return;
            }

            string thumbnailPath = GetClipThumbnailPath(clip);
            if (File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length > 0)
            {
                SetClipThumbnailSource(clip, thumbnailPath);
                return;
            }

            lock (_thumbnailJobs)
            {
                if (!_thumbnailJobs.Add(thumbnailPath))
                {
                    return;
                }
            }

            _ = Task.Run(async () =>
            {
                await _thumbnailSemaphore.WaitAsync();
                try
                {
                    string ffmpegPath = FindFfmpegExecutable();
                    if (string.IsNullOrWhiteSpace(ffmpegPath))
                    {
                        return;
                    }

                    if (GenerateClipThumbnail(ffmpegPath, sourceVideo, thumbnailPath))
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!_isWindowClosing)
                            {
                                SetClipThumbnailSource(clip, thumbnailPath);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    if (!_isWindowClosing)
                    {
                        CrashLogger.Log("Queue clip thumbnail", ex);
                    }
                }
                finally
                {
                    lock (_thumbnailJobs)
                    {
                        _thumbnailJobs.Remove(thumbnailPath);
                    }

                    _thumbnailSemaphore.Release();
                }
            });
        }

        private string GetClipThumbnailSourceVideoPath(TeslaClip clip)
        {
            try
            {
                TeslaClipSegment firstSegment = clip?.Segments?
                    .OrderBy(segment => segment.Timestamp)
                    .FirstOrDefault(segment => segment?.Cameras != null && segment.Cameras.Count > 0);

                if (firstSegment?.Cameras == null)
                {
                    return null;
                }

                foreach (string camera in GetCameraOrder())
                {
                    if (firstSegment.Cameras.TryGetValue(camera, out string path) && File.Exists(path))
                    {
                        return path;
                    }
                }

                return firstSegment.Cameras.Values.FirstOrDefault(File.Exists);
            }
            catch
            {
                return null;
            }
        }

        private string GetClipThumbnailPath(TeslaClip clip)
        {
            string root = GetThumbnailCacheRoot();
            Directory.CreateDirectory(root);
            string key = ComputeClipCacheKey(clip, clip?.Segments ?? new List<TeslaClipSegment>());
            return Path.Combine(root, $"{key}.jpg");
        }

        private string GetThumbnailCacheRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TeslaCamViewer", "ClipThumbnails");
        }

        private bool GenerateClipThumbnail(string ffmpegPath, string sourceVideo, string thumbnailPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(sourceVideo) || !File.Exists(sourceVideo))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(thumbnailPath));
                string tempPath = Path.Combine(
                    Path.GetDirectoryName(thumbnailPath),
                    $"{Path.GetFileNameWithoutExtension(thumbnailPath)}.{Guid.NewGuid():N}.tmp.jpg");

                try
                {
                    var psi = new ProcessStartInfo(ffmpegPath)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    psi.ArgumentList.Add("-hide_banner");
                    psi.ArgumentList.Add("-loglevel");
                    psi.ArgumentList.Add("error");
                    psi.ArgumentList.Add("-y");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(sourceVideo);
                    psi.ArgumentList.Add("-frames:v");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-vf");
                    psi.ArgumentList.Add("scale=320:-1");
                    psi.ArgumentList.Add("-q:v");
                    psi.ArgumentList.Add("3");
                    psi.ArgumentList.Add(tempPath);

                    using (var process = new Process { StartInfo = psi })
                    {
                        process.Start();
                        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                        var timeout = Stopwatch.StartNew();
                        bool exited = false;

                        while (!exited)
                        {
                            if (_isWindowClosing)
                            {
                                TryKillProcess(process);
                                return false;
                            }

                            exited = process.WaitForExit(150);
                            if (!exited && timeout.Elapsed > TimeSpan.FromSeconds(25))
                            {
                                TryKillProcess(process);
                                CrashLogger.LogMessage("Clip thumbnail", $"Timed out generating thumbnail for {sourceVideo}");
                                return false;
                            }
                        }

                        string stderr = SafeGetTaskResult(stderrTask);
                        _ = SafeGetTaskResult(stdoutTask);
                        if (process.ExitCode != 0)
                        {
                            CrashLogger.LogMessage("Clip thumbnail", $"Failed generating thumbnail for {sourceVideo}: {stderr}");
                            return false;
                        }
                    }

                    var tempInfo = new FileInfo(tempPath);
                    if (!tempInfo.Exists || tempInfo.Length == 0)
                    {
                        return false;
                    }

                    File.Move(tempPath, thumbnailPath, overwrite: true);
                    return true;
                }
                finally
                {
                    TryDeleteFile(tempPath);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Generate clip thumbnail", ex);
                return false;
            }
        }

        private void SetClipThumbnailSource(TeslaClip clip, string thumbnailPath)
        {
            try
            {
                if (clip == null || string.IsNullOrWhiteSpace(thumbnailPath) || !File.Exists(thumbnailPath))
                {
                    return;
                }

                clip.ThumbnailSource = new BitmapImage(new Uri(thumbnailPath, UriKind.Absolute));
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Set clip thumbnail source", ex);
            }
        }

        private CancellationToken BeginNewStitchOperation()
        {
            try
            {
                _stitchCancellation?.Cancel();
            }
            catch { }

            KillActiveFfmpegProcesses();
            _stitchCancellation = new CancellationTokenSource();
            return _stitchCancellation.Token;
        }

        private void CancelActiveStitch()
        {
            try
            {
                _stitchCancellation?.Cancel();
            }
            catch { }

            KillActiveFfmpegProcesses();
        }

        private CancellationToken BeginNewDisengagementMarkerScan()
        {
            CancelDisengagementMarkerScan();
            _disengagementMarkerCancellation = new CancellationTokenSource();
            return _disengagementMarkerCancellation.Token;
        }

        private void CancelDisengagementMarkerScan()
        {
            try
            {
                _disengagementMarkerCancellation?.Cancel();
            }
            catch { }
        }

        private void QueueStartupCleanup()
        {
            LaunchCleanupHelper(waitForCurrentProcess: false);
        }

        private void LaunchCleanupHelper(bool waitForCurrentProcess)
        {
            try
            {
                string helperPath = FindCleanupHelperExecutable();
                if (string.IsNullOrWhiteSpace(helperPath))
                {
                    return;
                }

                string rootPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TeslaCamViewer");

                var psi = new ProcessStartInfo(helperPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(helperPath)
                };

                psi.ArgumentList.Add("--root");
                psi.ArgumentList.Add(rootPath);
                psi.ArgumentList.Add("--cutoff-utc-ticks");
                psi.ArgumentList.Add(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--max-age-days");
                psi.ArgumentList.Add(StitchCacheMaxAgeDays.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--max-bytes");
                psi.ArgumentList.Add(StitchCacheMaxBytes.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--min-free-bytes");
                psi.ArgumentList.Add(StitchCacheMinFreeBytes.ToString(CultureInfo.InvariantCulture));

                if (waitForCurrentProcess)
                {
                    psi.ArgumentList.Add("--wait-pid");
                    psi.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                }

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                if (!_isWindowClosing)
                {
                    CrashLogger.Log("Launch cleanup helper", ex);
                }
            }
        }

        private string FindCleanupHelperExecutable()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Tools", "cleanup", "TeslaCamViewer.Cleanup.exe"),
                Path.Combine(baseDir, "TeslaCamViewer.Cleanup.exe"),
                Path.Combine(Environment.CurrentDirectory, "Tools", "cleanup", "TeslaCamViewer.Cleanup.exe")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private void SetPlaybackActivity(bool visible, string text = null)
        {
            _isPlaybackActivityVisible = visible;
            _playbackActivityText = visible && !string.IsNullOrWhiteSpace(text) ? text : "";
            UpdateActivityVisuals();
        }

        private void SetStitchActivity(bool visible, string text = null)
        {
            _isStitchActivityVisible = visible;
            _stitchActivityText = visible && !string.IsNullOrWhiteSpace(text) ? text : "";
            UpdateActivityVisuals();
        }

        private void SetExportActivity(bool visible, string text = null)
        {
            _isExportActivityVisible = visible;
            _exportActivityText = visible && !string.IsNullOrWhiteSpace(text) ? text : "";
            UpdateActivityVisuals();
        }

        private void QueueStitchActivity(bool visible, string text, TeslaClip clip, int clipVersion, CancellationToken cancellationToken)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isWindowClosing || cancellationToken.IsCancellationRequested || clipVersion != _clipSelectionVersion || _activeClip != clip)
                {
                    return;
                }

                SetStitchActivity(visible, text);
            });
        }

        private void UpdateActivityVisuals()
        {
            try
            {
                bool showPlayback = _isPlaybackActivityVisible;
                bool showStitch = _isStitchActivityVisible;
                bool showExport = _isExportActivityVisible;
                bool showStatus = showPlayback || showExport || showStitch;
                string statusText = showPlayback
                    ? (string.IsNullOrWhiteSpace(_playbackActivityText) ? "Loading video" : _playbackActivityText)
                    : showExport
                        ? (string.IsNullOrWhiteSpace(_exportActivityText) ? "Exporting range" : _exportActivityText)
                        : (string.IsNullOrWhiteSpace(_stitchActivityText) ? "Stitching drive" : _stitchActivityText);

                if (PlaybackStatusPill != null)
                {
                    PlaybackStatusPill.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
                }

                if (PlaybackStatusRing != null)
                {
                    PlaybackStatusRing.IsActive = showStatus;
                }

                if (PlaybackStatusText != null)
                {
                    PlaybackStatusText.Text = statusText;
                }

                if (VideoLoadingOverlay != null)
                {
                    VideoLoadingOverlay.Visibility = showPlayback ? Visibility.Visible : Visibility.Collapsed;
                }

                if (VideoLoadingRing != null)
                {
                    VideoLoadingRing.IsActive = showPlayback;
                }

                if (VideoLoadingText != null)
                {
                    VideoLoadingText.Text = string.IsNullOrWhiteSpace(_playbackActivityText) ? "Loading video" : _playbackActivityText;
                }
            }
            catch (Exception ex)
            {
                if (!_isWindowClosing)
                {
                    CrashLogger.Log("Update activity visuals", ex);
                }
            }
        }

        private TeslaClipSegment TryGetCachedStitchedPlaybackSegment(TeslaClip clip, List<TeslaClipSegment> segments)
        {
            try
            {
                if (segments == null || segments.Count <= 1)
                {
                    return null;
                }

                string cacheDir = Path.Combine(GetStitchCacheRoot(), ComputeClipCacheKey(clip, segments));
                if (!Directory.Exists(cacheDir))
                {
                    return null;
                }

                var stitchedCameras = new Dictionary<string, string>();
                foreach (string camera in GetCameraOrder())
                {
                    bool cameraExpected = true;
                    foreach (TeslaClipSegment segment in segments)
                    {
                        if (segment.Cameras == null ||
                            !segment.Cameras.TryGetValue(camera, out string cameraPath) ||
                            !File.Exists(cameraPath))
                        {
                            cameraExpected = false;
                            break;
                        }
                    }

                    if (!cameraExpected)
                    {
                        continue;
                    }

                    string outputPath = Path.Combine(cacheDir, $"{camera}.mp4");
                    var outputInfo = new FileInfo(outputPath);
                    if (!outputInfo.Exists || outputInfo.Length == 0)
                    {
                        return null;
                    }

                    outputInfo.LastAccessTimeUtc = DateTime.UtcNow;
                    stitchedCameras[camera] = outputPath;
                }

                if (!stitchedCameras.ContainsKey("front"))
                {
                    return null;
                }

                Directory.SetLastAccessTimeUtc(cacheDir, DateTime.UtcNow);
                return new TeslaClipSegment
                {
                    Timestamp = segments[0].Timestamp,
                    Cameras = stitchedCameras,
                    EstimatedDurationSeconds = EstimateClipDurationSeconds(segments.Count)
                };
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Load stitched cache", ex);
                return null;
            }
        }

        private async Task PrepareStitchedPlaybackInBackgroundAsync(TeslaClip clip, List<TeslaClipSegment> sortedSegments, int clipVersion, CancellationToken cancellationToken)
        {
            if (sortedSegments == null || sortedSegments.Count <= 1 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string ffmpegPath = FindFfmpegExecutable();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                CrashLogger.LogMessage("Stitch cache", "Bundled ffmpeg.exe was not found. Falling back to segment playback.");
                QueueStitchActivity(false, "", clip, clipVersion, cancellationToken);
                return;
            }

            SetStitchActivity(true, "Stitching seamless drive...");

            try
            {
                TeslaClipSegment stitchedSegment = await Task.Run(() => BuildStitchedPlaybackSegment(clip, sortedSegments, ffmpegPath, cancellationToken), cancellationToken);
                if (_isWindowClosing ||
                    cancellationToken.IsCancellationRequested ||
                    clipVersion != _clipSelectionVersion ||
                    _activeClip != clip ||
                    stitchedSegment?.Cameras == null ||
                    !stitchedSegment.Cameras.ContainsKey("front"))
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    PromoteStitchedPlayback(clip, stitchedSegment, clipVersion, cancellationToken);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Prepare stitched playback", ex);
            }
            finally
            {
                QueueStitchActivity(false, "", clip, clipVersion, cancellationToken);
            }
        }

        private void PromoteStitchedPlayback(TeslaClip clip, TeslaClipSegment stitchedSegment, int clipVersion, CancellationToken cancellationToken)
        {
            if (_isWindowClosing ||
                cancellationToken.IsCancellationRequested ||
                clipVersion != _clipSelectionVersion ||
                _activeClip != clip ||
                stitchedSegment?.Cameras == null ||
                !stitchedSegment.Cameras.ContainsKey("front"))
            {
                return;
            }

            if (_activePlaybackSegments != null &&
                _activePlaybackSegments.Count == 1 &&
                ReferenceEquals(_activePlaybackSegments[0], stitchedSegment))
            {
                return;
            }

            double globalSeconds = GetGlobalPlaybackSeconds();
            bool wasPlaying = GetPlaybackState(MainPlayer) == MediaPlaybackState.Playing;

            _activePlaybackSegments = new List<TeslaClipSegment> { stitchedSegment };
            InitializeClipTimeline(_activePlaybackSegments);
            globalSeconds = Math.Max(0.0, Math.Min(globalSeconds, _activeClipDurationSeconds));

            SetStitchActivity(false);
            ActiveClipSubtitle.Text = "Seamless drive cache ready";
            SelectClipSegment(stitchedSegment, wasPlaying, globalSeconds);
        }

        private TeslaClipSegment BuildStitchedPlaybackSegment(TeslaClip clip, List<TeslaClipSegment> segments, string ffmpegPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string cacheRoot = GetStitchCacheRoot();
            Directory.CreateDirectory(cacheRoot);

            string clipKey = ComputeClipCacheKey(clip, segments);
            string cacheDir = Path.Combine(cacheRoot, clipKey);
            Directory.CreateDirectory(cacheDir);

            var stitchedCameras = new Dictionary<string, string>();
            foreach (string camera in GetCameraOrder())
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<string> cameraFiles = new List<string>();
                bool cameraComplete = true;
                foreach (TeslaClipSegment segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (segment.Cameras == null ||
                        !segment.Cameras.TryGetValue(camera, out string cameraPath) ||
                        !File.Exists(cameraPath))
                    {
                        cameraComplete = false;
                        break;
                    }

                    cameraFiles.Add(cameraPath);
                }

                if (!cameraComplete || cameraFiles.Count == 0)
                {
                    continue;
                }

                string outputPath = Path.Combine(cacheDir, $"{camera}.mp4");
                if (StitchCameraFiles(ffmpegPath, cameraFiles, outputPath, clipKey, camera, cancellationToken))
                {
                    stitchedCameras[camera] = outputPath;
                }
                else if (camera == "front")
                {
                    return null;
                }
            }

            if (!stitchedCameras.ContainsKey("front"))
            {
                return null;
            }

            try
            {
                Directory.SetLastAccessTimeUtc(cacheDir, DateTime.UtcNow);
            }
            catch { }

            EnforceStitchCacheSize(cacheRoot, cacheDir);

            return new TeslaClipSegment
            {
                Timestamp = segments[0].Timestamp,
                Cameras = stitchedCameras,
                EstimatedDurationSeconds = EstimateClipDurationSeconds(segments.Count)
            };
        }

        private bool StitchCameraFiles(string ffmpegPath, List<string> inputFiles, string outputPath, string clipKey, string camera, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outputInfo = new FileInfo(outputPath);
                if (outputInfo.Exists && outputInfo.Length > 0)
                {
                    outputInfo.LastAccessTimeUtc = DateTime.UtcNow;
                    return true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                string tempRoot = GetStitchTempRoot();
                string workDir = Path.Combine(tempRoot, $"{clipKey}_{camera}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);

                string concatPath = Path.Combine(workDir, $"{camera}.ffconcat");
                string tempOutputPath = Path.Combine(Path.GetDirectoryName(outputPath), $"{camera}.{Guid.NewGuid():N}.tmp.mp4");

                try
                {
                    var lines = new List<string> { "ffconcat version 1.0" };
                    foreach (string inputFile in inputFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lines.Add($"file '{EscapeFfconcatPath(inputFile)}'");
                    }

                    File.WriteAllLines(concatPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    var psi = new ProcessStartInfo(ffmpegPath)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        WorkingDirectory = workDir
                    };

                    psi.ArgumentList.Add("-hide_banner");
                    psi.ArgumentList.Add("-loglevel");
                    psi.ArgumentList.Add("error");
                    psi.ArgumentList.Add("-y");
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("concat");
                    psi.ArgumentList.Add("-safe");
                    psi.ArgumentList.Add("0");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(concatPath);
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:v:0");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("copy");
                    psi.ArgumentList.Add("-movflags");
                    psi.ArgumentList.Add("+faststart");
                    psi.ArgumentList.Add(tempOutputPath);

                    using (var process = new Process { StartInfo = psi })
                    {
                        process.Start();
                        RegisterFfmpegProcess(process);
                        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();

                        try
                        {
                            var timeout = Stopwatch.StartNew();
                            bool exited = false;
                            while (!exited)
                            {
                                if (cancellationToken.IsCancellationRequested || _isWindowClosing)
                                {
                                    TryKillProcess(process);
                                    throw new OperationCanceledException(cancellationToken);
                                }

                                exited = process.WaitForExit(250);
                                if (!exited && timeout.Elapsed > TimeSpan.FromMinutes(5))
                                {
                                    TryKillProcess(process);
                                    CrashLogger.LogMessage("FFmpeg stitch", $"Timed out stitching {camera} for {clipKey}");
                                    return false;
                                }
                            }

                            string stderr = SafeGetTaskResult(stderrTask);
                            _ = SafeGetTaskResult(stdoutTask);

                            if (cancellationToken.IsCancellationRequested || _isWindowClosing)
                            {
                                throw new OperationCanceledException(cancellationToken);
                            }

                            if (process.ExitCode != 0)
                            {
                                CrashLogger.LogMessage("FFmpeg stitch", $"Failed stitching {camera} for {clipKey}: {stderr}");
                                return false;
                            }
                        }
                        finally
                        {
                            UnregisterFfmpegProcess(process);
                        }
                    }

                    var tempOutputInfo = new FileInfo(tempOutputPath);
                    if (!tempOutputInfo.Exists || tempOutputInfo.Length == 0)
                    {
                        CrashLogger.LogMessage("FFmpeg stitch", $"Output missing after stitching {camera}: {tempOutputPath}");
                        return false;
                    }

                    File.Move(tempOutputPath, outputPath, overwrite: true);
                    return true;
                }
                finally
                {
                    TryDeleteFile(tempOutputPath);
                    TryDeleteDirectory(workDir);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Stitch camera files", ex);
                return false;
            }
        }

        private string FindFfmpegExecutable()
        {
            foreach (string candidate in GetFfmpegCandidates())
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private IEnumerable<string> GetFfmpegCandidates()
        {
            string baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "Tools", "ffmpeg", "bin", "ffmpeg.exe");
            yield return Path.Combine(baseDir, "Tools", "ffmpeg.exe");
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "ffmpeg", "bin", "ffmpeg.exe");
            yield return Path.Combine(Environment.CurrentDirectory, "Tools", "ffmpeg", "bin", "ffmpeg.exe");
            yield return Path.Combine(Environment.CurrentDirectory, "Tools", "ffmpeg.exe");

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    yield return Path.Combine(entry.Trim(), "ffmpeg.exe");
                }
            }
        }

        private string ComputeClipCacheKey(TeslaClip clip, List<TeslaClipSegment> segments)
        {
            using (SHA256 sha = SHA256.Create())
            {
                var builder = new StringBuilder();
                builder.Append(clip?.Category ?? "");
                builder.Append('|');
                builder.Append(clip?.Timestamp ?? "");

                foreach (TeslaClipSegment segment in segments.OrderBy(s => s.Timestamp))
                {
                    builder.Append('|');
                    builder.Append(segment.Timestamp);

                    foreach (var pair in segment.Cameras.OrderBy(pair => pair.Key))
                    {
                        builder.Append('|');
                        builder.Append(pair.Key);
                        builder.Append('=');
                        builder.Append(pair.Value);

                        try
                        {
                            var info = new FileInfo(pair.Value);
                            builder.Append(':');
                            builder.Append(info.Length);
                            builder.Append(':');
                            builder.Append(info.LastWriteTimeUtc.Ticks);
                        }
                        catch { }
                    }
                }

                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                return Convert.ToHexString(hash).Substring(0, 24).ToLowerInvariant();
            }
        }

        private string EscapeFfconcatPath(string path)
        {
            return path.Replace("\\", "/").Replace("'", "'\\''");
        }

        private string[] GetCameraOrder()
        {
            return new[]
            {
                "front",
                "back",
                "left_repeater",
                "right_repeater",
                "left_pillar",
                "right_pillar"
            };
        }

        private void RegisterFfmpegProcess(Process process)
        {
            if (process == null) return;

            lock (_ffmpegProcessLock)
            {
                _activeFfmpegProcesses.Add(process);
            }
        }

        private void UnregisterFfmpegProcess(Process process)
        {
            if (process == null) return;

            lock (_ffmpegProcessLock)
            {
                _activeFfmpegProcesses.Remove(process);
            }
        }

        private void KillActiveFfmpegProcesses()
        {
            List<Process> processes;
            lock (_ffmpegProcessLock)
            {
                processes = _activeFfmpegProcesses.ToList();
                _activeFfmpegProcesses.Clear();
            }

            foreach (Process process in processes)
            {
                TryKillProcess(process);
            }
        }

        private void CleanupOwnedFfmpegProcesses()
        {
            try
            {
                string baseDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (Process process in Process.GetProcessesByName("ffmpeg"))
                {
                    try
                    {
                        string path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) &&
                            Path.GetFullPath(path).StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                        {
                            TryKillProcess(process);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Cleanup owned ffmpeg processes", ex);
            }
        }

        private string GetStitchCacheRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TeslaCamViewer", "StitchedDrives");
        }

        private string GetStitchTempRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TeslaCamViewer", "StitchTemp");
        }

        private void CleanupStitchCache()
        {
            try
            {
                CleanupStitchTempFiles();

                string cacheRoot = GetStitchCacheRoot();
                if (!Directory.Exists(cacheRoot))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow.AddDays(-StitchCacheMaxAgeDays);
                foreach (string file in Directory.EnumerateFiles(cacheRoot, "*.tmp.mp4", SearchOption.AllDirectories))
                {
                    TryDeleteFile(file);
                }

                foreach (string dir in Directory.EnumerateDirectories(cacheRoot))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.LastAccessTimeUtc < cutoff)
                        {
                            TryDeleteDirectory(dir);
                        }
                    }
                    catch { }
                }

                EnforceStitchCacheSize(cacheRoot, protectedDirectory: null);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Cleanup stitch cache", ex);
            }
        }

        private void CleanupStitchTempFiles()
        {
            try
            {
                TryDeleteDirectory(GetStitchTempRoot());
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Cleanup stitch temp", ex);
            }
        }

        private void EnforceStitchCacheSize(string cacheRoot, string protectedDirectory)
        {
            try
            {
                string protectedPath = string.IsNullOrWhiteSpace(protectedDirectory)
                    ? null
                    : Path.GetFullPath(protectedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var dirs = Directory.EnumerateDirectories(cacheRoot)
                    .Select(path => new DirectoryInfo(path))
                    .Select(info => new
                    {
                        Info = info,
                        Size = GetDirectorySize(info.FullName),
                        LastAccess = info.LastAccessTimeUtc,
                        IsProtected = protectedPath != null &&
                            string.Equals(
                                Path.GetFullPath(info.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                protectedPath,
                                StringComparison.OrdinalIgnoreCase)
                    })
                    .OrderBy(item => item.LastAccess)
                    .ToList();

                long totalBytes = dirs.Sum(item => item.Size);
                foreach (var dir in dirs)
                {
                    if (totalBytes <= StitchCacheMaxBytes && HasMinimumCacheDriveFreeSpace(cacheRoot))
                    {
                        break;
                    }

                    if (dir.IsProtected)
                    {
                        continue;
                    }

                    totalBytes -= dir.Size;
                    TryDeleteDirectory(dir.Info.FullName);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Enforce stitch cache size", ex);
            }
        }

        private bool HasMinimumCacheDriveFreeSpace(string cacheRoot)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(cacheRoot));
                if (string.IsNullOrWhiteSpace(root))
                {
                    return true;
                }

                var drive = new DriveInfo(root);
                return drive.AvailableFreeSpace >= StitchCacheMinFreeBytes;
            }
            catch
            {
                return true;
            }
        }

        private long GetDirectorySize(string path)
        {
            try
            {
                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file =>
                    {
                        try { return new FileInfo(file).Length; }
                        catch { return 0L; }
                    });
            }
            catch
            {
                return 0L;
            }
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch { }
        }

        private void TryKillProcess(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }

        private string SafeGetTaskResult(Task<string> task)
        {
            try
            {
                return task.Wait(1000) ? task.Result : "";
            }
            catch
            {
                return "";
            }
        }

        // --- PLAYBACK CONTROL & SYNC ---
        private void SelectClip(TeslaClip clip, bool keepPlaying = false)
        {
            int clipVersion = ++_clipSelectionVersion;
            CancellationToken stitchToken = BeginNewStitchOperation();
            CancellationToken markerToken = BeginNewDisengagementMarkerScan();
            SetStitchActivity(false);
            SetPlaybackActivity(false);
            ResetExportMarkers();
            ResetDisengagementMarkers();
            try
            {
                _activeClip = clip;
                _activePlaybackSegments = new List<TeslaClipSegment>();
                if (!keepPlaying)
                {
                    Pause();
                }

                ResetCameraLayout();

                // Sort segments chronologically
                var sortedSegments = clip.Segments.OrderBy(s => s.Timestamp).ToList();
                clip.Segments = sortedSegments;

                TeslaClipSegment cachedStitchedSegment = TryGetCachedStitchedPlaybackSegment(clip, sortedSegments);
                List<TeslaClipSegment> playbackSegments = cachedStitchedSegment != null
                    ? new List<TeslaClipSegment> { cachedStitchedSegment }
                    : sortedSegments;

                _activePlaybackSegments = playbackSegments;
                InitializeClipTimeline(playbackSegments);
                QueueDisengagementMarkersForClip(clip, sortedSegments, clipVersion, markerToken);

                if (playbackSegments.Count > 0)
                {
                    ActiveClipTitle.Text = !string.IsNullOrWhiteSpace(clip.DateText) ? clip.DateText : clip.Title;
                    SelectClipSegment(playbackSegments[0], wasPlaying: keepPlaying);
                }

                if (cachedStitchedSegment == null)
                {
                    _ = PrepareStitchedPlaybackInBackgroundAsync(clip, sortedSegments, clipVersion, stitchToken);
                }
            }
            catch (Exception ex)
            {
                SetStitchActivity(false);
                SetPlaybackActivity(false);
                ActiveClipSubtitle.Text = "Playback Error: " + ex.Message;
                CrashLogger.Log("SelectClip", ex);
            }
        }

        private async void SelectClipSegment(TeslaClipSegment segment, bool wasPlaying, double seekSeconds = 0.0, bool keepTimelineInteractive = false)
        {
            int loadVersion = ++_segmentLoadVersion;
            _isLoadingSegment = true;
            SetPlaybackActivity(true, keepTimelineInteractive ? "Seeking video..." : "Loading video...");
            bool keepSliderEnabled = keepTimelineInteractive && _isSliderDragging;

            try
            {
                if (segment?.Cameras == null)
                {
                    ActiveClipSubtitle.Text = "Invalid clip segment";
                    return;
                }

                _activeSegment = segment;
                _activeSegmentIndex = _activePlaybackSegments?.IndexOf(segment) ?? -1;
                Pause();
                if (keepSliderEnabled)
                {
                    PlayPauseBtn.IsEnabled = false;
                    SpeedComboBox.IsEnabled = false;
                }
                else
                {
                    SetPlaybackControlsEnabled(false);
                }
                double segmentStartSeconds = GetActiveSegmentStartSeconds();
                SetTimelineValue(segmentStartSeconds + seekSeconds);
                TimeCurrentText.Text = FormatSecs(segmentStartSeconds + seekSeconds);
                TimeDurationText.Text = FormatSecs(_activeClipDurationSeconds);

                if (!segment.Cameras.ContainsKey("front"))
                {
                    ActiveClipSubtitle.Text = "Front feed missing for this segment";
                    return;
                }

                if (!segment.Cameras.ContainsKey(_mainAngle))
                {
                    ResetCameraLayout();
                }

                if (!segment.Cameras.TryGetValue(_mainAngle, out string mainPath))
                {
                    ActiveClipSubtitle.Text = "Selected camera feed missing for this segment";
                    return;
                }

                ClearPlayerSource(MainPlayer);
                foreach (var slot in GetAuxSlots())
                {
                    ClearPlayerSource(slot.Player);
                    slot.Player.Visibility = Visibility.Collapsed;
                }

                MainAngleLabel.Text = GetFriendlyAngleLabel(_mainAngle);
                bool mainLoaded = await SetPlayerSourceAsync(MainPlayer, mainPath);
                if (!mainLoaded)
                {
                    ActiveClipSubtitle.Text = "Unable to load the main camera video";
                    return;
                }

                if (loadVersion != _segmentLoadVersion || _activeSegment != segment) return;

                foreach (var slot in GetAuxSlots())
                {
                    string currentTag = slot.Card.Tag as string;
                    if (!string.IsNullOrEmpty(currentTag) && segment.Cameras.TryGetValue(currentTag, out string auxPath))
                    {
                        slot.Player.Visibility = Visibility.Visible;
                        bool auxLoaded = await SetPlayerSourceAsync(slot.Player, auxPath);
                        if (!auxLoaded)
                        {
                            slot.Player.Visibility = Visibility.Collapsed;
                            ClearPlayerSource(slot.Player);
                        }

                        if (loadVersion != _segmentLoadVersion || _activeSegment != segment) return;
                    }
                    else
                    {
                        slot.Player.Visibility = Visibility.Collapsed;
                    }
                }

                var duration = GetNaturalDuration(MainPlayer);
                double durationSecs = duration.TotalSeconds > 0.0 ? duration.TotalSeconds : 60.0;
                UpdateActiveSegmentDuration(durationSecs);
                seekSeconds = Math.Max(0.0, Math.Min(seekSeconds, durationSecs));
                segmentStartSeconds = GetActiveSegmentStartSeconds();

                if (keepSliderEnabled && _isSliderDragging)
                {
                    double dragGlobalSeconds = TimelineSlider.Value;
                    var dragTarget = GetSegmentPositionForGlobalTime(dragGlobalSeconds);
                    if (dragTarget.SegmentIndex != _activeSegmentIndex)
                    {
                        _isLoadingSegment = false;
                        SeekToGlobalTime(dragGlobalSeconds, _resumePlaybackAfterScrub, keepTimelineInteractive: true);
                        return;
                    }

                    seekSeconds = Math.Max(0.0, Math.Min(dragTarget.LocalSeconds, durationSecs));
                }

                TimelineSlider.Maximum = _activeClipDurationSeconds;
                SetTimelineValue(segmentStartSeconds + seekSeconds);
                SetPlaybackRateForAll(_playbackRate);
                SeekAllPlayers(TimeSpan.FromSeconds(seekSeconds));
                SetPlaybackControlsEnabled(true);
                TimeCurrentText.Text = FormatSecs(segmentStartSeconds + seekSeconds);
                TimeDurationText.Text = FormatSecs(_activeClipDurationSeconds);
                ActiveClipSubtitle.Text = GetActiveClipPlaybackSubtitle();

                // LOAD SEI TELEMETRY RECORDSET NATIVELY
                _activeTelemetry.Clear();
                ResetHUD();

                string frontPath = segment.Cameras["front"];
                double telemetryDurationSecs = durationSecs;
                var telemetryPlayer = GetPlayerForCameraAngle("front");
                var telemetryDuration = telemetryPlayer?.MediaPlayer?.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
                if (telemetryDuration.TotalSeconds > 0.0)
                {
                    telemetryDurationSecs = telemetryDuration.TotalSeconds;
                }

                _ = Task.Run(() =>
                {
                    try
                    {
                        if (_isWindowClosing)
                        {
                            return;
                        }

                        var records = TeslaSeiParser.ExtractTelemetry(frontPath, telemetryDurationSecs);
                        var dispatcherQueue = DispatcherQueue;
                        if (records != null && !_isWindowClosing && dispatcherQueue != null)
                        {
                            dispatcherQueue.TryEnqueue(() =>
                            {
                                if (!_isWindowClosing && loadVersion == _segmentLoadVersion && _activeSegment == segment)
                                {
                                    _activeTelemetry = records;
                                    Debug.WriteLine($"C# SEI Parser successfully decoded {records.Count} telemetry logs.");
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_isWindowClosing)
                        {
                            CrashLogger.Log("Telemetry parse task", ex);
                        }
                    }
                });

                _isLoadingSegment = false;
                if (wasPlaying)
                {
                    Play();
                }
            }
            catch (Exception ex)
            {
                ActiveClipSubtitle.Text = "Segment Playback Error: " + ex.Message;
                CrashLogger.Log("SelectClipSegment", ex);
            }
            finally
            {
                if (loadVersion == _segmentLoadVersion)
                {
                    _isLoadingSegment = false;
                    SetPlaybackActivity(false);
                }
            }
        }

        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MainPlayer.MediaPlayer == null) return;

                var icon = PlayPauseBtn.Content as FontIcon;
                if (MainPlayer.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    Pause();
                }
                else
                {
                    Play();
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Play/pause click", ex);
            }
        }

        private void Play()
        {
            try
            {
                if (_isLoadingSegment || MainPlayer.MediaPlayer == null || MainPlayer.MediaPlayer.Source == null) return;

                var icon = PlayPauseBtn.Content as FontIcon;
                if (icon != null) icon.Glyph = "\uE769"; // Pause glyph

                SyncAuxPlayersToMain(force: true);
                SetPlaybackRateForAll(_playbackRate);
                TryPlayPlayer(MainPlayer);
                foreach (var player in GetLoadedAuxPlayers())
                {
                    TryPlayPlayer(player);
                }
                _hudTimer.Start();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Play", ex);
            }
        }

        private void Pause()
        {
            try
            {
                var icon = PlayPauseBtn.Content as FontIcon;
                if (icon != null) icon.Glyph = "\uE768"; // Play glyph

                TryPausePlayer(MainPlayer);
                foreach (var player in GetLoadedAuxPlayers())
                {
                    TryPausePlayer(player);
                }
                _hudTimer.Stop();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Pause", ex);
            }
        }

        // --- HIGH FREQUENCY HUD TICKS & SYNC ---
        private void HudTimer_Tick(object sender, object e)
        {
            try
            {
                if (MainPlayer.MediaPlayer == null || _isSliderDragging) return;

                double localSec = MainPlayer.MediaPlayer.PlaybackSession.Position.TotalSeconds;
                double globalSec = GetGlobalPlaybackSeconds(localSec);
                SetTimelineValue(globalSec);
                TimeCurrentText.Text = FormatSecs(globalSec);

                SyncAuxPlayersToMain(force: false);

                // Scrub telemetry
                UpdateHUD(localSec);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("HUD timer", ex);
            }
        }

        private void UpdateHUD(double curTime)
        {
            if (_activeTelemetry == null || _activeTelemetry.Count == 0) return;

            // Scan telemetry logs for closest time index
            SeiMetadata closest = _activeTelemetry[0];
            double minDiff = Math.Abs(closest.OffsetSec - curTime);

            for (int i = 1; i < _activeTelemetry.Count; i++)
            {
                double diff = Math.Abs(_activeTelemetry[i].OffsetSec - curTime);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = _activeTelemetry[i];
                }
            }

            if (closest != null)
            {
                RenderTelemetryHUD(closest);
            }
        }

        private void RenderTelemetryHUD(SeiMetadata data)
        {
            try
            {
                // 1. Speed readout
                double speedMph = GetDisplaySpeedMph(data);
                HudSpeed.Text = double.IsNaN(speedMph) ? "00" : Math.Round(speedMph).ToString("00");

                // 2. Autopilot Autonomy states
                string driveState = GetDriveStateText(data);
                bool hasAutonomyState = data.AutopilotState > 0;

                if (hasAutonomyState)
                {
                    BadgeDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 209, 88));
                    BadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 200, 250));
                    AutonomyBadge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(96, 255, 255, 255));
                    BadgeText.Text = $"AUTONOMY: {driveState}";
                    HudApText.Text = driveState;
                    HudApText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 200, 250));
                }
                else
                {
                    BadgeDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 123, 132, 146));
                    BadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 203, 213, 225));
                    AutonomyBadge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(64, 255, 255, 255));
                    BadgeText.Text = $"STATE: {driveState}";
                    HudApText.Text = driveState;
                    HudApText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 203, 213, 225));
                }

                // Rotate native wheel shape (Check NaN)
                SteerRotateTransform.Angle = double.IsNaN(data.SteeringWheelAngle) ? 0 : data.SteeringWheelAngle;

                // 3. Pedals (Clamp Height to 0)
                double accelHeight = (data.AcceleratorPedalPosition / 100.0) * 60.0;
                AccelBar.Height = double.IsNaN(accelHeight) ? 0 : Math.Max(0, accelHeight);
                HudAccelText.Text = double.IsNaN(data.AcceleratorPedalPosition) ? "0%" : $"{Math.Round(data.AcceleratorPedalPosition)}%";

                BrakeBar.Height = data.BrakeApplied ? 60.0 : 0.0;
                HudBrakeText.Text = data.BrakeApplied ? "ACTIVE" : "OFF";
                HudBrakeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(data.BrakeApplied ? 
                    Windows.UI.Color.FromArgb(255, 255, 69, 58) : Windows.UI.Color.FromArgb(255, 203, 213, 225));

                // 4. Steering readout pointer position
                HudSteerAngleText.Text = FormatSteeringAngle(data.SteeringWheelAngle);
                double steerOffset = double.IsNaN(data.SteeringWheelAngle) ? 0 : Math.Max(-46.0, Math.Min(46.0, (data.SteeringWheelAngle / 360.0) * 92.0));
                Canvas.SetLeft(SteerVisualPointer, 46.0 + steerOffset);

                // 5. Gear & Blinkers
                HudGearText.Text = GetGearText(data.GearState);

                if (data.BlinkerOnLeft && _blinkerFlashState)
                    BlinkerLeftText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 209, 88));
                else
                    BlinkerLeftText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(56, 255, 255, 255));

                if (data.BlinkerOnRight && _blinkerFlashState)
                    BlinkerRightText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 209, 88));
                else
                    BlinkerRightText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(56, 255, 255, 255));

                // G-Forces dot reposition (center is at 15,15 relative to canvas 30x30)
                double latG = data.LinearAccelerationX / 9.81;
                double lonG = data.LinearAccelerationY / 9.81;
                double gMag = Math.Sqrt(latG * latG + lonG * lonG);

                HudGForceText.Text = double.IsNaN(gMag) ? "0.00 G" : $"{gMag.ToString("0.00")} G";
                double gScale = 15.0; // scale factor to fit coordinate grid
                double gDotX = double.IsNaN(latG) ? 0 : Math.Max(-15.0, Math.Min(15.0, latG * gScale));
                double gDotY = double.IsNaN(lonG) ? 0 : Math.Max(-15.0, Math.Min(15.0, -lonG * gScale));
                Canvas.SetLeft(GDot, 15.0 + gDotX);
                Canvas.SetTop(GDot, 15.0 + gDotY);

                // 6. GPS
                HudLat.Text = double.IsNaN(data.LatitudeDeg) ? "0.000000°" : $"{data.LatitudeDeg.ToString("0.000000")}°";
                HudLon.Text = double.IsNaN(data.LongitudeDeg) ? "0.000000°" : $"{data.LongitudeDeg.ToString("0.000000")}°";
                HudHeading.Text = double.IsNaN(data.HeadingDeg) ? "0.0° N" : $"{data.HeadingDeg.ToString("0.0")}° {GetHeadingLetter(data.HeadingDeg)}";

                _activeLat = double.IsNaN(data.LatitudeDeg) ? 0 : data.LatitudeDeg;
                _activeLon = double.IsNaN(data.LongitudeDeg) ? 0 : data.LongitudeDeg;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error rendering telemetry HUD: {ex.Message}");
            }
        }

        private void ResetHUD()
        {
            HudSpeed.Text = "00";
            BadgeDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 123, 132, 146));
            BadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 203, 213, 225));
            AutonomyBadge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(64, 255, 255, 255));
            BadgeText.Text = "STATE: PARKED";
            HudApText.Text = "PARKED";
            HudApText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 203, 213, 225));
            SteerRotateTransform.Angle = 0;
            AccelBar.Height = 0;
            HudAccelText.Text = "0%";
            BrakeBar.Height = 0;
            HudBrakeText.Text = "OFF";
            HudSteerAngleText.Text = FormatSteeringAngle(0.0);
            Canvas.SetLeft(SteerVisualPointer, 46.0);
            HudGearText.Text = "P";
            BlinkerLeftText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(56, 255, 255, 255));
            BlinkerRightText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(56, 255, 255, 255));
            Canvas.SetLeft(GDot, 15.0);
            Canvas.SetTop(GDot, 15.0);
            HudGForceText.Text = "0.00 G";
            HudLat.Text = "0.000000°";
            HudLon.Text = "0.000000°";
            HudHeading.Text = "0.0° N";
            _activeLat = 0.0;
            _activeLon = 0.0;
        }

        private double GetDisplaySpeedMph(SeiMetadata data)
        {
            double signedSpeedMph = GetSignedSpeedMph(data);
            if (double.IsNaN(signedSpeedMph) || double.IsInfinity(signedSpeedMph))
            {
                return double.NaN;
            }

            double speedMph = Math.Abs(signedSpeedMph);
            return speedMph < 0.5 ? 0.0 : speedMph;
        }

        private string FormatSteeringAngle(double steeringWheelAngle)
        {
            if (double.IsNaN(steeringWheelAngle) || double.IsInfinity(steeringWheelAngle) || Math.Abs(steeringWheelAngle) < 0.05)
            {
                return "STRAIGHT 0.0\u00B0";
            }

            string direction = steeringWheelAngle < 0 ? "LEFT" : "RIGHT";
            return $"{direction} {Math.Abs(steeringWheelAngle).ToString("0.0", CultureInfo.InvariantCulture)}\u00B0";
        }

        private string GetDriveStateText(SeiMetadata data)
        {
            if (data == null)
            {
                return "PARKED";
            }

            double speedMph = GetDisplaySpeedMph(data);
            bool isMoving = !double.IsNaN(speedMph) && !double.IsInfinity(speedMph) && speedMph >= 0.5;
            string motionState = IsParkGear(data.GearState)
                ? "PARKED"
                : isMoving ? "DRIVING" : "IDLE";

            if (data.AutopilotState > 0)
            {
                return $"{GetAutonomyModeText(data.AutopilotState)} {motionState}";
            }

            if (motionState == "PARKED")
            {
                return "PARKED";
            }

            return $"MANUALLY {motionState}";
        }

        private string GetAutonomyModeText(uint autopilotState)
        {
            switch (autopilotState)
            {
                case 1:
                    return "FSD";
                case 2:
                    return "AUTOSTEER";
                default:
                    return "TACC";
            }
        }

        private double GetSignedSpeedMph(SeiMetadata data)
        {
            if (data == null)
            {
                return double.NaN;
            }

            float vehicleSpeedMps = data.VehicleSpeedMps;
            if (float.IsNaN(vehicleSpeedMps) || float.IsInfinity(vehicleSpeedMps))
            {
                return double.NaN;
            }

            double speedMph = vehicleSpeedMps * 2.23694;
            if (Math.Abs(speedMph) < 0.5)
            {
                return 0.0;
            }

            if (IsReverseGear(data.GearState))
            {
                return -Math.Abs(speedMph);
            }

            if (IsDriveGear(data.GearState))
            {
                return Math.Abs(speedMph);
            }

            return speedMph;
        }

        private bool IsParkGear(uint gearState)
        {
            return gearState == 0;
        }

        private bool IsDriveGear(uint gearState)
        {
            return gearState == 1;
        }

        private bool IsReverseGear(uint gearState)
        {
            return gearState == 2;
        }

        private string GetGearText(uint gearState)
        {
            string[] gears = new string[] { "P", "D", "R", "N" };
            return gearState < gears.Length ? gears[gearState] : "U";
        }

        private bool HasPlayerSource(MediaPlayerElement element)
        {
            try
            {
                return element?.MediaPlayer?.Source != null;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Read player source", ex);
                return false;
            }
        }

        private TimeSpan GetNaturalDuration(MediaPlayerElement element)
        {
            try
            {
                return element?.MediaPlayer?.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Read natural duration", ex);
                return TimeSpan.Zero;
            }
        }

        private TimeSpan GetPlayerPosition(MediaPlayerElement element)
        {
            try
            {
                return element?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Read player position", ex);
                return TimeSpan.Zero;
            }
        }

        private MediaPlaybackState GetPlaybackState(MediaPlayerElement element)
        {
            try
            {
                return element?.MediaPlayer?.PlaybackSession?.PlaybackState ?? MediaPlaybackState.None;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Read playback state", ex);
                return MediaPlaybackState.None;
            }
        }

        private void ClearPlayerSource(MediaPlayerElement element)
        {
            if (_isWindowClosing) return;

            try
            {
                if (element?.MediaPlayer != null)
                {
                    element.MediaPlayer.Pause();
                    element.MediaPlayer.Source = null;
                }
            }
            catch (Exception ex)
            {
                if (!_isWindowClosing)
                {
                    CrashLogger.Log("Clear player source", ex);
                }
            }
        }

        private void TryPlayPlayer(MediaPlayerElement element)
        {
            try
            {
                if (HasPlayerSource(element))
                {
                    element.MediaPlayer.Play();
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Play player", ex);
            }
        }

        private void TryPausePlayer(MediaPlayerElement element)
        {
            try
            {
                if (element?.MediaPlayer != null)
                {
                    element.MediaPlayer.Pause();
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Pause player", ex);
            }
        }

        private void TryDisposePlayer(MediaPlayerElement element)
        {
            try
            {
                if (element?.MediaPlayer != null)
                {
                    element.MediaPlayer.Pause();
                    element.MediaPlayer.Source = null;
                    element.MediaPlayer.Dispose();
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Dispose player", ex);
            }
        }

        private void ResetAuxLabel(Border card, string labelText)
        {
            var grid = card.Child as Grid;
            if (grid == null) return;
            foreach (var child in grid.Children)
            {
                if (child is Border labelBorder && labelBorder.Child is TextBlock textBlock)
                {
                    textBlock.Text = labelText;
                    break;
                }
            }
        }

        private IEnumerable<(Border Card, MediaPlayerElement Player)> GetAuxSlots()
        {
            yield return (CardBack, PlayerBack);
            yield return (CardLeftRepeater, PlayerLeftRepeater);
            yield return (CardRightRepeater, PlayerRightRepeater);
            yield return (CardLeftPillar, PlayerLeftPillar);
            yield return (CardRightPillar, PlayerRightPillar);
        }

        private IEnumerable<MediaPlayerElement> GetLoadedAuxPlayers()
        {
            foreach (var slot in GetAuxSlots())
            {
                if (HasPlayerSource(slot.Player))
                {
                    yield return slot.Player;
                }
            }
        }

        private IEnumerable<MediaPlayerElement> GetLoadedPlayers()
        {
            if (HasPlayerSource(MainPlayer))
            {
                yield return MainPlayer;
            }

            foreach (var player in GetLoadedAuxPlayers())
            {
                yield return player;
            }
        }

        private MediaPlayerElement GetPlayerForCard(Border card)
        {
            if (card == CardBack) return PlayerBack;
            if (card == CardLeftRepeater) return PlayerLeftRepeater;
            if (card == CardRightRepeater) return PlayerRightRepeater;
            if (card == CardLeftPillar) return PlayerLeftPillar;
            if (card == CardRightPillar) return PlayerRightPillar;
            return null;
        }

        private MediaPlayerElement GetPlayerForCameraAngle(string angle)
        {
            if (_mainAngle == angle) return MainPlayer;
            if (_auxPlayers.TryGetValue(angle, out var player)) return player;
            return null;
        }

        private void ResetCameraLayout()
        {
            _mainAngle = "front";
            MainAngleLabel.Text = "FRONT CAMERA";
            MainAngleLabel.Tag = "front";

            CardBack.Tag = "back";
            CardLeftRepeater.Tag = "left_repeater";
            CardRightRepeater.Tag = "right_repeater";
            CardLeftPillar.Tag = "left_pillar";
            CardRightPillar.Tag = "right_pillar";

            ResetAuxLabel(CardBack, "REAR VIEW");
            ResetAuxLabel(CardLeftRepeater, "L REPEATER (FENDER)");
            ResetAuxLabel(CardRightRepeater, "R REPEATER (FENDER)");
            ResetAuxLabel(CardLeftPillar, "L PILLAR (B-PILLAR)");
            ResetAuxLabel(CardRightPillar, "R PILLAR (B-PILLAR)");
            RebuildAuxPlayerMapFromCards();
        }

        private void RebuildAuxPlayerMapFromCards()
        {
            _auxPlayers.Clear();
            foreach (var slot in GetAuxSlots())
            {
                string angle = slot.Card.Tag as string;
                if (!string.IsNullOrEmpty(angle))
                {
                    _auxPlayers[angle] = slot.Player;
                }
            }
        }

        private void InitializeClipTimeline(List<TeslaClipSegment> sortedSegments)
        {
            _activeSegmentIndex = -1;
            _activeSegmentDurations = sortedSegments.Select(segment =>
                segment.EstimatedDurationSeconds > 0.0 ? segment.EstimatedDurationSeconds : 60.0).ToList();
            RebuildActiveSegmentStarts();
            TimelineSlider.Maximum = Math.Max(0.0, _activeClipDurationSeconds);
            SetTimelineValue(0);
            TimeCurrentText.Text = "00:00";
            TimeDurationText.Text = FormatSecs(_activeClipDurationSeconds);
            UpdateMarkerControls();
        }

        private void RebuildActiveSegmentStarts()
        {
            _activeSegmentStarts = new List<double>();
            double total = 0.0;

            foreach (double duration in _activeSegmentDurations)
            {
                _activeSegmentStarts.Add(total);
                total += Math.Max(0.1, duration);
            }

            _activeClipDurationSeconds = total;
        }

        private void UpdateActiveSegmentDuration(double durationSecs)
        {
            if (_activeSegmentIndex < 0 || _activeSegmentIndex >= _activeSegmentDurations.Count) return;
            if (durationSecs <= 0.0 || double.IsNaN(durationSecs)) return;

            if (Math.Abs(_activeSegmentDurations[_activeSegmentIndex] - durationSecs) > 0.1)
            {
                _activeSegmentDurations[_activeSegmentIndex] = durationSecs;
                RebuildActiveSegmentStarts();
            }
        }

        private double GetActiveSegmentStartSeconds()
        {
            if (_activeSegmentIndex < 0 || _activeSegmentIndex >= _activeSegmentStarts.Count) return 0.0;
            return _activeSegmentStarts[_activeSegmentIndex];
        }

        private double GetGlobalPlaybackSeconds()
        {
            if (MainPlayer?.MediaPlayer == null) return 0.0;
            return GetGlobalPlaybackSeconds(GetPlayerPosition(MainPlayer).TotalSeconds);
        }

        private double GetGlobalPlaybackSeconds(double localSeconds)
        {
            return Math.Max(0.0, Math.Min(_activeClipDurationSeconds, GetActiveSegmentStartSeconds() + Math.Max(0.0, localSeconds)));
        }

        private (int SegmentIndex, double LocalSeconds) GetSegmentPositionForGlobalTime(double globalSeconds)
        {
            if (_activePlaybackSegments == null || _activePlaybackSegments.Count == 0)
            {
                return (-1, 0.0);
            }

            double target = Math.Max(0.0, Math.Min(globalSeconds, _activeClipDurationSeconds));

            for (int i = 0; i < _activePlaybackSegments.Count; i++)
            {
                double start = i < _activeSegmentStarts.Count ? _activeSegmentStarts[i] : i * 60.0;
                double duration = i < _activeSegmentDurations.Count ? _activeSegmentDurations[i] : 60.0;
                double end = start + Math.Max(0.1, duration);

                if (target < end || i == _activePlaybackSegments.Count - 1)
                {
                    return (i, Math.Max(0.0, Math.Min(duration, target - start)));
                }
            }

            int lastIndex = _activePlaybackSegments.Count - 1;
            double lastDuration = lastIndex < _activeSegmentDurations.Count ? _activeSegmentDurations[lastIndex] : 60.0;
            return (lastIndex, lastDuration);
        }

        private void SeekToGlobalTime(double globalSeconds, bool resumePlayback, bool keepTimelineInteractive = false)
        {
            var target = GetSegmentPositionForGlobalTime(globalSeconds);
            if (target.SegmentIndex < 0) return;

            if (target.SegmentIndex == _activeSegmentIndex)
            {
                SeekAllPlayers(TimeSpan.FromSeconds(target.LocalSeconds));
                SetTimelineValue(globalSeconds);
                TimeCurrentText.Text = FormatSecs(globalSeconds);
                UpdateHUD(target.LocalSeconds);

                if (resumePlayback && GetPlaybackState(MainPlayer) != MediaPlaybackState.Playing)
                {
                    Play();
                }
                return;
            }

            SelectClipSegment(_activePlaybackSegments[target.SegmentIndex], resumePlayback, target.LocalSeconds, keepTimelineInteractive);
        }

        private string GetActiveClipPlaybackSubtitle()
        {
            string clipSummary = "";
            if (_activeClip != null)
            {
                string timeRange = _activeClip.TimeRangeText;
                string duration = _activeClip.DurationText;
                if (!string.IsNullOrWhiteSpace(timeRange) && !string.IsNullOrWhiteSpace(duration))
                {
                    clipSummary = $"{timeRange}, {duration}";
                }
                else if (!string.IsNullOrWhiteSpace(timeRange))
                {
                    clipSummary = timeRange;
                }
                else if (!string.IsNullOrWhiteSpace(duration))
                {
                    clipSummary = duration;
                }
            }

            if (_activeClip == null || _activeClip.Segments.Count <= 1)
            {
                string playingText = _activeSegment != null ? $"Playing: {FormatDate(_activeSegment.Timestamp)}, {FormatTime(_activeSegment.Timestamp)}" : "";
                return string.IsNullOrWhiteSpace(clipSummary) ? playingText : $"{clipSummary}, {playingText}";
            }

            string segmentText = $"Playing drive: {_activeClip.Segments.Count} segments";
            return string.IsNullOrWhiteSpace(clipSummary) ? segmentText : $"{clipSummary}, {segmentText}";
        }

        private void SetPlaybackControlsEnabled(bool enabled)
        {
            TimelineSlider.IsEnabled = enabled;
            TimelineScrubberHost.IsHitTestVisible = enabled;
            TimelineScrubberHost.Opacity = enabled ? 1.0 : 0.55;
            PlayPauseBtn.IsEnabled = enabled;
            SpeedComboBox.IsEnabled = enabled;
            UpdateMarkerControls();
        }

        private void SetTimelineValue(double seconds)
        {
            _isUpdatingTimeline = true;
            try
            {
                double max = TimelineSlider.Maximum > TimelineSlider.Minimum ? TimelineSlider.Maximum : seconds;
                TimelineSlider.Value = Math.Max(TimelineSlider.Minimum, Math.Min(max, seconds));
            }
            finally
            {
                _isUpdatingTimeline = false;
            }
            UpdateTimelineScrubberVisual();
        }

        private void UpdateTimelineScrubberVisual()
        {
            if (TimelineSlider == null || TimelineScrubberHost == null || TimelineTrack == null || TimelineProgress == null || TimelineGlassThumbTransform == null)
            {
                return;
            }

            double trackWidth = TimelineTrack.ActualWidth;
            if (trackWidth <= 0.0)
            {
                return;
            }

            double range = TimelineSlider.Maximum - TimelineSlider.Minimum;
            double progress = range > 0.0 ? (TimelineSlider.Value - TimelineSlider.Minimum) / range : 0.0;
            progress = Math.Max(0.0, Math.Min(1.0, progress));

            TimelineProgress.Width = progress * trackWidth;

            double thumbWidth = TimelineGlassThumb.ActualWidth > 0.0 ? TimelineGlassThumb.ActualWidth : TimelineGlassThumb.Width;
            double hostWidth = TimelineScrubberHost.ActualWidth;
            double trackLeft = 0.0;

            try
            {
                trackLeft = TimelineTrack.TransformToVisual(TimelineScrubberHost).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            }
            catch
            {
                trackLeft = 0.0;
            }

            double targetX = trackLeft + (progress * trackWidth) - (thumbWidth / 2.0);
            TimelineGlassThumbTransform.X = Math.Max(0.0, Math.Min(Math.Max(0.0, hostWidth - thumbWidth), targetX));
            UpdateDisengagementMarkerVisuals(trackLeft, trackWidth);
            UpdateExportMarkerVisuals(trackLeft, trackWidth);
        }

        private void QueueDisengagementMarkersForClip(TeslaClip clip, List<TeslaClipSegment> sortedSegments, int clipVersion, CancellationToken cancellationToken)
        {
            if (clip == null || sortedSegments == null || sortedSegments.Count == 0)
            {
                ResetDisengagementMarkers();
                return;
            }

            _ = Task.Run(() =>
            {
                var markerSeconds = new List<double>();
                bool? previousSegmentEndedFsd = null;
                double segmentStartSeconds = 0.0;

                try
                {
                    foreach (TeslaClipSegment segment in sortedSegments)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        double segmentDurationSeconds = segment?.EstimatedDurationSeconds > 0.0
                            ? segment.EstimatedDurationSeconds
                            : 60.0;
                        segmentDurationSeconds = Math.Max(0.1, segmentDurationSeconds);

                        if (segment?.Cameras != null &&
                            segment.Cameras.TryGetValue("front", out string frontPath) &&
                            !string.IsNullOrWhiteSpace(frontPath) &&
                            File.Exists(frontPath))
                        {
                            AutopilotDisengagementMarkers markers = TeslaSeiParser.ExtractAutopilotDisengagementMarkers(
                                frontPath,
                                segmentDurationSeconds,
                                cancellationToken);

                            if (markers != null && markers.TelemetryRecordCount > 0)
                            {
                                if (previousSegmentEndedFsd == true && !markers.FirstIsFsdEngaged)
                                {
                                    AddDisengagementMarkerSecond(markerSeconds, segmentStartSeconds);
                                }

                                foreach (double offset in markers.OffsetsSeconds)
                                {
                                    AddDisengagementMarkerSecond(markerSeconds, segmentStartSeconds + offset);
                                }

                                previousSegmentEndedFsd = markers.LastIsFsdEngaged;
                            }
                            else
                            {
                                previousSegmentEndedFsd = null;
                            }
                        }
                        else
                        {
                            previousSegmentEndedFsd = null;
                        }

                        segmentStartSeconds += segmentDurationSeconds;
                    }

                    if (_isWindowClosing || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (_isWindowClosing ||
                            cancellationToken.IsCancellationRequested ||
                            clipVersion != _clipSelectionVersion ||
                            _activeClip != clip)
                        {
                            return;
                        }

                        SetDisengagementMarkers(markerSeconds);
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (!_isWindowClosing)
                    {
                        CrashLogger.Log("Disengagement marker scan", ex);
                    }
                }
            }, cancellationToken);
        }

        private static void AddDisengagementMarkerSecond(List<double> markerSeconds, double seconds)
        {
            if (markerSeconds == null || double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                return;
            }

            seconds = Math.Max(0.0, seconds);
            if (markerSeconds.Count == 0 || Math.Abs(markerSeconds[markerSeconds.Count - 1] - seconds) > 0.15)
            {
                markerSeconds.Add(seconds);
            }
        }

        private void SetDisengagementMarkers(List<double> markerSeconds)
        {
            _activeDisengagementMarkerSeconds = markerSeconds
                ?.Where(seconds => !double.IsNaN(seconds) && !double.IsInfinity(seconds))
                .OrderBy(seconds => seconds)
                .ToList() ?? new List<double>();

            RebuildDisengagementMarkerElements();
            UpdateTimelineScrubberVisual();
        }

        private void ResetDisengagementMarkers()
        {
            _activeDisengagementMarkerSeconds.Clear();
            if (TimelineDisengagementMarkersCanvas != null)
            {
                TimelineDisengagementMarkersCanvas.Children.Clear();
                TimelineDisengagementMarkersCanvas.Visibility = Visibility.Collapsed;
            }
        }

        private void RebuildDisengagementMarkerElements()
        {
            if (TimelineDisengagementMarkersCanvas == null)
            {
                return;
            }

            TimelineDisengagementMarkersCanvas.Children.Clear();
            foreach (double seconds in _activeDisengagementMarkerSeconds)
            {
                var marker = new Border
                {
                    Width = 4,
                    Height = 16,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 214, 10)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Opacity = 0.98,
                    Tag = seconds,
                    IsHitTestVisible = false
                };

                TimelineDisengagementMarkersCanvas.Children.Add(marker);
            }

            TimelineDisengagementMarkersCanvas.Visibility = _activeDisengagementMarkerSeconds.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateDisengagementMarkerVisuals(double trackLeft, double trackWidth)
        {
            if (TimelineDisengagementMarkersCanvas == null || TimelineDisengagementMarkersCanvas.Children.Count == 0)
            {
                return;
            }

            double range = Math.Max(0.0, _activeClipDurationSeconds);
            if (range <= 0.0 || trackWidth <= 0.0)
            {
                TimelineDisengagementMarkersCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            TimelineDisengagementMarkersCanvas.Visibility = Visibility.Visible;
            double hostHeight = TimelineScrubberHost?.ActualHeight > 0.0 ? TimelineScrubberHost.ActualHeight : 32.0;

            foreach (UIElement element in TimelineDisengagementMarkersCanvas.Children)
            {
                if (element is FrameworkElement marker && marker.Tag is double seconds)
                {
                    double progress = Math.Max(0.0, Math.Min(1.0, seconds / range));
                    double markerWidth = marker.ActualWidth > 0.0 ? marker.ActualWidth : marker.Width;
                    double markerHeight = marker.ActualHeight > 0.0 ? marker.ActualHeight : marker.Height;

                    Canvas.SetLeft(marker, trackLeft + (progress * trackWidth) - (markerWidth / 2.0));
                    Canvas.SetTop(marker, Math.Max(0.0, (hostHeight - markerHeight) / 2.0));
                }
            }
        }

        private void UpdateExportMarkerVisuals()
        {
            if (TimelineTrack == null || TimelineScrubberHost == null)
            {
                return;
            }

            double trackWidth = TimelineTrack.ActualWidth;
            if (trackWidth <= 0.0)
            {
                return;
            }

            double trackLeft = 0.0;
            try
            {
                trackLeft = TimelineTrack.TransformToVisual(TimelineScrubberHost).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            }
            catch
            {
                trackLeft = 0.0;
            }

            UpdateExportMarkerVisuals(trackLeft, trackWidth);
        }

        private void UpdateExportMarkerVisuals(double trackLeft, double trackWidth)
        {
            if (ExportStartMarker == null || ExportEndMarker == null || ExportRangeHighlight == null ||
                ExportStartMarkerTransform == null || ExportEndMarkerTransform == null || ExportRangeHighlightTransform == null)
            {
                return;
            }

            double range = Math.Max(0.0, _activeClipDurationSeconds);
            PositionExportMarker(ExportStartMarker, ExportStartMarkerTransform, _exportStartSeconds, trackLeft, trackWidth, range);
            PositionExportMarker(ExportEndMarker, ExportEndMarkerTransform, _exportEndSeconds, trackLeft, trackWidth, range);

            if (_exportStartSeconds.HasValue && _exportEndSeconds.HasValue && _exportEndSeconds.Value > _exportStartSeconds.Value && range > 0.0)
            {
                double startProgress = Math.Max(0.0, Math.Min(1.0, _exportStartSeconds.Value / range));
                double endProgress = Math.Max(0.0, Math.Min(1.0, _exportEndSeconds.Value / range));
                double startX = trackLeft + (startProgress * trackWidth);
                double endX = trackLeft + (endProgress * trackWidth);

                ExportRangeHighlight.Visibility = Visibility.Visible;
                ExportRangeHighlightTransform.X = startX;
                ExportRangeHighlight.Width = Math.Max(2.0, endX - startX);
            }
            else
            {
                ExportRangeHighlight.Visibility = Visibility.Collapsed;
                ExportRangeHighlight.Width = 0.0;
            }
        }

        private void PositionExportMarker(Border marker, TranslateTransform transform, double? seconds, double trackLeft, double trackWidth, double range)
        {
            if (!seconds.HasValue || range <= 0.0)
            {
                marker.Visibility = Visibility.Collapsed;
                return;
            }

            double progress = Math.Max(0.0, Math.Min(1.0, seconds.Value / range));
            double markerWidth = marker.ActualWidth > 0.0 ? marker.ActualWidth : marker.Width;
            transform.X = trackLeft + (progress * trackWidth) - (markerWidth / 2.0);
            marker.Visibility = Visibility.Visible;
        }

        private void ResetExportMarkers()
        {
            _exportStartSeconds = null;
            _exportEndSeconds = null;
            UpdateMarkerControls();
            UpdateExportMarkerVisuals();
        }

        private void UpdateMarkerControls()
        {
            try
            {
                bool timelineReady = TimelineSlider != null && TimelineSlider.IsEnabled && _activeClip != null && _activeClipDurationSeconds > 0.0;
                bool hasStart = _exportStartSeconds.HasValue;
                bool hasEnd = _exportEndSeconds.HasValue;
                bool validRange = hasStart && hasEnd && _exportEndSeconds.Value > _exportStartSeconds.Value;

                if (ExportMarkerText != null)
                {
                    string startText = hasStart ? FormatSecs(_exportStartSeconds.Value) : "--:--";
                    string endText = hasEnd ? FormatSecs(_exportEndSeconds.Value) : "--:--";
                    ExportMarkerText.Text = $"Range {startText} - {endText}";
                    Brush markerBrush = validRange ? GetBrushResource("AccentCyanBrush") : GetBrushResource("TextSecondaryBrush");
                    if (markerBrush != null)
                    {
                        ExportMarkerText.Foreground = markerBrush;
                    }
                }

                if (MarkStartBtn != null) MarkStartBtn.IsEnabled = timelineReady && !_isExporting;
                if (MarkEndBtn != null) MarkEndBtn.IsEnabled = timelineReady && !_isExporting;
                if (ClearMarkersBtn != null) ClearMarkersBtn.IsEnabled = timelineReady && !_isExporting && (hasStart || hasEnd);
                if (ExportRangeBtn != null) ExportRangeBtn.IsEnabled = timelineReady && !_isExporting && validRange;
            }
            catch (Exception ex)
            {
                if (!_isWindowClosing)
                {
                    CrashLogger.Log("Update marker controls", ex);
                }
            }
        }

        private Brush GetBrushResource(string key)
        {
            try
            {
                return Application.Current.Resources[key] as Brush;
            }
            catch
            {
                return null;
            }
        }

        private double GetMarkerTimeSeconds()
        {
            double seconds = TimelineSlider?.Value ?? GetGlobalPlaybackSeconds();
            return Math.Max(0.0, Math.Min(_activeClipDurationSeconds, seconds));
        }

        private async Task ExportMarkedRangeAsync(bool exportAllViews)
        {
            if (_isExporting) return;

            if (!_exportStartSeconds.HasValue || !_exportEndSeconds.HasValue || _exportEndSeconds.Value <= _exportStartSeconds.Value)
            {
                ActiveClipSubtitle.Text = "Set valid IN and OUT markers before exporting.";
                return;
            }

            string camera = _mainAngle;
            if (string.IsNullOrWhiteSpace(camera))
            {
                camera = "front";
            }

            var playbackSegments = _activePlaybackSegments?.ToList() ?? new List<TeslaClipSegment>();
            var segmentStarts = _activeSegmentStarts?.ToList() ?? new List<double>();
            var segmentDurations = _activeSegmentDurations?.ToList() ?? new List<double>();
            double exportStart = _exportStartSeconds.Value;
            double exportEnd = _exportEndSeconds.Value;

            string ffmpegPath = FindFfmpegExecutable();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                ActiveClipSubtitle.Text = "Export unavailable: bundled ffmpeg.exe was not found.";
                return;
            }

            if (exportAllViews)
            {
                await ExportAllViewsMarkedRangeAsync(ffmpegPath, exportStart, exportEnd, playbackSegments, segmentStarts, segmentDurations);
                return;
            }

            var slices = BuildExportSlices(camera, exportStart, exportEnd, playbackSegments, segmentStarts, segmentDurations);
            if (slices.Count == 0)
            {
                ActiveClipSubtitle.Text = $"No {GetFriendlyAngleLabel(camera).ToLowerInvariant()} video exists in the marked range.";
                return;
            }

            var savePicker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
                SuggestedFileName = BuildExportFileName(camera, exportStart, exportEnd)
            };
            savePicker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            var saveFile = await savePicker.PickSaveFileAsync();
            if (saveFile == null)
            {
                return;
            }

            _isExporting = true;
            SetExportActivity(true, "Exporting marked range...");
            UpdateMarkerControls();

            try
            {
                await Task.Run(() => ExportSlicesWithFfmpeg(ffmpegPath, slices, saveFile.Path));
                SetExportActivity(true, "Validating telemetry...");
                ExportTelemetryValidation telemetryValidation = await Task.Run(() => ValidateExportTelemetry(saveFile.Path, exportEnd - exportStart));
                string exportSummary = $"Exported {FormatSecs(exportEnd - exportStart)} from {GetFriendlyAngleLabel(camera).ToLowerInvariant()}.";
                if (telemetryValidation.RecordCount > 0)
                {
                    ActiveClipSubtitle.Text = $"{exportSummary} Telemetry preserved ({telemetryValidation.RecordCount:N0} records).";
                }
                else if (camera == "front")
                {
                    ActiveClipSubtitle.Text = $"{exportSummary} Warning: telemetry was not detected in the export.";
                }
                else
                {
                    ActiveClipSubtitle.Text = $"{exportSummary} No embedded telemetry detected for this camera.";
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ActiveClipSubtitle.Text = "Export failed. See crash.txt for details.";
                CrashLogger.Log("Export marked range", ex);
            }
            finally
            {
                _isExporting = false;
                SetExportActivity(false);
                UpdateMarkerControls();
            }
        }

        private async Task ExportAllViewsMarkedRangeAsync(string ffmpegPath, double exportStart, double exportEnd, List<TeslaClipSegment> playbackSegments, List<double> segmentStarts, List<double> segmentDurations)
        {
            var exportPlans = BuildAllViewExportPlans(exportStart, exportEnd, playbackSegments, segmentStarts, segmentDurations);
            if (exportPlans.Count == 0)
            {
                ActiveClipSubtitle.Text = "No camera views exist in the marked range.";
                return;
            }

            var savePicker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
                SuggestedFileName = BuildExportArchiveFileName(exportStart, exportEnd)
            };
            savePicker.FileTypeChoices.Add("Compressed folder", new List<string> { ".zip" });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            var saveFile = await savePicker.PickSaveFileAsync();
            if (saveFile == null)
            {
                return;
            }

            string tempRoot = GetStitchTempRoot();
            string workDir = Path.Combine(tempRoot, $"export_all_{Guid.NewGuid():N}");
            string exportDir = Path.Combine(workDir, "TeslaCam Export");
            double durationSeconds = exportEnd - exportStart;
            var results = new List<CameraExportResult>();

            _isExporting = true;
            SetExportActivity(true, $"Exporting {exportPlans.Count} camera views...");
            UpdateMarkerControls();

            try
            {
                Directory.CreateDirectory(exportDir);

                for (int i = 0; i < exportPlans.Count; i++)
                {
                    CameraExportPlan plan = exportPlans[i];
                    string label = GetFriendlyAngleLabel(plan.Camera);
                    string outputName = BuildExportFileName(plan.Camera, exportStart, exportEnd) + ".mp4";
                    string outputPath = Path.Combine(exportDir, outputName);

                    SetExportActivity(true, $"Exporting {label} ({i + 1}/{exportPlans.Count})...");
                    await Task.Run(() => ExportSlicesWithFfmpeg(ffmpegPath, plan.Slices, outputPath));

                    SetExportActivity(true, $"Validating {label} ({i + 1}/{exportPlans.Count})...");
                    ExportTelemetryValidation telemetryValidation = await Task.Run(() => ValidateExportTelemetry(outputPath, durationSeconds));
                    results.Add(new CameraExportResult
                    {
                        Camera = plan.Camera,
                        OutputPath = outputPath,
                        TelemetryValidation = telemetryValidation
                    });
                }

                SetExportActivity(true, "Compressing exported views...");
                await Task.Run(() => CreateCompressedExportFolder(exportDir, saveFile.Path));

                CameraExportResult frontResult = results.FirstOrDefault(result => result.Camera == "front");
                string telemetryText = frontResult?.TelemetryValidation?.RecordCount > 0
                    ? $" Front telemetry preserved ({frontResult.TelemetryValidation.RecordCount:N0} records)."
                    : " Front telemetry was not detected.";
                ActiveClipSubtitle.Text = $"Exported {FormatSecs(durationSeconds)} from {results.Count} camera views to a compressed folder.{telemetryText}";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ActiveClipSubtitle.Text = "Export failed. See crash.txt for details.";
                CrashLogger.Log("Export all views marked range", ex);
            }
            finally
            {
                _isExporting = false;
                SetExportActivity(false);
                UpdateMarkerControls();
                TryDeleteDirectory(workDir);
            }
        }

        private List<ExportSlice> BuildExportSlices(string camera, double exportStart, double exportEnd, List<TeslaClipSegment> playbackSegments, List<double> segmentStarts, List<double> segmentDurations)
        {
            var slices = new List<ExportSlice>();
            for (int i = 0; i < playbackSegments.Count; i++)
            {
                TeslaClipSegment segment = playbackSegments[i];
                if (segment?.Cameras == null || !segment.Cameras.TryGetValue(camera, out string path) || !File.Exists(path))
                {
                    continue;
                }

                double segmentStart = i < segmentStarts.Count ? segmentStarts[i] : i * 60.0;
                double segmentDuration = i < segmentDurations.Count ? segmentDurations[i] : 60.0;
                double segmentEnd = segmentStart + Math.Max(0.1, segmentDuration);
                double overlapStart = Math.Max(exportStart, segmentStart);
                double overlapEnd = Math.Min(exportEnd, segmentEnd);
                if (overlapEnd <= overlapStart)
                {
                    continue;
                }

                slices.Add(new ExportSlice
                {
                    FilePath = path,
                    InSeconds = Math.Max(0.0, overlapStart - segmentStart),
                    OutSeconds = Math.Max(0.0, overlapEnd - segmentStart)
                });
            }

            return slices;
        }

        private List<CameraExportPlan> BuildAllViewExportPlans(double exportStart, double exportEnd, List<TeslaClipSegment> playbackSegments, List<double> segmentStarts, List<double> segmentDurations)
        {
            var plans = new List<CameraExportPlan>();
            foreach (string camera in GetAvailableExportCameraOrder(playbackSegments))
            {
                var slices = BuildExportSlices(camera, exportStart, exportEnd, playbackSegments, segmentStarts, segmentDurations);
                if (slices.Count == 0)
                {
                    continue;
                }

                plans.Add(new CameraExportPlan
                {
                    Camera = camera,
                    Slices = slices
                });
            }

            return plans;
        }

        private IEnumerable<string> GetAvailableExportCameraOrder(List<TeslaClipSegment> playbackSegments)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string camera in GetCameraOrder())
            {
                if (seen.Add(camera))
                {
                    yield return camera;
                }
            }

            if (playbackSegments == null)
            {
                yield break;
            }

            foreach (string camera in playbackSegments
                .Where(segment => segment?.Cameras != null)
                .SelectMany(segment => segment.Cameras.Keys)
                .Where(camera => !string.IsNullOrWhiteSpace(camera))
                .OrderBy(camera => camera, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(camera))
                {
                    yield return camera;
                }
            }
        }

        private void ExportSlicesWithFfmpeg(string ffmpegPath, List<ExportSlice> slices, string outputPath)
        {
            string tempRoot = GetStitchTempRoot();
            string workDir = Path.Combine(tempRoot, $"export_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);

            string concatPath = Path.Combine(workDir, "export.ffconcat");
            string outputDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                outputDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            Directory.CreateDirectory(outputDir);
            string tempOutputPath = Path.Combine(outputDir, $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp.mp4");

            try
            {
                var lines = new List<string> { "ffconcat version 1.0" };
                foreach (ExportSlice slice in slices)
                {
                    lines.Add($"file '{EscapeFfconcatPath(slice.FilePath)}'");
                    lines.Add($"inpoint {slice.InSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
                    lines.Add($"outpoint {slice.OutSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
                }

                File.WriteAllLines(concatPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var psi = new ProcessStartInfo(ffmpegPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = workDir
                };

                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("concat");
                psi.ArgumentList.Add("-safe");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(concatPath);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:v:0");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
                psi.ArgumentList.Add(tempOutputPath);

                using (var process = new Process { StartInfo = psi })
                {
                    process.Start();
                    RegisterFfmpegProcess(process);
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();

                    try
                    {
                        var timeout = Stopwatch.StartNew();
                        bool exited = false;
                        while (!exited)
                        {
                            if (_isWindowClosing)
                            {
                                TryKillProcess(process);
                                throw new OperationCanceledException();
                            }

                            exited = process.WaitForExit(250);
                            if (!exited && timeout.Elapsed > TimeSpan.FromMinutes(10))
                            {
                                TryKillProcess(process);
                                throw new TimeoutException("Timed out exporting marked range.");
                            }
                        }

                        string stderr = SafeGetTaskResult(stderrTask);
                        _ = SafeGetTaskResult(stdoutTask);

                        if (_isWindowClosing)
                        {
                            throw new OperationCanceledException();
                        }

                        if (process.ExitCode != 0)
                        {
                            throw new InvalidOperationException($"FFmpeg export failed: {stderr}");
                        }
                    }
                    finally
                    {
                        UnregisterFfmpegProcess(process);
                    }
                }

                var outputInfo = new FileInfo(tempOutputPath);
                if (!outputInfo.Exists || outputInfo.Length == 0)
                {
                    throw new IOException("FFmpeg did not create an export file.");
                }

                File.Move(tempOutputPath, outputPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(tempOutputPath);
                TryDeleteDirectory(workDir);
            }
        }

        private void CreateCompressedExportFolder(string sourceDirectory, string archivePath)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Export folder was not created.");
            }

            string archiveDir = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrWhiteSpace(archiveDir))
            {
                archiveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            Directory.CreateDirectory(archiveDir);
            string tempArchivePath = Path.Combine(archiveDir, $".{Path.GetFileNameWithoutExtension(archivePath)}.{Guid.NewGuid():N}.tmp.zip");

            try
            {
                TryDeleteFile(tempArchivePath);
                ZipFile.CreateFromDirectory(sourceDirectory, tempArchivePath, CompressionLevel.Optimal, includeBaseDirectory: false);

                var archiveInfo = new FileInfo(tempArchivePath);
                if (!archiveInfo.Exists || archiveInfo.Length == 0)
                {
                    throw new IOException("Compressed export folder was not created.");
                }

                File.Move(tempArchivePath, archivePath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(tempArchivePath);
            }
        }

        private ExportTelemetryValidation ValidateExportTelemetry(string outputPath, double durationSeconds)
        {
            var result = new ExportTelemetryValidation();
            try
            {
                if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                {
                    return result;
                }

                List<SeiMetadata> records = TeslaSeiParser.ExtractTelemetry(outputPath, Math.Max(1.0, durationSeconds));
                result.RecordCount = records?.Count ?? 0;
                if (records == null || records.Count == 0)
                {
                    return result;
                }

                result.FirstOffsetSeconds = records.Min(record => record.OffsetSec);
                result.LastOffsetSeconds = records.Max(record => record.OffsetSec);
                result.HasSpeed = records.Any(record => Math.Abs(record.VehicleSpeedMps) > 0.01f);
                result.HasGps = records.Any(record =>
                    Math.Abs(record.LatitudeDeg) > 0.000001 ||
                    Math.Abs(record.LongitudeDeg) > 0.000001);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Validate export telemetry", ex);
            }

            return result;
        }

        private string BuildExportFileName(string camera, double exportStart, double exportEnd)
        {
            string clipDate = !string.IsNullOrWhiteSpace(_activeClip?.DateText) ? _activeClip.DateText : "TeslaCam";
            string cameraText = GetFriendlyAngleLabel(camera).Replace(" ", "-").ToLowerInvariant();
            string fileName = $"{clipDate} {FormatSecs(exportStart).Replace(':', '-')} to {FormatSecs(exportEnd).Replace(':', '-')} {cameraText}";
            return SanitizeFileName(fileName);
        }

        private string BuildExportArchiveFileName(double exportStart, double exportEnd)
        {
            string clipDate = !string.IsNullOrWhiteSpace(_activeClip?.DateText) ? _activeClip.DateText : "TeslaCam";
            string fileName = $"{clipDate} {FormatSecs(exportStart).Replace(':', '-')} to {FormatSecs(exportEnd).Replace(':', '-')} all views";
            return SanitizeFileName(fileName);
        }

        private string SanitizeFileName(string fileName)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(invalid, '-');
            }

            return fileName;
        }

        private sealed class ExportSlice
        {
            public string FilePath { get; set; }
            public double InSeconds { get; set; }
            public double OutSeconds { get; set; }
        }

        private sealed class CameraExportPlan
        {
            public string Camera { get; set; }
            public List<ExportSlice> Slices { get; set; } = new List<ExportSlice>();
        }

        private sealed class CameraExportResult
        {
            public string Camera { get; set; }
            public string OutputPath { get; set; }
            public ExportTelemetryValidation TelemetryValidation { get; set; }
        }

        private sealed class ExportTelemetryValidation
        {
            public int RecordCount { get; set; }
            public bool HasSpeed { get; set; }
            public bool HasGps { get; set; }
            public double FirstOffsetSeconds { get; set; }
            public double LastOffsetSeconds { get; set; }
        }

        private void SetTimelineValueFromPointer(PointerRoutedEventArgs e)
        {
            if (TimelineSlider == null || TimelineTrack == null || !TimelineSlider.IsEnabled)
            {
                return;
            }

            double trackWidth = TimelineTrack.ActualWidth;
            if (trackWidth <= 0.0)
            {
                return;
            }

            var point = e.GetCurrentPoint(TimelineTrack).Position;
            double x = Math.Max(0.0, Math.Min(trackWidth, point.X));
            double progress = x / trackWidth;
            TimelineSlider.Value = TimelineSlider.Minimum + ((TimelineSlider.Maximum - TimelineSlider.Minimum) * progress);
        }

        private void SeekAllPlayers(TimeSpan target)
        {
            foreach (var player in GetLoadedPlayers())
            {
                try
                {
                    player.MediaPlayer.PlaybackSession.Position = target;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("Seek player", ex);
                }
            }
            SetPlaybackRateForAll(_playbackRate);
        }

        private void SetPlaybackRateForAll(double rate)
        {
            foreach (var player in GetLoadedPlayers())
            {
                try
                {
                    player.MediaPlayer.PlaybackSession.PlaybackRate = rate;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("Set playback rate", ex);
                }
            }
        }

        private void SyncAuxPlayersToMain(bool force)
        {
            if (!HasPlayerSource(MainPlayer)) return;

            TimeSpan mainPosition;
            bool mainPlaying;
            try
            {
                mainPosition = MainPlayer.MediaPlayer.PlaybackSession.Position;
                mainPlaying = MainPlayer.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Read main sync position", ex);
                return;
            }

            foreach (var player in GetLoadedAuxPlayers())
            {
                try
                {
                    var session = player.MediaPlayer.PlaybackSession;
                    double diff = session.Position.TotalSeconds - mainPosition.TotalSeconds;

                    if (force || Math.Abs(diff) > HardSyncThresholdSec)
                    {
                        session.Position = mainPosition;
                        session.PlaybackRate = _playbackRate;
                    }
                    else if (mainPlaying && Math.Abs(diff) > SoftSyncThresholdSec)
                    {
                        double correction = Math.Max(-0.08, Math.Min(0.08, -diff * 0.25));
                        session.PlaybackRate = Math.Max(0.05, _playbackRate + correction);
                    }
                    else
                    {
                        session.PlaybackRate = _playbackRate;
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("Sync aux player", ex);
                }
            }
        }

        private async Task<bool> SetPlayerSourceAsync(MediaPlayerElement element, string filePath)
        {
            if (_isWindowClosing) return false;
            if (element?.MediaPlayer == null || string.IsNullOrEmpty(filePath)) return false;
            if (!File.Exists(filePath))
            {
                CrashLogger.LogMessage("Set player source", $"Video file does not exist: {filePath}");
                return false;
            }

            var player = element.MediaPlayer;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Cleanup()
            {
                player.MediaOpened -= OnOpened;
                player.MediaFailed -= OnFailed;
            }

            void OnOpened(Windows.Media.Playback.MediaPlayer sender, object args)
            {
                Cleanup();
                tcs.TrySetResult(true);
            }

            void OnFailed(Windows.Media.Playback.MediaPlayer sender, MediaPlayerFailedEventArgs args)
            {
                Cleanup();
                tcs.TrySetResult(false);
            }

            player.MediaOpened += OnOpened;
            player.MediaFailed += OnFailed;

            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                if (_isWindowClosing)
                {
                    Cleanup();
                    return false;
                }

                player.Source = MediaSource.CreateFromStorageFile(file);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                if (_isWindowClosing)
                {
                    Cleanup();
                    return false;
                }

                if (completed != tcs.Task)
                {
                    Cleanup();
                    CrashLogger.LogMessage("Set player source", $"Timed out loading video: {filePath}");
                    return false;
                }

                return await tcs.Task;
            }
            catch (Exception ex)
            {
                Cleanup();
                if (!_isWindowClosing)
                {
                    CrashLogger.Log("Set player source", ex);
                }
                return false;
            }
        }

        // --- CAMERA ANGLE SWAP TRIPPERS ---
        private async void AuxCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_activeClip == null || _activeSegment == null || _isLoadingSegment) return;
            var border = sender as Border;
            if (border == null) return;

            string selectedAngle = border.Tag as string;
            if (selectedAngle == null) return;

            var auxElement = GetPlayerForCard(border);
            if (auxElement == null) return;

            string mainAngle = _mainAngle;
            if (mainAngle == null || mainAngle == selectedAngle) return;

            // Verify both angles have camera files available
            if (!_activeSegment.Cameras.TryGetValue(selectedAngle, out string selectedPath) ||
                !_activeSegment.Cameras.TryGetValue(mainAngle, out string previousMainPath))
            {
                return;
            }

            bool wasPlaying = isPlaying;
            TimeSpan currentTime = TimeSpan.Zero;
            try
            {
                currentTime = MainPlayer.MediaPlayer.PlaybackSession.Position;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Read swap position", ex);
            }
            int loadVersion = _segmentLoadVersion;
            _isLoadingSegment = true;
            SetPlaybackActivity(true, "Switching camera...");

            try
            {
                Pause();
                SetPlaybackControlsEnabled(false);

            // Create FRESH MediaSource objects from file paths — never reuse/transfer
                bool mainLoaded = await SetPlayerSourceAsync(MainPlayer, selectedPath);
                if (!mainLoaded || loadVersion != _segmentLoadVersion) return;

                bool auxLoaded = await SetPlayerSourceAsync(auxElement, previousMainPath);
                if (!auxLoaded || loadVersion != _segmentLoadVersion) return;
                auxElement.Visibility = Visibility.Visible;

            // Update the main viewport label and tracking
            _mainAngle = selectedAngle;
            MainAngleLabel.Text = GetFriendlyAngleLabel(selectedAngle);
            MainAngleLabel.Tag = selectedAngle;

            // Update the aux card's label to show the old main angle
            var grid = border.Child as Grid;
            if (grid != null)
            {
                foreach (var child in grid.Children)
                {
                    if (child is Border labelBorder && labelBorder.Child is TextBlock textBlock)
                    {
                        textBlock.Text = GetFriendlyAngleLabel(mainAngle);
                        break;
                    }
                }
            }

                // Update the border tag so next click knows this card now represents mainAngle
                border.Tag = mainAngle;
                RebuildAuxPlayerMapFromCards();

                SeekAllPlayers(currentTime);
                SetPlaybackRateForAll(_playbackRate);
                SetPlaybackControlsEnabled(true);
                _isLoadingSegment = false;

                if (wasPlaying)
                {
                    Play();
                }
            }
            catch (Exception ex)
            {
                ActiveClipSubtitle.Text = "Camera swap error: " + ex.Message;
                CrashLogger.Log("Aux camera swap", ex);
            }
            finally
            {
                if (loadVersion == _segmentLoadVersion)
                {
                    _isLoadingSegment = false;
                    SetPlaybackActivity(false);
                    SetPlaybackControlsEnabled(HasPlayerSource(MainPlayer));
                }
            }
        }

        private bool isPlaying
        {
            get
            {
                try
                {
                    return MainPlayer.MediaPlayer != null && MainPlayer.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("Read playback state", ex);
                    return false;
                }
            }
        }

        // --- BUTTON EVENTS ---
        private void MarkStartBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _exportStartSeconds = GetMarkerTimeSeconds();
                if (_exportEndSeconds.HasValue && _exportEndSeconds.Value <= _exportStartSeconds.Value)
                {
                    _exportEndSeconds = null;
                }

                ActiveClipSubtitle.Text = $"Export IN set at {FormatSecs(_exportStartSeconds.Value)}.";
                UpdateMarkerControls();
                UpdateExportMarkerVisuals();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Set export start marker", ex);
            }
        }

        private void MarkEndBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _exportEndSeconds = GetMarkerTimeSeconds();
                if (_exportStartSeconds.HasValue && _exportEndSeconds.Value <= _exportStartSeconds.Value)
                {
                    ActiveClipSubtitle.Text = "OUT marker must be after IN marker.";
                }
                else
                {
                    ActiveClipSubtitle.Text = $"Export OUT set at {FormatSecs(_exportEndSeconds.Value)}.";
                }

                UpdateMarkerControls();
                UpdateExportMarkerVisuals();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Set export end marker", ex);
            }
        }

        private void ClearMarkersBtn_Click(object sender, RoutedEventArgs e)
        {
            ResetExportMarkers();
            ActiveClipSubtitle.Text = "Export markers cleared.";
        }

        private void ExportRangeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting) return;

            if (!_exportStartSeconds.HasValue || !_exportEndSeconds.HasValue || _exportEndSeconds.Value <= _exportStartSeconds.Value)
            {
                ActiveClipSubtitle.Text = "Set valid IN and OUT markers before exporting.";
                return;
            }

            if (sender is FrameworkElement exportButton)
            {
                FlyoutBase.ShowAttachedFlyout(exportButton);
            }
        }

        private async void ExportCurrentViewMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportOptionsFlyout?.Hide();
            await ExportMarkedRangeAsync(exportAllViews: false);
        }

        private async void ExportAllViewsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportOptionsFlyout?.Hide();
            await ExportMarkedRangeAsync(exportAllViews: true);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement browseButton)
            {
                FlyoutBase.ShowAttachedFlyout(browseButton);
            }
        }

        private async void BrowseFolderSource_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SourceOptionsFlyout?.Hide();
                var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                folderPicker.FileTypeFilter.Add("*");

                // Associate window handle with picker (Required in WinUI 3)
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder != null)
                {
                    DirTextBox.Text = folder.Path;
                    TriggerScan(folder.Path);
                }
            }
            catch (Exception ex)
            {
                ActiveClipSubtitle.Text = "Folder picker error: " + ex.Message;
                CrashLogger.Log("Browse folder", ex);
            }
        }

        private async void BrowseArchiveSource_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SourceOptionsFlyout?.Hide();
                var filePicker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary
                };
                filePicker.FileTypeFilter.Add(".zip");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);

                var file = await filePicker.PickSingleFileAsync();
                if (file != null)
                {
                    DirTextBox.Text = file.Path;
                    TriggerScan(file.Path);
                }
            }
            catch (Exception ex)
            {
                ActiveClipSubtitle.Text = "Archive picker error: " + ex.Message;
                CrashLogger.Log("Browse archive", ex);
            }
        }

        private void Window_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            try
            {
                // Focus check: if user is typing in TextBox or SearchBox, ignore key triggers
                var focusElement = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
                if (focusElement is TextBox) return;

                switch (e.Key)
                {
                    case Windows.System.VirtualKey.Space:
                        e.Handled = true;
                        if (isPlaying) Pause();
                        else Play();
                        break;

                    case Windows.System.VirtualKey.Left:
                        e.Handled = true;
                        if (MainPlayer.MediaPlayer != null)
                        {
                            double target = Math.Max(0.0, GetGlobalPlaybackSeconds() - 5.0);
                            SeekToGlobalTime(target, resumePlayback: isPlaying);
                        }
                        break;

                    case Windows.System.VirtualKey.Right:
                        e.Handled = true;
                        if (MainPlayer.MediaPlayer != null)
                        {
                            double target = Math.Min(_activeClipDurationSeconds, GetGlobalPlaybackSeconds() + 5.0);
                            SeekToGlobalTime(target, resumePlayback: isPlaying);
                        }
                        break;

                    case Windows.System.VirtualKey.Up:
                        e.Handled = true;
                        if (SpeedComboBox.SelectedIndex < SpeedComboBox.Items.Count - 1)
                        {
                            SpeedComboBox.SelectedIndex++;
                        }
                        break;

                    case Windows.System.VirtualKey.Down:
                        e.Handled = true;
                        if (SpeedComboBox.SelectedIndex > 0)
                        {
                            SpeedComboBox.SelectedIndex--;
                        }
                        break;

                    // View swaps: 1-5 keys
                    case Windows.System.VirtualKey.Number1:
                    case Windows.System.VirtualKey.NumberPad1:
                        e.Handled = true;
                        TriggerSwapByTag((string)CardLeftRepeater.Tag, CardLeftRepeater);
                        break;

                    case Windows.System.VirtualKey.Number2:
                    case Windows.System.VirtualKey.NumberPad2:
                        e.Handled = true;
                        TriggerSwapByTag((string)CardRightRepeater.Tag, CardRightRepeater);
                        break;

                    case Windows.System.VirtualKey.Number3:
                    case Windows.System.VirtualKey.NumberPad3:
                        e.Handled = true;
                        TriggerSwapByTag((string)CardLeftPillar.Tag, CardLeftPillar);
                        break;

                    case Windows.System.VirtualKey.Number4:
                    case Windows.System.VirtualKey.NumberPad4:
                        e.Handled = true;
                        TriggerSwapByTag((string)CardRightPillar.Tag, CardRightPillar);
                        break;

                    case Windows.System.VirtualKey.Number5:
                    case Windows.System.VirtualKey.NumberPad5:
                        e.Handled = true;
                        TriggerSwapByTag((string)CardBack.Tag, CardBack);
                        break;
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Keyboard shortcut", ex);
            }
        }

        private void TriggerSwapByTag(string tag, Border card)
        {
            if (string.IsNullOrEmpty(tag) || _activeClip == null || _activeSegment == null) return;
            // Simulate pointer press swap
            AuxCard_PointerPressed(card, null);
        }

        private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
        {
            AutoDetectTeslaCam(initialScan: false);
        }

        private void AutoDetectTeslaCam(bool initialScan = true)
        {
            try
            {
                string detectedPath = null;
                // Scan all ready system drives (D:, E:, F:, C:, etc.) for a "TeslaCam" folder
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady)
                    {
                        string possiblePath = Path.Combine(drive.Name, "TeslaCam");
                        if (Directory.Exists(possiblePath))
                        {
                            detectedPath = possiblePath;
                            break;
                        }
                    }
                }

                if (detectedPath != null)
                {
                    DirTextBox.Text = detectedPath;
                    TriggerScan(detectedPath);
                }
                else if (!initialScan)
                {
                    ActiveClipSubtitle.Text = "Auto-detect: No TeslaCam folder found on any ready drives.";
                }
            }
            catch (Exception ex)
            {
                ActiveClipSubtitle.Text = "Auto-detect error: " + ex.Message;
                CrashLogger.Log("Auto-detect TeslaCam", ex);
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                var rdo = sender as ToggleButton;
                if (rdo == null || ClipsListView == null) return;

                // Handle mutual exclusion
                TabRecent.IsChecked = rdo == TabRecent;
                TabSaved.IsChecked = rdo == TabSaved;
                TabSentry.IsChecked = rdo == TabSentry;

                if (rdo == TabRecent) _activeCategory = "RecentClips";
                else if (rdo == TabSaved) _activeCategory = "SavedClips";
                else if (rdo == TabSentry) _activeCategory = "SentryClips";

                FilterAndRenderClips();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Tab checked", ex);
            }
        }

        private void SidebarResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                var handle = sender as UIElement;
                var root = Content as FrameworkElement;
                if (handle == null || root == null || SidebarColumn == null)
                {
                    return;
                }

                _isResizingSidebar = true;
                SetSidebarResizeHighlight(true);
                _sidebarResizeStartX = e.GetCurrentPoint(root).Position.X;
                _sidebarResizeStartWidth = SidebarColumn.ActualWidth > 0.0
                    ? SidebarColumn.ActualWidth
                    : SidebarColumn.Width.Value;

                handle.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Sidebar resize pointer pressed", ex);
            }
        }

        private void SidebarResizeHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                _isSidebarResizeEdgePointerOver = true;
                SetSidebarResizeHighlight(true);
                SetSystemCursor(IdcSizeWestEast);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Sidebar resize pointer entered", ex);
            }
        }

        private void SidebarResizeHandle_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                _isSidebarResizeEdgePointerOver = false;
                if (!_isResizingSidebar)
                {
                    SetSidebarResizeHighlight(false);
                    SetSystemCursor(IdcArrow);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Sidebar resize pointer exited", ex);
            }
        }

        private void SidebarResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizingSidebar)
            {
                return;
            }

            try
            {
                var root = Content as FrameworkElement;
                if (root == null || SidebarColumn == null)
                {
                    return;
                }

                SetSystemCursor(IdcSizeWestEast);
                double pointerX = e.GetCurrentPoint(root).Position.X;
                double delta = pointerX - _sidebarResizeStartX;
                double maxWidthForWindow = Math.Max(SidebarMinWidth, root.ActualWidth - MainContentMinWidth);
                double maxWidth = Math.Min(SidebarMaxWidth, maxWidthForWindow);
                double targetWidth = Math.Max(SidebarMinWidth, Math.Min(maxWidth, _sidebarResizeStartWidth + delta));

                SidebarColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Sidebar resize pointer moved", ex);
            }
        }

        private void SidebarResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndSidebarResize(sender as UIElement, e);
        }

        private void SidebarResizeHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            EndSidebarResize(sender as UIElement, e);
        }

        private void EndSidebarResize(UIElement handle, PointerRoutedEventArgs e)
        {
            try
            {
                _isResizingSidebar = false;
                handle?.ReleasePointerCapture(e.Pointer);
                SetSidebarResizeHighlight(_isSidebarResizeEdgePointerOver);
                SetSystemCursor(_isSidebarResizeEdgePointerOver ? IdcSizeWestEast : IdcArrow);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Sidebar resize ended", ex);
            }
        }

        private void SetSidebarResizeHighlight(bool isVisible)
        {
            if (SidebarResizeHighlight != null)
            {
                SidebarResizeHighlight.Opacity = isVisible ? 0.95 : 0.0;
            }
        }

        private void SetSystemCursor(int cursorId)
        {
            try
            {
                IntPtr cursor = LoadCursor(IntPtr.Zero, cursorId);
                if (cursor != IntPtr.Zero)
                {
                    SetCursor(cursor);
                }
            }
            catch { }
        }

        private void ClipViewMode_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                var toggle = sender as ToggleButton;
                if (toggle == null || ClipListModeButton == null || ClipCollageModeButton == null) return;

                ClipListModeButton.IsChecked = toggle == ClipListModeButton;
                ClipCollageModeButton.IsChecked = toggle == ClipCollageModeButton;
                _isCollageMode = toggle == ClipCollageModeButton;

                UpdateClipViewMode();
                QueueThumbnailsForRealizedClips();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Clip view mode checked", ex);
            }
        }

        private void UpdateClipViewMode()
        {
            if (ClipsListView == null || ClipsCollageView == null) return;

            ClipsListView.Visibility = _isCollageMode ? Visibility.Collapsed : Visibility.Visible;
            ClipsCollageView.Visibility = _isCollageMode ? Visibility.Visible : Visibility.Collapsed;

            TeslaClip selected = _activeClip;
            if (selected != null && !_filteredClips.Contains(selected))
            {
                selected = null;
            }

            _isSyncingClipSelection = true;
            try
            {
                ClipsListView.SelectedItem = selected;
                ClipsCollageView.SelectedItem = selected;
            }
            finally
            {
                _isSyncingClipSelection = false;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                FilterAndRenderClips();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Search text changed", ex);
            }
        }

        private void ClipsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_isSyncingClipSelection) return;

                var selector = sender as Selector;
                var selected = selector?.SelectedItem as TeslaClip;
                if (selected != null)
                {
                    _isSyncingClipSelection = true;
                    try
                    {
                        if (!ReferenceEquals(sender, ClipsListView))
                        {
                            ClipsListView.SelectedItem = selected;
                        }

                        if (!ReferenceEquals(sender, ClipsCollageView))
                        {
                            ClipsCollageView.SelectedItem = selected;
                        }
                    }
                    finally
                    {
                        _isSyncingClipSelection = false;
                    }

                    bool keepPlaying = _isAutoAdvancing || isPlaying;
                    _isAutoAdvancing = false;
                    SelectClip(selected, keepPlaying);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Clip selection changed", ex);
            }
        }

        private void ClipsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (sender == ClipsCollageView && !args.InRecycleQueue && args.Item is TeslaClip clip)
            {
                QueueThumbnailForClip(clip);
            }

#if DEBUG
            try
            {
                if (!args.InRecycleQueue && args.ItemIndex % 50 == 0)
                {
                    int realized = 0;
                    foreach (object item in sender.Items)
                    {
                        if (sender.ContainerFromItem(item) != null)
                        {
                            realized++;
                        }
                    }

                    Debug.WriteLine($"Clip list virtualization: realized {realized} containers for {sender.Items.Count} clips.");
                }
            }
            catch { }
#endif
        }

        private void TimelineSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                UpdateTimelineScrubberVisual();

                if (MainPlayer.MediaPlayer == null) return;
                if (_isUpdatingTimeline || _isLoadingSegment) return;

                TimeCurrentText.Text = FormatSecs(e.NewValue);
                var target = GetSegmentPositionForGlobalTime(e.NewValue);
                if (target.SegmentIndex == _activeSegmentIndex)
                {
                    UpdateHUD(target.LocalSeconds);
                }

                if (Math.Abs(GetGlobalPlaybackSeconds() - e.NewValue) > 0.1)
                {
                    SeekToGlobalTime(
                        e.NewValue,
                        resumePlayback: _isSliderDragging ? _resumePlaybackAfterScrub : isPlaying,
                        keepTimelineInteractive: _isSliderDragging);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Timeline value changed", ex);
            }
        }

        private void TimelineScrubber_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!TimelineSlider.IsEnabled) return;

                _resumePlaybackAfterScrub = isPlaying;
                _isSliderDragging = true;
                TimelineScrubberHost.CapturePointer(e.Pointer);
                SetTimelineValueFromPointer(e);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Timeline pointer pressed", ex);
            }
        }

        private void TimelineScrubber_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isSliderDragging || !TimelineSlider.IsEnabled) return;

                SetTimelineValueFromPointer(e);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Timeline pointer moved", ex);
            }
        }

        private void TimelineScrubber_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isSliderDragging) return;

                SetTimelineValueFromPointer(e);
                bool resumePlayback = _resumePlaybackAfterScrub;
                _resumePlaybackAfterScrub = false;
                _isSliderDragging = false;
                TimelineScrubberHost.ReleasePointerCapture(e.Pointer);
                if (MainPlayer.MediaPlayer != null)
                {
                    SeekToGlobalTime(TimelineSlider.Value, resumePlayback: resumePlayback);
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Timeline pointer released", ex);
            }
        }

        private void TimelineScrubber_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isSliderDragging) return;

                bool resumePlayback = _resumePlaybackAfterScrub;
                _resumePlaybackAfterScrub = false;
                _isSliderDragging = false;

                if (MainPlayer.MediaPlayer != null)
                {
                    SeekToGlobalTime(TimelineSlider.Value, resumePlayback: resumePlayback);
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Timeline pointer canceled", ex);
            }
        }

        private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (MainPlayer.MediaPlayer == null) return;
                var item = SpeedComboBox.SelectedItem as ComboBoxItem;
                if (item == null) return;

                if (!double.TryParse(item.Tag as string, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rate))
                {
                    return;
                }
                _playbackRate = rate;
                SetPlaybackRateForAll(rate);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Speed selection changed", ex);
            }
        }

        private List<TeslaClip> BuildRecentClipSessions(List<TeslaClipSegment> sortedSegments, string category)
        {
            var sessions = new List<TeslaClip>();
            var currentSegments = new List<TeslaClipSegment>();
            DateTime previousTimestamp = default;
            bool previousParsed = false;

            foreach (var segment in sortedSegments)
            {
                bool currentParsed = TryParseTeslaTimestamp(segment.Timestamp, out DateTime currentTimestamp);
                bool startsNewSession = false;

                if (currentSegments.Count > 0)
                {
                    if (previousParsed && currentParsed)
                    {
                        startsNewSession = (currentTimestamp - previousTimestamp).TotalSeconds > RecentClipSessionGapSeconds;
                    }
                    else
                    {
                        startsNewSession = segment.Timestamp != currentSegments[currentSegments.Count - 1].Timestamp;
                    }
                }

                if (startsNewSession)
                {
                    sessions.Add(CreateRecentClipSession(currentSegments, category));
                    currentSegments = new List<TeslaClipSegment>();
                }

                currentSegments.Add(segment);
                previousTimestamp = currentTimestamp;
                previousParsed = currentParsed;
            }

            if (currentSegments.Count > 0)
            {
                sessions.Add(CreateRecentClipSession(currentSegments, category));
            }

            return sessions;
        }

        private TeslaClip CreateRecentClipSession(List<TeslaClipSegment> segments, string category)
        {
            var sessionSegments = segments.OrderBy(s => s.Timestamp).ToList();
            var first = sessionSegments[0];
            var last = sessionSegments[sessionSegments.Count - 1];
            var display = BuildClipDisplayMetadata(first.Timestamp, last.Timestamp, sessionSegments.Count);

            return new TeslaClip
            {
                Timestamp = last.Timestamp,
                Category = category,
                Title = display.Title,
                DateText = display.DateText,
                TimeRangeText = display.TimeRangeText,
                DurationText = display.DurationText,
                ClipTypeText = display.TypeText,
                Segments = sessionSegments
            };
        }

        private sealed class ClipDisplayMetadata
        {
            public string Title { get; set; }
            public string DateText { get; set; }
            public string TimeRangeText { get; set; }
            public string DurationText { get; set; }
            public string TypeText { get; set; }
        }

        private sealed class ClipTelemetrySummary
        {
            public int TelemetryRecordCount { get; set; }
            public int FsdRecordCount { get; set; }
            public int FsdDisengagementCount { get; set; }
            public double TelemetrySeconds { get; set; }
            public double FsdSeconds { get; set; }
        }

        private sealed class PersistentClipTelemetrySummaryCacheEntry
        {
            public string Key { get; set; }
            public long LastAccessUtcTicks { get; set; }
            public AutopilotTelemetrySummary Summary { get; set; }
        }

        private string FormatClipTitle(string firstTimestamp, string lastTimestamp, int segmentCount, string suffix = null)
        {
            return BuildClipDisplayMetadata(firstTimestamp, lastTimestamp, segmentCount, suffix).Title;
        }

        private ClipDisplayMetadata BuildClipDisplayMetadata(string firstTimestamp, string lastTimestamp, int segmentCount, string suffix = null)
        {
            string duration = FormatDuration(EstimateClipDurationSeconds(segmentCount));
            string typeText = string.IsNullOrWhiteSpace(suffix) ? "Recent" : suffix;

            if (!TryParseTeslaTimestamp(firstTimestamp, out DateTime firstLocal) ||
                !TryParseTeslaTimestamp(lastTimestamp, out DateTime lastLocal))
            {
                string fallbackDateText = FormatDate(firstTimestamp);
                string timeText = FormatTime(firstTimestamp);
                return new ClipDisplayMetadata
                {
                    Title = $"{fallbackDateText} | {timeText} | {duration}{(string.IsNullOrWhiteSpace(suffix) ? "" : $" | {suffix}")}",
                    DateText = fallbackDateText,
                    TimeRangeText = timeText,
                    DurationText = duration,
                    TypeText = typeText
                };
            }

            DateTime endLocal = lastLocal.AddSeconds(60.0);
            string dateText = firstLocal.Date == endLocal.Date
                ? FormatLocalDate(firstLocal)
                : $"{FormatLocalDate(firstLocal)} - {FormatLocalDate(endLocal)}";
            string timeRangeText = $"{FormatLocalTime(firstLocal)} - {FormatLocalTime(endLocal)}";

            return new ClipDisplayMetadata
            {
                Title = $"{dateText} | {timeRangeText} | {duration}{(string.IsNullOrWhiteSpace(suffix) ? "" : $" | {suffix}")}",
                DateText = dateText,
                TimeRangeText = timeRangeText,
                DurationText = duration,
                TypeText = typeText
            };
        }

        private double EstimateClipDurationSeconds(int segmentCount)
        {
            return Math.Max(1, segmentCount) * 60.0;
        }

        private string FormatDuration(double seconds)
        {
            int totalMinutes = Math.Max(1, (int)Math.Round(seconds / 60.0));
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;

            if (hours > 0 && minutes > 0)
            {
                return $"{hours} hr {minutes} min";
            }

            if (hours > 0)
            {
                return $"{hours} hr";
            }

            return $"{minutes} min";
        }

        private string FormatLocalTime(DateTime localTime)
        {
            return localTime.ToString("h:mm tt", CultureInfo.CurrentCulture).ToLowerInvariant();
        }

        private string FormatLocalDate(DateTime localTime)
        {
            return localTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }

        private bool TryParseTeslaTimestamp(string timestamp, out DateTime parsed)
        {
            return DateTime.TryParseExact(
                timestamp,
                "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed);
        }

        // --- FORMATTING HELPERS ---
        private string FormatDate(string ts)
        {
            if (TryParseTeslaTimestamp(ts, out DateTime localTime))
            {
                return FormatLocalDate(localTime);
            }

            var parts = ts.Split('_');
            return parts[0];
        }

        private string FormatTime(string ts)
        {
            if (TryParseTeslaTimestamp(ts, out DateTime localTime))
            {
                return FormatLocalTime(localTime);
            }

            var parts = ts.Split('_');
            if (parts.Length < 2) return ts;
            return parts[1].Replace('-', ':');
        }

        private string FormatSecs(double secs)
        {
            int m = (int)(secs / 60);
            int s = (int)(secs % 60);
            return $"{m:00}:{s:00}";
        }

        private string GetHeadingLetter(double deg)
        {
            // Normalize deg to [0, 360) to support negative degrees from telemetry files gracefully
            deg = deg % 360.0;
            if (deg < 0) deg += 360.0;

            int idx = (int)(Math.Round(deg / 45.0) % 8.0);
            string[] dirs = new string[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return dirs[idx];
        }

        private string GetFriendlyAngleLabel(string cam)
        {
            switch (cam)
            {
                case "front": return "FRONT CAMERA";
                case "back": return "REAR VIEW";
                case "left_repeater": return "L REPEATER (FENDER)";
                case "right_repeater": return "R REPEATER (FENDER)";
                case "left_pillar": return "L PILLAR (B-PILLAR)";
                case "right_pillar": return "R PILLAR (B-PILLAR)";
                default: return cam.ToUpper();
            }
        }
    }

    // --- REPRESENTATIVE STRUCTS ---
    public class TeslaClipSegment
    {
        public string Timestamp { get; set; }
        public Dictionary<string, string> Cameras { get; set; } = new Dictionary<string, string>();
        public double EstimatedDurationSeconds { get; set; } = 60.0;
    }

    public class TeslaClip : INotifyPropertyChanged
    {
        private ImageSource _thumbnailSource;
        private string _fsdPercentText = "--% FSD";
        private string _disengagementCountText = "-- diseng.";

        public event PropertyChangedEventHandler PropertyChanged;

        public string Timestamp { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string DateText { get; set; }
        public string TimeRangeText { get; set; }
        public string DurationText { get; set; }
        public string ClipTypeText { get; set; }
        public List<TeslaClipSegment> Segments { get; set; } = new List<TeslaClipSegment>();

        public string FsdPercentText
        {
            get => _fsdPercentText;
            set
            {
                if (_fsdPercentText != value)
                {
                    _fsdPercentText = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FsdPercentText)));
                }
            }
        }

        public string DisengagementCountText
        {
            get => _disengagementCountText;
            set
            {
                if (_disengagementCountText != value)
                {
                    _disengagementCountText = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisengagementCountText)));
                }
            }
        }

        public ImageSource ThumbnailSource
        {
            get => _thumbnailSource;
            set
            {
                if (!ReferenceEquals(_thumbnailSource, value))
                {
                    _thumbnailSource = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailSource)));
                }
            }
        }

        public string SegmentCountText
        {
            get
            {
                int count = Segments?.Count ?? 0;
                return count == 1 ? "1 segment" : $"{count} segments";
            }
        }

        public string CameraCountText
        {
            get
            {
                if (Segments == null || Segments.Count == 0) return "0 cameras";
                int maxCam = Segments.Max(s => s.Cameras?.Count ?? 0);
                return maxCam == 1 ? "1 camera" : $"{maxCam} cameras";
            }
        }
        
        public string CameraCountStr
        {
            get
            {
                if (Segments == null || Segments.Count == 0) return "No Camera Angles Active";
                int segCount = Segments.Count;
                int maxCam = Segments.Max(s => s.Cameras?.Count ?? 0);
                if (segCount == 1) return $"{maxCam} Camera Angles Active";
                return $"{segCount} Segments, {maxCam} Camera Angles Active";
            }
        }
    }

    public class SeiMetadata
    {
        public uint Version { get; set; }
        public uint GearState { get; set; }
        public ulong FrameSeqNo { get; set; }
        public float VehicleSpeedMps { get; set; }
        public float AcceleratorPedalPosition { get; set; }
        public float SteeringWheelAngle { get; set; }
        public bool BlinkerOnLeft { get; set; }
        public bool BlinkerOnRight { get; set; }
        public bool BrakeApplied { get; set; }
        public uint AutopilotState { get; set; }
        public double LatitudeDeg { get; set; }
        public double LongitudeDeg { get; set; }
        public double HeadingDeg { get; set; }
        public double LinearAccelerationX { get; set; }
        public double LinearAccelerationY { get; set; }
        public double LinearAccelerationZ { get; set; }
        public double OffsetSec { get; set; }
    }

    public sealed class AutopilotTelemetrySummary
    {
        public static readonly AutopilotTelemetrySummary Empty = new AutopilotTelemetrySummary();

        public int TelemetryRecordCount { get; set; }
        public int FsdRecordCount { get; set; }
        public int FsdDisengagementCount { get; set; }
        public double TelemetrySeconds { get; set; }
        public double FsdSeconds { get; set; }
        public bool FirstIsFsdEngaged { get; set; }
        public bool LastIsFsdEngaged { get; set; }
    }

    public sealed class AutopilotDisengagementMarkers
    {
        public static readonly AutopilotDisengagementMarkers Empty = new AutopilotDisengagementMarkers();

        public int TelemetryRecordCount { get; set; }
        public bool FirstIsFsdEngaged { get; set; }
        public bool LastIsFsdEngaged { get; set; }
        public List<double> OffsetsSeconds { get; set; } = new List<double>();
    }

    // --- 100% NATIVE C# TELEMETRY PROTOBUF DECODER ---
    public static class TeslaSeiParser
    {
        private readonly struct AutopilotTelemetrySample
        {
            public AutopilotTelemetrySample(double offsetSec, uint autopilotState, ulong frameSeqNo)
            {
                OffsetSec = offsetSec;
                AutopilotState = autopilotState;
                FrameSeqNo = frameSeqNo;
            }

            public double OffsetSec { get; }
            public uint AutopilotState { get; }
            public ulong FrameSeqNo { get; }
        }

        public static AutopilotTelemetrySummary ExtractAutopilotSummary(string filePath, double durationSeconds = 60.0, CancellationToken cancellationToken = default)
        {
            var samples = new List<AutopilotTelemetrySample>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryExtractAutopilotSamplesWithSampleTiming(filePath, samples, cancellationToken))
                {
                    ExtractAutopilotSamplesFromMdat(filePath, samples, cancellationToken);
                    NormalizeAutopilotSampleOffsets(samples, durationSeconds);
                }

                return BuildAutopilotSummary(samples, durationSeconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error parsing autopilot summary: " + ex.Message);
                return AutopilotTelemetrySummary.Empty;
            }
        }

        public static AutopilotDisengagementMarkers ExtractAutopilotDisengagementMarkers(string filePath, double durationSeconds = 60.0, CancellationToken cancellationToken = default)
        {
            var samples = new List<AutopilotTelemetrySample>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryExtractAutopilotSamplesWithSampleTiming(filePath, samples, cancellationToken))
                {
                    ExtractAutopilotSamplesFromMdat(filePath, samples, cancellationToken);
                    NormalizeAutopilotSampleOffsets(samples, durationSeconds);
                }

                return BuildAutopilotDisengagementMarkers(samples, durationSeconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error parsing autopilot disengagement markers: " + ex.Message);
                return AutopilotDisengagementMarkers.Empty;
            }
        }

        public static List<SeiMetadata> ExtractTelemetry(string filePath, double durationSeconds = 60.0)
        {
            List<SeiMetadata> records = new List<SeiMetadata>();

            try
            {
                if (TryExtractTelemetryWithSampleTiming(filePath, records))
                {
                    return records;
                }

                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    // 1. Locate mdat atom offset
                    long mdatOffset = 0;
                    long mdatSize = 0;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        byte[] lenBytes = br.ReadBytes(4);
                        if (lenBytes.Length < 4) break;
                        uint atomSize = (uint)((lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3]);
                        
                        byte[] typeBytes = br.ReadBytes(4);
                        if (typeBytes.Length < 4) break;
                        string atomType = System.Text.Encoding.ASCII.GetString(typeBytes);
                        
                        long headerSize = 8;
                        if (atomSize == 1)
                        {
                            byte[] extBytes = br.ReadBytes(8);
                            if (extBytes.Length < 8) break;
                            ulong size64 = 0;
                            for (int i = 0; i < 8; i++) size64 = (size64 << 8) | extBytes[i];
                            mdatSize = (long)size64 - 16;
                            headerSize = 16;
                        }
                        else
                        {
                            mdatSize = (long)atomSize - 8;
                        }

                        if (atomType == "mdat")
                        {
                            mdatOffset = fs.Position;
                            break;
                        }

                        if (atomSize < headerSize) break;
                        fs.Seek(mdatSize, SeekOrigin.Current);
                    }

                    if (mdatOffset == 0) return records;

                    // 2. Iterate NAL units inside mdat sequentially
                    long endPosition = mdatOffset + mdatSize;
                    if (mdatSize == 0) endPosition = fs.Length;

                    while (fs.Position + 4 < endPosition)
                    {
                        byte[] sizeBytes = br.ReadBytes(4);
                        if (sizeBytes.Length < 4) break;
                        uint nalSize = (uint)((sizeBytes[0] << 24) | (sizeBytes[1] << 16) | (sizeBytes[2] << 8) | sizeBytes[3]);
                        
                        if (nalSize < 2 || fs.Position + nalSize > endPosition)
                        {
                            fs.Seek(Math.Max(1, (long)nalSize), SeekOrigin.Current);
                            continue;
                        }

                        byte firstByte = br.ReadByte();
                        byte secondByte = br.ReadByte();

                        // Check SEI type (nal_unit_type == 6, payloadType == 5)
                        if ((firstByte & 0x1F) == 6 && secondByte == 5)
                        {
                            byte[] nalRest = br.ReadBytes((int)nalSize - 2);
                            byte[] nal = new byte[nalSize];
                            nal[0] = firstByte;
                            nal[1] = secondByte;
                            Buffer.BlockCopy(nalRest, 0, nal, 2, nalRest.Length);

                            byte[] payload = ExtractProtoPayload(nal);
                            if (payload != null)
                            {
                                SeiMetadata meta = DecodeSeiMetadata(payload);
                                if (meta != null) records.Add(meta);
                            }
                        }
                        else
                        {
                            fs.Seek((long)nalSize - 2, SeekOrigin.Current);
                        }
                    }
                }

                NormalizeTelemetryOffsets(records, durationSeconds);

                // Do not write telemetry cache to the USB drive to keep it strictly read-only
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error parsing: " + ex.Message);
            }
            return records;
        }

        private static bool TryExtractAutopilotSamplesWithSampleTiming(string filePath, List<AutopilotTelemetrySample> samples, CancellationToken cancellationToken)
        {
            Mp4TimingInfo timing = ReadMp4TimingInfo(filePath);
            Mp4TrackInfo videoTrack = timing.GetPreferredVideoTrack();
            if (videoTrack == null)
            {
                return false;
            }

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                foreach (Mp4SampleSpan sample in EnumerateTrackSamples(videoTrack, timing))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ulong fileLength = (ulong)fs.Length;
                    if (sample.SampleSize == 0 ||
                        sample.FileOffset > fileLength ||
                        sample.SampleSize > fileLength - sample.FileOffset)
                    {
                        continue;
                    }

                    fs.Position = (long)sample.FileOffset;
                    ExtractAutopilotSamplesFromSample(
                        br,
                        (long)(sample.FileOffset + sample.SampleSize),
                        sample.PresentationSeconds,
                        videoTrack.NalLengthSize,
                        samples,
                        cancellationToken);
                }
            }

            samples.Sort((left, right) => left.OffsetSec.CompareTo(right.OffsetSec));
            return samples.Count > 0;
        }

        private static void ExtractAutopilotSamplesFromMdat(string filePath, List<AutopilotTelemetrySample> samples, CancellationToken cancellationToken)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                long mdatOffset = 0;
                long mdatSize = 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] lenBytes = br.ReadBytes(4);
                    if (lenBytes.Length < 4) break;
                    uint atomSize = (uint)((lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3]);

                    byte[] typeBytes = br.ReadBytes(4);
                    if (typeBytes.Length < 4) break;
                    string atomType = System.Text.Encoding.ASCII.GetString(typeBytes);

                    long headerSize = 8;
                    if (atomSize == 1)
                    {
                        byte[] extBytes = br.ReadBytes(8);
                        if (extBytes.Length < 8) break;
                        ulong size64 = 0;
                        for (int i = 0; i < 8; i++) size64 = (size64 << 8) | extBytes[i];
                        mdatSize = (long)size64 - 16;
                        headerSize = 16;
                    }
                    else
                    {
                        mdatSize = (long)atomSize - 8;
                    }

                    if (atomType == "mdat")
                    {
                        mdatOffset = fs.Position;
                        break;
                    }

                    if (atomSize < headerSize) break;
                    fs.Seek(mdatSize, SeekOrigin.Current);
                }

                if (mdatOffset == 0)
                {
                    return;
                }

                long endPosition = mdatOffset + mdatSize;
                if (mdatSize == 0) endPosition = fs.Length;

                while (fs.Position + 4 < endPosition)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] sizeBytes = br.ReadBytes(4);
                    if (sizeBytes.Length < 4) break;
                    uint nalSize = (uint)((sizeBytes[0] << 24) | (sizeBytes[1] << 16) | (sizeBytes[2] << 8) | sizeBytes[3]);

                    if (nalSize < 2 || fs.Position + nalSize > endPosition || nalSize > int.MaxValue)
                    {
                        fs.Seek(Math.Max(1, (long)nalSize), SeekOrigin.Current);
                        continue;
                    }

                    byte firstByte = br.ReadByte();
                    byte secondByte = br.ReadByte();
                    if (IsSeiNalHeader(firstByte))
                    {
                        byte[] nal = new byte[nalSize];
                        nal[0] = firstByte;
                        nal[1] = secondByte;
                        int restLength = (int)nalSize - 2;
                        byte[] nalRest = br.ReadBytes(restLength);
                        if (nalRest.Length == restLength)
                        {
                            Buffer.BlockCopy(nalRest, 0, nal, 2, nalRest.Length);
                            if (TryExtractAutopilotSampleNal(nal, 0.0, out AutopilotTelemetrySample autopilotSample))
                            {
                                samples.Add(autopilotSample);
                            }
                        }
                    }
                    else
                    {
                        fs.Seek((long)nalSize - 2, SeekOrigin.Current);
                    }
                }
            }
        }

        private static void ExtractAutopilotSamplesFromSample(
            BinaryReader br,
            long sampleEnd,
            double sampleOffsetSec,
            int nalLengthSize,
            List<AutopilotTelemetrySample> samples,
            CancellationToken cancellationToken)
        {
            nalLengthSize = Math.Max(1, Math.Min(4, nalLengthSize));
            int sampleSize = (int)Math.Min(int.MaxValue, Math.Max(0, sampleEnd - br.BaseStream.Position));
            if (sampleSize <= nalLengthSize)
            {
                return;
            }

            byte[] sampleData = br.ReadBytes(sampleSize);
            int pos = 0;
            while (pos + nalLengthSize <= sampleData.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int nalLengthOffset = pos;
                uint nalSize = ReadNalLength(sampleData, ref pos, nalLengthSize);
                if (nalSize < 2 || nalSize > int.MaxValue || pos + (int)nalSize > sampleData.Length)
                {
                    pos = Math.Min(sampleData.Length, nalLengthOffset + 1);
                    continue;
                }

                byte firstByte = sampleData[pos];
                if (IsSeiNalHeader(firstByte))
                {
                    byte[] nal = new byte[nalSize];
                    Buffer.BlockCopy(sampleData, pos, nal, 0, (int)nalSize);

                    if (TryExtractAutopilotSampleNal(nal, sampleOffsetSec, out AutopilotTelemetrySample autopilotSample))
                    {
                        samples.Add(autopilotSample);
                    }
                }

                pos += (int)nalSize;
            }
        }

        private static bool TryExtractAutopilotSampleNal(byte[] nal, double offsetSec, out AutopilotTelemetrySample sample)
        {
            sample = default;
            if (nal == null || nal.Length < 2 || !IsSeiNalHeader(nal[0]))
            {
                return false;
            }

            byte[] payload = ExtractProtoPayload(nal);
            if (payload == null ||
                !TryDecodeAutopilotTelemetry(payload, out uint autopilotState, out ulong frameSeqNo))
            {
                return false;
            }

            sample = new AutopilotTelemetrySample(offsetSec, autopilotState, frameSeqNo);
            return true;
        }

        private static bool IsSeiNalHeader(byte firstByte)
        {
            int h264NalType = firstByte & 0x1F;
            int h265NalType = (firstByte >> 1) & 0x3F;
            return h264NalType == 6 || h265NalType == 39 || h265NalType == 40;
        }

        private static AutopilotTelemetrySummary BuildAutopilotSummary(List<AutopilotTelemetrySample> samples, double durationSeconds)
        {
            if (samples == null || samples.Count == 0)
            {
                return AutopilotTelemetrySummary.Empty;
            }

            double duration = durationSeconds > 0.0 && !double.IsNaN(durationSeconds) && !double.IsInfinity(durationSeconds)
                ? durationSeconds
                : 60.0;
            var summary = new AutopilotTelemetrySummary();
            double fallbackIntervalSeconds = duration / samples.Count;
            bool? previousIsFsdEngaged = null;

            for (int i = 0; i < samples.Count; i++)
            {
                AutopilotTelemetrySample sample = samples[i];
                bool isFsdEngaged = sample.AutopilotState == 1;
                if (i == 0)
                {
                    summary.FirstIsFsdEngaged = isFsdEngaged;
                }

                summary.LastIsFsdEngaged = isFsdEngaged;
                summary.TelemetryRecordCount++;
                if (isFsdEngaged)
                {
                    summary.FsdRecordCount++;
                }

                double intervalStart = i == 0 ? 0.0 : ClampTelemetryOffset(sample.OffsetSec, duration);
                double intervalEnd = i + 1 < samples.Count
                    ? ClampTelemetryOffset(samples[i + 1].OffsetSec, duration)
                    : duration;

                if (intervalEnd <= intervalStart)
                {
                    intervalEnd = Math.Min(duration, intervalStart + fallbackIntervalSeconds);
                }

                double intervalSeconds = Math.Max(0.0, intervalEnd - intervalStart);
                summary.TelemetrySeconds += intervalSeconds;
                if (isFsdEngaged)
                {
                    summary.FsdSeconds += intervalSeconds;
                }

                if (previousIsFsdEngaged == true && !isFsdEngaged)
                {
                    summary.FsdDisengagementCount++;
                }

                previousIsFsdEngaged = isFsdEngaged;
            }

            return summary;
        }

        private static AutopilotDisengagementMarkers BuildAutopilotDisengagementMarkers(List<AutopilotTelemetrySample> samples, double durationSeconds)
        {
            if (samples == null || samples.Count == 0)
            {
                return AutopilotDisengagementMarkers.Empty;
            }

            double duration = durationSeconds > 0.0 && !double.IsNaN(durationSeconds) && !double.IsInfinity(durationSeconds)
                ? durationSeconds
                : 60.0;
            var markers = new AutopilotDisengagementMarkers();
            bool? previousIsFsdEngaged = null;

            for (int i = 0; i < samples.Count; i++)
            {
                AutopilotTelemetrySample sample = samples[i];
                bool isFsdEngaged = sample.AutopilotState == 1;
                if (i == 0)
                {
                    markers.FirstIsFsdEngaged = isFsdEngaged;
                }

                markers.LastIsFsdEngaged = isFsdEngaged;
                markers.TelemetryRecordCount++;

                if (previousIsFsdEngaged == true && !isFsdEngaged)
                {
                    double offsetSeconds = ClampTelemetryOffset(sample.OffsetSec, duration);
                    if (markers.OffsetsSeconds.Count == 0 ||
                        Math.Abs(markers.OffsetsSeconds[markers.OffsetsSeconds.Count - 1] - offsetSeconds) > 0.15)
                    {
                        markers.OffsetsSeconds.Add(offsetSeconds);
                    }
                }

                previousIsFsdEngaged = isFsdEngaged;
            }

            return markers;
        }

        private static void NormalizeAutopilotSampleOffsets(List<AutopilotTelemetrySample> samples, double durationSeconds)
        {
            if (samples == null || samples.Count == 0)
            {
                return;
            }

            if (samples.Any(sample => sample.OffsetSec > 0.0))
            {
                samples.Sort((left, right) => left.OffsetSec.CompareTo(right.OffsetSec));
                return;
            }

            double duration = durationSeconds > 0.0 && !double.IsNaN(durationSeconds) && !double.IsInfinity(durationSeconds)
                ? durationSeconds
                : 60.0;
            if (samples.Count == 1)
            {
                samples[0] = new AutopilotTelemetrySample(0.0, samples[0].AutopilotState, samples[0].FrameSeqNo);
                return;
            }

            ulong firstSeq = samples[0].FrameSeqNo;
            ulong lastSeq = samples[samples.Count - 1].FrameSeqNo;
            if (lastSeq > firstSeq)
            {
                double frameRate = (lastSeq - firstSeq) / duration;
                if (frameRate > 1.0 && !double.IsNaN(frameRate) && !double.IsInfinity(frameRate))
                {
                    for (int i = 0; i < samples.Count; i++)
                    {
                        samples[i] = new AutopilotTelemetrySample(
                            (samples[i].FrameSeqNo - firstSeq) / frameRate,
                            samples[i].AutopilotState,
                            samples[i].FrameSeqNo);
                    }
                    return;
                }
            }

            for (int i = 0; i < samples.Count; i++)
            {
                double offsetSec = (duration * i) / (samples.Count - 1);
                samples[i] = new AutopilotTelemetrySample(offsetSec, samples[i].AutopilotState, samples[i].FrameSeqNo);
            }
        }

        private static double ClampTelemetryOffset(double offsetSeconds, double durationSeconds)
        {
            if (double.IsNaN(offsetSeconds) || double.IsInfinity(offsetSeconds))
            {
                return 0.0;
            }

            return Math.Max(0.0, Math.Min(durationSeconds, offsetSeconds));
        }

        private static bool TryExtractTelemetryWithSampleTiming(string filePath, List<SeiMetadata> records)
        {
            Mp4TimingInfo timing = ReadMp4TimingInfo(filePath);
            Mp4TrackInfo videoTrack = timing.GetPreferredVideoTrack();
            if (videoTrack == null)
            {
                return false;
            }

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                foreach (Mp4SampleSpan sample in EnumerateTrackSamples(videoTrack, timing))
                {
                    ulong fileLength = (ulong)fs.Length;
                    if (sample.SampleSize == 0 ||
                        sample.FileOffset > fileLength ||
                        sample.SampleSize > fileLength - sample.FileOffset)
                    {
                        continue;
                    }

                    fs.Position = (long)sample.FileOffset;
                    ExtractTelemetryFromSample(
                        br,
                        (long)(sample.FileOffset + sample.SampleSize),
                        sample.PresentationSeconds,
                        videoTrack.NalLengthSize,
                        records);
                }
            }

            records.Sort((left, right) => left.OffsetSec.CompareTo(right.OffsetSec));
            return records.Count > 0;
        }

        private static void ExtractTelemetryFromSample(BinaryReader br, long sampleEnd, double sampleOffsetSec, int nalLengthSize, List<SeiMetadata> records)
        {
            nalLengthSize = Math.Max(1, Math.Min(4, nalLengthSize));
            while (br.BaseStream.Position + nalLengthSize <= sampleEnd)
            {
                long nalLengthOffset = br.BaseStream.Position;
                uint nalSize = ReadNalLength(br, nalLengthSize);
                if (nalSize < 2 || br.BaseStream.Position + nalSize > sampleEnd || nalSize > int.MaxValue)
                {
                    br.BaseStream.Position = Math.Min(sampleEnd, nalLengthOffset + 1);
                    continue;
                }

                byte[] nal = br.ReadBytes((int)nalSize);
                SeiMetadata meta = DecodeTelemetryNal(nal);
                if (meta != null)
                {
                    meta.OffsetSec = sampleOffsetSec;
                    records.Add(meta);
                }
            }
        }

        private static IEnumerable<Mp4SampleSpan> EnumerateTrackSamples(Mp4TrackInfo track, Mp4TimingInfo timing)
        {
            if (track == null || track.TimeScale == 0 || track.SampleSizes.Count == 0)
            {
                yield break;
            }

            int sampleIndex = 0;
            ulong decodeTicks = 0;
            for (int chunkIndex = 0; chunkIndex < track.ChunkOffsets.Count && sampleIndex < track.SampleSizes.Count; chunkIndex++)
            {
                SampleToChunkEntry sampleToChunk = GetSampleToChunkEntry(track, chunkIndex + 1);
                ulong fileOffset = track.ChunkOffsets[chunkIndex];

                for (uint sampleInChunk = 0; sampleInChunk < sampleToChunk.SamplesPerChunk && sampleIndex < track.SampleSizes.Count; sampleInChunk++)
                {
                    uint sampleSize = track.SampleSizes[sampleIndex];
                    uint sampleDelta = sampleIndex < track.SampleDeltas.Count
                        ? track.SampleDeltas[sampleIndex]
                        : track.SampleDeltas[track.SampleDeltas.Count - 1];
                    long compositionOffset = sampleIndex < track.CompositionOffsets.Count
                        ? track.CompositionOffsets[sampleIndex]
                        : 0;

                    double mediaSeconds = (decodeTicks / (double)track.TimeScale) + (compositionOffset / (double)track.TimeScale);
                    double presentationSeconds = MapTrackMediaTimeToPresentationSeconds(track, timing.MovieTimeScale, mediaSeconds);
                    yield return new Mp4SampleSpan(fileOffset, sampleSize, presentationSeconds);

                    fileOffset += sampleSize;
                    decodeTicks += sampleDelta;
                    sampleIndex++;
                }
            }
        }

        private static SampleToChunkEntry GetSampleToChunkEntry(Mp4TrackInfo track, int oneBasedChunkIndex)
        {
            SampleToChunkEntry selected = track.SampleToChunks[0];
            for (int i = 0; i < track.SampleToChunks.Count; i++)
            {
                SampleToChunkEntry entry = track.SampleToChunks[i];
                if (entry.FirstChunk > oneBasedChunkIndex)
                {
                    break;
                }

                selected = entry;
            }

            return selected;
        }

        private static double MapTrackMediaTimeToPresentationSeconds(Mp4TrackInfo track, uint movieTimeScale, double mediaSeconds)
        {
            if (track.EditList.Count == 0 || movieTimeScale == 0)
            {
                return Math.Max(0.0, mediaSeconds);
            }

            double presentationCursorSeconds = 0.0;
            foreach (EditListEntry edit in track.EditList)
            {
                double segmentDurationSeconds = edit.SegmentDuration / (double)movieTimeScale;
                if (edit.MediaTime < 0)
                {
                    presentationCursorSeconds += segmentDurationSeconds;
                    continue;
                }

                double mediaRate = edit.MediaRate > 0.0 && !double.IsNaN(edit.MediaRate) ? edit.MediaRate : 1.0;
                double editMediaStartSeconds = edit.MediaTime / (double)track.TimeScale;
                double editMediaEndSeconds = editMediaStartSeconds + (segmentDurationSeconds * mediaRate);

                if (mediaSeconds + 0.000001 >= editMediaStartSeconds &&
                    (segmentDurationSeconds <= 0.0 || mediaSeconds <= editMediaEndSeconds + 0.000001))
                {
                    return Math.Max(0.0, presentationCursorSeconds + ((mediaSeconds - editMediaStartSeconds) / mediaRate));
                }

                presentationCursorSeconds += segmentDurationSeconds;
            }

            bool hasFirstMediaEdit = false;
            EditListEntry firstMediaEdit = default;
            foreach (EditListEntry edit in track.EditList)
            {
                if (edit.MediaTime >= 0)
                {
                    firstMediaEdit = edit;
                    hasFirstMediaEdit = true;
                    break;
                }
            }

            if (hasFirstMediaEdit)
            {
                return Math.Max(0.0, mediaSeconds - (firstMediaEdit.MediaTime / (double)track.TimeScale));
            }

            return Math.Max(0.0, mediaSeconds);
        }

        private static SeiMetadata DecodeTelemetryNal(byte[] nal)
        {
            if (nal == null || nal.Length < 2)
            {
                return null;
            }

            int h264NalType = nal[0] & 0x1F;
            int h265NalType = (nal[0] >> 1) & 0x3F;
            bool isSei = h264NalType == 6 || h265NalType == 39 || h265NalType == 40;
            if (!isSei)
            {
                return null;
            }

            byte[] payload = ExtractProtoPayload(nal);
            return payload != null ? DecodeSeiMetadata(payload) : null;
        }

        private sealed class Mp4TimingInfo
        {
            public long FileLength { get; set; }
            public uint MovieTimeScale { get; set; }
            public List<Mp4TrackInfo> Tracks { get; } = new List<Mp4TrackInfo>();

            public Mp4TrackInfo GetPreferredVideoTrack()
            {
                return Tracks
                    .Where(track => track.IsUsable)
                    .OrderByDescending(track => track.HandlerType == "vide")
                    .ThenByDescending(track => track.HasVideoSampleDescription)
                    .FirstOrDefault();
            }
        }

        private sealed class Mp4TrackInfo
        {
            public uint TrackId { get; set; }
            public string HandlerType { get; set; } = string.Empty;
            public uint TimeScale { get; set; }
            public int NalLengthSize { get; set; } = 4;
            public List<string> SampleDescriptionFormats { get; } = new List<string>();
            public List<uint> SampleSizes { get; } = new List<uint>();
            public List<uint> SampleDeltas { get; } = new List<uint>();
            public List<long> CompositionOffsets { get; } = new List<long>();
            public List<SampleToChunkEntry> SampleToChunks { get; } = new List<SampleToChunkEntry>();
            public List<ulong> ChunkOffsets { get; } = new List<ulong>();
            public List<EditListEntry> EditList { get; } = new List<EditListEntry>();

            public bool HasVideoSampleDescription =>
                SampleDescriptionFormats.Any(format =>
                    format == "avc1" ||
                    format == "avc3" ||
                    format == "hvc1" ||
                    format == "hev1");

            public bool IsUsable =>
                HandlerType == "vide" &&
                TimeScale > 0 &&
                SampleSizes.Count > 0 &&
                SampleDeltas.Count > 0 &&
                SampleToChunks.Count > 0 &&
                ChunkOffsets.Count > 0;
        }

        private readonly struct SampleToChunkEntry
        {
            public SampleToChunkEntry(uint firstChunk, uint samplesPerChunk, uint sampleDescriptionIndex)
            {
                FirstChunk = firstChunk;
                SamplesPerChunk = samplesPerChunk;
                SampleDescriptionIndex = sampleDescriptionIndex;
            }

            public uint FirstChunk { get; }
            public uint SamplesPerChunk { get; }
            public uint SampleDescriptionIndex { get; }
        }

        private readonly struct EditListEntry
        {
            public EditListEntry(ulong segmentDuration, long mediaTime, double mediaRate)
            {
                SegmentDuration = segmentDuration;
                MediaTime = mediaTime;
                MediaRate = mediaRate;
            }

            public ulong SegmentDuration { get; }
            public long MediaTime { get; }
            public double MediaRate { get; }
        }

        private readonly struct Mp4SampleSpan
        {
            public Mp4SampleSpan(ulong fileOffset, uint sampleSize, double presentationSeconds)
            {
                FileOffset = fileOffset;
                SampleSize = sampleSize;
                PresentationSeconds = presentationSeconds;
            }

            public ulong FileOffset { get; }
            public uint SampleSize { get; }
            public double PresentationSeconds { get; }
        }

        private struct Mp4Box
        {
            public long Start;
            public long Size;
            public long HeaderSize;
            public string Type;
            public long PayloadStart => Start + HeaderSize;
            public long End => Start + Size;
        }

        private static Mp4TimingInfo ReadMp4TimingInfo(string filePath)
        {
            var timing = new Mp4TimingInfo();
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                timing.FileLength = fs.Length;
                ParseMp4Container(br, fs.Length, timing, null);
            }
            return timing;
        }

        private static void ParseMp4Container(BinaryReader br, long containerEnd, Mp4TimingInfo timing, Mp4TrackInfo currentTrack)
        {
            while (TryReadMp4Box(br, containerEnd, out Mp4Box box))
            {
                if (box.Type == "trak")
                {
                    var track = new Mp4TrackInfo();
                    br.BaseStream.Position = box.PayloadStart;
                    ParseMp4Container(br, box.End, timing, track);
                    timing.Tracks.Add(track);
                }
                else if (box.Type == "mvhd")
                {
                    ReadMvhd(br, box, timing);
                }
                else if (box.Type == "tkhd" && currentTrack != null)
                {
                    ReadTkhd(br, box, currentTrack);
                }
                else if (box.Type == "mdhd" && currentTrack != null)
                {
                    ReadMdhd(br, box, currentTrack);
                }
                else if (box.Type == "hdlr" && currentTrack != null)
                {
                    ReadHdlr(br, box, currentTrack);
                }
                else if (box.Type == "stsd" && currentTrack != null)
                {
                    ReadStsd(br, box, currentTrack);
                }
                else if (box.Type == "stts" && currentTrack != null)
                {
                    ReadStts(br, box, currentTrack);
                }
                else if (box.Type == "ctts" && currentTrack != null)
                {
                    ReadCtts(br, box, currentTrack);
                }
                else if (box.Type == "stsc" && currentTrack != null)
                {
                    ReadStsc(br, box, currentTrack);
                }
                else if (box.Type == "stco" && currentTrack != null)
                {
                    ReadStco(br, box, currentTrack);
                }
                else if (box.Type == "co64" && currentTrack != null)
                {
                    ReadCo64(br, box, currentTrack);
                }
                else if (box.Type == "stsz" && currentTrack != null)
                {
                    ReadStsz(br, box, currentTrack);
                }
                else if (box.Type == "elst" && currentTrack != null)
                {
                    ReadElst(br, box, currentTrack);
                }
                else if (IsMp4Container(box.Type))
                {
                    br.BaseStream.Position = box.PayloadStart;
                    ParseMp4Container(br, box.End, timing, currentTrack);
                }

                br.BaseStream.Position = box.End;
            }
        }

        private static bool TryReadMp4Box(BinaryReader br, long containerEnd, out Mp4Box box)
        {
            box = default;
            long start = br.BaseStream.Position;
            if (start + 8 > containerEnd)
            {
                return false;
            }

            uint smallSize = ReadUInt32BigEndian(br);
            byte[] typeBytes = br.ReadBytes(4);
            if (typeBytes.Length < 4)
            {
                return false;
            }

            long size = smallSize;
            long headerSize = 8;
            if (smallSize == 1)
            {
                size = (long)ReadUInt64BigEndian(br);
                headerSize = 16;
            }
            else if (smallSize == 0)
            {
                size = containerEnd - start;
            }

            if (size < headerSize || start + size > containerEnd)
            {
                return false;
            }

            box = new Mp4Box
            {
                Start = start,
                Size = size,
                HeaderSize = headerSize,
                Type = System.Text.Encoding.ASCII.GetString(typeBytes)
            };
            return true;
        }

        private static bool IsMp4Container(string type)
        {
            return type == "moov" ||
                   type == "mdia" ||
                   type == "minf" ||
                   type == "stbl" ||
                   type == "edts";
        }

        private static void ReadMvhd(BinaryReader br, Mp4Box box, Mp4TimingInfo timing)
        {
            br.BaseStream.Position = box.PayloadStart;
            byte version = br.ReadByte();
            br.BaseStream.Position += 3; // flags

            if (version == 1)
            {
                br.BaseStream.Position += 16; // creation_time + modification_time
                timing.MovieTimeScale = ReadUInt32BigEndian(br);
            }
            else
            {
                br.BaseStream.Position += 8; // creation_time + modification_time
                timing.MovieTimeScale = ReadUInt32BigEndian(br);
            }
        }

        private static void ReadTkhd(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart;
            byte version = br.ReadByte();
            br.BaseStream.Position += 3; // flags

            if (version == 1)
            {
                br.BaseStream.Position += 16; // creation_time + modification_time
            }
            else
            {
                br.BaseStream.Position += 8; // creation_time + modification_time
            }

            track.TrackId = ReadUInt32BigEndian(br);
        }

        private static void ReadMdhd(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart;
            byte version = br.ReadByte();
            br.BaseStream.Position += 3; // flags

            if (version == 1)
            {
                br.BaseStream.Position += 16; // creation_time + modification_time
                track.TimeScale = ReadUInt32BigEndian(br);
            }
            else
            {
                br.BaseStream.Position += 8; // creation_time + modification_time
                track.TimeScale = ReadUInt32BigEndian(br);
            }
        }

        private static void ReadHdlr(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart + 8; // version/flags + pre_defined
            byte[] handlerBytes = br.ReadBytes(4);
            if (handlerBytes.Length == 4)
            {
                track.HandlerType = System.Text.Encoding.ASCII.GetString(handlerBytes);
            }
        }

        private static void ReadStsd(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart + 4; // version + flags
            uint entryCount = ReadUInt32BigEndian(br);
            track.SampleDescriptionFormats.Clear();

            for (uint i = 0; i < entryCount && br.BaseStream.Position + 8 <= box.End; i++)
            {
                long entryStart = br.BaseStream.Position;
                uint entrySize = ReadUInt32BigEndian(br);
                byte[] formatBytes = br.ReadBytes(4);
                if (formatBytes.Length < 4 || entrySize < 8 || entryStart + entrySize > box.End)
                {
                    break;
                }

                string format = System.Text.Encoding.ASCII.GetString(formatBytes);
                track.SampleDescriptionFormats.Add(format);

                if (format == "avc1" || format == "avc3" || format == "hvc1" || format == "hev1")
                {
                    ReadVideoSampleDescription(br, entryStart + entrySize, track);
                }

                br.BaseStream.Position = entryStart + entrySize;
            }
        }

        private static void ReadVideoSampleDescription(BinaryReader br, long entryEnd, Mp4TrackInfo track)
        {
            long childStart = br.BaseStream.Position + 78;
            if (childStart >= entryEnd)
            {
                return;
            }

            br.BaseStream.Position = childStart;
            while (TryReadMp4Box(br, entryEnd, out Mp4Box childBox))
            {
                if (childBox.Type == "avcC" && childBox.PayloadStart + 5 <= childBox.End)
                {
                    br.BaseStream.Position = childBox.PayloadStart + 4;
                    track.NalLengthSize = (br.ReadByte() & 0x03) + 1;
                }
                else if (childBox.Type == "hvcC" && childBox.PayloadStart + 22 <= childBox.End)
                {
                    br.BaseStream.Position = childBox.PayloadStart + 21;
                    track.NalLengthSize = (br.ReadByte() & 0x03) + 1;
                }

                br.BaseStream.Position = childBox.End;
            }
        }

        private static void ReadStts(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart + 4; // version + flags
            uint entryCount = ReadUInt32BigEndian(br);
            track.SampleDeltas.Clear();

            for (uint i = 0; i < entryCount && br.BaseStream.Position + 8 <= box.End; i++)
            {
                uint sampleCount = ReadUInt32BigEndian(br);
                uint sampleDelta = ReadUInt32BigEndian(br);
                if (sampleCount > 1_000_000)
                {
                    break;
                }

                for (uint sample = 0; sample < sampleCount; sample++)
                {
                    track.SampleDeltas.Add(sampleDelta);
                }
            }
        }

        private static void ReadCtts(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart;
            byte version = br.ReadByte();
            br.BaseStream.Position += 3; // flags
            uint entryCount = ReadUInt32BigEndian(br);
            track.CompositionOffsets.Clear();

            for (uint i = 0; i < entryCount && br.BaseStream.Position + 8 <= box.End; i++)
            {
                uint sampleCount = ReadUInt32BigEndian(br);
                long sampleOffset = version == 1 ? ReadInt32BigEndian(br) : ReadUInt32BigEndian(br);
                if (sampleCount > 1_000_000)
                {
                    break;
                }

                for (uint sample = 0; sample < sampleCount; sample++)
                {
                    track.CompositionOffsets.Add(sampleOffset);
                }
            }
        }

        private static void ReadStsc(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart + 4; // version + flags
            uint entryCount = ReadUInt32BigEndian(br);
            track.SampleToChunks.Clear();

            for (uint i = 0; i < entryCount && br.BaseStream.Position + 12 <= box.End; i++)
            {
                uint firstChunk = ReadUInt32BigEndian(br);
                uint samplesPerChunk = ReadUInt32BigEndian(br);
                uint sampleDescriptionIndex = ReadUInt32BigEndian(br);
                if (firstChunk == 0 || samplesPerChunk == 0)
                {
                    continue;
                }

                track.SampleToChunks.Add(new SampleToChunkEntry(firstChunk, samplesPerChunk, sampleDescriptionIndex));
            }

            track.SampleToChunks.Sort((left, right) => left.FirstChunk.CompareTo(right.FirstChunk));
        }

        private static void ReadStco(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart + 4; // version + flags
            uint entryCount = ReadUInt32BigEndian(br);
            track.ChunkOffsets.Clear();

            for (uint i = 0; i < entryCount && br.BaseStream.Position + 4 <= box.End; i++)
            {
                track.ChunkOffsets.Add(ReadUInt32BigEndian(br));
            }
        }

        private static void ReadCo64(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart + 4; // version + flags
            uint entryCount = ReadUInt32BigEndian(br);
            track.ChunkOffsets.Clear();

            for (uint i = 0; i < entryCount && br.BaseStream.Position + 8 <= box.End; i++)
            {
                track.ChunkOffsets.Add(ReadUInt64BigEndian(br));
            }
        }

        private static void ReadStsz(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart + 4; // version + flags
            uint sampleSize = ReadUInt32BigEndian(br);
            uint sampleCount = ReadUInt32BigEndian(br);
            track.SampleSizes.Clear();

            if (sampleCount > 1_000_000)
            {
                return;
            }

            if (sampleSize > 0)
            {
                for (uint i = 0; i < sampleCount; i++)
                {
                    track.SampleSizes.Add(sampleSize);
                }
                return;
            }

            for (uint i = 0; i < sampleCount && br.BaseStream.Position + 4 <= box.End; i++)
            {
                track.SampleSizes.Add(ReadUInt32BigEndian(br));
            }
        }

        private static void ReadElst(BinaryReader br, Mp4Box box, Mp4TrackInfo track)
        {
            br.BaseStream.Position = box.PayloadStart;
            byte version = br.ReadByte();
            br.BaseStream.Position += 3; // flags
            uint entryCount = ReadUInt32BigEndian(br);
            track.EditList.Clear();

            for (uint i = 0; i < entryCount && br.BaseStream.Position < box.End; i++)
            {
                ulong segmentDuration;
                long mediaTime;
                if (version == 1)
                {
                    if (br.BaseStream.Position + 20 > box.End) break;
                    segmentDuration = ReadUInt64BigEndian(br);
                    mediaTime = ReadInt64BigEndian(br);
                }
                else
                {
                    if (br.BaseStream.Position + 12 > box.End) break;
                    segmentDuration = ReadUInt32BigEndian(br);
                    mediaTime = ReadInt32BigEndian(br);
                }

                short mediaRateInteger = ReadInt16BigEndian(br);
                ushort mediaRateFraction = ReadUInt16BigEndian(br);
                double mediaRate = mediaRateInteger + mediaRateFraction / 65536.0;
                track.EditList.Add(new EditListEntry(segmentDuration, mediaTime, mediaRate));
            }
        }

        private static uint ReadUInt32BigEndian(BinaryReader br)
        {
            byte[] bytes = br.ReadBytes(4);
            if (bytes.Length < 4)
            {
                return 0;
            }
            return ((uint)bytes[0] << 24) |
                   ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) |
                   bytes[3];
        }

        private static int ReadInt32BigEndian(BinaryReader br)
        {
            return unchecked((int)ReadUInt32BigEndian(br));
        }

        private static ulong ReadUInt64BigEndian(BinaryReader br)
        {
            byte[] bytes = br.ReadBytes(8);
            if (bytes.Length < 8)
            {
                return 0;
            }

            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | bytes[i];
            }
            return value;
        }

        private static long ReadInt64BigEndian(BinaryReader br)
        {
            return unchecked((long)ReadUInt64BigEndian(br));
        }

        private static ushort ReadUInt16BigEndian(BinaryReader br)
        {
            byte[] bytes = br.ReadBytes(2);
            if (bytes.Length < 2)
            {
                return 0;
            }

            return (ushort)((bytes[0] << 8) | bytes[1]);
        }

        private static short ReadInt16BigEndian(BinaryReader br)
        {
            return unchecked((short)ReadUInt16BigEndian(br));
        }

        private static uint ReadNalLength(BinaryReader br, int lengthSize)
        {
            uint value = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                byte nextByte;
                try
                {
                    nextByte = br.ReadByte();
                }
                catch (EndOfStreamException)
                {
                    return 0;
                }

                value = (value << 8) | nextByte;
            }

            return value;
        }

        private static uint ReadNalLength(byte[] data, ref int pos, int lengthSize)
        {
            uint value = 0;
            for (int i = 0; i < lengthSize && pos < data.Length; i++)
            {
                value = (value << 8) | data[pos++];
            }

            return value;
        }

        private static List<SeiMetadata> NormalizeTelemetryOffsets(List<SeiMetadata> records, double durationSeconds)
        {
            if (records == null) return new List<SeiMetadata>();

            int nRecords = records.Count;
            if (nRecords == 0) return records;
            if (records.Any(record => record.OffsetSec > 0.0))
            {
                records.Sort((left, right) => left.OffsetSec.CompareTo(right.OffsetSec));
                return records;
            }

            double duration = durationSeconds > 0.0 && !double.IsNaN(durationSeconds) ? durationSeconds : 60.0;
            if (nRecords == 1)
            {
                records[0].OffsetSec = 0.0;
                return records;
            }

            ulong firstSeq = records[0].FrameSeqNo;
            ulong lastSeq = records[nRecords - 1].FrameSeqNo;
            if (lastSeq > firstSeq)
            {
                double frameRate = (lastSeq - firstSeq) / duration;
                if (frameRate > 1.0 && !double.IsNaN(frameRate) && !double.IsInfinity(frameRate))
                {
                    for (int i = 0; i < nRecords; i++)
                    {
                        records[i].OffsetSec = (records[i].FrameSeqNo - firstSeq) / frameRate;
                    }
                    return records;
                }
            }

            for (int i = 0; i < nRecords; i++)
            {
                records[i].OffsetSec = (duration * i) / (nRecords - 1);
            }

            return records;
        }

        private static byte[] ExtractProtoPayload(byte[] nal)
        {
            if (nal.Length < 2) return null;
            for (int i = 2; i < nal.Length - 1; i++)
            {
                if (nal[i] == 0x69)
                {
                    bool hasMarkerPrefix = false;
                    int markerIndex = i - 1;
                    while (markerIndex >= 0 && nal[markerIndex] == 0x42)
                    {
                        hasMarkerPrefix = true;
                        markerIndex--;
                    }

                    if (hasMarkerPrefix)
                    {
                        byte[] raw = new byte[nal.Length - 1 - (i + 1)];
                        Buffer.BlockCopy(nal, i + 1, raw, 0, raw.Length);
                        return StripEmulationPreventionBytes(raw);
                    }
                }
            }
            return null;
        }

        private static byte[] StripEmulationPreventionBytes(byte[] data)
        {
            List<byte> stripped = new List<byte>();
            int zeroCount = 0;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (zeroCount >= 2 && b == 0x03)
                {
                    continue;
                }
                stripped.Add(b);
                if (b == 0) zeroCount++;
                else zeroCount = 0;
            }
            return stripped.ToArray();
        }

        private static ulong ReadVarint(byte[] data, ref int pos)
        {
            ulong val = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                val |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return val;
        }

        private static float ReadFloat(byte[] data, ref int pos)
        {
            if (pos + 4 > data.Length) { pos = data.Length; return 0.0f; }
            float val = BitConverter.ToSingle(data, pos);
            pos += 4;
            return val;
        }

        private static double ReadDouble(byte[] data, ref int pos)
        {
            if (pos + 8 > data.Length) { pos = data.Length; return 0.0; }
            double val = BitConverter.ToDouble(data, pos);
            pos += 8;
            return val;
        }

        private static bool TryDecodeAutopilotTelemetry(byte[] data, out uint autopilotState, out ulong frameSeqNo)
        {
            autopilotState = 0;
            frameSeqNo = 0;
            if (data == null)
            {
                return false;
            }

            int pos = 0;
            while (pos < data.Length)
            {
                ulong key = ReadVarint(data, ref pos);
                uint fieldNum = (uint)(key >> 3);
                uint wireType = (uint)(key & 0x07);

                if (wireType == 0)
                {
                    ulong val = ReadVarint(data, ref pos);
                    if (fieldNum == 3)
                    {
                        frameSeqNo = val;
                    }
                    else if (fieldNum == 10)
                    {
                        autopilotState = (uint)val;
                    }
                }
                else if (wireType == 5)
                {
                    if (pos + 4 > data.Length)
                    {
                        pos = data.Length;
                        break;
                    }

                    pos += 4;
                }
                else if (wireType == 1)
                {
                    if (pos + 8 > data.Length)
                    {
                        pos = data.Length;
                        break;
                    }

                    pos += 8;
                }
                else if (wireType == 2)
                {
                    ulong length = ReadVarint(data, ref pos);
                    if (length > int.MaxValue || pos + (int)length > data.Length)
                    {
                        pos = data.Length;
                        break;
                    }

                    pos += (int)length;
                }
                else
                {
                    break;
                }
            }

            return true;
        }

        private static SeiMetadata DecodeSeiMetadata(byte[] data)
        {
            int pos = 0;
            SeiMetadata meta = new SeiMetadata();
            while (pos < data.Length)
            {
                ulong key = ReadVarint(data, ref pos);
                uint fieldNum = (uint)(key >> 3);
                uint wireType = (uint)(key & 0x07);
                if (wireType == 0) // Varint
                {
                    ulong val = ReadVarint(data, ref pos);
                    if (fieldNum == 1) meta.Version = (uint)val;
                    else if (fieldNum == 2) meta.GearState = (uint)val;
                    else if (fieldNum == 3) meta.FrameSeqNo = val;
                    else if (fieldNum == 7) meta.BlinkerOnLeft = val != 0;
                    else if (fieldNum == 8) meta.BlinkerOnRight = val != 0;
                    else if (fieldNum == 9) meta.BrakeApplied = val != 0;
                    else if (fieldNum == 10) meta.AutopilotState = (uint)val;
                }
                else if (wireType == 5) // Float
                {
                    float val = ReadFloat(data, ref pos);
                    if (fieldNum == 4) meta.VehicleSpeedMps = val;
                    else if (fieldNum == 5) meta.AcceleratorPedalPosition = val;
                    else if (fieldNum == 6) meta.SteeringWheelAngle = val;
                }
                else if (wireType == 1) // Double
                {
                    double val = ReadDouble(data, ref pos);
                    if (fieldNum == 11) meta.LatitudeDeg = val;
                    else if (fieldNum == 12) meta.LongitudeDeg = val;
                    else if (fieldNum == 13) meta.HeadingDeg = val;
                    else if (fieldNum == 14) meta.LinearAccelerationX = val;
                    else if (fieldNum == 15) meta.LinearAccelerationY = val;
                    else if (fieldNum == 16) meta.LinearAccelerationZ = val;
                }
                else if (wireType == 2) // Length-delimited
                {
                    ulong length = ReadVarint(data, ref pos);
                    pos += (int)length;
                }
                else
                {
                    break;
                }
            }
            return meta;
        }
    }
}
