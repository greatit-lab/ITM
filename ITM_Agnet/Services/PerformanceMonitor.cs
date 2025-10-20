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
    public sealed class ProcessMetric
    {
        public string ProcessName { get; set; }
        public long MemoryUsageMB { get; set; } // Private Working Set (Private Bytes)
        public long SharedMemoryUsageMB { get; set; } // 추가: 공유 메모리 사용량
    }

    public sealed class PerformanceMonitor
    {
        private static readonly Lazy<PerformanceMonitor> _inst = new Lazy<PerformanceMonitor>(() => new PerformanceMonitor());
        public static PerformanceMonitor Instance => _inst.Value;

        private const long MAX_LOG_SIZE = 5 * 1024 * 1024;
        private readonly HardwareSampler sampler;
        private readonly CircularBuffer<Metric> buffer = new CircularBuffer<Metric>(capacity: 1000);
        private readonly Timer flushTimer;
        private readonly object sync = new object();
        private const int FLUSH_INTERVAL_MS = 30_000;
        private const int BULK_COUNT = 60;
        private bool isEnabled;
        private bool sampling;
        private bool fileLoggingEnabled;

        internal void StartSampling()
        {
            if (sampling) return;
            sampling = true;
            sampler.Start();
        }

        internal void StopSampling()
        {
            if (!sampling) return;
            sampler.Stop();
            DisableFileLogging();
            sampling = false;
        }

        internal void SetFileLogging(bool enable) => (enable ? (Action)EnableFileLogging : DisableFileLogging)();
        private void EnableFileLogging()
        {
            if (fileLoggingEnabled) return;
            Directory.CreateDirectory(GetLogDir());
            flushTimer.Change(FLUSH_INTERVAL_MS, FLUSH_INTERVAL_MS);
            fileLoggingEnabled = true;
        }

        private void DisableFileLogging()
        {
            if (!fileLoggingEnabled) return;
            flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            FlushToFile();
            fileLoggingEnabled = false;
        }

        internal void RegisterConsumer(Action<Metric> consumer)
        {
            sampler.OnSample += consumer;
        }

        internal void UnregisterConsumer(Action<Metric> consumer)
        {
            sampler.OnSample -= consumer;
        }

        private PerformanceMonitor()
        {
            sampler = new HardwareSampler(intervalMs: 5_000);
            sampler.OnSample += OnSampleReceived;
            sampler.OnThresholdExceeded += () => sampler.IntervalMs = 1_000;
            sampler.OnBackToNormal += () => sampler.IntervalMs = 5_000;
            flushTimer = new Timer(_ => FlushToFile(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (sync)
            {
                if (isEnabled) return;
                isEnabled = true;
                Directory.CreateDirectory(GetLogDir());
                sampler.Start();
                flushTimer.Change(FLUSH_INTERVAL_MS, FLUSH_INTERVAL_MS);
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                if (!isEnabled) return;
                isEnabled = false;
                sampler.Stop();
                flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                FlushToFile();
            }
        }

        private void OnSampleReceived(Metric m)
        {
            lock (sync)
            {
                buffer.Push(m);
                if (fileLoggingEnabled && buffer.Count >= BULK_COUNT)
                    FlushToFile();
            }
        }

        private void FlushToFile()
        {
            if (!fileLoggingEnabled || buffer.Count == 0) return;
            string fileName = $"{DateTime.Now:yyyyMMdd}_performance.log";
            string filePath = Path.Combine(GetLogDir(), fileName);
            RotatePerfLogIfNeeded(filePath);
            using (var fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Seek(0, SeekOrigin.End);
                using (var sw = new StreamWriter(fs))
                {
                    foreach (Metric m in buffer.ToArray())
                    {
                        var topProcessesLog = string.Join(", ", m.TopProcesses.Select(p => $"{p.ProcessName}={p.MemoryUsageMB}MB"));
                        sw.WriteLine($"{m.Timestamp:yyyy-MM-dd HH:mm:ss.fff} C:{m.Cpu:F2} M:{m.Mem:F2} CT:{m.CpuTemp:F1} GT:{m.GpuTemp:F1} FAN:{m.FanRpm} | Top5: [{topProcessesLog}]");
                    }
                }
            }
            buffer.Clear();
        }

        private void RotatePerfLogIfNeeded(string filePath)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length <= MAX_LOG_SIZE) return;
            string extension = fi.Extension;
            string baseName = Path.GetFileNameWithoutExtension(filePath);
            string dir = fi.DirectoryName;
            int index = 1;
            string rotatedPath;
            do
            {
                string rotatedName = $"{baseName}_{index}{extension}";
                rotatedPath = Path.Combine(dir, rotatedName);
                index++;
            }
            while (File.Exists(rotatedPath));
            File.Move(filePath, rotatedPath);
        }

        private static string GetLogDir() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    }

    internal readonly struct Metric
    {
        public DateTime Timestamp { get; }
        public float Cpu { get; }
        public float Mem { get; }
        public float CpuTemp { get; }
        public float GpuTemp { get; }
        public int FanRpm { get; }
        public List<ProcessMetric> TopProcesses { get; }

        public Metric(float cpu, float mem, float cpuTemp, float gpuTemp, int fanRpm, List<ProcessMetric> topProcesses)
        {
            Timestamp = DateTime.Now;
            Cpu = cpu;
            Mem = mem;
            CpuTemp = cpuTemp;
            GpuTemp = gpuTemp;
            FanRpm = fanRpm;
            TopProcesses = topProcesses;
        }
    }

    internal sealed class CircularBuffer<T>
    {
        private readonly T[] buf;
        private int head, count;
        public int Capacity { get; }
        public int Count => count;
        public CircularBuffer(int capacity) { Capacity = capacity; buf = new T[capacity]; }
        public void Push(T item)
        {
            buf[(head + count) % Capacity] = item;
            if (count == Capacity) head = (head + 1) % Capacity;
            else count++;
        }
        public IEnumerable<T> ToArray() => Enumerable.Range(0, count).Select(i => buf[(head + i) % Capacity]);
        public void Clear() => head = count = 0;
    }

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

        // ▼▼▼ [추가] 초기화 성공 여부 플래그 ▼▼▼
        private bool _isInitialized = false;

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
                _isInitialized = true; // 초기화 성공 플래그 설정
            }
            catch (Exception ex)
            {
                logManager.LogError($"[HardwareSampler] CRITICAL: Failed to open LibreHardwareMonitor on initial load: {ex.Message}. Performance monitoring will be disabled.");
                _isInitialized = false; // 초기화 실패 플래그 설정
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
            // ▼▼▼ [수정] 초기화에 성공한 경우에만 타이머 시작 ▼▼▼
            if (_isInitialized)
            {
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
                computer.Close();
            }
            catch { /* Ignore */ }
        }

        private void Sample()
        {
            float cpuUsage = 0, memUsage = 0, cpuTemp = 0, gpuTemp = 0;
            int fanRpm = 0;

            try
            {
                // ▼▼▼ 디버그 로그 추가 ▼▼▼
                if (LogManager.GlobalDebugEnabled && _consecutiveFailures == 0) // 첫 성공 시 또는 디버그 모드에서 센서 목록 로깅
                {
                    StringBuilder sensorInfo = new StringBuilder();
                    sensorInfo.AppendLine("[HardwareSampler] Detected Sensors:");
                    foreach (var hardware in computer.Hardware)
                    {
                        hardware.Update(); // 센서 업데이트
                        sensorInfo.AppendLine($"  Hardware: {hardware.Name} ({hardware.HardwareType})");
                        foreach (var sensor in hardware.Sensors)
                        {
                            sensorInfo.AppendLine($"    Sensor: {sensor.Name} ({sensor.SensorType}) - Value: {sensor.Value}");
                        }
                        foreach (var subHardware in hardware.SubHardware) // 서브 하드웨어도 확인
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
                }
                // ▲▲▲ 디버그 로그 추가 끝 ▲▲▲
                else // 일반 샘플링 시에는 업데이트만 수행
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

                var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    cpuUsage = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "CPU Total")?.Value ?? 0;
                    cpuTemp = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value ?? 0;
                }

                var memory = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
                if (memory != null)
                {
                    var memoryLoad = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                    if (memoryLoad?.Value != null)
                    {
                        memUsage = memoryLoad.Value.Value;
                    }
                    else
                    {
                        var used = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Used")?.Value;
                        var total = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Total")?.Value;
                        if (used.HasValue && total.HasValue && total > 0)
                        {
                            memUsage = (used.Value / total.Value) * 100;
                        }
                    }
                }

                // GPU 온도 가져오는 부분 수정 (좀 더 유연하게)
                var gpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia);
                if (gpu != null)
                {
                    // "Core" 대신 일반적인 GPU 온도 센서 이름 (예: "GPU Core") 또는 첫 번째 온도 센서 사용 시도
                    gpuTemp = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Core") || s.Name.Contains("Temp")))?.Value ??
                              gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value ?? 0; // 최후의 수단으로 첫 번째 온도 센서
                }

                // 팬 속도 가져오는 부분 수정 (좀 더 유연하게)
                var allSensors = computer.Hardware.SelectMany(h => h.Sensors.Concat(h.SubHardware.SelectMany(sh => sh.Sensors)));
                // CPU 팬 식별 시도 (다양한 이름 가능성 고려)
                var cpuFanSensor = allSensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && (s.Name.Contains("CPU") || s.Name.Equals("Fan #1", StringComparison.OrdinalIgnoreCase)));
                fanRpm = (int)(cpuFanSensor?.Value ?? // CPU 팬을 먼저 찾으면 그 값을 사용
                               allSensors.FirstOrDefault(s => s.SensorType == SensorType.Fan)?.Value ?? 0); // 못 찾으면 첫 번째 팬 값 사용

                // ▼▼▼ [핵심 수정] 실패 감지 및 로깅 상세화, 자동 복구 로직 개선 ▼▼▼
                bool hasCpuError = cpuUsage == 0;
                bool hasMemError = memUsage == 0;
                bool hasTempError = cpuTemp == 0;

                // CPU와 메모리 사용률이 동시에 0이거나, CPU 온도가 0인 경우를 실패로 간주
                if ((hasCpuError && hasMemError) || hasTempError)
                {
                    _consecutiveFailures++;
                    // 어떤 값이 0이었는지 상세히 로그에 남김
                    string failureDetails = $"Invalid sample detected (CPU Usage: {cpuUsage:F2}, Mem Usage: {memUsage:F2}, CPU Temp: {cpuTemp:F1}). Consecutive failures: {_consecutiveFailures}";
                    logManager.LogDebug($"[HardwareSampler] {failureDetails}");

                    if (_consecutiveFailures >= FAILURE_THRESHOLD)
                    {
                        logManager.LogError("[HardwareSampler] Consecutive sensor failures reached threshold. Attempting to re-initialize hardware monitor.");
                        try
                        {
                            computer.Close();
                            computer.Open();
                            _consecutiveFailures = 0; // 성공적으로 재시작 시도 후 카운터 초기화
                            logManager.LogEvent("[HardwareSampler] Hardware monitor re-initialized successfully.");
                        }
                        catch (Exception ex)
                        {
                            // 재초기화 실패 시, 타이머를 중지하여 반복적인 오류 로그 방지
                            logManager.LogError($"[HardwareSampler] CRITICAL: Failed to re-initialize hardware monitor: {ex.Message}. Stopping performance sampling.");
                            this.Stop(); // 샘플링 중단
                        }
                    }
                    return; // 유효하지 않은 샘플은 폐기
                }

                // 유효한 샘플을 받으면 실패 카운터 초기화
                _consecutiveFailures = 0;
                // ▲▲▲ 수정 끝 ▲▲▲

                var topProcesses = new List<ProcessMetric>();
                try
                {
                    // ▼▼▼ 메모리 수집 로직 수정 ▼▼▼
                    topProcesses = Process.GetProcesses()
                        .OrderByDescending(p => p.PrivateMemorySize64) // Private Working Set 기준으로 정렬
                        .Take(5)
                        .Select(p => {
                            long privateMemoryMB = p.PrivateMemorySize64 / (1024 * 1024);
                            long workingSetMB = p.WorkingSet64 / (1024 * 1024);
                            long sharedMemoryMB = workingSetMB > privateMemoryMB ? workingSetMB - privateMemoryMB : 0; // 공유 메모리 계산

                            return new ProcessMetric
                            {
                                ProcessName = p.ProcessName,
                                MemoryUsageMB = privateMemoryMB,         // Private Working Set 저장
                                SharedMemoryUsageMB = sharedMemoryMB     // 계산된 공유 메모리 저장
                            };
                        })
                        .ToList();
                    // ▲▲▲ 메모리 수집 로직 수정 끝 ▲▲▲
                }
                catch (Exception procEx)
                {
                    logManager.LogDebug($"[HardwareSampler] Failed to get process list: {procEx.Message}");
                }

                OnSample?.Invoke(new Metric(cpuUsage, memUsage, cpuTemp, gpuTemp, fanRpm, topProcesses));

                bool isOver = (cpuUsage > 75f) || (memUsage > 80f);
                if (isOver && !overload)
                {
                    overload = true;
                    OnThresholdExceeded?.Invoke();
                }
                else if (!isOver && overload)
                {
                    overload = false;
                    OnBackToNormal?.Invoke();
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[HardwareSampler] Failed to sample hardware info: {ex.Message}");
            }
        }
    }
}
