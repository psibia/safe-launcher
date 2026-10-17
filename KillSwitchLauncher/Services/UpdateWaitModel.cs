using System;
using System.Collections.Generic;
using SafeLauncher.Updates;

namespace KillSwitchLauncher.Services;

/// <summary>Состояние шага в окне ожидания обновления.</summary>
public enum UpdateWaitStepState
{
    Pending,
    Active,
    Done,
    Failed
}

/// <summary>Шаг в окне ожидания обновления.</summary>
public sealed record UpdateWaitStep(string Title, UpdateWaitStepState State);

/// <summary>Чем закончилась установка, как её видит окно ожидания.</summary>
public enum UpdateWaitOutcome
{
    /// <summary>Установка идёт.</summary>
    Running,

    /// <summary>
    /// Новая версия установлена, помощник ждёт, пока она запустится: окно запускает лаунчер и ждёт итога. Не запустится —
    /// помощник вернёт прежнюю версию (<see cref="Failed"/>).
    /// </summary>
    Starting,

    /// <summary>
    /// Новая версия установлена и проверена. Если перед этим был <see cref="Starting"/>, она уже запущена и сообщила об этом;
    /// иначе (помощник прежней версии не ждёт запуска) лаунчер пора запустить.
    /// </summary>
    Installed,

    /// <summary>Не установилась: служба сообщила причину. Прежняя версия на месте (или возвращена).</summary>
    Failed,

    /// <summary>Помощник службы перестал отвечать или не начал работу: чем кончилось, неизвестно.</summary>
    Lost
}

/// <summary>Что показать в окне ожидания.</summary>
public sealed record UpdateWaitView(UpdateWaitOutcome Outcome, string Title, string Message, IReadOnlyList<UpdateWaitStep> Steps);

/// <summary>
/// Окно ожидания обновления без окон WPF: по файлу хода установки (status.json, его пишет помощник службы) решает,
/// какой шаг идёт, закончилась ли установка и не перестал ли помощник отвечать.
/// </summary>
public sealed class UpdateWaitModel
{
    /// <summary>Помощник переписывает status.json раз в секунду: столько тишины значит, что он не работает.</summary>
    public static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Служба записывает status.json до ответа лаунчеру. Если файла нужной версии нет столько времени, установки нет.</summary>
    public static readonly TimeSpan NoStatusTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Предел на всю установку: лаунчер закрывается до минуты, установщик работает до 15 минут.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(25);

    private static readonly string[] StageOrder =
    {
        UpdateStages.Waiting, UpdateStages.Verifying, UpdateStages.Stopping, UpdateStages.Installing, UpdateStages.Checking, UpdateStages.Starting
    };

    private readonly string _version;
    private readonly DateTime _startedUtc;

    public UpdateWaitModel(string version, DateTime startedUtc)
    {
        _version = version;
        _startedUtc = startedUtc;
    }

    public string Title => $"Обновление до версии {_version}";

    /// <summary>Шаги по порядку, по одному на шаг помощника. Последний («Запуск лаунчера») окно ожидания выполняет вместе с помощником.</summary>
    private string[] StepTitles => new[]
    {
        "Закрытие лаунчера",
        "Проверка подписи установщика",
        "Остановка службы и ядра",
        $"Установка версии {_version}",
        "Проверка новой версии",
        "Запуск лаунчера"
    };

    private static int LaunchStep => Array.IndexOf(StageOrder, UpdateStages.Starting);

    /// <param name="state">Прочитанный status.json; null — его нет или он сейчас переписывается.</param>
    public UpdateWaitView Evaluate(UpdateState? state, DateTime utcNow)
    {
        var elapsed = utcNow - _startedUtc;
        if (state is null || state.Version != _version)
        {
            return elapsed > NoStatusTimeout
                ? Lost("Служба не начала установку. Лаунчер остался прежней версии.", 0)
                : new UpdateWaitView(UpdateWaitOutcome.Running, Title, "Ждём службу", Steps(0, UpdateWaitStepState.Active));
        }

        var current = Array.IndexOf(StageOrder, state.Stage);
        if (state.Done)
        {
            if (state.Ok)
                return new UpdateWaitView(UpdateWaitOutcome.Installed, Title, "Новая версия установлена, запускаем лаунчер",
                    Steps(LaunchStep, UpdateWaitStepState.Active));

            // Откат: служба вернула прежнюю версию, подробности в update.log и в журнале лаунчера.
            var message = state.RolledBack
                ? state.FailedStage == UpdateStages.Starting
                    ? $"Новая версия не запустилась, поэтому возвращена версия {state.FromVersion}. Подробности будут в журнале лаунчера."
                    : $"Новая версия не установилась, лаунчер остался на версии {state.FromVersion}. Подробности будут в журнале лаунчера."
                : state.Error ?? "Обновление не установилось.";
            var failed = Array.IndexOf(StageOrder, state.FailedStage);
            return new UpdateWaitView(UpdateWaitOutcome.Failed, "Обновление не установилось", message,
                Steps(failed < 0 ? LaunchStep - 1 : failed, UpdateWaitStepState.Failed));
        }

        if (state.IsStale(utcNow, StaleTimeout))
            return Lost("Помощник службы перестал отвечать. Если лаунчер не запустится, установите его заново.", Math.Max(current, 0));
        if (elapsed > TotalTimeout)
            return Lost($"Установка идёт дольше {TotalTimeout.TotalMinutes:0} минут. Если лаунчер не запустится, установите его заново.", Math.Max(current, 0));

        if (state.Stage == UpdateStages.RollingBack)
            return new UpdateWaitView(UpdateWaitOutcome.Running, "Возвращаем прежнюю версию",
                string.IsNullOrWhiteSpace(state.Message) ? $"Новая версия не встала, возвращаем версию {state.FromVersion}" : state.Message,
                Steps(Array.IndexOf(StageOrder, state.FailedStage) is var failedAt and >= 0 ? failedAt : LaunchStep - 1, UpdateWaitStepState.Failed));

        if (state.Stage == UpdateStages.Starting)
            return new UpdateWaitView(UpdateWaitOutcome.Starting, Title, "Новая версия установлена, запускаем лаунчер", Steps(LaunchStep, UpdateWaitStepState.Active));

        return new UpdateWaitView(UpdateWaitOutcome.Running, Title, state.Message, Steps(Math.Max(current, 0), UpdateWaitStepState.Active));
    }

    private UpdateWaitView Lost(string message, int step) =>
        new(UpdateWaitOutcome.Lost, "Установка прервалась", message, Steps(step, UpdateWaitStepState.Failed));

    /// <summary>Шаги до <paramref name="current"/> выполнены, он сам в состоянии <paramref name="state"/>, дальше ещё не начаты.</summary>
    private IReadOnlyList<UpdateWaitStep> Steps(int current, UpdateWaitStepState state)
    {
        var titles = StepTitles;
        var steps = new List<UpdateWaitStep>(titles.Length);
        for (var i = 0; i < titles.Length; i++)
            steps.Add(new UpdateWaitStep(titles[i], i < current ? UpdateWaitStepState.Done : i == current ? state : UpdateWaitStepState.Pending));
        return steps;
    }
}
