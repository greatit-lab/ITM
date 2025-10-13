// ITM_Agnet/ucPanel/ucLampLifePanel.cs
using ITM_Agent.Services;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucLampLifePanel : UserControl
    {
        private readonly SettingsManager _settingsManager;
        private readonly LampLifeService _lampLifeService;

        public ucLampLifePanel(SettingsManager settingsManager, LampLifeService lampLifeService)
        {
            InitializeComponent();
            _settingsManager = settingsManager;
            _lampLifeService = lampLifeService;

            LoadSettings();
        }

        private void LoadSettings()
        {
            chkEnable.Checked = _settingsManager.IsLampLifeCollectorEnabled;
            numInterval.Value = _settingsManager.LampLifeCollectorInterval;
            UpdateControlsEnabled();
        }

        private void chkEnable_CheckedChanged(object sender, EventArgs e)
        {
            _settingsManager.IsLampLifeCollectorEnabled = chkEnable.Checked;
            UpdateControlsEnabled();
        }

        private void numInterval_ValueChanged(object sender, EventArgs e)
        {
            _settingsManager.LampLifeCollectorInterval = (int)numInterval.Value;
        }

        private async void btnManualCollect_Click(object sender, EventArgs e)
        {
            btnManualCollect.Enabled = false;
            lblLastCollect.Text = "Collecting...";
            lblLastCollect.ForeColor = Color.Blue;

            try
            {
                await _lampLifeService.ExecuteCollectionAsync();
                lblLastCollect.Text = $"Manual collection successful at {DateTime.Now:HH:mm:ss}";
                lblLastCollect.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                lblLastCollect.Text = $"Manual collection failed at {DateTime.Now:HH:mm:ss}";
                lblLastCollect.ForeColor = Color.Red;
            }
            finally
            {
                btnManualCollect.Enabled = true;
            }
        }

        public void UpdateStatusOnRun(bool isRunning)
        {
            // Run/Stop 상태에 따라 UI 활성화/비활성화
            chkEnable.Enabled = !isRunning;
            numInterval.Enabled = !isRunning && chkEnable.Checked;
            btnManualCollect.Enabled = isRunning && chkEnable.Checked;
        }

        private void UpdateControlsEnabled()
        {
            numInterval.Enabled = chkEnable.Checked;
        }
    }
}
