using System.Windows;

namespace NewSanofi.Windows
{
    public partial class MessageWindow : Window
    {
        public MessageBoxResult result = MessageBoxResult.No;

        public MessageWindow(string message, bool is_message)
        {
            InitializeComponent();
            lb_message.Text = message;
            if (is_message)
            {
                grid_message.Visibility = Visibility.Visible;
                grid_yesno.Visibility = Visibility.Hidden;
            }
        }

        public MessageWindow(string message)
        {
            InitializeComponent();
            lb_message.Text = message;
        }

        public MessageWindow(string message, string ok_message, string cancel_message)
        {
            InitializeComponent();
            lb_message.Text = message;
            btn_ok.Content = ok_message;
            btn_cancel.Content = cancel_message;
            grid_message.Visibility = Visibility.Hidden;
            grid_yesno.Visibility = Visibility.Visible;
        }

        public static MessageBoxResult ShowMessage(string message)
        {
            MessageWindow message_window = new MessageWindow(message);
            message_window.ShowDialog();
            return message_window.result;
        }

        public static MessageBoxResult ShowMessage(string message, bool is_message)
        {
            MessageWindow message_window = new MessageWindow(message, is_message);
            message_window.ShowDialog();
            return message_window.result;
        }

        public static MessageBoxResult ShowMessage(string message, string ok_message, string cancel_message)
        {
            MessageWindow message_window = new MessageWindow(message, ok_message, cancel_message);
            message_window.ShowDialog();
            return message_window.result;
        }

        private void btn_ok_Click(object sender, RoutedEventArgs e)
        {
            result = MessageBoxResult.Yes;
            Close();
        }

        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            result = MessageBoxResult.No;
            Close();
        }
    }
}
