// ITM_Agnet/Services/LampLifeService.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ConnectInfo;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Npgsql;

namespace ITM_Agent.Services
{
    // Collector.cs에서 가져온 데이터 구조체
    public struct LampInfo
    {
        public string LampId { get; set; }
        public string Age { get; set; }
        public string LifeSpan { get; set; }
        public string LastChanged { get; set; }
    }

    public class LampLifeService
    {
        private readonly SettingsManager _settingsManager;
        private readonly LogManager _logManager;
        private System.Threading.Timer _collectTimer;
        private bool _isRunning = false;
        private readonly object _lock = new object();

        private const string PROCESS_NAME = "Main64";

        public LampLifeService(SettingsManager settingsManager, LogManager logManager)
        {
            _settingsManager = settingsManager;
            _logManager = logManager;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning || !_settingsManager.IsLampLifeCollectorEnabled)
                {
                    return;
                }

                _logManager.LogEvent("[LampLifeService] Starting...");
                int intervalMinutes = _settingsManager.LampLifeCollectorInterval;
                if (intervalMinutes <= 0)
                {
                    _logManager.LogEvent("[LampLifeService] Interval is zero or less. Service will run once and stop.");
                    // 한 번만 실행되도록 타이머 설정
                    _collectTimer = new System.Threading.Timer(OnTimerElapsed, null, 0, Timeout.Infinite);
                }
                else
                {
                    // 주기적으로 실행
                    _collectTimer = new System.Threading.Timer(OnTimerElapsed, null, 0, intervalMinutes * 60 * 1000);
                }
                _isRunning = true;
                _logManager.LogEvent($"[LampLifeService] Started with {intervalMinutes} min interval.");
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                {
                    return;
                }
                _logManager.LogEvent("[LampLifeService] Stopping...");
                _collectTimer?.Dispose();
                _collectTimer = null;
                _isRunning = false;
                _logManager.LogEvent("[LampLifeService] Stopped.");
            }
        }

        private async void OnTimerElapsed(object state)
        {
            try
            {
                _logManager.LogEvent("[LampLifeService] Collection task started.");
                await ExecuteCollectionAsync();
                _logManager.LogEvent("[LampLifeService] Collection task finished.");
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] Unhandled exception during collection: {ex.Message}");
            }
        }

        public async Task ExecuteCollectionAsync()
        {
            var collectedLamps = new List<LampInfo>();

            try
            {
                // UI 자동화 로직 (기존 Collector.cs)
                var app = Application.Attach(PROCESS_NAME);
                using (var automation = new UIA3Automation())
                {
                    var mainWindow = app.GetMainWindow(automation);

                    var systemButton = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("25004"))?.AsButton();
                    systemButton?.Click();
                    await Task.Delay(500);

                    var tabControl = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("1111"))?.AsTab();
                    var lampsTab = tabControl?.FindFirstDescendant(cf => cf.ByName("Lamps").And(cf.ByControlType(ControlType.TabItem)));
                    lampsTab?.Click();
                    await Task.Delay(500);

                    var lampList = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("10819").And(cf.ByControlType(ControlType.List)));
                    var lampItems = lampList?.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));

                    if (lampItems != null)
                    {
                        foreach (var item in lampItems)
                        {
                            var cells = item.FindAllChildren();
                            var newLamp = new LampInfo
                            {
                                LampId = item.Name,
                                Age = cells.FirstOrDefault(c => c.AutomationId == "ListViewSubItem-1")?.Name,
                                LifeSpan = cells.FirstOrDefault(c => c.AutomationId == "ListViewSubItem-2")?.Name,
                                LastChanged = cells.FirstOrDefault(c => c.AutomationId == "ListViewSubItem-4")?.Name
                            };

                            if (!string.IsNullOrEmpty(newLamp.LampId))
                                collectedLamps.Add(newLamp);
                        }
                    }

                    var processingButton = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("25003"))?.AsButton();
                    processingButton?.Click();
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] UI Automation failed: {ex.Message}");
                return; // 데이터 수집 실패 시 DB 업로드 중단
            }

            // DB 업로드 로직 (기존 IOnto_LampLT.cs)
            if (collectedLamps.Count > 0)
            {
                string eqpid = _settingsManager.GetEqpid();
                if (string.IsNullOrEmpty(eqpid))
                {
                    _logManager.LogError("[LampLifeService] Eqpid not found. Aborting DB upload.");
                    return;
                }

                try
                {
                    DataTable lampDataTable = ParseLampInfoToDataTable(collectedLamps, eqpid);
                    UploadToDatabase(lampDataTable);
                    _logManager.LogEvent($"[LampLifeService] SUCCESS - Uploaded {lampDataTable.Rows.Count} lamp records.");
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[LampLifeService] DB upload failed: {ex.Message}");
                }
            }
            else
            {
                _logManager.LogEvent("[LampLifeService] No lamp data collected to upload.");
            }
        }

        private DataTable ParseLampInfoToDataTable(List<LampInfo> lamps, string eqpid)
        {
            var dt = new DataTable();
            dt.Columns.Add("eqpid", typeof(string));
            dt.Columns.Add("collect_time", typeof(DateTime));
            dt.Columns.Add("lamp_id", typeof(string));
            dt.Columns.Add("age_hour", typeof(int));
            dt.Columns.Add("lifespan_hour", typeof(int));
            dt.Columns.Add("last_changed", typeof(DateTime));
            dt.Columns.Add("serv_ts", typeof(DateTime));

            DateTime collectTime = DateTime.Now;

            foreach (var lamp in lamps)
            {
                DataRow row = dt.NewRow();
                row["eqpid"] = eqpid;
                row["collect_time"] = collectTime;
                row["lamp_id"] = lamp.LampId;

                if (int.TryParse(lamp.Age, out int age))
                    row["age_hour"] = age;

                if (int.TryParse(lamp.LifeSpan, out int lifespan))
                    row["lifespan_hour"] = lifespan;

                if (DateTime.TryParse(lamp.LastChanged, out DateTime lastChanged))
                    row["last_changed"] = lastChanged;

                DateTime serv_kst = TimeSyncProvider.Instance.ToSynchronizedKst(collectTime);
                row["serv_ts"] = new DateTime(serv_kst.Year, serv_kst.Month, serv_kst.Day, serv_kst.Hour, serv_kst.Minute, serv_kst.Second);

                dt.Rows.Add(row);
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
                        writer.Write(row["eqpid"], NpgsqlTypes.NpgsqlDbType.Varchar);
                        writer.Write((DateTime)row["collect_time"], NpgsqlTypes.NpgsqlDbType.Timestamp);
                        writer.Write(row["lamp_id"], NpgsqlTypes.NpgsqlDbType.Varchar);
                        writer.Write((int)row["age_hour"], NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write((int)row["lifespan_hour"], NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write((DateTime)row["last_changed"], NpgsqlTypes.NpgsqlDbType.Timestamp);
                        writer.Write((DateTime)row["serv_ts"], NpgsqlTypes.NpgsqlDbType.Timestamp);
                    }
                    writer.Complete();
                }
            }
        }
    }
}
