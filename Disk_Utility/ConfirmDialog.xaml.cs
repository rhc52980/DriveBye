using System;
using System.Windows;
using System.Windows.Controls;

namespace DiskUtility;

public partial class ConfirmDialog : Window
{
    private readonly string _confirmPhrase;
    private readonly bool _acknowledgeRequired;

    /// <summary>Indexes of the methods the verify option applies to, or null if it applies to all.</summary>
    private readonly int[]? _verifyForOptionIndexes;

    /// <summary>Index of the method that needs a pattern typed in, if any.</summary>
    private readonly int? _patternForOptionIndex;

    public int SelectedOptionIndex => MethodCombo.SelectedIndex;

    public bool VerifyChecked => VerifyCheck.IsChecked == true;

    /// <summary>Whatever was typed into the pattern box; empty when it was never shown.</summary>
    public string PatternText => PatternBox.Text;

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
    /// <param name="verifyForOptionIndexes">
    /// If non-null, the verify checkbox is only enabled while one of those options is selected —
    /// so a verify request that would silently do nothing cannot be made in the first place.
    /// </param>
    /// <param name="patternForOptionIndex">
    /// If non-null, selecting that option reveals a pattern box, and Proceed stays disabled until
    /// something is typed into it.
    /// </param>
    public ConfirmDialog(
        string title,
        string message,
        string confirmPhrase,
        string[]? options = null,
        string? verifyLabel = null,
        string? acknowledgeText = null,
        int[]? verifyForOptionIndexes = null,
        int? patternForOptionIndex = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.WindowTheme.ApplyDarkTitleBar(this);

        Title = title;
        HeaderText.Text = title;
        MessageText.Text = message;
        _confirmPhrase = confirmPhrase;
        _verifyForOptionIndexes = verifyForOptionIndexes;
        _patternForOptionIndex = patternForOptionIndex;
        PhraseLabel.Text = $"To proceed, type exactly:  {confirmPhrase}";

        if (options is { Length: > 0 })
        {
            foreach (string opt in options) MethodCombo.Items.Add(opt);
            MethodCombo.SelectedIndex = 0;
            MethodCombo.SelectionChanged += (_, _) =>
            {
                UpdateVerifyAvailability();
                UpdatePatternAvailability();
                UpdateProceedEnabled();
            };
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
        UpdatePatternAvailability();
        UpdateProceedEnabled();
    }

    /// <summary>
    /// Greys out the verify option when the selected method wouldn't honour it, and clears any
    /// tick already made, so <see cref="VerifyChecked"/> can never report a verification the
    /// caller will not perform.
    /// </summary>
    private void UpdateVerifyAvailability()
    {
        if (_verifyForOptionIndexes is null) return;

        bool enabled = Array.IndexOf(_verifyForOptionIndexes, MethodCombo.SelectedIndex) >= 0;
        VerifyCheck.IsEnabled = enabled;
        if (!enabled) VerifyCheck.IsChecked = false;
    }

    /// <summary>Shows the pattern box only for the method that writes one.</summary>
    private void UpdatePatternAvailability()
    {
        if (_patternForOptionIndex is not int applicable) return;

        PatternPanel.Visibility = MethodCombo.SelectedIndex == applicable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>True when no pattern is being asked for, or one has actually been typed.</summary>
    private bool PatternSatisfied =>
        PatternPanel.Visibility != Visibility.Visible || PatternBox.Text.Length > 0;

    private void UpdateProceedEnabled()
        => ProceedBtn.IsEnabled =
            string.Equals(InputBox.Text, _confirmPhrase, StringComparison.Ordinal)
            && Acknowledged
            && PatternSatisfied;

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateProceedEnabled();

    private void PatternBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateProceedEnabled();

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
