using System;
using System.Windows;
using System.Windows.Controls;

namespace DiskUtility;

public partial class ConfirmDialog : Window
{
    private readonly string _confirmPhrase;

    public int SelectedOptionIndex => MethodCombo.SelectedIndex;
    public bool VerifyChecked => VerifyCheck.IsChecked == true;

    /// <param name="options">If non-null, shows a method dropdown populated with these labels.</param>
    /// <param name="verifyLabel">If non-null, shows a verify checkbox with this label.</param>
    public ConfirmDialog(
        string title,
        string message,
        string confirmPhrase,
        string[]? options = null,
        string? verifyLabel = null)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        _confirmPhrase = confirmPhrase;
        PhraseLabel.Text = $"To proceed, type exactly:  {confirmPhrase}";

        if (options is { Length: > 0 })
        {
            foreach (string opt in options) MethodCombo.Items.Add(opt);
            MethodCombo.SelectedIndex = 0;
        }
        else
        {
            MethodPanel.Visibility = Visibility.Collapsed;
        }

        if (verifyLabel is not null)
            VerifyCheck.Content = verifyLabel;
        else
            VerifyCheck.Visibility = Visibility.Collapsed;
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        => ProceedBtn.IsEnabled = string.Equals(InputBox.Text, _confirmPhrase, StringComparison.Ordinal);

    private void ProceedBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
