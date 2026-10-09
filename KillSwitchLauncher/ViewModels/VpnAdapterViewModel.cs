using System;
using System.Windows.Input;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Строка в панели «Правила VPN».</summary>
public sealed class VpnAdapterViewModel : ObservableObject
{
    private readonly Action<VpnAdapterViewModel> _selectionChanged;
    private bool _isSelected;
    private bool _isUp;
    private bool _isPresent;
    private string _description;

    private string _product;

    public VpnAdapterViewModel(string name, string description, string product, bool isVpnLike, bool isUp, bool isPresent, bool isSelected,
        Action<VpnAdapterViewModel> selectionChanged, Action<VpnAdapterViewModel>? forget = null)
    {
        Name = name;
        _product = product;
        _description = description;
        IsVpnLike = isVpnLike;
        _isUp = isUp;
        _isPresent = isPresent;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
        if (forget is not null)
            ForgetCommand = new RelayCommand(() => forget(this));
    }

    /// <summary>«Забыть» адаптер из списка удалённых; null — для адаптеров, которые сейчас есть в системе.</summary>
    public ICommand? ForgetCommand { get; }

    /// <summary>Системное имя адаптера (по нему сохраняется выбор), например «Подключение по локальной сети».</summary>
    public string Name { get; }

    /// <summary>Понятное название VPN, например «OpenVPN (TAP)».</summary>
    public string Product
    {
        get => _product;
        set => SetField(ref _product, value);
    }

    public bool IsVpnLike { get; }

    public string Description
    {
        get => _description;
        set
        {
            if (SetField(ref _description, value))
                OnPropertyChanged(nameof(Details));
        }
    }

    /// <summary>Вторая строка: системное имя и драйвер, чтобы было видно, что это за адаптер.</summary>
    public string Details => string.Equals(Name, Description, StringComparison.OrdinalIgnoreCase) ? Name : $"{Name} · {Description}";

    public bool IsUp
    {
        get => _isUp;
        set
        {
            if (SetField(ref _isUp, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>false — адаптер выбран в правилах, но сейчас в системе его нет (например, туннель WireGuard остановлен).</summary>
    public bool IsPresent
    {
        get => _isPresent;
        set
        {
            if (SetField(ref _isPresent, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => !IsPresent ? "нет в системе" : IsUp ? "Активен" : "Не активен";

    /// <summary>Обновить отметку без уведомления лаунчера (при сбросе черновика).</summary>
    public void SetSelectedSilently(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                _selectionChanged(this);
        }
    }
}
