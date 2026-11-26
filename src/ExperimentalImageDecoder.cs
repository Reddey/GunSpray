using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using StbImageSharp;
using UnityEngine;

// when using .gif or animated .webp files, first frame gets fetched and used as a spray
internal static class ExperimentalImageDecoder
{
    internal const int MaxDimension = 1280;
    private const int MaxSourceBytes = 1_048_576;
    private const string NativeResourcePrefix = "GunSpray.Native.";
    private const string ManagedResource = "GunSpray.Managed.StbImageSharp.dll";
    private const int WebpDemuxAbi = 0x0107;
    private static readonly object sync = new object();
    private static bool registered;
    private static WebpNative webp;

    internal static void RegisterManagedResolver()
    {
        lock (sync)
        {
            if (registered) return;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveManaged;
            registered = true;
        }
    }

    private static Assembly ResolveManaged(object sender, ResolveEventArgs args)
    {
        if (!new AssemblyName(args.Name).Name.Equals("StbImageSharp", StringComparison.OrdinalIgnoreCase)) return null;
        using (var stream = typeof(ExperimentalImageDecoder).Assembly.GetManifestResourceStream(ManagedResource))
        {
            if (stream == null) throw new FileNotFoundException("Embedded StbImageSharp resource missing");
            using (var data = new MemoryStream())
            {
                stream.CopyTo(data);
                return Assembly.Load(data.ToArray());
            }
        }
    }

    internal static bool IsExperimental(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    // reject oversized .gif or .webp images
    internal static bool TryGetDimensions(byte[] data, string path, out int width, out int height)
    {
        width = height = 0;
        if (data == null || data.Length < 10 || data.Length > MaxSourceBytes) return false;
        var ext = Path.GetExtension(path);
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            if (data.Length < 13 ||
                !(Matches(data, 0, "GIF87a") || Matches(data, 0, "GIF89a"))) return false;
            width = data[6] | (data[7] << 8);
            height = data[8] | (data[9] << 8);
            return width > 0 && height > 0;
        }
        if (!ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)) return false;
        if (data.Length < 30 || !Matches(data, 0, "RIFF") || !Matches(data, 8, "WEBP")) return false;
        if (Matches(data, 12, "VP8X"))
        {
            if (ReadUInt32(data, 16) < 10) return false;
            width = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
            height = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
        }
        else if (Matches(data, 12, "VP8L"))
        {
            if (ReadUInt32(data, 16) < 5 || data[20] != 0x2f) return false;
            width = 1 + data[21] + ((data[22] & 0x3f) << 8);
            height = 1 + (data[22] >> 6) + (data[23] << 2) + ((data[24] & 0x0f) << 10);
        }
        else if (Matches(data, 12, "VP8 "))
        {
            if (ReadUInt32(data, 16) < 10 || data[23] != 0x9d ||
                data[24] != 0x01 || data[25] != 0x2a) return false;
            width = (data[26] | (data[27] << 8)) & 0x3fff;
            height = (data[28] | (data[29] << 8)) & 0x3fff;
        }
        return width > 0 && height > 0;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static bool Matches(byte[] data, int offset, string text)
    {
        if (data.Length < offset + text.Length) return false;
        for (int i = 0; i < text.Length; i++) if (data[offset + i] != (byte)text[i]) return false;
        return true;
    }

    internal static Texture2D DecodeFirstFrame(byte[] data, string path)
    {
        if (!TryGetDimensions(data, path, out var expectedW, out var expectedH))
            throw new InvalidDataException("Invalid GIF/WebP header");
        if (expectedW > MaxDimension || expectedH > MaxDimension)
            throw new InvalidDataException("Image exceeds 1280 pixels on an axis");

        byte[] pixels;
        int width, height;
        if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            //stb image decode first framel only
            var frame = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            width = frame.Width;
            height = frame.Height;
            pixels = frame.Data;
        }
        else
        {
            lock (sync)
            {
                if (webp == null) webp = WebpNative.Load();
                pixels = webp.ReadFirstFrame(data, out width, out height);
            }
        }
        if (width != expectedW || height != expectedH || width < 1 || height < 1 ||
            width > MaxDimension || height > MaxDimension ||
            pixels == null || pixels.Length != checked(width * height * 4))
            throw new InvalidDataException("Decoded dimensions/pixel data do not match source header");

        // flip top to bottom rgba to bottom to top raw texture input used by unity. similar issue was present in imagesaw. learned my lesson this time
        int rowBytes = checked(width * 4);
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * rowBytes, flipped, (height - 1 - y) * rowBytes, rowBytes);
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            texture.LoadRawTextureData(flipped);
            texture.Apply(false, false); // remain cpu readable
            return texture;
        }
        catch
        {
            UnityEngine.Object.Destroy(texture);
            throw;
        }
    }

    private sealed class WebpNative
    {
        // absolute paths
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [StructLayout(LayoutKind.Sequential)]
        private struct WebPData { internal IntPtr bytes; internal UIntPtr size; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AnimNew(ref WebPData data, IntPtr options, int abi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AnimGetInfo(IntPtr decoder, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AnimGetNext(IntPtr decoder, out IntPtr rgba, out int timestamp);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void AnimDelete(IntPtr decoder);

        private readonly AnimNew create;
        private readonly AnimGetInfo getInfo;
        private readonly AnimGetNext getNext;
        private readonly AnimDelete destroy;

        private WebpNative(IntPtr module)
        {
            create = Export<AnimNew>(module, "WebPAnimDecoderNewInternal");
            getInfo = Export<AnimGetInfo>(module, "WebPAnimDecoderGetInfo");
            getNext = Export<AnimGetNext>(module, "WebPAnimDecoderGetNext");
            destroy = Export<AnimDelete>(module, "WebPAnimDecoderDelete");
        }

        private static T Export<T>(IntPtr module, string name) where T : Delegate
        {
            var ptr = GetProcAddress(module, name);
            if (ptr == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(ptr, typeof(T));
        }

        internal static WebpNative Load()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Experimental WebP is Windows x64 only");
            var assembly = typeof(ExperimentalImageDecoder).Assembly;
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(x => x.StartsWith(NativeResourcePrefix, StringComparison.Ordinal) &&
                            x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (resourceNames.Length == 0)
                throw new FileNotFoundException("GunSpray native WebP resources not embedded. Rebuild via build.ps1");

            var directory = Path.Combine(Path.GetTempPath(), "GunSpray", "webp-1.6.0-win-x64");
            Directory.CreateDirectory(directory);
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in resourceNames)
            {
                var shortName = name.Substring(NativeResourcePrefix.Length);
                var path = Path.Combine(directory, shortName);
                using (var resource = assembly.GetManifestResourceStream(name))
                {
                    if (resource == null) throw new IOException("Missing embedded native resource " + name);
                    using (var buffer = new MemoryStream())
                    {
                        resource.CopyTo(buffer);
                        var expectedBytes = buffer.ToArray();
                        var write = !File.Exists(path);
                        if (!write)
                        {
                            using (var sha = SHA256.Create())
                                write = !sha.ComputeHash(File.ReadAllBytes(path)).SequenceEqual(sha.ComputeHash(expectedBytes));
                        }
                        if (write)
                        {
                            // cache outside plugins. only verified embedded bytes r loaded. dont use existing altered or outdated files. never plan to update dependencies past what they are at release but sitll.
                            File.WriteAllBytes(path, expectedBytes);
                        }
                    }
                }
                paths[shortName] = path;
            }
            // load dependencies before demuxer. os will keep modules loaded. nuget runtime provides libwebp and libwebpdemux. filenames SHOULD remain intact so the dlls imports remain functional. load core b4 demux
            foreach (var path in paths.Where(x => x.Key.Equals("sharpyuv.dll", StringComparison.OrdinalIgnoreCase) ||
                                                  x.Key.Equals("libsharpyuv.dll", StringComparison.OrdinalIgnoreCase))
                                      .Select(x => x.Value)) LoadModule(path);
            var corePath = paths.FirstOrDefault(x => x.Key.Equals("webp.dll", StringComparison.OrdinalIgnoreCase) ||
                                                          x.Key.Equals("libwebp.dll", StringComparison.OrdinalIgnoreCase)).Value;
            var demuxPath = paths.FirstOrDefault(x => x.Key.Equals("webpdemux.dll", StringComparison.OrdinalIgnoreCase) ||
                                                           x.Key.Equals("libwebpdemux.dll", StringComparison.OrdinalIgnoreCase)).Value;
            if (corePath == null || demuxPath == null)
                throw new FileNotFoundException("Embedded native libwebp.dll / libwebpdemux.dll resources missing");
            LoadModule(corePath);
            return new WebpNative(LoadModule(demuxPath));
        }

        private static IntPtr LoadModule(string path)
        {
            var module = LoadLibraryW(path);
            if (module == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load " + Path.GetFileName(path));
            return module;
        }

        internal byte[] ReadFirstFrame(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr decoder = IntPtr.Zero;
            IntPtr info = IntPtr.Zero;
            try
            {
                var data = new WebPData { bytes = pinned.AddrOfPinnedObject(), size = new UIntPtr((uint)bytes.Length) };
                decoder = create(ref data, IntPtr.Zero, WebpDemuxAbi); // default is straight rgba
                if (decoder == IntPtr.Zero) throw new InvalidDataException("Native WebP decoder cannot parse source");
                //webpaniminfo is 5 unit32 + padding[4] = 36b.
                info = Marshal.AllocHGlobal(36);
                if (getInfo(decoder, info) == 0) throw new InvalidDataException("WebP info unavailable");
                width = Marshal.ReadInt32(info, 0);
                height = Marshal.ReadInt32(info, 4);
                if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
                    throw new InvalidDataException("Invalid WebP dimensions");
                if (getNext(decoder, out var buffer, out var timestamp) == 0 || buffer == IntPtr.Zero)
                    throw new InvalidDataException("WebP first frame could not be decoded");
                var result = new byte[checked(width * height * 4)];
                Marshal.Copy(buffer, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                if (decoder != IntPtr.Zero) destroy(decoder);
                pinned.Free();
            }
            // gata, pula mea. merge macar
        }
    }
}