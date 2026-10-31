using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace LiteIsland;

public sealed record BtDevice(string Name, int? Battery);

/// <summary>
/// Lists connected Bluetooth devices and their battery level (when Windows reports one).
/// No background watching: it only queries when the Bluetooth page is opened or refreshed.
/// </summary>
public static class BluetoothService
{
    // Windows device property that holds the Bluetooth battery percentage
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.Aep.ContainerId";

    public static async Task<List<BtDevice>> GetConnectedAsync()
    {
        var result = new List<BtDevice>();
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var infos = await DeviceInformation.FindAllAsync(selector, new[] { BatteryKey, ContainerKey });

            foreach (var info in infos)
            {
                if (string.IsNullOrWhiteSpace(info.Name)) continue;

                int? battery = ReadBattery(info);
                if (battery == null) battery = await ReadBatteryFromContainerAsync(info);

                result.Add(new BtDevice(info.Name, battery));
            }
        }
        catch
        {
            // Bluetooth off or not available
        }
        return result;
    }

    private static int? ReadBattery(DeviceInformation info)
    {
        try
        {
            if (info.Properties.TryGetValue(BatteryKey, out var v) && v != null)
            {
                int pct = Convert.ToInt32(v);
                if (pct >= 0 && pct <= 100) return pct;
            }
        }
        catch
        {
            // Value is not a number
        }
        return null;
    }

    // Some devices keep the battery value on a sibling device node with the same container
    private static async Task<int?> ReadBatteryFromContainerAsync(DeviceInformation info)
    {
        try
        {
            if (!info.Properties.TryGetValue(ContainerKey, out var c) || c == null) return null;

            string id = c.ToString()!.Trim('{', '}');
            string filter = "System.Devices.ContainerId:=\"{" + id + "}\"";
            var nodes = await DeviceInformation.FindAllAsync(filter, new[] { BatteryKey }, DeviceInformationKind.Device);

            foreach (var node in nodes)
            {
                int? b = ReadBattery(node);
                if (b != null) return b;
            }
        }
        catch
        {
            // Not available
        }
        return null;
    }
}
