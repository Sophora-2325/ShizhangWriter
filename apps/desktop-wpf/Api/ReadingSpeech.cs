using System.Runtime.InteropServices;
using System.Windows.Threading;
namespace NovelGm.Desktop.Api;
internal sealed record ReadingVoice(string Id, string Name);
/// <summary>All SAPI calls belong to one background STA dispatcher.</summary>
internal sealed class ReadingSpeech : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private dynamic? _voice;
    private readonly Func<object>? _createVoice;
    private DispatcherTimer? _timer;
    private bool _active, _paused, _disposed;
    private int _offset;
    public event Action<int>? Progress;
    public event Action? Completed;
    public event Action<string>? Failed;
    public ReadingSpeech(Func<object>? createVoice = null)
    {
        _createVoice = createVoice;
        var thread = new Thread(() => { _ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "NovelGm local speech" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    private void EnsureVoice()
    {
        if (_voice is not null) return;
        _voice = _createVoice is not null ? _createVoice() : Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("No SAPI voice"));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) =>
        {
            try
            {
                if (!_active || _paused) return;
                Progress?.Invoke(_offset + (int)_voice!.Status.InputWordPosition);
                if ((int)_voice!.Status.RunningState == 1) { _active = false; _timer.Stop(); Completed?.Invoke(); }
            }
            catch (Exception ex) { _active = false; _timer.Stop(); Failed?.Invoke(ex.Message); }
        };
    }
    public async Task<IReadOnlyList<ReadingVoice>> VoicesAsync()
    {
        if (_disposed) return Array.Empty<ReadingVoice>();
        var dispatcher = await _ready.Task;
        return await dispatcher.InvokeAsync<IReadOnlyList<ReadingVoice>>(() =>
        {
            try
            {
                EnsureVoice(); dynamic voices = _voice!.GetVoices(); var result = new List<ReadingVoice>();
                for (var i = 0; i < (int)voices.Count; i++) { dynamic token = voices.Item(i); result.Add(new((string)token.Id, (string)token.GetDescription())); Release(token); }
                Release(voices); return result;
            }
            catch (Exception ex) { Failed?.Invoke(ex.Message); return Array.Empty<ReadingVoice>(); }
        });
    }
    public async Task SpeakAsync(string text, int offset, ReadingPreferences prefs)
    {
        if (_disposed) return;
        var dispatcher = await _ready.Task;
        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                EnsureVoice(); StopCore(); dynamic voices = _voice!.GetVoices();
                if ((int)voices.Count == 0) throw new InvalidOperationException("No voices installed");
                for (var i = 0; i < (int)voices.Count; i++)
                {
                    dynamic token = voices.Item(i); var id = (string)token.Id;
                    var match = id == prefs.Voice || (string.IsNullOrEmpty(prefs.Voice) && id.Contains("ZH-CN", StringComparison.OrdinalIgnoreCase));
                    if (match) _voice.Voice = token;
                    Release(token); if (match) break;
                }
                Release(voices); _voice.Rate = prefs.SpeechRate; _voice.Volume = prefs.SpeechVolume;
                _offset = Math.Clamp(offset, 0, text.Length);
                var spoken = System.Text.RegularExpressions.Regex.Replace(text[_offset..], @"«插图:[^»]*»", m => new string(' ', m.Length)).Replace('\uFFFC', ' ');
                if (string.IsNullOrWhiteSpace(spoken)) { Completed?.Invoke(); return; }
                _voice.Speak(spoken, 3); _active = true; _paused = false; _timer!.Start();
            }
            catch (Exception ex) { Failed?.Invoke(ex.Message); }
        });
    }
    private static void Release(object value) { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public void Pause() => Post(() => { if (_active && !_paused) { _voice!.Pause(); _paused = true; } });
    public void Resume() => Post(() => { if (_active && _paused) { _voice!.Resume(); _paused = false; } });
    public void Stop() => Post(StopCore);
    private void StopCore() { _timer?.Stop(); if (_voice is not null) { if (_paused) _voice.Resume(); _voice.Speak("", 3); } _active = _paused = false; }
    private async void Post(Action action)
    {
        if (_disposed) return; var dispatcher = await _ready.Task;
        if (!dispatcher.HasShutdownStarted) await dispatcher.InvokeAsync(() => { try { action(); } catch (Exception ex) { Failed?.Invoke(ex.Message); } });
    }
    public async void Dispose()
    {
        if (_disposed) return; _disposed = true; var dispatcher = await _ready.Task;
        await dispatcher.InvokeAsync(() => { try { StopCore(); if (_voice is not null) Release(_voice); } finally { _voice = null; dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
    }
}
