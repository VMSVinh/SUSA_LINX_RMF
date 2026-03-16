using System.Windows.Controls;
using System.Windows.Input;
using NewSanofi.ViewModel;

namespace NewSanofi.UserControls
{
    public partial class SubControl : UserControl
    {
        public SubControl()
        {
            InitializeComponent();
            DataContext = (ViewModel = new SubControlViewModel());
        }

        public SubControlViewModel ViewModel
        {
            get { return DataContext as SubControlViewModel; }
            set { DataContext = value; }
        }

        private void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            ICommand command = ViewModel?.LoadedWindowCommand;
            if (command != null && command.CanExecute(this))
            {
                command.Execute(this);
            }
        }
    }
}
