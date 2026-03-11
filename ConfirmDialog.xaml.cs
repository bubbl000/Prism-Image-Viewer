using System.Windows;

namespace ImageViewer
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; } = false;

        // okOnly=true 时只显示确定按钮（信息提示模式）
        public ConfirmDialog(string title, string message, bool okOnly = false)
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtMessage.Text = message;
            if (okOnly)
            {
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnConfirm.Content = "确定";
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }
    }
}
