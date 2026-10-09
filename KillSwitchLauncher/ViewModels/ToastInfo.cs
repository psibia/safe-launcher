namespace KillSwitchLauncher.ViewModels;

public enum ToastKind
{
    Success,
    Danger,
    Info
}

/// <summary>Всплывающее уведомление сверху окна.</summary>
public sealed record ToastInfo(string Title, string Subtitle, string Glyph, ToastKind Kind);
