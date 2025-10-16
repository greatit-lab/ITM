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
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Npgsql;
using System.Windows.Forms; // MainForm 참조를 위해 추가

namespace ITM_Agent.Services
{
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
        private readonly string PROCESS_NAME;
        private readonly MainForm _mainForm;

        public event Action<bool, DateTime> CollectionCompleted;

        public LampLifeService(SettingsManager settingsManager, LogManager logManager, MainForm mainForm)
        {
            _settingsManager = settingsManager;
            _logManager = logManager;
            _mainForm = mainForm;
            PROCESS_NAME = Environment.Is64BitOperatingSystem ? "Main64" : "Main";
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
                    _collectTimer = new System.Threading.Timer(OnTimerElapsed, null, 0, Timeout.Infinite);
                }
                else
                {
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
                bool success = await ExecuteCollectionAsync();
                CollectionCompleted?.Invoke(success, DateTime.Now);
                _logManager.LogEvent($"[LampLifeService] Collection task finished. Success: {success}");
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] Unhandled exception during collection: {ex.Message}");
                CollectionCompleted?.Invoke(false, DateTime.Now);
            }
        }

        public async Task<bool> ExecuteCollectionAsync()
        {
            var collectedLamps = new List<LampInfo>();

            try
            {
                _mainForm.ShowTemporarilyForAutomation();
                await Task.Delay(500);

                var app = FlaUI.Core.Application.Attach(PROCESS_NAME);
                using (var automation = new UIA3Automation())
                {
                    var mainWindow = app.GetMainWindow(automation);

                    mainWindow.SetForeground();
                    await Task.Delay(500);

                    // 1. 'Processing' 버튼을 먼저 클릭하여 UI 상태를 초기화합니다.
                    var processingButton = mainWindow.FindFirstDescendant(cf => cf.ByName("Processing").And(cf.ByControlType(ControlType.Button)))?.AsButton();
                    if (processingButton == null && Environment.Is64BitOperatingSystem) { processingButton = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("25003"))?.AsButton(); }
                    if (processingButton == null) throw new Exception("UI Automation: 'Processing' 버튼을 찾을 수 없습니다.");
                    processingButton.Click();
                    await Task.Delay(500);

                    // 2. 'System' 버튼을 클릭합니다.
                    var systemButton = mainWindow.FindFirstDescendant(cf => cf.ByName("System").And(cf.ByControlType(ControlType.Button)))?.AsButton();
                    if (systemButton == null && Environment.Is64BitOperatingSystem) { systemButton = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("25004"))?.AsButton(); }
                    if (systemButton == null) throw new Exception("UI Automation: 'System' 버튼을 찾을 수 없습니다.");
                    systemButton.Click();
                    await Task.Delay(500);

                    // 3. 탭들을 포함하는 부모 'TabControl'이 나타날 때까지 기다립니다.
                    var tabControl = FindElementWithRetry(mainWindow, cf => cf.ByControlType(ControlType.Tab));
                    if (tabControl == null) throw new Exception("UI Automation: TabControl을 찾을 수 없습니다. (Timeout)");

                    // 4. TabControl 안에서 'Lamps' 탭을 찾아 클릭합니다. (Status 탭 클릭 과정 생략)
                    var lampsTab = FindElementWithRetry(tabControl, cf => cf.ByName("Lamps").And(cf.ByControlType(ControlType.TabItem)))?.AsTabItem();
                    if (lampsTab == null) throw new Exception("UI Automation: 'Lamps' 탭을 찾을 수 없습니다. (Timeout)");
                    lampsTab.Click(); // .Select() 대신 .Click() 사용
                    await Task.Delay(1000);

                    var lampList = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("10819").And(cf.ByControlType(ControlType.List)))?.AsListBox();
                    if (lampList == null) throw new Exception("UI Automation: 'Lamp Status' 목록(ID:10819)을 찾을 수 없습니다.");
                    var lampItems = lampList.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));

                    if (lampItems != null)
                    {
                        foreach (var item in lampItems)
                        {
                            var cells = item.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
                            if (cells.Length > 4)
                            {
                                var newLamp = new LampInfo { LampId = cells[0].Name, Age = cells[1].Name, LifeSpan = cells[2].Name, LastChanged = cells[4].Name };
                                if (!string.IsNullOrEmpty(newLamp.LampId))
                                    collectedLamps.Add(newLamp);
                            }
                        }
                    }

                    // 5. 작업 완료 후, 다시 'Processing' 버튼을 클릭하여 원래 화면으로 복귀합니다.
                    processingButton = mainWindow.FindFirstDescendant(cf => cf.ByName("Processing").And(cf.ByControlType(ControlType.Button)))?.AsButton();
                    if (processingButton == null && Environment.Is64BitOperatingSystem) { processingButton = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("25003"))?.AsButton(); }
                    if (processingButton == null) throw new Exception("UI Automation: 'Processing' 버튼을 찾을 수 없습니다.");
                    processingButton.Click();
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] UI Automation failed: {ex.Message}");
                return false;
            }
            finally
            {
                _mainForm.HideToTrayAfterAutomation();
            }

            if (collectedLamps.Count > 0)
            {
                string eqpid = _settingsManager.GetEqpid();
                if (string.IsNullOrEmpty(eqpid))
                {
                    _logManager.LogError("[LampLifeService] Eqpid not found. Aborting DB upload.");
                    return false;
                }
                try
                {
                    DataTable lampDataTable = ParseLampInfoToDataTable(collectedLamps, eqpid);
                    UploadToDatabase(lampDataTable);
                    _logManager.LogEvent($"[LampLifeService] SUCCESS - Uploaded {lampDataTable.Rows.Count} lamp records.");
                    return true;
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[LampLifeService] DB upload failed: {ex.Message}");
                    return false;
                }
            }
            else
            {
                _logManager.LogEvent("[LampLifeService] No lamp data collected to upload.");
                return true;
            }
        }

        private AutomationElement FindElementWithRetry(AutomationElement parent, Func<ConditionFactory, ConditionBase> conditionFunc, int timeoutMs = 5000)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            AutomationElement element = null;
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                element = parent.FindFirstDescendant(conditionFunc);
                if (element != null) break;
                Thread.Sleep(200);
            }
            return element;
        }

        private DataTable ParseLampInfoToDataTable(List<LampInfo> lamps, string eqpid)
        {
            var dt = new DataTable();
            dt.Columns.Add("eqpid", typeof(string));
            dt.Columns.Add("ts", typeof(DateTime));
            dt.Columns.Add("lamp_id", typeof(string));
            dt.Columns.Add("age_hour", typeof(int));
            dt.Columns.Add("lifespan_hour", typeof(int));
            dt.Columns.Add("last_changed", typeof(DateTime));
            dt.Columns.Add("serv_ts", typeof(DateTime));

            DateTime now = DateTime.Now;
            DateTime agent_time = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            DateTime server_time_kst = TimeSyncProvider.Instance.ToSynchronizedKst(agent_time);
            DateTime server_time = new DateTime(server_time_kst.Year, server_time_kst.Month, server_time_kst.Day, server_time_kst.Hour, server_time_kst.Minute, server_time_kst.Second);

            foreach (var lamp in lamps)
            {
                DataRow row = dt.NewRow();
                row["eqpid"] = eqpid;
                row["ts"] = agent_time;
                row["serv_ts"] = server_time;
                row["lamp_id"] = lamp.LampId;

                if (int.TryParse(lamp.Age, out int age)) row["age_hour"] = age;
                if (int.TryParse(lamp.LifeSpan, out int lifespan)) row["lifespan_hour"] = lifespan;
                if (DateTime.TryParse(lamp.LastChanged, out DateTime lastChanged)) row["last_changed"] = lastChanged;

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
                using (var tx = conn.BeginTransaction())
                {
                    const string sql = @"
                        INSERT INTO public.eqp_lamp_life 
                            (eqpid, lamp_id, ts, age_hour, lifespan_hour, last_changed, serv_ts)
                        VALUES 
                            (@eqpid, @lamp_id, @ts, @age_hour, @lifespan_hour, @last_changed, @serv_ts)
                        ON CONFLICT (eqpid, lamp_id) DO UPDATE SET
                            ts = EXCLUDED.ts,
                            age_hour = EXCLUDED.age_hour,
                            lifespan_hour = EXCLUDED.lifespan_hour,
                            last_changed = EXCLUDED.last_changed,
                            serv_ts = EXCLUDED.serv_ts;";

                    using (var cmd = new NpgsqlCommand(sql, conn, tx))
                    {
                        cmd.Parameters.Add("@eqpid", NpgsqlTypes.NpgsqlDbType.Varchar);
                        cmd.Parameters.Add("@lamp_id", NpgsqlTypes.NpgsqlDbType.Varchar);
                        cmd.Parameters.Add("@ts", NpgsqlTypes.NpgsqlDbType.Timestamp);
                        cmd.Parameters.Add("@age_hour", NpgsqlTypes.NpgsqlDbType.Integer);
                        cmd.Parameters.Add("@lifespan_hour", NpgsqlTypes.NpgsqlDbType.Integer);
                        cmd.Parameters.Add("@last_changed", NpgsqlTypes.NpgsqlDbType.Timestamp);
                        cmd.Parameters.Add("@serv_ts", NpgsqlTypes.NpgsqlDbType.Timestamp);

                        foreach (DataRow row in dt.Rows)
                        {
                            cmd.Parameters["@eqpid"].Value = row["eqpid"];
                            cmd.Parameters["@lamp_id"].Value = row["lamp_id"];
                            cmd.Parameters["@ts"].Value = row["ts"];
                            cmd.Parameters["@age_hour"].Value = row["age_hour"];
                            cmd.Parameters["@lifespan_hour"].Value = row["lifespan_hour"];
                            cmd.Parameters["@last_changed"].Value = row["last_changed"];
                            cmd.Parameters["@serv_ts"].Value = row["serv_ts"];

                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }
}
