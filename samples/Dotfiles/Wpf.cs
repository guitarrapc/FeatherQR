#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
// A file-based app turns PublishAot on, and WPF does not build with trimming.
#:property PublishAot=false
#:project ../../src/FeatherQR/FeatherQR.csproj
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FeatherQR;
using ComTypes = System.Runtime.InteropServices.ComTypes;

// WPF with the FeatherQR package: a window that makes a QR code as you type, and reads every symbol in an opened, pasted or dropped image.
// Run from the repository root on Windows: dotnet run samples/Dotfiles/Wpf.cs
// Add -- --no-window to run only the checks, as CI does.

const string url = "https://github.com/guitarrapc/FeatherQR";
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "wpf");
Directory.CreateDirectory(outputDirectory);
var failures = 0;

var qr = QRCodeGenerator.Create(url, QREccLevel.M);

// A symbol as a BitmapSource, opaque and transparent.
var bitmap = qr.ToBitmapSource(new SymbolImageOptions { ModulePixelSize = 8 });
Check("BitmapSource", bitmap);
SavePng(bitmap, Path.Combine(outputDirectory, "qr.png"));

var navyOnClear = new SymbolImageOptions { ModulePixelSize = 8, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A), LightColor = new SymbolColor(0, 0, 0, 0) };
var transparent = qr.ToBitmapSource(navyOnClear);
Check("BitmapSource, transparent", transparent);
SavePng(transparent, Path.Combine(outputDirectory, "qr-transparent.png"));

// A symbol as a DrawingImage, a vector image that renders at any size.
var drawing = qr.ToDrawingImage(Brushes.Black, Brushes.White);
foreach (var size in new[] { 250, 400 })
{
    var rendered = Render(drawing, size);
    Check($"DrawingImage at {size} px", rendered);
    SavePng(rendered, Path.Combine(outputDirectory, $"qr-vector-{size}.png"));
}

if (!args.Contains("--no-window"))
{
    // Controls such as Window and Image throw on a thread that is not in a single-threaded apartment,
    // and top-level statements cannot be marked [STAThread]. The window gets a thread of its own.
    var windowThread = new Thread(ShowWindow);
    windowThread.SetApartmentState(ApartmentState.STA);
    windowThread.Start();
    windowThread.Join();
}

Console.WriteLine($"Images written to {outputDirectory}");
return failures == 0 ? 0 : 1;

void Check(string label, BitmapSource source)
{
    var decoded = source.TryDecode(out var text) && text == url;
    Console.WriteLine($"{label,-28} {source.PixelWidth} x {source.PixelHeight} {(decoded ? "decodes" : "DOES NOT DECODE")}");
    if (!decoded)
        failures++;
}

void ShowWindow()
{
    // The XAML of a MainWindow.xaml, loaded at run time because a file-based app does not compile XAML.
    // The left half makes a QR code from the text typed. The right half reads every symbol in an image and outlines each one.
    var window = (Window)XamlReader.Parse("""
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="FeatherQR with WPF" Width="1000" Height="640" AllowDrop="True">
          <Window.Resources>
            <BooleanToVisibilityConverter x:Key="Visible" />
            <Style TargetType="Button">
              <Setter Property="Padding" Value="12,4" />
              <Setter Property="Margin" Value="0,0,8,0" />
            </Style>
            <Style x:Key="Heading" TargetType="TextBlock">
              <Setter Property="FontSize" Value="18" />
              <Setter Property="FontWeight" Value="SemiBold" />
              <Setter Property="Margin" Value="0,0,0,4" />
            </Style>
            <Style x:Key="Note" TargetType="TextBlock">
              <Setter Property="Foreground" Value="#5A5A5A" />
              <Setter Property="TextWrapping" Value="Wrap" />
            </Style>
          </Window.Resources>
          <Border Padding="16" Background="White">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition />
                <ColumnDefinition Width="32" />
                <ColumnDefinition />
              </Grid.ColumnDefinitions>

              <DockPanel>
                <TextBlock DockPanel.Dock="Top" Style="{StaticResource Heading}" Text="Make a QR code" />
                <TextBlock DockPanel.Dock="Top" Style="{StaticResource Note}" Text="Type text. The QR code changes as you type." />
                <TextBox DockPanel.Dock="Top" Margin="0,8,0,0" Padding="4" Text="{Binding Text, UpdateSourceTrigger=PropertyChanged}" />
                <TextBlock DockPanel.Dock="Top" Foreground="Firebrick" Text="{Binding Error}" />
                <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                  <Button x:Name="CopyButton" Content="Copy image" IsEnabled="{Binding CanExport}" />
                  <Button x:Name="SaveButton" Content="Save PNG…" IsEnabled="{Binding CanExport}" />
                  <TextBlock VerticalAlignment="Center" Style="{StaticResource Note}" Text="{Binding ExportStatus}" />
                </StackPanel>
                <Image Margin="0,8,0,0" Source="{Binding Symbol}" />
              </DockPanel>

              <DockPanel Grid.Column="2">
                <TextBlock DockPanel.Dock="Top" Style="{StaticResource Heading}" Text="Read QR codes" />
                <TextBlock DockPanel.Dock="Top" Style="{StaticResource Note}" Text="Open, paste or drop an image. Each symbol found is outlined and numbered." />
                <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,8,0,0">
                  <Button x:Name="OpenButton" Content="Open image…" />
                  <Button x:Name="PasteButton" Content="Paste" />
                </StackPanel>
                <TextBox DockPanel.Dock="Bottom" MaxHeight="110" IsReadOnly="True" BorderThickness="0" TextWrapping="Wrap"
                         VerticalScrollBarVisibility="Auto" Text="{Binding ReadResult, Mode=OneWay}" />
                <Grid Margin="0,8">
                  <Rectangle Stroke="#9A9A9A" StrokeThickness="2" StrokeDashArray="4 3" RadiusX="8" RadiusY="8" Fill="#F6F6F6" />
                  <TextBlock HorizontalAlignment="Center" VerticalAlignment="Center" TextAlignment="Center" FontSize="16" Foreground="#5A5A5A"
                             Visibility="{Binding ShowHint, Converter={StaticResource Visible}}" Text="Drop an image here&#10;or press Ctrl+V" />
                  <!-- The image and its outlines share one Grid the size of the image in pixels, so the corners the decoder reports
                       land on the image as the Viewbox scales both. -->
                  <Viewbox Margin="8" Visibility="{Binding HasSource, Converter={StaticResource Visible}}">
                    <Grid Width="{Binding SourceWidth}" Height="{Binding SourceHeight}">
                      <Image Source="{Binding Source}" Stretch="Fill" />
                      <ItemsControl ItemsSource="{Binding Markers}">
                        <ItemsControl.ItemsPanel>
                          <ItemsPanelTemplate>
                            <Canvas />
                          </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemTemplate>
                          <DataTemplate>
                            <Canvas>
                              <Polygon Points="{Binding Outline}" StrokeThickness="{Binding Thickness}" Stroke="#0078D4" Fill="#330078D4" />
                              <Border Canvas.Left="{Binding X}" Canvas.Top="{Binding Y}" Padding="{Binding Padding}" Background="#0078D4">
                                <TextBlock Foreground="White" FontWeight="Bold" FontSize="{Binding FontSize}" Text="{Binding Number}" />
                              </Border>
                            </Canvas>
                          </DataTemplate>
                        </ItemsControl.ItemTemplate>
                      </ItemsControl>
                    </Grid>
                  </Viewbox>
                </Grid>
              </DockPanel>
            </Grid>
          </Border>
        </Window>
        """);
    var model = new SymbolViewModel { Text = url };
    window.DataContext = model;

    // XAML loaded at run time cannot name event handlers, so they are attached here, as a code-behind file would.
    Button Find(string name) => (Button)window.FindName(name);
    Find("CopyButton").Click += (_, _) => model.CopyImage();
    Find("SaveButton").Click += (_, _) =>
    {
        var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "qr.png" };
        if (dialog.ShowDialog(window) == true)
            model.SavePng(dialog.FileName);
    };
    Find("OpenButton").Click += (_, _) =>
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*" };
        if (dialog.ShowDialog(window) == true)
            model.ReadFile(dialog.FileName);
    };
    Find("PasteButton").Click += (_, _) => model.ReadClipboard(reportEmpty: true);

    // Ctrl+V reads an image or an image file on the clipboard. Text on the clipboard pastes into the text box as usual.
    window.PreviewKeyDown += (_, e) =>
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && model.ReadClipboard(reportEmpty: false))
            e.Handled = true;
    };
    // A drop reads an image file, or an image dragged out of a browser.
    window.PreviewDragOver += (_, e) =>
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(VirtualFile.Descriptor))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    };
    window.PreviewDrop += (_, e) =>
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            model.ReadFile(files[0]);
        else if (VirtualFile.ReadFirst(e.Data) is { } bytes)
            model.ReadBytes(bytes, "Dragged image");
        else
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    };

    // What the window shows decodes too.
    window.ContentRendered += (_, _) =>
    {
        var shown = Capture((FrameworkElement)window.Content);
        Check("Window", shown);
        SavePng(shown, Path.Combine(outputDirectory, "window.png"));
    };
    new Application().Run(window);
}

static BitmapSource Render(DrawingImage image, int pixels)
{
    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
        context.DrawImage(image, new Rect(0, 0, pixels, pixels));
    var target = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
    target.Render(visual);
    target.Freeze();
    return target;
}

static BitmapSource Capture(FrameworkElement element)
{
    var target = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
    target.Render(element);
    target.Freeze();
    return target;
}

static void SavePng(BitmapSource source, string path)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(source));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

// Conversions between FeatherQR symbols and WPF images.
static class WpfSymbolExtensions
{
    public static BitmapSource ToBitmapSource(this QRCodeData data, SymbolImageOptions options)
    {
        // PixelFormats.Bgra32 is PixelLayout.Bgra8888: blue, green, red, alpha, not premultiplied.
        var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(data, PixelLayout.Bgra8888, options)];
        SymbolImageWriter.TryWritePixels(data, pixels, PixelLayout.Bgra8888, out var width, out var height, options);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static DrawingImage ToDrawingImage(this QRCodeData data, Brush dark, Brush light)
    {
        // All dark modules go in one geometry. Filled one rectangle at a time, anti-aliasing leaves light seams
        // where two rectangles meet inside a pixel, which happens when a module is not a whole number of pixels.
        // One unit is one module, with the quiet zone inside 0 to Size.
        var modules = new StreamGeometry();
        using (var context = modules.Open())
        {
            foreach (var rect in data.GetModuleRectangles())
            {
                context.BeginFigure(new Point(rect.X, rect.Y), isFilled: true, isClosed: true);
                context.LineTo(new Point(rect.X + rect.Width, rect.Y), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(rect.X + rect.Width, rect.Y + rect.Height), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(rect.X, rect.Y + rect.Height), isStroked: false, isSmoothJoin: false);
            }
        }
        modules.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, data.Size, data.Size))));
        group.Children.Add(new GeometryDrawing(dark, null, modules));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    // The first QR code in the image.
    public static bool TryDecode(this BitmapSource source, out string text)
    {
        var luminance = source.ToLuminance(out var width, out var height);
        return QRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out _);
    }

    // Every QR code, Micro QR code and rMQR code in the image.
    public static SymbolDecodeResult[] DecodeSymbols(this BitmapSource source)
    {
        var luminance = source.ToLuminance(out var width, out var height);
        return SymbolDecoder.DecodeImageSymbols(luminance, width, height);
    }

    static byte[] ToLuminance(this BitmapSource source, out int width, out int height)
    {
        // FormatConvertedBitmap turns the source's pixel format into straight BGRA. A conversion to Gray8 would drop alpha
        // and turn a transparent background black. BGRA keeps alpha, and LuminanceConverter puts transparent pixels on white.
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        (width, height) = (bgra.PixelWidth, bgra.PixelHeight);
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        var luminance = new byte[width * height];
        LuminanceConverter.Convert(pixels, width, height, width * 4, PixelLayout.Bgra8888, premultipliedAlpha: false, luminance);
        return luminance;
    }
}

// The window binds to this, as it would to a MainWindow's view model.
sealed class SymbolViewModel : INotifyPropertyChanged
{
    string text = "";
    QRCodeData? data;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Make a QR code: each change to Text writes a new symbol.
    public string Text
    {
        get => text;
        set
        {
            text = value;
            try
            {
                data = QRCodeGenerator.Create(value, QREccLevel.M);
                Symbol = data.ToDrawingImage(Brushes.Black, Brushes.White);
                Error = "";
            }
            catch (InvalidOperationException)
            {
                // Thrown when the text does not fit the largest QR code.
                data = null;
                Symbol = null;
                Error = "Too long for a QR code.";
            }
            ExportStatus = "";
            Changed(nameof(Text), nameof(Symbol), nameof(Error), nameof(CanExport), nameof(ExportStatus));
        }
    }

    public DrawingImage? Symbol { get; private set; }
    public string Error { get; private set; } = "";
    public bool CanExport => data is not null;
    public string ExportStatus { get; private set; } = "";

    public void CopyImage()
    {
        if (data is null)
            return;
        Clipboard.SetImage(data.ToBitmapSource(new SymbolImageOptions { ModulePixelSize = 8 }));
        SetExportStatus("Copied.");
    }

    public void SavePng(string path)
    {
        if (data is null)
            return;
        try
        {
            File.WriteAllBytes(path, SymbolImageWriter.GetPngBytes(data, new SymbolImageOptions { ModulePixelSize = 8 }));
            SetExportStatus($"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetExportStatus($"Cannot save {Path.GetFileName(path)}.");
        }
    }

    // Read QR codes: the image last read, an outline for each symbol in it, and a line for each.
    public BitmapSource? Source { get; private set; }
    public int SourceWidth => Source?.PixelWidth ?? 0;
    public int SourceHeight => Source?.PixelHeight ?? 0;
    public bool HasSource => Source is not null;
    public bool ShowHint => Source is null;
    public IReadOnlyList<Marker> Markers { get; private set; } = [];
    public string ReadResult { get; private set; } = "";

    public bool ReadClipboard(bool reportEmpty)
    {
        // ContainsImage reports a bitmap on the clipboard, but not a PNG on its own.
        if (Clipboard.GetData("PNG") is MemoryStream png)
            Read(BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), "Pasted image");
        else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            Read(image, "Pasted image");
        else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList()[0] is { } file)
            ReadFile(file);
        else
        {
            if (reportEmpty)
                Show(null, [], "The clipboard holds no image.");
            return false;
        }
        return true;
    }

    public void ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Read(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException)
        {
            Show(null, [], $"{Path.GetFileName(path)}: cannot be read as an image.");
        }
    }

    public void ReadBytes(byte[] bytes, string name)
    {
        try
        {
            Read(BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), name);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            Show(null, [], $"{name}: cannot be read as an image.");
        }
    }

    void Read(BitmapSource image, string name)
    {
        var symbols = image.DecodeSymbols();
        var lines = new List<string> { $"{name}, {image.PixelWidth} × {image.PixelHeight}: {Count(symbols.Length)}" };
        var markers = new List<Marker>();
        for (var i = 0; i < symbols.Length; i++)
        {
            lines.Add($"{i + 1}. {Name(symbols[i].Symbology)}: {symbols[i].Text}");
            markers.Add(Marker.Create(i + 1, symbols[i].Corners, Math.Max(image.PixelWidth, image.PixelHeight)));
        }
        Show(image, markers, string.Join(Environment.NewLine, lines));
    }

    static string Count(int symbols) => symbols switch
    {
        0 => "no QR code found",
        1 => "1 symbol",
        _ => $"{symbols} symbols",
    };

    static string Name(Symbology symbology) => symbology switch
    {
        Symbology.MicroQR => "Micro QR",
        Symbology.RmQR => "rMQR",
        _ => "QR",
    };

    void Show(BitmapSource? source, IReadOnlyList<Marker> markers, string result)
    {
        Source = source;
        Markers = markers;
        ReadResult = result;
        Changed(nameof(Source), nameof(SourceWidth), nameof(SourceHeight), nameof(HasSource), nameof(ShowHint), nameof(Markers), nameof(ReadResult));
    }

    void SetExportStatus(string value)
    {
        ExportStatus = value;
        Changed(nameof(ExportStatus));
    }

    void Changed(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// An outline around one symbol and its number at the symbol's top-left corner, in the image's pixels.
// Line and number sizes follow the image size, so they keep their look when the window scales the image.
sealed record Marker(string Number, PointCollection Outline, double X, double Y, double Thickness, double FontSize, double Padding)
{
    public static Marker Create(int number, SymbolCorners corners, int imageSize)
    {
        var outline = new PointCollection
        {
            new Point(corners.TopLeft.X, corners.TopLeft.Y),
            new Point(corners.TopRight.X, corners.TopRight.Y),
            new Point(corners.BottomRight.X, corners.BottomRight.Y),
            new Point(corners.BottomLeft.X, corners.BottomLeft.Y),
        };
        outline.Freeze();
        var fontSize = Math.Max(12, imageSize / 24.0);
        return new Marker(number.ToString(), outline, corners.TopLeft.X, corners.TopLeft.Y, Math.Max(2, imageSize / 200.0), fontSize, fontSize / 4);
    }
}

// Edge and Chrome drag an image as a virtual file: a name and its bytes, with no path on disk.
// WPF's GetData("FileContents") returns null for it. Asking through COM for the first file returns the bytes.
static class VirtualFile
{
    public const string Descriptor = "FileGroupDescriptorW";

    public static byte[]? ReadFirst(IDataObject data)
    {
        if (!data.GetDataPresent(Descriptor) || data is not ComTypes.IDataObject com)
            return null;
        var format = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat("FileContents").Id),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = 0,
            // Both browsers hand the bytes over as a stream.
            tymed = ComTypes.TYMED.TYMED_ISTREAM,
        };
        ComTypes.STGMEDIUM medium;
        try
        {
            com.GetData(ref format, out medium);
        }
        catch (COMException)
        {
            return null;
        }
        try
        {
            var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            stream.Stat(out var stat, 1); // STATFLAG_NONAME: the name is not needed.
            var bytes = new byte[stat.cbSize];
            stream.Read(bytes, bytes.Length, IntPtr.Zero);
            return bytes;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
}
