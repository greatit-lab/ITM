// ITM_Agnet/ucPanel/ucLampLifePanel.Designer.cs
namespace ITM_Agent.ucPanel
{
    partial class ucLampLifePanel
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        private void InitializeComponent()
        {
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.btnManualCollect = new System.Windows.Forms.Button();
            this.lblLastCollect = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.numInterval = new System.Windows.Forms.NumericUpDown();
            this.label3 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.chkEnable = new System.Windows.Forms.CheckBox();
            this.label1 = new System.Windows.Forms.Label();
            this.groupBox1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numInterval)).BeginInit();
            this.SuspendLayout();
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.btnManualCollect);
            this.groupBox1.Controls.Add(this.lblLastCollect);
            this.groupBox1.Controls.Add(this.label4);
            this.groupBox1.Controls.Add(this.numInterval);
            this.groupBox1.Controls.Add(this.label3);
            this.groupBox1.Controls.Add(this.label2);
            this.groupBox1.Controls.Add(this.chkEnable);
            this.groupBox1.Controls.Add(this.label1);
            this.groupBox1.Location = new System.Drawing.Point(25, 21);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(624, 133);
            this.groupBox1.TabIndex = 0;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "● Lamp Life Collector Settings";
            // 
            // btnManualCollect
            // 
            this.btnManualCollect.Location = new System.Drawing.Point(458, 94);
            this.btnManualCollect.Name = "btnManualCollect";
            this.btnManualCollect.Size = new System.Drawing.Size(140, 21);
            this.btnManualCollect.TabIndex = 7;
            this.btnManualCollect.Text = "Collect Now";
            this.btnManualCollect.UseVisualStyleBackColor = true;
            this.btnManualCollect.Click += new System.EventHandler(this.btnManualCollect_Click);
            // 
            // lblLastCollect
            // 
            this.lblLastCollect.AutoSize = true;
            this.lblLastCollect.Location = new System.Drawing.Point(129, 98);
            this.lblLastCollect.Name = "lblLastCollect";
            this.lblLastCollect.Size = new System.Drawing.Size(28, 12);
            this.lblLastCollect.TabIndex = 6;
            this.lblLastCollect.Text = "N/A";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Font = new System.Drawing.Font("굴림", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(129)));
            this.label4.Location = new System.Drawing.Point(20, 98);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(103, 12);
            this.label4.TabIndex = 5;
            this.label4.Text = "• Last Collection:";
            // 
            // numInterval
            // 
            this.numInterval.Location = new System.Drawing.Point(528, 61);
            this.numInterval.Maximum = new decimal(new int[] {
            1440,
            0,
            0,
            0});
            this.numInterval.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numInterval.Name = "numInterval";
            this.numInterval.Size = new System.Drawing.Size(70, 21);
            this.numInterval.TabIndex = 4;
            this.numInterval.Value = new decimal(new int[] {
            60,
            0,
            0,
            0});
            this.numInterval.ValueChanged += new System.EventHandler(this.numInterval_ValueChanged);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(20, 66);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(115, 12);
            this.label3.TabIndex = 3;
            this.label3.Text = "• Collection Interval";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(472, 66);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(50, 12);
            this.label2.TabIndex = 2;
            this.label2.Text = "minutes";
            // 
            // chkEnable
            // 
            this.chkEnable.AutoSize = true;
            this.chkEnable.Location = new System.Drawing.Point(583, 31);
            this.chkEnable.Name = "chkEnable";
            this.chkEnable.Size = new System.Drawing.Size(15, 14);
            this.chkEnable.TabIndex = 1;
            this.chkEnable.UseVisualStyleBackColor = true;
            this.chkEnable.CheckedChanged += new System.EventHandler(this.chkEnable_CheckedChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(20, 33);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(185, 12);
            this.label1.TabIndex = 0;
            this.label1.Text = "• Enable Lamp Life Data Polling";
            // 
            // ucLampLifePanel
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.groupBox1);
            this.Name = "ucLampLifePanel";
            this.Size = new System.Drawing.Size(676, 340);
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numInterval)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.CheckBox chkEnable;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.NumericUpDown numInterval;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Button btnManualCollect;
        private System.Windows.Forms.Label lblLastCollect;
        private System.Windows.Forms.Label label4;
    }
}
