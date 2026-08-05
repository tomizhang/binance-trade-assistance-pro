namespace WinFormsApp1
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
            formsPlot1 = new ScottPlot.WinForms.FormsPlot();
            button1 = new Button();
            btn_puase = new Button();
            lblSymbol = new Label();
            cmbSymbol = new ComboBox();
            lblInterval = new Label();
            cmbInterval = new ComboBox();
            lblTimeRange = new Label();
            cmbTimeRange = new ComboBox();
            btnFetch = new Button();
            lblStatus = new Label();
            chkAutoPlay = new CheckBox();
            SuspendLayout();
            // 
            // formsPlot1
            // 
            formsPlot1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            formsPlot1.Location = new Point(12, 12);
            formsPlot1.Name = "formsPlot1";
            formsPlot1.Size = new Size(790, 475);
            formsPlot1.TabIndex = 0;
            // 
            // rtbLog
            // 
            rtbLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            rtbLog.BackColor = Color.FromArgb(248, 249, 250);
            rtbLog.BorderStyle = BorderStyle.FixedSingle;
            rtbLog.Font = new Font("Consolas", 9F);
            rtbLog.Location = new Point(12, 495);
            rtbLog.Name = "rtbLog";
            rtbLog.ReadOnly = true;
            rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            rtbLog.Size = new Size(790, 165);
            rtbLog.TabIndex = 10;
            rtbLog.Text = "";
            // 
            // lblSymbol
            // 
            lblSymbol.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblSymbol.AutoSize = true;
            lblSymbol.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblSymbol.Location = new Point(815, 12);
            lblSymbol.Name = "lblSymbol";
            lblSymbol.Size = new Size(84, 17);
            lblSymbol.TabIndex = 3;
            lblSymbol.Text = "1. 选择币种:";
            // 
            // cmbSymbol
            // 
            cmbSymbol.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbSymbol.FormattingEnabled = true;
            cmbSymbol.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "DOGEUSDT", "XRPUSDT", "ADAUSDT" });
            cmbSymbol.Location = new Point(815, 30);
            cmbSymbol.Name = "cmbSymbol";
            cmbSymbol.Size = new Size(200, 25);
            cmbSymbol.TabIndex = 4;
            // 
            // lblInterval
            // 
            lblInterval.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblInterval.AutoSize = true;
            lblInterval.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblInterval.Location = new Point(815, 62);
            lblInterval.Name = "lblInterval";
            lblInterval.Size = new Size(108, 17);
            lblInterval.TabIndex = 5;
            lblInterval.Text = "2. 指定时间周期:";
            // 
            // cmbInterval
            // 
            cmbInterval.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbInterval.FormattingEnabled = true;
            cmbInterval.Items.AddRange(new object[] { "1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h", "6h", "8h", "12h", "1d" });
            cmbInterval.Location = new Point(815, 80);
            cmbInterval.Name = "cmbInterval";
            cmbInterval.Size = new Size(200, 25);
            cmbInterval.TabIndex = 6;
            // 
            // lblTimeRange
            // 
            lblTimeRange.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblTimeRange.AutoSize = true;
            lblTimeRange.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblTimeRange.Location = new Point(815, 112);
            lblTimeRange.Name = "lblTimeRange";
            lblTimeRange.Size = new Size(84, 17);
            lblTimeRange.TabIndex = 7;
            lblTimeRange.Text = "3. 历史时间范围:";
            // 
            // cmbTimeRange
            // 
            cmbTimeRange.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbTimeRange.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbTimeRange.FormattingEnabled = true;
            cmbTimeRange.Items.AddRange(new object[] { "最近1小时", "最近6小时", "最近24小时", "最近3天", "最近7天", "最近30天" });
            cmbTimeRange.Location = new Point(815, 130);
            cmbTimeRange.Name = "cmbTimeRange";
            cmbTimeRange.Size = new Size(200, 25);
            cmbTimeRange.TabIndex = 8;
            // 
            // btnFetch
            // 
            btnFetch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFetch.BackColor = Color.FromArgb(0, 122, 204);
            btnFetch.FlatStyle = FlatStyle.Flat;
            btnFetch.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            btnFetch.ForeColor = Color.White;
            btnFetch.Location = new Point(815, 165);
            btnFetch.Name = "btnFetch";
            btnFetch.Size = new Size(200, 32);
            btnFetch.TabIndex = 9;
            btnFetch.Text = "获取币种历史数据";
            btnFetch.UseVisualStyleBackColor = false;
            btnFetch.Click += btnFetch_Click;
            // 
            // lblPlaySpeed
            // 
            lblPlaySpeed.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblPlaySpeed.AutoSize = true;
            lblPlaySpeed.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblPlaySpeed.Location = new Point(815, 210);
            lblPlaySpeed.Name = "lblPlaySpeed";
            lblPlaySpeed.Size = new Size(84, 17);
            lblPlaySpeed.TabIndex = 12;
            lblPlaySpeed.Text = "4. 播放速度倍速:";
            // 
            // cmbPlaySpeed
            // 
            cmbPlaySpeed.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbPlaySpeed.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPlaySpeed.FormattingEnabled = true;
            cmbPlaySpeed.Items.AddRange(new object[] { "0.5x (慢速)", "1.0x (标准)", "2.0x (快速)", "5.0x (极速)", "10.0x (飞速)", "全速 (瞬时完成)" });
            cmbPlaySpeed.Location = new Point(815, 228);
            cmbPlaySpeed.Name = "cmbPlaySpeed";
            cmbPlaySpeed.Size = new Size(200, 25);
            cmbPlaySpeed.TabIndex = 13;
            // 
            // lblTrendState
            // 
            lblTrendState.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
            lblTrendState.BorderStyle = BorderStyle.FixedSingle;
            lblTrendState.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblTrendState.ForeColor = Color.DimGray;
            lblTrendState.Location = new Point(815, 260);
            lblTrendState.Name = "lblTrendState";
            lblTrendState.Size = new Size(200, 60);
            lblTrendState.TabIndex = 14;
            lblTrendState.Text = "策略状态: 监控中...";
            lblTrendState.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // chkEnableStrategy
            // 
            chkEnableStrategy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkEnableStrategy.AutoSize = true;
            chkEnableStrategy.Checked = true;
            chkEnableStrategy.CheckState = CheckState.Checked;
            chkEnableStrategy.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            chkEnableStrategy.Location = new Point(815, 330);
            chkEnableStrategy.Name = "chkEnableStrategy";
            chkEnableStrategy.Size = new Size(135, 20);
            chkEnableStrategy.TabIndex = 18;
            chkEnableStrategy.Text = "启用量化开仓策略";
            chkEnableStrategy.UseVisualStyleBackColor = true;
            // 
            // chkAutoFitY
            // 
            chkAutoFitY.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkAutoFitY.AutoSize = true;
            chkAutoFitY.Checked = true;
            chkAutoFitY.CheckState = CheckState.Checked;
            chkAutoFitY.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            chkAutoFitY.Location = new Point(815, 355);
            chkAutoFitY.Name = "chkAutoFitY";
            chkAutoFitY.Size = new Size(135, 20);
            chkAutoFitY.TabIndex = 17;
            chkAutoFitY.Text = "自动更新 Y 轴范围";
            chkAutoFitY.UseVisualStyleBackColor = true;
            // 
            // btnRewind
            // 
            btnRewind.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRewind.BackColor = Color.FromArgb(255, 140, 0);
            btnRewind.FlatStyle = FlatStyle.Flat;
            btnRewind.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            btnRewind.ForeColor = Color.White;
            btnRewind.Location = new Point(815, 385);
            btnRewind.Name = "btnRewind";
            btnRewind.Size = new Size(96, 32);
            btnRewind.TabIndex = 15;
            btnRewind.Text = "⏪ 单击回退";
            btnRewind.UseVisualStyleBackColor = false;
            btnRewind.Click += btnRewind_Click;
            // 
            // btnStepForward
            // 
            btnStepForward.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnStepForward.BackColor = Color.FromArgb(46, 139, 87);
            btnStepForward.FlatStyle = FlatStyle.Flat;
            btnStepForward.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            btnStepForward.ForeColor = Color.White;
            btnStepForward.Location = new Point(919, 385);
            btnStepForward.Name = "btnStepForward";
            btnStepForward.Size = new Size(96, 32);
            btnStepForward.TabIndex = 16;
            btnStepForward.Text = "⏩ 单击向前";
            btnStepForward.UseVisualStyleBackColor = false;
            btnStepForward.Click += btnStepForward_Click;
            // 
            // btn_puase
            // 
            btn_puase.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btn_puase.Location = new Point(815, 425);
            btn_puase.Name = "btn_puase";
            btn_puase.Size = new Size(200, 32);
            btn_puase.TabIndex = 2;
            btn_puase.Text = "暂停数据播放";
            btn_puase.UseVisualStyleBackColor = true;
            btn_puase.Click += btn_puase_Click;
            // 
            // button1
            // 
            button1.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            button1.Location = new Point(815, 465);
            button1.Name = "button1";
            button1.Size = new Size(200, 32);
            button1.TabIndex = 1;
            button1.Text = "重置/复位图表";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1030, 672);
            MinimumSize = new Size(980, 620);
            Controls.Add(chkEnableStrategy);
            Controls.Add(chkAutoFitY);
            Controls.Add(btnStepForward);
            Controls.Add(btnRewind);
            Controls.Add(lblTrendState);
            Controls.Add(cmbPlaySpeed);
            Controls.Add(lblPlaySpeed);
            Controls.Add(chkAutoPlay);
            Controls.Add(rtbLog);
            Controls.Add(btnFetch);
            Controls.Add(cmbTimeRange);
            Controls.Add(lblTimeRange);
            Controls.Add(cmbInterval);
            Controls.Add(lblInterval);
            Controls.Add(cmbSymbol);
            Controls.Add(lblSymbol);
            Controls.Add(btn_puase);
            Controls.Add(button1);
            Controls.Add(formsPlot1);
            Name = "Form1";
            Text = "币安合约历史数据实时队列接入分析器";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private ScottPlot.WinForms.FormsPlot formsPlot1;
        private Button button1;
        private Button btn_puase;
        private Label lblSymbol;
        private ComboBox cmbSymbol;
        private Label lblInterval;
        private ComboBox cmbInterval;
        private Label lblTimeRange;
        private ComboBox cmbTimeRange;
        private Button btnFetch;
        private RichTextBox rtbLog = new RichTextBox();
        private Label lblStatus = new Label();
        private CheckBox chkAutoPlay;
        private Label lblTrendState=new Label();
        private Label lblPlaySpeed=new Label();
        private ComboBox cmbPlaySpeed=new ComboBox();
        private Button btnRewind=new Button();
        private Button btnStepForward=new Button();
        private CheckBox chkAutoFitY=new CheckBox();
        private CheckBox chkEnableStrategy=new CheckBox();
    }
}
