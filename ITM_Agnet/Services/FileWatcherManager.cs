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
        // private bool isDebugMode; // 속성으로 대체
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, DateTime> fileProcessTracker = new Dictionary<string, DateTime>(); // 중복 이벤트 방지용
        private readonly TimeSpan duplicateEventThreshold = TimeSpan.FromSeconds(5); // 중복 이벤트 방지 시간

        private bool isRunning = false;

        // ▼▼▼ 안정화 감지용 멤버 변수 추가 ▼▼▼
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
        // ▲▲▲ 안정화 감지용 멤버 변수 추가 끝 ▲▲▲


        // Debug Mode 상태 속성
        public bool IsDebugMode { get; set; } = false;

        public FileWatcherManager(SettingsManager settingsManager, LogManager logManager, bool isDebugMode)
        {
            this.settingsManager = settingsManager;
            this.logManager = logManager;
            this.IsDebugMode = isDebugMode; // 속성에 할당
        }

        // 외부(ucOptionPanel)에서 Debug 모드 변경 시 호출
        public void UpdateDebugMode(bool isDebug)
        {
            this.IsDebugMode = isDebug;
            logManager.LogEvent($"[FileWatcherManager] Debug mode updated to: {isDebug}");
        }

        public void InitializeWatchers()
        {
            StopWatchers(); // 기존 Watcher 중지 및 정리
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
                    logManager.LogEvent($"[FileWatcherManager] Folder does not exist: {folder}", IsDebugMode); // 속성 사용
                    continue;
                }

                try // Watcher 생성 시 예외 처리 추가
                {
                    // FileSystemWatcher 인스턴스화 수정
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        // NotifyFilter 를 더 상세하게 설정하여 불필요한 이벤트 감소 시도
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };


                    watcher.Created += OnFileChanged;
                    watcher.Changed += OnFileChanged;
                    watcher.Deleted += OnFileChanged; // Deleted 이벤트도 OnFileChanged에서 처리

                    // 오류 이벤트 핸들러 추가 (선택 사항)
                    watcher.Error += OnWatcherError;

                    watchers.Add(watcher);

                    if (IsDebugMode)
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

            InitializeWatchers(); // 시작 시 항상 새로 초기화 (설정 변경 반영)

            if (watchers.Count == 0)
            {
                logManager.LogEvent("[FileWatcherManager] No watchers initialized. Monitoring cannot start.");
                return;
            }

            foreach (var watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = true; // 이벤트 활성화
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[FileWatcherManager] Failed to enable watcher for {watcher.Path}. Error: {ex.Message}");
                }
            }

            isRunning = true; // 상태 업데이트
            logManager.LogEvent("[FileWatcherManager] File monitoring started.");
            if (IsDebugMode)
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
                    w.Deleted -= OnFileChanged;
                    w.Error -= OnWatcherError; // 오류 핸들러 제거
                    w.Dispose();
                }
                catch (Exception ex) // Dispose 중 예외 발생 가능성 처리
                {
                    // logManager.LogWarning -> LogEvent로 수정
                    logManager.LogEvent($"[FileWatcherManager] Warning: Error disposing watcher for {w.Path}: {ex.Message}");
                }
            }
            watchers.Clear(); // 리스트 비우기

            // ▼▼▼ 타이머 중지 및 추적 목록 초기화 추가 ▼▼▼
            lock (trackingLock)
            {
                stabilityCheckTimer?.Dispose();
                stabilityCheckTimer = null;
                trackedFiles.Clear();
            }
            // ▲▲▲ 추가 끝 ▲▲▲

            isRunning = false; // 상태 업데이트
            logManager.LogEvent("[FileWatcherManager] File monitoring stopped.");
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!isRunning)
            {
                if (IsDebugMode)
                    logManager.LogDebug($"[FileWatcherManager] File event ignored (not running): {e.FullPath}");
                return;
            }

            // 중복 이벤트 무시 로직은 그대로 유지
            if (IsDuplicateEvent(e.FullPath))
            {
                if (IsDebugMode)
                    logManager.LogDebug($"[FileWatcherManager] Duplicate event ignored: {e.ChangeType} - {e.FullPath}");
                return;
            }

            // 제외 폴더 처리 로직은 그대로 유지
            var excludeFolders = settingsManager.GetFoldersFromSection("[ExcludeFolders]");
            string changedFolderPath = Path.GetDirectoryName(e.FullPath);

            // 경로 유효성 검사 추가
            if (string.IsNullOrEmpty(changedFolderPath))
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Could not get directory name for: {e.FullPath}");
                return;
            }

            foreach (var excludeFolder in excludeFolders)
            {
                try // Path.GetFullPath 관련 예외 처리
                {
                    string normalizedExclude = Path.GetFullPath(excludeFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string normalizedChanged = Path.GetFullPath(changedFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (normalizedChanged.StartsWith(normalizedExclude, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsDebugMode)
                            logManager.LogDebug($"[FileWatcherManager] File event ignored (in excluded folder): {e.FullPath}");
                        return;
                    }
                }
                catch (Exception pathEx)
                {
                    logManager.LogEvent($"[FileWatcherManager] Warning: Error processing exclude path '{excludeFolder}' or changed path '{changedFolderPath}': {pathEx.Message}");
                    return;
                }
            }

            // 안정화 추적 로직 추가
            try
            {
                // Deleted 이벤트는 즉시 로깅만 하고 추적하지 않음
                if (e.ChangeType == WatcherChangeTypes.Deleted)
                {
                    logManager.LogEvent($"[FileWatcherManager] File Deleted: {e.FullPath}");
                    // 추적 목록에서 제거
                    lock (trackingLock)
                    {
                        trackedFiles.Remove(e.FullPath);
                    }
                    return;
                }

                // Created 또는 Changed 이벤트 처리
                // 파일 존재 여부 및 접근 권한 확인 강화
                if (File.Exists(e.FullPath) && CanReadFile(e.FullPath))
                {
                    lock (trackingLock)
                    {
                        DateTime now = DateTime.UtcNow; // UTC 시간 사용
                        long currentSize = GetFileSizeSafe(e.FullPath);
                        DateTime currentWriteTime = GetLastWriteTimeSafe(e.FullPath); // UTC 시간 사용

                        // 파일 크기가 0이고 Changed 이벤트인 경우 무시 (생성 후 즉시 변경 감지 방지)
                        if (currentSize == 0 && e.ChangeType == WatcherChangeTypes.Changed)
                        {
                            if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] Ignoring zero-byte Changed event: {e.FullPath}");
                            return;
                        }

                        if (!trackedFiles.TryGetValue(e.FullPath, out FileTrackingInfo info))
                        {
                            info = new FileTrackingInfo();
                            trackedFiles[e.FullPath] = info;
                            if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] Start tracking: {e.FullPath}");
                        }

                        // 마지막 이벤트 정보 업데이트
                        info.LastEventTime = now;
                        info.LastSize = currentSize;
                        info.LastWriteTime = currentWriteTime;
                        info.LastChangeType = e.ChangeType;

                        // 안정성 검사 타이머 시작 또는 재시작
                        if (stabilityCheckTimer == null)
                        {
                            stabilityCheckTimer = new Timer(CheckFileStability, null, StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                            if (IsDebugMode) logManager.LogDebug("[FileWatcherManager] Stability check timer started.");
                        }
                        else
                        {
                            stabilityCheckTimer.Change(StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                        }
                    }
                }
                else
                {
                    if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] Ignoring event (file doesn't exist or cannot be read): {e.FullPath}");
                }
            }
            catch (Exception ex)
            {
                logManager.LogEvent($"[FileWatcherManager] Error in OnFileChanged for {e.FullPath}: {ex.Message}");
                if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] OnFileChanged Exception details: {ex.StackTrace}");
            }
        }

        // 파일 읽기 권한 확인 헬퍼
        private bool CanReadFile(string filePath)
        {
            try
            {
                using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return true;
                }
            }
            catch (IOException) // 파일 잠금
            {
                return false;
            }
            catch (UnauthorizedAccessException) // 권한 없음
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: No read permission for file: {filePath}");
                return false;
            }
            catch (Exception ex) // 기타 예외
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Error checking read access for {filePath}: {ex.Message}");
                return false;
            }
        }


        // 안전하게 파일 크기 얻는 헬퍼
        private long GetFileSizeSafe(string filePath)
        {
            try { return File.Exists(filePath) ? new FileInfo(filePath).Length : -1; } // 존재하지 않거나 오류 시 -1 반환
            catch (Exception ex)
            {
                if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] Error getting size for {filePath}: {ex.Message}");
                return -1;
            }
        }

        // 안전하게 마지막 수정 시간(UTC) 얻는 헬퍼
        private DateTime GetLastWriteTimeSafe(string filePath)
        {
            try { return File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue; }
            catch (Exception ex)
            {
                if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] Error getting write time for {filePath}: {ex.Message}");
                return DateTime.MinValue;
            }
        }

        // CheckFileStability 메서드 추가
        private void CheckFileStability(object state)
        {
            // 타이머 콜백 내에서 예외 발생 시 프로그램 중단 방지
            try
            {
                DateTime now = DateTime.UtcNow; // UTC 시간 사용
                var stableFilesToProcess = new List<string>();

                lock (trackingLock)
                {
                    if (!isRunning || trackedFiles.Count == 0) // 서비스 중지 또는 추적 대상 없으면 타이머 중지
                    {
                        stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite); // 즉시 중지
                        if (IsDebugMode && isRunning) logManager.LogDebug("[FileWatcherManager] No files to track. Stability check timer paused.");
                        return;
                    }

                    var currentTrackedFiles = trackedFiles.ToList(); // 반복 중 수정을 위해 복사본 사용

                    foreach (var kvp in currentTrackedFiles)
                    {
                        string filePath = kvp.Key;
                        FileTrackingInfo info = kvp.Value;

                        // 현재 파일 상태 다시 확인
                        long currentSize = GetFileSizeSafe(filePath);
                        DateTime currentWriteTime = GetLastWriteTimeSafe(filePath);

                        // 파일이 삭제되었거나 읽기 실패(-1)하면 추적 중단
                        if (currentSize == -1)
                        {
                            trackedFiles.Remove(filePath);
                            if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] Stop tracking (file not accessible or deleted): {filePath}");
                            continue;
                        }

                        // 크기나 수정 시간이 변경되었으면 아직 불안정 -> 마지막 이벤트 시간 갱신
                        if (currentSize != info.LastSize || currentWriteTime != info.LastWriteTime)
                        {
                            info.LastEventTime = now;
                            info.LastSize = currentSize;
                            info.LastWriteTime = currentWriteTime;
                            if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] File changed, resetting stability timer for: {filePath}");
                            continue;
                        }

                        // 변경 없음: 마지막 이벤트로부터 경과 시간 확인
                        double elapsedSeconds = (now - info.LastEventTime).TotalSeconds;

                        if (elapsedSeconds >= FileStableThresholdSeconds)
                        {
                            // 안정화 임계 시간 경과 -> 처리 대상으로 추가하고 추적 목록에서 제거 시도
                            if (IsFileReady(filePath)) // 최종적으로 파일 접근 가능한지 확인
                            {
                                stableFilesToProcess.Add(filePath);
                                trackedFiles.Remove(filePath);
                                if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] File stable and ready for processing: {filePath}");
                            }
                            else
                            {
                                // 안정화 시간은 지났지만 아직 잠겨있으면 다음 검사로 연기
                                if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] File stable but locked, retrying next check: {filePath}");
                            }
                        }
                    }

                    // 추적 목록 비었으면 타이머 일시 중지
                    if (trackedFiles.Count == 0)
                    {
                        stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite); // 다음 이벤트 발생 전까지 중지
                        if (IsDebugMode) logManager.LogDebug("[FileWatcherManager] Tracking list empty. Stability check timer paused.");
                    }

                } // lock 끝

                // 안정화된 파일들을 순차적으로 처리
                foreach (string stableFilePath in stableFilesToProcess)
                {
                    try
                    {
                        // ProcessFile은 이제 동기 메서드
                        ProcessFile(stableFilePath); // 안정화된 파일 복사 실행
                    }
                    catch (Exception ex)
                    {
                        logManager.LogError($"[FileWatcherManager] Error processing stable file {stableFilePath}: {ex.Message}");
                        if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] ProcessFile Exception details: {ex.StackTrace}");
                    }
                }
            }
            catch (Exception ex) // 타이머 콜백 자체의 예외 처리
            {
                logManager.LogError($"[FileWatcherManager] Unhandled exception in CheckFileStability timer callback: {ex.Message}");
                if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] CheckFileStability Exception details: {ex.StackTrace}");
            }
        }

        // ProcessFile 메서드 시그니처 변경 (async/Task 제거)
        private string ProcessFile(string filePath)
        {
            // 파일 이름 유효성 검사 추가
            string fileName;
            try
            {
                fileName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    logManager.LogEvent($"[FileWatcherManager] Warning: Invalid file path (empty filename): {filePath}");
                    return null;
                }
            }
            catch (ArgumentException ex) // 경로에 잘못된 문자가 있는 경우
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Invalid file path characters: {filePath}. Error: {ex.Message}");
                return null;
            }

            var regexList = settingsManager.GetRegexList();

            foreach (var kvp in regexList)
            {
                try // Regex 처리 중 예외 발생 가능성
                {
                    if (Regex.IsMatch(fileName, kvp.Key))
                    {
                        string destinationFolder = kvp.Value;
                        string destinationFile = Path.Combine(destinationFolder, fileName);

                        try
                        {
                            // 대상 폴더 존재 확인 및 생성 (한 번만 시도)
                            Directory.CreateDirectory(destinationFolder);

                            // 최종 파일 접근 확인 및 복사
                            if (!IsFileReady(filePath))
                            {
                                // 안정화 검사 후에도 잠겨있는 드문 경우
                                logManager.LogEvent($"[FileWatcherManager] File skipped (locked on final check before copy): {fileName}");
                                return null;
                            }

                            // 복사 실행 (덮어쓰기)
                            File.Copy(filePath, destinationFile, true);

                            logManager.LogEvent($"[FileWatcherManager] File Copied (after stabilization): {fileName} -> {destinationFolder}");

                            return destinationFolder; // 성공 시 매칭된 첫 번째 규칙만 처리하고 종료
                        }
                        catch (IOException ioEx) // 복사 중 IO 예외 (디스크 공간 부족, 권한 등)
                        {
                            logManager.LogError($"[FileWatcherManager] IO Error copying file {fileName} to {destinationFolder}: {ioEx.Message}");
                        }
                        catch (UnauthorizedAccessException uaEx) // 권한 예외
                        {
                            logManager.LogError($"[FileWatcherManager] Access Denied copying file {fileName} to {destinationFolder}: {uaEx.Message}");
                        }
                        catch (Exception ex) // 기타 예외
                        {
                            logManager.LogError($"[FileWatcherManager] Error copying file {fileName}: {ex.Message}");
                            if (IsDebugMode) logManager.LogDebug($"[FileWatcherManager] Copy Exception details: {ex.StackTrace}");
                        }
                    }
                }
                catch (RegexMatchTimeoutException rtEx) // 정규식 타임아웃 예외
                {
                    // logManager.LogWarning -> LogEvent로 수정
                    logManager.LogEvent($"[FileWatcherManager] Warning: Regex timeout for pattern '{kvp.Key}' on file '{fileName}': {rtEx.Message}");
                }
                catch (ArgumentException argEx) // 잘못된 정규식 패턴 예외
                {
                    logManager.LogError($"[FileWatcherManager] Invalid Regex pattern '{kvp.Key}': {argEx.Message}");
                }
            }

            // 모든 Regex 규칙에 매칭되지 않은 경우
            if (IsDebugMode)
            {
                logManager.LogDebug($"[FileWatcherManager] No matching regex for file: {fileName}");
            }
            return null;
        }

        private bool IsDuplicateEvent(string filePath)
        {
            DateTime now = DateTime.UtcNow; // UTC 사용

            lock (fileProcessTracker)
            {
                // 오래된 항목 삭제 로직 (선택 사항, 메모리 관리)
                if (fileProcessTracker.Count > 500) // 임계치 설정
                {
                    var keysToRemove = fileProcessTracker
                        .Where(kvp => (now - kvp.Value).TotalMinutes > 1) // 1분 이상 지난 항목
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in keysToRemove)
                    {
                        fileProcessTracker.Remove(key);
                    }
                    if (IsDebugMode && keysToRemove.Count > 0) logManager.LogDebug($"[FileWatcherManager] Cleaned {keysToRemove.Count} old entries from duplicate event tracker.");
                }

                if (fileProcessTracker.TryGetValue(filePath, out var lastProcessed))
                {
                    if ((now - lastProcessed) < duplicateEventThreshold) // TimeSpan 비교
                    {
                        return true; // 중복 이벤트로 간주
                    }
                }

                fileProcessTracker[filePath] = now; // 이벤트 처리 시간 갱신
                return false;
            }
        }

        private bool IsFileReady(string filePath)
        {
            // 파일 존재 여부 먼저 확인
            if (!File.Exists(filePath))
            {
                return false;
            }

            try
            {
                // FileShare.ReadWrite 로 열어 잠금 여부 확인
                using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return true; // 파일 접근 가능
                }
            }
            catch (FileNotFoundException) // 열려고 하는 순간 삭제된 경우
            {
                return false;
            }
            catch (IOException) // 일반적으로 파일 잠김 상태
            {
                return false;
            }
            catch (UnauthorizedAccessException) // 접근 권한 없음
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Access denied while checking if file is ready: {filePath}");
                return false;
            }
            catch (Exception ex) // 기타 예외
            {
                logManager.LogEvent($"[FileWatcherManager] Warning: Unexpected error checking if file is ready ({filePath}): {ex.Message}");
                return false;
            }
        }

        // FileSystemWatcher 오류 처리기
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Exception ex = e.GetException();
            logManager.LogError($"[FileWatcherManager] Watcher error: {ex?.Message ?? "Unknown error"}");
            if (ex != null && IsDebugMode)
            {
                logManager.LogDebug($"[FileWatcherManager] Watcher exception details: {ex.StackTrace}");
            }
        }
    }
}
