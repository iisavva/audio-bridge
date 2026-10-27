using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Audio;
using Windows.Media.Devices;

[assembly: AssemblyTitle("Audio Bridge")]
[assembly: AssemblyProduct("Audio Bridge")]
[assembly: AssemblyDescription("Звук Android и ПК через выбранный аудиовыход Windows")]

namespace AudioBridge
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var singleInstance = new Mutex(false, "Local\\AudioBridge"))
            {
                if (!singleInstance.WaitOne(0))
                {
                    MessageBox.Show("Приёмник уже запущен. Найди его окно или значок в области уведомлений.", "Audio Bridge");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new BridgeForm()); }
                finally { singleInstance.ReleaseMutex(); }
            }
        }
    }

    // Polling keeps this portable build independent of the Windows SDK and
    // preserves cancellation/timeout handling for native WinRT operations.
    static class NativeAsync
    {
        public static async Task<T> Result<T>(IAsyncOperation<T> operation, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                while (operation.Status == AsyncStatus.Started)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds > 25) throw new TimeoutException("Телефон не ответил за 25 секунд.");
                    await Task.Delay(100, token);
                }
                token.ThrowIfCancellationRequested();
                return operation.GetResults();
            }
            finally
            {
                Cleanup(operation);
            }
        }

        public static async Task Done(IAsyncAction operation, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                while (operation.Status == AsyncStatus.Started)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds > 25) throw new TimeoutException("Windows не ответила за 25 секунд.");
                    await Task.Delay(100, token);
                }
                token.ThrowIfCancellationRequested();
                operation.GetResults();
            }
            finally
            {
                Cleanup(operation);
            }
        }

        static void Cleanup(IAsyncInfo operation)
        {
            // Cancellation can complete asynchronously. Never close an operation
            // that is still Started, or mask its original exception in cleanup.
            try
            {
                if (operation.Status == AsyncStatus.Started) operation.Cancel();
                if (operation.Status != AsyncStatus.Started) operation.Close();
            }
            catch { }
        }
    }

    class PhoneItem
    {
        public DeviceInformation Device;
        public override string ToString() { return Device.Name; }
    }

    sealed class BridgeForm : Form
    {
        readonly ComboBox phones = new ComboBox();
        readonly Button connect = new Button();
        readonly Button stop = new Button();
        readonly Button refresh = new Button();
        readonly Label output = new Label();
        readonly Label status = new Label();
        readonly CheckBox reconnect = new CheckBox();
        readonly TextBox log = new TextBox();
        readonly TrackBar phoneVolume = new TrackBar();
        readonly TrackBar masterVolume = new TrackBar();
        readonly Label phonePercent = new Label();
        readonly Label masterPercent = new Label();
        readonly Label volumeHint = new Label();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        CancellationTokenSource cancellation = new CancellationTokenSource();
        AudioPlaybackConnection connection;
        string wantedId;
        bool busy;
        bool closing;
        bool exitRequested;
        bool wantConnection;
        DateTime nextAttempt;
        string lastState;
        string phoneSessionId;
        bool updatingVolumes;

        public BridgeForm()
        {
            Text = "Audio Bridge — звук ПК и Android";
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(720, 740);
            MinimumSize = new Size(700, 740);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 249, 251);
            Icon = SystemIcons.Application;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 10 };
            for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(Label("Звук ПК и телефона вместе", 20, FontStyle.Bold, 46));
            layout.Controls.Add(Label("Android → Bluetooth → ПК → наушники или колонки", 11, FontStyle.Regular, 32));

            output.AutoSize = false;
            output.Dock = DockStyle.Top;
            output.Height = 54;
            output.Text = "Выход Windows: проверяю…";
            layout.Controls.Add(output);

            var settings = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 12) };
            settings.Controls.Add(Button("Bluetooth Windows", delegate { Open("ms-settings:bluetooth"); }));
            settings.Controls.Add(Button("Выход звука", delegate { Open("ms-settings:sound"); }));
            settings.Controls.Add(Button("Звонки: Связь с телефоном", delegate { Open("ms-phone:"); }));
            layout.Controls.Add(settings);

            var source = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 0, 0, 12) };
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            phones.DropDownStyle = ComboBoxStyle.DropDownList;
            phones.Dock = DockStyle.Fill;
            phones.Margin = new Padding(0, 4, 10, 0);
            phones.AccessibleName = "Телефон — источник звука";
            phones.SelectedIndexChanged += delegate { UpdateButtons(); };
            source.Controls.Add(phones, 0, 0);
            refresh.Text = "Обновить";
            refresh.Height = 34;
            refresh.Dock = DockStyle.Fill;
            refresh.Click += async delegate { await Scan(); };
            source.Controls.Add(refresh, 1, 0);
            layout.Controls.Add(source);

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 12) };
            connect.Text = "Принимать звук телефона";
            connect.AutoSize = true;
            connect.Height = 38;
            connect.Padding = new Padding(8, 4, 8, 4);
            connect.Click += async delegate
            {
                var item = phones.SelectedItem as PhoneItem;
                if (item == null) return;
                wantedId = item.Device.Id;
                wantConnection = true;
                await Connect();
            };
            stop.Text = "Остановить";
            stop.AutoSize = true;
            stop.Height = 38;
            stop.Padding = new Padding(8, 4, 8, 4);
            stop.Click += delegate { Disconnect(); };
            reconnect.Text = "Восстанавливать связь";
            reconnect.Checked = true;
            reconnect.AutoSize = true;
            reconnect.Margin = new Padding(10, 10, 0, 0);
            actions.Controls.Add(connect);
            actions.Controls.Add(stop);
            actions.Controls.Add(reconnect);
            layout.Controls.Add(actions);

            var volumes = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 3, Margin = new Padding(0, 0, 0, 8) };
            volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
            volumes.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            volumes.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            volumes.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            volumes.Controls.Add(Label("Телефон", 10, FontStyle.Bold, 30), 0, 0);
            volumes.Controls.Add(Label("Общая громкость", 10, FontStyle.Regular, 30), 0, 1);
            foreach (var slider in new[] { phoneVolume, masterVolume })
            {
                slider.Minimum = 0;
                slider.Maximum = 100;
                slider.TickFrequency = 10;
                slider.TickStyle = TickStyle.None;
                slider.LargeChange = 5;
                slider.SmallChange = 1;
                slider.Dock = DockStyle.Fill;
                slider.Enabled = false;
                slider.Margin = new Padding(0);
            }
            phoneVolume.AccessibleName = "Громкость телефона";
            masterVolume.AccessibleName = "Общая громкость ПК и телефона";
            phoneVolume.ValueChanged += delegate { if (!updatingVolumes) ChangePhoneVolume(); };
            masterVolume.ValueChanged += delegate
            {
                if (updatingVolumes) return;
                try { AudioMixer.MasterVolume(masterVolume.Value / 100F); masterPercent.Text = masterVolume.Value + "%"; }
                catch (Exception ex) { SetStatus("Не удалось изменить общую громкость: " + ex.Message, true); }
            };
            foreach (var percent in new[] { phonePercent, masterPercent }) { percent.Dock = DockStyle.Fill; percent.TextAlign = ContentAlignment.TopRight; percent.Text = "—"; }
            volumes.Controls.Add(phoneVolume, 1, 0);
            volumes.Controls.Add(masterVolume, 1, 1);
            volumes.Controls.Add(phonePercent, 2, 0);
            volumes.Controls.Add(masterPercent, 2, 1);
            volumeHint.Text = "Включи воспроизведение на телефоне, чтобы появился его регулятор.";
            volumeHint.Dock = DockStyle.Fill;
            volumeHint.ForeColor = Color.DimGray;
            volumeHint.Font = new Font("Segoe UI", 9F);
            volumes.Controls.Add(volumeHint, 0, 2);
            volumes.SetColumnSpan(volumeHint, 3);
            layout.Controls.Add(volumes);

            status.Dock = DockStyle.Top;
            status.Height = 55;
            status.Text = "Ищу сопряжённые телефоны…";
            status.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(status);

            layout.Controls.Add(Label(
                "1. Выбери наушники или колонки как выход звука Windows.\r\n" +
                "2. На Android выбери этот ПК для «Мультимедиа» в Bluetooth.\r\n" +
                "3. Запусти звук на телефоне и на ПК.\r\n\r\n" +
                "Принимается звук, который Android отправляет как мультимедиа.\r\n" +
                "Звонки — через «Связь с телефоном». Часть сигналов Android может звучать только на телефоне.",
                10, FontStyle.Regular, 145));

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            log.BackColor = Color.White;
            log.AccessibleName = "Журнал подключения";
            layout.Controls.Add(log);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, delegate { ShowWindow(); });
            menu.Items.Add("Остановить приём", null, delegate { Disconnect(); });
            menu.Items.Add("Выйти", null, delegate { exitRequested = true; Close(); });
            tray.Icon = Icon;
            tray.Text = "Audio Bridge";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.Visible = true;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            Shown += async delegate { await Scan(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }
                closing = true;
                timer.Stop();
                cancellation.Cancel();
                ReleaseConnection();
                tray.Visible = false;
                tray.Dispose();
            };
            timer.Interval = 4000;
            timer.Tick += async delegate { await Tick(); };
            timer.Start();
            UpdateButtons();
        }

        static Label Label(string text, float size, FontStyle style, int height)
        {
            return new Label { Text = text, Height = height, Dock = DockStyle.Top, Font = new Font("Segoe UI", size, style), Margin = new Padding(0) };
        }

        static Button Button(string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(5), Margin = new Padding(0, 0, 8, 0) };
            button.Click += action;
            return button;
        }

        void Open(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("Не удалось открыть настройки: " + ex.Message, true); }
        }

        void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        void UpdateButtons()
        {
            connect.Enabled = !busy && connection == null && phones.SelectedItem != null;
            refresh.Enabled = !busy;
            phones.Enabled = !busy && connection == null;
            stop.Enabled = wantConnection || connection != null;
        }

        void SetStatus(string message, bool error)
        {
            if (closing) return;
            status.Text = message;
            status.ForeColor = error ? Color.FromArgb(175, 45, 30) : Color.FromArgb(28, 85, 60);
            AddLog(message);
        }

        void AddLog(string message)
        {
            if (closing) return;
            if (log.TextLength > 15000) log.Clear();
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message.Replace("\r\n", " ") + Environment.NewLine);
        }

        async Task Scan()
        {
            if (busy || closing) return;
            busy = true;
            UpdateButtons();
            try
            {
                await CheckOutput();
                var items = await NativeAsync.Result(DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector()), cancellation.Token);
                if (closing) return;
                var previous = phones.SelectedItem as PhoneItem;
                string selectedId = previous == null ? wantedId : previous.Device.Id;
                phones.Items.Clear();
                foreach (var device in items)
                {
                    var item = new PhoneItem { Device = device };
                    phones.Items.Add(item);
                    if (device.Id == selectedId) phones.SelectedItem = item;
                }
                if (phones.SelectedIndex < 0 && phones.Items.Count > 0) phones.SelectedIndex = 0;
                UpdateVolumes();
                if (connection == null && !wantConnection)
                    SetStatus(items.Count > 0 ? "Телефон найден. Нажми «Принимать звук телефона»." : "Телефоны не найдены. Выполни сопряжение и нажми «Обновить».", items.Count == 0);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("Не удалось проверить устройства: " + ex.Message, true); }
            finally { FinishOperation(); }
        }

        async Task CheckOutput()
        {
            try
            {
                string id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                if (String.IsNullOrEmpty(id)) { output.Text = "В Windows не выбран выход звука."; return; }
                var device = await NativeAsync.Result(DeviceInformation.CreateFromIdAsync(id), cancellation.Token);
                if (closing) return;
                output.Text = device != null
                    ? "Выход Windows: устройство по умолчанию.\r\nЗвук ПК и входящий звук телефона направляются в этот выход."
                    : "Выход звука недоступен. Выбери устройство в настройках Windows.";
            }
            catch (OperationCanceledException) { throw; }
            catch { output.Text = "Выход Windows: проверь выбранное устройство в настройках звука."; }
        }

        async Task Connect()
        {
            if (busy || closing || !wantConnection) return;
            busy = true;
            cancellation.Dispose();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            AudioPlaybackConnection attempt = null;
            UpdateButtons();
            try
            {
                ReleaseConnection();
                SetStatus("Открываю аудиосоединение с телефоном…", false);
                attempt = AudioPlaybackConnection.TryCreateFromId(wantedId);
                if (attempt == null) throw new InvalidOperationException("Источник недоступен. Проверь Bluetooth и сопряжение телефона.");
                connection = attempt;
                await NativeAsync.Done(attempt.StartAsync(), token);
                var result = await NativeAsync.Result(attempt.OpenAsync(), token);
                token.ThrowIfCancellationRequested();
                if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
                    throw new InvalidOperationException("Телефон не открыл звук (" + result.Status + "). Включи «Мультимедиа» для этого ПК в Bluetooth Android.");
                lastState = attempt.State.ToString();
                SetStatus("Аудиосоединение открыто. Запусти звук на Android и на ПК.", false);
                await CheckOutput();
                UpdateVolumes();
            }
            catch (OperationCanceledException) { if (!closing) SetStatus("Приём остановлен.", false); }
            catch (Exception ex)
            {
                ReleaseConnection();
                SetStatus(ex.Message, true);
                if (!reconnect.Checked) wantConnection = false;
            }
            finally
            {
                if (token.IsCancellationRequested && attempt != null)
                {
                    if (Object.ReferenceEquals(connection, attempt)) connection = null;
                    try { attempt.Dispose(); } catch { }
                }
                nextAttempt = DateTime.UtcNow.AddSeconds(15);
                FinishOperation();
            }
        }

        void FinishOperation()
        {
            busy = false;
            if (closing) return;
            if (cancellation.IsCancellationRequested)
            {
                cancellation.Dispose();
                cancellation = new CancellationTokenSource();
            }
            UpdateButtons();
        }

        void ReleaseConnection()
        {
            var old = connection;
            connection = null;
            if (old != null) { try { old.Dispose(); } catch { } }
            lastState = null;
        }

        void Disconnect()
        {
            wantConnection = false;
            cancellation.Cancel();
            // Also release during a device refresh; stopping must never leave
            // the receiver open merely because another operation was in flight.
            ReleaseConnection();
            phoneSessionId = null;
            phoneVolume.Enabled = false;
            SetStatus("Приём остановлен. Сопряжение телефона сохранено.", false);
            UpdateButtons();
            if (!busy) { cancellation.Dispose(); cancellation = new CancellationTokenSource(); }
        }

        async Task Tick()
        {
            if (busy || closing) return;
            try
            {
                if (connection != null)
                {
                    string state = connection.State.ToString();
                    if (state != lastState)
                    {
                        lastState = state;
                        SetStatus(state == "Opened" ? "Аудиосоединение открыто." : "Аудиосоединение закрыто. Проверяю возможность восстановления…", state != "Opened");
                    }
                    if (connection.State == AudioPlaybackConnectionState.Closed)
                    {
                        ReleaseConnection();
                        if (!reconnect.Checked) wantConnection = false;
                    }
                }
                if (connection == null && wantConnection && reconnect.Checked && DateTime.UtcNow >= nextAttempt)
                    await Connect();
                UpdateVolumes();
                UpdateButtons();
            }
            catch (Exception ex) { ReleaseConnection(); SetStatus("Связь прервалась: " + ex.Message, true); }
        }

        void UpdateVolumes()
        {
            if (closing) return;
            updatingVolumes = true;
            try
            {
                masterVolume.Value = Percent(AudioMixer.MasterVolume(null));
                masterPercent.Text = masterVolume.Value + "%";
                masterVolume.Enabled = true;
            }
            catch { masterVolume.Enabled = false; masterPercent.Text = "—"; }
            try
            {
                phoneSessionId = null;
                phoneVolume.Enabled = false;
                phonePercent.Text = "—";
                var phone = phones.SelectedItem as PhoneItem;
                if (connection == null || phone == null)
                {
                    volumeHint.Text = "Приём должен быть включён; затем запусти звук на телефоне.";
                    return;
                }
                var sessions = AudioMixer.Sessions();
                try
                {
                    MixerSession found = null;
                    int matches = 0;
                    foreach (var session in sessions)
                    {
                        // Match the phone's own A2DP sink, never a shared service
                        // process ID or an unrelated desktop application's audio.
                        if (session.State != 2 && session.Name.IndexOf(phone.Device.Name, StringComparison.OrdinalIgnoreCase) >= 0
                            && session.Name.IndexOf("A2DP SNK", StringComparison.OrdinalIgnoreCase) >= 0)
                        { found = session; matches++; }
                    }
                    if (matches == 1)
                    {
                        phoneSessionId = found.Id;
                        phoneVolume.Value = Percent(found.Volume);
                        phonePercent.Text = phoneVolume.Value + "%";
                        phoneVolume.Enabled = true;
                        volumeHint.Text = "«Телефон» меняет только Android; «Общая» — весь выбранный выход Windows.";
                    }
                    else volumeHint.Text = matches > 1 ? "Несколько потоков телефона. Выбери нужный в микшере Windows." : "Запусти звук на телефоне — регулятор появится автоматически.";
                }
                finally { foreach (var session in sessions) session.Dispose(); }
            }
            catch { volumeHint.Text = "Поток телефона пока недоступен. Проверь воспроизведение и подключение."; }
            finally { updatingVolumes = false; }
        }

        static int Percent(float value) { return Math.Max(0, Math.Min(100, (int)Math.Round(value * 100))); }

        void ChangePhoneVolume()
        {
            if (phoneSessionId == null || closing) return;
            try
            {
                bool changed = false;
                var sessions = AudioMixer.Sessions();
                try
                {
                    foreach (var session in sessions)
                    {
                        if (session.Id != phoneSessionId) continue;
                        session.Volume = phoneVolume.Value / 100F;
                        phonePercent.Text = phoneVolume.Value + "%";
                        changed = true;
                        break;
                    }
                }
                finally { foreach (var session in sessions) session.Dispose(); }
                if (!changed) UpdateVolumes();
            }
            catch (Exception ex) { SetStatus("Не удалось изменить громкость телефона: " + ex.Message, true); }
        }
    }
}
