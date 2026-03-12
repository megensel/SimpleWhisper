using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimpleWhisper.ViewModels;

/// <summary>
/// Base class for all view models, providing a standard <see cref="INotifyPropertyChanged"/>
/// implementation with a convenient <see cref="SetProperty{T}"/> helper.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the <see cref="PropertyChanged"/> event for the specified property.
    /// </summary>
    /// <param name="propertyName">
    /// The name of the property that changed. Automatically supplied by the compiler
    /// when called from within a property setter.
    /// </param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Sets the backing field to the new value and raises <see cref="PropertyChanged"/>
    /// if the value actually changed. Returns <c>true</c> if the value was updated.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="field">A reference to the backing field.</param>
    /// <param name="value">The new value to assign.</param>
    /// <param name="propertyName">
    /// The name of the property. Automatically supplied by the compiler.
    /// </param>
    /// <returns>
    /// <c>true</c> if the field was changed; <c>false</c> if the new value was equal to the current value.
    /// </returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Sets the backing field to the new value, raises <see cref="PropertyChanged"/>
    /// if the value changed, and then invokes the specified callback. Useful when
    /// dependent properties or commands need to be refreshed after a change.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="field">A reference to the backing field.</param>
    /// <param name="value">The new value to assign.</param>
    /// <param name="onChanged">A callback invoked after the value changes and the event fires.</param>
    /// <param name="propertyName">
    /// The name of the property. Automatically supplied by the compiler.
    /// </param>
    /// <returns>
    /// <c>true</c> if the field was changed; <c>false</c> if the new value was equal to the current value.
    /// </returns>
    protected bool SetProperty<T>(ref T field, T value, Action onChanged, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
            return false;

        onChanged();
        return true;
    }
}
