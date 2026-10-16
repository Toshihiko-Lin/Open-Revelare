using OpenRevelare.Core;

namespace OpenRevelare.Gui.Models;

/// <summary>Independent body/lens histories: never remember an exposure as a device default.</summary>
public static class CaptureDeviceLibrary
{
    public static void Remember(List<CaptureDevice> devices, CaptureDevice device)
    {
        device = new CaptureDevice { Make = device.Make.Trim(), Model = device.Model.Trim(), SerialNumber = device.SerialNumber.Trim() };
        if (device.IsEmpty) return;
        int existing = devices.FindIndex(d =>
            string.Equals(d.Make, device.Make, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Model, device.Model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.SerialNumber, device.SerialNumber, StringComparison.OrdinalIgnoreCase));
        if (existing < 0) devices.Add(device);
        else devices[existing] = device;
        devices.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label));
    }
}
