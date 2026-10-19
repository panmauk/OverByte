using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace OverByte;

public partial class Form1 : Form
{
    // ── Palette ──
    static readonly Color GreenPrimary = Color.FromArgb(16, 163, 100);
    static readonly Color GreenDark = Color.FromArgb(12, 130, 80);
    static readonly Color GreenLight = Color.FromArgb(230, 248, 240);
    static readonly Color BgWhite = Color.FromArgb(247, 248, 250);
    static readonly Color BgCard = Color.White;
    static readonly Color BorderLight = Color.FromArgb(228, 230, 235);
    static readonly Color TextDark = Color.FromArgb(30, 33, 40);
    static readonly Color TextMid = Color.FromArgb(100, 105, 115);
    static readonly Color TextLight = Color.FromArgb(160, 165, 175);
    static readonly Color AccentRed = Color.FromArgb(230, 60, 60);
    static readonly Color HeaderBg = Color.FromArgb(20, 24, 30);

    // ── Cached fonts (creating Font objects inside Paint handlers leaks GDI handles) ──
    static readonly Font FontLogo = new("Segoe UI", 16f, FontStyle.Bold);
    static readonly Font FontVersion = new("Segoe UI", 7.5f);
    static readonly Font FontCardIcon = new("Segoe UI", 22f, FontStyle.Bold);
    static readonly Font FontCardTitle = new("Segoe UI Semibold", 14f);
    static readonly Font FontCardDesc = new("Segoe UI", 9f);
    static readonly Font FontPillLabel = new("Segoe UI Semibold", 7.5f);
    static readonly Font FontPillValue = new("Segoe UI Semibold", 8.5f);

    // ── Screens ──
    Panel headerPanel = null!;
    Panel menuScreen = null!;
    Panel fileScreen = null!;
    Panel folderScreen = null!;

    // ── File mode controls ──
    Panel fileStatsPanel = null!;
    TextBox txtFilePath = null!;
    OBButton btnFileBrowse = null!;
    TextBox txtFileOutput = null!;
    OBButton btnFileOutputBrowse = null!;
    TextBox txtFileSize = null!;
    ComboBox cmbFileUnit = null!;
    OBButton btnFileInflate = null!;
    Label lblFileOrigSize = null!;
    Label lblFileStatus = null!;
    Label lblFileRatio = null!;
    Label lblFileStatOrig = null!;
    Label lblFileStatTarget = null!;
    Label lblFileStatRatio = null!;
    Panel fileProgressTrack = null!;
    Panel fileProgressFill = null!;
    CheckBox chkFileOverwrite = null!;

    // ── Folder mode controls ──
    enum FolderMode { None, CompressEnlarge, InflateAll }
    FolderMode _folderMode = FolderMode.None;
    Panel folderModePanel = null!;
    Panel folderWorkPanel = null!;
    Panel folderStatsPanel = null!;
    TextBox txtFolderPath = null!;
    OBButton btnFolderBrowse = null!;
    TextBox txtFolderOutput = null!;
    OBButton btnFolderOutputBrowse = null!;
    TextBox txtFolderSize = null!;
    ComboBox cmbFolderUnit = null!;
    OBButton btnFolderInflate = null!;
    Label lblFolderOrigSize = null!;
    Label lblFolderStatus = null!;
    Label lblFolderRatio = null!;
    Label lblFolderStatOrig = null!;
    Label lblFolderStatTarget = null!;
    Label lblFolderStatRatio = null!;
    Panel folderProgressTrack = null!;
    Panel folderProgressFill = null!;

    long _fileOriginalSize;     // size of the file loaded on the FILE screen
    long _folderOriginalSize;   // total size of the folder loaded on the FOLDER screen
    CancellationTokenSource? _cts;
    bool _inflating;
    bool _closeRequested;
    Task? _inflateTask;
    OBButton? _btnBack;
    readonly ToolTip _historyTip = new();

    // ── History ──
    Panel historyPanel = null!;
    static readonly HistoryStore History = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OverByte", "history.txt"));
    const int MaxHistory = HistoryStore.MaxHistory;

    public Form1()
    {
        InitializeComponent();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer | ControlStyles.OptimizedDoubleBuffer, true);
        BuildUI();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_inflating)
        {
            var r = MessageBox.Show(
                "Inflation is still in progress.\n\n" +
                "If you close now, the operation will be cancelled and " +
                "any partially inflated files will be deleted.\n\n" +
                "Close anyway?",
                "OverByte — Still Working",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            // Never block the UI thread waiting for the worker (the folder-mode tasks resume on the
            // UI thread, so a blocking wait here deadlocks). Cancel, keep the window open and let the
            // inflate handler close the form once it has cleaned up its partial output.
            e.Cancel = true;
            if (r == DialogResult.Yes)
            {
                _closeRequested = true;
                _cts?.Cancel();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _historyTip.Dispose();
        base.OnFormClosed(e);
    }

    /// Called from the inflate handlers' finally blocks: finishes a close the user confirmed mid-operation.
    void FinishOperation()
    {
        _cts?.Dispose();
        _cts = null;
        _inflating = false;
        _inflateTask = null;
        if (_btnBack != null) _btnBack.Enabled = true;
        if (_closeRequested) { _closeRequested = false; Close(); }
    }

    void BuildUI()
    {
        Text = "OverByte";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        ClientSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BgWhite;

        BuildHeader();
        BuildMenuScreen();
        BuildFileScreen();
        BuildFolderScreen();

        // Enable drag-drop on every control of every screen ("drag & drop anywhere")
        EnableDragDropRecursive(this);

        ShowScreen("menu");
    }

    // ════════════════════════════════════════════════
    //  HEADER
    // ════════════════════════════════════════════════

    void BuildHeader()
    {
        headerPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = HeaderBg };
        var btnBack = new OBButton("<  BACK", 820, 12, 80, 32);
        btnBack.Visible = false;
        btnBack.Click += (s, e) =>
        {
            if (_inflating) return;
            ShowScreen("menu");
        };
        headerPanel.Controls.Add(btnBack);
        headerPanel.Tag = btnBack; // store ref
        _btnBack = btnBack;

        headerPanel.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, "Over", FontLogo, new Point(28, 12), Color.White);
            var overW = TextRenderer.MeasureText("Over", FontLogo).Width - 10;
            TextRenderer.DrawText(g, "Byte", FontLogo, new Point(28 + overW, 12), GreenPrimary);
            var vRect = new Rectangle(740, 18, 50, 20);
            using var pillBrush = new SolidBrush(Color.FromArgb(40, 44, 52));
            using var pillPath = RoundRect(vRect, 10);
            g.FillPath(pillBrush, pillPath);
            TextRenderer.DrawText(g, "v1.0.1", FontVersion, vRect, Color.FromArgb(120, 125, 135),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        Controls.Add(headerPanel);
    }

    void ShowScreen(string screen)
    {
        menuScreen.Visible = screen == "menu";
        fileScreen.Visible = screen == "file";
        folderScreen.Visible = screen == "folder";
        var backBtn = headerPanel.Tag as OBButton;
        if (backBtn != null) backBtn.Visible = screen != "menu";
        headerPanel.Invalidate();
        if (screen == "menu") RefreshHistoryPanel();
    }

    // ════════════════════════════════════════════════
    //  MENU SCREEN
    // ════════════════════════════════════════════════

    void BuildMenuScreen()
    {
        menuScreen = new Panel { Location = new Point(0, 56), Size = new Size(960, 584), BackColor = BgWhite };
        Controls.Add(menuScreen);

        // Title
        var lblChoose = new Label
        {
            Text = "What would you like to inflate?",
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = TextDark,
            AutoSize = true,
            BackColor = BgWhite,
            Location = new Point(0, 36)
        };
        // Center it
        lblChoose.Location = new Point((960 - TextRenderer.MeasureText(lblChoose.Text, lblChoose.Font).Width) / 2, 36);
        menuScreen.Controls.Add(lblChoose);

        var lblSub = new Label
        {
            Text = "choose a mode to get started",
            Font = new Font("Segoe UI", 10f),
            ForeColor = TextLight,
            AutoSize = true,
            BackColor = BgWhite
        };
        lblSub.Location = new Point((960 - TextRenderer.MeasureText(lblSub.Text, lblSub.Font).Width) / 2, 76);
        menuScreen.Controls.Add(lblSub);

        // Two big cards side by side
        var fileCard = MakeMenuCard(140, 116, 300, 260,
            "FILE", "Inflate a single file to\nany target size. Works with\nJPG, PNG, MP4, PDF, ISO,\nand almost any format.",
            "file_icon");
        fileCard.Click += (s, e) => ShowScreen("file");
        fileCard.Cursor = Cursors.Hand;
        menuScreen.Controls.Add(fileCard);

        var folderCard = MakeMenuCard(520, 116, 300, 260,
            "FOLDER", "Inflate an entire folder.\nCompress & enlarge into\na big ZIP, or inflate every\nfile individually.",
            "folder_icon");
        folderCard.Click += (s, e) => OpenFolderScreen(null);
        folderCard.Cursor = Cursors.Hand;
        menuScreen.Controls.Add(folderCard);

        // History panel
        historyPanel = new Panel
        {
            Location = new Point(140, 420),
            Size = new Size(680, 150),
            BackColor = BgWhite,
            Visible = false
        };
        menuScreen.Controls.Add(historyPanel);
        RefreshHistoryPanel();

        // Drag-drop hint
        var lblDrag = new Label
        {
            Text = "you can also drag & drop files or folders anywhere",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = TextLight,
            AutoSize = true,
            BackColor = BgWhite,
            UseMnemonic = false
        };
        lblDrag.Location = new Point((960 - TextRenderer.MeasureText(lblDrag.Text, lblDrag.Font).Width) / 2, 388);
        menuScreen.Controls.Add(lblDrag);
    }

    void EnableDragDropRecursive(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += HandleDragEnter;
        c.DragDrop += HandleDragDrop;
        foreach (Control child in c.Controls) EnableDragDropRecursive(child);
    }

    void HandleDragEnter(object? s, DragEventArgs e)
    {
        if (!_inflating && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    void HandleDragDrop(object? s, DragEventArgs e)
    {
        if (_inflating) return;
        var paths = e.Data?.GetData(DataFormats.FileDrop) as string[];
        if (paths?.Length > 0)
        {
            bool isDir = Directory.Exists(paths[0]);
            if (fileScreen.Visible && !isDir)
            {
                LoadFile(paths[0]);
            }
            else if (folderScreen.Visible && isDir)
            {
                if (folderWorkPanel.Visible) LoadFolder(paths[0]);
            }
            else if (isDir)
            {
                OpenFolderScreen(paths[0]);
            }
            else if (File.Exists(paths[0]))
            {
                ShowScreen("file");
                LoadFile(paths[0]);
            }
        }
    }

    Panel MakeMenuCard(int x, int y, int w, int h, string title, string desc, string icon)
    {
        var card = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = BgCard,
            Cursor = Cursors.Hand
        };

        bool hover = false;
        card.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = new Rectangle(0, 0, w - 1, h - 1);
            using var path = RoundRect(rect, 12);
            using var borderPen = new Pen(hover ? GreenPrimary : BorderLight, hover ? 2 : 1);
            g.DrawPath(borderPen, path);

            // Icon area
            var iconRect = new Rectangle(w / 2 - 30, 30, 60, 60);
            using var iconBg = new SolidBrush(GreenLight);
            using var iconPath = RoundRect(iconRect, 16);
            g.FillPath(iconBg, iconPath);

            // Icon symbol
            var sym = title == "FILE" ? "F" : "D";
            TextRenderer.DrawText(g, sym, FontCardIcon, iconRect, GreenDark,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // Title
            TextRenderer.DrawText(g, title, FontCardTitle,
                new Rectangle(0, 105, w, 30), TextDark, TextFormatFlags.HorizontalCenter);

            // Description
            TextRenderer.DrawText(g, desc, FontCardDesc,
                new Rectangle(24, 142, w - 48, 100), TextMid,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        };

        card.MouseEnter += (s, e) => { hover = true; card.Invalidate(); };
        card.MouseLeave += (s, e) => { hover = false; card.Invalidate(); };

        return card;
    }

    // ════════════════════════════════════════════════
    //  FILE SCREEN (same as before)
    // ════════════════════════════════════════════════

    void BuildFileScreen()
    {
        // Stats bar
        fileStatsPanel = new Panel { Location = new Point(0, 56), Size = new Size(960, 52), BackColor = BgCard };
        fileStatsPanel.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var pen = new Pen(BorderLight);
            g.DrawLine(pen, 0, 51, 960, 51);
            DrawStatPill(g, 28, 12, "Original", lblFileStatOrig?.Tag?.ToString() ?? "—");
            DrawStatPill(g, 260, 12, "Target", lblFileStatTarget?.Tag?.ToString() ?? "—");
            DrawStatPill(g, 492, 12, "Ratio", lblFileStatRatio?.Tag?.ToString() ?? "—");
        };
        lblFileStatOrig = new Label { Visible = false, Tag = "—" };
        lblFileStatTarget = new Label { Visible = false, Tag = "—" };
        lblFileStatRatio = new Label { Visible = false, Tag = "—" };
        fileStatsPanel.Controls.AddRange(new Control[] { lblFileStatOrig, lblFileStatTarget, lblFileStatRatio });
        Controls.Add(fileStatsPanel);

        fileScreen = new Panel { Location = new Point(0, 108), Size = new Size(960, 532), BackColor = BgWhite };
        Controls.Add(fileScreen);

        int cl = 32, cw = 896, y = 20;

        // Source card
        var srcCard = MakeCard(cl, y, cw, 110);
        fileScreen.Controls.Add(srcCard);
        srcCard.Controls.Add(MakeSectionLabel("SOURCE FILE", 24, 18));
        txtFilePath = MakeTextBox(24, 46, cw - 128, "drag and drop or browse...");
        txtFilePath.ReadOnly = true;
        srcCard.Controls.Add(txtFilePath);
        btnFileBrowse = new OBButton("BROWSE", cw - 96, 46, 72, 32);
        btnFileBrowse.Click += (s, e) =>
        {
            using var ofd = new OpenFileDialog { Title = "Select file", Filter = "All Files (*.*)|*.*" };
            if (ofd.ShowDialog() == DialogResult.OK) LoadFile(ofd.FileName);
        };
        srcCard.Controls.Add(btnFileBrowse);
        lblFileOrigSize = new Label { Text = "", Location = new Point(26, 84), AutoSize = true, Font = new Font("Segoe UI", 8f), ForeColor = TextLight, BackColor = BgCard };
        srcCard.Controls.Add(lblFileOrigSize);
        y += 126;

        // Output card
        var outCard = MakeCard(cl, y, cw, 110);
        fileScreen.Controls.Add(outCard);
        outCard.Controls.Add(MakeSectionLabel("OUTPUT", 24, 18));
        txtFileOutput = MakeTextBox(24, 46, cw - 128, "auto-generated...");
        txtFileOutput.ReadOnly = true;
        outCard.Controls.Add(txtFileOutput);
        btnFileOutputBrowse = new OBButton("SAVE AS", cw - 96, 46, 72, 32);
        btnFileOutputBrowse.Click += (s, e) =>
        {
            if (string.IsNullOrEmpty(txtFilePath.Text)) return;
            var ext = Path.GetExtension(txtFilePath.Text);
            using var sfd = new SaveFileDialog
            {
                Title = "Save inflated file",
                Filter = $"Same format (*{ext})|*{ext}|All Files (*.*)|*.*",
                FileName = Path.GetFileNameWithoutExtension(txtFilePath.Text) + "_inflated" + ext
            };
            if (sfd.ShowDialog() == DialogResult.OK) txtFileOutput.Text = sfd.FileName;
        };
        outCard.Controls.Add(btnFileOutputBrowse);
        chkFileOverwrite = new CheckBox
        {
            Text = "overwrite original file", Location = new Point(26, 84), AutoSize = true,
            Font = new Font("Segoe UI", 8.5f), ForeColor = TextMid, BackColor = BgCard, FlatStyle = FlatStyle.Flat
        };
        chkFileOverwrite.CheckedChanged += (s, e) =>
        {
            txtFileOutput.Enabled = !chkFileOverwrite.Checked;
            btnFileOutputBrowse.Enabled = !chkFileOverwrite.Checked;
        };
        outCard.Controls.Add(chkFileOverwrite);
        y += 126;

        // Target size card
        var sizeCard = MakeCard(cl, y, cw, 80);
        fileScreen.Controls.Add(sizeCard);
        sizeCard.Controls.Add(MakeSectionLabel("TARGET SIZE", 24, 16));
        txtFileSize = MakeTextBox(24, 40, 160, "1.00");
        txtFileSize.Text = "1.00"; txtFileSize.ReadOnly = false;
        txtFileSize.TextAlign = HorizontalAlignment.Right;
        txtFileSize.Font = new Font("Segoe UI Semibold", 12f);
        txtFileSize.TextChanged += (s, e) => UpdateFileStats();
        sizeCard.Controls.Add(txtFileSize);
        cmbFileUnit = new ComboBox
        {
            Location = new Point(194, 40), Size = new Size(72, 32), DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI Semibold", 10f), BackColor = BgCard, ForeColor = TextDark, FlatStyle = FlatStyle.Flat
        };
        cmbFileUnit.Items.AddRange(new object[] { "KB", "MB", "GB" });
        cmbFileUnit.SelectedIndex = 1;
        cmbFileUnit.SelectedIndexChanged += (s, e) => UpdateFileStats();
        sizeCard.Controls.Add(cmbFileUnit);
        lblFileRatio = new Label { Text = "", Location = new Point(290, 46), AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = GreenPrimary, BackColor = BgCard };
        sizeCard.Controls.Add(lblFileRatio);
        y += 96;

        // Progress
        y += 4;
        fileProgressTrack = new Panel { Location = new Point(cl, y), Size = new Size(cw, 6), BackColor = BorderLight };
        fileScreen.Controls.Add(fileProgressTrack);
        fileProgressFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 6), BackColor = GreenPrimary };
        fileProgressTrack.Controls.Add(fileProgressFill);
        y += 16;

        lblFileStatus = new Label { Text = "select a file to begin", Location = new Point(cl + 2, y), AutoSize = true, Font = new Font("Segoe UI", 8.5f), ForeColor = TextLight, BackColor = BgWhite };
        fileScreen.Controls.Add(lblFileStatus);
        y += 30;

        btnFileInflate = new OBButton("INFLATE", cl, y, cw, 48, primary: true) { Enabled = false };
        btnFileInflate.Click += BtnFileInflate_Click;
        fileScreen.Controls.Add(btnFileInflate);
    }

    // ════════════════════════════════════════════════
    //  FOLDER SCREEN
    // ════════════════════════════════════════════════

    void BuildFolderScreen()
    {
        // Stats bar
        folderStatsPanel = new Panel { Location = new Point(0, 56), Size = new Size(960, 52), BackColor = BgCard, Visible = false };
        folderStatsPanel.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var pen = new Pen(BorderLight);
            g.DrawLine(pen, 0, 51, 960, 51);
            DrawStatPill(g, 28, 12, "Original", lblFolderStatOrig?.Tag?.ToString() ?? "—");
            DrawStatPill(g, 260, 12, "Target", lblFolderStatTarget?.Tag?.ToString() ?? "—");
            DrawStatPill(g, 492, 12, "Ratio", lblFolderStatRatio?.Tag?.ToString() ?? "—");
        };
        lblFolderStatOrig = new Label { Visible = false, Tag = "—" };
        lblFolderStatTarget = new Label { Visible = false, Tag = "—" };
        lblFolderStatRatio = new Label { Visible = false, Tag = "—" };
        folderStatsPanel.Controls.AddRange(new Control[] { lblFolderStatOrig, lblFolderStatTarget, lblFolderStatRatio });
        Controls.Add(folderStatsPanel);

        folderScreen = new Panel { Location = new Point(0, 108), Size = new Size(960, 532), BackColor = BgWhite };
        Controls.Add(folderScreen);

        // ── Sub-mode selection panel ──
        folderModePanel = new Panel { Location = new Point(0, 0), Size = new Size(960, 532), BackColor = BgWhite };
        folderScreen.Controls.Add(folderModePanel);

        var lblFolderTitle = new Label
        {
            Text = "Choose folder inflation mode",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = TextDark, AutoSize = true, BackColor = BgWhite
        };
        lblFolderTitle.Location = new Point((960 - TextRenderer.MeasureText(lblFolderTitle.Text, lblFolderTitle.Font).Width) / 2, 40);
        folderModePanel.Controls.Add(lblFolderTitle);

        var compressCard = MakeMenuCard(140, 110, 300, 280,
            "COMPRESS", "ZIP the entire folder into\na single archive, then inflate\nthe ZIP to your target size.\nGreat for sending one big file.",
            "zip");
        compressCard.Click += (s, e) => { _folderMode = FolderMode.CompressEnlarge; ShowFolderWork(); };
        folderModePanel.Controls.Add(compressCard);

        var inflateAllCard = MakeMenuCard(520, 110, 300, 280,
            "INFLATE ALL", "Inflate every file inside\nthe folder individually.\nEach file grows to the\ntarget size you set.",
            "files");
        inflateAllCard.Click += (s, e) => { _folderMode = FolderMode.InflateAll; ShowFolderWork(); };
        folderModePanel.Controls.Add(inflateAllCard);

        // ── Work panel (shown after sub-mode chosen) ──
        folderWorkPanel = new Panel { Location = new Point(0, 0), Size = new Size(960, 532), BackColor = BgWhite, Visible = false };
        folderScreen.Controls.Add(folderWorkPanel);

        int cl = 32, cw = 896, y = 20;

        // Source folder card
        var srcCard = MakeCard(cl, y, cw, 110);
        folderWorkPanel.Controls.Add(srcCard);
        srcCard.Controls.Add(MakeSectionLabel("SOURCE FOLDER", 24, 18));
        txtFolderPath = MakeTextBox(24, 46, cw - 128, "select a folder...");
        txtFolderPath.ReadOnly = true;
        srcCard.Controls.Add(txtFolderPath);
        btnFolderBrowse = new OBButton("BROWSE", cw - 96, 46, 72, 32);
        btnFolderBrowse.Click += (s, e) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select folder to inflate" };
            if (fbd.ShowDialog() == DialogResult.OK) LoadFolder(fbd.SelectedPath);
        };
        srcCard.Controls.Add(btnFolderBrowse);
        lblFolderOrigSize = new Label { Text = "", Location = new Point(26, 84), AutoSize = true, Font = new Font("Segoe UI", 8f), ForeColor = TextLight, BackColor = BgCard };
        srcCard.Controls.Add(lblFolderOrigSize);
        y += 126;

        // Output card
        var outCard = MakeCard(cl, y, cw, 90);
        folderWorkPanel.Controls.Add(outCard);
        outCard.Controls.Add(MakeSectionLabel("OUTPUT", 24, 18));
        txtFolderOutput = MakeTextBox(24, 46, cw - 128, "auto-generated...");
        txtFolderOutput.ReadOnly = true;
        outCard.Controls.Add(txtFolderOutput);
        btnFolderOutputBrowse = new OBButton("SAVE AS", cw - 96, 46, 72, 32);
        btnFolderOutputBrowse.Click += (s, e) =>
        {
            if (_folderMode == FolderMode.CompressEnlarge)
            {
                using var sfd = new SaveFileDialog { Title = "Save inflated ZIP", Filter = "ZIP Archive (*.zip)|*.zip" };
                if (sfd.ShowDialog() == DialogResult.OK) txtFolderOutput.Text = sfd.FileName;
            }
            else
            {
                using var fbd = new FolderBrowserDialog { Description = "Select output folder" };
                if (fbd.ShowDialog() == DialogResult.OK) txtFolderOutput.Text = fbd.SelectedPath;
            }
        };
        outCard.Controls.Add(btnFolderOutputBrowse);
        y += 106;

        // Target size card
        var sizeCard = MakeCard(cl, y, cw, 80);
        folderWorkPanel.Controls.Add(sizeCard);
        sizeCard.Controls.Add(MakeSectionLabel("TARGET SIZE", 24, 16));
        txtFolderSize = MakeTextBox(24, 40, 160, "1.00");
        txtFolderSize.Text = "1.00"; txtFolderSize.ReadOnly = false;
        txtFolderSize.TextAlign = HorizontalAlignment.Right;
        txtFolderSize.Font = new Font("Segoe UI Semibold", 12f);
        txtFolderSize.TextChanged += (s, e) => UpdateFolderStats();
        sizeCard.Controls.Add(txtFolderSize);
        cmbFolderUnit = new ComboBox
        {
            Location = new Point(194, 40), Size = new Size(72, 32), DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI Semibold", 10f), BackColor = BgCard, ForeColor = TextDark, FlatStyle = FlatStyle.Flat
        };
        cmbFolderUnit.Items.AddRange(new object[] { "KB", "MB", "GB" });
        cmbFolderUnit.SelectedIndex = 2;
        cmbFolderUnit.SelectedIndexChanged += (s, e) => UpdateFolderStats();
        sizeCard.Controls.Add(cmbFolderUnit);
        lblFolderRatio = new Label { Text = "", Location = new Point(290, 46), AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = GreenPrimary, BackColor = BgCard };
        sizeCard.Controls.Add(lblFolderRatio);

        // Label for inflate-all mode
        var lblPerFile = new Label
        {
            Text = "(per file)", Location = new Point(380, 46), AutoSize = true,
            Font = new Font("Segoe UI", 8.5f), ForeColor = TextLight, BackColor = BgCard, Tag = "perfile"
        };
        sizeCard.Controls.Add(lblPerFile);
        y += 96;

        // Progress
        y += 4;
        folderProgressTrack = new Panel { Location = new Point(cl, y), Size = new Size(cw, 6), BackColor = BorderLight };
        folderWorkPanel.Controls.Add(folderProgressTrack);
        folderProgressFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 6), BackColor = GreenPrimary };
        folderProgressTrack.Controls.Add(folderProgressFill);
        y += 16;

        lblFolderStatus = new Label { Text = "select a folder to begin", Location = new Point(cl + 2, y), AutoSize = true, Font = new Font("Segoe UI", 8.5f), ForeColor = TextLight, BackColor = BgWhite };
        folderWorkPanel.Controls.Add(lblFolderStatus);
        y += 30;

        btnFolderInflate = new OBButton("INFLATE", cl, y, cw, 48, primary: true) { Enabled = false };
        btnFolderInflate.Click += BtnFolderInflate_Click;
        folderWorkPanel.Controls.Add(btnFolderInflate);
    }

    void ShowFolderWork()
    {
        folderModePanel.Visible = false;
        folderWorkPanel.Visible = true;
        folderStatsPanel.Visible = true;

        // Update per-file label visibility
        foreach (Control c in folderWorkPanel.Controls)
        {
            if (c is Panel card)
                foreach (Control cc in card.Controls)
                    if (cc is Label lbl && lbl.Tag?.ToString() == "perfile")
                        lbl.Visible = _folderMode == FolderMode.InflateAll;
        }

        // Update button label
        btnFolderInflate.SetLabel(_folderMode == FolderMode.CompressEnlarge ? "COMPRESS & INFLATE" : "INFLATE ALL FILES");

        // A folder may already be loaded (history / drag-drop before the mode was chosen): fix the default output
        SetFolderOutputDefault();
    }

    // ════════════════════════════════════════════════
    //  SHARED UI HELPERS
    // ════════════════════════════════════════════════

    Panel MakeCard(int x, int y, int w, int h)
    {
        var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = BgCard };
        p.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(BorderLight);
            using var path = RoundRect(new Rectangle(0, 0, p.Width - 1, p.Height - 1), 8);
            g.DrawPath(pen, path);
        };
        return p;
    }

    static Label MakeSectionLabel(string text, int x, int y) => new()
    {
        Text = text, Location = new Point(x, y), AutoSize = true,
        Font = new Font("Segoe UI Semibold", 7.5f), ForeColor = TextMid, BackColor = BgCard
    };

    static TextBox MakeTextBox(int x, int y, int w, string placeholder) => new()
    {
        Location = new Point(x, y), Size = new Size(w, 32), Font = new Font("Segoe UI", 10f),
        BackColor = Color.FromArgb(250, 251, 253), ForeColor = TextDark, BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = placeholder
    };

    void DrawStatPill(Graphics g, int x, int y, string label, string value)
    {
        var r = new Rectangle(x, y, 210, 28);
        using var path = RoundRect(r, 14);
        using var brush = new SolidBrush(GreenLight);
        g.FillPath(brush, path);
        TextRenderer.DrawText(g, label, FontPillLabel, new Point(x + 14, y + 6), TextMid);
        var vs = TextRenderer.MeasureText(value, FontPillValue);
        TextRenderer.DrawText(g, value, FontPillValue, new Point(x + 196 - vs.Width, y + 5), GreenDark);
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ════════════════════════════════════════════════
    //  FILE MODE LOGIC
    // ════════════════════════════════════════════════

    void OpenFolderScreen(string? path)
    {
        _folderMode = FolderMode.None;
        folderModePanel.Visible = true;
        folderWorkPanel.Visible = false;
        folderStatsPanel.Visible = false;
        ShowScreen("folder");
        if (path != null) LoadFolder(path);
    }

    void LoadFile(string path)
    {
        if (_inflating || !File.Exists(path)) return;
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception ex)
        {
            lblFileStatus.Text = $"error: {ex.Message}";
            lblFileStatus.ForeColor = AccentRed;
            return;
        }
        SaveToHistory(path);
        txtFilePath.Text = path;
        _fileOriginalSize = size;
        lblFileOrigSize.Text = FormatSize(_fileOriginalSize);

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        txtFileOutput.Text = Path.Combine(dir, name + "_inflated" + ext);

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_fileOriginalSize < 1024 * 1024) { txtFileSize.Text = "10.00"; cmbFileUnit.SelectedIndex = 1; }
        else if (_fileOriginalSize < 1024L * 1024 * 1024) { txtFileSize.Text = "1.00"; cmbFileUnit.SelectedIndex = 2; }
        else { txtFileSize.Text = (Math.Ceiling((double)_fileOriginalSize / (1024.0 * 1024 * 1024)) * 2).ToString("F2", inv); cmbFileUnit.SelectedIndex = 2; }

        btnFileInflate.Enabled = true;
        UpdateFileStats();
        lblFileStatus.Text = "file loaded — set target and inflate";
        lblFileStatus.ForeColor = GreenDark;
    }

    void UpdateFileStats()
    {
        long target = GetTargetBytes(txtFileSize, cmbFileUnit);
        lblFileStatTarget!.Tag = FormatSize(target);
        if (_fileOriginalSize > 0)
        {
            lblFileStatOrig!.Tag = FormatSize(_fileOriginalSize);
            double ratio = target > _fileOriginalSize ? (double)target / _fileOriginalSize : 0;
            lblFileStatRatio!.Tag = ratio > 0 ? $"{ratio:F1}x" : "—";
            lblFileRatio.Text = ratio > 0 ? $"{ratio:F0}x inflation" : "";
            lblFileRatio.ForeColor = ratio > 100 ? AccentRed : GreenPrimary;
        }
        fileStatsPanel.Invalidate();
    }

    async void BtnFileInflate_Click(object? sender, EventArgs e)
    {
        if (_inflating) { _cts?.Cancel(); return; }
        if (string.IsNullOrEmpty(txtFilePath.Text)) return;
        var src = txtFilePath.Text;
        var dst = chkFileOverwrite.Checked ? src : txtFileOutput.Text;
        if (string.IsNullOrEmpty(dst)) { ShowMsg("Select an output path."); return; }

        // Re-read the real size now: the file may have changed since it was loaded
        long srcSize;
        try
        {
            if (!File.Exists(src)) { ShowMsg("The source file no longer exists."); return; }
            srcSize = new FileInfo(src).Length;
        }
        catch (Exception ex) { ShowMsg($"Cannot read the source file:\n{ex.Message}"); return; }
        _fileOriginalSize = srcSize;
        UpdateFileStats();

        long target = GetTargetBytes(txtFileSize, cmbFileUnit);
        if (target <= srcSize) { ShowMsg($"Target must exceed original ({FormatSize(srcSize)})."); return; }
        long padding = target - srcSize;

        // Warn about WIC 4GB limit for image formats
        var ext = Path.GetExtension(src).ToLowerInvariant();
        bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".jfif" or ".bmp" or ".gif" or ".tiff" or ".tif" or ".webp";
        if (isImage && target > 3_990_000_000L)
        {
            var r = MessageBox.Show(
                $"Windows Photos can't open image files larger than ~4 GB (Windows limit).\n\n" +
                $"The file will still be created at {FormatSize(target)} and will open fine in browsers (Chrome, Edge, Firefox) and apps like IrfanView.\n\n" +
                $"Continue?",
                "OverByte — WIC Limit", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return;
        }
        else if (target > 5L * 1024 * 1024 * 1024)
        {
            if (MessageBox.Show($"Creating {FormatSize(target)} file. Continue?", "OverByte", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        }

        // The output is staged in a temp file next to the destination, so the full target size is needed
        if (!CheckDiskSpace(dst, target)) return;

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
                lblFileStatus.ForeColor = GreenDark;
            });
            _inflateTask = Task.Run(() => Inflator.InflateFile(src, dst, padding, progress, ct), ct);
            await _inflateTask;

            SetProgress(fileProgressFill, fileProgressTrack, 1.0);
            lblFileStatus.Text = $"done — {FormatSize(srcSize)} → {FormatSize(new FileInfo(dst).Length)}";
            lblFileStatus.ForeColor = GreenPrimary;
            if (!_closeRequested) System.Media.SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException) { lblFileStatus.Text = "cancelled"; lblFileStatus.ForeColor = TextLight; }
        catch (UserMessageException ex) { lblFileStatus.Text = $"error: {ex.Message}"; lblFileStatus.ForeColor = AccentRed; ShowMsg(ex.Message); }
        catch (Exception ex) { lblFileStatus.Text = $"error: {ex.Message}"; lblFileStatus.ForeColor = AccentRed; }
        finally { SetFileUIBusy(false); FinishOperation(); }
    }

    void SetFileUIBusy(bool busy)
    {
        btnFileBrowse.Enabled = !busy;
        btnFileOutputBrowse.Enabled = !busy && !chkFileOverwrite.Checked;
        txtFileSize.Enabled = !busy;
        cmbFileUnit.Enabled = !busy; chkFileOverwrite.Enabled = !busy;
        if (busy) SetProgress(fileProgressFill, fileProgressTrack, 0);
        btnFileInflate.SetLabel(busy ? "CANCEL" : "INFLATE");
        if (_btnBack != null) _btnBack.Enabled = !busy;
        TaskbarProgress.SetState(Handle, busy);
    }

    // ════════════════════════════════════════════════
    //  FOLDER MODE LOGIC
    // ════════════════════════════════════════════════

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

    void LoadFolder(string path)
    {
        if (_inflating || !Directory.Exists(path)) return;
        // Normalise "C:\dir\" -> "C:\dir" so derived output names land next to the folder, not inside it
        if (Path.GetPathRoot(path) != path) path = Path.TrimEndingDirectorySeparator(path);

        long total; int count;
        var oldCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try { (total, count) = FolderInflator.Scan(path); }
        catch (Exception ex)
        {
            lblFolderStatus.Text = $"error: {ex.Message}";
            lblFolderStatus.ForeColor = AccentRed;
            return;
        }
        finally { Cursor = oldCursor; }

        SaveToHistory(path);
        txtFolderPath.Text = path;
        _folderOriginalSize = total;
        lblFolderOrigSize.Text = $"{FormatSize(total)} across {count} files";

        SetFolderOutputDefault();

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (total < 1024L * 1024 * 1024) { txtFolderSize.Text = "1.00"; cmbFolderUnit.SelectedIndex = 2; }
        else { txtFolderSize.Text = (Math.Ceiling((double)total / (1024.0 * 1024 * 1024)) * 2).ToString("F2", inv); cmbFolderUnit.SelectedIndex = 2; }

        btnFolderInflate.Enabled = true;
        UpdateFolderStats();
        lblFolderStatus.Text = $"folder loaded — {count} files found";
        lblFolderStatus.ForeColor = GreenDark;
    }

    void UpdateFolderStats()
    {
        long target = GetTargetBytes(txtFolderSize, cmbFolderUnit);
        lblFolderStatTarget!.Tag = FormatSize(target);
        if (_folderOriginalSize > 0)
        {
            lblFolderStatOrig!.Tag = FormatSize(_folderOriginalSize);
            double ratio = target > _folderOriginalSize ? (double)target / _folderOriginalSize : 0;
            lblFolderStatRatio!.Tag = ratio > 0 ? $"{ratio:F1}x" : "—";
            lblFolderRatio.Text = ratio > 0 ? $"{ratio:F0}x" : "";
            lblFolderRatio.ForeColor = ratio > 100 ? AccentRed : GreenPrimary;
        }
        folderStatsPanel.Invalidate();
    }

    async void BtnFolderInflate_Click(object? sender, EventArgs e)
    {
        if (_inflating) { _cts?.Cancel(); return; }
        if (string.IsNullOrEmpty(txtFolderPath.Text) || _folderMode == FolderMode.None) return;
        var src = txtFolderPath.Text;
        var dst = txtFolderOutput.Text;
        if (string.IsNullOrEmpty(dst)) { ShowMsg("Select an output path."); return; }
        if (!Directory.Exists(src)) { ShowMsg("The source folder no longer exists."); return; }

        long target = GetTargetBytes(txtFolderSize, cmbFolderUnit);
        var mode = _folderMode;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetFolderUIBusy(true);
        _inflating = true;

        try
        {
            var progress = new Progress<(double fraction, string status)>(p =>
            {
                SetProgress(folderProgressFill, folderProgressTrack, p.fraction);
                if (p.status != "done") { lblFolderStatus.Text = p.status; lblFolderStatus.ForeColor = GreenDark; }
            });

            if (mode == FolderMode.CompressEnlarge)
            {
                _inflateTask = Task.Run(() => FolderInflator.CompressEnlarge(src, dst, target, progress, ct), ct);
                await _inflateTask;

                SetProgress(folderProgressFill, folderProgressTrack, 1.0);
                var finalSize = new FileInfo(dst).Length;
                lblFolderStatus.Text = $"done — {FormatSize(_folderOriginalSize)} folder → {FormatSize(finalSize)} ZIP";
            }
            else
            {
                int n = 0;
                _inflateTask = Task.Run(() => { n = FolderInflator.InflateAll(src, dst, target, progress, ct); }, ct);
                await _inflateTask;

                SetProgress(folderProgressFill, folderProgressTrack, 1.0);
                lblFolderStatus.Text = $"done — {n} files inflated to {FormatSize(target)} each";
            }
            lblFolderStatus.ForeColor = GreenPrimary;
            if (!_closeRequested) System.Media.SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException) { lblFolderStatus.Text = "cancelled"; lblFolderStatus.ForeColor = TextLight; }
        catch (UserMessageException ex) { lblFolderStatus.Text = $"stopped: {ex.Message.Split('\n')[0]}"; lblFolderStatus.ForeColor = AccentRed; ShowMsg(ex.Message); }
        catch (Exception ex) { lblFolderStatus.Text = $"error: {ex.Message}"; lblFolderStatus.ForeColor = AccentRed; }
        finally { SetFolderUIBusy(false); FinishOperation(); }
    }

    void SetFolderUIBusy(bool busy)
    {
        btnFolderBrowse.Enabled = !busy;
        btnFolderOutputBrowse.Enabled = !busy; txtFolderSize.Enabled = !busy;
        cmbFolderUnit.Enabled = !busy;
        if (busy) SetProgress(folderProgressFill, folderProgressTrack, 0);
        btnFolderInflate.SetLabel(busy
            ? "CANCEL"
            : (_folderMode == FolderMode.CompressEnlarge ? "COMPRESS & INFLATE" : "INFLATE ALL FILES"));
        if (_btnBack != null) _btnBack.Enabled = !busy;
        TaskbarProgress.SetState(Handle, busy);
    }

    // ════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════

    static long GetTargetBytes(TextBox txt, ComboBox cmb) =>
        Inflator.ParseTargetBytes(txt.Text, cmb.SelectedIndex);

    void SetProgress(Panel fill, Panel track, double pct)
    {
        if (double.IsNaN(pct) || pct < 0) pct = 0;
        fill.Size = new Size((int)(track.Width * Math.Min(1.0, pct)), fill.Height);
        TaskbarProgress.SetValue(Handle, Math.Min(1.0, pct));
    }

    static void ShowMsg(string msg) =>
        MessageBox.Show(msg, "OverByte", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    static string FormatSize(long bytes) => Inflator.FormatSize(bytes);

    /// Returns true if there's enough space, false if not enough room (the user is told why).
    static bool CheckDiskSpace(string outputPath, long requiredBytes)
    {
        var msg = Inflator.CheckDiskSpace(outputPath, requiredBytes);
        if (msg == null) return true;
        ShowMsg(msg);
        return false;
    }

    // ════════════════════════════════════════════════
    //  HISTORY
    // ════════════════════════════════════════════════

    static List<string> LoadHistory() => History.Load();

    static void SaveToHistory(string path) => History.Add(path);


    void RefreshHistoryPanel()
    {
        foreach (Control old in historyPanel.Controls.Cast<Control>().ToArray())
        {
            historyPanel.Controls.Remove(old);
            old.Dispose();
        }
        var history = LoadHistory();
        if (history.Count == 0) { historyPanel.Visible = false; return; }

        historyPanel.Visible = true;
        var lblTitle = new Label
        {
            Text = "RECENT",
            Font = new Font("Segoe UI Semibold", 7.5f),
            ForeColor = TextMid,
            AutoSize = true,
            Location = new Point(0, 0),
            BackColor = BgWhite
        };
        historyPanel.Controls.Add(lblTitle);

        int y = 22;
        foreach (var path in history)
        {
            bool isDir = Directory.Exists(path);
            bool exists = isDir || File.Exists(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            var lbl = new Label
            {
                Text = (isDir ? "D  " : "F  ") + name,
                Font = new Font("Segoe UI", 9f),
                ForeColor = exists ? GreenDark : TextLight,
                AutoSize = false,
                Size = new Size(680, 22),
                Location = new Point(0, y),
                BackColor = BgWhite,
                Cursor = exists ? Cursors.Hand : Cursors.Default
            };
            var captured = path;
            if (exists)
            {
                lbl.MouseEnter += (s, e) => { lbl.ForeColor = GreenPrimary; };
                lbl.MouseLeave += (s, e) => { lbl.ForeColor = GreenDark; };
                lbl.Click += (s, e) =>
                {
                    if (isDir)
                    {
                        OpenFolderScreen(captured);
                    }
                    else
                    {
                        ShowScreen("file");
                        LoadFile(captured);
                    }
                };
            }

            // Tooltip with full path
            _historyTip.SetToolTip(lbl, path);
            lbl.AllowDrop = true;
            lbl.DragEnter += HandleDragEnter;
            lbl.DragDrop += HandleDragDrop;

            historyPanel.Controls.Add(lbl);
            y += 24;
        }
    }
}

// ══════════════════════════════════════════════════
//  Custom Button
// ══════════════════════════════════════════════════

internal class OBButton : Control
{
    bool _hover, _pressed;
    readonly bool _primary;
    string _label;

    static readonly Color GreenPrimary = Color.FromArgb(16, 163, 100);
    static readonly Color GreenHover = Color.FromArgb(14, 145, 88);
    static readonly Color GreenPress = Color.FromArgb(10, 120, 72);
    static readonly Color SecBg = Color.FromArgb(247, 248, 250);
    static readonly Color SecHover = Color.FromArgb(237, 238, 242);
    static readonly Color SecBorder = Color.FromArgb(210, 213, 220);
    static readonly Color SecBorderHover = Color.FromArgb(16, 163, 100);
    static readonly Font FontPrimary = new("Segoe UI Semibold", 11f);
    static readonly Font FontSecondary = new("Segoe UI Semibold", 8f);

    public OBButton(string label, int x, int y, int w, int h, bool primary = false)
    {
        _label = label; _primary = primary;
        Location = new Point(x, y); Size = new Size(w, h);
        Cursor = Cursors.Hand; DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer, true);
    }

    public void SetLabel(string l) { _label = l; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundRect(rect, 6);

        Color bg, fg, border;
        if (!Enabled) { bg = Color.FromArgb(240, 241, 243); fg = Color.FromArgb(180, 183, 190); border = Color.FromArgb(228, 230, 235); }
        else if (_primary) { bg = _pressed ? GreenPress : (_hover ? GreenHover : GreenPrimary); fg = Color.White; border = bg; }
        else { bg = _pressed ? SecHover : (_hover ? SecHover : SecBg); fg = _hover ? GreenPrimary : Color.FromArgb(60, 63, 70); border = _hover ? SecBorderHover : SecBorder; }

        using var bgBrush = new SolidBrush(bg);
        using var borderPen = new Pen(border);
        g.FillPath(bgBrush, path); g.DrawPath(borderPen, path);

        var font = _primary ? FontPrimary : FontSecondary;
        TextRenderer.DrawText(g, _label, font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); }
}

// ══════════════════════════════════════════════════
//  Windows Taskbar Progress (ITaskbarList3)
// ══════════════════════════════════════════════════

internal static class TaskbarProgress
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance { }

    private static readonly ITaskbarList3? _instance;

    static TaskbarProgress()
    {
        try { _instance = (ITaskbarList3)new TaskbarInstance(); _instance.HrInit(); }
        catch { _instance = null; }
    }

    public static void SetValue(IntPtr hwnd, double pct)
    {
        _instance?.SetProgressValue(hwnd, (ulong)(pct * 10000), 10000);
    }

    public static void SetState(IntPtr hwnd, bool active)
    {
        _instance?.SetProgressState(hwnd, active ? 2 /* Normal */ : 0 /* NoProgress */);
    }
}

