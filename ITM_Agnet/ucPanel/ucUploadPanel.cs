// ITM_Agent/ucPanel/ucUploadPanel.cs
using ITM_Agent.Plugins;
using ITM_Agent.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucUploadPanel : UserControl
    {
        private readonly ConcurrentQueue<Tuple<string, string, string>> uploadQueue = new ConcurrentQueue<Tuple<string, string, string>>();
        private readonly CancellationTokenSource ctsUpload = new CancellationTokenSource();
        private HashSet<string> prevPluginNames;

        private ucConfigurationPanel configPanel;
        private ucPluginPanel pluginPanel;
        private SettingsManager settingsManager;
        private LogManager logManager;
        private readonly ucOverrideNamesPanel overridePanel;
        private readonly ucImageTransPanel imageTransPanel;

        // 각 데이터 타입별 FileSystemWatcher 선언
        private FileSystemWatcher uploadFolderWatcher;
        private FileSystemWatcher preAlignFolderWatcher;
        private FileSystemWatcher errFolderWatcher;
        private FileSystemWatcher imageFolderWatcher;
        private FileSystemWatcher lampFolderWatcher;

        // Settings.ini 키 상수 선언
        private const string UploadSection = "UploadSetting";
        private const string UploadKey_WaferFlat = "WaferFlat";
        private const string UploadKey_PreAlign = "PreAlign";
        private const string UploadKey_Error = "Error";
        private const string UploadKey_Image = "Image";
        private const string UploadKey_Lamp = "Lamp"; // ▼▼▼ Lamp Data용 키 추가 ▼▼▼

        public ucUploadPanel(ucConfigurationPanel configPanel, ucPluginPanel pluginPanel, SettingsManager settingsManager,
            ucOverrideNamesPanel ovPanel, ucImageTransPanel imageTransPanel)
        {
            InitializeComponent();

            this.configPanel = configPanel ?? throw new ArgumentNullException(nameof(configPanel));
            this.pluginPanel = pluginPanel ?? throw new ArgumentNullException(nameof(pluginPanel));
            this.settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            this.overridePanel = ovPanel;
            this.imageTransPanel = imageTransPanel ?? throw new ArgumentNullException(nameof(imageTransPanel));

            logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);
            this.pluginPanel.PluginsChanged += PluginPanel_PluginsChanged;

            // 기존 버튼 이벤트 핸들러
            btn_FlatSet.Click += btn_FlatSet_Click;
            btn_FlatClear.Click += btn_FlatClear_Click;
            btn_PreAlignSet.Click += btn_PreAlignSet_Click;
            btn_PreAlignClear.Click += btn_PreAlignClear_Click;
            btn_ErrSet.Click += btn_ErrSet_Click;
            btn_ErrClear.Click += btn_ErrClear_Click;
            btn_ImgSet.Click += btn_ImgSet_Click;
            btn_ImgClear.Click += btn_ImgClear_Click;

            // ▼▼▼ Lamp Data용 버튼 이벤트 핸들러 연결 ▼▼▼
            btn_LampSet.Click += btn_LampSet_Click;
            btn_LampClear.Click += btn_LampClear_Click;

            this.Load += UcUploadPanel_Load;

            LoadTargetFolderItems(); // ImagePath 로드 로직은 여기서 제거됨
            LoadImageSaveFolder_PathChanged(); // ▼▼▼ ImageTransPanel로부터 경로를 가져오는 메서드 호출 ▼▼▼
            LoadPluginItems();

            prevPluginNames = new HashSet<string>(
                pluginPanel.GetLoadedPlugins().Select(p => p.PluginName),
                StringComparer.OrdinalIgnoreCase);

            LoadUploadSettings();

            Task.Run(() => ConsumeUploadQueueAsync(ctsUpload.Token));
        }

        /// <summary>
        /// ImageTransPanel의 저장 폴더가 변경될 때 호출되는 이벤트 핸들러입니다.
        /// </summary>
        public void LoadImageSaveFolder_PathChanged()
        {
            cb_ImgPath.Items.Clear();
            string imageSaveFolder = this.imageTransPanel.GetImageSaveFolder();

            if (!string.IsNullOrEmpty(imageSaveFolder))
            {
                cb_ImgPath.Items.Add(imageSaveFolder);
                cb_ImgPath.SelectedIndex = 0; // 자동으로 선택
                cb_ImgPath.Enabled = false;   // ▼▼▼ 사용자가 직접 수정하지 못하도록 비활성화 ▼▼▼
            }
            else
            {
                cb_ImgPath.Enabled = false; // 경로가 없으면 비활성화
            }
        }

        private void LoadUploadSettings()
        {
            string flatLine = settingsManager.GetValueFromSection(UploadSection, UploadKey_WaferFlat);
            if (!string.IsNullOrWhiteSpace(flatLine))
                RestoreUploadSetting("WaferFlat", flatLine);

            string preLine = settingsManager.GetValueFromSection(UploadSection, UploadKey_PreAlign);
            if (!string.IsNullOrWhiteSpace(preLine))
                RestoreUploadSetting("PreAlign", preLine);

            string errLine = settingsManager.GetValueFromSection(UploadSection, UploadKey_Error);
            if (!string.IsNullOrWhiteSpace(errLine))
                RestoreUploadSetting("Error", errLine);

            // ▼▼▼ PDF 이미지 설정 로드 추가 ▼▼▼
            string imgLine = settingsManager.GetValueFromSection(UploadSection, UploadKey_Image);
            if (!string.IsNullOrWhiteSpace(imgLine)) RestoreUploadSetting("Image", imgLine);

            // ▼▼▼ Lamp Data 설정 로드 추가 ▼▼▼
            string lampLine = settingsManager.GetValueFromSection(UploadSection, UploadKey_Lamp);
            if (!string.IsNullOrWhiteSpace(lampLine)) RestoreUploadSetting("Lamp", lampLine);
        }

        private void RestoreUploadSetting(string itemName, string valueLine)
        {
            string[] parts = valueLine.Split(',');
            if (parts.Length < 2) return;

            int colonFolder = parts[0].IndexOf(':');
            string folderPath = colonFolder >= 0 ? parts[0].Substring(colonFolder + 1).Trim() : string.Empty;

            int colonPlugin = parts[1].IndexOf(':');
            string pluginName = colonPlugin >= 0 ? parts[1].Substring(colonPlugin + 1).Trim() : string.Empty;

            if (itemName == "WaferFlat")
            {
                AddPathToCombo(cb_WaferFlat_Path, folderPath);
                AddPathToCombo(cb_FlatPlugin, pluginName);
                StartUploadFolderWatcher(NormalizePath(folderPath));
            }
            else if (itemName == "PreAlign")
            {
                AddPathToCombo(cb_PreAlign_Path, folderPath);
                AddPathToCombo(cb_PreAlignPlugin, pluginName);
                StartPreAlignFolderWatcher(NormalizePath(folderPath));
            }
            else if (itemName == "Error")
            {
                AddPathToCombo(cb_ErrPath, folderPath);
                AddPathToCombo(cb_ErrPlugin, pluginName);
                StartErrFolderWatcher(NormalizePath(folderPath));
            }
            else if (itemName == "Image")
            {
                // Image 경로는 ImageTransPanel에서 받아오므로, 여기서는 플러그인만 설정
                // AddPathToCombo(cb_ImgPath, folderPath); 
                AddPathToCombo(cb_ImagePlugin, pluginName);
                StartImageFolderWatcher(NormalizePath(folderPath));
            }
            else if (itemName == "Lamp")
            {
                AddPathToCombo(cb_LampPath, folderPath); 
                AddPathToCombo(cb_LampPlugin, pluginName);
                StartLampFolderWatcher(NormalizePath(folderPath));
            }
        }

        private void LoadTargetFolderItems()
        {
            cb_WaferFlat_Path.Items.Clear();
            cb_PreAlign_Path.Items.Clear();
            cb_ErrPath.Items.Clear();
            cb_LampPath.Items.Clear();

            IEnumerable<string> folders;
            if (configPanel != null)
            {
                folders = configPanel.GetTargetFolders();
            }
            else
            {
                folders = settingsManager.GetFoldersFromSection("[TargetFolders]");
            }

            var arr = folders.ToArray();
            cb_WaferFlat_Path.Items.AddRange(arr);
            cb_PreAlign_Path.Items.AddRange(arr);
            cb_ErrPath.Items.AddRange(arr);
            cb_LampPath.Items.AddRange(arr);

            DeduplicateComboItems(cb_WaferFlat_Path);
            DeduplicateComboItems(cb_PreAlign_Path);
            DeduplicateComboItems(cb_ErrPath);
            DeduplicateComboItems(cb_LampPath);
        }

        private void LoadPluginItems()
        {
            cb_FlatPlugin.Items.Clear();
            cb_PreAlignPlugin.Items.Clear();
            cb_ErrPlugin.Items.Clear();
            cb_ImagePlugin.Items.Clear();
            cb_LampPlugin.Items.Clear();

            if (pluginPanel == null) return;

            foreach (var p in pluginPanel.GetLoadedPlugins())
            {
                cb_FlatPlugin.Items.Add(p.PluginName);
                cb_PreAlignPlugin.Items.Add(p.PluginName);
                cb_ErrPlugin.Items.Add(p.PluginName);
                cb_ImagePlugin.Items.Add(p.PluginName);
                cb_LampPlugin.Items.Add(p.PluginName);
            }
        }

        private void StartUploadFolderWatcher(string folderPath)
        {
            try
            {
                folderPath = folderPath.Trim();
                if (string.IsNullOrEmpty(folderPath)) throw new ArgumentException("폴더 경로가 비어 있습니다.", nameof(folderPath));
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                uploadFolderWatcher?.Dispose();
                uploadFolderWatcher = new FileSystemWatcher(folderPath)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                uploadFolderWatcher.Created += UploadFolderWatcher_Event;
                logManager.LogEvent($"[UploadPanel] Wafer-Flat 폴더 감시 시작: {folderPath}");
            }
            catch (Exception ex)
            {
                logManager.LogError($"[UploadPanel] Wafer-Flat 폴더 감시 시작 실패: {ex.Message}");
            }
        }

        private void UploadFolderWatcher_Event(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(200);
            string pluginName = string.Empty;

            if (InvokeRequired)
                Invoke(new MethodInvoker(() => pluginName = cb_FlatPlugin.Text.Trim()));
            else
                pluginName = cb_FlatPlugin.Text.Trim();

            if (string.IsNullOrEmpty(pluginName))
            {
                logManager.LogEvent($"[UploadPanel] Wafer-Flat 플러그인이 설정되지 않아 처리를 건너뜁니다: {e.FullPath}");
                return;
            }
            // [수정] 데이터 타입("WaferFlat")을 명시적으로 큐에 추가
            uploadQueue.Enqueue(new Tuple<string, string, string>(e.FullPath, pluginName, "WaferFlat"));
            logManager.LogEvent($"[UploadPanel] 대기 큐에 추가 (WaferFlat): {e.FullPath}");
        }

        private void PreAlignFolderWatcher_Event(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(200);
            string pluginName = string.Empty;

            if (InvokeRequired)
                Invoke(new MethodInvoker(() => pluginName = cb_PreAlignPlugin.Text.Trim()));
            else
                pluginName = cb_PreAlignPlugin.Text.Trim();

            if (string.IsNullOrEmpty(pluginName))
            {
                logManager.LogEvent($"[UploadPanel] Pre-Align 플러그인이 설정되지 않아 처리를 건너뜁니다: {e.FullPath}");
                return;
            }
            // [수정] 데이터 타입("PreAlign")을 명시적으로 큐에 추가
            uploadQueue.Enqueue(new Tuple<string, string, string>(e.FullPath, pluginName, "PreAlign"));
            logManager.LogEvent($"[UploadPanel] (Pre-Align) 대기 큐 추가 : {e.FullPath}");
        }

        private void ErrFolderWatcher_Event(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(200);
            string pluginName = string.Empty;

            if (InvokeRequired)
                Invoke(new MethodInvoker(() => pluginName = cb_ErrPlugin.Text.Trim()));
            else
                pluginName = cb_ErrPlugin.Text.Trim();

            if (string.IsNullOrEmpty(pluginName))
            {
                logManager.LogEvent($"[UploadPanel] Error 플러그인이 설정되지 않아 처리를 건너뜁니다: {e.FullPath}");
                return;
            }
            // [수정] 데이터 타입("Error")을 명시적으로 큐에 추가
            uploadQueue.Enqueue(new Tuple<string, string, string>(e.FullPath, pluginName, "Error"));
            logManager.LogEvent($"[UploadPanel] (Error) 대기 큐 추가 : {e.FullPath}");
        }

        // ▼▼▼ PDF 이미지용 Watcher Event 및 Start 메서드 추가 ▼▼▼
        private void StartImageFolderWatcher(string folderPath)
        {
            try
            {
                folderPath = folderPath.Trim();
                if (string.IsNullOrEmpty(folderPath)) throw new ArgumentException("폴더 경로가 비어 있습니다.", nameof(folderPath));
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                imageFolderWatcher?.Dispose();
                imageFolderWatcher = new FileSystemWatcher(folderPath)
                {
                    Filter = "*.pdf", // PDF 파일만 감시
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                imageFolderWatcher.Created += ImageFolderWatcher_Event;
                logManager.LogEvent($"[UploadPanel] Image (PDF) 폴더 감시 시작: {folderPath}");
            }
            catch (Exception ex)
            {
                logManager.LogError($"[UploadPanel] Image (PDF) 폴더 감시 시작 실패: {ex.Message}");
            }
        }

        private void ImageFolderWatcher_Event(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(200);
            string pluginName = string.Empty;

            if (InvokeRequired)
                Invoke(new MethodInvoker(() => pluginName = cb_ImagePlugin.Text.Trim()));
            else
                pluginName = cb_ImagePlugin.Text.Trim();

            if (string.IsNullOrEmpty(pluginName))
            {
                logManager.LogEvent($"[UploadPanel] Image 플러그인이 설정되지 않아 처리를 건너뜁니다: {e.FullPath}");
                return;
            }
            uploadQueue.Enqueue(new Tuple<string, string, string>(e.FullPath, pluginName, "Image"));
            logManager.LogEvent($"[UploadPanel] 대기 큐에 추가 (Image): {e.FullPath}");
        }

        // ▼▼▼ Lamp Data용 Watcher Event 및 Start 메서드 추가 ▼▼▼
        private void StartLampFolderWatcher(string folderPath)
        {
            try
            {
                folderPath = folderPath.Trim();
                if (string.IsNullOrEmpty(folderPath)) return;
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                lampFolderWatcher?.Dispose();
                lampFolderWatcher = new FileSystemWatcher(folderPath)
                {
                    Filter = "*.*", // 필요시 "*.log" 등으로 필터링 가능
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                lampFolderWatcher.Created += LampFolderWatcher_Event;
                lampFolderWatcher.Changed += LampFolderWatcher_Event; // 파일이 완전히 쓰여진 후를 위해 Changed도 감지
                logManager.LogEvent($"[UploadPanel] Lamp Data 폴더 감시 시작: {folderPath}");
            }
            catch (Exception ex)
            {
                logManager.LogError($"[UploadPanel] Lamp Data 폴더 감시 시작 실패: {ex.Message}");
            }
        }

        private void LampFolderWatcher_Event(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(200); // 파일 쓰기 완료 대기
            string pluginName = string.Empty;

            if (InvokeRequired)
                Invoke(new MethodInvoker(() => pluginName = cb_LampPlugin.Text.Trim()));
            else
                pluginName = cb_LampPlugin.Text.Trim();

            if (string.IsNullOrEmpty(pluginName))
            {
                logManager.LogEvent($"[UploadPanel] Lamp Data 플러그인이 설정되지 않아 처리를 건너뜁니다: {e.FullPath}");
                return;
            }
            uploadQueue.Enqueue(new Tuple<string, string, string>(e.FullPath, pluginName, "Lamp"));
            logManager.LogEvent($"[UploadPanel] 대기 큐에 추가 (Lamp): {e.FullPath}");
        }

        private async Task ConsumeUploadQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!uploadQueue.TryDequeue(out var job))
                    {
                        await Task.Delay(300, token);
                        continue;
                    }

                    string rawPath = job.Item1;
                    string pluginName = job.Item2;
                    string dataType = job.Item3;
                    string readyPath = rawPath;

                    // ▼▼▼ WaferFlat 데이터 타입일 때만 Override 로직 실행 ▼▼▼
                    if (dataType == "WaferFlat")
                    {
                        readyPath = overridePanel?.EnsureOverrideAndReturnPath(rawPath, 3000) ?? rawPath;
                    }
                    // ▼▼▼ Lamp Data는 파일명 변경 로직을 건너뛰도록 수정 ▼▼▼
                    else if (dataType == "Lamp" || dataType == "PreAlign" || dataType == "Error" || dataType == "Image")
                    {
                        // Override(rename) 로직이 필요 없는 데이터 타입들
                        logManager.LogEvent($"[UploadPanel] Override(rename) step skipped for {pluginName} (DataType: {dataType}).");
                    }

                    var pluginItem = pluginPanel.GetLoadedPlugins()
                        .FirstOrDefault(p => p.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase));

                    if (pluginItem == null || !File.Exists(pluginItem.AssemblyPath))
                    {
                        logManager.LogError($"[UploadPanel] DLL을 찾을 수 없습니다: {pluginName}");
                        continue;
                    }

                    if (!TryRunProcessAndUpload(pluginItem.AssemblyPath, readyPath, out string err))
                    {
                        logManager.LogError($"[UploadPanel] 업로드 실패: {Path.GetFileName(readyPath)} - ({pluginName}) - {err}");
                    }
                    else
                    {
                        logManager.LogEvent($"[UploadPanel] ({pluginName}) 업로드 완료 : {readyPath}");
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[UploadPanel] 소비자 Task 오류: {ex.GetBaseException().Message}");
                }
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.ToUpperInvariant();
        }

        private void AddPathToCombo(ComboBox combo, string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return;
            string normNew = NormalizePath(rawPath);
            if (!combo.Items.Cast<string>().Any(p => NormalizePath(p) == normNew))
                combo.Items.Add(rawPath);
            combo.SelectedItem = combo.Items.Cast<string>().First(p => NormalizePath(p) == normNew);
        }

        private void DeduplicateComboItems(ComboBox combo)
        {
            var uniques = combo.Items.Cast<string>().GroupBy(NormalizePath).Select(g => g.First()).ToList();
            combo.Items.Clear();
            combo.Items.AddRange(uniques.ToArray());
        }

        private void UcUploadPanel_Load(object sender, EventArgs e)
        {
            DeduplicateComboItems(cb_WaferFlat_Path);
            DeduplicateComboItems(cb_PreAlign_Path);
            DeduplicateComboItems(cb_ErrPath);
            DeduplicateComboItems(cb_ImgPath); // ▼▼▼ 중복 제거 추가 ▼▼▼
        }

        private void btn_FlatSet_Click(object sender, EventArgs e)
        {
            string rawFolder = cb_WaferFlat_Path.Text.Trim();
            string rawPlugin = cb_FlatPlugin.Text.Trim();
            if (string.IsNullOrEmpty(rawFolder) || string.IsNullOrEmpty(rawPlugin))
            {
                MessageBox.Show("Wafer-Flat 폴더와 플러그인을 모두 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Directory.Exists(rawFolder))
            {
                MessageBox.Show("존재하지 않는 폴더입니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string normalizedFolder = NormalizePath(rawFolder);
            string iniValue = $"Folder : {normalizedFolder}, Plugin : {rawPlugin}";
            settingsManager.SetValueToSection(UploadSection, UploadKey_WaferFlat, iniValue);
            AddPathToCombo(cb_WaferFlat_Path, rawFolder);
            AddPathToCombo(cb_FlatPlugin, rawPlugin);
            DeduplicateComboItems(cb_WaferFlat_Path);
            StartUploadFolderWatcher(normalizedFolder);
            MessageBox.Show("Wafer-Flat 설정이 저장되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btn_FlatClear_Click(object sender, EventArgs e)
        {
            cb_WaferFlat_Path.SelectedIndex = -1;
            cb_WaferFlat_Path.Text = string.Empty;
            cb_FlatPlugin.SelectedIndex = -1;
            cb_FlatPlugin.Text = string.Empty;
            if (uploadFolderWatcher != null)
            {
                uploadFolderWatcher.EnableRaisingEvents = false;
                uploadFolderWatcher.Dispose();
                uploadFolderWatcher = null;
            }
            settingsManager.RemoveKeyFromSection(UploadSection, UploadKey_WaferFlat);
            MessageBox.Show("Wafer-Flat 설정이 초기화되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ▼▼▼ PDF 이미지용 버튼 클릭 이벤트 핸들러 구현 ▼▼▼
        private void btn_ImgSet_Click(object sender, EventArgs e)
        {
            string rawFolder = cb_ImgPath.Text.Trim();
            string rawPlugin = cb_ImagePlugin.Text.Trim();
            if (string.IsNullOrEmpty(rawFolder) || string.IsNullOrEmpty(rawPlugin))
            {
                MessageBox.Show("Image Data 폴더와 플러그인을 모두 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Directory.Exists(rawFolder))
            {
                MessageBox.Show("존재하지 않는 폴더입니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string normalizedFolder = NormalizePath(rawFolder);
            string iniValue = $"Folder : {normalizedFolder}, Plugin : {rawPlugin}";
            settingsManager.SetValueToSection(UploadSection, UploadKey_Image, iniValue);
            AddPathToCombo(cb_ImgPath, rawFolder);
            AddPathToCombo(cb_ImagePlugin, rawPlugin);
            DeduplicateComboItems(cb_ImgPath);
            StartImageFolderWatcher(normalizedFolder);
            MessageBox.Show("Image Data 설정이 저장되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btn_ImgClear_Click(object sender, EventArgs e)
        {
            cb_ImgPath.SelectedIndex = -1;
            cb_ImgPath.Text = string.Empty;
            cb_ImagePlugin.SelectedIndex = -1;
            cb_ImagePlugin.Text = string.Empty;
            imageFolderWatcher?.Dispose();
            imageFolderWatcher = null;
            settingsManager.RemoveKeyFromSection(UploadSection, UploadKey_Image);
            MessageBox.Show("Image Data 설정이 초기화되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ▼▼▼ Lamp Data용 버튼 클릭 이벤트 핸들러 구현 ▼▼▼
        private void btn_LampSet_Click(object sender, EventArgs e)
        {
            string rawFolder = cb_LampPath.Text.Trim();
            string rawPlugin = cb_LampPlugin.Text.Trim();
            if (string.IsNullOrEmpty(rawFolder) || string.IsNullOrEmpty(rawPlugin))
            {
                MessageBox.Show("Lamp Data 폴더와 플러그인을 모두 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Directory.Exists(rawFolder))
            {
                MessageBox.Show("존재하지 않는 폴더입니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string normalizedFolder = NormalizePath(rawFolder);
            string iniValue = $"Folder : {normalizedFolder}, Plugin : {rawPlugin}";
            settingsManager.SetValueToSection(UploadSection, UploadKey_Lamp, iniValue);
            AddPathToCombo(cb_LampPath, rawFolder);
            AddPathToCombo(cb_LampPlugin, rawPlugin);
            DeduplicateComboItems(cb_LampPath);
            StartLampFolderWatcher(normalizedFolder);
            MessageBox.Show("Lamp Data 설정이 저장되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btn_LampClear_Click(object sender, EventArgs e)
        {
            cb_LampPath.SelectedIndex = -1;
            cb_LampPath.Text = string.Empty;
            cb_LampPlugin.SelectedIndex = -1;
            cb_LampPlugin.Text = string.Empty;
            lampFolderWatcher?.Dispose();
            lampFolderWatcher = null;
            settingsManager.RemoveKeyFromSection(UploadSection, UploadKey_Lamp);
            MessageBox.Show("Lamp Data 설정이 초기화되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void PluginPanel_PluginsChanged(object sender, EventArgs e)
        {
            RefreshPluginCombo();
        }

        private void RefreshPluginCombo()
        {
            var currentPlugins = pluginPanel.GetLoadedPlugins().Select(p => p.PluginName).ToList();

            var removedPlugins = prevPluginNames.Except(currentPlugins, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string removed in removedPlugins)
            {
                RemovePluginReferences(removed);
            }

            ComboBox[] targets = { cb_FlatPlugin, cb_PreAlignPlugin, cb_ErrPlugin, cb_ImagePlugin, cb_LampPlugin };
            foreach (var cb in targets)
            {
                string previouslySelected = cb.Text;
                cb.BeginUpdate();
                cb.Items.Clear();
                cb.Items.AddRange(currentPlugins.ToArray());
                if (currentPlugins.Contains(previouslySelected))
                {
                    cb.SelectedItem = previouslySelected;
                }
                else
                {
                    cb.SelectedIndex = -1;
                }
                cb.EndUpdate();
            }

            prevPluginNames = new HashSet<string>(currentPlugins, StringComparer.OrdinalIgnoreCase);
        }

        private void RemovePluginReferences(string pluginName)
        {
            if (string.Equals(cb_FlatPlugin.Text, pluginName, StringComparison.OrdinalIgnoreCase)) btn_FlatClear_Click(this, EventArgs.Empty);
            if (string.Equals(cb_PreAlignPlugin.Text, pluginName, StringComparison.OrdinalIgnoreCase)) btn_PreAlignClear_Click(this, EventArgs.Empty);
            if (string.Equals(cb_ErrPlugin.Text, pluginName, StringComparison.OrdinalIgnoreCase)) btn_ErrClear_Click(this, EventArgs.Empty);
            if (string.Equals(cb_ImagePlugin.Text, pluginName, StringComparison.OrdinalIgnoreCase)) btn_ImgClear_Click(this, EventArgs.Empty);
            if (string.Equals(cb_LampPlugin.Text, pluginName, StringComparison.OrdinalIgnoreCase)) btn_LampClear_Click(this, EventArgs.Empty);
        }

        private bool TryRunProcessAndUpload(string dllPath, string readyPath, out string err)
        {
            err = null;
            try
            {
                byte[] dllBytes = File.ReadAllBytes(dllPath);
                Assembly asm = Assembly.Load(dllBytes);
                Type targetType = asm.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == "ProcessAndUpload"));
                if (targetType == null) { err = "ProcessAndUpload() 메서드를 가진 타입 없음"; return false; }

                object pluginObj = Activator.CreateInstance(targetType);

                MethodInfo mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(string) }) ??
                                targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(object), typeof(object) }) ??
                                targetType.GetMethod("ProcessAndUpload", new[] { typeof(string) });

                if (mi == null) { err = "호출 가능한 ProcessAndUpload() 오버로드 없음"; return false; }

                object[] args;
                var parameters = mi.GetParameters();
                if (parameters.Length >= 2 && (parameters[1].ParameterType == typeof(string) || parameters[1].ParameterType == typeof(object)))
                {
                    args = new object[] { readyPath, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini"), null };
                    // 3개 파라미터 메서드를 위해 인자 개수 조정
                    if (parameters.Length == 2)
                    {
                        Array.Resize(ref args, 2);
                    }
                }
                else
                {
                    args = new object[] { readyPath };
                }

                mi.Invoke(pluginObj, args);
                return true;
            }
            catch (Exception ex)
            {
                err = ex.GetBaseException().Message;
                return false;
            }
        }

        public void UpdateStatusOnRun(bool isRunning)
        {
            SetControlsEnabled(!isRunning);
        }

        private void SetControlsEnabled(bool enabled)
        {
            btn_FlatSet.Enabled = enabled;
            btn_FlatClear.Enabled = enabled;
            btn_PreAlignSet.Enabled = enabled;
            btn_PreAlignClear.Enabled = enabled;
            // ▼▼▼ PDF 이미지용 컨트롤 활성화/비활성화 추가 ▼▼▼
            btn_ImgSet.Enabled = enabled;
            btn_ImgClear.Enabled = enabled;
            btn_ErrSet.Enabled = enabled;
            btn_ErrClear.Enabled = enabled;
            btn_LampSet.Enabled = enabled;
            btn_LampClear.Enabled = enabled;
            btn_WaveSet.Enabled = enabled;
            btn_WaveClear.Enabled = enabled;
            cb_WaferFlat_Path.Enabled = enabled;
            cb_PreAlign_Path.Enabled = enabled;
            cb_ImgPath.Enabled = enabled;
            cb_ErrPath.Enabled = enabled;
            cb_LampPath.Enabled = enabled;
            cb_WavePath.Enabled = enabled;
            cb_FlatPlugin.Enabled = enabled;
            cb_PreAlignPlugin.Enabled = enabled;
            cb_ImagePlugin.Enabled = enabled;
            cb_ErrPlugin.Enabled = enabled;
            cb_LampPlugin.Enabled = enabled;
            cb_WavePlugin.Enabled = enabled;
        }

        private void btn_PreAlignSet_Click(object sender, EventArgs e)
        {
            string rawFolder = cb_PreAlign_Path.Text.Trim();
            string rawPlugin = cb_PreAlignPlugin.Text.Trim();
            if (string.IsNullOrEmpty(rawFolder) || string.IsNullOrEmpty(rawPlugin))
            {
                MessageBox.Show("Pre-Align 폴더와 플러그인을 모두 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Directory.Exists(rawFolder))
            {
                MessageBox.Show("존재하지 않는 폴더입니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string normalizedFolder = NormalizePath(rawFolder);
            string iniValue = $"Folder : {normalizedFolder}, Plugin : {rawPlugin}";
            settingsManager.SetValueToSection(UploadSection, UploadKey_PreAlign, iniValue);
            AddPathToCombo(cb_PreAlign_Path, rawFolder);
            AddPathToCombo(cb_PreAlignPlugin, rawPlugin);
            DeduplicateComboItems(cb_PreAlign_Path);
            StartPreAlignFolderWatcher(normalizedFolder);
            MessageBox.Show("Pre-Align 설정이 저장되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btn_PreAlignClear_Click(object sender, EventArgs e)
        {
            cb_PreAlign_Path.SelectedIndex = -1;
            cb_PreAlign_Path.Text = string.Empty;
            cb_PreAlignPlugin.SelectedIndex = -1;
            cb_PreAlignPlugin.Text = string.Empty;
            preAlignFolderWatcher?.Dispose();
            preAlignFolderWatcher = null;
            settingsManager.RemoveKeyFromSection(UploadSection, UploadKey_PreAlign);
            MessageBox.Show("Pre-Align 설정이 초기화되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void StartPreAlignFolderWatcher(string folderPath)
        {
            try
            {
                folderPath = folderPath.Trim();
                if (string.IsNullOrEmpty(folderPath)) return;
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
                preAlignFolderWatcher?.Dispose();
                preAlignFolderWatcher = new FileSystemWatcher(folderPath)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                preAlignFolderWatcher.Created += PreAlignFolderWatcher_Event;
                preAlignFolderWatcher.Changed += PreAlignFolderWatcher_Event;
                logManager.LogEvent($"[UploadPanel] Pre-Align 폴더 감시 시작: {folderPath}");
            }
            catch (Exception ex)
            {
                logManager.LogError($"[UploadPanel] Pre-Align 폴더 감시 시작 실패: {ex.Message}");
            }
        }

        private void btn_ErrSet_Click(object sender, EventArgs e)
        {
            string rawFolder = cb_ErrPath.Text.Trim();
            string rawPlugin = cb_ErrPlugin.Text.Trim();
            if (string.IsNullOrEmpty(rawFolder) || string.IsNullOrEmpty(rawPlugin))
            {
                MessageBox.Show("Error 폴더와 플러그인을 모두 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Directory.Exists(rawFolder))
            {
                MessageBox.Show("존재하지 않는 폴더입니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string normalizedFolder = NormalizePath(rawFolder);
            string iniValue = $"Folder : {normalizedFolder}, Plugin : {rawPlugin}";
            settingsManager.SetValueToSection(UploadSection, UploadKey_Error, iniValue);
            AddPathToCombo(cb_ErrPath, rawFolder);
            AddPathToCombo(cb_ErrPlugin, rawPlugin);
            DeduplicateComboItems(cb_ErrPath);
            StartErrFolderWatcher(normalizedFolder);
            MessageBox.Show("Error 설정이 저장되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btn_ErrClear_Click(object sender, EventArgs e)
        {
            cb_ErrPath.SelectedIndex = -1;
            cb_ErrPath.Text = string.Empty;
            cb_ErrPlugin.SelectedIndex = -1;
            cb_ErrPlugin.Text = string.Empty;
            errFolderWatcher?.Dispose();
            errFolderWatcher = null;
            settingsManager.RemoveKeyFromSection(UploadSection, UploadKey_Error);
            MessageBox.Show("Error 설정이 초기화되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void StartErrFolderWatcher(string folderPath)
        {
            try
            {
                folderPath = folderPath.Trim();
                if (string.IsNullOrEmpty(folderPath)) return;
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
                errFolderWatcher?.Dispose();
                errFolderWatcher = new FileSystemWatcher(folderPath)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                errFolderWatcher.Created += ErrFolderWatcher_Event;
                errFolderWatcher.Changed += ErrFolderWatcher_Event;
                logManager.LogEvent($"[UploadPanel] Error 폴더 감시 시작: {folderPath}");
            }
            catch (Exception ex)
            {
                logManager.LogError($"[UploadPanel] Error 폴더 감시 시작 실패: {ex.Message}");
            }
        }
    }
}
