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
                global::Android.Webkit.WebView? webView = null;
                try
                {
                    webView = new global::Android.Webkit.WebView(global::Android.App.Application.Context);
                    var settings = webView.Settings;
                    if (settings != null)
                    {
                        settings.JavaScriptEnabled = true;
                        settings.LoadsImagesAutomatically = true;
                        settings.AllowFileAccess = true;
                        settings.AllowFileAccessFromFileURLs = true;
                        settings.AllowUniversalAccessFromFileURLs = true;
                        settings.BlockNetworkLoads = false;
                    }

                    var finished = new TaskCompletionSource<bool>();
                    webView.SetWebViewClient(new JsEvalClient(() => finished.TrySetResult(true)));
                    webView.LoadDataWithBaseURL("file:///android_asset/", html, "text/html", "UTF-8", null);

                    var completed = await Task.WhenAny(finished.Task, Task.Delay(10000));
                    if (completed == finished.Task) await finished.Task;
                    await Task.Delay(300);

                    var js = "window.__getPdfDom ? window.__getPdfDom() : '[]'";
                    webView.EvaluateJavascript(js, new JsValueCallback(tcs));
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"JS eval error: {ex.Message}");
                    tcs.TrySetResult("[]");
                }
                finally
                {
                    try { webView?.Destroy(); } catch { }
                }
            });
            return await tcs.Task;
        }

        private class JsEvalClient : WebViewClient
        {
            private readonly Action _onFinished;
            public JsEvalClient(Action onFinished) { _onFinished = onFinished; }
            public override void OnPageFinished(global::Android.Webkit.WebView? view, string? url)
            {
                base.OnPageFinished(view, url);
                _onFinished?.Invoke();
            }
        }

        private class JsValueCallback : Java.Lang.Object, IValueCallback
        {
            private readonly TaskCompletionSource<string> _tcs;
            public JsValueCallback(TaskCompletionSource<string> tcs) { _tcs = tcs; }
            public void OnReceiveValue(Java.Lang.Object? value)
            {
                var str = value?.ToString() ?? "[]";
                if (str.StartsWith("\"") && str.EndsWith("\"") && str.Length >= 2)
                {
                    str = str.Substring(1, str.Length - 2);
                    str = System.Text.RegularExpressions.Regex.Unescape(str);
                }
                _tcs.TrySetResult(str);
            }
        }
    }
}
#endif
