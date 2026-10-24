using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Collectors;

/// <summary>
/// The elevated side: runs the kernel latency trace and streams one JSON line per second through a named pipe that
/// only the starting user may open. It accepts no commands and exits when the WhyLag process that started it exits.
/// Started as <c>WhyLag.exe --latency-helper &lt;pipe&gt; &lt;user SID&gt; &lt;parent PID&gt;</c>.
/// </summary>
public static class LatencyHelperHost
{
    public const string Argument = "--latency-helper";

    public static int Run(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[3], out var parentPid)) return 2;
        var pipeName = args[1];
        var userSid = new SecurityIdentifier(args[2]);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch (ArgumentException)
        {
            return 4;
        }

        // A helper that was killed leaves the kernel trace running; its marker file tells us it was ours.
        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhyLag", "latency-helper.pid");
        if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out var oldPid) && !IsRunning(oldPid))
            KernelLatencyTracer.StopKernelLogger();

        using var tracer = new KernelLatencyTracer();
        tracer.Start();
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, Environment.ProcessId.ToString());
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            tracer.Dispose();
            File.Delete(marker);
        };

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        using var exit = new CancellationTokenSource();
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => exit.Cancel();
        if (parent.HasExited) return 0;

        // The helper serves exactly one WhyLag recorder. If nobody connects (or reconnects) in time, the recorder
        // was restarted or closed, so the helper ends and frees the kernel trace.
        var connectTimeout = TimeSpan.FromSeconds(60);
        while (!exit.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security);
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(exit.Token);
                wait.CancelAfter(connectTimeout);
                pipe.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult();
                connectTimeout = TimeSpan.FromSeconds(10);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                tracer.TakeSecond(); // discard what accumulated while nobody listened
                while (!exit.IsCancellationRequested && pipe.IsConnected)
                {
                    exit.Token.WaitHandle.WaitOne(1000);
                    writer.WriteLine(JsonSerializer.Serialize(tracer.TakeSecond(), Json.Compact));
                }
            }
            catch (OperationCanceledException)
            {
                break; // WhyLag exited or nobody connected in time
            }
            catch (IOException)
            {
                // The client went away; give it a short chance to reconnect.
            }
        }
        tracer.Dispose();
        File.Delete(marker);
        return 0;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The WhyLag side: optionally starts the elevated helper (Windows asks for administrator approval) and adds its
/// per-driver DPC/ISR measurements to each tick.
/// </summary>
public sealed class LatencyHelperSource : IMetricSource
{
    private readonly CancellationTokenSource _cts = new();
    private volatile LatencySecond? _latest;
    private volatile string? _error;
    private Process? _helper;

    public string Name => WhyLag.Core.Localization.Loc.T("source.latency");

    public void Start()
    {
        var pipeName = $"WhyLag.Latency.{Guid.NewGuid():N}";
        _ = Task.Run(() => RunAsync(pipeName, _cts.Token));
    }

    private async Task RunAsync(string pipeName, CancellationToken ct)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            _helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { LatencyHelperHost.Argument, pipeName, identity.User!.Value, Environment.ProcessId.ToString() },
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception)
        {
            _error = T("err.helper.declined");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    _latest = JsonSerializer.Deserialize<LatencySecond>(line, Json.Compact);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException)
            {
                if (_helper is { HasExited: true } h)
                {
                    _error = h.ExitCode switch
                    {
                        3 => T("err.kernel.admin"),
                        _ => T("err.helper.stopped"),
                    };
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Collect(Tick tick)
    {
        if (_error is { } error) throw new InvalidOperationException(error);
        if (_latest is not { } s || DateTimeOffset.Now - s.Timestamp > TimeSpan.FromSeconds(2.5)) return;
        tick.Set(MetricKeys.DpcMaxUs, s.DpcMaxUs);
        tick.Set(MetricKeys.IsrMaxUs, s.IsrMaxUs);
        tick.Drivers.AddRange(s.Drivers);
    }

    public void Dispose() => _cts.Cancel(); // the helper exits by itself when WhyLag exits
}
