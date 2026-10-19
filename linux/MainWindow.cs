using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace OverByte;

public sealed class MainWindow : Window
{
    // ── Screens ──
    Canvas root = null!;
    Canvas headerPanel = null!;
    Canvas menuScreen = null!;
    Canvas fileScreen = null!;
    Canvas folderScreen = null!;
    OBButton btnBack = null!;

    // ── File mode controls ──
    Control fileStatsPanel = null!;
    TextBox txtFilePath = null!;
    OBButton btnFileBrowse = null!;
    TextBox txtFileOutput = null!;
    OBButton btnFileOutputBrowse = null!;
    TextBox txtFileSize = null!;
    ComboBox cmbFileUnit = null!;
    OBButton btnFileInflate = null!;
    TextBlock lblFileOrigSize = null!;
    TextBlock lblFileStatus = null!;
    TextBlock lblFileRatio = null!;
    TextBlock valFileStatOrig = null!;
    TextBlock valFileStatTarget = null!;
    TextBlock valFileStatRatio = null!;
    Border fileProgressTrack = null!;
    Border fileProgressFill = null!;
    CheckBox chkFileOverwrite = null!;

    // ── Folder mode controls ──
    enum FolderMode { None, CompressEnlarge, InflateAll }
    FolderMode _folderMode = FolderMode.None;
    Canvas folderModePanel = null!;
    Canvas folderWorkPanel = null!;
    Control folderStatsPanel = null!;
    TextBox txtFolderPath = null!;
    OBButton btnFolderBrowse = null!;
    TextBox txtFolderOutput = null!;
    OBButton btnFolderOutputBrowse = null!;
    TextBox txtFolderSize = null!;
    ComboBox cmbFolderUnit = null!;
    OBButton btnFolderInflate = null!;
    TextBlock lblFolderOrigSize = null!;
    TextBlock lblFolderStatus = null!;
    TextBlock lblFolderRatio = null!;
    TextBlock lblPerFile = null!;
    TextBlock valFolderStatOrig = null!;
    TextBlock valFolderStatTarget = null!;
    TextBlock valFolderStatRatio = null!;
    Border folderProgressTrack = null!;
    Border folderProgressFill = null!;

    long _fileOriginalSize;     // size of the file loaded on the FILE screen
    long _folderOriginalSize;   // total size of the folder loaded on the FOLDER screen
    CancellationTokenSource? _cts;
    bool _inflating;
    bool _closeRequested;
    bool _forceClose;
    bool _confirmingClose;

    // ── History ──
    Canvas historyPanel = null!;
    static readonly HistoryStore History = new(Path.Combine(DataDir(), "history.txt"));

    /// $XDG_DATA_HOME/OverByte, or ~/.local/share/OverByte
    static string DataDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdg) || !Path.IsPathRooted(xdg))
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdg, "OverByte");
    }

    public MainWindow(string? initialPath = null)
    {
        BuildUI();
        if (!string.IsNullOrEmpty(initialPath))
            Opened += (s, e) => Run(() => HandleDroppedPath(Path.GetFullPath(initialPath)));
    }

    void BuildUI()
    {
        Title = "OverByte";
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://overbyte/Assets/overbyte.png"))); } catch { }
        Width = 960; Height = 640;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Pal.B(Pal.BgWhite);

        root = new Canvas { Width = 960, Height = 640, Background = Pal.B(Pal.BgWhite) };
        Content = root;

        BuildHeader();
        BuildMenuScreen();
        BuildFileScreen();
        BuildFolderScreen();

        // Enable drag-drop on every control of every screen ("drag & drop anywhere")
        DragDrop.SetAllowDrop(this, true);
        EnableDragDropRecursive(root);
        AddHandler(DragDrop.DragOverEvent, HandleDragOver);
        AddHandler(DragDrop.DropEvent, HandleDragDrop);

        ShowScreen("menu");
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_inflating && !_forceClose)
        {
            // Never block the UI thread: cancel, keep the window open, and close once the worker has
            // cleaned up its partial output (see FinishOperation).
            e.Cancel = true;
            if (!_confirmingClose) _ = ConfirmCloseAsync();
        }
        base.OnClosing(e);
    }

    async Task ConfirmCloseAsync()
    {
        _confirmingClose = true;
        try
        {
            bool yes = await MessageDialog.ShowAsync(this,
                "Inflation is still in progress.\n\n" +
                "If you close now, the operation will be cancelled and " +
                "any partially inflated files will be deleted.\n\n" +
                "Close anyway?",
                "OverByte — Still Working", yesNo: true);
            if (yes && _inflating)
            {
                _closeRequested = true;
                _cts?.Cancel();
            }
            else if (yes) { _forceClose = true; Close(); }
        }
        finally { _confirmingClose = false; }
    }

    /// Called from the inflate handlers' finally blocks: finishes a close the user confirmed mid-operation.
    void FinishOperation()
    {
        _cts?.Dispose();
        _cts = null;
        _inflating = false;
        btnBack.IsEnabled = true;
        if (_closeRequested) { _closeRequested = false; _forceClose = true; Close(); }
    }

    // ════════════════════════════════════════════════
    //  UI helpers
    // ════════════════════════════════════════════════

    static T At<T>(Canvas parent, T c, double x, double y) where T : Control
    {
        Canvas.SetLeft(c, x); Canvas.SetTop(c, y);
        parent.Children.Add(c);
        return c;
    }

    static TextBlock Label(string text, double size, Color color, FontWeight? weight = null, double? width = null,
        TextAlignment align = TextAlignment.Left) => new()
    {
        Text = text, FontSize = size, Foreground = Pal.B(color), FontWeight = weight ?? FontWeight.Normal,
        Width = width ?? double.NaN, TextAlignment = align
    };

    static Border MakeCard(Canvas parent, double x, double y, double w, double h, out Canvas inner)
    {
        inner = new Canvas();
        var b = new Border
        {
            Width = w, Height = h, Background = Pal.B(Pal.BgCard),
            BorderBrush = Pal.B(Pal.BorderLight), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = inner
        };
        return At(parent, b, x, y);
    }

    static TextBlock SectionLabel(string text) =>
        Label(text, 10, Pal.TextMid, FontWeight.SemiBold);

    static TextBox MakeTextBox(double w, string placeholder) => new()
    {
        Width = w, Height = 32, MinHeight = 32, FontSize = 13, Watermark = placeholder,
        Background = Pal.B(Color.FromRgb(250, 251, 253)), Foreground = Pal.B(Pal.TextDark),
        BorderBrush = Pal.B(Pal.BorderLight), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0)
    };

    static ComboBox MakeUnitCombo(int selected)
    {
        var c = new ComboBox
        {
            Width = 80, Height = 32, MinHeight = 32, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Background = Pal.B(Pal.BgCard), Foreground = Pal.B(Pal.TextDark),
            ItemsSource = new[] { "KB", "MB", "GB" }
        };
        c.SelectedIndex = selected;
        return c;
    }

    static Border MakeProgressTrack(Canvas parent, double x, double y, double w, out Border fill)
    {
        fill = new Border { Width = 0, Height = 6, Background = Pal.B(Pal.GreenPrimary) };
        var track = new Border
        {
            Width = w, Height = 6, Background = Pal.B(Pal.BorderLight),
            Child = new Canvas { Children = { fill } }
        };
        return At(parent, track, x, y);
    }

    static Border MakePill(Canvas parent, double x, string label, out TextBlock value)
    {
        value = Label("—", 11.5, Pal.GreenDark, FontWeight.SemiBold);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        var lbl = Label(label, 10, Pal.TextMid, FontWeight.SemiBold);
        lbl.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { Margin = new Thickness(14, 0) };
        grid.Children.Add(lbl); grid.Children.Add(value);
        var pill = new Border { Width = 210, Height = 28, CornerRadius = new CornerRadius(14), Background = Pal.B(Pal.GreenLight), Child = grid };
        return At(parent, pill, x, 12);
    }

    static Border MakeStatsBar(out TextBlock orig, out TextBlock target, out TextBlock ratio)
    {
        var c = new Canvas();
        MakePill(c, 28, "Original", out orig);
        MakePill(c, 260, "Target", out target);
        MakePill(c, 492, "Ratio", out ratio);
        return new Border
        {
            Width = 960, Height = 52, Background = Pal.B(Pal.BgCard),
            BorderBrush = Pal.B(Pal.BorderLight), BorderThickness = new Thickness(0, 0, 0, 1), Child = c
        };
    }

    void SetProgress(Border fill, Border track, double pct)
    {
        if (double.IsNaN(pct) || pct < 0) pct = 0;
        fill.Width = track.Width * Math.Min(1.0, pct);
    }

    Task ShowMsg(string msg) => MessageDialog.ShowAsync(this, msg);
    Task<bool> Confirm(string msg, string title = "OverByte") => MessageDialog.ShowAsync(this, msg, title, yesNo: true);

    static string FormatSize(long bytes) => Inflator.FormatSize(bytes);

    static long GetTargetBytes(TextBox txt, ComboBox cmb) =>
        Inflator.ParseTargetBytes(txt.Text, cmb.SelectedIndex);

    /// Runs an async UI handler so that no exception can escape an async-void and crash the app.
    async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            try { await ShowMsg($"Unexpected error:\n{ex.Message}"); } catch { }
        }
    }

    // ════════════════════════════════════════════════
    //  HEADER
    // ════════════════════════════════════════════════

    void BuildHeader()
    {
        headerPanel = new Canvas { Width = 960, Height = 56, Background = Pal.B(Pal.HeaderBg) };
        At(root, headerPanel, 0, 0);

        var logo = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold };
        logo.Inlines!.Add(new Avalonia.Controls.Documents.Run("Over") { Foreground = Brushes.White });
        logo.Inlines.Add(new Avalonia.Controls.Documents.Run("Byte") { Foreground = Pal.B(Pal.GreenPrimary) });
        At(headerPanel, logo, 28, 10);

        var pill = new Border
        {
            Width = 50, Height = 20, CornerRadius = new CornerRadius(10), Background = Pal.B(Color.FromRgb(40, 44, 52)),
            Child = new TextBlock
            {
                Text = "v1.0.1", FontSize = 10, Foreground = Pal.B(Color.FromRgb(120, 125, 135)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        At(headerPanel, pill, 740, 18);

        btnBack = new OBButton("<  BACK", 80, 32) { IsVisible = false };
        btnBack.Click += (s, e) => { if (!_inflating) ShowScreen("menu"); };
        At(headerPanel, btnBack, 820, 12);
    }

    void ShowScreen(string screen)
    {
        menuScreen.IsVisible = screen == "menu";
        fileScreen.IsVisible = screen == "file";
        fileStatsPanel.IsVisible = screen == "file";
        folderScreen.IsVisible = screen == "folder";
        folderStatsPanel.IsVisible = screen == "folder" && folderWorkPanel.IsVisible;
        btnBack.IsVisible = screen != "menu";
        if (screen == "menu") RefreshHistoryPanel();
    }

    // ════════════════════════════════════════════════
    //  MENU SCREEN
    // ════════════════════════════════════════════════

    void BuildMenuScreen()
    {
        menuScreen = new Canvas { Width = 960, Height = 584, Background = Pal.B(Pal.BgWhite) };
        At(root, menuScreen, 0, 56);

        At(menuScreen, Label("What would you like to inflate?", 24, Pal.TextDark, FontWeight.Bold, 960, TextAlignment.Center), 0, 36);
        At(menuScreen, Label("choose a mode to get started", 13, Pal.TextLight, null, 960, TextAlignment.Center), 0, 78);

        var fileCard = MakeMenuCard(menuScreen, 140, 116, 300, 260,
            "FILE", "Inflate a single file to\nany target size. Works with\nJPG, PNG, MP4, PDF, ISO,\nand almost any format.");
        fileCard.PointerReleased += (s, e) => { if (_inflating) return; ShowScreen("file"); };

        var folderCard = MakeMenuCard(menuScreen, 520, 116, 300, 260,
            "FOLDER", "Inflate an entire folder.\nCompress & enlarge into\na big ZIP, or inflate every\nfile individually.");
        folderCard.PointerReleased += (s, e) => { if (_inflating) return; OpenFolderScreen(null); };

        At(menuScreen, Label("you can also drag & drop files or folders anywhere", 11.5, Pal.TextLight, null, 960, TextAlignment.Center), 0, 388);

        // History panel
        historyPanel = new Canvas { Width = 680, Height = 150, IsVisible = false };
        At(menuScreen, historyPanel, 140, 420);
    }

    Border MakeMenuCard(Canvas parent, double x, double y, double w, double h, string title, string desc)
    {
        var inner = new Canvas();
        var card = new Border
        {
            Width = w, Height = h, Background = Pal.B(Pal.BgCard), CornerRadius = new CornerRadius(12),
            BorderBrush = Pal.B(Pal.BorderLight), BorderThickness = new Thickness(1), Child = inner,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var iconBox = new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(16), Background = Pal.B(Pal.GreenLight),
            Child = new TextBlock
            {
                Text = title == "FILE" ? "F" : "D", FontSize = 29, FontWeight = FontWeight.Bold, Foreground = Pal.B(Pal.GreenDark),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        At(inner, iconBox, w / 2 - 30, 30);
        At(inner, Label(title, 19, Pal.TextDark, FontWeight.SemiBold, w, TextAlignment.Center), 0, 103);
        At(inner, new TextBlock
        {
            Text = desc, FontSize = 12, Foreground = Pal.B(Pal.TextMid), TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Width = w - 48
        }, 24, 142);

        card.PointerEntered += (s, e) => { card.BorderBrush = Pal.B(Pal.GreenPrimary); card.BorderThickness = new Thickness(2); };
        card.PointerExited += (s, e) => { card.BorderBrush = Pal.B(Pal.BorderLight); card.BorderThickness = new Thickness(1); };

        return At(parent, card, x, y);
    }

    // ════════════════════════════════════════════════
    //  DRAG & DROP
    // ════════════════════════════════════════════════

    void EnableDragDropRecursive(Control c)
    {
        DragDrop.SetAllowDrop(c, true);
        foreach (var child in c.GetLogicalChildren().OfType<Control>())
            EnableDragDropRecursive(child);
    }

#pragma warning disable CS0618 // DragEventArgs.Data: classic IDataObject API (stable across 11.x)
    void HandleDragOver(object? s, DragEventArgs e)
    {
        e.DragEffects = !_inflating && e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void HandleDragDrop(object? s, DragEventArgs e)
    {
        if (_inflating) return;
        var path = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        e.Handled = true;
        if (path == null) return;
        Run(() => HandleDroppedPath(path));
    }
#pragma warning restore CS0618

    async Task HandleDroppedPath(string path)
    {
        bool isDir = Directory.Exists(path);
        if (fileScreen.IsVisible && !isDir)
        {
            LoadFile(path);
        }
        else if (folderScreen.IsVisible && isDir)
        {
            if (folderWorkPanel.IsVisible) await LoadFolderAsync(path);
        }
        else if (isDir)
        {
            await OpenFolderScreenAsync(path);
        }
        else if (File.Exists(path))
        {
            ShowScreen("file");
            LoadFile(path);
        }
    }

    // ════════════════════════════════════════════════
    //  FILE SCREEN
    // ════════════════════════════════════════════════

    void BuildFileScreen()
    {
        var bar = MakeStatsBar(out valFileStatOrig, out valFileStatTarget, out valFileStatRatio);
        fileStatsPanel = bar;
        At(root, bar, 0, 56);

        fileScreen = new Canvas { Width = 960, Height = 532, Background = Pal.B(Pal.BgWhite) };
        At(root, fileScreen, 0, 108);

        double cl = 32, cw = 896, y = 20;

        // Source card
        MakeCard(fileScreen, cl, y, cw, 110, out var srcCard);
        At(srcCard, SectionLabel("SOURCE FILE"), 24, 16);
        txtFilePath = At(srcCard, MakeTextBox(cw - 128, "drag and drop or browse..."), 24, 46);
        txtFilePath.IsReadOnly = true;
        btnFileBrowse = At(srcCard, new OBButton("BROWSE", 72, 32), cw - 96, 46);
        btnFileBrowse.Click += (s, e) => Run(BrowseFileAsync);
        lblFileOrigSize = At(srcCard, Label("", 11, Pal.TextLight), 26, 84);
        y += 126;

        // Output card
        MakeCard(fileScreen, cl, y, cw, 110, out var outCard);
        At(outCard, SectionLabel("OUTPUT"), 24, 16);
        txtFileOutput = At(outCard, MakeTextBox(cw - 128, "auto-generated..."), 24, 46);
        txtFileOutput.IsReadOnly = true;
        btnFileOutputBrowse = At(outCard, new OBButton("SAVE AS", 72, 32), cw - 96, 46);
        btnFileOutputBrowse.Click += (s, e) => Run(SaveFileAsAsync);
        chkFileOverwrite = At(outCard, new CheckBox { Content = "overwrite original file", FontSize = 11.5, Foreground = Pal.B(Pal.TextMid) }, 20, 80);
        chkFileOverwrite.IsCheckedChanged += (s, e) =>
        {
            bool on = chkFileOverwrite.IsChecked == true;
            txtFileOutput.IsEnabled = !on;
            btnFileOutputBrowse.IsEnabled = !on;
        };
        y += 126;

        // Target size card
        MakeCard(fileScreen, cl, y, cw, 80, out var sizeCard);
        At(sizeCard, SectionLabel("TARGET SIZE"), 24, 14);
        txtFileSize = At(sizeCard, MakeTextBox(160, "1.00"), 24, 38);
        txtFileSize.Text = "1.00"; txtFileSize.TextAlignment = TextAlignment.Right; txtFileSize.FontSize = 16; txtFileSize.FontWeight = FontWeight.SemiBold;
        txtFileSize.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) UpdateFileStats(); };
        cmbFileUnit = At(sizeCard, MakeUnitCombo(1), 194, 38);
        cmbFileUnit.SelectionChanged += (s, e) => UpdateFileStats();
        lblFileRatio = At(sizeCard, Label("", 12.5, Pal.GreenPrimary, FontWeight.SemiBold), 290, 44);
        y += 96;

        // Progress
        y += 4;
        fileProgressTrack = MakeProgressTrack(fileScreen, cl, y, cw, out fileProgressFill);
        y += 16;

        lblFileStatus = At(fileScreen, Label("select a file to begin", 11.5, Pal.TextLight), cl + 2, y);
        y += 30;

        btnFileInflate = At(fileScreen, new OBButton("INFLATE", cw, 48, primary: true), cl, y);
        btnFileInflate.IsEnabled = false;
        btnFileInflate.Click += (s, e) => Run(FileInflateClickAsync);
    }

    async Task BrowseFileAsync()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select file", AllowMultiple = false });
        var p = r.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(p)) LoadFile(p);
    }

    async Task SaveFileAsAsync()
    {
        if (string.IsNullOrEmpty(txtFilePath.Text)) return;
        var ext = Path.GetExtension(txtFilePath.Text);
        var dir = Path.GetDirectoryName(txtFileOutput.Text ?? "") ?? "";
        var opts = new FilePickerSaveOptions
        {
            Title = "Save inflated file",
            SuggestedFileName = Path.GetFileNameWithoutExtension(txtFilePath.Text) + "_inflated" + ext,
            DefaultExtension = ext.TrimStart('.'),
            ShowOverwritePrompt = true,
        };
        if (dir.Length > 0) opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);
        var f = await StorageProvider.SaveFilePickerAsync(opts);
        var p = f?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(p)) txtFileOutput.Text = p;
    }

    void LoadFile(string path)
    {
        if (_inflating || !File.Exists(path)) return;
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception ex)
        {
            lblFileStatus.Text = $"error: {ex.Message}";
            lblFileStatus.Foreground = Pal.B(Pal.AccentRed);
            return;
        }
        History.Add(path);
        txtFilePath.Text = path;
        _fileOriginalSize = size;
        lblFileOrigSize.Text = FormatSize(_fileOriginalSize);

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        txtFileOutput.Text = Path.Combine(dir, name + "_inflated" + ext);

        var inv = CultureInfo.InvariantCulture;
        if (_fileOriginalSize < 1024 * 1024) { txtFileSize.Text = "10.00"; cmbFileUnit.SelectedIndex = 1; }
        else if (_fileOriginalSize < 1024L * 1024 * 1024) { txtFileSize.Text = "1.00"; cmbFileUnit.SelectedIndex = 2; }
        else { txtFileSize.Text = (Math.Ceiling((double)_fileOriginalSize / (1024.0 * 1024 * 1024)) * 2).ToString("F2", inv); cmbFileUnit.SelectedIndex = 2; }

        btnFileInflate.IsEnabled = true;
        UpdateFileStats();
        lblFileStatus.Text = "file loaded — set target and inflate";
        lblFileStatus.Foreground = Pal.B(Pal.GreenDark);
    }

    void UpdateFileStats()
    {
        if (valFileStatTarget == null || cmbFileUnit == null || lblFileRatio == null) return; // during construction
        long target = GetTargetBytes(txtFileSize, cmbFileUnit);
        valFileStatTarget.Text = FormatSize(target);
        if (_fileOriginalSize > 0)
        {
            valFileStatOrig.Text = FormatSize(_fileOriginalSize);
            double ratio = target > _fileOriginalSize ? (double)target / _fileOriginalSize : 0;
            valFileStatRatio.Text = ratio > 0 ? $"{ratio:F1}x" : "—";
            lblFileRatio.Text = ratio > 0 ? $"{ratio:F0}x inflation" : "";
            lblFileRatio.Foreground = Pal.B(ratio > 100 ? Pal.AccentRed : Pal.GreenPrimary);
        }
    }

    async Task FileInflateClickAsync()
    {
        if (_inflating) { _cts?.Cancel(); return; }
        if (string.IsNullOrEmpty(txtFilePath.Text)) return;
        var src = txtFilePath.Text!;
        var dst = chkFileOverwrite.IsChecked == true ? src : txtFileOutput.Text;
        if (string.IsNullOrEmpty(dst)) { await ShowMsg("Select an output path."); return; }

        // Re-read the real size now: the file may have changed since it was loaded
        long srcSize;
        try
        {
            if (!File.Exists(src)) { await ShowMsg("The source file no longer exists."); return; }
            srcSize = new FileInfo(src).Length;
        }
        catch (Exception ex) { await ShowMsg($"Cannot read the source file:\n{ex.Message}"); return; }
        _fileOriginalSize = srcSize;
        UpdateFileStats();

        long target = GetTargetBytes(txtFileSize, cmbFileUnit);
        if (target <= srcSize) { await ShowMsg($"Target must exceed original ({FormatSize(srcSize)})."); return; }
        long padding = target - srcSize;

        if (target > 5L * 1024 * 1024 * 1024)
        {
            if (!await Confirm($"Creating {FormatSize(target)} file. Continue?")) return;
        }

        // The output is staged in a temp file next to the destination, so the full target size is needed
        var space = Inflator.CheckDiskSpace(dst, target);
        if (space != null) { await ShowMsg(space); return; }

        if (_inflating) return; // a second click while a dialog was open
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetFileUIBusy(true);
        _inflating = true;
        try
        {
            var progress = new Progress<(long written, string status)>(p =>
            {
                SetProgress(fileProgressFill, fileProgressTrack, (double)p.written / padding);
                lblFileStatus.Text = $"inflating... {FormatSize(p.written)} / {FormatSize(padding)}";
                lblFileStatus.Foreground = Pal.B(Pal.GreenDark);
            });
            await Task.Run(() => Inflator.InflateFile(src, dst, padding, progress, ct), ct);

            SetProgress(fileProgressFill, fileProgressTrack, 1.0);
            lblFileStatus.Text = $"done — {FormatSize(srcSize)} → {FormatSize(new FileInfo(dst).Length)}";
            lblFileStatus.Foreground = Pal.B(Pal.GreenPrimary);
        }
        catch (OperationCanceledException) { lblFileStatus.Text = "cancelled"; lblFileStatus.Foreground = Pal.B(Pal.TextLight); }
        catch (UserMessageException ex) { lblFileStatus.Text = $"error: {ex.Message}"; lblFileStatus.Foreground = Pal.B(Pal.AccentRed); }
        catch (Exception ex) { lblFileStatus.Text = $"error: {ex.Message}"; lblFileStatus.Foreground = Pal.B(Pal.AccentRed); }
        finally { SetFileUIBusy(false); FinishOperation(); }
    }

    void SetFileUIBusy(bool busy)
    {
        btnFileBrowse.IsEnabled = !busy;
        btnFileOutputBrowse.IsEnabled = !busy && chkFileOverwrite.IsChecked != true;
        txtFileSize.IsEnabled = !busy;
        cmbFileUnit.IsEnabled = !busy; chkFileOverwrite.IsEnabled = !busy;
        if (busy) SetProgress(fileProgressFill, fileProgressTrack, 0);
        btnFileInflate.SetLabel(busy ? "CANCEL" : "INFLATE");
        btnBack.IsEnabled = !busy;
    }

    // ════════════════════════════════════════════════
    //  FOLDER SCREEN
    // ════════════════════════════════════════════════

    void BuildFolderScreen()
    {
        var bar = MakeStatsBar(out valFolderStatOrig, out valFolderStatTarget, out valFolderStatRatio);
        folderStatsPanel = bar;
        bar.IsVisible = false;
        At(root, bar, 0, 56);

        folderScreen = new Canvas { Width = 960, Height = 532, Background = Pal.B(Pal.BgWhite) };
        At(root, folderScreen, 0, 108);

        // ── Sub-mode selection panel ──
        folderModePanel = new Canvas { Width = 960, Height = 532, Background = Pal.B(Pal.BgWhite) };
        At(folderScreen, folderModePanel, 0, 0);

        At(folderModePanel, Label("Choose folder inflation mode", 21, Pal.TextDark, FontWeight.Bold, 960, TextAlignment.Center), 0, 40);

        var compressCard = MakeMenuCard(folderModePanel, 140, 110, 300, 280,
            "COMPRESS", "ZIP the entire folder into\na single archive, then inflate\nthe ZIP to your target size.\nGreat for sending one big file.");
        compressCard.PointerReleased += (s, e) => { if (!_inflating) { _folderMode = FolderMode.CompressEnlarge; ShowFolderWork(); } };

        var inflateAllCard = MakeMenuCard(folderModePanel, 520, 110, 300, 280,
            "INFLATE ALL", "Inflate every file inside\nthe folder individually.\nEach file grows to the\ntarget size you set.");
        inflateAllCard.PointerReleased += (s, e) => { if (!_inflating) { _folderMode = FolderMode.InflateAll; ShowFolderWork(); } };

        // ── Work panel (shown after sub-mode chosen) ──
        folderWorkPanel = new Canvas { Width = 960, Height = 532, Background = Pal.B(Pal.BgWhite), IsVisible = false };
        At(folderScreen, folderWorkPanel, 0, 0);

        double cl = 32, cw = 896, y = 20;

        MakeCard(folderWorkPanel, cl, y, cw, 110, out var srcCard);
        At(srcCard, SectionLabel("SOURCE FOLDER"), 24, 16);
        txtFolderPath = At(srcCard, MakeTextBox(cw - 128, "select a folder..."), 24, 46);
        txtFolderPath.IsReadOnly = true;
        btnFolderBrowse = At(srcCard, new OBButton("BROWSE", 72, 32), cw - 96, 46);
        btnFolderBrowse.Click += (s, e) => Run(BrowseFolderAsync);
        lblFolderOrigSize = At(srcCard, Label("", 11, Pal.TextLight), 26, 84);
        y += 126;

        MakeCard(folderWorkPanel, cl, y, cw, 90, out var outCard);
        At(outCard, SectionLabel("OUTPUT"), 24, 16);
        txtFolderOutput = At(outCard, MakeTextBox(cw - 128, "auto-generated..."), 24, 44);
        txtFolderOutput.IsReadOnly = true;
        btnFolderOutputBrowse = At(outCard, new OBButton("SAVE AS", 72, 32), cw - 96, 44);
        btnFolderOutputBrowse.Click += (s, e) => Run(FolderOutputBrowseAsync);
        y += 106;

        MakeCard(folderWorkPanel, cl, y, cw, 80, out var sizeCard);
        At(sizeCard, SectionLabel("TARGET SIZE"), 24, 14);
        txtFolderSize = At(sizeCard, MakeTextBox(160, "1.00"), 24, 38);
        txtFolderSize.Text = "1.00"; txtFolderSize.TextAlignment = TextAlignment.Right; txtFolderSize.FontSize = 16; txtFolderSize.FontWeight = FontWeight.SemiBold;
        txtFolderSize.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) UpdateFolderStats(); };
        cmbFolderUnit = At(sizeCard, MakeUnitCombo(2), 194, 38);
        cmbFolderUnit.SelectionChanged += (s, e) => UpdateFolderStats();
        lblFolderRatio = At(sizeCard, Label("", 12.5, Pal.GreenPrimary, FontWeight.SemiBold), 290, 44);
        lblPerFile = At(sizeCard, Label("(per file)", 11.5, Pal.TextLight), 380, 44);
        y += 96;

        y += 4;
        folderProgressTrack = MakeProgressTrack(folderWorkPanel, cl, y, cw, out folderProgressFill);
        y += 16;

        lblFolderStatus = At(folderWorkPanel, Label("select a folder to begin", 11.5, Pal.TextLight), cl + 2, y);
        y += 30;

        btnFolderInflate = At(folderWorkPanel, new OBButton("INFLATE", cw, 48, primary: true), cl, y);
        btnFolderInflate.IsEnabled = false;
        btnFolderInflate.Click += (s, e) => Run(FolderInflateClickAsync);
    }

    async Task BrowseFolderAsync()
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder to inflate", AllowMultiple = false });
        var p = r.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(p)) await LoadFolderAsync(p);
    }

    async Task FolderOutputBrowseAsync()
    {
        if (_folderMode == FolderMode.CompressEnlarge)
        {
            var opts = new FilePickerSaveOptions
            {
                Title = "Save inflated ZIP", DefaultExtension = "zip", ShowOverwritePrompt = true,
                SuggestedFileName = Path.GetFileName(txtFolderOutput.Text ?? "") is { Length: > 0 } n ? n : "inflated.zip",
                FileTypeChoices = new[] { new FilePickerFileType("ZIP Archive") { Patterns = new[] { "*.zip" } } }
            };
            var f = await StorageProvider.SaveFilePickerAsync(opts);
            var p = f?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) txtFolderOutput.Text = p;
        }
        else
        {
            var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select output folder", AllowMultiple = false });
            var p = r.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) txtFolderOutput.Text = p;
        }
    }

    void OpenFolderScreen(string? path) => Run(() => OpenFolderScreenAsync(path));

    async Task OpenFolderScreenAsync(string? path)
    {
        _folderMode = FolderMode.None;
        folderModePanel.IsVisible = true;
        folderWorkPanel.IsVisible = false;
        ShowScreen("folder");
        if (path != null) await LoadFolderAsync(path);
    }

    void ShowFolderWork()
    {
        folderModePanel.IsVisible = false;
        folderWorkPanel.IsVisible = true;
        folderStatsPanel.IsVisible = true;
        lblPerFile.IsVisible = _folderMode == FolderMode.InflateAll;
        btnFolderInflate.SetLabel(_folderMode == FolderMode.CompressEnlarge ? "COMPRESS & INFLATE" : "INFLATE ALL FILES");

        // A folder may already be loaded (history / drag-drop before the mode was chosen): fix the default output
        SetFolderOutputDefault();
    }

    void SetFolderOutputDefault()
    {
        var path = txtFolderPath.Text;
        if (string.IsNullOrEmpty(path)) return;
        if (_folderMode == FolderMode.CompressEnlarge)
        {
            var parent = Path.GetDirectoryName(path) ?? path;
            txtFolderOutput.Text = Path.Combine(parent, Path.GetFileName(path) + "_inflated.zip");
        }
        else
        {
            txtFolderOutput.Text = path + "_inflated";
        }
    }

    async Task LoadFolderAsync(string path)
    {
        if (_inflating || !Directory.Exists(path)) return;
        // Normalise "/dir/" -> "/dir" so derived output names land next to the folder, not inside it
        if (Path.GetPathRoot(path) != path) path = Path.TrimEndingDirectorySeparator(path);

        lblFolderStatus.Text = "scanning folder...";
        lblFolderStatus.Foreground = Pal.B(Pal.GreenDark);
        long total; int count;
        try { (total, count) = await Task.Run(() => FolderInflator.Scan(path)); }
        catch (Exception ex)
        {
            lblFolderStatus.Text = $"error: {ex.Message}";
            lblFolderStatus.Foreground = Pal.B(Pal.AccentRed);
            return;
        }

        History.Add(path);
        txtFolderPath.Text = path;
        _folderOriginalSize = total;
        lblFolderOrigSize.Text = $"{FormatSize(total)} across {count} files";

        SetFolderOutputDefault();

        var inv = CultureInfo.InvariantCulture;
        if (total < 1024L * 1024 * 1024) { txtFolderSize.Text = "1.00"; cmbFolderUnit.SelectedIndex = 2; }
        else { txtFolderSize.Text = (Math.Ceiling((double)total / (1024.0 * 1024 * 1024)) * 2).ToString("F2", inv); cmbFolderUnit.SelectedIndex = 2; }

        btnFolderInflate.IsEnabled = true;
        UpdateFolderStats();
        lblFolderStatus.Text = $"folder loaded — {count} files found";
        lblFolderStatus.Foreground = Pal.B(Pal.GreenDark);
    }

    void UpdateFolderStats()
    {
        if (valFolderStatTarget == null || cmbFolderUnit == null || lblFolderRatio == null) return; // during construction
        long target = GetTargetBytes(txtFolderSize, cmbFolderUnit);
        valFolderStatTarget.Text = FormatSize(target);
        if (_folderOriginalSize > 0)
        {
            valFolderStatOrig.Text = FormatSize(_folderOriginalSize);
            double ratio = target > _folderOriginalSize ? (double)target / _folderOriginalSize : 0;
            valFolderStatRatio.Text = ratio > 0 ? $"{ratio:F1}x" : "—";
            lblFolderRatio.Text = ratio > 0 ? $"{ratio:F0}x" : "";
            lblFolderRatio.Foreground = Pal.B(ratio > 100 ? Pal.AccentRed : Pal.GreenPrimary);
        }
    }

    async Task FolderInflateClickAsync()
    {
        if (_inflating) { _cts?.Cancel(); return; }
        if (string.IsNullOrEmpty(txtFolderPath.Text) || _folderMode == FolderMode.None) return;
        var src = txtFolderPath.Text!;
        var dst = txtFolderOutput.Text;
        if (string.IsNullOrEmpty(dst)) { await ShowMsg("Select an output path."); return; }
        if (!Directory.Exists(src)) { await ShowMsg("The source folder no longer exists."); return; }

        long target = GetTargetBytes(txtFolderSize, cmbFolderUnit);
        var mode = _folderMode;
        if (_inflating) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetFolderUIBusy(true);
        _inflating = true;

        try
        {
            var progress = new Progress<(double fraction, string status)>(p =>
            {
                SetProgress(folderProgressFill, folderProgressTrack, p.fraction);
                if (p.status != "done") { lblFolderStatus.Text = p.status; lblFolderStatus.Foreground = Pal.B(Pal.GreenDark); }
            });

            if (mode == FolderMode.CompressEnlarge)
            {
                await Task.Run(() => FolderInflator.CompressEnlarge(src, dst, target, progress, ct), ct);

                SetProgress(folderProgressFill, folderProgressTrack, 1.0);
                var finalSize = new FileInfo(dst).Length;
                lblFolderStatus.Text = $"done — {FormatSize(_folderOriginalSize)} folder → {FormatSize(finalSize)} ZIP";
            }
            else
            {
                int n = await Task.Run(() => FolderInflator.InflateAll(src, dst, target, progress, ct), ct);

                SetProgress(folderProgressFill, folderProgressTrack, 1.0);
                lblFolderStatus.Text = $"done — {n} files inflated to {FormatSize(target)} each";
            }
            lblFolderStatus.Foreground = Pal.B(Pal.GreenPrimary);
        }
        catch (OperationCanceledException) { lblFolderStatus.Text = "cancelled"; lblFolderStatus.Foreground = Pal.B(Pal.TextLight); }
        catch (UserMessageException ex)
        {
            lblFolderStatus.Text = $"stopped: {ex.Message.Split('\n')[0]}"; lblFolderStatus.Foreground = Pal.B(Pal.AccentRed);
            if (!_closeRequested) await ShowMsg(ex.Message);
        }
        catch (Exception ex) { lblFolderStatus.Text = $"error: {ex.Message}"; lblFolderStatus.Foreground = Pal.B(Pal.AccentRed); }
        finally { SetFolderUIBusy(false); FinishOperation(); }
    }

    void SetFolderUIBusy(bool busy)
    {
        btnFolderBrowse.IsEnabled = !busy;
        btnFolderOutputBrowse.IsEnabled = !busy; txtFolderSize.IsEnabled = !busy;
        cmbFolderUnit.IsEnabled = !busy;
        if (busy) SetProgress(folderProgressFill, folderProgressTrack, 0);
        btnFolderInflate.SetLabel(busy
            ? "CANCEL"
            : (_folderMode == FolderMode.CompressEnlarge ? "COMPRESS & INFLATE" : "INFLATE ALL FILES"));
        btnBack.IsEnabled = !busy;
    }

    // ════════════════════════════════════════════════
    //  HISTORY
    // ════════════════════════════════════════════════

    void RefreshHistoryPanel()
    {
        historyPanel.Children.Clear();
        var history = History.Load();
        if (history.Count == 0) { historyPanel.IsVisible = false; return; }

        historyPanel.IsVisible = true;
        At(historyPanel, Label("RECENT", 10, Pal.TextMid, FontWeight.SemiBold), 0, 0);

        double y = 22;
        foreach (var path in history)
        {
            bool isDir = Directory.Exists(path);
            bool exists = isDir || File.Exists(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            var lbl = new TextBlock
            {
                Text = (isDir ? "D  " : "F  ") + name, FontSize = 12.5, Width = 680, Height = 22,
                Foreground = Pal.B(exists ? Pal.GreenDark : Pal.TextLight),
                Background = Brushes.Transparent, TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = new Cursor(exists ? StandardCursorType.Hand : StandardCursorType.Arrow)
            };
            ToolTip.SetTip(lbl, path);
            DragDrop.SetAllowDrop(lbl, true);
            var captured = path;
            if (exists)
            {
                lbl.PointerEntered += (s, e) => lbl.Foreground = Pal.B(Pal.GreenPrimary);
                lbl.PointerExited += (s, e) => lbl.Foreground = Pal.B(Pal.GreenDark);
                lbl.PointerReleased += (s, e) =>
                {
                    if (_inflating) return;
                    if (isDir) OpenFolderScreen(captured);
                    else { ShowScreen("file"); LoadFile(captured); }
                };
            }
            At(historyPanel, lbl, 0, y);
            y += 24;
        }
    }
}
