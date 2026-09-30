using System.IO;
using System.IO.Ports;
using Microsoft.Win32;

namespace Brobot.Connection;

/// <summary>
/// Finds the COM port Peemo's USB cable shows up as, without opening
/// anything: the ESP32-C3's native USB (the SuperMini has no bridge chip)
/// enumerates as Espressif's VID 303A / PID 1001, and Windows records the
/// COM name it assigned under that device's registry key.
///
/// Registry rather than opening every COM port and asking: probing arbitrary
/// ports is slow, can disturb whatever owns them, and — against this board's
/// native USB-CDC — is exactly the kind of Open() that has hung before (see
/// BrobotConnection.ConnectSerial). A hit here is still only a *candidate*
/// (any Espressif board matches); BrobotConnection's PING verification is
/// what actually confirms it is a Peemo.
/// </summary>
public static class PeemoUsbPorts
{
    private const string EnumUsbKey = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const string EspressifUsbSerialJtagPrefix = "VID_303A&PID_1001";

    /// <summary>COM ports of Espressif USB-Serial/JTAG devices that are plugged in right now.</summary>
    public static IReadOnlyList<string> FindCandidates()
    {
        var found = new List<string>();
        try
        {
            // Enum keeps an entry for every device ever plugged in, so the
            // registry alone lists ports of long-gone cables; only names that
            // also exist as live ports count.
            var live = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

            using RegistryKey? usb = Registry.LocalMachine.OpenSubKey(EnumUsbKey);
            if (usb == null)
            {
                return found;
            }

            // The device is composite (usbccgp): the serial function lives
            // under a "...&MI_00" child key, not under the bare VID/PID one.
            foreach (string deviceKeyName in usb.GetSubKeyNames())
            {
                if (!deviceKeyName.StartsWith(EspressifUsbSerialJtagPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using RegistryKey? device = usb.OpenSubKey(deviceKeyName);
                if (device == null)
                {
                    continue;
                }

                foreach (string instanceName in device.GetSubKeyNames())
                {
                    using RegistryKey? parameters = device.OpenSubKey(instanceName + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string portName
                        && live.Contains(portName)
                        && !found.Contains(portName, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(portName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registry unreadable — same as "no cable": WiFi carries on.
        }

        return found;
    }
}
