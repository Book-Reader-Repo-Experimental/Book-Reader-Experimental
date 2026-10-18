#if IOS
using Foundation;
using UIKit;
using WebKit;
using System;
using System.Threading.Tasks;
using CoreGraphics;

namespace BookViewer.Platforms.iOS
{
    public static class HtmlJsEvaluator
    {
        public static async Task<string> EvaluateInteractiveElementsAsync(string html)
        {
            var tcs = new TaskCompletionSource<string>();
            var config = new WKWebViewConfiguration();
            var webView = new WKWebView(new CGRect(0, 0, 1024, 1344), config);

            var navDelegate = new JsEvalNavigationDelegate(async () =>
            {
                try
                {
                    await Task.Delay(300);
                    var js = "window.__getInteractiveElements ? window.__getInteractiveElements() : '[]'";
                    var result = await webView.EvaluateJavaScriptAsync(js);
                    tcs.TrySetResult(result?.ToString() ?? "[]");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"JS eval error: {ex.Message}");
                    tcs.TrySetResult("[]");
                }
            });

            webView.NavigationDelegate = navDelegate;
            webView.LoadHtmlString(html, new NSUrl("file:///"));

            return await tcs.Task;
        }

        private class JsEvalNavigationDelegate : WKNavigationDelegate
        {
            private readonly Func<Task> _onFinished;
            private bool _hasRun;
            public JsEvalNavigationDelegate(Func<Task> onFinished) { _onFinished = onFinished; }
            public override async void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
            {
                if (_hasRun) return;
                _hasRun = true;
                await _onFinished();
            }
        }
    }
}
#endif
