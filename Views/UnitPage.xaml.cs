using BookViewer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace BookViewer.Views
{
    public partial class UnitPage : ContentPage
    {
        private readonly string _bookFolder;
        private readonly BookData _bookData;
        private readonly UnitData _unit;

        private readonly List<ResourceDisplay> _allResources = new();

        public class ResourceDisplay
        {
            public string Type { get; set; } = "";
            public string TypeIcon { get; set; } = "";
            public string Description { get; set; } = "";
            public string PageNumber { get; set; } = "";
            public string Path { get; set; } = "";
            public string FallbackUrl { get; set; } = "";
        }

        private static readonly object _logLock = new object();
        private static string _logFilePath = null;

        private string GetLogFilePath()
        {
            if (_logFilePath == null)
            {
                try
                {
                    string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string logFolder = Path.Combine(documentsPath, "BookViewer");
                    Directory.CreateDirectory(logFolder);
                    _logFilePath = Path.Combine(logFolder, $"UnitPage_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
                catch
                {
                    _logFilePath = Path.Combine(Path.GetTempPath(), $"UnitPage_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
            }
            return _logFilePath;
        }

        private void Log(string message)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
                lock (_logLock)
                {
                    File.AppendAllText(GetLogFilePath(), line + Environment.NewLine);
                }
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch { }
        }

        public UnitPage(string bookFolder, BookData bookData, UnitData unit)
        {
            InitializeComponent();
            NavigationPage.SetHasNavigationBar(this, false);

            _bookFolder = bookFolder;
            _bookData = bookData;
            _unit = unit;

            UnitTitleLabel.Text = unit.Title;

            LoadBanner();
            LoadSections();
            LoadResources();
        }

        private void LoadBanner()
        {
            try
            {
                // Look for units/unitUID_XXXX.png in the book folder
                var unitsDir = Path.Combine(_bookFolder, "units");

                string bannerPath = null;

                if (Directory.Exists(unitsDir))
                {
                    var candidate = Path.Combine(unitsDir, $"{_unit.Id}.png");
                    if (File.Exists(candidate))
                        bannerPath = candidate;
                }

                if (bannerPath == null)
                {
                    var matches = Directory.GetFiles(_bookFolder, $"{_unit.Id}.png", SearchOption.AllDirectories);
                    if (matches.Length > 0)
                        bannerPath = matches[0];
                }

                if (!string.IsNullOrEmpty(bannerPath) && File.Exists(bannerPath))
                {
                    UnitBannerImage.Source = ImageSource.FromFile(bannerPath);
                    UnitBannerImage.IsVisible = true;
                }
                else
                {
                    UnitBannerImage.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading banner: {ex.Message}");
                UnitBannerImage.IsVisible = false;
            }
        }

        private void LoadSections()
        {
            var sections = _bookData.GetSectionsForUnit(_unit.Id);
            SectionsCollection.ItemsSource = sections;
        }

        private void LoadResources()
        {
            _allResources.Clear();

            var sections = _bookData.GetSectionsForUnit(_unit.Id);
            if (sections == null || sections.Count == 0)
            {
                ResourcesCollection.ItemsSource = _allResources;
                return;
            }

            foreach (var section in sections)
            {
                foreach (var r in section.Resources)
                {
                    _allResources.Add(new ResourceDisplay
                    {
                        Type = r.Type,
                        TypeIcon = GetIconForType(r.Type),
                        Description = r.Description,
                        PageNumber = r.PageNumber,
                        Path = r.Path,
                        FallbackUrl = r.FallbackUrl
                    });
                }
            }

            var sorted = _allResources
                .OrderBy(r => int.TryParse(r.PageNumber, out var n) ? n : 0)
                .ThenBy(r => r.Description)
                .ToList();

            ResourcesCollection.ItemsSource = sorted;
        }

        private string GetIconForType(string type)
        {
            return type switch
            {
                "audio" => "♪",
                "video" => "▶",
                "pdf" => "PDF",
                "doc" => "DOC",
                "web" => "URL",
                _ => "•"
            };
        }
        
        private void OnContentsTabClicked(object sender, EventArgs e)
        {
            ContentsView.IsVisible = true;
            ResourcesView.IsVisible = false;
        
            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        
            ContentsTab.BackgroundColor = dark ? Color.FromArgb("#48484A") : Colors.White;
            ContentsTab.TextColor = dark ? Colors.White : Colors.Black;
            ContentsTab.FontAttributes = FontAttributes.Bold;
        
            ResourcesTab.BackgroundColor = Colors.Transparent;
            ResourcesTab.TextColor = dark ? Color.FromArgb("#98989D") : Color.FromArgb("#8E8E93");
            ResourcesTab.FontAttributes = FontAttributes.None;
        }
        
        private void OnResourcesTabClicked(object sender, EventArgs e)
        {
            ContentsView.IsVisible = false;
            ResourcesView.IsVisible = true;
        
            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        
            ContentsTab.BackgroundColor = Colors.Transparent;
            ContentsTab.TextColor = dark ? Color.FromArgb("#98989D") : Color.FromArgb("#8E8E93");
            ContentsTab.FontAttributes = FontAttributes.None;
        
            ResourcesTab.BackgroundColor = dark ? Color.FromArgb("#48484A") : Colors.White;
            ResourcesTab.TextColor = dark ? Colors.White : Colors.Black;
            ResourcesTab.FontAttributes = FontAttributes.Bold;
        }

        private async void OnBackClicked(object sender, EventArgs e)
        {
            await Navigation.PopAsync();
        }

        private async void OnSectionTapped(object sender, TappedEventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is not SectionData section)
                return;

            int folio = 1;
            if (int.TryParse(section.PageStart, out int f) && f > 0)
                folio = f;

            var viewer = new BookViewerPage(_bookFolder, section.Id, folio);
            await Navigation.PushAsync(viewer);
        }

        private async void OnResourceTapped(object sender, TappedEventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is not ResourceDisplay r)
                return;

            await OpenResourceAsync(r);
        }

        private async Task OpenResourceAsync(ResourceDisplay r)
        {
            try
            {
                var target = r.Path ?? "";
                Log($"Resource tapped: desc='{r.Description}' target='{target}'");

                if (string.IsNullOrWhiteSpace(target))
                    return;

                // Online
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    Log($"Opening online URL: {target}");
                    await Launcher.Default.OpenAsync(target);
                    return;
                }

                // Local
                var fileName = Path.GetFileName(target);
                if (string.IsNullOrEmpty(fileName))
                    return;

                string localPath = null;
                var resourcesDir = Path.Combine(_bookFolder, "resources");

                if (Directory.Exists(resourcesDir))
                {
                    var direct = Path.Combine(resourcesDir, fileName);
                    if (File.Exists(direct))
                    {
                        localPath = direct;
                    }
                    else
                    {
                        var found = Directory.GetFiles(resourcesDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null) localPath = found;
                    }
                }

                if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
                {
                    // Fall back to URL if one was provided
                    if (!string.IsNullOrEmpty(r.FallbackUrl) &&
                        Uri.TryCreate(r.FallbackUrl, UriKind.Absolute, out var fbUri) &&
                        (fbUri.Scheme == Uri.UriSchemeHttp || fbUri.Scheme == Uri.UriSchemeHttps))
                    {
                        Log($"Local missing, opening fallback URL: {r.FallbackUrl}");
                        await Launcher.Default.OpenAsync(r.FallbackUrl);
                        return;
                    }

                    Log($"File not found: {fileName}");
                    await DisplayAlert("Not found", $"File not found:\n{fileName}", "OK");
                    return;
                }

                Log($"Opening local file: {localPath}");

#if WINDOWS
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.GetFullPath(localPath),
                        UseShellExecute = true
                    });
                    Log("Windows Process.Start succeeded");
                }
                catch (Exception wex)
                {
                    Log($"Windows open failed: {wex.Message}");
                    await DisplayAlert("Cannot open", wex.Message, "OK");
                }
#elif IOS
                try
                {
                    await Share.Default.RequestAsync(new ShareFileRequest
                    {
                        Title = Path.GetFileName(localPath),
                        File = new ShareFile(localPath)
                    });
                    Log("iOS share sheet presented");
                }
                catch (Exception iex)
                {
                    Log($"iOS share failed: {iex.Message}");
                    await DisplayAlert("Cannot share", iex.Message, "OK");
                }
#elif ANDROID
                try
                {
                    var ok = BookViewer.Platforms.Android.FileOpener.OpenFile(localPath);
                    if (!ok)
                        await DisplayAlert("Cannot open", $"No app can open {fileName}", "OK");
                    Log($"Android FileOpener returned {ok}");
                }
                catch (Exception aex)
                {
                    Log($"Android open failed: {aex.Message}");
                    await DisplayAlert("Cannot open", aex.Message, "OK");
                }
#else
                try
                {
                    await Launcher.Default.OpenAsync(new OpenFileRequest
                    {
                        File = new ReadOnlyFile(localPath)
                    });
                    Log("Launcher.OpenAsync succeeded");
                }
                catch (Exception lex)
                {
                    Log($"Launcher open failed: {lex.Message}");
                    await DisplayAlert("Cannot open", lex.Message, "OK");
                }
#endif
            }
            catch (Exception ex)
            {
                Log($"OpenResourceAsync exception: {ex.Message}");
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }
    }
}
