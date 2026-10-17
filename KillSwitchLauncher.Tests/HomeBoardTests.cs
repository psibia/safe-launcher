using System;
using System.Collections.ObjectModel;
using System.Linq;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.ViewModels;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Перестановка плиток без мыши и разметки: порядок, сохранение, отмена.
/// Только Windows: плитка приложения ссылается на ImageSource (PresentationCore), на Linux сборка не загружается.
/// </summary>
public class HomeBoardTests
{
    private sealed class Board
    {
        public AppSettings Settings { get; } = new();
        public ObservableCollection<AppTileViewModel> Apps { get; } = new();
        public int Saves { get; set; }
        public HomeBoardViewModel Home { get; }

        public Board(int count)
        {
            for (var i = 1; i <= count; i++)
                Add($"App{i}");
            Home = new HomeBoardViewModel(Settings, Apps, () => Saves++);
        }

        public AppTileViewModel this[int index] => Apps[index];

        public void Add(string name)
        {
            var entry = new AppEntry { Name = name };
            Settings.Apps.Add(entry);
            Apps.Add(Tile(entry));
        }

        /// <summary>Плитки на экране по порядку.</summary>
        public string Screen() => string.Join(" ", Apps.Select(a => a.Name));

        /// <summary>Порядок в настройках.</summary>
        public string Saved() => string.Join(" ", Settings.Apps.Select(a => a.Name));
    }

    private static AppTileViewModel Tile(AppEntry entry)
    {
        var none = new RelayCommand(() => { });
        return new AppTileViewModel(entry, null, none, none, none);
    }

    [Fact]
    public void Drag_MovesAndSavesOnce()
    {
        var board = new Board(4);

        Assert.True(board.Home.BeginDrag(board[1]));
        Assert.True(board[1].IsDragSource);
        Assert.Equal("App1 App2 App3 App4", board.Saved()); // во время перетаскивания настройки не меняются
        board.Home.MoveDragged(0);
        board.Home.MoveDragged(3);
        Assert.Equal(3, board.Home.DraggedIndex);
        board.Home.Drop();

        Assert.False(board.Home.IsDragging);
        Assert.Equal("App1 App3 App4 App2", board.Screen());
        Assert.Equal("App1 App3 App4 App2", board.Saved());
        Assert.DoesNotContain(board.Apps, a => a.IsDragSource);
        Assert.Equal(1, board.Saves);
    }

    [Fact]
    public void Drop_OnSamePlace_DoesNotSave()
    {
        var board = new Board(3);

        board.Home.BeginDrag(board[1]);
        board.Home.MoveDragged(0);
        board.Home.MoveDragged(1);
        board.Home.Drop();

        Assert.Equal("App1 App2 App3", board.Screen());
        Assert.Equal(0, board.Saves);
    }

    [Fact]
    public void MoveDragged_ClampsIndex()
    {
        var board = new Board(3);

        board.Home.BeginDrag(board[0]);
        board.Home.MoveDragged(10);
        Assert.Equal("App2 App3 App1", board.Screen());
        board.Home.MoveDragged(-5);
        Assert.Equal("App1 App2 App3", board.Screen());
        board.Home.CancelDrag();
    }

    [Fact]
    public void CancelDrag_RestoresOrder_WithoutSaving()
    {
        var board = new Board(4);
        var tiles = board.Apps.ToList();

        board.Home.BeginDrag(board[3]);
        board.Home.MoveDragged(0);
        board.Home.MoveDragged(2);
        board.Home.CancelDrag();

        Assert.Equal(tiles, board.Apps); // те же плитки в прежнем порядке
        Assert.Equal("App1 App2 App3 App4", board.Saved());
        Assert.False(board.Home.IsDragging);
        Assert.False(tiles[3].IsDragSource);
        Assert.Equal(0, board.Saves);
    }

    [Fact]
    public void DropThenCancel_KeepsResult_SavesOnce()
    {
        var board = new Board(3);

        board.Home.BeginDrag(board[0]);
        board.Home.MoveDragged(2);
        board.Home.Drop();
        board.Home.CancelDrag(); // захват мыши отпущен после Drop
        board.Home.Drop();
        board.Home.MoveDragged(0);

        Assert.Equal("App2 App3 App1", board.Screen());
        Assert.Equal("App2 App3 App1", board.Saved());
        Assert.Equal(1, board.Saves);
    }

    [Fact]
    public void BeginDrag_Twice_OrUnknownTile_IsRefused()
    {
        var board = new Board(2);
        var stranger = Tile(new AppEntry { Name = "Чужое" });

        Assert.False(board.Home.BeginDrag(stranger));
        Assert.True(board.Home.BeginDrag(board[0]));
        Assert.False(board.Home.BeginDrag(board[1]));
        board.Home.CancelDrag();
    }

    [Fact]
    public void AppsChangedDuringDrag_EndsDrag_SettingsFollowScreen()
    {
        var board = new Board(3);

        board.Home.BeginDrag(board[0]);
        board.Home.MoveDragged(2);
        board.Add("App4");

        Assert.False(board.Home.IsDragging);
        Assert.DoesNotContain(board.Apps, a => a.IsDragSource);
        Assert.Equal("App2 App3 App1 App4", board.Screen());
        Assert.Equal("App2 App3 App1 App4", board.Saved());

        // Удаление вне перетаскивания порядок не трогает.
        board.Settings.Apps.Remove(board[0].Entry);
        board.Apps.RemoveAt(0);
        Assert.Equal("App3 App1 App4", board.Saved());
        Assert.Equal(0, board.Saves);
    }

    [Fact]
    public void Order_SurvivesSaveAndLoad()
    {
        using var dir = new TempDir();
        var storage = new KillSwitchLauncher.Services.SettingsStorage(dir.File("settings.json"));
        var board = new Board(3);

        board.Home.BeginDrag(board[2]);
        board.Home.MoveDragged(0);
        board.Home.Drop();
        storage.Save(board.Settings);

        Assert.Equal(new[] { "App3", "App1", "App2" }, storage.Load().Apps.Select(a => a.Name));
    }
}
