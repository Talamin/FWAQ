using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using robotManager.Helpful;
using WMemory = wManager.Wow.Memory;
using Math = System.Math;                 // robotManager.Helpful also defines Math / Mouse
using Cursors = System.Windows.Input.Cursors;

namespace Wholesome_Auto_Quester.GUI
{
    /// <summary>
    /// Native settings overlay drawn OVER the WoW client (borderless / windowed) — the Quester's equivalent of the
    /// AIO3 fightclass overlay, in the SAME dark, tabbed style (just a green accent instead of blue). It generates
    /// controls from the <see cref="OverlaySetting"/> list (which wraps <see cref="WholesomeAQSettings"/>) and binds
    /// two-way directly, so an edit persists + applies on the next planner cycle with no game Lua/UI touched.
    ///
    /// Ported from AIO3's NativeOverlay (a Product loads from disk so full WPF works); runs on its own STA thread,
    /// everything guarded — if WPF can't start it logs and the normal config window keeps working. DECOUPLED from the
    /// fightclass overlay (own window, own persisted position) so the two sit side by side without coupling.
    /// </summary>
    internal sealed class QuesterOverlay
    {
        private readonly string _title;
        private readonly IReadOnlyList<OverlaySetting> _settings;
        private readonly string _profile;
        private Thread _uiThread;
        private QuesterOverlayWindow _window;

        public QuesterOverlay(string title, IReadOnlyList<OverlaySetting> settings, string profile = null)
        {
            _title = title;
            _settings = settings;
            _profile = profile;
            Start();
        }

        private void Start()
        {
            try
            {
                _uiThread = new Thread(Run) { IsBackground = true, Name = "WAQ-Overlay" };
                _uiThread.SetApartmentState(ApartmentState.STA);
                _uiThread.Start();
            }
            catch (Exception e)
            {
                Logging.WriteError("[WAQ] overlay thread failed to start: " + e.Message);
            }
        }

        private void Run()
        {
            try
            {
                _window = new QuesterOverlayWindow(_title, _settings, _profile);
                _window.Show();
                Logging.Write("[WAQ] Native settings overlay ready (over the game).");
                Dispatcher.Run();
            }
            catch (Exception e)
            {
                Logging.WriteError("[WAQ] overlay unavailable (the config window still works): " + e);
            }
        }

        public void Dispose()
        {
            try
            {
                Dispatcher d = _window?.Dispatcher;
                if (d != null)
                {
                    d.Invoke(() => { try { _window.Shutdown(); } catch { } });
                    d.InvokeShutdown();
                }
            }
            catch { /* tearing down — never throw */ }
        }
    }

    /// <summary>The WPF window: a transparent, topmost, borderless panel that tracks the WoW window and renders the
    /// settings as Category tabs of controls. Built entirely in code (dark theme, green accent).</summary>
    internal sealed class QuesterOverlayWindow : Window
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // Dark theme like AIO3, with a GREEN accent (the Quester's "slightly different base colour").
        private static readonly Brush PanelBg = Freeze(new SolidColorBrush(Color.FromArgb(232, 22, 24, 30))); // slightly translucent
        private static readonly Brush BarBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 34, 38, 48)));
        private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromArgb(255, 120, 205, 150))); // green
        private static readonly Brush Fg = Freeze(new SolidColorBrush(Color.FromArgb(255, 226, 230, 238)));
        private static readonly Brush Divider = Freeze(new SolidColorBrush(Color.FromArgb(40, 120, 130, 150)));
        private static readonly Brush RowHover = Freeze(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)));
        private static readonly Brush InputBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 40, 44, 54)));

        private const double FullW = 340, FullH = 470, TokenW = 86, TokenH = 26;
        private const int DefaultOffX = 24, DefaultOffY = 80;

        private readonly List<(OverlaySetting setting, FrameworkElement row)> _rows = new List<(OverlaySetting, FrameworkElement)>();
        private readonly IReadOnlyList<OverlaySetting> _settings;
        private readonly DispatcherTimer _timer;

        private TextBox _search;
        private Border _panel;
        private Border _token;
        private TabControl _tabs;
        private bool _minimized;
        private bool _dragging;
        private Point _tokenDown;
        private bool _tokenMoved;
        private int _offX = DefaultOffX, _offY = DefaultOffY;
        private IntPtr _hwnd;
        private readonly string _stateFile;
        private double _dpiScale = 1.0;
        private bool _loggedRect;

        public QuesterOverlayWindow(string title, IReadOnlyList<OverlaySetting> settings, string profile)
        {
            _settings = settings ?? new List<OverlaySetting>();
            _stateFile = StatePath(profile);
            LoadState();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = false;
            FontSize = 12;
            Width = FullW;
            Height = FullH;
            Left = 200; Top = 200;

            BuildChrome(title);
            BuildControls();
            SetMinimized(_minimized);

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(66) };
            _timer.Tick += Track;
            _timer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            try { SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW); } catch { }
            try
            {
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null) _dpiScale = src.CompositionTarget.TransformToDevice.M11;
                if (_dpiScale <= 0) _dpiScale = 1.0;
            }
            catch { _dpiScale = 1.0; }
        }

        // --- persisted position + minimized state, per character ---

        private static string StatePath(string profile)
        {
            try
            {
                if (string.IsNullOrEmpty(profile)) profile = "default";
                foreach (char c in Path.GetInvalidFileNameChars()) profile = profile.Replace(c, '_');
                return Path.Combine(Others.GetCurrentDirectory, "Settings", "WAQ", profile + ".overlay");
            }
            catch { return null; }
        }

        private void LoadState()
        {
            try
            {
                if (_stateFile == null || !File.Exists(_stateFile)) return;
                string[] p = File.ReadAllText(_stateFile).Split(',');
                if (p.Length >= 3 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y))
                {
                    _offX = x; _offY = y; _minimized = p[2] == "1";
                }
            }
            catch { }
        }

        private void SaveState()
        {
            try
            {
                if (_stateFile == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_stateFile));
                File.WriteAllText(_stateFile, _offX + "," + _offY + "," + (_minimized ? "1" : "0"));
            }
            catch { }
        }

        public void Shutdown()
        {
            _timer?.Stop();
            Close();
        }

        // --- chrome: title bar + filter + tabs, plus a minimized token ---

        private void BuildChrome(string title)
        {
            var titleText = new TextBlock
            {
                Text = "Quester — " + title, Foreground = Fg, FontWeight = FontWeights.Bold, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            var min = new Button
            {
                Content = "–", Width = 20, Height = 18, Foreground = Fg, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontWeight = FontWeights.Bold, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0)
            };
            min.Click += (s, e) => SetMinimized(true);

            var bar = new Grid { Background = BarBg, Height = 26 };
            bar.Children.Add(titleText);
            bar.Children.Add(min);
            bar.MouseLeftButtonDown += (s, e) => Drag();

            _search = new TextBox
            {
                Margin = new Thickness(6, 4, 6, 2), Padding = new Thickness(4, 1, 4, 1),
                Background = InputBg, Foreground = Fg, BorderBrush = Divider, CaretBrush = Fg,
                ToolTip = "Filter settings — type part of a setting's name"
            };
            _search.TextChanged += (s, e) => ApplyFilter();

            _tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(4, 2, 4, 4) };
            _tabs.SelectionChanged += (s, e) => ApplyTabAccent();

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(_search, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(_search);
            dock.Children.Add(_tabs);

            _panel = new Border
            {
                Background = PanelBg, CornerRadius = new CornerRadius(6),
                BorderBrush = Accent, BorderThickness = new Thickness(1),
                Child = dock
            };

            var tokenText = new TextBlock
            {
                Text = "Quester", Foreground = Fg, FontWeight = FontWeights.Bold, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            _token = new Border
            {
                Background = BarBg, CornerRadius = new CornerRadius(5), Child = tokenText,
                BorderBrush = Accent, BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
                ToolTip = "Wholesome Quester — click to open, drag to move"
            };
            _token.MouseLeftButtonDown += (s, e) =>
            {
                _tokenDown = PointToScreen(e.GetPosition(this));
                _tokenMoved = false;
                _token.CaptureMouse();
                e.Handled = true;
            };
            _token.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !_token.IsMouseCaptured) return;
                Point now = PointToScreen(e.GetPosition(this));
                if (!_tokenMoved && (Math.Abs(now.X - _tokenDown.X) > 4 || Math.Abs(now.Y - _tokenDown.Y) > 4))
                {
                    _tokenMoved = true;
                    _token.ReleaseMouseCapture();
                    Drag();
                }
            };
            _token.MouseLeftButtonUp += (s, e) =>
            {
                if (_token.IsMouseCaptured) _token.ReleaseMouseCapture();
                if (!_tokenMoved) SetMinimized(false);
                e.Handled = true;
            };

            var root = new Grid();
            root.Children.Add(_panel);
            root.Children.Add(_token);
            Content = root;
        }

        private void SetMinimized(bool min)
        {
            _minimized = min;
            _panel.Visibility = min ? Visibility.Collapsed : Visibility.Visible;
            _token.Visibility = min ? Visibility.Visible : Visibility.Collapsed;
            Width = min ? TokenW : FullW;
            Height = min ? TokenH : FullH;
            SaveState();
        }

        private void Drag()
        {
            try { _dragging = true; DragMove(); }
            catch { }
            finally
            {
                _dragging = false;
                if (TryGetWowRect(out RECT r))
                {
                    _offX = (int)(Left - r.Left / _dpiScale);
                    _offY = (int)(Top - r.Top / _dpiScale);
                }
                SaveState();
            }
        }

        // --- controls: generated from the OverlaySetting list, grouped into Category tabs ---

        private void BuildControls()
        {
            int selected = _tabs.SelectedIndex;
            _tabs.Items.Clear();
            _rows.Clear();
            foreach (var group in _settings.GroupBy(s => string.IsNullOrEmpty(s.Category) ? "General" : s.Category))
            {
                var groupSettings = group.ToList();
                string cat = group.Key;
                var stack = new StackPanel { Margin = new Thickness(2) };
                foreach (OverlaySetting setting in groupSettings)
                {
                    var holder = new Border
                    {
                        Child = Row(setting),
                        Padding = new Thickness(6, 1, 6, 1),
                        BorderBrush = Divider,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        ToolTip = Hint(setting)
                    };
                    holder.MouseEnter += (s, e) => holder.Background = RowHover;
                    holder.MouseLeave += (s, e) => holder.Background = Brushes.Transparent;
                    stack.Children.Add(holder);
                    _rows.Add((setting, holder));
                }

                var reset = new Button
                {
                    Content = "↺ Reset", HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(6, 6, 6, 2), Padding = new Thickness(8, 1, 8, 1),
                    Foreground = Fg, Background = BarBg, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                    ToolTip = "Reset this tab's settings to their values when the overlay opened"
                };
                reset.Click += (s, e) =>
                {
                    foreach (OverlaySetting st in groupSettings) st.Reset();
                    Logging.Write("[WAQ] reset " + cat);
                    BuildControls();
                };
                stack.Children.Add(reset);

                var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
                _tabs.Items.Add(new TabItem
                {
                    Header = new TextBlock { Text = cat, FontSize = 11, Foreground = Fg },
                    Content = scroll,
                    Padding = new Thickness(7, 1, 7, 1)
                });
            }
            if (selected >= 0 && selected < _tabs.Items.Count) _tabs.SelectedIndex = selected;
            ApplyTabAccent();
            ApplyFilter();
        }

        private static string Hint(OverlaySetting s)
        {
            if (!string.IsNullOrEmpty(s.Description)) return s.Description;
            switch (s)
            {
                case IntOverlaySetting i: return i.Label + ": " + i.Min + "–" + i.Max + " (step " + i.Step + ")";
                case ChoiceOverlaySetting c: return c.Label + ": " + string.Join(" / ", c.Options);
                default: return s.Label;
            }
        }

        private void Changed(string label, object value)
        {
            try { Logging.Write("[WAQ] " + label + " = " + value); } catch { }
        }

        private UIElement Row(OverlaySetting setting)
        {
            switch (setting)
            {
                case ToggleOverlaySetting t:
                {
                    var cb = new CheckBox { Content = t.Label, Foreground = Fg, IsChecked = t.Value, Margin = new Thickness(2, 4, 2, 4) };
                    cb.Checked += (s, e) => { t.Value = true; Changed(t.Label, true); };
                    cb.Unchecked += (s, e) => { t.Value = false; Changed(t.Label, false); };
                    return cb;
                }
                case IntOverlaySetting i:
                {
                    var label = new TextBlock { Foreground = Fg, Margin = new Thickness(2, 3, 2, 1) };
                    void Refresh() => label.Text = i.Label + ": " + i.Value;
                    Refresh();
                    var slider = new Slider
                    {
                        Minimum = i.Min, Maximum = i.Max, Value = i.Value, TickFrequency = Math.Max(1, i.Step),
                        IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
                    };
                    void Set(int v)
                    {
                        v = Math.Max(i.Min, Math.Min(i.Max, v));
                        if (v == i.Value) return;
                        i.Value = v; slider.Value = v; Refresh(); Changed(i.Label, v);
                    }
                    slider.ValueChanged += (s, e) => Set((int)Math.Round(e.NewValue));
                    var minus = StepButton("−"); minus.Click += (s, e) => Set(i.Value - i.Step);
                    var plus = StepButton("+"); plus.Click += (s, e) => Set(i.Value + i.Step);

                    var stepRow = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                    DockPanel.SetDock(minus, Dock.Left);
                    DockPanel.SetDock(plus, Dock.Right);
                    stepRow.Children.Add(minus);
                    stepRow.Children.Add(plus);
                    stepRow.Children.Add(slider);

                    var box = new StackPanel();
                    box.Children.Add(label);
                    box.Children.Add(stepRow);
                    return box;
                }
                case ChoiceOverlaySetting c:
                {
                    var label = new TextBlock { Text = c.Label, Foreground = Fg, Margin = new Thickness(2, 3, 2, 1) };
                    var combo = new ComboBox { ItemsSource = c.Options, SelectedItem = c.Value, Margin = new Thickness(2, 0, 2, 3) };
                    combo.SelectionChanged += (s, e) =>
                    {
                        if (combo.SelectedItem is string v && v != c.Value) { c.Value = v; Changed(c.Label, v); }
                    };
                    var box = new StackPanel();
                    box.Children.Add(label);
                    box.Children.Add(combo);
                    return box;
                }
                default:
                    return new TextBlock { Text = setting.Label, Foreground = Fg };
            }
        }

        private Button StepButton(string glyph) => new Button
        {
            Content = glyph, Width = 22, Height = 20, Foreground = Fg, Background = BarBg,
            BorderThickness = new Thickness(0), FontWeight = FontWeights.Bold, Cursor = Cursors.Hand,
            Margin = new Thickness(1, 0, 1, 0)
        };

        private void ApplyTabAccent()
        {
            foreach (TabItem ti in _tabs.Items.OfType<TabItem>())
                if (ti.Header is TextBlock tb) tb.Foreground = ti.IsSelected ? Accent : Fg;
        }

        private void ApplyFilter()
        {
            string q = _search?.Text?.Trim() ?? "";
            bool all = q.Length == 0;
            foreach (var (setting, row) in _rows)
                row.Visibility = all || (setting.Label != null && setting.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        // --- tracking: glue the overlay to the WoW window (device px → DIPs via the DPI scale) ---

        private void Track(object sender, EventArgs e)
        {
            try
            {
                IntPtr wow = WowHandle();
                if (wow == IntPtr.Zero || IsIconic(wow)) { Visibility = Visibility.Hidden; return; }
                if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
                if (!Topmost) Topmost = true;

                if (!_dragging && TryGetWowRect(out RECT r))
                {
                    double s = _dpiScale;
                    double wowL = r.Left / s, wowT = r.Top / s, wowR = r.Right / s, wowB = r.Bottom / s;
                    double wowH = wowB - wowT;

                    if (!_minimized)
                        Height = Math.Max(180, Math.Min(FullH, wowH - _offY - 10));

                    double left = wowL + _offX, top = wowT + _offY;
                    left = Math.Max(wowL + 2, Math.Min(left, wowR - Width - 2));
                    top = Math.Max(wowT + 2, Math.Min(top, wowB - Height - 2));
                    Left = left; Top = top;

                    if (!_loggedRect)
                    {
                        _loggedRect = true;
                        Logging.Write($"[WAQ] overlay tracked WoW rect {(int)(wowR - wowL)}x{(int)wowH} dpi={s:0.##} → placed at {(int)Left},{(int)Top}");
                    }
                }
                else if (!_loggedRect)
                {
                    _loggedRect = true;
                    Logging.Write("[WAQ] overlay: WoW window rect unavailable → showing at the default spot.");
                }
            }
            catch (Exception ex)
            {
                if (!_loggedRect) { _loggedRect = true; Logging.WriteError("[WAQ] overlay track error: " + ex.Message); }
            }
        }

        private static IntPtr WowHandle()
        {
            try { return WMemory.WowMemory?.Memory?.WindowHandle ?? IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        private static bool TryGetWowRect(out RECT rect)
        {
            rect = default(RECT);
            IntPtr h = WowHandle();
            return h != IntPtr.Zero && GetWindowRect(h, out rect) && rect.Right > rect.Left;
        }

        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    }
}
