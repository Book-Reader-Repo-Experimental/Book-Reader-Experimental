#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;

namespace BookViewer.Platforms.Windows
{
    public static class HtmlJsEvaluator
    {
        public static async Task<string> EvaluateInteractiveElementsAsync(string html)
        {
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

                hostControl.Measure(new global::Windows.Foundation.Size(1024, 1344));
                hostControl.Arrange(new global::Windows.Foundation.Rect(0, 0, 1024, 1344));
                hostControl.UpdateLayout();

                var tcs = new TaskCompletionSource<bool>();
                webView.NavigationCompleted += (s, e) => tcs.TrySetResult(true);

                webView.NavigateToString(html);
                await tcs.Task;
                await Task.Delay(300);

                var js = "window.__getPdfDom ? window.__getPdfDom() : '[]'";
                var result = await webView.CoreWebView2.ExecuteScriptAsync(js);

                webView.Close();
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
