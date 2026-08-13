# Third-party notices

GitGui is [MIT licensed](LICENSE). Its release builds are self-contained — the .NET
runtime, the rendering stack and the native git library are all inside the download, so
users need nothing installed. That means the binaries you download from the Releases page
contain the following third-party software, and this file is the attribution that comes
with it.

Everything here permits redistribution in a closed or open product. Nothing in this list
imposes a copyleft obligation on GitGui's own source.

Version numbers are those referenced by `src/GitGui/GitGui.csproj` and its transitive
dependencies at the time of writing; `dotnet list package --include-transitive` gives the
current set.

---

## The one worth reading properly

### libgit2 — GPLv2 **with a linking exception**

Shipped inside `LibGit2Sharp.NativeBinaries`. It is the native library that does all the
actual git work, and it is the reason GitGui needs no git installation.

libgit2 is GPLv2, which would normally be a problem for an MIT application distributing
it. It isn't, because of an explicit exception granted by the libgit2 authors:

> **LINKING EXCEPTION**
>
> In addition to the permissions in the GNU General Public License, the authors give you
> unlimited permission to link the compiled version of this library into combinations
> with other programs, and to distribute those combinations without any restriction
> coming from the use of this file. (The General Public License restrictions do apply in
> other respects; for example, they cover modification of the file, and distribution when
> not linked into a combined executable.)

So: linking it into GitGui and distributing the result is unrestricted. **Modifying
libgit2 itself** would still be covered by the GPL. GitGui ships it unmodified.

Copyright (C) the libgit2 contributors. Full text:
<https://github.com/libgit2/libgit2/blob/main/COPYING>

---

## MIT

Used under the MIT License, which requires only that the copyright notice and permission
notice travel with the software — which is what this file does.

| Component | Version | Copyright |
| --- | --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) — and `Desktop`, `Themes.Fluent`, `Fonts.Inter`, `Skia`, `X11`, `Win32`, `Native`, `FreeDesktop`, `HarfBuzz`, `Controls.DataGrid`, `Controls.ColorPicker`, `Remote.Protocol`, `BuildServices` | 12.1.1 | © The AvaloniaUI Team and contributors |
| [LibGit2Sharp](https://github.com/libgit2/libgit2sharp) | 0.32.0 | © LibGit2Sharp contributors |
| [FluentAvalonia](https://github.com/amwx/FluentAvalonia) | 3.0.2 | © amwx |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | © .NET Foundation and Contributors |
| [SkiaSharp](https://github.com/mono/SkiaSharp) — and its Linux, macOS and Win32 native assets | 3.119.4 | © Microsoft Corporation |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) — and its native assets | 8.3.1.3 | © Microsoft Corporation |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | 0.11.6 | © MicroCom contributors |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | 0.94.1 | © Tom Deseyn and contributors |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | 8.0.0 | © .NET Foundation and Contributors |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | 8.0.0 | © .NET Foundation and Contributors |
| [Microsoft.IO.RecyclableMemoryStream](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) | 3.0.1 | © Microsoft Corporation |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | 10.0.11 | © .NET Foundation and Contributors |
| [.NET runtime and libraries](https://github.com/dotnet/runtime) | 10.0 | © .NET Foundation and Contributors |

The MIT License text is the same as [GitGui's own](LICENSE), with the respective
copyright holder substituted.

---

## Other licences

### Skia — BSD 3-Clause

Bundled inside SkiaSharp's native assets. Skia is what actually draws every pixel of
GitGui, which is why the app looks the same on all three platforms.

© Google LLC. <https://github.com/google/skia/blob/main/LICENSE>

### ANGLE — BSD 3-Clause

Bundled as `Avalonia.Angle.Windows.Natives`. Translates OpenGL ES calls to Direct3D on
Windows.

© The ANGLE Project Authors. <https://github.com/google/angle/blob/main/LICENSE>

### HarfBuzz — MIT ("Old MIT" variant)

Bundled inside HarfBuzzSharp's native assets. Text shaping.

© Behdad Esfahbod and others. <https://github.com/harfbuzz/harfbuzz/blob/main/COPYING>

### Inter — SIL Open Font License 1.1

The typeface, embedded via `Avalonia.Fonts.Inter`. The OFL permits bundling and
redistribution with software; it forbids selling the font on its own and requires that
any modified version be renamed. GitGui embeds it unmodified.

© The Inter Project Authors. <https://github.com/rsms/inter/blob/master/LICENSE.txt>

---

## Development-only dependencies

These are used to build and test GitGui and are **not** present in any release binary.
Listed for completeness rather than obligation.

| Component | Version | Licence |
| --- | --- | --- |
| [xunit](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 3.1.5 | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 18.8.1 | MIT |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 10.0.1 | MIT |
| [AvaloniaUI.DiagnosticsSupport](https://github.com/AvaloniaUI/Avalonia) | 2.2.3 | MIT |

`AvaloniaUI.DiagnosticsSupport` is excluded from non-Debug configurations by the
`IncludeAssets`/`PrivateAssets` conditions in `GitGui.csproj`, so it never reaches a
release build.

---

## Packaging tools

Not distributed with GitGui, and not linked into it — these produce the installers.

- **[fpm](https://github.com/jordansissel/fpm)** — MIT — builds the `.deb` and `.rpm`.
- **[Inno Setup](https://jrsoftware.org/isinfo.php)** — its own permissive licence —
  builds the Windows installer.

---

If something is missing or misattributed here, that's a bug — please
[open an issue](https://github.com/Polemus/GitGui/issues/new/choose).
