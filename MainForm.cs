using ITM_Agent.ucPanel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ITM_Agent
{
    public partial class MainForm : Form
    {
        private string settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini");
        private List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        
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
            
            LoadOrCreateSettingsFile();
            
            // ucScreen1의 상태 변경 이벤트 구독
            ucSc1.RunButtonStateChanged += OnRunButtonStateChanged;
            
            // MainForm에 ucScreen1 추가
            pMain.Controls.Add(ucSc1);
            
            btn_Run.Click += btn_Run_Click; // Run 버튼 클릭 이벤트 등록
            btn_Stop.Click += btn_Stop_Click; // Stop 버튼 클릭 이벤트 등록
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
                InitializeFileWatchers();  // 파일 감시 초기화
                MessageBox.Show("파일 변화 감지가 시작되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일 감지 시작 중 오류 발생: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btn_Stop_Click(object sender, EventArgs e)
        {
            try
            {
                StopFileWatchers();  // 파일 감시 중단
                MessageBox.Show("파일 변화 감지가 중지되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일 감지 중단 중 오류 발생: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeFileWatchers()
        {
            // 기존 Watcher 정리
            foreach (var watcher in watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            watchers.Clear();

            // Target Folders 불러오기
            var targetFolders = GetFoldersFromSection("[TargetFolders]");
            var excludeFolders = GetFoldersFromSection("[ExcludeFolders]");

            foreach (var folder in targetFolders)
            {
                if (excludeFolders.Any(excluded => folder.StartsWith(excluded)))
                {
                    // 제외된 폴더는 감시하지 않음
                    continue;
                }

                // 파일 감시자 생성
                var watcher = new FileSystemWatcher
                {
                    Path = folder,
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };

                // 이벤트 핸들러 등록
                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.EnableRaisingEvents = true;

                watchers.Add(watcher);
            }
        }

        private void StopFileWatchers()
        {
            foreach (var watcher in watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            watchers.Clear();
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // 정규표현식과 매칭
                var regexList = GetRegexListFromSettings();
                foreach (var regexInfo in regexList)
                {
                    var regex = regexInfo.Key;
                    var targetFolder = regexInfo.Value;

                    if (Regex.IsMatch(e.Name, regex))
                    {
                        // 매칭된 파일 복사
                        var destinationPath = Path.Combine(targetFolder, Path.GetFileName(e.Name));
                        Directory.CreateDirectory(targetFolder); // 대상 폴더가 없으면 생성
                        File.Copy(e.FullPath, destinationPath, true); // 파일 복사
                        break; // 첫 번째 매칭 후 종료
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일 처리 중 오류 발생: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
