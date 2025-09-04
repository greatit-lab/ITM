// Library/IOnto_WaferMapImage.cs
using System;
using System.Data;
using System.IO;
using System.Net; // FtpWebRequest 사용
using System.Text;
using System.Threading;
using ConnectInfo; // FtpsInfo, DatabaseInfo 참조
using ITM_Agent.Services; // TimeSyncProvider 참조
using Npgsql;

// 1. 네임스페이스 변경
namespace Onto_WaferMapImageLib
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
                    // 6. 로그 메시지 출처 변경
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WaferMapImage] {msg}{Environment.NewLine}";
                    File.AppendAllText(Path.Combine(_logDir, $"{DateTime.Now:yyyyMMdd}_{suffix}.log"), line, Encoding.UTF8);
                }
                catch { /* 로깅 실패는 무시 */ }
            }
        }
        public static void Event(string msg) => Write("event", msg);
        public static void Error(string msg) => Write("error", msg);
        public static void Debug(string msg) { if (_debugEnabled) Write("debug", msg); }
    }

    /* ─────────────────── 2. 인터페이스명 변경 ─────────────────── */
    public interface IOnto_WaferMapImage
    {
        string PluginName { get; }
        void ProcessAndUpload(string filePath, object settingsPathObj = null, object arg2 = null);
    }

    /* ─────────────────── 3. 클래스명 변경 ─────────────────── */
    public class Onto_WaferMapImage : IOnto_WaferMapImage
    {
        // 4. 플러그인 이름 변경
        public string PluginName => "Onto_WaferMapImage";

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

            string fileUri = null;

            try
            {
                string sdwt = GetSdwtFromDatabase(eqpid);
                if (string.IsNullOrEmpty(sdwt))
                {
                    return;
                }

                fileUri = UploadToFtps(filePath, sdwt, eqpid);
                if (string.IsNullOrEmpty(fileUri))
                {
                    return;
                }

                InsertToDatabase(filePath, eqpid, fileUri);

                // 5. 모든 작업 성공 시 로컬 원본 파일 삭제
                TryDeleteLocalFile(filePath);

                SimpleLogger.Event($"SUCCESS - DB record created and local file deleted for {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"Unhandled EX in ProcessAndUpload for {filePath} ▶ {ex.GetBaseException().Message}");
                if (fileUri != null)
                {
                    SimpleLogger.Error($"CRITICAL: File was uploaded to FTPS ({fileUri}) but DB insert or local file deletion failed. Manual check required.");
                }
            }
        }

        private string GetSdwtFromDatabase(string eqpid)
        {
            try
            {
                var dbInfo = DatabaseInfo.CreateDefault();
                using (var conn = new NpgsqlConnection(dbInfo.GetConnectionString()))
                {
                    conn.Open();
                    const string sql = "SELECT sdwt FROM public.ref_equipment WHERE eqpid = @eqpid LIMIT 1;";
                    using (var cmd = new NpgsqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@eqpid", eqpid);
                        object result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            SimpleLogger.Debug($"Found sdwt '{result}' for eqpid '{eqpid}'.");
                            return result.ToString();
                        }
                        else
                        {
                            SimpleLogger.Error($"SDWT not found in ref_equipment for eqpid: {eqpid}");
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"Failed to get SDWT from DB for eqpid {eqpid}. EX: {ex.Message}");
                return null;
            }
        }

        private string UploadToFtps(string localFilePath, string sdwt, string eqpid)
        {
            var ftpsInfo = FtpsInfo.CreateDefault();
            string fileName = Path.GetFileName(localFilePath);
            string dateFolder = DateTime.Now.ToString("yyyyMMdd");

            string remoteDirectory = $"{sdwt}/{eqpid}/{dateFolder}";

            try
            {
                EnsureRemoteDirectoryExists(ftpsInfo, sdwt);
                EnsureRemoteDirectoryExists(ftpsInfo, $"{sdwt}/{eqpid}");
                EnsureRemoteDirectoryExists(ftpsInfo, remoteDirectory);

                string remoteFileUri = $"ftp://{ftpsInfo.Host}:{ftpsInfo.Port}/{remoteDirectory}/{fileName}";

                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(remoteFileUri);
                request.Method = WebRequestMethods.Ftp.UploadFile;
                request.Credentials = new NetworkCredential(ftpsInfo.Username, ftpsInfo.Password);
                request.EnableSsl = true;

                using (var fileStream = File.OpenRead(localFilePath))
                using (var requestStream = request.GetRequestStream())
                {
                    fileStream.CopyTo(requestStream);
                }

                using (var response = (FtpWebResponse)request.GetResponse())
                {
                    SimpleLogger.Event($"FTPS upload complete: {fileName} to {remoteDirectory}, Status: {response.StatusCode}");
                }

                return $"ftps://{ftpsInfo.Host}/{remoteDirectory}/{fileName}";
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"FTPS upload failed for {fileName}. EX: {ex.Message}");
                return null;
            }
        }

        private void EnsureRemoteDirectoryExists(FtpsInfo ftpsInfo, string directoryPath)
        {
            try
            {
                string directoryUri = $"ftp://{ftpsInfo.Host}:{ftpsInfo.Port}/{directoryPath}";
                var request = (FtpWebRequest)WebRequest.Create(directoryUri);
                request.Method = WebRequestMethods.Ftp.MakeDirectory;
                request.Credentials = new NetworkCredential(ftpsInfo.Username, ftpsInfo.Password);
                request.EnableSsl = true;

                using (request.GetResponse()) { }
            }
            catch (WebException ex)
            {
                var response = (FtpWebResponse)ex.Response;
                if (response.StatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                {
                    SimpleLogger.Error($"Failed to create directory {directoryPath}. Status: {response.StatusCode}, EX: {ex.Message}");
                    throw;
                }
            }
        }

        private void InsertToDatabase(string localFilePath, string eqpid, string fileUri)
        {
            string fileName = Path.GetFileName(localFilePath);
            DateTime fileDateTime = ExtractDateTimeFromFileName(fileName);

            var dbInfo = DatabaseInfo.CreateDefault();
            using (var conn = new NpgsqlConnection(dbInfo.GetConnectionString()))
            {
                conn.Open();

                const string sql = @"
                    INSERT INTO public.plg_wf_map 
                        (eqpid, datetime, file_uri, original_filename, serv_ts)
                    VALUES 
                        (@eqpid, @datetime, @file_uri, @original_filename, @serv_ts)
                    ON CONFLICT (eqpid, datetime, original_filename) DO NOTHING;";

                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    DateTime serv_kst = TimeSyncProvider.Instance.ToSynchronizedKst(fileDateTime);

                    cmd.Parameters.AddWithValue("@eqpid", eqpid);
                    cmd.Parameters.AddWithValue("@datetime", fileDateTime);
                    cmd.Parameters.AddWithValue("@file_uri", fileUri);
                    cmd.Parameters.AddWithValue("@original_filename", fileName);
                    cmd.Parameters.AddWithValue("@serv_ts", serv_kst);

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
