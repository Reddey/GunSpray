# Third party dependencies

GunSpray utilizes the following open source libraries to add .gif and .webp support.

## StbImageSharp v2.30.16
- Dev: StbSharp
- License: Unlicense (Also available under MIT)
- GitHub: https://github.com/StbSharp/StbImageSharp
- NuGet: https://www.nuget.org/packages/StbImageSharp/2.30.16
Utilized for decoding .gif images. Embedded directly inside GunSpray.

## libwebp v1.6.0
- Dev: Google
- License: BSD-3 Clause
- GitHub: https://github.com/webmproject/libwebp
Utilized for decoding .webp images, including extracting first frames. Native decoder and demux libs are embedded directly inside GunSpray.

## Imazen WebP native runtime v1.6.1
- Dev: Imazen
- License: MIT
- NuGet: https://www.nuget.org/packages/Imazen.WebP.NativeRuntime.win-x64/1.6.1
Provides precompiled win x64 libwebp libraries used by GunSpray.

## Distribution & dependencies
When building from source, the build script will automatically download the required dependencies utilizing the NuGet package registry, and embed them directly when required. GunSpray WILL NOT download any dependencies when running. The embedded native WebP libs are extracted to the users %temp% directory when required.
All third-party components retain their respective licenses and copyright notices. The full licenses are available in the licenses/ folder.
Source archives for StbImageSharp v2.30.16 and libwebp v1.6.0 are available in third_party/sources/.
