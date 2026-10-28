using System.ComponentModel;

namespace S3Explorer.Models;

/// <summary>
/// A running scan / download / delete / copy: one progress row with its own Cancel button.
/// Holds the profiles it works on; a profile runs only one task at a time.
/// </summary>
public sealed class BackgroundTask : INotifyPropertyChanged, IDisposable
{
    private string _text;

    public BackgroundTask(string text, IReadOnlyCollection<string> profileIds)
    {
        _text = text;
        ProfileIds = profileIds;
    }

    public CancellationTokenSource Cts { get; } = new();
    public CancellationToken Token => Cts.Token;
    public IReadOnlyCollection<string> ProfileIds { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose() => Cts.Dispose();
}
