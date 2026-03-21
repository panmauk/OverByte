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

    long _originalSize;
    CancellationTokenSource? _cts;
    bool _inflating;
    Task? _inflateTask;
    readonly List<string> _activeOutputFiles = new();

    // ── History ──
    Panel historyPanel = null!;
    static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OverByte", "history.txt");
    const int MaxHistory = 5;

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
            if (r != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            // Cancel the operation and wait for file handles to be released
            _cts?.Cancel();
            try { _inflateTask?.GetAwaiter().GetResult(); } catch { /* swallow cancel/errors */ }
            // Delete partial output files
            foreach (var f in _activeOutputFiles)
            {
                try
                {
                    if (File.Exists(f)) File.Delete(f);
                    else if (Directory.Exists(f)) Directory.Delete(f, recursive: true);
                }
                catch { /* best effort */ }
            }
        }
        base.OnFormClosing(e);
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

        // Enable drag-drop on all screens
        EnablePanelDragDrop(menuScreen);
        EnablePanelDragDrop(fileScreen);
        EnablePanelDragDrop(folderScreen);
        EnablePanelDragDrop(folderModePanel);
        EnablePanelDragDrop(folderWorkPanel);

        ShowScreen("menu");
    }

    // ════════════════════════════════════════════════
    //  HEADER
    // ════════════════════════════════════════════════

    void BuildHeader()
    {
        headerPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = HeaderBg };
        OBButton? btnBack = null;
        btnBack = new OBButton("<  BACK", 820, 12, 80, 32);
        btnBack.Visible = false;
        btnBack.Click += (s, e) =>
        {
            ShowScreen("menu");
            btnBack.Visible = false;
        };
        headerPanel.Controls.Add(btnBack);
        headerPanel.Tag = btnBack; // store ref

        headerPanel.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, "Over", new Font("Segoe UI", 16f, FontStyle.Bold), new Point(28, 12), Color.White);
            var overW = TextRenderer.MeasureText("Over", new Font("Segoe UI", 16f, FontStyle.Bold)).Width - 10;
            TextRenderer.DrawText(g, "Byte", new Font("Segoe UI", 16f, FontStyle.Bold), new Point(28 + overW, 12), GreenPrimary);
            var vRect = new Rectangle(740, 18, 50, 20);
            using var pillBrush = new SolidBrush(Color.FromArgb(40, 44, 52));
            using var pillPath = RoundRect(vRect, 10);
            g.FillPath(pillBrush, pillPath);
            TextRenderer.DrawText(g, "v1.0", new Font("Segoe UI", 7.5f), vRect, Color.FromArgb(120, 125, 135),
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
            Location = new Point(0, 80)
        };
        // Center it
        lblChoose.Location = new Point((960 - TextRenderer.MeasureText(lblChoose.Text, lblChoose.Font).Width) / 2, 80);
        menuScreen.Controls.Add(lblChoose);

        var lblSub = new Label
        {
            Text = "choose a mode to get started",
            Font = new Font("Segoe UI", 10f),
            ForeColor = TextLight,
            AutoSize = true,
            BackColor = BgWhite
        };
        lblSub.Location = new Point((960 - TextRenderer.MeasureText(lblSub.Text, lblSub.Font).Width) / 2, 120);
        menuScreen.Controls.Add(lblSub);

        // Two big cards side by side
        var fileCard = MakeMenuCard(140, 190, 300, 260,
            "FILE", "Inflate a single file to\nany target size. Works with\nJPG, PNG, MP4, PDF, ISO,\nand almost any format.",
            "file_icon");
        fileCard.Click += (s, e) => ShowScreen("file");
        fileCard.Cursor = Cursors.Hand;
        menuScreen.Controls.Add(fileCard);

        var folderCard = MakeMenuCard(520, 190, 300, 260,
            "FOLDER", "Inflate an entire folder.\nCompress & enlarge into\na big ZIP, or inflate every\nfile individually.",
            "folder_icon");
        folderCard.Click += (s, e) =>
        {
            _folderMode = FolderMode.None;
            folderModePanel.Visible = true;
            folderWorkPanel.Visible = false;
            folderStatsPanel.Visible = false;
            ShowScreen("folder");
        };
        folderCard.Cursor = Cursors.Hand;
        menuScreen.Controls.Add(folderCard);

        // History panel
        historyPanel = new Panel
        {
            Location = new Point(140, 470),
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
        lblDrag.Location = new Point((960 - TextRenderer.MeasureText(lblDrag.Text, lblDrag.Font).Width) / 2, 480);
        menuScreen.Controls.Add(lblDrag);

        // Drag-drop everywhere
        AllowDrop = true;
        DragEnter += HandleDragEnter;
        DragDrop += HandleDragDrop;
    }

    void EnablePanelDragDrop(Panel panel)
    {
        panel.AllowDrop = true;
        panel.DragEnter += HandleDragEnter;
        panel.DragDrop += HandleDragDrop;
    }

    void HandleDragEnter(object? s, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    void HandleDragDrop(object? s, DragEventArgs e)
    {
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
                _folderMode = FolderMode.None;
                folderModePanel.Visible = true;
                folderWorkPanel.Visible = false;
                folderStatsPanel.Visible = false;
                ShowScreen("folder");
                LoadFolder(paths[0]);
            }
            else
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
            var iconFont = new Font("Segoe UI", 22f, FontStyle.Bold);
            var sym = title == "FILE" ? "F" : "D";
            TextRenderer.DrawText(g, sym, iconFont, iconRect, GreenDark,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // Title
            TextRenderer.DrawText(g, title, new Font("Segoe UI Semibold", 14f),
                new Rectangle(0, 105, w, 30), TextDark, TextFormatFlags.HorizontalCenter);

            // Description
            TextRenderer.DrawText(g, desc, new Font("Segoe UI", 9f),
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
        TextRenderer.DrawText(g, label, new Font("Segoe UI Semibold", 7.5f), new Point(x + 14, y + 6), TextMid);
        var vs = TextRenderer.MeasureText(value, new Font("Segoe UI Semibold", 8.5f));
        TextRenderer.DrawText(g, value, new Font("Segoe UI Semibold", 8.5f), new Point(x + 196 - vs.Width, y + 5), GreenDark);
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

    void LoadFile(string path)
    {
        if (!File.Exists(path)) return;
        SaveToHistory(path);
        txtFilePath.Text = path;
        _originalSize = new FileInfo(path).Length;
        lblFileOrigSize.Text = FormatSize(_originalSize);

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        txtFileOutput.Text = Path.Combine(dir, name + "_inflated" + ext);

        if (_originalSize < 1024 * 1024) { txtFileSize.Text = "10.00"; cmbFileUnit.SelectedIndex = 1; }
        else if (_originalSize < 1024L * 1024 * 1024) { txtFileSize.Text = "1.00"; cmbFileUnit.SelectedIndex = 2; }
        else { txtFileSize.Text = (Math.Ceiling((double)_originalSize / (1024.0 * 1024 * 1024)) * 2).ToString("F2"); cmbFileUnit.SelectedIndex = 2; }

        btnFileInflate.Enabled = true;
        UpdateFileStats();
        lblFileStatus.Text = "file loaded — set target and inflate";
        lblFileStatus.ForeColor = GreenDark;
    }

    void UpdateFileStats()
    {
        long target = GetTargetBytes(txtFileSize, cmbFileUnit);
        lblFileStatTarget!.Tag = FormatSize(target);
        if (_originalSize > 0)
        {
            lblFileStatOrig!.Tag = FormatSize(_originalSize);
            double ratio = target > _originalSize ? (double)target / _originalSize : 0;
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

        long target = GetTargetBytes(txtFileSize, cmbFileUnit);
        if (target <= _originalSize) { ShowMsg($"Target must exceed original ({FormatSize(_originalSize)})."); return; }
        long padding = target - _originalSize;

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

        if (!CheckDiskSpace(dst, target)) return;

        SetFileUIBusy(true);
        _cts = new CancellationTokenSource();
        _inflating = true;
        _activeOutputFiles.Clear();
        _activeOutputFiles.Add(dst);
        try
        {
            var progress = new Progress<(long written, string status)>(p =>
            {
                SetProgress(fileProgressFill, fileProgressTrack, (double)p.written / padding);
                lblFileStatus.Text = $"inflating... {FormatSize(p.written)} / {FormatSize(padding)}";
                lblFileStatus.ForeColor = GreenDark;
            });
            _inflateTask = Task.Run(() => InflateFile(src, dst, padding, progress, _cts.Token), _cts.Token);
            await _inflateTask;

            SetProgress(fileProgressFill, fileProgressTrack, 1.0);
            lblFileStatus.Text = $"done — {FormatSize(_originalSize)} → {FormatSize(new FileInfo(dst).Length)}";
            lblFileStatus.ForeColor = GreenPrimary;
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException) { lblFileStatus.Text = "cancelled"; lblFileStatus.ForeColor = TextLight; }
        catch (Exception ex) { lblFileStatus.Text = $"error: {ex.Message}"; lblFileStatus.ForeColor = AccentRed; }
        finally { SetFileUIBusy(false); _inflating = false; _inflateTask = null; _activeOutputFiles.Clear(); }
    }

    void SetFileUIBusy(bool busy)
    {
        btnFileBrowse.Enabled = !busy;
        btnFileOutputBrowse.Enabled = !busy; txtFileSize.Enabled = !busy;
        cmbFileUnit.Enabled = !busy; chkFileOverwrite.Enabled = !busy;
        if (busy) SetProgress(fileProgressFill, fileProgressTrack, 0);
        btnFileInflate.SetLabel(busy ? "CANCEL" : "INFLATE");
        TaskbarProgress.SetState(Handle, busy);
    }

    // ════════════════════════════════════════════════
    //  FOLDER MODE LOGIC
    // ════════════════════════════════════════════════

    void LoadFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        SaveToHistory(path);
        txtFolderPath.Text = path;

        long total = 0;
        int count = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(f).Length;
            count++;
        }
        _originalSize = total;
        lblFolderOrigSize.Text = $"{FormatSize(total)} across {count} files";

        if (_folderMode == FolderMode.CompressEnlarge)
        {
            var parent = Path.GetDirectoryName(path) ?? path;
            txtFolderOutput.Text = Path.Combine(parent, Path.GetFileName(path) + "_inflated.zip");
        }
        else
        {
            txtFolderOutput.Text = path + "_inflated";
        }

        if (total < 1024L * 1024 * 1024) { txtFolderSize.Text = "1.00"; cmbFolderUnit.SelectedIndex = 2; }
        else { txtFolderSize.Text = (Math.Ceiling((double)total / (1024.0 * 1024 * 1024)) * 2).ToString("F2"); cmbFolderUnit.SelectedIndex = 2; }

        btnFolderInflate.Enabled = true;
        UpdateFolderStats();
        lblFolderStatus.Text = $"folder loaded — {count} files found";
        lblFolderStatus.ForeColor = GreenDark;
    }

    void UpdateFolderStats()
    {
        long target = GetTargetBytes(txtFolderSize, cmbFolderUnit);
        lblFolderStatTarget!.Tag = FormatSize(target);
        if (_originalSize > 0)
        {
            lblFolderStatOrig!.Tag = FormatSize(_originalSize);
            double ratio = target > _originalSize ? (double)target / _originalSize : 0;
            lblFolderStatRatio!.Tag = ratio > 0 ? $"{ratio:F1}x" : "—";
            lblFolderRatio.Text = ratio > 0 ? $"{ratio:F0}x" : "";
            lblFolderRatio.ForeColor = ratio > 100 ? AccentRed : GreenPrimary;
        }
        folderStatsPanel.Invalidate();
    }

    async void BtnFolderInflate_Click(object? sender, EventArgs e)
    {
        if (_inflating) { _cts?.Cancel(); return; }
        if (string.IsNullOrEmpty(txtFolderPath.Text)) return;
        var src = txtFolderPath.Text;
        var dst = txtFolderOutput.Text;
        if (string.IsNullOrEmpty(dst)) { ShowMsg("Select an output path."); return; }

        long target = GetTargetBytes(txtFolderSize, cmbFolderUnit);
        _cts = new CancellationTokenSource();
        SetFolderUIBusy(true);
        _inflating = true;
        _activeOutputFiles.Clear();
        _activeOutputFiles.Add(dst);

        try
        {
            if (_folderMode == FolderMode.CompressEnlarge)
            {
                _inflateTask = DoCompressEnlarge(src, dst, target, _cts.Token);
                await _inflateTask;
            }
            else
            {
                _inflateTask = DoInflateAll(src, dst, target, _cts.Token);
                await _inflateTask;
            }
        }
        catch (OperationCanceledException) { lblFolderStatus.Text = "cancelled"; lblFolderStatus.ForeColor = TextLight; }
        catch (Exception ex) { lblFolderStatus.Text = $"error: {ex.Message}"; lblFolderStatus.ForeColor = AccentRed; }
        finally { SetFolderUIBusy(false); _inflating = false; _inflateTask = null; _activeOutputFiles.Clear(); }
    }

    async Task DoCompressEnlarge(string srcFolder, string outputZip, long targetBytes, CancellationToken ct)
    {
        if (!CheckDiskSpace(outputZip, targetBytes)) return;

        lblFolderStatus.Text = "compressing folder to ZIP...";
        lblFolderStatus.ForeColor = GreenDark;

        // Step 1: Create ZIP
        await Task.Run(() =>
        {
            if (File.Exists(outputZip)) File.Delete(outputZip);
            ZipFile.CreateFromDirectory(srcFolder, outputZip, CompressionLevel.Optimal, includeBaseDirectory: false);
        }, ct);

        var zipSize = new FileInfo(outputZip).Length;
        if (targetBytes <= zipSize)
        {
            ShowMsg($"Target ({FormatSize(targetBytes)}) must exceed ZIP size ({FormatSize(zipSize)}).");
            return;
        }

        long padding = targetBytes - zipSize;
        lblFolderStatus.Text = $"ZIP created ({FormatSize(zipSize)}) — inflating...";

        // Step 2: Add invisible padding (same EOCD-relocation technique)
        var progress = new Progress<(long written, string status)>(p =>
        {
            SetProgress(folderProgressFill, folderProgressTrack, (double)p.written / padding);
            lblFolderStatus.Text = $"inflating ZIP... {FormatSize(p.written)} / {FormatSize(padding)}";
        });

        await Task.Run(() =>
        {
            InflateZipBased(outputZip, outputZip, padding, progress, ct);
        }, ct);

        SetProgress(folderProgressFill, folderProgressTrack, 1.0);
        var finalSize = new FileInfo(outputZip).Length;
        lblFolderStatus.Text = $"done — {FormatSize(_originalSize)} folder → {FormatSize(finalSize)} ZIP";
        lblFolderStatus.ForeColor = GreenPrimary;
        System.Media.SystemSounds.Asterisk.Play();
    }

    async Task DoInflateAll(string srcFolder, string outputFolder, long targetPerFile, CancellationToken ct)
    {
        var files = Directory.GetFiles(srcFolder, "*", SearchOption.AllDirectories);
        if (files.Length == 0) { ShowMsg("Folder is empty."); return; }

        long totalRequired = targetPerFile * files.Length;
        if (!CheckDiskSpace(outputFolder, totalRequired)) return;

        // Create output folder structure
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        int done = 0;
        long totalWritten = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(srcFolder, file);
            var outPath = Path.Combine(outputFolder, relPath);
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            var fileSize = new FileInfo(file).Length;
            if (targetPerFile <= fileSize)
            {
                // Just copy if already bigger than target
                File.Copy(file, outPath, overwrite: true);
            }
            else
            {
                long padding = targetPerFile - fileSize;
                var progress = new Progress<(long written, string status)>(p =>
                {
                    double filePct = (double)p.written / padding;
                    double totalPct = ((double)done + filePct) / files.Length;
                    SetProgress(folderProgressFill, folderProgressTrack, totalPct);
                    lblFolderStatus.Text = $"[{done + 1}/{files.Length}] {Path.GetFileName(file)} — {FormatSize(p.written)} / {FormatSize(padding)}";
                    lblFolderStatus.ForeColor = GreenDark;
                });

                await Task.Run(() => InflateFile(file, outPath, padding, progress, ct), ct);

                totalWritten += padding;
            }
            done++;
        }

        SetProgress(folderProgressFill, folderProgressTrack, 1.0);
        lblFolderStatus.Text = $"done — {files.Length} files inflated to {FormatSize(targetPerFile)} each";
        lblFolderStatus.ForeColor = GreenPrimary;
        System.Media.SystemSounds.Asterisk.Play();
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
        TaskbarProgress.SetState(Handle, busy);
    }

    // ════════════════════════════════════════════════
    //  FORMAT ROUTER
    // ════════════════════════════════════════════════

    static void InflateFile(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();

        if (ext == ".png")
            InflatePng(source, output, paddingBytes, progress, ct);
        else if (ext is ".jpg" or ".jpeg" or ".jfif")
            InflateJpeg(source, output, paddingBytes, progress, ct);
        else if (ext is ".mp4" or ".mov" or ".m4v" or ".m4a" or ".3gp" or ".f4v")
            InflateMp4(source, output, paddingBytes, progress, ct);
        else if (ext is ".zip" or ".pptx" or ".docx" or ".xlsx" or ".odt" or ".ods" or ".odp"
                 or ".jar" or ".apk" or ".xpi" or ".epub")
            InflateZipBased(source, output, paddingBytes, progress, ct);
        else if (ext == ".pdf")
            InflatePdf(source, output, paddingBytes, progress, ct);
        else
            InflateGeneric(source, output, paddingBytes, progress, ct);
    }

    // ════════════════════════════════════════════════
    //  PNG: Hybrid approach.
    //  Phase 1: paDd chunks before IEND (up to 2.5GB)
    //           — WIC processes these correctly.
    //  Phase 2: Raw zeros appended AFTER IEND for the
    //           rest — WIC stops at IEND, trailing data
    //           is post-EOF and ignored.
    //  Image loads fully before hitting any padding.
    // ════════════════════════════════════════════════

    const long MaxStructuredPadding = 2_500_000_000L; // 2.5GB safe zone for WIC

    static void InflatePng(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);

        long srcLen = src.Length;
        long structuredBytes = Math.Min(paddingBytes, MaxStructuredPadding);
        long trailingBytes = paddingBytes - structuredBytes;

        // Copy everything EXCEPT the last 12 bytes (IEND chunk)
        long copyLen = srcLen - 12;
        CopyStream(src, dst, copyLen, ct);

        // Phase 1: paDd chunks before IEND (up to 2.5GB)
        if (structuredBytes > 0)
        {
            // Use 16MB chunks — small enough for WIC, large enough for speed
            const int maxChunkData = 16 * 1024 * 1024; // 16MB per chunk
            byte[] chunkType = "paDd"u8.ToArray();
            long remaining = structuredBytes;
            long totalWritten = 0;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int chunkDataSize = (int)Math.Min(maxChunkData, remaining);

                WriteBE32(dst, chunkDataSize);
                dst.Write(chunkType, 0, 4);

                uint crc = Crc32Png.Init();
                crc = Crc32Png.Update(crc, chunkType);

                byte[] buf = new byte[1024 * 1024];
                int dataLeft = chunkDataSize;
                while (dataLeft > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int toWrite = Math.Min(buf.Length, dataLeft);
                    crc = Crc32Png.Update(crc, buf.AsSpan(0, toWrite));
                    dst.Write(buf, 0, toWrite);
                    dataLeft -= toWrite;
                    totalWritten += toWrite;
                    if (totalWritten % (1024 * 1024 * 4) == 0 || dataLeft <= 0)
                        progress.Report((totalWritten, ""));
                }

                WriteBE32(dst, (int)Crc32Png.Final(crc));
                remaining -= chunkDataSize;
            }
        }

        // Write original IEND chunk (last 12 bytes)
        src.Seek(-12, SeekOrigin.End);
        byte[] iend = new byte[12];
        src.ReadExactly(iend, 0, 12);
        dst.Write(iend, 0, 12);

        // Phase 2: Raw zeros after IEND (WIC stops at IEND, ignores trailing)
        if (trailingBytes > 0)
        {
            long alreadyWritten = structuredBytes;
            var trailProgress = new Progress<(long, string)>(p =>
                progress.Report((alreadyWritten + p.Item1, "")));
            WritePaddingRaw(dst, trailingBytes, trailProgress, ct);
        }

        dst.Flush();
        progress.Report((paddingBytes, ""));
    }

    // ════════════════════════════════════════════════
    //  JPEG: Hybrid approach.
    //  Phase 1: COM markers before EOI (up to 2.5GB)
    //           — WIC processes these correctly.
    //  Phase 2: Raw zeros appended AFTER EOI for the
    //           rest — WIC stops at EOI, trailing data
    //           is post-EOF and ignored.
    //  Image decoded fully BEFORE any padding.
    //  COM data = 0x20 (space), valid ISO 8859-1.
    // ════════════════════════════════════════════════

    static void InflateJpeg(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);

        long srcLen = src.Length;
        long structuredBytes = Math.Min(paddingBytes, MaxStructuredPadding);
        long trailingBytes = paddingBytes - structuredBytes;

        // Copy everything EXCEPT the last 2 bytes (EOI: FF D9)
        long copyLen = srcLen - 2;
        CopyStream(src, dst, copyLen, ct);

        // Phase 1: COM markers before EOI (up to 2.5GB)
        if (structuredBytes > 0)
        {
            const int maxComData = 65533;
            byte[] spaceBuf = new byte[maxComData];
            Array.Fill(spaceBuf, (byte)0x20);
            long remaining = structuredBytes;
            long totalWritten = 0;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int dataSize = (int)Math.Min(maxComData, remaining);
                int segLength = dataSize + 2;

                dst.WriteByte(0xFF);
                dst.WriteByte(0xFE);
                dst.WriteByte((byte)(segLength >> 8));
                dst.WriteByte((byte)(segLength & 0xFF));
                dst.Write(spaceBuf, 0, dataSize);

                remaining -= dataSize;
                totalWritten += dataSize;
                if (totalWritten % (maxComData * 64) == 0 || remaining <= 0)
                    progress.Report((totalWritten, ""));
            }
        }

        // Write original EOI (last 2 bytes: FF D9)
        src.Seek(-2, SeekOrigin.End);
        byte[] eoi = new byte[2];
        src.ReadExactly(eoi, 0, 2);
        dst.Write(eoi, 0, 2);

        // Phase 2: Raw zeros after EOI (WIC stops at EOI, ignores trailing)
        if (trailingBytes > 0)
        {
            long alreadyWritten = structuredBytes;
            var trailProgress = new Progress<(long, string)>(p =>
                progress.Report((alreadyWritten + p.Item1, "")));
            WritePaddingRaw(dst, trailingBytes, trailProgress, ct);
        }

        dst.Flush();
        progress.Report((paddingBytes, ""));
    }

    // Stream copy helper — copies exactly 'count' bytes without loading all into RAM
    static void CopyStream(Stream src, Stream dst, long count, CancellationToken ct)
    {
        byte[] buf = new byte[1024 * 1024];
        long left = count;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buf.Length, left);
            int read = src.Read(buf, 0, toRead);
            if (read == 0) break;
            dst.Write(buf, 0, read);
            left -= read;
        }
    }

    // ════════════════════════════════════════════════
    //  MP4/MOV: Append a "free" box (ISO 14496-12 standard)
    //  All MP4 parsers skip free/skip boxes by spec.
    // ════════════════════════════════════════════════

    static void InflateMp4(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, output, overwrite: true);

        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, 1024 * 1024);
        fs.Seek(0, SeekOrigin.End);

        long totalBoxSize = paddingBytes + 16; // extended header: 4 + 4 + 8

        // Extended size header (supports >4GB)
        WriteBE32(fs, 1); // size=1 means "use 64-bit extended size"
        fs.Write("free"u8.ToArray(), 0, 4);
        WriteBE64(fs, totalBoxSize);

        // Padding data
        WritePaddingRaw(fs, paddingBytes, progress, ct);
    }

    // ════════════════════════════════════════════════
    //  PDF: Append as a PDF stream object + updated xref
    //  Keeps PDF structure valid via incremental update.
    // ════════════════════════════════════════════════

    static void InflatePdf(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, output, overwrite: true);

        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, 1024 * 1024);
        long startPos = fs.Length;
        fs.Seek(0, SeekOrigin.End);

        // Write a padding stream object as an incremental update
        // Use a high object number unlikely to conflict
        int objNum = 999999;
        string header = $"\n{objNum} 0 obj\n<< /Length {paddingBytes} >>\nstream\n";
        string trailer = $"\nendstream\nendobj\n";

        byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
        byte[] trailerBytes = System.Text.Encoding.ASCII.GetBytes(trailer);

        fs.Write(headerBytes, 0, headerBytes.Length);
        WritePaddingRaw(fs, paddingBytes, progress, ct);
        fs.Write(trailerBytes, 0, trailerBytes.Length);

        // Write a minimal xref + trailer pointing back to original
        long xrefPos = fs.Position;
        string xref = $"xref\n0 1\n0000000000 65535 f \n{objNum} 1\n{startPos + 1:D10} 00000 n \ntrailer\n<< /Size {objNum + 1} >>\nstartxref\n{xrefPos}\n%%EOF\n";
        byte[] xrefBytes = System.Text.Encoding.ASCII.GetBytes(xref);
        fs.Write(xrefBytes, 0, xrefBytes.Length);
        fs.Flush();
    }

    // ════════════════════════════════════════════════
    //  ZIP-based: Append padding AFTER zip, then relocate
    //  the EOCD record to the very end. ZIP readers scan
    //  backward from EOF for the EOCD signature, find it
    //  at the end, and the central-directory offset inside
    //  it is still correct (central dir didn't move).
    //  Result: zero visible entries, completely invisible.
    // ════════════════════════════════════════════════

    static void InflateZipBased(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, output, overwrite: true);

        // Read the EOCD from the end of the file
        byte[] fileBytes;
        using (var r = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Read last 64KB to find EOCD (max comment = 65535 + 22 byte EOCD)
            int tailSize = (int)Math.Min(r.Length, 65557);
            fileBytes = new byte[tailSize];
            r.Seek(-tailSize, SeekOrigin.End);
            r.ReadExactly(fileBytes, 0, tailSize);
        }

        // Find EOCD signature (0x50 0x4B 0x05 0x06) scanning backward
        int eocdOffset = -1;
        for (int i = fileBytes.Length - 22; i >= 0; i--)
        {
            if (fileBytes[i] == 0x50 && fileBytes[i + 1] == 0x4B &&
                fileBytes[i + 2] == 0x05 && fileBytes[i + 3] == 0x06)
            {
                eocdOffset = i;
                break;
            }
        }

        if (eocdOffset < 0)
        {
            // Fallback: can't find EOCD, use generic append
            using var fs2 = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, 1024 * 1024);
            fs2.Seek(0, SeekOrigin.End);
            WritePaddingRaw(fs2, paddingBytes, progress, ct);
            return;
        }

        // Extract the complete EOCD record (22 bytes + comment)
        int commentLen = fileBytes[eocdOffset + 20] | (fileBytes[eocdOffset + 21] << 8);
        int eocdTotalLen = 22 + commentLen;
        byte[] eocdRecord = new byte[eocdTotalLen];
        Array.Copy(fileBytes, eocdOffset, eocdRecord, 0, eocdTotalLen);

        // Calculate where EOCD starts in the actual file
        long fileLen;
        using (var r = new FileStream(output, FileMode.Open, FileAccess.Read)) fileLen = r.Length;
        long eocdFileOffset = fileLen - (fileBytes.Length - eocdOffset);

        // Truncate file at EOCD position, append padding, then re-append EOCD
        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, 1024 * 1024);
        fs.SetLength(eocdFileOffset); // chop off the EOCD
        fs.Seek(0, SeekOrigin.End);

        // Write padding
        WritePaddingRaw(fs, paddingBytes, progress, ct);

        // Re-append the original EOCD record at the new end
        fs.Write(eocdRecord, 0, eocdRecord.Length);
        fs.Flush();
    }

    // ════════════════════════════════════════════════
    //  Generic fallback: append zeros after file
    //  Works for WAV, AU, BMP, GIF, FLAC, ISO, EXE, etc.
    // ════════════════════════════════════════════════

    static void InflateGeneric(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, output, overwrite: true);
        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, 1024 * 1024);
        fs.Seek(0, SeekOrigin.End);
        WritePaddingRaw(fs, paddingBytes, progress, ct);
    }

    // ════════════════════════════════════════════════
    //  Raw padding writer
    // ════════════════════════════════════════════════

    static void WritePaddingRaw(Stream stream, long totalBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        const int chunkSize = 1024 * 1024;
        var buffer = new byte[chunkSize]; // zero-filled
        long written = 0;
        while (written < totalBytes)
        {
            ct.ThrowIfCancellationRequested();
            int toWrite = (int)Math.Min(chunkSize, totalBytes - written);
            stream.Write(buffer, 0, toWrite);
            written += toWrite;
            if (written % (chunkSize * 4) == 0 || written >= totalBytes)
                progress.Report((written, ""));
        }
        stream.Flush();
    }

    // ════════════════════════════════════════════════
    //  Binary helpers
    // ════════════════════════════════════════════════

    static void WriteBE32(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    static void WriteBE64(Stream s, long value)
    {
        WriteBE32(s, (int)(value >> 32));
        WriteBE32(s, (int)value);
    }

    // ════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════

    static long GetTargetBytes(TextBox txt, ComboBox cmb)
    {
        if (!double.TryParse(txt.Text, out double val)) val = 1;
        return cmb.SelectedIndex switch
        {
            0 => (long)(val * 1024),
            1 => (long)(val * 1024 * 1024),
            2 => (long)(val * 1024 * 1024 * 1024),
            _ => (long)(val * 1024 * 1024)
        };
    }

    void SetProgress(Panel fill, Panel track, double pct)
    {
        fill.Size = new Size((int)(track.Width * Math.Min(1.0, pct)), fill.Height);
        TaskbarProgress.SetValue(Handle, pct);
    }

    static void ShowMsg(string msg) =>
        MessageBox.Show(msg, "OverByte", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// Returns true if there's enough space, false if user cancelled or not enough room.
    static bool CheckDiskSpace(string outputPath, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(root)) return true;
        var drive = new DriveInfo(root);
        if (!drive.IsReady) return true;
        long free = drive.AvailableFreeSpace;
        if (free >= requiredBytes) return true;

        ShowMsg(
            $"Not enough disk space on {drive.Name}\n\n" +
            $"Required:   {FormatSize(requiredBytes)}\n" +
            $"Available:  {FormatSize(free)}\n" +
            $"Short by:   {FormatSize(requiredBytes - free)}\n\n" +
            $"Free up space or choose a different output location.");
        return false;
    }

    static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ════════════════════════════════════════════════
    //  HISTORY
    // ════════════════════════════════════════════════

    static List<string> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFile))
                return File.ReadAllLines(HistoryFile).Where(l => l.Length > 0).Take(MaxHistory).ToList();
        }
        catch { }
        return new();
    }

    static void SaveToHistory(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var history = LoadHistory();
            history.RemoveAll(h => h.Equals(path, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, path);
            if (history.Count > MaxHistory) history = history.Take(MaxHistory).ToList();
            File.WriteAllLines(HistoryFile, history);
        }
        catch { }
    }

    void RefreshHistoryPanel()
    {
        historyPanel.Controls.Clear();
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
                        _folderMode = FolderMode.None;
                        folderModePanel.Visible = true;
                        folderWorkPanel.Visible = false;
                        folderStatsPanel.Visible = false;
                        ShowScreen("folder");
                        LoadFolder(captured);
                    }
                    else
                    {
                        ShowScreen("file");
                        LoadFile(captured);
                    }
                };
            }

            // Tooltip with full path
            var tt = new ToolTip();
            tt.SetToolTip(lbl, path);

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
        var path = RoundRect(rect, 6);

        Color bg, fg, border;
        if (!Enabled) { bg = Color.FromArgb(240, 241, 243); fg = Color.FromArgb(180, 183, 190); border = Color.FromArgb(228, 230, 235); }
        else if (_primary) { bg = _pressed ? GreenPress : (_hover ? GreenHover : GreenPrimary); fg = Color.White; border = bg; }
        else { bg = _pressed ? SecHover : (_hover ? SecHover : SecBg); fg = _hover ? GreenPrimary : Color.FromArgb(60, 63, 70); border = _hover ? SecBorderHover : SecBorder; }

        using var bgBrush = new SolidBrush(bg);
        using var borderPen = new Pen(border);
        g.FillPath(bgBrush, path); g.DrawPath(borderPen, path);

        var font = _primary ? new Font("Segoe UI Semibold", 11f) : new Font("Segoe UI Semibold", 8f);
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
//  PNG CRC-32 (standard polynomial 0xEDB88320)
// ══════════════════════════════════════════════════

internal static class Crc32Png
{
    static readonly uint[] Table;

    static Crc32Png()
    {
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            Table[i] = c;
        }
    }

    public static uint Init() => 0xFFFFFFFF;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Final(uint crc) => crc ^ 0xFFFFFFFF;
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

