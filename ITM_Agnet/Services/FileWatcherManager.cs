// ITM_Agent/Services/FileWatcherManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading; // Timer 사용을 위해 추가
using System.Threading.Tasks; // Task 사용 (필요한 경우)

namespace ITM_Agent.Services
{
    public class FileWatcherManager
    {
        private SettingsManager settingsManager;
        private LogManager logManager;
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, DateTime> fileProcessTracker = new Dictionary<string, DateTime>(); // 중복 이벤트 방지용
        private readonly TimeSpan duplicateEventThreshold = TimeSpan.FromSeconds(5); // 중복 이벤트 방지 시간

        private bool isRunning = false;

        // 안정화 감지용 멤버 변수
        private readonly Dictionary<string, FileTrackingInfo> trackedFiles = new Dictionary<string, FileTrackingInfo>(StringComparer.OrdinalIgnoreCase);
        private System.Threading.Timer stabilityCheckTimer;
        private readonly object trackingLock = new object();
        private const int StabilityCheckIntervalMs = 1000; // 1초 간격으로 안정성 검사
        private const double FileStableThresholdSeconds = 5.0; // 마지막 변경 후 5초 동안 변화 없으면 안정화 간주

        // 안정화 감지용 내부 클래스
        private class FileTrackingInfo
        {
            public DateTime LastEventTime { get; set; }
            public long LastSize { get; set; }
            public DateTime LastWriteTime { get; set; } // UTC 시간으로 저장 권장
            public WatcherChangeTypes LastChangeType { get; set; }
        }

        // 버퍼 오버플로 복구 작업 동시 실행 방지용 잠금 객체
        private readonly object recoveryLock = new object();


        // Debug Mode 상태 속성 (LogManager.GlobalDebugEnabled 사용으로 대체)
        // public bool IsDebugMode { get; set; } = false;

        public FileWatcherManager(SettingsManager settingsManager, LogManager logManager, bool isDebugMode)
        {
            this.settingsManager = settingsManager;
            this.logManager = logManager;
            LogManager.GlobalDebugEnabled = isDebugMode; // 전역 플래그 설정
        }

        // 외부(ucOptionPanel)에서 Debug 모드 변경 시 호출
        public void UpdateDebugMode(bool isDebug)
        {
            LogManager.GlobalDebugEnabled = isDebug; // 전역 플래그 업데이트
            logManager.LogEvent($"[FileWatcherManager] Debug mode updated to: {isDebug}");
        }

        public void InitializeWatchers()
        {
            StopWatchers();
            var targetFolders = settingsManager.GetFoldersFromSection("[TargetFolders]");
            if (targetFolders.Count == 0)
            {
                logManager.LogEvent("[FileWatcherManager] No target folders configured for monitoring.");
                return;
            }

            foreach (var folder in targetFolders)
            {
                if (!Directory.Exists(folder))
                {
                    logManager.LogEvent($"[FileWatcherManager] Folder does not exist: {folder}", LogManager.GlobalDebugEnabled); // 전역 플래그 사용
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 131072 // 128KB
                    };

                    watcher.Created += OnFileChanged;
                    watcher.Changed += OnFileChanged;
                    // Deleted 이벤트 핸들러 연결 제거됨
                    watcher.Error += OnWatcherError;

                    watchers.Add(watcher);

                    if (LogManager.GlobalDebugEnabled) // 전역 플래그 사용
                    {
                        logManager.LogDebug($"[FileWatcherManager] Initialized watcher for folder: {folder}");
                    }
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[FileWatcherManager] Failed to create watcher for {folder}. Error: {ex.Message}");
                }
            }

            logManager.LogEvent($"[FileWatcherManager] {watchers.Count} watcher(s) initialized.");
        }

        public void StartWatching()
        {
            if (isRunning)
            {
                logManager.LogEvent("[FileWatcherManager] File monitoring is already running.");
                return;
            }

            InitializeWatchers();

            if (watchers.Count == 0)
            {
                logManager.LogEvent("[FileWatcherManager] No watchers initialized. Monitoring cannot start.");
                return;
            }

            foreach (var watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[FileWatcherManager] Failed to enable watcher for {watcher.Path}. Error: {ex.Message}");
                }
            }

            isRunning = true;
            logManager.LogEvent("[FileWatcherManager] File monitoring started.");
            if (LogManager.GlobalDebugEnabled) // 전역 플래그 사용
            {
                logManager.LogDebug(
                    $"[FileWatcherManager] Monitoring {watchers.Count} folder(s): " +
                    $"{string.Join(", ", watchers.Select(w => w.Path))}"
                );
            }
        }

        public void StopWatchers()
        {
            foreach (var w in watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Created -= OnFileChanged;
                    w.Changed -= OnFileChanged;
                    w.Error -= OnWatcherError;
                    w.Dispose();
                }
                catch (Exception ex)
                {
                    logManager.LogEvent($"[FileWatcherManager] Warning: Error disposing watcher for {w.Path}: {ex.Message}");
                }
            }
            watchers.Clear();

            lock (trackingLock)
            {
                stabilityCheckTimer?.Dispose();
                stabilityCheckTimer = null;
                trackedFiles.Clear();
            }

            isRunning = false;
            logManager.LogEvent("[FileWatcherManager] File monitoring stopped.");
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // ▼▼▼ 진입 확인용 Debug 로그 추가 ▼▼▼
            if (LogManager.GlobalDebugEnabled)
            {
                // 이벤트 종류와 전체 경로를 기록
                logManager.LogDebug($"[FileWatcherManager] OnFileChanged ENTERED: Type={e.ChangeType}, Path={e.FullPath}");
            }
            // ▲▲▲ 추가 끝 ▲▲▲

            // 1. 에이전트 실행 상태 확인
            if (!isRunning)
            {
                if (LogManager.GlobalDebugEnabled) // 전역 플래그 사용
                    logManager.LogDebug($"[FileWatcherManager] File event ignored (not running): {e.FullPath}");
                return;
            }

            // 2. 제외 폴더 확인 (최우선)
            var excludeFolders = settingsManager.GetFoldersFromSection("[ExcludeFolders]");
            string changedFolderPath = null;
            try
            {
                changedFolderPath = Path.GetDirectoryName(e.FullPath);
                if (string.IsNullOrEmpty(changedFolderPath))
                {
                    logManager.LogEvent($"[FileWatcherManager] Warning: Could not get directory name for: {e.FullPath}. Skipping event.");
                    return;
                }
            }
            catch (Exception pathEx)
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Error getting directory for '{e.FullPath}': {pathEx.Message}. Skipping event.");
                return;
            }

            foreach (var excludeFolder in excludeFolders)
            {
                try
                {
                    string normalizedExclude = Path.GetFullPath(excludeFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string normalizedChanged = Path.GetFullPath(changedFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    // 제외 폴더 내 파일이면 아무 로그도 남기지 않고 즉시 종료
                    if (normalizedChanged.StartsWith(normalizedExclude, StringComparison.OrdinalIgnoreCase))
                    {
                        return; // 여기서 바로 종료 (로그 없음)
                    }
                }
                catch (Exception pathEx)
                {
                    logManager.LogEvent($"[FileWatcherManager] Warning: Error processing exclude path '{excludeFolder}' or changed path '{changedFolderPath}': {pathEx.Message}. Skipping event.");
                    return;
                }
            }

            // 3. 중복 이벤트 확인 (제외 폴더가 아닌 경우만 실행)
            if (IsDuplicateEvent(e.FullPath))
            {
                if (LogManager.GlobalDebugEnabled) // 전역 플래그 사용
                    logManager.LogDebug($"[FileWatcherManager] Duplicate event ignored: {e.ChangeType} - {e.FullPath}");
                return;
            }

            // --- 이하 안정화 추적 로직 ---
            try
            {
                if (File.Exists(e.FullPath) && CanReadFile(e.FullPath))
                {
                    lock (trackingLock)
                    {
                        DateTime now = DateTime.UtcNow;
                        long currentSize = GetFileSizeSafe(e.FullPath);
                        DateTime currentWriteTime = GetLastWriteTimeSafe(e.FullPath);

                        if (currentSize == 0 && e.ChangeType == WatcherChangeTypes.Changed)
                        {
                            if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Ignoring zero-byte Changed event: {e.FullPath}");
                            return;
                        }

                        if (!trackedFiles.TryGetValue(e.FullPath, out FileTrackingInfo info))
                        {
                            info = new FileTrackingInfo();
                            trackedFiles[e.FullPath] = info;
                            if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Start tracking: {e.FullPath}");
                        }

                        info.LastEventTime = now;
                        info.LastSize = currentSize;
                        info.LastWriteTime = currentWriteTime;
                        info.LastChangeType = e.ChangeType;

                        if (stabilityCheckTimer == null)
                        {
                            stabilityCheckTimer = new Timer(CheckFileStability, null, StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                            if (LogManager.GlobalDebugEnabled) logManager.LogDebug("[FileWatcherManager] Stability check timer started.");
                        }
                        else
                        {
                            stabilityCheckTimer.Change(StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                        }
                    }
                }
                else
                {
                    lock (trackingLock)
                    {
                        if (trackedFiles.ContainsKey(e.FullPath))
                        {
                            trackedFiles.Remove(e.FullPath);
                            if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Stop tracking (file doesn't exist or cannot be read): {e.FullPath}");
                        }
                    }
                    if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Ignoring event (file doesn't exist or cannot be read): {e.FullPath}");
                }
            }
            catch (Exception ex)
            {
                logManager.LogEvent($"[FileWatcherManager] Error in OnFileChanged for {e.FullPath}: {ex.Message}");
                if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] OnFileChanged Exception details: {ex.StackTrace}");
            }
        }

        // 파일 읽기 권한 확인 헬퍼
        private bool CanReadFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return true;
                }
            }
            catch (FileNotFoundException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException)
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: No read permission for file: {filePath}");
                return false;
            }
            catch (Exception ex)
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Error checking read access for {filePath}: {ex.Message}");
                return false;
            }
        }


        // 안전하게 파일 크기 얻는 헬퍼
        private long GetFileSizeSafe(string filePath)
        {
            try { return File.Exists(filePath) ? new FileInfo(filePath).Length : -1; }
            catch (FileNotFoundException) { return -1; }
            catch (Exception ex)
            {
                if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Error getting size for {filePath}: {ex.Message}");
                return -1;
            }
        }

        // 안전하게 마지막 수정 시간(UTC) 얻는 헬퍼
        private DateTime GetLastWriteTimeSafe(string filePath)
        {
            try { return File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue; }
            catch (FileNotFoundException) { return DateTime.MinValue; }
            catch (Exception ex)
            {
                if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Error getting write time for {filePath}: {ex.Message}");
                return DateTime.MinValue;
            }
        }

        // CheckFileStability 메서드
        private void CheckFileStability(object state)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                var stableFilesToProcess = new List<string>();

                lock (trackingLock)
                {
                    if (!isRunning || trackedFiles.Count == 0)
                    {
                        stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        if (LogManager.GlobalDebugEnabled && isRunning) logManager.LogDebug("[FileWatcherManager] No files to track. Stability check timer paused.");
                        return;
                    }

                    var currentTrackedFiles = trackedFiles.ToList();

                    foreach (var kvp in currentTrackedFiles)
                    {
                        string filePath = kvp.Key;
                        FileTrackingInfo info = kvp.Value;

                        long currentSize = GetFileSizeSafe(filePath);
                        DateTime currentWriteTime = GetLastWriteTimeSafe(filePath);

                        if (currentSize == -1 || currentWriteTime == DateTime.MinValue)
                        {
                            trackedFiles.Remove(filePath);
                            if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Stop tracking (file not accessible or deleted during stability check): {filePath}");
                            continue;
                        }

                        if (currentSize != info.LastSize || currentWriteTime != info.LastWriteTime)
                        {
                            info.LastEventTime = now;
                            info.LastSize = currentSize;
                            info.LastWriteTime = currentWriteTime;
                            if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] File changed, resetting stability timer for: {filePath}");
                            continue;
                        }

                        double elapsedSeconds = (now - info.LastEventTime).TotalSeconds;

                        if (elapsedSeconds >= FileStableThresholdSeconds)
                        {
                            if (IsFileReady(filePath))
                            {
                                stableFilesToProcess.Add(filePath);
                                trackedFiles.Remove(filePath);
                                if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] File stable and ready for processing: {filePath}");
                            }
                            else
                            {
                                if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] File stable but locked, retrying next check: {filePath}");
                            }
                        }
                    }

                    if (trackedFiles.Count == 0)
                    {
                        stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        if (LogManager.GlobalDebugEnabled) logManager.LogDebug("[FileWatcherManager] Tracking list empty. Stability check timer paused.");
                    }
                    else
                    {
                        stabilityCheckTimer?.Change(StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                    }
                } // lock 끝

                foreach (string stableFilePath in stableFilesToProcess)
                {
                    try { ProcessFile(stableFilePath); }
                    catch (Exception ex)
                    {
                        logManager.LogError($"[FileWatcherManager] Error processing stable file {stableFilePath}: {ex.Message}");
                        if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] ProcessFile Exception details: {ex.StackTrace}");
                    }
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[FileWatcherManager] Unhandled exception in CheckFileStability timer callback: {ex.Message}");
                if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] CheckFileStability Exception details: {ex.StackTrace}");
                try { stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            }
        }


        // ProcessFile 메서드
        private string ProcessFile(string filePath)
        {
            string fileName;
            try { fileName = Path.GetFileName(filePath); if (string.IsNullOrEmpty(fileName)) { logManager.LogEvent($"[FileWatcherManager] Warning: Invalid file path (empty filename): {filePath}"); return null; } } catch (ArgumentException ex) { logManager.LogEvent($"[FileWatcherManager] Warning: Invalid file path characters: {filePath}. Error: {ex.Message}"); return null; }

            var regexList = settingsManager.GetRegexList();

            foreach (var kvp in regexList)
            {
                try
                {
                    if (Regex.IsMatch(fileName, kvp.Key))
                    {
                        string destinationFolder = kvp.Value;
                        string destinationFile = Path.Combine(destinationFolder, fileName);
                        try
                        {
                            Directory.CreateDirectory(destinationFolder);
                            if (!IsFileReady(filePath)) { logManager.LogEvent($"[FileWatcherManager] File skipped (locked on final check before copy): {fileName}"); return null; }
                            File.Copy(filePath, destinationFile, true);
                            logManager.LogEvent($"[FileWatcherManager] File Copied (after stabilization): {fileName} -> {destinationFolder}");
                            return destinationFolder;
                        }
                        catch (FileNotFoundException) { logManager.LogEvent($"[FileWatcherManager] Copy skipped: Source file not found: {fileName}"); return null; }
                        catch (IOException ioEx) { logManager.LogError($"[FileWatcherManager] IO Error copying file {fileName} to {destinationFolder}: {ioEx.Message}"); }
                        catch (UnauthorizedAccessException uaEx) { logManager.LogError($"[FileWatcherManager] Access Denied copying file {fileName} to {destinationFolder}: {uaEx.Message}"); }
                        catch (Exception ex) { logManager.LogError($"[FileWatcherManager] Error copying file {fileName}: {ex.Message}"); if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] Copy Exception details: {ex.StackTrace}"); }
                        return null; // 복사 실패 시 null 반환
                    }
                }
                catch (RegexMatchTimeoutException rtEx) { logManager.LogEvent($"[FileWatcherManager] Warning: Regex timeout for pattern '{kvp.Key}' on file '{fileName}': {rtEx.Message}"); }
                catch (ArgumentException argEx) { logManager.LogError($"[FileWatcherManager] Invalid Regex pattern '{kvp.Key}': {argEx.Message}"); }
            }

            if (LogManager.GlobalDebugEnabled)
            {
                logManager.LogDebug($"[FileWatcherManager] No matching regex for file: {fileName}");
            }
            return null;
        }


        // IsDuplicateEvent 메서드
        private bool IsDuplicateEvent(string filePath)
        {
            DateTime now = DateTime.UtcNow;
            lock (fileProcessTracker)
            {
                if (fileProcessTracker.Count > 1000)
                {
                    var keysToRemove = fileProcessTracker
                        .Where(kvp => (now - kvp.Value).TotalMinutes > 5)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    foreach (var key in keysToRemove) fileProcessTracker.Remove(key);
                    if (LogManager.GlobalDebugEnabled && keysToRemove.Count > 0) logManager.LogDebug($"[FileWatcherManager] Cleaned {keysToRemove.Count} old entries from duplicate event tracker.");
                }
                if (fileProcessTracker.TryGetValue(filePath, out var lastProcessed))
                {
                    if ((now - lastProcessed) < duplicateEventThreshold) return true;
                }
                fileProcessTracker[filePath] = now;
                return false;
            }
        }


        // IsFileReady 메서드
        private bool IsFileReady(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            try
            {
                using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return true;
                }
            }
            catch (FileNotFoundException) { return false; }
            catch (IOException ioEx)
            {
                if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[FileWatcherManager] IsFileReady IO Exception for {filePath}: {ioEx.Message}");
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Access denied while checking if file is ready: {filePath}");
                return false;
            }
            catch (Exception ex)
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Unexpected error checking if file is ready ({filePath}): {ex.Message}");
                return false;
            }
        }


        // OnWatcherError 메서드 (버퍼 오버플로 복구 로직 포함)
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Exception ex = e.GetException();
            string errorMessage = ex?.Message ?? "Unknown watcher error";
            logManager.LogError($"[FileWatcherManager] Watcher error: {errorMessage}");
            if (ex != null && LogManager.GlobalDebugEnabled)
            {
                logManager.LogDebug($"[FileWatcherManager] Watcher exception details: {ex.StackTrace}");
            }

            if (ex is InternalBufferOverflowException bufferEx)
            {
                FileSystemWatcher watcher = sender as FileSystemWatcher;
                if (watcher != null)
                {
                    if (Monitor.TryEnter(recoveryLock))
                    {
                        try
                        {
                            logManager.LogEvent($"[FileWatcherManager] Buffer overflow detected for watcher on '{watcher.Path}'. Initiating recovery process...");
                            watcher.EnableRaisingEvents = false;
                            logManager.LogEvent($"[FileWatcherManager] Watcher temporarily disabled for '{watcher.Path}'.");
                            Thread.Sleep(10000);
                            logManager.LogEvent($"[FileWatcherManager] Manually scanning folder '{watcher.Path}' for missed changes...");
                            ManuallyScanAndProcessFolder(watcher.Path);
                            logManager.LogEvent($"[FileWatcherManager] Manual scan completed for '{watcher.Path}'.");
                            try
                            {
                                if (watchers.Contains(watcher))
                                {
                                    watcher.EnableRaisingEvents = true;
                                    logManager.LogEvent($"[FileWatcherManager] Watcher re-enabled for '{watcher.Path}'.");
                                }
                                else
                                {
                                    logManager.LogEvent($"[FileWatcherManager] Watcher for '{watcher.Path}' removed during recovery. Skipping re-enable.");
                                }
                            }
                            catch (ObjectDisposedException) { logManager.LogEvent($"[FileWatcherManager] Watcher for '{watcher.Path}' was disposed. Cannot re-enable."); }
                            catch (Exception restartEx) { logManager.LogError($"[FileWatcherManager] Failed to re-enable watcher for '{watcher.Path}'. Error: {restartEx.Message}"); }
                        }
                        catch (Exception recoveryEx)
                        {
                            logManager.LogError($"[FileWatcherManager] Error during buffer overflow recovery for '{watcher.Path}': {recoveryEx.Message}");
                            try { if (watchers.Contains(watcher) && !watcher.EnableRaisingEvents) { watcher.EnableRaisingEvents = true; logManager.LogEvent($"[FileWatcherManager] Attempting to re-enable watcher for '{watcher.Path}' after recovery failure."); } } catch { /* Ignore */ }
                        }
                        finally { Monitor.Exit(recoveryLock); }
                    }
                    else { logManager.LogEvent($"[FileWatcherManager] Recovery process already in progress for watcher on '{watcher.Path}'. Skipping."); }
                }
                else { logManager.LogError("[FileWatcherManager] Buffer overflow occurred, but could not identify the watcher instance."); }
            }
        }


        // ManuallyScanAndProcessFolder 메서드
        private void ManuallyScanAndProcessFolder(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    logManager.LogEvent($"[FileWatcherManager] Manual scan skipped: Folder '{folderPath}' no longer exists.");
                    return;
                }

                var excludeFolders = settingsManager.GetFoldersFromSection("[ExcludeFolders]")
                    .Select(p =>
                    {
                        try
                        {
                            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        }
                        catch
                        {
                            logManager.LogEvent($"[FileWatcherManager] Manual scan: Invalid exclude path '{p}'.");
                            return null;
                        }
                    })
                    .Where(p => p != null)
                    .ToList();

                logManager.LogEvent($"[FileWatcherManager] Starting manual scan for: {folderPath}");

                int scannedFileCount = 0;
                int processedFileCount = 0;

                try
                {
                    Directory.EnumerateDirectories(folderPath, "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    logManager.LogError($"[FileWatcherManager] Manual scan: Access denied for top-level folder '{folderPath}'. Cannot scan. Error: {uaEx.Message}");
                    return;
                }

                foreach (string filePath in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories))
                {
                    scannedFileCount++;

                    try
                    {
                        string currentFileDir = Path.GetDirectoryName(filePath);
                        if (string.IsNullOrEmpty(currentFileDir))
                            continue;

                        string normalizedCurrentDir = Path.GetFullPath(currentFileDir)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        bool isExcluded = false;
                        foreach (string excludePath in excludeFolders)
                        {
                            if (normalizedCurrentDir.StartsWith(excludePath, StringComparison.OrdinalIgnoreCase))
                            {
                                isExcluded = true;
                                break;
                            }
                        }

                        if (isExcluded)
                            continue;
                    }
                    catch (PathTooLongException ptle)
                    {
                        logManager.LogEvent($"[FileWatcherManager] Manual scan: Path too long '{filePath}'. Skipping. Error: {ptle.Message}");
                        continue;
                    }
                    catch (Exception pathEx)
                    {
                        logManager.LogEvent($"[FileWatcherManager] Manual scan: Error processing path for '{filePath}': {pathEx.Message}. Skipping.");
                        continue;
                    }

                    bool recentlyProcessed = false;
                    lock (fileProcessTracker)
                    {
                        if (fileProcessTracker.TryGetValue(filePath, out var lastProcessed) &&
                            (DateTime.UtcNow - lastProcessed).TotalMinutes < 5)
                        {
                            recentlyProcessed = true;
                        }
                    }

                    bool currentlyTracked = false;
                    lock (trackingLock)
                    {
                        currentlyTracked = trackedFiles.ContainsKey(filePath);
                    }

                    if (recentlyProcessed || currentlyTracked)
                        continue; // 로그 제거됨

                    if (IsFileReady(filePath))
                    {
                        try
                        {
                            string result = ProcessFile(filePath);
                            if (result != null)
                            {
                                processedFileCount++;
                                lock (fileProcessTracker)
                                {
                                    fileProcessTracker[filePath] = DateTime.UtcNow;
                                }
                            }
                        }
                        catch (Exception processEx)
                        {
                            logManager.LogError($"[FileWatcherManager] Manual scan: Error processing file '{filePath}': {processEx.Message}");
                        }
                    }
                    // 잠긴 파일 로그 제거됨
                }

                logManager.LogEvent($"[FileWatcherManager] Manual scan finished for: {folderPath}. Scanned: {scannedFileCount}, Processed: {processedFileCount}.");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                logManager.LogError($"[FileWatcherManager] Manual scan: Access denied during scan of '{folderPath}'. Error: {uaEx.Message}");
            }
            catch (Exception ex)
            {
                logManager.LogError($"[FileWatcherManager] Error during manual scan of folder '{folderPath}': {ex.Message}");
            }
        }
    } // Class End
} // Namespace End
