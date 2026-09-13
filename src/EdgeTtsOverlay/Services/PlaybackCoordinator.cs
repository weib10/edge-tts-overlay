using System.Collections.ObjectModel;
using EdgeTtsOverlay.Models;
using NAudio.Wave;

namespace EdgeTtsOverlay.Services;

public interface IAudioOutput : IDisposable
{
    Task PlayAsync(string path, bool startPaused, CancellationToken cancellationToken);
    void Pause();
    void Resume();
    void Stop();
}

public sealed class NAudioOutput : IAudioOutput
{
    private readonly WaveOutEvent _output = new();
    private BufferedWaveProvider? _buffer;
    private bool _initialized;

    public async Task PlayAsync(string path, bool startPaused, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var mp3 = new Mp3FileReader(path);
        using var pcm = WaveFormatConversionStream.CreatePcmStream(mp3);
        if (!_initialized)
        {
            _buffer = new BufferedWaveProvider(pcm.WaveFormat) { DiscardOnBufferOverflow = false, BufferDuration = TimeSpan.FromSeconds(45), ReadFully = true };
            _output.Init(_buffer); _initialized = true;
        }
        if (_buffer!.WaveFormat.SampleRate != pcm.WaveFormat.SampleRate || _buffer.WaveFormat.Channels != pcm.WaveFormat.Channels)
            throw new InvalidOperationException("語音片段音訊格式不一致。");
        if (startPaused) _output.Pause(); else _output.Play();
        var bytes = new byte[16 * 1024];
        int read;
        while ((read = pcm.Read(bytes, 0, bytes.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (_buffer.BufferLength - _buffer.BufferedBytes < read) { cancellationToken.ThrowIfCancellationRequested(); await Task.Delay(15, cancellationToken); }
            _buffer.AddSamples(bytes, 0, read);
        }
        while (_buffer.BufferedBytes > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(15, cancellationToken);
        }
    }
    public void Pause() => _output.Pause();
    public void Resume() => _output.Play();
    public void Stop() { _output.Stop(); _buffer?.ClearBuffer(); }
    public void Dispose() => _output.Dispose();
}

public sealed class PlaybackCoordinator : IDisposable
{
    private readonly ITtsSynthesizer _client;
    private readonly Func<AppSettings> _settings;
    private readonly IAudioOutput _audio;
    public const int MaxQueuedItems = 200;
    private readonly SemaphoreSlim _synthesisSlots = new(2);
    private readonly SemaphoreSlim _commands = new(1);
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _segmentCts;
    private Task? _runTask;
    private TaskCompletionSource _resume = CompletedGate();
    private bool _skipRequested;
    private sealed record Prefetch(CancellationTokenSource Cancellation, Task<string> Task);
    public ObservableCollection<ReadingItem> Queue { get; } = [];
    public ReadingItem? Current { get; private set; }
    public string CurrentSentence { get; private set; } = "等待文字";
    public bool IsPaused { get; private set; }
    public string DisplayStatus
    {
        get
        {
            if (IsPaused) return "已暫停";
            if (Current is not null && Current.State != "已完成") return Current.State;
            var completed = Queue.Count(item => item.State == "已完成");
            if (completed == 1 && completed == Queue.Count) return "已完成";
            if (completed > 0 && completed == Queue.Count) return $"就緒 · {completed} 項已完成";
            return Queue.Any(item => item.State is "等待中" or "已停止") ? "佇列待播" : "就緒";
        }
    }
    public event Action? Changed;

    public PlaybackCoordinator(ITtsSynthesizer client, Func<AppSettings> settings, IAudioOutput? audio = null)
    { _client = client; _settings = settings; _audio = audio ?? new NAudioOutput(); }

    public async Task ReplaceAsync(ReadingItem item)
    {
        await _commands.WaitAsync(); try { await StopRunAsync(false); Queue.Clear(); Queue.Add(item); StartRun(item); } finally { _commands.Release(); }
    }
    public void Append(ReadingItem item) { Queue.Add(item); TrimFinished(); Changed?.Invoke(); if (_runTask is null) StartRun(); }
    public async Task PlayAsync(ReadingItem item) { await _commands.WaitAsync(); try { await StopRunAsync(false); item.CurrentSegment = 0; item.State = "等待中"; StartRun(item); } finally { _commands.Release(); } }
    public async Task RetryAsync() { await _commands.WaitAsync(); try { if (Current is null) return; await StopRunAsync(false); StartRun(Current); } finally { _commands.Release(); } }
    public async Task StopAsync() { await _commands.WaitAsync(); try { await StopRunAsync(true); } finally { _commands.Release(); } }
    public async Task ClearAsync() { await _commands.WaitAsync(); try { await StopRunAsync(false); Queue.Clear(); Current = null; Changed?.Invoke(); } finally { _commands.Release(); } }
    public async Task RemoveAsync(ReadingItem item) { await _commands.WaitAsync(); try { if (item == Current) { await StopRunAsync(false); Current = null; } Queue.Remove(item); Changed?.Invoke(); } finally { _commands.Release(); } }
    public void Skip()
    {
        if (_runTask is null && Current is { } failed && failed.State.StartsWith("失敗")) { failed.CurrentSegment = Math.Min(failed.CurrentSegment + 1, failed.Segments.Count); failed.State = "等待中"; StartRun(failed); return; }
        _skipRequested = true;
        try { _segmentCts?.Cancel(); } catch (ObjectDisposedException) { /* segment already moved on */ }
        _audio.Stop();
    }

    public void TogglePause()
    {
        if (_runTask is null)
        {
            if (Current is not null) { Current.CurrentSegment = 0; Current.State = "等待中"; StartRun(Current); }
            return;
        }
        if (IsPaused) { IsPaused = false; _resume.TrySetResult(); _audio.Resume(); }
        else { IsPaused = true; _resume = new(TaskCreationOptions.RunContinuationsAsynchronously); _audio.Pause(); }
        Changed?.Invoke();
    }

    private void StartRun(ReadingItem? preferred = null)
    {
        if (_runTask is not null) return;
        var cts = new CancellationTokenSource(); _runCts = cts;
        _runTask = RunAsync(preferred, cts);
    }

    private async Task RunAsync(ReadingItem? preferred, CancellationTokenSource owner)
    {
        var token = owner.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Current = preferred ?? Queue.FirstOrDefault(x => x.State is "等待中" or "已停止"); preferred = null;
                if (Current is null) break;
                Current.State = "準備中"; Changed?.Invoke();
                await PlayItemAsync(Current, token);
                if (!token.IsCancellationRequested && Current.CurrentSegment >= Current.Segments.Count)
                { Current.CurrentSegment = 0; Current.State = "已完成"; Changed?.Invoke(); }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* PlayItemAsync already exposes the actionable error on the item. */ }
        finally
        {
            if (ReferenceEquals(_runCts, owner)) { _runCts = null; _runTask = null; Changed?.Invoke(); }
            owner.Dispose();
        }
    }

    private async Task PlayItemAsync(ReadingItem item, CancellationToken runToken)
    {
        var pending = new Dictionary<int, Prefetch>();
        var nextSchedule = item.CurrentSegment;
        try
        {
            for (var index = item.CurrentSegment; index < item.Segments.Count; index++)
            {
                while (pending.Count < 3 && nextSchedule < item.Segments.Count)
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                    pending[nextSchedule] = new(cts, SynthesizeLimitedAsync(item.Segments[nextSchedule], cts.Token));
                    nextSchedule++;
                }
                var currentPrefetch = pending[index];
                // A linked source registers a callback on runToken; replacing it without
                // disposing would pile up one registration per segment for the whole run.
                var previousSegmentCts = _segmentCts;
                _segmentCts = CancellationTokenSource.CreateLinkedTokenSource(runToken, currentPrefetch.Cancellation.Token);
                previousSegmentCts?.Dispose();
                string path;
                try { path = await currentPrefetch.Task.WaitAsync(_segmentCts.Token); pending.Remove(index); currentPrefetch.Cancellation.Dispose(); }
                catch (OperationCanceledException) when (_skipRequested && !runToken.IsCancellationRequested)
                {
                    _skipRequested = false; pending.Remove(index); currentPrefetch.Cancellation.Cancel();
                    try { TryDelete(await currentPrefetch.Task); } catch { } currentPrefetch.Cancellation.Dispose();
                    item.CurrentSegment = index + 1; continue;
                }
                try
                {
                    await _resume.Task.WaitAsync(runToken);
                    CurrentSentence = item.Segments[index]; item.State = $"朗讀 {index + 1}/{item.Segments.Count}"; Changed?.Invoke();
                    try { await _audio.PlayAsync(path, IsPaused, _segmentCts.Token); }
                    catch (OperationCanceledException) when (_skipRequested && !runToken.IsCancellationRequested) { _skipRequested = false; }
                }
                finally { TryDelete(path); }
                item.CurrentSegment = index + 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { item.State = $"失敗：{ex.Message}"; Changed?.Invoke(); throw; }
        finally
        {
            _segmentCts?.Dispose(); _segmentCts = null;
            foreach (var entry in pending.Values) entry.Cancellation.Cancel();
            foreach (var entry in pending.Values)
            {
                try { TryDelete(await entry.Task); } catch { }
                entry.Cancellation.Dispose();
            }
        }
    }

    private async Task<string> SynthesizeLimitedAsync(string text, CancellationToken token)
    {
        await _synthesisSlots.WaitAsync(token);
        try { return await _client.SynthesizeAsync(text, _settings(), token); }
        finally { _synthesisSlots.Release(); }
    }

    private async Task StopRunAsync(bool resetCurrent)
    {
        var cts = _runCts; var task = _runTask; var current = Current;
        if (cts is null || task is null) { if (resetCurrent && current is not null) current.CurrentSegment = 0; return; }
        cts.Cancel(); _audio.Stop(); _resume.TrySetResult();
        try { await task; } catch (OperationCanceledException) { }
        if (resetCurrent && current is not null) { current.CurrentSegment = 0; current.State = "已停止"; }
        IsPaused = false; Changed?.Invoke();
    }

    /// <summary>每個 ReadingItem 帶著原文與分段副本，長期 append 會無界成長；只丟已完成的，使用者排進來還沒讀的都留著。</summary>
    private void TrimFinished()
    {
        while (Queue.Count > MaxQueuedItems)
        {
            var oldest = Queue.FirstOrDefault(x => x.State == "已完成" && !ReferenceEquals(x, Current));
            if (oldest is null) return;
            Queue.Remove(oldest);
        }
    }

    private static TaskCompletionSource CompletedGate() { var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); tcs.SetResult(); return tcs; }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    public void Dispose() { _runCts?.Cancel(); _audio.Stop(); _audio.Dispose(); _runCts?.Dispose(); _synthesisSlots.Dispose(); _commands.Dispose(); }
}
