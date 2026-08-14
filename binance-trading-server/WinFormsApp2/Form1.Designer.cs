namespace WinFormsApp2
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            splitContainerMain = new SplitContainer();
            splitContainerLeft = new SplitContainer();
            formsPlot1 = new ScottPlot.WinForms.FormsPlot();
            grpLog = new GroupBox();
            rtbLog = new RichTextBox();
            panelRight = new Panel();
            grpActions = new GroupBox();
            btnClearLog = new Button();
            btnStop = new Button();
            btnStepForward = new Button();
            btnStepBackward = new Button();
            btnPause = new Button();
            btnStart = new Button();
            grpParams = new GroupBox();
            chkHighlightHighVolume = new CheckBox();
            chkAutoFitPrice = new CheckBox();
            chkEnableTickPush = new CheckBox();
            dtpEndDate = new DateTimePicker();
            lblEndDate = new Label();
            dtpStartDate = new DateTimePicker();
            lblStartDate = new Label();
            cmbKlineInterval = new ComboBox();
            lblKlineInterval = new Label();
            numInterval = new NumericUpDown();
            lblInterval = new Label();
            txtSymbol = new TextBox();
            lblSymbol = new Label();

            ((System.ComponentModel.ISupportInitialize)splitContainerMain).BeginInit();
            splitContainerMain.Panel1.SuspendLayout();
            splitContainerMain.Panel2.SuspendLayout();
            splitContainerMain.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)splitContainerLeft).BeginInit();
            splitContainerLeft.Panel1.SuspendLayout();
            splitContainerLeft.Panel2.SuspendLayout();
            splitContainerLeft.SuspendLayout();
            grpLog.SuspendLayout();
            panelRight.SuspendLayout();
            grpActions.SuspendLayout();
            grpParams.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)numInterval).BeginInit();
            SuspendLayout();
            // 
            // splitContainerMain
            // 
            splitContainerMain.Dock = DockStyle.Fill;
            splitContainerMain.Location = new Point(0, 0);
            splitContainerMain.Name = "splitContainerMain";
            // 
            // splitContainerMain.Panel1
            // 
            splitContainerMain.Panel1.Controls.Add(splitContainerLeft);
            // 
            // splitContainerMain.Panel2
            // 
            splitContainerMain.Panel2.Controls.Add(panelRight);
            splitContainerMain.Size = new Size(1184, 761);
            splitContainerMain.SplitterDistance = 880;
            splitContainerMain.TabIndex = 0;
            // 
            // splitContainerLeft
            // 
            splitContainerLeft.Dock = DockStyle.Fill;
            splitContainerLeft.Location = new Point(0, 0);
            splitContainerLeft.Name = "splitContainerLeft";
            splitContainerLeft.Orientation = Orientation.Horizontal;
            // 
            // splitContainerLeft.Panel1
            // 
            splitContainerLeft.Panel1.Controls.Add(formsPlot1);
            // 
            // splitContainerLeft.Panel2
            // 
            splitContainerLeft.Panel2.Controls.Add(grpLog);
            splitContainerLeft.Size = new Size(880, 761);
            splitContainerLeft.SplitterDistance = 530;
            splitContainerLeft.TabIndex = 0;
            // 
            // formsPlot1
            // 
            formsPlot1.DisplayScale = 1F;
            formsPlot1.Dock = DockStyle.Fill;
            formsPlot1.Location = new Point(0, 0);
            formsPlot1.Name = "formsPlot1";
            formsPlot1.Size = new Size(880, 530);
            formsPlot1.TabIndex = 0;
            // 
            // grpLog
            // 
            grpLog.Controls.Add(rtbLog);
            grpLog.Dock = DockStyle.Fill;
            grpLog.Location = new Point(0, 0);
            grpLog.Name = "grpLog";
            grpLog.Padding = new Padding(8);
            grpLog.Size = new Size(880, 227);
            grpLog.TabIndex = 0;
            grpLog.TabStop = false;
            grpLog.Text = "日志输出 (Log Output)";
            // 
            // rtbLog
            // 
            rtbLog.BackColor = Color.FromArgb(30, 30, 30);
            rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.Dock = DockStyle.Fill;
            rtbLog.Font = new Font("Consolas", 9.75F, FontStyle.Regular, GraphicsUnit.Point);
            rtbLog.ForeColor = Color.Gainsboro;
            rtbLog.Location = new Point(8, 24);
            rtbLog.Name = "rtbLog";
            rtbLog.ReadOnly = true;
            rtbLog.Size = new Size(864, 195);
            rtbLog.TabIndex = 0;
            rtbLog.Text = "";
            // 
            // panelRight
            // 
            panelRight.Controls.Add(grpActions);
            panelRight.Controls.Add(grpParams);
            panelRight.Dock = DockStyle.Fill;
            panelRight.Location = new Point(0, 0);
            panelRight.Name = "panelRight";
            panelRight.Padding = new Padding(8);
            panelRight.Size = new Size(300, 761);
            panelRight.TabIndex = 0;
            // 
            // grpParams
            // 
            grpParams.Controls.Add(chkHighlightHighVolume);
            grpParams.Controls.Add(chkAutoFitPrice);
            grpParams.Controls.Add(chkEnableTickPush);
            grpParams.Controls.Add(dtpEndDate);
            grpParams.Controls.Add(lblEndDate);
            grpParams.Controls.Add(dtpStartDate);
            grpParams.Controls.Add(lblStartDate);
            grpParams.Controls.Add(cmbKlineInterval);
            grpParams.Controls.Add(lblKlineInterval);
            grpParams.Controls.Add(numInterval);
            grpParams.Controls.Add(lblInterval);
            grpParams.Controls.Add(txtSymbol);
            grpParams.Controls.Add(lblSymbol);
            grpParams.Dock = DockStyle.Top;
            grpParams.Location = new Point(8, 8);
            grpParams.Name = "grpParams";
            grpParams.Size = new Size(284, 370);
            grpParams.TabIndex = 0;
            grpParams.TabStop = false;
            grpParams.Text = "参数设置 (Parameters)";
            // 
            // chkHighlightHighVolume
            // 
            chkHighlightHighVolume.AutoSize = true;
            chkHighlightHighVolume.Checked = true;
            chkHighlightHighVolume.CheckState = CheckState.Checked;
            chkHighlightHighVolume.Location = new Point(16, 335);
            chkHighlightHighVolume.Name = "chkHighlightHighVolume";
            chkHighlightHighVolume.Size = new Size(165, 21);
            chkHighlightHighVolume.TabIndex = 12;
            chkHighlightHighVolume.Text = "标注高成交量 K线";
            chkHighlightHighVolume.UseVisualStyleBackColor = true;
            // 
            // chkAutoFitPrice
            // 
            chkAutoFitPrice.AutoSize = true;
            chkAutoFitPrice.Checked = true;
            chkAutoFitPrice.CheckState = CheckState.Checked;
            chkAutoFitPrice.Location = new Point(16, 310);
            chkAutoFitPrice.Name = "chkAutoFitPrice";
            chkAutoFitPrice.Size = new Size(165, 21);
            chkAutoFitPrice.TabIndex = 11;
            chkAutoFitPrice.Text = "自动聚焦最新价格视口";
            chkAutoFitPrice.UseVisualStyleBackColor = true;
            // 
            // chkEnableTickPush
            // 
            chkEnableTickPush.AutoSize = true;
            chkEnableTickPush.Checked = true;
            chkEnableTickPush.CheckState = CheckState.Checked;
            chkEnableTickPush.Location = new Point(16, 285);
            chkEnableTickPush.Name = "chkEnableTickPush";
            chkEnableTickPush.Size = new Size(165, 21);
            chkEnableTickPush.TabIndex = 10;
            chkEnableTickPush.Text = "启用 Tick 细粒度推送";
            chkEnableTickPush.UseVisualStyleBackColor = true;
            // 
            // dtpEndDate
            // 
            dtpEndDate.CustomFormat = "yyyy-MM-dd";
            dtpEndDate.Format = DateTimePickerFormat.Custom;
            dtpEndDate.Location = new Point(16, 198);
            dtpEndDate.Name = "dtpEndDate";
            dtpEndDate.Size = new Size(248, 23);
            dtpEndDate.TabIndex = 7;
            // 
            // lblEndDate
            // 
            lblEndDate.AutoSize = true;
            lblEndDate.Location = new Point(16, 178);
            lblEndDate.Name = "lblEndDate";
            lblEndDate.Size = new Size(138, 17);
            lblEndDate.TabIndex = 6;
            lblEndDate.Text = "结束日期 (最小单位: 天):";
            // 
            // dtpStartDate
            // 
            dtpStartDate.CustomFormat = "yyyy-MM-dd";
            dtpStartDate.Format = DateTimePickerFormat.Custom;
            dtpStartDate.Location = new Point(16, 148);
            dtpStartDate.Name = "dtpStartDate";
            dtpStartDate.Size = new Size(248, 23);
            dtpStartDate.TabIndex = 5;
            // 
            // lblStartDate
            // 
            lblStartDate.AutoSize = true;
            lblStartDate.Location = new Point(16, 128);
            lblStartDate.Name = "lblStartDate";
            lblStartDate.Size = new Size(138, 17);
            lblStartDate.TabIndex = 4;
            lblStartDate.Text = "开始日期 (最小单位: 天):";
            // 
            // cmbKlineInterval
            // 
            cmbKlineInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbKlineInterval.FormattingEnabled = true;
            cmbKlineInterval.Location = new Point(16, 95);
            cmbKlineInterval.Name = "cmbKlineInterval";
            cmbKlineInterval.Size = new Size(248, 25);
            cmbKlineInterval.TabIndex = 3;
            // 
            // lblKlineInterval
            // 
            lblKlineInterval.AutoSize = true;
            lblKlineInterval.Location = new Point(16, 75);
            lblKlineInterval.Name = "lblKlineInterval";
            lblKlineInterval.Size = new Size(55, 17);
            lblKlineInterval.TabIndex = 2;
            lblKlineInterval.Text = "K线周期:";
            // 
            // numInterval
            // 
            numInterval.Location = new Point(16, 248);
            numInterval.Maximum = new decimal(new int[] { 60000, 0, 0, 0 });
            numInterval.Minimum = new decimal(new int[] { 50, 0, 0, 0 });
            numInterval.Name = "numInterval";
            numInterval.Size = new Size(248, 23);
            numInterval.TabIndex = 9;
            numInterval.Value = new decimal(new int[] { 100, 0, 0, 0 });
            // 
            // lblInterval
            // 
            lblInterval.AutoSize = true;
            lblInterval.Location = new Point(16, 228);
            lblInterval.Name = "lblInterval";
            lblInterval.Size = new Size(117, 17);
            lblInterval.TabIndex = 8;
            lblInterval.Text = "刷新/回放间隔 (ms):";
            // 
            // txtSymbol
            // 
            txtSymbol.Location = new Point(16, 45);
            txtSymbol.Name = "txtSymbol";
            txtSymbol.Size = new Size(248, 23);
            txtSymbol.TabIndex = 1;
            txtSymbol.Text = "BTCUSDT";
            // 
            // lblSymbol
            // 
            lblSymbol.AutoSize = true;
            lblSymbol.Location = new Point(16, 25);
            lblSymbol.Name = "lblSymbol";
            lblSymbol.Size = new Size(47, 17);
            lblSymbol.TabIndex = 0;
            lblSymbol.Text = "交易对:";
            // 
            // grpActions
            // 
            grpActions.Controls.Add(btnClearLog);
            grpActions.Controls.Add(btnStop);
            grpActions.Controls.Add(btnStepForward);
            grpActions.Controls.Add(btnStepBackward);
            grpActions.Controls.Add(btnPause);
            grpActions.Controls.Add(btnStart);
            grpActions.Dock = DockStyle.Top;
            grpActions.Location = new Point(8, 378);
            grpActions.Name = "grpActions";
            grpActions.Size = new Size(284, 215);
            grpActions.TabIndex = 1;
            grpActions.TabStop = false;
            grpActions.Text = "控制面板 (Controls)";
            // 
            // btnStart
            // 
            btnStart.Location = new Point(16, 25);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(248, 30);
            btnStart.TabIndex = 0;
            btnStart.Text = "开始回放 (Play)";
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // btnPause
            // 
            btnPause.Location = new Point(16, 60);
            btnPause.Name = "btnPause";
            btnPause.Size = new Size(248, 30);
            btnPause.TabIndex = 1;
            btnPause.Text = "暂停/恢复 (Pause/Resume)";
            btnPause.UseVisualStyleBackColor = true;
            btnPause.Click += btnPause_Click;
            // 
            // btnStepBackward
            // 
            btnStepBackward.Location = new Point(16, 95);
            btnStepBackward.Name = "btnStepBackward";
            btnStepBackward.Size = new Size(120, 30);
            btnStepBackward.TabIndex = 2;
            btnStepBackward.Text = "单步向后 ◄";
            btnStepBackward.UseVisualStyleBackColor = true;
            btnStepBackward.Click += btnStepBackward_Click;
            // 
            // btnStepForward
            // 
            btnStepForward.Location = new Point(144, 95);
            btnStepForward.Name = "btnStepForward";
            btnStepForward.Size = new Size(120, 30);
            btnStepForward.TabIndex = 3;
            btnStepForward.Text = "单步向前 ►";
            btnStepForward.UseVisualStyleBackColor = true;
            btnStepForward.Click += btnStepForward_Click;
            // 
            // btnStop
            // 
            btnStop.Location = new Point(16, 130);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(248, 30);
            btnStop.TabIndex = 4;
            btnStop.Text = "停止回放 (Stop)";
            btnStop.UseVisualStyleBackColor = true;
            btnStop.Click += btnStop_Click;
            // 
            // btnClearLog
            // 
            btnClearLog.Location = new Point(16, 165);
            btnClearLog.Name = "btnClearLog";
            btnClearLog.Size = new Size(248, 30);
            btnClearLog.TabIndex = 5;
            btnClearLog.Text = "清空日志 (Clear Log)";
            btnClearLog.UseVisualStyleBackColor = true;
            btnClearLog.Click += btnClearLog_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1184, 761);
            Controls.Add(splitContainerMain);
            MinimumSize = new Size(900, 600);
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Trading Assistance Pro";
            splitContainerMain.Panel1.ResumeLayout(false);
            splitContainerMain.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainerMain).EndInit();
            splitContainerMain.ResumeLayout(false);
            splitContainerLeft.Panel1.ResumeLayout(false);
            splitContainerLeft.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainerLeft).EndInit();
            splitContainerLeft.ResumeLayout(false);
            grpLog.ResumeLayout(false);
            panelRight.ResumeLayout(false);
            grpActions.ResumeLayout(false);
            grpParams.ResumeLayout(false);
            grpParams.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)numInterval).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private SplitContainer splitContainerMain;
        private SplitContainer splitContainerLeft;
        private ScottPlot.WinForms.FormsPlot formsPlot1;
        private GroupBox grpLog;
        private RichTextBox rtbLog;
        private Panel panelRight;
        private GroupBox grpParams;
        private Label lblSymbol;
        private TextBox txtSymbol;
        private Label lblKlineInterval;
        private ComboBox cmbKlineInterval;
        private Label lblStartDate;
        private DateTimePicker dtpStartDate;
        private Label lblEndDate;
        private DateTimePicker dtpEndDate;
        private Label lblInterval;
        private NumericUpDown numInterval;
        private CheckBox chkEnableTickPush;
        private CheckBox chkAutoFitPrice;
        private CheckBox chkHighlightHighVolume;
        private GroupBox grpActions;
        private Button btnStart;
        private Button btnPause;
        private Button btnStepBackward;
        private Button btnStepForward;
        private Button btnStop;
        private Button btnClearLog;
    }
}
