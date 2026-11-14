using System;
using System.Text;

namespace Sakura.Unity.Editor
{
    public static class SakuraEncodingChecks
    {
        public static void Verify()
        {
            byte[] encoded = { 0x82, 0xA0 };
            string decoded = Encoding.GetEncoding(932).GetString(encoded);
            if (decoded != "あ") throw new InvalidOperationException("CP932 did not decode a Japanese PMX/VMD name correctly.");
            byte[] roundTrip = Encoding.GetEncoding(932).GetBytes(decoded);
            if (roundTrip.Length != encoded.Length || roundTrip[0] != encoded[0] || roundTrip[1] != encoded[1])
                throw new InvalidOperationException("CP932 did not preserve Japanese PMX/VMD names on encode.");
        }
    }
}
