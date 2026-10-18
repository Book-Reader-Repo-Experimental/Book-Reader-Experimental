using BookViewer;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BookViewer.Services
{
    public class DownloadService
    {
        private const string BaseUrl = "https://isolution.oupchina.com.hk";
        private readonly HttpClient _httpClient;
        private readonly DecryptionService _decryptionService = new();
        private bool _isDownloading;

        public event EventHandler<int> OnProgress;
        public event EventHandler<string> OnComplete;
        public event EventHandler<string> OnError;

        private static readonly object _logLock = new object();
        private static string _logFilePath = null;

        public DownloadService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "BookViewer/1.0");
        }

        private static string GetSharedResourcesDir()
        {
            var booksDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "BookViewer", "Books");
            Directory.CreateDirectory(booksDir);
            var shared = Path.Combine(booksDir, "resources");
            Directory.CreateDirectory(shared);
            return shared;
        }

        private string GetLogFilePath()
        {
            if (_logFilePath == null)
            {
                try
                {
                    string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string logFolder = Path.Combine(documentsPath, "BookViewer");
                    Directory.CreateDirectory(logFolder);
                    _logFilePath = Path.Combine(logFolder, $"DownloadService_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
                catch
                {
                    _logFilePath = Path.Combine(Path.GetTempPath(), $"DownloadService_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
            }
            return _logFilePath;
        }

        private void Log(string message)
        {
            try
            {
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
                lock (_logLock)
                {
                    File.AppendAllText(GetLogFilePath(), logMessage + Environment.NewLine);
                }
                System.Diagnostics.Debug.WriteLine($"[DownloadService] {logMessage}");
            }
            catch
            {
            }
        }

        public async Task DownloadBookAsync(int bookNumber)
        {
            if (_isDownloading)
            {
                OnError?.Invoke(this, "Download already in progress");
                return;
            }

            _isDownloading = true;

            try
            {
                Log($"=== DOWNLOAD BOOK {bookNumber} START ===");
                OnProgress?.Invoke(this, 0);

                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var booksDir = Path.Combine(documentsPath, "BookViewer", "Books");
                Directory.CreateDirectory(booksDir);
                Log($"Books directory: {booksDir}");

                var tempDir = Path.Combine(booksDir, "temp_" + Guid.NewGuid().ToString().Substring(0, 8));
                Directory.CreateDirectory(tempDir);
                Log($"Temp directory: {tempDir}");

                var finalBookDir = Path.Combine(booksDir, $"book_{bookNumber}");

                if (Directory.Exists(finalBookDir))
                {
                    Log($"Book {bookNumber} already exists at: {finalBookDir}");
                    OnError?.Invoke(this, $"Book {bookNumber} already exists. Please delete it first.");
                    _isDownloading = false;
                    return;
                }

                var urlPatterns = new[]
                {
                    $"ebook_distribute/V3_otf/{bookNumber}/P02/book_{bookNumber}_P02.zip",
                    $"ebook_distribute/V3_otf/{bookNumber}/book_{bookNumber}.zip",
                };

                bool bookDownloaded = false;
                string bookExtractDir = null;
                string parentDir = null;

                for (int i = 0; i < urlPatterns.Length; i++)
                {
                    if (bookDownloaded) break;

                    var pattern = urlPatterns[i];
                    var url = $"{BaseUrl}/{pattern}";
                    var savePath = Path.Combine(tempDir, $"book_{bookNumber}.zip");

                    Log($"Trying URL [{i + 1}/{urlPatterns.Length}]: {url}");
                    OnProgress?.Invoke(this, 10 + (i * 20));

                    if (await DownloadFileAsync(url, savePath))
                    {
                        var size = new FileInfo(savePath).Length;
                        Log($"Downloaded: {savePath} ({size} bytes)");

                        if (size < 1000)
                        {
                            Log($"File too small, likely not a valid zip. Skipping.");
                            try { File.Delete(savePath); } catch { }
                            continue;
                        }

                        if (await VerifyZipAsync(savePath))
                        {
                            Log("Zip signature verified");

                            bookExtractDir = Path.Combine(tempDir, "extracted");
                            Directory.CreateDirectory(bookExtractDir);

                            OnProgress?.Invoke(this, 30);
                            Log("Extracting main book zip...");
                            await ExtractZipAsync(savePath, bookExtractDir);
                            Log("Main book zip extracted");

                            try { File.Delete(savePath); } catch { }
                            bookDownloaded = true;

                            parentDir = pattern.Contains("/P02/")
                                ? pattern.Split("/P02/")[0] + "/P02"
                                : pattern.Substring(0, pattern.LastIndexOf('/'));
                            Log($"Parent dir resolved: {parentDir}");

                            // Resource PC (goes to shared folder)
                            OnProgress?.Invoke(this, 50);
                            Log("Downloading resource PC zip...");
                            await DownloadAndExtractResourcePCAsync(bookNumber, parentDir, bookExtractDir);

                            // Units
                            OnProgress?.Invoke(this, 65);
                            var unitUids = FindUnitUids(bookExtractDir);
                            Log($"Found {unitUids.Count} unit UIDs: [{string.Join(", ", unitUids)}]");

                            if (unitUids.Any())
                            {
                                Log("Downloading unit zips...");
                                await DownloadAndExtractUnitsAsync(unitUids, parentDir, bookNumber, bookExtractDir);
                            }

                            // Copy to final
                            OnProgress?.Invoke(this, 80);
                            Log($"Copying to final directory: {finalBookDir}");
                            Directory.CreateDirectory(finalBookDir);
                            CopyDirectory(bookExtractDir, finalBookDir);

                            // Small delay to let filesystem settle
                            await Task.Delay(200);

                            // ============================================================
                            // DECRYPT EVERYTHING NOW
                            // ============================================================
                            OnProgress?.Invoke(this, 88);
                            Log("=== DECRYPTING DOWNLOADED BOOK ===");
                            await DecryptBookFolderAsync(finalBookDir, bookNumber.ToString());
                            Log("=== DECRYPTION COMPLETE ===");

                            // Cover
                            OnProgress?.Invoke(this, 95);
                            Log("Locating/copying cover image...");
                            await CopyBookCoverAsync(bookNumber, finalBookDir);

                            // Cleanup
                            Log("Cleaning up temp directory...");
                            try { Directory.Delete(tempDir, true); } catch { }

                            OnProgress?.Invoke(this, 100);
                            Log($"=== DOWNLOAD BOOK {bookNumber} SUCCESS ===");
                            OnComplete?.Invoke(this, finalBookDir);
                            _isDownloading = false;
                            return;
                        }
                        else
                        {
                            Log($"Zip verification FAILED for {savePath}");
                            try { File.Delete(savePath); } catch { }
                        }
                    }
                    else
                    {
                        Log($"Download FAILED: {url}");
                    }
                }

                if (!bookDownloaded)
                {
                    Log($"=== DOWNLOAD BOOK {bookNumber} FAILED (all URLs exhausted) ===");
                    OnError?.Invoke(this, $"Failed to download book {bookNumber}");
                }
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
                OnError?.Invoke(this, $"Download failed: {ex.Message}");
            }
            finally
            {
                _isDownloading = false;
            }
        }

        private async Task DecryptBookFolderAsync(string bookFolder, string bookUid)
        {
            try
            {
                var htmlFiles = Directory.GetFiles(bookFolder, "*.html", SearchOption.AllDirectories).ToList();
                var xmlFiles = Directory.GetFiles(bookFolder, "*.xml", SearchOption.AllDirectories).ToList();
                var htmFiles = Directory.GetFiles(bookFolder, "*.htm", SearchOption.AllDirectories).ToList();

                Log($"Decrypt: {htmlFiles.Count} html, {xmlFiles.Count} xml, {htmFiles.Count} htm");

                // 1) Decrypt book.xml first (uses book UID as key)
                var bookXmlPath = Path.Combine(bookFolder, "book.xml");
                if (File.Exists(bookXmlPath))
                {
                    var content = await File.ReadAllTextAsync(bookXmlPath);
                    Log($"book.xml: {content.Length} bytes, encrypted={_decryptionService.IsEncrypted(content)}");

                    if (_decryptionService.IsEncrypted(content))
                    {
                        var decrypted = _decryptionService.DecryptBookFile(content, bookUid);
                        await File.WriteAllTextAsync(bookXmlPath, decrypted);
                        Log($"book.xml decrypted → {decrypted.Length} bytes");
                    }
                }

                // 2) Decrypt all XML/HTM (per-file key from filename)
                int xmlDecrypted = 0, xmlSkipped = 0;
                foreach (var file in xmlFiles.Concat(htmFiles))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file);
                        if (!_decryptionService.IsEncrypted(content))
                        {
                            xmlSkipped++;
                            continue;
                        }

                        var fileName = Path.GetFileName(file);
                        var decrypted = _decryptionService.DecryptXmlOrHtm(content, fileName);
                        await File.WriteAllTextAsync(file, decrypted);
                        xmlDecrypted++;
                        Log($"  ✓ {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  ✗ {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
                Log($"XML/HTM: {xmlDecrypted} decrypted, {xmlSkipped} skipped");

                // 3) Decrypt all HTML using book key
                int bookKey = _decryptionService.CalculateKey(bookUid);
                Log($"Book Key: {bookKey}");

                int htmlDecrypted = 0, htmlSkipped = 0;
                foreach (var file in htmlFiles)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file);
                        bool looksPlain = content.Contains("<") && content.Contains(">") &&
                                          (content.Contains("</") || content.Contains("/>")) &&
                                          (content.Contains("class=") || content.Contains("<div") || content.Contains("id="));

                        if (looksPlain)
                        {
                            htmlSkipped++;
                            continue;
                        }

                        var decrypted = _decryptionService.DecryptWithKey(content, bookKey);
                        await File.WriteAllTextAsync(file, decrypted);
                        htmlDecrypted++;
                        Log($"  ✓ {Path.GetFileName(file)}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  ✗ {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
                Log($"HTML: {htmlDecrypted} decrypted, {htmlSkipped} skipped");
            }
            catch (Exception ex)
            {
                Log($"DecryptBookFolderAsync error: {ex.Message}");
            }
        }

        private async Task ExtractZipAsync(string zipPath, string extractDir)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/"))
                        continue;

                    var entryName = entry.FullName.Replace('\\', '/');
                    var fullPath = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    entry.ExtractToFile(fullPath, true);
                }
                Log($"Extracted zip to: {extractDir}");
            }
            catch (Exception ex)
            {
                Log($"Error extracting zip: {ex.Message}");
                throw;
            }
        }

        private void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(source))
            {
                var destFile = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                var destSubDir = Path.Combine(dest, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private async Task DownloadAndExtractResourcePCAsync(int bookNumber, string parentDir, string extractDir)
        {
            try
            {
                var patterns = new[]
                {
                    $"{parentDir}/book_{bookNumber}_resource_pc.zip",
                    $"ebook_distribute/V3_otf/{bookNumber}/P02/book_{bookNumber}_resource_pc.zip",
                };

                foreach (var pattern in patterns)
                {
                    var url = $"{BaseUrl}/{pattern}";
                    var savePath = Path.Combine(Path.GetTempPath(), $"book_{bookNumber}_pc.zip");

                    if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                    {
                        try
                        {
                            using var zip = ZipFile.OpenRead(savePath);
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.EndsWith("/"))
                                    continue;

                                var entryName = entry.FullName.Replace('\\', '/');
                                var relative = entryName;
                                // Strip the zip's internal root prefix so we don't get resources/resources
                                if (relative.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
                                    relative = relative.Substring("resources/".Length);
                                else if (relative.StartsWith("resource/", StringComparison.OrdinalIgnoreCase))
                                    relative = relative.Substring("resource/".Length);

                                var sharedDir = GetSharedResourcesDir();
                                var fullPath = Path.Combine(sharedDir, relative.Replace('/', Path.DirectorySeparatorChar));
                                var directory = Path.GetDirectoryName(fullPath);
                                if (!string.IsNullOrEmpty(directory))
                                    Directory.CreateDirectory(directory);

                                if (!File.Exists(fullPath))
                                    entry.ExtractToFile(fullPath, true);
                            }
                            Log("Extracted resource PC zip to shared folder");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error extracting resource PC zip: {ex.Message}");
                        }

                        try { File.Delete(savePath); } catch { }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error downloading resource PC: {ex.Message}");
            }
        }

        private List<string> FindUnitUids(string extractDir)
        {
            var unitUids = new HashSet<string>();

            try
            {
                var unitsDir = Path.Combine(extractDir, "units");
                if (Directory.Exists(unitsDir))
                {
                    foreach (var file in Directory.GetFiles(unitsDir, "*.png"))
                    {
                        var match = Regex.Match(Path.GetFileNameWithoutExtension(file), @"unitUID_(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int num) && num >= 1000 && num <= 100000)
                            unitUids.Add(match.Groups[1].Value);
                    }
                }

                foreach (var file in Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".xml") || file.EndsWith(".json") || file.EndsWith(".txt"))
                    {
                        try
                        {
                            var content = File.ReadAllText(file);
                            var matches = Regex.Matches(content, @"unitUID[_:]?\s*[=:]?\s*[""']?(\d{4,6})[""']?");
                            foreach (Match match in matches)
                            {
                                if (int.TryParse(match.Groups[1].Value, out int num) && num >= 1000 && num <= 100000)
                                    unitUids.Add(match.Groups[1].Value);
                            }
                        }
                        catch { }
                    }
                }

                return unitUids.ToList();
            }
            catch (Exception ex)
            {
                Log($"Error finding unit UIDs: {ex.Message}");
                return new List<string>();
            }
        }

        private async Task DownloadAndExtractUnitsAsync(List<string> unitUids, string parentDir, int bookNumber, string extractDir)
        {
            try
            {
                int total = unitUids.Count;
                int current = 0;

                foreach (var uid in unitUids)
                {
                    current++;
                    int pct = 65 + (int)((current / (double)total) * 12);
                    OnProgress?.Invoke(this, pct);
                    Log($"[Unit {current}/{total}] Downloading unitUID_{uid}...");

                    await DownloadAndExtractUnitAsync(uid, parentDir, bookNumber, extractDir);
                }

                Log($"All {total} units processed");
            }
            catch (Exception ex)
            {
                Log($"Error downloading units: {ex.Message}");
            }
        }

        private async Task DownloadAndExtractUnitAsync(string uid, string parentDir, int bookNumber, string extractDir)
        {
            try
            {
                var patterns = new[]
                {
                    $"{parentDir}/unitUID_{uid}.zip",
                    $"{parentDir}/unit_{uid}.zip",
                };

                foreach (var pattern in patterns)
                {
                    var url = $"{BaseUrl}/{pattern}";
                    var savePath = Path.Combine(Path.GetTempPath(), $"unit_{uid}.zip");

                    if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                    {
                        try
                        {
                            using var zip = ZipFile.OpenRead(savePath);
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.EndsWith("/"))
                                    continue;

                                var entryName = entry.FullName.Replace('\\', '/');
                                var fullPath = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                                var directory = Path.GetDirectoryName(fullPath);
                                if (!string.IsNullOrEmpty(directory))
                                    Directory.CreateDirectory(directory);

                                if (!File.Exists(fullPath))
                                    entry.ExtractToFile(fullPath, true);
                            }
                            Log($"Extracted unit {uid}");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error extracting unit {uid}: {ex.Message}");
                        }

                        try { File.Delete(savePath); } catch { }
                        break;
                    }
                }

                await DownloadAndExtractUnitPCAsync(uid, parentDir, bookNumber, extractDir);
            }
            catch (Exception ex)
            {
                Log($"Error downloading unit {uid}: {ex.Message}");
            }
        }

        private async Task DownloadAndExtractUnitPCAsync(string uid, string parentDir, int bookNumber, string extractDir)
        {
            try
            {
                var patterns = new[]
                {
                    $"{parentDir}/unitUID_{uid}_resource_pc.zip",
                    $"{parentDir}/unitUID_{uid}_pc.zip",
                };

                foreach (var pattern in patterns)
                {
                    var url = $"{BaseUrl}/{pattern}";
                    var savePath = Path.Combine(Path.GetTempPath(), $"unit_{uid}_pc.zip");

                    if (await DownloadFileAsync(url, savePath) && await VerifyZipAsync(savePath))
                    {
                        try
                        {
                            using var zip = ZipFile.OpenRead(savePath);
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.EndsWith("/"))
                                    continue;

                                var entryName = entry.FullName.Replace('\\', '/');
                                var relative = entryName;
                                if (relative.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
                                    relative = relative.Substring("resources/".Length);
                                else if (relative.StartsWith("resource/", StringComparison.OrdinalIgnoreCase))
                                    relative = relative.Substring("resource/".Length);

                                var sharedDir = GetSharedResourcesDir();
                                var fullPath = Path.Combine(sharedDir, relative.Replace('/', Path.DirectorySeparatorChar));
                                var directory = Path.GetDirectoryName(fullPath);
                                if (!string.IsNullOrEmpty(directory))
                                    Directory.CreateDirectory(directory);

                                if (!File.Exists(fullPath))
                                    entry.ExtractToFile(fullPath, true);
                            }
                            Log($"Extracted unit PC {uid} to shared folder");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error extracting unit PC {uid}: {ex.Message}");
                        }

                        try { File.Delete(savePath); } catch { }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error downloading unit PC {uid}: {ex.Message}");
            }
        }

        private async Task CopyBookCoverAsync(int bookNumber, string bookDir)
        {
            try
            {
                string[] possibleCoverPaths = new[]
                {
                    Path.Combine(bookDir, $"{bookNumber}.png"),
                    Path.Combine(bookDir, $"book_{bookNumber}.png"),
                    Path.Combine(bookDir, "cover.png"),
                    Path.Combine(bookDir, "cover.jpg"),
                    Path.Combine(bookDir, "images", $"book_{bookNumber}.png"),
                    Path.Combine(bookDir, "images", $"{bookNumber}.png"),
                    Path.Combine(bookDir, "resource", $"book_{bookNumber}.png"),
                };

                string foundCover = null;
                foreach (var path in possibleCoverPaths)
                {
                    if (File.Exists(path))
                    {
                        foundCover = path;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(foundCover))
                {
                    string[] coverUrls = new[]
                    {
                        $"{BaseUrl}/ebook_distribute/V3_otf/{bookNumber}/P02/{bookNumber}.png",
                        $"{BaseUrl}/ebook_distribute/V3_otf/{bookNumber}/book_{bookNumber}.png",
                    };

                    string coverPath = Path.Combine(bookDir, $"{bookNumber}.png");

                    foreach (var coverUrl in coverUrls)
                    {
                        if (await DownloadFileAsync(coverUrl, coverPath))
                        {
                            foundCover = coverPath;
                            Log($"Downloaded cover image for book {bookNumber}");
                            break;
                        }
                    }
                }
                else
                {
                    string destPath = Path.Combine(bookDir, $"{bookNumber}.png");
                    if (foundCover != destPath)
                    {
                        File.Copy(foundCover, destPath, true);
                        foundCover = destPath;
                    }
                    Log($"Found cover image: {foundCover}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error copying book cover: {ex.Message}");
            }
        }

        private async Task<bool> DownloadFileAsync(string url, string savePath)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    return false;

                var content = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(savePath, content);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> VerifyZipAsync(string filePath)
        {
            try
            {
                var buffer = new byte[4];
                using var fs = File.OpenRead(filePath);
                await fs.ReadAsync(buffer, 0, 4);
                return buffer[0] == 0x50 && buffer[1] == 0x4B;
            }
            catch
            {
                return false;
            }
        }
    }
}
