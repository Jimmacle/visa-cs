using System.Text;
using VisaCs.Lan.Protocols;

namespace VisaCs.Lan.Tests;

/// <summary>Wire-level code that is internal to VisaCs.Lan: mDNS parsing and the SCRAM client.</summary>
public sealed class ProtocolInternalsTests
{
    [Fact]
    public void MdnsAssemblySkipsMalformedRecords()
    {
        var message = new MemoryStream();
        message.Write(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 3, 0, 0, 0, 0 });
        // A PTR to a good instance, a PTR to a bad one, and an SRV for the bad one whose data is too short.
        WriteRecord(message, "_hislip._tcp.local", 12, w => MdnsDiscovery.WriteName(w, "bad._hislip._tcp.local"));
        WriteRecord(message, "bad._hislip._tcp.local", 33, w => w.Write(new byte[] { 0, 0, 0x13 }));
        WriteRecord(message, "_hislip._tcp.local", 12, w => w.Write(new byte[] { 0xC0, 0xFF }));
        var records = MdnsDiscovery.ParseRecords(message.ToArray());
        Assert.Equal(3, records.Count);
        Assert.Empty(MdnsDiscovery.Assemble(["_hislip._tcp.local"], records));
    }

    private static void WriteRecord(Stream stream, string name, ushort type, Action<Stream> data)
    {
        MdnsDiscovery.WriteName(stream, name);
        var body = new MemoryStream();
        data(body);
        stream.Write(new byte[] { (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120, (byte)(body.Length >> 8), (byte)body.Length });
        stream.Write(body.ToArray());
    }

    [Fact]
    public void ScramMatchesRfc7677TestVector()
    {
        // RFC 7677 section 3: user "user", password "pencil", client nonce "rOprNGfwEbeRWgbNEkqO".
        var client = new ScramSha256ClientMechanism("user", "pencil", null, channelBindingAvailable: false, nonce: "rOprNGfwEbeRWgbNEkqO");
        Assert.Equal("n,,n=user,r=rOprNGfwEbeRWgbNEkqO", Encoding.UTF8.GetString(client.InitialResponse()));
        var final = client.Respond(Encoding.UTF8.GetBytes(
            "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"));
        Assert.Equal("c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=",
            Encoding.UTF8.GetString(final));
        Assert.True(client.VerifyOutcome(Encoding.UTF8.GetBytes("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=")));
        Assert.False(client.VerifyOutcome(Encoding.UTF8.GetBytes("v=AAAA")));
    }

    [Fact]
    public void RejectsMalformedScramChallenges()
    {
        var badSalt = new ScramSha256ClientMechanism("user", "pencil", null, channelBindingAvailable: false, nonce: "abc");
        badSalt.InitialResponse();
        Assert.Equal(SecurityFailure.Authentication,
            Assert.Throws<VisaSecurityException>(() => badSalt.Respond(Encoding.UTF8.GetBytes("r=abcdef,s=not base64!,i=4096"))).Failure);

        var tooMany = new ScramSha256ClientMechanism("user", "pencil", null, channelBindingAvailable: false, nonce: "abc");
        tooMany.InitialResponse();
        Assert.Throws<VisaSecurityException>(() => tooMany.Respond(Encoding.UTF8.GetBytes("r=abcdef,s=AAAA,i=2000000000")));

        var sameNonce = new ScramSha256ClientMechanism("user", "pencil", null, channelBindingAvailable: false, nonce: "abc");
        sameNonce.InitialResponse();
        Assert.Throws<VisaSecurityException>(() => sameNonce.Respond(Encoding.UTF8.GetBytes("r=abc,s=AAAA,i=4096")));
    }

    [Fact]
    public void HislipHeaderRoundTrips()
    {
        var header = new byte[Hislip.HeaderSize];
        Hislip.WriteHeader(header, HislipMessageType.DataEnd, 1, 0xFFFFFF00, 42);
        var parsed = Hislip.ParseHeader(header);
        Assert.Equal(new HislipHeader(HislipMessageType.DataEnd, 1, 0xFFFFFF00, 42), parsed);
        header[0] = (byte)'X';
        Assert.Equal(VisaStatus.ErrorIO, Assert.Throws<VisaException>(() => Hislip.ParseHeader(header)).Status);
    }
}
