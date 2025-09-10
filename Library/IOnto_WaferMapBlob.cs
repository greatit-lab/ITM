// Library/IOnto_WaferMapBlob.cs
using System;
using System.Data;
using System.IO;
using System.Text;
using ConnectInfo;
using ITM_Agent.Services;
using Npgsql;
using NpgsqlTypes; // NpgsqlDbType.Bytea를 위해 추가

namespace Onto_WaferMapBlobLib
{
    /* ──────────────────────── Logger ──────────────────────── */
    internal static class SimpleLogger
    {
        private static volatile bool _debugEnabled = false;
        public static void SetDebugMode(bool enable) => _debugEnabled = enable;
        private static readonly object _sync = new object();
        private static readonly string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private static void Write(string suffix, string msg)
        {
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(_logDir);
                    // 로그 메시지 출처 변경
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WaferMapBlob] {msg}{Environment.NewLine}";
                    File.AppendAllText(Path.Combine(_logDir, $"{DateTime.Now:yyyyMMdd}_{suffix}.log"), line, Encoding.UTF8);
                }
                catch { /* 로깅 실패는 무시 */ }
            }
        }
        public static void Event(string msg) => Write("event", msg);
        public static void Error(string msg) => Write("error", msg);
        public static void Debug(string msg) { if (_debugEnabled) Write("debug", msg); }
    }

    /* ─────────────────── 인터페이스명 변경 ─────────────────── */
    public interface IOnto_WaferMapBlob
    {
        string PluginName { get; }
        void ProcessAndUpload(string filePath, object settingsPathObj = null, object arg2 = null);
    }

    /* ─────────────────── 클래스명 변경 ─────────────────── */
    public class Onto_WaferMapBlob : IOnto_WaferMapBlob
    {
        // 플러그인 이름 변경
        public string PluginName => "Onto_WaferMapBlob";

        public void ProcessAndUpload(string filePath, object settingsPathObj = null, object arg2 = null)
        {
            SimpleLogger.Event($"ProcessAndUpload ▶ {Path.GetFileName(filePath)}");
            if (!WaitForFileReady(filePath))
            {
                SimpleLogger.Error($"SKIP – File is locked or does not exist: {filePath}");
                return;
            }

            string eqpid = GetEqpidFromSettings(settingsPathObj as string ?? "Settings.ini");
            if (string.IsNullOrEmpty(eqpid))
            {
                SimpleLogger.Error("Eqpid not found in Settings.ini. Aborting process.");
                return;
            }

            try
            {
                // 1. 파일을 byte 배열로 읽기
                byte[] blobData = File.ReadAllBytes(filePath);

                // 2. 데이터베이스에 직접 적재
                InsertToDatabase(filePath, eqpid, blobData);

                // 3. 모든 작업 성공 시 로컬 원본 파일 삭제
                TryDeleteLocalFile(filePath);

                SimpleLogger.Event($"SUCCESS - DB record created and local file deleted for {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"Unhandled EX in ProcessAndUpload for {filePath} ▶ {ex.GetBaseException().Message}");
            }
        }

        private void InsertToDatabase(string localFilePath, string eqpid, byte[] blobData)
        {
            string fileName = Path.GetFileName(localFilePath);
            DateTime fileDateTime = ExtractDateTimeFromFileName(fileName);

            var dbInfo = DatabaseInfo.CreateDefault();
            using (var conn = new NpgsqlConnection(dbInfo.GetConnectionString()))
            {
                conn.Open();

                // 테이블명과 컬럼명을 blob에 맞게 수정
                const string sql = @"
                    INSERT INTO public.plg_wf_map_blob
                        (eqpid, datetime, blob_data, original_filename, serv_ts)
                    VALUES
                        (@eqpid, @datetime, @blob_data, @original_filename, @serv_ts)
                    ON CONFLICT (eqpid, datetime, original_filename) DO NOTHING;";

                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    DateTime serv_kst = TimeSyncProvider.Instance.ToSynchronizedKst(fileDateTime);
                    serv_kst = new DateTime(serv_kst.Year, serv_kst.Month, serv_kst.Day, serv_kst.Hour, serv_kst.Minute, serv_kst.Second);

                    cmd.Parameters.AddWithValue("@eqpid", eqpid);
                    cmd.Parameters.AddWithValue("@datetime", fileDateTime);
                    cmd.Parameters.AddWithValue("@original_filename", fileName);
                    cmd.Parameters.AddWithValue("@serv_ts", serv_kst);
                    // bytea 타입 파라미터 추가
                    cmd.Parameters.Add(new NpgsqlParameter("@blob_data", NpgsqlDbType.Bytea)).Value = blobData;

                    cmd.ExecuteNonQuery();
                }
            }
        }

        #region Helper Methods

        private void TryDeleteLocalFile(string filePath)
        {
            try
            {
                File.Delete(filePath);
                SimpleLogger.Debug($"Local file deleted: {filePath}");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"Failed to delete local file {filePath}: {ex.Message}");
            }
        }

        private DateTime ExtractDateTimeFromFileName(string fileName)
        {
            try
            {
                string[] parts = fileName.Split('_');
                if (parts.Length >= 2)
                {
                    return DateTime.ParseExact($"{parts[0]}{parts[1]}", "yyyyMMddHHmmss", null);
                }
            }
            catch { /* 파싱 실패 시 현재 시간으로 대체 */ }
            return DateTime.Now;
        }

        private bool WaitForFileReady(string path, int maxRetries = 10, int delayMs = 500)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                if (!File.Exists(path)) return false;
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        return true;
                    }
                }
                catch (IOException) { System.Threading.Thread.Sleep(delayMs); }
            }
            return false;
        }

        private string GetEqpidFromSettings(string iniPath)
        {
            if (!File.Exists(iniPath)) return "";
            foreach (var line in File.ReadAllLines(iniPath))
            {
                if (line.Trim().StartsWith("Eqpid", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = line.IndexOf('=');
                    if (idx > 0) return line.Substring(idx + 1).Trim();
                }
            }
            return "";
        }
        #endregion
    }
}
