using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AEPiLightingSystem.Models;

/// <summary>
/// A single par light fixture. Each par light shows one solid color.
/// </summary>
public class ParLight : INotifyPropertyChanged
{
    private Color _color = Colors.Black;

    public int Id { get; }

    public string Label { get; }

    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value)
                return;

            _color = value;
            OnPropertyChanged();
        }
    }

    public ParLight(int id, string label)
    {
        Id = id;
        Label = label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
