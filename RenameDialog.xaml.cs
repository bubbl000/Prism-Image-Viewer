using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImageViewer
{
    public partial class RenameDialog : Window
    {
        public string NewName { get; private set; } = "";

        public RenameDialog(string currentName)
        {
            InitializeComponent();
            TxtName.Text = currentName;
            TxtName.SelectAll();
            TxtName.Focus();
            UpdateOKButton();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateOKButton();
        }

        private void UpdateOKButton()
        {
            BtnOK.IsEnabled = !string.IsNullOrWhiteSpace(TxtName.Text);
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            NewName = TxtName.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
