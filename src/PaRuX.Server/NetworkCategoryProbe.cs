using System.Reflection;
using System.Runtime.InteropServices;

namespace PaRuX.Server;

/// <summary>
/// Reports how Windows classifies the network this PC is on: Public, Private or DomainAuthenticated.
///
/// Why this is not the same question as "is the address private". <see cref="RemoteAccessService"/>
/// gates LAN access on the address being RFC1918, which is necessary but not sufficient, because
/// Windows Firewall does not apply rules by address range - it applies them by the *category* the
/// user assigned to the network. Measured on the physical PC: address [redacted private LAN address], unmistakably
/// private, on a home router whose network Windows had categorised **Public**. PaRuX enabled LAN
/// access, issued a pairing code, and told the user "Windows Firewall must allow PaRuX Core on
/// Private networks only" - advice that could not be followed, because the prompt Windows actually
/// raises on that machine is for a Public network. A phone on the same Wi-Fi then failed to pair
/// with "Couldn't reach PaRuX PC", and nothing in PaRuX explained why.
///
/// Knowing the category lets PaRuX say the true thing instead. It never changes the category or any
/// firewall rule: that is the user's decision, made in Windows, and PaRuX only reports what it sees.
/// </summary>
public static class NetworkCategoryProbe
{
    public enum Category { Unknown, Public, Private, DomainAuthenticated }

    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    private const int ConnectedNetworks = 1;
    private const int CategoryPublic = 0;
    private const int CategoryPrivate = 1;
    private const int CategoryDomainAuthenticated = 2;

    /// <summary>
    /// The most permissive category among the connected networks, or Unknown when it cannot be
    /// read. Most permissive is the honest answer to a firewall question: if any connected network
    /// is Domain or Private, an inbound allowance on that profile can work.
    ///
    /// Late binding through IDispatch rather than a declared vtable: INetwork exposes GetCategory
    /// as its twelfth method, so a hand-written ComImport interface that lists it first silently
    /// calls GetName instead and every machine reports Unknown - which is exactly what a first
    /// attempt here did on the physical PC, on a network Windows really does class as Public.
    /// Binding by name cannot be misordered.
    /// </summary>
    public static Category GetConnectedCategory()
    {
        if (!OperatingSystem.IsWindows()) return Category.Unknown;

        object? manager = null;
        try
        {
            Type? type = Type.GetTypeFromCLSID(NetworkListManagerClsid);
            if (type == null) return Category.Unknown;
            manager = Activator.CreateInstance(type);
            if (manager == null) return Category.Unknown;

            object? enumerated = type.InvokeMember(
                "GetNetworks", BindingFlags.InvokeMethod, null, manager, new object[] { ConnectedNetworks });
            if (enumerated is not System.Collections.IEnumerable networks) return Category.Unknown;

            var best = Category.Unknown;
            foreach (object? network in networks)
            {
                if (network == null) continue;
                try
                {
                    Type networkType = network.GetType();
                    object? connected = networkType.InvokeMember(
                        "IsConnected", BindingFlags.GetProperty, null, network, null);
                    if (connected is bool isConnected && !isConnected) continue;

                    object? raw = networkType.InvokeMember(
                        "GetCategory", BindingFlags.InvokeMethod, null, network, null);
                    Category category = Map(raw);
                    if (Rank(category) > Rank(best)) best = category;
                }
                catch { /* one unreadable network must not hide the others */ }
                finally { if (Marshal.IsComObject(network)) Marshal.ReleaseComObject(network); }
            }
            return best;
        }
        catch { return Category.Unknown; }
        finally { if (manager != null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager); }
    }

    public static string Describe(Category category) => category switch
    {
        Category.Public => "Public",
        Category.Private => "Private",
        Category.DomainAuthenticated => "Domain",
        _ => "unknown"
    };

    private static Category Map(object? raw) => Convert.ToInt32(raw) switch
    {
        CategoryPublic => Category.Public,
        CategoryPrivate => Category.Private,
        CategoryDomainAuthenticated => Category.DomainAuthenticated,
        _ => Category.Unknown
    };

    // Unknown ranks lowest so a network we could not read never masks one we could.
    private static int Rank(Category category) => category switch
    {
        Category.DomainAuthenticated => 3,
        Category.Private => 2,
        Category.Public => 1,
        _ => 0
    };
}
