using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using NewSanofi.ViewModel;

namespace NewSanofi
{
    public partial class MainWindow : Window
    {
        public static string currentDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        public static List<string> lsText = new List<string>();
        public static System.DateTime datecheck;
        public static int numCache = -1;
        public static int numChar = 0;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                LoadNumCache();
            }
            catch
            {
            }
            if (DataContext == null)
            {
                DataContext = new MainViewModel();
            }
        }

        public static FrameworkElement GetTabItemParent(FrameworkElement current)
        {
            while (current != null && !(current is TabItem))
            {
                current = current.Parent as FrameworkElement;
            }

            return current;
        }

        public void LoadNumCache()
        {
            var lines = ClassHelper.TextFileProcess.ReadFile("NumCache");
            numCache = int.Parse(lines[0]);
            numChar = int.Parse(lines[1]);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ICommand command = (DataContext as MainViewModel)?.LoadedWindowCommand;
            if (command != null && command.CanExecute(this))
            {
                command.Execute(this);
            }
        }
    }
}
