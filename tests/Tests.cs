using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GoldClip;
using LiveSplit.Model;
class Tests
{
    static void Assert(bool ok, string description) { if (!ok) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    static void Main() {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
            string path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("AUTO_CLIP_LIVESPLIT"), new AssemblyName(e.Name).Name + ".dll");
            return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Test().GetAwaiter().GetResult(); } catch (Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }
    }
    static Component TestComponent(LiveSplitState state)
    {
        var component = new Component(state);
        var opts = (Options)typeof(Component).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);
        opts.Folder = System.IO.Path.GetFullPath("work/GoldClip/test-clips");
        return component;
    }
    static void Drain(Component component)
    {
        using (var done = new ManualResetEventSlim())
        {
            typeof(Component).GetMethod("Post", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(component, new object[] { (Action)(() => done.Set()) });
            if (!done.Wait(5000)) throw new Exception("Background event dispatcher did not finish.");
        }
    }
    static async Task Test()
    {
        var options = new Options { Url = "ws://127.0.0.1:4456", Password = "test-password" };
        for (int n = 0; n < 2; n++) using (var obs = new Obs())
        {
            await obs.Connect(options);
            Assert((bool)(await obs.Request("GetReplayBufferStatus"))["outputActive"], "OBS authenticated status");
            Assert(await obs.Save(() => {}) == "fixture.mkv", "Save event before response");
        }
        Assert(Component.SeekStart(390, 100, 40, 3) == 327, "Wall time trimming offset");
        Assert(Component.SeekStart(10, 100, 40, 3) == 0, "Short buffer clamps start");
        Assert(Component.SplitOffset("[29.83] Maul") == 29.83, "Leading bracket offset is parsed in seconds");
        Assert(Component.SplitOffset("[60] Maul") == 60, "Whole-second offset");
        Assert(Component.SplitOffset("Maul [60]") == 60, "Offset tags work anywhere in a split name");
        Assert(Component.SplitOffset("[45.0] Split name here") == 45.0, "One decimal second offset");
        Assert(Component.SplitOffset("[4.312] Test") == 4.312, "Millisecond precision offset");
        Assert(Math.Abs(Component.SplitOffset("[10:03:41.111] Hours included") - 36221.111) < 0.000001, "Hour offset");
        Assert(Component.SplitOffset("[4:31] Minutes seconds") == 271, "Minute offset");
        Assert(Component.SplitOffset("One decimal place [15:11.1]") == 911.1, "Embedded minute offset with decimal");
        Assert(Component.SplitOffset("Two decimal [0:11.22] places") == 11.22, "Embedded hundredths offset");
        Assert(Component.SplitOffset("[131.34] Total seconds") == 131.34, "Total seconds may exceed sixty");
        Assert(Component.SplitOffset("[Boss] Maul") == 0 && Component.SplitOffset("[-2] Maul") == 0 && Component.SplitOffset("[1:60] Maul") == 0 && Component.SplitOffset("[1:60:00]") == 0 && Component.SplitOffset("[4.1234]") == 0, "Malformed offsets ignored");
        Assert(Component.SeekStart(390, 23, 0, 3) == 364, "Twenty seconds of footage keeps twenty seconds plus padding regardless of timer offsets");
        Assert(Component.GoldFileName("Maul", new DateTime(2026, 4, 15), TimeSpan.FromMilliseconds(221889)) == "Maul Gold 4-15-26 3\uA78941.88.mkv", "Gold filename uses split date and segment centiseconds");
        Assert(Component.FullRunFileName("Any%", new DateTime(2026, 4, 15), TimeSpan.FromMilliseconds(1541889)) == "Any% 4-15-26 25\uA78941.88.mkv", "Full-run filename uses category date and final time");
        Assert(Component.StripOffsets("[45.17] 5-1") == "5-1" && Component.StripOffsets("Level [4:31] Name") == "Level Name", "Offset tags removed from split filenames");
        Assert(Component.StripOffsets("[Boss] [1:60] Maul") == "[Boss] [1:60] Maul", "Non-offset bracket labels preserved");
        Assert(Component.GoldFileName("[45.17] 5-1", new DateTime(2026, 4, 15), TimeSpan.FromSeconds(20)).StartsWith("5-1 Gold "), "Gold filename excludes offset tag");
        Assert(Component.PickKeyframe(5, 7, new[] { 2.0, 6.0, 10.0 }) == 6, "Choose closer keyframe to reduce excess padding");
        Assert(Component.PickKeyframe(5, 5.5, new[] { 2.0, 6.0 }) == 2, "Never choose a keyframe after gameplay starts");
        Assert(Component.GoldFileName("A/B", new DateTime(2026, 4, 15), TimeSpan.FromHours(1)) == "A_B Gold 4-15-26 60\uA78900.00.mkv", "Filename sanitizes split names and supports long times");
        Assert(Component.CoversRun(390, 500, 120), "Full run fits replay");
        Assert(!Component.CoversRun(390, 500, 100), "Overlong full run is rejected");
        Assert(!Component.CoversRun(390, 500, 109), "Post-padding cannot push run start outside buffer");
        Assert(Tools.HasPair(Tools.FindFFmpeg()), "Auto-detect locates installed FFmpeg and FFprobe");
        string toolsDir = System.IO.Path.GetFullPath("work/GoldClip/detection-test");
        System.IO.Directory.CreateDirectory(toolsDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(toolsDir, "ffmpeg.exe"), "fixture");
        Assert(Tools.FindInFolders(new[] { toolsDir }) == null, "Auto-detect requires FFprobe alongside FFmpeg");
        System.IO.File.WriteAllText(System.IO.Path.Combine(toolsDir, "ffprobe.exe"), "fixture");
        Assert(Tools.FindInFolders(new[] { toolsDir }) == System.IO.Path.Combine(toolsDir, "ffmpeg.exe"), "Auto-detect selects complete tool pair");
        System.IO.File.Delete(System.IO.Path.Combine(toolsDir, "ffprobe.exe"));
        var run = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        run.AddSegment("One", bestSegmentTime: new Time(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)));
        run.AddSegment("Two", bestSegmentTime: new Time(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)));
        var state = new LiveSplitState(run, null, null, null, null);
        var model = new TimerModel { CurrentState = state };
        var component = TestComponent(state);
        var clipsField = typeof(Component).GetField("clips", BindingFlags.Instance | BindingFlags.NonPublic);
        var clips = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(component);
        model.Start(); Drain(component); Thread.Sleep(30); model.Split(); Drain(component);
        Assert(clips.ContainsKey(0), "Actual LiveSplit event detects gold");
        var first = clips[0]; model.UndoSplit(); Drain(component);
        Assert(first.Undone && !clips.ContainsKey(0), "Undo cancels pending clip");
        model.Split(); Drain(component); Assert(clips.ContainsKey(0) && clips[0].Id != first.Id, "Re-split has independent identity");
        model.UndoSplit(); Drain(component); model.SkipSplit(); Drain(component); model.Split(); Drain(component);
        Assert(!clips.ContainsKey(1), "Skipped split does not produce single-level gold");
        component.Dispose();
        var doc = new System.Xml.XmlDocument(); var settings = component.GetSettings(doc);
        var password = settings.SelectSingleNode("Password").InnerText;
        Assert(password == "", "Empty password settings round trip");
        component.SetSettings(settings);
        var run2 = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        run2.AddSegment("[60] Integration", bestSegmentTime: new Time(TimeSpan.FromSeconds(180), null));
        var state2 = new LiveSplitState(run2, null, null, null, null);
        var model2 = new TimerModel { CurrentState = state2 };
        var component2 = TestComponent(state2);
        options.Folder = System.IO.Path.GetFullPath("work/GoldClip/test-clips"); options.After = 0;
        typeof(Component).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(component2, options);
        var clips2 = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(component2);
        model2.Start(); Drain(component2);
        var captureStarts = (System.Collections.Generic.Dictionary<int, double>)typeof(Component).GetField("starts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component2);
        var captureClock = (System.Diagnostics.Stopwatch)typeof(Component).GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component2);
        captureStarts[0] = captureClock.Elapsed.TotalSeconds - 20;
        state2.AdjustedStartTime = TimeStamp.Now - TimeSpan.FromSeconds(80);
        model2.Split(); Drain(component2);
        var complete = clips2[0];
        Assert(Math.Abs(complete.SegmentTime.Value.TotalSeconds - 80) < 0.2, "Filename time includes sixty second offset while footage lasts twenty seconds");
        for (int n = 0; n < 150 && complete.File == null; n++) await Task.Delay(100);
        Assert(complete.File != null && System.IO.File.Exists(complete.File), "Full capture and FFmpeg trim creates clip");
        using (var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(options.FFmpeg), "ffprobe.exe"),
            "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"" + complete.File + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
        {
            double seconds = double.Parse(probe.StandardOutput.ReadToEnd().Trim(), System.Globalization.CultureInfo.InvariantCulture);
            probe.WaitForExit();
            Assert(seconds >= 19.5 && seconds <= 21, "Twenty second replay survives a sixty second synthetic offset");
        }
        Assert(System.IO.Directory.GetFiles(options.Folder, "raw-*").Length == 0, "Successful trim cleans owned raw replay");
        model2.UndoSplit(); Drain(component2);
        for (int n = 0; n < 100 && System.IO.File.Exists(complete.File); n++) await Task.Delay(100);
        Assert(!System.IO.File.Exists(complete.File), "Undo recycles completed video");
        component2.Dispose();
        var run3 = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        run3.GameName = "Do not include this game"; run3.CategoryName = "Any%";
        run3.AddSegment("Not a gold", bestSegmentTime: new Time(TimeSpan.FromMilliseconds(1), null));
        var state3 = new LiveSplitState(run3, null, null, null, null);
        var model3 = new TimerModel { CurrentState = state3 };
        var component3 = TestComponent(state3);
        var opts3 = (Options)typeof(Component).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component3);
        var clips3 = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(component3);
        model3.Start(); Drain(component3); Thread.Sleep(30); model3.Split(); Drain(component3);
        Assert(clips3.Count == 0, "Gold-only mode ignores slower split");
        model3.UndoSplit(); Drain(component3); opts3.CaptureMode = 1;
        model3.Split(); Drain(component3);
        Assert(clips3.Count == 1 && clips3.ContainsKey(-1), "Full-run mode does not make split clips");
        var whole3 = clips3[-1]; model3.UndoSplit(); Drain(component3);
        Assert(whole3.Undone && clips3.Count == 0, "Undo final split cancels full-run clip");
        model3.Split(); Drain(component3);
        Assert(clips3.Count == 1 && clips3[-1].IsFullRun, "Full-run only mode");
        var settings3 = component3.GetSettings(new System.Xml.XmlDocument());
        opts3.CaptureMode = 0; component3.SetSettings(settings3);
        Assert(opts3.CaptureMode == 1, "Capture mode persists through settings round trip");
        model3.UndoSplit(); Drain(component3); component3.Dispose();
        var state4 = new LiveSplitState(run3, null, null, null, null);
        var model4 = new TimerModel { CurrentState = state4 };
        var component4 = TestComponent(state4);
        var opts4 = options.Copy(); opts4.CaptureMode = 1; opts4.After = 0;
        typeof(Component).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(component4, opts4);
        var clips4 = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(component4);
        model4.Start(); Drain(component4); Thread.Sleep(30); model4.Split(); Drain(component4);
        var full4 = clips4[-1];
        Assert(full4.Name == "Any%" && full4.SegmentTime == state4.CurrentTime[state4.CurrentTimingMethod], "Full-run metadata snapshots category and final timer value");
        for (int n = 0; n < 150 && full4.File == null; n++) await Task.Delay(100);
        Assert(full4.File != null && System.IO.File.Exists(full4.File), "Full-run capture creates real trimmed video");
        Assert(System.IO.Path.GetFileName(full4.File).StartsWith("Any% ") && !full4.File.Contains(run3.GameName), "Saved full-run video excludes game name");
        model4.UndoSplit(); Drain(component4);
        for (int n = 0; n < 100 && System.IO.File.Exists(full4.File); n++) await Task.Delay(100);
        Assert(!System.IO.File.Exists(full4.File), "Undo recycles full-run video");
        typeof(Component).GetField("runStart", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(component4, (double?)(-500));
        model4.Split(); Drain(component4);
        var statusField = typeof(Component).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int n = 0; n < 150 && !((string)statusField.GetValue(component4)).StartsWith("Full run skipped:"); n++) await Task.Delay(100);
        Assert(((string)statusField.GetValue(component4)).StartsWith("Full run skipped:"), "Real saved replay too short for full run is skipped");
        Assert(System.IO.Directory.GetFiles(options.Folder, "*.mkv").Length == 0, "Skipped full run leaves no partial video");
        component4.Dispose();
        var run5 = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        run5.AddSegment("[60] Maul", bestSegmentTime: new Time(TimeSpan.FromSeconds(600), null));
        var state5 = new LiveSplitState(run5, null, null, null, null);
        var model5 = new TimerModel { CurrentState = state5 };
        var component5 = TestComponent(state5);
        model5.Start(); Drain(component5);
        var starts5 = (System.Collections.Generic.Dictionary<int, double>)typeof(Component).GetField("starts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component5);
        var clock5 = (System.Diagnostics.Stopwatch)typeof(Component).GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component5);
        double originalStart = clock5.Elapsed.TotalSeconds - 300;
        starts5[0] = originalStart;
        state5.AdjustedStartTime = TimeStamp.Now - TimeSpan.FromSeconds(360);
        model5.Split(); Drain(component5);
        var clips5 = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(component5);
        var offsetClip = clips5[0];
        Assert(Math.Abs(offsetClip.Start - originalStart) < 0.001, "Tag offset preserves actual footage start");
        Assert(Math.Abs(offsetClip.SegmentTime.Value.TotalSeconds - 360) < 0.2, "Filename retains LiveSplit segment time including offset");
        model5.UndoSplit(); Drain(component5); Assert(offsetClip.Undone, "Offset clip still supports undo");
        component5.Dispose();
        var parityRun = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        for (int i = 0; i < 64; i++) parityRun.AddSegment("Parity " + i, bestSegmentTime: new Time(TimeSpan.FromSeconds(i % 3 == 0 ? 90 : 110), TimeSpan.FromSeconds(80)));
        var parityState = new LiveSplitState(parityRun, null, null, null, null);
        var parityModel = new TimerModel { CurrentState = parityState };
        using (var parityComponent = TestComponent(parityState))
        {
            var parityOpts = (Options)typeof(Component).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parityComponent);
            parityOpts.CaptureMode = 1; // Record boundaries without starting any capture jobs.
            parityModel.Start(); Drain(parityComponent);
            var onSplit = (Action<object, EventArgs>)Delegate.CreateDelegate(typeof(Action<object, EventArgs>), parityComponent, typeof(Component).GetMethod("Split", BindingFlags.Instance | BindingFlags.NonPublic));
            int checkedCases = 0;
            for (int i = 0; i < 63; i++)
            {
                parityRun[i].SplitTime = new Time(TimeSpan.FromSeconds((i + 1) * 100), i % 9 == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds((i + 1) * 90));
                parityRun[i].Comparisons["Best Segments"] = i % 4 == 0 ? default(Time) : new Time(TimeSpan.FromSeconds((i + 1) * 95), TimeSpan.FromSeconds((i + 1) * 85));
                parityState.CurrentSplitIndex = i + 1; onSplit(null, null); Drain(parityComponent);
                var cachedSplits = (Time[])typeof(Component).GetField("matchingSplitTimes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parityComponent);
                var cachedComparisons = (Time[])typeof(Component).GetField("matchingComparisons", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parityComponent);
                foreach (TimingMethod method in new[] { TimingMethod.RealTime, TimingMethod.GameTime })
                {
                    TimeSpan? previous = i == 0 ? TimeSpan.Zero : parityRun[i - 1].SplitTime[method];
                    if (!previous.HasValue) continue; // Such segments are deliberately excluded from capture.
                    bool actual = Component.IsGold(parityRun[i].SplitTime[method], previous, parityRun[i].BestSegmentTime[method],
                        parityRun[i].Comparisons["Best Segments"][method], cachedSplits[i][method], cachedComparisons[i][method]);
                    if (actual != LiveSplitStateHelper.CheckBestSegment(parityState, i, method)) throw new Exception("Gold cache differs from LiveSplit at segment " + i + " " + method);
                    checkedCases++;
                }
            }
            Assert(checkedCases > 100, "Cached gold checks match LiveSplit for both timing methods and missing comparisons");
        }
        var undoRun = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        undoRun.AddSegment("Undo contention", bestSegmentTime: new Time(TimeSpan.FromSeconds(60), null));
        var undoState = new LiveSplitState(undoRun, null, null, null, null);
        var undoModel = new TimerModel { CurrentState = undoState };
        using (var undoComponent = TestComponent(undoState))
        using (var gateHeld = new ManualResetEventSlim())
        using (var releaseGate = new ManualResetEventSlim())
        {
            undoModel.Start(); Drain(undoComponent); Thread.Sleep(20); undoModel.Split(); Drain(undoComponent);
            var undoClips = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(undoComponent);
            var undoClip = undoClips[0];
            var worker = Task.Run(() => { lock (undoClip.Gate) { gateHeld.Set(); releaseGate.Wait(); } });
            gateHeld.Wait();
            var undoTask = Task.Run(() => undoModel.UndoSplit());
            bool returned;
            try { returned = undoTask.Wait(1000); }
            finally { releaseGate.Set(); }
            await worker; await undoTask; Drain(undoComponent);
            Assert(returned && undoClip.Undone, "Undo callback never waits for a worker holding the file-operation lock");
        }
        foreach (double tag in new[] { 0.0, 29.83 })
        {
            var run6 = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
            run6.Offset = TimeSpan.FromSeconds(139.8);
            run6.AddSegment(tag == 0 ? "First" : "[29.83] First", bestSegmentTime: new Time(TimeSpan.FromSeconds(900), null));
            run6.AddSegment("Second", bestSegmentTime: new Time(TimeSpan.FromSeconds(900), null));
            var state6 = new LiveSplitState(run6, null, null, null, null);
            var model6 = new TimerModel { CurrentState = state6 };
            using (var component6 = TestComponent(state6))
            {
                model6.Start(); Drain(component6);
                var starts6 = (System.Collections.Generic.Dictionary<int, double>)typeof(Component).GetField("starts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component6);
                var clock6 = (System.Diagnostics.Stopwatch)typeof(Component).GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component6);
                double firstStart = clock6.Elapsed.TotalSeconds - 20;
                starts6[0] = firstStart;
                state6.AdjustedStartTime = TimeStamp.Now - TimeSpan.FromSeconds(159.8 + tag);
                model6.Split(); Drain(component6);
                var clips6 = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(component6);
                Assert(Math.Abs(clips6[0].SegmentTime.Value.TotalSeconds - (159.8 + tag)) < 0.2, "Filename preserves starting timer and split tag offsets " + tag);
                Assert(Math.Abs(clips6[0].Start - firstStart) < 0.001, "Combined offsets preserve all twenty seconds of footage " + tag);
                state6.AdjustedStartTime = TimeStamp.Now - TimeSpan.FromSeconds(179.8 + tag);
                model6.Split(); Drain(component6);
                Assert(Math.Abs(clips6[1].SegmentTime.Value.TotalSeconds - 20) < 0.2, "Run offset does not affect later splits " + tag);
                model6.UndoSplit(); Drain(component6); model6.UndoSplit(); Drain(component6);
            }
        }
        var deferredRun = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        deferredRun.AddSegment("Original name", bestSegmentTime: new Time(TimeSpan.FromSeconds(60), null));
        var deferredState = new LiveSplitState(deferredRun, null, null, null, null);
        var deferredModel = new TimerModel { CurrentState = deferredState };
        using (var deferred = TestComponent(deferredState))
        using (var blocked = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            deferredModel.Start(); Drain(deferred);
            typeof(Component).GetMethod("Post", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(deferred, new object[] { (Action)(() => { blocked.Set(); release.Wait(); }) });
            blocked.Wait(); Thread.Sleep(20);
            var splitTask = Task.Run(() => deferredModel.Split());
            bool returned = splitTask.Wait(1000);
            var deferredClips = (System.Collections.Generic.Dictionary<int, Clip>)clipsField.GetValue(deferred);
            bool noChecksYet = deferredClips.Count == 0;
            TimeSpan? recorded = deferredRun[0].SplitTime.RealTime;
            // Modify live state before the worker is allowed to run. The event
            // snapshot, not these later values, must determine the clip.
            deferredRun[0].Name = "Later name";
            deferredRun[0].BestSegmentTime = new Time(TimeSpan.Zero, null);
            deferredRun[0].SplitTime = new Time(TimeSpan.FromSeconds(999), null);
            release.Set(); await splitTask; Drain(deferred);
            Assert(returned && noChecksYet, "Split callback returns while gold worker is blocked; no inline gold processing");
            Assert(deferredClips.ContainsKey(0) && deferredClips[0].Name == "Original name" && deferredClips[0].SegmentTime == recorded,
                "Deferred gold uses immutable event data rather than later LiveSplit state");
            var original = deferredClips[0];
            blocked.Reset(); release.Reset();
            typeof(Component).GetMethod("Post", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(deferred, new object[] { (Action)(() => { blocked.Set(); release.Wait(); }) });
            blocked.Wait();
            deferredRun[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(60), null);
            deferredModel.UndoSplit(); deferredModel.Split();
            release.Set(); Drain(deferred);
            Assert(original.Undone && deferredClips.ContainsKey(0) && deferredClips[0].Id != original.Id,
                "Queued split undo and re-split preserve order and independent clip identities");
            deferredModel.UndoSplit(); Drain(deferred);
        }
        var burstRun = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        for (int i = 0; i < 1000; i++) burstRun.AddSegment("No gold " + i, bestSegmentTime: new Time(TimeSpan.Zero, null));
        var burstState = new LiveSplitState(burstRun, null, null, null, null);
        var burstModel = new TimerModel { CurrentState = burstState };
        using (var burst = TestComponent(burstState))
        {
            var burstOptions = (Options)typeof(Component).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(burst);
            burstOptions.FFmpeg = "not-installed-for-this-test.exe";
            burstOptions.Folder = System.IO.Path.GetFullPath("work/GoldClip/no-gold-" + Guid.NewGuid().ToString("N"));
            burstModel.Start(); Drain(burst);
            burstState.AdjustedStartTime = TimeStamp.Now - TimeSpan.FromSeconds(10);
            for (int i = 0; i < 1000; i++) burstModel.Split();
            Drain(burst); await Task.Delay(100);
            var pending = (System.Collections.Concurrent.ConcurrentQueue<Action>)typeof(Component).GetField("pendingEvents", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(burst);
            Assert(burstState.CurrentSplitIndex == 1000 && pending.IsEmpty,
                "One thousand ordered split events finish without a growing backlog");
            Assert((int)typeof(Component).GetField("dispatching", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(burst) == 0,
                "Event worker exits when idle; no segment polling");
            Assert((int)typeof(Component).GetField("activeCaptures", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(burst) == 0 &&
                !System.IO.Directory.Exists(burstOptions.Folder),
                "Non-golds do not start capture jobs or produce diagnostic/file activity");
        }
        var layoutRun = new Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());
        layoutRun.AddSegment("Layout regression");
        var layoutState = new LiveSplitState(layoutRun, null, null, null, null);
        using (var settingsComponent = TestComponent(layoutState))
        {
            var settingsOptions = (Options)typeof(Component).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(settingsComponent);
            settingsOptions.Password = "test-only-password";
            var layout = new LiveSplit.UI.Layout {
                Settings = new LiveSplit.Options.SettingsFactories.StandardLayoutSettingsFactory().Create()
            };
            layout.LayoutComponents.Add(new LiveSplit.UI.Components.LayoutComponent("LiveSplit.GoldClip.dll", settingsComponent));
            var saver = new LiveSplit.UI.LayoutSavers.XMLLayoutSaver();
            int initial = saver.CreateLayoutNode(null, null, layout);
            for (int i = 0; i < 500; i++)
                if (saver.CreateLayoutNode(null, null, layout) != initial) throw new Exception("Unchanged layout hash must stay stable");
            Assert(!(bool)typeof(Component).GetField("hasProtectedPassword", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(settingsComponent),
                "Real LiveSplit refresh checks use fast hash without password encryption or XML serialization");
            Assert(true, "Five hundred unchanged layout checks remain stable with an OBS password configured");
            settingsOptions.Before++;
            Assert(saver.CreateLayoutNode(null, null, layout) != initial, "Layout hash still detects actual setting changes");
            var xml1 = settingsComponent.GetSettings(new System.Xml.XmlDocument()).InnerXml;
            var xml2 = settingsComponent.GetSettings(new System.Xml.XmlDocument()).InnerXml;
            Assert(xml1 == xml2, "Repeated actual settings serialization reuses cached protected password");
            int previousHash = settingsComponent.GetSettingsHashCode();
            settingsOptions.Password = "changed-test-only-password";
            Assert(settingsComponent.GetSettingsHashCode() != previousHash, "Password edits invalidate the settings hash");
            settingsComponent.GetSettings(new System.Xml.XmlDocument());
            Assert((string)typeof(Component).GetField("protectedPasswordSource", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(settingsComponent) == settingsOptions.Password,
                "Changing password refreshes the protection cache only when saving settings");
        }
        Console.WriteLine("All tests passed.");
    }
}
