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
        private System.Windows.Forms.GroupBox grpStrategy;
        private System.Windows.Forms.CheckBox chkShowPivots;
        private System.Windows.Forms.CheckBox chkShowTrendLines;
        private System.Windows.Forms.CheckBox chkEnableStrategy;
        private System.Windows.Forms.CheckBox chkAutoScale;
        private System.Windows.Forms.Button btnResetView;
        private System.Windows.Forms.Label lblActiveLines;
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
            this.grpStrategy = new System.Windows.Forms.GroupBox();
            this.btnResetView = new System.Windows.Forms.Button();
            this.chkAutoScale = new System.Windows.Forms.CheckBox();
            this.lblActiveLines = new System.Windows.Forms.Label();
            this.chkEnableStrategy = new System.Windows.Forms.CheckBox();
            this.chkShowTrendLines = new System.Windows.Forms.CheckBox();
            this.chkShowPivots = new System.Windows.Forms.CheckBox();
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
            this.grpStrategy.SuspendLayout();
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
            this.splitContainerLeft.Size = new System.Drawing.Size(754, 761);
            this.splitContainerLeft.SplitterDistance = 560;
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
            this.formsPlot1.Size = new System.Drawing.Size(754, 560);
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
            this.grpLogs.Size = new System.Drawing.Size(738, 188);
            this.grpLogs.TabIndex = 0;
            this.grpLogs.TabStop = false;
            this.grpLogs.Text = "运行日志与策略信号追踪 (UTC+0)";
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
            this.txtLogs.Size = new System.Drawing.Size(726, 134);
            this.txtLogs.TabIndex = 1;
            // 
            // panelLogTools
            // 
            this.panelLogTools.Controls.Add(this.chkAutoScroll);
            this.panelLogTools.Controls.Add(this.btnClearLogs);
            this.panelLogTools.Dock = System.Windows.Forms.DockStyle.Top;
            this.panelLogTools.Location = new System.Drawing.Point(6, 22);
            this.panelLogTools.Name = "panelLogTools";
            this.panelLogTools.Size = new System.Drawing.Size(726, 26);
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
            this.btnClearLogs.Location = new System.Drawing.Point(648, 2);
            this.btnClearLogs.Name = "btnClearLogs";
            this.btnClearLogs.Size = new System.Drawing.Size(75, 23);
            this.btnClearLogs.TabIndex = 0;
            this.btnClearLogs.Text = "清空日志";
            this.btnClearLogs.UseVisualStyleBackColor = true;
            this.btnClearLogs.Click += new System.EventHandler(this.btnClearLogs_Click);
            // 
            // panelRight
            // 
            this.panelRight.AutoScroll = true;
            this.panelRight.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(249)))), ((int)(((byte)(250)))));
            this.panelRight.Controls.Add(this.grpStatus);
            this.panelRight.Controls.Add(this.grpControls);
            this.panelRight.Controls.Add(this.grpStrategy);
            this.panelRight.Controls.Add(this.grpDateRange);
            this.panelRight.Controls.Add(this.grpSettings);
            this.panelRight.Dock = System.Windows.Forms.DockStyle.Right;
            this.panelRight.Location = new System.Drawing.Point(754, 0);
            this.panelRight.Name = "panelRight";
            this.panelRight.Padding = new System.Windows.Forms.Padding(12);
            this.panelRight.Size = new System.Drawing.Size(330, 761);
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
            this.grpStatus.Location = new System.Drawing.Point(12, 642);
            this.grpStatus.Name = "grpStatus";
            this.grpStatus.Padding = new System.Windows.Forms.Padding(10);
            this.grpStatus.Size = new System.Drawing.Size(306, 175);
            this.grpStatus.TabIndex = 4;
            this.grpStatus.TabStop = false;
            this.grpStatus.Text = "当前行情与游标 (UTC+0)";
            // 
            // lblBuffer
            // 
            this.lblBuffer.AutoSize = true;
            this.lblBuffer.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblBuffer.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(41)))), ((int)(((byte)(128)))), ((int)(((byte)(185)))));
            this.lblBuffer.Location = new System.Drawing.Point(12, 146);
            this.lblBuffer.Name = "lblBuffer";
            this.lblBuffer.Size = new System.Drawing.Size(126, 17);
            this.lblBuffer.TabIndex = 5;
            this.lblBuffer.Text = "100条缓存: 0 / 100";
            // 
            // lblVolume
            // 
            this.lblVolume.AutoSize = true;
            this.lblVolume.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblVolume.Location = new System.Drawing.Point(12, 122);
            this.lblVolume.Name = "lblVolume";
            this.lblVolume.Size = new System.Drawing.Size(59, 17);
            this.lblVolume.TabIndex = 4;
            this.lblVolume.Text = "成交量: --";
            // 
            // lblHighLow
            // 
            this.lblHighLow.AutoSize = true;
            this.lblHighLow.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblHighLow.Location = new System.Drawing.Point(12, 98);
            this.lblHighLow.Name = "lblHighLow";
            this.lblHighLow.Size = new System.Drawing.Size(76, 17);
            this.lblHighLow.TabIndex = 3;
            this.lblHighLow.Text = "高 / 低: -- / --";
            // 
            // lblPrice
            // 
            this.lblPrice.AutoSize = true;
            this.lblPrice.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.lblPrice.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(41)))), ((int)(((byte)(128)))), ((int)(((byte)(185)))));
            this.lblPrice.Location = new System.Drawing.Point(12, 72);
            this.lblPrice.Name = "lblPrice";
            this.lblPrice.Size = new System.Drawing.Size(97, 19);
            this.lblPrice.TabIndex = 2;
            this.lblPrice.Text = "最新收盘价: --";
            // 
            // lblTime
            // 
            this.lblTime.AutoSize = true;
            this.lblTime.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblTime.Location = new System.Drawing.Point(12, 48);
            this.lblTime.Name = "lblTime";
            this.lblTime.Size = new System.Drawing.Size(47, 17);
            this.lblTime.TabIndex = 1;
            this.lblTime.Text = "时间: --";
            // 
            // lblProgress
            // 
            this.lblProgress.AutoSize = true;
            this.lblProgress.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblProgress.Location = new System.Drawing.Point(12, 24);
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
            this.grpControls.Location = new System.Drawing.Point(12, 454);
            this.grpControls.Name = "grpControls";
            this.grpControls.Padding = new System.Windows.Forms.Padding(10);
            this.grpControls.Size = new System.Drawing.Size(306, 188);
            this.grpControls.TabIndex = 3;
            this.grpControls.TabStop = false;
            this.grpControls.Text = "回放控制";
            // 
            // btnReset
            // 
            this.btnReset.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(236)))), ((int)(((byte)(240)))), ((int)(((byte)(241)))));
            this.btnReset.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnReset.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnReset.Location = new System.Drawing.Point(12, 146);
            this.btnReset.Name = "btnReset";
            this.btnReset.Size = new System.Drawing.Size(282, 30);
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
            this.btnStepNext.Location = new System.Drawing.Point(156, 106);
            this.btnStepNext.Name = "btnStepNext";
            this.btnStepNext.Size = new System.Drawing.Size(138, 32);
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
            this.btnStepPrev.Location = new System.Drawing.Point(12, 106);
            this.btnStepPrev.Name = "btnStepPrev";
            this.btnStepPrev.Size = new System.Drawing.Size(138, 32);
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
            this.btnStartPause.Location = new System.Drawing.Point(12, 60);
            this.btnStartPause.Name = "btnStartPause";
            this.btnStartPause.Size = new System.Drawing.Size(282, 38);
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
            this.numSpeed.Location = new System.Drawing.Point(150, 26);
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
            this.lblSpeed.Location = new System.Drawing.Point(12, 28);
            this.lblSpeed.Name = "lblSpeed";
            this.lblSpeed.Size = new System.Drawing.Size(117, 17);
            this.lblSpeed.TabIndex = 0;
            this.lblSpeed.Text = "回放间隔 (毫秒/步):";
            // 
            // grpStrategy
            // 
            this.grpStrategy.Controls.Add(this.btnResetView);
            this.grpStrategy.Controls.Add(this.chkAutoScale);
            this.grpStrategy.Controls.Add(this.lblActiveLines);
            this.grpStrategy.Controls.Add(this.chkEnableStrategy);
            this.grpStrategy.Controls.Add(this.chkShowTrendLines);
            this.grpStrategy.Controls.Add(this.chkShowPivots);
            this.grpStrategy.Dock = System.Windows.Forms.DockStyle.Top;
            this.grpStrategy.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.grpStrategy.Location = new System.Drawing.Point(12, 288);
            this.grpStrategy.Name = "grpStrategy";
            this.grpStrategy.Padding = new System.Windows.Forms.Padding(10);
            this.grpStrategy.Size = new System.Drawing.Size(306, 166);
            this.grpStrategy.TabIndex = 2;
            this.grpStrategy.TabStop = false;
            this.grpStrategy.Text = "趋势线策略与图层配置";
            // 
            // btnResetView
            // 
            this.btnResetView.Font = new System.Drawing.Font("Microsoft YaHei UI", 8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnResetView.Location = new System.Drawing.Point(198, 126);
            this.btnResetView.Name = "btnResetView";
            this.btnResetView.Size = new System.Drawing.Size(96, 26);
            this.btnResetView.TabIndex = 5;
            this.btnResetView.Text = "🔍 自适应视图";
            this.btnResetView.UseVisualStyleBackColor = true;
            this.btnResetView.Click += new System.EventHandler(this.btnResetView_Click);
            // 
            // chkAutoScale
            // 
            this.chkAutoScale.AutoSize = true;
            this.chkAutoScale.Checked = true;
            this.chkAutoScale.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkAutoScale.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.chkAutoScale.Location = new System.Drawing.Point(12, 129);
            this.chkAutoScale.Name = "chkAutoScale";
            this.chkAutoScale.Size = new System.Drawing.Size(147, 21);
            this.chkAutoScale.TabIndex = 4;
            this.chkAutoScale.Text = "自动跟随最新视窗 (Lock)";
            this.chkAutoScale.UseVisualStyleBackColor = true;
            this.chkAutoScale.CheckedChanged += new System.EventHandler(this.chkAutoScale_CheckedChanged);
            // 
            // lblActiveLines
            // 
            this.lblActiveLines.AutoSize = true;
            this.lblActiveLines.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblActiveLines.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(142)))), ((int)(((byte)(68)))), ((int)(((byte)(173)))));
            this.lblActiveLines.Location = new System.Drawing.Point(12, 102);
            this.lblActiveLines.Name = "lblActiveLines";
            this.lblActiveLines.Size = new System.Drawing.Size(126, 17);
            this.lblActiveLines.TabIndex = 3;
            this.lblActiveLines.Text = "监控中存活趋势线: 0 条";
            // 
            // chkEnableStrategy
            // 
            this.chkEnableStrategy.AutoSize = true;
            this.chkEnableStrategy.Checked = true;
            this.chkEnableStrategy.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkEnableStrategy.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.chkEnableStrategy.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(192)))), ((int)(((byte)(57)))), ((int)(((byte)(43)))));
            this.chkEnableStrategy.Location = new System.Drawing.Point(12, 75);
            this.chkEnableStrategy.Name = "chkEnableStrategy";
            this.chkEnableStrategy.Size = new System.Drawing.Size(248, 21);
            this.chkEnableStrategy.TabIndex = 2;
            this.chkEnableStrategy.Text = "接入并运行趋势线Tick回弹策略 (Strategy)";
            this.chkEnableStrategy.UseVisualStyleBackColor = true;
            this.chkEnableStrategy.CheckedChanged += new System.EventHandler(this.chkStrategy_CheckedChanged);
            // 
            // chkShowTrendLines
            // 
            this.chkShowTrendLines.AutoSize = true;
            this.chkShowTrendLines.Checked = true;
            this.chkShowTrendLines.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkShowTrendLines.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.chkShowTrendLines.Location = new System.Drawing.Point(12, 49);
            this.chkShowTrendLines.Name = "chkShowTrendLines";
            this.chkShowTrendLines.Size = new System.Drawing.Size(248, 21);
            this.chkShowTrendLines.TabIndex = 1;
            this.chkShowTrendLines.Text = "显示趋势线 (🔴高点阻力线 / 🟢低点支撑线)";
            this.chkShowTrendLines.UseVisualStyleBackColor = true;
            this.chkShowTrendLines.CheckedChanged += new System.EventHandler(this.chkLayer_CheckedChanged);
            // 
            // chkShowPivots
            // 
            this.chkShowPivots.AutoSize = true;
            this.chkShowPivots.Checked = true;
            this.chkShowPivots.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkShowPivots.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.chkShowPivots.Location = new System.Drawing.Point(12, 23);
            this.chkShowPivots.Name = "chkShowPivots";
            this.chkShowPivots.Size = new System.Drawing.Size(225, 21);
            this.chkShowPivots.TabIndex = 0;
            this.chkShowPivots.Text = "在图表标记高低极值点 (Pivot Point)";
            this.chkShowPivots.UseVisualStyleBackColor = true;
            this.chkShowPivots.CheckedChanged += new System.EventHandler(this.chkLayer_CheckedChanged);
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
            this.grpDateRange.Location = new System.Drawing.Point(12, 116);
            this.grpDateRange.Name = "grpDateRange";
            this.grpDateRange.Padding = new System.Windows.Forms.Padding(10);
            this.grpDateRange.Size = new System.Drawing.Size(306, 172);
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
            this.btnLoadData.Location = new System.Drawing.Point(12, 122);
            this.btnLoadData.Name = "btnLoadData";
            this.btnLoadData.Size = new System.Drawing.Size(282, 38);
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
            this.dtpEndDate.Location = new System.Drawing.Point(82, 78);
            this.dtpEndDate.Name = "dtpEndDate";
            this.dtpEndDate.Size = new System.Drawing.Size(212, 23);
            this.dtpEndDate.TabIndex = 3;
            // 
            // lblEndDate
            // 
            this.lblEndDate.AutoSize = true;
            this.lblEndDate.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblEndDate.Location = new System.Drawing.Point(12, 81);
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
            this.dtpStartDate.Location = new System.Drawing.Point(82, 34);
            this.dtpStartDate.Name = "dtpStartDate";
            this.dtpStartDate.Size = new System.Drawing.Size(212, 23);
            this.dtpStartDate.TabIndex = 1;
            // 
            // lblStartDate
            // 
            this.lblStartDate.AutoSize = true;
            this.lblStartDate.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblStartDate.Location = new System.Drawing.Point(12, 37);
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
            this.grpSettings.Size = new System.Drawing.Size(306, 104);
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
            this.cboInterval.Location = new System.Drawing.Point(82, 66);
            this.cboInterval.Name = "cboInterval";
            this.cboInterval.Size = new System.Drawing.Size(212, 25);
            this.cboInterval.TabIndex = 3;
            this.cboInterval.SelectedIndexChanged += new System.EventHandler(this.cboInterval_SelectedIndexChanged);
            // 
            // lblInterval
            // 
            this.lblInterval.AutoSize = true;
            this.lblInterval.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblInterval.Location = new System.Drawing.Point(12, 69);
            this.lblInterval.Name = "lblInterval";
            this.lblInterval.Size = new System.Drawing.Size(59, 17);
            this.lblInterval.TabIndex = 2;
            this.lblInterval.Text = "K线周期:";
            // 
            // txtSymbol
            // 
            this.txtSymbol.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.txtSymbol.Location = new System.Drawing.Point(82, 26);
            this.txtSymbol.Name = "txtSymbol";
            this.txtSymbol.Size = new System.Drawing.Size(212, 23);
            this.txtSymbol.TabIndex = 1;
            this.txtSymbol.Text = "BTCUSDT";
            // 
            // lblSymbol
            // 
            this.lblSymbol.AutoSize = true;
            this.lblSymbol.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblSymbol.Location = new System.Drawing.Point(12, 29);
            this.lblSymbol.Name = "lblSymbol";
            this.lblSymbol.Size = new System.Drawing.Size(47, 17);
            this.lblSymbol.TabIndex = 0;
            this.lblSymbol.Text = "交易对:";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1084, 761);
            this.Controls.Add(this.splitContainerLeft);
            this.Controls.Add(this.panelRight);
            this.MinimumSize = new System.Drawing.Size(900, 650);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Binance 行情数据回放系统 - ScottPlot 趋势线策略与极值点呈现";
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
            this.grpStrategy.ResumeLayout(false);
            this.grpStrategy.PerformLayout();
            this.grpDateRange.ResumeLayout(false);
            this.grpDateRange.PerformLayout();
            this.grpSettings.ResumeLayout(false);
            this.grpSettings.PerformLayout();
            this.ResumeLayout(false);

        }
    }
}
