// ITM_Agent/Services/InfoRetentionCleaner.cs
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ITM_Agent.Services
{
    /// <summary>
    /// Baseline, Regex 대상 폴더, PDF 출력 폴더의 파일을
    /// 선택한 보존일수 기준으로 자동 삭제합니다.
    /// 삭제 완료 후 폴더별 총 삭제 파일 수를 로그로 남깁니다.
    /// </summary>
    internal sealed class InfoRetentionCleaner : IDisposable
    {
        private readonly SettingsManager settings;
        private readonly LogManager log;
        private readonly Timer timer;
        private static readonly Regex TsRegex = new Regex(@"^(?<ts>\d{8}_\d{6})_", RegexOptions.Compiled);

        // 파일명에 포함될 수 있는 날짜/시간 패턴
        private static readonly Regex RxYmdHms = new Regex(@"(?<!\d)(?<ymd>\d{8})_(?<hms>\d{6})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxHyphen = new Regex(@"(?<!\d)(?<date>\d{4}-\d{2}-\d{2})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxYmd = new Regex(@"(?<!\d)(?<ymd>\d{8})(?!\d)", RegexOptions.Compiled);

        private const int SCAN_INTERVAL_MS = 6 * 60 * 60 * 1000;    // 6시간(6 * 60분) 간격

        // 삭제 작업 간 지연 시간 (밀리초) 및 배치 크기
        private const int DELETE_DELAY_MS = 50;
        private const int DELETE_BATCH_SIZE = 100;
        private const int BATCH_DELAY_MS = 500;

        public InfoRetentionCleaner(SettingsManager settingsManager)
        {
            settings = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            log = new LogManager(AppDomain.CurrentDomain.BaseDirectory);
            // 타이머 생성 시 즉시 실행(0) 대신 초기 지연(예: 10초) 후 시작하도록 변경 (선택 사항)
            timer = new Timer(_ => Execute(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(SCAN_INTERVAL_MS));
        }

        private void Execute()
        {
            // ▼▼▼ 작업 시작 로그 추가 ▼▼▼
            log.LogEvent("[InfoCleaner] Starting periodic cleanup task...");
            // ▲▲▲ 추가 끝 ▲▲▲

            if (!settings.IsInfoDeletionEnabled)
            {
                // ▼▼▼ 비활성화 시 로그 추가 ▼▼▼
                log.LogEvent("[InfoCleaner] Auto deletion is disabled. Skipping cleanup.");
                // ▲▲▲ 추가 끝 ▲▲▲
                return;
            }
            int days = settings.InfoRetentionDays;
            if (days <= 0)
            {
                // ▼▼▼ 보존 기간 설정 오류 로그 추가 ▼▼▼
                log.LogEvent($"[InfoCleaner] Invalid retention period ({days} days). Skipping cleanup.");
                // ▲▲▲ 추가 끝 ▲▲▲
                return;
            }

            int totalDeletedCount = 0; // 작업 전체에서 삭제된 파일 수 집계

            // --- 1. Baseline 폴더의 .info 파일 정리 ---
            string baseFolder = settings.GetBaseFolder();
            if (!string.IsNullOrEmpty(baseFolder))
            {
                string baselineDir = Path.Combine(baseFolder, "Baseline");
                if (Directory.Exists(baselineDir))
                {
                    int baselineDeletedCount = 0; // Baseline 폴더 삭제 카운트
                    int deletedInBatch = 0;

                    // .info 파일은 이름에 yyyyMMdd_HHmmss 형식이 있으므로 기존 로직 유지 + 카운팅 추가
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(baselineDir, "*.info")) // EnumerateFiles 사용
                        {
                            string name = Path.GetFileName(file);
                            Match m = TsRegex.Match(name);
                            if (!m.Success) continue;

                            if (DateTime.TryParseExact(m.Groups["ts"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ts))
                            {
                                if ((DateTime.Today - ts.Date).TotalDays >= days)
                                {
                                    if (TryDelete(file, name)) // 삭제 성공 시 카운트 증가
                                    {
                                        baselineDeletedCount++;
                                        deletedInBatch++;
                                        Thread.Sleep(DELETE_DELAY_MS); // 개별 지연
                                        if (deletedInBatch >= DELETE_BATCH_SIZE)
                                        {
                                            Thread.Sleep(BATCH_DELAY_MS); // 배치 지연
                                            deletedInBatch = 0;
                                        }
                                    }
                                }
                            }
                        }

                        // ▼▼▼ Baseline 폴더 작업 완료 후 요약 로그 ▼▼▼
                        if (baselineDeletedCount > 0)
                        {
                            log.LogEvent($"[InfoCleaner] Completed cleanup for Baseline folder '{baselineDir}'. Deleted: {baselineDeletedCount} files.");
                            totalDeletedCount += baselineDeletedCount;
                        }
                        else
                        {
                            log.LogDebug($"[InfoCleaner] No files deleted from Baseline folder '{baselineDir}'.");
                        }
                        // ▲▲▲ 추가 끝 ▲▲▲
                    }
                    catch (Exception ex)
                    {
                        log.LogError($"[InfoCleaner] Error cleaning up Baseline folder '{baselineDir}': {ex.Message}");
                    }
                }
                else
                {
                    log.LogDebug($"[InfoCleaner] Baseline folder not found: {baselineDir}");
                }
            }
            else
            {
                log.LogDebug("[InfoCleaner] BaseFolder not set. Skipping Baseline cleanup.");
            }


            // --- 2. Regex 대상 폴더 전체 파일 정리 ---
            var regexFolders = settings.GetRegexList().Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); // ToList 추가
            if (regexFolders.Any())
            {
                log.LogEvent($"[InfoCleaner] Starting cleanup for {regexFolders.Count} Regex target folder(s).");
                foreach (string folder in regexFolders)
                {
                    if (Directory.Exists(folder))
                    {
                        int deletedCount = CleanFolderRecursively(folder, days); // 삭제된 파일 수 반환 받음
                        // ▼▼▼ Regex 폴더 작업 완료 후 요약 로그 ▼▼▼
                        if (deletedCount > 0)
                        {
                            log.LogEvent($"[InfoCleaner] Completed cleanup for Regex target folder '{folder}'. Deleted: {deletedCount} files.");
                            totalDeletedCount += deletedCount;
                        }
                        else
                        {
                            log.LogDebug($"[InfoCleaner] No files deleted from Regex target folder '{folder}'.");
                        }
                        // ▲▲▲ 추가 끝 ▲▲▲
                    }
                    else
                    {
                        log.LogEvent($"[InfoCleaner] Regex target folder not found: {folder}");
                    }
                }
            }
            else
            {
                log.LogDebug("[InfoCleaner] No Regex target folders configured. Skipping Regex folders cleanup.");
            }


            // --- 3. (신규 기능) PDF 병합 파일 저장 폴더 정리 ---
            string pdfSaveFolder = settings.GetValueFromSection("ImageTrans", "SaveFolder");
            if (!string.IsNullOrEmpty(pdfSaveFolder) && Directory.Exists(pdfSaveFolder))
            {
                log.LogEvent($"[InfoCleaner] Starting cleanup for PDF Save folder: {pdfSaveFolder}");
                int deletedCount = CleanFolderRecursively(pdfSaveFolder, days); // 삭제된 파일 수 반환 받음
                // ▼▼▼ PDF 폴더 작업 완료 후 요약 로그 ▼▼▼
                if (deletedCount > 0)
                {
                    log.LogEvent($"[InfoCleaner] Completed cleanup for PDF Save folder '{pdfSaveFolder}'. Deleted: {deletedCount} files.");
                    totalDeletedCount += deletedCount;
                }
                else
                {
                    log.LogDebug($"[InfoCleaner] No files deleted from PDF Save folder '{pdfSaveFolder}'.");
                }
                // ▲▲▲ 추가 끝 ▲▲▲
            }
            else
            {
                log.LogDebug("[InfoCleaner] PDF Save folder not set or not found. Skipping PDF folder cleanup.");
            }

            // --- 4. 최종 작업 완료 로그 ---
            log.LogEvent($"[InfoCleaner] Periodic cleanup task finished. Total files deleted in this run: {totalDeletedCount}.");

        }

        /// <summary>
        /// 지정된 폴더와 모든 하위 폴더를 스캔하여 오래된 파일을 삭제하고, 삭제된 파일 수를 반환합니다.
        /// </summary>
        /// <returns>삭제된 파일의 총 개수</returns>
        private int CleanFolderRecursively(string rootDir, int days) // 반환 타입 int로 변경
        {
            DateTime today = DateTime.Today;
            int totalDeletedCount = 0; // 해당 폴더 트리에서 삭제된 총 파일 수
            int deletedCountInBatch = 0; // 배치 카운터

            try
            {
                // EnumerateFiles를 사용하여 메모리 효율적으로 처리
                foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    DateTime? fileDate = TryExtractDateFromFileName(name);
                    if (fileDate.HasValue && (today - fileDate.Value.Date).TotalDays >= days)
                    {
                        if (TryDelete(file, name)) // 삭제 성공 시에만 카운트 및 지연
                        {
                            totalDeletedCount++; // 총 카운트 증가
                            deletedCountInBatch++;

                            // 속도 조절 로직
                            Thread.Sleep(DELETE_DELAY_MS);

                            if (deletedCountInBatch >= DELETE_BATCH_SIZE)
                            {
                                Thread.Sleep(BATCH_DELAY_MS);
                                deletedCountInBatch = 0;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) // 중단 예외 처리 (만약 CancellationToken 사용 시)
            {
                log.LogEvent($"[InfoCleaner] Cleanup operation cancelled for folder {rootDir}.");
            }
            catch (UnauthorizedAccessException uaEx) // 접근 권한 문제 로깅
            {
                log.LogError($"[InfoCleaner] Access denied while scanning folder {rootDir}. Error: {uaEx.Message}");
            }
            catch (Exception ex)
            {
                log.LogError($"[InfoCleaner] Failed to scan folder {rootDir}. Error: {ex.Message}");
            }
            return totalDeletedCount; // 삭제된 총 파일 수 반환
        }

        /// <summary>
        /// 파일명에서 다양한 형식의 날짜를 추출합니다. (시간이 있어도 '날짜' 부분만 반환)
        /// </summary>
        private static DateTime? TryExtractDateFromFileName(string fileName)
        {
            // 1) yyyyMMdd_HHmmss 형식
            var m1 = RxYmdHms.Match(fileName);
            if (m1.Success && DateTime.TryParseExact(m1.Groups["ymd"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d1))
                return d1.Date;

            // 2) yyyy-MM-dd 형식
            var m2 = RxHyphen.Match(fileName);
            if (m2.Success && DateTime.TryParseExact(m2.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d2))
                return d2.Date;

            // 3) yyyyMMdd 형식
            var m3 = RxYmd.Match(fileName);
            if (m3.Success && DateTime.TryParseExact(m3.Groups["ymd"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d3))
                return d3.Date;

            return null;
        }

        /// <summary>
        /// 최종 결과에 따라 로그를 기록하고 삭제 성공 여부를 반환하는 파일 삭제 로직
        /// </summary>
        /// <returns>삭제 성공 시 true, 실패 시 false</returns>
        private bool TryDelete(string filePath, string displayName)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    // ▼▼▼ 개별 파일 로그는 Debug 레벨로 변경 ▼▼▼
                    log.LogDebug($"[InfoCleaner] Skip delete (already removed): {displayName}");
                    // ▲▲▲ 변경 끝 ▲▲▲
                    return true;
                }

                File.Delete(filePath);
                // ▼▼▼ 개별 성공 로그 제거 ▼▼▼
                // log.LogEvent($"[InfoCleaner] Deleted: {displayName}");
                log.LogDebug($"[InfoCleaner] Deleted: {displayName}"); // Debug 로그로 변경 (선택 사항)
                // ▲▲▲ 제거 끝 ▲▲▲
                return true;
            }
            catch (FileNotFoundException)
            {
                // ▼▼▼ 개별 파일 로그는 Debug 레벨로 변경 ▼▼▼
                log.LogDebug($"[InfoCleaner] File disappeared during delete attempt (considered successful): {displayName}");
                // ▲▲▲ 변경 끝 ▲▲▲
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    var attrs = File.GetAttributes(filePath);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(filePath);
                    // ▼▼▼ 개별 성공 로그 제거 ▼▼▼
                    // log.LogEvent($"[InfoCleaner] Deleted (after attribute change): {displayName}");
                    log.LogDebug($"[InfoCleaner] Deleted (after attribute change): {displayName}"); // Debug 로그로 변경 (선택 사항)
                    // ▲▲▲ 제거 끝 ▲▲▲
                    return true;
                }
                catch (FileNotFoundException)
                {
                    // ▼▼▼ 개별 파일 로그는 Debug 레벨로 변경 ▼▼▼
                    log.LogDebug($"[InfoCleaner] File disappeared during second delete attempt (considered successful): {displayName}");
                    // ▲▲▲ 변경 끝 ▲▲▲
                    return true;
                }
                catch (Exception ex2)
                {
                    log.LogError($"[InfoCleaner] Delete failed finally for {displayName} -> {ex2.Message}");
                    return false;
                }
            }
            catch (IOException ioEx) // IO 예외 (파일 잠김 등)
            {
                log.LogError($"[InfoCleaner] Delete failed (IO Exception) for {displayName} -> {ioEx.Message}");
                return false;
            }
            catch (Exception ex) // 기타 예외
            {
                log.LogError($"[InfoCleaner] Delete failed {displayName} -> {ex.Message}");
                return false;
            }
        }

        public void Dispose() => timer?.Dispose();
    }
}
