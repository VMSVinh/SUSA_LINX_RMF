using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NewSanofi.ClassHelper;
using NewSanofi.UserControls;
using NewSanofi.Windows;

namespace NewSanofi.ViewModel
{
    public class MainViewModel : BaseViewModel
    {
        private MainWindow mw;
        private ObservableCollection<TabItem> _TabControler;
        private WindowState _WindowsState = WindowState.Normal;
        private int _SelectedNumberCache = -1;
        private int _NumberChar;

        public ObservableCollection<TabItem> TabControler
        {
            get { return _TabControler; }
            set { SetProperty(ref _TabControler, value); }
        }

        public WindowState WindowsState
        {
            get { return _WindowsState; }
            set { SetProperty(ref _WindowsState, value); }
        }

        public bool AddEnable
        {
            get { return TabControler == null || TabControler.Count < 10; }
        }

        public int SelectedNumberCache
        {
            get { return _SelectedNumberCache; }
            set
            {
                _SelectedNumberCache = value;
                MainWindow.numCache = value;
                OnPropertyChanged();
            }
        }

        public int NumberChar
        {
            get { return _NumberChar; }
            set
            {
                _NumberChar = value;
                MainWindow.numChar = value;
                OnPropertyChanged();
            }
        }

        public ICommand LoadedWindowCommand { get; set; }
        public ICommand CloseCommand { get; set; }
        public ICommand HideCommand { get; set; }
        public ICommand AddCommand { get; set; }
        public ICommand DeleteCommand { get; set; }

        public MainViewModel()
        {
            LoadedWindowCommand = new RelayCommand<object>(_ => true, p =>
            {
                TabControler = new ObservableCollection<TabItem>();
                mw = p as MainWindow;
                try
                {
                    LoadListDevice();
                }
                catch
                {
                }

                SelectedNumberCache = MainWindow.numCache;
                NumberChar = MainWindow.numChar;

                try
                {
                    CheckDate();
                }
                catch
                {
                    WriteDate();
                }

                string text = ConfigurationManager.AppSettings["LicenseDay"];
                if (!string.IsNullOrEmpty(text))
                {
                    int days = int.Parse(text);
                    int delta = (DateTime.Now - MainWindow.datecheck).Days;
                    if (delta > days || delta < -days)
                    {
                        MessageBox.Show("License expired");
                        mw?.Close();
                    }
                }
            });

            CloseCommand = new RelayCommand<object>(_ => true, p =>
            {
                if (MessageWindow.ShowMessage("Do You Want To Close") == MessageBoxResult.Yes)
                {
                    SaveListDevice();
                    SaveNumCache();
                    CloseAllSub();
                    (p as Window)?.Close();
                }
            });

            HideCommand = new RelayCommand<object>(_ => true, p =>
            {
                Window window = p as Window;
                if (window != null)
                {
                    window.WindowState = WindowState.Minimized;
                }
            });

            DeleteCommand = new RelayCommand<object>(_ => true, p =>
            {
                if (MessageWindow.ShowMessage("Do You Want To Delete") == MessageBoxResult.Yes)
                {
                    var item = MainWindow.GetTabItemParent(p as FrameworkElement) as TabItem;
                    if (item != null)
                    {
                        TabControler.Remove(item);
                        OnPropertyChanged(nameof(AddEnable));
                    }
                }
            });

            AddCommand = new RelayCommand<object>(_ => true, _ =>
            {
                if (TabControler.Count < 10)
                {
                    AddNewDevice("Printer Connector", "");
                }
                else
                {
                    new ManualDialog().ManualShow(1, "Add New Device", "Maximum Device Is 10, Can't Insert More");
                }
            });
        }

        private void CloseAllSub()
        {
            foreach (TabItem item in TabControler)
            {
                ((item.Content as SubControl)?.DataContext as SubControlViewModel)?.CloseAll();
            }
            OnPropertyChanged(nameof(AddEnable));
        }

        private void SaveNumCache()
        {
            if (MainWindow.numCache != -1 || MainWindow.numChar != 0)
            {
                TextFileProcess.WriteFile("NumCache", new List<string>
                {
                    MainWindow.numCache.ToString(),
                    NumberChar.ToString()
                });
            }
        }

        private void AddNewDevice(string printer, string port)
        {
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            TextBlock text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White),
                Text = printer,
                Width = 132.0,
                FontSize = 14,
                Margin = new Thickness(2, 0, 0, 0)
            };

            Button b = new Button
            {
                Content = "X",
                Width = 50,
                Height = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5.0, 0.0, 5.0, 0.0),
                Foreground = new SolidColorBrush(Colors.White),
                Background = new SolidColorBrush(Colors.Red),
                Opacity = 0.85,
                FontWeight = FontWeights.Bold,
                CommandParameter = null,
                Command = DeleteCommand
            };
            b.CommandParameter = b;

            sp.Children.Add(text);
            sp.Children.Add(b);

            SubControl sc = new SubControl();
            TabItem temp = new TabItem
            {
                Header = sp,
                Content = sc
            };

            (sc.DataContext as SubControlViewModel).mainTab = temp;
            if (printer != "" && printer != "Printer Connector")
            {
                (sc.DataContext as SubControlViewModel).IPAddress = printer;
                (sc.DataContext as SubControlViewModel).Port = port;
            }

            TabControler.Add(temp);
            OnPropertyChanged(nameof(AddEnable));
        }

        private void SaveListDevice()
        {
            List<string> deviceList = new List<string>();
            foreach (TabItem tab in TabControler)
            {
                SubControlViewModel item = (tab.Content as SubControl)?.DataContext as SubControlViewModel;
                if (item != null)
                {
                    deviceList.Add(item.IPAddress + "_" + item.Port);
                }
            }

            TextFileProcess.WriteFile("ListDevices", deviceList);
        }

        private void LoadListDevice()
        {
            List<string> ls = TextFileProcess.ReadFile("ListDevices");
            foreach (string s in ls)
            {
                string[] temp = s.Split('_');
                if (temp[0] == "")
                {
                    AddNewDevice("Printer Connector", "");
                }
                else if (temp.Length > 1)
                {
                    AddNewDevice(temp[0], temp[1]);
                }
            }
        }

        private void CheckDate()
        {
            List<string> ls = TextFileProcess.ReadFile("DateToExComeTo");
            MainWindow.datecheck = DateTime.Parse(ls[0]);
        }

        private void WriteDate()
        {
            MainWindow.datecheck = DateTime.Now;
            TextFileProcess.WriteFile("DateToExComeTo", new List<string>
            {
                MainWindow.datecheck.ToLongDateString()
            });
        }
    }
}
