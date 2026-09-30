using System.Diagnostics;
using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Gui;

internal static class UnifiedPrinterHelper
{
    public static async Task<List<LocalPrinterInfo>> GetPrintersAsync()
    {
        const string script = """
$items = @(Get-Printer | ForEach-Object {
  $p = $_
  $port = Get-PrinterPort -Name $p.PortName -ErrorAction SilentlyContinue

  $deviceUrl = ''
  $deviceUuid = ''
  $hostAddress = ''
  $portNumber = $null

  if($port) {
    if($port.PSObject.Properties['DeviceURL']) { $deviceUrl = [string]$port.DeviceURL }
    if($port.PSObject.Properties['DeviceUUID']) { $deviceUuid = [string]$port.DeviceUUID }
    if($port.PSObject.Properties['PrinterHostAddress']) { $hostAddress = [string]$port.PrinterHostAddress }
    if($port.PSObject.Properties['PortNumber'] -and $port.PortNumber) { $portNumber = [int]$port.PortNumber }
  }

  $isWsdPort = ([string]$p.PortName).StartsWith('WSD-',[System.StringComparison]::OrdinalIgnoreCase)

  if($isWsdPort -and
     ([string]::IsNullOrWhiteSpace($deviceUrl) -or [string]::IsNullOrWhiteSpace($deviceUuid))) {
    $wsdKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\' + [string]$p.PortName

    if(Test-Path -LiteralPath $wsdKey) {
      $wsd = Get-ItemProperty -LiteralPath $wsdKey -ErrorAction SilentlyContinue

      if($wsd) {
        if([string]::IsNullOrWhiteSpace($deviceUuid) -and $wsd.PSObject.Properties['Printer UUID']) {
          $deviceUuid = [string]$wsd.'Printer UUID'
        }

        foreach($property in $wsd.PSObject.Properties) {
          if($property.Name -match '^PS(Path|ParentPath|ChildName|Drive|Provider)$') { continue }

          $value = [string]$property.Value
          if([string]::IsNullOrWhiteSpace($value)) { continue }

          if([string]::IsNullOrWhiteSpace($deviceUuid) -and
             $value -match '(?i)urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}') {
            $deviceUuid = $Matches[0]
          }

          if([string]::IsNullOrWhiteSpace($deviceUrl) -and
             $value -match '^(?i)(https?|ipps?)://') {
            $deviceUrl = $value
          }
        }
      }
    }
  }

  $transportMode = 'Tunnel'
  $directAddress = ''

  if([string]$p.DriverName -match 'Microsoft IPP Class Driver') {
    if($isWsdPort -and
       (-not [string]::IsNullOrWhiteSpace($deviceUrl) -or
        -not [string]::IsNullOrWhiteSpace($deviceUuid))) {
      $transportMode = 'Wsd'
      $directAddress = $deviceUrl
    }
    elseif(-not [string]::IsNullOrWhiteSpace($deviceUrl)) {
      $directAddress = $deviceUrl
      $transportMode = 'Ipp'
    }
    elseif(-not [string]::IsNullOrWhiteSpace($hostAddress) -and
           $hostAddress -notin @('127.0.0.1','::1','localhost')) {
      $directAddress = $hostAddress
      $transportMode = 'Ipp'
    }
  }

  [pscustomobject]@{
    Name = [string]$p.Name
    DriverName = [string]$p.DriverName
    PortName = [string]$p.PortName
    PrinterStatus = [string]$p.PrinterStatus
    Comment = [string]$p.Comment
    TransportMode = $transportMode
    DirectAddress = $directAddress
    DeviceUuid = $deviceUuid
    PrinterHostAddress = $hostAddress
    PortNumber = $portNumber
  }
})

ConvertTo-Json -InputObject $items -Compress
""";

        var result = await PowerShellRunner.RunAsync(script);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Lokale Windows-Drucker konnten nicht gelesen werden."
                    : result.StdErr.Trim());

        var printers = JsonSerializer.Deserialize<List<LocalPrinterInfo>>(
                           result.StdOut,
                           JsonStore.Options)
                       ?? [];

        foreach (var printer in printers)
        {
            if (!string.Equals(
                    printer.TransportMode,
                    PrinterTransport.Wsd,
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(printer.DeviceUuid))
            {
                continue;
            }

            var directedAddress = await WsdAddressResolver.ResolveAsync(printer.DeviceUuid);
            if (string.IsNullOrWhiteSpace(directedAddress))
                continue;

            printer.TransportMode = PrinterTransport.Ipp;
            printer.DirectAddress = directedAddress;
        }

        return printers;
    }

    public static void OpenQueue(string printerName)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments =
                $"printui.dll,PrintUIEntry /o /n {QuoteArgument(printerName)}",
            UseShellExecute = true
        });
    }

    public static void PrintTestPage(string printerName)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments =
                $"printui.dll,PrintUIEntry /k /n {QuoteArgument(printerName)}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    public static bool IsUnsafeSimplePrintLoop(LocalPrinterInfo printer) =>
        (!string.IsNullOrWhiteSpace(printer.Comment) &&
         printer.Comment.StartsWith("SimplePrint:", StringComparison.OrdinalIgnoreCase)) ||
        PrinterTransport.IsSimplePrintPort(printer.PortName) ||
        PrinterTransport.IsLoopbackProxy(
            printer.PrinterHostAddress,
            printer.PortNumber);
}
