using System;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Чей процесс, который не под контролем лаунчера: ответ ядру на вопрос «чей процесс» для того, что ядро само по папке
/// приложения не узнает. Ответ нужен в момент соединения, поэтому дерево процессов проверяется по одному процессу вверх
/// по родителям, а не ждёт прохода наблюдения раз в секунду.
///
///   1. Программа приложения в другой папке, узнанная по подписи издателя (<see cref="AppFamily.SignedBySame"/>): копия
///      приложения, как если бы она лежала в его папке.
///   2. Процесс, который запустила копия приложения, открытая не из лаунчера, или её потомок: помощник из другой папки
///      (например, из AppData). Идёт так же, как копия.
///   3. Процесс, который запустил процесс под контролем лаунчера, но который в задание не попал (например, запущен с правами
///      администратора через запрос UAC): часть запуска, идёт как приложение.
///
/// Своими не считаются программы других приложений лаунчера (у них свои правила) и браузер, открытый по ссылке.
/// </summary>
internal static class ProcessKin
{
    /// <summary>Сколько родителей вверх проверять.</summary>
    public const int MaxDepth = 8;

    /// <summary>Процесс: путь программы (null — не узнать), PID родителя и время создания (null — не узнать).</summary>
    public readonly record struct Info(string? Image, int ParentPid, DateTime? Created);

    public enum Kind
    {
        None,
        /// <summary>Копия приложения или её потомок: идёт, как выбрано в «Если открыто не из лаунчера».</summary>
        Copy,
        /// <summary>Потомок процесса под контролем лаунчера вне его задания: идёт как само приложение.</summary>
        Launch
    }

    /// <param name="pid">Процесс, о котором спрашивает ядро.</param>
    /// <param name="info">PID → сведения о процессе; null — процесса нет, он в другом сеансе или его не открыть.</param>
    /// <param name="controlled">PID → приложение, если процесс под контролем лаунчера; иначе null.</param>
    /// <param name="family">Путь программы → приложение и узнано ли оно только по подписи (не по папке); null — ничья.</param>
    /// <param name="isLinkBrowser">Приложение и путь программы → это браузер, открытый приложением по ссылке, а не его часть.</param>
    public static (Guid App, Kind Kind) Resolve(int pid, Func<int, Info?> info, Func<int, Guid?> controlled,
        Func<string, (Guid App, bool BySignature)?> family, Func<Guid, string, bool> isLinkBrowser)
    {
        if (info(pid) is not { } self)
            return (Guid.Empty, Kind.None);

        if (self.Image is not null && family(self.Image) is { } own)
        {
            // Программа приложения в его папке — копия, которую ядро узнаёт само по папке. Отвечаем только за ту, что
            // узнана по подписи: её папку ядро не знает.
            return own.BySignature ? (own.App, Kind.Copy) : (Guid.Empty, Kind.None);
        }

        var child = self;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var parentPid = child.ParentPid;
            if (parentPid <= 4 || parentPid == pid || info(parentPid) is not { } parent)
                break;
            // PID родителя мог достаться другому процессу: родитель не может быть моложе потомка.
            if (parent.Created is not { } born || child.Created is not { } childBorn || born > childBorn)
                break;

            (Guid App, Kind Kind)? found = controlled(parentPid) is { } app
                ? (app, Kind.Launch)
                : parent.Image is not null && family(parent.Image) is { } copy
                    ? (copy.App, Kind.Copy)
                    : null;
            if (found is { } result)
            {
                return self.Image is not null && isLinkBrowser(result.App, self.Image)
                    ? (Guid.Empty, Kind.None)
                    : result;
            }

            child = parent;
        }

        return (Guid.Empty, Kind.None);
    }
}
