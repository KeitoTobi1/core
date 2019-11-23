using Omnius.Core.Cryptography;

#nullable enable

namespace Omnius.Core.Network.Connections.Secure
{
    public enum OmniSecureConnectionType : byte
    {
        Connected = 0,
        Accepted = 1,
    }

    public enum OmniSecureConnectionVersion : byte
    {
        Version1 = 1,
    }

}