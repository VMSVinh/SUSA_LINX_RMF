using System;
using System.IO;
using System.Windows;

namespace NewSanofi
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            EnsureRuntimeFolders();
            SqliteCom.Instance.CreateDatabase();
        }

        private static void EnsureRuntimeFolders()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(Path.Combine(baseDir, "Data"));
            Directory.CreateDirectory(Path.Combine(baseDir, "x86"));
        }
    }
}
