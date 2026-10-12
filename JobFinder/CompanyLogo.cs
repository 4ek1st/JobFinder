using JobFinder.Core;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JobFinder;

public sealed class CompanyLogoService(string directory, bool networkEnabled = true) : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _gate = new(4);
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> _images = new();
    public Task<ImageSource?> LoadAsync(string url)
    {
        if (!DetailParser.IsLogoUrl(url)) return Task.FromResult<ImageSource?>(null);
        if (_images.Count >= 160) _images.Clear();
        return _images.GetOrAdd(url, value => new(() => LoadImageAsync(value))).Value;
    }
    internal void Seed(string url, byte[] image) => _images[url] = new(() => Task.FromResult(Decode(image)));
    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 128; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }
    private async Task<ImageSource?> LoadImageAsync(string url)
    {
        string file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img");
        const int limit = 2 * 1024 * 1024;
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length <= limit)
            {
                var cached = await Task.Run(() => Decode(File.ReadAllBytes(file)));
                if (cached is not null) return cached;
            }
            if (!networkEnabled) return null;
            HttpResponseMessage? response = null;
            try
            {
                for (int hop = 0; hop < 3; hop++)
                {
                    response?.Dispose();
                    response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if ((int)response.StatusCode is >= 300 and < 400)
                    {
                        if (!Uri.TryCreate(new Uri(url), response.Headers.Location, out var target) || !DetailParser.IsLogoUrl(target.AbsoluteUri)) return null;
                        url = target.AbsoluteUri; continue;
                    }
                    break;
                }
                if (response is null || !response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit) return null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
                while ((read = await input.ReadAsync(chunk, timeout.Token)) > 0) { if (buffer.Length + read > limit) return null; buffer.Write(chunk, 0, read); }
                var bytes = buffer.ToArray(); var image = await Task.Run(() => Decode(bytes));
                if (image is not null)
                {
                    // The bounded cache contains only source image bytes, never profile data.
                    try { Directory.CreateDirectory(directory); if (Directory.EnumerateFiles(directory, "*.img").Take(101).Count() <= 100) await File.WriteAllBytesAsync(file, bytes); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                return image;
            }
            finally { response?.Dispose(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        finally { _gate.Release(); }
    }
    public void Dispose() => _http.Dispose();
}

public sealed class CompanyLogo : Border
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(nameof(Url), typeof(string), typeof(CompanyLogo), new PropertyMetadata("", Update));
    public static readonly DependencyProperty CompanyProperty = DependencyProperty.Register(nameof(Company), typeof(string), typeof(CompanyLogo), new PropertyMetadata("", Update));
    public static readonly DependencyProperty LoaderProperty = DependencyProperty.Register(nameof(Loader), typeof(CompanyLogoService), typeof(CompanyLogo), new PropertyMetadata(null, Update));
    public string Url { get => (string)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public string Company { get => (string)GetValue(CompanyProperty); set => SetValue(CompanyProperty, value); }
    public CompanyLogoService? Loader { get => (CompanyLogoService?)GetValue(LoaderProperty); set => SetValue(LoaderProperty, value); }
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new(5), Visibility = Visibility.Collapsed };
    private readonly TextBlock _initials = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private int _generation;
    public bool HasImage => _image.Source is not null;
    public CompanyLogo()
    {
        Width = Height = 48; CornerRadius = new(10); ClipToBounds = true; VerticalAlignment = VerticalAlignment.Top;
        var grid = new Grid(); grid.Children.Add(_initials); grid.Children.Add(_image); Child = grid;
        _initials.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); SetResourceReference(BackgroundProperty, "Hover");
        Loaded += (_, _) => RefreshLogo(); Unloaded += (_, _) => ++_generation;
    }
    public static string Initials(string value)
    {
        var words = value.Split([' ', '.', '-', '&'], StringSplitOptions.RemoveEmptyEntries).Where(x => x.Any(char.IsLetterOrDigit)).ToArray();
        return words.Length == 0 ? "J" : string.Concat(words.Take(2).Select(x => x.First(char.IsLetterOrDigit))).ToUpperInvariant();
    }
    private static void Update(DependencyObject owner, DependencyPropertyChangedEventArgs _) => ((CompanyLogo)owner).RefreshLogo();
    private async void RefreshLogo()
    {
        int generation = ++_generation; _initials.Text = Initials(Company ?? "");
        _image.Source = null; _image.Visibility = Visibility.Collapsed; _initials.Visibility = Visibility.Visible;
        SetResourceReference(BackgroundProperty, "Hover");
        if (!IsLoaded || Loader is null || string.IsNullOrEmpty(Url)) return;
        var image = await Loader.LoadAsync(Url);
        if (generation != _generation || !IsLoaded || image is null) return;
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        _image.Source = image; _image.Visibility = Visibility.Visible; _initials.Visibility = Visibility.Collapsed;
    }
}
