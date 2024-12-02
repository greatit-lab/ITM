using ITM_Agent.ucPanel;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ITM_Agent
{
    public partial class MainForm : Form
    {
        private string settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini");
        private readonly string logFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EventLog");
        private readonly string logFileName = $"{DateTime.Now:yyyyMMdd}_event.log";
        // private readonly string logFolderPath = @"C:\Temp\EventLog"; // 권한이 보장된 경로로 설정
        
        // 이벤트 핸들러 연결
        private readonly string debugLogFileName = $"{DateTime.Now:yyyyMMdd}_debug.log";
        
        private bool isInitialStatusShown = false; // Ready to Run 상태가 한 번만 표시되도록 제어
        private List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        
        private NotifyIcon trayIcon; // TrayIcon 객체
        private ContextMenuStrip trayMenu; // TrayIcon 메뉴
        
        ucPanel.ucScreen1 ucSc1 = new ucPanel.ucScreen1();
        ucPanel.ucScreen2 ucSc2 = new ucPanel.ucScreen2();
        ucPanel.ucScreen3 ucSc3 = new ucPanel.ucScreen3();
        ucPanel.ucScreen4 ucSc4 = new ucPanel.ucScreen4();
        
        private const string AppVersion = "v1.0.0";
        
        public MainForm()
        {
            InitializeComponent();
            
            // Main Form 제목 설정
            this.Text = $"ITM Agent - {AppVersion}";
            this.MaximizeBox = false;   // 최대화 버튼 비활성화
            btn_Run.Enabled = false; // 초기에는 비활성화
            
            // TrayIcon 초기화
            InitializeTrayIcon();

            // 폼 닫기 버튼 이벤트 설정
            this.FormClosing += MainForm_FormClosing;
            
            LoadOrCreateSettingsFile();
            
            // ucScreen1의 상태 변경 이벤트 구독
            //ucSc1.RunButtonStateChanged += OnRunButtonStateChanged;
            
            // ucScreen1 상태 변경 이벤트 구독
            ucSc1.StatusUpdated += UpdateMainStatus;
            
            // MainForm에 ucScreen1 추가
            pMain.Controls.Add(ucSc1);
            
            // 초기 상태 확인
            InitializeStatus();
            
            btn_Run.Click += btn_Run_Click; // Run 버튼 클릭 이벤트 등록
            btn_Stop.Click += btn_Stop_Click; // Stop 버튼 클릭 이벤트 등록
            
            // 목록 상태 변경 이벤트에서 Validate 호출
            ucSc1.ListSelectionChanged += ValidateAndUpdateStatus;
        }
        
        private ToolStripMenuItem titleItem; // MainForm 제목 항목
        private ToolStripMenuItem runItem; // Run 메뉴 항목
        private ToolStripMenuItem stopItem; // Stop 메뉴 항목
        private ToolStripMenuItem quitItem; // Quit 메뉴 항목
        
        private void InitializeTrayIcon()
        {
            // ContextMenuStrip 생성
            trayMenu = new ContextMenuStrip();
        
            // MainForm 제목 추가
            titleItem = new ToolStripMenuItem(this.Text);
            titleItem.Click += (sender, e) => RestoreMainForm(); // 제목 클릭 시 MainForm 복원
            trayMenu.Items.Add(titleItem);
        
            // 구분선 추가
            trayMenu.Items.Add(new ToolStripSeparator());
        
            // Run 메뉴 항목
            runItem = new ToolStripMenuItem("Run", null, (sender, e) => btn_Run.PerformClick());
            trayMenu.Items.Add(runItem);
        
            // Stop 메뉴 항목
            stopItem = new ToolStripMenuItem("Stop", null, (sender, e) => btn_Stop.PerformClick());
            trayMenu.Items.Add(stopItem);
        
            // Quit 메뉴 항목
            quitItem = new ToolStripMenuItem("Quit", null, (sender, e) => PerformQuit());

            trayMenu.Items.Add(quitItem);
        
            // NotifyIcon 생성
            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application, // 트레이 아이콘 설정
                ContextMenuStrip = trayMenu,
                Visible = true,
                Text = this.Text
            };
        
            // 더블클릭 시 MainForm 복원
            trayIcon.DoubleClick += (sender, e) => RestoreMainForm();
        
            // 초기 상태 업데이트
            UpdateTrayMenuStatus();
        }
        
        private void PerformQuit()
        {
            try
            {
                // 디버깅용 로그
                Console.WriteLine("PerformQuit: 종료 로직 시작");
        
                // FileSystemWatcher 및 기타 리소스 정리
                StopFileWatchers();
        
                // NotifyIcon 리소스 정리
                trayIcon?.Dispose();
        
                // 디버깅용 로그
                Console.WriteLine("PerformQuit: 리소스 정리 완료");
        
                // 애플리케이션 강제 종료
                Environment.Exit(0);
        
                // 디버깅용 로그
                Console.WriteLine("PerformQuit: 애플리케이션 종료 완료");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"종료 중 오류 발생: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        
        private void UpdateTrayMenuStatus()
        {
            if (runItem != null) runItem.Enabled = btn_Run.Enabled;
            if (stopItem != null) stopItem.Enabled = btn_Stop.Enabled;
            if (quitItem != null) quitItem.Enabled = btn_Quit.Enabled;
        }
        
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 폼을 닫을 때 강제로 종료
            StopFileWatchers();
            trayIcon?.Dispose();
            Environment.Exit(0); // 모든 리소스를 정리하고 프로세스 종료
        }

        
        private void RestoreMainForm()
        {
            // MainForm 활성화
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        
            // MainForm 제목 메뉴 비활성화
            titleItem.Enabled = false;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // MainForm 닫기 버튼 숨기기
            this.ShowInTaskbar = true; // 작업 표시줄에 표시 여부
        }
        
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
        
            // 창이 처음 표시될 때 제목 메뉴 비활성화
            titleItem.Enabled = false;
        }
        
        private void ValidateAndUpdateStatus()
        {
            // 현재 상태가 "Running..."이면 상태 변경 방지
            if (ts_Status.Text == "Running...")
            {
                return;
            }
        
            // 상태 검증 및 업데이트
            bool isReadyToRun = IsReadyToRun();
            if (isReadyToRun)
            {
                UpdateMainStatus("Ready to Run", Color.Green);
            }
            else
            {
                UpdateMainStatus("Stopped!", Color.Red);
            }
        }
        
        private void InitializeStatus()
        {
            if (IsReadyToRun() && !isInitialStatusShown)
            {
                UpdateMainStatus("Ready to Run", Color.Green);
                isInitialStatusShown = true; // 초기 상태는 한 번만 표시
            }
            else
            {
                UpdateMainStatus("Stopped!", Color.Red);
            }
        }

        private bool IsReadyToRun()
        {
            // BaseFolder, TargetFolders, Regex 섹션에 최소 1개 이상의 값이 있는지 확인
            return HasValuesInSection("[BaseFolder]") &&
                   HasValuesInSection("[TargetFolders]") &&
                   HasValuesInSection("[Regex]");
        }

        private bool HasValuesInSection(string section)
        {
            if (!File.Exists(settingsFilePath)) return false;

            var lines = File.ReadAllLines(settingsFilePath).ToList();
            int sectionIndex = lines.FindIndex(line => line.Trim() == section);
            if (sectionIndex == -1) return false;

            int endIndex = lines.FindIndex(sectionIndex + 1, line =>
                line.StartsWith("[") || string.IsNullOrWhiteSpace(line));
            if (endIndex == -1) endIndex = lines.Count;

            // 섹션 내용이 빈 줄이 아닌 항목이 있는지 확인
            return lines.Skip(sectionIndex + 1).Take(endIndex - sectionIndex - 1).Any(line => !string.IsNullOrWhiteSpace(line));
        }

        private void UpdateMainStatus(string status, Color color)
        {
            ts_Status.Text = status; // 상태 업데이트
            ts_Status.ForeColor = color;
        
            // 상태별 버튼 활성화/비활성화 설정
            switch (status)
            {
                case "Ready to Run":
                    btn_Run.Enabled = true;  // Run 버튼 활성화
                    btn_Stop.Enabled = false; // Stop 버튼 비활성화
                    btn_Quit.Enabled = true; // Quit 버튼 활성화
        
                    // ucScreen1 내부 버튼 활성화
                    ucSc1.SetButtonsEnabled(true);
                    break;
        
                case "Stopped!":
                    btn_Run.Enabled = true;  // Run 버튼 활성화
                    btn_Stop.Enabled = false; // Stop 버튼 비활성화
                    btn_Quit.Enabled = true; // Quit 버튼 활성화
        
                    // ucScreen1 내부 버튼 활성화
                    ucSc1.SetButtonsEnabled(true);
                    break;
        
                case "Running...":
                    btn_Run.Enabled = false; // Run 버튼 비활성화
                    btn_Stop.Enabled = true;  // Stop 버튼 활성화
                    btn_Quit.Enabled = false; // Quit 버튼 비활성화
        
                    // ucScreen1 내부 버튼 비활성화
                    ucSc1.SetButtonsEnabled(false);
                    break;
            }
            // TrayIcon 메뉴 상태 동기화
            UpdateTrayMenuStatus();
        }
        
        private void OnRunButtonStateChanged(bool isEnabled)
        {
            // btn_Run 버튼 상태 업데이트
            btn_Run.Enabled = isEnabled;
        }
        
        private void LoadOrCreateSettingsFile()
        {
            // 설정 파일이 존재하고 eqpid 값이 있는지 확인
            if (File.Exists(settingsFilePath))
            {
                string eqpid = GetEqpidFromSettings();
                if (!string.IsNullOrEmpty(eqpid))
                {
                    // eqpid 값이 있으면 Main Form으로 진행
                    ProceedWithMainFunctionality(eqpid);
                }
                else
                {
                    // eqpid 값이 없으면 입력 받기
                    PromptForEqpid();
                }
            }
            else
            {
                // 설정 파일이 없으면 파일 생성 및 eqpid 입력 받기
                File.Create(settingsFilePath).Dispose();
                PromptForEqpid();
            }
        }
        
        private string GetEqpidFromSettings()
        {
            // 설정 파일에 [Eqpid] 섹션의 eqpid 값을 가져오기
            string[] lines = File.ReadAllLines(settingsFilePath);
            bool eqpidSectionFound = false;
            
            foreach (string line in lines)
            {
                if (line.Trim() == "[Eqpid]")
                {
                    eqpidSectionFound = true;
                }
                else if (eqpidSectionFound && line.StartsWith("Eqpid = "))
                {
                    return line.Substring("Eqpid =".Length).Trim();
                }
            }
            return null;
        }
        
        private void PromptForEqpid()
        {
            using (var form = new EqpidInputForm())
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    string input = form.Eqpid;
                    if (!string.IsNullOrEmpty(input))
                    {
                        // 입력받은 장비명을 대문자로 변환
                        string upperInput = input.ToUpper();
                        
                        // eqpid 값을 설정 파일에 저장하고 메인 기능으로 진행
                        File.WriteAllText(settingsFilePath, "[Eqpid]\nEqpid = " + input);
                        ProceedWithMainFunctionality(input);
                    }
                    else
                    {
                        MessageBox.Show("Eqpid input is required.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        PromptForEqpid();
                    }
                }
            }
        }
        
        private void ProceedWithMainFunctionality(string eqpid)
        {
            // eqpid 값을 lb_eqpid에 표시
            lb_eqpid.Text = $"Eqpid: {eqpid}";
        }
        
        private void Form1_Load(object sender, EventArgs e)
        {
            pMain.Controls.Add(ucSc1);
        }
        
        private void categorizeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            pMain.Controls.Clear();
            pMain.Controls.Add(ucSc1);
        }
        
        private void overrideNamesToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            pMain.Controls.Clear();
            pMain.Controls.Add(ucSc2);
        }
        
        private void imageTransToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            pMain.Controls.Clear();
            pMain.Controls.Add(ucSc3);
        }
        
        private void uploadDataToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            pMain.Controls.Clear();
            pMain.Controls.Add(ucSc4);
        }
        
        private void optionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            pMain.Controls.Clear();
        }
        
        private void btn_Run_Click(object sender, EventArgs e)
        {
            try
            {
                // 파일 감시 시작 (예: ucSc1에서 실행)
                ucSc1.UpdateStatusOnRun(true); // Running... 상태로 변경
                UpdateMainStatus("Running...", Color.Blue);
                
                // Log start success
                LogEvent("File monitoring started successfully.");
                
                // Debug mode: Create debug. log and log detailed information
                if (cb_DebugMode.Checked)
                {
                    LogEvent("Debug mode is active during Run. Debug logs will be recorded.", true);
                }
                MessageBox.Show("File monitoring has started.", "Notification", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                LogEvent($"File monitoring failed to start: {ex.Message}");
                MessageBox.Show($"An error occurred while starting file monitoring: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btn_Stop_Click(object sender, EventArgs e)
        {
            try
            {
                // 파일 감시 중지 (예: ucSc1에서 실행 중지)
                ucSc1.UpdateStatusOnRun(false); // 상태 복원
                UpdateMainStatus("Stopped!", Color.Red);
                
                // Log stop success
                LogEvent("File monitoring Stopped successfully.");
                
                MessageBox.Show("File monitoring has Stopped.", "Notification", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                LogEvent($"File monitoring failed to stop: {ex.Message}");
                MessageBox.Show($"An error occurred while stopping file monitoring: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void btn_Quit_Click(object sender, EventArgs e)
        {
            Console.WriteLine("btn_Quit_Click: 이벤트 호출됨");
        
            if (ts_Status.Text == "Ready to Run" || ts_Status.Text == "Stopped!")
            {
                Console.WriteLine("btn_Quit_Click: PerformQuit 호출");
                PerformQuit();
            }
            else
            {
                MessageBox.Show("실행 중에는 종료할 수 없습니다. 먼저 작업을 중지하세요.",
                                "종료 불가",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
            }
        }


        
        private void InitializeFileWatchers()
        {
            foreach (var watcher in watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            watchers.Clear();
            
            // Load target folders
            var targetFolders = GetFoldersFromSection("[TargetFolders]");
            var excludeFolders = GetFoldersFromSection("[ExcludeFolders]");

            foreach (var folder in targetFolders)
            {
                if (excludeFolders.Any(excluded => folder.StartsWith(excluded)))
                {
                    // Skip excluded folders
                    continue;
                }
                
                // Create a new watcher
                var watcher = new FileSystemWatcher
                {
                    Path = folder,
                    IncludeSubdirectories = true,   // Monitor subdirectories
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                
                // Register event handlers for file changes
                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
                
                // Log watcher initialization
                LogEvent($"File watcher initialized for folder: {folder}");
            }
        }

        private void StopFileWatchers()
        {
            if (watchers != null)
            {
                foreach (var watcher in watchers)
                {
                    watcher.EnableRaisingEvents = false; // 이벤트 비활성화
                    watcher.Dispose(); // 리소스 해제
                }
                watchers.Clear(); // 리스트 초기화
            }
        }



        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Debugging Information
                Console.WriteLine($"OnFileChanged triggered: {e.ChangeType} - {e.FullPath}");
                
                // Log the detected file change
                string eventType = e.ChangeType.ToString().ToLower();
                string filePath = e.FullPath;
                LogEvent($"File {eventType}: {filePath}");
                
                if (cb_DebugMode.Checked)
                {
                    LogEvent($"Debug: File {eventType} detected at {filePath} by {sender.GetType().Name}.", true);
                }
                
                // Process regex matching and file backup
                var regexList = GetRegexListFromSettings();
                bool isMatched = false;
                
                foreach (var regexInfo in regexList)
                {
                    string regex = regexInfo.Key;
                    string targetFolder = regexInfo.Value;
                    
                    if (Regex.IsMatch(e.Name, regex))
                    {
                        string destinationPath = Path.Combine(targetFolder, Path.GetFileName(e.Name));
                        Directory.CreateDirectory(targetFolder);    // Ensure target folder exists
                        File.Copy(filePath, destinationPath, true); // copy the file
                        
                        LogEvent($"Regex matched: {regex}. File copied to: {destinationPath}");
                        if (cb_DebugMode.Checked)
                        {
                            LogEvent($"Debug: Regex {regex} matched for {filePath}, copied to {destinationPath}.", true);
                        }
                        
                        isMatched = true;
                        break;
                    }
                }
                
                if (!isMatched)
                {
                    LogEvent($"No regex match found for file: {filePath}");
                    if (cb_DebugMode.Checked)
                    {
                        LogEvent($"Debug: No regex match for {filePath}.", true);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnFileChanged: {ex.Message}");
                LogEvent($"Error while processing file: {ex.Message}");
                if (cb_DebugMode.Checked)
                {
                    LogEvent($"Debug: Error details - {ex}.", true);
                }
            }
        }

        private List<string> GetFoldersFromSection(string section)
        {
            var folders = new List<string>();
            if (File.Exists(settingsFilePath))
            {
                var lines = File.ReadAllLines(settingsFilePath);
                bool inSection = false;

                foreach (var line in lines)
                {
                    if (line.Trim() == section)
                    {
                        inSection = true;
                        continue;
                    }

                    if (inSection)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("["))
                            break;

                        folders.Add(line.Trim());
                    }
                }
            }
            return folders;
        }

        private Dictionary<string, string> GetRegexListFromSettings()
        {
            var regexList = new Dictionary<string, string>();
            if (File.Exists(settingsFilePath))
            {
                var lines = File.ReadAllLines(settingsFilePath);
                bool inRegexSection = false;

                foreach (var line in lines)
                {
                    if (line.Trim() == "[Regex]")
                    {
                        inRegexSection = true;
                        continue;
                    }

                    if (inRegexSection)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("["))
                            break;

                        var parts = line.Split(new[] { "->" }, StringSplitOptions.None);
                        if (parts.Length == 2)
                        {
                            regexList[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }
            }
            return regexList;
        }
        
        private void LogEvent(string message, bool isDebug = false)
        {
            try
            {
                // Console 디버깅 메시지 출력
                Console.WriteLine($"LogEvent called. Message: {message}, IsDebug: {isDebug}");
        
                // Ensure log folder exists
                if (!Directory.Exists(logFolderPath))
                {
                    Console.WriteLine($"Log folder not found. Creating folder: {logFolderPath}");
                    Directory.CreateDirectory(logFolderPath);
                }
        
                // Determine log file path (event.log or debug.log)
                string logFilePath = isDebug ? Path.Combine(logFolderPath, debugLogFileName) : Path.Combine(logFolderPath, logFileName);
        
                // Rotate log file if size exceeds 5 MB
                if (File.Exists(logFilePath))
                {
                    var fileSize = new FileInfo(logFilePath).Length;
                    Console.WriteLine($"Log file exists. Current size: {fileSize} bytes");
        
                    if (fileSize >= 5 * 1024 * 1024)
                    {
                        Console.WriteLine("Log file size exceeded 5MB. Rotating log file...");
                        string archiveLogFileName = $"{Path.GetFileNameWithoutExtension(logFilePath)}_{GetNextLogIndex()}.log";
                        string archiveLogFilePath = Path.Combine(logFolderPath, archiveLogFileName);
                        File.Move(logFilePath, archiveLogFilePath);
                        Console.WriteLine($"Log file rotated. New archive created: {archiveLogFilePath}");
                    }
                }
                else
                {
                    Console.WriteLine($"Log file not found. Creating new log file: {logFilePath}");
                }
        
                // Write log message
                using (var writer = new StreamWriter(logFilePath, true))
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    writer.WriteLine($"{timestamp} - {message}");
                }
        
                Console.WriteLine($"Successfully logged message: {message}");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                Console.WriteLine($"UnauthorizedAccessException: {uaEx.Message}");
                MessageBox.Show($"Access denied: {uaEx.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (DirectoryNotFoundException dnEx)
            {
                Console.WriteLine($"DirectoryNotFoundException: {dnEx.Message}");
                MessageBox.Show($"Directory not found: {dnEx.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (IOException ioEx)
            {
                Console.WriteLine($"IOException: {ioEx.Message}");
                MessageBox.Show($"IO Error: {ioEx.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"General Exception: {ex.Message}");
                MessageBox.Show($"Unexpected error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void TestLogWrite()
        {
            string testFolderPath = @"C:\Temp\EventLog";
            string testLogFilePath = Path.Combine(testFolderPath, "test_log.txt");
        
            try
            {
                if (!Directory.Exists(testFolderPath))
                {
                    Directory.CreateDirectory(testFolderPath);
                }
        
                File.WriteAllText(testLogFilePath, "Test log message.");
                MessageBox.Show("Log written successfully.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to write log: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void CheckExecutionPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string currentDir = Environment.CurrentDirectory;
        
            MessageBox.Show($"Base Directory: {baseDir}\nCurrent Directory: {currentDir}", "Execution Path", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        
        private void cb_DebugMode_CheckedChanged(object sender, EventArgs e)
        {
            if (cb_DebugMode.Checked)
            {
                LogEvent("Debug mode enabled."); // Event log
                LogEvent("Debug mode has been activated.", true); // Debug log
            }
            else
            {
                LogEvent("Debug mode disabled."); // Event log
                LogEvent("Debug mode has been deactivated.", true); // Debug log
            }
        }
        
        private int GetNextLogIndex()
        {
            var existingFiles = Directory.GetFiles(logFolderPath, $"{DateTime.Now:yyyyMMdd}_event_*.log");
            int maxIndex = 0;
        
            foreach (var file in existingFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                string[] parts = fileName.Split('_');
                if (parts.Length > 2 && int.TryParse(parts.Last(), out int index))
                {
                    maxIndex = Math.Max(maxIndex, index);
                }
            }
        
            return maxIndex + 1;
        }
    }
        
    public class EqpidInputForm : Form
    {
        public string Eqpid { get; private set; }
        private TextBox textBox;
        private Button submitButton;
        private Button cancelButton;
        private System.Windows.Forms.Label instructionLabel;
        private System.Windows.Forms.Label warningLabel;
    
        public EqpidInputForm()
        {
            this.Text = "New EQPID Registry";
            this.Size = new System.Drawing.Size(300, 200);
            
            // 화면 중앙에 표시되도록 설정
            this.StartPosition = FormStartPosition.CenterScreen;
    
            // Disable Minimize, Maximize, and Close buttons
            this.FormBorderStyle = FormBorderStyle.FixedDialog;   // Fixed size, no Maximize
            this.ControlBox = false;    // Disables Minimize, Maximize, and Close buttons
    
            // Instruction Label
            instructionLabel = new System.Windows.Forms.Label()
            {
                Text = "신규로 등록 필요한 장비명을 입력하세요.",
                Top = 20,
                Left = 25,
                Width = 300
            };
    
            // TextBox for input
            textBox = new TextBox()
            {
                Top = 50,
                Left = 90,
                Width = 110
            };
    
            // Warning Label (hidden by default)
            warningLabel = new System.Windows.Forms.Label()
            {
                Text = "장비명을 입력해주세요.",
                Top = 80,
                Left = 80,
                ForeColor = System.Drawing.Color.Red,
                AutoSize = true,
                Visible = false // Hidden initially
            };
    
            // Submit Button
            submitButton = new Button()
            {
                Text = "Submit",
                Top = 120,
                Left = 50,
                Width = 90
            };
    
            // Cancel Button
            cancelButton = new Button()
            {
                Text = "Cancel",
                Top = 120,
                Left = 150,
                Width = 90
            };
    
            // Submit Button Click Event
            submitButton.Click += (sender, e) =>
            {
                if (string.IsNullOrWhiteSpace(textBox.Text)) // Check if input is empty
                {
                    warningLabel.Visible = true; // Show warning message
                    return; // Prevent closing the form
                }
    
                this.Eqpid = textBox.Text;  // Save input
                this.DialogResult = DialogResult.OK;    // Close the form with OK result
                this.Close();
            };
    
            // Cancel Button Click Event
            cancelButton.Click += (sender, e) =>
            {
                Application.Exit(); // Terminate the entire application
            };
    
            // Add controls to the form
            this.Controls.Add(instructionLabel);
            this.Controls.Add(textBox);
            this.Controls.Add(warningLabel);
            this.Controls.Add(submitButton);
            this.Controls.Add(cancelButton);
        }
    }
}