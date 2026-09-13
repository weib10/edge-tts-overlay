using EdgeTtsOverlay.Models;
using EdgeTtsOverlay.Services;
using NAudio.Wave;
using System.Diagnostics;
using System.Net;

if (args is ["--stress", var stressScenario, var stressIterStr])
{
    if (!int.TryParse(stressIterStr, out var stressIterations) || stressIterations <= 0)
    { Console.Error.WriteLine("iterations 必須是正整數"); return 2; }
    return await RunStressAsync(stressScenario, stressIterations);
}
if (args is ["--decode", var media])
{
    using var reader = new Mp3FileReader(media); using var pcm = WaveFormatConversionStream.CreatePcmStream(reader);
    var buffer = new byte[8192]; long bytes = 0; int read; while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0) bytes += read;
    Console.WriteLine($"DECODE PASS pcm_bytes={bytes} format={pcm.WaveFormat}"); return bytes > 0 ? 0 : 1;
}
if (args is ["--audio-restart", var audioMedia])
{
    using var output = new NAudioOutput(); using var first = new CancellationTokenSource();
    var interrupted = output.PlayAsync(audioMedia, false, first.Token); await Task.Delay(80); first.Cancel(); output.Stop(); try { await interrupted; } catch (OperationCanceledException) { }
    await output.PlayAsync(audioMedia, false, CancellationToken.None); Console.WriteLine("AUDIO RESTART PASS"); return 0;
}

var tests = new (string, Func<Task>)[] {
    ("text mixed terms", TestText), ("sentence and path boundaries", TestSegments),
    ("hard cap", TestHardCap), ("playback order and once", TestPlayback), ("replace cancels old", TestReplace),
    ("pause while buffering", TestPauseBuffering), ("skip while synthesizing", TestSkipSynth), ("prefetch max two", TestConcurrency),
    ("rapid triple replace", TestRapidReplace), ("failure retry", TestFailureRetry), ("failure skip", TestFailureSkip),
    ("client retries local busy", TestClientBusyRetry), ("client does not retry untagged 429", TestClientUntagged429),
    ("voice status degrades when list fails", TestVoiceStatusDegrades), ("voice status reports voice presence", TestVoiceStatusPresence),
    ("queue drops only finished items", TestQueueBound), ("long item survives skip storm", TestSkipStorm),
    ("playback display status", TestDisplayStatus)
};
var failed = 0;
foreach (var (name, test) in tests) { try { await test(); Console.WriteLine($"PASS {name}"); } catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); } }
return failed;

static Task TestText()
{
    var p = AppSettings.DefaultPronunciations();
    var value = TextProcessor.Prepare("這是一段中文 mixed with English API、JSON、Python 3.12。organizerJSON 保持原樣。", true, p);
    Assert(value.Contains("A P I") && value.Contains("JSON") && !value.Contains("J SON"), value);
    Assert(value.Contains("organizerJSON"), value);
    var migrated = AppSettings.NormalizePronunciations(new Dictionary<string,string>{{"JSON", "J SON"},{"custom","自訂"}});
    Assert(migrated["JSON"] == "JSON" && migrated["custom"] == "自訂", "legacy JSON migration failed");
    var markdown = TextProcessor.Prepare("[文件](https://example.com) `term`\n```cs\nboom();\n```", true, p);
    Assert(markdown.Contains("文件") && !markdown.Contains("https://") && markdown.Contains("程式碼區塊略過"), markdown);
    return Task.CompletedTask;
}
static Task TestSegments()
{
    var input = string.Join(' ', Enumerable.Repeat("One sentence. Another sentence.", 8)) + " 更新 src/app.py 後執行 npm run build。 Dr. Smith uses e.g. HTTP/2 and v1.2.3. Next sentence.";
    var parts = TextSegmenter.Split(input);
    Assert(parts.Count > 2, string.Join(" | ", parts));
    Assert(parts.Any(x => x.Contains("src/app.py")), "path lost");
    Assert(parts.Any(x => x.Contains("Dr. Smith uses e.g. HTTP/2 and v1.2.3.")), string.Join(" | ", parts));
    return Task.CompletedTask;
}
static Task TestHardCap()
{
    var parts = TextSegmenter.Split(new string('測', 500));
    Assert(parts.Count > 1 && parts.All(x => x.Length <= 120), string.Join(",", parts.Select(x => x.Length)));
    Assert(string.Concat(parts) == new string('測', 500), "content mismatch"); return Task.CompletedTask;
}
static async Task TestPlayback()
{
    var synth = new FakeSynth(); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio);
    var item = Item("a", "one", "two", "three"); await p.ReplaceAsync(item); await Wait(() => item.State == "已完成");
    Assert(audio.Played.SequenceEqual(new[] { "one", "two", "three" }), string.Join(',', audio.Played));
    await Task.Delay(100); Assert(audio.Played.Count == 3, "completed item replayed"); p.Dispose();
}
static async Task TestReplace()
{
    var synth = new FakeSynth(100); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio);
    await p.ReplaceAsync(Item("old", "old1", "old2")); await Task.Delay(20); await p.ReplaceAsync(Item("new", "new1")); await Wait(() => audio.Played.Contains("new1"));
    Assert(!audio.Played.Contains("old2"), "late old audio played"); p.Dispose();
}
static async Task TestPauseBuffering()
{
    var synth = new FakeSynth(80); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio);
    await p.ReplaceAsync(Item("pause", "held")); p.TogglePause(); await Task.Delay(140);
    Assert(audio.Played.Count == 0, "audio started while paused"); Assert(p.DisplayStatus == "已暫停", p.DisplayStatus); p.TogglePause(); await Wait(() => audio.Played.Count == 1); p.Dispose();
}
static async Task TestDisplayStatus()
{
    var p = new PlaybackCoordinator(new FakeSynth(), () => new(), new FakeAudio());
    var item = Item("done", "one"); await p.ReplaceAsync(item); await Wait(() => item.State == "已完成");
    Assert(p.DisplayStatus == "已完成", p.DisplayStatus); p.Dispose();
}
static async Task TestSkipSynth()
{
    var synth = new FakeSynth(120); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio);
    await p.ReplaceAsync(Item("skip", "first", "second", "third")); await Task.Delay(20); p.Skip(); await Wait(() => audio.Played.Contains("second"));
    Assert(!audio.Played.Contains("first") && audio.Played.Contains("second"), string.Join(',', audio.Played)); p.Dispose();
}
static async Task TestConcurrency()
{
    var synth = new FakeSynth(80); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio);
    await p.ReplaceAsync(Item("parallel", "1", "2", "3", "4")); await Wait(() => audio.Played.Count == 4);
    Assert(synth.MaxActive == 2, $"max={synth.MaxActive}"); p.Dispose();
}
static async Task TestRapidReplace()
{
    var synth = new FakeSynth(90); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio);
    var a = p.ReplaceAsync(Item("a", "a1", "a2")); var b = p.ReplaceAsync(Item("b", "b1")); var c = p.ReplaceAsync(Item("c", "c1"));
    await Task.WhenAll(a,b,c); await Wait(() => audio.Played.Contains("c1"));
    Assert(!audio.Played.Any(x => x.StartsWith('a') || x.StartsWith('b')), string.Join(',', audio.Played)); p.Dispose();
}
static async Task TestFailureRetry()
{
    var synth = new FlakySynth(); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio); var item = Item("retry", "retry-me");
    await p.ReplaceAsync(item); await Wait(() => item.State.StartsWith("失敗")); await p.RetryAsync(); await Wait(() => item.State == "已完成");
    Assert(audio.Played.SequenceEqual(new[]{"retry-me"}), string.Join(',', audio.Played)); p.Dispose();
}
static async Task TestFailureSkip()
{
    var synth = new FailTextSynth("bad"); var audio = new FakeAudio(); var p = new PlaybackCoordinator(synth, () => new(), audio); var item=Item("skip failure","bad","good");
    await p.ReplaceAsync(item); await Wait(() => item.State.StartsWith("失敗")); p.Skip(); await Wait(() => item.State=="已完成");
    Assert(audio.Played.SequenceEqual(new[]{"good"}), string.Join(',',audio.Played)); p.Dispose();
}
static async Task TestClientBusyRetry()
{
    var busy1 = new HttpResponseMessage(HttpStatusCode.TooManyRequests); busy1.Headers.Add("X-Edge-Tts-Error","busy");
    var busy2 = new HttpResponseMessage(HttpStatusCode.TooManyRequests); busy2.Headers.Add("X-Edge-Tts-Error","busy");
    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("ID3-audio"u8.ToArray()) };
    var handler = new SequenceHandler(busy1,busy2,ok); using var http = new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
    var path = await new TtsClient(http).SynthesizeAsync("test",new(),CancellationToken.None); try { Assert(handler.Calls==3,$"calls={handler.Calls}"); } finally { File.Delete(path); }
}
static async Task TestClientUntagged429()
{
    var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("upstream")}); using var http = new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
    try { await new TtsClient(http).SynthesizeAsync("test",new(),CancellationToken.None); throw new Exception("expected failure"); }
    catch(HttpRequestException ex) { Assert(handler.Calls==1,$"calls={handler.Calls}"); Assert(ex.Message.Contains("佇列忙碌"),ex.Message); }
}
static async Task TestVoiceStatusDegrades()
{
    var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.BadGateway){Content=new StringContent("{\"detail\":\"無法取得 Microsoft 語音清單\"}")});
    using var http = new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
    var status = await new TtsClient(http).DescribeVoiceStatusAsync("zh-TW-HsiaoChenNeural");
    Assert(status == TtsClient.VoiceListUnavailable, status);
    Assert(!status.Contains("502") && !status.Contains("Bad Gateway"), status);
}
static async Task TestVoiceStatusPresence()
{
    const string listJson = "[{\"id\":\"zh-TW-HsiaoChenNeural\",\"locale\":\"zh-TW\",\"name\":\"HsiaoChen\",\"gender\":\"Female\"}]";
    static HttpResponseMessage Ok() => new(HttpStatusCode.OK){Content=new StringContent(listJson, System.Text.Encoding.UTF8, "application/json")};
    var handler = new SequenceHandler(Ok(), Ok()); using var http = new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
    var client = new TtsClient(http);
    var ready = await client.DescribeVoiceStatusAsync("zh-TW-HsiaoChenNeural");
    Assert(ready == "就緒", ready);
    var missing = await client.DescribeVoiceStatusAsync("zz-XX-Missing");
    Assert(missing.Contains("預設聲音不存在"), missing);
}
static Task TestQueueBound()
{
    var p = new PlaybackCoordinator(new FakeSynth(), () => new(), new FakeAudio());
    for (var i = 0; i < PlaybackCoordinator.MaxQueuedItems; i++)
    { var done = Item($"done{i}", "seg"); done.State = "已完成"; p.Queue.Add(done); }
    var unread = Item("unread", "seg"); unread.State = "等待中"; p.Queue.Add(unread);
    p.Append(Item("fresh", "seg"));
    try
    {
        Assert(p.Queue.Count == PlaybackCoordinator.MaxQueuedItems, $"count={p.Queue.Count}");
        Assert(p.Queue.Contains(unread), "dropped an item the user has not heard yet");
        Assert(!p.Queue.Any(x => x.Title == "done0"), "oldest finished item survived the trim");
    }
    finally { p.Dispose(); }
    return Task.CompletedTask;
}
static async Task TestSkipStorm()
{
    var segments = Enumerable.Range(0, 40).Select(i => $"seg{i}").ToArray();
    var p = new PlaybackCoordinator(new FakeSynth(), () => new(), new FakeAudio());
    var item = Item("long", segments);
    await p.ReplaceAsync(item);
    // Each segment disposes the previous linked CTS; Skip must tolerate racing that.
    for (var i = 0; i < 25; i++) { p.Skip(); await Task.Delay(3); }
    await Wait(() => item.State == "已完成");
    p.Dispose();
}
static ReadingItem Item(string title, params string[] segments) => new() { Title = title, OriginalText = string.Join(' ', segments), Segments = segments };
static async Task Wait(Func<bool> condition) { for (var i=0;i<100 && !condition();i++) await Task.Delay(20); Assert(condition(), "timeout"); }
static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }

// ---------------------------------------------------------------------------
// Memory / handle stress harness for PlaybackCoordinator (--stress <scenario> <iterations>).
// Runs a warm-up pass (discarded), measures a baseline, runs the real pass, measures again,
// and prints the delta. All scenarios use fakes only — never touches the network or real audio.
// ---------------------------------------------------------------------------

static (long Heap, int Handles, long PrivateBytes) MeasureStress()
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    using var proc = Process.GetCurrentProcess();
    return (GC.GetTotalMemory(true), proc.HandleCount, proc.PrivateMemorySize64);
}

static async Task RunStressWithTimeoutAsync(Func<Task> action, TimeSpan timeout, string label)
{
    var task = action();
    var winner = await Task.WhenAny(task, Task.Delay(timeout));
    if (!ReferenceEquals(winner, task)) throw new TimeoutException($"{label} 逾時（疑似死結），已等待 {timeout.TotalSeconds:0}s");
    await task;
}

static async Task<int> RunStressAsync(string scenario, int iterations)
{
    Func<int, Task> run = scenario switch
    {
        "long-article" => StressLongArticleAsync,
        "skip-storm" => StressSkipStormAsync,
        "pause-resume" => StressPauseResumeAsync,
        "replace-storm" => StressReplaceStormAsync,
        "append-flood" => StressAppendFloodAsync,
        "fuzz" => StressFuzzAsync,
        _ => null!,
    };
    if (run is null)
    {
        Console.Error.WriteLine($"未知情境：{scenario}（可用：long-article, skip-storm, pause-resume, replace-storm, append-flood, fuzz）");
        return 2;
    }

    try
    {
        Console.WriteLine($"STRESS {scenario} warmup start (iterations={Math.Max(5, iterations / 20)})");
        await RunStressWithTimeoutAsync(() => run(Math.Max(5, iterations / 20)), TimeSpan.FromMinutes(2), $"{scenario} warmup");
        Console.WriteLine($"STRESS {scenario} warmup done");

        var (baseHeap, baseHandles, basePrivate) = MeasureStress();
        Console.WriteLine($"STRESS {scenario} baseline heap={baseHeap} handles={baseHandles} private={basePrivate}");

        var sw = Stopwatch.StartNew();
        await RunStressWithTimeoutAsync(() => run(iterations), TimeSpan.FromMinutes(10), scenario);
        sw.Stop();

        var (endHeap, endHandles, endPrivate) = MeasureStress();
        Console.WriteLine($"STRESS {scenario} final iterations={iterations} heap={endHeap} handles={endHandles} private={endPrivate} elapsed_ms={sw.ElapsedMilliseconds}");
        Console.WriteLine($"STRESS {scenario} delta heap={endHeap - baseHeap} handles={endHandles - baseHandles} private={endPrivate - basePrivate}");
        Console.WriteLine($"STRESS {scenario} RESULT PASS");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"STRESS {scenario} RESULT FAIL: {ex}");
        return 1;
    }
}

static async Task StressLongArticleAsync(int segmentCount)
{
    segmentCount = Math.Max(10, segmentCount);
    const int repeats = 3;
    var synth = new StressSynth(2);
    var audio = new StressAudio(1);
    var p = new PlaybackCoordinator(synth, () => new(), audio);
    try
    {
        for (var run = 0; run < repeats; run++)
        {
            var segments = Enumerable.Range(0, segmentCount).Select(i => $"r{run}-seg{i}").ToArray();
            var item = new ReadingItem { Title = $"long-{run}", OriginalText = string.Join(' ', segments), Segments = segments };
            var checkpoint = Math.Max(1, segmentCount / 8);
            var playedBefore = audio.Played;
            var playTask = p.ReplaceAsync(item);
            var lastSampledAt = -1;
            var samples = new List<(int Seg, long Heap, int Handles)>();
            var watchdog = Stopwatch.StartNew();
            while (item.State != "已完成")
            {
                if (watchdog.Elapsed > TimeSpan.FromMinutes(3))
                    throw new Exception($"run={run} 播放逾時：state={item.State} seg={item.CurrentSegment}/{segmentCount}");
                if (item.CurrentSegment - lastSampledAt >= checkpoint)
                {
                    lastSampledAt = item.CurrentSegment;
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    using var proc = Process.GetCurrentProcess();
                    samples.Add((item.CurrentSegment, GC.GetTotalMemory(true), proc.HandleCount));
                }
                await Task.Delay(10);
            }
            await playTask;
            if (run == 0)
                foreach (var s in samples)
                    Console.WriteLine($"STRESS long-article progress seg={s.Seg}/{segmentCount} heap={s.Heap} handles={s.Handles}");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var proc2 = Process.GetCurrentProcess();
            Console.WriteLine($"STRESS long-article run={run} done heap={GC.GetTotalMemory(true)} handles={proc2.HandleCount}");
            var playedThisRun = audio.Played - playedBefore;
            if (playedThisRun != segmentCount)
                throw new Exception($"run={run} 播放片段數不對：played={playedThisRun}/{segmentCount}（State 已回報完成，但片段數對不上）");
        }
    }
    finally { p.Dispose(); }
}

static async Task StressSkipStormAsync(int skipCount)
{
    var segmentCount = Math.Clamp(skipCount, 50, 4000);
    var synth = new StressSynth(3);
    var audio = new StressAudio(1);
    var p = new PlaybackCoordinator(synth, () => new(), audio);
    try
    {
        var segments = Enumerable.Range(0, segmentCount).Select(i => $"seg{i}").ToArray();
        var item = new ReadingItem { Title = "skip-storm", OriginalText = string.Join(' ', segments), Segments = segments };
        await p.ReplaceAsync(item);
        // Fire Skip() back-to-back with no delay to maximize the race between the UI-thread
        // Cancel() and the playback-thread Dispose() of the same segment CTS (see PlaybackCoordinator.Skip).
        for (var i = 0; i < skipCount; i++)
        {
            p.Skip();
            if (i % 4 == 0) await Task.Yield();
        }
        var sw = Stopwatch.StartNew();
        while (item.State != "已完成" && sw.Elapsed < TimeSpan.FromMinutes(3)) await Task.Delay(15);
        if (item.State != "已完成")
            throw new Exception($"skip storm 後未完成：state={item.State}, seg={item.CurrentSegment}/{segmentCount}");
    }
    finally { p.Dispose(); }
}

static async Task StressPauseResumeAsync(int toggles)
{
    var segmentCount = Math.Clamp(toggles, 30, 1000);
    var synth = new StressSynth(4);
    var audio = new StressAudio(2);
    var p = new PlaybackCoordinator(synth, () => new(), audio);
    try
    {
        var segments = Enumerable.Range(0, segmentCount).Select(i => $"seg{i}").ToArray();
        var item = new ReadingItem { Title = "pause-resume", OriginalText = string.Join(' ', segments), Segments = segments };
        await p.ReplaceAsync(item);
        var rng = new Random(42);
        for (var i = 0; i < toggles; i++)
        {
            p.TogglePause();
            await Task.Delay(rng.Next(0, 3));
        }
        if (p.IsPaused) p.TogglePause(); // leave resumed so it can actually finish
        var sw = Stopwatch.StartNew();
        while (item.State != "已完成" && sw.Elapsed < TimeSpan.FromMinutes(3)) await Task.Delay(15);
        if (item.State != "已完成")
            throw new Exception($"pause/resume 後未完成：state={item.State}, seg={item.CurrentSegment}/{segmentCount}, paused={p.IsPaused}");
    }
    finally { p.Dispose(); }
}

static async Task StressReplaceStormAsync(int replaces)
{
    var synth = new StressSynth(6); // slow enough that most replaces interrupt in-flight synthesis
    var audio = new StressAudio(1);
    var p = new PlaybackCoordinator(synth, () => new(), audio);
    try
    {
        var items = new ReadingItem[replaces];
        var tasks = new Task[replaces];
        for (var i = 0; i < replaces; i++)
        {
            var segments = new[] { $"r{i}-a", $"r{i}-b", $"r{i}-c" };
            items[i] = new ReadingItem { Title = $"replace-{i}", OriginalText = string.Join(' ', segments), Segments = segments };
            tasks[i] = p.ReplaceAsync(items[i]); // fired without awaiting individually: _commands serializes them
        }
        await Task.WhenAll(tasks);
        var last = items[^1];
        var sw = Stopwatch.StartNew();
        while (last.State != "已完成" && sw.Elapsed < TimeSpan.FromMinutes(3)) await Task.Delay(15);
        if (last.State != "已完成") throw new Exception($"最後一次 replace 未完成：state={last.State}");
        if (p.Queue.Count != 1) throw new Exception($"replace 後 Queue 應只剩最後一項，實際 count={p.Queue.Count}");
    }
    finally { p.Dispose(); }
}

static async Task StressAppendFloodAsync(int count)
{
    // Phase A: flooding with already-finished items must stay bounded near MaxQueuedItems.
    {
        var p = new PlaybackCoordinator(new StressSynth(1), () => new(), new StressAudio());
        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = new ReadingItem { Title = $"done{i}", OriginalText = "x", Segments = new[] { "x" } };
                item.State = "已完成";
                p.Append(item);
            }
            if (p.Queue.Count > PlaybackCoordinator.MaxQueuedItems)
                throw new Exception($"append-flood(finished) 未被裁剪：count={p.Queue.Count} > cap={PlaybackCoordinator.MaxQueuedItems}");
            Console.WriteLine($"STRESS append-flood finished-phase appended={count} final_queue_count={p.Queue.Count} cap={PlaybackCoordinator.MaxQueuedItems}");
        }
        finally { p.Dispose(); }
    }
    await Task.Yield();
    // Phase B: flooding with all-unread items is allowed to exceed the cap by design;
    // nothing may be silently dropped. The one item picked up for playback never finishes
    // (HangingSynth), so Append() calls stay cheap and we can flood freely.
    {
        var p = new PlaybackCoordinator(new HangingSynth(), () => new(), new StressAudio());
        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = new ReadingItem { Title = $"unread{i}", OriginalText = "x", Segments = new[] { "x" } };
                p.Append(item);
            }
            if (p.Queue.Count != count)
                throw new Exception($"append-flood(unread) 掉了項目：appended={count}, queue_count={p.Queue.Count}");
            Console.WriteLine($"STRESS append-flood unread-phase appended={count} final_queue_count={p.Queue.Count}（預期無上限，全數保留是刻意行為）");
        }
        finally { p.Dispose(); }
    }
}

static async Task StressFuzzAsync(int ops)
{
    var synth = new StressSynth(2, failureRate: 0.05);
    var audio = new StressAudio(1);
    var p = new PlaybackCoordinator(synth, () => new(), audio);
    var rng = new Random(2024);
    try
    {
        ReadingItem MakeItem(int i)
        {
            var n = rng.Next(1, 12);
            var segments = Enumerable.Range(0, n).Select(j => $"f{i}-{j}").ToArray();
            return new ReadingItem { Title = $"fuzz-{i}", OriginalText = string.Join(' ', segments), Segments = segments };
        }
        await p.ReplaceAsync(MakeItem(-1));
        for (var i = 0; i < ops; i++)
        {
            switch (rng.Next(6))
            {
                case 0: p.Skip(); break;
                case 1: p.TogglePause(); break;
                case 2: await p.ReplaceAsync(MakeItem(i)); break;
                case 3: p.Append(MakeItem(i)); break;
                case 4: await p.RetryAsync(); break;
                case 5:
                    if (p.Queue.Count > 0) { var victim = p.Queue[rng.Next(p.Queue.Count)]; await p.RemoveAsync(victim); }
                    break;
            }
            if (i % 8 == 0) await Task.Delay(rng.Next(0, 4));
        }
        if (p.IsPaused) p.TogglePause();
        await p.StopAsync();
    }
    finally { p.Dispose(); }
}

sealed class FakeSynth(int delay = 1) : ITtsSynthesizer
{
    private int _active; public int MaxActive { get; private set; }
    public async Task<string> SynthesizeAsync(string text, AppSettings settings, CancellationToken token) { var active = Interlocked.Increment(ref _active); MaxActive = Math.Max(MaxActive, active); try { await Task.Delay(delay, token); var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".fake"); await File.WriteAllTextAsync(path, text, token); return path; } finally { Interlocked.Decrement(ref _active); } }
}
sealed class FakeAudio : IAudioOutput
{
    public List<string> Played { get; } = [];
    public async Task PlayAsync(string path, bool startPaused, CancellationToken token) { Played.Add(await File.ReadAllTextAsync(path, token)); await Task.Delay(5, token); }
    public void Pause() {} public void Resume() {} public void Stop() {} public void Dispose() {}
}
sealed class FlakySynth : ITtsSynthesizer
{
    private int _attempt;
    public async Task<string> SynthesizeAsync(string text, AppSettings settings, CancellationToken token) { await Task.Delay(5, token); if (Interlocked.Increment(ref _attempt) == 1) throw new IOException("temporary"); var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".fake"); await File.WriteAllTextAsync(path,text,token); return path; }
}
sealed class FailTextSynth(string bad) : ITtsSynthesizer
{
    public async Task<string> SynthesizeAsync(string text, AppSettings settings, CancellationToken token) { await Task.Delay(5,token); if(text==bad) throw new IOException("bad segment"); var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".fake"); await File.WriteAllTextAsync(path,text,token); return path; }
}
sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses); public int Calls {get;private set;}
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++; return Task.FromResult(_responses.Dequeue());}
}

sealed class StressSynth(int delayMs = 2, double failureRate = 0) : ITtsSynthesizer
{
    private readonly Random _rng = new(12345);
    public async Task<string> SynthesizeAsync(string text, AppSettings settings, CancellationToken token)
    {
        await Task.Delay(delayMs, token);
        if (failureRate > 0 && _rng.NextDouble() < failureRate) throw new IOException("stress-injected failure");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fake");
        await File.WriteAllTextAsync(path, text, token);
        return path;
    }
}
sealed class StressAudio(int delayMs = 1) : IAudioOutput
{
    private int _played;
    public int Played => _played;
    public async Task PlayAsync(string path, bool startPaused, CancellationToken token)
    {
        Interlocked.Increment(ref _played);
        await Task.Delay(delayMs, token);
    }
    public void Pause() {} public void Resume() {} public void Stop() {} public void Dispose() {}
}
sealed class HangingSynth : ITtsSynthesizer
{
    public async Task<string> SynthesizeAsync(string text, AppSettings settings, CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return "";
    }
}
