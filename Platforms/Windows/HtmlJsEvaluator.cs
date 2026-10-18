#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BookViewer.Platforms.Windows
{
    public static class HtmlJsEvaluator
    {
        public static async Task<string> EvaluateInteractiveElementsAsync(string html)
        {
            var tcs = new TaskCompletionSource<string>();
            try
            {
                var hostControl = new Microsoft.UI.Xaml.Controls.Grid { Width = 1024, Height = 1344 };
                var webView = new Microsoft.UI.Xaml.Controls.WebView2 { Width = 1024, Height = 1344 };
                hostControl.Children.Add(webView);

                var userDataFolder = Path.Combine(Path.GetTempPath(), "BookViewerWebView2");
                Directory.CreateDirectory(userDataFolder);

                var options = new CoreWebView2EnvironmentOptions();
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
                await webView.EnsureCoreWebView2Async(env);

                // Wait for navigation to finish
                var navTcs = new TaskCompletionSource<bool>();
                webView.NavigationCompleted += (s, e) => navTcs.TrySetResult(true);

                webView.NavigateToString(html);
                await navTcs.Task;
                
                // CRITICAL: Wait for WebView2 to render the layout and execute the script
                await Task.Delay(2000);

                var js = "window.__getPdfDom ? window.__getPdfDom() : '[]'";
                var result = await webView.CoreWebView2.ExecuteScriptAsync(js);

                webView.Close();

                // WebView2 returns JSON-encoded string (e.g., "\"[{...}]\"")
                if (!string.IsNullOrEmpty(result))
                {
                    try
                    {
                        var unescaped = JsonSerializer.Deserialize<string>(result);
                        if (!string.IsNullOrEmpty(unescaped))
                        {
                            return unescaped;
                        }
                    }
                    catch
                    {
                        // If it fails, return the raw result
                    }
                }
                return result ?? "[]";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JS eval error: {ex.Message}");
                return "[]";
            }
        }
    }
}
#endif
