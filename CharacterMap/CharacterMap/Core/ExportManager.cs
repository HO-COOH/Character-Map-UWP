﻿using CharacterMap.Helpers;
using CharacterMap.Services;
using CharacterMapCX;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using CharacterMap.Models;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Toolkit.Mvvm.DependencyInjection;
using Microsoft.Toolkit.Mvvm.Messaging;
using System.Threading;

namespace CharacterMap.Core
{
    public enum ExportStyle
    {
        Black,
        White,
        ColorGlyph
    }

    public class ExportResult
    {
        public StorageFile File { get; }
        public bool Success { get; }

        public ExportResult(bool success, StorageFile file)
        {
            Success = success;
            File = file;
        }
    }

    public class ExportFontFileResult
    {
        public StorageFolder Folder { get; }
        public StorageFile File { get; }
        public bool Success { get; }

        public ExportFontFileResult(bool success, StorageFile file)
        {
            Success = success;
            File = file;
        }

        public ExportFontFileResult(StorageFolder folder, bool success)
        {
            Success = success;
            Folder = folder;
        }

        public string GetMessage()
        {
            if (Folder != null)
                return Localization.Get("ExportedToFolderMessage", Folder.Name);
            else
                return Localization.Get("FontExportedMessage", File.Name);
        }
    }

    public static class ExportManager
    {
        public static string GetSVG(
            ExportStyle style,
            CharacterRenderingOptions options,
            Character selectedChar)
        {
            // We want to prepare geometry at 1024px, so force this
            options = options with { FontSize = 1024 };
            using var typography = options.CreateCanvasTypography();

            CanvasDevice device = Utils.CanvasDevice;
            Color textColor = style == ExportStyle.Black ? Colors.Black : Colors.White;

            // If COLR format (e.g. Segoe UI Emoji), we have special export path.
            if (style == ExportStyle.ColorGlyph && options.Analysis.HasColorGlyphs && !options.Analysis.GlyphFormats.Contains(GlyphImageFormat.Svg))
            {
                NativeInterop interop = Utils.GetInterop();
                List<string> paths = new List<string>();
                Rect bounds = Rect.Empty;

                // Try to find the bounding box of all glyph layers combined
                foreach (var thing in options.Analysis.Indicies)
                {
                    var path = interop.GetPathDatas(options.Variant.FontFace, thing.ToArray()).First();
                    paths.Add(path.Path);

                    if (!path.Bounds.IsEmpty)
                    {
                        var left = Math.Min(bounds.Left, path.Bounds.Left);
                        var top = Math.Min(bounds.Top, path.Bounds.Top);
                        var right = Math.Max(bounds.Right, path.Bounds.Right);
                        var bottom = Math.Max(bounds.Bottom, path.Bounds.Bottom);
                        bounds = new Rect(
                            left,
                            top,
                            right - left,
                            bottom - top);
                    }
                }

                using CanvasSvgDocument document = Utils.GenerateSvgDocument(device, bounds, paths, options.Analysis.Colors, invertBounds: false);
                return document.GetXml();
            }

            var data = GetGeometry(selectedChar, options);
            string GetMonochrome()
            {
                using CanvasSvgDocument document = string.IsNullOrWhiteSpace(data.Path) 
                    ? new CanvasSvgDocument(Utils.CanvasDevice)
                    : Utils.GenerateSvgDocument(device, data.Bounds, data.Path, textColor);
                return document.GetXml();
            }

            // If the font uses SVG glyphs, we can extract the raw SVG from the font file
            if (options.Analysis.GlyphFormats.Contains(GlyphImageFormat.Svg))
            {
                string str = null;
                IBuffer b = GetGlyphBuffer(options.Variant.FontFace, selectedChar.UnicodeIndex, GlyphImageFormat.Svg);
                if (b.Length > 2 && b.GetByte(0) == 31 && b.GetByte(1) == 139)
                {
                    using var stream = b.AsStream();
                    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip);
                    str = reader.ReadToEnd();
                }
                else
                {
                    using var dataReader = DataReader.FromBuffer(b);
                    dataReader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    str = dataReader.ReadString(b.Length);
                }

                if (str.StartsWith("<?xml"))
                    str = str.Remove(0, str.IndexOf(">") + 1);

                str = str.TrimStart();

                try
                {
                    using (CanvasSvgDocument document = CanvasSvgDocument.LoadFromXml(Utils.CanvasDevice, str))
                    {
                        // We need to transform the SVG to fit within the default document bounds, as characters
                        // are based *above* the base origin of (0,0) as (0,0) is the Baseline (bottom left) position for a character, 
                        // so by default a will appear out of bounds of the default SVG viewport (towards top left).

                        //if (!document.Root.IsAttributeSpecified("viewBox")) // Specified viewbox requires baseline transform?
                        {
                            // We'll regroup all the elements inside a "g" / group tag,
                            // and apply a transform to the "g" tag to try and put in 
                            // in the correct place. There's probably a more accurate way
                            // to do this by directly setting the root viewBox, if anyone
                            // can find the correct calculation...

                            List<ICanvasSvgElement> elements = new List<ICanvasSvgElement>();

                            double minTop = 0;
                            double minLeft = double.MaxValue;
                            double maxWidth = double.MinValue;
                            double maxHeight = double.MinValue;

                            void ProcessChildren(CanvasSvgNamedElement root)
                            {
                                CanvasSvgNamedElement ele = root.FirstChild as CanvasSvgNamedElement;
                                while (true)
                                {
                                    CanvasSvgNamedElement next = root.GetNextSibling(ele) as CanvasSvgNamedElement;
                                    if (ele.Tag == "g")
                                    {
                                        ProcessChildren(ele);
                                    }
                                    else if (ele.Tag == "path")
                                    {
                                        // Create a XAML geometry to try and find the bounds of each character
                                        // Probably more efficient to do in Win2D, but far less code to do with XAML.
                                        Geometry gm = XamlBindingHelper.ConvertValue(typeof(Geometry), ele.GetStringAttribute("d")) as Geometry;
                                        minTop = Math.Min(minTop, gm.Bounds.Top);
                                        minLeft = Math.Min(minLeft, gm.Bounds.Left);
                                        maxWidth = Math.Max(maxWidth, gm.Bounds.Width);
                                        maxHeight = Math.Max(maxHeight, gm.Bounds.Height);
                                    }
                                    ele = next;
                                    if (ele == null)
                                        break;
                                }
                            }

                            ProcessChildren(document.Root);

                            double top = minTop < 0 ? minTop : 0;
                            double left = minLeft;
                            document.Root.SetRectangleAttribute("viewBox", new Rect(left, top, data.Bounds.Width, data.Bounds.Height));
                        }

                        return document.GetXml();
                    }
                }
                catch
                {
                    // Certain fonts seem to have their SVG glyphs encoded with... I don't even know what encoding.
                    // for example: https://github.com/adobe-fonts/emojione-color
                    // In these cases, fallback to monochrome black
                    return GetMonochrome();
                }
            }
            else
            {
                return GetMonochrome();
            }
        }

        public static async Task<ExportResult> ExportSvgAsync(
            ExportStyle style,
            InstalledFont selectedFont,
            CharacterRenderingOptions options,
            Character selectedChar)
        {
            try
            {
                // We want to prepare geometry at 1024px, so force this
                options = options with { FontSize = 1024 };
                using var typography = options.CreateCanvasTypography();

                // Get save file
                string name = GetFileName(selectedFont, options.Variant, selectedChar, "svg");
                if (await PickFileAsync(name, "SVG", new[] { ".svg" }) is StorageFile file)
                {
                    try
                    {
                        CachedFileManager.DeferUpdates(file);
                        // Generate SVG doc and write to file
                        await Utils.WriteSvgAsync(GetSVG(style, options, selectedChar), file);
                        return new ExportResult(true, file);
                    }
                    finally
                    {
                        await CachedFileManager.CompleteUpdatesAsync(file);
                    }
                }
            }
            catch (Exception ex)
            {
                await Ioc.Default.GetService<IDialogService>()
                    .ShowMessageAsync(ex.Message, Localization.Get("SaveImageError"));
            }

            return new ExportResult(false, null);
        }

        public static async Task<ExportResult> ExportPngAsync(
            ExportStyle style,
            InstalledFont selectedFont,
            CharacterRenderingOptions options,
            Character selectedChar,
            AppSettings settings)
        {
            try
            {
                using var typography = options.CreateCanvasTypography();

                string name = GetFileName(selectedFont, options.Variant, selectedChar, "png");
                if (await PickFileAsync(name, "PNG Image", new[] { ".png" }) is StorageFile file)
                {
                    CachedFileManager.DeferUpdates(file);

                    if (options.Analysis.GlyphFormats.Contains(GlyphImageFormat.Png))
                    {
                        IBuffer buffer = GetGlyphBuffer(options.Variant.FontFace, selectedChar.UnicodeIndex, GlyphImageFormat.Png);
                        await FileIO.WriteBufferAsync(file, buffer);
                    }
                    else
                    {
                        var device = Utils.CanvasDevice;
                        var localDpi = 96; //Windows.Graphics.Display.DisplayInformation.GetForCurrentView().LogicalDpi;

                        var canvasH = (float)settings.PngSize;
                        var canvasW = (float)settings.PngSize;

                        using var renderTarget = new CanvasRenderTarget(device, canvasW, canvasH, localDpi);
                        using (var ds = renderTarget.CreateDrawingSession())
                        {
                            ds.Clear(Colors.Transparent);
                            var d = settings.PngSize;
                            var r = settings.PngSize / 2;

                            var textColor = style == ExportStyle.Black ? Colors.Black : Colors.White;
                            var fontSize = (float)d;

                            using CanvasTextLayout layout = new CanvasTextLayout(device, $"{selectedChar.Char}", new CanvasTextFormat
                            {
                                FontSize = fontSize,
                                FontFamily = options.Variant.Source,
                                FontStretch = options.Variant.FontFace.Stretch,
                                FontWeight = options.Variant.FontFace.Weight,
                                FontStyle = options.Variant.FontFace.Style,
                                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                                Options = style == ExportStyle.ColorGlyph ? CanvasDrawTextOptions.EnableColorFont : CanvasDrawTextOptions.Default
                            }, canvasW, canvasH);

                            if (style == ExportStyle.ColorGlyph)
                                layout.Options = CanvasDrawTextOptions.EnableColorFont;

                            layout.SetTypography(0, 1, typography);

                            var db = layout.DrawBounds;
                            double scale = Math.Min(1, Math.Min(canvasW / db.Width, canvasH / db.Height));
                            var x = -db.Left + ((canvasW - (db.Width * scale)) / 2d);
                            var y = -db.Top + ((canvasH - (db.Height * scale)) / 2d);

                            ds.Transform =
                                Matrix3x2.CreateTranslation(new Vector2((float)x, (float)y))
                                * Matrix3x2.CreateScale(new Vector2((float)scale));

                            ds.DrawTextLayout(layout, new Vector2(0), textColor);
                        }

                        using var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite);
                        fileStream.Size = 0;
                        await renderTarget.SaveAsync(fileStream, CanvasBitmapFileFormat.Png, 1f);
                    }

                    await CachedFileManager.CompleteUpdatesAsync(file);
                    return new ExportResult(true, file);
                }
            }
            catch (Exception ex)
            {
                await Ioc.Default.GetService<IDialogService>()
                    .ShowMessageAsync(ex.Message, Localization.Get("SaveImageError"));
            }

            return new ExportResult(false, null);
        }

        private static IBuffer GetGlyphBuffer(CanvasFontFace fontface, uint unicodeIndex, GlyphImageFormat format)
        {
            return DirectWrite.GetImageDataBuffer(fontface, 1024, unicodeIndex, format);
        }

        private static string GetFileName(
            InstalledFont selectedFont,
            FontVariant selectedVariant,
            Character selectedChar,
            string ext)
        {
            var chr = selectedVariant.GetDescription(selectedChar) ?? selectedChar.UnicodeString;
            return $"{selectedFont.Name} {selectedVariant.PreferredName} - {chr}.{ext}";
        }

        private static async Task<StorageFile> PickFileAsync(string fileName, string key, IList<string> values, PickerLocationId suggestedLocation = PickerLocationId.PicturesLibrary)
        {
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = suggestedLocation
            };

            savePicker.FileTypeChoices.Add(key, values);
            savePicker.SuggestedFileName = fileName;

            try
            {
                return await savePicker.PickSaveFileAsync();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static (string Path, Rect Bounds) GetGeometry(
            Character selectedChar,
            CharacterRenderingOptions options)
        {
            /* 
             * Note: this only constructs the monochrome version
             * of the glyph.
             * 
             * Drop into C++/CX for color / multi-variant glyphs.
             */

            using CanvasGeometry geom = CreateGeometry(selectedChar, options);
            var bounds = geom.ComputeBounds();
            var interop = Utils.GetInterop();
            var s = interop.GetPathData(geom);

            if (string.IsNullOrWhiteSpace(s.Path))
                return (s.Path, bounds);

            var t = s.Transform.Translation;
            bounds = new Rect(t.X - bounds.Left, -bounds.Top + t.Y, bounds.Width, bounds.Height);
            return (s.Path, bounds);
        }

        public static CanvasGeometry CreateGeometry(
           Character selectedChar,
           CharacterRenderingOptions options)
        {
            CanvasDevice device = Utils.CanvasDevice;

            /* SVG Exports render at fixed size - but a) they're vectors, and b) they're
             * inside an auto-scaling viewport. So render-size is *largely* pointless */
            float canvasH = options.FontSize, canvasW = options.FontSize, fontSize = options.FontSize;

            using var typography = options.CreateCanvasTypography();
            using (CanvasTextLayout layout = new CanvasTextLayout(device, $"{selectedChar.Char}", new CanvasTextFormat
            {
                FontSize = fontSize,
                FontFamily = options.Variant.Source,
                FontStretch = options.Variant.FontFace.Stretch,
                FontWeight = options.Variant.FontFace.Weight,
                FontStyle = options.Variant.FontFace.Style,
                HorizontalAlignment = CanvasHorizontalAlignment.Center
            }, canvasW, canvasH))
            {
                layout.SetTypography(0, 1, typography);
                layout.Options = options.Analysis.GlyphFormats.Contains(GlyphImageFormat.Svg) ? CanvasDrawTextOptions.EnableColorFont : CanvasDrawTextOptions.Default;

                return CanvasGeometry.CreateText(layout);
            }
        }

        public static async void RequestExportFontFile(FontVariant variant)
        {
            var scheme = ResourceHelper.AppSettings.ExportNamingScheme;

            if (DirectWrite.IsFontLocal(variant.FontFace))
            {
                string filePath = GetFileName(variant, scheme);
                string name = Path.GetFileNameWithoutExtension(filePath);
                string ext = Path.GetExtension(filePath);

                if (await PickFileAsync(name, Localization.Get("ExportFontFile/Text"), new[] { ext }, PickerLocationId.DocumentsLibrary) is StorageFile file)
                {
                    try
                    {
                        bool success = await TryWriteToFileAsync(variant, file);
                        WeakReferenceMessenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(success, file)));
                        return;
                    }
                    catch
                    {
                    }
                }
            }

            WeakReferenceMessenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(null, false)));
        }

        internal static Task ExportCollectionAsZipAsync(List<InstalledFont> fontList, UserFontCollection selectedCollection)
        {
            var fonts = fontList.SelectMany(f => f.Variants).ToList();
            return ExportFontsAsZipAsync(fonts, selectedCollection.Name);
        }

        internal static async Task ExportFontsAsZipAsync(List<FontVariant> fonts, string name)
        {
            if (await PickFileAsync(name, "ZIP", new[] { ".zip" }) is StorageFile file)
            {
                await Task.Run(async () =>
                {
                    ExportNamingScheme scheme = ResourceHelper.AppSettings.ExportNamingScheme;

                    using var i = await file.OpenStreamForWriteAsync();
                    i.SetLength(0);

                    using ZipArchive z = new ZipArchive(i, ZipArchiveMode.Create);
                    foreach (var font in fonts)
                    {
                        if (DirectWrite.IsFontLocal(font.FontFace))
                        {
                            string fileName = GetFileName(font, scheme);
                            ZipArchiveEntry entry = z.CreateEntry(fileName);
                            using IOutputStream s = entry.Open().AsOutputStream();
                            await DirectWrite.WriteToStreamAsync(font.FontFace, s);
                        }
                    }
                });

                WeakReferenceMessenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(true, file)));
            }
        }

        internal static Task ExportCollectionToFolderAsync(List<InstalledFont> fontList)
        {
            var fonts = fontList.SelectMany(f => f.Variants).ToList();
            return ExportFontsToFolderAsync(fonts);
        }

        internal static async Task ExportFontsToFolderAsync(List<FontVariant> fonts)
        {
            FolderPicker picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");


            if (await picker.PickSingleFolderAsync() is StorageFolder folder)
            {
                await Task.Run(async () =>
                {
                    ExportNamingScheme scheme = ResourceHelper.AppSettings.ExportNamingScheme;

                    foreach (var font in fonts)
                    {
                        if (DirectWrite.IsFontLocal(font.FontFace))
                        {
                            string fileName = GetFileName(font, scheme);
                            StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);
                            await TryWriteToFileAsync(font, file).ConfigureAwait(false);
                        }
                    }
                });

                WeakReferenceMessenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(folder, true)));
            }
        }

        private static async Task<bool> TryWriteToFileAsync(FontVariant font, StorageFile file)
        {
            try
            {
                using IRandomAccessStream s = await file.OpenAsync(FileAccessMode.ReadWrite).AsTask().ConfigureAwait(false);
                s.Size = 0;

                using IOutputStream o = s.GetOutputStreamAt(0);
                await DirectWrite.WriteToStreamAsync(font.FontFace, o).AsTask().ConfigureAwait(false);
                return true;
            }
            catch { }

            return false;
        }

        private static string GetFileName(FontVariant font, ExportNamingScheme scheme)
        {
            string fileName = null;
            string ext = ".ttf";
            
            var src = DirectWrite.GetFileName(font.FontFace);
            if (!string.IsNullOrWhiteSpace(src))
                ext = Path.GetExtension(src);

            if (scheme == ExportNamingScheme.System)
                fileName = src;

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"{font.FamilyName} {font.PreferredName}{ext}";

            return $"{Utils.Humanise(Path.GetFileNameWithoutExtension(fileName), false)}{Path.GetExtension(fileName).ToLower()}";
        }

    }
}
