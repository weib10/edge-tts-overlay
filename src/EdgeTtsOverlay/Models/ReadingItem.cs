using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EdgeTtsOverlay.Models;

public sealed class ReadingItem : INotifyPropertyChanged
{
    private string _state = "等待中";
    public Guid Id { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string OriginalText { get; init; }
    public required IReadOnlyList<string> Segments { get; init; }
    public int CurrentSegment { get; set; }
    public string State { get => _state; set { _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); } }
    public string Display => $"{Title}  ·  {State}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

