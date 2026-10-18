#if ANDROID
using Android.Views;
using Android.Webkit;
using Microsoft.Maui.ApplicationModel;
using System;
using System.Threading.Tasks;

namespace BookViewer.Platforms.Android
{
    public static class HtmlJsEvaluator
    {
        public static async Task<string> EvaluateInteractiveElementsAsync(string html)
        {
            var tcs = new TaskCompletionSource<string>();
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var webView = new global::Android.Webkit.WebView(global::Android.App.Application.Context);
                webView.Settings.JavaScriptEnabled = true;
                webView.Settings.AllowFileAccess = true;
                
                var finished = new TaskCompletionSource<bool>();
                webView.SetWebViewClient(new JsEvalClient(() => finished.TrySetResult(true)));
                webView.LoadDataWithBaseURL("file:///android_asset/", html, "text/html", "UTF-8", null);
                
                await Task.WhenAny(finished.Task, Task.Delay(5000));
                await Task.Delay(500); // Wait for layout
                
                var result = await webView.EvaluateJavascriptAsync("window.__getInteractiveElements()");
                webView.Destroy();
                tcs.TrySetResult(result);
            });
            return await tcs.Task;
        }

        private class JsEvalClient : WebViewClient
        {
            private readonly Action _onFinished;
            public JsEvalClient(Action onFinished) => _onFinished = onFinished;
            public override void OnPageFinished(global::Android.Webkit.WebView view, string url) => _onFinished();
        }
    }
}
#endif
