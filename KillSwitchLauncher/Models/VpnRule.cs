using System;
using System.Collections.Generic;

namespace KillSwitchLauncher.Models;

/// <summary>Неизменяемый снимок правила для монитора VPN (безопасно читать из фонового потока).</summary>
public sealed class VpnRule
{
    public VpnRule(VpnRuleMode mode, IEnumerable<string> selectedAdapters)
    {
        Mode = mode;
        SelectedAdapters = new HashSet<string>(selectedAdapters, StringComparer.OrdinalIgnoreCase);
    }

    public VpnRuleMode Mode { get; }

    public IReadOnlySet<string> SelectedAdapters { get; }
}
