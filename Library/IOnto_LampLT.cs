// Library/IOnto_LampLT.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ConnectInfo;
using ITM_Agent.Services;
using Npgsql;

// 1. 네임스페이스를 Onto_LampLTLib 으로 변경
namespace Onto_LampLTLib
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
                    // 2. 로그 출처를 [LampLT] 로 변경
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [LampLT] {msg}{Environment.NewLine}";
                    File.AppendAllText(Path.Combine(_logDir, $"{DateTime.Now:yyyyMMdd}_{suffix}.log"), line, Encoding.UTF8);
                }
                catch { /* 로깅 실패는 무시 */ }
            }
        }
        public static void Event(string msg) => Write("event", msg);
        public static void Error(string msg) => Write("error", msg);
        public static void Debug(string msg) { if (_debugEnabled) Write("debug", msg); }
    }

    /* ─────────────────── 인터페이스 및 클래스 ─────────────────── */
    // 3. 인터페이스 이름 변경
    public interface IOnto_LampLT
    {
        string PluginName { get; }
        void ProcessAndUpload(string filePath, object settingsPathObj = null, object arg2 = null);
    }

    // 4. 클래스 이름 및 구현 인터페이스 이름 변경
    public class Onto_LampLT : IOnto_LampLT
    {
        // 5. 플러그인 이름 변경
        public string PluginName => "Onto_LampLT";

        public void ProcessAndUpload(string filePath, object settingsPathObj = null, object arg2 = null)
        {
            SimpleLogger.Event($"ProcessAndUpload ▶ {Path.GetFileName(filePath)}");

            if (!WaitForFileReady(filePath, 10, 500))
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
                string fileContent = File.ReadAllText(filePath);
                DataTable lampDataTable = ParseLogFile(fileContent, eqpid);

                if (lampDataTable != null && lampDataTable.Rows.Count > 0)
                {
                    UploadToDatabase(lampDataTable);
                    SimpleLogger.Event($"SUCCESS - Uploaded {lampDataTable.Rows.Count} records from {Path.GetFileName(filePath)}.");
                }
                else
                {
                    SimpleLogger.Event($"No data to upload from {Path.GetFileName(filePath)}.");
                }

                TryDeleteLocalFile(filePath);
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"Unhandled EX in ProcessAndUpload for {filePath} ▶ {ex.GetBaseException().Message}");
            }
        }

        private DataTable ParseLogFile(string content, string eqpid)
        {
            var dt = new DataTable();
            dt.Columns.Add("eqpid", typeof(string));
            dt.Columns.Add("collect_time", typeof(DateTime));
            dt.Columns.Add("lamp_id", typeof(string));
            dt.Columns.Add("age_hour", typeof(int));
            dt.Columns.Add("lifespan_hour", typeof(int));
            dt.Columns.Add("last_changed", typeof(DateTime));
            dt.Columns.Add("serv_ts", typeof(DateTime));

            DateTime collectTime = DateTime.MinValue;
            var dateTimeMatch = Regex.Match(content, @"^DateTime:(.*)", RegexOptions.Multiline);
            if (dateTimeMatch.Success)
            {
                DateTime.TryParse(dateTimeMatch.Groups[1].Value.Trim(), out collectTime);
            }
            if (collectTime == DateTime.MinValue) return null;

            string[] lampSections = Regex.Split(content, @"\[Lamp\]");

            foreach (string section in lampSections)
            {
                if (string.IsNullOrWhiteSpace(section)) continue;

                var lampIdMatch = Regex.Match(section, @"Lamp_ID:(.*)");
                var ageHourMatch = Regex.Match(section, @"Age_Hour:(.*)");
                var lifeSpanMatch = Regex.Match(section, @"LifeSpan_Hour:(.*)");
                var lastChangedMatch = Regex.Match(section, @"Last_Changed:(.*)");

                if (lampIdMatch.Success && ageHourMatch.Success && lifeSpanMatch.Success && lastChangedMatch.Success)
                {
                    DataRow row = dt.NewRow();
                    row["eqpid"] = eqpid;
                    row["collect_time"] = collectTime;
                    row["lamp_id"] = lampIdMatch.Groups[1].Value.Trim();

                    if (int.TryParse(ageHourMatch.Groups[1].Value.Trim(), out int age))
                        row["age_hour"] = age;

                    if (int.TryParse(lifeSpanMatch.Groups[1].Value.Trim(), out int lifespan))
                        row["lifespan_hour"] = lifespan;

                    if (DateTime.TryParse(lastChangedMatch.Groups[1].Value.Trim(), out DateTime lastChanged))
                        row["last_changed"] = lastChanged;

                    DateTime serv_kst = TimeSyncProvider.Instance.ToSynchronizedKst(collectTime);
                    row["serv_ts"] = new DateTime(serv_kst.Year, serv_kst.Month, serv_kst.Day, serv_kst.Hour, serv_kst.Minute, serv_kst.Second);

                    dt.Rows.Add(row);
                }
            }
            return dt;
        }

        private void UploadToDatabase(DataTable dt)
        {
            var dbInfo = DatabaseInfo.CreateDefault();
            using (var conn = new NpgsqlConnection(dbInfo.GetConnectionString()))
            {
                conn.Open();
                using (var writer = conn.BeginBinaryImport("COPY public.plg_lamp_life (eqpid, collect_time, lamp_id, age_hour, lifespan_hour, last_changed, serv_ts) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (DataRow row in dt.Rows)
                    {
                        writer.StartRow();
                        writer.Write(row["eqpid"]);
                        writer.Write((DateTime)row["collect_time"], NpgsqlTypes.NpgsqlDbType.Timestamp);
                        writer.Write(row["lamp_id"]);
                        writer.Write((int)row["age_hour"], NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write((int)row["lifespan_hour"], NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write((DateTime)row["last_changed"], NpgsqlTypes.NpgsqlDbType.Timestamp);
                        writer.Write((DateTime)row["serv_ts"], NpgsqlTypes.NpgsqlDbType.Timestamp);
                    }
                    writer.Complete();
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
                catch (IOException) { Thread.Sleep(delayMs); }
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
