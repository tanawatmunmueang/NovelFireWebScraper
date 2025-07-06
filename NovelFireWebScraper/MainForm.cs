// MainForm.cs

// ALL using directives MUST be at the top of the file, BEFORE the namespace keyword.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using HtmlAgilityPack;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI; // For WebDriverWait
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;

namespace NovelFireWebScraper
{
    public partial class MainForm : Form
    {
        private TextBox txtLink = null!;
        private TextBox txtLocalDir = null!;
        private Button btnStartScraping = null!;
        private Button btnRetryFailedChapters = null!;
        private TextBox txtLogs = null!;
        private TextBox errorLogs = null!;
        private Label lblStatus = null!;
        private System.Windows.Forms.ProgressBar progressBarOdd = null!;
        private System.Windows.Forms.ProgressBar progressBarEven = null!;
        private CheckBox chkHeadlessToggle = null!;
        private CheckBox chkIncludeChapterName = null!;
        private NumericUpDown numStartPage = null!;
        private Button btnPauseResume = null!;
        private Button btnCancel = null!;

        private IWebDriver? _chapterListDriver;
        private const string SettingsFile = "settings.xml";
        private List<string> _failedChapters = new List<string>();

        private Random _random = new Random();
        private List<string> _userAgents = new List<string> {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        };

        private bool _isHeadlessMode = true;
        private bool _includeChapterNameInContent = false;

        private CancellationTokenSource? _cancellationTokenSource;
        private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
        private volatile bool _isScraping = false;
        private volatile bool _isPaused = false;


        public MainForm()
        {
            this.Text = "NovelFire Scraper";
            this.Width = 800;
            this.Height = 580;
            CreateUI();
            LoadSettings();
            this.FormClosing += MainForm_FormClosing;
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _chapterListDriver?.Quit();
        }

        private void RandomThreadSleep(int minMilliseconds, int maxMilliseconds, CancellationToken token)
        {
            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
            _pauseEvent.Wait(token);
            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

            int delay = _random.Next(minMilliseconds, maxMilliseconds + 1);
            for (int i = 0; i < delay / 100; i++)
            {
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                _pauseEvent.Wait(token);
                System.Threading.Thread.Sleep(100);
            }
            System.Threading.Thread.Sleep(delay % 100);
        }

        private void CreateUI()
        {
            Label lblLink = new Label { Text = "Book Link:", Left = 20, Top = 20, Width = 100 };
            txtLink = new TextBox { Left = 130, Top = 20, Width = 620, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Label lblLocalDir = new Label { Text = "Local Directory:", Left = 20, Top = 55, Width = 100 };
            txtLocalDir = new TextBox { Left = 130, Top = 55, Width = 620, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

            chkHeadlessToggle = new CheckBox { Text = "Run Headless", Left = 130, Top = txtLocalDir.Bottom + 10, Checked = _isHeadlessMode, AutoSize = true };
            chkHeadlessToggle.CheckedChanged += ChkHeadlessToggle_CheckedChanged;

            chkIncludeChapterName = new CheckBox { Text = "Include Chapter Name in File", Left = chkHeadlessToggle.Right + 15, Top = chkHeadlessToggle.Top, Checked = _includeChapterNameInContent, AutoSize = true };
            chkIncludeChapterName.CheckedChanged += ChkIncludeChapterName_CheckedChanged;

            Label lblStartPage = new Label { Text = "Chapter Page Index:", Left = 130, Top = chkHeadlessToggle.Bottom + 10, AutoSize = true };

            // This line adds x pixels of space between the label and the box for better visuals.
            numStartPage = new NumericUpDown { Left = lblStartPage.Right + 20, Top = lblStartPage.Top - 3, Width = 80, Minimum = 1, Maximum = 9999, Value = 1 };

            btnStartScraping = new Button { Text = "Start Scraping", Left = 130, Top = numStartPage.Bottom + 10, Width = 120 };
            btnStartScraping.Click += BtnStartScraping_Click;

            btnPauseResume = new Button { Text = "Pause", Left = btnStartScraping.Right + 10, Top = btnStartScraping.Top, Width = 70, Enabled = false };
            btnPauseResume.Click += BtnPauseResume_Click;

            btnCancel = new Button { Text = "Cancel", Left = btnPauseResume.Right + 10, Top = btnStartScraping.Top, Width = 70, Enabled = false };
            btnCancel.Click += BtnCancel_Click;

            btnRetryFailedChapters = new Button { Text = "Retry Failed", Left = btnCancel.Right + 10, Top = btnStartScraping.Top, Width = 100, Visible = false };
            btnRetryFailedChapters.Click += BtnRetryFailedChapters_Click;

            txtLogs = new TextBox { Left = 20, Top = btnStartScraping.Bottom + 15, Width = 730, Height = 100, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, ReadOnly = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            progressBarOdd = new ProgressBar { Left = 20, Top = txtLogs.Bottom + 10, Width = 350, Minimum = 0, Maximum = 100, Value = 0, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            progressBarEven = new ProgressBar { Left = progressBarOdd.Right + 10, Top = txtLogs.Bottom + 10, Width = 370, Minimum = 0, Maximum = 100, Value = 0, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            errorLogs = new TextBox { Left = 20, Top = progressBarOdd.Bottom + 10, Width = 730, Height = 100, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, ReadOnly = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            lblStatus = new Label { Text = "Ready", Left = 20, Top = this.ClientSize.Height - 30, Width = 730, ForeColor = System.Drawing.Color.Blue, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };

            this.Resize += (s, e) => {
                int totalBarWidth = this.ClientSize.Width - 40 - 10; progressBarOdd.Width = totalBarWidth / 2; progressBarEven.Left = progressBarOdd.Right + 10; progressBarEven.Width = totalBarWidth - progressBarOdd.Width;
            };
            Controls.AddRange(new Control[] { lblLink, txtLink, lblLocalDir, txtLocalDir, chkHeadlessToggle, chkIncludeChapterName, lblStartPage, numStartPage, btnStartScraping, btnPauseResume, btnCancel, btnRetryFailedChapters, txtLogs, progressBarOdd, progressBarEven, errorLogs, lblStatus });
        }

        private void ChkIncludeChapterName_CheckedChanged(object? sender, EventArgs e)
        {
            _includeChapterNameInContent = chkIncludeChapterName.Checked;
            AddLog($"Include chapter name in content set to: {_includeChapterNameInContent}");
        }

        private void BtnPauseResume_Click(object? sender, EventArgs e)
        {
            if (!_isScraping) return;
            _isPaused = !_isPaused;
            if (_isPaused) { _pauseEvent.Reset(); btnPauseResume.Text = "Resume"; lblStatus.Text = "Scraping Paused."; AddLog("Scraping Paused."); }
            else { _pauseEvent.Set(); btnPauseResume.Text = "Pause"; lblStatus.Text = "Scraping Resumed..."; AddLog("Scraping Resumed."); }
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            if (_isScraping && _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            { AddLog("Cancellation requested..."); lblStatus.Text = "Cancelling..."; _cancellationTokenSource.Cancel(); _pauseEvent.Set(); btnCancel.Enabled = false; btnPauseResume.Enabled = false; }
        }

        private void UpdateScrapingUIState(bool isStarting)
        {
            _isScraping = isStarting;
            btnStartScraping.Enabled = !isStarting;
            btnRetryFailedChapters.Visible = !isStarting && _failedChapters.Any();
            txtLink.Enabled = !isStarting;
            txtLocalDir.Enabled = !isStarting;
            chkHeadlessToggle.Enabled = !isStarting;
            chkIncludeChapterName.Enabled = !isStarting;
            numStartPage.Enabled = !isStarting;
            btnPauseResume.Enabled = isStarting;
            btnCancel.Enabled = isStarting;
            if (isStarting) { btnPauseResume.Text = "Pause"; _isPaused = false; _pauseEvent.Set(); _failedChapters.Clear(); progressBarOdd.Value = 0; progressBarEven.Value = 0; progressBarEven.Visible = true; txtLogs.Clear(); errorLogs.Clear(); }
        }

        private void ChkHeadlessToggle_CheckedChanged(object? sender, EventArgs e) { _isHeadlessMode = chkHeadlessToggle.Checked; AddLog($"Headless mode set to: {_isHeadlessMode}"); }
        private void LoadSettings() { if (File.Exists(SettingsFile)) { try { var serializer = new XmlSerializer(typeof(Settings)); using (var reader = new StreamReader(SettingsFile)) { var settings = (Settings?)serializer.Deserialize(reader); if (settings != null) { txtLink.Text = settings.BookLink; txtLocalDir.Text = settings.LocalDirectory; } } } catch (Exception ex) { AddErrorLog($"Error loading settings: {ex.Message}"); } } }
        private void SaveSettings() { var settings = new Settings { BookLink = txtLink.Text, LocalDirectory = txtLocalDir.Text, }; try { var serializer = new XmlSerializer(typeof(Settings)); using (var writer = new StreamWriter(SettingsFile)) { serializer.Serialize(writer, settings); } AddLog("Settings saved."); } catch (Exception ex) { AddErrorLog($"Error saving settings: {ex.Message}"); } }
        private ChromeOptions GetCurrentChromeOptions() { var opts = new ChromeOptions(); if (_isHeadlessMode) { opts.AddArgument("--headless=new"); } string randomUserAgent = _userAgents[_random.Next(_userAgents.Count)]; opts.AddArgument($"user-agent={randomUserAgent}"); opts.AddArgument("--disable-gpu"); opts.AddArgument("--window-size=1920,1080"); opts.AddArgument("--no-sandbox"); opts.AddArgument("--disable-dev-shm-usage"); opts.AddArgument("--disable-blink-features=AutomationControlled"); opts.AddExcludedArgument("enable-automation"); opts.AddAdditionalChromeOption("useAutomationExtension", false); AddLog($"Using User-Agent: {randomUserAgent} | Headless: {_isHeadlessMode}"); return opts; }

        private Dictionary<double, string> GetChapterLinksFromNovelFire(string baseUrl, CancellationToken token)
        {
            Dictionary<double, string> chapters = new Dictionary<double, string>();
            if (string.IsNullOrWhiteSpace(baseUrl)) { AddErrorLog("Base URL is empty."); return chapters; }
            token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token);
            try { var opts = GetCurrentChromeOptions(); var svc = ChromeDriverService.CreateDefaultService(); svc.HideCommandPromptWindow = true; _chapterListDriver?.Quit(); token.ThrowIfCancellationRequested(); _chapterListDriver = new ChromeDriver(svc, opts); _chapterListDriver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60); }
            catch (OperationCanceledException) { AddLog("Chapter list fetching cancelled during driver init."); throw; }
            catch (Exception ex) { AddErrorLog($"ChromeDriver init failed for chapter list: {ex.Message}"); return chapters; }

            int startPage = 1;
            this.Invoke((Action)(() => {
                startPage = (int)numStartPage.Value;
            }));

            int currentPage = startPage > 1 ? startPage : 1;
            bool firstPage = true;
            AddLog($"Fetching chapter list from NovelFire, starting at page {currentPage}...");

            while (_chapterListDriver != null && !token.IsCancellationRequested)
            {
                _pauseEvent.Wait(token); token.ThrowIfCancellationRequested();
                if (!firstPage) { AddLog($"[ChapterListDriver] Delaying 2-4s before page {currentPage}..."); RandomThreadSleep(2000, 4000, token); }
                firstPage = false; token.ThrowIfCancellationRequested(); string pageUrl = $"{baseUrl.TrimEnd('/')}/chapters?page={currentPage}"; AddLog($"[ChapterListDriver] Navigating to: {pageUrl}");
                try
                {
                    _chapterListDriver.Navigate().GoToUrl(pageUrl); token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token);
                    if (_chapterListDriver.Title.Contains("Error 1015") || _chapterListDriver.PageSource.Contains("You are being rate limited") || _chapterListDriver.PageSource.Contains("Access denied")) { AddErrorLog($"[ChapterListDriver] BLOCKED page {currentPage}. Stopping."); break; }
                    var chapterListItems = _chapterListDriver.FindElements(By.CssSelector("ul.chapter-list li"));
                    if (!chapterListItems.Any()) { AddLog($"No items page {currentPage}. End of list."); break; }
                    foreach (var listItem in chapterListItems)
                    {
                        token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token);
                        try { var linkElement = listItem.FindElement(By.TagName("a")); string chapterUrl = linkElement.GetAttribute("href"); if (string.IsNullOrEmpty(chapterUrl)) { AddLog($"Null/empty URL page {currentPage}. Skip."); continue; } double chapterNumberKey; try { var chapterNoSpan = listItem.FindElement(By.CssSelector("span.chapter-no")); string chapterNoStr = chapterNoSpan.Text; if (!double.TryParse(chapterNoStr, NumberStyles.Any, CultureInfo.InvariantCulture, out chapterNumberKey)) { AddLog($"Parse fail span: '{chapterNoStr}' for {chapterUrl}. Skip."); continue; } } catch (NoSuchElementException) { var match = Regex.Match(chapterUrl, @"/chapter-(\d+(\.\d+)?)"); if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out chapterNumberKey)) { } else { AddLog($"Parse fail URL: {chapterUrl}. Skip."); continue; } } if (!chapters.ContainsKey(chapterNumberKey)) { chapters[chapterNumberKey] = chapterUrl; AddLog($"Ch {chapterNumberKey} [{chapterUrl}] collected."); } }
                        catch (NoSuchElementException nseEx) { AddErrorLog($"NSE in list item: {nseEx.Message}"); }
                        catch (Exception ex) { AddErrorLog($"Ex in list item: {ex.Message}"); }
                    }
                    currentPage++;
                }
                catch (OperationCanceledException) { AddLog("Chapter list fetch cancelled."); throw; }
                catch (WebDriverException wdEx) { AddErrorLog($"[ChapterListDriver] WebDriver error {pageUrl}: {wdEx.Message}"); break; }
                catch (Exception ex) { AddErrorLog($"[ChapterListDriver] Error {pageUrl}: {ex.Message}"); break; }
            }
            AddLog($"Total entries: {chapters.Count}"); try { _chapterListDriver?.Quit(); _chapterListDriver = null; } catch (Exception ex) { AddErrorLog($"Error closing ChapterListDriver: {ex.Message}"); }
            return chapters;
        }

        private async void BtnStartScraping_Click(object? sender, EventArgs e)
        {
            if (_isScraping) { AddLog("Scraping already in progress."); return; }
            string baseUrl = txtLink.Text.Trim(); string localDir = txtLocalDir.Text.Trim();
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(localDir)) { MessageBox.Show("Book Link and Directory required.", "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            UpdateScrapingUIState(true); lblStatus.Text = "Scraping starting...";
            _cancellationTokenSource = new CancellationTokenSource(); var token = _cancellationTokenSource.Token;
            try
            {
                new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);

                var chaps = await Task.Run(() => GetChapterLinksFromNovelFire(baseUrl, token), token);

                if (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                if (!chaps.Any()) { lblStatus.Text = "No items/rate limited."; AddLog("No chapters/rate limited."); UpdateScrapingUIState(false); return; }
                lblStatus.Text = "Scraping with two drivers...";
                var oddChaps = chaps.Where(kvp => kvp.Key % 2 != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var evenChaps = chaps.Where(kvp => kvp.Key % 2 == 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                progressBarOdd.Maximum = Math.Max(1, oddChaps.Count); progressBarEven.Maximum = Math.Max(1, evenChaps.Count);
                AddLog($"Parallel processing. Odd: {oddChaps.Count}, Even: {evenChaps.Count}.");
                await Task.WhenAll(
                    Task.Run(() => ProcessChaptersParallel(oddChaps, localDir, "Driver1_Odd", progressBarOdd, token), token),
                    Task.Run(() => ProcessChaptersParallel(evenChaps, localDir, "Driver2_Even", progressBarEven, token), token)
                );
                if (token.IsCancellationRequested) { AddLog("Scraping cancelled by user post-tasks."); }
                else { lblStatus.Text = _failedChapters.Any() ? $"Scraping completed with {_failedChapters.Count} failed." : "Scraping completed!"; }
                SaveSettings();
            }
            catch (OperationCanceledException) { lblStatus.Text = "Scraping Canceled."; AddLog("Scraping Canceled."); }
            catch (Exception ex) { AddErrorLog($"Fatal error: {ex.Message}"); lblStatus.Text = "Scraping Error."; }
            finally { UpdateScrapingUIState(false); _cancellationTokenSource?.Dispose(); _cancellationTokenSource = null; }
        }

        private void ProcessChaptersParallel(Dictionary<double, string> chapters, string localDir, string driverLabel, ProgressBar progressBar, CancellationToken token)
        {
            if (!chapters.Any()) { UpdateProgressBar(progressBar, progressBar.Maximum); return; }
            IWebDriver? chapterProcessingDriver = null;
            try
            {
                var opts = GetCurrentChromeOptions(); var svc = ChromeDriverService.CreateDefaultService(); svc.HideCommandPromptWindow = true; token.ThrowIfCancellationRequested(); chapterProcessingDriver = new ChromeDriver(svc, opts); chapterProcessingDriver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
                bool firstChapterInBatch = true;
                foreach (var chap in chapters)
                {
                    token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token);
                    if (!firstChapterInBatch) { AddLog($"[{driverLabel}] Delaying 1-3s..."); RandomThreadSleep(1000, 3000, token); }
                    firstChapterInBatch = false; token.ThrowIfCancellationRequested();
                    ProcessChapter((ChromeDriver)chapterProcessingDriver, chap, localDir, driverLabel, progressBar, token);
                }
            }
            catch (OperationCanceledException) { AddLog($"[{driverLabel}] Task cancelled."); }
            catch (Exception ex) { AddErrorLog($"[{driverLabel}] Error in parallel task: {ex.Message}"); }
            finally { chapterProcessingDriver?.Quit(); }
        }

        private void ProcessChapter(ChromeDriver driverInstance, KeyValuePair<double, string> chapterEntry, string localDir, string driverLabel, ProgressBar progressBar, CancellationToken token)
        {
            string url = chapterEntry.Value;
            try
            {
                token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token); AddLog($"[{driverLabel}] Navigating to: {url}"); driverInstance.Navigate().GoToUrl(url);
                token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token);
                if (driverInstance.Title.Contains("Error 1015") || driverInstance.PageSource.Contains("You are being rate limited") || driverInstance.PageSource.Contains("Access denied")) { AddErrorLog($"[{driverLabel}] BLOCKED: {url}."); LogFailedChapter(url, "Blocked (1015/Access Denied)"); if (!_failedChapters.Contains(url)) _failedChapters.Add(url); return; }
                try { WebDriverWait contentWait = new WebDriverWait(driverInstance, TimeSpan.FromSeconds(5)); contentWait.Until(d => { token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token); return d.FindElement(By.Id("content")).Displayed; }); AddLog($"[{driverLabel}] Content div found: {url}."); }
                catch (WebDriverTimeoutException) { AddErrorLog($"[{driverLabel}] EMPTY CONTENT: {url} (5s wait)."); SavePageSourceOnError(driverInstance, localDir, url, "EMPTY_CONTENT"); LogFailedChapter(url, "Empty content (5s timeout)"); if (!_failedChapters.Contains(url)) _failedChapters.Add(url); return; }
                catch (OperationCanceledException) { AddLog($"[{driverLabel}] Cancelled content wait: {url}."); throw; }
                catch (NoSuchElementException) { AddErrorLog($"[{driverLabel}] UNEXPECTED NSE #content: {url}."); SavePageSourceOnError(driverInstance, localDir, url, "UNEXPECTED_NSE_WAIT"); LogFailedChapter(url, "Unexpected NSE content wait"); if (!_failedChapters.Contains(url)) _failedChapters.Add(url); return; }
                token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token); AddLog($"[{driverLabel}] Parsing {url}."); var doc = new HtmlAgilityPack.HtmlDocument(); doc.LoadHtml(driverInstance.PageSource);
                string? bookTitle = ExtractBookTitle(doc); string? chapterTitle = ExtractChapterTitle(doc); string? chapterContent = ExtractChapterContent(doc);
                if (string.IsNullOrEmpty(bookTitle) || string.IsNullOrEmpty(chapterTitle) || string.IsNullOrEmpty(chapterContent)) { AddErrorLog($"[{driverLabel}] Missing elements: {url}."); SavePageSourceOnError(driverInstance, localDir, url, "MISSING_HAP_ELEMENTS"); LogFailedChapter(url, "Missing elements post-parse"); if (!_failedChapters.Contains(url)) _failedChapters.Add(url); return; }
                token.ThrowIfCancellationRequested(); string fileName = $"{SanitizeFileName(bookTitle)} - {SanitizeFileName(chapterTitle)}.txt"; string filePath = Path.Combine(localDir, fileName); SaveToFile(filePath, chapterTitle, chapterContent); AddLog($"[{driverLabel}] Saved: {fileName}");
                if (!WaitForFile(filePath, TimeSpan.FromSeconds(15))) { AddErrorLog($"[{driverLabel}] Timeout saving {fileName}."); LogFailedChapter(url, "Timeout saving file"); if (!_failedChapters.Contains(url)) _failedChapters.Add(url); return; }
                if (!token.IsCancellationRequested) { UpdateProgressBar(progressBar, progressBar.Value + 1); }
            }
            catch (OperationCanceledException) { AddLog($"[{driverLabel}] Cancelled processing {url}."); throw; }
            catch (Exception ex) { AddErrorLog($"[{driverLabel}] GENERAL EXCEPTION {url}: {ex.Message}"); SavePageSourceOnError(driverInstance, localDir, url, "GENERAL_EXCEPTION"); LogFailedChapter(url, $"General Exception: {ex.Message}"); if (!_failedChapters.Contains(url)) _failedChapters.Add(url); }
        }

        private void SavePageSourceOnError(IWebDriver? driverInstance, string localDir, string url, string errorType) { if (driverInstance == null) { AddErrorLog($"[{errorType}] Driver null for URL: {url}"); return; } try { string safeFileNameSuffix = SanitizeFileName(url.Split('/').LastOrDefault() ?? "unknown_url"); string pageSourcePath = Path.Combine(localDir, $"FAILED_{errorType}_{safeFileNameSuffix}.html"); File.WriteAllText(pageSourcePath, driverInstance.PageSource); AddLog($"Saved fail source ({errorType}) to: {pageSourcePath}"); } catch (Exception ex) { AddErrorLog($"Error saving source {errorType} for {url}: {ex.Message}"); } }

        private async void BtnRetryFailedChapters_Click(object? sender, EventArgs e)
        {
            if (_isScraping) { MessageBox.Show("Cannot retry while scraping.", "Operation Blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!_failedChapters.Any()) { MessageBox.Show("No failed chapters.", "Retry Info", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            UpdateScrapingUIState(true); lblStatus.Text = "Retrying failed chapters..."; txtLogs.AppendText($"\n--- Retrying {_failedChapters.Count} chapters ---\n");
            progressBarEven.Visible = false; progressBarOdd.Value = 0; var chaptersToRetry = new List<string>(_failedChapters); _failedChapters.Clear(); progressBarOdd.Maximum = Math.Max(1, chaptersToRetry.Count);
            _cancellationTokenSource = new CancellationTokenSource(); var token = _cancellationTokenSource.Token;
            try
            {
                await Task.Run(() => { var opts = GetCurrentChromeOptions(); var svc = ChromeDriverService.CreateDefaultService(); svc.HideCommandPromptWindow = true; using (var d = new ChromeDriver(svc, opts)) { d.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60); bool firstRetryChapter = true; foreach (var url in chaptersToRetry) { token.ThrowIfCancellationRequested(); _pauseEvent.Wait(token); if (!firstRetryChapter) { AddLog($"[RetryDriver] Delaying 2-5s..."); RandomThreadSleep(2000, 5000, token); } firstRetryChapter = false; ProcessChapter(d, new KeyValuePair<double, string>(0, url), txtLocalDir.Text.Trim(), "RetryDriver", progressBarOdd, token); } } }, token);
                if (token.IsCancellationRequested) { AddLog("Retry cancelled."); } else { lblStatus.Text = _failedChapters.Any() ? $"Retry done: {_failedChapters.Count} still failed." : "Retry done: All succeeded!"; }
            }
            catch (OperationCanceledException) { lblStatus.Text = "Retry Canceled."; AddLog("Retry Canceled."); }
            catch (Exception ex) { AddErrorLog($"Fatal error retry: {ex.Message}"); lblStatus.Text = "Retry Error."; }
            finally { UpdateScrapingUIState(false); _cancellationTokenSource?.Dispose(); _cancellationTokenSource = null; progressBarEven.Visible = true; }
        }

        private string SanitizeFileName(string input) { if (string.IsNullOrWhiteSpace(input)) return "Untitled_Chapter"; input = input.Replace(":", " - ").Replace("?", "").Replace("*", "").Replace("\"", "'").Replace("/", "-").Replace("\\", "-").Replace("|", "-").Replace("<", "_lt_").Replace(">", "_gt_"); string invalidFileChars = new string(Path.GetInvalidFileNameChars()); Regex r = new Regex($"[{Regex.Escape(invalidFileChars)}]"); input = r.Replace(input, "_"); input = Regex.Replace(input.Trim('_', '.'), @"__+", "_"); const int maxLength = 200; if (input.Length > maxLength) { input = input.Substring(0, maxLength).TrimEnd('.', '_'); } if (string.IsNullOrWhiteSpace(input)) return "Sanitized_Untitled"; return input; }
        private bool WaitForFile(string filePath, TimeSpan timeout) { DateTime start = DateTime.Now; while (DateTime.Now - start < timeout) { if (File.Exists(filePath) && new FileInfo(filePath).Length > 0) return true; System.Threading.Thread.Sleep(500); } return File.Exists(filePath) && new FileInfo(filePath).Length > 0; }
        private void LogFailedChapter(string url, string reason) { string logFilePath = Path.Combine(Application.StartupPath, "FailedChapters.log"); string logEntry = $"{DateTime.Now:u} | URL: {url} | Reason: {reason}{Environment.NewLine}"; try { File.AppendAllText(logFilePath, logEntry); } catch (Exception ex) { AddErrorLog($"[Logger Error] Failed to write to FailedChapters.log: {ex.Message}"); } }
        private string? ExtractBookTitle(HtmlAgilityPack.HtmlDocument doc) => doc.DocumentNode.SelectSingleNode("//a[@class='booktitle']")?.InnerText.Trim();
        private string? ExtractChapterTitle(HtmlAgilityPack.HtmlDocument doc) => doc.DocumentNode.SelectSingleNode("//span[@class='chapter-title']")?.InnerText.Trim();

        private bool IsReadableLine(string line)
        {
            const int MIN_LENGTH_FOR_NARRATION = 3;
            char[] quoteChars = { '“', '‘', '"', '\'' };
            bool hasLetters = line.Any(char.IsLetter);
            bool hasDigits = line.Any(char.IsDigit);
            bool isDialogue = line.Length > 0 && quoteChars.Contains(line[0]);
            if (isDialogue) { return hasLetters || hasDigits; }
            if (!hasLetters && !hasDigits) { return false; }
            if (hasLetters && line.Length < MIN_LENGTH_FOR_NARRATION) { return false; }
            return true;
        }

        private string? ExtractChapterContent(HtmlAgilityPack.HtmlDocument doc)
        {
            var contentNode = doc.DocumentNode.SelectSingleNode("//div[@id='content']");
            if (contentNode == null)
            {
                this.AddErrorLog("HAP: #content div not found.");
                return null;
            }
            contentNode.Descendants("p").Where(p => p.Attributes["class"]?.Value.Contains("box-notification") ?? false).ToList().ForEach(p => p.Remove());
            contentNode.Descendants("nfe059").ToList().ForEach(n => n.Remove());
            contentNode.Descendants().Where(n => Regex.IsMatch(n.Name, @"^nf[0-9a-f]+$")).ToList().ForEach(n => n.Remove());
            contentNode.Descendants().Where(n => n.Name == "script" || n.Name == "style" || n.Name == "iframe" || (n.Attributes["class"]?.Value.ToLowerInvariant().Contains("ad") ?? false) || (n.Attributes["id"]?.Value.ToLowerInvariant().Contains("ad") ?? false) || (n.Attributes["class"]?.Value.ToLowerInvariant().Contains("banner") ?? false) || (n.Attributes["class"]?.Value.ToLowerInvariant().Contains("hidden") ?? false) || (n.GetAttributeValue("style", "").ToLowerInvariant().Contains("display:none")) || n.Name == "ins").ToList().ForEach(n => n.Remove());
            string content = contentNode.InnerHtml;
            content = Regex.Replace(content, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, "<p[^>]*>", "\n\n", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, "</p>", "", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, "<!--.*?-->", "", RegexOptions.Singleline);
            content = Regex.Replace(content, "<[^>]+>", " ").Trim();
            content = HtmlEntity.DeEntitize(content);
            content = Regex.Replace(content, @"[ \t]+", " ").Trim();
            content = Regex.Replace(content, @"(\r\n|\r|\n){2,}", "\n\n").Trim();
            var cleanedLines = content.Split(new[] { '\n' }, StringSplitOptions.None)
                                      .Select(line => line.Trim())
                                      .Where(IsReadableLine);
            content = string.Join("\n\n", cleanedLines);
            content = content.Replace("~", "");
            content = content.Replace("→", " ");
            return content;
        }

        private void SaveToFile(string filePath, string chapterTitleForFileHeader, string contentBody)
        {
            string? directoryPath = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directoryPath)) { AddErrorLog($"Invalid directory: {filePath}"); return; }
            if (!filePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) { filePath += ".txt"; }
            Directory.CreateDirectory(directoryPath);
            using (var writer = new StreamWriter(filePath))
            {
                if (_includeChapterNameInContent)
                {
                    writer.WriteLine(chapterTitleForFileHeader);
                    writer.WriteLine();
                }
                if (!string.IsNullOrWhiteSpace(contentBody))
                {
                    writer.WriteLine(contentBody);
                }
            }
        }

        public void AddLog(string message) { if (txtLogs.InvokeRequired) txtLogs.Invoke(() => txtLogs.AppendText(message + Environment.NewLine)); else txtLogs.AppendText(message + Environment.NewLine); }
        public void AddErrorLog(string message) { if (errorLogs.InvokeRequired) errorLogs.Invoke(() => errorLogs.AppendText(message + Environment.NewLine)); else errorLogs.AppendText(message + Environment.NewLine); }
        public void UpdateProgressBar(ProgressBar progressBar, int value) { if (progressBar.InvokeRequired) progressBar.Invoke(() => progressBar.Value = Math.Min(value, progressBar.Maximum)); else progressBar.Value = Math.Min(value, progressBar.Maximum); }
    }

    public class Settings
    {
        public string BookLink { get; set; } = "";
        public string LocalDirectory { get; set; } = "";
    }
}