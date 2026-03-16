using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using NewSanofi.ClassHelper;
using NewSanofi.UserControls;
using NewSanofi.Windows;
using SimpleTCP;
using TcpMessage = SimpleTCP.Message;

namespace NewSanofi.ViewModel
{
    public class SubControlViewModel : BaseViewModel
    {
        private SubControl subControl;
        private StackPanel rowStackPanel;
        private SimpleTcpClient client;
        public TabItem mainTab;

        private string _IPAddress = "";
        private Visibility _PlaybackVisible = Visibility.Hidden;
        private List<string> _codeList = new List<string>();
        private string _Port = "";
        private int counterNotReset;
        private int _CounterValue;
        private int _RowStart;
        private string _CounterText = "0";
        private bool _StatusConnect;
        private bool _StatusRun;
        private string _RunString = "Run";
        private string _StatusString = "Connect";
        private string _Speed = "";
        private string _ContentPrint = "";
        private string _ExcelPath = "";
        private string _ViewStatus = "Show View";
        private string _ResponseMessage = "";
        private WindowState _WindowsState = WindowState.Normal;
        private bool done;
        private bool firsttime;
        private int i;
        private byte[] utf8String;
        private bool checkonetime = true;

        public string IPAddress
        {
            get { return _IPAddress; }
            set
            {
                _IPAddress = value;
                StackPanel header = mainTab?.Header as StackPanel;
                TextBlock text = header?.Children.Count > 0 ? header.Children[0] as TextBlock : null;
                if (text != null)
                {
                    text.Text = value;
                }

                OnPropertyChanged();
            }
        }

        public Visibility PlaybackVisible
        {
            get { return _PlaybackVisible; }
            set { SetProperty(ref _PlaybackVisible, value); }
        }

        public string Port
        {
            get { return _Port; }
            set { SetProperty(ref _Port, value); }
        }

        public int CounterValue
        {
            get { return _CounterValue; }
            set
            {
                _CounterValue = value;
                CounterText = value.ToString();
                OnPropertyChanged();
            }
        }

        public int RowStart
        {
            get { return _RowStart; }
            set { SetProperty(ref _RowStart, value); }
        }

        public string CounterText
        {
            get { return _CounterText; }
            set { SetProperty(ref _CounterText, value); }
        }

        public bool StatusConnect
        {
            get { return _StatusConnect; }
            set
            {
                _StatusConnect = value;
                if (StatusConnect)
                {
                    StatusString = "Disconnect";
                }
                else
                {
                    StatusString = "Connect";
                    StatusRun = false;
                    firsttime = false;
                }

                OnPropertyChanged();
            }
        }

        public bool StatusRun
        {
            get { return _StatusRun; }
            set
            {
                _StatusRun = value;
                RunString = value ? "Stop" : "Run";
                OnPropertyChanged();
            }
        }

        public string RunString
        {
            get { return _RunString; }
            set { SetProperty(ref _RunString, value); }
        }

        public string StatusString
        {
            get { return _StatusString; }
            set { SetProperty(ref _StatusString, value); }
        }

        public string Speed
        {
            get { return _Speed; }
            set { SetProperty(ref _Speed, value); }
        }

        public string ContentPrint
        {
            get { return _ContentPrint; }
            set { SetProperty(ref _ContentPrint, value); }
        }

        public string ExcelPath
        {
            get { return _ExcelPath; }
            set { SetProperty(ref _ExcelPath, value); }
        }

        public string ViewStatus
        {
            get { return _ViewStatus; }
            set { SetProperty(ref _ViewStatus, value); }
        }

        public string ResponseMessage
        {
            get { return _ResponseMessage; }
            set { SetProperty(ref _ResponseMessage, value); }
        }

        public WindowState WindowsState
        {
            get { return _WindowsState; }
            set { SetProperty(ref _WindowsState, value); }
        }

        public ICommand LoadedWindowCommand { get; set; }
        public ICommand SetCommand { get; set; }
        public ICommand CloseCommand { get; set; }
        public ICommand RunCommand { get; set; }
        public ICommand ResetCommand { get; set; }
        public ICommand InportExcelCommand { get; set; }
        public ICommand HideCommand { get; set; }
        public ICommand ConfirmCommand { get; set; }

        public SubControlViewModel()
        {
            ResetCommand = new RelayCommand<object>(_ => true, _ =>
            {
                CounterValue = 0;
            });

            SetCommand = new RelayCommand<object>(_ => _codeList.Count != 0, _ =>
            {
                if (RowStart < _codeList.Count)
                {
                    i = RowStart;
                    if (rowStackPanel != null)
                    {
                        rowStackPanel.Visibility = Visibility.Hidden;
                    }
                }
                else
                {
                    new ManualDialog().ManualShow(1, "Set Row Start", "Can't Set, Row Start Value Larger than Code List Count");
                }
            });

            InportExcelCommand = new RelayCommand<object>(_ => true, _ =>
            {
                OpenFileDialog openFile = new OpenFileDialog
                {
                    RestoreDirectory = true
                };

                if (openFile.ShowDialog() == DialogResult.OK)
                {
                    string extension = System.IO.Path.GetExtension(openFile.FileName).TrimStart('.').ToLowerInvariant();
                    if (extension == "xlsx" || extension == "xls")
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                _codeList = ImportExcel.import_start(openFile.FileName);
                            }
                            catch
                            {
                            }
                        }).ContinueWith(_1 =>
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                            {
                                ManualDialog manualDialog2 = new ManualDialog();
                                if (_codeList.Count > 0)
                                {
                                    manualDialog2.ManualShow(0, IPAddress + "_Load File Excel", "Done, Number Of File:" + _codeList.Count);
                                    i = 0;
                                    counterNotReset = 0;
                                    CounterValue = 0;
                                    ExcelPath = openFile.FileName;
                                }
                                else
                                {
                                    manualDialog2.ManualShow(1, IPAddress + "_Load File Excel", "Fail");
                                }
                            }));
                        });
                    }
                    else if (extension == "txt")
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                _codeList = TextFileProcess.ReadFileExtent(openFile.FileName);
                            }
                            catch
                            {
                            }
                        }).ContinueWith(_1 =>
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                            {
                                ManualDialog manualDialog2 = new ManualDialog();
                                if (_codeList.Count > 0)
                                {
                                    manualDialog2.ManualShow(0, IPAddress + "_Load File Text", "Done, Number Of File:" + _codeList.Count);
                                    i = 0;
                                    counterNotReset = 0;
                                    CounterValue = 0;
                                    ExcelPath = openFile.FileName;
                                }
                                else
                                {
                                    manualDialog2.ManualShow(1, IPAddress + "_Load File Text", "Fail");
                                }
                            }));
                        });
                    }
                    else
                    {
                        new ManualDialog().ManualShow(1, "Import File", "Error, Can't Define File Type");
                    }
                }
            });

            LoadedWindowCommand = new RelayCommand<object>(_ => true, p =>
            {
                if (checkonetime)
                {
                    subControl = p as SubControl;
                    rowStackPanel = subControl?.StartRowSP;
                    if (rowStackPanel != null)
                    {
                        rowStackPanel.Visibility = Visibility.Hidden;
                    }

                    LoadExcel();
                    checkonetime = false;
                }
            });

            ConfirmCommand = new RelayCommand<object>(_ => true, _ =>
            {
                if (!StatusConnect)
                {
                    try
                    {
                        Connect(IPAddress, Port);
                        StatusConnect = true;
                        return;
                    }
                    catch (Exception ex)
                    {
                        ResponseMessage = ex.Message;
                        StatusConnect = false;
                        return;
                    }
                }

                client?.Disconnect();
                StatusConnect = false;
            });

            RunCommand = new RelayCommand<object>(_ => StatusConnect, _ =>
            {
                if (_codeList.Count == 0)
                {
                    new ManualDialog().ManualShow(1, "Run Process", "Please Import Excel File");
                }
                else if (rowStackPanel != null && rowStackPanel.Visibility == Visibility.Visible)
                {
                    new ManualDialog().ManualShow(1, "Run Process", "Please Set Row Start Value");
                }
                else if (MainWindow.numCache == -1 || MainWindow.numChar == 0)
                {
                    new ManualDialog().ManualShow(1, "Run Process", "Please Choose Number Insert To Cache Or Set Number Of Character");
                }
                else
                {
                    int num = (MainWindow.numCache + 1) * 5;
                    if (!StatusRun)
                    {
                        StatusRun = true;
                        if (!firsttime)
                        {
                            firsttime = true;
                            done = false;
                            for (int j = 0; j < num; j++)
                            {
                                WriteData(_codeList[i]);
                                i++;
                                if (i >= _codeList.Count)
                                {
                                    System.Windows.MessageBox.Show("You Have Push All Codes");
                                    break;
                                }
                            }

                            System.Windows.MessageBox.Show("done");
                        }
                        else
                        {
                            RunExcute();
                        }
                    }
                    else
                    {
                        StatusRun = false;
                    }
                }
            });

            CloseCommand = new RelayCommand<object>(_ => true, p =>
            {
                if (MessageWindow.ShowMessage("Do You Want To Close") == MessageBoxResult.Yes)
                {
                    SaveFile();
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
        }

        public void CloseAll()
        {
            if (ExcelPath != "" && _codeList.Count > 0 && counterNotReset < _codeList.Count)
            {
                TextFileProcess.WriteFile(IPAddress + "_excelPath", new List<string>
                {
                    ExcelPath,
                    CounterText,
                    counterNotReset.ToString()
                });
            }
        }

        private void LoadExcel()
        {
            try
            {
                List<string> ls = TextFileProcess.ReadFile(IPAddress + "_excelPath");
                if (ls.Count == 0)
                {
                    return;
                }

                string s = IPAddress + "_A Processing Found, Do You Want To Continue?";
                if (MessageWindow.ShowMessage(s) == MessageBoxResult.Yes)
                {
                    ExcelPath = ls[0];
                    CounterValue = int.Parse(ls[1]);
                    i = int.Parse(ls[2]);

                    string extension = System.IO.Path.GetExtension(ExcelPath).TrimStart('.').ToLowerInvariant();
                    if (extension == "xlsx" || extension == "xls")
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                _codeList = ImportExcel.import_start(ExcelPath);
                            }
                            catch
                            {
                            }
                        }).ContinueWith(_ =>
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                            {
                                ManualDialog manualDialog = new ManualDialog();
                                if (_codeList.Count > 0)
                                {
                                    manualDialog.ManualShow(0, IPAddress + "_Load File Excel", "Done, Number Of File:" + _codeList.Count);
                                }
                                else
                                {
                                    manualDialog.ManualShow(1, IPAddress + "_Load File Excel", "Fail");
                                }
                            }));
                        });
                    }
                    else if (extension == "txt")
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                _codeList = TextFileProcess.ReadFileExtent(ExcelPath);
                            }
                            catch
                            {
                            }
                        }).ContinueWith(_ =>
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                            {
                                ManualDialog manualDialog = new ManualDialog();
                                if (_codeList.Count > 0)
                                {
                                    manualDialog.ManualShow(0, IPAddress + "_Load File Text", "Done, Number Of File:" + _codeList.Count);
                                }
                                else
                                {
                                    manualDialog.ManualShow(1, IPAddress + "_Load File Text", "Fail");
                                }
                            }));
                        });
                    }
                }
                else
                {
                    if (rowStackPanel != null)
                    {
                        rowStackPanel.Visibility = Visibility.Visible;
                    }
                }

                TextFileProcess.DeleteFile(IPAddress + "_excelPath");
            }
            catch
            {
            }
        }

        private void RunExcute()
        {
            if (!StatusRun)
            {
                return;
            }

            try
            {
                if (i < _codeList.Count && done)
                {
                    done = false;
                    WriteData(_codeList[i]);
                    i++;
                }
            }
            catch (Exception ex)
            {
                ResponseMessage = ex.Message;
                StatusConnect = false;
            }
        }

        private void SaveFile()
        {
            TextFileProcess.WriteFile("config", new List<string>
            {
                IPAddress,
                Port,
                Speed
            });
        }

        private void Connect(string ip, string port)
        {
            client = new SimpleTcpClient();
            client.StringEncoder = Encoding.ASCII;
            client.Connect(ip, int.Parse(port));
            client.DataReceived += Client_DataReceived;
        }

        private void Client_DataReceived(object sender, TcpMessage e)
        {
            if (e.MessageString == "\u001b\u000f")
            {
                done = true;
                CounterValue++;
                counterNotReset++;
                if (StatusRun)
                {
                    RunExcute();
                }
            }
            else
            {
                utf8String = Encoding.UTF8.GetBytes(e.MessageString);
                ResponseMessage = BitConverter.ToString(utf8String);
            }
        }

        private void WriteData(string data)
        {
            int numbyte = 7 + MainWindow.numChar;
            byte[] b = new byte[numbyte];
            b[0] = 27;
            b[1] = 2;
            b[2] = 29;
            b[3] = (byte)MainWindow.numChar;
            b[4] = 0;
            for (int index = 0; index < MainWindow.numChar; index++)
            {
                b[index + 5] = index < data.Length ? (byte)data[index] : (byte)' ';
            }

            b[numbyte - 2] = 27;
            b[numbyte - 1] = 3;
            client.Write(b);
            ContentPrint = data ?? "";
        }
    }
}
