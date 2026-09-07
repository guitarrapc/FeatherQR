# SkiaSharp.QrCode

This package contains no code. From 2.0.0 it is a compatibility metapackage whose only content is a dependency on **[FeatherQR.SkiaSharp](https://www.nuget.org/packages/FeatherQR.SkiaSharp)**, which in turn depends on **[FeatherQR](https://www.nuget.org/packages/FeatherQR)** (the dependency-free QR Code, Micro QR and rMQR core) and on SkiaSharp.

It exists so that an existing project line keeps restoring after the upgrade:

```xml
<PackageReference Include="SkiaSharp.QrCode" Version="2.0.0-preview.3" />
```

New projects should reference `FeatherQR.SkiaSharp` directly, or `FeatherQR` alone when no image rendering is needed:

```xml
<PackageReference Include="FeatherQR.SkiaSharp" Version="2.0.0-preview.3" />
```

The namespaces are `FeatherQR` (generators, decoders, data types) and `FeatherQR.SkiaSharp` (image builders, `SymbolRenderer`, `SKCanvas` extensions, bitmap decoding). See the [migration notes](https://github.com/guitarrapc/FeatherQR/blob/main/docs/migration.md) for the 2.0.0 changes.
