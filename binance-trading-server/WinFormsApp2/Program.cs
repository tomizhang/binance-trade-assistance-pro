using System;

namespace WinFormsApp2
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序主入口：自动读取 settings.json 配置文件，多币种差异化周期与杠杆参数自动生效
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // 预先读取 settings.json 确认配置文件健全
            var settings = UserSettings.Load();

            Application.Run(new Form1());
        }
    }
}