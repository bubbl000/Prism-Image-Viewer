using System.Windows;

namespace ImageBrowser;

/// <summary>
/// 简单的输入对话框
/// </summary>
public partial class InputDialog : Window
{
    public string TitleText { get; set; }
    public string Message { get; set; }
    public string InputText { get; set; }

    public InputDialog(string title, string message, string defaultInput = "")
    {
        InitializeComponent();
        TitleText = title;
        Message = message;
        InputText = defaultInput;
        DataContext = this;
        
        // 设置窗口标题
        Title = title;
        
        // 聚焦到输入框并选中所有文本
        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
