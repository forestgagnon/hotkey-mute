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
[assembly: AssemblyVersion("1.2.1")]
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
        public string Process;
        public int Mods; // Alt=1 Ctrl=2 Shift=4 Win=8
        public int Vk;
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

        public static void Load(List<Binding> bindings, out bool notify)
        {
            notify = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (line.StartsWith("notify=")) { notify = line.Substring(7) == "1"; continue; }
                    string[] p = line.Split('|');
                    int mods, vk;
                    if (p.Length == 3 && int.TryParse(p[1], out mods) && int.TryParse(p[2], out vk))
                        bindings.Add(new Binding { Process = p[0], Mods = mods, Vk = vk });
                }
            }
            catch { }
        }

        public static void Save(List<Binding> bindings, bool notify)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                lines.Add("notify=" + (notify ? "1" : "0"));
                foreach (var b in bindings) lines.Add(b.Process + "|" + b.Mods + "|" + b.Vk);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
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
                if (Audio.Norm(cb.Text).Length == 0) { MessageBox.Show(this, "Enter a program name.", "Add hotkey"); return; }
                if (Vk == 0) { MessageBox.Show(this, "Press a hotkey in the hotkey box.", "Add hotkey"); return; }
                ProcessName = cb.Text.Trim();
                DialogResult = DialogResult.OK;
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };

            Controls.AddRange(new Control[] { l1, cb, refresh, l2, tbKey, ok, cancel });
            FillProcesses();
            if (existing != null)
            {
                cb.Text = existing.Process;
                Mods = existing.Mods; Vk = existing.Vk;
                tbKey.Text = Hotkeys.Format(Mods, Vk);
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

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "HotkeyMute";

        List<Binding> bindings = new List<Binding>();
        bool notify = true;
        bool exiting, allowShow, capturing;
        HashSet<int> held = new HashSet<int>();
        HookProc hookProc;
        IntPtr hook = IntPtr.Zero;

        ListView lv = new ListView();
        CheckBox cbNotify = new CheckBox();
        CheckBox cbStartup = new CheckBox();
        NotifyIcon tray = new NotifyIcon();
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public MainForm(bool startHidden)
        {
            allowShow = !startHidden;
            Text = "Hotkey Mute v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            ClientSize = new Size(560, 320);
            MinimumSize = new Size(480, 280);
            StartPosition = FormStartPosition.CenterScreen;
            Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;

            bool n; Store.Load(bindings, out n); notify = n;

            lv.View = View.Details; lv.ShowItemToolTips = true; lv.FullRowSelect = true; lv.MultiSelect = false; lv.HideSelection = false;
            lv.Columns.Add("Program", 190); lv.Columns.Add("Hotkey", 170); lv.Columns.Add("Status", 160);
            lv.Left = 12; lv.Top = 12; lv.Width = ClientSize.Width - 24; lv.Height = ClientSize.Height - 90;
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lv.DoubleClick += delegate { EditBinding(Selected()); };

            var add = new Button { Text = "Add...", Left = 12, Top = ClientSize.Height - 68, Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var remove = new Button { Text = "Remove", Left = 98, Top = ClientSize.Height - 68, Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var toggle = new Button { Text = "Toggle now", Left = 184, Top = ClientSize.Height - 68, Width = 90, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            add.Click += delegate { EditBinding(null); };
            remove.Click += delegate { RemoveSelected(); };
            toggle.Click += delegate { ToggleSelected(); };

            cbNotify.Text = "Show notifications"; cbNotify.Checked = notify; cbNotify.AutoSize = true;
            cbNotify.Left = 12; cbNotify.Top = ClientSize.Height - 34; cbNotify.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            cbNotify.CheckedChanged += delegate { notify = cbNotify.Checked; Save(); };

            cbStartup.Text = "Start with Windows (in tray)"; cbStartup.AutoSize = true;
            cbStartup.Left = 170; cbStartup.Top = ClientSize.Height - 34; cbStartup.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            cbStartup.Checked = GetStartup();
            cbStartup.CheckedChanged += delegate { SetStartup(cbStartup.Checked); };

            var hint = new Label { Text = "Closing the window keeps it running in the tray.", AutoSize = true, ForeColor = SystemColors.GrayText,
                Left = 300, Top = ClientSize.Height - 62, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            Controls.AddRange(new Control[] { lv, add, remove, toggle, cbNotify, cbStartup, hint });

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { ShowWindow(); });
            menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            tray.Icon = appIcon; tray.Text = "Hotkey Mute"; tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };

            timer.Interval = 1500;
            timer.Tick += delegate { if (Visible) RefreshStatus(); };
            timer.Start();

            Reload();

            hookProc = HookCb;
            hook = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) MessageBox.Show("Could not start keyboard monitoring; hotkeys will not work.", "Hotkey Mute");
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
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------ hotkey handling

        IntPtr HookCb(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int msg = (int)w.ToInt64();
                int vk = Marshal.ReadInt32(l);
                if (msg == 0x100 || msg == 0x104) // key down / sys key down
                {
                    if (held.Add(vk) && !capturing)
                    {
                        int mods = CurrentMods();
                        foreach (var b in bindings.ToArray())
                        {
                            if (b.Vk == vk && b.Mods == mods)
                            {
                                Binding hit = b;
                                BeginInvoke(new Action(delegate { Fire(hit); }));
                            }
                        }
                    }
                }
                else if (msg == 0x101 || msg == 0x105) held.Remove(vk);
            }
            return CallNextHookEx(hook, code, w, l);
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

        void Fire(Binding b)
        {
            bool? r;
            try { r = Audio.Toggle(b.Process); }
            catch (Exception ex) { Notify(b.Process, "Error: " + ex.Message); return; }
            Notify(b.Process, r == null ? "no audio session found (is it playing sound?)" : (r.Value ? "muted" : "unmuted"));
            if (Visible) RefreshStatus();
        }

        void Notify(string who, string what)
        {
            if (notify) tray.ShowBalloonTip(1000, "Hotkey Mute", who + ": " + what, ToolTipIcon.None);
        }

        // ------------------------------------------------------------ list UI

        void Reload()
        {
            lv.Items.Clear();
            foreach (var b in bindings)
            {
                var item = new ListViewItem(b.Process);
                // Several programs may share a hotkey on purpose; flag it so it is never a surprise.
                var others = new List<string>();
                foreach (var o in bindings) if (o != b && o.Mods == b.Mods && o.Vk == b.Vk) others.Add(o.Process);
                string keyText = Hotkeys.Format(b.Mods, b.Vk);
                if (others.Count > 0)
                {
                    keyText = "\u26A0 " + keyText;
                    item.ToolTipText = "Shared hotkey - also toggles: " + string.Join(", ", others.ToArray());
                }
                item.SubItems.Add(keyText);
                item.SubItems.Add("");
                item.Tag = b;
                lv.Items.Add(item);
            }
            RefreshStatus();
        }

        void RefreshStatus()
        {
            var all = Audio.Sessions();
            foreach (ListViewItem item in lv.Items)
            {
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
            capturing = true;
            try
            {
                using (var dlg = new AddDialog(existing))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (existing == null) bindings.Add(new Binding { Process = dlg.ProcessName, Mods = dlg.Mods, Vk = dlg.Vk });
                    else { existing.Process = dlg.ProcessName; existing.Mods = dlg.Mods; existing.Vk = dlg.Vk; }
                    Save(); Reload();
                }
            }
            finally { capturing = false; held.Clear(); }
        }

        void RemoveSelected()
        {
            Binding b = Selected();
            if (b == null) return;
            bindings.Remove(b); Save(); Reload();
        }

        void ToggleSelected()
        {
            Binding b = Selected();
            if (b != null) Fire(b);
        }

        void Save() { Store.Save(bindings, notify); }

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
