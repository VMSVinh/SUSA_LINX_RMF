using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NewSanofi.Windows
{
    public partial class ManualDialog : Window
    {
        public ManualDialog()
        {
            InitializeComponent();
        }

        public void ManualShow(int mode, string title, string message)
        {
            switch (mode)
            {
                case 0:
                    content_lbl.Background = Brushes.Green;
                    break;
                case 1:
                    content_lbl.Background = Brushes.Red;
                    break;
                case 2:
                    content_lbl.Background = Brushes.Yellow;
                    break;
            }

            content_lbl.Content = title;
            content_tbl.Text = message;
            ShowDialog();
        }

        public void ManualShow2(int mode, string title, string message)
        {
            switch (mode)
            {
                case 0:
                    content_lbl.Background = Brushes.Green;
                    break;
                case 1:
                    content_lbl.Background = Brushes.Red;
                    break;
                case 2:
                    content_lbl.Background = Brushes.Yellow;
                    break;
            }

            ok_btn.Visibility = Visibility.Collapsed;
            content_lbl.Content = title;
            content_tbl.Text = message;
            Show();
        }

        private void ok_btn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                ok_btn_Click(null, null);
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}
