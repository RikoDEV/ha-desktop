using System;
using System.Windows.Input;

namespace HaDesktop.Tray;

/// <summary>Minimal ICommand for tray/menu actions that never need CanExecute logic.</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
