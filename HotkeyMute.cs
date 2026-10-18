// Hotkey Mute - global hotkeys that mute/unmute individual programs.
//
// What it touches (and nothing else):
//   * Windows Core Audio session API (the same one the Volume Mixer uses) to set a program's mute flag.
//   * A passive WH_KEYBOARD_LL hook in THIS process to see hotkeys. No DLL is injected anywhere;
//     no other process is opened, read, written or hooked. It only reads process *names*.
// Written for the .NET Framework C# compiler (C# 5) that ships with Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Bump this for each release; it is shown in the window title and the exe properties.
[assembly: AssemblyVersion("1.4.1")]
[assembly: AssemblyTitle("Hotkey Mute")]

namespace HotkeyMute
{
    // ---------------------------------------------------------------- Core Audio COM

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr a, int b, out IntPtr c);
        int GetSimpleAudioVolume(IntPtr a, int b, out IntPtr c);
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int s);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string s, ref Guid g);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string s, ref Guid g);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, ref Guid c);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreferences(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid ctx);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid ctx);
        int GetMute(out bool mute);
    }

    class AudioSession
    {
        public string Name;
        public ISimpleAudioVolume Volume;
    }

    static class Audio
    {
        public static string Norm(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
            return s;
        }

        // All audio sessions on all active playback devices, tagged with their process name.
        public static List<AudioSession> Sessions()
        {
            var result = new List<AudioSession>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDeviceCollection devices;
                enumerator.EnumAudioEndpoints(0, 1, out devices); // render, active
                uint deviceCount; devices.GetCount(out deviceCount);
                for (uint d = 0; d < deviceCount; d++)
                {
                    try
                    {
                        IMMDevice device; devices.Item(d, out device);
                        Guid iid = typeof(IAudioSessionManager2).GUID; object o;
                        device.Activate(ref iid, 23, IntPtr.Zero, out o);
                        IAudioSessionEnumerator sessions;
                        ((IAudioSessionManager2)o).GetSessionEnumerator(out sessions);
                        int count; sessions.GetCount(out count);
                        for (int i = 0; i < count; i++)
                        {
                            try
                            {
                                IAudioSessionControl2 control; sessions.GetSession(i, out control);
                                uint pid; control.GetProcessId(out pid);
                                if (pid == 0) continue; // system sounds
                                string name;
                                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { continue; }
                                result.Add(new AudioSession { Name = name, Volume = (ISimpleAudioVolume)control });
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        public static List<AudioSession> For(string process)
        {
            string want = Norm(process);
            return Sessions().FindAll(delegate(AudioSession s) { return Norm(s.Name) == want; });
        }

        // true = now muted, false = now unmuted, null = program has no audio session right now.
        public static bool? Toggle(string process)
        {
            var list = For(process);
            if (list.Count == 0) return null;
            bool anyUnmuted = false;
            foreach (var s in list) { bool m; s.Volume.GetMute(out m); if (!m) anyUnmuted = true; }
            Guid ctx = Guid.Empty;
            foreach (var s in list) s.Volume.SetMute(anyUnmuted, ref ctx);
            return anyUnmuted;
        }

        // Mutes/unmutes every audio session (processes == null) or only those of the given programs.
        // Returns how many sessions were changed.
        public static int SetMuteAll(ICollection<string> processes, bool mute)
        {
            HashSet<string> want = null;
            if (processes != null)
            {
                want = new HashSet<string>();
                foreach (string p in processes) want.Add(Norm(p));
            }
            Guid ctx = Guid.Empty;
            int changed = 0;
            foreach (var s in Sessions())
            {
                if (want != null && !want.Contains(Norm(s.Name))) continue;
                s.Volume.SetMute(mute, ref ctx);
                changed++;
            }
            return changed;
        }

        public static string Status(string process)
        {
            var list = For(process);
            if (list.Count == 0) return "No audio session";
            foreach (var s in list) { bool m; s.Volume.GetMute(out m); if (!m) return "Unmuted"; }
            return "Muted";
        }
    }

    // ---------------------------------------------------------------- Model + storage

    class Binding
    {
        public string Process; // null for built-in actions
        public int Mods; // Alt=1 Ctrl=2 Shift=4 Win=8
        public int Vk;   // 0 = not set (only built-in actions can be unset)
        public string Action; // null for a program row; otherwise a BuiltIn id
    }

    // A named set of program hotkeys. Only the selected config is live.
    class Config
    {
        public string Name;
        public List<Binding> Items = new List<Binding>();
    }

    // Built-in actions: one hotkey each, shared by every config, unbound by default.
    static class BuiltIn
    {
        public static readonly string[] Ids = { "cfg-mute", "cfg-unmute", "sys-mute", "sys-unmute" };

        public static string Label(string id)
        {
            switch (id)
            {
                case "cfg-mute": return "Mute all in this config";
                case "cfg-unmute": return "Unmute all in this config";
                case "sys-mute": return "Mute everything (system-wide)";
                default: return "Unmute everything (system-wide)";
            }
        }

        public static string Scope(string id)
        {
            return id.StartsWith("cfg") ? "Programs in the selected config" : "All programs with audio";
        }
    }

    class AppData
    {
        public List<Config> Configs = new List<Config>();
        public List<Binding> Actions = new List<Binding>();
        public string Selected = "Default";
        public bool Notify = true;

        public Config Find(string name)
        {
            foreach (var c in Configs) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public Config GetOrAdd(string name)
        {
            Config c = Find(name);
            if (c == null) { c = new Config { Name = name }; Configs.Add(c); }
            return c;
        }
    }

    static class Hotkeys
    {
        public static string Format(int mods, int vk)
        {
            string s = "";
            if ((mods & 2) != 0) s += "Ctrl+";
            if ((mods & 4) != 0) s += "Shift+";
            if ((mods & 1) != 0) s += "Alt+";
            if ((mods & 8) != 0) s += "Win+";
            return s + ((Keys)vk).ToString();
        }
    }

    static class Store
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotkeyMute");
        static readonly string FilePath = Path.Combine(Dir, "settings.txt");

        // File format (plain text):
        //   notify=1
        //   selected=Default
        //   action:cfg-mute|mods|vk          (built-in action hotkeys, shared by all configs)
        //   [config:Default]
        //   firefox.exe|mods|vk              (program hotkeys of that config)
        // Program lines that appear before any [config:] header belong to Default, which is how
        // settings files from versions before 1.4 load unchanged.
        public static AppData Load()
        {
            var d = new AppData();
            foreach (string id in BuiltIn.Ids) d.Actions.Add(new Binding { Action = id });
            try
            {
                Config cur = null;
                if (File.Exists(FilePath))
                {
                    foreach (string raw in File.ReadAllLines(FilePath))
                    {
                        string line = raw.TrimEnd();
                        if (line.Length == 0) continue;
                        if (line.StartsWith("notify=")) { d.Notify = line.Substring(7) == "1"; continue; }
                        if (line.StartsWith("selected=")) { d.Selected = line.Substring(9); continue; }
                        if (line.StartsWith("[config:") && line.EndsWith("]"))
                        {
                            cur = d.GetOrAdd(line.Substring(8, line.Length - 9));
                            continue;
                        }
                        string[] p = line.Split('|');
                        int mods, vk;
                        if (p.Length != 3 || !int.TryParse(p[1], out mods) || !int.TryParse(p[2], out vk)) continue;
                        if (p[0].StartsWith("action:"))
                        {
                            string id = p[0].Substring(7);
                            foreach (var a in d.Actions) if (a.Action == id) { a.Mods = mods; a.Vk = vk; }
                            continue;
                        }
                        if (cur == null) cur = d.GetOrAdd("Default");
                        cur.Items.Add(new Binding { Process = p[0], Mods = mods, Vk = vk });
                    }
                }
            }
            catch { }
            Config def = d.GetOrAdd("Default");
            d.Configs.Remove(def); d.Configs.Insert(0, def); // Default is always first
            if (d.Find(d.Selected) == null) d.Selected = "Default";
            return d;
        }

        public static void Save(AppData d)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                lines.Add("notify=" + (d.Notify ? "1" : "0"));
                lines.Add("selected=" + d.Selected);
                foreach (var a in d.Actions) lines.Add("action:" + a.Action + "|" + a.Mods + "|" + a.Vk);
                foreach (var c in d.Configs)
                {
                    lines.Add("[config:" + c.Name + "]");
                    foreach (var b in c.Items) lines.Add(b.Process + "|" + b.Mods + "|" + b.Vk);
                }
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }

        // One config as a standalone file for sharing/backup.
        public static void Export(Config c, string path)
        {
            var lines = new List<string>();
            lines.Add("hotkeymute-config=1");
            lines.Add("name=" + c.Name);
            foreach (var b in c.Items) lines.Add(b.Process + "|" + b.Mods + "|" + b.Vk);
            File.WriteAllLines(path, lines.ToArray());
        }

        // Returns null if the file is not a Hotkey Mute config.
        public static Config Import(string path)
        {
            string[] all = File.ReadAllLines(path);
            if (all.Length == 0 || all[0].Trim() != "hotkeymute-config=1") return null;
            var c = new Config { Name = Path.GetFileNameWithoutExtension(path) };
            foreach (string raw in all)
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("name=")) { if (line.Length > 5) c.Name = line.Substring(5); continue; }
                string[] p = line.Split('|');
                int mods, vk;
                if (p.Length == 3 && int.TryParse(p[1], out mods) && int.TryParse(p[2], out vk) && vk != 0)
                    c.Items.Add(new Binding { Process = p[0], Mods = mods, Vk = vk });
            }
            return c;
        }
    }

    // ---------------------------------------------------------------- Name prompt

    class NameDialog : Form
    {
        public string Value;
        TextBox tb = new TextBox();

        public NameDialog(string title, string label, string initial, Func<string, string> validate)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(340, 100);
            var l = new Label { Text = label, Left = 12, Top = 10, Width = 316, Height = 18 };
            tb.Left = 12; tb.Top = 30; tb.Width = 316; tb.Text = initial;
            var ok = new Button { Text = "OK", Left = 172, Top = 64, Width = 75 };
            var cancel = new Button { Text = "Cancel", Left = 253, Top = 64, Width = 75 };
            ok.Click += delegate
            {
                string name = tb.Text.Trim();
                string err = validate(name);
                if (err != null) { MessageBox.Show(this, err, Text); return; }
                Value = name;
                DialogResult = DialogResult.OK;
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { l, tb, ok, cancel });
            Shown += delegate { tb.Focus(); tb.SelectAll(); };
        }
    }

    // ---------------------------------------------------------------- Add dialog

    class AddDialog : Form
    {
        public string ProcessName;
        public int Mods, Vk;
        ComboBox cb = new ComboBox();
        TextBox tbKey = new TextBox();

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        public AddDialog() : this(null) { }

        public AddDialog(Binding existing)
        {
            bool isAction = existing != null && existing.Action != null;
            Text = existing == null ? "Add hotkey" : "Edit hotkey";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 150);

            var l1 = new Label { Text = "Program (pick one that is playing audio, or type e.g. game.exe):", Left = 12, Top = 10, Width = 360, Height = 18 };
            cb.Left = 12; cb.Top = 30; cb.Width = 290; cb.DropDownStyle = ComboBoxStyle.DropDown;
            var refresh = new Button { Text = "Refresh", Left = 308, Top = 29, Width = 62 };
            refresh.Click += delegate { FillProcesses(); };

            var l2 = new Label { Text = "Hotkey (click the box, then press the keys):", Left = 12, Top = 62, Width = 360, Height = 18 };
            tbKey.Left = 12; tbKey.Top = 82; tbKey.Width = 358; tbKey.ReadOnly = true; tbKey.BackColor = SystemColors.Window;
            tbKey.PreviewKeyDown += delegate(object s, PreviewKeyDownEventArgs e) { e.IsInputKey = true; };
            tbKey.KeyDown += OnKey;

            var ok = new Button { Text = "OK", Left = 214, Top = 114, Width = 75 };
            var cancel = new Button { Text = "Cancel", Left = 295, Top = 114, Width = 75 };
            ok.Click += delegate
            {
                if (!isAction && Audio.Norm(cb.Text).Length == 0) { MessageBox.Show(this, "Enter a program name.", "Add hotkey"); return; }
                if (Vk == 0) { MessageBox.Show(this, "Press a hotkey in the hotkey box.", "Add hotkey"); return; }
                ProcessName = isAction ? null : cb.Text.Trim();
                DialogResult = DialogResult.OK;
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };

            Controls.AddRange(new Control[] { l1, cb, refresh, l2, tbKey, ok, cancel });
            if (isAction)
            {
                Text = "Set hotkey";
                l1.Text = "Built-in: " + BuiltIn.Label(existing.Action) + " (all configs)";
                cb.Visible = false; refresh.Visible = false;
            }
            else FillProcesses();
            if (existing != null)
            {
                if (!isAction) cb.Text = existing.Process;
                Mods = existing.Mods; Vk = existing.Vk;
                if (Vk != 0) tbKey.Text = Hotkeys.Format(Mods, Vk);
                Shown += delegate { tbKey.Focus(); };
            }
        }

        void FillProcesses()
        {
            string keep = cb.Text;
            cb.Items.Clear();
            var seen = new List<string>();
            foreach (var s in Audio.Sessions())
            {
                string n = s.Name + ".exe";
                if (!seen.Contains(n)) seen.Add(n);
            }
            seen.Sort();
            foreach (string n in seen) cb.Items.Add(n);
            cb.Text = keep;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            e.Handled = true; e.SuppressKeyPress = true;
            Keys k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin ||
                k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LShiftKey || k == Keys.RShiftKey ||
                k == Keys.LMenu || k == Keys.RMenu) return;
            int mods = 0;
            if (e.Alt) mods |= 1;
            if (e.Control) mods |= 2;
            if (e.Shift) mods |= 4;
            if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) mods |= 8;
            Mods = mods; Vk = (int)k;
            tbKey.Text = Hotkeys.Format(Mods, Vk);
        }
    }

    // ---------------------------------------------------------------- Main window

    class MainForm : Form
    {
        // Passive low-level keyboard hook: observes keys in this process's hook chain only.
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "HotkeyMute";

        AppData data;
        Config current;
        List<Binding> bindings; // the selected config's program hotkeys
        bool notify = true;
        // Read by the audio worker (not the UI thread) when a built-in action fires.
        volatile string[] cfgProcesses = new string[0];
        volatile string cfgName = "Default";
        bool exiting, allowShow;
        volatile bool capturing;
        // The hook lives on its own thread with its own message loop, so slow work on the UI thread
        // (audio lookups, dialogs) can never make Windows time out and silently drop it.
        // Everything the hook thread reads is either locked (held) or an immutable snapshot (snapshot).
        readonly HashSet<int> held = new HashSet<int>();
        volatile Binding[] snapshot = new Binding[0];
        HookProc hookProc;
        Thread hookThread;
        uint hookThreadId;
        Label hint;
        System.Windows.Forms.Timer hintTimer = new System.Windows.Forms.Timer();

        ListView lv = new ListView();
        ListViewGroup gProgs = new ListViewGroup("Programs");
        ListViewGroup gBuiltIn = new ListViewGroup("Built-in actions (shared by all configs, unset by default)");
        ComboBox cbConfig = new ComboBox();
        Button btnRename, btnDelete;
        bool loadingCombo;
        CheckBox cbNotify = new CheckBox();
        CheckBox cbStartup = new CheckBox();
        NotifyIcon tray = new NotifyIcon();
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public MainForm(bool startHidden)
        {
            allowShow = !startHidden;
            Text = "Hotkey Mute v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            ClientSize = new Size(640, 470);
            MinimumSize = new Size(600, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;

            data = Store.Load();
            notify = data.Notify;
            current = data.Find(data.Selected);
            bindings = current.Items;

            // Top: actions on the program list.
            var add = new Button { Text = "Add...", Left = 12, Top = 12, Width = 80 };
            var remove = new Button { Text = "Remove", Left = 98, Top = 12, Width = 80 };
            var toggle = new Button { Text = "Toggle now", Left = 184, Top = 12, Width = 90 };
            add.Click += delegate { EditBinding(null); };
            remove.Click += delegate { RemoveSelected(); };
            toggle.Click += delegate { ToggleSelected(); };
            new ToolTip().SetToolTip(remove, "Removes a program from this config. On a built-in action it just clears the hotkey.");

            lv.View = View.Details; lv.ShowItemToolTips = true; lv.FullRowSelect = true; lv.MultiSelect = false; lv.HideSelection = false;
            lv.ShowGroups = true; lv.Groups.Add(gProgs); lv.Groups.Add(gBuiltIn);
            lv.Columns.Add("Program / action", 230); lv.Columns.Add("Hotkey", 160); lv.Columns.Add("Status", 210);
            lv.Left = 12; lv.Top = 44; lv.Width = ClientSize.Width - 24; lv.Height = ClientSize.Height - 210;
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lv.DoubleClick += delegate { EditBinding(Selected()); };

            // Bottom, first box: config management.
            var gbConfig = new GroupBox { Text = "Config management", Left = 12, Top = ClientSize.Height - 158, Width = ClientSize.Width - 24, Height = 58,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var lblCfg = new Label { Text = "Config:", AutoSize = true, Left = 10, Top = 27 };
            cbConfig.DropDownStyle = ComboBoxStyle.DropDownList; cbConfig.Left = 58; cbConfig.Top = 23; cbConfig.Width = 140;
            cbConfig.SelectedIndexChanged += delegate
            {
                if (loadingCombo) return;
                Config c = data.Find(cbConfig.SelectedItem as string);
                if (c != null && c != current) SwitchConfig(c);
            };
            var btnNew = new Button { Text = "New", Left = 206, Top = 22, Width = 52 };
            btnRename = new Button { Text = "Rename", Left = 262, Top = 22, Width = 62 };
            var btnDup = new Button { Text = "Duplicate", Left = 328, Top = 22, Width = 70 };
            btnDelete = new Button { Text = "Delete", Left = 402, Top = 22, Width = 52 };
            var btnExport = new Button { Text = "Export...", Left = 458, Top = 22, Width = 70 };
            var btnImport = new Button { Text = "Import...", Left = 532, Top = 22, Width = 70 };
            btnNew.Click += delegate { NewConfig(); };
            btnRename.Click += delegate { RenameConfig(); };
            btnDup.Click += delegate { DuplicateConfig(); };
            btnDelete.Click += delegate { DeleteConfig(); };
            btnExport.Click += delegate { ExportConfig(); };
            btnImport.Click += delegate { ImportConfig(); };
            new ToolTip().SetToolTip(cbConfig, "Only the selected config's hotkeys are active. Switching does not mute or unmute anything.");
            gbConfig.Controls.AddRange(new Control[] { lblCfg, cbConfig, btnNew, btnRename, btnDup, btnDelete, btnExport, btnImport });

            // Bottom, second box: everything else.
            var gbMisc = new GroupBox { Text = "Misc", Left = 12, Top = ClientSize.Height - 92, Width = ClientSize.Width - 24, Height = 80,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var restart = new Button { Text = "Reset keyboard listener", Left = 10, Top = 20, Width = 150 };
            restart.Click += delegate { RestartHook(); };
            new ToolTip().SetToolTip(restart, "If your hotkeys stop responding, click this to reload the keyboard listener.\nYour settings are not changed.");
            hint = new Label { Text = "Closing the window hides it to the tray.", AutoSize = true, ForeColor = SystemColors.GrayText, Left = 168, Top = 25 };
            hintTimer.Interval = 3000;
            hintTimer.Tick += delegate { hintTimer.Stop(); hint.Text = "Closing the window hides it to the tray."; };

            cbNotify.Text = "Show notifications"; cbNotify.Checked = notify; cbNotify.AutoSize = true; cbNotify.Left = 10; cbNotify.Top = 50;
            cbNotify.CheckedChanged += delegate { notify = cbNotify.Checked; Save(); };
            cbStartup.Text = "Start with Windows (in tray)"; cbStartup.AutoSize = true; cbStartup.Left = 170; cbStartup.Top = 50;
            cbStartup.Checked = GetStartup();
            cbStartup.CheckedChanged += delegate { SetStartup(cbStartup.Checked); };
            gbMisc.Controls.AddRange(new Control[] { restart, hint, cbNotify, cbStartup });

            Controls.AddRange(new Control[] { add, remove, toggle, lv, gbConfig, gbMisc });
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { ShowWindow(); });
            menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            tray.Icon = appIcon; tray.Text = "Hotkey Mute"; tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };

            timer.Interval = 1500;
            timer.Tick += delegate { if (Visible) RefreshStatus(); };
            timer.Start();

            FillConfigCombo();
            Reload();

            hookProc = HookCb;
            if (!StartHook()) MessageBox.Show("Could not start keyboard monitoring; hotkeys will not work.", "Hotkey Mute");
        }

        bool StartHook()
        {
            var ready = new ManualResetEvent(false);
            bool ok = false;
            var t = new Thread(delegate()
            {
                IntPtr h = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
                hookThreadId = GetCurrentThreadId();
                ok = h != IntPtr.Zero;
                MSG m;
                PeekMessage(out m, IntPtr.Zero, 0, 0, 0); // creates this thread's message queue before we signal
                ready.Set();
                if (!ok) return;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { } // runs until WM_QUIT
                UnhookWindowsHookEx(h);
            });
            t.IsBackground = true; t.Name = "Hotkey Mute keyboard hook";
            t.Start();
            ready.WaitOne();
            if (ok) hookThread = t;
            return ok;
        }

        void StopHook()
        {
            Thread t = hookThread;
            if (t == null) return;
            PostThreadMessage(hookThreadId, 0x12, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
            t.Join(1000);
            hookThread = null;
        }

        // Manual recovery: tear the hook down and install a fresh one. Only ever runs when the user asks.
        void RestartHook()
        {
            StopHook();
            lock (held) { held.Clear(); }
            hint.Text = StartHook() ? "Listener reset. Try it now." : "Could not reset the listener.";
            hintTimer.Stop(); hintTimer.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow) { value = false; if (!IsHandleCreated) CreateHandle(); }
            base.SetVisibleCore(value);
        }

        void ShowWindow()
        {
            allowShow = true; Show(); WindowState = FormWindowState.Normal; Activate(); RefreshStatus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            StopHook();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------ hotkey handling

        // Runs on the hook thread: must stay tiny. No COM, no UI.
        IntPtr HookCb(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int msg = (int)w.ToInt64();
                int vk = Marshal.ReadInt32(l);
                if (msg == 0x100 || msg == 0x104) // key down / sys key down
                {
                    bool first;
                    lock (held) { first = held.Add(vk); }
                    if (first && !capturing)
                    {
                        int mods = CurrentMods();
                        foreach (var b in snapshot)
                            if (b.Vk == vk && b.Mods == mods) Fire(b);
                    }
                }
                else if (msg == 0x101 || msg == 0x105) { lock (held) { held.Remove(vk); } }
            }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        static int CurrentMods()
        {
            int m = 0;
            if ((GetAsyncKeyState(0x12) & 0x8000) != 0) m |= 1;
            if ((GetAsyncKeyState(0x11) & 0x8000) != 0) m |= 2;
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0) m |= 4;
            if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) m |= 8;
            return m;
        }

        // Audio work happens on a thread-pool thread, never on the hook or UI thread.
        void Fire(Binding b)
        {
            ThreadPool.QueueUserWorkItem(delegate { FireWork(b); });
        }

        void FireWork(Binding b)
        {
            string who, text;
            try
            {
                if (b.Action == null)
                {
                    who = b.Process;
                    bool? r = Audio.Toggle(who);
                    text = r == null ? "no audio session found (is it playing sound?)" : (r.Value ? "muted" : "unmuted");
                }
                else
                {
                    bool mute = !b.Action.EndsWith("-unmute");
                    bool system = b.Action.StartsWith("sys");
                    int n = Audio.SetMuteAll(system ? null : cfgProcesses, mute);
                    who = system ? "Everything" : "Config " + cfgName;
                    text = (mute ? "muted" : "unmuted") + " (" + n + (n == 1 ? " stream)" : " streams)");
                }
            }
            catch (Exception ex) { who = b.Process ?? BuiltIn.Label(b.Action); text = "Error: " + ex.Message; }
            try { BeginInvoke(new Action(delegate { Notify(who, text); if (Visible) RefreshStatus(); })); }
            catch { } // window already closed
        }

        void Notify(string who, string what)
        {
            if (notify) tray.ShowBalloonTip(1000, "Hotkey Mute", who + ": " + what, ToolTipIcon.None);
        }

        // ------------------------------------------------------------ list UI

        void Reload()
        {
            var names = new List<string>();
            foreach (var b in bindings) names.Add(b.Process);
            cfgProcesses = names.ToArray();
            cfgName = current.Name;

            // Everything that is live right now: the selected config's programs plus any bound built-in actions.
            var active = new List<Binding>(bindings);
            foreach (var a in data.Actions) if (a.Vk != 0) active.Add(a);
            snapshot = active.ToArray();

            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var b in bindings) lv.Items.Add(MakeItem(b, active, gProgs));
            foreach (var a in data.Actions) lv.Items.Add(MakeItem(a, active, gBuiltIn));
            lv.EndUpdate();
            RefreshStatus();
        }

        ListViewItem MakeItem(Binding b, List<Binding> active, ListViewGroup group)
        {
            bool isAction = b.Action != null;
            var item = new ListViewItem(isAction ? BuiltIn.Label(b.Action) : b.Process, group);
            string keyText = b.Vk == 0 ? "(not set)" : Hotkeys.Format(b.Mods, b.Vk);
            if (isAction) item.ToolTipText = BuiltIn.Scope(b.Action) + ". Double-click to set the hotkey.";
            if (b.Vk != 0)
            {
                // Several rows may share a hotkey on purpose; flag it so it is never a surprise.
                var others = new List<string>();
                foreach (var o in active)
                    if (o != b && o.Mods == b.Mods && o.Vk == b.Vk) others.Add(o.Process ?? BuiltIn.Label(o.Action));
                if (others.Count > 0)
                {
                    keyText = "\u26A0 " + keyText;
                    item.ToolTipText = "Shared hotkey - also triggers: " + string.Join(", ", others.ToArray());
                }
            }
            item.SubItems.Add(keyText);
            item.SubItems.Add(isAction ? BuiltIn.Scope(b.Action) : "");
            item.Tag = b;
            return item;
        }

        void RefreshStatus()
        {
            var all = Audio.Sessions();
            foreach (ListViewItem item in lv.Items)
            {
                if (((Binding)item.Tag).Action != null) continue;
                string want = Audio.Norm(((Binding)item.Tag).Process);
                string status = "No audio session"; bool found = false, anyUnmuted = false;
                foreach (var s in all)
                {
                    if (Audio.Norm(s.Name) != want) continue;
                    found = true; bool m; s.Volume.GetMute(out m); if (!m) anyUnmuted = true;
                }
                if (found) status = anyUnmuted ? "Unmuted" : "Muted";
                if (item.SubItems[2].Text != status) item.SubItems[2].Text = status;
            }
        }

        Binding Selected()
        {
            return lv.SelectedItems.Count == 0 ? null : (Binding)lv.SelectedItems[0].Tag;
        }

        // existing == null adds a new binding; otherwise edits that one in place.
        void EditBinding(Binding existing)
        {
            RunModal(delegate
            {
                using (var dlg = new AddDialog(existing))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (existing == null) bindings.Add(new Binding { Process = dlg.ProcessName, Mods = dlg.Mods, Vk = dlg.Vk });
                    else
                    {
                        if (existing.Action == null) existing.Process = dlg.ProcessName;
                        existing.Mods = dlg.Mods; existing.Vk = dlg.Vk;
                    }
                    Save(); Reload();
                }
            });
        }

        // Hotkeys must not fire while a dialog is open (the user may be typing or picking a key).
        void RunModal(Action show)
        {
            capturing = true;
            try { show(); }
            finally { capturing = false; lock (held) { held.Clear(); } }
        }

        void RemoveSelected()
        {
            Binding b = Selected();
            if (b == null) return;
            if (b.Action != null) { b.Mods = 0; b.Vk = 0; } // built-in actions stay in the list, just unset
            else bindings.Remove(b);
            Save(); Reload();
        }

        void ToggleSelected()
        {
            Binding b = Selected();
            if (b != null) Fire(b);
        }

        void Save() { data.Notify = notify; data.Selected = current.Name; Store.Save(data); }

        // ------------------------------------------------------------ configs

        static bool IsDefault(Config c) { return string.Equals(c.Name, "Default", StringComparison.OrdinalIgnoreCase); }

        void FillConfigCombo()
        {
            loadingCombo = true;
            cbConfig.Items.Clear();
            foreach (var c in data.Configs) cbConfig.Items.Add(c.Name);
            cbConfig.SelectedItem = current.Name;
            loadingCombo = false;
            btnRename.Enabled = btnDelete.Enabled = !IsDefault(current); // Default can be neither renamed nor deleted
        }

        void SwitchConfig(Config c)
        {
            current = c; bindings = c.Items;
            Save(); FillConfigCombo(); Reload();
        }

        string ValidateName(string name, Config except)
        {
            if (name.Length == 0) return "Enter a name.";
            if (name.IndexOfAny(new char[] { '[', ']', '|', '\r', '\n' }) >= 0) return "Names cannot contain [ ] | or line breaks.";
            foreach (var c in data.Configs)
                if (c != except && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return "A config with that name already exists.";
            return null;
        }

        string AskName(string title, string label, string initial, Config except)
        {
            string result = null;
            RunModal(delegate
            {
                using (var dlg = new NameDialog(title, label, initial, delegate(string n) { return ValidateName(n, except); }))
                    if (dlg.ShowDialog(this) == DialogResult.OK) result = dlg.Value;
            });
            return result;
        }

        void NewConfig()
        {
            string name = AskName("New config", "Name for the new config:", "", null);
            if (name == null) return;
            var c = new Config { Name = name };
            data.Configs.Add(c);
            SwitchConfig(c);
        }

        void RenameConfig()
        {
            if (IsDefault(current)) return;
            string name = AskName("Rename config", "New name:", current.Name, current);
            if (name == null) return;
            current.Name = name;
            Save(); FillConfigCombo(); Reload();
        }

        void DuplicateConfig()
        {
            string name = AskName("Duplicate config", "Name for the copy:", current.Name + " copy", null);
            if (name == null) return;
            var c = new Config { Name = name };
            foreach (var b in current.Items) c.Items.Add(new Binding { Process = b.Process, Mods = b.Mods, Vk = b.Vk });
            data.Configs.Add(c);
            SwitchConfig(c);
        }

        void DeleteConfig()
        {
            if (IsDefault(current)) return;
            DialogResult r = DialogResult.No;
            RunModal(delegate
            {
                r = MessageBox.Show(this, "Delete config \"" + current.Name + "\" and its " + current.Items.Count + " hotkey(s)?",
                    "Delete config", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            });
            if (r != DialogResult.Yes) return;
            data.Configs.Remove(current);
            SwitchConfig(data.Find("Default"));
        }

        void ExportConfig()
        {
            RunModal(delegate
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "Export config";
                    dlg.Filter = "Hotkey Mute config (*.hmcfg)|*.hmcfg";
                    dlg.FileName = current.Name + ".hmcfg";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try { Store.Export(current, dlg.FileName); }
                    catch (Exception ex) { MessageBox.Show(this, "Could not export: " + ex.Message, "Export config"); }
                }
            });
        }

        void ImportConfig()
        {
            RunModal(delegate
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Import config";
                    dlg.Filter = "Hotkey Mute config (*.hmcfg)|*.hmcfg|All files (*.*)|*.*";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    Config c;
                    try { c = Store.Import(dlg.FileName); }
                    catch (Exception ex) { MessageBox.Show(this, "Could not read the file: " + ex.Message, "Import config"); return; }
                    if (c == null) { MessageBox.Show(this, "That is not a Hotkey Mute config file.", "Import config"); return; }

                    // Never overwrite: pick a free name.
                    string baseName = c.Name.Trim();
                    foreach (char bad in new char[] { '[', ']', '|', '\r', '\n' }) baseName = baseName.Replace(bad, '_');
                    if (baseName.Length == 0) baseName = "Imported";
                    string name = baseName;
                    for (int i = 2; ValidateName(name, null) != null; i++) name = baseName + " (" + i + ")";
                    c.Name = name;
                    data.Configs.Add(c);
                    SwitchConfig(c);
                }
            });
        }

        // ------------------------------------------------------------ start with Windows

        static bool GetStartup()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" /tray");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "HotkeyMute.SingleInstance", out createdNew))
            {
                if (!createdNew) { MessageBox.Show("Hotkey Mute is already running (check the system tray).", "Hotkey Mute"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool hidden = Array.IndexOf(args, "/tray") >= 0;
                Application.Run(new MainForm(hidden));
            }
        }
    }
}
