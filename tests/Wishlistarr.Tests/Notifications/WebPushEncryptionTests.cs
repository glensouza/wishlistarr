using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Wishlistarr.Api.Notifications;

namespace Wishlistarr.Tests.Notifications;

public sealed class WebPushEncryptionTests
{
    [Fact]
    public void Seal_matches_the_rfc8291_example()
    {
        byte[] plaintext = "When I grow up, I want to be a watermelon"u8.ToArray();
        byte[] auth = Base64Url.DecodeFromChars("BTBZMqHH6r4Tts7J_aSIgg");
        byte[] userAgentPublic = Base64Url.DecodeFromChars("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        byte[] serverPrivate = Base64Url.DecodeFromChars("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
        byte[] serverPublic = Base64Url.DecodeFromChars("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        byte[] salt = Base64Url.DecodeFromChars("DGv6ra1nlYgDCS1FRnbzlw");
        using ECDiffieHellman ephemeral = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = serverPrivate,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..] },
        });

        byte[] body = WebPushEncryption.Seal(plaintext, userAgentPublic, auth, salt, ephemeral);

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            Base64Url.EncodeToString(body));
    }
}
