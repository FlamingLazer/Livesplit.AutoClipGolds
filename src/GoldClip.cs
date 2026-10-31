using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;
using Microsoft.VisualBasic.FileIO;

[assembly: ComponentFactory(typeof(GoldClip.Factory))]
namespace GoldClip
{
    public class Factory : IComponentFactory
    {
        public string ComponentName { get { return "Auto Clip Golds"; } }
        public string Description { get { return "Save gold segments using the OBS replay buffer."; } }
        public ComponentCategory Category { get { return ComponentCategory.Other; } }
        public string UpdateName { get { return ComponentName; } }
        public string XMLURL { get { return ""; } }
        public string UpdateURL { get { return ""; } }
        public Version Version { get { return new Version(0, 9, 6); } }
        public IComponent Create(LiveSplitState state) { return new Component(state); }
    }

    public class Options
    {
        public string Url = "ws://127.0.0.1:4455";
        public string Password = "";
        public string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Gold Clips");
        public string FFmpeg = Tools.FindFFmpeg() ?? "";
        // 0 = gold splits (compatible default), 1 = full runs only.
        public int CaptureMode = 0;
        public int Before = 3, After = 3;
        public Options Copy() { return (Options)MemberwiseClone(); }
    }

    public static class Tools
    {
        public static bool HasPair(string exe)
        {
            return !string.IsNullOrWhiteSpace(exe) && File.Exists(exe) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)), "ffprobe.exe"));
        }
        public static string FindInFolders(IEnumerable<string> folders)
        {
            foreach (string folder in folders)
                try { if (string.IsNullOrWhiteSpace(folder)) continue; string exe = Path.Combine(folder.Trim().Trim('"'), "ffmpeg.exe"); if (HasPair(exe)) return Path.GetFullPath(exe); } catch { }
            return null;
        }
        static void AddChildren(List<string> folders, string root, int depth)
        {
            folders.Add(root);
            if (depth == 0) return;
            try { foreach (string dir in Directory.GetDirectories(root)) AddChildren(folders, dir, depth - 1); } catch { }
        }
        public static string FindFFmpeg()
        {
            var folders = new List<string> { AppDomain.CurrentDomain.BaseDirectory, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "bin") };
            foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string path = Environment.GetEnvironmentVariable("PATH", target);
                if (path != null) folders.AddRange(path.Split(';'));
            }
            folders.Add(@"C:\ffmpeg\bin");
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
            string found = FindInFolders(folders); if (found != null) return found;
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            folders.Clear();
            try { foreach (string dir in Directory.GetDirectories(packages, "*FFmpeg*")) AddChildren(folders, dir, 3); } catch { }
            return FindInFolders(folders);
        }
    }

    // One request/response reader per connection; saving is serialized by the component.
    public class Obs : IDisposable
    {
        readonly ClientWebSocket ws = new ClientWebSocket();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        public static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
        async Task Send(object value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(value));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
        }
        async Task<Dictionary<string, object>> Receive()
        {
            using (var stream = new MemoryStream())
            {
                var buffer = new byte[8192]; WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) throw new IOException("OBS closed the connection. Check the password.");
                    stream.Write(buffer, 0, result.Count);
                    if (stream.Length > 1024 * 1024) throw new IOException("OBS message too large.");
                } while (!result.EndOfMessage);
                return Map(json.DeserializeObject(Encoding.UTF8.GetString(stream.ToArray())));
            }
        }
        static string Hash(string input)
        {
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(input)));
        }
        public async Task Connect(Options options)
        {
            var uri = new Uri(options.Url);
            if (!uri.IsLoopback || uri.Scheme != "ws") throw new ArgumentException("Use a local OBS address, such as ws://127.0.0.1:4455.");
            await ws.ConnectAsync(uri, timeout.Token);
            var hello = await Receive();
            if (Convert.ToInt32(hello["op"]) != 0) throw new IOException("Expected OBS WebSocket 5 Hello.");
            var data = Map(hello["d"]);
            var identify = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", 64 } };
            if (data.ContainsKey("authentication"))
            {
                var auth = Map(data["authentication"]);
                identify["authentication"] = Hash(Hash(options.Password + auth["salt"]) + auth["challenge"]);
            }
            await Send(new { op = 1, d = identify });
            if (Convert.ToInt32((await Receive())["op"]) != 2) throw new IOException("OBS identification failed.");
        }
        public async Task<Dictionary<string, object>> Request(string type)
        {
            string id = Guid.NewGuid().ToString("N");
            await Send(new { op = 6, d = new { requestType = type, requestId = id } });
            while (true)
            {
                var msg = await Receive();
                if (Convert.ToInt32(msg["op"]) != 7) continue;
                var d = Map(msg["d"]);
                if ((string)d["requestId"] != id) continue;
                var status = Map(d["requestStatus"]);
                if (!(bool)status["result"]) throw new IOException("OBS: " + (status.ContainsKey("comment") ? status["comment"] : status["code"]));
                return d.ContainsKey("responseData") ? Map(d["responseData"]) : new Dictionary<string, object>();
            }
        }
        public async Task<string> Save(Action requested)
        {
            string id = Guid.NewGuid().ToString("N"), path = null; bool accepted = false;
            requested();
            await Send(new { op = 6, d = new { requestType = "SaveReplayBuffer", requestId = id } });
            while (!accepted || path == null)
            {
                var msg = await Receive(); var d = Map(msg["d"]); int op = Convert.ToInt32(msg["op"]);
                if (op == 7 && (string)d["requestId"] == id)
                {
                    var status = Map(d["requestStatus"]);
                    if (!(bool)status["result"]) throw new IOException("OBS could not save the replay buffer.");
                    accepted = true;
                }
                if (op == 5 && (string)d["eventType"] == "ReplayBufferSaved") path = (string)Map(d["eventData"])["savedReplayPath"];
            }
            return path;
        }
        public void Dispose() { ws.Dispose(); timeout.Dispose(); }
    }

    public class Clip
    {
        public int Index;
        public double Start, End;
        public string Name, Id = Guid.NewGuid().ToString("N"), File;
        public Options Options;
        int cancelled;
        public bool Undone { get { return Volatile.Read(ref cancelled) != 0; } set { if (value) Interlocked.Exchange(ref cancelled, 1); } }
        public bool IsFullRun;
        public DateTime SplitDate;
        public TimeSpan? SegmentTime;
        public readonly object Gate = new object();
    }

    public class Component : IComponent
    {
        readonly LiveSplitState state;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<int, double> starts = new Dictionary<int, double>();
        readonly Dictionary<int, Clip> clips = new Dictionary<int, Clip>();
        readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);
        Options options = new Options();
        volatile string status = "Auto Clip Golds 0.9.6 test - ready";
        volatile bool disposed;
        double? runStart;
        Time[] matchingSplitTimes = new Time[0], matchingComparisons = new Time[0];
        int activeCaptures;
        readonly ConcurrentQueue<Action> pendingEvents = new ConcurrentQueue<Action>();
        int dispatching;
        readonly IDictionary<string, Action> menu;
        string protectedPasswordSource, protectedPasswordValue;
        bool hasProtectedPassword;
        public Component(LiveSplitState state)
        {
            this.state = state;
            menu = new Dictionary<string, Action> { { "Auto Clip Golds status", () => MessageBox.Show(status, "Auto Clip Golds") } };
            state.OnStart += Started; state.OnSplit += Split; state.OnUndoSplit += Undo; state.OnSkipSplit += Skip;
        }
        double Now { get { return clock.Elapsed.TotalSeconds; } }
        // Event callbacks only snapshot already-recorded values and enqueue work.
        // One on-demand dispatcher preserves start/split/undo order. It exits
        // when idle; there is no polling loop or periodic timer for capture.
        void Post(Action action)
        {
            if (disposed) return;
            pendingEvents.Enqueue(action);
            StartDispatcher();
        }
        void StartDispatcher()
        {
            if (Interlocked.CompareExchange(ref dispatching, 1, 0) != 0) return;
            Task.Run(() => {
                try {
                    Action action;
                    while (pendingEvents.TryDequeue(out action)) {
                        if (disposed) continue;
                        try { action(); } catch (Exception ex) { SetStatus("Split processing error: " + ex.Message); }
                    }
                }
                finally { Volatile.Write(ref dispatching, 0); if (!pendingEvents.IsEmpty) StartDispatcher(); }
            });
        }
        void Started(object sender, EventArgs e)
        {
            double when = Now; int count = state.Run.Count;
            Post(() => {
                starts.Clear(); clips.Clear(); starts[0] = when; runStart = when;
                matchingSplitTimes = new Time[count + 1]; matchingComparisons = new Time[count + 1];
            });
        }
        void Skip(object sender, EventArgs e)
        {
            double when = Now; int index = state.CurrentSplitIndex;
            Post(() => {
                starts[index] = when; int i = index - 1;
                if (i >= 0 && i + 1 < matchingSplitTimes.Length) RecordComparison(i, default(Time), default(Time));
            });
        }
        sealed class SplitSnapshot
        {
            public int Index, RunCount;
            public double End;
            public bool Ended;
            public Time SplitTime, Comparison, Best, Previous;
            public TimingMethod Method;
            public string Name, Category;
            public DateTime Date;
            public Options Options;
        }
        void RecordComparison(int index, Time split, Time comparison)
        {
            var priorSplit = matchingSplitTimes[index]; var priorComparison = matchingComparisons[index];
            if (split.RealTime.HasValue && comparison.RealTime.HasValue) { priorSplit.RealTime = split.RealTime; priorComparison.RealTime = comparison.RealTime; }
            if (split.GameTime.HasValue && comparison.GameTime.HasValue) { priorSplit.GameTime = split.GameTime; priorComparison.GameTime = comparison.GameTime; }
            matchingSplitTimes[index + 1] = priorSplit; matchingComparisons[index + 1] = priorComparison;
        }
        public static bool IsGold(TimeSpan? current, TimeSpan? previous, TimeSpan? best,
            TimeSpan? comparison, TimeSpan? matchingSplit, TimeSpan? matchingComparison)
        {
            if (!current.HasValue) return false;
            TimeSpan? segment = current - previous;
            TimeSpan? delta = current - comparison;
            if (matchingSplit.HasValue) delta -= matchingSplit - matchingComparison;
            return !best.HasValue || segment < best || delta < TimeSpan.Zero;
        }
        void Split(object sender, EventArgs e)
        {
            int index = state.CurrentSplitIndex - 1;
            if (index < 0 || index >= state.Run.Count) return;
            var segment = state.Run[index];
            var snapshot = new SplitSnapshot {
                Index = index, RunCount = state.Run.Count, End = Now,
                Ended = state.CurrentPhase == TimerPhase.Ended,
                SplitTime = segment.SplitTime, Comparison = segment.Comparisons["Best Segments"],
                Best = segment.BestSegmentTime, Previous = index == 0 ? new Time(TimeSpan.Zero, TimeSpan.Zero) : state.Run[index - 1].SplitTime,
                Method = state.CurrentTimingMethod, Name = segment.Name,
                Category = state.Run.CategoryName, Date = DateTime.Now, Options = options.Copy()
            };
            Post(() => HandleSplit(snapshot));
        }
        void HandleSplit(SplitSnapshot snapshot)
        {
            int i = snapshot.Index; double end = snapshot.End;
            starts[i + 1] = end;
            if (i + 1 >= matchingSplitTimes.Length) { SetStatus("Start a fresh attempt to capture clips."); return; }
            RecordComparison(i, snapshot.SplitTime, snapshot.Comparison);
            if (snapshot.Ended && snapshot.Options.CaptureMode == 1 && runStart.HasValue)
            {
                Queue(new Clip { Index = -1, IsFullRun = true, Start = runStart.Value, End = end,
                    Name = string.IsNullOrWhiteSpace(snapshot.Category) ? "Uncategorized" : snapshot.Category,
                    Options = snapshot.Options, SplitDate = snapshot.Date,
                    SegmentTime = snapshot.SplitTime[snapshot.Method] ?? snapshot.SplitTime.RealTime });
            }
            if (snapshot.Options.CaptureMode == 1) return;
            double start;
            if (!starts.TryGetValue(i, out start)) { SetStatus("Start a fresh attempt to capture clips."); return; }
            TimingMethod method = snapshot.Method;
            TimeSpan? previousTime = snapshot.Previous[method];
            if (i > 0 && !previousTime.HasValue) return;
            if (!IsGold(snapshot.SplitTime[method], previousTime, snapshot.Best[method],
                snapshot.Comparison[method], matchingSplitTimes[i][method], matchingComparisons[i][method])) return;
            Queue(new Clip { Index = i, Start = start, End = end, Name = snapshot.Name, Options = snapshot.Options,
                SplitDate = snapshot.Date, SegmentTime = snapshot.SplitTime[method] - previousTime });
        }
        void Queue(Clip clip)
        {
            clips[clip.Index] = clip;
            Interlocked.Increment(ref activeCaptures);
            Task.Run(() => Capture(clip));
        }
        void Undo(object sender, EventArgs e)
        {
            int index = state.CurrentSplitIndex, count = state.Run.Count;
            Post(() => {
                CancelClip(index);
                if (index == count - 1) CancelClip(-1);
                foreach (int key in new List<int>(starts.Keys)) if (key > index) starts.Remove(key);
            });
        }
        void CancelClip(int i)
        {
            Clip clip;
            if (clips.TryGetValue(i, out clip))
            {
                // The timer thread must never wait for recycling or file I/O.
                clip.Undone = true;
                Task.Run(() => { try { lock (clip.Gate) { Recycle(clip.File); } SetStatus("Undone clip removed."); } catch (Exception ex) { SetStatus("Could not recycle clip: " + ex.Message); } });
                clips.Remove(i);
            }
        }
        static void Recycle(string path)
        {
            if (path != null && File.Exists(path)) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
        void SetStatus(string message)
        {
            status = message;
            WriteLog(message);
        }
        void WriteLog(string message)
        {
            string folder = options.Folder;
            string entry = DateTime.Now.ToString("s") + " " + message + Environment.NewLine;
            if (Interlocked.Increment(ref logCount) > 256) { Interlocked.Decrement(ref logCount); return; }
            logs.Enqueue(() => { try { Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "GoldClip.log"), entry); } catch { } });
            StartLogger();
        }
        static readonly ConcurrentQueue<Action> logs = new ConcurrentQueue<Action>();
        static int logCount, logging;
        static void StartLogger()
        {
            if (Interlocked.CompareExchange(ref logging, 1, 0) != 0) return;
            Task.Run(() => {
                try { Action write; while (logs.TryDequeue(out write)) { Interlocked.Decrement(ref logCount); write(); } }
                finally { Volatile.Write(ref logging, 0); if (!logs.IsEmpty) StartLogger(); }
            });
        }
        static string Quote(string value) { if (value.Contains("\"")) throw new ArgumentException("Invalid quote in path."); return "\"" + value + "\""; }
        static string Number(double value) { return value.ToString("0.000", CultureInfo.InvariantCulture); }
        public static double SeekStart(double duration, double saveTime, double segmentStart, int before)
        { return Math.Max(0, duration - (saveTime - segmentStart + before)); }
        public static bool CoversRun(double duration, double saveTime, double start)
        { return duration + 0.1 >= saveTime - start; }
        public static double SplitOffset(string name)
        {
            foreach (Match match in Regex.Matches(name ?? "", @"\[(\d+(?::\d{1,2}){0,2}(?:\.\d{1,3})?)\]"))
            {
                string[] parts = match.Groups[1].Value.Split(':');
                double total = 0; bool valid = true;
                for (int i = 0; i < parts.Length; i++)
                {
                    double value;
                    if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
                        || double.IsInfinity(value) || (i > 0 && value >= 60)) { valid = false; break; }
                    total = total * 60 + value;
                }
                if (valid && total <= TimeSpan.MaxValue.TotalSeconds) return total;
            }
            return 0;
        }
        public static string GoldFileName(string splitName, DateTime date, TimeSpan time)
        { return ClipFileName(StripOffsets(splitName), date, time, true); }
        public static string StripOffsets(string name)
        {
            string clean = Regex.Replace(name ?? "", @"\[(\d+(?::\d{1,2}){0,2}(?:\.\d{1,3})?)\]", match =>
                SplitOffset(match.Value) > 0 || Regex.IsMatch(match.Value, @"^\[0+(?::0{1,2}){0,2}(?:\.0{1,3})?\]$") ? "" : match.Value);
            clean = Regex.Replace(clean, @"\s{2,}", " ").Trim();
            return clean.Length == 0 ? "Split" : clean;
        }
        public static double PickKeyframe(double desired, double segmentStart, IEnumerable<double> keys)
        {
            double best = 0, distance = double.MaxValue;
            foreach (double key in keys)
            {
                if (key < 0 || key > segmentStart) continue;
                double delta = Math.Abs(key - desired);
                if (delta < distance || (delta == distance && key < best)) { best = key; distance = delta; }
            }
            return best;
        }
        public static string FullRunFileName(string category, DateTime date, TimeSpan time)
        { return ClipFileName(category, date, time, false); }
        static string ClipFileName(string splitName, DateTime date, TimeSpan time, bool gold)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) splitName = splitName.Replace(bad, '_');
            splitName = splitName.Trim().TrimEnd('.');
            if (splitName.Length > 70) splitName = splitName.Substring(0, 70);
            long centiseconds = Math.Max(0, time.Ticks / (TimeSpan.TicksPerMillisecond * 10));
            return splitName + (gold ? " Gold " : " ") + date.ToString("M-d-yy", CultureInfo.InvariantCulture) + " " +
                (centiseconds / 6000).ToString(CultureInfo.InvariantCulture) + "\uA789" +
                ((centiseconds / 100) % 60).ToString("00", CultureInfo.InvariantCulture) + "." +
                (centiseconds % 100).ToString("00", CultureInfo.InvariantCulture) + ".mkv";
        }
        static readonly HashSet<string> reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string ReserveName(string folder, string fileName)
        {
            lock (reservedNames)
            {
                string path = Path.Combine(folder, fileName); int number = 2;
                while (File.Exists(path) || reservedNames.Contains(path))
                    path = Path.Combine(folder, Path.GetFileNameWithoutExtension(fileName) + " (" + number++ + ")" + Path.GetExtension(fileName));
                reservedNames.Add(path); return path;
            }
        }
        static string Run(string exe, string args)
        {
            using (var p = new Process { StartInfo = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } })
            {
                p.Start(); try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var error = p.StandardError.ReadToEndAsync(); var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(120000)) { p.Kill(); throw new IOException("Video processing timed out; raw replay retained."); }
                Task.WaitAll(error, output);
                if (p.ExitCode != 0) throw new IOException("Video processing failed: " + error.Result);
                return output.Result;
            }
        }
        async Task Capture(Clip clip)
        {
            string raw = null, output = null; bool held = false;
            try
            {
                if (!Tools.HasPair(clip.Options.FFmpeg)) throw new IOException("Select FFmpeg and FFprobe, or use Auto-detect FFmpeg in settings.");
                SetStatus("Clip: " + clip.Name + " - waiting for padding");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, clip.End + clip.Options.After - Now)));
                lock (clip.Gate) { if (clip.Undone || disposed) return; }
                await saveGate.WaitAsync(); held = true;
                lock (clip.Gate) { if (clip.Undone || disposed) return; }
                double savedAt = 0; string savedPath;
                using (var obs = new Obs())
                {
                    await obs.Connect(clip.Options);
                    if (!(bool)(await obs.Request("GetReplayBufferStatus"))["outputActive"]) throw new IOException("OBS replay buffer is not running.");
                    savedPath = await obs.Save(() => savedAt = Now);
                }
                // Take ownership only of the replay reported by this save operation.
                Directory.CreateDirectory(clip.Options.Folder);
                string owned = Path.Combine(clip.Options.Folder, "raw-" + clip.Id + Path.GetExtension(savedPath));
                File.Move(savedPath, owned); raw = owned;
                saveGate.Release(); held = false;
                lock (clip.Gate) { if (clip.Undone) { Recycle(raw); return; } }
                string probe = Path.Combine(Path.GetDirectoryName(clip.Options.FFmpeg), "ffprobe.exe");
                var format = Obs.Map(Obs.Map(new JavaScriptSerializer().DeserializeObject(Run(probe,
                    "-v error -show_entries format=duration,start_time -of json " + Quote(raw))))["format"]);
                double duration = double.Parse((string)format["duration"], CultureInfo.InvariantCulture);
                double sourceStart = 0;
                if (format.ContainsKey("start_time")) double.TryParse((string)format["start_time"], NumberStyles.Float, CultureInfo.InvariantCulture, out sourceStart);
                if (clip.IsFullRun && !CoversRun(duration, savedAt, clip.Start))
                {
                    Recycle(raw);
                    SetStatus("Full run skipped: replay buffer does not contain the start of the run. Allow room for post-run padding.");
                    return;
                }
                double needed = savedAt - clip.Start + clip.Options.Before;
                double seek = SeekStart(duration, savedAt, clip.Start, clip.Options.Before);
                var keys = new List<double>();
                var keyDecodeTimes = new Dictionary<double, double>();
                string packets = Run(probe, "-v error -select_streams v:0 -show_packets -show_entries packet=pts_time,dts_time,flags -of csv=p=0 " + Quote(raw));
                using (var reader = new StringReader(packets))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] fields = line.Split(','); double pts, dts;
                        if (fields.Length >= 3 && fields[2].Contains("K") &&
                            double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out pts) &&
                            double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out dts))
                        { keys.Add(pts - sourceStart); keyDecodeTimes[pts - sourceStart] = dts - sourceStart; }
                    }
                }
                double segmentStart = Math.Max(0, duration - (savedAt - clip.Start));
                seek = PickKeyframe(seek, segmentStart, keys);
                // Packet filtering uses decode timestamps; using display time
                // can discard a B-frame keyframe and skip to the next GOP.
                if (keyDecodeTimes.ContainsKey(seek)) seek = Math.Max(0, keyDecodeTimes[seek]);
                double end = Math.Min(duration, duration - (savedAt - clip.End - clip.Options.After));
                TimeSpan clipTime = clip.SegmentTime ?? TimeSpan.FromSeconds(clip.End - clip.Start);
                output = ReserveName(clip.Options.Folder, clip.IsFullRun
                    ? FullRunFileName(clip.Name, clip.SplitDate, clipTime)
                    : GoldFileName(clip.Name, clip.SplitDate, clipTime));
                // Output-side packet filtering avoids input seeking pulling in
                // the previous GOP. The selected keyframe precedes gameplay.
                SetStatus("Trimming: " + clip.Name);
                Run(clip.Options.FFmpeg, "-nostdin -v error -n -i " + Quote(raw) +
                    (seek > 0 ? " -ss " + Number(Math.Max(0, seek - 0.001)) : "") +
                    " -t " + Number(Math.Max(0.1, end - seek)) + " -map 0:v? -map 0:a? -c copy -avoid_negative_ts make_zero " + Quote(output));
                lock (clip.Gate)
                {
                    clip.File = output;
                    if (clip.Undone) { Recycle(output); Recycle(raw); return; }
                    File.Delete(raw);
                }
                SetStatus((duration + 1 < needed ? "Saved (buffer too short; start truncated): " : "Saved: ") + output);
            }
            catch (Exception ex)
            {
                try { lock (clip.Gate) { if (clip.Undone) { Recycle(raw); Recycle(output); } else { clip.File = raw; if (output != null && File.Exists(output)) Recycle(output); } } } catch { }
                SetStatus("Clip error: " + ex.Message + (raw != null ? " Replay: " + raw : ""));
            }
            finally {
                Interlocked.Decrement(ref activeCaptures);
                if (held) saveGate.Release(); if (output != null) lock (reservedNames) { reservedNames.Remove(output); }
            }
        }

        public Control GetSettingsControl(LayoutMode mode)
        {
            var panel = new TableLayoutPanel { Width = 460, MinimumSize = new Size(460, 0), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, Padding = new Padding(6), Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var capture = AddGroup(panel, "Capture");
            var modes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            modes.Items.AddRange(new object[] { "Gold splits", "Full runs" });
            modes.SelectedIndex = options.CaptureMode;
            AddRow(capture, "Mode", modes, null);
            var padding = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            padding.Controls.Add(new Label { Text = "Before", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            padding.Controls.Add(NumberBox(options.Before, v => options.Before = v));
            padding.Controls.Add(new Label { Text = "After", AutoSize = true, Margin = new Padding(10, 6, 4, 0) });
            padding.Controls.Add(NumberBox(options.After, v => options.After = v));
            padding.Controls.Add(new Label { Text = "seconds", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
            AddRow(capture, "Padding", padding, null);
            var note = new Label { AutoSize = true, MaximumSize = new Size(405, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 5, 0, 2) };
            Action updateNote = () => note.Text = modes.SelectedIndex == 1
                ? "Runs must fit in the replay buffer, including end padding."
                : "";
            note.Visible = options.CaptureMode == 1;
            updateNote();
            modes.SelectedIndexChanged += (sender, e) => { options.CaptureMode = modes.SelectedIndex; updateNote(); note.Visible = modes.SelectedIndex == 1; };
            AddWide(capture, note);

            var save = AddGroup(panel, "Save location");
            var folder = TextBox(options.Folder, false, v => options.Folder = v);
            var browse = Button("Browse...");
            browse.Click += (sender, e) => {
                using (var dialog = new FolderBrowserDialog { Description = "Choose where to save clips", SelectedPath = folder.Text })
                    if (dialog.ShowDialog(panel.FindForm()) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            };
            AddRow(save, "Clip folder", folder, browse);
            var ffmpeg = TextBox(options.FFmpeg, false, v => options.FFmpeg = v);
            var detect = Button("Auto-detect");
            AddRow(save, "FFmpeg", ffmpeg, detect);
            var toolStatus = Feedback();
            Action updateTools = () => {
                bool valid = false; try { valid = Tools.HasPair(ffmpeg.Text); } catch { }
                toolStatus.Text = valid ? "FFmpeg and FFprobe found." : "Choose ffmpeg.exe with FFprobe in the same folder.";
                toolStatus.ForeColor = valid ? Color.DarkGreen : SystemColors.GrayText;
            };
            updateTools(); ffmpeg.TextChanged += (sender, e) => updateTools();
            AddWide(save, toolStatus);
            detect.Click += async (sender, e) => {
                detect.Enabled = false; toolStatus.Text = "Searching for FFmpeg...";
                try {
                    string found = await Task.Run(() => Tools.FindFFmpeg());
                    if (panel.IsDisposed) return;
                    if (found != null) { ffmpeg.Text = found; updateTools(); }
                    else { toolStatus.Text = "FFmpeg not found. Enter its location manually."; toolStatus.ForeColor = Color.Firebrick; }
                } catch (Exception ex) { if (!panel.IsDisposed) toolStatus.Text = "Search failed: " + ex.Message; }
                finally { if (!detect.IsDisposed) detect.Enabled = true; }
            };

            var obsGroup = AddGroup(panel, "OBS connection");
            AddRow(obsGroup, "Address", TextBox(options.Url, false, v => options.Url = v), null);
            AddRow(obsGroup, "Password", TextBox(options.Password, true, v => options.Password = v), null);
            var test = Button("Test connection");
            var connectionStatus = Feedback(); connectionStatus.Text = "Enable the WebSocket server in OBS to connect.";
            AddWide(obsGroup, test); AddWide(obsGroup, connectionStatus);
            test.Click += async (sender, e) => {
                test.Enabled = false; connectionStatus.Text = "Connecting..."; connectionStatus.ForeColor = SystemColors.GrayText;
                var snapshot = options.Copy();
                try { using (var obs = new Obs()) {
                    await obs.Connect(snapshot);
                    bool active = (bool)(await obs.Request("GetReplayBufferStatus"))["outputActive"];
                    if (panel.IsDisposed) return;
                    connectionStatus.Text = active ? "Connected. Replay buffer is running." : "Connected. Start the replay buffer in OBS.";
                    connectionStatus.ForeColor = active ? Color.DarkGreen : SystemColors.GrayText;
                } } catch (Exception ex) { if (!panel.IsDisposed) { connectionStatus.Text = ex.Message; connectionStatus.ForeColor = Color.Firebrick; } }
                finally { if (!test.IsDisposed) test.Enabled = true; }
            };

            var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 4, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var latest = new Label { Text = status, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 6, 0) };
            var tooltip = new ToolTip(); tooltip.SetToolTip(latest, status);
            var log = Button("View log");
            log.Click += (sender, e) => {
                try {
                    string path = Path.Combine(options.Folder, "GoldClip.log");
                    if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    else MessageBox.Show(panel.FindForm(), "No log entries yet. Capture a clip to create the log.", "Auto Clip Golds");
                } catch (Exception ex) { MessageBox.Show(panel.FindForm(), ex.Message, "Auto Clip Golds"); }
            };
            footer.Controls.Add(latest); footer.Controls.Add(log); panel.Controls.Add(footer);
            var refresh = new System.Windows.Forms.Timer { Interval = 1000 };
            refresh.Tick += (sender, e) => { if (latest.Text != status) { latest.Text = status; tooltip.SetToolTip(latest, status); } };
            panel.HandleCreated += (sender, e) => refresh.Start();
            panel.HandleDestroyed += (sender, e) => refresh.Stop();
            panel.Disposed += (sender, e) => { refresh.Dispose(); tooltip.Dispose(); };
            return panel;
        }
        static Button Button(string text) { return new Button { Text = text, AutoSize = true, Margin = new Padding(4, 2, 0, 2) }; }
        static Label Feedback() { return new Label { AutoSize = true, MaximumSize = new Size(405, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 2) }; }
        static TextBox TextBox(string value, bool secret, Action<string> change)
        {
            var box = new TextBox { Text = value, Dock = DockStyle.Fill, UseSystemPasswordChar = secret, Margin = new Padding(0, 3, 0, 3) };
            box.TextChanged += (sender, e) => change(box.Text); return box;
        }
        static NumericUpDown NumberBox(int value, Action<int> change)
        {
            var box = new NumericUpDown { Minimum = 0, Maximum = 30, Value = value, Width = 48 };
            box.ValueChanged += (sender, e) => change((int)box.Value); return box;
        }
        static TableLayoutPanel AddGroup(TableLayoutPanel parent, string title)
        {
            var group = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 17, 10, 9), Margin = new Padding(0, 0, 0, 9) };
            var table = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            group.Controls.Add(table); parent.Controls.Add(group); return table;
        }
        static void AddRow(TableLayoutPanel table, string label, Control field, Control action)
        {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, 0, row);
            table.Controls.Add(field, 1, row);
            if (action == null) table.SetColumnSpan(field, 2); else table.Controls.Add(action, 2, row);
        }
        static void AddWide(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control, 0, row); table.SetColumnSpan(control, 3);
        }
        // LiveSplit calls this optional method while checking the layout on
        // redraw ticks. Never serialize XML or encrypt a password here.
        public int GetSettingsHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (options.Url ?? "").GetHashCode();
                hash = hash * 31 + (options.Password ?? "").GetHashCode();
                hash = hash * 31 + (options.Folder ?? "").GetHashCode();
                hash = hash * 31 + (options.FFmpeg ?? "").GetHashCode();
                hash = hash * 31 + options.CaptureMode;
                hash = hash * 31 + options.Before;
                hash = hash * 31 + options.After;
                return hash;
            }
        }
        string ProtectedPassword()
        {
            string password = options.Password ?? "";
            if (hasProtectedPassword && protectedPasswordSource == password) return protectedPasswordValue;
            string value = "";
            if (password.Length > 0)
            {
                try { value = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser)); }
                catch (CryptographicException) { SetStatus("Windows could not protect the OBS password. Re-enter it after restarting LiveSplit."); }
            }
            protectedPasswordSource = password; protectedPasswordValue = value; hasProtectedPassword = true;
            return value;
        }
        public XmlNode GetSettings(XmlDocument doc)
        {
            var root = doc.CreateElement("Settings");
            foreach (var field in typeof(Options).GetFields())
            {
                var item = doc.CreateElement(field.Name); string value = Convert.ToString(field.GetValue(options), CultureInfo.InvariantCulture);
                if (field.Name == "Password") value = ProtectedPassword();
                item.InnerText = value; root.AppendChild(item);
            }
            return root;
        }
        public void SetSettings(XmlNode root)
        {
            foreach (var field in typeof(Options).GetFields())
            {
                var node = root.SelectSingleNode(field.Name); if (node == null) continue;
                try
                {
                    string value = node.InnerText;
                    if (field.Name == "Password" && value.Length > 0) value = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
                    if (field.Name == "Password") { protectedPasswordSource = value; protectedPasswordValue = node.InnerText; hasProtectedPassword = true; }
                    if (field.FieldType == typeof(int)) field.SetValue(options, Math.Max(0, Math.Min(field.Name == "CaptureMode" ? 1 : 30, int.Parse(value, CultureInfo.InvariantCulture))));
                    else field.SetValue(options, value);
                }
                catch { SetStatus("Could not load " + field.Name + "; check settings."); }
            }
        }
        public void Dispose() { disposed = true; state.OnStart -= Started; state.OnSplit -= Split; state.OnUndoSplit -= Undo; state.OnSkipSplit -= Skip; }
        public string ComponentName { get { return "Auto Clip Golds"; } }
        public float HorizontalWidth { get { return 0; } }
        public float MinimumHeight { get { return 0; } }
        public float VerticalHeight { get { return 0; } }
        public float MinimumWidth { get { return 0; } }
        public float PaddingTop { get { return 0; } }
        public float PaddingBottom { get { return 0; } }
        public float PaddingLeft { get { return 0; } }
        public float PaddingRight { get { return 0; } }
        public IDictionary<string, Action> ContextMenuControls { get { return menu; } }
        public void DrawHorizontal(Graphics g, LiveSplitState s, float height, Region region) { }
        public void DrawVertical(Graphics g, LiveSplitState s, float width, Region region) { }
        public void Update(IInvalidator invalidator, LiveSplitState s, float width, float height, LayoutMode mode) { }
    }
}
