using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;

namespace NovelGm.Desktop.Api;

/// <summary>Locate novel-gm root and start FastAPI if port is free.</summary>
public sealed class BackendLauncher : IDisposable
{
    public const string Host = "127.0.0.1";
    public static int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("NOVEL_GM_PORT"), out var p) && p > 0 ? p : 18765;
    public static string BaseUrl => $"http://{Host}:{Port}";

    private Process? _process;
    private readonly string _logPath;
    private readonly string _rootFile;

    public string? Root { get; private set; }
    public string DataDir { get; }

    public BackendLauncher()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(local, "novel-gm");
        DataDir = Path.Combine(appDir, "storage");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(appDir);
        _logPath = Path.Combine(DataDir, "backend.log");
        _rootFile = Path.Combine(appDir, "root.txt");
    }

    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        var (up, runningRoot) = await HealthInfoAsync(ct);
        if (up)
        {
            // 端口上可能跑着另一个副本（例如旧的装机版）的后端：它的代码与当前副本不同，
            // 静默复用会让新界面配上旧后端（语音 404、新字段被丢弃）。这里直接拒绝。
            Root = ResolveRoot(promptIfMissing: false);
            if (runningRoot is not null && Root is not null && !SameRoot(runningRoot, Root))
            {
                throw new InvalidOperationException(L.T("Backend.ForeignBackend", Port, runningRoot));
            }
            Log("backend already up");
            return;
        }

        Root = ResolveRoot(promptIfMissing: true);
        if (Root is null)
        {
            throw new InvalidOperationException(
                L.T("Backend.RootNotFound"));
        }

        var python = new[]
        {
            Path.Combine(Root, "runtime", "python", "python.exe"),
            Path.Combine(Root, ".venv", "Scripts", "python.exe"),
        }.FirstOrDefault(File.Exists);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["NOVEL_GM_ROOT"] = Root;
        psi.Environment["NOVEL_GM_DATA"] = DataDir;
        psi.Environment["NOVEL_GM_PORT"] = Port.ToString();

        if (python is not null)
        {
            psi.FileName = python;
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("services.core");
        }
        else
        {
            psi.FileName = "py";
            psi.ArgumentList.Add("-3.13");
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("services.core");
        }

        Log($"spawning {psi.FileName} in {Root}");
        _process = Process.Start(psi) ?? throw new InvalidOperationException(L.T("Backend.StartFailed"));

        _ = Task.Run(() => Drain(_process.StandardOutput), CancellationToken.None);
        _ = Task.Run(() => Drain(_process.StandardError), CancellationToken.None);

        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await HealthOkAsync(ct))
            {
                Log($"ready after {i + 1} checks");
                return;
            }
            await Task.Delay(500, ct);
        }

        throw new TimeoutException(L.T("Backend.PortTimeout", Port, _logPath));
    }

    /// <summary>
    /// 0) installed layout (exe dir with runtime\python)  1) NOVEL_GM_ROOT
    /// 2) %LOCALAPPDATA%\novel-gm\root.txt  3) walk up from exe  4) optional folder picker
    /// </summary>
    public string? ResolveRoot(bool promptIfMissing)
    {
        // Not persisted to root.txt so an installed copy never redirects a dev checkout.
        var bundled = BundledRoot();
        if (bundled is not null)
            return bundled;

        var found = FindRoot();
        if (found is not null)
        {
            SaveRoot(found);
            return found;
        }

        if (!promptIfMissing)
            return null;

        string? picked = null;
        void ShowPicker()
        {
            MessageBox.Show(
                L.T("Backend.PickRootPrompt"),
                L.T("App.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            picked = FolderPicker.Pick(L.T("Backend.PickRoot"));
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true)
            ShowPicker();
        else if (Application.Current is not null)
            Application.Current.Dispatcher.Invoke(ShowPicker);
        else
            ShowPicker();

        if (picked is not null && IsValidRoot(picked))
        {
            SaveRoot(picked);
            return Path.GetFullPath(picked);
        }

        if (picked is not null)
            Log($"invalid root chosen: {picked}");

        return null;
    }

    public void SaveRoot(string root)
    {
        try
        {
            var full = Path.GetFullPath(root);
            if (!IsValidRoot(full)) return;
            File.WriteAllText(_rootFile, full);
            Log($"saved root.txt => {full}");
        }
        catch (Exception ex)
        {
            Log($"save root failed: {ex.Message}");
        }
    }

    public static bool IsValidRoot(string path) =>
        File.Exists(Path.Combine(path, "services", "core", "main.py"));

    private static string? BundledRoot()
    {
        var dir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        if (!File.Exists(Path.Combine(dir, "runtime", "python", "python.exe")) || !IsValidRoot(dir))
            return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
    }

    private string? ReadSavedRoot()
    {
        try
        {
            if (!File.Exists(_rootFile)) return null;
            var line = File.ReadAllText(_rootFile).Trim();
            if (string.IsNullOrWhiteSpace(line)) return null;
            return IsValidRoot(line) ? Path.GetFullPath(line) : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task Drain(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                Log(line);
        }
        catch { /* ignore */ }
    }

    private void Log(string msg)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    public static async Task<bool> HealthOkAsync(CancellationToken ct = default)
        => (await HealthInfoAsync(ct)).Ok;

    /// <summary>探测端口上的后端；同时读出它自报的根目录，用来判断是不是同一个副本。</summary>
    public static async Task<(bool Ok, string? Root)> HealthInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await client.GetAsync($"{BaseUrl}/health", ct);
            if (!resp.IsSuccessStatusCode) return (false, null);
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement.TryGetProperty("paths", out var paths)
                           && paths.TryGetProperty("root", out var r)
                    ? r.GetString()
                    : null;
                return (true, root);
            }
            catch (JsonException)
            {
                return (true, null);  // 起来了但读不出根目录：按旧行为照旧复用，不误判
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return (false, null);
        }
    }

    /// <summary>两个根目录是否指同一个地方；路径本身非法时按「相同」处理，不误伤。</summary>
    private static bool SameRoot(string a, string b)
    {
        try
        {
            static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public string? FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("NOVEL_GM_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && IsValidRoot(env))
            return Path.GetFullPath(env);

        var saved = ReadSavedRoot();
        if (saved is not null)
            return saved;

        var starts = new List<string>();
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var exeDir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(exeDir))
                    starts.Add(exeDir);
            }
        }
        catch { /* ignore */ }

        if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
            starts.Add(AppContext.BaseDirectory);
        starts.Add(Environment.CurrentDirectory);

        foreach (var start in starts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 12 && dir is not null; i++)
            {
                if (IsValidRoot(dir.FullName))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
        }
        catch { /* ignore */ }
    }
}
