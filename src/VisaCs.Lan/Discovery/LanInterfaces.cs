using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VisaCs.Lan;

/// <summary>The local IPv4 interfaces that LAN discovery sends on.</summary>
internal static class LanInterfaces
{
    /// <summary>The address and mask of every IPv4 interface that is up, except loopback.</summary>
    public static IEnumerable<(IPAddress Address, IPAddress Mask)> IPv4Interfaces()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                yield return (ua.Address, ua.IPv4Mask);
            }
        }
    }

    /// <summary>The directed broadcast address of every IPv4 interface.</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var (address, mask) in IPv4Interfaces())
        {
            if (mask is null || mask.Equals(IPAddress.Any)) continue;
            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            yield return new IPAddress(b);
        }
    }
}
