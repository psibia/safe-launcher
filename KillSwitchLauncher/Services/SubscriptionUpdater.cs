using System;
using System.Linq;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Когда обновлять подписку и как применить скачанный список серверов.</summary>
public static class SubscriptionUpdater
{
    /// <summary>Пора обновить: подписка ни разу не загружалась или прошло больше заданного у неё времени (по умолчанию сутки).</summary>
    public static bool IsDue(VpnConnection connection, DateTimeOffset now) =>
        connection.Kind == ConnectionKind.Subscription &&
        !string.IsNullOrWhiteSpace(connection.SubscriptionUrl) &&
        (connection.SubscriptionServers.Count == 0 ||
         connection.SubscriptionUpdatedAt is not { } updated ||
         now - updated >= TimeSpan.FromHours(Math.Max(1, connection.SubscriptionUpdateHours)));

    /// <summary>
    /// Новый список серверов. Выбранный сервер остаётся выбранным, если он ещё есть в списке (сравнение без названия
    /// после «#»: провайдеры часто переименовывают серверы). true — изменился сервер, через который пойдёт трафик.
    /// </summary>
    public static bool Apply(VpnConnection connection, SubscriptionContent content, DateTimeOffset now)
    {
        var before = Subscriptions.Pick(connection.SubscriptionServers, connection.SelectedServer);
        connection.SubscriptionServers = content.Links.ToList();
        connection.SubscriptionUpdatedAt = now;

        var after = Subscriptions.Pick(connection.SubscriptionServers, connection.SelectedServer);
        if (connection.SelectedServer is not null && after is not null &&
            Subscriptions.ServerKey(after) != Subscriptions.ServerKey(connection.SelectedServer))
            connection.SelectedServer = null; // выбранного больше нет: берётся первый

        return before != after;
    }
}
