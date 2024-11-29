using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucScreen1 : UserControl
    {
        public event Action<bool> RunButtonStateChanged; // btn_Run 상태 변경 알림 이벤트
        private string settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini");
        private string baseFolder;
        private const string TargetFoldersSection = "[TargetFolders]";
        private const string ExcludeFoldersSection = "[ExcludeFolders]";
        private const string RegexSection = "[Regex]";
        
        private List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        public ucScreen1()
        {
            InitializeComponent();
            EnsureSettingsFileExists();
            LoadFolders(TargetFoldersSection, lb_TargetList);
            LoadFolders(ExcludeFoldersSection, lb_ExcludeList);
            LoadBaseFolder();
            InitializeFileWatchers();

            // 이벤트 핸들러 추가
            btn_TargetFolder.Click += (sender, e) => AddFolder(TargetFoldersSection, lb_TargetList);
            btn_TargetRemove.Click += (sender, e) => RemoveFolders(TargetFoldersSection, lb_TargetList);

            btn_ExcludeFolder.Click += (sender, e) => AddFolder(ExcludeFoldersSection, lb_ExcludeList);
            btn_ExcludeRemove.Click += (sender, e) => RemoveFolders(ExcludeFoldersSection, lb_ExcludeList);
            
            btn_BaseFolder.Click += btn_BaseFolder_Click;
            
            // 이벤트 핸들러 연결
            btn_RegAdd.Click += btn_RegAdd_Click;
            btn_RegEdit.Click += btn_RegEdit_Click;
            btn_RegRemove.Click += btn_RegRemove_Click;

            // 설정 파일에서 기존 Regex 로드
            LoadRegexFromSettings();
            
            // 컨트롤 변경 이벤트 연결
            lb_TargetList.SelectedIndexChanged += ValidateRunButtonState;
            lb_RegexList.SelectedIndexChanged += ValidateRunButtonState;
            lb_BaseFolder.TextChanged += ValidateRunButtonState;
        }
        
        private void ValidateRunButtonState(object sender, EventArgs e)
        {
            // 조건 확인
            bool hasTargetFolders = lb_TargetList.Items.Count > 0; // Target Folders 지정 여부
            bool hasBaseFolder = !string.IsNullOrEmpty(lb_BaseFolder.Text) && lb_BaseFolder.Text != "폴더가 미선택되었습니다"; // Base Folder 지정 여부
            bool hasRegexPatterns = lb_RegexList.Items.Count > 0; // Regex 지정 여부

            // 조건이 모두 충족되었는지 확인
            bool isRunButtonEnabled = hasTargetFolders && hasBaseFolder && hasRegexPatterns;

            // 상태 변경 이벤트 호출
            RunButtonStateChanged?.Invoke(isRunButtonEnabled);
        }
        
        private void LoadBaseFolder()
        {
            if (File.Exists(settingsFilePath))
            {
                var lines = File.ReadAllLines(settingsFilePath);
                bool inBaseFolderSection = false;

                foreach (var line in lines)
                {
                    if (line.Trim() == "[BaseFolder]")
                    {
                        inBaseFolderSection = true;
                        continue;
                    }

                    if (inBaseFolderSection)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("["))
                            break;

                        // BaseFolder 값 설정
                        baseFolder = line.Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(baseFolder))
            {
                // BaseFolder 값이 없으면 미선택 메시지 표시
                lb_BaseFolder.Text = "폴더가 미선택되었습니다";
                lb_BaseFolder.ForeColor = System.Drawing.Color.Red;
            }
            else
            {
                // BaseFolder 값 표시
                lb_BaseFolder.Text = baseFolder;
                lb_BaseFolder.ForeColor = System.Drawing.Color.Black;
            }
        }

        private void EnsureSettingsFileExists()
        {
            if (!File.Exists(settingsFilePath))
            {
                using (var writer = new StreamWriter(settingsFilePath, false))
                {
                    writer.WriteLine(TargetFoldersSection);
                    writer.WriteLine();
                    writer.WriteLine(ExcludeFoldersSection);
                }
            }
            else
            {
                var lines = File.ReadAllLines(settingsFilePath).ToList();
                if (!lines.Contains(TargetFoldersSection))
                {
                    lines.Add("");
                    lines.Add(TargetFoldersSection);
                }
                if (!lines.Contains(ExcludeFoldersSection))
                {
                    lines.Add("");
                    lines.Add(ExcludeFoldersSection);
                }
                File.WriteAllLines(settingsFilePath, lines);
            }
        }

        private void LoadFolders(string section, ListBox listBox)
        {
            listBox.Items.Clear();

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
                        {
                            break;
                        }

                        AddFolderToList(line.Trim(), listBox);
                    }
                }

                ReorderFolders(listBox);
            }
        }

        private void AddFolder(string section, ListBox listBox)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedFolder = folderDialog.SelectedPath;
        
                    if (!IsFolderAlreadyAdded(selectedFolder, listBox))
                    {
                        AddFolderToList(selectedFolder, listBox);
                        SaveAllFoldersToSettings(section, listBox); // 항목 추가 후 저장
                    }
                    else
                    {
                        MessageBox.Show("해당 폴더는 이미 등록되어 있습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }

        private void RemoveFolders(string section, ListBox listBox)
        {
            var selectedItems = listBox.SelectedItems.Cast<string>().ToList();
        
            if (selectedItems.Count > 0)
            {
                var confirmResult = MessageBox.Show("선택한 폴더를 정말 삭제하시겠습니까?", "삭제 확인",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        
                if (confirmResult == DialogResult.Yes)
                {
                    foreach (var item in selectedItems)
                    {
                        RemoveFolderFromList(item, listBox);
                    }
        
                    ReorderFolders(listBox);
                    SaveAllFoldersToSettings(section, listBox); // 설정 파일에 변경 사항 저장
                }
            }
            else
            {
                MessageBox.Show("삭제할 폴더를 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void AddFolderToList(string folderPath, ListBox listBox)
        {
            listBox.Items.Add($"{listBox.Items.Count + 1} {folderPath}");
        }

        private void RemoveFolderFromList(string item, ListBox listBox)
        {
            int spaceIndex = item.IndexOf(' ');
            if (spaceIndex != -1)
            {
                string folderPath = item.Substring(spaceIndex + 1);
                listBox.Items.Remove(item);
            }
        }

        private void ReorderFolders(ListBox listBox)
        {
            var items = listBox.Items.Cast<string>().ToList();
            listBox.Items.Clear();

            int index = 1;
            foreach (var item in items)
            {
                int spaceIndex = item.IndexOf(' ');
                if (spaceIndex != -1)
                {
                    string folderPath = item.Substring(spaceIndex + 1);
                    listBox.Items.Add($"{index} {folderPath}");
                    index++;
                }
            }
        }

        private bool IsFolderAlreadyAdded(string folderPath, ListBox listBox)
        {
            return listBox.Items.Cast<string>().Any(item => item.Contains(folderPath));
        }
        
        private void SaveAllFoldersToSettings(string section, ListBox listBox)
        {
            var lines = File.Exists(settingsFilePath) ? File.ReadAllLines(settingsFilePath).ToList() : new List<string>();
        
            // 섹션이 존재하는지 확인
            int sectionIndex = lines.FindIndex(line => line.Trim() == section);
            if (sectionIndex == -1)
            {
                // 섹션이 없으면 새로 추가
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                {
                    lines.Add(""); // 마지막 줄에 빈 줄 추가
                }
                lines.Add(section);
                lines.Add(""); // 섹션 아래 빈 줄 추가
                sectionIndex = lines.Count - 2;
            }
        
            // 기존 섹션의 모든 항목 제거
            int endIndex = lines.FindIndex(sectionIndex + 1, line => line.StartsWith("[") || string.IsNullOrWhiteSpace(line));
            if (endIndex == -1) endIndex = lines.Count;
        
            lines.RemoveRange(sectionIndex + 1, endIndex - sectionIndex - 1);
        
            // 새로운 항목 추가
            foreach (var item in listBox.Items)
            {
                int spaceIndex = item.ToString().IndexOf(' ');
                if (spaceIndex != -1)
                {
                    string folderPath = item.ToString().Substring(spaceIndex + 1);
                    lines.Insert(sectionIndex + 1, folderPath);
                    sectionIndex++;
                }
            }
        
            // 마지막 섹션 뒤에 빈 줄 추가 (중복 방지)
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
            {
                lines.Add("");
            }
        
            File.WriteAllLines(settingsFilePath, lines);
        }
        
        private void btn_BaseFolder_Click(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                // 폴더 선택 대화창의 초기 경로 설정
                folderDialog.SelectedPath = string.IsNullOrEmpty(baseFolder) ? AppDomain.CurrentDomain.BaseDirectory : baseFolder;

                // 폴더 선택 대화창 표시
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    baseFolder = folderDialog.SelectedPath;

                    // 선택된 폴더를 Label에 표시
                    lb_BaseFolder.Text = baseFolder;
                    lb_BaseFolder.ForeColor = System.Drawing.Color.Black;

                    // 선택된 폴더를 설정 파일에 저장
                    SaveBaseFolder(baseFolder);
                }
            }
        }

        private void SaveBaseFolder(string folderPath)
        {
            var lines = File.Exists(settingsFilePath) ? File.ReadAllLines(settingsFilePath).ToList() : new List<string>();

            // [BaseFolder] 섹션 처리
            if (!lines.Contains("[BaseFolder]"))
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                {
                    lines.Add(""); // 섹션 앞에 빈 줄 추가
                }
                lines.Add("[BaseFolder]");
            }

            int sectionIndex = lines.IndexOf("[BaseFolder]");
            int endIndex = lines.FindIndex(sectionIndex + 1, line => line.StartsWith("[") || string.IsNullOrWhiteSpace(line));
            if (endIndex == -1) endIndex = lines.Count;

            // 기존 BaseFolder 값 제거 후 추가
            lines = lines.Take(sectionIndex + 1)
                         .Concat(lines.Skip(endIndex))
                         .Concat(new[] { folderPath })
                         .ToList();

            File.WriteAllLines(settingsFilePath, lines);
        }
        
        private void LoadRegexFromSettings()
        {
            if (!File.Exists(settingsFilePath)) return;

            var lines = File.ReadAllLines(settingsFilePath).ToList();
            bool inRegexSection = false;

            foreach (var line in lines)
            {
                if (line.Trim() == RegexSection)
                {
                    inRegexSection = true;
                    continue;
                }

                if (inRegexSection)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("["))
                        break;

                    // Regex 항목 추가
                    AddRegexToListFromSettings(line.Trim());
                }
            }
        }

        private void AddRegexToListFromSettings(string settingLine)
        {
            int arrowIndex = settingLine.IndexOf("->");
            if (arrowIndex == -1) return;

            string regex = settingLine.Substring(0, arrowIndex).Trim();
            string folder = settingLine.Substring(arrowIndex + 2).Trim();

            AddRegexToList(regex, folder);
        }

        private void btn_RegAdd_Click(object sender, EventArgs e)
        {
            using (var regexForm = new ucScreen1_Reg(this)) // this 전달
            {
                if (regexForm.ShowDialog() == DialogResult.OK)
                {
                    string regex = regexForm.RegexPattern;
                    string targetFolder = regexForm.TargetFolder;
        
                    AddRegexToList(regex, targetFolder);
                    SaveRegexToSettings();
                }
            }
        }


        private void btn_RegEdit_Click(object sender, EventArgs e)
        {
            // 선택된 항목 확인
            if (lb_RegexList.SelectedItem == null)
            {
                MessageBox.Show("수정할 항목을 선택하세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        
            // 선택된 항목에서 정규표현식과 폴더 경로 추출
            string selectedItem = lb_RegexList.SelectedItem.ToString();
            int arrowIndex = selectedItem.IndexOf("->");
            if (arrowIndex == -1)
            {
                MessageBox.Show("항목 형식이 올바르지 않습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        
            string regex = selectedItem.Substring(selectedItem.IndexOf(' ') + 1, arrowIndex - selectedItem.IndexOf(' ') - 2).Trim();
            string targetFolder = selectedItem.Substring(arrowIndex + 2).Trim();
        
            // UcScreen1_Reg 창 생성
            using (var regexForm = new ucScreen1_Reg())
            {
                regexForm.RegexPattern = regex; // 정규표현식 설정
                regexForm.TargetFolder = targetFolder; // 폴더 경로 설정
        
                // 창 활성화
                if (regexForm.ShowDialog() == DialogResult.OK)
                {
                    // 수정된 정보로 목록 업데이트
                    string updatedRegex = regexForm.RegexPattern;
                    string updatedFolder = regexForm.TargetFolder;
                    UpdateRegexInList(updatedRegex, updatedFolder, lb_RegexList.SelectedIndex);
                    SaveRegexToSettings();
                }
            }
        }

        private void btn_RegRemove_Click(object sender, EventArgs e)
        {
            if (lb_RegexList.SelectedItem == null)
            {
                MessageBox.Show("삭제할 항목을 선택하세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirmResult = MessageBox.Show("선택한 항목을 삭제하시겠습니까?", "삭제 확인",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmResult == DialogResult.Yes)
            {
                lb_RegexList.Items.RemoveAt(lb_RegexList.SelectedIndex);
                ReorderRegexList();
                SaveRegexToSettings();
            }
        }

        private void AddRegexToList(string regex, string targetFolder)
        {
            int index = lb_RegexList.Items.Count + 1;
            lb_RegexList.Items.Add($"{index} {regex} -> {targetFolder}");
        }

        private void UpdateRegexInList(string regex, string targetFolder, int selectedIndex)
        {
            lb_RegexList.Items[selectedIndex] = $"{selectedIndex + 1} {regex} -> {targetFolder}";
        }

        private void ReorderRegexList()
        {
            for (int i = 0; i < lb_RegexList.Items.Count; i++)
            {
                string item = lb_RegexList.Items[i].ToString();
                int spaceIndex = item.IndexOf(' ');
                if (spaceIndex != -1)
                {
                    lb_RegexList.Items[i] = $"{i + 1}{item.Substring(spaceIndex)}";
                }
            }
        }

        private void SaveRegexToSettings()
        {
            var lines = File.Exists(settingsFilePath) ? File.ReadAllLines(settingsFilePath).ToList() : new List<string>();

            // [Regex] 섹션 재작성
            int sectionIndex = lines.FindIndex(line => line.Trim() == RegexSection);
            if (sectionIndex != -1)
            {
                int endIndex = lines.FindIndex(sectionIndex + 1, line => line.StartsWith("[") || string.IsNullOrWhiteSpace(line));
                if (endIndex == -1) endIndex = lines.Count;

                lines.RemoveRange(sectionIndex, endIndex - sectionIndex);
            }

            if (!lines.Contains(RegexSection))
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                {
                    lines.Add(""); // 섹션 앞에 빈 줄 추가
                }
                lines.Add(RegexSection);
            }

            foreach (var item in lb_RegexList.Items)
            {
                string regexItem = item.ToString();
                int arrowIndex = regexItem.IndexOf("->");
                if (arrowIndex != -1)
                {
                    string regex = regexItem.Substring(regexItem.IndexOf(' ') + 1, arrowIndex - regexItem.IndexOf(' ') - 2).Trim();
                    string folder = regexItem.Substring(arrowIndex + 2).Trim();
                    lines.Add($"{regex} -> {folder}");
                }
            }

            File.WriteAllLines(settingsFilePath, lines);
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
            var targetFolders = GetFoldersFromSection(TargetFoldersSection);
            var excludeFolders = GetFoldersFromSection(ExcludeFoldersSection);

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
                    if (line.Trim() == RegexSection)
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
        
        public string BaseFolder
        {
            get => lb_BaseFolder.Text != "폴더가 미선택되었습니다" ? lb_BaseFolder.Text : null;
        }
    }
}
