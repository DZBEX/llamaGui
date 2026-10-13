using System.Net;

namespace LlamaLauncher.Domain;

/// <summary>监听地址范围分类。</summary>
public static class HostAddress
{
    /// <summary>将 host 字符串分类为本机/局域网/公网。无法解析的非空主机名按局域网对待（更保守）。</summary>
    public static AddressScope Classify(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return AddressScope.Invalid;
        }

        var value = host.Trim();
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return AddressScope.Localhost;
        }

        if (IPAddress.TryParse(value, out var address))
        {
            if (IPAddress.IsLoopback(address))
            {
                return AddressScope.Localhost;
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return AddressScope.Lan;
            }

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                var isPrivate =
                    bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
                return isPrivate ? AddressScope.Lan : AddressScope.Public;
            }

            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return AddressScope.Lan;
            }

            return AddressScope.Public;
        }

        // 非 IP 的主机名：本机解析行为不可预知，按局域网对待并要求安全确认。
        return value.Contains(' ', StringComparison.Ordinal) ? AddressScope.Invalid : AddressScope.Lan;
    }

    /// <summary>是否属于高风险（本机以外）监听。</summary>
    public static bool IsNonLocal(AddressScope scope) => scope is AddressScope.Lan or AddressScope.Public;
}
