using System.IO;
using NAudio.Wave;

namespace NovelGm.Desktop.Api;

/// <summary>16 kHz 单声道 WAV 录音，供本机 SenseVoice 转写。</summary>
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _path;
    private bool _disposed;

    public bool IsRecording => _waveIn is not null;

    public void Start()
    {
        if (IsRecording) return;
        _path = Path.Combine(Path.GetTempPath(), $"novel-gm-stt-{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(SampleRate, 16, 1);
        _waveIn = new WaveInEvent
        {
            WaveFormat = format,
            BufferMilliseconds = 50,
        };
        _writer = new WaveFileWriter(_path, format);
        _waveIn.DataAvailable += OnDataAvailable;
        try
        {
            _waveIn.StartRecording();
        }
        catch
        {
            CleanupPartial();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _writer is null) return;
        _writer.Write(e.Buffer, 0, e.BytesRecorded);
    }

    /// <summary>停止录音并返回 WAV 路径；无有效录音时返回 null。</summary>
    public string? Stop()
    {
        if (_waveIn is null) return null;
        var path = _path;
        try
        {
            _waveIn.StopRecording();
        }
        catch { /* ignore */ }
        _waveIn.DataAvailable -= OnDataAvailable;
        try { _writer?.Flush(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        try { _waveIn.Dispose(); } catch { /* ignore */ }
        _waveIn = null;
        _path = null;
        if (path is null || !File.Exists(path) || new FileInfo(path).Length < 44)
        {
            TryDelete(path);
            return null;
        }
        return path;
    }

    public void Cancel()
    {
        var path = Stop();
        TryDelete(path);
    }

    private void CleanupPartial()
    {
        if (_waveIn is not null) _waveIn.DataAvailable -= OnDataAvailable;
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        try { _waveIn?.Dispose(); } catch { /* ignore */ }
        _waveIn = null;
        TryDelete(_path);
        _path = null;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsRecording) Cancel();
        else CleanupPartial();
    }
}
