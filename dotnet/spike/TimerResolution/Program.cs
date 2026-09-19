using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// ---------------------------------------------------------------------------
// Why this exists
//
// A side-by-side pacing comparison found that at the 10 ms interval floor the Go server
// delivers 200 frames in 2 s while the ASP.NET Core server delivers 132, with a ~15.5 ms
// median instead of 10 ms. The cause is not the pacer's logic: it is that every .NET waiting
// primitive is quantised to the Windows system tick.
//
//   requested      1      2      5     10     16     20     50    100
//   Task.Delay  15.31  15.61  15.18  15.65  27.75  31.03  62.27 109.18   (ms, average)
//
// Every figure is ceil(n / 15.625) * 15.625. Go reaches a 10.0 ms median because its
// runtime waits on a high-resolution waitable timer, which is not subject to that tick.
//
// This program measures whether .NET can use the same primitive, in both a blocking form
// and an awaitable form suitable for the streaming loop.
// ---------------------------------------------------------------------------

const int Iterations = 40;
const int TargetMs = 10;

Console.WriteLine($"target = {TargetMs} ms, {Iterations} samples each, .NET {Environment.Version} on {RuntimeInformation.OSDescription}");
Console.WriteLine();

Measure("Task.Delay (current Pacer)", _ => Task.Delay(TargetMs));
Measure("Thread.Sleep", _ =>
{
    Thread.Sleep(TargetMs);
    return Task.CompletedTask;
});
Measure("high-res timer, blocking", _ =>
{
    using var timer = HighResolutionTimer.Create();
    timer.WaitRelative(TimeSpan.FromMilliseconds(TargetMs));
    return Task.CompletedTask;
});
Measure("high-res timer, async", async _ =>
{
    // The await has to happen inside the lambda: returning the pending task would dispose
    // the timer while the wait is still outstanding, closing the handle underneath it.
    using var timer = HighResolutionTimer.Create();
    await timer.WaitRelativeAsync(TimeSpan.FromMilliseconds(TargetMs)).ConfigureAwait(false);
});

// The real shape of the problem: the pacer waits for an absolute deadline, so the wait
// length varies frame to frame. This measures the deadline error rather than the sleep
// length, which is the number that actually shows up at the client.
Console.WriteLine();
MeasureDeadline("Task.Delay", TargetMs, async (sw, target) =>
{
    var remaining = target - sw.Elapsed.TotalMilliseconds;
    if (remaining > 0)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(remaining));
    }
});

using (var timer = HighResolutionTimer.Create())
{
    MeasureDeadline("high-res timer", TargetMs, async (sw, target) =>
    {
        var remaining = target - sw.Elapsed.TotalMilliseconds;
        if (remaining > 0)
        {
            await timer.WaitRelativeAsync(TimeSpan.FromMilliseconds(remaining));
        }
    });
}

static void Measure(string label, Func<int, Task> wait)
{
    // Warm up so JIT and thread-pool growth do not land in the samples.
    for (var i = 0; i < 5; i++)
    {
        wait(0).GetAwaiter().GetResult();
    }

    var samples = new List<double>(Iterations);
    var watch = new Stopwatch();
    for (var i = 0; i < Iterations; i++)
    {
        watch.Restart();
        wait(i).GetAwaiter().GetResult();
        samples.Add(watch.Elapsed.TotalMilliseconds);
        watch.Stop();
    }

    Report(label, samples);
}

/// <summary>Measures the error against an absolute deadline on a fixed grid.</summary>
static void MeasureDeadline(string label, int intervalMs, Func<Stopwatch, double, Task> wait)
{
    var errors = new List<double>();
    var grid = Stopwatch.StartNew();
    var target = 0.0;

    for (var i = 0; i < Iterations; i++)
    {
        target += intervalMs;
        wait(grid, target).GetAwaiter().GetResult();
        errors.Add(grid.Elapsed.TotalMilliseconds - target);
    }

    errors.Sort();
    var median = errors[errors.Count / 2];
    Console.WriteLine(
        $"  {label,-30} deadline error  median {median,7:N2} ms   p95 {errors[(int)(errors.Count * 0.95)],7:N2} ms   worst {errors[^1],7:N2} ms");
}

static void Report(string label, List<double> samples)
{
    var sorted = samples.Order().ToList();
    var median = sorted[sorted.Count / 2];
    var mean = samples.Average();
    Console.WriteLine(
        $"  {label,-30} median {median,7:N2} ms   mean {mean,7:N2} ms   min {sorted[0],7:N2} ms   max {sorted[^1],7:N2} ms");
}

/// <summary>
/// A waitable timer created with CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, which Windows 10
/// 1803+ honours without the caller having to raise the system timer resolution.
/// </summary>
internal sealed class HighResolutionTimer : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint INFINITE = 0xFFFFFFFF;

    private readonly IntPtr _handle;
    private NativeWaitHandle? _waitHandle;

    private HighResolutionTimer(IntPtr handle) => _handle = handle;

    /// <summary>Creates the timer, preferring the high-resolution variant.</summary>
    /// <exception cref="InvalidOperationException">The timer could not be created.</exception>
    public static HighResolutionTimer Create()
    {
        var handle = CreateWaitableTimerExW(
            IntPtr.Zero,
            IntPtr.Zero,
            CREATE_WAITABLE_TIMER_MANUAL_RESET | CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_ALL_ACCESS);

        if (handle == IntPtr.Zero)
        {
            // Pre-1803 Windows: fall back to the ordinary timer and accept the tick.
            handle = CreateWaitableTimerExW(
                IntPtr.Zero,
                IntPtr.Zero,
                CREATE_WAITABLE_TIMER_MANUAL_RESET,
                TIMER_ALL_ACCESS);
        }

        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWaitableTimerEx failed: {Marshal.GetLastWin32Error()}");
        }

        return new HighResolutionTimer(handle);
    }

    /// <summary>Blocks until <paramref name="delay"/> has elapsed.</summary>
    public void WaitRelative(TimeSpan delay)
    {
        // SetWaitableTimer takes a relative due time as a negative count of 100 ns units.
        var dueTime = -(long)(delay.TotalMilliseconds * 10_000);
        if (!SetWaitableTimer(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            throw new InvalidOperationException($"SetWaitableTimer failed: {Marshal.GetLastWin32Error()}");
        }

        WaitForSingleObject(_handle, INFINITE);
    }

    /// <summary>
    /// Awaits <paramref name="delay"/> without occupying a thread for its whole duration.
    /// </summary>
    /// <remarks>
    /// The completion is delivered by a thread-pool wait thread rather than the timer queue,
    /// so it must be checked that the thread-pool hop does not reintroduce the 15.625 ms
    /// quantisation this is meant to avoid — which is exactly what the measurement above
    /// reports.
    /// </remarks>
    public Task WaitRelativeAsync(TimeSpan delay)
    {
        var dueTime = -(long)(delay.TotalMilliseconds * 10_000);
        if (!SetWaitableTimer(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            throw new InvalidOperationException($"SetWaitableTimer failed: {Marshal.GetLastWin32Error()}");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitHandle ??= new NativeWaitHandle(_handle);

        var registration = ThreadPool.RegisterWaitForSingleObject(
            _waitHandle,
            (state, timedOut) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.Infinite,
            executeOnlyOnce: true);

        return AwaitAndReleaseAsync(completion.Task, registration);
    }

    /// <summary>
    /// Presents a raw kernel handle as a <see cref="WaitHandle"/> without taking ownership of
    /// it, so the timer's own <c>Dispose</c> remains the only thing that closes it.
    /// </summary>
    private sealed class NativeWaitHandle : WaitHandle
    {
        internal NativeWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }

    private static async Task AwaitAndReleaseAsync(Task completion, RegisteredWaitHandle registration)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    public void Dispose() => CloseHandle(_handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
