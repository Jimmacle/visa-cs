# VisaCs.Formatting

IEEE 488.2 message formats for SCPI instruments, independent of how the bytes travel (serial port, GPIB, USBTMC,
sockets):

- definite-length binary blocks (`#3123...`): build them, and parse them from a whole message or header by header;
- binary arrays to and from bytes in either byte order;
- comma-separated ASCII values in NR1/NR2/NR3 form, as instruments send them;
- the VISA printf/scanf format language (viPrintf/viScanf).

## Install

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs.Formatting --version 0.1.0
```

## Example

```csharp
using VisaCs.Formatting;   // Ieee4882, VisaFormatter, ByteOrder
using VisaCs.Serial;       // NativeSerialPort (separate package)

using var port = NativeSerialPort.Open("/dev/ttyUSB0");
port.Write(VisaFormatter.Format("FORM REAL,32;:FORM:BORD SWAP;:CURV? %d\n", [1000]), timeoutMilliseconds: 1000);
byte[] response = ReadMessage(port);                                       // your read loop, up to the end of the block

float[] samples = Ieee4882.BytesToValues<float>(Ieee4882.ParseBlock(response), ByteOrder.LittleEndian);
object?[] idn = VisaFormatter.ParseString("%[^,],%[^,]", "Keysight,34461A,MY1,A.02\n");   // ["Keysight", "34461A"]
double[] trace = Ieee4882.ParseValues<double>("+1.5E+00,-2.0E-01\n");
```

- `Ieee4882.BuildBlockHeader`, `BuildBlock`, `TryParseBlockHeader`, `TryParseBlock`, `ParseBlock`.
- `Ieee4882.BytesToValues<T>` / `ValuesToBytes<T>` for any unmanaged `T`, with a `ByteOrder`; `SwapBytes`.
- `Ieee4882.ParseValues<T>`, `ParseValue<T>`, `TryParseValue<T>` for any `ISpanParsable<T>`, and `FormatValues`.
  Integers accept `1.000E+00`, booleans `1`/`0` and `ON`/`OFF`. Everything uses the invariant culture.
- `VisaFormatter.Format`/`FormatString` and `Parse`/`ParseString`; `HasBinaryConversion` tells whether a read
  format contains binary data, which must not end at a termination character.
- Malformed data, formats or arguments throw `FormatException`; an unknown or unsupported conversion throws
  `NotSupportedException`.
- The package depends on no other package.

The `MessageBasedSession` of the `VisaCs` package uses these for `QueryBinaryValues`, `QueryAsciiValues` and
`Printf`/`Scanf`/`Queryf`, and reports these errors as `VI_ERROR_INV_FMT` and `VI_ERROR_NSUP_FMT`. More in the
[sessions guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/using-sessions.md#without-a-session).

## License

MIT
