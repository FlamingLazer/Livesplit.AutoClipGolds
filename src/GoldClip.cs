using System;
using System.Collections.Generic;
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
        public Version Version { get { return new Version(0, 9, 1); } }
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
        public bool Undone;
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
        volatile string status = "Ready (OBS must have replay buffer running)";
        volatile bool disposed;
        double? runStart;
        public Component(LiveSplitState state)
        {
            this.state = state;
            state.OnStart += Started; state.OnSplit += Split; state.OnUndoSplit += Undo; state.OnSkipSplit += Skip;
        }
        double Now { get { return clock.Elapsed.TotalSeconds; } }
        void Started(object sender, EventArgs e) {
            starts.Clear(); clips.Clear(); starts[0] = Now; runStart = starts[0];
        }
        void Skip(object sender, EventArgs e) { starts[state.CurrentSplitIndex] = Now; }
        void Split(object sender, EventArgs e)
        {
            int i = state.CurrentSplitIndex - 1; double end = Now;
            starts[state.CurrentSplitIndex] = end;
            if (state.CurrentPhase == TimerPhase.Ended && options.CaptureMode == 1 && runStart.HasValue)
            {
                Queue(new Clip { Index = -1, IsFullRun = true, Start = runStart.Value, End = end,
                    Name = string.IsNullOrWhiteSpace(state.Run.CategoryName) ? "Uncategorized" : state.Run.CategoryName,
                    Options = options.Copy(), SplitDate = DateTime.Now,
                    SegmentTime = state.CurrentTime[state.CurrentTimingMethod] ?? state.CurrentTime.RealTime });
            }
            if (options.CaptureMode == 1) return;
            double start;
            if (i < 0 || !starts.TryGetValue(i, out start)) { SetStatus("Start a fresh attempt to capture clips."); return; }
            // A skipped split combines multiple levels, so it is not a single-level gold.
            if (i > 0 && state.Run[i - 1].SplitTime[state.CurrentTimingMethod] == null) return;
            if (!LiveSplitStateHelper.CheckBestSegment(state, i, state.CurrentTimingMethod)) return;
            var clip = new Clip { Index = i, Start = start, End = end, Name = state.Run[i].Name, Options = options.Copy(),
                SplitDate = DateTime.Now, SegmentTime = LiveSplitStateHelper.GetPreviousSegmentTime(state, i, state.CurrentTimingMethod) };
            // Keep LiveSplit's segment time intact for filenames, including
            // starting timer values and split offsets. Footage uses wall time.
            Queue(clip);
        }
        void Queue(Clip clip)
        {
            clips[clip.Index] = clip;
            Task.Run(() => Capture(clip));
        }
        void Undo(object sender, EventArgs e)
        {
            int i = state.CurrentSplitIndex;
            CancelClip(i);
            // Undoing the final split also invalidates that finished run's clip.
            if (i == state.Run.Count - 1) CancelClip(-1);
            foreach (int key in new List<int>(starts.Keys)) if (key > i) starts.Remove(key);
        }
        void CancelClip(int i)
        {
            Clip clip;
            if (clips.TryGetValue(i, out clip))
            {
                lock (clip.Gate) { clip.Undone = true; }
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
            try { lock (logGate) { Directory.CreateDirectory(options.Folder); File.AppendAllText(Path.Combine(options.Folder, "GoldClip.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } } catch { }
        }
        static readonly object logGate = new object();
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
            finally { if (held) saveGate.Release(); if (output != null) lock (reservedNames) { reservedNames.Remove(output); } }
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
        public XmlNode GetSettings(XmlDocument doc)
        {
            var root = doc.CreateElement("Settings");
            foreach (var field in typeof(Options).GetFields())
            {
                var item = doc.CreateElement(field.Name); string value = Convert.ToString(field.GetValue(options), CultureInfo.InvariantCulture);
                if (field.Name == "Password" && value.Length > 0)
                {
                    try { value = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)); }
                    catch (CryptographicException) { value = ""; SetStatus("Windows could not protect the OBS password. Re-enter it after restarting LiveSplit."); }
                }
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
        public IDictionary<string, Action> ContextMenuControls { get { return new Dictionary<string, Action> { { "Auto Clip Golds status", () => MessageBox.Show(status, "Auto Clip Golds") } }; } }
        public void DrawHorizontal(Graphics g, LiveSplitState s, float height, Region region) { }
        public void DrawVertical(Graphics g, LiveSplitState s, float width, Region region) { }
        public void Update(IInvalidator invalidator, LiveSplitState s, float width, float height, LayoutMode mode) { }
    }
}
