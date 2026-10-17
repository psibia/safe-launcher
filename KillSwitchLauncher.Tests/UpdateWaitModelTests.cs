using System;
using System.IO;
using System.Linq;
using KillSwitchLauncher.Services;
using SafeLauncher.Updates;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Ход установки через службу: файл status.json и то, что по нему показывает окно ожидания обновления.</summary>
public class UpdateWaitModelTests
{
    private static readonly DateTime Start = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static UpdateState State(string stage, int secondsAgo = 0, string version = "2.4.0") => new()
    {
        Version = version,
        FromVersion = "2.3.3",
        Stage = stage,
        Message = "Сообщение помощника",
        Updated = Start.AddSeconds(60 - secondsAgo),
        Started = Start
    };

    private static UpdateWaitModel Model() => new("2.4.0", Start);

    private static UpdateWaitStepState[] States(UpdateWaitView view) => view.Steps.Select(s => s.State).ToArray();

    // ---------------------------------------------------------------- status.json

    [Fact]
    public void UpdateState_WritesAndReadsBack()
    {
        using var dir = new TempDir();
        var path = dir.File(UpdateState.FileName);
        var state = State(UpdateStages.Done);
        state.Done = true;
        state.RolledBack = true;
        state.Error = "Установщик завершился с кодом 2";
        state.FailedStage = UpdateStages.Installing;

        state.Write(path);
        var read = UpdateState.Read(path)!;

        Assert.Equal(state.ToJson(), read.ToJson());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ повреждён")]
    public void UpdateState_MissingOrDamaged_IsNull(string? content)
    {
        using var dir = new TempDir();
        var path = dir.File(UpdateState.FileName);
        if (content is not null)
            File.WriteAllText(path, content);

        Assert.Null(UpdateState.Read(path));
    }

    [Fact]
    public void UpdateState_IsStale_OnlyWhileRunning()
    {
        var state = State(UpdateStages.Installing, secondsAgo: 40);
        var now = Start.AddSeconds(60);

        Assert.True(state.IsStale(now, TimeSpan.FromSeconds(30)));
        Assert.False(state.IsStale(now, TimeSpan.FromSeconds(60)));

        state.Done = true;
        Assert.False(state.IsStale(now, TimeSpan.FromSeconds(30)));
    }

    // ---------------------------------------------------------------- окно ожидания

    [Fact]
    public void NoStatusYet_WaitsForTheService_ThenGivesUp()
    {
        var waiting = Model().Evaluate(null, Start.AddSeconds(5));
        Assert.Equal(UpdateWaitOutcome.Running, waiting.Outcome);
        Assert.Equal(UpdateWaitStepState.Active, waiting.Steps[0].State);

        var lost = Model().Evaluate(null, Start + UpdateWaitModel.NoStatusTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal(UpdateWaitOutcome.Lost, lost.Outcome);
    }

    [Fact]
    public void StatusOfAnotherVersion_IsNotThisUpdate()
    {
        var view = Model().Evaluate(State(UpdateStages.Installing, version: "2.3.9"), Start.AddSeconds(5));

        Assert.Equal(UpdateWaitOutcome.Running, view.Outcome);
        Assert.Equal(UpdateWaitStepState.Active, view.Steps[0].State);
    }

    [Fact]
    public void Installing_EarlierStepsDone_LaterPending_MessageFromService()
    {
        var view = Model().Evaluate(State(UpdateStages.Installing), Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Running, view.Outcome);
        Assert.Equal("Сообщение помощника", view.Message);
        Assert.Equal(new[]
        {
            UpdateWaitStepState.Done, UpdateWaitStepState.Done, UpdateWaitStepState.Done, UpdateWaitStepState.Active,
            UpdateWaitStepState.Pending, UpdateWaitStepState.Pending
        }, States(view));
        Assert.Contains("2.4.0", view.Steps[3].Title);
    }

    [Fact]
    public void Installed_LaunchStepActive()
    {
        var state = State(UpdateStages.Done);
        state.Done = true;
        state.Ok = true;

        var view = Model().Evaluate(state, Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Installed, view.Outcome);
        Assert.Equal(UpdateWaitStepState.Active, view.Steps[^1].State);
        Assert.All(view.Steps.Take(view.Steps.Count - 1), s => Assert.Equal(UpdateWaitStepState.Done, s.State));
    }

    [Fact]
    public void RolledBack_MarksTheFailedStep_AndNamesTheVersionLeft()
    {
        var state = State(UpdateStages.Done);
        state.Done = true;
        state.RolledBack = true;
        state.FailedStage = UpdateStages.Installing;
        state.Error = "Установщик завершился с кодом 2";

        var view = Model().Evaluate(state, Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Failed, view.Outcome);
        Assert.Contains("2.3.3", view.Message);
        Assert.Equal(UpdateWaitStepState.Failed, view.Steps[3].State);
        Assert.Equal(UpdateWaitStepState.Done, view.Steps[2].State);
        Assert.Equal(UpdateWaitStepState.Pending, view.Steps[4].State);
    }

    [Fact]
    public void Starting_InstalledAndWaitingForTheNewLauncher()
    {
        var view = Model().Evaluate(State(UpdateStages.Starting), Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Starting, view.Outcome);
        Assert.Equal(UpdateWaitStepState.Active, view.Steps[5].State);
        Assert.All(view.Steps.Take(5), s => Assert.Equal(UpdateWaitStepState.Done, s.State));
    }

    [Fact]
    public void NewLauncherDidNotStart_RolledBack_SaysSo()
    {
        var state = State(UpdateStages.Done);
        state.Done = true;
        state.RolledBack = true;
        state.FailedStage = UpdateStages.Starting;
        state.Error = "Новая версия лаунчера закрылась сразу после запуска.";

        var view = Model().Evaluate(state, Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Failed, view.Outcome);
        Assert.Contains("не запустилась", view.Message);
        Assert.Contains("2.3.3", view.Message);
        Assert.Equal(UpdateWaitStepState.Failed, view.Steps[5].State);
        Assert.Equal(UpdateWaitStepState.Done, view.Steps[4].State);
    }

    [Fact]
    public void CancelledBeforeAnyChange_ShowsTheServiceReason()
    {
        var state = State(UpdateStages.Done);
        state.Done = true;
        state.FailedStage = UpdateStages.Waiting;
        state.Error = "Лаунчер не закрылся за минуту. Обновление отменено, ничего не изменилось.";

        var view = Model().Evaluate(state, Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Failed, view.Outcome);
        Assert.Equal(state.Error, view.Message);
        Assert.Equal(UpdateWaitStepState.Failed, view.Steps[0].State);
    }

    [Fact]
    public void RollingBack_IsStillRunning_WithTheFailedStepMarked()
    {
        var state = State(UpdateStages.RollingBack);
        state.FailedStage = UpdateStages.Checking;

        var view = Model().Evaluate(state, Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Running, view.Outcome);
        Assert.Equal(UpdateWaitStepState.Failed, view.Steps[4].State);
    }

    [Fact]
    public void HelperSilent_IsLost()
    {
        var view = Model().Evaluate(State(UpdateStages.Installing, secondsAgo: 31), Start.AddSeconds(60));

        Assert.Equal(UpdateWaitOutcome.Lost, view.Outcome);
        Assert.Equal(UpdateWaitStepState.Failed, view.Steps[3].State);
    }

    [Fact]
    public void TooLong_IsLost()
    {
        var now = Start + UpdateWaitModel.TotalTimeout + TimeSpan.FromSeconds(1);
        var state = State(UpdateStages.Installing);
        state.Updated = now;

        Assert.Equal(UpdateWaitOutcome.Lost, Model().Evaluate(state, now).Outcome);
    }
}
