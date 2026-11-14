# Third-party notices

## .NET Standard compatibility assemblies

These assemblies are included under `Assets/Plugins` for `Sakura.Runtime` CP932/Shift-JIS decoding and encoding. Versions are pinned from NuGet package contents; they are not copied from the .NET Framework reference assemblies. Each package is MIT-licensed by Microsoft/dotnet foundation. Package metadata and license text are available from the linked NuGet pages.

| Assembly | NuGet package | Version | License | SHA-256 |
|---|---|---:|---|---|
| `System.Text.Encoding.CodePages.dll` | [System.Text.Encoding.CodePages](https://www.nuget.org/packages/System.Text.Encoding.CodePages/6.0.0) | 6.0.0 | MIT | `B6EFA7B8399200093D4DE8C66D186B0DFA222632317EC839E96B541188866D03` |
| `System.Memory.dll` | [System.Memory](https://www.nuget.org/packages/System.Memory/4.5.4) | 4.5.4 | MIT | `D2E3949FCF3A3B3A20BB9A568A4DA582D9A90D64F34C0BDC07C9029BFE8F1858` |
| `System.Buffers.dll` | [System.Buffers](https://www.nuget.org/packages/System.Buffers/4.5.1) | 4.5.1 | MIT | `C65FFF603B283DC966D1A8B730C11D5E5E750E8021BD24640612F6CC3F2C6FB7` |
| `System.Numerics.Vectors.dll` | [System.Numerics.Vectors](https://www.nuget.org/packages/System.Numerics.Vectors/4.4.0) | 4.4.0 | MIT | `2324EE5A35674269225E2AA20957CE8830DBCA0CFFB918BD593F7A3222DEE480` |
| `System.Runtime.CompilerServices.Unsafe.dll` | [System.Runtime.CompilerServices.Unsafe](https://www.nuget.org/packages/System.Runtime.CompilerServices.Unsafe/6.0.0) | 6.0.0 | MIT | `01748200F2400C742AA689F1F5101BD6298EFDFD92C00C18F4FA473847235BA9` |

These support the `shift-jis-encoding` parity check. They do not change imported model data or the source PMX/VRM/Live2D files.
