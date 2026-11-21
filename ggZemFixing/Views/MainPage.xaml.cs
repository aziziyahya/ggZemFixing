using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ggZemFixing.Views
{
    /// <summary>
    /// XAMPP MySQL Recovery Tool — restores a broken MySQL data directory
    /// by pulling a clean schema from the backup folder and re-attaching
    /// user databases from the renamed data_old directory.
    /// </summary>
    public partial class MainPage : Page
    {
        // ── Default path ─────────────────────────────────────────
        private const string DefaultXamppRoot = @"C:\xampp";

        // Folders to skip when scanning for user databases
        private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "mysql", "performance_schema", "phpmyadmin", "test"
        };


        // ── State ────────────────────────────────────────────────────
        private bool _isRunning = false;

        private static Style? TryGetStyle(string key)
        {
            try
            {
                if (Application.Current.Resources.TryGetValue(key, out var val) && val is Style s)
                    return s;
            }
            catch {}
            return null;
        }



        // ── Colour constants for log entries ─────────────────────────
        private static readonly Color ColourOk   = Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E);
        private static readonly Color ColourWarn  = Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23);
        private static readonly Color ColourError = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);
        private static readonly Color ColourInfo  = Color.FromArgb(0xFF, 0x93, 0xC5, 0xFD);
        private static readonly Color ColourMuted = Color.FromArgb(0xFF, 0x9C, 0xA3, 0xAF);

        // ── Glyphs for step indicators ───────────────────────────────
        private const string GlyphDone  = "\uE73E";
        private const string GlyphError = "\uE783";

        public MainPage()
        {
            this.InitializeComponent();

            // Auto-detect XAMPP installation if C:\xampp doesn't exist
            if (!Directory.Exists(@"C:\xampp"))
            {
                string[] commonPaths = [@"C:\src\xampp", @"D:\xampp", @"E:\xampp", @"C:\xampp7", @"C:\xampp8"];
                foreach (var p in commonPaths)
                {
                    if (Directory.Exists(p) && Directory.Exists(Path.Combine(p, "mysql", "backup")))
                    {
                        TxtMysqlRoot.Text = p;
                        break;
                    }
                }
            }

            // Wire up live path-hint updates and recovery button states
            TxtMysqlRoot.TextChanged += (_, _) => UpdatePathHints();
            BtnStartRecovery.IsEnabledChanged += (_, _) => UpdateRecoveryButtonVisual();
            UpdatePathHints();
            UpdateRecoveryButtonVisual();
        }

        private void UpdateRecoveryButtonVisual()
        {
            try
            {
                bool isEnabled = BtnStartRecovery.IsEnabled;
                if (isEnabled)
                {
                    BtnIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                    BtnLabel.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                }
                else
                {
                    var disabledBrush = Application.Current.Resources.TryGetValue("BtnDisabledForeground", out var brushObj) && brushObj is Brush b
                        ? b
                        : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
                    BtnIcon.Foreground = disabledBrush;
                    BtnLabel.Foreground = disabledBrush;
                }
            }
            catch {}
        }

        // ════════════════════════════════════════════════════════════
        //  Path helpers — always read from the TextBox
        // ════════════════════════════════════════════════════════════

        private string XamppRoot  => TxtMysqlRoot.Text.Trim().TrimEnd('\\');
        private string MysqlRoot  => Path.Combine(XamppRoot, "mysql");
        private string BackupPath => Path.Combine(MysqlRoot, "backup");
        private string DataPath   => Path.Combine(MysqlRoot, "data");

        private void UpdatePathHints()
        {
            string root = string.IsNullOrWhiteSpace(XamppRoot) ? "[xampp]" : XamppRoot;
            HintBackup.Text = $@"{root}\mysql\backup";
            HintData.Text   = $@"{root}\mysql\data";

            try
            {
                bool rootExists = !string.IsNullOrWhiteSpace(XamppRoot) && Directory.Exists(XamppRoot);
                bool backupExists = rootExists && Directory.Exists(BackupPath);
                bool dataExists = rootExists && Directory.Exists(DataPath);

                BackupStatusIcon.Glyph = backupExists ? "\uE73E" : "\uE711";
                BackupStatusIcon.Foreground = backupExists
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94))
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorDisabledBrush"];

                DataStatusIcon.Glyph = dataExists ? "\uE73E" : "\uE711";
                DataStatusIcon.Foreground = dataExists
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94))
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorDisabledBrush"];

                if (!_isRunning)
                {
                    bool canRecover = rootExists && backupExists;
                    BtnStartRecovery.IsEnabled = canRecover;

                    if (!rootExists)
                    {
                        ProgressDetailText.Text = "XAMPP not found";
                        SetStatusBadge("Path Not Found", ColourError);
                        ToolTipService.SetToolTip(BtnStartRecovery, "Please select a valid XAMPP installation directory.");
                    }
                    else if (!backupExists)
                    {
                        ProgressDetailText.Text = "mysql\\backup not found";
                        SetStatusBadge("Missing Backup", ColourWarn);
                        ToolTipService.SetToolTip(BtnStartRecovery, "Recovery disabled: mysql\\backup template folder was not found.");
                    }
                    else
                    {
                        ProgressDetailText.Text = "Ready";
                        SetStatusBadge("Ready", ColourOk);
                        ToolTipService.SetToolTip(BtnStartRecovery, "Start MySQL automated recovery.");
                    }
                }
            }
            catch {}
        }

        // ════════════════════════════════════════════════════════════
        //  Button handlers
        // ════════════════════════════════════════════════════════════

        /// <summary>Opens a folder picker so the user can browse to the mysql root.</summary>
        private async void OnBrowseClicked(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            // Required for unpackaged WinUI 3 apps: associate picker with the window HWND
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                TxtMysqlRoot.Text = folder.Path;
            }
        }

        private async void OnStartRecoveryClicked(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            await RunRecoveryAsync();
        }



        private static void OpenBrowserUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
        }

        private void OnGitHubAuthorClicked(object sender, RoutedEventArgs e)
        {
            OpenBrowserUrl("https://github.com/aziziyahya");
        }

        private void OnGitHubRepoClicked(object sender, RoutedEventArgs e)
        {
            OpenBrowserUrl("https://github.com/aziziyahya/ggZemFixing");
        }

        private void OnExitClicked(object sender, RoutedEventArgs e)
        {
            Application.Current.Exit();
        }

        private async void OnAboutClicked(object sender, RoutedEventArgs e)
        {
            var targetXamlRoot = this.XamlRoot ?? this.Content?.XamlRoot;
            if (targetXamlRoot == null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = targetXamlRoot,
                CornerRadius = new CornerRadius(12)
            };

            var panel = new StackPanel { Spacing = 10, Padding = new Thickness(8, 12, 8, 8), Width = 410 };

            // ── Hero Header: App Icon + Title & Subtitle ──
            var heroGrid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // App Icon in modern container
            var iconBorder = new Border
            {
                Width = 50,
                Height = 50,
                CornerRadius = new CornerRadius(12),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var logoImage = new Microsoft.UI.Xaml.Controls.Image
            {
                Width = 38,
                Height = 38,
                Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri("ms-appx:///Assets/AppIcon.svg"))
            };
            iconBorder.Child = logoImage;
            Grid.SetColumn(iconBorder, 0);
            heroGrid.Children.Add(iconBorder);

            // Title, Version & Subtitle
            var headerTextPanel = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new TextBlock
            {
                Text = "ggZemFixing",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var versionText = version != null ? $"v{version.Major}.{version.Minor}" : "v3.0";
            
            var versionBadge = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = versionText,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentGreenBrush"]
                }
            };
            titleRow.Children.Add(versionBadge);
            headerTextPanel.Children.Add(titleRow);

            headerTextPanel.Children.Add(new TextBlock
            {
                Text = "Automated XAMPP MySQL Recovery & Port Conflict Resolver",
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });

            Grid.SetColumn(headerTextPanel, 1);
            heroGrid.Children.Add(headerTextPanel);
            panel.Children.Add(heroGrid);

            // ── Description ──
            panel.Children.Add(new TextBlock
            {
                Text = "A modern, lightweight recovery utility designed to restore corrupted MySQL databases from backup templates and resolve Windows port conflicts without losing existing data.",
                FontSize = 11.5,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });

            // ── Info & Links Card ──
            var infoCard = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var cardPanel = new StackPanel { Spacing = 0 };

            UIElement CreateDetailRow(string label, string value, string? url = null)
            {
                var grid = new Grid { Padding = new Thickness(0, 7, 0, 7) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Pixel) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Label
                var lbl = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(lbl, 0);
                grid.Children.Add(lbl);

                // Value or Link
                if (!string.IsNullOrEmpty(url))
                {
                    var linkBtn = new HyperlinkButton
                    {
                        Padding = new Thickness(0),
                        MinHeight = 0,
                        MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var linkContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    linkContent.Children.Add(new TextBlock
                    {
                        Text = value,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium
                    });
                    linkContent.Children.Add(new FontIcon
                    {
                        Glyph = "\uE8A7",
                        FontSize = 10,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                    });
                    linkBtn.Content = linkContent;
                    linkBtn.Click += (s, e) => OpenBrowserUrl(url);

                    Grid.SetColumn(linkBtn, 1);
                    grid.Children.Add(linkBtn);
                }
                else
                {
                    var valText = new TextBlock
                    {
                        Text = value,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(valText, 1);
                    grid.Children.Add(valText);
                }

                return grid;
            }

            Border CreateDivider() => new Border
            {
                Height = 1,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
            };

            cardPanel.Children.Add(CreateDetailRow("Developer", "Azizi Yahya (@aziziyahya)", "https://github.com/aziziyahya"));
            cardPanel.Children.Add(CreateDivider());
            cardPanel.Children.Add(CreateDetailRow("Repository", "aziziyahya/ggZemFixing", "https://github.com/aziziyahya/ggZemFixing"));
            cardPanel.Children.Add(CreateDivider());
            cardPanel.Children.Add(CreateDetailRow("Technology", "WinUI 3 (.NET 9) • WinAppSDK"));
            cardPanel.Children.Add(CreateDivider());
            cardPanel.Children.Add(CreateDetailRow("Target OS", "Windows 10 / 11 (x64)"));

            infoCard.Child = cardPanel;
            panel.Children.Add(infoCard);

            // ── Action Buttons ──
            var buttonPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                Spacing = 10, 
                HorizontalAlignment = HorizontalAlignment.Center, 
                Margin = new Thickness(0, 4, 0, 0) 
            };

            var btnCheckUpdates = new Button
            {
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(6)
            };
            var checkStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var checkIcon = new FontIcon { Glyph = "\uE895", FontSize = 11 };
            var checkText = new TextBlock { Text = "Check for Updates", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            checkStack.Children.Add(checkIcon);
            checkStack.Children.Add(checkText);
            btnCheckUpdates.Content = checkStack;

            btnCheckUpdates.Click += async (s, ev) =>
            {
                btnCheckUpdates.IsEnabled = false;
                checkText.Text = "Checking…";

                bool hasUpdate = false;
                string? newTag = null;
                string? htmlUrl = null;
                bool isError = false;

                var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 0, 0);

                try
                {
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        "https://api.github.com/repos/aziziyahya/ggZemFixing/releases/latest");
                    request.Headers.UserAgent.ParseAdd("ggZemFixing/" + assemblyVersion.ToString(3));

                    var response = await UpdateHttpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        var jsonStr = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(jsonStr);
                        var root = doc.RootElement;
                        
                        newTag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                        htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : "https://github.com/aziziyahya/ggZemFixing/releases";

                        if (!string.IsNullOrEmpty(newTag))
                        {
                            var cleanTag = newTag.TrimStart('v', 'V');
                            if (Version.TryParse(cleanTag, out var latestVer))
                            {
                                hasUpdate = latestVer > assemblyVersion;
                            }
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // No releases published yet on the repository -> up to date
                        hasUpdate = false;
                    }
                    else
                    {
                        isError = true;
                    }
                }
                catch
                {
                    isError = true;
                }

                btnCheckUpdates.IsEnabled = true;
                checkText.Text = "Check for Updates";

                // Open a dialog on top of the About dialog without closing the About dialog
                ShowUpdateResultDialog(targetXamlRoot, hasUpdate, newTag, htmlUrl, isError);
            };

            var btnClose = new Button
            {
                Content = "Close",
                Width = 90,
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(6)
            };
            btnClose.Click += (s, ev) => { dialog.Hide(); };

            buttonPanel.Children.Add(btnCheckUpdates);
            buttonPanel.Children.Add(btnClose);
            panel.Children.Add(buttonPanel);

            dialog.Content = panel;
            await dialog.ShowAsync();
        }

        private static readonly HttpClient UpdateHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        private void ShowUpdateResultDialog(XamlRoot targetXamlRoot, bool hasUpdate, string? newTag, string? releaseUrl, bool isError)
        {
            var popup = new Popup
            {
                XamlRoot = targetXamlRoot,
                ShouldConstrainToRootBounds = true
            };

            var smokeGrid = new Grid
            {
                Width = targetXamlRoot.Size.Width,
                Height = targetXamlRoot.Size.Height,
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
            {
                smokeGrid.Width = sender.Size.Width;
                smokeGrid.Height = sender.Size.Height;
            }
            targetXamlRoot.Changed += OnRootChanged;
            popup.Closed += (s, e) => targetXamlRoot.Changed -= OnRootChanged;

            var dialogCard = new Border
            {
                Width = 350,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 22, 24, 20)
            };

            // Dialog theme styling
            dialogCard.Background = Application.Current.Resources.TryGetValue("ContentDialogBackground", out var bgObj) && bgObj is Brush bg
                ? bg
                : (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var solidBg) && solidBg is Brush sbg
                    ? sbg
                    : new SolidColorBrush(Color.FromArgb(255, 32, 32, 32)));

            dialogCard.BorderBrush = Application.Current.Resources.TryGetValue("ContentDialogBorderBrush", out var borderObj) && borderObj is Brush br
                ? br
                : (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var strokeBrush) && strokeBrush is Brush strBr
                    ? strBr
                    : new SolidColorBrush(Color.FromArgb(255, 60, 60, 65)));

            try
            {
                dialogCard.Shadow = new ThemeShadow();
                dialogCard.Translation = new System.Numerics.Vector3(0, 0, 32);
            }
            catch {}

            var contentPanel = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var currentVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var currentVerText = currentVer != null ? $"v{currentVer.Major}.{currentVer.Minor}" : "v3.0";

            if (isError)
            {
                contentPanel.Children.Add(new FontIcon
                {
                    Glyph = "\uE783",
                    FontSize = 36,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

                contentPanel.Children.Add(new TextBlock
                {
                    Text = "Update Check Failed",
                    FontSize = 17,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                contentPanel.Children.Add(new TextBlock
                {
                    Text = "Unable to connect to GitHub. Please check your internet connection or browse releases manually.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 300,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

                var btnStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var btnReleases = new Button
                {
                    Content = "Open GitHub",
                    Padding = new Thickness(14, 6, 14, 6),
                    CornerRadius = new CornerRadius(6)
                };
                btnReleases.Click += (s, e) =>
                {
                    popup.IsOpen = false;
                    OpenBrowserUrl("https://github.com/aziziyahya/ggZemFixing/releases");
                };
                btnStack.Children.Add(btnReleases);

                var btnClose = new Button
                {
                    Content = "Close",
                    Width = 80,
                    Padding = new Thickness(14, 6, 14, 6),
                    CornerRadius = new CornerRadius(6)
                };
                btnClose.Click += (s, e) => popup.IsOpen = false;
                btnStack.Children.Add(btnClose);

                contentPanel.Children.Add(btnStack);
            }
            else if (hasUpdate)
            {
                contentPanel.Children.Add(new FontIcon
                {
                    Glyph = "\uE896",
                    FontSize = 36,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["AccentOrangeBrush"]
                });

                contentPanel.Children.Add(new TextBlock
                {
                    Text = "Update Available!",
                    FontSize = 17,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                contentPanel.Children.Add(new TextBlock
                {
                    Text = $"A new version ({newTag}) is available!\nYou are currently running {currentVerText}.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 300,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                });

                var btnStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var btnDownload = new Button
                {
                    Content = "Download Update",
                    Padding = new Thickness(16, 6, 16, 6),
                    CornerRadius = new CornerRadius(6)
                };
                btnDownload.Click += (s, e) =>
                {
                    popup.IsOpen = false;
                    OpenBrowserUrl(releaseUrl ?? "https://github.com/aziziyahya/ggZemFixing/releases");
                };
                btnStack.Children.Add(btnDownload);

                var btnLater = new Button
                {
                    Content = "Later",
                    Width = 80,
                    Padding = new Thickness(14, 6, 14, 6),
                    CornerRadius = new CornerRadius(6)
                };
                btnLater.Click += (s, e) => popup.IsOpen = false;
                btnStack.Children.Add(btnLater);

                contentPanel.Children.Add(btnStack);
            }
            else
            {
                contentPanel.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 36,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["AccentGreenBrush"]
                });

                contentPanel.Children.Add(new TextBlock
                {
                    Text = "You're Up to Date",
                    FontSize = 17,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                contentPanel.Children.Add(new TextBlock
                {
                    Text = $"ggZemFixing {currentVerText} is currently the newest version available.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 300,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

                var btnOk = new Button
                {
                    Content = "OK",
                    Width = 100,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(14, 6, 14, 6),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 6, 0, 0)
                };
                btnOk.Click += (s, e) => popup.IsOpen = false;
                contentPanel.Children.Add(btnOk);
            }

            dialogCard.Child = contentPanel;
            smokeGrid.Children.Add(dialogCard);
            popup.Child = smokeGrid;
            popup.IsOpen = true;
        }

        private async void OnSupportClicked(object sender, RoutedEventArgs e)
        {
            var targetXamlRoot = this.XamlRoot ?? this.Content?.XamlRoot;
            if (targetXamlRoot == null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = targetXamlRoot,
                CornerRadius = new CornerRadius(12)
            };

            var panel = new StackPanel { Spacing = 10, Padding = new Thickness(10, 14, 10, 10), Width = 380 };

            // Title
            panel.Children.Add(new TextBlock 
            { 
                Text = "Buy me a Coffee / Matcha", 
                FontSize = 18, 
                FontWeight = Microsoft.UI.Text.FontWeights.Bold, 
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Support Description
            panel.Children.Add(new TextBlock 
            { 
                Text = "Scan a QR code or copy the address to support the developer:", 
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            // Container for QR codes (Side-by-side DuitNow & Bitcoin)
            var qrContainer = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                Spacing = 14, 
                HorizontalAlignment = HorizontalAlignment.Center, 
                Margin = new Thickness(0, 4, 0, 4) 
            };

            StackPanel CreateQrColumn(string imagePath, string labelText, string subText)
            {
                var col = new StackPanel { Spacing = 4, Width = 130 };
                
                var border = new Border
                {
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5),
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                };

                var resourceName = "ggZemFixing.Assets." + System.IO.Path.GetFileName(imagePath);
                var sourceImage = LoadEmbeddedImage(resourceName);

                if (sourceImage != null)
                {
                    var img = new Microsoft.UI.Xaml.Controls.Image
                    {
                        Source = sourceImage,
                        Width = 110,
                        Height = 110,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
                    };
                    border.Child = img;
                }
                else
                {
                    var placeholder = new StackPanel
                    {
                        Width = 110,
                        Height = 110,
                        Spacing = 4,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    placeholder.Children.Add(new FontIcon
                    {
                        Glyph = "\uE91B",
                        FontSize = 26,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                    });
                    placeholder.Children.Add(new TextBlock
                    {
                        Text = "Not configured",
                        FontSize = 9.5,
                        TextAlignment = TextAlignment.Center,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                    });
                    border.Child = placeholder;
                }

                var lbl = new TextBlock
                {
                    Text = labelText,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                };

                var sub = new TextBlock
                {
                    Text = subText,
                    FontSize = 9.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };

                col.Children.Add(border);
                col.Children.Add(lbl);
                col.Children.Add(sub);
                return col;
            }

            qrContainer.Children.Add(CreateQrColumn("Assets/QR_DuitNow.jpg", "DuitNow QR", "Banks & eWallets"));
            qrContainer.Children.Add(CreateQrColumn("Assets/QR_Bitcoin.jpg", "Bitcoin (BTC)", "BTC Network"));

            panel.Children.Add(qrContainer);

            // Bitcoin Address Card with Copy Button
            const string btcAddress = "3Aq32GFhF18kBPZ1DnX9LjbAGfJdVkWfAc";
            var btcCard = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 8, 6),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var btcGrid = new Grid();
            btcGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btcGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var addrStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            addrStack.Children.Add(new TextBlock
            {
                Text = "Bitcoin Address (BTC)",
                FontSize = 9.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            addrStack.Children.Add(new TextBlock
            {
                Text = btcAddress,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                FontSize = 10.5,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(addrStack, 0);
            btcGrid.Children.Add(addrStack);

            var copyBtn = new Button
            {
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var copyStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var copyIcon = new FontIcon { Glyph = "\uE8C8", FontSize = 11 };
            var copyText = new TextBlock { Text = "Copy", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            copyStack.Children.Add(copyIcon);
            copyStack.Children.Add(copyText);
            copyBtn.Content = copyStack;

            copyBtn.Click += async (s, ev) =>
            {
                try
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(btcAddress);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                    
                    copyIcon.Glyph = "\uE73E";
                    copyText.Text = "Copied!";
                    await Task.Delay(2000);
                    copyIcon.Glyph = "\uE8C8";
                    copyText.Text = "Copy";
                }
                catch {}
            };

            Grid.SetColumn(copyBtn, 1);
            btcGrid.Children.Add(copyBtn);

            btcCard.Child = btcGrid;
            panel.Children.Add(btcCard);

            // Custom Close Button
            var btnClose = new Button 
            { 
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 100,
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 4, 0, 0)
            };
            btnClose.Click += (s, ev) => { dialog.Hide(); };
            panel.Children.Add(btnClose);

            dialog.Content = panel;
            await dialog.ShowAsync();
        }

        private async void OnMethodClicked(object sender, RoutedEventArgs e)
        {
            var targetXamlRoot = this.XamlRoot ?? this.Content?.XamlRoot;
            if (targetXamlRoot == null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = targetXamlRoot,
                CornerRadius = new CornerRadius(12)
            };

            var mainPanel = new StackPanel { Spacing = 10, Padding = new Thickness(6, 10, 6, 4), Width = 460 };

            // Header
            var headerPanel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
            headerPanel.Children.Add(new FontIcon
            {
                Glyph = "\uE897",
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"]
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = "How It Works",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = "Automated recovery & diagnostics engines explained.",
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center
            });
            mainPanel.Children.Add(headerPanel);

            var scrollContent = new StackPanel { Spacing = 10 };

            // ── Section 1: Automated MySQL Recovery ──
            var recoveryCard = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var recoveryStack = new StackPanel { Spacing = 8 };

            var recHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            recHeaderRow.Children.Add(new FontIcon
            {
                Glyph = "\uE768",
                FontSize = 13,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
            recHeaderRow.Children.Add(new TextBlock
            {
                Text = "Automated MySQL Recovery",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
            recoveryStack.Children.Add(recHeaderRow);

            recoveryStack.Children.Add(new TextBlock
            {
                Text = "Restores corrupted mysql\\data folder from backup and automatically re-links your custom InnoDB databases.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            var stepsPanel = new StackPanel { Spacing = 6, Margin = new Thickness(2, 2, 0, 0) };
            void AddStep(string num, string title, string desc)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var badge = new Border
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"],
                    CornerRadius = new CornerRadius(7),
                    Width = 16, Height = 16,
                    Margin = new Thickness(0, 1, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = num,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                        FontSize = 9.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetColumn(badge, 0);
                row.Children.Add(badge);

                var textCol = new StackPanel { Spacing = 1 };
                textCol.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] });
                textCol.Children.Add(new TextBlock { Text = desc, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                Grid.SetColumn(textCol, 1);
                row.Children.Add(textCol);
                stepsPanel.Children.Add(row);
            }

            AddStep("1", "Check Running Instances & Port 3306", "Verifies mysqld.exe isn't locking files and checks for external service conflicts.");
            AddStep("2", "Safe Snapshot Backup", "Creates a timestamped snapshot of your corrupted data folder so nothing is ever lost.");
            AddStep("3", "Reconstruct System Tables", "Generates a clean data folder using official XAMPP templates from the backup directory.");
            AddStep("4", "Re-link User Databases", "Transfers all your custom database directories into the clean structure.");
            AddStep("5", "Preserve InnoDB Engine & ibdata1", "Keeps ibdata1 and restores redo logs to maintain complete database integrity.");

            recoveryStack.Children.Add(stepsPanel);
            recoveryCard.Child = recoveryStack;
            scrollContent.Children.Add(recoveryCard);

            // ── Section 2: XAMPP Services & Port Diagnostics ──
            var diagCard = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var diagStack = new StackPanel { Spacing = 8 };

            var diagHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            diagHeaderRow.Children.Add(new FontIcon
            {
                Glyph = "\uE756",
                FontSize = 13,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
            diagHeaderRow.Children.Add(new TextBlock
            {
                Text = "XAMPP Services & Port Diagnostics",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
            diagStack.Children.Add(diagHeaderRow);

            diagStack.Children.Add(new TextBlock
            {
                Text = "Inspects all XAMPP ports (80, 443, 3306) and detects conflicting Windows services (e.g., World Wide Web Publishing Service blocking Apache on Port 80).",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            var diagPoints = new StackPanel { Spacing = 6, Margin = new Thickness(2, 2, 0, 0) };
            void AddDiagPoint(string glyph, string boldText, string normalText)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var icon = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 11,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"],
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 8, 0)
                };
                Grid.SetColumn(icon, 0);
                row.Children.Add(icon);

                var pText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 10.5 };
                var runBold = new Microsoft.UI.Xaml.Documents.Run { Text = boldText + " ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] };
                var runNorm = new Microsoft.UI.Xaml.Documents.Run { Text = normalText, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
                pText.Inlines.Add(runBold);
                pText.Inlines.Add(runNorm);
                Grid.SetColumn(pText, 1);
                row.Children.Add(pText);
                diagPoints.Children.Add(row);
            }

            AddDiagPoint("\uE756", "Real-Time Port Scanning:", "Scans Ports 80, 443, 3306, 8080, 8005, 21, 25 and parses custom ports from httpd.conf and my.ini.");
            AddDiagPoint("\uEA39", "Windows Service Detection:", "Detects IIS / W3SVC (World Wide Web Publishing Service), MsDepSvc, ReportServer, PeerDistSvc, and standalone MySQL services.");
            AddDiagPoint("\uE930", "1-Click 'Fix & Continue':", "Automatically stops blocking Windows services and releases locked ports with UAC elevation support so Apache and MySQL start cleanly.");

            diagStack.Children.Add(diagPoints);
            diagCard.Child = diagStack;
            scrollContent.Children.Add(diagCard);

            var scrollViewer = new ScrollViewer
            {
                Content = scrollContent,
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0)
            };
            mainPanel.Children.Add(scrollViewer);

            var btnClose = new Button
            {
                Content = "Got It",
                Style = TryGetStyle("AccentButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 110,
                Margin = new Thickness(0, 4, 0, 0)
            };
            btnClose.Click += (s, ev) => dialog.Hide();
            mainPanel.Children.Add(btnClose);

            dialog.Content = mainPanel;
            await dialog.ShowAsync();
        }

        // ════════════════════════════════════════════════════════════
        //  Port Conflict Checker
        // ════════════════════════════════════════════════════════════

        private class XamppPortTarget
        {
            public string ServiceName { get; set; } = "";
            public int Port { get; set; }
            public string Protocol { get; set; } = "TCP";
            public string Description { get; set; } = "";
            public bool IsCustom { get; set; }
        }

        private class XamppPortStatus
        {
            public XamppPortTarget Target { get; set; } = null!;
            public bool IsListening { get; set; }
            public string ProcessName { get; set; } = "";
            public string ProcessPath { get; set; } = "";
            public int Pid { get; set; }
            public bool IsXamppProcess { get; set; }
            public bool IsConflict => IsListening && !IsXamppProcess;
        }

        private class XamppServiceCheck
        {
            public string ServiceName { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public int TargetPort { get; set; }
            public string Impact { get; set; } = "";
            public bool IsInstalled { get; set; }
            public bool IsRunning { get; set; }
        }

        private List<XamppPortTarget> GetXamppPortTargets()
        {
            var targets = new List<XamppPortTarget>
            {
                new() { ServiceName = "MySQL / MariaDB", Port = 3306, Description = "Database engine" },
                new() { ServiceName = "Apache HTTP", Port = 80, Description = "Web server (HTTP)" },
                new() { ServiceName = "Apache HTTPS", Port = 443, Description = "Web server (SSL)" },
                new() { ServiceName = "Tomcat HTTP", Port = 8080, Description = "Java application server" },
                new() { ServiceName = "Tomcat Admin", Port = 8005, Description = "Tomcat shutdown port" },
                new() { ServiceName = "FileZilla FTP", Port = 21, Description = "FTP control port" },
                new() { ServiceName = "Mercury SMTP", Port = 25, Description = "Mail transfer protocol" },
                new() { ServiceName = "Mercury POP3", Port = 110, Description = "Post office protocol" },
                new() { ServiceName = "Mercury IMAP", Port = 143, Description = "Internet message access protocol" }
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(XamppRoot) && Directory.Exists(XamppRoot))
                {
                    string myIni = Path.Combine(XamppRoot, "mysql", "bin", "my.ini");
                    if (File.Exists(myIni))
                    {
                        foreach (var line in File.ReadAllLines(myIni))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("port", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                            {
                                var parts = trimmed.Split('=');
                                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int customPort))
                                {
                                    if (targets.All(t => t.Port != customPort))
                                    {
                                        targets.Insert(1, new() { ServiceName = "MySQL (Custom)", Port = customPort, Description = "Configured in my.ini", IsCustom = true });
                                    }
                                    break;
                                }
                            }
                        }
                    }

                    string httpdConf = Path.Combine(XamppRoot, "apache", "conf", "httpd.conf");
                    if (File.Exists(httpdConf))
                    {
                        foreach (var line in File.ReadAllLines(httpdConf))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("Listen ", StringComparison.OrdinalIgnoreCase))
                            {
                                var portStr = trimmed[7..].Trim();
                                if (int.TryParse(portStr, out int customPort))
                                {
                                    if (targets.All(t => t.Port != customPort))
                                    {
                                        targets.Insert(3, new() { ServiceName = "Apache (Custom)", Port = customPort, Description = "Configured in httpd.conf", IsCustom = true });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch {}

            return targets;
        }

        private List<XamppServiceCheck> GetKnownConflictingServices()
        {
            return new List<XamppServiceCheck>
            {
                new() { ServiceName = "W3SVC", DisplayName = "World Wide Web Publishing Service", TargetPort = 80, Impact = "Occupies Port 80, preventing Apache web server from starting." },
                new() { ServiceName = "MsDepSvc", DisplayName = "Web Deployment Agent Service", TargetPort = 80, Impact = "Occupies Port 80." },
                new() { ServiceName = "PeerDistSvc", DisplayName = "BranchCache", TargetPort = 80, Impact = "May occupy Port 80." },
                new() { ServiceName = "ReportServer", DisplayName = "SQL Server Reporting Services", TargetPort = 80, Impact = "Occupies Port 80." },
                new() { ServiceName = "MySQL", DisplayName = "Standalone MySQL Server", TargetPort = 3306, Impact = "External service holding Port 3306." },
                new() { ServiceName = "MySQL57", DisplayName = "MySQL 5.7 Server", TargetPort = 3306, Impact = "External service holding Port 3306." },
                new() { ServiceName = "MySQL80", DisplayName = "MySQL 8.0 Server", TargetPort = 3306, Impact = "External service holding Port 3306." },
                new() { ServiceName = "MariaDB", DisplayName = "Standalone MariaDB Server", TargetPort = 3306, Impact = "External service holding Port 3306." }
            };
        }

        private async void OnCheckPortClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await ShowXamppDiagnosticsDialogAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("Diagnostics Error", ex.Message);
            }
        }

        private async Task ShowXamppDiagnosticsDialogAsync()
        {
            var targetXamlRoot = this.XamlRoot ?? this.Content?.XamlRoot;
            if (targetXamlRoot == null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = targetXamlRoot,
                CornerRadius = new CornerRadius(12)
            };

            var mainPanel = new StackPanel { Spacing = 10, Padding = new Thickness(8, 12, 8, 6), Width = 460 };

            var headerPanel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            var headerIcon = new FontIcon
            {
                Glyph = "\uE946",
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"]
            };
            var headerTitle = new TextBlock
            {
                Text = "XAMPP Ports & Services Inspector",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var headerSubtitle = new TextBlock
            {
                Text = "Scanning active ports and checking Windows services…",
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };

            headerPanel.Children.Add(headerIcon);
            headerPanel.Children.Add(headerTitle);
            headerPanel.Children.Add(headerSubtitle);
            mainPanel.Children.Add(headerPanel);

            var contentStack = new StackPanel { Spacing = 8 };
            contentStack.Children.Add(new ProgressRing
            {
                IsActive = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 24)
            });

            var scrollViewer = new ScrollViewer
            {
                Content = contentStack,
                MaxHeight = 250,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0)
            };
            mainPanel.Children.Add(scrollViewer);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var btnFixContinue = new Button
            {
                Content = "Fix & Continue",
                Style = TryGetStyle("AccentButtonStyle"),
                Width = 120,
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed
            };

            var btnRefresh = new Button { Content = "Re-scan All", Width = 95, FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
            var btnClose = new Button { Content = "Close", Width = 75, FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
            btnClose.Click += (s, ev) => dialog.Hide();

            btnRow.Children.Add(btnFixContinue);
            btnRow.Children.Add(btnRefresh);
            btnRow.Children.Add(btnClose);
            mainPanel.Children.Add(btnRow);

            List<XamppServiceCheck> lastRunningServices = new();
            List<XamppPortStatus> lastPortConflicts = new();

            btnFixContinue.Click += async (s, ev) =>
            {
                btnFixContinue.IsEnabled = false;
                btnRefresh.IsEnabled = false;
                headerSubtitle.Text = "Resolving conflicts (stopping services & releasing ports)…";

                foreach (var svc in lastRunningServices)
                {
                    await StopServiceAsync(svc.ServiceName);
                }

                foreach (var p in lastPortConflicts)
                {
                    if (p.Pid != 4 && p.Pid != 0)
                    {
                        await KillProcessDirectAsync(p.Pid, p.ProcessName);
                    }
                }

                await Task.Delay(1000);
                await PopulateDiagnosticsAsync();
                SetStatusBadge("Conflicts Fixed", ColourOk);
                btnRefresh.IsEnabled = true;
            };

            async Task PopulateDiagnosticsAsync()
            {
                headerSubtitle.Text = "Scanning active ports and checking Windows services…";
                contentStack.Children.Clear();
                contentStack.Children.Add(new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 30) });

                var targets = GetXamppPortTargets();
                var servicesToCheck = GetKnownConflictingServices();

                var portTask = ScanXamppPortsAsync(targets);
                var svcTask = CheckServicesAsync(servicesToCheck);
                await Task.WhenAll(portTask, svcTask);

                var pStatuses = portTask.Result;
                var sStatuses = svcTask.Result;

                contentStack.Children.Clear();

                var runningServices = sStatuses.Where(s => s.IsRunning).ToList();
                int portConflicts = pStatuses.Count(p => p.IsConflict);
                int totalConflicts = portConflicts + runningServices.Count;

                lastRunningServices = runningServices;
                lastPortConflicts = pStatuses.Where(p => p.IsConflict).ToList();

                if (totalConflicts > 0)
                {
                    headerIcon.Glyph = "\uEA39";
                    headerIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));
                    if (runningServices.Any(s => s.ServiceName == "W3SVC"))
                    {
                        headerSubtitle.Text = "⚠️ World Wide Web Publishing Service is blocking Port 80! Click 'Fix & Continue' to resolve.";
                    }
                    else
                    {
                        headerSubtitle.Text = $"⚠️ {totalConflicts} conflict{(totalConflicts > 1 ? "s" : "")} detected! Click 'Fix & Continue' to resolve.";
                    }
                    btnFixContinue.Visibility = Visibility.Visible;
                    btnFixContinue.IsEnabled = true;
                    btnFixContinue.Content = "Fix & Continue";
                    ToolTipService.SetToolTip(btnFixContinue, "Resolve detected conflicts automatically.");
                }
                else
                {
                    headerIcon.Glyph = "\uE930";
                    headerIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94));
                    int activeXampp = pStatuses.Count(p => p.IsListening && p.IsXamppProcess);
                    headerSubtitle.Text = activeXampp > 0
                        ? $"All clear: {activeXampp} active XAMPP service(s) running, no conflicting Windows services."
                        : "All clear: All standard XAMPP ports are free and no conflicting Windows services are running.";
                    btnFixContinue.Visibility = Visibility.Visible;
                    btnFixContinue.IsEnabled = false;
                    btnFixContinue.Content = "No Conflicts";
                    ToolTipService.SetToolTip(btnFixContinue, "All standard ports and services are clear. No fix required.");
                }

                var svcHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 2) };
                svcHeaderRow.Children.Add(new FontIcon { Glyph = "\uE946", FontSize = 14, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"] });
                svcHeaderRow.Children.Add(new TextBlock { Text = "Conflicting Windows Services", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
                contentStack.Children.Add(svcHeaderRow);

                if (runningServices.Count == 0)
                {
                    var clearSvcCard = new Border
                    {
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 10, 12, 10)
                    };
                    var clearRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    clearRow.Children.Add(new FontIcon { Glyph = "\uE930", FontSize = 14, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94)) });
                    clearRow.Children.Add(new TextBlock
                    {
                        Text = "No conflicting Windows services running (World Wide Web Publishing Service / IIS are stopped or not installed).",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                    clearSvcCard.Child = clearRow;
                    contentStack.Children.Add(clearSvcCard);
                }
                else
                {
                    foreach (var svc in runningServices)
                    {
                        var svcCard = new Border
                        {
                            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68)),
                            BorderThickness = new Thickness(1.5),
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(12, 10, 12, 10)
                        };

                        var sGrid = new Grid();
                        sGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        sGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                        var sLeft = new StackPanel { Spacing = 2 };
                        var sTitleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                        sTitleRow.Children.Add(new TextBlock { Text = svc.DisplayName, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

                        var badge = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1) };
                        badge.Child = new TextBlock { Text = $"Blocks Port {svc.TargetPort}", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
                        sTitleRow.Children.Add(badge);
                        sLeft.Children.Add(sTitleRow);

                        sLeft.Children.Add(new TextBlock { Text = $"{svc.Impact} Service name: {svc.ServiceName}", FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68)), TextWrapping = TextWrapping.Wrap });
                        sGrid.Children.Add(sLeft);

                        var btnStop = new Button
                        {
                            Content = "Stop Service",
                            Style = TryGetStyle("AccentButtonStyle"),
                            FontSize = 11,
                            Padding = new Thickness(10, 4, 10, 4),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        btnStop.Click += async (s, ev) =>
                        {
                            btnStop.IsEnabled = false;
                            string res = await StopServiceAsync(svc.ServiceName);
                            if (res == "OK")
                            {
                                SetStatusBadge("Service Stopped", ColourOk);
                                await PopulateDiagnosticsAsync();
                            }
                            else
                            {
                                await ShowKillResultDialogAsync(false, "Stop Failed", $"Could not stop {svc.DisplayName}.\n\n{res}\n\nPlease run ggZemFixing as Administrator to stop Windows Services.");
                                btnStop.IsEnabled = true;
                            }
                        };
                        Grid.SetColumn(btnStop, 1);
                        sGrid.Children.Add(btnStop);

                        svcCard.Child = sGrid;
                        contentStack.Children.Add(svcCard);
                    }
                }

                var portHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 10, 0, 2) };
                portHeaderRow.Children.Add(new FontIcon { Glyph = "\uE756", FontSize = 14, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentOrangeBrush"] });
                portHeaderRow.Children.Add(new TextBlock { Text = "XAMPP Network Ports Status", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
                contentStack.Children.Add(portHeaderRow);

                foreach (var status in pStatuses)
                {
                    var card = new Border
                    {
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 8, 12, 8)
                    };

                    var cardGrid = new Grid();
                    cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                    var leftStack = new StackPanel { Spacing = 2 };

                    var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    titleRow.Children.Add(new TextBlock { Text = status.Target.ServiceName, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

                    var portBadge = new Border
                    {
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"],
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 1, 6, 1)
                    };
                    portBadge.Child = new TextBlock
                    {
                        Text = $"Port {status.Target.Port}",
                        FontSize = 10,
                        FontFamily = new FontFamily("Cascadia Code, Consolas")
                    };
                    titleRow.Children.Add(portBadge);
                    leftStack.Children.Add(titleRow);

                    var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
                    var statusIcon = new FontIcon { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                    var statusDesc = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

                    if (!status.IsListening)
                    {
                        statusIcon.Glyph = "\uE930";
                        statusIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94));
                        statusDesc.Text = "Port is Free — Ready to use";
                        statusDesc.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                    }
                    else if (status.IsXamppProcess)
                    {
                        statusIcon.Glyph = "\uE73E";
                        statusIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 130, 246));
                        statusDesc.Text = $"Active XAMPP: {status.ProcessName} (PID {status.Pid})";
                        statusDesc.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 130, 246));
                    }
                    else
                    {
                        statusIcon.Glyph = "\uEA39";
                        statusIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));
                        string conflictNote = status.Pid == 4
                            ? (runningServices.Any(s => s.ServiceName == "W3SVC")
                                ? "Blocked by World Wide Web Publishing Service (IIS / HTTP.sys)"
                                : "Blocked by Windows System / HTTP.sys (IIS / Web Services)")
                            : $"Blocked by {status.ProcessName} (PID {status.Pid})";
                        statusDesc.Text = conflictNote;
                        statusDesc.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));
                    }

                    statusRow.Children.Add(statusIcon);
                    statusRow.Children.Add(statusDesc);
                    leftStack.Children.Add(statusRow);

                    cardGrid.Children.Add(leftStack);

                    if (status.IsListening && status.Pid != 4 && status.Pid != 0)
                    {
                        var btnKill = new Button
                        {
                            Content = "Kill",
                            FontSize = 10,
                            Padding = new Thickness(8, 2, 8, 2),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        if (status.IsConflict)
                        {
                            btnKill.Style = TryGetStyle("AccentButtonStyle");
                        }
                        btnKill.Click += async (s, ev) =>
                        {
                            btnKill.IsEnabled = false;
                            string kRes = await KillProcessDirectAsync(status.Pid, status.ProcessName);
                            if (kRes == "OK")
                            {
                                SetStatusBadge("Port Cleared", ColourOk);
                                await PopulateDiagnosticsAsync();
                            }
                            else
                            {
                                await ShowKillResultDialogAsync(false, "Kill Failed", $"Could not terminate {status.ProcessName} (PID {status.Pid}).\n{kRes}");
                                btnKill.IsEnabled = true;
                            }
                        };
                        Grid.SetColumn(btnKill, 1);
                        cardGrid.Children.Add(btnKill);
                    }

                    card.Child = cardGrid;
                    contentStack.Children.Add(card);
                }
            }

            btnRefresh.Click += async (s, ev) => await PopulateDiagnosticsAsync();

            dialog.Content = mainPanel;
            var showTask = dialog.ShowAsync();
            await PopulateDiagnosticsAsync();
            await showTask;
        }

        private async Task<List<XamppServiceCheck>> CheckServicesAsync(List<XamppServiceCheck> servicesToCheck)
        {
            return await Task.Run(() =>
            {
                foreach (var s in servicesToCheck)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "sc.exe",
                            Arguments = $"query \"{s.ServiceName}\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = Process.Start(psi)!;
                        string scOut = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();

                        s.IsInstalled = !scOut.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase);
                        s.IsRunning = s.IsInstalled && scOut.Contains("STATE") && scOut.Contains("RUNNING");
                    }
                    catch
                    {
                        s.IsInstalled = false;
                        s.IsRunning = false;
                    }
                }
                return servicesToCheck;
            });
        }

        private async Task<List<XamppPortStatus>> ScanXamppPortsAsync(List<XamppPortTarget> targets)
        {
            // Read UI property on UI thread before entering background thread to avoid COMException 0x8001010E
            string xamppRoot = "";
            try { xamppRoot = XamppRoot; } catch {}

            return await Task.Run(() =>
            {
                var listeners = new Dictionary<int, int>();
                try
                {
                    var netstat = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/C netstat -aon -p tcp",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var ns = Process.Start(netstat)!;
                    string output = ns.StandardOutput.ReadToEnd();
                    ns.WaitForExit();

                    foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = rawLine.Trim();
                        if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (cols.Length < 5)
                            continue;

                        string localAddr = cols[1];
                        int lastColon = localAddr.LastIndexOf(':');
                        if (lastColon < 0)
                            continue;

                        string portStr = localAddr[(lastColon + 1)..];
                        if (int.TryParse(portStr, out int portNum) && int.TryParse(cols[^1], out int pid))
                        {
                            listeners[portNum] = pid;
                        }
                    }
                }
                catch {}

                var procMap = new Dictionary<int, (string Name, string Path)>();
                try
                {
                    foreach (var proc in Process.GetProcesses())
                    {
                        try
                        {
                            procMap[proc.Id] = (proc.ProcessName, proc.MainModule?.FileName ?? "");
                        }
                        catch
                        {
                            procMap[proc.Id] = (proc.ProcessName, "");
                        }
                    }
                }
                catch {}
                procMap[4] = ("System (HTTP.sys)", @"C:\Windows\System32\ntoskrnl.exe");

                var portList = new List<XamppPortStatus>();
                foreach (var target in targets)
                {
                    var status = new XamppPortStatus { Target = target };
                    if (listeners.TryGetValue(target.Port, out int pid))
                    {
                        status.IsListening = true;
                        status.Pid = pid;

                        if (procMap.TryGetValue(pid, out var pInfo))
                        {
                            status.ProcessName = pInfo.Name;
                            status.ProcessPath = pInfo.Path;
                        }
                        else
                        {
                            status.ProcessName = $"PID {pid}";
                        }

                        if (!string.IsNullOrEmpty(status.ProcessPath) && !string.IsNullOrEmpty(xamppRoot) &&
                            status.ProcessPath.StartsWith(xamppRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            status.IsXamppProcess = true;
                        }
                        else if (status.ProcessName.Equals("mysqld", StringComparison.OrdinalIgnoreCase) ||
                                 status.ProcessName.Equals("mariadbd", StringComparison.OrdinalIgnoreCase) ||
                                 status.ProcessName.Equals("httpd", StringComparison.OrdinalIgnoreCase) ||
                                 status.ProcessName.Equals("filezillaserver", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(status.ProcessPath) && status.ProcessPath.Contains("xampp", StringComparison.OrdinalIgnoreCase))
                                status.IsXamppProcess = true;
                            else if (string.IsNullOrEmpty(status.ProcessPath))
                                status.IsXamppProcess = true;
                        }
                    }
                    portList.Add(status);
                }

                return portList;
            });
        }

        private async Task<string> StopServiceAsync(string serviceName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "net.exe",
                        Arguments = $"stop \"{serviceName}\" /y",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi)!;
                    string err = proc.StandardError.ReadToEnd().Trim();
                    proc.WaitForExit();

                    if (proc.ExitCode == 0) return "OK";

                    // If failed due to permissions, prompt Windows UAC elevation
                    if (proc.ExitCode == 2 || proc.ExitCode == 5 || err.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var elevatedPsi = new ProcessStartInfo
                            {
                                FileName = "net.exe",
                                Arguments = $"stop \"{serviceName}\" /y",
                                UseShellExecute = true,
                                Verb = "runas",
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            using var elevProc = Process.Start(elevatedPsi);
                            elevProc?.WaitForExit();
                            if (elevProc?.ExitCode == 0) return "OK";
                        }
                        catch (Exception elevEx)
                        {
                            return $"Administrator elevation needed: {elevEx.Message}";
                        }
                    }

                    return string.IsNullOrWhiteSpace(err) ? $"Exit code {proc.ExitCode}" : err;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });
        }

        private async Task<string> KillProcessDirectAsync(int pid, string processName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                    return "OK";
                }
                catch (Exception ex)
                {
                    // Fallback to elevated taskkill if access denied
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "taskkill.exe",
                            Arguments = $"/F /T /PID {pid}",
                            UseShellExecute = true,
                            Verb = "runas",
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        using var elevProc = Process.Start(psi);
                        elevProc?.WaitForExit();
                        if (elevProc?.ExitCode == 0) return "OK";
                    }
                    catch {}

                    return ex.Message;
                }
            });
        }


        private async Task ShowKillResultDialogAsync(bool success, string title, string message)
        {
            var dialog = new ContentDialog { XamlRoot = this.XamlRoot, CornerRadius = new CornerRadius(12) };
            var panel  = new StackPanel { Spacing = 16, Padding = new Thickness(10, 20, 10, 10) };

            panel.Children.Add(new FontIcon
            {
                Glyph      = success ? "\uE930" : "\uEA39",
                FontSize   = 48,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    success ? Windows.UI.Color.FromArgb(255, 39, 174, 96)
                            : Windows.UI.Color.FromArgb(255, 231, 76, 60)),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = title, FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            var btnClose = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Center, Width = 110, Margin = new Thickness(0, 8, 0, 0) };
            btnClose.Click += (s, ev) => dialog.Hide();
            panel.Children.Add(btnClose);

            dialog.Content = panel;
            await dialog.ShowAsync();
        }

        // ════════════════════════════════════════════════════════════
        //  Core recovery orchestration
        // ════════════════════════════════════════════════════════════

        private async Task RunRecoveryAsync()
        {
            if (string.IsNullOrWhiteSpace(XamppRoot) || !Directory.Exists(XamppRoot))
            {
                await ShowErrorDialogAsync("Invalid Path", $"The specified XAMPP folder does not exist:\n{XamppRoot}");
                return;
            }

            _isRunning = true;
            SetUiBusy(true);
            ClearProgress();

            string mysqlRoot   = MysqlRoot;
            string backupPath  = BackupPath;
            string dataPath    = DataPath;

            try
            {
                SetProgress(5, "Checking for running instances & port conflicts…");
                await Task.Delay(600);

                // Pre-flight check on Port 3306 & conflicting database services
                var port3306Targets = GetXamppPortTargets().Where(t => t.Port == 3306).ToList();
                var pScan = await ScanXamppPortsAsync(port3306Targets);
                var p3306 = pScan.FirstOrDefault();
                var svcScan = await CheckServicesAsync(GetKnownConflictingServices());
                var runningDbServices = svcScan.Where(s => s.IsRunning && s.TargetPort == 3306).ToList();

                if ((p3306 != null && p3306.IsConflict) || runningDbServices.Count > 0)
                {
                    string conflictDesc = p3306 != null && p3306.IsConflict
                        ? $"{p3306.ProcessName} (PID {p3306.Pid}) occupying Port 3306"
                        : string.Join(", ", runningDbServices.Select(s => s.DisplayName));

                    SetStatusBadge("Conflict", ColourWarn);
                    bool shouldFix = await ShowPortConflictContinueDialogAsync(conflictDesc);
                    if (!shouldFix)
                    {
                        SetStatusBadge("Cancelled", ColourWarn);
                        SetProgress(0, "Recovery cancelled");
                        return;
                    }

                    SetProgress(8, "Stopping conflicting services & releasing port 3306…");
                    foreach (var s in runningDbServices)
                    {
                        await StopServiceAsync(s.ServiceName);
                    }
                    if (p3306 != null && p3306.Pid != 4 && p3306.Pid != 0)
                    {
                        await KillProcessDirectAsync(p3306.Pid, p3306.ProcessName);
                    }
                    await Task.Delay(800);
                }

                if (IsMysqldRunning())
                {
                    SetStatusBadge("Blocked", ColourError);
                    SetProgress(0, "Blocked — close XAMPP first");
                    
                    bool forceClose = await ShowMySQLRunningDialogAsync();
                    if (!forceClose)
                    {
                        return;
                    }

                    SetProgress(10, "Closing XAMPP and MySQL instances…");
                    await KillXamppAndMySqlProcessesAsync();
                    await Task.Delay(1000); // Give the OS a moment to release locks

                    if (IsMysqldRunning())
                    {
                        SetStatusBadge("Blocked", ColourError);
                        SetProgress(0, "Blocked — close manually");
                        await ShowErrorDialogAsync("Could Not Stop MySQL", "Unable to automatically stop MySQL. Please stop MySQL in the XAMPP Control Panel or Task Manager manually.");
                        return;
                    }
                }

                SetProgress(20, "Renaming data folder…");
                await Task.Delay(600);

                string timestamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dataOldPath = Path.Combine(mysqlRoot, $"data_old_{timestamp}");

                if (!Directory.Exists(dataPath))
                {
                    SetStatusBadge("Failed", ColourError);
                    SetProgress(20, "data folder not found");
                    await ShowErrorDialogAsync("Folder Not Found", $"The data folder was not found at:\n{dataPath}");
                    return;
                }

                try { Directory.Move(dataPath, dataOldPath); }
                catch (Exception ex)
                {
                    SetStatusBadge("Failed", ColourError);
                    SetProgress(20, "Rename failed");
                    await ShowErrorDialogAsync("Rename Failed", $"Could not rename the data folder. Check if files are open or if XAMPP is fully closed.\n\nError: {ex.Message}");
                    return;
                }

                SetProgress(40, "Creating new data folder from backup…");
                await Task.Delay(1000);

                if (!Directory.Exists(backupPath))
                {
                    TryRollback(dataOldPath, dataPath);
                    SetStatusBadge("Failed", ColourError);
                    SetProgress(40, "Backup folder missing");
                    await ShowErrorDialogAsync("Backup Missing", $"The backup folder was not found at:\n{backupPath}\n\nRecovery cannot continue.");
                    return;
                }

                try { await Task.Run(() => CopyDirectory(backupPath, dataPath)); }
                catch (Exception ex)
                {
                    SetStatusBadge("Failed", ColourError);
                    SetProgress(40, "Backup copy failed");
                    await ShowErrorDialogAsync("Copy Failed", $"Failed to copy the backup folder to the data folder.\n\nError: {ex.Message}");
                    return;
                }

                SetProgress(60, "Scanning for user databases…");
                await Task.Delay(800);

                string[] allDirs = [];
                try { allDirs = Directory.GetDirectories(dataOldPath); }
                catch (Exception ex)
                {
                    SetStatusBadge("Failed", ColourError);
                    SetProgress(60, "Cannot read data_old");
                    await ShowErrorDialogAsync("Read Failed", $"Could not read from the renamed data folder.\n\nError: {ex.Message}");
                    return;
                }

                var userDbs = allDirs.Select(d => new DirectoryInfo(d)).Where(d => !SystemFolders.Contains(d.Name)).ToList();

                if (userDbs.Count > 0)
                {
                    int dbIndex = 0;
                    foreach (var dbDir in userDbs)
                    {
                        dbIndex++;
                        string destDb = Path.Combine(dataPath, dbDir.Name);
                        double pct = 60 + (dbIndex / (double)userDbs.Count * 20);
                        SetProgress((int)pct, $"Restoring: {dbDir.Name}");
                        await Task.Delay(300); // Simulate restoring time per database
                        try { await Task.Run(() => CopyDirectory(dbDir.FullName, destDb)); } catch { }
                    }
                }

                SetProgress(85, "Copying ibdata1…");
                await Task.Delay(600);

                string ibdataSource = Path.Combine(dataOldPath, "ibdata1");
                string ibdataDest   = Path.Combine(dataPath, "ibdata1");

                if (File.Exists(ibdataSource))
                {
                    try { await Task.Run(() => File.Copy(ibdataSource, ibdataDest, overwrite: true)); } catch { }
                }

                SetProgress(100, "Recovery complete!");
                SetStatusBadge("Complete", ColourOk);
                await Task.Delay(500); // Small pause at 100% before the dialog pops up
                await ShowSuccessDialogAsync();
            }
            catch (Exception ex)
            {
                SetStatusBadge("Error", ColourError);
                await ShowErrorDialogAsync("Unexpected Error", $"A fatal error occurred during recovery:\n\n{ex.Message}");
            }
            finally
            {
                _isRunning = false;
                SetUiBusy(false);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Success UX
        // ════════════════════════════════════════════════════════════
        
        private async Task ShowSuccessDialogAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                CornerRadius = new CornerRadius(12)
            };

            var panel = new StackPanel { Spacing = 16, Padding = new Thickness(10, 20, 10, 10) };

            // Big success icon
            panel.Children.Add(new FontIcon 
            { 
                Glyph = "\uE930", // Checkmark
                FontSize = 56, 
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 39, 174, 96)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Title
            panel.Children.Add(new TextBlock 
            { 
                Text = "Recovery Complete", 
                FontSize = 24, 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Description
            panel.Children.Add(new TextBlock 
            { 
                Text = "Your databases have been restored.\nYou can safely restart MySQL in XAMPP.", 
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            // Student credit
            panel.Children.Add(new TextBlock 
            { 
                Text = "Built by Azizi Yahya. From student to student.", 
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(0, 4, 0, 4)
            });

            // Primary Actions Stack (Close + Open Folder + Control Panel)
            var btnPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                Spacing = 8, 
                HorizontalAlignment = HorizontalAlignment.Center, 
                Margin = new Thickness(0, 16, 0, 0) 
            };
            
            var btnClose = new Button 
            { 
                Content = "Close",
                Width = 100
            };
            btnClose.Click += (s, e) => { dialog.Hide(); };

            var btnFolder = new Button 
            { 
                Content = "Open Folder", 
                Width = 110
            };
            btnFolder.Click += (s, e) => {
                try { System.Diagnostics.Process.Start("explorer.exe", $"\"{MysqlRoot}\""); } catch { }
                dialog.Hide();
            };

            var btnControlPanel = new Button 
            { 
                Content = "Control Panel", 
                Style = TryGetStyle("AccentButtonStyle"),
                Width = 120
            };
            btnControlPanel.Click += (s, e) => {
                try {
                    string controlPanelPath = Path.Combine(XamppRoot, "xampp-control.exe");
                    if (File.Exists(controlPanelPath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = controlPanelPath, UseShellExecute = true });
                    }
                    else
                    {
                        string defaultPath = @"C:\xampp\xampp-control.exe";
                        if (File.Exists(defaultPath))
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = defaultPath, UseShellExecute = true });
                        }
                    }
                } catch { }
                dialog.Hide();
            };

            btnPanel.Children.Add(btnClose);
            btnPanel.Children.Add(btnFolder);
            btnPanel.Children.Add(btnControlPanel);
            panel.Children.Add(btnPanel);

            dialog.Content = panel;
            await dialog.ShowAsync();
        }

        private async Task ShowErrorDialogAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                CornerRadius = new CornerRadius(12)
            };

            var panel = new StackPanel { Spacing = 16, Padding = new Thickness(10, 20, 10, 10) };

            // Big error icon
            panel.Children.Add(new FontIcon 
            { 
                Glyph = "\uEA39", // Warning / Error icon
                FontSize = 56, 
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 231, 76, 60)), // Red
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Title
            panel.Children.Add(new TextBlock 
            { 
                Text = title, 
                FontSize = 20, 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Description
            panel.Children.Add(new TextBlock 
            { 
                Text = message, 
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            // Close Button
            var btnClose = new Button 
            { 
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 120,
                Margin = new Thickness(0, 16, 0, 0)
            };
            btnClose.Click += (s, e) => { dialog.Hide(); };
            panel.Children.Add(btnClose);

            dialog.Content = panel;
            await dialog.ShowAsync();
        }

        // ════════════════════════════════════════════════════════════
        //  Helper — file system
        // ════════════════════════════════════════════════════════════

        private static bool IsMysqldRunning()
        {
            try { return Process.GetProcessesByName("mysqld").Length > 0; }
            catch { return false; }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string filePath in Directory.GetFiles(source))
                File.Copy(filePath, Path.Combine(destination, Path.GetFileName(filePath)), overwrite: true);
            foreach (string subDir in Directory.GetDirectories(source))
                CopyDirectory(subDir, Path.Combine(destination, Path.GetFileName(subDir)));
        }

        private static void TryRollback(string dataOldPath, string dataPath)
        {
            try
            {
                if (Directory.Exists(dataOldPath) && !Directory.Exists(dataPath))
                    Directory.Move(dataOldPath, dataPath);
            }
            catch { /* best-effort */ }
        }

        private async Task<bool> ShowPortConflictContinueDialogAsync(string details)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                CornerRadius = new CornerRadius(12),
                Title = "Port 3306 Conflict Detected",
                Content = new TextBlock
                {
                    Text = $"A conflicting service or process is currently occupying MySQL Port 3306:\n\n{details}\n\nWould you like to automatically stop the conflicting service, release the port, and continue with recovery?",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                },
                PrimaryButtonText = "Fix & Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async Task<bool> ShowMySQLRunningDialogAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                CornerRadius = new CornerRadius(12),
                Title = "MySQL is Running",
                Content = new TextBlock
                {
                    Text = "MySQL is currently running. The recovery tool needs to modify MySQL database files, which are locked when MySQL is active.\n\nWould you like to automatically close all running XAMPP and MySQL instances and proceed?",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                },
                PrimaryButtonText = "Close all & Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async Task<bool> KillXamppAndMySqlProcessesAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Kill mysqld.exe (MySQL Database Server)
                    foreach (var proc in Process.GetProcessesByName("mysqld"))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }

                    // Kill xampp-control.exe (XAMPP Control Panel)
                    foreach (var proc in Process.GetProcessesByName("xampp-control"))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        // ════════════════════════════════════════════════════════════
        //  Helper — UI state
        // ════════════════════════════════════════════════════════════

        private void SetUiBusy(bool busy)
        {
            BtnStartRecovery.IsEnabled = !busy && Directory.Exists(XamppRoot) && Directory.Exists(BackupPath);
            BtnBrowse.IsEnabled        = !busy;
            TxtMysqlRoot.IsReadOnly    = busy;
            BtnLabel.Text  = busy ? "Running…" : "Start Recovery";
            BtnIcon.Glyph  = busy ? "\uF16A"   : "\uE768";
        }

        private void ClearProgress()
        {
            RecoveryProgressBar.Value = 0;
            ProgressPercentText.Text  = "0%";
            ProgressDetailText.Text   = "Initializing…";
            SetStatusBadge("Running", ColourWarn);
        }

        private void SetProgress(int percent, string detail)
        {
            RecoveryProgressBar.Value = percent;
            ProgressPercentText.Text  = $"{percent}%";
            ProgressDetailText.Text   = detail;
        }

        private void SetStatusBadge(string text, Windows.UI.Color colour)
        {
            try
            {
                if (RecoveryStatusBadge != null)
                {
                    RecoveryStatusBadge.Text = text;
                    RecoveryStatusBadge.Foreground = new SolidColorBrush(colour);
                }
            }
            catch {}
        }

        private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage LoadEmbeddedImage(string resourceName)
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) return null!;

                using var ms = new System.IO.MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using var randomAccessStream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms);
                bitmapImage.SetSource(randomAccessStream);
                return bitmapImage;
            }
            catch
            {
                return null!;
            }
        }
    }
}
