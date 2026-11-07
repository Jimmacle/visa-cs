# VisaCs.Primitives

The VISA status codes and exception types shared by the VisaCs libraries. You normally get this package
through `VisaCs`, `VisaCs.Serial`, `VisaCs.Usb` or `VisaCs.Gpib`. Reference it directly only if a library of
yours handles VisaCs errors without depending on those packages.

## Install

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs.Primitives --version 0.1.0
```

## Contents (namespace `VisaCs`)

| Type | Purpose |
|---|---|
| `VisaStatus` | VISA completion and error codes; the values match `visa.h` |
| `VisaStatusInfo` | `GetName` (`VI_ERROR_TMO`), `GetDescription`, and the `IsError()` extension |
| `VisaException` | Every failure: `Status`, `StatusName`, and `NativeError` when an OS or driver error code is known |
| `VisaTimeoutException` | `VI_ERROR_TMO`; `BytesTransferred` tells how much arrived before the timeout |

Cancelling a token is not an error code: it throws `OperationCanceledException`.

```csharp
using VisaCs;
using VisaCs.Serial;

using var port = NativeSerialPort.Open("COM3");
try
{
    port.Write("*IDN?\n"u8, timeoutMilliseconds: 1000);
    var buffer = new byte[256];
    int n = port.Read(buffer, timeoutMilliseconds: 2000);
}
catch (VisaTimeoutException ex)
{
    Console.WriteLine($"Timed out after {ex.BytesTransferred} bytes");
}
catch (VisaException ex) when (ex.Status == VisaStatus.ErrorConnectionLost)
{
    Console.WriteLine($"{ex.StatusName}: {VisaStatusInfo.GetDescription(ex.Status)}");
}
```

## License

MIT
