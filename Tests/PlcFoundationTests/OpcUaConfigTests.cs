using MyPLCManagerLibrary.Model;
using Xunit;

namespace PlcFoundationTests;

public class OpcUaConfigTests
{
    [Fact]
    public void PLCConfig_OpcUa_Extensions()
    {
        var cfg = new PLCConfig
        {
            Protocol = ProtocolType.OpcUa,
            Name = "产线UA",
            EndpointUrl = "opc.tcp://192.168.1.10:4840",
            OpcUaMode = OpcUaSecurityMode.SignAndEncrypt,
            OpcUaUser = "opcuser",
            OpcUaPassword = "opcpass"
        };
        Assert.Equal(ProtocolType.OpcUa, cfg.Protocol);
        Assert.Contains("opc.tcp://192.168.1.10:4840", cfg.OpcUaConnectionKey);
        Assert.Contains("opc.tcp://192.168.1.10:4840", cfg.ConnectionKey);
        Assert.Equal(OpcUaSecurityMode.SignAndEncrypt, cfg.OpcUaMode);
    }
}
