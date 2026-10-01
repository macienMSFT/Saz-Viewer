using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using SazViewer.Core;

namespace SazViewer.App;

/// <summary>Modal masked password prompt. The password is only read as a <see cref="SecureString"/>.</summary>
internal partial class PasswordDialog : Window
{
    private char[]? password;

    public PasswordDialog(string captureName, string purpose, SazPasswordRequest request)
    {
        InitializeComponent();
        PromptText.Text = $"'{captureName}' is encrypted. Enter its password {purpose}.";
        AttemptText.Text = $"Attempt {request.Attempt} of {request.MaximumAttempts}";
        if (request.PreviousPasswordRejected)
        {
            ErrorText.Text = "Incorrect password. Try again.";
            ErrorText.Visibility = Visibility.Visible;
        }
        Closed += (_, _) => PasswordInput.Clear();
    }

    /// <summary>Transfers ownership of the entered characters to the caller, who must clear them.</summary>
    public char[]? TakePassword()
    {
        var value = password;
        password = null;
        return value;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        using (var secure = PasswordInput.SecurePassword)
        {
            password = ToCharArray(secure);
        }
        PasswordInput.Clear();
        DialogResult = true;
    }

    internal static char[] ToCharArray(SecureString secure)
    {
        var characters = new char[secure.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            Marshal.Copy(pointer, characters, 0, characters.Length);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
        return characters;
    }
}

/// <summary>
/// Shows <see cref="PasswordDialog"/> on the UI thread for a parse running on a worker thread.
/// Up to three attempts, matching the CLI's interactive prompt. Cancel maps to
/// <see cref="SazPasswordCancelledException"/> via a null return.
/// </summary>
internal sealed class DialogPasswordProvider(Window owner, string captureName, string purpose) : ISazPasswordProvider
{
    public int MaximumAttempts => 3;

    public char[]? GetPassword(SazPasswordRequest request) =>
        owner.Dispatcher.Invoke(() =>
        {
            var dialog = new PasswordDialog(captureName, purpose, request) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.TakePassword() : null;
        });
}
