using System;
using System.Windows;
using System.Windows.Controls;

namespace DiskUtility;

public partial class ConfirmDialog : Window
{
    private readonly string _confirmPhrase;
    private readonly bool _acknowledgeRequired;

    /// <summary>Index of the method the verify option applies to, or null if it applies to all.</summary>
    private readonly int? _verifyOnlyForOptionIndex;

    public int SelectedOptionIndex => MethodCombo.SelectedIndex;

    public bool VerifyChecked => VerifyCheck.IsChecked == true;

    /// <summary>
    /// True when the caller demanded no acknowledgement, or the user gave it. Callers gate the
    /// operation on this rather than assuming consent.
    /// </summary>
    public bool Acknowledged => !_acknowledgeRequired || AckCheck.IsChecked == true;

    /// <param name="options">If non-null, shows a method dropdown populated with these labels.</param>
    /// <param name="verifyLabel">If non-null, shows a verify checkbox with this label.</param>
    /// <param name="acknowledgeText">
    /// If non-null, shows a checkbox with this text that must be ticked before Proceed enables.
    /// Use it for risks the confirm phrase alone doesn't establish the user has taken in.
    /// </param>
    /// <param name="verifyOnlyForOptionIndex">
    /// If non-null, the verify checkbox is only enabled while that option is selected — so a
    /// verify request that would silently do nothing cannot be made in the first place.
    /// </param>
    public ConfirmDialog(
        string title,
        string message,
        string confirmPhrase,
        string[]? options = null,
        string? verifyLabel = null,
        string? acknowledgeText = null,
        int? verifyOnlyForOptionIndex = null)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        _confirmPhrase = confirmPhrase;
        _verifyOnlyForOptionIndex = verifyOnlyForOptionIndex;
        PhraseLabel.Text = $"To proceed, type exactly:  {confirmPhrase}";

        if (options is { Length: > 0 })
        {
            foreach (string opt in options) MethodCombo.Items.Add(opt);
            MethodCombo.SelectedIndex = 0;
            MethodCombo.SelectionChanged += (_, _) => UpdateVerifyAvailability();
        }
        else
        {
            MethodPanel.Visibility = Visibility.Collapsed;
        }

        if (verifyLabel is not null)
            VerifyCheck.Content = verifyLabel;
        else
            VerifyCheck.Visibility = Visibility.Collapsed;

        _acknowledgeRequired = acknowledgeText is not null;
        if (acknowledgeText is not null)
            AckText.Text = acknowledgeText;
        else
            AckCheck.Visibility = Visibility.Collapsed;

        UpdateVerifyAvailability();
        UpdateProceedEnabled();
    }

    /// <summary>
    /// Greys out the verify option when the selected method wouldn't honour it, and clears any
    /// tick already made, so <see cref="VerifyChecked"/> can never report a verification the
    /// caller will not perform.
    /// </summary>
    private void UpdateVerifyAvailability()
    {
        if (_verifyOnlyForOptionIndex is not int applicable) return;

        bool enabled = MethodCombo.SelectedIndex == applicable;
        VerifyCheck.IsEnabled = enabled;
        if (!enabled) VerifyCheck.IsChecked = false;
    }

    private void UpdateProceedEnabled()
        => ProceedBtn.IsEnabled =
            string.Equals(InputBox.Text, _confirmPhrase, StringComparison.Ordinal) && Acknowledged;

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateProceedEnabled();

    private void Gate_Changed(object sender, RoutedEventArgs e) => UpdateProceedEnabled();

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
