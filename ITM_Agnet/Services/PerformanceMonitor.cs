// ITM_Agent/Services/PerformanceMonitor.cs
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics; // Process 클래스 사용을 위해 추가
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ITM_Agent.Services
{
    // ... (ProcessMetric, PerformanceMonitor, Metric, CircularBuffer 클래스는 이전과 동일)

    internal sealed class HardwareSampler
    {
        public event Action<Metric> OnSample;
        public event Action OnThresholdExceeded;
        public event Action OnBackToNormal;

        private readonly Computer computer;
        private Timer timer;
        private int interval;
        private bool overload;
        private readonly LogManager logManager;

        private int _consecutiveFailures = 0;
        private const int FAILURE_THRESHOLD = 3;
        private bool _isInitialized = false;

        private static bool _sensorInfoLogged = false;

        public int IntervalMs
        {
            get => interval;
            set { interval = Math.Max(500, value); timer?.Change(0, interval); }
        }

        public HardwareSampler(int intervalMs)
        {
            interval = intervalMs;
            logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true
            };

            try
            {
                if (!IsRunningAsAdmin())
                {
                    logManager.LogError("[HardwareSampler] Not running with Administrator privileges. Hardware sensor data may be unavailable. Please run the agent as an administrator.");
                }
                computer.Open();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                logManager.LogError($"[HardwareSampler] CRITICAL: Failed to open LibreHardwareMonitor on initial load: {ex.Message}. Performance monitoring will be disabled.");
                _isInitialized = false;
            }
        }

        private bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public void Start()
        {
            if (_isInitialized)
            {
                // _sensorInfoLogged = false; // 필요 시 주석 해제
                timer = new Timer(_ => Sample(), null, 0, interval);
            }
            else
            {
                logManager.LogEvent("[HardwareSampler] Skipping Start() because initial hardware monitor load failed.");
            }
        }

        public void Stop()
        {
            timer?.Dispose();
            timer = null;
            try
            {
                if (_isInitialized)
                {
                    computer.Close();
                }
            }
            catch { /* Ignore */ }
        }


        private void Sample()
        {
            if (!_isInitialized) return;

            float cpuUsage = 0, memUsage = 0, cpuTemp = 0, gpuTemp = 0;
            int fanRpm = 0;

            try
            {
                // Debug 모드가 켜져 있고, 아직 센서 정보가 로그되지 않았을 때만 상세 정보 기록
                if (LogManager.GlobalDebugEnabled && !_sensorInfoLogged)
                {
                    StringBuilder sensorInfo = new StringBuilder();
                    sensorInfo.AppendLine("[HardwareSampler] Detected Sensors (Logged Once):");
                    foreach (var hardware in computer.Hardware)
                    {
                        hardware.Update();
                        sensorInfo.AppendLine($"  Hardware: {hardware.Name} ({hardware.HardwareType})");
                        foreach (var sensor in hardware.Sensors)
                        {
                            sensorInfo.AppendLine($"    Sensor: {sensor.Name} ({sensor.SensorType}) - Value: {sensor.Value}");
                        }
                        foreach (var subHardware in hardware.SubHardware)
                        {
                            subHardware.Update();
                            sensorInfo.AppendLine($"    SubHardware: {subHardware.Name} ({subHardware.HardwareType})");
                            foreach (var sensor in subHardware.Sensors)
                            {
                                sensorInfo.AppendLine($"      Sensor: {sensor.Name} ({sensor.SensorType}) - Value: {sensor.Value}");
                            }
                        }
                    }
                    logManager.LogDebug(sensorInfo.ToString());
                    _sensorInfoLogged = true;
                }
                else // 일반 샘플링 시 업데이트만 수행
                {
                    foreach (var hardware in computer.Hardware)
                    {
                        hardware.Update();
                        foreach (var subHardware in hardware.SubHardware)
                        {
                            subHardware.Update();
                        }
                    }
                }

                // --- 나머지 센서 값 읽기 로직 (이전과 동일) ---
                var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    cpuUsage = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "CPU Total")?.Value ?? 0;
                    cpuTemp = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Core (Tctl/Tdie)")))?.Value ??
                              cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value ?? 0;
                }

                var memory = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
                if (memory != null)
                {
                    var memoryLoad = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                    if (memoryLoad?.Value != null) memUsage = memoryLoad.Value.Value;
                    else
                    {
                        var used = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Used")?.Value;
                        var total = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Total")?.Value;
                        if (used.HasValue && total.HasValue && total > 0) memUsage = (used.Value / total.Value) * 100;
                    }
                }

                var gpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia);
                if (gpu != null)
                {
                    gpuTemp = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Core") || s.Name.Contains("Temp")))?.Value ??
                              gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value ?? 0;
                }

                var allSensors = computer.Hardware.SelectMany(h => h.Sensors.Concat(h.SubHardware.SelectMany(sh => sh.Sensors)));
                var cpuFanSensor = allSensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && (s.Name.Contains("CPU") || s.Name.Equals("Fan #1", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("System Fan")));
                fanRpm = (int)(cpuFanSensor?.Value ?? allSensors.FirstOrDefault(s => s.SensorType == SensorType.Fan)?.Value ?? 0);

                // --- 실패 감지 및 자동 복구 로직 (이전과 동일) ---
                bool hasCpuError = cpuUsage == 0;
                bool hasMemError = memUsage == 0;
                bool hasTempError = cpuTemp == 0;

                if ((hasCpuError && hasMemError) || hasTempError)
                {
                    _consecutiveFailures++;
                    string failureDetails = $"Invalid sample detected (CPU Usage: {cpuUsage:F2}, Mem Usage: {memUsage:F2}, CPU Temp: {cpuTemp:F1}). Consecutive failures: {_consecutiveFailures}";
                    logManager.LogDebug($"[HardwareSampler] {failureDetails}");

                    if (_consecutiveFailures >= FAILURE_THRESHOLD)
                    {
                        logManager.LogEvent("[HardwareSampler] Consecutive sensor failures reached threshold. Attempting to re-initialize hardware monitor.");
                        try
                        {
                            computer.Close();
                            computer.Open();
                            _consecutiveFailures = 0;
                            _sensorInfoLogged = false; // 재초기화 성공 시 플래그 리셋
                            logManager.LogEvent("[HardwareSampler] Hardware monitor re-initialized successfully.");
                        }
                        catch (Exception ex)
                        {
                            logManager.LogError($"[HardwareSampler] CRITICAL: Failed to re-initialize hardware monitor: {ex.Message}. Stopping performance sampling.");
                            this.Stop();
                            _isInitialized = false; // 재초기화 실패 시 상태 변경
                        }
                    }
                    return;
                }
                _consecutiveFailures = 0;

                // --- Top 5 프로세스 정보 수집 (이전과 동일) ---
                var topProcesses = new List<ProcessMetric>();
                try
                {
                    topProcesses = Process.GetProcesses()
                        .OrderByDescending(p => p.PrivateMemorySize64)
                        .Take(5)
                        .Select(p => {
                            long privateMemoryMB = p.PrivateMemorySize64 / (1024 * 1024);
                            long workingSetMB = p.WorkingSet64 / (1024 * 1024);
                            long sharedMemoryMB = workingSetMB > privateMemoryMB ? workingSetMB - privateMemoryMB : 0;
                            return new ProcessMetric { ProcessName = p.ProcessName, MemoryUsageMB = privateMemoryMB, SharedMemoryUsageMB = sharedMemoryMB };
                        })
                        .ToList();
                }
                catch (Exception procEx)
                {
                    logManager.LogDebug($"[HardwareSampler] Failed to get process list: {procEx.Message}");
                }

                // --- 이벤트 발생 (이전과 동일) ---
                OnSample?.Invoke(new Metric(cpuUsage, memUsage, cpuTemp, gpuTemp, fanRpm, topProcesses));

                bool isOver = (cpuUsage > 75f) || (memUsage > 80f);
                if (isOver && !overload) { overload = true; OnThresholdExceeded?.Invoke(); }
                else if (!isOver && overload) { overload = false; OnBackToNormal?.Invoke(); }
            }
            catch (NullReferenceException nre) when (!_isInitialized)
            {
                logManager.LogError($"[HardwareSampler] Attempted to sample but hardware monitor is not initialized. {nre.Message}");
                this.Stop();
            }
            catch (Exception ex)
            {
                if (_isInitialized)
                {
                    logManager.LogError($"[HardwareSampler] Failed to sample hardware info: {ex.Message}");
                    // ▼▼▼ 오류 발생 부분 수정: IsDebugMode 대신 LogManager.GlobalDebugEnabled 사용 ▼▼▼
                    if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[HardwareSampler] Sampling Exception details: {ex.StackTrace}");
                    // ▲▲▲ 수정 끝 ▲▲▲
                }
            }
        } // Sample() 메서드 끝

    } // HardwareSampler 클래스 끝

} // 네임스페이스 끝
