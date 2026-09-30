using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace BitosX.App.Controls;

/// <summary>
/// A text box for a password or another secret. It looks and behaves like a
/// <see cref="TextBox"/> with a <see cref="TextBox.PasswordChar"/>; the difference is in what it
/// tells the accessibility interface of the operating system.
/// </summary>
/// <remarks>
/// <para>
/// A text box hands its text to that interface (UI Automation on Windows, and its counterparts
/// elsewhere) whether or not it shows it on screen. For a box that shows dots, that means every
/// program of the same user that asks — a screen reader, a remote-support or automation tool, a
/// program that records what is on screen — is given the password in the clear, and is sent every
/// intermediate text while it is typed. Found in 0.7.0 by reading the master password back from
/// another process.
/// </para>
/// <para>
/// This box tells the interface what it shows on screen: the dots while the text is hidden, the
/// text once the user has chosen to reveal it. Text can still be entered through the interface.
/// </para>
/// <para>
/// This keeps the password from software that reads the screen. It is no protection against
/// software that records the keyboard or reads the memory of the process; see
/// docs/SECURITY_MODEL.md.
/// </para>
/// </remarks>
public class SecretBox : TextBox
{
    /// <summary>The character shown for each character of the secret, unless the view sets another.</summary>
    public const char DefaultPasswordChar = '●';

    public SecretBox()
    {
        PasswordChar = DefaultPasswordChar;
    }

    // Styled as the text box it is.
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>What the box shows on screen: the text, or one <see cref="TextBox.PasswordChar"/> for each character of it.</summary>
    public string DisplayedText
    {
        get
        {
            string text = Text ?? string.Empty;
            return RevealPassword || PasswordChar == default ? text : new string(PasswordChar, text.Length);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SecretBoxAutomationPeer(this);
}

/// <summary>
/// What a <see cref="SecretBox"/> tells the accessibility interface: what it shows on screen, and
/// nothing about the text while it is hidden.
/// </summary>
public class SecretBoxAutomationPeer : ControlAutomationPeer, IValueProvider
{
    public SecretBoxAutomationPeer(SecretBox owner)
        : base(owner)
    {
        // Deliberately no notification when the text changes: it would carry the old and the new
        // text to whoever listens.
    }

    public new SecretBox Owner => (SecretBox)base.Owner;

    public bool IsReadOnly => Owner.IsReadOnly;

    public string? Value => Owner.DisplayedText;

    public void SetValue(string? value) => Owner.Text = value;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

    protected override string? GetPlaceholderTextCore() => Owner.PlaceholderText;
}
