using System.Net;

namespace Pako.SNIProxy.Auth;

public interface IClientAuthenticator
{
    bool IsAllowed(IPAddress clientIp);
    bool AllowAll { get; }
    IReadOnlyList<string> GetWhitelist();
    void AddEntry(string entry);
    bool RemoveEntry(string entry);
    void SetAllowAll(bool allowAll);
}
