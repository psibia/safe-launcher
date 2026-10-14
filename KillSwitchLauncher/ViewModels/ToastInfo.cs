namespace KillSwitchLauncher.ViewModels;

public enum ToastKind
{
    Success,
    Danger,
    Info
}

/// <summary>Всплывающее уведомление сверху окна.</summary>
/// <param name="ActionText">Кнопка в уведомлении (например «Выгрузить»); null — без кнопки.</param>
/// <param name="Action">Что делает кнопка (в уведомлении Windows — щелчок по нему).</param>
public sealed record ToastInfo(string Title, string Subtitle, string Glyph, ToastKind Kind, string? ActionText = null, System.Action? Action = null)
{
    public bool HasAction => ActionText is not null && Action is not null;
}
