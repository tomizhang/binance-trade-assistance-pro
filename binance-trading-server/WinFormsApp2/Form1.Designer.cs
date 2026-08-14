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
            btnStart = new Button();
            grpParams = new GroupBox();
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
            grpParams.Size = new Size(284, 340);
            grpParams.TabIndex = 0;
            grpParams.TabStop = false;
            grpParams.Text = "参数设置 (Parameters)";
            // 
            // lblSymbol
            // 
            lblSymbol.AutoSize = true;
            lblSymbol.Location = new Point(16, 25);
            lblSymbol.Name = "lblSymbol";
            lblSymbol.Size = new Size(51, 17);
            lblSymbol.TabIndex = 0;
            lblSymbol.Text = "交易对:";
            // 
            // txtSymbol
            // 
            txtSymbol.Location = new Point(16, 45);
            txtSymbol.Name = "txtSymbol";
            txtSymbol.Size = new Size(248, 23);
            txtSymbol.TabIndex = 1;
            txtSymbol.Text = "BTCUSDT";
            // 
            // lblKlineInterval
            // 
            lblKlineInterval.AutoSize = true;
            lblKlineInterval.Location = new Point(16, 75);
            lblKlineInterval.Name = "lblKlineInterval";
            lblKlineInterval.Size = new Size(87, 17);
            lblKlineInterval.TabIndex = 2;
            lblKlineInterval.Text = "K线周期:";
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
            // lblStartDate
            // 
            lblStartDate.AutoSize = true;
            lblStartDate.Location = new Point(16, 128);
            lblStartDate.Name = "lblStartDate";
            lblStartDate.Size = new Size(116, 17);
            lblStartDate.TabIndex = 4;
            lblStartDate.Text = "开始日期 (最小单位: 天):";
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
            // lblEndDate
            // 
            lblEndDate.AutoSize = true;
            lblEndDate.Location = new Point(16, 178);
            lblEndDate.Name = "lblEndDate";
            lblEndDate.Size = new Size(116, 17);
            lblEndDate.TabIndex = 6;
            lblEndDate.Text = "结束日期 (最小单位: 天):";
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
            // lblInterval
            // 
            lblInterval.AutoSize = true;
            lblInterval.Location = new Point(16, 228);
            lblInterval.Name = "lblInterval";
            lblInterval.Size = new Size(87, 17);
            lblInterval.TabIndex = 8;
            lblInterval.Text = "刷新间隔 (ms):";
            // 
            // numInterval
            // 
            numInterval.Location = new Point(16, 248);
            numInterval.Maximum = new decimal(new int[] { 60000, 0, 0, 0 });
            numInterval.Minimum = new decimal(new int[] { 100, 0, 0, 0 });
            numInterval.Name = "numInterval";
            numInterval.Size = new Size(248, 23);
            numInterval.TabIndex = 9;
            numInterval.Value = new decimal(new int[] { 1000, 0, 0, 0 });
            // 
            // grpActions
            // 
            grpActions.Controls.Add(btnClearLog);
            grpActions.Controls.Add(btnStop);
            grpActions.Controls.Add(btnStart);
            grpActions.Dock = DockStyle.Top;
            grpActions.Location = new Point(8, 356);
            grpActions.Name = "grpActions";
            grpActions.Size = new Size(284, 170);
            grpActions.TabIndex = 1;
            grpActions.TabStop = false;
            grpActions.Text = "控制面板 (Controls)";
            // 
            // btnStart
            // 
            btnStart.Location = new Point(16, 30);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(248, 35);
            btnStart.TabIndex = 0;
            btnStart.Text = "加载并查看数据 (Start)";
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // btnStop
            // 
            btnStop.Location = new Point(16, 75);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(248, 35);
            btnStop.TabIndex = 1;
            btnStop.Text = "停止 (Stop)";
            btnStop.UseVisualStyleBackColor = true;
            btnStop.Click += btnStop_Click;
            // 
            // btnClearLog
            // 
            btnClearLog.Location = new Point(16, 120);
            btnClearLog.Name = "btnClearLog";
            btnClearLog.Size = new Size(248, 35);
            btnClearLog.TabIndex = 2;
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
        private GroupBox grpActions;
        private Button btnStart;
        private Button btnStop;
        private Button btnClearLog;
    }
}
