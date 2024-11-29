using System;
using System.Drawing;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucScreen1_Reg : Form
    {
        private readonly ucScreen1 parentScreen; // ucScreen1 참조 저장

        public string RegexPattern
        {
            get => tb_RegInput.Text; // tb_RegInput 값을 반환
            set => tb_RegInput.Text = value; // tb_RegInput 값을 설정
        }

        public string TargetFolder
        {
            get => tb_RegFolder.Text; // tb_RegFolder 값을 반환
            set => tb_RegFolder.Text = value; // tb_RegFolder 값을 설정
        }

        public ucScreen1_Reg(ucScreen1 parentScreen)
        {
            this.parentScreen = parentScreen; // 부모 ucScreen1 참조 저장

            // Form 초기화
            InitializeComponent();

            // Form 제목 설정
            this.Text = "Regular Expressions";
            
            // tb_RegFolder를 읽기 전용으로 설정
            tb_RegFolder.ReadOnly = true;

            // 폼 속성 설정
            this.FormBorderStyle = FormBorderStyle.FixedDialog; // 창 크기 고정
            this.MaximizeBox = false; // 최대화 버튼 비활성화
            this.MinimizeBox = false; // 최소화 버튼 비활성화

            // 이벤트 핸들러 추가
            btn_RegSelectFolder.Click += btn_RegSelectFolder_Click;
            btn_RegApply.Click += btn_RegApply_Click;
            btn_RegCancel.Click += (sender, e) => this.DialogResult = DialogResult.Cancel;

            // 폼 로드 시 중앙 위치 계산
            this.Load += ucScreen1_Reg_Load;
        }

        private void ucScreen1_Reg_Load(object sender, EventArgs e)
        {
            if (parentScreen != null)
            {
                var parentForm = parentScreen.FindForm(); // ucScreen1이 포함된 부모 폼 가져오기
                if (parentForm != null)
                {
                    // 부모 폼(MainForm)과 ucScreen1의 위치를 기준으로 중앙 좌표 계산
                    int centerX = parentForm.Location.X + parentScreen.Location.X + (parentScreen.Width - this.Width) / 2;
                    int centerY = parentForm.Location.Y + parentScreen.Location.Y + (parentScreen.Height - this.Height) / 2;

                    // 폼 위치 설정
                    this.StartPosition = FormStartPosition.Manual;
                    this.Location = new Point(centerX, centerY);
                }
            }
        }

        private void btn_RegSelectFolder_Click(object sender, EventArgs e)
        {
            string initialPath = AppDomain.CurrentDomain.BaseDirectory; // 기본 경로

            if (parentScreen != null && !string.IsNullOrEmpty(parentScreen.BaseFolder))
            {
                initialPath = parentScreen.BaseFolder; // BaseFolder 값 사용
            }

            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.SelectedPath = initialPath; // 초기 경로 설정
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    TargetFolder = folderDialog.SelectedPath; // 선택한 폴더 경로를 TargetFolder에 저장
                }
            }
        }

        private void btn_RegApply_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(RegexPattern))
            {
                MessageBox.Show("정규표현식을 입력해주세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(TargetFolder))
            {
                MessageBox.Show("복사 폴더를 선택해주세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
