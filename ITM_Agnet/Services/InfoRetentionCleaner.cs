// ITM_Agent\Services\InfoRetentionCleaner.cs
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

        //private const int SCAN_INTERVAL_MS = 60 * 60 * 1000;    // 60분 간격
        private const int SCAN_INTERVAL_MS = 10 * 60 * 1000;

        public InfoRetentionCleaner(SettingsManager settingsManager)
        {
            settings = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            log = new LogManager(AppDomain.CurrentDomain.BaseDirectory);
            timer = new Timer(_ => Execute(), null, 0, SCAN_INTERVAL_MS);
        }

        private void Execute()
        {
            if (!settings.IsInfoDeletionEnabled) return;
            int days = settings.InfoRetentionDays;
            if (days <= 0) return;

            // --- 1. Baseline 폴더의 .info 파일 정리 ---
            string baseFolder = settings.GetBaseFolder();
            if (!string.IsNullOrEmpty(baseFolder))
            {
                string baselineDir = Path.Combine(baseFolder, "Baseline");
                if (Directory.Exists(baselineDir))
                {
                    // .info 파일은 이름에 yyyyMMdd_HHmmss 형식이 있으므로 기존 로직 유지
                    foreach (string file in Directory.GetFiles(baselineDir, "*.info"))
                    {
                        string name = Path.GetFileName(file);
                        Match m = TsRegex.Match(name);
                        if (!m.Success) continue;

                        if (DateTime.TryParseExact(m.Groups["ts"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ts))
                        {
                            if ((DateTime.Today - ts.Date).TotalDays >= days)
                            {
                                TryDelete(file, name);
                            }
                        }
                    }
                }
            }

            // --- 2. Regex 대상 폴더 전체 파일 정리 ---
            var regexFolders = settings.GetRegexList().Values.Distinct(StringComparer.OrdinalIgnoreCase);
            if (regexFolders.Any())
            {
                log.LogEvent($"[InfoCleaner] Starting cleanup for {regexFolders.Count()} Regex folders.");
                foreach (string folder in regexFolders)
                {
                    if (Directory.Exists(folder))
                    {
                        CleanFolderRecursively(folder, days);
                    }
                }
            }

            // --- 3. (신규 기능) PDF 병합 파일 저장 폴더 정리 ---
            string pdfSaveFolder = settings.GetValueFromSection("ImageTrans", "SaveFolder");
            if (!string.IsNullOrEmpty(pdfSaveFolder) && Directory.Exists(pdfSaveFolder))
            {
                log.LogEvent($"[InfoCleaner] Starting cleanup for PDF Save folder: {pdfSaveFolder}");
                CleanFolderRecursively(pdfSaveFolder, days);
            }
        }

        /// <summary>
        /// 지정된 폴더와 모든 하위 폴더를 스캔하여 오래된 파일을 삭제합니다.
        /// </summary>
        private void CleanFolderRecursively(string rootDir, int days)
        {
            DateTime today = DateTime.Today;
            try
            {
                // PDF 파일도 대상으로 포함하여 모든 파일을 검사
                foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    DateTime? fileDate = TryExtractDateFromFileName(name);
                    if (fileDate.HasValue && (today - fileDate.Value.Date).TotalDays >= days)
                    {
                        TryDelete(file, name);
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError($"[InfoCleaner] Failed to scan folder {rootDir}. Error: {ex.Message}");
            }
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
        /// 최종 결과에 따라 로그를 기록하는 파일 삭제 로직
        /// </summary>
        private void TryDelete(string filePath, string displayName)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    log.LogEvent($"[InfoCleaner] Skip (already removed): {displayName}");
                    return;
                }

                // 1차 삭제 시도
                File.Delete(filePath);
                log.LogEvent($"[InfoCleaner] Deleted: {displayName}");
            }
            catch (UnauthorizedAccessException)
            {
                // 1차 실패 시, 읽기 전용 속성 제거 후 2차 시도
                try
                {
                    var attrs = File.GetAttributes(filePath);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(filePath);
                    log.LogEvent($"[InfoCleaner] Deleted (after attribute change): {displayName}");
                }
                catch (Exception ex2)
                {
                    // 2차 시도도 실패하면 최종 에러 로그 기록
                    log.LogError($"[InfoCleaner] Delete failed finally for {displayName} -> {ex2.Message}");
                }
            }
            catch (Exception ex)
            {
                // 기타 예외 발생 시 에러 로그 기록
                log.LogError($"[InfoCleaner] Delete failed {displayName} -> {ex.Message}");
            }
        }

        public void Dispose() => timer?.Dispose();
    }
}
