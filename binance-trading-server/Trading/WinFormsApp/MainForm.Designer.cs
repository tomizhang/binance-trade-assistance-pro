namespace WinFormsApp
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.SplitContainer splitContainerLeft;
        private ScottPlot.WinForms.FormsPlot formsPlot1;
        private System.Windows.Forms.GroupBox grpLogs;
        private System.Windows.Forms.TextBox txtLogs;
        private System.Windows.Forms.Panel panelLogTools;
        private System.Windows.Forms.CheckBox chkAutoScroll;
        private System.Windows.Forms.Button btnClearLogs;
        private System.Windows.Forms.Panel panelRight;
        private System.Windows.Forms.GroupBox grpSettings;
        private System.Windows.Forms.Label lblSymbol;
        private System.Windows.Forms.TextBox txtSymbol;
        private System.Windows.Forms.Label lblInterval;
        private System.Windows.Forms.ComboBox cboInterval;
        private System.Windows.Forms.GroupBox grpDateRange;
        private System.Windows.Forms.Label lblStartDate;
        private System.Windows.Forms.DateTimePicker dtpStartDate;
        private System.Windows.Forms.Label lblEndDate;
        private System.Windows.Forms.DateTimePicker dtpEndDate;
        private System.Windows.Forms.Button btnLoadData;
        private System.Windows.Forms.GroupBox grpControls;
        private System.Windows.Forms.Label lblSpeed;
        private System.Windows.Forms.NumericUpDown numSpeed;
        private System.Windows.Forms.Button btnStartPause;
        private System.Windows.Forms.Button btnStepPrev;
        private System.Windows.Forms.Button btnStepNext;
        private System.Windows.Forms.Button btnReset;
        private System.Windows.Forms.GroupBox grpStatus;
        private System.Windows.Forms.Label lblProgress;
        private System.Windows.Forms.Label lblTime;
        private System.Windows.Forms.Label lblPrice;
        private System.Windows.Forms.Label lblHighLow;
        private System.Windows.Forms.Label lblVolume;
        private System.Windows.Forms.Label lblBuffer;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.splitContainerLeft = new System.Windows.Forms.SplitContainer();
            this.formsPlot1 = new ScottPlot.WinForms.FormsPlot();
            this.grpLogs = new System.Windows.Forms.GroupBox();
            this.txtLogs = new System.Windows.Forms.TextBox();
            this.panelLogTools = new System.Windows.Forms.Panel();
            this.chkAutoScroll = new System.Windows.Forms.CheckBox();
            this.btnClearLogs = new System.Windows.Forms.Button();
            this.panelRight = new System.Windows.Forms.Panel();
            this.grpStatus = new System.Windows.Forms.GroupBox();
            this.lblBuffer = new System.Windows.Forms.Label();
            this.lblVolume = new System.Windows.Forms.Label();
            this.lblHighLow = new System.Windows.Forms.Label();
            this.lblPrice = new System.Windows.Forms.Label();
            this.lblTime = new System.Windows.Forms.Label();
            this.lblProgress = new System.Windows.Forms.Label();
            this.grpControls = new System.Windows.Forms.GroupBox();
            this.btnReset = new System.Windows.Forms.Button();
            this.btnStepNext = new System.Windows.Forms.Button();
            this.btnStepPrev = new System.Windows.Forms.Button();
            this.btnStartPause = new System.Windows.Forms.Button();
            this.numSpeed = new System.Windows.Forms.NumericUpDown();
            this.lblSpeed = new System.Windows.Forms.Label();
            this.grpDateRange = new System.Windows.Forms.GroupBox();
            this.btnLoadData = new System.Windows.Forms.Button();
            this.dtpEndDate = new System.Windows.Forms.DateTimePicker();
            this.lblEndDate = new System.Windows.Forms.Label();
            this.dtpStartDate = new System.Windows.Forms.DateTimePicker();
            this.lblStartDate = new System.Windows.Forms.Label();
            this.grpSettings = new System.Windows.Forms.GroupBox();
            this.cboInterval = new System.Windows.Forms.ComboBox();
            this.lblInterval = new System.Windows.Forms.Label();
            this.txtSymbol = new System.Windows.Forms.TextBox();
            this.lblSymbol = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainerLeft)).BeginInit();
            this.splitContainerLeft.Panel1.SuspendLayout();
            this.splitContainerLeft.Panel2.SuspendLayout();
            this.splitContainerLeft.SuspendLayout();
            this.grpLogs.SuspendLayout();
            this.panelLogTools.SuspendLayout();
            this.panelRight.SuspendLayout();
            this.grpStatus.SuspendLayout();
            this.grpControls.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numSpeed)).BeginInit();
            this.grpDateRange.SuspendLayout();
            this.grpSettings.SuspendLayout();
            this.SuspendLayout();
            // 
            // splitContainerLeft
            // 
            this.splitContainerLeft.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainerLeft.Location = new System.Drawing.Point(0, 0);
            this.splitContainerLeft.Name = "splitContainerLeft";
            this.splitContainerLeft.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainerLeft.Panel1
            // 
            this.splitContainerLeft.Panel1.Controls.Add(this.formsPlot1);
            // 
            // splitContainerLeft.Panel2
            // 
            this.splitContainerLeft.Panel2.Controls.Add(this.grpLogs);
            this.splitContainerLeft.Panel2.Padding = new System.Windows.Forms.Padding(8, 0, 8, 8);
            this.splitContainerLeft.Size = new System.Drawing.Size(764, 721);
            this.splitContainerLeft.SplitterDistance = 530;
            this.splitContainerLeft.SplitterWidth = 5;
            this.splitContainerLeft.TabIndex = 0;
            // 
            // formsPlot1
            // 
            this.formsPlot1.DisplayScale = 1F;
            this.formsPlot1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.formsPlot1.Location = new System.Drawing.Point(0, 0);
            this.formsPlot1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.formsPlot1.Name = "formsPlot1";
            this.formsPlot1.Size = new System.Drawing.Size(764, 530);
            this.formsPlot1.TabIndex = 0;
            // 
            // grpLogs
            // 
            this.grpLogs.Controls.Add(this.txtLogs);
            this.grpLogs.Controls.Add(this.panelLogTools);
            this.grpLogs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpLogs.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.grpLogs.Location = new System.Drawing.Point(8, 0);
            this.grpLogs.Name = "grpLogs";
            this.grpLogs.Padding = new System.Windows.Forms.Padding(6);
            this.grpLogs.Size = new System.Drawing.Size(748, 178);
            this.grpLogs.TabIndex = 0;
            this.grpLogs.TabStop = false;
            this.grpLogs.Text = "运行日志与事件追踪 (UTC+0)";
            // 
            // txtLogs
            // 
            this.txtLogs.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(250)))), ((int)(((byte)(252)))));
            this.txtLogs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtLogs.Font = new System.Drawing.Font("Consolas", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.txtLogs.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(44)))), ((int)(((byte)(62)))), ((int)(((byte)(80)))));
            this.txtLogs.Location = new System.Drawing.Point(6, 48);
            this.txtLogs.Multiline = true;
            this.txtLogs.Name = "txtLogs";
            this.txtLogs.ReadOnly = true;
            this.txtLogs.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLogs.Size = new System.Drawing.Size(736, 124);
            this.txtLogs.TabIndex = 1;
            // 
            // panelLogTools
            // 
            this.panelLogTools.Controls.Add(this.chkAutoScroll);
            this.panelLogTools.Controls.Add(this.btnClearLogs);
            this.panelLogTools.Dock = System.Windows.Forms.DockStyle.Top;
            this.panelLogTools.Location = new System.Drawing.Point(6, 22);
            this.panelLogTools.Name = "panelLogTools";
            this.panelLogTools.Size = new System.Drawing.Size(736, 26);
            this.panelLogTools.TabIndex = 0;
            // 
            // chkAutoScroll
            // 
            this.chkAutoScroll.AutoSize = true;
            this.chkAutoScroll.Checked = true;
            this.chkAutoScroll.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkAutoScroll.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.chkAutoScroll.Location = new System.Drawing.Point(4, 3);
            this.chkAutoScroll.Name = "chkAutoScroll";
            this.chkAutoScroll.Size = new System.Drawing.Size(75, 21);
            this.chkAutoScroll.TabIndex = 1;
            this.chkAutoScroll.Text = "自动滚屏";
            this.chkAutoScroll.UseVisualStyleBackColor = true;
            // 
            // btnClearLogs
            // 
            this.btnClearLogs.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnClearLogs.Font = new System.Drawing.Font("Microsoft YaHei UI", 8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnClearLogs.Location = new System.Drawing.Point(658, 2);
            this.btnClearLogs.Name = "btnClearLogs";
            this.btnClearLogs.Size = new System.Drawing.Size(75, 23);
            this.btnClearLogs.TabIndex = 0;
            this.btnClearLogs.Text = "清空日志";
            this.btnClearLogs.UseVisualStyleBackColor = true;
            this.btnClearLogs.Click += new System.EventHandler(this.btnClearLogs_Click);
            // 
            // panelRight
            // 
            this.panelRight.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(249)))), ((int)(((byte)(250)))));
            this.panelRight.Controls.Add(this.grpStatus);
            this.panelRight.Controls.Add(this.grpControls);
            this.panelRight.Controls.Add(this.grpDateRange);
            this.panelRight.Controls.Add(this.grpSettings);
            this.panelRight.Dock = System.Windows.Forms.DockStyle.Right;
            this.panelRight.Location = new System.Drawing.Point(764, 0);
            this.panelRight.Name = "panelRight";
            this.panelRight.Padding = new System.Windows.Forms.Padding(12);
            this.panelRight.Size = new System.Drawing.Size(320, 721);
            this.panelRight.TabIndex = 1;
            // 
            // grpStatus
            // 
            this.grpStatus.Controls.Add(this.lblBuffer);
            this.grpStatus.Controls.Add(this.lblVolume);
            this.grpStatus.Controls.Add(this.lblHighLow);
            this.grpStatus.Controls.Add(this.lblPrice);
            this.grpStatus.Controls.Add(this.lblTime);
            this.grpStatus.Controls.Add(this.lblProgress);
            this.grpStatus.Dock = System.Windows.Forms.DockStyle.Top;
            this.grpStatus.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.grpStatus.Location = new System.Drawing.Point(12, 502);
            this.grpStatus.Name = "grpStatus";
            this.grpStatus.Padding = new System.Windows.Forms.Padding(10);
            this.grpStatus.Size = new System.Drawing.Size(296, 195);
            this.grpStatus.TabIndex = 3;
            this.grpStatus.TabStop = false;
            this.grpStatus.Text = "当前行情与游标 (UTC+0)";
            // 
            // lblBuffer
            // 
            this.lblBuffer.AutoSize = true;
            this.lblBuffer.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblBuffer.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(41)))), ((int)(((byte)(128)))), ((int)(((byte)(185)))));
            this.lblBuffer.Location = new System.Drawing.Point(12, 162);
            this.lblBuffer.Name = "lblBuffer";
            this.lblBuffer.Size = new System.Drawing.Size(126, 17);
            this.lblBuffer.TabIndex = 5;
            this.lblBuffer.Text = "100条缓存: 0 / 100";
            // 
            // lblVolume
            // 
            this.lblVolume.AutoSize = true;
            this.lblVolume.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblVolume.Location = new System.Drawing.Point(12, 136);
            this.lblVolume.Name = "lblVolume";
            this.lblVolume.Size = new System.Drawing.Size(59, 17);
            this.lblVolume.TabIndex = 4;
            this.lblVolume.Text = "成交量: --";
            // 
            // lblHighLow
            // 
            this.lblHighLow.AutoSize = true;
            this.lblHighLow.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblHighLow.Location = new System.Drawing.Point(12, 110);
            this.lblHighLow.Name = "lblHighLow";
            this.lblHighLow.Size = new System.Drawing.Size(76, 17);
            this.lblHighLow.TabIndex = 3;
            this.lblHighLow.Text = "高 / 低: -- / --";
            // 
            // lblPrice
            // 
            this.lblPrice.AutoSize = true;
            this.lblPrice.Font = new System.Drawing.Font("Microsoft YaHei UI", 10.5F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.lblPrice.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(41)))), ((int)(((byte)(128)))), ((int)(((byte)(185)))));
            this.lblPrice.Location = new System.Drawing.Point(12, 80);
            this.lblPrice.Name = "lblPrice";
            this.lblPrice.Size = new System.Drawing.Size(97, 19);
            this.lblPrice.TabIndex = 2;
            this.lblPrice.Text = "最新收盘价: --";
            // 
            // lblTime
            // 
            this.lblTime.AutoSize = true;
            this.lblTime.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblTime.Location = new System.Drawing.Point(12, 54);
            this.lblTime.Name = "lblTime";
            this.lblTime.Size = new System.Drawing.Size(47, 17);
            this.lblTime.TabIndex = 1;
            this.lblTime.Text = "时间: --";
            // 
            // lblProgress
            // 
            this.lblProgress.AutoSize = true;
            this.lblProgress.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblProgress.Location = new System.Drawing.Point(12, 28);
            this.lblProgress.Name = "lblProgress";
            this.lblProgress.Size = new System.Drawing.Size(64, 17);
            this.lblProgress.TabIndex = 0;
            this.lblProgress.Text = "进度: 0 / 0";
            // 
            // grpControls
            // 
            this.grpControls.Controls.Add(this.btnReset);
            this.grpControls.Controls.Add(this.btnStepNext);
            this.grpControls.Controls.Add(this.btnStepPrev);
            this.grpControls.Controls.Add(this.btnStartPause);
            this.grpControls.Controls.Add(this.numSpeed);
            this.grpControls.Controls.Add(this.lblSpeed);
            this.grpControls.Dock = System.Windows.Forms.DockStyle.Top;
            this.grpControls.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.grpControls.Location = new System.Drawing.Point(12, 298);
            this.grpControls.Name = "grpControls";
            this.grpControls.Padding = new System.Windows.Forms.Padding(10);
            this.grpControls.Size = new System.Drawing.Size(296, 204);
            this.grpControls.TabIndex = 2;
            this.grpControls.TabStop = false;
            this.grpControls.Text = "回放控制";
            // 
            // btnReset
            // 
            this.btnReset.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(236)))), ((int)(((byte)(240)))), ((int)(((byte)(241)))));
            this.btnReset.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnReset.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnReset.Location = new System.Drawing.Point(12, 160);
            this.btnReset.Name = "btnReset";
            this.btnReset.Size = new System.Drawing.Size(272, 32);
            this.btnReset.TabIndex = 5;
            this.btnReset.Text = "⏹ 重置 (Reset)";
            this.btnReset.UseVisualStyleBackColor = false;
            this.btnReset.Click += new System.EventHandler(this.btnReset_Click);
            // 
            // btnStepNext
            // 
            this.btnStepNext.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(236)))), ((int)(((byte)(240)))), ((int)(((byte)(241)))));
            this.btnStepNext.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnStepNext.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnStepNext.Location = new System.Drawing.Point(152, 118);
            this.btnStepNext.Name = "btnStepNext";
            this.btnStepNext.Size = new System.Drawing.Size(132, 34);
            this.btnStepNext.TabIndex = 4;
            this.btnStepNext.Text = "⏭ 单步前进";
            this.btnStepNext.UseVisualStyleBackColor = false;
            this.btnStepNext.Click += new System.EventHandler(this.btnStepNext_Click);
            // 
            // btnStepPrev
            // 
            this.btnStepPrev.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(236)))), ((int)(((byte)(240)))), ((int)(((byte)(241)))));
            this.btnStepPrev.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnStepPrev.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnStepPrev.Location = new System.Drawing.Point(12, 118);
            this.btnStepPrev.Name = "btnStepPrev";
            this.btnStepPrev.Size = new System.Drawing.Size(132, 34);
            this.btnStepPrev.TabIndex = 3;
            this.btnStepPrev.Text = "⏮ 单步后退";
            this.btnStepPrev.UseVisualStyleBackColor = false;
            this.btnStepPrev.Click += new System.EventHandler(this.btnStepPrev_Click);
            // 
            // btnStartPause
            // 
            this.btnStartPause.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(52)))), ((int)(((byte)(152)))), ((int)(((byte)(219)))));
            this.btnStartPause.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnStartPause.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.btnStartPause.ForeColor = System.Drawing.Color.White;
            this.btnStartPause.Location = new System.Drawing.Point(12, 68);
            this.btnStartPause.Name = "btnStartPause";
            this.btnStartPause.Size = new System.Drawing.Size(272, 42);
            this.btnStartPause.TabIndex = 2;
            this.btnStartPause.Text = "▶ 开始回放";
            this.btnStartPause.UseVisualStyleBackColor = false;
            this.btnStartPause.Click += new System.EventHandler(this.btnStartPause_Click);
            // 
            // numSpeed
            // 
            this.numSpeed.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.numSpeed.Increment = new decimal(new int[] {
            50,
            0,
            0,
            0});
            this.numSpeed.Location = new System.Drawing.Point(140, 28);
            this.numSpeed.Maximum = new decimal(new int[] {
            2000,
            0,
            0,
            0});
            this.numSpeed.Minimum = new decimal(new int[] {
            10,
            0,
            0,
            0});
            this.numSpeed.Name = "numSpeed";
            this.numSpeed.Size = new System.Drawing.Size(144, 23);
            this.numSpeed.TabIndex = 1;
            this.numSpeed.Value = new decimal(new int[] {
            100,
            0,
            0,
            0});
            this.numSpeed.ValueChanged += new System.EventHandler(this.numSpeed_ValueChanged);
            // 
            // lblSpeed
            // 
            this.lblSpeed.AutoSize = true;
            this.lblSpeed.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblSpeed.Location = new System.Drawing.Point(12, 30);
            this.lblSpeed.Name = "lblSpeed";
            this.lblSpeed.Size = new System.Drawing.Size(117, 17);
            this.lblSpeed.TabIndex = 0;
            this.lblSpeed.Text = "回放间隔 (毫秒/步):";
            // 
            // grpDateRange
            // 
            this.grpDateRange.Controls.Add(this.btnLoadData);
            this.grpDateRange.Controls.Add(this.dtpEndDate);
            this.grpDateRange.Controls.Add(this.lblEndDate);
            this.grpDateRange.Controls.Add(this.dtpStartDate);
            this.grpDateRange.Controls.Add(this.lblStartDate);
            this.grpDateRange.Dock = System.Windows.Forms.DockStyle.Top;
            this.grpDateRange.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.grpDateRange.Location = new System.Drawing.Point(12, 120);
            this.grpDateRange.Name = "grpDateRange";
            this.grpDateRange.Padding = new System.Windows.Forms.Padding(10);
            this.grpDateRange.Size = new System.Drawing.Size(296, 178);
            this.grpDateRange.TabIndex = 1;
            this.grpDateRange.TabStop = false;
            this.grpDateRange.Text = "回放区间 (UTC+0)";
            // 
            // btnLoadData
            // 
            this.btnLoadData.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(46)))), ((int)(((byte)(204)))), ((int)(((byte)(113)))));
            this.btnLoadData.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnLoadData.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.btnLoadData.ForeColor = System.Drawing.Color.White;
            this.btnLoadData.Location = new System.Drawing.Point(12, 126);
            this.btnLoadData.Name = "btnLoadData";
            this.btnLoadData.Size = new System.Drawing.Size(272, 38);
            this.btnLoadData.TabIndex = 4;
            this.btnLoadData.Text = "📥 加载/检索历史数据";
            this.btnLoadData.UseVisualStyleBackColor = false;
            this.btnLoadData.Click += new System.EventHandler(this.btnLoadData_Click);
            // 
            // dtpEndDate
            // 
            this.dtpEndDate.CustomFormat = "yyyy-MM-dd";
            this.dtpEndDate.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.dtpEndDate.Format = System.Windows.Forms.DateTimePickerFormat.Custom;
            this.dtpEndDate.Location = new System.Drawing.Point(82, 80);
            this.dtpEndDate.Name = "dtpEndDate";
            this.dtpEndDate.Size = new System.Drawing.Size(202, 23);
            this.dtpEndDate.TabIndex = 3;
            // 
            // lblEndDate
            // 
            this.lblEndDate.AutoSize = true;
            this.lblEndDate.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblEndDate.Location = new System.Drawing.Point(12, 83);
            this.lblEndDate.Name = "lblEndDate";
            this.lblEndDate.Size = new System.Drawing.Size(59, 17);
            this.lblEndDate.TabIndex = 2;
            this.lblEndDate.Text = "结束日期:";
            // 
            // dtpStartDate
            // 
            this.dtpStartDate.CustomFormat = "yyyy-MM-dd";
            this.dtpStartDate.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.dtpStartDate.Format = System.Windows.Forms.DateTimePickerFormat.Custom;
            this.dtpStartDate.Location = new System.Drawing.Point(82, 36);
            this.dtpStartDate.Name = "dtpStartDate";
            this.dtpStartDate.Size = new System.Drawing.Size(202, 23);
            this.dtpStartDate.TabIndex = 1;
            // 
            // lblStartDate
            // 
            this.lblStartDate.AutoSize = true;
            this.lblStartDate.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblStartDate.Location = new System.Drawing.Point(12, 39);
            this.lblStartDate.Name = "lblStartDate";
            this.lblStartDate.Size = new System.Drawing.Size(59, 17);
            this.lblStartDate.TabIndex = 0;
            this.lblStartDate.Text = "开始日期:";
            // 
            // grpSettings
            // 
            this.grpSettings.Controls.Add(this.cboInterval);
            this.grpSettings.Controls.Add(this.lblInterval);
            this.grpSettings.Controls.Add(this.txtSymbol);
            this.grpSettings.Controls.Add(this.lblSymbol);
            this.grpSettings.Dock = System.Windows.Forms.DockStyle.Top;
            this.grpSettings.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.grpSettings.Location = new System.Drawing.Point(12, 12);
            this.grpSettings.Name = "grpSettings";
            this.grpSettings.Padding = new System.Windows.Forms.Padding(10);
            this.grpSettings.Size = new System.Drawing.Size(296, 108);
            this.grpSettings.TabIndex = 0;
            this.grpSettings.TabStop = false;
            this.grpSettings.Text = "行情配置";
            // 
            // cboInterval
            // 
            this.cboInterval.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cboInterval.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.cboInterval.FormattingEnabled = true;
            this.cboInterval.Items.AddRange(new object[] {
            "1m",
            "3m",
            "5m",
            "15m",
            "30m",
            "1h",
            "2h",
            "4h",
            "1d"});
            this.cboInterval.Location = new System.Drawing.Point(82, 68);
            this.cboInterval.Name = "cboInterval";
            this.cboInterval.Size = new System.Drawing.Size(202, 25);
            this.cboInterval.TabIndex = 3;
            // 
            // lblInterval
            // 
            this.lblInterval.AutoSize = true;
            this.lblInterval.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblInterval.Location = new System.Drawing.Point(12, 71);
            this.lblInterval.Name = "lblInterval";
            this.lblInterval.Size = new System.Drawing.Size(59, 17);
            this.lblInterval.TabIndex = 2;
            this.lblInterval.Text = "K线周期:";
            // 
            // txtSymbol
            // 
            this.txtSymbol.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.txtSymbol.Location = new System.Drawing.Point(82, 28);
            this.txtSymbol.Name = "txtSymbol";
            this.txtSymbol.Size = new System.Drawing.Size(202, 23);
            this.txtSymbol.TabIndex = 1;
            this.txtSymbol.Text = "BTCUSDT";
            // 
            // lblSymbol
            // 
            this.lblSymbol.AutoSize = true;
            this.lblSymbol.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblSymbol.Location = new System.Drawing.Point(12, 31);
            this.lblSymbol.Name = "lblSymbol";
            this.lblSymbol.Size = new System.Drawing.Size(47, 17);
            this.lblSymbol.TabIndex = 0;
            this.lblSymbol.Text = "交易对:";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1084, 721);
            this.Controls.Add(this.splitContainerLeft);
            this.Controls.Add(this.panelRight);
            this.MinimumSize = new System.Drawing.Size(800, 600);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Binance 行情数据回放系统 - ScottPlot (淡蓝色线条显示)";
            this.splitContainerLeft.Panel1.ResumeLayout(false);
            this.splitContainerLeft.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainerLeft)).EndInit();
            this.splitContainerLeft.ResumeLayout(false);
            this.grpLogs.ResumeLayout(false);
            this.grpLogs.PerformLayout();
            this.panelLogTools.ResumeLayout(false);
            this.panelLogTools.PerformLayout();
            this.panelRight.ResumeLayout(false);
            this.grpStatus.ResumeLayout(false);
            this.grpStatus.PerformLayout();
            this.grpControls.ResumeLayout(false);
            this.grpControls.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numSpeed)).EndInit();
            this.grpDateRange.ResumeLayout(false);
            this.grpDateRange.PerformLayout();
            this.grpSettings.ResumeLayout(false);
            this.grpSettings.PerformLayout();
            this.ResumeLayout(false);

        }
    }
}
