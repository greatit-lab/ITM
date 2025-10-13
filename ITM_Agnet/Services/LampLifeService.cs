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
        private readonly string PROCESS_NAME;

        public LampLifeService(SettingsManager settingsManager, LogManager logManager)
        {
            _settingsManager = settingsManager;
            _logManager = logManager;
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
                var app = Application.Attach(PROCESS_NAME);
                using (var automation = new UIA3Automation())
                {
                    var mainWindow = app.GetMainWindow(automation);

                    // 1. "System" 버튼 찾기 및 클릭
                    var systemButton = mainWindow.FindFirstDescendant(cf => cf.ByName("System").And(cf.ByControlType(ControlType.Button)))?.AsButton();
                    if (systemButton == null && Environment.Is64BitOperatingSystem)
                    {
                        systemButton = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("25004"))?.AsButton();
                    }
                    if (systemButton == null) throw new Exception("UI Automation: 'System' 버튼을 찾을 수 없습니다.");
                    systemButton.Click();
                    await Task.Delay(1000);

                    // 2. "Lamps" 탭 아이템 찾기
                    var lampsTab = mainWindow.FindFirstDescendant(cf => cf.ByName("Lamps").And(cf.ByControlType(ControlType.TabItem)))?.AsTabItem();
                    if (lampsTab == null) throw new Exception("UI Automation: 'Lamps' 탭을 찾을 수 없습니다.");

                    // 3. .Select() 메서드로 탭 선택
                    lampsTab.Select();
                    await Task.Delay(1000);

                    // ▼▼▼ [핵심 수정] AutomationId "10819"를 사용하여 Lamp Status 목록을 직접 찾기 ▼▼▼
                    var lampList = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("10819").And(cf.ByControlType(ControlType.List)))?.AsListBox();
                    if (lampList == null) throw new Exception("UI Automation: 'Lamp Status' 목록(ID:10819)을 찾을 수 없습니다.");

                    var lampItems = lampList.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));

                    if (lampItems != null)
                    {
                        foreach (var item in lampItems)
                        {
                            var cells = item.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));

                            // 5개의 컬럼이 모두 있는지 확인하는 로직으로 복원
                            if (cells.Length > 4)
                            {
                                var newLamp = new LampInfo
                                {
                                    LampId = cells[0].Name,
                                    Age = cells[1].Name,
                                    LifeSpan = cells[2].Name,
                                    LastChanged = cells[4].Name
                                };

                                if (!string.IsNullOrEmpty(newLamp.LampId))
                                    collectedLamps.Add(newLamp);
                            }
                        }
                    }

                    // 5. 원래 화면으로 돌아가기 위해 "Processing" 버튼 클릭
                    var processingButton = mainWindow.FindFirstDescendant(cf => cf.ByName("Processing").And(cf.ByControlType(ControlType.Button)))?.AsButton();
                    if (processingButton == null && Environment.Is64BitOperatingSystem)
                    {
                        processingButton = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("25003"))?.AsButton();
                    }
                    if (processingButton == null) throw new Exception("UI Automation: 'Processing' 버튼을 찾을 수 없습니다.");
                    processingButton.Click();
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] UI Automation failed: {ex.Message}");
                return;
            }

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
            dt.Columns.Add("ts", typeof(DateTime));
            dt.Columns.Add("lamp_id", typeof(string));
            dt.Columns.Add("age_hour", typeof(int));
            dt.Columns.Add("lifespan_hour", typeof(int));
            dt.Columns.Add("last_changed", typeof(DateTime));
            dt.Columns.Add("serv_ts", typeof(DateTime));

            // ▼▼▼ [수정] 시간 계산 로직을 명확하게 분리 ▼▼▼

            // 1. 장비(에이전트)의 현재 시간을 명확히 정의 (밀리초 제거)
            DateTime now = DateTime.Now;
            DateTime agent_time = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

            // 2. 서버 보정 시간을 미리 계산
            DateTime server_time_kst = TimeSyncProvider.Instance.ToSynchronizedKst(agent_time);
            DateTime server_time = new DateTime(server_time_kst.Year, server_time_kst.Month, server_time_kst.Day, server_time_kst.Hour, server_time_kst.Minute, server_time_kst.Second);

            foreach (var lamp in lamps)
            {
                DataRow row = dt.NewRow();
                row["eqpid"] = eqpid;

                // 3. 각 컬럼에 명확히 할당
                row["ts"] = agent_time;      // 장비 시간
                row["serv_ts"] = server_time;  // 서버 보정 시간

                row["lamp_id"] = lamp.LampId;

                if (int.TryParse(lamp.Age, out int age))
                    row["age_hour"] = age;

                if (int.TryParse(lamp.LifeSpan, out int lifespan))
                    row["lifespan_hour"] = lifespan;

                if (DateTime.TryParse(lamp.LastChanged, out DateTime lastChanged))
                    row["last_changed"] = lastChanged;

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
                    // ▼▼▼ [수정] SQL 쿼리의 컬럼명 및 파라미터명 변경 (collect_time -> ts) ▼▼▼
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
                        // 파라미터 미리 정의
                        cmd.Parameters.Add("@eqpid", NpgsqlTypes.NpgsqlDbType.Varchar);
                        cmd.Parameters.Add("@lamp_id", NpgsqlTypes.NpgsqlDbType.Varchar);
                        cmd.Parameters.Add("@ts", NpgsqlTypes.NpgsqlDbType.Timestamp);
                        cmd.Parameters.Add("@age_hour", NpgsqlTypes.NpgsqlDbType.Integer);
                        cmd.Parameters.Add("@lifespan_hour", NpgsqlTypes.NpgsqlDbType.Integer);
                        cmd.Parameters.Add("@last_changed", NpgsqlTypes.NpgsqlDbType.Timestamp);
                        cmd.Parameters.Add("@serv_ts", NpgsqlTypes.NpgsqlDbType.Timestamp);

                        foreach (DataRow row in dt.Rows)
                        {
                            // 각 행의 데이터로 파라미터 값 설정
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
