using System.Windows.Input;

namespace SimpleWhisper.Helpers;

/// <summary>
/// A lightweight <see cref="ICommand"/> implementation that delegates execution to
/// <see cref="Action"/> and <see cref="Func{Boolean}"/> callbacks. Supports manual
/// re-evaluation of <see cref="CanExecute"/> via <see cref="RaiseCanExecuteChanged"/>.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>
    /// Creates a command that can always execute.
    /// </summary>
    /// <param name="execute">The action to run when the command is invoked.</param>
    public RelayCommand(Action execute)
        : this(_ => execute(), null) { }

    /// <summary>
    /// Creates a command with a <paramref name="canExecute"/> guard.
    /// </summary>
    /// <param name="execute">The action to run when the command is invoked.</param>
    /// <param name="canExecute">A predicate that determines whether the command can execute.</param>
    public RelayCommand(Action execute, Func<bool> canExecute)
        : this(_ => execute(), _ => canExecute()) { }

    /// <summary>
    /// Creates a command that accepts a command parameter.
    /// </summary>
    /// <param name="execute">The action to run, receiving the command parameter.</param>
    public RelayCommand(Action<object?> execute)
        : this(execute, null) { }

    /// <summary>
    /// Creates a command that accepts a command parameter and a <paramref name="canExecute"/> guard.
    /// </summary>
    /// <param name="execute">The action to run, receiving the command parameter.</param>
    /// <param name="canExecute">A predicate that determines whether the command can execute.</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Raises <see cref="CanExecuteChanged"/> so the UI re-queries <see cref="CanExecute"/>.
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
