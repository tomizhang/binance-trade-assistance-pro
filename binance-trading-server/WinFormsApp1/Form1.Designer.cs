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
            formsPlot1.Size = new Size(795, 713);
            formsPlot1.TabIndex = 0;
            // 
            // button1
            // 
            button1.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            button1.Location = new Point(820, 680);
            button1.Name = "button1";
            button1.Size = new Size(185, 30);
            button1.TabIndex = 1;
            button1.Text = "重置/复位图表";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // btn_puase
            // 
            btn_puase.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btn_puase.Location = new Point(820, 640);
            btn_puase.Name = "btn_puase";
            btn_puase.Size = new Size(185, 30);
            btn_puase.TabIndex = 2;
            btn_puase.Text = "暂停数据播放";
            btn_puase.UseVisualStyleBackColor = true;
            btn_puase.Click += btn_puase_Click;
            // 
            // lblSymbol
            // 
            lblSymbol.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblSymbol.AutoSize = true;
            lblSymbol.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblSymbol.Location = new Point(820, 15);
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
            cmbSymbol.Location = new Point(820, 35);
            cmbSymbol.Name = "cmbSymbol";
            cmbSymbol.Size = new Size(185, 25);
            cmbSymbol.TabIndex = 4;
            // 
            // lblInterval
            // 
            lblInterval.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblInterval.AutoSize = true;
            lblInterval.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblInterval.Location = new Point(820, 70);
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
            cmbInterval.Location = new Point(820, 90);
            cmbInterval.Name = "cmbInterval";
            cmbInterval.Size = new Size(185, 25);
            cmbInterval.TabIndex = 6;
            // 
            // lblTimeRange
            // 
            lblTimeRange.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblTimeRange.AutoSize = true;
            lblTimeRange.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblTimeRange.Location = new Point(820, 125);
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
            cmbTimeRange.Location = new Point(820, 145);
            cmbTimeRange.Name = "cmbTimeRange";
            cmbTimeRange.Size = new Size(185, 25);
            cmbTimeRange.TabIndex = 8;
            // 
            // btnFetch
            // 
            btnFetch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFetch.BackColor = Color.FromArgb(0, 122, 204);
            btnFetch.FlatStyle = FlatStyle.Flat;
            btnFetch.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            btnFetch.ForeColor = Color.White;
            btnFetch.Location = new Point(820, 185);
            btnFetch.Name = "btnFetch";
            btnFetch.Size = new Size(185, 35);
            btnFetch.TabIndex = 9;
            btnFetch.Text = "获取币种历史数据";
            btnFetch.UseVisualStyleBackColor = false;
            btnFetch.Click += btnFetch_Click;
            // 
            // lblStatus
            // 
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblStatus.BackColor = Color.FromArgb(240, 240, 240);
            lblStatus.BorderStyle = BorderStyle.FixedSingle;
            lblStatus.Font = new Font("Microsoft YaHei UI", 8.5F);
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Location = new Point(820, 230);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(185, 120);
            lblStatus.TabIndex = 10;
            lblStatus.Text = "就绪: 请选择币种与周期后点击【获取币种历史数据】";
            // 
            // lblPlaySpeed
            // 
            lblPlaySpeed.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblPlaySpeed.AutoSize = true;
            lblPlaySpeed.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblPlaySpeed.Location = new Point(820, 390);
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
            cmbPlaySpeed.Location = new Point(820, 410);
            cmbPlaySpeed.Name = "cmbPlaySpeed";
            cmbPlaySpeed.Size = new Size(185, 25);
            cmbPlaySpeed.TabIndex = 13;
            // 
            // lblTrendState
            // 
            lblTrendState.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
            lblTrendState.BorderStyle = BorderStyle.FixedSingle;
            lblTrendState.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            lblTrendState.ForeColor = Color.DimGray;
            lblTrendState.Location = new Point(820, 450);
            lblTrendState.Name = "lblTrendState";
            lblTrendState.Size = new Size(185, 45);
            lblTrendState.TabIndex = 14;
            lblTrendState.Text = "趋势状态: 计算中...";
            lblTrendState.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1027, 737);
            Controls.Add(lblTrendState);
            Controls.Add(cmbPlaySpeed);
            Controls.Add(lblPlaySpeed);
            Controls.Add(chkAutoPlay);
            Controls.Add(lblStatus);
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
        private Label lblStatus;
        private CheckBox chkAutoPlay;
        private Label lblTrendState =new Label();
        private Label lblPlaySpeed = new Label();
        private ComboBox cmbPlaySpeed=new ComboBox();
    }
}
