using System.Windows;

namespace Wisp;

public partial class SessionKeyWindow : Window
{
    public string? Key { get; private set; }

    public SessionKeyWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => KeyBox.Focus();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        Key = KeyBox.Password.Trim();
        DialogResult = true;
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        Key = null;
        DialogResult = true;
    }
}
