using System;
using System.Drawing;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucScreen1_Reg : Form   // 반드시 Form을 상속받아야 함
    {
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

        public ucScreen1_Reg()
        {
            // Form 제목 설정
            this.Text = Regular Expressions";
            
            InitializeComponent();

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
            
            // 폼 로드 시 위치 설정
            this.Load += ucScreen1_Reg_Load;
        }
        
        private void ucScreen1_Reg_Load(object sender, EventArgs e)
        {
            var parentControl = this.Owner as Control; // Owner가 ucScreen1이어야 함
            if (parentControl != null)
            {
                // UcScreen1 중심 계산
                int centerX = parentControl.Location.X + (parentControl.Width - this.Width) / 2;
                int centerY = parentControl.Location.Y + (parentControl.Height - this.Height) / 2;

                // 위치 설정
                this.StartPosition = FormStartPosition.Manual;
                this.Location = new Point(centerX, centerY);
            }
        }

        private void btn_RegSelectFolder_Click(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    TargetFolder = folderDialog.SelectedPath; // TargetFolder 속성을 사용
                }
            }
        }

        private void btn_RegApply_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(RegexPattern)) // RegexPattern 속성을 사용
            {
                MessageBox.Show("정규표현식을 입력해주세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(TargetFolder)) // TargetFolder 속성을 사용
            {
                MessageBox.Show("복사 폴더를 선택해주세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
