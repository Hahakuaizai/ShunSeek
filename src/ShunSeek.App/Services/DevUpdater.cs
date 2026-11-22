using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShunSeek.App.Services;

/// <summary>一次"检测"的结果：开发目录里哪些源码比安装目录里的程序新。</summary>
internal sealed class UpdateCheckResult
{
    public required string DevRoot { get; init; }
    public required string TargetRoot { get; init; }
    public DateTime? InstalledAt { get; init; }
    public DateTime? NewestSource { get; init; }
    public int NewerFiles { get; init; }
    public bool CanCompare { get; init; }
    public string? Problem { get; init; }

    public bool HasUpdate => CanCompare && NewerFiles > 0;
}

/// <summary>重启后要弹给用户看的更新说明。</summary>
public sealed class PendingUpdate
{
    public string Time { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Files { get; set; }
    public List<string> Items { get; set; } = new();
}

/// <summary>
/// 开发版更新：比对开发目录源码与安装目录程序的时间戳，确认有新改动后
/// 调 dotnet publish 重新编译到暂存目录，再交给一个后台 PowerShell 脚本
/// 等本进程退出 → 覆盖安装目录 → 拉起新版本（带 --updated）。
/// 换文件必须等进程退出，运行中的 exe/dll 是锁着的，所以只能借外部脚本动手。
/// </summary>
internal static class DevUpdater
{
    public const string DefaultDevRoot = @"E:\project_开\ShunSeek\ShunSeek";

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShunSeek");

    private static string DevRootFile => Path.Combine(ConfigDir, "devroot.txt");
    private static string StagingDir => Path.Combine(ConfigDir, "update-staging");
    private static string PendingFile => Path.Combine(ConfigDir, "pending-update.json");
    private static string DeployedMarker => Path.Combine(ConfigDir, "deployed-at.txt");
    private static string LogFile => Path.Combine(ConfigDir, "logs", "update.log");

    /// <summary>安装目录 = 当前进程所在目录。</summary>
    public static string TargetRoot => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string DevRoot
    {
        get
        {
            try
            {
                if (File.Exists(DevRootFile))
                {
                    var saved = File.ReadAllText(DevRootFile).Trim();
                    if (saved.Length > 0 && Directory.Exists(saved)) return saved;
                }
            }
            catch { /* 配置文件坏了就用默认 */ }
            return DefaultDevRoot;
        }
    }

    public static void SaveDevRoot(string path)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(DevRootFile, path.Trim());
    }

    /// <summary>源码文件：只认会进产物的类型，bin/obj 这类中间产物不算。</summary>
    private static IEnumerable<string> SourceFiles(string devRoot)
    {
        var src = Path.Combine(devRoot, "src");
        if (!Directory.Exists(src)) return Array.Empty<string>();

        string[] extensions = { ".cs", ".xaml", ".csproj", ".axaml", ".manifest", ".ico", ".ps1", ".json" };
        return Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase));
    }

    public static UpdateCheckResult Check()
    {
        var devRoot = DevRoot;
        var installedDll = Path.Combine(TargetRoot, "ShunSeek.dll");

        if (!Directory.Exists(devRoot))
            return new UpdateCheckResult
            {
                DevRoot = devRoot,
                TargetRoot = TargetRoot,
                CanCompare = false,
                Problem = "开发目录不存在，可在设置里重新指定",
            };

        if (!File.Exists(installedDll))
            return new UpdateCheckResult
            {
                DevRoot = devRoot,
                TargetRoot = TargetRoot,
                CanCompare = false,
                Problem = "找不到安装目录里的 ShunSeek.dll，无法判断新旧",
            };

        var installedAt = File.GetLastWriteTime(installedDll);
        var files = SourceFiles(devRoot).ToList();
        if (files.Count == 0)
            return new UpdateCheckResult
            {
                DevRoot = devRoot,
                TargetRoot = TargetRoot,
                InstalledAt = installedAt,
                CanCompare = false,
                Problem = "开发目录里没有找到源码",
            };

        DateTime newest = DateTime.MinValue;
        int newer = 0;
        foreach (var file in files)
        {
            var time = File.GetLastWriteTime(file);
            if (time > newest) newest = time;
            if (time > installedAt) newer++;
        }

        return new UpdateCheckResult
        {
            DevRoot = devRoot,
            TargetRoot = TargetRoot,
            InstalledAt = installedAt,
            NewestSource = newest,
            NewerFiles = newer,
            CanCompare = true,
        };
    }

    /// <summary>编译到暂存目录；成功返回 null，失败返回一句可以直接显示的原因。</summary>
    public static async Task<string?> PublishAsync(Action<string> log)
    {
        var devRoot = DevRoot;
        if (!Directory.Exists(devRoot)) return $"开发目录不存在：{devRoot}";

        var dotnet = FindDotnet();
        if (dotnet == null) return "没找到 dotnet SDK（需要 .NET 8 SDK）";

        try
        {
            if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true);
            Directory.CreateDirectory(StagingDir);
        }
        catch (Exception ex)
        {
            return "清空暂存目录失败：" + ex.Message;
        }

        log("开始编译…");
        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = devRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet) ?? string.Empty;
        foreach (var arg in new[]
                 {
                     "publish", Path.Combine("src", "ShunSeek.App"),
                     "-c", "Release", "-r", "win-x64", "--self-contained", "true",
                     "-p:PublishSingleFile=false", "-p:PublishReadyToRun=true",
                     "-o", StagingDir, "--nologo",
                 })
            psi.ArgumentList.Add(arg);

        var tail = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => AppendTail(tail, e.Data);
        process.ErrorDataReceived += (_, e) => AppendTail(tail, e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            log("编译失败：" + ex.Message);
            return "编译失败：" + ex.Message;
        }

        if (process.ExitCode != 0)
        {
            log("编译失败，退出码 " + process.ExitCode);
            return "编译失败（退出码 " + process.ExitCode + "）：" + Short(tail.ToString());
        }

        var stagedExe = Path.Combine(StagingDir, "ShunSeek.exe");
        if (!File.Exists(stagedExe)) return "编译产物里没有 ShunSeek.exe";

        var payload = new PendingUpdate
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Target = TargetRoot,
            Files = SourceFiles(devRoot).Count(f => File.GetLastWriteTime(f) > (Check().InstalledAt ?? DateTime.MinValue)),
            Items = BuildChangeList(devRoot, LastDeployedAt()),
        };
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(PendingFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        try { File.WriteAllText(DeployedMarker, DateTime.Now.ToString("O")); } catch { /* 写不进去只影响下次说明的基准 */ }
        log($"编译完成，暂存 {Path.GetFileName(StagingDir)}，改动 {payload.Files} 个文件");
        return null;
    }

    /// <summary>上次部署（或上次更新）时间：更新说明的 git 提交就从这一刻开始数，没有就退回 dll 时间。</summary>
    private static DateTime LastDeployedAt()
    {
        try
        {
            if (File.Exists(DeployedMarker))
            {
                var text = File.ReadAllText(DeployedMarker).Trim();
                if (DateTime.TryParse(text, out var parsed)) return parsed;
            }
        }
        catch { /* 标记坏了按兜底处理 */ }

        var installedDll = Path.Combine(TargetRoot, "ShunSeek.dll");
        return File.Exists(installedDll) ? File.GetLastWriteTime(installedDll) : DateTime.Now.AddDays(-7);
    }

    /// <summary>更新说明优先取 git 提交标题（比"文件变了"更像人话），没有 git 就退回文件数。</summary>
    private static List<string> BuildChangeList(string devRoot, DateTime since)
    {
        var items = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = devRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("log");
            psi.ArgumentList.Add("--no-merges");
            psi.ArgumentList.Add("--pretty=format:%s");
            psi.ArgumentList.Add("--since=" + since.ToString("yyyy-MM-ddTHH:mm:ss"));

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                foreach (var line in output.Split('\n'))
                {
                    var text = line.Trim();
                    if (text.Length > 0 && !items.Contains(text)) items.Add(text);
                }
            }
        }
        catch { /* git 不在或不是仓库：忽略 */ }

        return items;
    }

    /// <summary>把覆盖安装 + 重启交给一个后台脚本，本进程随后退出。</summary>
    public static bool LaunchUpdaterAndRestart(Action<string> log)
    {
        var target = TargetRoot;
        var exe = Path.Combine(target, "ShunSeek.exe");
        var script = Path.Combine(ConfigDir, "apply-update.ps1");
        var pwsh = FindPwsh();
        if (pwsh == null)
        {
            log("没有找到 pwsh 7，无法自动替换文件");
            return false;
        }

        Directory.CreateDirectory(ConfigDir);
        var body = string.Join(Environment.NewLine, new[]
        {
            "param([int]$WaitPid,[string]$Staging,[string]$Target,[string]$Exe,[string]$Log)",
            "$deadline = (Get-Date).AddSeconds(90)",
            "while ((Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 400 }",
            "Start-Sleep -Milliseconds 800",
            "try {",
            "  robocopy $Staging $Target /E /IS /IT /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null",
            "  Add-Content -LiteralPath $Log -Value (\"[\" + (Get-Date -Format \"HH:mm:ss\") + \"] replace exit=\" + $LASTEXITCODE)",
            "} catch { Add-Content -LiteralPath $Log -Value (\"replace failed: \" + $_) }",
            "Start-Sleep -Milliseconds 300",
            "Start-Process -FilePath $Exe -ArgumentList '--updated'",
            "Start-Sleep -Seconds 2",
            "Remove-Item -LiteralPath $Staging -Recurse -Force -ErrorAction SilentlyContinue",
        });
        try
        {
            File.WriteAllText(script, body, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            log("写更新脚本失败：" + ex.Message);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(pwsh)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                         "-WaitPid", Environment.ProcessId.ToString(),
                         "-Staging", StagingDir, "-Target", target, "-Exe", exe, "-Log", LogFile,
                     })
                psi.ArgumentList.Add(arg);

            Process.Start(psi);
            log("已交给后台脚本替换文件，准备重启");
            return true;
        }
        catch (Exception ex)
        {
            log("拉起更新脚本失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>重启后读一次更新说明，读完即删，避免每次启动都弹。</summary>
    public static PendingUpdate? TakePendingUpdate()
    {
        try
        {
            if (!File.Exists(PendingFile)) return null;
            var json = File.ReadAllText(PendingFile, Encoding.UTF8);
            File.Delete(PendingFile);
            return JsonSerializer.Deserialize<PendingUpdate>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* 日志失败不影响功能 */ }
    }

    private static void AppendTail(StringBuilder tail, string? line)
    {
        if (line == null) return;
        tail.AppendLine(line);
        if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
    }

    private static string Short(string text) =>
        text.Length <= 600 ? text.Trim() : text[^600..].Trim();

    private static string? FindDotnet()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
        {
            var fromEnv = Path.Combine(root, "dotnet.exe");
            if (File.Exists(fromEnv)) return fromEnv;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe"),
            @"C:\Program Files\dotnet\dotnet.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", "dotnet.exe"),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir.Trim(), "dotnet.exe");
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string? FindPwsh()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe"),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir.Trim(), "pwsh.exe");
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
