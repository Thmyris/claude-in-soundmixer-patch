// claude-chime: Claude Code hook chime + "put Claude's terminal audio under the
// Claude slider" watcher.
//
//   claude-chime.exe            play chime.wav or chime.mp3 (next to the exe), start the watcher
//   claude-chime.exe <file>     play that file (.wav, or .mp3 etc. via MCI), start the watcher
//   claude-chime.exe --start    only start the watcher (SessionStart hook), no sound
//   claude-chime.exe --why      print which level the chime would use and why, no sound
//   claude-chime.exe --watch    run the watcher (normally started by --start or the chime)
//
// The chime plays at the level of the Claude desktop app's Volume Mixer slider
// and stays silent while Claude is muted. The level comes from, first match wins:
//   0. claude-level.txt while the watcher runs (it owns the slider, see below).
//   1. Claude's live audio session on the default output device.
//   2. claude-level.txt, the last live level the watcher saw for this run of Claude.
//   3. The level Windows saved for Claude.exe on the default output device
//      (HKCU\...\Audio\PolicyConfig\PropertyStore). Windows restores it into
//      Claude's slider next time, but writes it lazily, not on every slider move.
//   4. Claude's live session on any other output device.
//   5. 100%.
//
// Claude itself only has a mixer slider for a while after it has played audio (e.g.
// from its browser pane); Chromium drops the session again after some idle minutes.
//
// So the watcher keeps a permanent "Claude" slider: it holds a silent, never-started
// audio stream of its own on the default output device, labelled "Claude" with
// Claude's icon. Its session, Claude's own sessions (when they exist) and every
// session of a process the Claude desktop app started (Terminal panel shells, what
// they run, this chime) share one fixed grouping GUID, so the Volume Mixer draws
// them as one slider. Whichever member the user moves (the mixer moves them all;
// Settings > App volume moves one) sets the level and mute for all of them.
// If some unrelated process of an exe it adjusted (e.g. a PowerShell in Windows
// Terminal) inherits that level from Windows' per-exe memory, it is put back to
// 100%. It polls every second, and at once when Windows reports that any app opened
// an audio session (so e.g. Claude's sound after an update, which Windows starts at
// 100% because it has no saved level for the new exe path, is pulled down within
// milliseconds). It remembers each audio process's verdict and only snapshots the
// process table for new ones. The watcher exits when the Claude
// desktop app is no longer running; one instance at a time. Log: watcher.log next
// to the exe.
//
// Rebuild:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:claude-chime.exe claude-chime.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

static class ClaudeChime
{
    const string PolicyStore = @"Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore";
    const string VolumeSubkey = "{219ED5A0-9CBF-4F3A-B927-37C9E5C5F14F}";
    const string WatcherMutex = @"Local\ClaudeChimeWatcher";

    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string LevelFile = Path.Combine(Dir, "claude-level.txt");
    static readonly string LogFile = Path.Combine(Dir, "watcher.log");

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        try
        {
            if (mode == "--watch") return Watch();
            if (mode == "--start") { EnsureWatcher(new Procs()); return 0; }

            var procs = new Procs();
            float volume; bool muted; string source;
            ResolveChimeLevel(procs, out volume, out muted, out source);

            if (mode == "--why")
            {
                Console.WriteLine("volume={0:0.00} muted={1} source={2}", volume, muted, source);
                Console.WriteLine("watcher running: {0}", WatcherRunning());
                return 0;
            }

            EnsureWatcher(procs);
            if (muted || volume <= 0f) return 0;

            string sound = mode.Length > 0 ? mode : DefaultSound();
            if (sound == null) return 0;
            // Windows remembers this exe's own level too, so always set it (also back to 1.0).
            SetOwnSessionVolume(volume);
            if (string.Equals(Path.GetExtension(sound), ".wav", StringComparison.OrdinalIgnoreCase))
                using (var player = new SoundPlayer(sound)) player.PlaySync();
            else
                PlayWithMci(sound);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("claude-chime: " + e.Message);
            if (mode == "--watch") Log("crashed: " + e);
            return 1;
        }
    }

    // ==== chime sound ===============================================================

    // The installer puts the chosen chime next to the exe as chime.wav or chime.mp3.
    static string DefaultSound()
    {
        foreach (var name in new[] { "chime.wav", "chime.mp3", "cursor-done.wav" })
        {
            string p = Path.Combine(Dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // MP3 and other non-WAV files: MCI decodes them in this process, so they play in
    // the session whose level SetOwnSessionVolume just set.
    static void PlayWithMci(string file)
    {
        Mci("open \"" + file + "\" type mpegvideo alias claudechime");
        try { Mci("play claudechime wait"); }
        finally { mciSendString("close claudechime", null, 0, IntPtr.Zero); }
    }

    static void Mci(string command)
    {
        int err = mciSendString(command, null, 0, IntPtr.Zero);
        if (err == 0) return;
        var text = new StringBuilder(256);
        mciGetErrorString(err, text, text.Capacity);
        throw new Exception("MCI: " + text);
    }

    // ==== chime level ===============================================================

    static void ResolveChimeLevel(Procs procs, out float volume, out bool muted, out string source)
    {
        // 0. while the watcher runs it owns the Claude slider and writes its level down
        if (WatcherRunning() && TryReadLevelFile(procs, out volume, out muted))
        {
            source = "Claude slider (watcher, claude-level.txt)";
            return;
        }

        var enumerator = NewEnumerator();
        IMMDevice def;
        Check(enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out def));
        string defId = DeviceId(def);

        // 1. live session on the default device
        foreach (var s in SessionsOf(def))
        {
            if (!procs.IsDesktopApp(s.Pid) || s.State == 2) continue;
            volume = s.Volume; muted = s.Muted;
            source = "live Claude session on default device";
            return;
        }

        // 2. what the watcher last saw during this run of Claude
        if (TryReadLevelFile(procs, out volume, out muted))
        {
            source = "last level seen by the watcher (claude-level.txt)";
            return;
        }

        // 3. level Windows saved for Claude on the default device
        if (TrySavedLevel(def, procs, out volume, out muted))
        {
            source = "level Windows saved for Claude (registry)";
            return;
        }

        // 4. live session on another device
        foreach (var dev in ActiveDevices(enumerator))
        {
            if (DeviceId(dev) == defId) continue;
            foreach (var s in SessionsOf(dev))
            {
                if (!procs.IsDesktopApp(s.Pid) || s.State == 2) continue;
                volume = s.Volume; muted = s.Muted;
                source = "live Claude session on another device";
                return;
            }
        }

        volume = 1f; muted = false;
        source = procs.ClaudeRoot() == 0 ? "Claude app not running, default 100%" : "no Claude level found, default 100%";
    }

    static void SetOwnSessionVolume(float level)
    {
        IMMDevice def;
        Check(NewEnumerator().GetDefaultAudioEndpoint(0, 0, out def));
        ISimpleAudioVolume vol;
        Guid session = Guid.Empty, context = Guid.Empty;
        Check(SessionManager(def).GetSimpleAudioVolume(ref session, 0, out vol));
        Check(vol.SetMute(false, ref context));
        Check(vol.SetMasterVolume(Clamp(level), ref context));
    }

    // claude-level.txt: "<claude root pid> <root start time> <level> <muted>".
    // Only valid for the Claude run it was written in; after a restart Windows
    // restores Claude's slider from the registry instead.
    static bool TryReadLevelFile(Procs procs, out float volume, out bool muted)
    {
        volume = 1f; muted = false;
        try
        {
            uint root = procs.ClaudeRoot();
            if (root == 0 || !File.Exists(LevelFile)) return false;
            var f = File.ReadAllText(LevelFile).Trim().Split(' ');
            if (f.Length < 4) return false;
            if (uint.Parse(f[0]) != root || long.Parse(f[1]) != procs.StartTime(root)) return false;
            volume = float.Parse(f[2], CultureInfo.InvariantCulture);
            muted = f[3] == "1";
            return true;
        }
        catch { return false; }
    }

    static void WriteLevelFile(uint root, long rootStart, float volume, bool muted)
    {
        File.WriteAllText(LevelFile, string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:0.0000} {3}",
            root, rootStart, volume, muted ? 1 : 0));
    }

    // ==== watcher ===================================================================

    static bool WatcherRunning()
    {
        Mutex m;
        if (!Mutex.TryOpenExisting(WatcherMutex, out m)) return false;
        m.Dispose();
        return true;
    }

    static void EnsureWatcher(Procs procs)
    {
        if (procs.ClaudeRoot() == 0 || WatcherRunning()) return;
        string exe = Path.Combine(Dir, "claude-chime.exe");
        string cmd = "\"" + exe + "\" --watch";
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        PROCESS_INFORMATION pi;
        // Detached, no inherited handles (so the hook doesn't wait on our pipes), and
        // out of the hook's job object when allowed (so the hook ending can't kill it).
        const uint DETACHED_PROCESS = 0x8, CREATE_NEW_PROCESS_GROUP = 0x200, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        bool ok = CreateProcess(exe, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
            DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB, IntPtr.Zero, Dir, ref si, out pi);
        if (!ok)
            ok = CreateProcess(exe, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
                DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, IntPtr.Zero, Dir, ref si, out pi);
        if (ok) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
    }

    struct Target { public float Volume; public bool Muted; }

    // What the watcher decided about one audio-owning process. A process's exe and
    // parent never change, so this is kept while the pid lives; the start time
    // guards against the pid being reused by another process.
    class Verdict { public long Start; public bool Desktop, Descendant; public string Exe; public int Seen; }

    // Grouping GUID of the Claude slider: every session carrying it is drawn as one
    // slider in the Volume Mixer, and moving that slider sets all of them.
    static readonly Guid ClaudeGroup = new Guid("c1a0de00-5eed-4c1a-9d0e-c1a0dec1a0de");
    const string SliderName = "Claude";

    static int Watch()
    {
        bool createdNew;
        using (var mutex = new Mutex(true, WatcherMutex, out createdNew))
        {
            if (!createdNew) return 0;
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            string selfExe = System.Reflection.Assembly.GetEntryAssembly().Location;
            Log("started, pid " + self);

            var lastApplied = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); // exe -> level we set
            var checkedOthers = new HashSet<string>();                                         // "device|pid"
            var lastSet = new Dictionary<string, Target>();                                   // session instance id -> level we left it at
            var verdicts = new Dictionary<uint, Verdict>();
            var managers = new Dictionary<string, IAudioSessionManager2>();                   // device -> session manager
            var wake = new AutoResetEvent(false);                                               // set when any app opens a session
            var notifier = new SessionNotifier(wake);
            IMMDeviceEnumerator enumerator = null;
            StandIn standIn = null;
            float level = 1f; bool muted = false, haveLevel = false;
            string lastError = null, lastLevelLine = null, standInError = null;
            uint root = 0; long rootStart = 0; string rootExe = null;
            IntPtr rootHandle = IntPtr.Zero;
            int poll = 0;

            while (true)
            {
                try
                {
                    poll++;
                    if (checkedOthers.Count > 10000) checkedOthers.Clear();
                    Procs procs = null; // process table snapshot, only taken when needed

                    // Is the Claude app still running? Waiting on its handle costs nothing; a
                    // snapshot is only needed at start and after that process has exited.
                    if (rootHandle == IntPtr.Zero || WaitForSingleObject(rootHandle, 0) == 0 /*WAIT_OBJECT_0*/)
                    {
                        if (rootHandle != IntPtr.Zero) { CloseHandle(rootHandle); rootHandle = IntPtr.Zero; }
                        procs = new Procs();
                        uint r = procs.ClaudeRoot();
                        if (r == 0) { Log("Claude app not running, exiting"); return 0; }
                        if (r != root)
                        {
                            root = r; rootStart = procs.StartTime(r); rootExe = procs.Path(r);
                            haveLevel = false; lastLevelLine = null; lastSet.Clear();
                        }
                        rootHandle = OpenProcess(0x00100000 /*SYNCHRONIZE*/, false, r);
                    }

                    if (enumerator == null) enumerator = NewEnumerator();
                    IMMDevice def;
                    Check(enumerator.GetDefaultAudioEndpoint(0, 0, out def));
                    string defId = DeviceId(def);

                    // Our own silent stream on the default device keeps the Claude slider in
                    // the mixer while Claude itself is quiet. Reopen it when the default
                    // device changes or the stream was invalidated (device unplugged, an app
                    // took the device in exclusive mode, audio service restarted).
                    if (standIn != null && (standIn.DeviceId != defId || !standIn.Alive()))
                    {
                        standIn.Dispose(); standIn = null;
                    }
                    if (standIn == null)
                    {
                        // Can fail while an app holds the device in exclusive mode; the rest of
                        // the poll still syncs Claude's and the Terminal's sessions.
                        try { standIn = StandIn.Open(def, defId); standInError = null; }
                        catch (Exception e)
                        {
                            if (e.Message != standInError) Log("stand-in slider unavailable: " + e.Message);
                            standInError = e.Message;
                        }
                    }

                    // Every session of the Claude app, of what it started, and our own stand-in
                    // is a member of the Claude slider, on every output device.
                    var all = new List<Session>();
                    var members = new List<Session>();
                    var active = new HashSet<string>();
                    foreach (var dev in ActiveDevices(enumerator))
                    {
                        string devId = DeviceId(dev);
                        active.Add(devId);
                        IAudioSessionManager2 mgr;
                        if (!managers.TryGetValue(devId, out mgr))
                        {
                            mgr = SessionManager(dev);
                            // If this fails the once-a-second poll still catches new sessions.
                            int hr = mgr.RegisterSessionNotification(notifier);
                            if (hr != 0) Log(string.Format("session notifications unavailable (0x{0:X8}), polling only", hr));
                            managers[devId] = mgr;
                        }
                        foreach (var s in SessionsOf(mgr))
                        {
                            s.Device = devId;
                            s.V = GetVerdict(verdicts, s.Pid, poll, ref procs);
                            all.Add(s);
                            if (s.State == 2 /*expired*/) continue;
                            if (s.Pid == self || s.V.Desktop || s.V.Descendant)
                            {
                                s.Id = InstanceId(s.Control);
                                if (s.Id != null) members.Add(s);
                            }
                        }
                    }
                    // Own session first: it's the one the slider is named after.
                    members.Sort((a, b) => (a.Pid == self ? 0 : 1).CompareTo(b.Pid == self ? 0 : 1));

                    bool firstPoll = !haveLevel;
                    if (!haveLevel)
                    {
                        // Start from Claude's live slider, else what we last saw during this run
                        // of Claude, else the level Windows kept for our own stream.
                        Session live = null, mine = null;
                        foreach (var s in members)
                        {
                            if (s.Device != defId) continue;
                            if (s.V.Desktop && (live == null || (s.State == 1 && live.State != 1))) live = s;
                            if (s.Pid == self) mine = s;
                        }
                        string from;
                        if (live != null) { level = live.Volume; muted = live.Muted; from = "Claude's live session"; }
                        else if (TryReadLevelFile(procs ?? new Procs(), out level, out muted)) from = "claude-level.txt";
                        else if (mine != null) { level = mine.Volume; muted = mine.Muted; from = "own stream"; }
                        else { level = 1f; muted = false; from = "default"; }
                        haveLevel = true;
                        Log(string.Format(CultureInfo.InvariantCulture, "level {0:0.00}{1} from {2}", level, muted ? " muted" : "", from));
                    }
                    else
                    {
                        // A member that no longer has the level we left it at was moved by the
                        // user (the mixer moves the whole group; Settings > App volume moves one).
                        foreach (var s in members)
                        {
                            Target prev;
                            if (!lastSet.TryGetValue(s.Id, out prev)) continue;
                            if (Math.Abs(s.Volume - prev.Volume) > 0.004f || s.Muted != prev.Muted)
                            {
                                level = s.Volume; muted = s.Muted;
                                break;
                            }
                        }
                    }

                    var seen = new HashSet<string>();
                    foreach (var s in members)
                    {
                        Guid ctx = Guid.Empty;
                        // A new session that opened at another level (e.g. Claude after an update:
                        // Windows has no saved level for the new exe path, so it starts at 100%).
                        if (!firstPoll && !lastSet.ContainsKey(s.Id)
                            && (Math.Abs(s.Volume - level) > 0.004f || s.Muted != muted))
                            Log(string.Format(CultureInfo.InvariantCulture, "new {0} session (pid {1}) opened at {2:0.00}{3}, set to {4:0.00}",
                                s.V.Exe == null ? "?" : System.IO.Path.GetFileName(s.V.Exe), s.Pid, s.Volume, s.Muted ? " muted" : "", level));
                        if (s.Grouping != ClaudeGroup) { Guid g = ClaudeGroup; s.Control.SetGroupingParam(ref g, ref ctx); }
                        if (Math.Abs(s.Volume - level) > 0.004f) s.Volume_.SetMasterVolume(Clamp(level), ref ctx);
                        if (s.Muted != muted) s.Volume_.SetMute(muted, ref ctx);
                        // Claude's own sessions already say Claude; ours and the Terminal's are
                        // renamed so the group reads "Claude" whichever member the mixer asks.
                        if (!s.V.Desktop) Label(s.Control, rootExe);
                        Target t; t.Volume = level; t.Muted = muted;
                        lastSet[s.Id] = t;
                        seen.Add(s.Id);
                        if (s.V.Descendant && s.V.Exe != null && !string.Equals(s.V.Exe, selfExe, StringComparison.OrdinalIgnoreCase))
                            lastApplied[s.V.Exe] = level;
                    }
                    foreach (var id in new List<string>(lastSet.Keys))
                        if (!seen.Contains(id)) lastSet.Remove(id);

                    string line = string.Format(CultureInfo.InvariantCulture, "{0:0.0000} {1}", level, muted);
                    if (line != lastLevelLine)
                    {
                        WriteLevelFile(root, rootStart, level, muted);
                        if (lastLevelLine != null)
                            Log(string.Format(CultureInfo.InvariantCulture, "slider moved: {0:0.00}{1}", level, muted ? " muted" : ""));
                        lastLevelLine = line;
                    }

                    foreach (var s in all)
                    {
                        if (s.State == 2 || s.Id != null) continue;
                        string exe = s.V.Exe;
                        if (exe == null || string.Equals(exe, selfExe, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!checkedOthers.Add(s.Device + "|" + s.Pid)) continue;
                        // An unrelated process of an exe we adjusted may have inherited our
                        // level from Windows' per-exe memory: put it back to 100%.
                        float applied;
                        if (lastApplied.TryGetValue(exe, out applied) && applied < 0.996f
                            && Math.Abs(s.Volume - applied) < 0.004f)
                        {
                            Guid ctx = Guid.Empty;
                            s.Volume_.SetMasterVolume(1f, ref ctx);
                            Log("reset " + System.IO.Path.GetFileName(exe) + " pid " + s.Pid + " to 100% (inherited " + applied.ToString("0.00", CultureInfo.InvariantCulture) + ")");
                        }
                    }
                    ReleaseAll(all);

                    foreach (var id in new List<string>(managers.Keys))
                        if (!active.Contains(id)) { DropManager(managers[id], notifier); managers.Remove(id); }
                    foreach (var pid in new List<uint>(verdicts.Keys))
                        if (verdicts[pid].Seen != poll) verdicts.Remove(pid);
                    lastError = null;
                }
                catch (Exception e)
                {
                    if (e.Message != lastError) Log("error: " + e.Message);
                    lastError = e.Message;
                    // e.g. the Windows audio service restarted: start over with fresh objects.
                    foreach (var m in managers.Values) DropManager(m, notifier);
                    managers.Clear();
                    if (standIn != null) { standIn.Dispose(); standIn = null; }
                    enumerator = null;
                }
                // Poll again after a second, or right away when an app opens an audio session.
                wake.WaitOne(1000);
            }
        }
    }

    static void DropManager(IAudioSessionManager2 manager, SessionNotifier notifier)
    {
        try { manager.UnregisterSessionNotification(notifier); } catch { }
        try { Marshal.ReleaseComObject(manager); } catch { }
    }

    // Windows calls this on one of its own threads the moment any app opens an audio
    // session. It only wakes the watcher, which then polls at once, so a new Claude
    // session is pulled to the slider's level within milliseconds instead of at the
    // next once-a-second poll.
    class SessionNotifier : IAudioSessionNotification
    {
        readonly AutoResetEvent wake;
        public SessionNotifier(AutoResetEvent wake) { this.wake = wake; }

        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            if (newSession != null) Marshal.ReleaseComObject(newSession);
            wake.Set();
            return 0;
        }
    }

    // The watcher's own audio stream: opened on the default device and never started,
    // so it makes no sound and costs no CPU, but it owns an audio session, and with it
    // a Volume Mixer slider, for as long as the watcher runs.
    class StandIn : IDisposable
    {
        public string DeviceId;
        IAudioClient client;

        public static StandIn Open(IMMDevice device, string deviceId)
        {
            Guid iid = typeof(IAudioClient).GUID;
            object o;
            Check(device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o));
            var c = (IAudioClient)o;
            try
            {
                IntPtr format;
                Check(c.GetMixFormat(out format));
                try
                {
                    Guid session = Guid.Empty; // the process's default session, same as the chime's
                    Check(c.Initialize(0 /*AUDCLNT_SHAREMODE_SHARED*/, 0, 0, 0, format, ref session));
                }
                finally { Marshal.FreeCoTaskMem(format); }
            }
            catch { Marshal.FinalReleaseComObject(c); throw; }
            var s = new StandIn();
            s.DeviceId = deviceId;
            s.client = c;
            return s;
        }

        public bool Alive()
        {
            uint padding;
            return client.GetCurrentPadding(out padding) == 0;
        }

        public void Dispose()
        {
            try { Marshal.FinalReleaseComObject(client); } catch { }
        }
    }

    static void Label(IAudioSessionControl2 c, string iconExe)
    {
        Guid ctx = Guid.Empty;
        IntPtr p;
        if (c.GetDisplayName(out p) != 0 || TakeString(p) != SliderName) c.SetDisplayName(SliderName, ref ctx);
        if (iconExe == null) return;
        string icon = iconExe + ",0";
        if (c.GetIconPath(out p) != 0 || !string.Equals(TakeString(p), icon, StringComparison.OrdinalIgnoreCase))
            c.SetIconPath(icon, ref ctx);
    }

    static string InstanceId(IAudioSessionControl2 c)
    {
        IntPtr p;
        return c.GetSessionInstanceIdentifier(out p) == 0 ? TakeString(p) : null;
    }

    // A string the callee allocated with CoTaskMemAlloc: read it and free it.
    static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        string s = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return s;
    }

    static Verdict GetVerdict(Dictionary<uint, Verdict> verdicts, uint pid, int poll, ref Procs procs)
    {
        long start = Procs.QueryStart(pid);
        Verdict v;
        if (!verdicts.TryGetValue(pid, out v) || v.Start != start)
        {
            if (procs == null) procs = new Procs();
            v = new Verdict();
            v.Start = start;
            v.Desktop = procs.IsDesktopApp(pid);
            v.Descendant = !v.Desktop && procs.IsClaudeDescendant(pid);
            v.Exe = procs.Path(pid);
            verdicts[pid] = v;
        }
        v.Seen = poll;
        return v;
    }

    static void Log(string msg)
    {
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 100 * 1024)
                File.Delete(LogFile);
            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
        }
        catch { }
    }

    // ==== saved level (registry) ======================================================

    // Registry entries look like
    //   {2}.\\?\hdaudio#func_01&ven_xxxx&...#{6994ad04-...}\elineouttopo/00010001|\Device\HarddiskVolumeN\Program Files\...\claude.exe%b{00000000-...}
    // while the endpoint's device path is
    //   {2}.\\?\hdaudio#func_01&ven_xxxx&...#<instance>#{6994ad04-...}\elineoutwave
    // i.e. the same hardware id without the instance segment.
    static bool TrySavedLevel(IMMDevice device, Procs procs, out float volume, out bool muted)
    {
        volume = 1f; muted = false;
        var exeTails = new List<string>();
        foreach (var exe in procs.DesktopAppPaths())
            exeTails.Add((exe.Length > 2 && exe[1] == ':' ? exe.Substring(2) : exe) + "%b{00000000-0000-0000-0000-000000000000}");
        if (exeTails.Count == 0) return false;

        string wavePath = DeviceString(device, new PROPERTYKEY(new Guid("233164c8-1b2c-4c7d-bc68-b671687a2567"), 1));
        string topoPath = DeviceString(device, new PROPERTYKEY(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 11));
        if (wavePath == null) return false;
        string[] parts = wavePath.Split('#');
        if (parts.Length < 4) return false;
        string ifaceGuid = parts[3].Split('\\')[0];
        string devicePrefix = parts[0] + "#" + parts[1] + "#" + ifaceGuid + "\\";
        string topoFilter = topoPath != null ? topoPath.Substring(topoPath.LastIndexOf('\\') + 1) : null;

        string bestKey = null; bool bestTopoMatch = false;
        using (var root = Registry.CurrentUser.OpenSubKey(PolicyStore))
        {
            if (root == null) return false;
            foreach (var name in root.GetSubKeyNames())
            {
                using (var k = root.OpenSubKey(name))
                {
                    var id = k == null ? null : k.GetValue(null) as string;
                    if (id == null || !id.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    bool exeMatch = false;
                    foreach (var tail in exeTails)
                        if (id.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { exeMatch = true; break; }
                    if (!exeMatch) continue;
                    bool topoMatch = topoFilter != null
                        && id.IndexOf("\\" + topoFilter + "/", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (bestKey == null || (topoMatch && !bestTopoMatch)) { bestKey = name; bestTopoMatch = topoMatch; }
                }
            }
        }
        if (bestKey == null) return false;

        using (var k = Registry.CurrentUser.OpenSubKey(PolicyStore + "\\" + bestKey + "\\" + VolumeSubkey))
        {
            if (k == null) return false;
            var vol = k.GetValue("3") as byte[];
            var mute = k.GetValue("5") as byte[];
            if (vol == null || vol.Length < 12 || vol[0] != 4 /*VT_R4*/) return false;
            volume = BitConverter.ToSingle(vol, 8);
            muted = mute != null && mute.Length >= 10 && mute[0] == 11 /*VT_BOOL*/ && BitConverter.ToInt16(mute, 8) != 0;
            return true;
        }
    }

    // ==== processes =================================================================

    // One snapshot of the process table: parent links, paths and start times.
    // Paths and start times never change for a live process, so they are kept
    // across snapshots (the watcher takes one a second) and dropped once the pid is
    // gone or reused by a different exe.
    class Procs
    {
        static readonly Dictionary<uint, string> path = new Dictionary<uint, string>();
        static readonly Dictionary<uint, long> start = new Dictionary<uint, long>();
        static readonly Dictionary<uint, string> cachedName = new Dictionary<uint, string>();

        readonly Dictionary<uint, uint> parent = new Dictionary<uint, uint>();
        readonly Dictionary<uint, string> name = new Dictionary<uint, string>();

        public Procs()
        {
            TakeSnapshot();
            var stale = new List<uint>();
            foreach (var kv in cachedName)
            {
                string n;
                if (!name.TryGetValue(kv.Key, out n) || n != kv.Value) stale.Add(kv.Key);
            }
            foreach (var pid in stale) { cachedName.Remove(pid); path.Remove(pid); start.Remove(pid); }
        }

        void TakeSnapshot()
        {
            IntPtr snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == new IntPtr(-1)) throw new Win32Exception("CreateToolhelp32Snapshot");
            try
            {
                var e = new PROCESSENTRY32W();
                e.dwSize = (uint)Marshal.SizeOf(e);
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    parent[e.th32ProcessID] = e.th32ParentProcessID;
                    name[e.th32ProcessID] = e.szExeFile;
                }
            }
            finally { CloseHandle(snap); }
        }

        public string Path(uint pid)
        {
            string p;
            if (path.TryGetValue(pid, out p)) return p;
            long t;
            Query(pid, out p, out t);
            string n;
            // Only cache pids this snapshot knows, so the next snapshot can expire them.
            if (name.TryGetValue(pid, out n)) { path[pid] = p; start[pid] = t; cachedName[pid] = n; }
            return p;
        }

        public long StartTime(uint pid)
        {
            long t;
            if (start.TryGetValue(pid, out t)) return t;
            string p;
            Query(pid, out p, out t);
            string n;
            if (name.TryGetValue(pid, out n)) { path[pid] = p; start[pid] = t; cachedName[pid] = n; }
            return t;
        }

        // Start time only (0 if the process can't be opened), no snapshot needed.
        public static long QueryStart(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return 0;
            long c, x, k, u;
            long t = GetProcessTimes(h, out c, out x, out k, out u) ? c : 0;
            CloseHandle(h);
            return t;
        }

        static void Query(uint pid, out string p, out long startTime)
        {
            p = null; startTime = 0;
            IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size)) p = sb.ToString();
            long c, x, k, u;
            if (GetProcessTimes(h, out c, out x, out k, out u)) startTime = c;
            CloseHandle(h);
        }

        // Store build: ...\WindowsApps\Claude_<ver>_x64__...\app\Claude.exe
        // Classic build: ...\AnthropicClaude\app-<ver>\claude.exe
        // The Claude Code CLI (...\claude-code\<ver>\claude.exe) is not the app.
        public bool IsDesktopApp(uint pid)
        {
            string n;
            if (!name.TryGetValue(pid, out n) || !string.Equals(n, "claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
            string p = Path(pid);
            return p != null
                && p.IndexOf(@"\claude-code\", StringComparison.OrdinalIgnoreCase) < 0
                && (p.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0
                 || p.IndexOf(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Started (directly or indirectly) by the Claude desktop app. Parent links are
        // checked against start times so a reused pid can't fake an ancestor.
        public bool IsClaudeDescendant(uint pid)
        {
            uint cur = pid;
            for (int hops = 0; hops < 64; hops++)
            {
                uint pp;
                if (!parent.TryGetValue(cur, out pp) || pp == 0 || pp == cur || !parent.ContainsKey(pp)) return false;
                long ct = StartTime(cur), pt = StartTime(pp);
                if (ct == 0 || pt == 0 || pt > ct) return false;
                if (IsDesktopApp(pp)) return true;
                cur = pp;
            }
            return false;
        }

        // The main Claude desktop process (the app process whose parent isn't the app).
        public uint ClaudeRoot()
        {
            uint best = 0; long bestStart = long.MaxValue;
            foreach (var pid in parent.Keys)
            {
                if (!IsDesktopApp(pid)) continue;
                uint pp = parent[pid];
                if (parent.ContainsKey(pp) && IsDesktopApp(pp)) continue;
                long s = StartTime(pid);
                if (s < bestStart) { best = pid; bestStart = s; }
            }
            return best;
        }

        public List<string> DesktopAppPaths()
        {
            var result = new List<string>();
            foreach (var pid in parent.Keys)
                if (IsDesktopApp(pid) && !result.Contains(Path(pid))) result.Add(Path(pid));
            return result;
        }
    }

    // ==== audio sessions ==============================================================

    class Session
    {
        public IAudioSessionControl2 Control;
        public ISimpleAudioVolume Volume_;
        public uint Pid; public int State; public float Volume; public bool Muted; public Guid Grouping;
        public Verdict V;
        public string Device, Id; // Id (session instance id) is only filled in for Claude slider members
    }

    static List<Session> SessionsOf(IMMDevice device) { return SessionsOf(SessionManager(device)); }

    static List<Session> SessionsOf(IAudioSessionManager2 manager)
    {
        var result = new List<Session>();
        IAudioSessionEnumerator sessions;
        Check(manager.GetSessionEnumerator(out sessions));
        int count;
        Check(sessions.GetCount(out count));
        for (int i = 0; i < count; i++)
        {
            IAudioSessionControl ctl;
            if (sessions.GetSession(i, out ctl) != 0) continue;
            var s = new Session();
            s.Control = (IAudioSessionControl2)ctl;
            s.Volume_ = (ISimpleAudioVolume)ctl;
            s.Control.GetProcessId(out s.Pid);
            s.Control.GetState(out s.State);
            s.Control.GetGroupingParam(out s.Grouping);
            s.Volume_.GetMasterVolume(out s.Volume);
            s.Volume_.GetMute(out s.Muted);
            result.Add(s);
        }
        Marshal.ReleaseComObject(sessions);
        return result;
    }

    static void ReleaseAll(List<Session> sessions)
    {
        foreach (var s in sessions)
            if (s.Control != null && Marshal.IsComObject(s.Control)) Marshal.FinalReleaseComObject(s.Control);
    }

    static IMMDeviceEnumerator NewEnumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }

    static List<IMMDevice> ActiveDevices(IMMDeviceEnumerator enumerator)
    {
        var result = new List<IMMDevice>();
        IMMDeviceCollection devices;
        Check(enumerator.EnumAudioEndpoints(0, 1 /*DEVICE_STATE_ACTIVE*/, out devices));
        uint n;
        Check(devices.GetCount(out n));
        for (uint i = 0; i < n; i++)
        {
            IMMDevice dev;
            Check(devices.Item(i, out dev));
            result.Add(dev);
        }
        return result;
    }

    static IAudioSessionManager2 SessionManager(IMMDevice device)
    {
        Guid iid = typeof(IAudioSessionManager2).GUID;
        object o;
        Check(device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o));
        return (IAudioSessionManager2)o;
    }

    static string DeviceId(IMMDevice device)
    {
        IntPtr p;
        Check(device.GetId(out p));
        string s = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return s;
    }

    static string DeviceString(IMMDevice device, PROPERTYKEY key)
    {
        IPropertyStore store;
        if (device.OpenPropertyStore(0 /*STGM_READ*/, out store) != 0) return null;
        PROPVARIANT v;
        if (store.GetValue(ref key, out v) != 0) return null;
        if (v.vt != 31 /*VT_LPWSTR*/ || v.ptr == IntPtr.Zero) return null;
        string s = Marshal.PtrToStringUni(v.ptr);
        PropVariantClear(ref v);
        return s;
    }

    static float Clamp(float v) { return Math.Max(0f, Math.Min(1f, v)); }

    static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }

    class Win32Exception : Exception { public Win32Exception(string what) : base(what + " failed: " + Marshal.GetLastWin32Error()) { } }

    // ==== Win32 =======================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr procAttr, IntPtr threadAttr, bool inherit,
        uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT pvar);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendString(string command, StringBuilder result, int resultLength, IntPtr callback);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool mciGetErrorString(int error, StringBuilder text, int length);

    // ==== Core Audio COM interop ======================================================

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY
    {
        public Guid fmtid; public int pid;
        public PROPERTYKEY(Guid f, int p) { fmtid = f; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT { [FieldOffset(0)] public short vt; [FieldOffset(8)] public IntPtr ptr; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId(out IntPtr id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out ISimpleAudioVolume volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IAudioSessionControl newSession);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
                                     IntPtr format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid context);
        [PreserveSig] int GetMute(out bool mute);
    }
}
